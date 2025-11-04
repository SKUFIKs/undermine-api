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
            string QuestLink = "79bdf87bbafaf27a1ea541a945e938f22aa84040c4c8548f4438e850e371bbae/6908f535/2mbaMZk2p7t0945E3s1R5KAG_gXkCR3ljzHwzzem5QER8S0QjxrtVj4sIR6kfEOPJU8Vxfo4xd_t9hmesjclnw==?uid=0&filename=ftbquests.zip&disposition=attachment&hash=jfAfAFOmoLrDmoXbgBJ5hzI5TU3hSPAzgDMRvd7Ka6EWZ7wmrlFjcZbt%2B1IXgzs2q/J6bpmRyOJonT3VoXnDag%3D%3D&limit=0&content_type=application%2Fzip&owner_uid=1608853094&fsize=338377&hid=49ecf66f0f4c03600fa8bb7e2a3d9429&media_type=compressed&tknv=v3&ts=642b4ef8ff740&s=1077399f09cde96861f993742881f7baddc55d0f9432eda8043f3d01813cb76d&pb=U2FsdGVkX1_fpOwZZic8v9X55OKT7smE4Yr89vnQRq2FSUFsr2bbm7JfpiiGuwvC8KKOboamWxGgtWAHmy4iQb0lZAD550U2iRkhNmB3_Yk";
            string GuiLink = "045f90a2b578da95607cb2338587da3289725070af9dd46cdddfdf986e1f9c5e/6908f4c1/2mbaMZk2p7t0945E3s1R5DCGk0TbmyLpez1VbmptjOKfmhm42oNAAzi_9smXewttDeC9GcBWv64w5u-FwLMGNA==?uid=0&filename=fancymenu.zip&disposition=attachment&hash=HcTL0eGRo7ppOqFczveYuRzVVSmt2Xgx/8MzWgOJeCb0W6cXYfL%2BqCrQ5Ua8P6Njq/J6bpmRyOJonT3VoXnDag%3D%3D&limit=0&content_type=application%2Fzip&owner_uid=1608853094&fsize=8256695&hid=0b6bfa465099c7842f5a5bb0166aee84&media_type=compressed&tknv=v3&ts=642b4e8a5f240&s=c067ddf307dd5df2747bd507c12fc76941b3428421a41577bc6bf06f5b730b46&pb=U2FsdGVkX1-cz1dy-ITv3VBY9Sq9qTAtF4A_WkMA4SCueWfyG35hQ2rR5mDuTZgdCMjKXzlHZtp9lf8PXnZXgYcMvBiW8FZraoP_sv8sZmw";
            string ShadersLink = "3c70b3d4c0bcb9065f4d87b34e21c28c95726ccdb853b341596c5482a0aaf15b/6908f55d/2mbaMZk2p7t0945E3s1R5K5oc3LlUQ3CQAXE7PSupneK_IrQ0SN5jQR-lioYO5VXJ-BpRO2PjYZ4JcMOPU_Srw==?uid=0&filename=shaderpacks.zip&disposition=attachment&hash=TNK4JZJOlq5Av%2BFcfo0IwSic8aiW7SuFnwBrkvdqSiT%2BDQNs%2Bln/KnF4cz5QbBd8q/J6bpmRyOJonT3VoXnDag%3D%3D&limit=0&content_type=application%2Fzip&owner_uid=1608853094&fsize=4607035&hid=70447b9d75685544252da4f558108e8c&media_type=compressed&tknv=v3&ts=642b4f1f25140&s=a5e0bb76e6478f4b31aeda65bf42730825c62e548e8f04145122ebaa100672ce&pb=U2FsdGVkX19SKrZOtIJ4A3d17LqdIrXqRPb9z7_YMylbc2sKO0zmQ9oW6qwF2yXbvc8t63NiiSnMHVI0_VSllcb9vRS1ztIhaPHOdS_bAjU";

            string ForgeInstallerLink = "7v46159xi9s747q28lfdf/forge-1.20.1-47.4.10-installer.jar?rlkey=n8n9llybm7ry8ihmx5bsfuomf&st=imkq92br&dl=1";

            var result = new
            {
                username,
                versionMC1 = VersionMC1,
                forgeV1 = ForgeV1,
                manifestLink1 = BASE_URL_DROPBOX + SERVER_1,
                InstallerLink = BASE_URL_DROPBOX + ForgeInstallerLink,
                QuestLink = BASE_URL_YANDEX + QuestLink,
                GuiLink = BASE_URL_YANDEX + GuiLink,
                ShadersLink = BASE_URL_YANDEX + ShadersLink,
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