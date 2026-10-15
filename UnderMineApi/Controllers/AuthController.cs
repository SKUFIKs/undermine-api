using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers;


[ApiController]
[Route("api/v1.0")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly IJwtService _jwtService;

    public AuthController(AppDbContext context, IConfiguration config, ILogger<AuthController> logger, IJwtService jwtService)
    {
        _context = context;
        _config = config;
        _logger = logger;
        _jwtService = jwtService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegRequestDto request)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                const int MaxAccountsPerDevice = 2;
                const int MaxAccountsPerId = 4;

                _logger.LogInformation("Registration attempt for user {Username} from IP {IP}", request.Login, HttpContext.Connection.RemoteIpAddress);

                if (!ModelState.IsValid)
                {
                    _logger.LogWarning("Registration failed: invalid model state for user {Username}", request.Login);
                    return BadRequest(new { status = 400, message = "Invalid request data" });
                }

                if (string.IsNullOrWhiteSpace(request.DeviceId))
                {
                    _logger.LogWarning("Registration failed: missing device ID for user {Username}", request.Login);
                    return BadRequest(new
                    {
                        status = 400,
                        message = "Invalid request data"
                    });
                }

                var deviceId = request.DeviceId.Trim();
                var lowerName = request.Login.Trim().ToLowerInvariant();
                var realName = request.Login.Trim();
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

                if (await _context.McAuthAccounts.AnyAsync(u => u.Username == lowerName))
                {
                    _logger.LogWarning("Registration failed: username {Username} already exists", lowerName);
                    return Conflict(new
                    {
                        status = 409,
                        message = "User with this username already exists!"
                    });
                }

                // Device limit counts registrations for this device only; the per-IP limit is
                // enforced separately below, otherwise MaxAccountsPerIp is unreachable because
                // the OR with RegIp fires as soon as 2 accounts exist for the same IP.
                int accountCountForDevice = await _context.McAuthAccounts.CountAsync(u => u.DeviceId == deviceId);
                int accountCountForIp = await _context.McAuthAccounts.CountAsync(u => u.RegIp == ip);

                if (accountCountForDevice >= MaxAccountsPerDevice || accountCountForIp >= MaxAccountsPerId)
                {
                    _logger.LogWarning("Registration failed: account limit exceeded for device {DeviceId} or IP {IP}", deviceId, ip);
                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        status = 429,
                        message = "Account limit exceeded for this device!"
                    });
                }

                string hashedPassword = BCrypt.Net.BCrypt.HashPassword(request.Password);

                var uuid = GetUuid.GetOfflineUuid(realName).ToString();

                var user = new UnderMineApi.Domain.Entities.UserAccount
                {
                    Username = lowerName,
                    RealName = realName,
                    HashPassword = hashedPassword,
                    Uuid = uuid,
                    RegIp = ip,
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
                await _context.SaveChangesAsync();

                _logger.LogInformation("User {Username} (ID: {UserId}) registered successfully from IP {IP}", lowerName, user.Id, ip);

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
                _logger.LogError(ex, "Registration failed for user {Username}: {ErrorMessage}", request.Login, ex.Message);
                return StatusCode(500, new { message = "Server unavailable" });
            }
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LogRequestDto request)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                _logger.LogInformation("Login attempt for user {Username} from IP {IP}. ModelState valid: {IsValid}",
                    request.Login, HttpContext.Connection.RemoteIpAddress, ModelState.IsValid);

                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                    _logger.LogWarning("Login failed: invalid model state. Errors: {Errors}", string.Join(", ", errors));
                    return BadRequest(new { status = 400, message = "Invalid request data" });
                }

                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

                var lowerName = request.Login.Trim().ToLowerInvariant();

                var dbUser = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Username == lowerName);

                if (dbUser == null)
                {
                    _logger.LogWarning("Login failed: user {Username} not found", lowerName);
                    return Unauthorized(new
                    {
                        status = 401,
                        message = "Invalid username or password!"
                    });
                }

                if (!BCrypt.Net.BCrypt.Verify(request.Password, dbUser.HashPassword))
                {
                    _logger.LogWarning("Login failed: invalid password for user {Username} (ID: {UserId})", lowerName, dbUser.Id);
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
                            lowerName, dbUser.Id, activeBan.Reason, expirationInfo);

                        return Unauthorized(new
                        {
                            status = 403,
                            message = $"You are banned ({expirationInfo}). Reason: {activeBan.Reason}"
                        });
                    }
                }

                var token = _jwtService.GenerateToken(dbUser);

                dbUser.Ip = ip;
                dbUser.LastLogin = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                await _context.SaveChangesAsync();

                _logger.LogInformation("User {Username} (ID: {UserId}) logged in successfully from IP {IP}", lowerName, dbUser.Id, ip);

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
                _logger.LogError(ex, "Login failed: {ErrorMessage}", ex.Message);
                return StatusCode(500, new
                {
                    status = 500,
                    message = "Server unavailable"
                });
            }
        }
    }

    [HttpPost("validate-token")]
    public IActionResult ValidateToken([FromHeader(Name = "Authorization")] string authorization)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer "))
                {
                    _logger.LogWarning("Token validation failed: missing or invalid Authorization header");
                    return Unauthorized(new { message = "Bearer token required" });
                }

                var token = authorization.Substring("Bearer ".Length).Trim();

                _jwtService.ValidateToken(token);

                _logger.LogInformation("Token validated successfully");

                return Ok(new ValidationResponseDto
                {
                    Valid = true,
                    Message = "Token is valid"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token validation failed: {ErrorMessage}", ex.Message);
                return Unauthorized(new { message = "Invalid or expired token" });
            }
        }
    }
}
