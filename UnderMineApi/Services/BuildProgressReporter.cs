using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Collects live progress metrics and log lines for one build instance and persists
    /// them to the database on a short throttle while the build worker is running.
    /// The admin panel reads those columns / rows while polling /builds, so no websocket
    /// or SSE plumbing is needed to see the install process, bytes downloaded, speed and
    /// errors in the web UI.
    ///
    /// All mutation methods are cheap (volatile fields / a queue); the only I/O is the
    /// throttled <see cref="FlushAsync"/>, which uses its own scoped context so it never
    /// interferes with the worker's tracked entity.
    /// </summary>
    public sealed class BuildProgressReporter
    {
        /// <summary>Persist metrics and queued log lines at most once per second.</summary>
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<(string Level, string Message)> _pendingLogs = new();

        public long BuildInstanceId { get; }

        // ---- live metrics (published to the DB on flush) ----
        // Guarded by _metricsLock: mutated from the worker flow AND from parallel
        // download tasks (BeginFile/ReportFileBytes/EndFile), read on flush.
        private readonly object _metricsLock = new();
        private int _progressPercent;
        private long _bytesDownloaded;
        private long _totalBytes;
        private double _downloadSpeed;
        private int _downloadedFiles;
        private int _totalFiles;
        private string? _currentStep;
        private string? _currentFile;

        // ---- download bookkeeping (only touched from the single worker flow) ----
        private long _baseBytesBeforeFile; // cumulative bytes before the current file started
        private long _lastSampleBytes;
        private long _lastSampleTicks;
        private double _speedEma;
        private long _lastFlushTicks;
        private int _flushing;
        private bool _useFileProgress; // hashing / S3 phases advance by file count, not bytes

        public BuildProgressReporter(IServiceScopeFactory scopeFactory, long buildInstanceId, ILogger logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            BuildInstanceId = buildInstanceId;
            _lastSampleTicks = Stopwatch.GetTimestamp();
        }

        // ---------- step / file tracking ----------

        /// <summary>Names the current phase, e.g. "Downloading assets (541 files)".</summary>
        public void SetStep(string step) => _currentStep = step;

        /// <summary>Names the file currently being processed (full path is fine here).</summary>
        public void SetCurrentFile(string filePath) => _currentFile = filePath;

        /// <summary>
        /// Starts tracking one file. <paramref name="expectedBytes"/> comes from the HTTP
        /// Content-Length header and may be null for chunked responses.
        /// </summary>
        public void BeginFile(string filePath, long? expectedBytes)
        {
            lock (_metricsLock)
            {
                _baseBytesBeforeFile = _bytesDownloaded;
                _currentFile = filePath;
                _totalFiles++;
                _useFileProgress = false;
                if (expectedBytes is > 0) _totalBytes += expectedBytes.Value;
                RecomputePercent();
            }
        }

        /// <summary>
        /// Called repeatedly while a file is being read; <paramref name="bytesDone"/> is the
        /// number of bytes read so far in the current file. Also computes download speed.
        /// </summary>
        public void ReportFileBytes(long bytesDone)
        {
            lock (_metricsLock)
            {
                var now = Stopwatch.GetTimestamp();
                var seconds = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
                if (seconds >= 0.5)
                {
                    var delta = bytesDone - _lastSampleBytes;
                    if (delta > 0 && seconds > 0)
                    {
                        var instantaneous = delta / seconds;
                        _speedEma = _speedEma <= 0 ? instantaneous : _speedEma * 0.6 + instantaneous * 0.4;
                        _downloadSpeed = _speedEma;
                    }
                    _lastSampleBytes = bytesDone;
                    _lastSampleTicks = now;
                }

                _bytesDownloaded = _baseBytesBeforeFile + bytesDone;
                RecomputePercent();
            }
        }

        /// <summary>
        /// Finishes tracking a file. <paramref name="knownSize"/> is true when the size was
        /// known up front (Content-Length); a chunked file contributes its actual size here.
        /// </summary>
        public void EndFile(string filePath, long actualBytes, bool knownSize)
        {
            lock (_metricsLock)
            {
                if (!knownSize && actualBytes > 0) _totalBytes += actualBytes;
                _bytesDownloaded = _baseBytesBeforeFile + actualBytes;
                _currentFile = filePath;
                _downloadedFiles++;
                RecomputePercent();
            }
        }

        /// <summary>Progress driven by file counts (used by the hashing / S3 upload phases).</summary>
        public void SetFileProgress(int filesDone, int filesTotal)
        {
            lock (_metricsLock)
            {
                _useFileProgress = true;
                _downloadedFiles = filesDone;
                _totalFiles = filesTotal;
                RecomputePercent();
            }
        }

        /// <summary>
        /// Prepares the reporter for a phase driven by several parallel download workers
        /// (modpack file imports): percent switches to file counts, the byte total is
        /// pre-registered and workers feed the running total through
        /// <see cref="AddParallelBytes"/>. Safe to call from the single worker flow only.
        /// </summary>
        public void BeginParallelPhase(int totalFiles, long totalBytes)
        {
            lock (_metricsLock)
            {
                _useFileProgress = true;
                _downloadedFiles = 0;
                _totalFiles = totalFiles;
                if (totalBytes > 0) _totalBytes += totalBytes;
                // Restart the speed window so the first sample does not treat the whole
                // previous phase as one short burst.
                _lastSampleBytes = _bytesDownloaded;
                _lastSampleTicks = Stopwatch.GetTimestamp();
                _speedEma = 0;
                _downloadSpeed = 0;
                RecomputePercent();
            }
        }

        /// <summary>
        /// Accounts bytes downloaded by one of the parallel workers and refreshes the
        /// current file + speed estimate. Called from concurrent download tasks, hence
        /// fully lock protected.
        /// </summary>
        public void AddParallelBytes(long delta, string? currentFile = null)
        {
            if (delta <= 0) return;

            lock (_metricsLock)
            {
                _bytesDownloaded += delta;
                if (currentFile != null) _currentFile = currentFile;

                var now = Stopwatch.GetTimestamp();
                var seconds = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
                if (seconds >= 0.5)
                {
                    var deltaTotal = _bytesDownloaded - _lastSampleBytes;
                    if (deltaTotal > 0)
                    {
                        var instantaneous = deltaTotal / seconds;
                        _speedEma = _speedEma <= 0 ? instantaneous : _speedEma * 0.6 + instantaneous * 0.4;
                        _downloadSpeed = _speedEma;
                    }
                    _lastSampleBytes = _bytesDownloaded;
                    _lastSampleTicks = now;
                }

                RecomputePercent();
            }
        }

        private void RecomputePercent()
        {
            if (_useFileProgress && _totalFiles > 0)
            {
                _progressPercent = (int)Math.Round(Math.Min(100.0, _downloadedFiles * 100.0 / _totalFiles));
            }
            else if (_totalBytes > 0)
            {
                _progressPercent = (int)Math.Round(Math.Min(100.0, _bytesDownloaded * 100.0 / _totalBytes));
            }
            else if (_totalFiles > 0)
            {
                _progressPercent = (int)Math.Round(Math.Min(100.0, _downloadedFiles * 100.0 / _totalFiles));
            }
        }

        // ---------- logging / persistence ----------

        /// <summary>Queues a log line and flushes immediately (throttled to once per second).</summary>
        public async Task LogAsync(string level, string message, CancellationToken ct)
        {
            _pendingLogs.Enqueue((level, message));
            await FlushAsync(ct);
        }

        /// <summary>Persists metrics + queued log lines if the flush throttle allows it.</summary>
        public async Task FlushAsync(CancellationToken ct)
        {
            var elapsed = (Stopwatch.GetTimestamp() - _lastFlushTicks) / (double)Stopwatch.Frequency;
            if (elapsed < FlushInterval.TotalSeconds) return;
            await FlushNowAsync(ct);
        }

        /// <summary>Persists regardless of the throttle (used on phase boundaries / completion).</summary>
        public async Task FlushNowAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return; // already flushing

            try
            {
                await PersistAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep the build running; progress is best-effort. Log lines stay queued
                // and are picked up by the next successful flush.
                _logger.LogError(ex, "Failed to persist progress for build {BuildId}", BuildInstanceId);
                return;
            }
            finally
            {
                _lastFlushTicks = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        /// <summary>Marks the build as done (100%) and persists everything still queued.</summary>
        public async Task CompleteAsync(CancellationToken ct)
        {
            _currentStep = "Done";
            _currentFile = null;
            _progressPercent = 100;
            await FlushNowAsync(ct);
        }

        private async Task PersistAsync(CancellationToken ct)
        {
            // Drain the queue up front, so a failed write does not lose the lines.
            var batch = new List<(string Level, string Message)>();
            while (_pendingLogs.TryDequeue(out var item)) batch.Add(item);

            // Snapshot metrics under the lock so parallel download tasks can keep
            // mutating while we build the DB update.
            int progressPercent;
            string? currentStep;
            string? currentFile;
            long bytesDownloaded;
            long totalBytes;
            double downloadSpeed;
            int downloadedFiles;
            int totalFiles;
            lock (_metricsLock)
            {
                progressPercent = _progressPercent;
                currentStep = _currentStep;
                currentFile = _currentFile;
                bytesDownloaded = _bytesDownloaded;
                totalBytes = _totalBytes;
                downloadSpeed = _downloadSpeed;
                downloadedFiles = _downloadedFiles;
                totalFiles = _totalFiles;
            }

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = await context.BuildInstances.FirstOrDefaultAsync(b => b.Id == BuildInstanceId, ct);
            if (row != null)
            {
                row.ProgressPercent = progressPercent;
                row.CurrentStep = currentStep;
                row.CurrentFile = currentFile;
                row.BytesDownloaded = bytesDownloaded;
                row.TotalBytes = totalBytes;
                row.DownloadSpeed = downloadSpeed;
                row.DownloadedFiles = downloadedFiles;
                row.TotalFiles = totalFiles;
                row.UpdatedAt = DateTime.UtcNow;
            }

            if (batch.Count > 0)
            {
                context.BuildLogs.AddRange(batch.Select(b => new BuildLog
                {
                    BuildInstanceId = BuildInstanceId,
                    Level = b.Level,
                    Message = b.Message
                }));
            }

            await context.SaveChangesAsync(ct);
        }
    }
}