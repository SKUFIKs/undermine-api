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

namespace UnderMineApi.Controllers;

/// <summary>
/// Receives crash reports from the launcher. Uploads are accepted only with an explicit consent
/// flag; authenticated launchers are linked to the player account, anonymous ones (for example a
/// crash on the login screen) are grouped by device id. The rate limiter, the strict 30 MB size
/// cap and the .log/.txt-only rule keep this public endpoint from being used as free storage.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingExtensions.ReportsPolicy)]
public class ClientReportsController : ControllerBase
{
    private readonly ClientReportService _reports;
    private readonly AppDbContext _context;
    private readonly ILogger<ClientReportsController> _logger;

    public ClientReportsController(
        ClientReportService reports,
        AppDbContext context,
        ILogger<ClientReportsController> logger)
    {
        _reports = reports;
        _context = context;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(40 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 40 * 1024 * 1024, ValueCountLimit = 50)]
    public async Task<IActionResult> Create(
        [FromForm] ClientReportForm form,
        CancellationToken cancellationToken = default)
    {
        if (!form.Consent)
        {
            return BadRequest(new
            {
                status = StatusCodes.Status400BadRequest,
                error = "CONSENT_REQUIRED",
                message = "The report can only be sent after the player explicitly consents.",
                request_id = RequestId()
            });
        }

        // Optional authentication: a valid Bearer token links the report to the account, while
        // launcher-startup crashes are still accepted with a device id.
        long? userId = null;
        if (User.Identity?.IsAuthenticated == true)
        {
            var raw = User.FindFirst("UserId")?.Value
                      ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (long.TryParse(raw, out var parsed) &&
                await _context.McAuthAccounts.AsNoTracking()
                    .AnyAsync(u => u.Id == parsed, cancellationToken))
            {
                userId = parsed;
            }
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var reportId = await _reports.CreateAsync(form, userId, ip, cancellationToken);

            _logger.LogInformation(
                "Report {ReportId} accepted (user {UserId}).", reportId, userId);

            return Ok(new
            {
                id = reportId,
                status = "received",
                request_id = RequestId()
            });
        }
        catch (ClientReportException ex)
        {
            return BadRequest(new
            {
                status = StatusCodes.Status400BadRequest,
                error = ex.Code,
                message = ex.Message,
                request_id = RequestId()
            });
        }
        catch (Exception ex)
        {
            // Do not include the form values: they may contain player-provided text.
            _logger.LogError(ex, "Failed to store a client report.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                status = StatusCodes.Status500InternalServerError,
                error = "INTERNAL_ERROR",
                message = "Could not store the report. Please try again later.",
                request_id = RequestId()
            });
        }
    }

    private string? RequestId() =>
        HttpContext.Items.TryGetValue("RequestId", out var id) ? id?.ToString() : null;
}
