using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services.Interfaces;

namespace UnderMineApi.Services
{
    public class ServerService : IServerService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<ServerService> _logger;
        private readonly AdminAuditService _audit;
        private readonly ConfigCacheService _cache;
        private readonly BuildLinkService _buildLink;

        public ServerService(AppDbContext context, ILogger<ServerService> logger, AdminAuditService audit, ConfigCacheService cache, BuildLinkService buildLink)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
            _cache = cache;
            _buildLink = buildLink;
        }

        public async Task<(bool Success, ServerManifest? Server, string? Error)> CreateServerAsync(AdminServerUpsertDto dto, string actor, CancellationToken cancellationToken = default)
        {
            // Case-insensitive exact comparison. ILike() treats the server name as a pattern,
            // so names containing '%' or '_' would match unrelated servers and block creation.
            var normalizedName = dto.ServerName.Trim().ToLowerInvariant();
            var exists = await _context.ServerManifests.AsNoTracking()
                .AnyAsync(s => s.ServerName.ToLower() == normalizedName, cancellationToken);
            if (exists) return (false, null, "SERVER_ALREADY_EXISTS");

            var server = new ServerManifest
            {
                ServerName = dto.ServerName.Trim(),
                AppVersion = dto.AppVersion?.Trim() ?? string.Empty,
                LoaderType = (dto.LoaderType ?? string.Empty).Trim().ToLowerInvariant(),
                IpAddress = NormalizeIp(dto.IpAddress),
                IsActive = dto.IsActive,
                BuildManifest = null,
                BuildId = null,
                Config = new ServerConfigDetails
                {
                    TypeVersion = dto.TypeVersion?.Trim() ?? string.Empty
                }
            };

            var linkError = await ApplyBuildSelectionAsync(server, dto, cancellationToken);
            if (linkError != null) return (false, null, linkError);

            _context.ServerManifests.Add(server);
            await _context.SaveChangesAsync(cancellationToken);

            // Refresh in-memory cache so API returns new server immediately
            try
            {
                await _cache.RefreshCacheAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh config cache after creating server {ServerName}", dto.ServerName);
            }

            _logger.LogInformation("Server {ServerName} created by {Actor}", dto.ServerName, actor);
            await _audit.LogAsync(actor, "create_server", "server", server.Id, dto.ServerName, cancellationToken);
            return (true, server, null);
        }

        public async Task<(bool Success, string? Error)> UpdateServerAsync(long id, AdminServerUpsertDto dto, string actor, CancellationToken cancellationToken = default)
        {
            var server = await _context.ServerManifests.Include(s => s.Config).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (server == null) return (false, "NOT_FOUND");

            server.ServerName = dto.ServerName.Trim();
            server.AppVersion = dto.AppVersion?.Trim() ?? string.Empty;
            server.LoaderType = (dto.LoaderType ?? string.Empty).Trim().ToLowerInvariant();
            server.IpAddress = NormalizeIp(dto.IpAddress);
            server.IsActive = dto.IsActive;

            if (server.Config == null) server.Config = new ServerConfigDetails();

            server.Config.TypeVersion = dto.TypeVersion?.Trim() ?? string.Empty;

            // Link (or unlink) the client build this server serves its launcher manifest from.
            var linkError = await ApplyBuildSelectionAsync(server, dto, cancellationToken);
            if (linkError != null) return (false, linkError);

            await _context.SaveChangesAsync(cancellationToken);

            try
            {
                await _cache.RefreshCacheAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh config cache after updating server {ServerId}", id);
            }

            _logger.LogInformation("Server {ServerId} updated by {Actor}", id, actor);
            await _audit.LogAsync(actor, "update_server", "server", id, dto.ServerName, cancellationToken);
            return (true, null);
        }

        public async Task<(bool Success, string? Error)> DeleteServerAsync(long id, string actor, CancellationToken cancellationToken = default)
        {
            var server = await _context.ServerManifests.FindAsync(new object[] { id }, cancellationToken: cancellationToken);
            if (server == null) return (false, "NOT_FOUND");

            _context.ServerManifests.Remove(server);
            await _context.SaveChangesAsync(cancellationToken);

            // Rewind the sequence so the freed server id is reused by the next server.
            // Housekeeping only - never fail the delete over it.
            try
            {
                await _context.RewindAsync(IdentitySequence.Servers, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rewind identity sequence after deleting server {ServerId}", id);
            }

            try
            {
                await _cache.RefreshCacheAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh config cache after deleting server {ServerId}", id);
            }

            _logger.LogInformation("Server {ServerId} deleted by {Actor}", id, actor);
            await _audit.LogAsync(actor, "delete_server", "server", id, server.ServerName, cancellationToken);
            return (true, null);
        }
        public async Task<IEnumerable<AdminServerDto>> GetAllServersAdminAsync(CancellationToken cancellationToken = default)
        {
            // The admin panel lists every server (including inactive ones), which the public
            // /servers endpoint filters out. BuildManifest is deliberately omitted here - it can
            // hold a multi-megabyte JSON and is only needed by the single-server endpoint.
            var servers = await _context.ServerManifests
                .AsNoTracking()
                .Include(s => s.Config)
                .OrderBy(s => s.Id)
                .ToListAsync(cancellationToken);

            return servers.Select(MapAdminServer);
        }

        public async Task<AdminServerDto?> GetServerAdminAsync(long id, CancellationToken cancellationToken = default)
        {
            var server = await _context.ServerManifests
                .AsNoTracking()
                .Include(s => s.Config)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

            if (server == null) return null;

            var dto = MapAdminServer(server);
            dto.BuildManifest = server.BuildManifest;
            return dto;
        }

        private static AdminServerDto MapAdminServer(ServerManifest m) => new()
        {
            Id = m.Id,
            ServerName = m.ServerName,
            AppVersion = m.AppVersion,
            LoaderType = m.LoaderType,
            IpAddress = m.IpAddress,
            IsActive = m.IsActive,
            BuildManifest = null,
            BuildId = m.BuildId,
            Config = new AdminServerConfigDto
            {
                LoaderVersion = m.Config?.TypeVersion ?? string.Empty
            }
        };

        private static string NormalizeIp(string? ip)
        {
            var value = ip?.Trim();
            return string.IsNullOrEmpty(value) ? "127.0.0.1" : value;
        }

        /// <summary>
        /// Applies the requested build link to <paramref name="server"/> and caches the resolved
        /// manifest JSON. Returns an error code when the request cannot be satisfied.
        /// </summary>
        private async Task<string?> ApplyBuildSelectionAsync(ServerManifest server, AdminServerUpsertDto dto, CancellationToken cancellationToken)
        {
            // An explicit manifest wins: it is not tied to a build row ("legacy" API path).
            if (dto.BuildManifest != null)
            {
                server.BuildId = null;
                server.BuildManifest = dto.BuildManifest;
                return null;
            }

            if (dto.BuildId == null)
            {
                // "auto": follow the newest completed build with the same MC / loader versions.
                var match = await _buildLink.FindMatchingAsync(
                    server.AppVersion, server.LoaderType, server.Config?.TypeVersion, cancellationToken);
                server.BuildId = null;
                server.BuildManifest = match?.ManifestJson;
                return null;
            }

            if (dto.BuildId.Length == 0)
            {
                // "none": no manifest for this server.
                server.BuildId = string.Empty;
                server.BuildManifest = null;
                return null;
            }

            // "pinned": attach the chosen build's manifest.
            var build = await _buildLink.FindByIdAsync(dto.BuildId, cancellationToken);
            if (build == null) return "BUILD_NOT_FOUND";

            server.BuildId = build.BuildId;
            server.BuildManifest = build.ManifestJson;
            return null;
        }
    }
}