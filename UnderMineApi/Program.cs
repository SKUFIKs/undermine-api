using Asp.Versioning;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;
using System.Text;
using System.Text.Json.Serialization;
using UnderMineApi.Data;
using UnderMineApi.Extensions;
using UnderMineApi.HealthChecks;
using UnderMineApi.Middleware;
using UnderMineApi.Options;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.Registration;
using UnderMineApi.Services.Security;

DotNetEnv.Env.Load();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/api-.log",
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        buffered: true)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog();
    builder.Services.AddValidatedStartupOptions(builder.Configuration);

    System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

    var connectionString = builder.Configuration.GetConnectionString("PostgreConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Connection string 'PostgreConnection' is missing. Set it in the .env file as ConnectionStrings__PostgreConnection.");
    }

    builder.Services.AddHealthChecks()
        .AddNpgSql(
            connectionString: connectionString,
            name: "PostgreSQL",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" })
        // Liveness must not depend on external services: the process is alive even while the
        // database is restarting.
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
        // Storage is reported by /health (full) but not required for /health/ready: the API can
        // still serve manifests while MinIO is temporarily unavailable.
        .AddCheck<S3HealthCheck>("MinIO", tags: new[] { "storage" });

    // Trust X-Forwarded-* only from the explicitly configured reverse proxy (nginx in
    // docker-compose). Without configuration the safe loopback-only defaults apply, so a direct
    // client cannot spoof its address by sending an X-Forwarded-For header.
    var forwardedHeadersEnabled = builder.Configuration.IsForwardedHeadersEnabled();
    builder.Services.AddConfiguredForwardedHeaders(builder.Configuration);

    // Separate policies for public API, admin polling, auth and registration.
    builder.Services.AddConfiguredRateLimiting(builder.Configuration);

    var redisConn = builder.Configuration.GetConnectionString("Redis");
    IConnectionMultiplexer? multiplexer = null;

    if (string.IsNullOrEmpty(redisConn))
    {
        var redisHost = builder.Configuration["Redis:Host"];
        var redisPort = builder.Configuration["Redis:Port"];
        var redisUser = builder.Configuration["Redis:Username"];
        var redisPass = builder.Configuration["Redis:Password"];
        var redisSsl = builder.Configuration.GetValue<bool?>("Redis:Ssl") ?? false;

        if (!string.IsNullOrEmpty(redisHost))
        {
            var port = string.IsNullOrEmpty(redisPort) ? "6379" : redisPort;
            var options = new ConfigurationOptions
            {
                AbortOnConnectFail = false,
                Ssl = redisSsl
            };
            options.EndPoints.Add($"{redisHost}:{port}");
            if (!string.IsNullOrEmpty(redisPass)) options.Password = redisPass;
            if (!string.IsNullOrEmpty(redisUser)) options.User = redisUser;

            try
            {
                multiplexer = ConnectionMultiplexer.Connect(options);
                builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to connect to Redis at application startup (detailed parameters).");
            }
        }
    }
    else
    {
        try
        {
            multiplexer = ConnectionMultiplexer.Connect(redisConn);
            builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to connect to Redis using the connection string.");
        }
    }

    builder.Services.AddSingleton<ConfigCacheService>(sp =>
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = sp.GetRequiredService<ILogger<ConfigCacheService>>();
        var redis = sp.GetService<IConnectionMultiplexer>();
        return new ConfigCacheService(scopeFactory, logger, redis);
    });

    builder.Services.AddHostedService<ConfigUpdateService>();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddScoped<UnderMineApi.Services.Interfaces.IUserService, UnderMineApi.Services.UserService>();
    builder.Services.AddScoped<UnderMineApi.Services.Interfaces.IServerService, UnderMineApi.Services.ServerService>();

    // Re-validates the account behind an already issued JWT (ban / role changes / deleted user).
    builder.Services.AddScoped<UnderMineApi.Services.Interfaces.IUserAccessService, UnderMineApi.Services.UserAccessService>();

    // Registration abuse limits (device/subnet quotas and a strict create-account rate limit).
    builder.Services.Configure<RegistrationOptions>(builder.Configuration.GetSection("Registration"));

    // Fresh installs have no admin to sign in with. Creates one with a random login/password on
    // startup and prints the credentials, so the admin panel is reachable without touching the DB.
    builder.Services.AddScoped<IAdminBootstrapService, AdminBootstrapService>();
    builder.Services.AddScoped<UnderMineApi.Services.PasswordHasher.IPasswordHasher, UnderMineApi.Services.PasswordHasher.BcryptPasswordHasher>();
    builder.Services.AddScoped<UnderMineApi.Services.AdminAuditService>();

    builder.Services.AddControllers(options =>
        {
            // One error envelope for every MVC error result.
            options.Filters.Add<ApiErrorNormalizationFilter>();
        })
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

    // OpenAPI/Swagger is exposed only in Development (see the pipeline below).
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(0, 1);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

    var jwtKey = builder.Configuration["Jwt:Key"];
    if (string.IsNullOrEmpty(jwtKey) || jwtKey.Length < 32)
    {
        throw new InvalidOperationException("JWT Key must be at least 32 characters long. Set it in .env file as Jwt__Key.");
    }

    builder.Services.AddScoped<IJwtService, JwtService>();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwtConfig = builder.Configuration.GetSection("Jwt");
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtConfig["Issuer"],
                ValidAudience = jwtConfig["Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig["Key"]!)),
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = StatusCodes.Status401Unauthorized,
                        error = "UNAUTHORIZED",
                        message = "Authentication required.",
                        request_id = RequestId(context.HttpContext)
                    });
                },
                OnForbidden = async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = StatusCodes.Status403Forbidden,
                        error = "FORBIDDEN",
                        message = "You do not have permission to perform this action.",
                        request_id = RequestId(context.HttpContext)
                    });
                }
            };
        });

    // Role/permission model for the admin area. The admin role is implicit and always passes;
    // moderator/user permissions are stored in role_permissions and edited in Settings.
    builder.Services.AddScoped<IPermissionService, PermissionService>();
    builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
    builder.Services.AddScoped<IAuthorizationHandler, AnyPermissionAuthorizationHandler>();

    builder.Services.AddAuthorization(options =>
    {
        foreach (var permission in Permissions.AllKeys)
        {
            options.AddPolicy(permission, policy =>
                policy.Requirements.Add(new PermissionRequirement(permission)));
        }

        options.AddPolicy(Permissions.UsersViewPolicy, policy =>
            policy.Requirements.Add(new AnyPermissionRequirement(
                Permissions.UsersBan,
                Permissions.UsersChangePassword,
                Permissions.UsersChangeRole)));

        options.AddPolicy(Permissions.AdminAreaPolicy, policy =>
            policy.Requirements.Add(new AnyPermissionRequirement(
                Permissions.AllKeys.ToArray())));

        options.AddPolicy(Permissions.SettingsManagePolicy, policy =>
            policy.RequireRole(Roles.Admin));
    });

    // CORS: only explicitly configured origins in Production; same-origin otherwise.
    builder.Services.AddConfiguredCors(builder.Configuration, builder.Environment);


    // Build services
    builder.Services.AddScoped<EngineDownloadService>();
    builder.Services.AddScoped<S3StorageService>();
    builder.Services.AddScoped<BanEvidenceService>();
    builder.Services.AddScoped<ClientReportService>();
    builder.Services.AddScoped<BuildManifestService>();
    builder.Services.AddScoped<BuildEditorService>();
    builder.Services.AddHttpClient<ModrinthService>();
    builder.Services.AddScoped<CustomArtifactService>();

    // Java runtimes are downloaded from Adoptium once and cached on disk, then streamed to
    // launchers through /api/v1.0/java. The client has a generous timeout because the first
    // request may fetch a ~50 MB archive.
    builder.Services.AddHttpClient(JavaRuntimeService.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromMinutes(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UnderMineApi/1.0");
    });
    builder.Services.AddSingleton<JavaRuntimeService>();

    // .mrpack modpack imports (staging, index parsing, parallel file download).
    // Resolves CurseForge (projectID, fileID) pairs when an uploaded CurseForge pack is imported.
    builder.Services.AddScoped<CurseForgeService>();
    builder.Services.AddScoped<ModpackImportService>();

    // Links a server to the build whose client manifest it serves.
    builder.Services.AddScoped<BuildLinkService>();
    builder.Services.AddHttpClient();

    // Version auto-discovery for the admin panel (Mojang / Fabric meta / Forge + NeoForge maven).
    // Cached in memory because the upstream metadata is large and changes rarely.
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<VersionCatalogService>();

    // Cross-process build ownership (PostgreSQL advisory lock). One build is processed by one
    // worker even when several API replicas share the same database.
    builder.Services.AddSingleton<UnderMineApi.Services.Interfaces.IBuildLockService, UnderMineApi.Services.BuildLockService>();

    // Only one node processes the build queue. Standby/DR replicas set Build:WorkerEnabled=false.
    if (RuntimeFlags.RunBuildWorker(builder.Configuration))
    {
        builder.Services.AddHostedService<BuildWorkerService>();
    }
    else
    {
        Log.Information("Build worker is disabled (Build:WorkerEnabled=false).");
    }

    builder.Services.AddHostedService<BuildLogRetentionService>();
    builder.Services.AddHostedService<ClientReportRetentionService>();

    var app = builder.Build();

    // --migrate-only turns the image into a one-shot migration job: apply migrations, then exit
    // without starting Kestrel. This is how deploy pipelines migrate before rolling API replicas.
    var migrateOnly = args.Any(arg =>
        string.Equals(arg, "--migrate-only", StringComparison.OrdinalIgnoreCase));

    // Bounded retries (the DB container may still be starting), then fail fast. Serving traffic
    // against an outdated/missing schema is worse than not starting at all.
    await app.MigrateDatabaseWithRetryAsync(force: migrateOnly);

    if (migrateOnly)
    {
        Log.Information("Database migrations completed; exiting because --migrate-only was specified.");
        return;
    }

    // Runs after Migrate() so the user_accounts / user_data tables exist. Kept in its own try/catch:
    // a bootstrap problem must never stop the API from serving traffic (the admin panel can still
    // be reached and used to create an admin by other means).
    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        try
        {
            await services.GetRequiredService<IAdminBootstrapService>().EnsureAdminAsync();
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Failed to bootstrap the default admin account");
        }
    }

    if (multiplexer != null)
    {
        using (var scope = app.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<ConfigCacheService>();
            await cache.TryLoadFromDistributedCacheAsync();
        }

        var sub = multiplexer.GetSubscriber();
        _ = sub.SubscribeAsync(RedisChannel.Literal("ConfigCache:Updated"), async (channel, value) =>
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var cache = scope.ServiceProvider.GetRequiredService<ConfigCacheService>();
                await cache.RefreshCacheAsync();
            }
            catch (Exception ex)
            {
                var logger = app.Services.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(ex, "Error processing cache update notification");
            }
        });
    }

    // --- Middleware Pipeline ---

    app.UseSerilogRequestLogging();
    app.UseMiddleware<UnderMineApi.Middleware.RequestLoggingMiddleware>();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseExceptionHandler();

    // /health/live  - process liveness only (no external dependencies)
    // /health/ready - readiness: PostgreSQL is reachable
    // /health       - full status: readiness + MinIO/S3
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live")
    });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate = _ => true
    });

    // Forwarded headers must be applied before authentication/rate limiting so that the
    // client IP and scheme seen downstream are the ones proxied by nginx.
    if (forwardedHeadersEnabled)
        app.UseForwardedHeaders();

    // Explicit routing so endpoint metadata ([Authorize] / [AllowAnonymous]) is available to
    // AccountBanMiddleware below.
    app.UseRouting();

    // Same-origin production deployments do not need the CORS middleware at all.
    if (CorsExtensions.IsCorsEnabled(builder.Configuration, builder.Environment))
        app.UseCors(CorsExtensions.PolicyName);

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Authentication runs before the maintenance lock so that admins can still reach
    // the API (and switch maintenance off) while the public API is locked.
    app.UseAuthentication();
    app.UseRateLimiter();

    // A valid JWT is not enough: the account may have been banned or demoted since it was
    // issued. Runs before maintenance/authorization so banned admins are blocked as well.
    app.UseMiddleware<AccountBanMiddleware>();

    app.UseMiddleware<ApiLockMiddleware>();
    app.UseAuthorization();

    app.MapControllers();

    // UseDefaultFiles must run before UseStaticFiles, otherwise a request for a
    // directory (e.g. /admin/) is never rewritten to its index.html and returns 404.
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            // The admin panel is a small, constantly updated SPA: cache-bust everything so a
            // deploy (or a table/column change) is never hidden behind a stale browser copy.
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
        }
    });

    static string? RequestId(HttpContext context) =>
        context.Items.TryGetValue("RequestId", out var id) ? id?.ToString() : null;

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unexpected error causing the application to stop.");

    // Fail-fast must also be visible to Docker/systemd: a non-zero exit code triggers a restart
    // policy instead of leaving a half-initialized process.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}