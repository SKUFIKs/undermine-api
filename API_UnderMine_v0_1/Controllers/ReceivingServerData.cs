using API_UnderMine.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/v0.1")]
[Authorize]
public class ReceivingServerData : ControllerBase
{
    private readonly ILogger<ReceivingServerData> _logger;
    private readonly ConfigCacheService _cache;

    public ReceivingServerData(ILogger<ReceivingServerData> logger, ConfigCacheService cache)
    {
        _logger = logger;
        _cache = cache;
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

            string SERVER_1 = "2rjaahqptphugfvmoaovu/manifest.json?rlkey=zsx932cs6ja47dxd6ajw8gmos&st=m55hmjnk&dl=1";
            string SERVER_2 = "-";
            string VersionMC1 = "1.20.1";
            string VersionMC2 = "-";
            string ForgeV1 = "47.4.10";
            string ForgeV2 = "-";
            string ConfLink = "xh871gz6kbvobmx0ec2ri/config.zip?rlkey=uupfrmr31nzf81rgn4sr9yio7&st=qzs9ofms&dl=1";
            string KubejsLink = "vukyaymbdvuflc92x7jdh/kubejs.zip?rlkey=fp6a6uef04wkdztz47r4w4opg&st=nsl6cekb&dl=1";
            string ShadersLink = "19fwnrlnmr8vfc4lkprnm/shaderpacks.zip?rlkey=c6igrdodw11x0vjct2zo8ckjp&st=76h9pas6&dl=1";

            string ForgeInstallerLink = "7v46159xi9s747q28lfdf/forge-1.20.1-47.4.10-installer.jar?rlkey=n8n9llybm7ry8ihmx5bsfuomf&st=imkq92br&dl=1";

            var result = new
            {
                username,
                versionMC1 = VersionMC1,
                forgeV1 = ForgeV1,
                manifestLink1 = BASE_URL_DROPBOX + SERVER_1,
                InstallerLink = BASE_URL_DROPBOX + ForgeInstallerLink,
                ConfLink = BASE_URL_DROPBOX + ConfLink,
                KubejsLink = BASE_URL_DROPBOX + KubejsLink,
                ShadersLink = BASE_URL_DROPBOX + ShadersLink,
                versionMC2 = VersionMC2,
                forgeV2 = ForgeV2,
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
    [HttpGet("TechnoMagic")]
    public IActionResult GetNewData()
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);
            if (string.IsNullOrEmpty(username))
            {
                _logger.LogWarning("Unauthorized access attempt to server_info endpoint.");
                return Unauthorized(new { error = "UNAUTHORIZED", message = "User not authenticated" });
            }

            var config = _cache.CachedConfig;

            if (config == null)
            {
                return StatusCode(503, "Config is not loaded yet. Please try again in a minute.");
            }

            return Ok(new
            {
                username,
                version = config.Version,
                forge = config.ForgeVersion,
                manifestLink = config.ManifestLink,
                installerLink = config.InstallLink,
                confLink = config.ConfLink,
                kubejsLink = config.KubejsLink,
                shadersLink = config.ShadersLink,
                libraryExpectedChecksum = config.LibraryExpectedChecksum,
                versionExpectedChecksum = config.VersionExpectedChecksum,
                assetsExpectedChecksum = config.AssetsExpectedChecksum,
                uiVersion = config.UiVersion,
                clientLink = config.ClientLink
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetData");
            return StatusCode(500, new { Error = "INTERNAL_ERROR" });
        }
    }
}