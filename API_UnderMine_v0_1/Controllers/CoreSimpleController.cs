using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/v0.1")]
[Authorize]
public class CoreSimpleController : ControllerBase
{
    private readonly ILogger<CoreSimpleController> _logger;

    public CoreSimpleController(ILogger<CoreSimpleController> logger)
    {
        _logger = logger;
    }

    [HttpGet("server_info")]
    public IActionResult GetData()
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);

            if (string.IsNullOrEmpty(username))
            {
                return Unauthorized(new
                {
                    Error = "USER_NOT_FOUND",
                    Message = "User information not found in token"
                });
            }

            _logger.LogInformation("User {Username} accessed core data", username);

            string BASE_URL = "https://www.dropbox.com/scl/fi/";
            string KEY_URL_SERVER_1 = "xrxzg95onmleug3aylkkp/manifest.json?rlkey=7k7cs5j3lfhn3c4bu4lwh0woc&st=rw896grm&dl=1";
            string KEY_URL_SERVER_2 = "xrxzg95onmleug3aylkkp/manifest.json?rlkey=7k7cs5j3lfhn3c4bu4lwh0woc&st=rw896grm&dl=1";
            string VersionMC1 = "1.20.1";
            string VersionMC2 = "1.20.1";
            string ForgeV1 = "47.4.6";
            string ForgeV2 = "47.4.6";

            var result = new
            {
                username,
                versionMC1 = VersionMC1,
                forgeV1 = ForgeV1,
                manifestLink1 = BASE_URL + KEY_URL_SERVER_1,

                versionMC2 = VersionMC2,
                forgeV2 = ForgeV2,
                manifestLink2 = BASE_URL + KEY_URL_SERVER_2
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetData");
            return StatusCode(500, new
            {
                Error = "INTERNAL_ERROR",
                Message = "An error occurred while processing your request"
            });
        }
    }
}