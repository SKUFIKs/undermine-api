using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Models;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Controllers;

/// <summary>
/// Administrative activity log: every audited admin action with filters, paging and a compact
/// summary (counts, unique actors, top actions) for monitoring changes made in the panel.
/// </summary>
[ApiController]
[Route("api/v1.0/admin/activity")]
[Authorize(Policy = Permissions.ActivityView)]
[EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
public class AdminActivityController : ControllerBase
{
    private readonly AppDbContext _context;

    public AdminActivityController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// GET /api/v1.0/admin/activity?page=1&amp;pageSize=25&amp;actor=admin&amp;action=ban_user&amp;days=7
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<AdminActivityResponse>> GetActivity(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? actor = null,
        [FromQuery] string? action = null,
        [FromQuery] string? targetType = null,
        [FromQuery] string? query = null,
        [FromQuery] int days = 0,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        days = Math.Clamp(days, 0, 365);

        IQueryable<AdminAudit> filtered = _context.AdminAudits.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(actor))
        {
            var value = actor.Trim().ToLowerInvariant();
            filtered = filtered.Where(a => a.Actor.ToLower() == value);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var value = action.Trim().ToLowerInvariant();
            filtered = filtered.Where(a => a.Action.ToLower() == value);
        }

        if (!string.IsNullOrWhiteSpace(targetType))
        {
            var value = targetType.Trim().ToLowerInvariant();
            filtered = filtered.Where(a => a.TargetType != null && a.TargetType.ToLower() == value);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            filtered = filtered.Where(a =>
                EF.Functions.ILike(a.Actor, pattern) ||
                EF.Functions.ILike(a.Action, pattern) ||
                (a.Details != null && EF.Functions.ILike(a.Details, pattern)));
        }

        var now = DateTimeOffset.UtcNow;

        if (days > 0)
        {
            var cutoff = now.AddDays(-days).ToUnixTimeSeconds();
            filtered = filtered.Where(a => a.CreatedAt >= cutoff);
        }

        var totalCount = await filtered.CountAsync(ct);
        var totalPages = totalCount == 0
            ? 0
            : (int)Math.Ceiling(totalCount / (double)pageSize);

        if (totalPages > 0 && page > totalPages)
            page = totalPages;

        var items = await filtered
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AdminActivityItemDto
            {
                Id = a.Id,
                Actor = a.Actor,
                Action = a.Action,
                TargetType = a.TargetType,
                TargetId = a.TargetId,
                Details = a.Details,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync(ct);

        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        var weekStart = now.AddDays(-7).ToUnixTimeSeconds();

        var todayCount = await filtered.CountAsync(a => a.CreatedAt >= todayStart, ct);
        var last7DaysCount = await filtered.CountAsync(a => a.CreatedAt >= weekStart, ct);
        var uniqueActors = await filtered.Select(a => a.Actor).Distinct().CountAsync(ct);
        var actionTypeCount = await filtered.Select(a => a.Action).Distinct().CountAsync(ct);

        var topActions = await filtered
            .GroupBy(a => a.Action)
            .Select(g => new AdminActivityCountDto { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name)
            .Take(6)
            .ToListAsync(ct);

        var topActors = await filtered
            .GroupBy(a => a.Actor)
            .Select(g => new AdminActivityCountDto { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name)
            .Take(6)
            .ToListAsync(ct);

        // Dropdown options always cover the full history, not the current filter.
        var all = _context.AdminAudits.AsNoTracking();
        var actions = await all
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(a => a)
            .Take(500)
            .ToListAsync(ct);

        var actors = await all
            .Select(a => a.Actor)
            .Distinct()
            .OrderBy(a => a)
            .Take(500)
            .ToListAsync(ct);

        var targetTypes = await all
            .Where(a => a.TargetType != null && a.TargetType != "")
            .Select(a => a.TargetType!)
            .Distinct()
            .OrderBy(a => a)
            .Take(500)
            .ToListAsync(ct);

        return Ok(new AdminActivityResponse
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Summary = new AdminActivitySummaryDto
            {
                TotalCount = totalCount,
                TodayCount = todayCount,
                Last7DaysCount = last7DaysCount,
                UniqueActors = uniqueActors,
                ActionTypeCount = actionTypeCount,
                TopActions = topActions,
                TopActors = topActors
            },
            FilterOptions = new AdminActivityFilterOptionsDto
            {
                Actions = actions,
                Actors = actors,
                TargetTypes = targetTypes
            }
        });
    }
}
