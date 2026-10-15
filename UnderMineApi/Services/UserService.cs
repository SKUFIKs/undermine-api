using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.PasswordHasher;

namespace UnderMineApi.Services
{
    public class UserService : IUserService
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _hasher;
        private readonly ILogger<UserService> _logger;
        private readonly AdminAuditService _audit;

        public UserService(AppDbContext context, IPasswordHasher hasher, ILogger<UserService> logger, AdminAuditService audit)
        {
            _context = context;
            _hasher = hasher;
            _logger = logger;
            _audit = audit;
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

            var allowedRoles = new[] { "admin", "user" };
            var normalizedRole = newRole.Trim().ToLower();

            if (!allowedRoles.Contains(normalizedRole))
                return (false, "INVALID_ROLE");

            string oldRole = user.Role;
            user.Role = normalizedRole;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Role for user {UserId} changed from {OldRole} to {NewRole} by {Actor}", id, oldRole, normalizedRole, actor);

            await _audit.LogAsync(actor, "change_role", "user", id, $"Role changed from {oldRole} to {normalizedRole}", cancellationToken);

            return (true, null);
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

            _logger.LogInformation("User {UserId} unbanned by {Actor}", id, actor);
            await _audit.LogAsync(actor, "unban_user", "user", id, null, cancellationToken);
            return (true, null);
        }

        public async Task<(bool Success, string? Error)> ChangePasswordAsync(long id, string newPassword, string actor, CancellationToken cancellationToken = default)
        {
            var user = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user == null) return (false, "USER_NOT_FOUND");

            user.HashPassword = _hasher.HashPassword(newPassword);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Password for user {UserId} changed by {Actor}", id, actor);
            await _audit.LogAsync(actor, "change_password", "user", id, null, cancellationToken);
            return (true, null);
        }
    }
}
