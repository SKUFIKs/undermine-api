using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UnderMineApi.Services;

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

        var clientIp = LogSanitizer.Clean(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var method = LogSanitizer.Clean(context.Request.Method);
        var path = LogSanitizer.Clean(context.Request.Path.ToString());
        var userAgent = LogSanitizer.Clean(context.Request.Headers.UserAgent.ToString());
        if (userAgent.Length > 100)
            userAgent = userAgent[..100];

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = requestId,
            ["Method"] = method,
            ["Path"] = path,
            ["ClientIP"] = clientIp
        }))
        {
            _logger.LogInformation(
                "Request started: {Method} {Path} [RequestId: {RequestId}] [ClientIP: {ClientIP}] [UserAgent: {UserAgent}]",
                method,
                path,
                requestId,
                clientIp,
                userAgent);

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
                    method,
                    path,
                    requestId,
                    LogSanitizer.Clean(sw.ElapsedMilliseconds),
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
                method,
                path,
                LogSanitizer.Clean(statusCode),
                requestId,
                LogSanitizer.Clean(sw.ElapsedMilliseconds),
                clientIp);
        }
    }
}
