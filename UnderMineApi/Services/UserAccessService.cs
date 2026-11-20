using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using UnderMineApi.Data;
using UnderMineApi.Services.Interfaces;

namespace UnderMineApi.Services;

/// <summary>
/// Resolves the current ban/role state of a user for JWT-based authorization. Results are cached
/// briefly, so a ban or role change takes effect within <c>Access:BanCacheSeconds</c> even for
/// tokens issued long ago.
/// </summary>
public class UserAccessService : IUserAccessService
{
    private const int DefaultCacheSeconds = 30;

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserAccessService> _logger;

    public UserAccessService(
        AppDbContext context,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<UserAccessService> logger)
    {
        _context = context;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    private TimeSpan CacheDuration =>
        TimeSpan.FromSeconds(Math.Max(1, _configuration.GetValue("Access:BanCacheSeconds", DefaultCacheSeconds)));

    public async Task<UserAccessSnapshot> GetSnapshotAsync(long userId, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(userId);
        if (_cache.TryGetValue<UserAccessSnapshot>(cacheKey, out var cached) && cached != null)
            return cached;

        var user = await _context.McAuthAccounts
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Role, u.TokenVersion })
            .FirstOrDefaultAsync(ct);

        if (user == null)
        {
            _cache.Set(cacheKey, UserAccessSnapshot.Missing, CacheDuration);
            return UserAccessSnapshot.Missing;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ban = await _context.UserBans
            .AsNoTracking()
            .Where(b => b.UserId == userId &&
                        b.IsActive &&
                        (b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new { b.Reason, b.ExpiresAt, b.BannedBy })
            .FirstOrDefaultAsync(ct);

        var snapshot = new UserAccessSnapshot(
            Exists: true,
            Role: user.Role,
            TokenVersion: user.TokenVersion,
            Ban: ban == null
                ? BanStatus.NotBanned
                : new BanStatus(true, ban.Reason, ban.ExpiresAt, ban.BannedBy));

        _cache.Set(cacheKey, snapshot, CacheDuration);
        return snapshot;
    }

    public void Invalidate(long userId)
    {
        _cache.Remove(CacheKey(userId));
        _logger.LogDebug("Dropped cached access snapshot for user {UserId}", userId);
    }

    private static string CacheKey(long userId) => $"user-access:{userId}";
}
