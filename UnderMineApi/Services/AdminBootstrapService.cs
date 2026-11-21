using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.Models;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.PasswordHasher;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Services
{
    /// <summary>Outcome of the bootstrap run, used for the startup log message.</summary>
    public sealed class BootstrapAdminResult
    {
        /// <summary>An account was created and the generated credentials should be shown.</summary>
        public bool Created { get; init; }

        /// <summary>Nothing was created (disabled by config, or an admin already exists).</summary>
        public bool Skipped { get; init; }

        /// <summary>Human readable explanation for the skipped/created outcome.</summary>
        public string? Reason { get; init; }

        /// <summary>Login of the freshly created account. Plaintext password lives in <see cref="Password"/>.</summary>
        public string? Username { get; init; }

        /// <summary>Plaintext password. Only ever populated for a freshly generated account.</summary>
        public string? Password { get; init; }
    }

    public interface IAdminBootstrapService
    {
        Task<BootstrapAdminResult> EnsureAdminAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Guarantees the admin panel is reachable on a fresh installation: when no admin account
    /// exists yet, one is created with a randomly generated login and password. The credentials
    /// are written to a root-only file (BootstrapAdmin:CredentialsFile) instead of the log. That
    /// removes the "psql by hand before the first login" step without ever shipping a hard-coded
    /// default password.
    /// 
    /// Configuration (all optional):
    ///   BootstrapAdmin__Enabled   - set to false to turn the whole thing off (default true).
    ///   BootstrapAdmin__Username  - fixed login instead of a generated one.
    ///   BootstrapAdmin__Password  - fixed password; supplying both upserts the account, so it
    ///                               doubles as a recovery path when the generated password is
    ///                               lost. Never printed to the log.
    ///
    /// An account that already exists is never silently re-passworded in random mode; the service
    /// only steps in when there is no admin at all.
    /// </summary>
    public class AdminBootstrapService : IAdminBootstrapService
    {
        // Ambiguous glyphs are left out so a generated password can be copied by hand from the
        // credentials file without introducing O/0, l/1 or I mistakes. '$' is excluded on purpose:
        // it is the interpolation character of docker compose / .env and would be mangled there.
        private const string LowerChars = "abcdefghijkmnopqrstuvwxyz";
        private const string UpperChars = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const string DigitChars = "23456789";
        private const string SymbolChars = "!#%*+-=?@^_";

        private const int PasswordLength = 24;
        private const int UsernameSuffixLength = 8;
        private const int MaxAttempts = 8;

        private readonly AppDbContext _context;
        private readonly IConfiguration _config;
        private readonly IPasswordHasher _hasher;
        private readonly ILogger<AdminBootstrapService> _logger;
        private readonly IUserAccessService _access;

        public AdminBootstrapService(
            AppDbContext context,
            IConfiguration config,
            IPasswordHasher hasher,
            ILogger<AdminBootstrapService> logger,
            IUserAccessService access)
        {
            _context = context;
            _config = config;
            _hasher = hasher;
            _logger = logger;
            _access = access;
        }

        public async Task<BootstrapAdminResult> EnsureAdminAsync(CancellationToken ct = default)
        {
            if (!_config.GetValue("BootstrapAdmin:Enabled", true))
            {
                _logger.LogInformation("Admin bootstrap disabled by BootstrapAdmin:Enabled, no account created");
                return new BootstrapAdminResult { Skipped = true, Reason = "disabled by BootstrapAdmin:Enabled" };
            }

            // Standard ASP.NET configuration uses BootstrapAdmin__Username/Password
            // (section BootstrapAdmin). The flat names are kept for backward compatibility
            // with older .env files and docker-compose mappings.
            var configuredUser = _config["BootstrapAdmin:Username"];
            if (string.IsNullOrWhiteSpace(configuredUser))
                configuredUser = _config["BootstrapAdminUsername"];

            var configuredPassword = _config["BootstrapAdmin:Password"];
            if (string.IsNullOrEmpty(configuredPassword))
                configuredPassword = _config["BootstrapAdminPassword"];

            var hasUser = !string.IsNullOrWhiteSpace(configuredUser);
            var hasPassword = !string.IsNullOrEmpty(configuredPassword);

            if (hasUser != hasPassword)
            {
                _logger.LogWarning(
                    "BootstrapAdmin: only one of Username/Password is set - falling back to a generated login. " +
                    "Set both to use fixed credentials.");
            }
            else if (hasUser)
            {
                return await UpsertFixedAsync(configuredUser!, configuredPassword!, ct);
            }

            return await CreateRandomAdminAsync(ct);
        }

        /// <summary>
        /// Creates the configured account (or resets its password / role). This is the documented
        /// way back in when the generated password was lost, so it intentionally overwrites.
        /// </summary>
        private async Task<BootstrapAdminResult> UpsertFixedAsync(string username, string password, CancellationToken ct)
        {
            if (!PasswordPolicy.TryValidate(password, out var policyError))
            {
                _logger.LogError(
                    "BootstrapAdmin:Password is rejected: {Reason}. Fix it in .env and restart.",
                    policyError);

                return new BootstrapAdminResult { Skipped = true, Reason = policyError };
            }

            var login = username.Trim().ToLowerInvariant();
            var existing = await _context.McAuthAccounts
                .Include(u => u.Stats)
                .FirstOrDefaultAsync(u => u.Username == login, ct);

            if (existing == null)
            {
                _context.McAuthAccounts.Add(BuildAccount(login, login, _hasher.HashPassword(password)));
                await _context.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "Created admin account '{Login}' from BootstrapAdmin:Username/Password", login);

                return new BootstrapAdminResult { Created = true, Username = login, Reason = "created from configuration" };
            }

            existing.HashPassword = _hasher.HashPassword(password);
            existing.Role = "admin";
            existing.TokenVersion++;
            if (existing.Stats == null)
                existing.Stats = NewStats();

            // This path is the documented recovery route. An active ban would otherwise keep the
            // reset account locked out, so recovery also lifts bans on that account.
            var activeBans = await _context.UserBans
                .Where(b => b.UserId == existing.Id && b.IsActive)
                .ToListAsync(ct);
            foreach (var ban in activeBans)
                ban.IsActive = false;

            await _context.SaveChangesAsync(ct);
            _access.Invalidate(existing.Id);

            _logger.LogWarning(
                "Reset admin account '{Login}' from BootstrapAdmin:Username/Password - password and role were overwritten, active bans lifted",
                login);

            return new BootstrapAdminResult { Created = true, Username = login, Reason = "reset from configuration" };
        }

        private async Task<BootstrapAdminResult> CreateRandomAdminAsync(CancellationToken ct)
        {
            var adminExists = await _context.McAuthAccounts.AnyAsync(u => u.Role == "admin", ct);
            if (adminExists)
            {
                _logger.LogInformation("Admin account already exists, skipping bootstrap");
                return new BootstrapAdminResult { Skipped = true, Reason = "an admin account already exists" };
            }

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var login = GenerateUsername();
                var taken = await _context.McAuthAccounts.AnyAsync(u => u.Username == login, ct);
                if (taken) continue; // 2^32 space, practically unreachable - but never fail on it

                var password = GeneratePassword();
                _context.McAuthAccounts.Add(BuildAccount(login, login, _hasher.HashPassword(password)));
                await _context.SaveChangesAsync(ct);

                var credentialsFile = _config["BootstrapAdmin:CredentialsFile"]
                    ?? Path.Combine(AppContext.BaseDirectory, "bootstrap-admin.txt");

                try
                {
                    await File.WriteAllTextAsync(
                        credentialsFile,
                        $"Login: {login}{Environment.NewLine}Password: {password}{Environment.NewLine}",
                        ct);

                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(
                            credentialsFile,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    _logger.LogWarning(
                        "DEFAULT ADMIN CREATED. Login: {Login}. Password written to {CredentialsFile}. Read it and delete the file.",
                        LogSanitizer.Clean(login),
                        LogSanitizer.Clean(credentialsFile));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "DEFAULT ADMIN CREATED for {Login}, but the credentials file could not be written. " +
                        "Set BootstrapAdmin__Username/Password and restart.",
                        LogSanitizer.Clean(login));
                }

                return new BootstrapAdminResult
                {
                    Created = true,
                    Username = login,
                    Password = password,
                    Reason = "generated"
                };
            }

            _logger.LogError("Could not generate a free admin username after {Attempts} attempts", MaxAttempts);
            return new BootstrapAdminResult { Skipped = true, Reason = "no free username generated" };
        }

        private static UserAccount BuildAccount(string login, string realName, string hash) => new()
        {
            Username = login,
            RealName = realName,
            HashPassword = hash,
            // Same derivation the registration flow uses, so the launcher can later attach the
            // same account to a game profile.
            Uuid = GetUuid.GetOfflineUuid(realName).ToString(),
            Role = "admin",
            RegDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Stats = NewStats()
        };

        private static UserData NewStats() => new()
        {
            PlayTime = 0,
            Murders = 0,
            Deaths = 0
        };

        /// <summary>"admin_" + 8 hex chars: obviously an admin account, but not guessable.</summary>
        private static string GenerateUsername()
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(UsernameSuffixLength / 2))
                .ToLowerInvariant();
            return "admin_" + suffix;
        }

        /// <summary>
        /// 24 characters guaranteeing at least one lowercase, uppercase, digit and symbol, then
        /// shuffled so the guaranteed characters are not always in the same positions.
        /// </summary>
        private static string GeneratePassword()
        {
            var alphabet = LowerChars + UpperChars + DigitChars + SymbolChars;

            var chars = new List<char>(PasswordLength)
            {
                Pick(LowerChars),
                Pick(UpperChars),
                Pick(DigitChars),
                Pick(SymbolChars)
            };

            while (chars.Count < PasswordLength)
                chars.Add(Pick(alphabet));

            // Fisher-Yates with a cryptographic source.
            for (var i = chars.Count - 1; i > 0; i--)
            {
                var j = RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }

            return new string(chars.ToArray());
        }

        private static char Pick(string source) => source[RandomNumberGenerator.GetInt32(source.Length)];
    }
}