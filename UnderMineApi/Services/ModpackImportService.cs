using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;

namespace UnderMineApi.Services;

/// <summary>Import failed for a reason the API surface should map to a 400 response.</summary>
public class ModpackImportException : Exception
{
    public ModpackImportException(string message) : base(message) { }
    public ModpackImportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The requested Modrinth project/version does not exist (maps to 404).</summary>
public class ModpackNotFoundException : ModpackImportException
{
    public ModpackNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Imports complete Minecraft modpacks from Modrinth (slug/version id) or from an admin-uploaded
/// archive, in either supported format:
/// <list type="bullet">
/// <item>Modrinth .mrpack - modrinth.index.json carries paths, URLs and hashes;</item>
/// <item>CurseForge pack zip - manifest.json carries (projectID, fileID) pairs only, so the files
/// are resolved through the CurseForge API while the build runs.</item>
/// </list>
/// Responsibilities:
///
///  1. stage the archive under {Build:RootPath}/imports/{buildId}.mrpack,
///  2. detect the format and parse + validate its manifest (System.Text.Json),
///  3. resolve the exact Minecraft + loader versions,
///  4. (build worker) resolve CurseForge ids when needed, then download every client file in
///     parallel with hash verification and unpack the pack's overrides onto the build directory.
///
/// The staged archive is kept until the build completes so retries can re-run the
/// import without re-uploading / re-downloading it.
/// </summary>
public class ModpackImportService
{
    /// <summary>Concurrent mod downloads for one build (matches the S3 upload pool size).</summary>
    public const int MaxParallelDownloads = 8;

    /// <summary>Hard cap for a staged .mrpack or a single index file (1 GB).</summary>
    public const long MaxFileBytes = 1024L * 1024 * 1024;

    /// <summary>Default HTTP timeout for large file downloads (whole-request timeout).</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private const string UserAgent = "UnderMineApi/1.0 (contact@api-undermine.ru)";
    private const string IndexEntryName = "modrinth.index.json";
    private const string CurseForgeManifestEntryName = "manifest.json";
    private const string StagingSubDir = "imports";
    private const string StagingExtension = ".mrpack";

    /// <summary>Archive holds modrinth.index.json (.mrpack, Modrinth format).</summary>
    public const string FormatModrinth = "mrpack";

    /// <summary>Archive holds manifest.json (CurseForge format: ids only, no jars inside).</summary>
    public const string FormatCurseForge = "curseforge";

    /// <summary>
    /// Concurrent CurseForge lookups per build. Comfortably below the API allowance while still
    /// resolving a 200+ file pack in a few seconds.
    /// </summary>
    private const int CurseForgeResolveParallelism = 6;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] OverrideRoots = { "overrides/", "client-overrides/" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ModpackImportService> _logger;
    private readonly CurseForgeService _curseForge;

    public ModpackImportService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<ModpackImportService> logger,
        CurseForgeService curseForge)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _curseForge = curseForge;
    }

    // ------------------------------------------------------------------
    // Staging helpers (shared with the build worker and the admin controller)
    // ------------------------------------------------------------------

    /// <summary>Directory holding staged modpack archives: {Build:RootPath}/imports.</summary>
    private string ImportsDir => Path.Combine(_config["Build:RootPath"] ?? "builds", StagingSubDir);

    /// <summary>
    /// Path of the staged archive for a build. Both formats share the .mrpack extension so
    /// cleanup (which sweeps *.mrpack) stays format-agnostic; the real format is detected from
    /// the archive contents.
    /// </summary>
    public static string GetStagingPath(IConfiguration config, string buildId) =>
        Path.Combine(config["Build:RootPath"] ?? "builds", StagingSubDir, buildId + StagingExtension);

    public string GetStagingPath(string buildId) => GetStagingPath(_config, buildId);

