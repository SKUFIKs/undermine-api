using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using UnderMineApi.Services.Registration;

namespace UnderMineApi.Extensions;

/// <summary>
/// Rate limiting policies separated by workload:
/// <list type="bullet">
///   <item><c>api</c> — normal client traffic (public/user endpoints);</item>
///   <item><c>admin</c> — admin panel, which polls builds/logs;</item>
///   <item><c>auth</c> — login / token validation (strict, per subnet);</item>
///   <item><c>register</c> — account creation (very strict, per subnet);</item>
/// </list>
/// Authenticated requests are partitioned by user id, so several administrators behind one NAT
/// do not share a single budget. Anonymous requests fall back to the normalized IP/subnet key.
/// </summary>
public static class RateLimitingExtensions
{
    public const string ApiPolicy = "api";
    public const string AdminPolicy = "admin";
    public const string AuthPolicy = "auth";
    public const string RegisterPolicy = "register";
    public const string ReportsPolicy = "reports";

    public static IServiceCollection AddConfiguredRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var apiPermit = Math.Max(1, configuration.GetValue("RateLimiting:Api:PermitLimit", 120));
        var apiWindow = Seconds(configuration.GetValue("RateLimiting:Api:WindowSeconds", 60));

        var adminPermit = Math.Max(1, configuration.GetValue("RateLimiting:Admin:PermitLimit", 600));
        var adminWindow = Seconds(configuration.GetValue("RateLimiting:Admin:WindowSeconds", 60));

        var authPermit = Math.Max(1, configuration.GetValue("RateLimiting:Auth:PermitLimit", 20));
        var authWindow = Seconds(configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60));

        // Backwards-compatible with the values shipped together with API-03.
        var registerPermit = Math.Max(1, configuration.GetValue(
            "RateLimiting:Register:PermitLimit",
            configuration.GetValue("Registration:RateLimitPermits", 10)));
        var registerWindow = Seconds(configuration.GetValue(
            "RateLimiting:Register:WindowSeconds",
            configuration.GetValue("Registration:RateLimitWindowMinutes", 10) * 60));

        var staticPermit = Math.Max(1, configuration.GetValue("RateLimiting:Static:PermitLimit", 1000));
        var staticWindow = Seconds(configuration.GetValue("RateLimiting:Static:WindowSeconds", 10));

        // Crash-report uploads are rare but can carry up to 30 MB. Keep the anonymous budget
        // small; authenticated users get their own bucket through the user-id partition.
        var reportsPermit = Math.Max(1, configuration.GetValue("RateLimiting:Reports:PermitLimit", 10));
        var reportsWindow = Seconds(configuration.GetValue("RateLimiting:Reports:WindowSeconds", 600));

        var ipv4Prefix = Math.Clamp(configuration.GetValue("Registration:Ipv4PrefixLength", 32), 1, 32);
        var ipv6Prefix = Math.Clamp(configuration.GetValue("Registration:Ipv6PrefixLength", 64), 1, 128);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // The global limiter is applied to every request, including endpoints that
                // declare their own [EnableRateLimiting] policy. Let those endpoints be handled
                // by their named policy only (otherwise the global API window becomes a hard cap
                // for the admin/auth/register budgets as well).
                var explicitPolicy = httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>();
                if (explicitPolicy != null)
                    return RateLimitPartition.GetNoLimiter<string>("policy:" + explicitPolicy.PolicyName);

                // Static assets (admin SPA, health) are outside the API budget.
                if (!httpContext.Request.Path.StartsWithSegments("/api"))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: "static",
                        factory: _ => FixedWindow(staticPermit, staticWindow));
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext, ipv4Prefix, ipv6Prefix, preferUser: true),
                    factory: _ => FixedWindow(apiPermit, apiWindow));
            });

            options.AddPolicy(ApiPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext, ipv4Prefix, ipv6Prefix, preferUser: true),
                    factory: _ => FixedWindow(apiPermit, apiWindow)));

            options.AddPolicy(AdminPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext, ipv4Prefix, ipv6Prefix, preferUser: true),
                    factory: _ => FixedWindow(adminPermit, adminWindow)));

            options.AddPolicy(AuthPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext, ipv4Prefix, ipv6Prefix, preferUser: false),
                    factory: _ => FixedWindow(authPermit, authWindow)));

            options.AddPolicy(RegisterPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext, ipv4Prefix, ipv6Prefix, preferUser: false),
                    factory: _ => FixedWindow(registerPermit, registerWindow)));

            options.AddPolicy(ReportsPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext, ipv4Prefix, ipv6Prefix, preferUser: true),
                    factory: _ => FixedWindow(reportsPermit, reportsWindow)));

            options.OnRejected = async (context, token) =>
            {
                var http = context.HttpContext;
                if (http.Response.HasStarted)
                    return;

                var retryAfterSeconds = 0;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);

                if (retryAfterSeconds > 0)
                    http.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                var requestId = http.Items.TryGetValue("RequestId", out var id) ? id?.ToString() : null;

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.Response.WriteAsJsonAsync(new
                {
                    status = 429,
                    error = "RATE_LIMITED",
                    message = "Too many requests. Please retry later.",
                    retry_after = retryAfterSeconds > 0 ? retryAfterSeconds : (int?)null,
                    request_id = requestId
                }, token);
            };
        });

        return services;
    }

    private static string PartitionKey(HttpContext context, int ipv4Prefix, int ipv6Prefix, bool preferUser)
    {
        if (preferUser)
        {
            var userId = context.User.FindFirst("UserId")?.Value;
            if (!string.IsNullOrEmpty(userId))
                return "user:" + userId;
        }

        var ipKey = RegistrationIp.ToPrefixKey(context.Connection.RemoteIpAddress, ipv4Prefix, ipv6Prefix);
        return "ip:" + ipKey;
    }

    private static FixedWindowRateLimiterOptions FixedWindow(int permitLimit, TimeSpan window) => new()
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0
    };

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(Math.Max(1, seconds));
}
