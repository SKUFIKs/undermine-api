using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Services;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Controllers;

[ApiController]
[Route("api/v1.0/admin/presets")]
[Authorize(Policy = Permissions.PresetsCreate)]
[EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
public class AdminPresetsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AdminAuditService _audit;

    public AdminPresetsController(AppDbContext context, AdminAuditService audit)
    {
        _context = context;
        _audit = audit;
    }

    private string Actor => User.Identity?.Name ?? "system";

    [HttpGet]
    public async Task<IActionResult> GetPresets(CancellationToken ct)
    {
        var presets = await _context.BuildPresets
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        return Ok(presets);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetPreset(long id, CancellationToken ct)
    {
        var preset = await _context.BuildPresets.FindAsync([id], ct);
        if (preset == null) return NotFound();
        return Ok(preset);
    }

    [HttpPost]
    public async Task<IActionResult> CreatePreset([FromBody] BuildPresetUpsertDto request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var preset = new BuildPreset
        {
            Name = request.Name.Trim(),
            GameVersion = request.GameVersion.Trim(),
            LoaderType = request.LoaderType.Trim().ToLowerInvariant(),
            LoaderVersion = string.IsNullOrWhiteSpace(request.LoaderVersion)
                ? null
                : request.LoaderVersion.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.BuildPresets.Add(preset);
        await _context.SaveChangesAsync(ct);
        await _audit.LogAsync(Actor, "create_preset", "preset", preset.Id, preset.Name, ct);
        return CreatedAtAction(nameof(GetPreset), new { id = preset.Id }, preset);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePreset(long id, [FromBody] BuildPresetUpsertDto request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var preset = await _context.BuildPresets.FindAsync([id], ct);
        if (preset == null) return NotFound();

        preset.Name = request.Name.Trim();
        preset.GameVersion = request.GameVersion.Trim();
        preset.LoaderType = request.LoaderType.Trim().ToLowerInvariant();
        preset.LoaderVersion = string.IsNullOrWhiteSpace(request.LoaderVersion)
            ? null
            : request.LoaderVersion.Trim();
        preset.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        await _audit.LogAsync(Actor, "update_preset", "preset", preset.Id, preset.Name, ct);
        return Ok(preset);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePreset(long id, CancellationToken ct)
    {
        var preset = await _context.BuildPresets.FindAsync([id], ct);
        if (preset == null) return NotFound();
        _context.BuildPresets.Remove(preset);
        await _context.SaveChangesAsync(ct);
        await _audit.LogAsync(Actor, "delete_preset", "preset", id, preset.Name, ct);

        // Deleted preset ids are retired and never reused.
        return NoContent();
    }
}
