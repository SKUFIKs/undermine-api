using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using UnderMineApi.Data;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers
{

    [ApiController]
    [Route("api/v1.0")]
    [Authorize]
    public class UserController : Controller
    {
        private readonly AppDbContext _context;
        private readonly ILogger<UserController> _logger;
        private readonly ConfigCacheService _cache;

        public UserController(AppDbContext context, ILogger<UserController> logger, ConfigCacheService cache)
        {
            _context = context;
            _logger = logger;
            _cache = cache;
        }

        private string? GetUsername() => User.FindFirstValue(ClaimTypes.Name);

        [HttpGet("user_data")]
        public async Task<IActionResult> GetUserData()
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                try
                {
                    var username = GetUsername();
                    if (string.IsNullOrEmpty(username))
                    {
                        _logger.LogWarning("GetUserData failed: user not authenticated");
                        return Unauthorized();
                    }

                    _logger.LogInformation("Fetching user data for {Username}", username);

                    var dbUser = await _context.McAuthAccounts
                        .Include(u => u.Stats)
                        .FirstOrDefaultAsync(u => u.Username == username);

                    if (dbUser == null)
                    {
                        _logger.LogWarning("GetUserData failed: user {Username} not found", username);
                        return NotFound(new { Error = "USER_NOT_FOUND" });
                    }

                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var activeBan = await _context.UserBans.AnyAsync(b =>
                        b.UserId == dbUser.Id &&
                        b.IsActive &&
                        (b.ExpiresAt == null || b.ExpiresAt > currentTime)
                    );

                    if (activeBan)
                    {
                        _logger.LogWarning("GetUserData blocked: user {Username} (ID: {UserId}) is banned", username, dbUser.Id);
                        return StatusCode(403, new { Error = "ACCOUNT_BANNED" });
                    }

                    var response = new
                    {
                        dbUser.Username,
                        dbUser.RealName,
                        dbUser.Uuid,
                    };

                    _logger.LogInformation("Returned user data for {Username} (ID: {UserId})", username, dbUser.Id);

                    return Ok(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving user data for {Username}: {ErrorMessage}", GetUsername(), ex.Message);
                    return StatusCode(500, new { Error = "INTERNAL_ERROR" });
                }
            }
        }

        [HttpGet("user_data/statistics")]
        public async Task<IActionResult> GetUserStatistics()
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                try
                {
                    var username = GetUsername();
                    if (string.IsNullOrEmpty(username))
                    {
                        _logger.LogWarning("GetUserStatistics failed: user not authenticated");
                        return Unauthorized();
                    }

                    _logger.LogInformation("Fetching user statistics for {Username}", username);

                    var dbUser = await _context.McAuthAccounts
                        .Include(u => u.Stats)
                        .FirstOrDefaultAsync(u => u.Username == username);

                    if (dbUser == null)
                    {
                        _logger.LogWarning("GetUserStatistics failed: user {Username} not found", username);
                        return NotFound(new { Error = "USER_NOT_FOUND" });
                    }

                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var activeBan = await _context.UserBans.AnyAsync(b =>
                        b.UserId == dbUser.Id &&
                        b.IsActive &&
                        (b.ExpiresAt == null || b.ExpiresAt > currentTime)
                    );

                    if (activeBan)
                    {
                        _logger.LogWarning("GetUserStatistics blocked: user {Username} (ID: {UserId}) is banned", username, dbUser.Id);
                        return StatusCode(403, new { Error = "ACCOUNT_BANNED" });
                    }

                    var response = new
                    {
                        dbUser.RealName,
                        dbUser.Uuid,
                        dbUser.Role,
                        Statistics = new
                        {
                            dbUser.Stats.PlayTime,
                            dbUser.Stats.Murders,
                            dbUser.Stats.Deaths
                        }
                    };

                    _logger.LogInformation("Returned user statistics for {Username} (ID: {UserId})", username, dbUser.Id);

                    return Ok(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving user statistics for {Username}: {ErrorMessage}", GetUsername(), ex.Message);
                    return StatusCode(500, new { Error = "INTERNAL_ERROR" });
                }
            }
        }

        [HttpGet("{id}/public-key")]
        public async Task<IActionResult> GetPublicKey(long id)
        {
            var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
            using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
            {
                _logger.LogInformation("Fetching public key for user ID {UserId}", id);

                var user = await _context.McAuthAccounts
                    .Where(u => u.Id == id)
                    .Select(u => new { u.PublicKey })
                    .FirstOrDefaultAsync();

                if (user == null)
                {
                    _logger.LogWarning("GetPublicKey failed: user ID {UserId} not found", id);
                    return NotFound(new { error = "USER_NOT_FOUND" });
                }

                if (string.IsNullOrEmpty(user.PublicKey))
                {
                    _logger.LogWarning("GetPublicKey failed: user ID {UserId} has no public key set", id);
                    return BadRequest(new { error = "KEY_NOT_SET" });
                }

                _logger.LogInformation("Returned public key for user ID {UserId}", id);

                return Ok(new { publicKey = user.PublicKey });
            }
        }

    }
}
