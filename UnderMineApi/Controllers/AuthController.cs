using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Models;
using UnderMineApi.Services;
using UnderMineApi.Services.Interfaces;
using UnderMineApi.Services.PasswordHasher;
using UnderMineApi.Services.Registration;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Controllers;


[ApiController]
[Route("api/v1.0")]
[EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<AuthController> _logger;
    private readonly IJwtService _jwtService;
    private readonly IUserAccessService _access;
    private readonly IOptions<RegistrationOptions> _registrationOptions;
    private readonly IPasswordHasher _passwordHasher;

    // Real BCrypt hash used to keep the "unknown user" path as slow as a real login attempt,
    // so user enumeration by response time is not practical.
    private static readonly string DummyPasswordHash =
        BCrypt.Net.BCrypt.HashPassword("UnderMine-Timing-Equalizer-2026");

    public AuthController(
        AppDbContext context,
        ILogger<AuthController> logger,
        IJwtService jwtService,
        IUserAccessService access,
        IOptions<RegistrationOptions> registrationOptions,
        IPasswordHasher passwordHasher)
    {
        _context = context;
        _logger = logger;
        _jwtService = jwtService;
        _access = access;
        _registrationOptions = registrationOptions;
        _passwordHasher = passwordHasher;
    }

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitingExtensions.RegisterPolicy)]
    public async Task<IActionResult> Register([FromBody] RegRequestDto request, CancellationToken ct)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                var options = _registrationOptions.Value;

                _logger.LogInformation("Registration attempt for user {Username} from IP {IP}",
                    LogSanitizer.Clean(request.Login),
                    LogSanitizer.Clean(RegistrationIp.ToLogString(HttpContext.Connection.RemoteIpAddress)));

                if (!ModelState.IsValid)
                {
                    _logger.LogWarning("Registration failed: invalid model state for user {Username}",
                        LogSanitizer.Clean(request.Login));
                    return BadRequest(new { status = 400, message = "Invalid request data" });
                }

                if (!PasswordPolicy.TryValidate(request.Password, out var passwordError))
                {
                    _logger.LogWarning("Registration failed: weak password for user {Username}",
                        LogSanitizer.Clean(request.Login));
                    return BadRequest(new
                    {
                        status = 400,
                        error = "WEAK_PASSWORD",
                        message = passwordError
                    });
                }

                if (!RegistrationPolicy.TryNormalizeDeviceId(
                        request.DeviceId,
                        options.DeviceIdMinLength,
                        options.DeviceIdMaxLength,
                        out var deviceId,
                        out var deviceError))
                {
                    _logger.LogWarning("Registration failed: invalid device ID for user {Username}: {Reason}",
                        LogSanitizer.Clean(request.Login), LogSanitizer.Clean(deviceError));
                    return BadRequest(new
                    {
                        status = 400,
                        error = "INVALID_DEVICE_ID",
                        message = deviceError
                    });
                }

                var lowerName = request.Login.Trim().ToLowerInvariant();
                var realName = request.Login.Trim();

                var address = RegistrationIp.Normalize(HttpContext.Connection.RemoteIpAddress);
                var ip = address?.ToString() ?? string.Empty;
                var ipPrefix = RegistrationIp.ToPrefixKey(address, options.Ipv4PrefixLength, options.Ipv6PrefixLength);

                if (await _context.McAuthAccounts.AnyAsync(u => u.Username == lowerName, ct))
                {
                    _logger.LogWarning("Registration failed: username {Username} already exists", LogSanitizer.Clean(lowerName));
                    return Conflict(new
                    {
                        status = 409,
                        error = "USERNAME_TAKEN",
                        message = "User with this username already exists!"
                    });
                }

                // Serialize registrations that share a device or a subnet. Without this two
                // concurrent requests could both pass the count check and exceed the limit.
                await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                await _context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))",
                    new object[] { "registration:device:" + deviceId },
                    ct);
                await _context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))",
                    new object[] { "registration:ip:" + ipPrefix },
                    ct);

                var deviceAccounts = await _context.McAuthAccounts
                    .AsNoTracking()
                    .CountAsync(u => u.DeviceId == deviceId, ct);
                var ipAccounts = await _context.McAuthAccounts
                    .AsNoTracking()
                    .CountAsync(u => u.RegIp == ip || u.RegIpPrefix == ipPrefix, ct);

                if (deviceAccounts >= options.MaxAccountsPerDevice)
                {
                    _logger.LogWarning(
                        "Registration failed: device limit reached for {DeviceId} ({Count}/{Limit})",
                        LogSanitizer.Clean(RegistrationPolicy.MaskDeviceId(deviceId)),
                        LogSanitizer.Clean(deviceAccounts),
                        LogSanitizer.Clean(options.MaxAccountsPerDevice));

                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        status = 429,
                        error = "DEVICE_LIMIT",
                        message = $"Account limit exceeded for this device ({options.MaxAccountsPerDevice})."
                    });
                }

                if (ipAccounts >= options.MaxAccountsPerIp)
                {
                    _logger.LogWarning(
                        "Registration failed: network limit reached for {IpPrefix} ({Count}/{Limit})",
                        LogSanitizer.Clean(ipPrefix),
                        LogSanitizer.Clean(ipAccounts),
                        LogSanitizer.Clean(options.MaxAccountsPerIp));

                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        status = 429,
                        error = "IP_LIMIT",
                        message = "Account limit exceeded for this network. Try again later or contact support."
                    });
                }

                var user = new UnderMineApi.Domain.Entities.UserAccount
                {
                    Username = lowerName,
                    RealName = realName,
                    HashPassword = _passwordHasher.HashPassword(request.Password),
                    Uuid = GetUuid.GetOfflineUuid(realName).ToString(),
                    RegIp = ip,
                    RegIpPrefix = ipPrefix,
                    DeviceId = deviceId,
                    RegDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Stats = new UnderMineApi.Models.UserData
                    {
                        PlayTime = 0,
                        Murders = 0,
                        Deaths = 0
                    }
                };

                _context.McAuthAccounts.Add(user);

                try
                {
                    await _context.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // The unique index is the final guard against two simultaneous registrations
                    // with the same login (the request-level check above cannot be atomic).
                    _logger.LogWarning("Registration failed: username {Username} already exists (unique index)", LogSanitizer.Clean(lowerName));
                    return Conflict(new
                    {
                        status = 409,
                        error = "USERNAME_TAKEN",
                        message = "User with this username already exists!"
                    });
                }

                _logger.LogInformation(
                    "User {Username} (ID: {UserId}) registered successfully from {IpPrefix}",
                    LogSanitizer.Clean(lowerName), LogSanitizer.Clean(user.Id), LogSanitizer.Clean(ipPrefix));

                return Ok(new AuthResponseDto
                {
                    Success = true,
                    Message = "Registration successful!",
                    UserId = user.Id,
                    Login = user.Username
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration failed for user {Username}: {ErrorMessage}",
                    LogSanitizer.Clean(request.Login), LogSanitizer.Clean(ex.Message));
                return StatusCode(500, new { message = "Server unavailable" });
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LogRequestDto request)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                _logger.LogInformation("Login attempt for user {Username} from IP {IP}. ModelState valid: {IsValid}",
                    LogSanitizer.Clean(request.Login),
                    LogSanitizer.Clean(HttpContext.Connection.RemoteIpAddress),
                    LogSanitizer.Clean(ModelState.IsValid));

                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                    _logger.LogWarning("Login failed: invalid model state. Errors: {Errors}",
                        LogSanitizer.Clean(string.Join(", ", errors)));
                    return BadRequest(new { status = 400, message = "Invalid request data" });
                }

                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

                var lowerName = request.Login.Trim().ToLowerInvariant();

                var dbUser = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Username == lowerName);

                if (dbUser == null)
                {
                    // Do a real BCrypt verification anyway so "unknown user" and "wrong password"
                    // take similar time (anti user-enumeration).
                    _passwordHasher.VerifyPassword(request.Password, DummyPasswordHash);

                    _logger.LogWarning("Login failed: user {Username} not found", LogSanitizer.Clean(lowerName));
                    return Unauthorized(new
                    {
                        status = 401,
                        message = "Invalid username or password!"
                    });
                }

                if (!_passwordHasher.VerifyPassword(request.Password, dbUser.HashPassword))
                {
                    _logger.LogWarning("Login failed: invalid password for user {Username} (ID: {UserId})",
                        LogSanitizer.Clean(lowerName), LogSanitizer.Clean(dbUser.Id));
                    return Unauthorized(new
                    {
                        status = 401,
                        message = "Invalid username or password!"
                    });
                }

                var activeBan = await _context.UserBans
                 .Where(b => b.UserId == dbUser.Id && b.IsActive)
                 .OrderByDescending(b => b.CreatedAt)
                 .FirstOrDefaultAsync();

                if (activeBan != null)
                {
                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (activeBan.ExpiresAt == null || activeBan.ExpiresAt > currentTime)
                    {
                        string expirationInfo = activeBan.ExpiresAt == null
                            ? "permanent"
                            : $"until {DateTimeOffset.FromUnixTimeSeconds(activeBan.ExpiresAt.Value).DateTime}";

                        _logger.LogWarning("Login blocked: user {Username} (ID: {UserId}) is banned. Reason: {Reason}, Expires: {Expiration}",
                            LogSanitizer.Clean(lowerName),
                            LogSanitizer.Clean(dbUser.Id),
                            LogSanitizer.Clean(activeBan.Reason),
                            LogSanitizer.Clean(expirationInfo));

                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            status = 403,
                            error = "ACCOUNT_BANNED",
                            message = $"You are banned ({expirationInfo}). Reason: {activeBan.Reason}",
                            reason = activeBan.Reason,
                            expires_at = activeBan.ExpiresAt
                        });
                    }
                }

                var token = _jwtService.GenerateToken(dbUser);

                dbUser.Ip = ip;
                dbUser.LastLogin = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                await _context.SaveChangesAsync();

                _logger.LogInformation("User {Username} (ID: {UserId}) logged in successfully from IP {IP}",
                    LogSanitizer.Clean(lowerName), LogSanitizer.Clean(dbUser.Id), LogSanitizer.Clean(ip));

                return Ok(new LoginResponseDto
                {
                    Success = true,
                    Token = token,
                    UserId = dbUser.Id,
                    Login = dbUser.Username,
                    ExpiresIn = (int)TimeSpan.FromDays(30).TotalSeconds // matches JwtService token lifetime
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed: {ErrorMessage}", LogSanitizer.Clean(ex.Message));
                return StatusCode(500, new
                {
                    status = 500,
                    message = "Server unavailable"
                });
            }
        }
    }

    [HttpPost("validate-token")]
    public async Task<IActionResult> ValidateToken([FromHeader(Name = "Authorization")] string authorization)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                var token = ExtractBearerToken(authorization);

                var principal = _jwtService.ValidateToken(token);

                // A valid signature is not enough: the account may have been banned, demoted or
                // deleted after the token was issued.
                var userIdValue = principal.FindFirst("UserId")?.Value
                                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (!long.TryParse(userIdValue, out var userId))
                {
                    _logger.LogWarning("Token validation failed: token has no usable user id claim");
                    return Unauthorized(new { valid = false, message = "Token does not contain a user id" });
                }

                var access = await _access.GetSnapshotAsync(userId, HttpContext.RequestAborted);

                if (!access.Exists)
                {
                    _logger.LogWarning("Token validation failed: user {UserId} no longer exists", LogSanitizer.Clean(userId));
                    return Unauthorized(new
                    {
                        valid = false,
                        error = "USER_NOT_FOUND",
                        message = "The account for this token no longer exists. Please sign in again."
                    });
                }

                if (access.Ban.IsBanned)
                {
                    var until = access.Ban.ExpiresAt is null
                        ? "permanently"
                        : $"until {DateTimeOffset.FromUnixTimeSeconds(access.Ban.ExpiresAt.Value).UtcDateTime:u} UTC";

                    _logger.LogWarning("Token validation blocked: user {UserId} is banned", LogSanitizer.Clean(userId));

                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        valid = false,
                        error = "ACCOUNT_BANNED",
                        message = string.IsNullOrWhiteSpace(access.Ban.Reason)
                            ? $"This account is banned {until}."
                            : $"This account is banned {until}. Reason: {access.Ban.Reason}",
                        reason = access.Ban.Reason,
                        expires_at = access.Ban.ExpiresAt
                    });
                }

                var tokenRole = principal.FindFirst(ClaimTypes.Role)?.Value;
                if (!string.Equals(tokenRole, access.Role, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Token validation failed: role for user {UserId} changed from {TokenRole} to {CurrentRole}",
                        LogSanitizer.Clean(userId), LogSanitizer.Clean(tokenRole), LogSanitizer.Clean(access.Role));

                    return Unauthorized(new
                    {
                        valid = false,
                        error = "ROLE_CHANGED",
                        message = "Your permissions changed. Please sign in again."
                    });
                }

                var tokenVersionValue = principal.FindFirst("token_version")?.Value;
                var tokenVersion = int.TryParse(tokenVersionValue, out var parsedVersion) ? parsedVersion : 0;
                if (tokenVersion != access.TokenVersion)
                {
                    _logger.LogWarning(
                        "Token validation failed: token version {TokenVersion} for user {UserId} is stale (current {CurrentVersion})",
                        LogSanitizer.Clean(tokenVersion), LogSanitizer.Clean(userId), LogSanitizer.Clean(access.TokenVersion));

                    return Unauthorized(new
                    {
                        valid = false,
                        error = "TOKEN_REVOKED",
                        message = "This session is no longer valid. Please sign in again."
                    });
                }

                _logger.LogInformation("Token validated successfully for user {UserId}", LogSanitizer.Clean(userId));

                return Ok(new ValidationResponseDto
                {
                    Valid = true,
                    Message = "Token is valid"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token validation failed: {ErrorMessage}", LogSanitizer.Clean(ex.Message));
                return Unauthorized(new { valid = false, message = "Invalid or expired token" });
            }
        }
    }

    private static string ExtractBearerToken(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
            return string.Empty;

        var value = authorization.Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : value;
    }
}
