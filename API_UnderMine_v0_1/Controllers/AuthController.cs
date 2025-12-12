using API_UnderMine_v0_1.Data;
using API_UnderMine_v0_1.DTOs;
using API_UnderMine_v0_1.Models;
using API_UnderMine_v0_1.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/auth")]
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
    public async Task<IActionResult> Register([FromBody] AuthRequestDto request)
    {
        try
        {
            const int MaxAccountsPerDevice = 2;
            const int MaxAccountsPerId = 4;

            if (!ModelState.IsValid)
                return BadRequest(new { status = 400, message = "Ошибка запроса" });

            if (string.IsNullOrWhiteSpace(request.DeviceId))
                return BadRequest(new
                {
                    status = 400,
                    message = "Ошибка получения ID устройства!"
                });

            var deviceId = request.DeviceId.Trim();
            var lowerName = request.Login.Trim().ToLowerInvariant();
            var realName = request.Login.Trim();
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

            if (await _context.McAuthAccounts.AnyAsync(u => u.Username == lowerName))
                return Conflict(new
                {
                    status = 409,
                    message = "Пользователь уже существует!"
                });

            int accountCountForDevice = await _context.McAuthAccounts.CountAsync(u => u.DeviceId == deviceId || u.RegIp == ip);
            int accountCountForIp = await _context.McAuthAccounts.CountAsync(u => u.RegIp == ip);

            if (accountCountForDevice >= MaxAccountsPerDevice || accountCountForIp >= MaxAccountsPerId)
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    status = 429,
                    message = "Превышен лимит аккаунтов на устройство!"
                });

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(request.Password);

            var user = new McAuthAccount
            {
                Username = lowerName,
                RealName = realName,
                Password = hashedPassword,
                RegIp = ip,       
                DeviceId = deviceId, 
                RegDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 

                IsBanned = false
            };

            _context.McAuthAccounts.Add(user);
            await _context.SaveChangesAsync();

            return Ok(new AuthResponseDto
            {
                Success = true,
                Message = "Успешная регистрация пользователя!",
                UserId = user.Id,
                Login = user.RealName,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка регистрации!");
            return StatusCode(500, new { message = "Сервер не доступен" });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthRequestDto request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(new { status = 400, message = "Ошибка запросa" });

            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

            var lowerName = request.Login.Trim().ToLowerInvariant();

            var dbUser = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Username == lowerName);

            if (dbUser == null)
                return Unauthorized(new
                {
                    status = 401,
                    message = "Пользователь не найден"
                });

            if (!BCrypt.Net.BCrypt.Verify(request.Password, dbUser.Password))
                return Unauthorized(new
                {
                    status = 401,
                    message = "Неверный логин или пароль!"
                });

            if (dbUser.IsBanned == true)
            {
                return Unauthorized(new
                {
                    status = 410,
                    message = "Твой аккаунт забанен!"
                });
            }

            var token = _jwtService.GenerateToken(dbUser);

            dbUser.Ip = ip;
            dbUser.LastLogin = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            dbUser.HasSession = 1;

            await _context.SaveChangesAsync();

            return Ok(new LoginResponseDto
            {
                Success = true,
                Message = "Успешная авторизация",
                Token = token,
                ExpiresIn = 300,
                UserId = dbUser.Id,
                Login = dbUser.RealName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка авторизации!");
            return StatusCode(500, new
            {
                status = 500,
                message = "Сервер не доступен"
            });
        }
    }


    [HttpPost("validate-token")]
    public IActionResult ValidateToken([FromHeader(Name = "Authorization")] string authorization)
    {
        try
        {
            if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer "))
                return Unauthorized(new { message = "Bearer token required" });

            var token = authorization.Substring("Bearer ".Length).Trim();

            _jwtService.ValidateToken(token);

            return Ok(new ValidationResponseDto
            {
                Valid = true,
                Message = "Token is valid"
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Invalid token validation attempt");
            return Unauthorized(new { message = "Invalid or expired token" });
        }
    }
}