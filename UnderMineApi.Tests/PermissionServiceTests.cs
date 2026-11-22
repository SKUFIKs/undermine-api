using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UnderMineApi.Data;
using UnderMineApi.Services;
using UnderMineApi.Services.Security;
using Xunit;

namespace UnderMineApi.Tests;

/// <summary>
/// Covers the role permission store: implicit admin access, seeded moderator defaults,
/// replacement of a role's permission set and rejection of unknown roles/permissions.
/// </summary>
public class PermissionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly PermissionService _service;

    public PermissionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _service = new PermissionService(
            _context,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PermissionService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AdminAlwaysHasEveryPermission()
    {
        var permissions = await _service.GetPermissionsAsync(Roles.Admin);

        Assert.Equal(Permissions.AllKeys.Count, permissions.Count);
        Assert.All(Permissions.AllKeys, permission => Assert.Contains(permission, permissions));
    }

    [Fact]
    public async Task ModeratorUsesSeededDefaults()
    {
        var permissions = await _service.GetPermissionsAsync(Roles.Moderator);

        Assert.Contains(Permissions.ServersView, permissions);
        Assert.Contains(Permissions.BuildsUpload, permissions);
        Assert.Contains(Permissions.UsersBan, permissions);
        Assert.DoesNotContain(Permissions.UsersChangeRole, permissions);
        Assert.DoesNotContain(Permissions.UsersChangePassword, permissions);
    }

    [Fact]
    public async Task UserStartsWithNoPermissions()
    {
        var permissions = await _service.GetPermissionsAsync(Roles.User);

        Assert.Empty(permissions);
    }

    [Fact]
    public async Task ReplacingPermissionsInvalidatesTheCache()
    {
        var initial = await _service.GetPermissionsAsync(Roles.Moderator);
        Assert.Contains(Permissions.UsersBan, initial);

        var result = await _service.SetRolePermissionsAsync(
            Roles.Moderator,
            new[] { Permissions.ServersView, Permissions.ServersEdit });

        Assert.True(result.Success, result.Error);

        var updated = await _service.GetPermissionsAsync(Roles.Moderator);
        Assert.Equal(2, updated.Count);
        Assert.Contains(Permissions.ServersView, updated);
        Assert.Contains(Permissions.ServersEdit, updated);
        Assert.DoesNotContain(Permissions.UsersBan, updated);

        var stored = await _context.RolePermissions
            .Where(p => p.Role == Roles.Moderator)
            .Select(p => p.Permission)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task RejectsUnknownPermissionsAndRoles()
    {
        var invalidPermission = await _service.SetRolePermissionsAsync(
            Roles.User, new[] { "root.everything" });
        Assert.False(invalidPermission.Success);
        Assert.Equal("INVALID_PERMISSION", invalidPermission.Error);

        var adminRole = await _service.SetRolePermissionsAsync(
            Roles.Admin, new[] { Permissions.ServersView });
        Assert.False(adminRole.Success);
        Assert.Equal("ADMIN_ROLE_IMMUTABLE", adminRole.Error);

        var unknownRole = await _service.SetRolePermissionsAsync(
            "owner", new[] { Permissions.ServersView });
        Assert.False(unknownRole.Success);
        Assert.Equal("INVALID_ROLE", unknownRole.Error);
    }

    [Fact]
    public async Task AnyPermissionChecksTheWholeGroup()
    {
        await _service.SetRolePermissionsAsync(Roles.User, new[] { Permissions.UsersBan });

        Assert.True(await _service.HasAnyPermissionAsync(
            Roles.User,
            new[] { Permissions.UsersChangeRole, Permissions.UsersBan }));
        Assert.False(await _service.HasAnyPermissionAsync(
            Roles.User,
            new[] { Permissions.UsersChangeRole, Permissions.UsersChangePassword }));
    }
}
