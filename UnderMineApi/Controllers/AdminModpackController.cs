using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers;

/// <summary>
/// Admin endpoints for importing ready-made modpacks as builds.
///
///   POST /api/v1.0/admin/builds/import-modpack/modrinth - by Modrinth slug/id
///   POST /api/v1.0/admin/builds/import-modpack/file     - by uploaded archive (.mrpack or
///                                                          CurseForge pack zip)
///
/// Both stage the archive, parse its manifest (fail fast on invalid input), create a
/// BuildInstance in "pending" status with a ModpackImport descriptor and let the background
/// build worker run the BuildType.ModpackImport scenario. The archive format is detected from
/// its contents: modrinth.index.json for Modrinth, manifest.json for CurseForge.
/// </summary>
[ApiController]
[Route("api/v1.0/admin/builds/import-modpack")]
[Authorize(Roles = "admin")]
public class AdminModpackController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ModpackImportService _modpacks;
    private readonly ILogger<AdminModpackController> _logger;

    public AdminModpackController(
        AppDbContext context,
        ModpackImportService modpacks,
        ILogger<AdminModpackController> logger)
    {
        _context = context;
        _modpacks = modpacks;
        _logger = logger;
    }

    /// <summary>Imports the latest release (or a pinned version) of a Modrinth modpack.</summary>
    [HttpPost("modrinth")]
    public async Task<ActionResult<BuildResponse>> ImportFromModrinth(
        [FromBody] ImportModpackRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var slug = request.Slug.Trim();
        if (slug.Length == 0)
        {
            return BadRequest(new { Error = "SLUG_REQUIRED", Message = "Modpack slug or id is required" });
        }

        var server = await FindServerAsync(request.ServerId, ct);
        if (request.ServerId != null && server == null)
        {
            return NotFound(new { Error = "SERVER_NOT_FOUND", Message = $"Server {request.ServerId} not found" });
        }

        var buildId = Guid.NewGuid().ToString("N");

        StagedModpack staged;
        try
        {
            staged = await _modpacks.StageFromModrinthAsync(slug, request.VersionId, buildId, ct);
        }
        catch (ModpackNotFoundException ex)
        {
            return NotFound(new { Error = "MODPACK_NOT_FOUND", Message = ex.Message });
        }
        catch (ModpackImportException ex)
        {
            return BadRequest(new { Error = "MODPACK_IMPORT_FAILED", Message = ex.Message });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return StatusCode(429, new { Error = "MODRINTH_RATE_LIMIT", Message = "Modrinth API rate limit exceeded" });
        }

        var info = new ModpackImportInfo
        {
            SourceType = ModpackImportInfo.SourceModrinth,
            SlugOrId = slug,
            VersionId = staged.VersionId,
            ModpackName = staged.Index.Name
        };

        return await CreateBuildAsync(request.Name, staged, info, server, ct);
    }

    /// <summary>
    /// Imports an admin-uploaded modpack archive: an .mrpack from Modrinth or a CurseForge
    /// pack zip (manifest.json + overrides, mods referenced by projectID/fileID).
    /// The format is detected from the archive contents, not from the file name.
    /// </summary>
    [HttpPost("file")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ModpackImportService.MaxFileBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ModpackImportService.MaxFileBytes)]
    public async Task<ActionResult<BuildResponse>> ImportFromFile(
        [FromForm] IFormFile file,
        [FromForm] string? name,
        [FromForm] long? serverId,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { Error = "FILE_REQUIRED", Message = "No modpack file uploaded" });
        }

        var originalName = Path.GetFileName(file.FileName);
        if (!originalName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
            && !originalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                Error = "INVALID_FILE_TYPE",
                Message = "Only .mrpack or .zip modpack files are accepted"
            });
        }

        var server = await FindServerAsync(serverId, ct);
        if (serverId != null && server == null)
        {
            return NotFound(new { Error = "SERVER_NOT_FOUND", Message = $"Server {serverId} not found" });
        }

        var buildId = Guid.NewGuid().ToString("N");

        StagedModpack staged;
        try
        {
            await using var stream = file.OpenReadStream();
            staged = await _modpacks.StageFromUploadAsync(stream, originalName, buildId, ct);
        }
        catch (ModpackImportException ex)
        {
            return BadRequest(new { Error = "MODPACK_IMPORT_FAILED", Message = ex.Message });
        }

        var info = new ModpackImportInfo
        {
            SourceType = ModpackImportInfo.SourceFile,
            FileName = originalName,
            ModpackName = staged.Index.Name
        };

        return await CreateBuildAsync(name, staged, info, server, ct);
    }

    /// <summary>
    /// Validates the index dependencies, creates the queued BuildInstance and optionally
    /// pins the requested server to it.
    /// </summary>
    private async Task<ActionResult<BuildResponse>> CreateBuildAsync(
        string? nameOverride,
        StagedModpack staged,
        ModpackImportInfo info,
        ServerManifest? server,
        CancellationToken ct)
    {
        string mcVersion;
        string loaderType;
        string? loaderVersion;

        try
        {
            (mcVersion, loaderType, loaderVersion) = _modpacks.ResolveDependencies(staged.Index);
        }
        catch (ModpackImportException ex)
        {
            // No build row yet - drop the staged archive so nothing leaks.
            _modpacks.CleanupStaging(staged.BuildId);
            return BadRequest(new { Error = "MODPACK_INDEX_INVALID", Message = ex.Message });
        }

        var name = Truncate(
            string.IsNullOrWhiteSpace(nameOverride) ? staged.Index.Name ?? staged.FileName : nameOverride,
            100);

        var build = new BuildInstance
        {
            Name = name,
            McVersion = mcVersion,
            LoaderType = loaderType,
            LoaderVersion = loaderVersion,
            Status = "pending",
            BuildId = staged.BuildId,
            ModpackImport = info.Serialize()
        };

        _context.BuildInstances.Add(build);
        await _context.SaveChangesAsync(ct);

        if (server != null)
        {
            // Pin now: the server starts serving the manifest as soon as the build completes.
            server.BuildId = build.BuildId;
            server.BuildManifest = null;
            await _context.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Admin {Actor} queued modpack import {BuildId}: '{Name}' ({McVersion}, {Loader} {LoaderVersion}), source {Source}, version {ModpackVersion}",
            User.Identity?.Name ?? "system", build.BuildId, name, mcVersion, loaderType,
            loaderVersion, info.SourceType, staged.VersionLabel ?? staged.FileName);

        return Ok(Map(build));
    }

    private async Task<ServerManifest?> FindServerAsync(long? serverId, CancellationToken ct) =>
        serverId == null
            ? null
            : await _context.ServerManifests.FindAsync([serverId.Value], ct);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static BuildResponse Map(BuildInstance build) => new()
    {
        Id = build.Id,
        Name = build.Name,
        McVersion = build.McVersion,
        LoaderType = build.LoaderType,
        LoaderVersion = build.LoaderVersion,
        BuildPresetId = build.BuildPresetId,
        Status = build.Status,
        BuildId = build.BuildId,
        ErrorMessage = build.ErrorMessage,
        CreatedAt = build.CreatedAt,
        UpdatedAt = build.UpdatedAt,
        CompletedAt = build.CompletedAt,
        BuildType = nameof(BuildType.ModpackImport)
    };
}
