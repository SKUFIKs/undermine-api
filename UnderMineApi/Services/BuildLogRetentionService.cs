using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;

namespace UnderMineApi.Services;

/// <summary>
/// Trims the build log table so a long-running installation does not grow without bound.
/// Retention by age is enabled by default (30 days); a per-build cap can be enabled with
/// <c>Build:LogRetentionMaxPerBuild</c> (0 disables it).
/// </summary>
public sealed class BuildLogRetentionService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BuildLogRetentionService> _logger;

    public BuildLogRetentionService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<BuildLogRetentionService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = Math.Max(0, _configuration.GetValue("Build:LogRetentionDays", 30));
        var intervalHours = Math.Max(1, _configuration.GetValue("Build:LogRetentionIntervalHours", 24));
        var maxPerBuild = Math.Max(0, _configuration.GetValue("Build:LogRetentionMaxPerBuild", 0));

        if (retentionDays == 0 && maxPerBuild == 0)
        {
            _logger.LogInformation("Build log retention is disabled");
            return;
        }

        var interval = TimeSpan.FromHours(intervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(retentionDays, maxPerBuild, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Build log retention failed");
            }

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

    private async Task CleanupAsync(int retentionDays, int maxPerBuild, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var deleted = await context.BuildLogs
                .Where(l => l.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Deleted {Count} build log rows older than {Days} days", deleted, retentionDays);
            }
        }

        if (maxPerBuild > 0)
        {
            // Keep only the newest N lines per build. This is a maintenance query and runs at most
            // once per retention interval, not during normal request handling.
            const string sql = """
                DELETE FROM build_logs
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id, ROW_NUMBER() OVER (
                            PARTITION BY build_instance_id
                            ORDER BY id DESC
                        ) AS rn
                        FROM build_logs
                    ) ranked
                    WHERE ranked.rn > {0}
                )
                """;

            var trimmed = await context.Database.ExecuteSqlRawAsync(
                sql, new object[] { maxPerBuild }, ct);

            if (trimmed > 0)
            {
                _logger.LogInformation(
                    "Trimmed {Count} build log rows (max {MaxPerBuild} per build)",
                    trimmed, maxPerBuild);
            }
        }
    }
}
