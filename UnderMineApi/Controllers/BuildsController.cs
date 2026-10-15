using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers
{
    [ApiController]
    [Route("api/v1.0/builds")]
    public class BuildsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<BuildsController> _logger;
        private readonly IConfiguration _config;
        private readonly S3StorageService _s3Storage;

        public BuildsController(
            AppDbContext context,
            ILogger<BuildsController> logger,
            IConfiguration config,
            S3StorageService s3Storage)
        {
            _context = context;
            _logger = logger;
            _config = config;
            _s3Storage = s3Storage;
        }

        /// <summary>
        /// Creates a new build and queues it for background processing.
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "admin")]
        public async Task<ActionResult<BuildResponse>> CreateBuild(
            [FromBody] CreateBuildRequest request,
            CancellationToken ct)
        {
            var actor = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

            _logger.LogInformation("Admin {Actor} creating build: {Name} ({McVersion}, {Loader})",
                actor, request.Name, request.McVersion, request.Loader);

            var build = new BuildInstance
            {
                Name = request.Name,
                McVersion = request.McVersion,
                LoaderType = request.Loader.ToLowerInvariant(),
                LoaderVersion = request.LoaderVersion,
                Status = "pending",
                BuildId = Guid.NewGuid().ToString("N"),
                BuildPresetId = request.BuildPresetId
            };

            _context.BuildInstances.Add(build);
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Build {BuildId} created and queued", build.BuildId);

            return Ok(new BuildResponse
            {
                Id = build.Id,
                Name = build.Name,
                McVersion = build.McVersion,
                LoaderType = build.LoaderType,
                LoaderVersion = build.LoaderVersion,
                BuildPresetId = build.BuildPresetId,
                Status = build.Status,
                BuildId = build.BuildId,
                CreatedAt = build.CreatedAt,
                UpdatedAt = build.UpdatedAt
            });
        }

        /// <summary>
        /// Lists all builds.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = "admin")]
        public async Task<ActionResult<List<BuildResponse>>> ListBuilds(CancellationToken ct)
        {
            var builds = await _context.BuildInstances
                .AsNoTracking()
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => new BuildResponse
                {
                    Id = b.Id,
                    Name = b.Name,
                    McVersion = b.McVersion,
                    LoaderType = b.LoaderType,
                    LoaderVersion = b.LoaderVersion,
                    BuildType = b.ModpackImport == null ? "Standard" : "ModpackImport",
                    Status = b.Status,
                    BuildId = b.BuildId,
                    ErrorMessage = b.ErrorMessage,
                    CreatedAt = b.CreatedAt,
                    UpdatedAt = b.UpdatedAt,
                    CompletedAt = b.CompletedAt,
                    FileCount = b.Files.Count,
                    TotalSize = b.Files.Sum(f => f.Size),
                    ProgressPercent = b.ProgressPercent,
                    CurrentStep = b.CurrentStep,
                    CurrentFile = b.CurrentFile,
                    BytesDownloaded = b.BytesDownloaded,
                    TotalBytes = b.TotalBytes,
                    DownloadSpeed = b.DownloadSpeed,
                    DownloadedFiles = b.DownloadedFiles,
                    TotalFiles = b.TotalFiles
                })
                .ToListAsync(ct);

            return Ok(builds);
        }

        /// <summary>
        /// Gets build status by ID.
        /// </summary>
        [HttpGet("{id:long}")]
        [Authorize(Roles = "admin")]
        public async Task<ActionResult<BuildResponse>> GetBuild(long id, CancellationToken ct)
        {
            // Project instead of Include(b => b.Files): a materialized join would ship the
            // 1+ MB manifest_json column once per file row (4000+ files -> gigabytes and
            // 15-30s responses). Aggregates are computed in SQL instead.
            var response = await _context.BuildInstances
                .AsNoTracking()
                .Where(b => b.Id == id)
                .Select(b => new BuildResponse
                {
                    Id = b.Id,
                    Name = b.Name,
                    McVersion = b.McVersion,
                    LoaderType = b.LoaderType,
                    LoaderVersion = b.LoaderVersion,
                    BuildPresetId = b.BuildPresetId,
                    BuildType = b.ModpackImport == null ? "Standard" : "ModpackImport",
                    Status = b.Status,
                    BuildId = b.BuildId,
                    ErrorMessage = b.ErrorMessage,
                    CreatedAt = b.CreatedAt,
                    UpdatedAt = b.UpdatedAt,
                    CompletedAt = b.CompletedAt,
                    FileCount = b.Files.Count,
                    TotalSize = b.Files.Sum(f => f.Size),
                    ProgressPercent = b.ProgressPercent,
                    CurrentStep = b.CurrentStep,
                    CurrentFile = b.CurrentFile,
                    BytesDownloaded = b.BytesDownloaded,
                    TotalBytes = b.TotalBytes,
                    DownloadSpeed = b.DownloadSpeed,
                    DownloadedFiles = b.DownloadedFiles,
                    TotalFiles = b.TotalFiles
                })
                .FirstOrDefaultAsync(ct);

            if (response == null)
                return NotFound();

            return Ok(response);
        }

        /// <summary>
        /// Returns a build's processing log. With afterId=0 the most recent lines are
        /// returned (chronological order); pass the previously received latest log id to
        /// only get the newer lines while a build is running.
        /// </summary>
        [HttpGet("{id:long}/logs")]
        [Authorize(Roles = "admin")]
        public async Task<ActionResult<object>> GetBuildLogs(
            long id, CancellationToken ct, [FromQuery] long afterId = 0, [FromQuery] int limit = 200)
        {
            var exists = await _context.BuildInstances.AnyAsync(b => b.Id == id, ct);
            if (!exists)
                return NotFound(new { Error = "BUILD_NOT_FOUND" });

            limit = Math.Clamp(limit, 1, 500);

            var query = _context.BuildLogs
                .AsNoTracking()
                .Where(l => l.BuildInstanceId == id && l.Id > afterId);

            List<BuildLog> rows;
            if (afterId == 0)
            {
                // Newest lines first for the initial fetch, then flipped to chronological order.
                rows = await query.OrderByDescending(l => l.Id).Take(limit).ToListAsync(ct);
                rows.Reverse();
            }
            else
            {
                rows = await query.OrderBy(l => l.Id).Take(limit).ToListAsync(ct);
            }

            var logs = rows.Select(l => new BuildLogResponse
            {
                Id = l.Id,
                Level = l.Level,
                Message = l.Message,
                CreatedAt = l.CreatedAt
            }).ToList();

            return Ok(new
            {
                Logs = logs,
                LatestId = logs.Count > 0 ? logs[^1].Id : afterId
            });
        }

        /// <summary>
        /// Deletes a build (database rows, local cache directory and uploaded objects).
        /// Builds that are currently being processed cannot be deleted.
        /// </summary>
        [HttpDelete("{id:long}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> DeleteBuild(long id, CancellationToken ct)
        {
            var build = await _context.BuildInstances
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id, ct);

            if (build == null)
                return NotFound(new { Error = "BUILD_NOT_FOUND" });

            if (build.Status == "downloading" || build.Status == "hashing")
            {
                return Conflict(new
                {
                    Error = "BUILD_IN_PROGRESS",
                    Message = "The build is being processed right now. Wait for it to finish."
                });
            }

            var actor = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            _logger.LogWarning("Admin {Actor} deleting build {BuildId} ({Name}, status {Status})",
                actor, build.BuildId, build.Name, build.Status);

            var s3Keys = await _context.BuildFiles
                .AsNoTracking()
                .Where(f => f.BuildInstanceId == id && f.S3Key != null)
                .Select(f => f.S3Key!)
                .Distinct()
                .ToListAsync(ct);

            var fileRows = await _context.BuildFiles
                .Where(f => f.BuildInstanceId == id)
                .ToListAsync(ct);
            _context.BuildFiles.RemoveRange(fileRows);

            var entity = await _context.BuildInstances.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (entity == null)
                return NotFound(new { Error = "BUILD_NOT_FOUND" });

            _context.BuildInstances.Remove(entity);
            await _context.SaveChangesAsync(ct);

            // The build rows (and their files / logs through the cascade) are gone: rewind the
            // identity sequences so the next build reuses the freed id instead of continuing
            // from the high-water mark. Housekeeping only - never fail the delete over it.
            try
            {
                await _context.RewindAsync(IdentitySequence.Builds, ct);
                await _context.RewindAsync(IdentitySequence.BuildFiles, ct);
                await _context.RewindAsync(IdentitySequence.BuildLogs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not rewind identity sequences after deleting build {Id}", id);
            }

            // Local files: best effort - the worker may still hold them if it just crashed.
            try
            {
                var buildDir = Path.Combine(_config["Build:RootPath"] ?? "builds", build.BuildId);
                if (Directory.Exists(buildDir))
                    Directory.Delete(buildDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete local directory for build {BuildId}", build.BuildId);
            }

            // Staged .mrpack of a modpack import: no longer referenced by any row.
            try
            {
                var staging = ModpackImportService.GetStagingPath(_config, build.BuildId);
                if (System.IO.File.Exists(staging))
                    System.IO.File.Delete(staging);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete staged modpack for build {BuildId}", build.BuildId);
            }

            // Uploaded objects: best effort, they are only referenced by this build.
            try
            {
                await _s3Storage.DeleteObjectsAsync(s3Keys, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete S3 objects for build {BuildId}", build.BuildId);
            }

            _logger.LogInformation("Build {BuildId} deleted by {Actor} ({FileCount} files)", build.BuildId, actor, s3Keys.Count);

            return Ok(new { Message = "Build deleted", FileCount = s3Keys.Count });
        }

        /// <summary>
        /// Gets the client manifest for a build (public endpoint for launcher).
        /// </summary>
        [HttpGet("{buildId}/manifest")]
        [AllowAnonymous]
        public async Task<ActionResult<ClientManifestResponse>> GetClientManifest(string buildId, CancellationToken ct)
        {
            var build = await _context.BuildInstances
                .AsNoTracking()
                .Where(b => b.BuildId == buildId && b.Status == "completed")
                .Select(b => new { b.BuildId, b.Status, b.ManifestJson })
                .FirstOrDefaultAsync(ct);

            if (build == null)
                return NotFound(new { Error = "BUILD_NOT_FOUND_OR_NOT_READY" });

            if (string.IsNullOrEmpty(build.ManifestJson))
                return StatusCode(500, new { Error = "MANIFEST_NOT_GENERATED" });

            var manifest = System.Text.Json.JsonSerializer.Deserialize<ClientManifestResponse>(build.ManifestJson);
            if (manifest == null)
                return StatusCode(500, new { Error = "MANIFEST_DESERIALIZE_ERROR" });

            return Ok(manifest);
        }
    }
}
