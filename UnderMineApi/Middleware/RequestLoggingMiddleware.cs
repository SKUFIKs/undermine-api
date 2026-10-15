using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UnderMineApi.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString();
        context.Items["RequestId"] = requestId;

        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = context.Request.Headers.UserAgent.ToString();

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = requestId,
            ["Method"] = context.Request.Method,
            ["Path"] = context.Request.Path.ToString(),
            ["ClientIP"] = clientIp
        }))
        {
            _logger.LogInformation(
                "Request started: {Method} {Path} [RequestId: {RequestId}] [ClientIP: {ClientIP}] [UserAgent: {UserAgent}]",
                context.Request.Method,
                context.Request.Path,
                requestId,
                clientIp,
                userAgent.Length > 100 ? userAgent[..100] : userAgent);

            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(
                    ex,
                    "Request failed: {Method} {Path} [RequestId: {RequestId}] [Duration: {DurationMs}ms] [ClientIP: {ClientIP}]",
                    context.Request.Method,
                    context.Request.Path,
                    requestId,
                    sw.ElapsedMilliseconds,
                    clientIp);
                throw;
            }
            finally
            {
                sw.Stop();
            }

            var statusCode = context.Response?.StatusCode ?? 0;
            var level = statusCode >= 400 ? LogLevel.Warning : LogLevel.Information;

            _logger.Log(
                level,
                "Request completed: {Method} {Path} [StatusCode: {StatusCode}] [RequestId: {RequestId}] [Duration: {DurationMs}ms] [ClientIP: {ClientIP}]",
                context.Request.Method,
                context.Request.Path,
                statusCode,
                requestId,
                sw.ElapsedMilliseconds,
                clientIp);
        }
    }
}
