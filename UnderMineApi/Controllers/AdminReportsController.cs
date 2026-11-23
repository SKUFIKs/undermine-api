using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Services;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Controllers;

/// <summary>
/// Admin support workflow for launcher/Minecraft crash reports: filter by player/device/status,
/// read metadata, download attachments, change the triage status and delete resolved reports.
/// The binary payload is never included in list/detail responses.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/reports")]
[Authorize]
[EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
public class AdminReportsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AdminAuditService _audit;
    private readonly ILogger<AdminReportsController> _logger;

    public AdminReportsController(
        AppDbContext context,
        AdminAuditService audit,
        ILogger<AdminReportsController> logger)
    {
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    private string GetActor() =>
        LogSanitizer.Clean(User?.Identity?.Name
            ?? User?.FindFirst(ClaimTypes.Name)?.Value
            ?? "system");

    [HttpGet]
    [Authorize(Policy = Permissions.ReportsView)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? query = null,
        [FromQuery] long? userId = null,
        [FromQuery] int days = 0,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var reports = _context.ClientReports.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            if (!ClientReportService.AllowedStatuses.Contains(normalized))
                return BadRequest(new { Error = "INVALID_STATUS", Message = "Unknown report status." });

            reports = reports.Where(r => r.Status == normalized);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalized = kind.Trim().ToLowerInvariant();
            if (!ClientReportService.AllowedKinds.Contains(normalized))
                return BadRequest(new { Error = "INVALID_KIND", Message = "Unknown report kind." });

            reports = reports.Where(r => r.Kind == normalized);
        }

        if (userId.HasValue)
            reports = reports.Where(r => r.UserId == userId.Value);

        if (days > 0)
        {
            var since = DateTimeOffset.UtcNow
                .AddDays(-Math.Clamp(days, 1, 365))
                .ToUnixTimeSeconds();
            reports = reports.Where(r => r.CreatedAt >= since);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            if (term.Length > 100)
                term = term[..100];

            reports = reports.Where(r =>
                r.Title.ToLower().Contains(term) ||
                r.DeviceId.ToLower().Contains(term) ||
                (r.ServerName != null && r.ServerName.ToLower().Contains(term)) ||
                (r.User != null && r.User.Username.ToLower().Contains(term)));
        }

        var totalCount = await reports.CountAsync(cancellationToken);

        var items = await reports
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ClientReportListItemDto
            {
                Id = r.Id,
                UserId = r.UserId,
                Username = r.User != null ? r.User.Username : null,
                DeviceId = r.DeviceId,
                Kind = r.Kind,
                Title = r.Title,
                LauncherVersion = r.LauncherVersion,
                MinecraftVersion = r.MinecraftVersion,
                Loader = r.Loader,
                ServerName = r.ServerName,
                Status = r.Status,
                CreatedAt = r.CreatedAt,
                FileCount = r.Files.Count,
                TotalBytes = r.Files.Sum(f => f.Size)
            })
            .ToListAsync(cancellationToken);

        return Ok(new ClientReportPageDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        });
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.ReportsView)]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken = default)
    {
        var report = await _context.ClientReports
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new ClientReportDetailsDto
            {
                Id = r.Id,
                UserId = r.UserId,
                Username = r.User != null ? r.User.Username : null,
                DeviceId = r.DeviceId,
                Kind = r.Kind,
                Title = r.Title,
                Summary = r.Summary,
                LauncherVersion = r.LauncherVersion,
                MinecraftVersion = r.MinecraftVersion,
                Loader = r.Loader,
                ServerName = r.ServerName,
                Os = r.Os,
                Ip = r.Ip,
                Status = r.Status,
                AdminNote = r.AdminNote,
                ConsentVersion = r.ConsentVersion,
                CreatedAt = r.CreatedAt,
                ResolvedAt = r.ResolvedAt,
                ResolvedBy = r.ResolvedBy,
                FileCount = r.Files.Count,
                TotalBytes = r.Files.Sum(f => f.Size),
                Files = r.Files
                    .OrderBy(f => f.Id)
                    .Select(f => new ClientReportFileDto
                    {
                        Id = f.Id,
                        Kind = f.Kind,
                        FileName = f.FileName,
                        ContentType = f.ContentType,
                        Size = f.Size,
                        Sha256 = f.Sha256,
                        CreatedAt = f.CreatedAt
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (report == null)
            return NotFound(new { Error = "REPORT_NOT_FOUND", Message = "Report not found." });

        return Ok(report);
    }

    /// <summary>
    /// Downloads one attachment. The Content-Disposition filename is generated server-side from
    /// the report id and file kind, so a malicious client filename can never escape the header.
    /// </summary>
    [HttpGet("{id:long}/files/{fileId:long}")]
    [Authorize(Policy = Permissions.ReportsView)]
    public async Task<IActionResult> DownloadFile(
        long id,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        var file = await _context.ClientReportFiles
            .AsNoTracking()
            .Where(f => f.Id == fileId && f.ReportId == id)
            .Select(f => new { f.Kind, f.Data })
            .FirstOrDefaultAsync(cancellationToken);

        if (file == null)
            return NotFound(new { Error = "FILE_NOT_FOUND", Message = "Report file not found." });

        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        var safeKind = file.Kind.Replace("_", "-");
        var downloadName = $"report-{id}-{safeKind}.log";

        return File(file.Data, "text/plain; charset=utf-8", downloadName);
    }

    [HttpPatch("{id:long}")]
    [Authorize(Policy = Permissions.ReportsManage)]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] UpdateClientReportDto request,
        CancellationToken cancellationToken = default)
    {
        var report = await _context.ClientReports
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (report == null)
            return NotFound(new { Error = "REPORT_NOT_FOUND", Message = "Report not found." });

        string? normalizedStatus = null;
        if (request.Status != null)
        {
            normalizedStatus = request.Status.Trim().ToLowerInvariant();
            if (!ClientReportService.AllowedStatuses.Contains(normalizedStatus))
                return BadRequest(new { Error = "INVALID_STATUS", Message = "Unknown report status." });
        }

        string? note = null;
        if (request.AdminNote != null)
        {
            note = request.AdminNote.Trim();
            if (note.Length > 4000)
                return BadRequest(new { Error = "NOTE_TOO_LONG", Message = "The note can be at most 4000 characters." });
        }

        if (normalizedStatus == null && request.AdminNote == null)
            return BadRequest(new { Error = "NO_CHANGES", Message = "Nothing to update." });

        var actor = GetActor();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (normalizedStatus != null)
        {
            report.Status = normalizedStatus;

            if (normalizedStatus == "resolved")
            {
                report.ResolvedAt = now;
                report.ResolvedBy = actor;
            }
            else
            {
                report.ResolvedAt = null;
                report.ResolvedBy = null;
            }
        }

        if (note != null)
            report.AdminNote = note;

        await _context.SaveChangesAsync(cancellationToken);

        // The admin note may contain sensitive player data; only the status is audited.
        await _audit.LogAsync(
            actor,
            "update_client_report",
            "client_report",
            report.Id,
            $"status={report.Status}",
            cancellationToken);

        _logger.LogInformation(
            "Admin {Actor} updated report {ReportId} to {Status}.",
            actor, report.Id, LogSanitizer.Clean(report.Status));

        return Ok(new { Message = "Report updated.", Status = report.Status });
    }

    [HttpDelete("{id:long}")]
    [Authorize(Policy = Permissions.ReportsManage)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken = default)
    {
        var report = await _context.ClientReports
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (report == null)
            return NotFound(new { Error = "REPORT_NOT_FOUND", Message = "Report not found." });

        var actor = GetActor();

        _context.ClientReports.Remove(report);
        await _context.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(
            actor,
            "delete_client_report",
            "client_report",
            id,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Admin {Actor} deleted report {ReportId}.", actor, id);

        return Ok(new { Message = "Report deleted." });
    }

    /// <summary>Small summary used by the admin panel header: totals by status and last 24h.</summary>
    [HttpGet("stats")]
    [Authorize(Policy = Permissions.ReportsView)]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken = default)
    {
        var byStatus = await _context.ClientReports
            .AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var since = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var last24h = await _context.ClientReports
            .AsNoTracking()
            .CountAsync(r => r.CreatedAt >= since, cancellationToken);

        var total = byStatus.Sum(x => x.Count);

        return Ok(new
        {
            Total = total,
            New = byStatus.FirstOrDefault(x => x.Status == "new")?.Count ?? 0,
            Investigating = byStatus.FirstOrDefault(x => x.Status == "investigating")?.Count ?? 0,
            Resolved = byStatus.FirstOrDefault(x => x.Status == "resolved")?.Count ?? 0,
            Last24h = last24h
        });
    }
}
