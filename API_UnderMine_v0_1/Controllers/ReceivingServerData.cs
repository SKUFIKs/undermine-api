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

            string BASE_URL_DROPBOX = "https://www.dropbox.com/scl/fi/";
            string BASE_URL_YANDEX = "https://s1224sas.storage.yandex.net/rdisk/";
            string SERVER_1 = "2rjaahqptphugfvmoaovu/manifest.json?rlkey=zsx932cs6ja47dxd6ajw8gmos&st=m55hmjnk&dl=1";
            string SERVER_2 = "-";
            string VersionMC1 = "1.20.1";
            string VersionMC2 = "-";
            string ForgeV1 = "47.4.10";
            string ForgeV2 = "-";
            string QuestLink = "65r7jq4bjcfobfzs7r4rg/ftbquests.zip?rlkey=8f7fmq9zxabqxe4lr39rxkgk8&st=jkzm7w3c&dl=1";
            string GuiLink = "wyg8n1qijq8v83yhtkml6/fancymenu.zip?rlkey=dki3xakpepw6j4htrb87m4ro9&st=gl9n91q6&dl=1";
            string ShadersLink = "19fwnrlnmr8vfc4lkprnm/shaderpacks.zip?rlkey=c6igrdodw11x0vjct2zo8ckjp&st=76h9pas6&dl=1";

            string ForgeInstallerLink = "7v46159xi9s747q28lfdf/forge-1.20.1-47.4.10-installer.jar?rlkey=n8n9llybm7ry8ihmx5bsfuomf&st=imkq92br&dl=1";

            var result = new
            {
                username,
                versionMC1 = VersionMC1,
                forgeV1 = ForgeV1,
                manifestLink1 = BASE_URL_DROPBOX + SERVER_1,
                InstallerLink = BASE_URL_DROPBOX + ForgeInstallerLink,
                QuestLink = BASE_URL_DROPBOX + QuestLink,
                GuiLink = BASE_URL_DROPBOX + GuiLink,
                ShadersLink = BASE_URL_DROPBOX + ShadersLink,
                versionMC2 = VersionMC2,
                forgeV2 =  ForgeV2,
                manifestLink2 = BASE_URL_DROPBOX + SERVER_2,

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