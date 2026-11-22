using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.Models;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.PasswordHasher;
using Xunit;

namespace UnderMineApi.Tests;

/// <summary>
/// Covers the irreversible admin account deletion: confirmation, ownership guards and the
/// transactional cleanup of statistics and ban history.
/// </summary>
public class UserDeletionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly FakeAccessService _access = new();
    private readonly UserService _service;

    public UserDeletionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _service = new UserService(
            _context,
            new BcryptPasswordHasher(),
            NullLogger<UserService>.Instance,
            new AdminAuditService(
                _context,
                NullLogger<AdminAuditService>.Instance,
                new HttpContextAccessor()),
            _access,
            new FakeJwtService());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task DeletesAccountStatsBansAndWritesAudit()
    {
        var (user, admin) = await SeedAsync();

        var result = await _service.DeleteUserAsync(
            user.Id, "target", "policy violation", admin.Id, "root");

        Assert.True(result.Success, result.Error);
        Assert.Null(await _context.McAuthAccounts.FindAsync(user.Id));
        Assert.Empty(await _context.UserData.Where(s => s.AccountId == user.Id).ToListAsync());
        Assert.Empty(await _context.UserBans.Where(b => b.UserId == user.Id).ToListAsync());
        Assert.Empty(await _context.UserIpHistory.Where(h => h.UserId == user.Id).ToListAsync());
        Assert.Contains(user.Id, _access.Invalidated);

        var audit = await _context.AdminAudits.SingleAsync();
        Assert.Equal("delete_user", audit.Action);
        Assert.Equal("user", audit.TargetType);
        Assert.Equal(user.Id, audit.TargetId);
        Assert.Contains("target", audit.Details);
        Assert.Contains("policy violation", audit.Details);
    }

    [Fact]
    public async Task RejectsDeletingOwnAccount()
    {
        var (_, admin) = await SeedAsync();

        var result = await _service.DeleteUserAsync(
            admin.Id, "root", "cleanup", admin.Id, "root");

        Assert.False(result.Success);
        Assert.Equal("CANNOT_DELETE_SELF", result.Error);
        Assert.NotNull(await _context.McAuthAccounts.FindAsync(admin.Id));
    }

    [Fact]
    public async Task RejectsDeletingAnAdministrator()
    {
        var (user, admin) = await SeedAsync();

        var result = await _service.DeleteUserAsync(
            admin.Id, "root", "cleanup", user.Id, "target");

        Assert.False(result.Success);
        Assert.Equal("CANNOT_DELETE_ADMIN", result.Error);
        Assert.NotNull(await _context.McAuthAccounts.FindAsync(admin.Id));
    }

    [Fact]
    public async Task RequiresTheExactUsername()
    {
        var (user, admin) = await SeedAsync();

        var result = await _service.DeleteUserAsync(
            user.Id, "wrong-name", "policy violation", admin.Id, "root");

        Assert.False(result.Success);
        Assert.Equal("CONFIRMATION_MISMATCH", result.Error);
        Assert.NotNull(await _context.McAuthAccounts.FindAsync(user.Id));
    }

    [Fact]
    public async Task RequiresAReason()
    {
        var (user, admin) = await SeedAsync();

        var result = await _service.DeleteUserAsync(
            user.Id, "target", "no", admin.Id, "root");

        Assert.False(result.Success);
        Assert.Equal("INVALID_REASON", result.Error);
        Assert.NotNull(await _context.McAuthAccounts.FindAsync(user.Id));
    }

    [Fact]
    public async Task ReturnsNotFoundForMissingUser()
    {
        var (_, admin) = await SeedAsync();

        var result = await _service.DeleteUserAsync(
            99999, "ghost", "policy violation", admin.Id, "root");

        Assert.False(result.Success);
        Assert.Equal("USER_NOT_FOUND", result.Error);
    }

    [Fact]
    public async Task RefusesToDemoteTheLastAdministrator()
    {
        var (_, admin) = await SeedAsync();

        var result = await _service.ChangeUserRoleAsync(admin.Id, "user", "root");

        Assert.False(result.Success);
        Assert.Equal("LAST_ADMIN", result.Error);

        var stored = await _context.McAuthAccounts.FindAsync(admin.Id);
        Assert.Equal("admin", stored!.Role);
    }

    [Fact]
    public async Task AllowsDemotingAnAdministratorWhenAnotherAdminExists()
    {
        var (_, admin) = await SeedAsync();
        _context.McAuthAccounts.Add(NewUser("second-admin", "admin"));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _service.ChangeUserRoleAsync(admin.Id, "user", "root");

        Assert.True(result.Success, result.Error);
        var stored = await _context.McAuthAccounts.FindAsync(admin.Id);
        Assert.Equal("user", stored!.Role);
    }

    private async Task<(UserAccount User, UserAccount Admin)> SeedAsync()
    {
        var user = NewUser("target", "user");
        var admin = NewUser("root", "admin");

        _context.McAuthAccounts.AddRange(user, admin);
        await _context.SaveChangesAsync();

        _context.UserBans.Add(new UserBans
        {
            UserId = user.Id,
            Reason = "previous ban",
            BannedBy = "root",
            CreatedAt = 1,
            IsActive = true
        });
        _context.UserIpHistory.Add(new UserIpHistory
        {
            UserId = user.Id,
            Ip = "203.0.113.10",
            FirstSeen = 1,
            LastSeen = 1,
            SeenCount = 1
        });
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        return (user, admin);
    }

    private static UserAccount NewUser(string username, string role) => new()
    {
        Uuid = Guid.NewGuid().ToString("N"),
        Username = username,
        RealName = username,
        HashPassword = "not-used-in-these-tests",
        RegDate = 1,
        Role = role,
        Stats = new UserData()
    };

    private sealed class FakeAccessService : IUserAccessService
    {
        public List<long> Invalidated { get; } = new();

        public Task<UserAccessSnapshot> GetSnapshotAsync(long userId, CancellationToken ct = default) =>
            Task.FromResult(UserAccessSnapshot.Missing);

        public void Invalidate(long userId) => Invalidated.Add(userId);
    }

    private sealed class FakeJwtService : IJwtService
    {
        public string GenerateToken(UserAccount user) => "token";

        public ClaimsPrincipal ValidateToken(string token) => new(new ClaimsIdentity());
    }
}
