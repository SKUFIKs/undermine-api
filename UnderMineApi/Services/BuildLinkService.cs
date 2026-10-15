using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Resolves which completed build a server should hand to the launcher, either by an
    /// explicit build id ("pinned") or by matching the server's Minecraft / loader versions ("auto").
    /// </summary>
    public class BuildLinkService
    {
        public const string CompletedStatus = "completed";

        private readonly AppDbContext _context;

        public BuildLinkService(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Returns the completed build with the given build id, or null when it does not exist /
        /// has no manifest yet.
        /// </summary>
        public Task<BuildInstance?> FindByIdAsync(string? buildId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(buildId))
                return Task.FromResult<BuildInstance?>(null);

            return _context.BuildInstances
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.BuildId == buildId && b.Status == CompletedStatus && b.ManifestJson != null,
                    cancellationToken);
        }

        /// <summary>
        /// Newest completed build matching the given Minecraft version, loader and loader build.
        /// Empty loader versions are normalised so vanilla (which has no loader build) matches too.
        /// </summary>
        public Task<BuildInstance?> FindMatchingAsync(
            string? mcVersion,
            string? loaderType,
            string? loaderVersion,
            CancellationToken cancellationToken = default)
        {
            var mc = (mcVersion ?? string.Empty).Trim().ToLowerInvariant();
            var loader = (loaderType ?? string.Empty).Trim().ToLowerInvariant();
            var version = NormalizeLoaderVersion(loaderVersion);

            var query = _context.BuildInstances
                .AsNoTracking()
                .Where(b => b.Status == CompletedStatus && b.ManifestJson != null)
                .Where(b => b.McVersion.ToLower() == mc && b.LoaderType.ToLower() == loader);

            query = version == null
                ? query.Where(b => b.LoaderVersion == null || b.LoaderVersion == "")
                : query.Where(b => b.LoaderVersion == version);

            return query
                .OrderByDescending(b => b.CompletedAt ?? b.CreatedAt)
                .ThenByDescending(b => b.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public static string? NormalizeLoaderVersion(string? loaderVersion)
            => string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();
    }
}
