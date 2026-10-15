using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Generates integrity manifests for build instances and manages file hashing.
    /// </summary>
    public class BuildManifestService
    {
        private readonly AppDbContext _context;
        private readonly S3StorageService _s3Storage;
        private readonly IConfiguration _config;
        private readonly ILogger<BuildManifestService> _logger;

        // Policy classification rules
        /// <summary>Concurrent SHA-256 hashing lanes (disk + CPU bound).</summary>
        private const int MaxHashInParallel = 8;

        /// <summary>Concurrent S3 uploads per build (each upload is two HTTP round-trips).</summary>
        private const int MaxS3UploadsInParallel = 8;

        private static readonly HashSet<string> PreserveDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "saves", "screenshots", "logs",
            "crash-reports", "backups", "local", "emblems", "playerdata", "stats",
            "advancements", "realms"
        };

        private static readonly HashSet<string> PreserveFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "servers.dat", "usercache.json", "usernamecache.json"
        };

        // Launcher / player preferences: shipped once, never overwrite local edits.
        private static readonly HashSet<string> DownloadOnceFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "options.txt", "launcher_profiles.json"
        };

        private static readonly HashSet<string> ForceSyncDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "libraries", "runtime", "versions", "assets", "natives", "mods",
            // Content directories added by build presets and modpack imports: they are part
            // of the build and must always reach the client.
            "shaderpacks", "resourcepacks"
        };

        public BuildManifestService(
            AppDbContext context,
            S3StorageService s3Storage,
            IConfiguration config,
            ILogger<BuildManifestService> logger)
        {
            _context = context;
            _s3Storage = s3Storage;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Finalizes a build: scans directory, hashes files, uploads to S3, generates manifest.
        /// </summary>
        public async Task<ClientManifestResponse> FinalizeBuildAsync(
            long buildInstanceId, string buildDir, CancellationToken ct = default,
            BuildProgressReporter? progress = null)
        {
            var build = await _context.BuildInstances
                .Include(b => b.Files)
                .FirstOrDefaultAsync(b => b.Id == buildInstanceId, ct)
                ?? throw new InvalidOperationException("Build not found");

            if (!Directory.Exists(buildDir))
            {
                throw new InvalidOperationException($"Build directory not found: {buildDir}");
            }

            _logger.LogInformation("Finalizing build {BuildId} at {BuildDir}", build.BuildId, buildDir);

            // 1. Scan and hash all files
            progress?.SetStep("Сканирование и хеширование файлов");
            if (progress != null) await progress.FlushAsync(ct);
            var files = await ScanAndHashDirectoryAsync(buildDir, build.BuildId, progress, ct);

            // 2. Upload to S3 with deduplication
            // The bucket may not exist yet on a fresh environment - creating it here also
            // applies the public-read policy required by the launcher.
            await _s3Storage.EnsureBucketAsync(ct);

            var baseUrl = _config["S3:CdnBaseUrl"]?.TrimEnd('/') ?? "";
            var manifestFiles = new List<ClientManifestFile>();
            var dbFiles = new List<BuildFile>();

            progress?.SetStep($"Загрузка в S3 ({files.Count} файлов)");
            if (progress != null) await progress.FlushAsync(ct);

            // Upload to S3 in parallel: each file costs two HTTP round-trips (dedup stat +
            // put), so a sequential loop throttles the build to one file at a time. Results
            // are collected per-index to keep the manifest ordering deterministic.
            var uploadedCount = 0;
            var manifestByIndex = new ClientManifestFile[files.Count];
            var dbByIndex = new BuildFile[files.Count];

            using (var semaphore = new SemaphoreSlim(MaxS3UploadsInParallel))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var linkedCt = linkedCts.Token;
                var tasks = new List<Task>(files.Count);

                for (var idx = 0; idx < files.Count; idx++)
                {
                    var file = files[idx];
                    var index = idx; // for-loop variable is shared; capture a per-iteration copy
                    await semaphore.WaitAsync(linkedCt);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var s3Key = $"builds/{build.BuildId}/{file.Path}";
                            await _s3Storage.UploadFileAsync(file.LocalPath, s3Key, file.Sha256, linkedCt);

                            manifestByIndex[index] = new ClientManifestFile
                            {
                                Path = file.Path,
                                Sha256 = file.Sha256,
                                Size = file.Size,
                                Policy = file.Policy,
                                Url = _s3Storage.GetPublicUrl(s3Key)
                            };

                            dbByIndex[index] = new BuildFile
                            {
                                BuildInstanceId = build.Id,
                                Path = file.Path,
                                Sha256 = file.Sha256,
                                Size = file.Size,
                                Policy = file.Policy,
                                S3Key = s3Key
                            };

                            var done = Interlocked.Increment(ref uploadedCount);
                            progress?.SetFileProgress(done, files.Count);
                            if (progress != null && (done % 25 == 0 || done == files.Count))
                                await progress.LogAsync("info", $"Выгружено в S3: {done}/{files.Count}", linkedCt);
                        }
                        catch (Exception ex)
                        {
                            // Log the root failure before cancelling siblings so the true cause is visible.
                            _logger.LogError(ex, "Failed to upload file {Path} to S3 (index {Index})", file.Path, index);
                            linkedCts.Cancel();
                            throw;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, linkedCt));
                }

                await Task.WhenAll(tasks);
            }

            manifestFiles.AddRange(manifestByIndex);
            dbFiles.AddRange(dbByIndex);

            progress?.SetStep("Генерация манифеста");

            // The launch metadata (java version, main class, JVM/game args) is derived from the
            // version JSONs the engine downloader left in the build directory. This keeps the
            // manifest correct for every loader instead of hardcoding Forge/Fabric values.
            var launchInfo = ResolveLaunchInfo(buildDir, build);

            var manifest = new ClientManifestResponse
            {
                BuildId = build.BuildId,
                DisplayName = build.Name,
                GameVersion = build.McVersion,
                LoaderType = build.LoaderType,
                LoaderVersion = build.LoaderVersion,
                JavaVersion = ResolveJavaVersion(buildDir, build),
                MainClass = launchInfo.MainClass,
                JvmArgs = launchInfo.JvmArgs,
                GameArgs = launchInfo.GameArgs,
                BaseUrl = baseUrl,
                Files = manifestFiles
            };

            // 4. Save to database
            build.ManifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            build.Files.Clear();
            foreach (var f in dbFiles)
                build.Files.Add(f);
            build.Status = "completed";
            build.CompletedAt = DateTime.UtcNow;
            build.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Build {BuildId} finalized. Files: {Count}, Uploaded: {Uploaded}",
                build.BuildId, files.Count, files.Count);

            return manifest;
        }

        /// <summary>
        /// Scans a directory and computes SHA-256 for all files.
        /// </summary>
        private async Task<List<ScanResult>> ScanAndHashDirectoryAsync(
            string buildDir, string buildId, BuildProgressReporter? progress, CancellationToken ct)
        {
            var allFiles = Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Hashing thousands of small files one-by-one wastes the disk: SHA-256 of a
            // 5 KB asset finishes long before the next sequential read even starts. Lanes
            // run in parallel; the bag preserves no particular order, but the manifest
            // consumer only needs path-keyed entries, so that is fine.
            var results = new System.Collections.Concurrent.ConcurrentBag<ScanResult>();
            var hashed = 0;

            using (var semaphore = new SemaphoreSlim(MaxHashInParallel))
            {
                var tasks = new List<Task>(allFiles.Count);

                foreach (var file in allFiles)
                {
                    await semaphore.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            ct.ThrowIfCancellationRequested();

                            var relativePath = Path.GetRelativePath(buildDir, file).Replace('\\', '/');
                            var fileInfo = new FileInfo(file);
                            var policy = ClassifyFile(relativePath);

                            // Every file (including PRESERVE ones such as saved games) is listed in
                            // the manifest. Without a PRESERVE entry the client cannot tell a user
                            // file apart from a stale one and would prune it. PRESERVE entries are
                            // never overwritten or deleted by the client, only restored if missing.
                            string sha256;
                            using (var sha = SHA256.Create())
                            await using (var stream = File.OpenRead(file))
                            {
                                var hash = await sha.ComputeHashAsync(stream, ct);
                                sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                            }

                            results.Add(new ScanResult
                            {
                                LocalPath = file,
                                Path = relativePath,
                                Sha256 = sha256,
                                Size = fileInfo.Length,
                                Policy = policy
                            });

                            var done = Interlocked.Increment(ref hashed);
                            progress?.SetFileProgress(done, allFiles.Count);
                            progress?.SetCurrentFile(file);
                            if (progress != null && (done % 50 == 0 || done == allFiles.Count))
                                await progress.LogAsync("info", $"Хеширование: {done}/{allFiles.Count}", ct);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks);
            }

            return results.ToList();
        }

        private string ClassifyFile(string relativePath)
        {
            var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return "FORCE_SYNC";

            var firstDir = segments[0];
            var fileName = segments[^1];

            // Preferences first: options.txt / launcher profiles may live in any directory
            // (build root or a modpack override) and must never clobber local edits.
            if (DownloadOnceFiles.Contains(fileName)) return "DOWNLOAD_ONCE";

            // Engine data and content added by presets / modpack imports (mods, packs, configs).
            if (ForceSyncDirs.Contains(firstDir)) return "FORCE_SYNC";
            if (firstDir == "config" || firstDir == "defaultconfigs") return "FORCE_SYNC";

            // Per-user data the launcher must not touch.
            if (PreserveDirs.Contains(firstDir)) return "PRESERVE";
            if (PreserveFiles.Contains(fileName)) return "PRESERVE";

            return "FORCE_SYNC";
        }

        /// <summary>
        /// Java major version required by the build. Read from the vanilla version JSON that the
        /// engine downloader stored in the build ("javaVersion.majorVersion"), falling back to a
        /// coarse mapping by game version when the file is unavailable.
        /// </summary>
        private int ResolveJavaVersion(string buildDir, BuildInstance build)
        {
            try
            {
                var versionJson = Path.Combine(buildDir, "versions", build.McVersion, $"{build.McVersion}.json");
                if (File.Exists(versionJson))
                {
                    var node = JsonNode.Parse(File.ReadAllText(versionJson));
                    var major = node?["javaVersion"]?["majorVersion"]?.GetValue<int>();
                    if (major is > 0) return major.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read javaVersion from version JSON for {McVersion}", build.McVersion);
            }

            return GetJavaVersionForMc(build.McVersion);
        }

        /// <summary>
        /// Rough Java requirement by Minecraft version (1.20.5+ needs 21, 1.18-1.20.4 need 17,
        /// 1.17 needs 16, everything older runs on 8).
        /// </summary>
        private static int GetJavaVersionForMc(string mcVersion)
        {
            var parts = mcVersion.Split('.');
            int major = 1, minor = 0, patch = 0;
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            if (parts.Length > 2) int.TryParse(parts[2], out patch);

            if (major > 1) return 21;

            return (minor, patch) switch
            {
                ( >= 21, _) => 21,
                (20, >= 5) => 21,
                ( >= 18, _) => 17,
                (17, _) => 16,
                _ => 8
            };
        }

        /// <summary>
        /// Resolves the launch metadata from the loader version JSON the engine downloader wrote
        /// into the build (Fabric profile, Forge/NeoForge installer profile). Falls back to the
        /// built-in defaults when no loader profile is present (vanilla) or it is unreadable.
        /// </summary>
        private LaunchInfo ResolveLaunchInfo(string buildDir, BuildInstance build)
        {
            var info = new LaunchInfo
            {
                MainClass = GetMainClass(build.LoaderType),
                JvmArgs = GetDefaultJvmArgs(),
                GameArgs = GetDefaultGameArgs()
            };

            var versionJsonPath = ResolveLoaderVersionJson(buildDir, build);
            if (versionJsonPath == null) return info;

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(versionJsonPath));
                if (node == null) return info;

                var mainClass = node["mainClass"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(mainClass)) info.MainClass = mainClass!;

                var jvm = ReadStringArray(node["arguments"]?["jvm"]);
                var game = ReadStringArray(node["arguments"]?["game"]);
                var legacyGame = node["minecraftArguments"]?.GetValue<string>();
                var inheritsFrom = node["inheritsFrom"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(inheritsFrom))
                {
                    // Overlay profile (Fabric and friends): it only adds a few arguments on top of
                    // the vanilla client. Keep the safe defaults and append the loader's extras
                    // instead of replacing them with an incomplete argument list.
                    if (jvm.Count > 0) info.JvmArgs.AddRange(jvm);
                    if (game.Count > 0) info.GameArgs.AddRange(game);
                }
                else
                {
                    // Standalone profile (Forge/NeoForge installers): it ships a complete set of
                    // argument templates with placeholders such as ${library_directory}.
                    if (jvm.Count > 0) info.JvmArgs = jvm;
                    if (game.Count > 0) info.GameArgs = game;
                    else if (!string.IsNullOrWhiteSpace(legacyGame))
                        info.GameArgs = legacyGame!.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                _logger.LogInformation("Launch info resolved from {Path}: mainClass={MainClass}", versionJsonPath, info.MainClass);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse loader version JSON {Path}", versionJsonPath);
            }

            return info;
        }

        private static List<string> ReadStringArray(JsonNode? node)
        {
            var result = new List<string>();
            if (node is not JsonArray array) return result;

            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var s))
                    result.Add(s);
            }

            return result;
        }

        /// <summary>
        /// Finds the JSON profile describing the loader, written by the engine downloader into
        /// "versions/&lt;profile&gt;/&lt;profile&gt;.json". Fabric uses "fabric-loader-&lt;ver&gt;-&lt;mc&gt;",
        /// Forge/NeoForge use names produced by their installers, so the directory is matched by
        /// the loader name and (when pinned) the loader version.
        /// </summary>
        private static string? ResolveLoaderVersionJson(string buildDir, BuildInstance build)
        {
            var loader = build.LoaderType?.ToLowerInvariant();
            if (string.IsNullOrEmpty(loader) || loader == "vanilla") return null;

            var versionsDir = Path.Combine(buildDir, "versions");
            if (!Directory.Exists(versionsDir)) return null;

            var loaderVersion = build.LoaderVersion ?? string.Empty;
            var candidates = Directory.EnumerateDirectories(versionsDir)
                .Select(Path.GetFileName)
                .Where(name => name != null && name.Contains(loader, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prefer the profile whose folder also names the pinned loader version.
            var match = candidates.FirstOrDefault(name =>
                            !string.IsNullOrEmpty(loaderVersion) &&
                            name!.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault();

            if (match == null) return null;

            var json = Path.Combine(versionsDir, match, match + ".json");
            return File.Exists(json) ? json : null;
        }

        private static string GetMainClass(string? loaderType) => (loaderType ?? string.Empty).ToLowerInvariant() switch
        {
            "forge" => "net.minecraftforge.bootstrap.ForgeBootstrap",
            "neoforge" => "net.neoforged.bootstrap.NeoForgeBootstrap",
            "fabric" => "net.fabricmc.loader.impl.launch.knot.KnotClient",
            _ => "net.minecraft.client.main.Main"
        };

        private static List<string> GetDefaultJvmArgs() => new()
        {
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=200",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC",
            "-XX:G1NewSizePercent=30",
            "-XX:G1MaxNewSizePercent=40",
            "-XX:G1HeapRegionSize=8M",
            "-XX:G1ReservePercent=20",
            "-XX:G1HeapWastePercent=5",
            "-XX:G1MixedGCCountTarget=4",
            "-XX:InitiatingHeapOccupancyPercent=15",
            "-XX:G1MixedGCLiveThresholdPercent=90",
            "-XX:G1RSetUpdatingPauseTimePercent=5",
            "-XX:SurvivorRatio=32",
            "-XX:+PerfDisableSharedMem",
            "-XX:MaxTenuringThreshold=1"
        };

        private static List<string> GetDefaultGameArgs() => new()
        {
            "--username", "${username}",
            "--version", "${version}",
            "--gameDir", "${gameDir}",
            "--assetsDir", "${assetsDir}",
            "--assetIndex", "${assetIndex}",
            "--uuid", "${uuid}",
            "--accessToken", "${accessToken}",
            "--userType", "${userType}",
            "--versionType", "${versionType}"
        };

        private class ScanResult
        {
            public string LocalPath { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
            public long Size { get; set; }
            public string Policy { get; set; } = string.Empty;
        }

        /// <summary>
        /// Launch metadata extracted from a loader version profile.
        /// </summary>
        private class LaunchInfo
        {
            public string MainClass { get; set; } = string.Empty;
            public List<string> JvmArgs { get; set; } = new();
            public List<string> GameArgs { get; set; } = new();
        }
    }
}
