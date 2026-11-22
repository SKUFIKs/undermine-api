using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.Security;
using UnderMineApi.Extensions;

namespace UnderMineApi.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/v{version:apiVersion}/admin")]
    [Authorize(Roles = "admin")]
    [EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
    public class AdminController : ControllerBase
    {

        private readonly ILogger<AdminController> _logger;
        private readonly ConfigCacheService _cache;
        private readonly AppDbContext _context;
        private readonly IUserService _userService;
        private readonly IServerService _serverService;
        private readonly AdminAuditService _audit;

        public AdminController(ILogger<AdminController> logger,
                               ConfigCacheService cache,
                               AppDbContext context,
                               IUserService userService,
                               IServerService serverService,
                               AdminAuditService audit)
        {
            _logger = logger;
            _cache = cache;
            _context = context;
            _userService = userService;
            _serverService = serverService;
            _audit = audit;
        }

        private string GetActor() => LogSanitizer.Clean(User?.Identity?.Name ?? User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "system");

        private long? GetActorId()
        {
            var value = User?.FindFirst("UserId")?.Value
                        ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return long.TryParse(value, out var id) ? id : null;
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetAllUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                _logger.LogInformation("Admin {Actor} fetching users list. Page: {Page}, PageSize: {PageSize}", GetActor(), page, pageSize);
                var result = await _userService.GetPagedUsersAsync(page, pageSize, cancellationToken);
                _logger.LogInformation("Admin {Actor} fetched users list", GetActor());
                return Ok(result);
            }
        }

        [HttpGet("users/search")]
        public async Task<IActionResult> SearchUsers([FromQuery] string query, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                if (string.IsNullOrWhiteSpace(query)) return BadRequest();
                _logger.LogInformation("Admin {Actor} searching users with query: {Query}", GetActor(), LogSanitizer.Clean(query));
                var users = await _userService.SearchUsersAsync(query, 10, cancellationToken);
                _logger.LogInformation("Admin {Actor} completed user search", GetActor());
                return Ok(users);
            }
        }

        [HttpPost("users/{id}/ban")]
        public async Task<IActionResult> BanUser(long id, [FromBody] BanRequestDto request, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();
                _logger.LogInformation("Admin {Actor} banning user {UserId}. Reason: {Reason}, Duration: {Duration} days",
                    actor, LogSanitizer.Clean(id), LogSanitizer.Clean(request.Reason), LogSanitizer.Clean(request.DurationDays));
                var result = await _userService.BanUserAsync(id, request, actor, cancellationToken);
                if (!result.Success) return result.ToActionResult();
                _logger.LogInformation("Admin {Actor} successfully banned user {UserId}", actor, id);
                return Ok(new { Message = "User has been banned" });
            }
        }

        [HttpPost("users/{id}/unban")]
        public async Task<IActionResult> UnbanUser(long id, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();
                _logger.LogInformation("Admin {Actor} unbanning user {UserId}", actor, id);
                var result = await _userService.UnbanUserAsync(id, actor, cancellationToken);
                if (!result.Success) return result.ToActionResult();
                _logger.LogInformation("Admin {Actor} successfully unbanned user {UserId}", actor, id);
                return Ok(new { Message = "User has been unbanned" });
            }
        }

        [HttpPatch("users/{id}/role")]
        public async Task<IActionResult> ChangeRole(long id, [FromBody] AdminChangeRoleDto request, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                if (string.IsNullOrWhiteSpace(request.Role))
                    return BadRequest(new { Error = "INVALID_ROLE", Message = "Role cannot be empty." });

                var actor = GetActor();

                _logger.LogInformation("Admin {Actor} changing role for user {UserId} to {NewRole}", actor, LogSanitizer.Clean(id), LogSanitizer.Clean(request.Role));

                var result = await _userService.ChangeUserRoleAsync(id, request.Role, actor, cancellationToken);

                if (!result.Success)
                {
                    if (result.Error == "USER_NOT_FOUND")
                    {
                        _logger.LogWarning("Admin {Actor} failed to change role: user {UserId} not found", actor, id);
                        return NotFound(new { Error = result.Error, Message = "User not found." });
                    }

                    if (result.Error == "INVALID_ROLE")
                    {
                        _logger.LogWarning("Admin {Actor} failed to change role: invalid role {Role}", actor, LogSanitizer.Clean(request.Role));
                        return BadRequest(new { Error = result.Error, Message = "Invalid role. Available roles: 'admin', 'user'." });
                    }

                    if (result.Error == "LAST_ADMIN")
                    {
                        _logger.LogWarning("Admin {Actor} failed to demote the last administrator {UserId}", actor, id);
                        return Conflict(new { Error = result.Error, Message = "Cannot demote the last administrator. Promote another admin first." });
                    }

                    _logger.LogError("Admin {Actor} failed to change role for user {UserId}: {Error}", actor, id, result.Error);
                    return StatusCode(500, new { Error = "INTERNAL_ERROR", Message = "An internal server error occurred." });
                }

                _logger.LogInformation("Admin {Actor} successfully changed role for user {UserId} to {NewRole}", actor, LogSanitizer.Clean(id), LogSanitizer.Clean(request.Role.ToLower()));
                return Ok(new { Message = $"User role successfully changed to {request.Role.ToLower()}" });
            }
        }

        [HttpPatch("users/{id}/change-password")]
        public async Task<IActionResult> ChangePassword(long id, [FromBody] AdminChangePasswordDto request, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                if (!PasswordPolicy.TryValidate(request.NewPassword, out var passwordError))
                {
                    return BadRequest(new
                    {
                        Error = "INVALID_PASSWORD",
                        Message = passwordError
                    });
                }

                var actor = GetActor();

                _logger.LogInformation("Admin {Actor} changing password for user {UserId}", actor, id);

                var result = await _userService.ChangePasswordAsync(id, request.NewPassword, actor, cancellationToken);

                if (!result.Success)
                {
                    _logger.LogWarning("Admin {Actor} failed to change password for user {UserId}: {Error}", actor, id, result.Error);
                    return result.ToActionResult();
                }

                _logger.LogInformation("Admin {Actor} successfully changed password for user {UserId}", actor, id);
                return Ok(new { Message = "Password changed successfully" });
            }
        }

        [HttpPost("users/{id}/delete")]
        public async Task<IActionResult> DeleteUser(long id, [FromBody] DeleteUserRequestDto request, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();
                var actorId = GetActorId();
                if (actorId == null)
                    return Unauthorized(new { Error = "UNAUTHORIZED", Message = "The current administrator could not be identified." });

                _logger.LogInformation(
                    "Admin {Actor} permanently deleting user {UserId}. Reason: {Reason}",
                    actor, id, LogSanitizer.Clean(request.Reason));

                var result = await _userService.DeleteUserAsync(
                    id,
                    request.ConfirmUsername,
                    request.Reason,
                    actorId.Value,
                    actor,
                    cancellationToken);

                if (!result.Success)
                {
                    _logger.LogWarning("Admin {Actor} failed to delete user {UserId}: {Error}", actor, id, result.Error);

                    return result.Error switch
                    {
                        "USER_NOT_FOUND" => NotFound(new
                        {
                            Error = result.Error,
                            Message = "User not found."
                        }),
                        "CONFIRMATION_MISMATCH" => BadRequest(new
                        {
                            Error = result.Error,
                            Message = "Type the username exactly to confirm deletion."
                        }),
                        "CANNOT_DELETE_SELF" => BadRequest(new
                        {
                            Error = result.Error,
                            Message = "You cannot delete your own account."
                        }),
                        "CANNOT_DELETE_ADMIN" => Conflict(new
                        {
                            Error = result.Error,
                            Message = "Demote the administrator to user before deleting the account."
                        }),
                        "INVALID_REASON" => BadRequest(new
                        {
                            Error = result.Error,
                            Message = "Provide a reason of at least 5 characters."
                        }),
                        "USER_HAS_REFERENCES" => Conflict(new
                        {
                            Error = result.Error,
                            Message = "The account still has related records and cannot be deleted."
                        }),
                        _ => StatusCode(500, new
                        {
                            Error = "INTERNAL_ERROR",
                            Message = "An internal server error occurred."
                        })
                    };
                }

                _logger.LogInformation("Admin {Actor} permanently deleted user {UserId}", actor, id);
                return Ok(new { Message = "User deleted" });
            }
        }

        [HttpPost("servers")]
        public async Task<IActionResult> CreateServer([FromBody] AdminServerUpsertDto dto, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();

                _logger.LogInformation("Admin {Actor} creating server: {ServerName}", actor, LogSanitizer.Clean(dto.ServerName));

                var (success, server, error) = await _serverService.CreateServerAsync(dto, actor, cancellationToken);

                if (!success)
                {
                    if (error == "SERVER_ALREADY_EXISTS")
                    {
                        _logger.LogWarning("Admin {Actor} failed to create server: server {ServerName} already exists", actor, LogSanitizer.Clean(dto.ServerName));
                        return Conflict(new { Error = error, Message = "A server with this name already exists." });
                    }

                    if (error == "BUILD_NOT_FOUND")
                    {
                        _logger.LogWarning("Admin {Actor} failed to create server {ServerName}: build {BuildId} not found",
                            actor, LogSanitizer.Clean(dto.ServerName), LogSanitizer.Clean(dto.BuildId));
                        return BadRequest(new { Error = error, Message = "The selected build does not exist or is not completed yet." });
                    }

                    _logger.LogError("Admin {Actor} failed to create server {ServerName}: {Error}", actor, LogSanitizer.Clean(dto.ServerName), LogSanitizer.Clean(error));
                    return StatusCode(500, new { Error = "INTERNAL_ERROR" });
                }

                await _cache.RefreshCacheAsync();

                _logger.LogInformation("Admin {Actor} successfully created server: {ServerName}", actor, LogSanitizer.Clean(dto.ServerName));
                return Ok(server);
            }
        }

        [HttpPut("servers/{id}")]
        public async Task<IActionResult> UpdateServer(long id, [FromBody] AdminServerUpsertDto dto, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();

                _logger.LogInformation("Admin {Actor} updating server {ServerId}: {ServerName}", actor, LogSanitizer.Clean(id), LogSanitizer.Clean(dto.ServerName));

                var result = await _serverService.UpdateServerAsync(id, dto, actor, cancellationToken);

                if (!result.Success)
                {
                    _logger.LogWarning("Admin {Actor} failed to update server {ServerId}: {Error}", actor, id, result.Error);
                    return result.ToActionResult();
                }

                await _cache.RefreshCacheAsync();

                _logger.LogInformation("Admin {Actor} successfully updated server {ServerId}: {ServerName}", actor, LogSanitizer.Clean(id), LogSanitizer.Clean(dto.ServerName));
                return Ok(new { Message = "Server updated" });
            }
        }

        [HttpDelete("servers/{id}")]
        public async Task<IActionResult> DeleteServer(long id, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();

                _logger.LogInformation("Admin {Actor} deleting server {ServerId}", actor, id);

                var result = await _serverService.DeleteServerAsync(id, actor, cancellationToken);

                if (!result.Success)
                {
                    _logger.LogWarning("Admin {Actor} failed to delete server {ServerId}: {Error}", actor, id, result.Error);
                    return result.ToActionResult();
                }

                await _cache.RefreshCacheAsync();

                _logger.LogInformation("Admin {Actor} successfully deleted server {ServerId}", actor, id);
                return Ok(new { Message = "Server deleted" });
            }
        }

        [HttpPost("status/maintenance")]
        public async Task<IActionResult> SetMaintenanceMode([FromQuery] bool enabled)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                try
                {
                    _logger.LogInformation("Admin {Actor} setting maintenance mode to {Enabled}", GetActor(), enabled);

                    var maintenanceSetting = await _context.GlobalSettings
                        .FirstOrDefaultAsync(s => s.Key == "Maintenance");

                    if (maintenanceSetting == null)
                    {
                        maintenanceSetting = new GlobalSetting
                        {
                            Key = "Maintenance",
                            Value = enabled.ToString().ToLower()
                        };
                        _context.GlobalSettings.Add(maintenanceSetting);
                    }
                    else
                    {
                        maintenanceSetting.Value = enabled.ToString().ToLower();
                    }

                    await _context.SaveChangesAsync();
                    await _cache.RefreshCacheAsync();

                    await _audit.LogAsync(
                        GetActor(), "set_maintenance", "global_settings", null,
                        enabled ? "enabled" : "disabled");

                    _logger.LogInformation("Admin {Actor} successfully set maintenance mode to {Enabled}", GetActor(), enabled);

                    return Ok(new
                    {
                        Message = enabled ? "Maintenance mode enabled" : "Maintenance mode disabled",
                        IsMaintenance = enabled
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error changing maintenance mode: {ErrorMessage}", ex.Message);
                    return StatusCode(500, new { Error = "INTERNAL_ERROR" });
                }
            }
        }

        [HttpGet("status/maintenance")]
        public async Task<IActionResult> GetMaintenanceMode(CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var setting = await _context.GlobalSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Key == "Maintenance", cancellationToken);

                var enabled = setting?.Value == "true";
                _logger.LogInformation("Admin {Actor} requested maintenance state: {Enabled}", GetActor(), enabled);

                return Ok(new { IsMaintenance = enabled });
            }
        }

        [HttpGet("servers")]
        public async Task<IActionResult> GetAllServersAdmin(CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                _logger.LogInformation("Admin {Actor} fetching all servers", GetActor());
                var response = await _serverService.GetAllServersAdminAsync(cancellationToken);
                _logger.LogInformation("Admin {Actor} fetched all servers", GetActor());
                return Ok(response);
            }
        }

        [HttpGet("servers/{id:long}")]
        public async Task<IActionResult> GetServerAdmin(long id, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var server = await _serverService.GetServerAdminAsync(id, cancellationToken);

                if (server == null)
                    return NotFound(new { Error = "SERVER_NOT_FOUND", Message = "Server not found." });

                return Ok(server);
            }
        }
    }
}
