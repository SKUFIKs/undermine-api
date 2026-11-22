using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.PasswordHasher;
using Xunit;

namespace UnderMineApi.Tests;

/// <summary>
/// Covers the ban workflow: mandatory written reason, expiry and evidence files, replacement
/// of previous active bans, and evidence metadata in the user statistics response.
/// </summary>
public class UserBanTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly FakeAccessService _access = new();
    private readonly UserService _service;

    public UserBanTests()
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
    public async Task CreatesBanWithReasonExpiryAndEvidence()
    {
        var user = await SeedUserAsync();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
        var evidence = new[]
        {
            new UploadedBanEvidence("screenshot.png", "image/png", 4, "hash", new byte[] { 1, 2, 3, 4 })
        };

        var result = await _service.BanUserAsync(
            user.Id,
            new BanRequestDto { Reason = "Cheating in ranked matches", ExpiresAt = expiresAt },
            evidence,
            "moderator");

        Assert.True(result.Success, result.Error);

        var ban = await _context.UserBans
            .Include(b => b.Evidence)
            .SingleAsync(b => b.UserId == user.Id);

        Assert.Equal("Cheating in ranked matches", ban.Reason);
        Assert.Equal(expiresAt, ban.ExpiresAt);
        Assert.True(ban.IsActive);
        Assert.Equal("moderator", ban.BannedBy);

        var file = Assert.Single(ban.Evidence);
        Assert.Equal("screenshot.png", file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, file.Data);

        Assert.Contains(user.Id, _access.Invalidated);
        var audit = await _context.AdminAudits.SingleAsync();
        Assert.Equal("ban_user", audit.Action);
        Assert.Contains("evidence: 1", audit.Details);
    }

    [Fact]
    public async Task RejectsBanWithoutEvidence()
    {
        var user = await SeedUserAsync();

        var result = await _service.BanUserAsync(
            user.Id,
            new BanRequestDto
            {
                Reason = "No evidence attached",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            },
            Array.Empty<UploadedBanEvidence>(),
            "admin");

        Assert.False(result.Success);
        Assert.Equal("EVIDENCE_REQUIRED", result.Error);
        Assert.Empty(await _context.UserBans.ToListAsync());
    }

    [Fact]
    public async Task RejectsShortReasonAndPastExpiry()
    {
        var user = await SeedUserAsync();
        var evidence = new[]
        {
            new UploadedBanEvidence("log.txt", "text/plain", 3, "hash", new byte[] { 1, 2, 3 })
        };

        var shortReason = await _service.BanUserAsync(
            user.Id,
            new BanRequestDto
            {
                Reason = "short",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            },
            evidence,
            "admin");

        Assert.False(shortReason.Success);
        Assert.Equal("INVALID_REASON", shortReason.Error);

        var pastExpiry = await _service.BanUserAsync(
            user.Id,
            new BanRequestDto
            {
                Reason = "This reason is long enough",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()
            },
            evidence,
            "admin");

        Assert.False(pastExpiry.Success);
        Assert.Equal("INVALID_EXPIRY", pastExpiry.Error);
        Assert.Empty(await _context.UserBans.ToListAsync());
    }

    [Fact]
    public async Task ReplacingBanDeactivatesPreviousBan()
    {
        var user = await SeedUserAsync();
        var evidence = new[]
        {
            new UploadedBanEvidence("proof.png", "image/png", 2, "hash", new byte[] { 9, 9 })
        };

        var first = await _service.BanUserAsync(
            user.Id,
            new BanRequestDto
            {
                Reason = "First violation reason",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
            },
            evidence,
            "admin");

        var second = await _service.BanUserAsync(
            user.Id,
            new BanRequestDto
            {
                Reason = "Second violation reason",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()
            },
            evidence,
            "admin");

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);

        var bans = await _context.UserBans.OrderBy(b => b.Id).ToListAsync();
        Assert.Equal(2, bans.Count);
        Assert.False(bans[0].IsActive);
        Assert.True(bans[1].IsActive);
    }

    [Fact]
    public async Task StatisticsIncludeBanHistoryAndEvidenceMetadata()
    {
        var user = await SeedUserAsync();
        var evidence = new[]
        {
            new UploadedBanEvidence("screen.jpg", "image/jpeg", 5, "hash", new byte[] { 5, 4, 3, 2, 1 })
        };

        await _service.BanUserAsync(
            user.Id,
            new BanRequestDto
            {
                Reason = "Harassment in chat",
                ExpiresAt = null
            },
            evidence,
            "admin");

        var statistics = await _service.GetUserStatisticsAsync(user.Id);

        Assert.NotNull(statistics);
        Assert.True(statistics!.IsBanned);
        var ban = Assert.Single(statistics.Bans);
        Assert.Equal("Harassment in chat", ban.Reason);
        Assert.True(ban.IsActive);
        var file = Assert.Single(ban.Evidence);
        Assert.Equal("screen.jpg", file.FileName);
        Assert.True(file.IsImage);
        Assert.Equal(5, file.Size);
    }

    [Fact]
    public async Task UnbanDeactivatesActiveBan()
    {
        var user = await SeedUserAsync();
        var evidence = new[]
        {
            new UploadedBanEvidence("proof.txt", "text/plain", 1, "hash", new byte[] { 7 })
        };

        await _service.BanUserAsync(
            user.Id,
            new BanRequestDto { Reason = "Rule violation here", ExpiresAt = null },
            evidence,
            "admin");

        var result = await _service.UnbanUserAsync(user.Id, "admin");

        Assert.True(result.Success, result.Error);
        var ban = await _context.UserBans.SingleAsync();
        Assert.False(ban.IsActive);
        Assert.Contains(user.Id, _access.Invalidated);
    }

    private async Task<UserAccount> SeedUserAsync()
    {
        var user = new UserAccount
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Username = "target",
            RealName = "target",
            HashPassword = "not-used-in-these-tests",
            RegDate = 1,
            Role = "user",
            Stats = new UserData()
        };

        _context.McAuthAccounts.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return user;
    }

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
