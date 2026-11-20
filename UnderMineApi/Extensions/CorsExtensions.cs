using Microsoft.AspNetCore.Cors.Infrastructure;

namespace UnderMineApi.Extensions;

/// <summary>
/// CORS setup with a secure default: cross-origin access is allowed only for the explicitly
/// configured origins. In Production an empty <c>CORS:AllowedOrigins</c> means "same-origin only"
/// (the admin panel is served by the API and the launcher is not a browser), instead of the old
/// "allow everything with credentials" fallback.
/// </summary>
public static class CorsExtensions
{
    public const string PolicyName = "UnderMineCors";

    /// <summary>
    /// CORS middleware is only needed when there is at least one allowed origin (or when running
    /// in Development with the convenience "any origin" mode). Production without an allow-list
    /// is same-origin only and must not pay for (or log about) CORS processing.
    /// </summary>
    public static bool IsCorsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        ReadOrigins(configuration).Length > 0 || environment.IsDevelopment();

    public static IServiceCollection AddConfiguredCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var origins = ReadOrigins(configuration);
        var allowCredentials = configuration.GetValue("CORS:AllowCredentials", false);

        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"CORS:AllowedOrigins contains an invalid origin: '{origin}'. " +
                    "Use absolute http(s) URLs, e.g. https://admin.example.com.");
            }
        }

        services.AddCors(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10));

                if (origins.Length > 0)
                {
                    policy.WithOrigins(origins);

                    // Credentials (cookies) are opt-in: the API authenticates with Bearer tokens,
                    // and allowing credentials together with a broad origin list is a common
                    // security mistake.
                    if (allowCredentials)
                        policy.AllowCredentials();
                }
                else if (environment.IsDevelopment())
                {
                    // Local development convenience; no credentials are allowed here.
                    policy.SetIsOriginAllowed(_ => true);
                }
                else
                {
                    // Production without an explicit allow-list: never emit CORS headers.
                    policy.SetIsOriginAllowed(_ => false);
                }
            });
        });

        return services;
    }

    private static string[] ReadOrigins(IConfiguration configuration) =>
        configuration["CORS:AllowedOrigins"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? Array.Empty<string>();
}
