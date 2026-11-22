using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Services;

/// <summary>
/// Role permission lookup with a short cache. The admin role is implicit: it always has every
/// permission, regardless of the rows in <c>role_permissions</c>.
/// </summary>
public class PermissionService : IPermissionService
{
    // Short cache: admin traffic is low, and permission edits must become visible quickly on
    // every API replica (each replica has its own memory cache; eviction is local).
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PermissionService> _logger;

    public PermissionService(
        AppDbContext context,
        IMemoryCache cache,
        ILogger<PermissionService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        var normalizedRole = NormalizeRole(role);
        if (normalizedRole == Roles.Admin)
            return Permissions.AllKeys;

        if (string.IsNullOrEmpty(normalizedRole))
            return new HashSet<string>(StringComparer.Ordinal);

        var cacheKey = CacheKey(normalizedRole);
        if (_cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached) && cached != null)
            return cached;

        var stored = await _context.RolePermissions
            .AsNoTracking()
            .Where(r => r.Role == normalizedRole)
            .Select(r => r.Permission)
            .ToListAsync(cancellationToken);

        // Ignore rows for permissions that no longer exist after a deploy.
        var set = stored
            .Where(Permissions.AllKeys.Contains)
            .ToHashSet(StringComparer.Ordinal);

        _cache.Set(cacheKey, (IReadOnlySet<string>)set, CacheDuration);
        return set;
    }

    public async Task<bool> HasPermissionAsync(
        string role,
        string permission,
        CancellationToken cancellationToken = default)
    {
        if (NormalizeRole(role) == Roles.Admin)
            return true;

        var permissions = await GetPermissionsAsync(role, cancellationToken);
        return permissions.Contains(permission);
    }

    public async Task<bool> HasAnyPermissionAsync(
        string role,
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default)
    {
        if (NormalizeRole(role) == Roles.Admin)
            return true;

        var granted = await GetPermissionsAsync(role, cancellationToken);
        return permissions.Any(granted.Contains);
    }

    public async Task<(bool Success, string? Error)> SetRolePermissionsAsync(
        string role,
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default)
    {
        var normalizedRole = NormalizeRole(role);
        if (normalizedRole == Roles.Admin)
            return (false, "ADMIN_ROLE_IMMUTABLE");

        if (!Roles.Configurable.Contains(normalizedRole))
            return (false, "INVALID_ROLE");

        var requested = permissions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var canonical = new List<string>(requested.Count);
        foreach (var permission in requested)
        {
            var known = Permissions.All.FirstOrDefault(
                p => string.Equals(p.Key, permission, StringComparison.OrdinalIgnoreCase));

            if (known == null)
                return (false, "INVALID_PERMISSION");

            canonical.Add(known.Key);
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var existing = await _context.RolePermissions
            .Where(r => r.Role == normalizedRole)
            .ToListAsync(cancellationToken);

        _context.RolePermissions.RemoveRange(existing);

        if (existing.Count > 0)
            await _context.SaveChangesAsync(cancellationToken);

        _context.RolePermissions.AddRange(canonical.Select(permission => new RolePermission
        {
            Role = normalizedRole,
            Permission = permission
        }));

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Invalidate();
        _logger.LogInformation(
            "Permissions for role {Role} updated to: {Permissions}",
            normalizedRole, string.Join(", ", canonical));

        return (true, null);
    }

    public void Invalidate()
    {
        foreach (var role in Roles.Configurable)
            _cache.Remove(CacheKey(role));
    }

    private static string CacheKey(string role) => $"role-permissions:{role}";

    private static string NormalizeRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? string.Empty : role.Trim().ToLowerInvariant();
}
