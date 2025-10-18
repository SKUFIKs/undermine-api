using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/v0.1")]
[Authorize]
public class ReceivingServerData : ControllerBase
{
    private readonly ILogger<ReceivingServerData> _logger;

    public ReceivingServerData(ILogger<ReceivingServerData> logger)
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
            string ConfLink1 = "c5sboql7cjaj7ipxfidr5/config.zip?rlkey=tx2emfq0kr2vuam8wswlmvjv8&st=gq1y5ji2&dl=1";
            string GuiHach1 = "1eb8056abf369dfe9957ca4fa8f95aae40799db5fcc2e6e9dced268cd83f0af0-0000014A";

            var result = new
            {
                username,
                versionMC1 = VersionMC1,
                forgeV1 = ForgeV1,
                manifestLink1 = BASE_URL + KEY_URL_SERVER_1,

                versionMC2 = VersionMC2,
                forgeV2 = ForgeV2,
                manifestLink2 = BASE_URL + KEY_URL_SERVER_2,
                ConfLink1 = BASE_URL + ConfLink1,
                ConfHach1 = GuiHach1

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