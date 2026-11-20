using Microsoft.Extensions.Options;

namespace UnderMineApi.Options;

public sealed class JwtOptions
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
}

public sealed class S3Options
{
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string CdnBaseUrl { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
    public bool PublicRead { get; set; } = true;

    /// <summary>
    /// When false (the default for Garage), the API never creates the bucket or changes its
    /// policy. The bucket, access key and website are provisioned once by garage-bootstrap.yml.
    /// </summary>
    public bool ManageBucket { get; set; }
}

public sealed class BuildOptions
{
    public string RootPath { get; set; } = "builds";

    /// <summary>
    /// Only one node should process the build queue (prod-1). Standby/DR replicas set this
    /// to false and are started with the queue worker disabled.
    /// </summary>
    public bool WorkerEnabled { get; set; } = true;
}

public sealed class DatabaseOptions
{
    /// <summary>
    /// Migrations run once on the primary. API replicas set this to false and rely on the
    /// already-migrated schema. <c>--migrate-only</c> forces a run regardless of this flag.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}

public sealed class BootstrapAdminOptions
{
    public bool Enabled { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class JavaRuntimeOptions
{
    public string RootPath { get; set; } = "java";
    public string AdoptiumApiBaseUrl { get; set; } = "https://api.adoptium.net";
    public int CacheHours { get; set; } = 24;
    public int MinMajor { get; set; } = 8;
    public int MaxMajor { get; set; } = 64;
    public long MaxArchiveBytes { get; set; } = 250L * 1024 * 1024;
}

/// <summary>
/// Registers startup-only options models. Their purpose is validation, not consumption: the
/// services still read configuration directly, but ValidateOnStart turns a missing/incorrect
/// key into a clear startup failure instead of a runtime 500.
/// </summary>
public static class StartupOptionsExtensions
{
    public static IServiceCollection AddValidatedStartupOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection("Jwt"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Key) && o.Key.Length >= 32,
                "Jwt:Key is required and must be at least 32 characters long.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Jwt:Issuer and Jwt:Audience are required.")
            .ValidateOnStart();

        services.AddOptions<S3Options>()
            .Bind(configuration.GetSection("S3"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint), "S3:Endpoint is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AccessKey) && !string.IsNullOrWhiteSpace(o.SecretKey),
                "S3:AccessKey and S3:SecretKey are required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "S3:Bucket is required.")
            .Validate(o => Uri.TryCreate(o.CdnBaseUrl, UriKind.Absolute, out _),
                "S3:CdnBaseUrl must be an absolute URL (e.g. https://cdn.example.com).")
            .ValidateOnStart();

        services.AddOptions<BuildOptions>()
            .Bind(configuration.GetSection("Build"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.RootPath), "Build:RootPath cannot be empty.")
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection("Database"))
            .ValidateOnStart();

        services.AddOptions<BootstrapAdminOptions>()
            .Bind(configuration.GetSection("BootstrapAdmin"))
            .ValidateOnStart();

        services.AddOptions<JavaRuntimeOptions>()
            .Bind(configuration.GetSection("JavaRuntime"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.RootPath),
                "JavaRuntime:RootPath cannot be empty.")
            .Validate(o => Uri.TryCreate(o.AdoptiumApiBaseUrl, UriKind.Absolute, out var uri) &&
                           uri.Scheme == Uri.UriSchemeHttps,
                "JavaRuntime:AdoptiumApiBaseUrl must be an absolute HTTPS URL.")
            .Validate(o => o.CacheHours >= 0,
                "JavaRuntime:CacheHours cannot be negative.")
            .Validate(o => o.MinMajor >= 8 && o.MaxMajor >= o.MinMajor,
                "JavaRuntime:MinMajor/MaxMajor must describe a valid Java version range.")
            .Validate(o => o.MaxArchiveBytes > 0,
                "JavaRuntime:MaxArchiveBytes must be greater than zero.")
            .ValidateOnStart();

        return services;
    }
}
