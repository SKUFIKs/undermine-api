using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Models;
using UnderMineApi.Services;
using UnderMineApi.DTOs;

namespace UnderMineApi.Controllers;

[ApiController]
[Route("api/v1.0")]
[Authorize]
public class ServersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<ServersController> _logger;
    private readonly ConfigCacheService _cache;
    private readonly BuildLinkService _buildLink;


    public ServersController(AppDbContext context, ILogger<ServersController> logger, ConfigCacheService cache, BuildLinkService buildLink)
    {
        _context = context;
        _logger = logger;
        _cache = cache;
        _buildLink = buildLink;
    }

    [HttpGet("servers")]
    public async Task<IActionResult> GetServers()
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                _logger.LogInformation("Fetching servers list");

                var source = _cache.CachedServers;

                if (source == null || !source.Any())
                {
                    _logger.LogInformation("Cache is empty, fetching servers from database");

                    source = await _context.ServerManifests
                        .AsNoTracking()
                        .Include(s => s.Config)
                        .Where(s => s.IsActive)
                        .ToListAsync();

                    _logger.LogInformation("Fetched {ServersCount} servers from database", source.Count);

                    _ = _cache.RefreshCacheAsync();
                }
                else
                {
                    _logger.LogInformation("Returning {ServersCount} servers from cache", source.Count);
                }

                var response = source.Select(m => new ServerList(
                    m.Id.ToString(),
                    m.ServerName,
                    m.AppVersion,
                    m.LoaderType,
                    m.IpAddress,
                    null
                ));

                _logger.LogInformation("Returned servers list. Count: {ServersCount}", response.Count());

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching servers list: {ErrorMessage}", ex.Message);
                return StatusCode(500, new { Error = "INTERNAL_ERROR" });
            }
        }
    }

    [HttpGet("servers/{id}")]
    public async Task<IActionResult> GetServerDetails(long id)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                _logger.LogInformation("Fetching server details for ID {ServerId}", id);

                var server = _cache.CachedServers.FirstOrDefault(m => m.Id == id);

                if (server == null)
                {
                    _logger.LogInformation("Server ID {ServerId} not found in cache, fetching from database", id);

                    var dbServer = await _context.ServerManifests
                        .AsNoTracking()
                        .Include(s => s.Config)
                        .FirstOrDefaultAsync(s => s.Id == id && s.IsActive);

                    if (dbServer == null)
                    {
                        _logger.LogWarning("Server ID {ServerId} not found", id);
                        return NotFound(new { Error = "SERVER_NOT_FOUND" });
                    }

                    server = dbServer;

                    _logger.LogInformation("Server ID {ServerId} found in database", id);

                    _ = _cache.RefreshCacheAsync();
                }
                else
                {
                    _logger.LogInformation("Server ID {ServerId} found in cache", id);
                }

                ServerConfig? configDto = null;
                if (server.Config != null)
                {
                    configDto = new ServerConfig(server.Config.TypeVersion);
                }

                var response = new ServerList(
                    server.Id.ToString(),
                    server.ServerName,
                    server.AppVersion,
                    server.LoaderType,
                    server.IpAddress,
                    configDto
                );

                _logger.LogInformation("Returned server details for ID {ServerId}: {ServerName}", id, server.ServerName);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching server details for ID {ServerId}: {ErrorMessage}", id, ex.Message);
                return StatusCode(500, new { Error = "INTERNAL_ERROR" });
            }
        }
    }

    [HttpGet("servers/{id}/manifest")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBuildManifest(long id)
    {
        var requestId = HttpContext.Items.ContainsKey("RequestId") ? HttpContext.Items["RequestId"]?.ToString() : null;
        using (_logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            try
            {
                _logger.LogInformation("Fetching build manifest for server ID {ServerId}", id);

                var server = await _context.ServerManifests
                    .AsNoTracking()
                    .Include(s => s.Config)
                    .FirstOrDefaultAsync(s => s.Id == id);

                if (server == null)
                {
                    _logger.LogWarning("Server ID {ServerId} not found", id);
                    return NotFound(new { Error = "SERVER_NOT_FOUND" });
                }

                // null  -> auto: resolve the newest completed build with matching versions
                // ""    -> none: no manifest
                // value -> pinned: that exact build
                string? manifestJson;
                if (server.BuildId == null)
                {
                    var match = await _buildLink.FindMatchingAsync(
                        server.AppVersion, server.LoaderType, server.Config?.TypeVersion);
                    manifestJson = match?.ManifestJson ?? server.BuildManifest;
                }
                else if (server.BuildId.Length > 0)
                {
                    var build = await _buildLink.FindByIdAsync(server.BuildId);
                    manifestJson = build?.ManifestJson;
                }
                else
                {
                    manifestJson = null;
                }

                if (string.IsNullOrEmpty(manifestJson))
                {
                    _logger.LogWarning("Server ID {ServerId} has no build manifest (link: {BuildLink})", id, server.BuildId ?? "auto");
                    return NotFound(new { Error = "MANIFEST_NOT_FOUND" });
                }

                var manifest = System.Text.Json.JsonSerializer.Deserialize<BuildManifestDto>(manifestJson);

                if (manifest == null)
                {
                    _logger.LogError("Failed to deserialize build manifest for server ID {ServerId}", id);
                    return StatusCode(500, new { Error = "MANIFEST_DESERIALIZE_ERROR" });
                }

                _logger.LogInformation("Returned build manifest for server ID {ServerId}: {ServerName}", id, server.ServerName);

                return Ok(manifest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching build manifest for server ID {ServerId}: {ErrorMessage}", id, ex.Message);
                return StatusCode(500, new { Error = "INTERNAL_ERROR" });
            }
        }
    }
}
