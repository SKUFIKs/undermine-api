using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Background worker that processes build queue.
    /// </summary>
    public partial class BuildWorkerService : BackgroundService
    {
        /// <summary>Number of times a build is executed before it is considered permanently failed.</summary>
        private const int MaxAttempts = 3;

        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<BuildWorkerService> _logger;

        public BuildWorkerService(
            IServiceProvider serviceProvider,
            IConfiguration config,
            ILogger<BuildWorkerService> logger)
        {
            _serviceProvider = serviceProvider;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await RecoverInterruptedBuildsAsync(stoppingToken);
            await CleanupOrphanModpackStagingAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var engineDownloader = scope.ServiceProvider.GetRequiredService<EngineDownloadService>();
                    var manifestService = scope.ServiceProvider.GetRequiredService<BuildManifestService>();
                    var scopeFactory = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();

                    // Find next pending build (oldest first, so a failing build cannot starve the queue)
                    var build = await context.BuildInstances
                        .Where(b => b.Status == "pending")
                        .OrderBy(b => b.Id)
                        .FirstOrDefaultAsync(stoppingToken);

                    if (build != null)
                    {
                        await ProcessBuildAsync(context, build, engineDownloader, manifestService, scopeFactory, stoppingToken);
                    }
                    else
                    {
                        await ScheduleRetryAsync(context, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Build worker error");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Re-queues builds that were left in an intermediate state ("downloading"/"hashing")
        /// by a crash or hard restart. Without this they would stay stuck forever because the
        /// queue only picks up "pending" builds. Re-running is safe: downloads skip files that
        /// already exist and partial downloads are written to "*.part".
        /// </summary>
        private async Task RecoverInterruptedBuildsAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var interrupted = await context.BuildInstances
                    .Where(b => b.Status == "downloading" || b.Status == "hashing")
                    .OrderBy(b => b.Id)
                    .ToListAsync(ct);

                if (interrupted.Count == 0) return;

                foreach (var build in interrupted)
                {
                    _logger.LogWarning(
                        "Build {BuildId} was interrupted in state {Status}, re-queued as pending",
                        build.BuildId, build.Status);
                    build.Status = "pending";
                    build.UpdatedAt = DateTime.UtcNow;
                }

                await context.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover interrupted builds");
            }
        }

        /// <summary>
        /// Re-queues the oldest failed build while it still has attempts left.
        /// Builds that exhausted their attempts are skipped, so they are not re-run
        /// (and their errors not logged) on every worker cycle.
        /// </summary>
        private async Task ScheduleRetryAsync(AppDbContext context, CancellationToken ct)
        {
            var failedBuilds = await context.BuildInstances
                .Where(b => b.Status == "failed")
                .OrderBy(b => b.Id)
                .ToListAsync(ct);

            foreach (var failed in failedBuilds)
            {
                var attempts = ParseAttemptCount(failed.ErrorMessage);

                if (attempts >= MaxAttempts)
                {
                    _logger.LogDebug(
                        "Build {BuildId} permanently failed after {Attempts} attempts: {Error}",
                        failed.BuildId, attempts, failed.ErrorMessage);
                    continue;
                }

                _logger.LogWarning(
                    "Build {BuildId} failed after {Attempts}/{MaxAttempts} attempt(s): {Error}. Scheduling retry.",
                    failed.BuildId, attempts, MaxAttempts, failed.ErrorMessage);

                failed.Status = "pending";
                failed.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
                break; // one retry per cycle keeps the queue fair
            }
        }

        /// <summary>
        /// Error messages are prefixed with the attempt counter ("&lt;attempt&gt;|&lt;error&gt;").
        /// </summary>
        private static int ParseAttemptCount(string? errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return 0;

            var prefix = errorMessage.Split('|')[0];
            return int.TryParse(prefix, out var attempts) ? attempts : 0;
        }

        /// <summary>
        /// Drops staged .mrpack archives whose build is already completed or was deleted,
        /// so interrupted modpack imports do not leak disk space. Archives of builds that
        /// may still retry (pending / failed ...) are kept: re-running the import needs them.
        /// </summary>
        private async Task CleanupOrphanModpackStagingAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var modpackService = scope.ServiceProvider.GetRequiredService<ModpackImportService>();

                var activeIds = await context.BuildInstances
                    .Where(b => b.Status != "completed")
                    .Select(b => b.BuildId)
                    .ToListAsync(ct);

                modpackService.CleanupOrphanStaging(activeIds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Modpack staging cleanup failed");
            }
        }

        private async Task ProcessBuildAsync(
            AppDbContext context,
            BuildInstance build,
            EngineDownloadService engineDownloader,
            BuildManifestService manifestService,
            IServiceScopeFactory scopeFactory,
            CancellationToken ct)
        {
            var buildDir = Path.Combine(_config["Build:RootPath"] ?? "builds", build.BuildId);
            Directory.CreateDirectory(buildDir);

            // Collects live progress + log lines for the admin panel. It flushes through its
            // own scoped contexts, so it never touches the `build` entity tracked by `context`.
            var progress = new BuildProgressReporter(scopeFactory, build.Id, _logger);

            // Selects the pipeline scenario: a build carrying a modpack import descriptor
            // runs BuildType.ModpackImport (index files + overrides), everything else is
            // the standard engine + preset pipeline.
            var importInfo = ModpackImportInfo.Parse(build.ModpackImport);
            var buildType = importInfo != null ? BuildType.ModpackImport : BuildType.Standard;

            // `build` is tracked by `context`, so every status change below must be saved
            // with this same context instance - saving through another scope silently does nothing.
            try
            {
                build.Status = "downloading";
                build.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);

                await progress.LogAsync(
                    "info",
                    $"Начало сборки {build.Name} ({build.McVersion}, {build.LoaderType}{(string.IsNullOrEmpty(build.LoaderVersion) ? "" : " " + build.LoaderVersion)}) [{buildType}]",
                    ct);

                // Step 1: Download engine
                _logger.LogInformation("Build {BuildId}: downloading engine...", build.BuildId);
                await engineDownloader.DownloadEngineAsync(
                    build.McVersion, build.LoaderType, build.LoaderVersion, buildDir, ct, progress);

                // Step 1.5: Add mods, shaders, configs from preset
                await AddPresetContentAsync(build, buildDir, context, scopeFactory, progress, ct);

                // Step 1.6: Modpack import scenario - download every file from
                // modrinth.index.json (parallel, hash verified) and unpack overrides/
                // on top of the engine + preset content.
                if (importInfo != null)
                {
                    using var modpackScope = scopeFactory.CreateScope();
                    var modpackService = modpackScope.ServiceProvider.GetRequiredService<ModpackImportService>();
                    await modpackService.ApplyModpackAsync(build, importInfo, buildDir, progress, ct);
                }

                // Step 2: Hash and upload
                build.Status = "hashing";
                build.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);

                await progress.LogAsync("info", "Загрузка завершена: хеширование и выгрузка…", ct);
                _logger.LogInformation("Build {BuildId}: hashing and uploading...", build.BuildId);
                await manifestService.FinalizeBuildAsync(build.Id, buildDir, ct, progress);

                build.ErrorMessage = null;
                await context.SaveChangesAsync(ct);

                // The staged .mrpack is only needed while the build can still be retried.
                if (importInfo != null)
                {
                    using var cleanupScope = scopeFactory.CreateScope();
                    cleanupScope.ServiceProvider
                        .GetRequiredService<ModpackImportService>()
                        .CleanupStaging(build.BuildId);
                }

                await progress.LogAsync("info", "Сборка завершена", ct);
                await progress.CompleteAsync(ct);

                _logger.LogInformation("Build {BuildId}: completed!", build.BuildId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown in the middle of a build: put it back into the queue instead of
                // counting the interruption as a failed attempt.
                build.Status = "pending";
                build.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(CancellationToken.None);

                _logger.LogWarning("Build {BuildId} interrupted by shutdown, re-queued as pending", build.BuildId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Build {BuildId} failed", build.BuildId);

                var attempts = ParseAttemptCount(build.ErrorMessage) + 1;

                build.Status = "failed";
                build.ErrorMessage = $"{attempts}|{ex.Message}";
                build.UpdatedAt = DateTime.UtcNow;

                try
                {
                    await progress.LogAsync("error", $"Сборка не удалась (попытка {attempts}): {ex.Message}", CancellationToken.None);
                    await progress.FlushNowAsync(CancellationToken.None);
                }
                catch
                {
                    // Progress persistence must never mask the real failure.
                }

                await context.SaveChangesAsync(CancellationToken.None);
            }
        }
    }
}
