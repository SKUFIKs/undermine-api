using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;

namespace UnderMineApi.Services;

/// <summary>
/// Deletes crash reports older than <c>Reports:RetentionDays</c> so the database does not grow
/// forever. Runs once at startup and then every <c>Reports:RetentionIntervalHours</c> hours.
/// A value of zero disables retention (reports are kept until an administrator deletes them).
/// </summary>
public sealed class ClientReportRetentionService : BackgroundService
{
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientReportRetentionService> _logger;
    private readonly int _retentionDays;
    private readonly TimeSpan _interval;

    public ClientReportRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ClientReportRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _retentionDays = Math.Max(0, configuration.GetValue("Reports:RetentionDays", 90));
        var hours = Math.Max(1, configuration.GetValue("Reports:RetentionIntervalHours", 24));
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retentionDays <= 0)
        {
            _logger.LogInformation("Crash report retention is disabled (Reports:RetentionDays=0).");
            return;
        }

        using var timer = new PeriodicTimer(_interval);

        do
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Crash report retention cleanup failed.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays).ToUnixTimeSeconds();
        var deleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var batch = await context.ClientReports
                .Where(r => r.CreatedAt < cutoff)
                .OrderBy(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            context.ClientReports.RemoveRange(batch);
            await context.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} crash reports older than {Days} days.",
                deleted, _retentionDays);
        }
    }
}
