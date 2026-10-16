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
using UnderMineApi.Services.PasswordHasher;

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
    /// exists yet, one is created with a randomly generated login and password and the
    /// credentials are printed to the log. That removes the "psql by hand before the first login"
    /// step without ever shipping a hard-coded default password.
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
        // Ambiguous glyphs are left out so a generated password can be copied by hand from a log
        // line without introducing O/0, l/1 or I mistakes. '$' is excluded on purpose: it is the
        // interpolation character of docker compose / .env and would be mangled there.
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

        public AdminBootstrapService(
            AppDbContext context,
            IConfiguration config,
            IPasswordHasher hasher,
            ILogger<AdminBootstrapService> logger)
        {
            _context = context;
            _config = config;
            _hasher = hasher;
            _logger = logger;
        }

        public async Task<BootstrapAdminResult> EnsureAdminAsync(CancellationToken ct = default)
        {
            if (!_config.GetValue("BootstrapAdmin:Enabled", true))
            {
                _logger.LogInformation("Admin bootstrap disabled by BootstrapAdmin:Enabled, no account created");
                return new BootstrapAdminResult { Skipped = true, Reason = "disabled by BootstrapAdmin:Enabled" };
            }

            var configuredUser = _config["BootstrapAdmin:Username"]?.Trim();
            var configuredPassword = _config["BootstrapAdmin:Password"];

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
            if (existing.Stats == null)
                existing.Stats = NewStats();

            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Reset admin account '{Login}' from BootstrapAdmin:Username/Password - password and role were overwritten",
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

                _logger.LogWarning(
                    "\n" +
                    "======================== DEFAULT ADMIN CREATED ========================\n" +
                    " Login:    {Login}\n" +
                    " Password: {Password}\n" +
                    " Sign in to the admin panel with these credentials. They are shown only\n" +
                    " once - store them now. Lost? Set BootstrapAdmin__Username and\n" +
                    " BootstrapAdmin__Password in .env and restart to reset the account.\n" +
                    "=====================================================================",
                    login, password);

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