using Microsoft.AspNetCore.Diagnostics;
using UnderMineApi.Services;

namespace UnderMineApi.Middleware
{
    public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var requestId = LogSanitizer.Clean(httpContext.Items.ContainsKey("RequestId")
                ? httpContext.Items["RequestId"]?.ToString()
                : Guid.NewGuid().ToString());

            var method = LogSanitizer.Clean(httpContext.Request.Method);
            var path = LogSanitizer.Clean(httpContext.Request.Path.ToString());
            var clientIp = LogSanitizer.Clean(httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var exceptionType = LogSanitizer.Clean(exception.GetType().Name);

            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["RequestId"] = requestId,
                ["Method"] = method,
                ["Path"] = path,
                ["ClientIP"] = clientIp
            }))
            {
                logger.LogError(
                    exception,
                    "Unhandled exception: {Method} {Path} [RequestId: {RequestId}] [Exception: {ExceptionType}] [ClientIP: {ClientIP}]",
                    method,
                    path,
                    requestId,
                    exceptionType,
                    clientIp);
            }

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            httpContext.Response.ContentType = "application/json";

            var response = new
            {
                status = 500,
                error = "INTERNAL_ERROR",
                message = "Internal server error. Please try again later.",
                request_id = requestId
            };

            await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

            return true;
        }
    }
}
