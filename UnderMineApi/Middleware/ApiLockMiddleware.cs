using UnderMineApi.Services;

namespace UnderMineApi.Middleware
{
    public class ApiLockMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ApiLockMiddleware> _logger;

        public ApiLockMiddleware(RequestDelegate next, ILogger<ApiLockMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, ConfigCacheService cache)
        {
            // Maintenance mode only locks the API surface: health checks, the admin panel
            // static files and anything else outside /api must stay reachable.
            var isApiRequest = context.Request.Path.StartsWithSegments("/api");

            if (!cache.IsApiLocked || !isApiRequest)
            {
                await _next(context);
                return;
            }

            // Authenticated admins bypass the lock. Without this the admin panel could not
            // be used to switch maintenance off again.
            if (context.User?.Identity?.IsAuthenticated == true && context.User.IsInRole("admin"))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = 503;
            var requestId = context.Items.ContainsKey("RequestId")
                ? context.Items["RequestId"]?.ToString()
                : Guid.NewGuid().ToString();

            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["RequestId"] = requestId,
                ["Method"] = context.Request.Method,
                ["Path"] = context.Request.Path.ToString(),
                ["ClientIP"] = clientIp
            }))
            {
                _logger.LogWarning(
                    "Request rejected: API is in maintenance mode. {Method} {Path} [RequestId: {RequestId}] [ClientIP: {ClientIP}]",
                    context.Request.Method,
                    context.Request.Path,
                    requestId,
                    clientIp);
            }

            await context.Response.WriteAsJsonAsync(new
            {
                Error = "MAINTENANCE",
                Message = "Server is under maintenance. Please try again later.",
                request_id = requestId
            });
        }
    }
}
