using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;

namespace UnderMineApi.Middleware;

/// <summary>
/// Re-validates the database state behind an already issued JWT: the account must still exist,
/// must not have an active ban, and its role must match the role claim. This closes the gap where
/// a 30-day token kept working after a ban/demotion; the check is cached for
/// <c>Access:BanCacheSeconds</c> so it does not hit the database on every request.
/// </summary>
public class AccountBanMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AccountBanMiddleware> _logger;

    public AccountBanMiddleware(RequestDelegate next, ILogger<AccountBanMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IUserAccessService access)
    {
        if (!ShouldCheck(context))
        {
            await _next(context);
            return;
        }

        var userIdValue = context.User.FindFirst("UserId")?.Value
                          ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // A token without a usable user id is rejected later by the authorization layer.
        if (!long.TryParse(userIdValue, out var userId))
        {
            await _next(context);
            return;
        }

        var snapshot = await access.GetSnapshotAsync(userId, context.RequestAborted);

        if (!snapshot.Exists)
        {
            await RejectAsync(
                context, userId, StatusCodes.Status401Unauthorized, "USER_NOT_FOUND",
                "The account for this token no longer exists. Please sign in again.");
            return;
        }

        if (snapshot.Ban.IsBanned)
        {
            await RejectAsync(
                context, userId, StatusCodes.Status403Forbidden, "ACCOUNT_BANNED",
                BuildBanMessage(snapshot.Ban), snapshot.Ban);
            return;
        }

        var tokenRole = context.User.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(tokenRole, snapshot.Role, StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(
                context, userId, StatusCodes.Status401Unauthorized, "ROLE_CHANGED",
                "Your permissions changed. Please sign in again.");
            return;
        }

        // Tokens issued before this claim existed have no "token_version"; they are treated as
        // version 0, which matches fresh databases and keeps existing sessions working until the
        // first password change (which increments the counter).
        var tokenVersionValue = context.User.FindFirst("token_version")?.Value;
        var tokenVersion = int.TryParse(tokenVersionValue, out var parsedVersion) ? parsedVersion : 0;
        if (tokenVersion != snapshot.TokenVersion)
        {
            await RejectAsync(
                context, userId, StatusCodes.Status401Unauthorized, "TOKEN_REVOKED",
                "This session is no longer valid. Please sign in again.");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Only endpoints that actually require authorization are checked. Public endpoints (login,
    /// registration, anonymous manifests) stay reachable for everyone, including banned users.
    /// </summary>
    private static bool ShouldCheck(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return false;

        if (!context.Request.Path.StartsWithSegments("/api"))
            return false;

        var endpoint = context.GetEndpoint();
        if (endpoint == null)
            return false;

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
            return false;

        return endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
    }

    private async Task RejectAsync(
        HttpContext context,
        long userId,
        int statusCode,
        string error,
        string message,
        BanStatus? ban = null)
    {
        if (context.Response.HasStarted)
            return;

        var requestId = context.Items.TryGetValue("RequestId", out var id)
            ? id?.ToString()
            : null;

        _logger.LogWarning(
            "Blocked request for user {UserId}: {Error} ({Method} {Path}) [RequestId: {RequestId}]",
            LogSanitizer.Clean(userId),
            LogSanitizer.Clean(error),
            LogSanitizer.Clean(context.Request.Method),
            LogSanitizer.Clean(context.Request.Path.ToString()),
            LogSanitizer.Clean(requestId));

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            status = statusCode,
            error,
            message,
            reason = ban?.Reason,
            expires_at = ban?.ExpiresAt,
            request_id = requestId
        }, context.RequestAborted);
    }

    private static string BuildBanMessage(BanStatus ban)
    {
        var until = ban.ExpiresAt is null
            ? "permanently"
            : $"until {DateTimeOffset.FromUnixTimeSeconds(ban.ExpiresAt.Value).UtcDateTime:u} UTC";

        return string.IsNullOrWhiteSpace(ban.Reason)
            ? $"This account is banned {until}."
            : $"This account is banned {until}. Reason: {ban.Reason}";
    }
}
