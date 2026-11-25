using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using UnderMineApi.Data;
using UnderMineApi.Metrics;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Services;

/// <summary>
/// Refreshes database-backed Prometheus gauges (users, bans, builds, storage, worker state)
/// every <c>Metrics:SnapshotIntervalSeconds</c> seconds. Event counters are updated directly
/// at the business-event call sites.
/// </summary>
public sealed class MetricsSnapshotService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<MetricsSnapshotService> _logger;

    public MetricsSnapshotService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<MetricsSnapshotService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Max(5, _config.GetValue("Metrics:SnapshotIntervalSeconds", 30)));

        while (!stoppingToken.IsCancellationRequested)
        {
            await CollectAsync(stoppingToken);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s3 = scope.ServiceProvider.GetRequiredService<S3StorageService>();
        var redis = scope.ServiceProvider.GetService<IConnectionMultiplexer>();

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var dayAgo = now - 86_400;

            var roleCounts = await db.McAuthAccounts
                .AsNoTracking()
                .GroupBy(u => u.Role)
                .Select(g => new { Role = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            AppMetrics.SetUsers(roleCounts
                .GroupBy(x => x.Role.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Count)));

            AppMetrics.SetBannedUsers(await db.UserBans
                .AsNoTracking()
                .Where(b => b.IsActive && (b.ExpiresAt == null || b.ExpiresAt > now))
                .Select(b => b.UserId)
                .Distinct()
                .CountAsync(ct));

            AppMetrics.SetActiveUsers24h(await db.McAuthAccounts
                .AsNoTracking()
                .CountAsync(u => u.LastLogin != null && u.LastLogin >= dayAgo, ct));

            AppMetrics.SetRegistrations24h(await db.McAuthAccounts
                .AsNoTracking()
                .CountAsync(u => u.RegDate >= dayAgo, ct));

            var servers = await db.ServerManifests
                .AsNoTracking()
                .GroupBy(s => s.IsActive)
                .Select(g => new { Active = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            AppMetrics.SetServers(
                servers.FirstOrDefault(x => x.Active)?.Count ?? 0,
                servers.FirstOrDefault(x => !x.Active)?.Count ?? 0);

            var builds = await db.BuildInstances
                .AsNoTracking()
                .GroupBy(b => b.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var buildCounts = builds
                .GroupBy(x => x.Status)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

            AppMetrics.SetBuilds(buildCounts);
            AppMetrics.SetBuildQueue(buildCounts.TryGetValue("pending", out var pending) ? pending : 0);
            AppMetrics.SetBuildsInProgress(
                (buildCounts.TryGetValue("downloading", out var downloading) ? downloading : 0) +
                (buildCounts.TryGetValue("hashing", out var hashing) ? hashing : 0));

            AppMetrics.SetPresets(await db.BuildPresets.AsNoTracking().CountAsync(ct));

            var fileCount = await db.BuildFiles.AsNoTracking().CountAsync(ct);
            var fileBytes = await db.BuildFiles
                .AsNoTracking()
                .SumAsync(f => (long?)f.Size, ct) ?? 0;

            AppMetrics.SetBuildFiles(fileCount);
            AppMetrics.SetBuildBytes(fileBytes);

            var evidenceCount = await db.UserBanEvidence.AsNoTracking().CountAsync(ct);
            var evidenceBytes = await db.UserBanEvidence
                .AsNoTracking()
                .SumAsync(e => (long?)e.Size, ct) ?? 0;

            AppMetrics.SetBanEvidenceFiles(evidenceCount);
            AppMetrics.SetBanEvidenceBytes(evidenceBytes);

            AppMetrics.SetAdminAuditRows(await db.AdminAudits.AsNoTracking().CountAsync(ct));

            var maintenance = await db.GlobalSettings
                .AsNoTracking()
                .Where(s => s.Key == "Maintenance")
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);

            AppMetrics.SetMaintenance(string.Equals(maintenance, "true", StringComparison.OrdinalIgnoreCase));

            var permissionCounts = await db.RolePermissions
                .AsNoTracking()
                .GroupBy(p => p.Role)
                .Select(g => new { Role = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            AppMetrics.SetRolePermissions(Roles.Admin, Permissions.AllKeys.Count);
            foreach (var role in Roles.Configurable)
            {
                var count = permissionCounts
                    .Where(x => string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.Count);
                AppMetrics.SetRolePermissions(role, count);
            }

            AppMetrics.SetDatabaseUp(true);
            AppMetrics.SetStorageUp(await s3.IsAvailableAsync(ct));
            if (redis != null)
                AppMetrics.SetRedisUp(redis.IsConnected);
        }
        catch (Exception ex)
        {
            AppMetrics.SetDatabaseUp(false);
            _logger.LogWarning(ex, "Failed to refresh Prometheus snapshot metrics");
        }
    }
}
