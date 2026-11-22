using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.PasswordHasher;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Services
{
    public class UserService : IUserService
    {
        private const int MaxIpHistoryEntries = 50;

        private readonly AppDbContext _context;
        private readonly IPasswordHasher _hasher;
        private readonly ILogger<UserService> _logger;
        private readonly AdminAuditService _audit;
        private readonly IUserAccessService _access;
        private readonly IJwtService _jwtService;

        public UserService(
            AppDbContext context,
            IPasswordHasher hasher,
            ILogger<UserService> logger,
            AdminAuditService audit,
            IUserAccessService access,
            IJwtService jwtService)
        {
            _context = context;
            _hasher = hasher;
            _logger = logger;
            _audit = audit;
            _access = access;
            _jwtService = jwtService;
        }

        public async Task<object> GetPagedUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        {
            if (pageSize > 100) pageSize = 100;
            if (page < 1) page = 1;

            var totalUsers = await _context.McAuthAccounts.AsNoTracking().CountAsync(cancellationToken);

            var users = await _context.McAuthAccounts
                .AsNoTracking()
                .Include(u => u.Stats)
                .OrderByDescending(u => u.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.RealName,
                    u.Uuid,
                    u.Role,
                    u.Email,
                    u.LastLogin,
                    Statistics = u.Stats != null ? new
                    {
                        u.Stats.PlayTime,
                        u.Stats.Murders,
                        u.Stats.Deaths
                    } : null
                })
                .ToListAsync(cancellationToken);

            var bans = await LoadActiveBansAsync(users.Select(u => u.Id), cancellationToken);

            // Only active bans that have not expired count as banned; expired rows are kept
            // for history but must not show up as a ban in the admin panel.
            var data = users.Select(u =>
            {
                bans.TryGetValue(u.Id, out var ban);
                return new
                {
                    u.Id,
                    u.Username,
                    u.RealName,
                    u.Uuid,
                    u.Role,
                    u.Email,
                    u.LastLogin,
                    u.Statistics,
                    IsBanned = ban != null,
                    BanReason = ban?.Reason,
                    BanExpiresAt = ban?.ExpiresAt,
                    BannedBy = ban?.BannedBy
                };
            }).ToList();

            return new
            {
                TotalCount = totalUsers,
                Page = page,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling(totalUsers / (double)pageSize),
                Data = data
            };
        }

        /// <summary>
        /// Loads the most recent non-expired active ban per user (batches by user id).
        /// </summary>
        private async Task<Dictionary<long, UserBans>> LoadActiveBansAsync(IEnumerable<long> userIds, CancellationToken cancellationToken)
        {
            var ids = userIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<long, UserBans>();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var bans = await _context.UserBans
                .AsNoTracking()
                .Where(b => ids.Contains(b.UserId)
                            && b.IsActive
                            && (b.ExpiresAt == null || b.ExpiresAt > now))
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync(cancellationToken);

            return bans.GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.First());
        }

        public async Task<IEnumerable<object>> SearchUsersAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
        {
            var users = await _context.McAuthAccounts
                .AsNoTracking()
                .Where(u => EF.Functions.ILike(u.Username, $"%{query}%") || EF.Functions.ILike(u.RealName, $"%{query}%"))
                .Take(limit)
                .Select(u => new { u.Id, u.Username, u.RealName, u.Role })
                .ToListAsync(cancellationToken);

            var bans = await LoadActiveBansAsync(users.Select(u => u.Id), cancellationToken);

            return users.Select(u =>
            {
                bans.TryGetValue(u.Id, out var ban);
                return new
                {
                    u.Id,
                    u.Username,
                    u.RealName,
                    u.Role,
                    IsBanned = ban != null,
                    BanReason = ban?.Reason,
                    BanExpiresAt = ban?.ExpiresAt,
                    BannedBy = ban?.BannedBy
                };
            }).ToList<object>();
        }

        public async Task<(bool Success, string? Error)> ChangeUserRoleAsync(long id, string newRole, string actor, CancellationToken cancellationToken = default)
        {
            var user = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user == null) return (false, "USER_NOT_FOUND");

            var allowedRoles = Roles.Assignable;
            var normalizedRole = newRole.Trim().ToLowerInvariant();

            if (!allowedRoles.Contains(normalizedRole))
                return (false, "INVALID_ROLE");

            string oldRole = user.Role;

            // Demoting the last administrator would leave the installation without an admin.
            // Deleting an admin is blocked entirely; the admin must be demoted first.
            if (string.Equals(oldRole, Roles.Admin, StringComparison.OrdinalIgnoreCase) &&
                normalizedRole != Roles.Admin)
            {
                var otherAdmins = await _context.McAuthAccounts
                    .CountAsync(u => u.Id != id && u.Role.ToLower() == Roles.Admin, cancellationToken);
                if (otherAdmins == 0)
                    return (false, "LAST_ADMIN");
            }

            user.Role = normalizedRole;

            await _context.SaveChangesAsync(cancellationToken);

            // The old JWT still carries the previous role; drop the cached snapshot so the next
            // request forces a fresh check (and a re-login for the affected user).
            _access.Invalidate(id);

            _logger.LogInformation("Role for user {UserId} changed from {OldRole} to {NewRole} by {Actor}",
                LogSanitizer.Clean(id), LogSanitizer.Clean(oldRole), LogSanitizer.Clean(normalizedRole), LogSanitizer.Clean(actor));

            await _audit.LogAsync(actor, "change_role", "user", id, $"Role changed from {oldRole} to {normalizedRole}", cancellationToken);

            return (true, null);
        }

        public async Task<(bool Success, string? Error)> DeleteUserAsync(
            long id,
            string confirmUsername,
            string reason,
            long actorUserId,
            string actor,
            CancellationToken cancellationToken = default)
        {
            if (id == actorUserId)
                return (false, "CANNOT_DELETE_SELF");

            var user = await _context.McAuthAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

            if (user == null)
                return (false, "USER_NOT_FOUND");

            if (!string.Equals(
                    user.Username.Trim(),
                    (confirmUsername ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return (false, "CONFIRMATION_MISMATCH");
            }

            // An admin must be demoted first. This also prevents deleting the last admin:
            // ChangeUserRoleAsync refuses to demote the last admin.
            if (string.Equals(user.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
                return (false, "CANNOT_DELETE_ADMIN");

            var cleanReason = LogSanitizer.Clean(reason ?? string.Empty).Trim();
            if (cleanReason.Length < 5)
                return (false, "INVALID_REASON");

            var actorName = LogSanitizer.Clean(actor);
            if (actorName.Length > 100)
                actorName = actorName[..100];

            var username = LogSanitizer.Clean(user.Username);

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Remove owned rows explicitly in addition to the configured cascade, so the
                // delete also works for databases created before the cascade was added.
                await _context.UserBans
                    .Where(b => b.UserId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.UserIpHistory
                    .Where(h => h.UserId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                await _context.UserData
                    .Where(s => s.AccountId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                var deleted = await _context.McAuthAccounts
                    .Where(u => u.Id == id)
                    .ExecuteDeleteAsync(cancellationToken);

                if (deleted == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (false, "USER_NOT_FOUND");
                }

                // The audit row is written in the same transaction, so an audit failure rolls
                // the deletion back instead of leaving an untraceable removal.
                _context.AdminAudits.Add(new AdminAudit
                {
                    Actor = actorName,
                    Action = "delete_user",
                    TargetType = "user",
                    TargetId = id,
                    Details = $"username={username}; reason={cleanReason}",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Failed to delete user {UserId}; related records may still reference it", id);
                return (false, "USER_HAS_REFERENCES");
            }

            // Invalidate after commit: the next request carrying an old JWT sees a missing
            // account (AccountBanMiddleware) and returns 401.
            _access.Invalidate(id);

            _logger.LogInformation(
                "User {UserId} ({Username}) permanently deleted by {Actor}. Reason: {Reason}",
                id, username, actorName, cleanReason);

            return (true, null);
        }

        public async Task RecordLoginAsync(
            UserAccount user,
            string? ip,
            long loginAt,
            CancellationToken cancellationToken = default)
        {
            user.Ip = ip;
            user.LastLogin = loginAt;

            if (string.IsNullOrWhiteSpace(ip))
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            // The user is tracked by the same scoped context, so RecordIpAsync saves the
            // last-login fields and the IP row together.
            await RecordIpAsync(user.Id, ip, loginAt, cancellationToken);
        }

        public async Task RecordIpAsync(
            long userId,
            string? ip,
            long seenAt,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return;

            var normalizedIp = ip.Trim();
            var history = await _context.UserIpHistory
                .FirstOrDefaultAsync(h => h.UserId == userId && h.Ip == normalizedIp, cancellationToken);

            if (history == null)
            {
                _context.UserIpHistory.Add(new UserIpHistory
                {
                    UserId = userId,
                    Ip = normalizedIp,
                    FirstSeen = seenAt,
                    LastSeen = seenAt,
                    SeenCount = 1
                });
            }
            else
            {
                history.LastSeen = seenAt;
                history.SeenCount++;
            }

            await _context.SaveChangesAsync(cancellationToken);
            await TrimIpHistoryAsync(userId, cancellationToken);
        }

        private async Task TrimIpHistoryAsync(long userId, CancellationToken cancellationToken)
        {
            try
            {
                var staleIds = await _context.UserIpHistory
                    .Where(h => h.UserId == userId)
                    .OrderByDescending(h => h.LastSeen)
                    .ThenByDescending(h => h.Id)
                    .Skip(MaxIpHistoryEntries)
                    .Select(h => h.Id)
                    .ToListAsync(cancellationToken);

                if (staleIds.Count == 0) return;

                await _context.UserIpHistory
                    .Where(h => staleIds.Contains(h.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Cleanup is best-effort; it must never break a successful login.
                _logger.LogWarning(ex, "Failed to trim IP history for user {UserId}", userId);
            }
        }

        public async Task<AdminUserStatisticsDto?> GetUserStatisticsAsync(
            long id,
            CancellationToken cancellationToken = default)
        {
            var user = await _context.McAuthAccounts
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new AdminUserStatisticsDto
                {
                    Id = u.Id,
                    Username = u.Username,
                    RealName = u.RealName,
                    Uuid = u.Uuid,
                    Role = u.Role,
                    Email = u.Email,
                    RegDate = u.RegDate,
                    LastLogin = u.LastLogin,
                    RegIp = u.RegIp,
                    LastIp = u.Ip,
                    PlayTime = u.Stats != null ? u.Stats.PlayTime : 0,
                    Murders = u.Stats != null ? u.Stats.Murders : 0,
                    Deaths = u.Stats != null ? u.Stats.Deaths : 0
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (user == null) return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var ban = await _context.UserBans
                .AsNoTracking()
                .Where(b => b.UserId == id &&
                            b.IsActive &&
                            (b.ExpiresAt == null || b.ExpiresAt > now))
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => new { b.Reason, b.ExpiresAt, b.BannedBy })
                .FirstOrDefaultAsync(cancellationToken);

            if (ban != null)
            {
                user.IsBanned = true;
                user.BanReason = ban.Reason;
                user.BanExpiresAt = ban.ExpiresAt;
                user.BannedBy = ban.BannedBy;
            }

            user.IpHistory = await _context.UserIpHistory
                .AsNoTracking()
                .Where(h => h.UserId == id)
                .OrderByDescending(h => h.LastSeen)
                .ThenByDescending(h => h.Id)
                .Take(MaxIpHistoryEntries)
                .Select(h => new AdminUserIpDto
                {
                    Ip = h.Ip,
                    FirstSeen = h.FirstSeen,
                    LastSeen = h.LastSeen,
                    SeenCount = h.SeenCount
                })
                .ToListAsync(cancellationToken);

            return user;
        }

        public async Task<(bool Success, string? Error)> BanUserAsync(long id, BanRequestDto request, string actor, CancellationToken cancellationToken = default)
        {
            var exists = await _context.McAuthAccounts.AsNoTracking().AnyAsync(u => u.Id == id, cancellationToken);
            if (!exists) return (false, "USER_NOT_FOUND");

            var oldBans = await _context.UserBans.Where(b => b.UserId == id && b.IsActive).ToListAsync(cancellationToken);
            foreach (var b in oldBans) b.IsActive = false;

            var ban = new UserBans
            {
                UserId = id,
                Reason = request.Reason ?? "No reason provided",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExpiresAt = request.DurationDays.HasValue ? DateTimeOffset.UtcNow.AddDays(request.DurationDays.Value).ToUnixTimeSeconds() : null,
                IsActive = true,
                BannedBy = actor
            };

            _context.UserBans.Add(ban);
            await _context.SaveChangesAsync(cancellationToken);

            _access.Invalidate(id);

            _logger.LogInformation("User {UserId} banned by {Actor}", id, actor);
            await _audit.LogAsync(actor, "ban_user", "user", id, request.Reason, cancellationToken);
            return (true, null);
        }

        public async Task<(bool Success, string? Error)> UnbanUserAsync(long id, string actor, CancellationToken cancellationToken = default)
        {
            var activeBans = await _context.UserBans.Where(b => b.UserId == id && b.IsActive).ToListAsync(cancellationToken);
            if (!activeBans.Any()) return (false, "USER_NOT_BANNED");

            foreach (var ban in activeBans) ban.IsActive = false;
            await _context.SaveChangesAsync(cancellationToken);

            _access.Invalidate(id);

            _logger.LogInformation("User {UserId} unbanned by {Actor}", id, actor);
            await _audit.LogAsync(actor, "unban_user", "user", id, null, cancellationToken);
            return (true, null);
        }

        public async Task<(bool Success, string? Error)> ChangePasswordAsync(long id, string newPassword, string actor, CancellationToken cancellationToken = default)
        {
            if (!PasswordPolicy.TryValidate(newPassword, out _))
                return (false, "INVALID_PASSWORD");

            var user = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user == null) return (false, "USER_NOT_FOUND");

            user.HashPassword = _hasher.HashPassword(newPassword);
            user.TokenVersion++;
            await _context.SaveChangesAsync(cancellationToken);
            _access.Invalidate(id);

            _logger.LogInformation("Password for user {UserId} changed by {Actor}", id, actor);
            await _audit.LogAsync(actor, "change_password", "user", id, null, cancellationToken);
            return (true, null);
        }

        public async Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(
            long userId,
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken = default)
        {
            var user = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user == null)
                return new ChangeOwnPasswordResult(false, "USER_NOT_FOUND", "User not found.", null, 0);

            if (!_hasher.VerifyPassword(currentPassword, user.HashPassword))
                return new ChangeOwnPasswordResult(false, "INVALID_CURRENT_PASSWORD", "Current password is incorrect.", null, 0);

            if (!PasswordPolicy.TryValidate(newPassword, out var policyError))
                return new ChangeOwnPasswordResult(false, "WEAK_PASSWORD", policyError, null, 0);

            if (_hasher.VerifyPassword(newPassword, user.HashPassword))
                return new ChangeOwnPasswordResult(false, "SAME_PASSWORD", "The new password must differ from the current one.", null, 0);

            user.HashPassword = _hasher.HashPassword(newPassword);
            user.TokenVersion++;
            await _context.SaveChangesAsync(cancellationToken);
            _access.Invalidate(user.Id);

            _logger.LogInformation("Password for user {UserId} changed by the user", userId);
            await _audit.LogAsync(user.Username, "change_own_password", "user", userId, null, cancellationToken);

            var token = _jwtService.GenerateToken(user);
            return new ChangeOwnPasswordResult(true, null, null, token, (int)TimeSpan.FromDays(30).TotalSeconds);
        }
    }
}
