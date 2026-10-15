using UnderMineApi.Data;
using UnderMineApi.Models;
using UnderMineApi.Extensions;
using Microsoft.AspNetCore.Http;

namespace UnderMineApi.Services
{
    public class AdminAuditService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<AdminAuditService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AdminAuditService(AppDbContext context, ILogger<AdminAuditService> logger, IHttpContextAccessor httpContextAccessor)
        {
            _context = context;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogAsync(string actor, string action, string? targetType = null, long? targetId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var audit = new AdminAudit
                {
                    Actor = actor,
                    Action = action,
                    TargetType = targetType,
                    TargetId = targetId,
                    Details = details,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                _context.AdminAudits.Add(audit);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogStructuredError(ex, "Failed to write admin audit", _httpContextAccessor.HttpContext);
            }
        }
    }
}
