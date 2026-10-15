using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers;

[ApiController]
[Route("api/v1.0/admin")]
[Authorize(Roles = "admin")]
public class AdminModsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ModrinthService _modrinthService;
    private readonly CustomArtifactService _customArtifactService;
    private readonly S3StorageService _s3Storage;

    public AdminModsController(
        AppDbContext context,
        ModrinthService modrinthService,
        CustomArtifactService customArtifactService,
        S3StorageService s3Storage)
    {
        _context = context;
        _modrinthService = modrinthService;
        _customArtifactService = customArtifactService;
        _s3Storage = s3Storage;
    }

    [HttpGet("presets/{presetId}/mods")]
    public async Task<IActionResult> GetMods(long presetId, CancellationToken ct)
    {
        var preset = await _context.BuildPresets.FindAsync([presetId], ct);
        if (preset == null)
        {
            return NotFound("Preset not found");
        }

        var mods = await _context.BuildPresetMods
            .Where(m => m.BuildPresetId == presetId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var result = mods.Select(m => new ModPresetModDto
        {
            Id = m.Id,
            BuildPresetId = m.BuildPresetId,
            SourceType = m.SourceType,
            TargetFolder = m.TargetFolder,
            ModrinthSlugOrId = m.ModrinthSlugOrId,
            CustomFileName = m.CustomFileName,
            CustomFileS3Key = m.CustomFileS3Key,
            Sha256Hash = m.Sha256Hash,
            FileSize = m.FileSize,
            IsEnabled = m.IsEnabled,
            CreatedAt = m.CreatedAt,
            UpdatedAt = m.UpdatedAt
        }).ToList();

        return Ok(result);
    }

    [HttpGet("modrinth/search")]
    public async Task<IActionResult> SearchModrinth([FromQuery] string query, [FromQuery] string mcVersion, [FromQuery] string loader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(loader))
        {
            return BadRequest("Query, mcVersion and loader are required");
        }

        try
        {
            var result = await _modrinthService.SearchModsAsync(query, mcVersion, loader, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return StatusCode(429, "Modrinth API rate limit exceeded");
        }
    }

    [HttpPost("presets/{presetId}/mods/modrinth")]
    public async Task<IActionResult> AddModrinthMod(long presetId, [FromBody] AddModrinthModRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var preset = await _context.BuildPresets.FindAsync([presetId], ct);
        if (preset == null)
        {
            return NotFound("Preset not found");
        }

        var targetFolder = request.TargetFolder ?? "mods";
        if (targetFolder != "mods" && targetFolder != "shaderpacks" && targetFolder != "resourcepacks")
        {
            return BadRequest("Target folder must be mods, shaderpacks, or resourcepacks");
        }

        // Check for duplicates
        var existing = await _context.BuildPresetMods
            .AnyAsync(m => m.BuildPresetId == presetId && m.ModrinthSlugOrId == request.SlugOrId && m.SourceType == "Modrinth" && m.TargetFolder == targetFolder, ct);
        if (existing)
        {
            return Conflict("Mod already exists in this preset");
        }

        // Validate compatibility
        var version = await _modrinthService.GetCompatibleVersionAsync(request.SlugOrId, preset.GameVersion, preset.LoaderType, ct);
        if (version == null)
        {
            return BadRequest("No compatible version found for this Minecraft version/loader");
        }

        var mod = new BuildPresetMod
        {
            Id = Guid.NewGuid(),
            BuildPresetId = presetId,
            SourceType = "Modrinth",
            TargetFolder = targetFolder,
            ModrinthSlugOrId = request.SlugOrId,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.BuildPresetMods.Add(mod);
        await _context.SaveChangesAsync(ct);

        var dto = new ModPresetModDto
        {
            Id = mod.Id,
            BuildPresetId = mod.BuildPresetId,
            SourceType = mod.SourceType,
            TargetFolder = mod.TargetFolder,
            ModrinthSlugOrId = mod.ModrinthSlugOrId,
            IsEnabled = mod.IsEnabled,
            CreatedAt = mod.CreatedAt,
            UpdatedAt = mod.UpdatedAt
        };

        return CreatedAtAction(nameof(GetMods), new { presetId }, dto);
    }

    [HttpPost("presets/{presetId}/mods/custom")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddCustomMod(long presetId, [FromForm] IFormFile file, [FromForm] string targetFolder = "mods", CancellationToken ct = default)
    {
        var preset = await _context.BuildPresets.FindAsync([presetId], ct);
        if (preset == null)
        {
            return NotFound("Preset not found");
        }

        if (targetFolder != "mods" && targetFolder != "shaderpacks" && targetFolder != "resourcepacks")
        {
            return BadRequest("Target folder must be mods, shaderpacks, or resourcepacks");
        }

        try
        {
            var result = await _customArtifactService.UploadCustomArtifactAsync(file, targetFolder, ct);

            // Check for duplicates by hash in same preset
            var existing = await _context.BuildPresetMods
                .AnyAsync(m => m.BuildPresetId == presetId && m.Sha256Hash == result.Sha256 && m.TargetFolder == targetFolder, ct);
            if (existing)
            {
                return Conflict("File with same hash already exists in this preset");
            }

            var mod = new BuildPresetMod
            {
                Id = Guid.NewGuid(),
                BuildPresetId = presetId,
                SourceType = "CustomUpload",
                TargetFolder = targetFolder,
                CustomFileName = result.FileName,
                CustomFileS3Key = result.S3Key,
                Sha256Hash = result.Sha256,
                FileSize = result.Size,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.BuildPresetMods.Add(mod);
            await _context.SaveChangesAsync(ct);

            var dto = new ModPresetModDto
            {
                Id = mod.Id,
                BuildPresetId = mod.BuildPresetId,
                SourceType = mod.SourceType,
                TargetFolder = mod.TargetFolder,
                CustomFileName = mod.CustomFileName,
                CustomFileS3Key = mod.CustomFileS3Key,
                Sha256Hash = mod.Sha256Hash,
                FileSize = mod.FileSize,
                IsEnabled = mod.IsEnabled,
                CreatedAt = mod.CreatedAt,
                UpdatedAt = mod.UpdatedAt
            };

            return CreatedAtAction(nameof(GetMods), new { presetId }, dto);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("presets/{presetId}/mods/{modId}")]
    public async Task<IActionResult> DeleteMod(long presetId, Guid modId, CancellationToken ct)
    {
        var mod = await _context.BuildPresetMods
            .FirstOrDefaultAsync(m => m.Id == modId && m.BuildPresetId == presetId, ct);
        if (mod == null)
        {
            return NotFound("Mod not found");
        }

        // If custom and unique, optionally remove from S3
        if (mod.SourceType == "CustomUpload" && !string.IsNullOrEmpty(mod.CustomFileS3Key))
        {
            // Check if used by other presets
            var usageCount = await _context.BuildPresetMods
                .CountAsync(m => m.CustomFileS3Key == mod.CustomFileS3Key && m.Id != modId, ct);
            if (usageCount == 0)
            {
                await _s3Storage.RemoveFileIfUnusedAsync(mod.CustomFileS3Key, ct);
            }
        }

        _context.BuildPresetMods.Remove(mod);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPatch("presets/{presetId}/mods/{modId}/toggle")]
    public async Task<IActionResult> ToggleMod(long presetId, Guid modId, [FromBody] ToggleModRequest? request = null, CancellationToken ct = default)
    {
        var mod = await _context.BuildPresetMods
            .FirstOrDefaultAsync(m => m.Id == modId && m.BuildPresetId == presetId, ct);
        if (mod == null)
        {
            return NotFound("Mod not found");
        }

        mod.IsEnabled = request?.Enabled ?? !mod.IsEnabled;
        mod.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Ok(new { mod.Id, mod.IsEnabled });
    }
}
