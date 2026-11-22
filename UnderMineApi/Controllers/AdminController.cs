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
    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
    public class AdminController : ControllerBase
    {

        private readonly ILogger<AdminController> _logger;
        private readonly ConfigCacheService _cache;
        private readonly AppDbContext _context;
        private readonly IUserService _userService;
        private readonly IServerService _serverService;
        private readonly AdminAuditService _audit;
        private readonly BanEvidenceService _banEvidence;

        public AdminController(ILogger<AdminController> logger,
                               ConfigCacheService cache,
                               AppDbContext context,
                               IUserService userService,
                               IServerService serverService,
                               AdminAuditService audit,
                               BanEvidenceService banEvidence)
        {
            _logger = logger;
            _cache = cache;
            _context = context;
            _userService = userService;
            _serverService = serverService;
            _audit = audit;
            _banEvidence = banEvidence;
        }

        private string GetActor() => LogSanitizer.Clean(User?.Identity?.Name ?? User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "system");

        private long? GetActorId()
        {
            var value = User?.FindFirst("UserId")?.Value
                        ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return long.TryParse(value, out var id) ? id : null;
        }

        private bool IsAdmin => User?.IsInRole(Roles.Admin) == true;

        private string CurrentRole =>
            User?.FindFirst(ClaimTypes.Role)?.Value?.Trim().ToLowerInvariant() ?? string.Empty;

        private static int RoleRank(string? role) => role?.Trim().ToLowerInvariant() switch
        {
            Roles.Admin => 3,
            Roles.Moderator => 2,
            Roles.User => 1,
            _ => 0
        };

        private Task<string?> GetTargetRoleAsync(long id, CancellationToken cancellationToken) =>
            _context.McAuthAccounts
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => u.Role)
                .FirstOrDefaultAsync(cancellationToken);

        private bool CanManageTarget(string? targetRole) =>
            IsAdmin || RoleRank(targetRole) <= RoleRank(CurrentRole);

        private IActionResult HigherRoleForbidden() =>
            StatusCode(StatusCodes.Status403Forbidden, new
            {
                Error = "INSUFFICIENT_ROLE",
                Message = "You cannot manage an account with a higher role than your own."
            });

        [HttpGet("users")]
        [Authorize(Policy = Permissions.UsersViewPolicy)]
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
        [Authorize(Policy = Permissions.UsersViewPolicy)]
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

        [HttpGet("users/{id:long}/statistics")]
        [Authorize(Policy = Permissions.UsersViewPolicy)]
        public async Task<IActionResult> GetUserStatistics(long id, CancellationToken cancellationToken = default)
        {
            var targetRole = await GetTargetRoleAsync(id, cancellationToken);
            if (targetRole != null && !CanManageTarget(targetRole))
                return HigherRoleForbidden();

            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                _logger.LogInformation("Admin {Actor} fetching statistics for user {UserId}", GetActor(), id);

                var statistics = await _userService.GetUserStatisticsAsync(id, cancellationToken);
                if (statistics == null)
                    return NotFound(new { Error = "USER_NOT_FOUND", Message = "User not found." });

                return Ok(statistics);
            }
        }

        [HttpPost("users/{id}/ban")]
        [Authorize(Policy = Permissions.UsersBan)]
        [RequestSizeLimit(50 * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 50 * 1024 * 1024)]
        public async Task<IActionResult> BanUser(
            long id,
            [FromForm] BanRequestDto request,
            [FromForm] List<IFormFile>? evidence,
            CancellationToken cancellationToken = default)
        {
            var targetRole = await GetTargetRoleAsync(id, cancellationToken);
            if (targetRole != null && !CanManageTarget(targetRole))
                return HigherRoleForbidden();

            var reason = (request.Reason ?? string.Empty).Trim();
            if (reason.Length < 10 || reason.Length > 2000)
            {
                return BadRequest(new
                {
                    Error = "INVALID_REASON",
                    Message = "Describe the reason in 10–2000 characters."
                });
            }

            var now = DateTimeOffset.UtcNow;
            if (request.ExpiresAt.HasValue &&
                (request.ExpiresAt.Value <= now.ToUnixTimeSeconds() ||
                 request.ExpiresAt.Value > now.AddYears(100).ToUnixTimeSeconds()))
            {
                return BadRequest(new
                {
                    Error = "INVALID_EXPIRY",
                    Message = "Choose a ban end date and time in the future."
                });
            }

            IReadOnlyList<UploadedBanEvidence> uploadedEvidence;
            try
            {
                uploadedEvidence = await _banEvidence.ReadAsync(evidence, cancellationToken);
            }
            catch (BanEvidenceException ex)
            {
                return BadRequest(new { Error = ex.Code, Message = ex.Message });
            }

            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                var actor = GetActor();
                var expiryText = request.ExpiresAt.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(request.ExpiresAt.Value).ToString("u")
                    : "permanent";

                _logger.LogInformation(
                    "Admin {Actor} banning user {UserId} until {Expiry} with {EvidenceCount} evidence file(s). Reason: {Reason}",
                    actor, id, expiryText, uploadedEvidence.Count, LogSanitizer.Clean(reason));

                var result = await _userService.BanUserAsync(
                    id, request, uploadedEvidence, actor, cancellationToken);

                if (!result.Success)
                {
                    _logger.LogWarning("Admin {Actor} failed to ban user {UserId}: {Error}", actor, id, result.Error);

                    return result.Error switch
                    {
                        "USER_NOT_FOUND" => NotFound(new
                        {
                            Error = result.Error,
                            Message = "User not found."
                        }),
                        "INVALID_REASON" => BadRequest(new
                        {
                            Error = result.Error,
                            Message = "Describe the reason in 10–2000 characters."
                        }),
                        "INVALID_EXPIRY" => BadRequest(new
                        {
                            Error = result.Error,
                            Message = "Choose a ban end date and time in the future."
                        }),
                        "EVIDENCE_REQUIRED" => BadRequest(new
                        {
                            Error = result.Error,
                            Message = "Attach at least one evidence file."
                        }),
                        _ => StatusCode(500, new
                        {
                            Error = "INTERNAL_ERROR",
                            Message = "An internal server error occurred."
                        })
                    };
                }

                _logger.LogInformation("Admin {Actor} successfully banned user {UserId}", actor, id);
                return Ok(new { Message = "User has been banned" });
            }
        }

        /// <summary>
        /// Downloads one evidence file attached to a ban. Files live in PostgreSQL and are
        /// reachable only through this authenticated endpoint, never through the public CDN.
        /// </summary>
        [HttpGet("ban-evidence/{evidenceId:long}")]
        [Authorize(Policy = Permissions.UsersBan)]
        public async Task<IActionResult> GetBanEvidence(long evidenceId, CancellationToken cancellationToken = default)
        {
            var evidence = await _context.UserBanEvidence
                .AsNoTracking()
                .Where(e => e.Id == evidenceId)
                .Select(e => new
                {
                    e.FileName,
                    e.ContentType,
                    e.Data,
                    UserId = e.Ban != null ? e.Ban.UserId : 0
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (evidence == null || evidence.UserId == 0)
                return NotFound(new { Error = "EVIDENCE_NOT_FOUND", Message = "Evidence file not found." });

            var targetRole = await GetTargetRoleAsync(evidence.UserId, cancellationToken);
            if (targetRole != null && !CanManageTarget(targetRole))
                return HigherRoleForbidden();

            Response.Headers["Cache-Control"] = "no-store";
            return File(evidence.Data, evidence.ContentType, evidence.FileName);
        }

        [HttpPost("users/{id}/unban")]
        [Authorize(Policy = Permissions.UsersBan)]
        public async Task<IActionResult> UnbanUser(long id, CancellationToken cancellationToken = default)
        {
            var targetRole = await GetTargetRoleAsync(id, cancellationToken);
            if (targetRole != null && !CanManageTarget(targetRole))
                return HigherRoleForbidden();

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
        [Authorize(Policy = Permissions.UsersChangeRole)]
        public async Task<IActionResult> ChangeRole(long id, [FromBody] AdminChangeRoleDto request, CancellationToken cancellationToken = default)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                if (string.IsNullOrWhiteSpace(request.Role))
                    return BadRequest(new { Error = "INVALID_ROLE", Message = "Role cannot be empty." });

                var requestedRole = request.Role.Trim().ToLowerInvariant();
                var targetRole = await GetTargetRoleAsync(id, cancellationToken);

                if (targetRole == null)
                    return NotFound(new { Error = "USER_NOT_FOUND", Message = "User not found." });

                if (!IsAdmin)
                {
                    if (id == GetActorId())
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Error = "CANNOT_CHANGE_OWN_ROLE",
                            Message = "You cannot change your own role."
                        });
                    }

                    if (RoleRank(requestedRole) > RoleRank(CurrentRole) ||
                        RoleRank(targetRole) > RoleRank(CurrentRole))
                    {
                        return HigherRoleForbidden();
                    }
                }

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
                        return BadRequest(new { Error = result.Error, Message = "Invalid role. Available roles: 'admin', 'moderator', 'user'." });
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
        [Authorize(Policy = Permissions.UsersChangePassword)]
        public async Task<IActionResult> ChangePassword(long id, [FromBody] AdminChangePasswordDto request, CancellationToken cancellationToken = default)
        {
            var targetRole = await GetTargetRoleAsync(id, cancellationToken);
            if (targetRole != null && !CanManageTarget(targetRole))
                return HigherRoleForbidden();

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
        [Authorize(Roles = Roles.Admin)]
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
        [Authorize(Policy = Permissions.ServersCreate)]
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
        [Authorize(Policy = Permissions.ServersEdit)]
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
        [Authorize(Policy = Permissions.ServersEdit)]
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
        [Authorize(Policy = Permissions.SettingsManagePolicy)]
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
        [Authorize(Policy = Permissions.SettingsManagePolicy)]
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
        [Authorize(Policy = Permissions.ServersView)]
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
        [Authorize(Policy = Permissions.ServersView)]
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
