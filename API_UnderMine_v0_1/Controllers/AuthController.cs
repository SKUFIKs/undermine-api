using API_UnderMine_v0_1.Data;
using API_UnderMine_v0_1.DTOs;
using API_UnderMine_v0_1.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext context, IConfiguration config)
    {
        _context = context;
        _config = config;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] AuthRequestDto request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Login and password are required" });

            var lowerName = request.Login.Trim().ToLower();
            var realName = request.Login.Trim();
            string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            if (await _context.McAuthAccounts.AnyAsync(u => u.Username == lowerName))
                return BadRequest(new { message = "User already exists" });

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(request.Password);

            var user = new McAuthAccount
            {
                Username = lowerName,
                RealName = realName,
                PasswordHash = hashedPassword,
                regip = ip
            };

            _context.McAuthAccounts.Add(user);
            await _context.SaveChangesAsync();

            return Ok(new AuthResponseDto
            {
                Success = true,
                Message = "User registered successfully",
                UserId = user.Id,
                Login = user.RealName,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal error", error = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthRequestDto request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Login and password are required" });

            var lowerName = request.Login.Trim().ToLower();
            var dbUser = await _context.McAuthAccounts.FirstOrDefaultAsync(u => u.Username == lowerName);

            if (dbUser == null || !BCrypt.Net.BCrypt.Verify(request.Password, dbUser.PasswordHash))
                return Unauthorized(new { message = "Invalid credentials" });
            

            var token = GenerateJwtToken(dbUser);

            dbUser.ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            await _context.SaveChangesAsync();

            return Ok(new LoginResponseDto
            {
                Success = true,
                Message = "Login successful",
                Token = token,
                ExpiresIn = 7200,
                UserId = dbUser.Id,
                Login = dbUser.RealName
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal error", error = ex.Message });
        }
    }

    [HttpPost("validate-token")]
    public IActionResult ValidateToken([FromHeader] string authorization)
    {
        try
        {
            if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer "))
                return Unauthorized(new { message = "Bearer token required" });

            var token = authorization.Substring("Bearer ".Length).Trim();
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_config["Jwt:Key"]!);

            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _config["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _config["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out _);

            return Ok(new ValidationResponseDto
            {
                Valid = true,
                Message = "Token is valid"
            });
        }
        catch
        {
            return Unauthorized(new { message = "Invalid or expired token" });
        }
    }

    private string GenerateJwtToken(McAuthAccount user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.RealName),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
