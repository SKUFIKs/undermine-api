using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;

namespace UnderMineApi.Controllers;

[ApiController]
[Route("api/v1.0/admin/presets")]
[Authorize(Roles = "admin")]
public class AdminPresetsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<AdminPresetsController> _logger;

    public AdminPresetsController(AppDbContext context, ILogger<AdminPresetsController> logger)
    {
        _context = context;
        _logger = logger;
    }

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
    public async Task<IActionResult> CreatePreset([FromBody] BuildPreset preset, CancellationToken ct)
    {
        preset.Id = 0;
        preset.CreatedAt = DateTime.UtcNow;
        preset.UpdatedAt = DateTime.UtcNow;
        _context.BuildPresets.Add(preset);
        await _context.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetPresets), new { id = preset.Id }, preset);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePreset(long id, [FromBody] BuildPreset updated, CancellationToken ct)
    {
        var preset = await _context.BuildPresets.FindAsync([id], ct);
        if (preset == null) return NotFound();
        preset.Name = updated.Name;
        preset.GameVersion = updated.GameVersion;
        preset.LoaderType = updated.LoaderType;
        preset.LoaderVersion = updated.LoaderVersion;
        preset.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return Ok(preset);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePreset(long id, CancellationToken ct)
    {
        var preset = await _context.BuildPresets.FindAsync([id], ct);
        if (preset == null) return NotFound();
        _context.BuildPresets.Remove(preset);
        await _context.SaveChangesAsync(ct);

        // Rewind the sequence so the freed id is reused by the next preset. Housekeeping only.
        try
        {
            await _context.RewindAsync(IdentitySequence.Presets, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rewind identity sequence after deleting preset {Id}", id);
        }

        return NoContent();
    }
}
