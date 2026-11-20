using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace UnderMineApi.Middleware;

/// <summary>
/// Rewrites every MVC error result into one envelope:
/// <c>{ status, error, message, request_id, errors? }</c>.
///
/// Historically controllers returned five different shapes (Error/Message, status/message,
/// error/message, plain text, empty body). Normalizing centrally keeps the existing controller
/// code working while giving clients a single error contract.
/// </summary>
public sealed class ApiErrorNormalizationFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var statusCode = ResolveStatusCode(context);
        if (statusCode < StatusCodes.Status400BadRequest)
            return;

        var value = (context.Result as ObjectResult)?.Value;
        var requestId = context.HttpContext.Items.TryGetValue("RequestId", out var id)
            ? id?.ToString()
            : null;

        context.Result = new ObjectResult(BuildEnvelope(statusCode, value, requestId))
        {
            StatusCode = statusCode
        };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
        // Nothing to do.
    }

    private static int ResolveStatusCode(ResultExecutingContext context)
    {
        if (context.Result is IStatusCodeActionResult statusResult && statusResult.StatusCode.HasValue)
            return statusResult.StatusCode.Value;

        // FileResult (e.g. PhysicalFileResult) is not an IStatusCodeActionResult and must be
        // left alone: it is a successful response, not an error. Returning 400 here used to
        // break every file download (build editor, Java runtime, ...).
        return context.HttpContext.Response.StatusCode;
    }

    private static Dictionary<string, object?> BuildEnvelope(int statusCode, object? value, string? requestId)
    {
        var fallbackMessage = ReasonPhrases.GetReasonPhrase(statusCode);
        if (string.IsNullOrWhiteSpace(fallbackMessage))
            fallbackMessage = "Request failed.";

        var envelope = new Dictionary<string, object?>
        {
            ["status"] = statusCode,
            ["error"] = $"HTTP_{statusCode}",
            ["message"] = fallbackMessage,
            ["request_id"] = requestId
        };

        switch (value)
        {
            case null:
                return envelope;

            case string text when !string.IsNullOrWhiteSpace(text):
                envelope["message"] = text;
                return envelope;

            case ProblemDetails problem:
                envelope["error"] = problem is ValidationProblemDetails
                    ? "VALIDATION_ERROR"
                    : "REQUEST_FAILED";
                envelope["message"] = problem.Detail ?? problem.Title ?? fallbackMessage;

                if (problem is ValidationProblemDetails validation && validation.Errors.Count > 0)
                    envelope["errors"] = validation.Errors;

                return envelope;
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(value);
            if (element.ValueKind != JsonValueKind.Object)
                return envelope;

            foreach (var property in element.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "error":
                        if (property.Value.ValueKind == JsonValueKind.String)
                            envelope["error"] = property.Value.GetString();
                        break;

                    case "message":
                        if (property.Value.ValueKind == JsonValueKind.String)
                            envelope["message"] = property.Value.GetString();
                        break;

                    case "request_id":
                    case "requestid":
                        if (property.Value.ValueKind == JsonValueKind.String)
                            envelope["request_id"] = property.Value.GetString();
                        break;

                    case "errors":
                        envelope["errors"] = property.Value.Clone();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // A non-serializable error payload: keep the status/reason fallback.
        }

        return envelope;
    }
}
