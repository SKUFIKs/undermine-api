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
/// Covers the personal statistics shown in the admin panel and the IP history recorded on
/// registration/login.
/// </summary>
public class UserStatisticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly UserService _service;

    public UserStatisticsTests()
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
            new FakeAccessService(),
            new FakeJwtService());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RecordIp_CreatesThenAggregatesTheSameAddress()
    {
        var user = await SeedUserAsync();

        await _service.RecordIpAsync(user.Id, "203.0.113.5", 100);
        await _service.RecordIpAsync(user.Id, "203.0.113.5", 200);

        var row = Assert.Single(await _context.UserIpHistory.AsNoTracking().ToListAsync());
        Assert.Equal("203.0.113.5", row.Ip);
        Assert.Equal(100, row.FirstSeen);
        Assert.Equal(200, row.LastSeen);
        Assert.Equal(2, row.SeenCount);
    }

    [Fact]
    public async Task RecordIp_KeepsAtMostFiftyAddresses()
    {
        var user = await SeedUserAsync();

        for (var i = 0; i < 55; i++)
            await _service.RecordIpAsync(user.Id, $"198.51.100.{i}", 100 + i);

        var rows = await _context.UserIpHistory
            .AsNoTracking()
            .Where(h => h.UserId == user.Id)
            .ToListAsync();

        Assert.Equal(50, rows.Count);
        Assert.DoesNotContain(rows, h => h.Ip == "198.51.100.0");
        Assert.Contains(rows, h => h.Ip == "198.51.100.54");
    }

    [Fact]
    public async Task RecordLogin_UpdatesLastLoginAndIp()
    {
        var user = await SeedUserAsync();
        var tracked = await _context.McAuthAccounts.FirstAsync(u => u.Id == user.Id);

        await _service.RecordLoginAsync(tracked, "192.0.2.44", 500);

        _context.ChangeTracker.Clear();
        var saved = await _context.McAuthAccounts.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(500, saved.LastLogin);
        Assert.Equal("192.0.2.44", saved.Ip);

        var history = await _context.UserIpHistory.AsNoTracking().SingleAsync(h => h.UserId == user.Id);
        Assert.Equal("192.0.2.44", history.Ip);
        Assert.Equal(1, history.SeenCount);
    }

    [Fact]
    public async Task GetStatistics_ReturnsDatesStatsBanAndIpHistory()
    {
        var user = await SeedUserAsync();

        _context.UserBans.Add(new UserBans
        {
            UserId = user.Id,
            Reason = "cheating",
            BannedBy = "root",
            CreatedAt = 50,
            ExpiresAt = null,
            IsActive = true
        });
        _context.UserIpHistory.AddRange(
            new UserIpHistory
            {
                UserId = user.Id,
                Ip = "203.0.113.10",
                FirstSeen = 10,
                LastSeen = 10,
                SeenCount = 1
            },
            new UserIpHistory
            {
                UserId = user.Id,
                Ip = "203.0.113.20",
                FirstSeen = 20,
                LastSeen = 30,
                SeenCount = 3
            });
        await _context.SaveChangesAsync();

        var stats = await _service.GetUserStatisticsAsync(user.Id);

        Assert.NotNull(stats);
        Assert.Equal(user.Id, stats!.Id);
        Assert.Equal("player", stats.Username);
        Assert.Equal(100, stats.RegDate);
        Assert.Equal(200, stats.LastLogin);
        Assert.Equal(3600, stats.PlayTime);
        Assert.Equal(2, stats.Murders);
        Assert.Equal(3, stats.Deaths);
        Assert.True(stats.IsBanned);
        Assert.Equal("cheating", stats.BanReason);
        Assert.Equal("root", stats.BannedBy);
        Assert.Equal(2, stats.IpHistory.Count);
        Assert.Equal("203.0.113.20", stats.IpHistory[0].Ip);
        Assert.Equal(3, stats.IpHistory[0].SeenCount);
    }

    [Fact]
    public async Task GetStatistics_ReturnsNullForMissingUser()
    {
        Assert.Null(await _service.GetUserStatisticsAsync(99999));
    }

    private async Task<UserAccount> SeedUserAsync()
    {
        var user = new UserAccount
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Username = "player",
            RealName = "Player",
            HashPassword = "not-used-in-these-tests",
            RegDate = 100,
            LastLogin = 200,
            Role = "user",
            Stats = new UserData
            {
                PlayTime = 3600,
                Murders = 2,
                Deaths = 3
            }
        };

        _context.McAuthAccounts.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return user;
    }

    private sealed class FakeAccessService : IUserAccessService
    {
        public Task<UserAccessSnapshot> GetSnapshotAsync(long userId, CancellationToken ct = default) =>
            Task.FromResult(UserAccessSnapshot.Missing);

        public void Invalidate(long userId)
        {
        }
    }

    private sealed class FakeJwtService : IJwtService
    {
        public string GenerateToken(UserAccount user) => "token";

        public ClaimsPrincipal ValidateToken(string token) => new(new ClaimsIdentity());
    }
}
