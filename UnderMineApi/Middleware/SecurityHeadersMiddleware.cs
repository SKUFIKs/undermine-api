namespace UnderMineApi.Middleware;

/// <summary>
/// Adds baseline browser security headers to every response, including errors and static files.
/// HSTS is emitted only for HTTPS requests (including requests that became HTTPS through
/// X-Forwarded-Proto from nginx).
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    // The admin SPA has no inline <script> or event handlers (all actions are delegated via
    // data-action), so scripts can be strictly same-origin. Inline styles are still used by
    // progress bars, hence style-src 'unsafe-inline'.
    private const string AdminContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    // Swagger UI (Development only) loads its initialization/inline assets; keep it working
    // without weakening the admin panel policy.
    private static readonly string DefaultContentSecurityPolicy =
        AdminContentSecurityPolicy.Replace("script-src 'self';", "script-src 'self' 'unsafe-inline';");

    private readonly RequestDelegate _next;
    private readonly int _hstsMaxAgeDays;

    public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _hstsMaxAgeDays = Math.Max(1, configuration.GetValue("Security:HstsMaxAgeDays", 365));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Content-Security-Policy"] = context.Request.Path.StartsWithSegments("/admin")
                ? AdminContentSecurityPolicy
                : DefaultContentSecurityPolicy;

            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] =
                    $"max-age={_hstsMaxAgeDays * 24 * 60 * 60}; includeSubDomains";
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
