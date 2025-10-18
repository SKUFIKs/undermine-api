using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using API_UnderMine_v0_1.Config;
using API_UnderMine_v0_1.Migrations;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/core/v{version:apiVersion}")]
[ApiVersion("0.1")]
[Authorize]
public class CoreExtendedController : ControllerBase
{
    private readonly ILogger<CoreExtendedController> _logger;
    private readonly ServerManager _serverManager;

    public CoreExtendedController(ILogger<CoreExtendedController> logger)
    {
        _logger = logger;
        _serverManager = new ServerManager();
    }

    [HttpGet("server_info")]
    [MapToApiVersion("0.1")]
    [ProducesResponseType(typeof(CoreDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public IActionResult GetData()
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(username))
            {
                _logger.LogWarning("User claim not found for authenticated user");
                return Unauthorized(new ErrorResponse
                {
                    ErrorCode = "USER_CLAIM_MISSING",
                    Message = "User information not found in token"
                });
            }

            _logger.LogInformation("User {Username} (ID: {UserId}) accessed core data", username, userId);

            var response = new CoreDataResponse
            {
                Success = true,
                Message = "Data retrieved successfully",
                User = new CoreUserInfo
                {
                    Username = username,
                    UserId = userId
                },
                Servers = _serverManager.GetAllServers(),
                Timestamp = DateTime.UtcNow,
                ApiVersion = "0.1"
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving core data for user");
            return StatusCode(500, new ErrorResponse
            {
                ErrorCode = "INTERNAL_ERROR",
                Message = "An error occurred while retrieving data",
                Details = new { Error = ex.Message }
            });
        }
    }
}

// DTO классы для CoreExtendedController
public class CoreDataResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public CoreUserInfo User { get; set; } = new CoreUserInfo();
    public ServerInfo[] Servers { get; set; } = Array.Empty<ServerInfo>();
    public DateTime Timestamp { get; set; }
    public string ApiVersion { get; set; } = string.Empty;
}

public class CoreUserInfoResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public CoreUserInfo User { get; set; } = new CoreUserInfo();
    public bool IsAuthenticated { get; set; }
    public string? AuthenticationType { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ServerStatusResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int PlayersOnline { get; set; }
    public int MaxPlayers { get; set; }
    public DateTime LastChecked { get; set; }
    public int ResponseTime { get; set; }
    public ServerInfo? ServerInfo { get; set; } // Добавляем полную информацию о сервере
}
public class CoreUserInfo
{
    public string? Username { get; set; }
    public string? UserId { get; set; }
    public string? Email { get; set; }
}

public class ServerInfo
{
    public string ServerId { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string ForgeVersion { get; set; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
}

public class ErrorResponse
{
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Details { get; set; }
}