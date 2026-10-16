using Asp.Versioning;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using UnderMineApi.Data;
using UnderMineApi.Middleware;
using UnderMineApi.Services;

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
            failureStatus: HealthStatus.Unhealthy
        );

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        options.ForwardLimit = null;
    });

    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            // Static assets of the admin panel are not part of the public API and must not
            // consume the API rate limit budget (the limit is shared per client IP otherwise).
            if (!httpContext.Request.Path.StartsWithSegments("/api"))
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "static",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 1000,
                        Window = TimeSpan.FromSeconds(10),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            }

            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ip,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 15,
                    Window = TimeSpan.FromSeconds(10),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
        });
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

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

    // Fresh installs have no admin to sign in with. Creates one with a random login/password on
    // startup and prints the credentials, so the admin panel is reachable without touching the DB.
    builder.Services.AddScoped<IAdminBootstrapService, AdminBootstrapService>();
    builder.Services.AddScoped<UnderMineApi.Services.PasswordHasher.IPasswordHasher, UnderMineApi.Services.PasswordHasher.BcryptPasswordHasher>();
    builder.Services.AddScoped<UnderMineApi.Services.AdminAuditService>();

    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

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
            options.Events = new JwtBearerEvents();
        });

    var allowedOrigins = builder.Configuration["CORS:AllowedOrigins"]?
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();

    builder.Services.AddCors(options =>
    {
        if (allowedOrigins.Length > 0)
        {
            options.AddPolicy("ConfiguredOrigins", policy =>
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials());
        }
        else
        {
            options.AddPolicy("AllowAll", policy =>
                policy.SetIsOriginAllowed(_ => true)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials());
        }
    });

    builder.Services.AddScoped<ApplicationStatusService>();

    // Build services
    builder.Services.AddScoped<EngineDownloadService>();
    builder.Services.AddScoped<S3StorageService>();
    builder.Services.AddScoped<BuildManifestService>();
    builder.Services.AddHttpClient<ModrinthService>();
    builder.Services.AddScoped<CustomArtifactService>();

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

    builder.Services.AddHostedService<BuildWorkerService>();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        try
        {
            var context = services.GetRequiredService<AppDbContext>();
            context.Database.Migrate();
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Error during automatic application of PostgreSQL database migrations");
        }
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
    app.UseExceptionHandler();
    app.MapHealthChecks("/health");

    // Forwarded headers must be applied before authentication/rate limiting so that the
    // client IP and scheme seen downstream are the ones proxied by nginx.
    app.UseForwardedHeaders();

    var corsPolicyName = allowedOrigins.Length > 0 ? "ConfiguredOrigins" : "AllowAll";
    app.UseCors(corsPolicyName);

    // Authentication runs before the maintenance lock so that admins can still reach
    // the API (and switch maintenance off) while the public API is locked.
    app.UseAuthentication();
    app.UseRateLimiter();
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

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unexpected error causing the application to stop.");
}
finally
{
    Log.CloseAndFlush();
}