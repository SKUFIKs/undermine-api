using Microsoft.AspNetCore.Diagnostics;

namespace UnderMineApi.Middleware
{
    public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var requestId = httpContext.Items.ContainsKey("RequestId")
                ? httpContext.Items["RequestId"]?.ToString()
                : Guid.NewGuid().ToString();

            var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["RequestId"] = requestId,
                ["Method"] = httpContext.Request.Method,
                ["Path"] = httpContext.Request.Path.ToString(),
                ["ClientIP"] = clientIp
            }))
            {
                logger.LogError(
                    exception,
                    "Unhandled exception: {Method} {Path} [RequestId: {RequestId}] [Exception: {ExceptionType}] [ClientIP: {ClientIP}]",
                    httpContext.Request.Method,
                    httpContext.Request.Path,
                    requestId,
                    exception.GetType().Name,
                    clientIp);
            }

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            httpContext.Response.ContentType = "application/json";

            var response = new
            {
                status = 500,
                request_id = requestId,
                message = "Internal server error. Please try again later."
            };

            await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

            return true;
        }
    }
}
