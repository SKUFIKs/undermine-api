using UnderMineApi.Models;
using UnderMineApi.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using StackExchange.Redis;

namespace UnderMineApi.Services
{
    public class ConfigCacheService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ConfigCacheService> _logger;
        private readonly IConnectionMultiplexer? _redis;
        private const string CacheKey = "ConfigCache:Servers";

        public virtual List<ServerManifest> CachedServers { get; set; } = new();
        public virtual bool IsApiLocked { get; set; }
        public ConfigCacheService(IServiceScopeFactory scopeFactory, ILogger<ConfigCacheService> logger, IConnectionMultiplexer? redis = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _redis = redis;
        }

        public virtual bool IsMaintenance { get; set; }

        public async Task RefreshCacheAsync(CancellationToken ct = default)
        {
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    CachedServers = await dbContext.ServerManifests
                        .AsNoTracking()
                        .Include(s => s.Config)
                        .Where(s => s.IsActive)
                        .ToListAsync(ct);

                    var settings = await dbContext.GlobalSettings.AsNoTracking().ToListAsync(ct);

                    var maintenanceValue = settings.FirstOrDefault(s => s.Key == "Maintenance")?.Value == "true";

                    // ApiLockMiddleware and ApplicationStatusService read these flags, so they must
                    // be refreshed together with the server list - otherwise switching maintenance
                    // mode in the admin panel has no effect until the application restarts.
                    IsMaintenance = maintenanceValue;
                    IsApiLocked = maintenanceValue;

                    _logger.LogDebug(
                        "Config cache refreshed. Servers: {ServersCount}, Maintenance: {IsMaintenance}",
                        CachedServers.Count,
                        maintenanceValue);

                    // Update Redis cache (for other instances)
                    if (_redis != null)
                    {
                        try
                        {
                            var json = JsonSerializer.Serialize(CachedServers);
                            var db = _redis.GetDatabase();
                            await db.StringSetAsync(CacheKey, json, expiry: TimeSpan.FromMinutes(10));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to write cache to Redis");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh config cache");
            }
        }

        // Try to load cache from Redis on startup
        public async Task TryLoadFromDistributedCacheAsync(CancellationToken ct = default)
        {
            if (_redis == null) return;

            try
            {
                var db = _redis.GetDatabase();
                var str = await db.StringGetAsync(CacheKey);
                if (!str.IsNullOrEmpty)
                {
                    var list = JsonSerializer.Deserialize<List<ServerManifest>>(str!);
                    if (list != null)
                    {
                        CachedServers = list;
                        _logger.LogInformation(
                            "Cache loaded from Redis. Servers: {ServersCount}",
                            CachedServers.Count);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cache from Redis");
            }
        }
    }
}
