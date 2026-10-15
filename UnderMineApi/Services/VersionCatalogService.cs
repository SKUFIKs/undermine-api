using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using UnderMineApi.DTOs;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Discovers which Minecraft and mod-loader versions can actually be installed, so the admin
    /// panel can offer pick-lists instead of asking for hand-typed versions.
    ///
    /// Sources:
    ///  - Minecraft: Mojang piston version manifest.
    ///  - Fabric:    meta.fabricmc.net (game versions + loader versions per game version).
    ///  - Forge:     maven.minecraftforge.net maven-metadata.xml ("1.20.1-47.4.27").
    ///  - NeoForge:  maven.neoforged.net maven-metadata.xml ("20.2.86", "21.1.209", "26.3.0.76").
    ///
    /// The upstream metadata is large (Forge ~5000 versions, NeoForge ~1800) and changes a few
    /// times a week, so every source is cached in memory.
    /// </summary>
    public class VersionCatalogService
    {
        private const string MojangManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private const string FabricGameUrl = "https://meta.fabricmc.net/v2/versions/game";
        private const string FabricLoaderUrl = "https://meta.fabricmc.net/v2/versions/loader/{0}";
        private const string ForgeMetadataUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        private const string NeoForgeMetadataUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
        private static readonly char[] MetadataSeparators = { '-', '+' };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<VersionCatalogService> _logger;

        public VersionCatalogService(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ILogger<VersionCatalogService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _logger = logger;
        }

        public static readonly string[] KnownLoaders = { "vanilla", "fabric", "forge", "neoforge" };

        public static bool IsKnownLoader(string? loader) =>
            loader != null && KnownLoaders.Contains(loader.Trim().ToLowerInvariant());

        /// <summary>
        /// Lists the Minecraft versions a loader can be installed for. Selecting a loader first
        /// therefore never offers a version combination that does not exist.
        /// </summary>
        public async Task<VersionListResponse> GetMinecraftVersionsAsync(
            string loader,
            bool includePrerelease,
            CancellationToken ct)
        {
            var normalized = loader.Trim().ToLowerInvariant();
            var response = new VersionListResponse { Loader = normalized, Source = "mojang" };

            var releases = await GetMinecraftReleasesAsync(ct);

            // null means "no filtering" (vanilla supports every Minecraft version).
            HashSet<string>? supported = null;

            try
            {
                switch (normalized)
                {
                    case "fabric":
                        supported = new HashSet<string>(
                            (await GetFabricGameVersionsAsync(ct)).Keys, StringComparer.OrdinalIgnoreCase);
                        response.Source = "meta.fabricmc.net";
                        break;

                    case "forge":
                        supported = new HashSet<string>(
                            (await GetForgeVersionsAsync(ct)).Keys, StringComparer.OrdinalIgnoreCase);
                        response.Source = "maven.minecraftforge.net";
                        break;

                    case "neoforge":
                        var neoVersions = await GetNeoForgeVersionsAsync(ct);
                        var neoFamilies = neoVersions
                            .Select(NeoForgeFamily)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        supported = new HashSet<string>(
                            releases.Where(r => NeoForgeFamilies(r.Id).Any(neoFamilies.Contains)).Select(r => r.Id),
                            StringComparer.OrdinalIgnoreCase);
                        response.Source = "maven.neoforged.net";
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A loader-specific upstream being down must not make the picker unusable: fall
                // back to the full Mojang list and tell the client why.
                _logger.LogWarning(ex, "Could not load {Loader} Minecraft versions, using the plain Mojang list", normalized);
                response.Message = $"Could not read the {normalized} version list; showing all Minecraft versions.";
                supported = null;
            }

            foreach (var release in releases)
            {
                if (supported != null && !supported.Contains(release.Id)) continue;

                var stable = release.Type == "release";
                if (!stable && !includePrerelease) continue;

                response.Versions.Add(new VersionOptionDto
                {
                    Version = release.Id,
                    Type = release.Type,
                    Stable = stable
                });
            }

            MarkRecommended(response.Versions);
            return response;
        }

        /// <summary>
        /// Lists the loader builds available for one Minecraft version.
        /// </summary>
        public async Task<VersionListResponse> GetLoaderVersionsAsync(
            string loader,
            string mcVersion,
            bool includePrerelease,
            CancellationToken ct)
        {
            var normalized = loader.Trim().ToLowerInvariant();
            var response = new VersionListResponse
            {
                Loader = normalized,
                MinecraftVersion = mcVersion
            };

            if (normalized == "vanilla")
            {
                response.Source = "none";
                response.Message = "Vanilla has no separate loader version.";
                return response;
            }

            try
            {
                switch (normalized)
                {
                    case "fabric":
                        response.Source = "meta.fabricmc.net";
                        foreach (var (version, stable) in await GetFabricLoaderVersionsAsync(mcVersion, includePrerelease, ct))
                        {
                            response.Versions.Add(new VersionOptionDto
                            {
                                Version = version,
                                Type = stable ? "release" : "beta",
                                Stable = stable
                            });
                        }
                        break;

                    case "forge":
                        response.Source = "maven.minecraftforge.net";
                        var forge = await GetForgeVersionsAsync(ct);
                        if (forge.TryGetValue(mcVersion, out var forgeVersions))
                        {
                            foreach (var version in forgeVersions.OrderByDescending(v => v, VersionComparer.Instance))
                            {
                                response.Versions.Add(new VersionOptionDto
                                {
                                    Version = version,
                                    Type = "release",
                                    Stable = true
                                });
                            }
                        }
                        else
                        {
                            response.Message = $"Forge has no builds for Minecraft {mcVersion}.";
                        }
                        break;

                    case "neoforge":
                        response.Source = "maven.neoforged.net";
                        var families = NeoForgeFamilies(mcVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var neoVersions = await GetNeoForgeVersionsAsync(ct);
                        foreach (var version in neoVersions
                                     .Where(v => families.Contains(NeoForgeFamily(v)))
                                     .OrderByDescending(v => v, VersionComparer.Instance))
                        {
                            var stable = !IsPrerelease(version);
                            if (!stable && !includePrerelease) continue;

                            response.Versions.Add(new VersionOptionDto
                            {
                                Version = version,
                                Type = stable ? "release" : "beta",
                                Stable = stable
                            });
                        }

                        if (response.Versions.Count == 0)
                            response.Message = $"NeoForge has no builds for Minecraft {mcVersion}.";
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not load {Loader} {McVersion} loader versions", normalized, mcVersion);
                response.Message = $"Could not read the {normalized} version list for Minecraft {mcVersion}.";
            }

            MarkRecommended(response.Versions);
            return response;
        }

        /// <summary>
        /// Newest usable entry of a loader, used as the default when a build does not pin a version.
        /// </summary>
        public async Task<string?> GetLatestLoaderVersionAsync(string loader, string mcVersion, CancellationToken ct)
        {
            var result = await GetLoaderVersionsAsync(loader, mcVersion, includePrerelease: false, ct);
            return result.Versions.FirstOrDefault()?.Version;
        }

        private static void MarkRecommended(List<VersionOptionDto> versions)
        {
            if (versions.Count == 0) return;

            // The lists are ordered newest first, so the first stable entry is the safest default.
            var recommended = versions.FirstOrDefault(v => v.Stable) ?? versions[0];
            recommended.Recommended = true;
        }

        // ---------- Mojang ----------

        private sealed record MinecraftRelease(string Id, string Type);

        private async Task<List<MinecraftRelease>> GetMinecraftReleasesAsync(CancellationToken ct)
        {
            var cached = await _cache.GetOrCreateAsync("versions:minecraft", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                var client = _httpClientFactory.CreateClient();
                var manifest = await client.GetFromJsonAsync<JsonNode>(MojangManifestUrl, ct)
                    ?? throw new InvalidOperationException("Mojang version manifest is empty");

                var releases = new List<MinecraftRelease>();
                foreach (var node in manifest["versions"]?.AsArray() ?? new JsonArray())
                {
                    var id = node?["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;

                    releases.Add(new MinecraftRelease(id, node?["type"]?.GetValue<string>() ?? "release"));
                }

                // The manifest is newest-first and the picker relies on that order.
                return releases;
            });

            return cached ?? new List<MinecraftRelease>();
        }

        // ---------- Fabric ----------

        private async Task<Dictionary<string, bool>> GetFabricGameVersionsAsync(CancellationToken ct)
        {
            var cached = await _cache.GetOrCreateAsync("versions:fabric:game", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                var client = _httpClientFactory.CreateClient();
                var json = await client.GetFromJsonAsync<JsonNode>(FabricGameUrl, ct)
                    ?? throw new InvalidOperationException("Fabric game version list is empty");

                var versions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in json.AsArray())
                {
                    var version = node?["version"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(version)) continue;

                    versions[version] = node?["stable"]?.GetValue<bool>() ?? true;
                }

                return versions;
            });

            return cached ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<List<(string Version, bool Stable)>> GetFabricLoaderVersionsAsync(
            string mcVersion, bool includePrerelease, CancellationToken ct)
        {
            var key = $"versions:fabric:loader:{mcVersion}:{includePrerelease}";

            var cached = await _cache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                var client = _httpClientFactory.CreateClient();
                var json = await client.GetFromJsonAsync<JsonNode>(string.Format(FabricLoaderUrl, mcVersion), ct)
                    ?? new JsonArray();

                var versions = new List<(string Version, bool Stable)>();
                foreach (var node in json.AsArray())
                {
                    var version = node?["loader"]?["version"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(version)) continue;

                    var stable = node?["loader"]?["stable"]?.GetValue<bool>() ?? false;
                    if (!stable && !includePrerelease) continue;

                    versions.Add((version, stable));
                }

                return versions;
            });

            return cached ?? new List<(string Version, bool Stable)>();
        }

        // ---------- Forge / NeoForge maven metadata ----------

        /// <summary>Maps a Minecraft version to the list of Forge loader versions published for it.</summary>
        private async Task<Dictionary<string, List<string>>> GetForgeVersionsAsync(CancellationToken ct)
        {
            var cached = await _cache.GetOrCreateAsync("versions:forge", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                var client = _httpClientFactory.CreateClient();
                var xml = await client.GetStringAsync(ForgeMetadataUrl, ct);

                var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in ParseMavenVersions(xml))
                {
                    // "1.20.1-47.4.27" -> minecraft "1.20.1", loader "47.4.27".
                    // Very old builds carry the game version twice ("1.7.10-10.13.4.1614-1.7.10").
                    var separator = raw.IndexOf('-');
                    if (separator <= 0) continue;

                    var minecraft = raw[..separator];
                    var loader = raw[(separator + 1)..].Split('-')[0];
                    if (minecraft.Length == 0 || loader.Length == 0) continue;

                    if (!result.TryGetValue(minecraft, out var list))
                        result[minecraft] = list = new List<string>();

                    if (!list.Contains(loader)) list.Add(loader);
                }

                if (result.Count == 0)
                    throw new InvalidOperationException("Forge maven metadata contained no versions");

                return result;
            });

            return cached ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<List<string>> GetNeoForgeVersionsAsync(CancellationToken ct)
        {
            var cached = await _cache.GetOrCreateAsync("versions:neoforge", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                var client = _httpClientFactory.CreateClient();
                var xml = await client.GetStringAsync(NeoForgeMetadataUrl, ct);

                var versions = ParseMavenVersions(xml);
                if (versions.Count == 0)
                    throw new InvalidOperationException("NeoForge maven metadata contained no versions");

                return versions;
            });

            return cached ?? new List<string>();
        }

        private static List<string> ParseMavenVersions(string xml)
        {
            // maven-metadata.xml has no XML namespace (unlike pom.xml), but match by local name
            // so the parser keeps working if an upstream ever adds one.
            var document = XDocument.Parse(xml);
            return document.Descendants()
                .Where(e => e.Name.LocalName == "version")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// NeoForge versions are named "&lt;family&gt;.&lt;build&gt;" where the family encodes the
        /// Minecraft version: 1.20.2 -&gt; 20.2, 1.21 -&gt; 21.0, 1.21.1 -&gt; 21.1 and (newer
        /// Mojang numbering) 26.3 -&gt; 26.3.
        /// </summary>
        private static IEnumerable<string> NeoForgeFamilies(string mcVersion)
        {
            var parts = CoreVersion(mcVersion).Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) yield break;

            if (parts[0] == "1")
                yield return $"{parts[1]}.{(parts.Length >= 3 ? parts[2] : "0")}";
            else
                yield return $"{parts[0]}.{parts[1]}";
        }

        private static string NeoForgeFamily(string neoVersion)
        {
            var parts = CoreVersion(neoVersion).Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : CoreVersion(neoVersion);
        }

        private static string CoreVersion(string version)
        {
            var cut = version.IndexOfAny(MetadataSeparators);
            return cut > 0 ? version[..cut] : version;
        }

        private static bool IsPrerelease(string version) =>
            version.Contains('-') || version.Contains('+');

        /// <summary>
        /// Compares dotted versions numerically so that 47.10.0 sorts above 47.9.1, with
        /// betas/alphas below the corresponding release.
        /// </summary>
        private sealed class VersionComparer : IComparer<string>
        {
            public static readonly VersionComparer Instance = new();

            public int Compare(string? x, string? y)
            {
                var (left, leftPre) = Numbers(x);
                var (right, rightPre) = Numbers(y);

                for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
                {
                    var a = i < left.Length ? left[i] : 0;
                    var b = i < right.Length ? right[i] : 0;
                    if (a != b) return a.CompareTo(b);
                }

                if (leftPre != rightPre) return leftPre ? -1 : 1;
                return string.CompareOrdinal(x, y);
            }

            private static (int[] Numbers, bool Prerelease) Numbers(string? version)
            {
                if (string.IsNullOrEmpty(version)) return (Array.Empty<int>(), true);

                var prerelease = false;
                var core = version;

                var plus = core.IndexOf('+');
                if (plus >= 0)
                {
                    core = core[..plus];
                    prerelease = true;
                }

                var dash = core.IndexOf('-');
                if (dash >= 0)
                {
                    core = core[..dash];
                    prerelease = true;
                }

                var numbers = core
                    .Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p, out var n) ? n : -1)
                    .ToArray();

                return (numbers, prerelease);
            }
        }
    }
}
