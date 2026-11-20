using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UnderMineApi.Domain.Entities;
using UnderMineApi.Models;
using UnderMineApi.Services;
using UnderMineApi.Services.PasswordHasher;
using UnderMineApi.Services.Security;
using Xunit;

namespace UnderMineApi.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("secret123")]
    [InlineData("12345678")]
    [InlineData("пароль123")]
    public void AcceptsValidPasswords(string password)
    {
        Assert.True(PasswordPolicy.TryValidate(password, out var error), error);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("        ")]
    public void RejectsShortOrEmptyPasswords(string password)
    {
        Assert.False(PasswordPolicy.TryValidate(password, out _));
    }

    [Fact]
    public void RejectsPasswordLongerThan72Utf8Bytes()
    {
        var password = new string('a', PasswordPolicy.MaxBytes + 1);
        Assert.False(PasswordPolicy.TryValidate(password, out _));
    }
}

public class BcryptPasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void HashAndVerify_RoundTrips()
    {
        var hash = _hasher.HashPassword("secret123");

        Assert.NotEqual("secret123", hash);
        Assert.True(_hasher.VerifyPassword("secret123", hash));
        Assert.False(_hasher.VerifyPassword("wrong-password", hash));
    }

    [Fact]
    public void Verify_ReturnsFalseForMalformedHash()
    {
        Assert.False(_hasher.VerifyPassword("secret123", "not-a-bcrypt-hash"));
    }
}

public class JwtServiceTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "01234567890123456789012345678901",
            ["Jwt:Issuer"] = "under-test",
            ["Jwt:Audience"] = "under-test"
        }).Build();

    [Fact]
    public void GeneratedToken_ContainsIdentityRoleAndTokenVersion()
    {
        var service = new JwtService(Configuration(), NullLogger<JwtService>.Instance);
        var user = new UserAccount
        {
            Id = 42,
            Username = "player",
            RealName = "Player",
            Uuid = Guid.NewGuid().ToString(),
            HashPassword = "unused",
            Role = "admin",
            TokenVersion = 7,
            Stats = new UserData()
        };

        var token = service.GenerateToken(user);
        var principal = service.ValidateToken(token);

        Assert.Equal("42", principal.FindFirst("UserId")?.Value);
        Assert.Equal("player", principal.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal("admin", principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal("7", principal.FindFirst("token_version")?.Value);
    }
}
