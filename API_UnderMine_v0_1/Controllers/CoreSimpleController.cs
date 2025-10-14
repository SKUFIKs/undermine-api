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
            string KEY_URL_SERVER_1 = "2rjaahqptphugfvmoaovu/manifest.json?rlkey=zsx932cs6ja47dxd6ajw8gmos&st=m55hmjnk&dl=1";
            string KEY_URL_SERVER_2 = "2rjaahqptphugfvmoaovu/manifest.json?rlkey=zsx932cs6ja47dxd6ajw8gmos&st=m55hmjnk&dl=1";
            string VersionMC1 = "1.20.1";
            string VersionMC2 = "1.20.1";
            string ForgeV1 = "47.4.6";
            string ForgeV2 = "47.4.6";
            string GuiLink1 = "6o7ysngndi7j56qz7vnwj/fancymenu.zip?rlkey=rpzqq49ym3melv15bbd7ycaac&st=x8m9dslf&dl=1";
            string GuiHach1 = "992EC0654451ED432FC1FDC3C21B1B51600052DFDACAF40036ABBDB21FEA8C22";

            var result = new
            {
                username,
                versionMC1 = VersionMC1,
                forgeV1 = ForgeV1,
                manifestLink1 = BASE_URL + KEY_URL_SERVER_1,

                versionMC2 = VersionMC2,
                forgeV2 = ForgeV2,
                manifestLink2 = BASE_URL + KEY_URL_SERVER_2,
                GuiLink1 = BASE_URL + GuiLink1,
                GuiHach1 = GuiHach1

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