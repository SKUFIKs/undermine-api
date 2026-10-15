using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using UnderMineApi.DTOs;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers
{
    /// <summary>
    /// Auto-discovery of installable Minecraft / mod-loader versions for the admin panel.
    /// </summary>
    [ApiController]
    [Route("api/v1.0/versions")]
    [Authorize(Roles = "admin")]
    public class VersionsController : ControllerBase
    {
        private readonly VersionCatalogService _catalog;
        private readonly ILogger<VersionsController> _logger;

        public VersionsController(VersionCatalogService catalog, ILogger<VersionsController> logger)
        {
            _catalog = catalog;
            _logger = logger;
        }

        /// <summary>
        /// Minecraft versions available for a loader, newest first.
        /// GET /api/v1.0/versions/minecraft?loader=fabric&amp;includePrerelease=false
        /// </summary>
        [HttpGet("minecraft")]
        public async Task<ActionResult<VersionListResponse>> GetMinecraftVersions(
            [FromQuery] string loader = "vanilla",
            [FromQuery] bool includePrerelease = false,
            CancellationToken ct = default)
        {
            if (!VersionCatalogService.IsKnownLoader(loader))
                return BadRequest(new
                {
                    Error = "UNKNOWN_LOADER",
                    Message = $"Unknown loader '{loader}'. Use one of: {string.Join(", ", VersionCatalogService.KnownLoaders)}."
                });

            var response = await _catalog.GetMinecraftVersionsAsync(loader, includePrerelease, ct);

            _logger.LogInformation("Listed {Count} Minecraft versions for {Loader} (snapshots: {Snapshots})",
                response.Versions.Count, response.Loader, includePrerelease);

            return Ok(response);
        }

        /// <summary>
        /// Loader builds available for one Minecraft version, newest first.
        /// GET /api/v1.0/versions/loader?loader=forge&amp;mcVersion=1.20.1
        /// </summary>
        [HttpGet("loader")]
        public async Task<ActionResult<VersionListResponse>> GetLoaderVersions(
            [FromQuery] string loader,
            [FromQuery] string mcVersion,
            [FromQuery] bool includePrerelease = false,
            CancellationToken ct = default)
        {
            if (!VersionCatalogService.IsKnownLoader(loader))
                return BadRequest(new
                {
                    Error = "UNKNOWN_LOADER",
                    Message = $"Unknown loader '{loader}'. Use one of: {string.Join(", ", VersionCatalogService.KnownLoaders)}."
                });

            if (string.IsNullOrWhiteSpace(mcVersion))
                return BadRequest(new { Error = "MC_VERSION_REQUIRED", Message = "Query parameter 'mcVersion' is required." });

            var response = await _catalog.GetLoaderVersionsAsync(loader, mcVersion.Trim(), includePrerelease, ct);

            _logger.LogInformation("Listed {Count} {Loader} builds for Minecraft {McVersion}",
                response.Versions.Count, response.Loader, response.MinecraftVersion);

            return Ok(response);
        }
    }
}
