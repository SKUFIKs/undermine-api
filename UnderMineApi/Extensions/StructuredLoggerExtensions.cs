using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UnderMineApi.Extensions;

public static class StructuredLoggerExtensions
{
    public static void LogStructuredInformation(this ILogger logger, string message, HttpContext? context = null, int? statusCode = null, long? durationMs = null, object? extra = null)
    {
        var requestId = context?.Items.ContainsKey("RequestId") == true ? context?.Items["RequestId"]?.ToString() : null;

        var log = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            ["level"] = "INFO",
            ["request_id"] = requestId,
            ["method"] = context?.Request?.Method,
            ["path"] = context?.Request?.Path.ToString(),
            ["status_code"] = statusCode,
            ["duration_ms"] = durationMs,
            ["message"] = message,
            ["extra"] = extra
        };

        logger.LogInformation(JsonSerializer.Serialize(log));
    }

    public static void LogStructuredWarning(this ILogger logger, string message, HttpContext? context = null, int? statusCode = null, object? extra = null)
    {
        var requestId = context?.Items.ContainsKey("RequestId") == true ? context?.Items["RequestId"]?.ToString() : null;

        var log = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            ["level"] = "WARN",
            ["request_id"] = requestId,
            ["method"] = context?.Request?.Method,
            ["path"] = context?.Request?.Path.ToString(),
            ["status_code"] = statusCode,
            ["message"] = message,
            ["extra"] = extra
        };

        logger.LogWarning(JsonSerializer.Serialize(log));
    }

    public static void LogStructuredError(this ILogger logger, Exception ex, string? message = null, HttpContext? context = null, int? statusCode = null, object? extra = null)
    {
        var requestId = context?.Items.ContainsKey("RequestId") == true ? context?.Items["RequestId"]?.ToString() : null;

        var log = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            ["level"] = "ERROR",
            ["request_id"] = requestId,
            ["method"] = context?.Request?.Method,
            ["path"] = context?.Request?.Path.ToString(),
            ["status_code"] = statusCode,
            ["message"] = message ?? ex.Message,
            ["exception"] = ex.ToString(),
            ["extra"] = extra
        };

        logger.LogError(JsonSerializer.Serialize(log));
    }
}
