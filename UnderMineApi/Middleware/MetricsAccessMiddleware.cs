using System.Security.Cryptography;
using System.Text;

namespace UnderMineApi.Middleware;

/// <summary>
/// Optional bearer-token protection for the Prometheus scrape endpoint. When
/// <c>Metrics:Token</c> is configured, requests to <c>/metrics</c> must send it as
/// <c>Authorization: Bearer &lt;token&gt;</c> or <c>X-Metrics-Token</c>.
/// </summary>
public sealed class MetricsAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[]? _token;

    public MetricsAccessMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        var token = configuration["Metrics:Token"]?.Trim();
        _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        if (_token != null && !IsValidToken(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new
            {
                status = StatusCodes.Status401Unauthorized,
                error = "UNAUTHORIZED",
                message = "A metrics access token is required."
            });
            return;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await _next(context);
    }

    private bool IsValidToken(HttpRequest request)
    {
        var provided = request.Headers["X-Metrics-Token"].FirstOrDefault();

        if (string.IsNullOrEmpty(provided))
        {
            var authorization = request.Headers.Authorization.FirstOrDefault();
            if (authorization != null &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = authorization["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(provided))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(providedBytes, _token!);
    }
}
