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

            var config = _cache.CachedConfig;

            if (config == null)
            {
                return StatusCode(503, "Config is not loaded yet. Please try again in a minute.");
            }


            var result = new
            {
                username,
                versionMC1 = config.Version,
                forgeV1 = config.ForgeVersion,
                manifestLink1 = config.ManifestLink,
                InstallerLink = config.InstallLink,
                ConfLink = config.ConfLink,
                KubejsLink = config.KubejsLink,
                ShadersLink = config.ShadersLink,
                versionMC2 = config.Version,
                forgeV2 = config.ForgeVersion,
                manifestLink2 = config.ManifestLink,

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