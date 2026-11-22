using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Controllers;

/// <summary>
/// Current-user capabilities for the admin panel and the administrator-only editor for the
/// permission sets of the moderator and user roles. The admin role itself is implicit and
/// always has every permission.
/// </summary>
[ApiController]
[Route("api/v1.0/admin")]
[EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
public class AdminAccessController : ControllerBase
{
    private readonly IPermissionService _permissions;
    private readonly AdminAuditService _audit;
    private readonly ILogger<AdminAccessController> _logger;

    public AdminAccessController(
        IPermissionService permissions,
        AdminAuditService audit,
        ILogger<AdminAccessController> logger)
    {
        _permissions = permissions;
        _audit = audit;
        _logger = logger;
    }

    private string CurrentRole =>
        User.FindFirst(ClaimTypes.Role)?.Value?.Trim().ToLowerInvariant() ?? string.Empty;

    private string Actor =>
        LogSanitizer.Clean(User.Identity?.Name ?? User.FindFirst(ClaimTypes.Name)?.Value ?? "system");

    /// <summary>
    /// Returns the permissions of the signed-in account. The admin panel uses this to hide
    /// pages and actions the account cannot use.
    /// GET /api/v1.0/admin/me
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentAccess(CancellationToken cancellationToken = default)
    {
        var role = CurrentRole;
        var isAdmin = string.Equals(role, Roles.Admin, StringComparison.Ordinal);
        var granted = await _permissions.GetPermissionsAsync(role, cancellationToken);
        var effective = granted.ToHashSet(StringComparer.Ordinal);

        if (isAdmin)
        {
            effective.UnionWith(Permissions.AllKeys);
            effective.Add(Permissions.UsersViewPolicy);
            effective.Add(Permissions.SettingsManagePolicy);
        }
        else if (Permissions.UserManagement.Any(effective.Contains))
        {
            // The users page is useful when at least one user-management action is available.
            effective.Add(Permissions.UsersViewPolicy);
        }

        return Ok(new
        {
            Username = Actor,
            Role = role,
            IsAdmin = isAdmin,
            Permissions = effective.OrderBy(p => p, StringComparer.Ordinal).ToArray()
        });
    }

    /// <summary>
    /// Returns every permission and the current permission set of each configurable role.
    /// GET /api/v1.0/admin/permissions
    /// </summary>
    [HttpGet("permissions")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> GetRolePermissions(CancellationToken cancellationToken = default)
    {
        var roles = new List<object>();

        foreach (var role in Roles.Configurable)
        {
            var granted = await _permissions.GetPermissionsAsync(role, cancellationToken);
            roles.Add(new
            {
                Role = role,
                Label = RoleLabel(role),
                Permissions = granted.OrderBy(p => p, StringComparer.Ordinal).ToArray()
            });
        }

        return Ok(new
        {
            AdminRole = Roles.Admin,
            AdminPermissions = Permissions.AllKeys.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            Permissions = Permissions.All.Select(p => new
            {
                p.Key,
                p.Label,
                p.Description
            }),
            Roles = roles
        });
    }

    /// <summary>
    /// Replaces the permissions of the moderator or user role.
    /// PUT /api/v1.0/admin/permissions/{role}
    /// </summary>
    [HttpPut("permissions/{role}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateRolePermissions(
        string role,
        [FromBody] RolePermissionsUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Permissions == null)
            return BadRequest(new { Error = "INVALID_PERMISSION", Message = "A permission list is required." });

        var (success, error) = await _permissions.SetRolePermissionsAsync(
            role, request.Permissions, cancellationToken);

        if (!success)
        {
            _logger.LogWarning(
                "Admin {Actor} failed to update permissions for role {Role}: {Error}",
                Actor, LogSanitizer.Clean(role), error);

            return error switch
            {
                "ADMIN_ROLE_IMMUTABLE" => BadRequest(new
                {
                    Error = error,
                    Message = "The administrator role always has every permission and cannot be edited."
                }),
                "INVALID_ROLE" => BadRequest(new
                {
                    Error = error,
                    Message = "Only the moderator and user roles can be configured."
                }),
                "INVALID_PERMISSION" => BadRequest(new
                {
                    Error = error,
                    Message = "The permission list contains an unknown permission."
                }),
                _ => StatusCode(500, new { Error = "INTERNAL_ERROR", Message = "An internal server error occurred." })
            };
        }

        var updated = await _permissions.GetPermissionsAsync(role, cancellationToken);
        await _audit.LogAsync(
            Actor,
            "update_role_permissions",
            "role",
            null,
            $"{role.ToLowerInvariant()}: {string.Join(", ", updated.OrderBy(p => p, StringComparer.Ordinal))}",
            cancellationToken);

        _logger.LogInformation(
            "Admin {Actor} updated permissions for role {Role}.",
            Actor, LogSanitizer.Clean(role));

        return Ok(new
        {
            Role = role.Trim().ToLowerInvariant(),
            Permissions = updated.OrderBy(p => p, StringComparer.Ordinal).ToArray()
        });
    }

    private static string RoleLabel(string role) => role switch
    {
        Roles.Moderator => "Moderator",
        Roles.User => "User",
        _ => role
    };
}