    /// <summary>Removes the staged archive of a build (called after a successful build).</summary>
    public void CleanupStaging(string buildId)
    {
        var path = GetStagingPath(buildId);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete staged modpack {Path}", path);
        }
    }

    /// <summary>Startup sweep: drops staged archives left behind by builds that no longer exist.</summary>
    public void CleanupOrphanStaging(IEnumerable<string> activeBuildIds)
    {
        try
        {
            if (!Directory.Exists(ImportsDir)) return;

            var active = new HashSet<string>(activeBuildIds, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(ImportsDir, "*" + StagingExtension))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (active.Contains(id)) continue;

                try { File.Delete(file); _logger.LogInformation("Removed orphan staged modpack {File}", file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {File}", file); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orphan staging cleanup failed");
        }
    }

    // ------------------------------------------------------------------
    // 1. Staging: Modrinth slug/id or uploaded file
    // ------------------------------------------------------------------

    /// <summary>
    /// Downloads the .mrpack of the latest release (or a concrete version id) from Modrinth
    /// into the staging area and parses its index.
    /// </summary>
    public async Task<StagedModpack> StageFromModrinthAsync(
        string slugOrId, string? versionId, string buildId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slugOrId))
            throw new ModpackImportException("Modpack slug or id is required");

        var version = await FindModpackVersionAsync(slugOrId.Trim(), versionId, ct);

        var file = version.Files.FirstOrDefault(f => f.Filename.EndsWith(StagingExtension, StringComparison.OrdinalIgnoreCase))
                   ?? version.Files.FirstOrDefault()
                   ?? throw new ModpackImportException($"Modpack version {version.Id} has no downloadable files");

        Directory.CreateDirectory(ImportsDir);
        var stagingPath = GetStagingPath(buildId);

        try
        {
            await DownloadVerifiedAsync(
                file.Url, stagingPath, file.Hashes, expectedSize: null,
                progress: null, displayPath: file.Filename, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ModpackNotFoundException($"Modpack file not found on Modrinth: {file.Filename}");
        }

        var index = ParseIndex(stagingPath);
        _logger.LogInformation(
            "Staged modpack {Name} (version {Version}) for build {BuildId} from Modrinth",
            index.Name ?? file.Filename, version.VersionNumber ?? version.Id, buildId);

        return new StagedModpack
        {
            BuildId = buildId,
            FilePath = stagingPath,
            Index = index,
            FileName = file.Filename,
            Format = FormatModrinth,
            VersionId = version.Id,
            VersionLabel = version.VersionNumber ?? version.Name
        };
    }

    /// <summary>
    /// Streams an admin-uploaded modpack archive into the staging area and parses its manifest.
    /// Both supported formats arrive here: .mrpack (Modrinth) and the CurseForge pack zip.
    /// </summary>
    public async Task<StagedModpack> StageFromUploadAsync(
        Stream upload, string originalFileName, string buildId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ImportsDir);
        var stagingPath = GetStagingPath(buildId);

        try
        {
            await using (var fileStream = File.Create(stagingPath))
            {
                await upload.CopyToAsync(fileStream, ct);
            }

            var size = new FileInfo(stagingPath).Length;
            if (size == 0)
                throw new ModpackImportException("The uploaded file is empty");
            if (size > MaxFileBytes)
                throw new ModpackImportException($"The uploaded file exceeds {MaxFileBytes / (1024 * 1024)} MB");

            var descriptor = ReadDescriptor(stagingPath);
            var index = descriptor.Index;

            // CurseForge packs only get their URLs during the build, so validate the API key now
            // and probe a single file: a missing or rejected key must fail this request rather
            // than resurface minutes later as an inexplicable failed build.
            if (descriptor.Format == FormatCurseForge)
            {
                EnsureCurseForgeConfigured();
                var probe = descriptor.CurseForge!.Files[0];
                await _curseForge.GetFileAsync(probe.ProjectId, probe.FileId, ct);
            }

            _logger.LogInformation(
                "Staged uploaded modpack {FileName} ({Name}, format {Format}) for build {BuildId}",
                originalFileName, index.Name, descriptor.Format, buildId);

            return new StagedModpack
            {
                BuildId = buildId,
                FilePath = stagingPath,
                Index = index,
                FileName = originalFileName,
                Format = descriptor.Format,
                CurseForge = descriptor.CurseForge
            };
        }
        catch
        {
            // A file that never became a valid import descriptor must not linger in staging.
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Picks the modpack version from Modrinth: an exact version id when requested,
    /// otherwise the newest release (falling back to the newest pre-release/beta).
    /// </summary>
    private async Task<ModrinthModpackVersion> FindModpackVersionAsync(
        string slugOrId, string? versionId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(slugOrId)}/version";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await client.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Modrinth rate limit exceeded", null, HttpStatusCode.TooManyRequests);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ModpackNotFoundException($"Modrinth project not found: {slugOrId}");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        List<ModrinthModpackVersion>? versions;
        try
        {
            versions = JsonSerializer.Deserialize<List<ModrinthModpackVersion>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ModpackImportException("Unexpected response from Modrinth API", ex);
        }

        if (versions == null || versions.Count == 0)
            throw new ModpackNotFoundException($"The Modrinth project '{slugOrId}' has no published versions");

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            return versions.FirstOrDefault(v => v.Id == versionId)
                   ?? throw new ModpackNotFoundException(
                       $"Version '{versionId}' not found for project '{slugOrId}'");
        }

        var releases = versions
            .Where(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pool = releases.Count > 0 ? releases : versions;

        return pool.OrderByDescending(v => v.DatePublished).First();
    }

    // ------------------------------------------------------------------
    // 2. Index parsing / validation
    // ------------------------------------------------------------------

    /// <summary>Validated modrinth index of a staged archive (format-agnostic entry point).</summary>
    public ModrinthIndex ParseIndex(string archivePath) => ReadDescriptor(archivePath).Index;

    /// <summary>
    /// Opens a staged modpack archive and returns whichever manifest it carries, so admins can
    /// import packs from both stores without converting them by hand:
    /// <list type="bullet">
    /// <item>.mrpack - modrinth.index.json: a complete index (paths, URLs, hashes);</item>
    /// <item>CurseForge - manifest.json: ids only, the files themselves still have to be
    /// resolved through the CurseForge API later, by the build worker.</item>
    /// </list>
    /// For CurseForge the returned index already carries the pack name and the engine
    /// dependencies (everything the controller needs to create the build); its Files list stays
    /// empty until <see cref="ResolveCurseForgeIndexAsync"/> fills it in during the build.
    /// </summary>
    public ArchiveDescriptor ReadDescriptor(string archivePath)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException ex)
        {
            throw new ModpackImportException("The file is not a valid ZIP archive (.mrpack / CurseForge modpack)", ex);
        }
        catch (IOException ex)
        {
            throw new ModpackImportException("Could not read the modpack archive", ex);
        }

        using (zip)
        {
            // A CurseForge pack never carries modrinth.index.json, so the cheaper signal wins.
            var indexEntry = zip.GetEntry(IndexEntryName);
            if (indexEntry != null)
                return new ArchiveDescriptor { Format = FormatModrinth, Index = ParseModrinthIndex(indexEntry) };

            var manifestEntry = zip.GetEntry(CurseForgeManifestEntryName)
                ?? throw new ModpackImportException(
                    $"The archive contains neither {IndexEntryName} nor {CurseForgeManifestEntryName} - not a modpack");

            var curseForge = ParseCurseForgeManifest(manifestEntry);
            return new ArchiveDescriptor
            {
                Format = FormatCurseForge,
                CurseForge = curseForge,
                Index = ToModrinthShapedIndex(curseForge)
            };
        }
    }

    /// <summary>Reads and validates modrinth.index.json of a .mrpack archive.</summary>
    private ModrinthIndex ParseModrinthIndex(ZipArchiveEntry entry)
    {
        string json;
        using (var reader = new StreamReader(entry.Open()))
        {
            json = reader.ReadToEnd();
        }

        ModrinthIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<ModrinthIndex>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ModpackImportException($"{IndexEntryName} is not valid JSON: {ex.Message}", ex);
        }

        if (index == null)
            throw new ModpackImportException($"{IndexEntryName} is empty");

        if (!string.Equals(index.Game?.Trim(), "minecraft", StringComparison.OrdinalIgnoreCase))
            throw new ModpackImportException($"Unsupported game in index: '{index.Game}'");

        if (index.Files.Count == 0)
            throw new ModpackImportException($"{IndexEntryName} contains no files");

        foreach (var file in index.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new ModpackImportException("An index file entry has no path");

            ValidateRelativePath(file.Path);

            if ((file.Downloads == null || file.Downloads.Count == 0) && file.Sha1 == null)
                throw new ModpackImportException(
                    $"Index file '{file.Path}' has no download URLs and no sha1 to resolve it by hash");
        }

        return index;
    }

    /// <summary>Reads and validates the CurseForge manifest.json of an uploaded pack.</summary>
    private CurseForgeManifest ParseCurseForgeManifest(ZipArchiveEntry entry)
    {
        string json;
        using (var reader = new StreamReader(entry.Open()))
        {
            json = reader.ReadToEnd();
        }

        CurseForgeManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CurseForgeManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ModpackImportException($"{CurseForgeManifestEntryName} is not valid JSON: {ex.Message}", ex);
        }

        if (manifest == null)
            throw new ModpackImportException($"{CurseForgeManifestEntryName} is empty");

        if (!string.IsNullOrWhiteSpace(manifest.ManifestType)
            && !string.Equals(manifest.ManifestType.Trim(), "minecraftModpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModpackImportException($"Unsupported manifestType '{manifest.ManifestType}' (expected minecraftModpack)");
        }

        var mcVersion = manifest.Minecraft.Version?.Trim();
        if (string.IsNullOrEmpty(mcVersion))
            throw new ModpackImportException($"{CurseForgeManifestEntryName} has no minecraft version");
        if (mcVersion.Length > 50)
            throw new ModpackImportException($"Minecraft version is too long: '{mcVersion}'");
        manifest.Minecraft.Version = mcVersion;

        if (manifest.Files.Count == 0)
            throw new ModpackImportException($"{CurseForgeManifestEntryName} contains no files");

        foreach (var file in manifest.Files)
        {
            if (file.ProjectId <= 0 || file.FileId <= 0)
                throw new ModpackImportException(
                    $"A CurseForge file entry has invalid ids (projectID {file.ProjectId}, fileID {file.FileId})");
        }

        // Fails here for loaders the engine cannot install (quilt, liteloader, ...).
        ToModrinthShapedIndex(manifest);

        return manifest;
    }

    /// <summary>
    /// Projects a CurseForge manifest into the index shape the rest of the import pipeline
    /// already consumes: pack name plus engine dependencies. Files stay empty on purpose -
    /// CurseForge stores carry no paths or URLs until every id is resolved.
    /// </summary>
    private static ModrinthIndex ToModrinthShapedIndex(CurseForgeManifest manifest)
    {
        var index = new ModrinthIndex
        {
            Game = "minecraft",
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? null : manifest.Name!.Trim(),
            VersionId = string.IsNullOrWhiteSpace(manifest.Version) ? null : manifest.Version!.Trim(),
            Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minecraft"] = manifest.Minecraft.Version!.Trim()
            }
        };

        var loader = ResolveCurseForgeLoader(manifest);
        if (loader != null)
            index.Dependencies![loader.Value.Key] = loader.Value.Version;

        return index;
    }

    /// <summary>Loaders the engine installer knows how to apply.</summary>
    private static readonly string[] SupportedCurseForgeLoaders = { "neoforge", "forge", "fabric", "quilt" };

    /// <summary>
    /// Reads the primary mod loader entry ("forge-14.23.5.2860" -> forge/14.23.5.2860) into an
    /// index dependency key. Packs that repeat the game version ("forge-1.12.2-14.23.5.2860")
    /// are handled too, and anything unsupported fails with a readable message instead of
    /// producing a build that cannot launch.
    /// </summary>
    private static (string Key, string Version)? ResolveCurseForgeLoader(CurseForgeManifest manifest)
    {
        var loaders = manifest.Minecraft.ModLoaders;
        if (loaders == null || loaders.Count == 0)
            return null;

        var chosen = loaders.FirstOrDefault(l => l.Primary) ?? loaders[0];
        var id = chosen?.Id?.Trim();
        if (string.IsNullOrEmpty(id))
            return null;

        var dash = id.IndexOf('-');
        if (dash <= 0 || dash >= id.Length - 1)
            throw new ModpackImportException($"Unsupported CurseForge mod loader id '{id}'");

        var loader = id[..dash].Trim();
        var version = id[(dash + 1)..].Trim();

        var mc = manifest.Minecraft.Version;
        if (!string.IsNullOrEmpty(mc) && version.StartsWith(mc + "-", StringComparison.Ordinal))
            version = version[(mc.Length + 1)..];

        if (!SupportedCurseForgeLoaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
            throw new ModpackImportException(
                $"CurseForge loader '{loader}' is not supported (supported: forge, neoforge, fabric)");

        if (string.IsNullOrEmpty(version))
            throw new ModpackImportException($"CurseForge loader '{loader}' has no version");

        // The engine indexes loaders by Modrinth's dependency keys; quilt has to land there too
        // so ResolveDependencies can reject it explicitly instead of silently building vanilla.
        var key = loader.ToLowerInvariant() switch
        {
            "fabric" => "fabric-loader",
            "quilt" => "quilt-loader",
            var other => other
        };

        return (key, version);
    }

    /// <summary>
    /// Maps index dependencies to the engine parameters: exact Minecraft version plus
    /// loader type and its exact version ("fabric-loader" -> fabric, "forge" -> forge,
    /// "neoforge" -> neoforge; no loader key -> vanilla).
    /// </summary>
    public (string McVersion, string LoaderType, string? LoaderVersion) ResolveDependencies(ModrinthIndex index)
    {
        var deps = index.Dependencies ?? new Dictionary<string, string>();

        if (!deps.TryGetValue("minecraft", out var mc) || string.IsNullOrWhiteSpace(mc))
            throw new ModpackImportException($"{IndexEntryName} has no minecraft dependency");

        // Some hand-written indexes quote the value ("1.20.1" -> 1.20.1).
        var mcVersion = mc.Trim().Trim('"');
        if (mcVersion.Length > 50)
            throw new ModpackImportException($"Minecraft version is too long: '{mcVersion}'");

        string loaderType = "vanilla";
        string? loaderVersion = null;

        if (TryGetLoader(deps, "neoforge", out var neo))
        {
            loaderType = "neoforge";
            loaderVersion = neo;
        }
        else if (TryGetLoader(deps, "forge", out var forge))
        {
            loaderType = "forge";
            loaderVersion = forge;
        }
        else if (TryGetLoader(deps, "fabric-loader", out var fabric))
        {
            loaderType = "fabric";
            loaderVersion = fabric;
        }
        else if (deps.Keys.Any(k => string.Equals(k, "quilt-loader", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ModpackImportException("Quilt modpacks are not supported (use a Fabric pack instead)");
        }

        if (loaderVersion != null && loaderVersion.Length > 100)
            throw new ModpackImportException($"Loader version is too long: '{loaderVersion}'");

        return (mcVersion, loaderType, loaderVersion);
    }

    private static bool TryGetLoader(Dictionary<string, string> deps, string key, out string version)
    {
        version = string.Empty;
        foreach (var pair in deps)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(pair.Value)) return false;

            version = pair.Value.Trim().Trim('"');
            return version.Length > 0;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 2b. CurseForge: (projectID, fileID) -> real files
    // ------------------------------------------------------------------

    /// <summary>Fails fast when CurseForge import is used without a configured API key.</summary>
    public void EnsureCurseForgeConfigured() => _curseForge.EnsureConfigured();

    /// <summary>
    /// Turns a CurseForge manifest into the index shape the download pipeline already consumes:
    /// every unique (projectID, fileID) is looked up through the CurseForge API to obtain the
    /// real file name, URL, size and sha1, and is placed in <c>mods/</c>.
    ///
    /// One unresolved file fails the whole import on purpose - a pack silently missing a mod is
    /// far worse than a readable error, because the broken result would only show up in game.
    /// </summary>
    public async Task<ModrinthIndex> ResolveCurseForgeIndexAsync(
        CurseForgeManifest manifest,
        string buildId,
        BuildProgressReporter progress,
        CancellationToken ct)
    {
        EnsureCurseForgeConfigured();

        var raw = manifest.Files;
        var unique = raw
            .Where(f => f.ProjectId > 0 && f.FileId > 0)
            .DistinctBy(f => (f.ProjectId, f.FileId))
            .ToList();

        if (unique.Count == 0)
            throw new ModpackImportException($"{CurseForgeManifestEntryName} contains no files");

        var repeated = raw.Count - unique.Count;
        if (repeated > 0)
            await progress.LogAsync("info",
                $"CurseForge: повторяющихся записей файлов — {repeated} (пропущены)", ct);

        await progress.LogAsync("info",
            $"CurseForge: получение описаний {unique.Count} файлов через API…", ct);

        var resolved = new ConcurrentBag<ModrinthIndexFile>();
        string? firstError = null;
        var failed = 0;

        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions { MaxDegreeOfParallelism = CurseForgeResolveParallelism, CancellationToken = ct },
            async (entry, token) =>
            {
                try
                {
                    var file = await _curseForge.GetFileAsync(entry.ProjectId, entry.FileId, token);
                    var fileName = SanitizeCurseForgeFileName(file.FileName);
                    var spec = new ModrinthIndexFile
                    {
                        Path = "mods/" + fileName,
                        Downloads = new List<string> { file.DownloadUrl },
                        FileSize = file.FileLength is > 0 ? file.FileLength : null,
                        Hashes = file.Sha1 == null
                            ? null
                            : new Dictionary<string, string> { ["sha1"] = file.Sha1 }
                    };

                    ValidateRelativePath(spec.Path);
                    resolved.Add(spec);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Interlocked.CompareExchange(ref firstError, ex.Message, null);
                    _logger.LogWarning(ex, "CurseForge could not resolve file {ProjectId}/{FileId} for build {BuildId}",
                        entry.ProjectId, entry.FileId, buildId);
                }
            });

        if (failed > 0)
            throw new ModpackImportException(
                $"CurseForge: не удалось получить {failed} из {unique.Count} файлов. Первая ошибка: {firstError}");

        // Two different ids can legitimately point at the same jar name; keep one copy.
        var files = resolved
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var index = ToModrinthShapedIndex(manifest);
        index.Files = files;

        var withoutHash = files.Count(f => f.Sha1 == null);
        var megabytes = files.Sum(f => f.FileSize ?? 0) / (1024.0 * 1024.0);
        await progress.LogAsync("info",
            $"CurseForge: файлов {files.Count}, объём {megabytes:F1} МБ" +
            (withoutHash > 0 ? $", без sha1 — {withoutHash} (проверка только по размеру)" : string.Empty),
            ct);

        return index;
    }

    /// <summary>
    /// CurseForge packs ship a single overrides folder named by <c>manifest.overrides</c>
    /// (usually "overrides") and have no client-overrides counterpart.
    /// </summary>
    private static string[] CurseForgeOverrideRoots(CurseForgeManifest manifest)
    {
        var dir = (manifest.Overrides ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(dir)) dir = "overrides";

        if (dir.Contains('/') || dir is "." or "..")
            throw new ModpackImportException(
                $"Unsupported overrides folder name '{manifest.Overrides}' in the CurseForge manifest");

        return new[] { dir + "/" };
    }

    /// <summary>
    /// Keeps only the bare file name and drops characters Windows would reject, so a malformed
    /// name coming from the API can never escape the mods folder.
    /// </summary>
    private static string SanitizeCurseForgeFileName(string fileName)
    {
        var candidate = fileName.Replace('\\', '/');
        var slash = candidate.LastIndexOf('/');
        if (slash >= 0) candidate = candidate[(slash + 1)..];

        var cleaned = new string(candidate
            .Where(c => c >= 32
                        && c != '"' && c != '<' && c != '>' && c != '|' && c != ':'
                        && c != '*' && c != '?' && c != '/')
            .ToArray());

        cleaned = cleaned.Trim().TrimEnd('.', ' ').Trim();

        if (cleaned.Length == 0 || cleaned is "." or "..")
            throw new ModpackImportException($"CurseForge returned an unusable file name '{fileName}'");

        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    // ------------------------------------------------------------------
    // 3. Build-time application (build worker): parallel downloads + overrides
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies the modpack content on top of the downloaded engine: downloads every client file
    /// (parallel, hash verified) and then unpacks the pack's overrides over the result.
    /// Handles both staged formats - .mrpack indexes resolve immediately, CurseForge manifests
    /// first go through the CurseForge API to turn their ids into real files.
    /// </summary>
    public async Task ApplyModpackAsync(
        BuildInstance build,
        ModpackImportInfo info,
        string buildDir,
        BuildProgressReporter progress,
        CancellationToken ct)
    {
        var stagingPath = GetStagingPath(build.BuildId);

        if (!File.Exists(stagingPath))
        {
            if (string.Equals(info.SourceType, ModpackImportInfo.SourceModrinth, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(info.SlugOrId))
            {
                await progress.LogAsync("info",
                    "Локальная копия .mrpack отсутствует — повторное скачивание с Modrinth", ct);
                var staged = await StageFromModrinthAsync(info.SlugOrId, info.VersionId, build.BuildId, ct);
                stagingPath = staged.FilePath;
            }
            else
            {
                throw new ModpackImportException(
                    "The staged modpack file is missing on the server. Import the modpack again.");
            }
        }

        var descriptor = ReadDescriptor(stagingPath);

        ModrinthIndex index;
        string[] overrideRoots;
        if (descriptor.Format == FormatCurseForge)
        {
            progress.SetStep("Резолв файлов CurseForge");
            await progress.FlushAsync(ct);
            index = await ResolveCurseForgeIndexAsync(descriptor.CurseForge!, build.BuildId, progress, ct);
            overrideRoots = CurseForgeOverrideRoots(descriptor.CurseForge!);
        }
        else
        {
            index = descriptor.Index;
            overrideRoots = OverrideRoots;
        }

        var clientFiles = index.Files.Where(f => !f.IsClientUnsupported).ToList();
        var skipped = index.Files.Count - clientFiles.Count;

        if (skipped > 0)
            await progress.LogAsync("info", $"Пропущено серверных файлов (client=unsupported): {skipped}", ct);

        await progress.LogAsync("info",
            $"Модпак «{index.Name ?? build.Name}»: скачивание {clientFiles.Count} файлов", ct);

        progress.SetStep($"Скачивание файлов модпака ({clientFiles.Count})");
        progress.BeginParallelPhase(
            clientFiles.Count,
            clientFiles.Sum(f => f.FileSize ?? 0));
        await progress.FlushAsync(ct);

        var completed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelDownloads,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(clientFiles, options, async (file, token) =>
        {
            var destPath = SafeResolvePath(buildDir, file.Path);

            // Retried builds: a file already on disk with the expected hash is skipped,
            // which keeps the second attempt of a large modpack cheap.
            if (File.Exists(destPath) && await MatchesHashAsync(destPath, file.Sha512, file.Sha1, token))
            {
                var skippedDone = Interlocked.Increment(ref completed);
                progress.SetFileProgress(skippedDone, clientFiles.Count);
                return;
            }

            await DownloadIndexFileAsync(file, destPath, progress, token);

            var done = Interlocked.Increment(ref completed);
            progress.SetFileProgress(done, clientFiles.Count);

            if (done % 25 == 0 || done == clientFiles.Count)
                await progress.LogAsync("info", $"Модпак: скачано {done}/{clientFiles.Count}", token);
        });

        // Overrides are applied last so pack-provided configs win over downloaded files.
        progress.SetStep("Применение overrides модпака");
        var overridesApplied = ApplyOverrides(stagingPath, buildDir, overrideRoots, ct);
        await progress.LogAsync("info", $"Применено файлов из overrides: {overridesApplied}", ct);
    }

    /// <summary>Downloads one index file, trying every mirror and verifying its hash.</summary>
    private async Task DownloadIndexFileAsync(
        ModrinthIndexFile file,
        string destPath,
        BuildProgressReporter progress,
        CancellationToken ct)
    {
        var urls = new List<string>();
        if (file.Downloads != null)
            urls.AddRange(file.Downloads.Where(u => !string.IsNullOrWhiteSpace(u)));

        // Modrinth-hosted files may ship an empty downloads array; the direct CDN link is
        // then resolved from the file's sha1 through /v2/version_file/{hash}.
        if (urls.Count == 0 && file.Sha1 != null)
        {
            var resolved = await ResolveModrinthUrlByHashAsync(file.Sha1, ct);
            if (resolved != null) urls.Add(resolved);
        }

        if (urls.Count == 0)
            throw new ModpackImportException($"No download URL available for index file '{file.Path}'");

        Exception? lastError = null;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadVerifiedAsync(url, destPath, file.Hashes, file.FileSize, progress, file.Path, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ModpackImportException)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Download attempt failed for '{Path}' from {Url}", file.Path, url);
            }
        }

        throw new ModpackImportException(
            $"Failed to download '{file.Path}' ({urls.Count} URL(s) tried): {lastError?.Message}", lastError!);
    }

    /// <summary>Streams a URL to disk (via *.part) with SHA-512/SHA-1 verification.</summary>
    private async Task DownloadVerifiedAsync(
        string url,
        string destPath,
        IReadOnlyDictionary<string, string>? hashes,
        long? expectedSize,
        BuildProgressReporter? progress,
        string displayPath,
        CancellationToken ct)
    {
        var expectedSha512 = hashes != null && hashes.TryGetValue("sha512", out var s512) ? s512 : null;
        var expectedSha1 = hashes != null && hashes.TryGetValue("sha1", out var s1) ? s1 : null;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var partPath = destPath + ".part";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = DownloadTimeout;

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxFileBytes)
            throw new ModpackImportException($"Remote file is too large: {contentLength} bytes");

        long written;
        string? computedHash = null;

        using (var hasher = CreateHasher(expectedSha512, expectedSha1))
        {
            {
                await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(
                    partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                written = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    hasher?.AppendData(buffer, 0, read);
                    written += read;
                    progress?.AddParallelBytes(read, displayPath);
                }

                await fileStream.FlushAsync(ct);
            }

            if (hasher != null)
            {
                computedHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
        }

        if (computedHash != null)
        {
            var expected = (expectedSha512 ?? expectedSha1!).Trim().ToLowerInvariant();
            if (!string.Equals(computedHash, expected, StringComparison.Ordinal))
            {
                TryDelete(partPath);
                throw new ModpackImportException(
                    $"Checksum mismatch for '{displayPath}': expected {expected}, got {computedHash}");
            }
        }
        else if (expectedSize is > 0 && written != expectedSize.Value)
        {
            // No hashes to compare - fall back to the declared size.
            TryDelete(partPath);
            throw new ModpackImportException(
                $"Size mismatch for '{displayPath}': expected {expectedSize}, got {written}");
        }

        File.Move(partPath, destPath, overwrite: true);
    }

    /// <summary>Resolves a direct CDN url for a Modrinth-hosted file by its sha1.</summary>
    private async Task<string?> ResolveModrinthUrlByHashAsync(string sha1, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://api.modrinth.com/v2/version_file/{sha1}?algorithm=sha1";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Modrinth rate limit exceeded", null, HttpStatusCode.TooManyRequests);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.TryGetProperty("url", out var urlProp) &&
                        urlProp.ValueKind == JsonValueKind.String)
                    {
                        var value = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve Modrinth URL by sha1 {Sha1}", sha1);
            return null;
        }
    }

    /// <summary>
    /// Copies the pack's override folders into the build directory in the given order, so a
    /// later root (e.g. client-overrides/) wins over an earlier one (overrides/) regardless of
    /// entry order inside the archive. Zip-slip safe.
    /// </summary>
    private int ApplyOverrides(string archivePath, string buildDir, string[] overrideRoots, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var count = 0;

        foreach (var prefix in overrideRoots)
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = name[prefix.Length..];
                if (relative.Length == 0) continue;

                if (relative.EndsWith('/'))
                {
                    Directory.CreateDirectory(SafeResolvePath(buildDir, relative));
                    continue;
                }

                var destPath = SafeResolvePath(buildDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                count++;
            }
        }

        return count;
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    /// <summary>Resolves a modpack-relative path inside <paramref name="root"/>, rejecting traversal.</summary>
    private static string SafeResolvePath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0)
            throw new ModpackImportException("Empty path in modpack");

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new ModpackImportException($"Unsafe path in modpack: '{relativePath}'");

        return combined;
    }

    private static void ValidateRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var hasTraversal = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
        if (normalized.StartsWith('/') || hasTraversal || Path.IsPathRooted(relativePath))
        {
            throw new ModpackImportException($"Unsafe path in modpack: '{relativePath}'");
        }
    }

    private static IncrementalHash? CreateHasher(string? sha512, string? sha1)
    {
        if (sha512 != null) return IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        if (sha1 != null) return IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        return null;
    }

    /// <summary>True when the file on disk already matches one of the expected hashes.</summary>
    private static async Task<bool> MatchesHashAsync(string path, string? sha512, string? sha1, CancellationToken ct)
    {
        var expected = (sha512 ?? sha1)?.Trim().ToLowerInvariant();
        if (expected == null) return false;

        using var hasher = sha512 != null
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512)
            : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        await using var stream = File.OpenRead(path);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }

        var computed = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return string.Equals(computed, expected, StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

/// <summary>
/// What a staged modpack archive actually contains. Two formats are supported so packs can be
/// imported straight from either store: Modrinth .mrpack and the CurseForge pack zip.
/// </summary>
public sealed class ArchiveDescriptor
{
    /// <summary><see cref="ModpackImportService.FormatModrinth"/> or <see cref="ModpackImportService.FormatCurseForge"/>.</summary>
    public string Format { get; init; } = ModpackImportService.FormatModrinth;

    /// <summary>
    /// Modrinth index. Complete (with files) for .mrpack; for CurseForge it carries the pack
    /// name and engine dependencies only, until the build resolves every id.
    /// </summary>
    public ModrinthIndex Index { get; init; } = null!;

    /// <summary>Parsed CurseForge manifest (format == FormatCurseForge only).</summary>
    public CurseForgeManifest? CurseForge { get; init; }
}

/// <summary>A staged modpack archive together with its parsed manifest.</summary>
public sealed class StagedModpack
{
    /// <summary>Build the archive is staged for (staging file is {buildId}.mrpack).</summary>
    public string BuildId { get; init; } = string.Empty;

    /// <summary>Absolute path of the staged archive.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Parsed manifest projected into the shared index shape.</summary>
    public ModrinthIndex Index { get; init; } = null!;

    /// <summary>Original (downloaded or uploaded) file name.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary><see cref="ModpackImportService.FormatModrinth"/> or <see cref="ModpackImportService.FormatCurseForge"/>.</summary>
    public string Format { get; init; } = ModpackImportService.FormatModrinth;

    /// <summary>Parsed CurseForge manifest, when the archive came from CurseForge.</summary>
    public CurseForgeManifest? CurseForge { get; init; }

    /// <summary>Modrinth version id the archive came from (Modrinth source only).</summary>
    public string? VersionId { get; init; }

    /// <summary>Human readable version label reported by Modrinth.</summary>
    public string? VersionLabel { get; init; }
}
