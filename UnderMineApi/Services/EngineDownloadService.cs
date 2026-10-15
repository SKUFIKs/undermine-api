using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Downloads vanilla Minecraft components, Fabric libraries, and runs headless Forge/NeoForge installers.
    /// </summary>
    public class EngineDownloadService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EngineDownloadService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly VersionCatalogService _versionCatalog;

        public EngineDownloadService(
            IConfiguration config,
            ILogger<EngineDownloadService> logger,
            IHttpClientFactory httpClientFactory,
            VersionCatalogService versionCatalog)
        {
            _config = config;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _versionCatalog = versionCatalog;
        }

        /// <summary>
        /// Downloads all engine files to the target directory.
        /// </summary>
        public async Task<EngineDownloadResult> DownloadEngineAsync(
            string mcVersion,
            string loaderType,
            string? loaderVersion,
            string targetDir,
            CancellationToken ct = default,
            BuildProgressReporter? progress = null)
        {
            var result = new EngineDownloadResult();
            var httpClient = _httpClientFactory.CreateClient();

            // 1. Download vanilla client
            var vanillaResult = await DownloadVanillaAsync(httpClient, mcVersion, targetDir, ct, progress);
            result.MainClass = vanillaResult.MainClass;
            result.JvmArgs = vanillaResult.JvmArgs;
            result.GameArgs = vanillaResult.GameArgs;

            // 2. Apply loader-specific patches
            switch (loaderType.ToLowerInvariant())
            {
                case "fabric":
                    await ApplyFabricAsync(httpClient, mcVersion, loaderVersion, targetDir, ct, progress);
                    break;
                case "neoforge":
                    var neoResult = await ApplyNeoForgeAsync(httpClient, mcVersion, loaderVersion, targetDir, ct, progress);
                    result.MainClass = neoResult.MainClass;
                    result.JvmArgs = neoResult.JvmArgs;
                    result.GameArgs = neoResult.GameArgs;
                    break;
                case "forge":
                    var forgeResult = await ApplyForgeAsync(httpClient, mcVersion, loaderVersion, targetDir, ct, progress);
                    result.MainClass = forgeResult.MainClass;
                    result.JvmArgs = forgeResult.JvmArgs;
                    result.GameArgs = forgeResult.GameArgs;
                    break;
            }

            return result;
        }

        private async Task<VanillaDownloadResult> DownloadVanillaAsync(
            HttpClient httpClient, string mcVersion, string targetDir, CancellationToken ct,
            BuildProgressReporter? progress = null)
        {
            _logger.LogInformation("Downloading vanilla {McVersion}...", mcVersion);

            // Fetch version manifest
            var versionListUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            progress?.SetStep("Запрос списка версий Minecraft");
            var versionList = await httpClient.GetFromJsonAsync<JsonNode>(versionListUrl, ct)
                ?? throw new InvalidOperationException("Failed to fetch version manifest");

            var versionUrl = versionList["versions"]?.AsArray()
                .FirstOrDefault(v => v?["id"]?.GetValue<string>() == mcVersion)?["url"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"Version {mcVersion} not found");

            var versionJson = await httpClient.GetFromJsonAsync<JsonNode>(versionUrl, ct)
                ?? throw new InvalidOperationException("Failed to fetch version JSON");

            // Create directories
            var versionsDir = Path.Combine(targetDir, "versions", mcVersion);
            var librariesDir = Path.Combine(targetDir, "libraries");
            var assetsDir = Path.Combine(targetDir, "assets");
            Directory.CreateDirectory(versionsDir);
            Directory.CreateDirectory(librariesDir);
            Directory.CreateDirectory(assetsDir);

            // Download client.jar
            var clientUrl = versionJson["downloads"]?["client"]?["url"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Client download URL not found");
            var clientPath = Path.Combine(versionsDir, $"{mcVersion}.jar");
            progress?.SetStep($"Скачивание клиента {mcVersion} (client.jar)");
            if (progress != null) await progress.FlushAsync(ct);
            await DownloadFileAsync(httpClient, clientUrl, clientPath, ct, progress, $"versions/{mcVersion}.jar", logFile: true);

            // Version metadata (classpath rules, asset index id, ...). Kept next to the client
            // jar in the standard layout so the launcher can resolve ${assetIndex} and friends.
            progress?.SetStep($"Скачивание метаданных {mcVersion}");
            await DownloadFileAsync(httpClient, versionUrl, Path.Combine(versionsDir, $"{mcVersion}.json"), ct, progress, $"versions/{mcVersion}.json", logFile: true);

            // Download libraries
            var libraries = versionJson["libraries"]?.AsArray() ?? new JsonArray();
            progress?.SetStep($"Скачивание библиотек ({libraries.Count})");
            if (progress != null) await progress.FlushAsync(ct);

            var libraryFiles = new List<(string Url, string Path)>();
            foreach (var lib in libraries)
            {
                var downloads = lib?["downloads"];
                var artifact = downloads?["artifact"];
                var url = artifact?["url"]?.GetValue<string>();
                var path = artifact?["path"]?.GetValue<string>();

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
                    continue;

                var libPath = Path.Combine(librariesDir, path.Replace('/', Path.DirectorySeparatorChar));
                libraryFiles.Add((url, libPath));
            }

            await DownloadFilesParallelAsync(httpClient, libraryFiles, ct, progress, maxParallel: 12);

            // Download assets index
            var assetIndex = versionJson["assetIndex"];
            var assetIndexUrl = assetIndex?["url"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Asset index URL not found");

            var assetIndexPath = Path.Combine(assetsDir, "indexes", $"{mcVersion}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(assetIndexPath)!);
            progress?.SetStep($"Скачивание индекса ассетов {mcVersion}");
            await DownloadFileAsync(httpClient, assetIndexUrl, assetIndexPath, ct, progress, $"assets/indexes/{mcVersion}.json", logFile: true);

            // Parse asset index and download assets
            var assetIndexJson = await httpClient.GetFromJsonAsync<JsonNode>(assetIndexUrl, ct);
            var objects = assetIndexJson?["objects"]?.AsObject();

            if (objects != null)
            {
                progress?.SetStep($"Скачивание ассетов ({objects.Count} файлов)");
                if (progress != null) await progress.FlushAsync(ct);

                var assetFiles = new List<(string Url, string Path)>(objects.Count);
                foreach (var prop in objects)
                {
                    var hash = prop.Value?["hash"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(hash)) continue;

                    var assetUrl = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
                    var assetPath = Path.Combine(assetsDir, "objects", hash[..2], hash);
                    assetFiles.Add((assetUrl, assetPath));
                }

                await DownloadFilesParallelAsync(httpClient, assetFiles, ct, progress, maxParallel: 16);
            }

            // Extract launch info
            var mainClass = versionJson["mainClass"]?.GetValue<string>() ?? "net.minecraft.client.main.Main";
            var jvmArgs = new List<string>();
            var gameArgs = new List<string>();

            // Parse arguments
            var arguments = versionJson["arguments"];
            if (arguments?["jvm"] is JsonArray jvmArray)
            {
                foreach (var arg in jvmArray)
                {
                    if (arg is JsonValue jv && jv.TryGetValue<string>(out var s))
                        jvmArgs.Add(s);
                }
            }

            if (arguments?["game"] is JsonArray gameArray)
            {
                foreach (var arg in gameArray)
                {
                    if (arg is JsonValue jv && jv.TryGetValue<string>(out var s))
                        gameArgs.Add(s);
                }
            }

            _logger.LogInformation("Vanilla {McVersion} downloaded. Main class: {MainClass}", mcVersion, mainClass);

            return new VanillaDownloadResult
            {
                MainClass = mainClass,
                JvmArgs = jvmArgs,
                GameArgs = gameArgs
            };
        }

        private async Task ApplyFabricAsync(
            HttpClient httpClient, string mcVersion, string? loaderVersion, string targetDir, CancellationToken ct,
            BuildProgressReporter? progress = null)
        {
            _logger.LogInformation("Applying Fabric for {McVersion}...", mcVersion);

            var loaderVer = loaderVersion;
            if (string.IsNullOrEmpty(loaderVer))
            {
                // No version pinned: take the newest stable Fabric loader for this game version.
                loaderVer = await _versionCatalog.GetLatestLoaderVersionAsync("fabric", mcVersion, ct)
                    ?? throw new InvalidOperationException($"No Fabric loader version found for {mcVersion}");
            }

            var profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVer}/profile/json";

            progress?.SetStep($"Применение Fabric loader {loaderVer}");
            if (progress != null) await progress.FlushAsync(ct);

            var profileJson = await httpClient.GetFromJsonAsync<JsonNode>(profileUrl, ct)
                ?? throw new InvalidOperationException("Failed to fetch Fabric profile");

            var librariesDir = Path.Combine(targetDir, "libraries");
            var libraries = profileJson["libraries"]?.AsArray() ?? new JsonArray();

            var fabricLibFiles = new List<(string Url, string Path)>();
            foreach (var lib in libraries)
            {
                var name = lib?["name"]?.GetValue<string>();
                var url = lib?["url"]?.GetValue<string>() ?? "";

                if (string.IsNullOrEmpty(name)) continue;

                var path = NameToPath(name);
                if (string.IsNullOrEmpty(path)) continue;

                var libPath = Path.Combine(librariesDir, path);
                var fullUrl = string.IsNullOrEmpty(url) ? $"https://maven.fabricmc.net/{path}" : $"{url}/{path}";
                fabricLibFiles.Add((fullUrl, libPath));
            }

            await DownloadFilesParallelAsync(httpClient, fabricLibFiles, ct, progress, maxParallel: 8);

            // Update version.json with Fabric main class
            var fabricVersion = profileJson["id"]?.GetValue<string>() ?? $"fabric-loader-{loaderVer}-{mcVersion}";
            var mainClass = profileJson["mainClass"]?.GetValue<string>() ?? "net.fabricmc.loader.impl.launch.knot.KnotClient";

            // Persist the Fabric profile as the version JSON in the standard layout so the
            // launcher can identify the Fabric game version (versions/<id>/<id>.json).
            var fabricVersionDir = Path.Combine(targetDir, "versions", fabricVersion);
            Directory.CreateDirectory(fabricVersionDir);
            await File.WriteAllTextAsync(
                Path.Combine(fabricVersionDir, $"{fabricVersion}.json"),
                profileJson.ToJsonString(),
                ct);

            _logger.LogInformation("Fabric applied. Version: {Version}, Main class: {MainClass}", fabricVersion, mainClass);
        }

        private async Task<LoaderResult> ApplyForgeAsync(
            HttpClient httpClient, string mcVersion, string? loaderVersion, string targetDir, CancellationToken ct,
            BuildProgressReporter? progress = null)
        {
            _logger.LogInformation("Applying Forge for {McVersion}...", mcVersion);

            var forgeVersion = loaderVersion ?? await GetLatestForgeVersionAsync(mcVersion, ct);
            if (string.IsNullOrEmpty(forgeVersion))
                throw new InvalidOperationException($"No Forge version found for Minecraft {mcVersion}");
            var installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";

            var tempDir = Path.Combine(Path.GetTempPath(), "forge-installer", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Download installer
                var installerPath = Path.Combine(tempDir, "installer.jar");
                progress?.SetStep($"Скачивание Forge installer {forgeVersion}");
                if (progress != null) await progress.FlushAsync(ct);
                await DownloadFileAsync(httpClient, installerUrl, installerPath, ct, progress, $"forge-{mcVersion}-{forgeVersion}-installer.jar", logFile: true);

                // Run headless installer
                progress?.SetStep("Запуск Forge installer (headless)…");
                if (progress != null) await progress.FlushAsync(ct);
                await RunInstallerAsync(installerPath, targetDir, tempDir, "Forge", ct, progress);

                // Read generated version.json. The installer names the profile
                // "<mc>-forge-<ver>" (e.g. "1.21.1-forge-52.1.19"); older installers used
                // "forge-<mc>-<ver>", so resolve it instead of hardcoding one layout.
                var versionJsonPath = ResolveLoaderVersionJson(targetDir, "forge", mcVersion, forgeVersion);
                if (versionJsonPath != null)
                {
                    var json = await File.ReadAllTextAsync(versionJsonPath, ct);
                    var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var mainClass = root.GetProperty("mainClass").GetString() ?? "net.minecraft.launchwrapper.Launch";
                    var jvmArgs = new List<string>();
                    var gameArgs = new List<string>();

                    if (root.TryGetProperty("arguments", out var args))
                    {
                        if (args.TryGetProperty("jvm", out var jvmArr))
                            foreach (var a in jvmArr.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String)
                                    jvmArgs.Add(a.GetString()!);

                        if (args.TryGetProperty("game", out var gameArr))
                            foreach (var a in gameArr.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String)
                                    gameArgs.Add(a.GetString()!);
                    }

                    _logger.LogInformation("Forge {ForgeVersion} applied. Main class: {MainClass}", forgeVersion, mainClass);

                    return new LoaderResult { MainClass = mainClass, JvmArgs = jvmArgs, GameArgs = gameArgs };
                }

                throw new InvalidOperationException("Forge version.json not found after installation");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private async Task<LoaderResult> ApplyNeoForgeAsync(
            HttpClient httpClient, string mcVersion, string? loaderVersion, string targetDir, CancellationToken ct,
            BuildProgressReporter? progress = null)
        {
            _logger.LogInformation("Applying NeoForge for {McVersion}...", mcVersion);

            var neoVersion = loaderVersion ?? await GetLatestNeoForgeVersionAsync(mcVersion, ct);
            if (string.IsNullOrEmpty(neoVersion))
                throw new InvalidOperationException($"No NeoForge version found for Minecraft {mcVersion}");
            var installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-installer.jar";

            var tempDir = Path.Combine(Path.GetTempPath(), "neoforge-installer", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Download installer
                var installerPath = Path.Combine(tempDir, "installer.jar");
                progress?.SetStep($"Скачивание NeoForge installer {neoVersion}");
                if (progress != null) await progress.FlushAsync(ct);
                await DownloadFileAsync(httpClient, installerUrl, installerPath, ct, progress, $"neoforge-{neoVersion}-installer.jar", logFile: true);

                // Run headless installer
                progress?.SetStep("Запуск NeoForge installer (headless)…");
                if (progress != null) await progress.FlushAsync(ct);
                await RunInstallerAsync(installerPath, targetDir, tempDir, "NeoForge", ct, progress);

                // Read generated version.json (profile naming varies between installer versions).
                var versionJsonPath = ResolveLoaderVersionJson(targetDir, "neoforge", mcVersion, neoVersion);
                if (versionJsonPath != null)
                {
                    var json = await File.ReadAllTextAsync(versionJsonPath, ct);
                    var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var mainClass = root.GetProperty("mainClass").GetString() ?? "net.neoforged.bootstrap.NeoForgeBootstrap";
                    var jvmArgs = new List<string>();
                    var gameArgs = new List<string>();

                    if (root.TryGetProperty("arguments", out var args))
                    {
                        if (args.TryGetProperty("jvm", out var jvmArr))
                            foreach (var a in jvmArr.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String)
                                    jvmArgs.Add(a.GetString()!);

                        if (args.TryGetProperty("game", out var gameArr))
                            foreach (var a in gameArr.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String)
                                    gameArgs.Add(a.GetString()!);
                    }

                    _logger.LogInformation("NeoForge {NeoVersion} applied. Main class: {MainClass}", neoVersion, mainClass);

                    return new LoaderResult { MainClass = mainClass, JvmArgs = jvmArgs, GameArgs = gameArgs };
                }

                throw new InvalidOperationException("NeoForge version.json not found after installation");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Newest Forge build for a game version. The version list (and its ordering) comes from
        /// the version catalog, which sorts numerically instead of relying on maven document order.
        /// </summary>
        private async Task<string?> GetLatestForgeVersionAsync(string mcVersion, CancellationToken ct)
        {
            var version = await _versionCatalog.GetLatestLoaderVersionAsync("forge", mcVersion, ct);
            if (string.IsNullOrEmpty(version))
                throw new InvalidOperationException($"No Forge version found for {mcVersion}");

            return version;
        }

        /// <summary>
        /// Newest NeoForge build for a game version. NeoForge versions do not contain the game
        /// version literally (1.20.2 -&gt; 20.2, 1.21 -&gt; 21.0), so the mapping lives in the catalog.
        /// </summary>
        private async Task<string?> GetLatestNeoForgeVersionAsync(string mcVersion, CancellationToken ct)
        {
            var version = await _versionCatalog.GetLatestLoaderVersionAsync("neoforge", mcVersion, ct);
            if (string.IsNullOrEmpty(version))
                throw new InvalidOperationException($"No NeoForge version found for {mcVersion}");

            return version;
        }

        private async Task DownloadFileAsync(
            HttpClient httpClient, string url, string path, CancellationToken ct,
            BuildProgressReporter? progress = null, string? label = null, bool logFile = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Remote paths are immutable (versioned jars / content-addressed assets), so a file
            // left over by a previous or interrupted build can be reused instead of re-downloaded.
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                _logger.LogDebug("Skipped (already downloaded): {Path}", path);
                if (progress != null && logFile)
                    await progress.LogAsync("info", $"Уже скачано, пропуск: {label ?? path}", ct);
                return;
            }

            var name = label ?? path;
            // Write to a temporary file first so an interrupted download is never mistaken
            // for a complete one by the skip check above (or hashed into the manifest).
            var tempPath = path + ".part";

            using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                try
                {
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    if (progress != null)
                        await progress.LogAsync("error", $"Ошибка загрузки: {name} — {ex.Message}", ct);
                    throw;
                }

                var expected = response.Content.Headers.ContentLength;
                if (progress != null)
                {
                    progress.BeginFile(name, expected);
                    if (logFile)
                        await progress.LogAsync("info", $"Скачивание: {name}{FormatSize(expected)}", ct);
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    long total = 0;
                    while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        total += read;
                        if (progress != null)
                        {
                            progress.ReportFileBytes(total);
                            // Throttled: keeps the panel live during long downloads without
                            // hammering the database from a tight copy loop.
                            await progress.FlushAsync(ct);
                        }
                    }

                    if (progress != null)
                        progress.EndFile(name, total, knownSize: expected.HasValue);
                }
            }

            File.Move(tempPath, path, overwrite: true);

            if (progress != null && logFile)
            {
                var size = new FileInfo(path).Length;
                await progress.LogAsync("info", $"Скачано: {name} ({FormatSize(size)})", ct);
            }

            _logger.LogDebug("Downloaded: {Path}", path);
        }

        private static string FormatSize(long? bytes)
        {
            if (bytes is null or <= 0) return "";
            if (bytes >= 1048576) return $"{bytes.Value / 1048576.0:0.0} MB";
            if (bytes >= 1024) return $"{bytes.Value / 1024.0:0.0} KB";
            return $"{bytes.Value} B";
        }

        /// <summary>
        /// Downloads many small files in parallel. Uses file-count progress (not bytes)
        /// because per-file byte tracking across concurrent tasks would require a more
        /// complex reporter; file-count progress is accurate and simpler.
        /// </summary>
        private async Task DownloadFilesParallelAsync(
            HttpClient httpClient,
            IReadOnlyList<(string Url, string Path)> files,
            CancellationToken ct,
            BuildProgressReporter? progress = null,
            int maxParallel = 16)
        {
            if (files.Count == 0) return;

            var done = 0;
            var total = files.Count;
            progress?.SetFileProgress(0, total);
            // Phase boundary: force a flush, the throttled FlushAsync may skip it if the
            // previous phase flushed less than FlushInterval ago.
            if (progress != null) await progress.FlushNowAsync(ct);

            using var semaphore = new SemaphoreSlim(maxParallel);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = linkedCts.Token;

            var tasks = new List<Task>(total);

            foreach (var (url, path) in files)
            {
                await semaphore.WaitAsync(linkedCt);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DownloadFileWithRetryAsync(httpClient, url, path, linkedCt);
                        var current = Interlocked.Increment(ref done);
                        progress?.SetFileProgress(current, total);
                        // Publish live progress periodically; the flush is throttled to
                        // once per second and single-flight, so this is cheap from N lanes.
                        if (progress != null && (current % 25 == 0 || current == total))
                            await progress.FlushAsync(linkedCt);
                    }
                    catch
                    {
                        // Cancel siblings so the first failure fails the phase quickly.
                        linkedCts.Cancel();
                        throw;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, linkedCt));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                if (progress != null) await progress.FlushNowAsync(ct);
            }
        }

        /// <summary>
        /// Downloads one file, skipping it if it already exists. Retries transient
        /// failures up to 2 times with exponential backoff.
        /// </summary>
        private async Task DownloadFileWithRetryAsync(
            HttpClient httpClient, string url, string path, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return;

            var tempPath = path + ".part";

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await contentStream.CopyToAsync(fileStream, 81920, ct);
                    }

                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 2 && ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Download attempt {Attempt} failed for {Url}, retrying...", attempt + 1, url);
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
                }
            }
        }

        /// <summary>
        /// Runs a Forge/NeoForge installer headlessly. Both output streams are drained
        /// concurrently so the child process can never block on a full pipe buffer.
        /// </summary>
        private async Task RunInstallerAsync(
            string installerPath,
            string targetDir,
            string workingDir,
            string installerName,
            CancellationToken ct,
            BuildProgressReporter? progress = null)
        {
            var javaPath = FindJavaPath();
            if (string.IsNullOrEmpty(javaPath))
                throw new InvalidOperationException("Java not found. Please install Java 17+.");

            // The Forge/NeoForge installers refuse to run without a launcher profile in the
            // target directory ("There is no Minecraft launcher profile in ... you need to
            // run the launcher first!"). Our build directory is not a real .minecraft folder,
            // so drop in a minimal profile before launching the installer.
            EnsureLauncherProfile(targetDir);

            var processInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = $"-jar \"{installerPath}\" --installClient \"{targetDir}\"",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo)
                ?? throw new InvalidOperationException($"Failed to start {installerName} installer");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process exited while we were trying to stop it.
                }

                throw;
            }

            if (process.ExitCode != 0)
            {
                var stdout = await stdoutTask;
                if (!string.IsNullOrWhiteSpace(stdout))
                    _logger.LogDebug("{InstallerName} output: {Output}", installerName, stdout);

                var stderr = await stderrTask;
                // Installers usually report the real failure on stdout, so include both streams.
                var reason = stderr.Trim();
                if (string.IsNullOrWhiteSpace(reason))
                    reason = LastLines(stdout, 15);
                else if (!string.IsNullOrWhiteSpace(stdout))
                    reason = reason + " | " + LastLines(stdout, 15);
                if (progress != null)
                    await progress.LogAsync("error", $"{installerName} installer упал (код {process.ExitCode}): {reason}", ct);
                throw new InvalidOperationException(
                    $"{installerName} installer failed with exit code {process.ExitCode}: {reason}");
            }

            await stdoutTask;
            await stderrTask;

            if (progress != null)
                await progress.LogAsync("info", $"{installerName} installer завершён успешно", ct);
        }

        /// <summary>
        /// Locates the version profile JSON written by a Forge/NeoForge installer.
        /// Naming differs across installer generations ("1.21.1-forge-52.1.19" vs
        /// "forge-1.21.1-52.1.19", "neoforge-21.1.x"), so the directory is resolved by
        /// candidates first and then by scanning the loader's version folders.
        /// </summary>
        private static string? ResolveLoaderVersionJson(string targetDir, string loader, string mcVersion, string version)
        {
            var candidates = new[]
            {
                Path.Combine(targetDir, "versions", $"{mcVersion}-{loader}-{version}", $"{mcVersion}-{loader}-{version}.json"),
                Path.Combine(targetDir, "versions", $"{loader}-{mcVersion}-{version}", $"{loader}-{mcVersion}-{version}.json"),
                Path.Combine(targetDir, "versions", $"{loader}-{version}", $"{loader}-{version}.json"),
            };

            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;

            var versionsDir = Path.Combine(targetDir, "versions");
            if (!Directory.Exists(versionsDir)) return null;

            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var name = Path.GetFileName(dir);
                if (!name.Contains(loader, StringComparison.OrdinalIgnoreCase) ||
                    !name.Contains(version, StringComparison.OrdinalIgnoreCase)) continue;

                var json = Path.Combine(dir, name + ".json");
                if (File.Exists(json)) return json;
            }

            return null;
        }

        /// <summary>
        /// Writes a minimal launcher_profiles.json into the target directory when missing.
        /// Forge/NeoForge installers abort with "There is no Minecraft launcher profile ...
        /// you need to run the launcher first!" otherwise. The installer injects its own
        /// profile entry; an empty profile map is enough.
        /// </summary>
        private static void EnsureLauncherProfile(string targetDir)
        {
            try
            {
                var profilePath = Path.Combine(targetDir, "launcher_profiles.json");
                if (File.Exists(profilePath)) return;

                Directory.CreateDirectory(targetDir);
                File.WriteAllText(profilePath, """{"profiles":{},"version":3}""");
            }
            catch
            {
                // Non-fatal: if the profile cannot be written the installer reports the reason itself.
            }
        }

        private static string LastLines(string? text, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var lines = text.TrimEnd().Split('\n');
            var tail = lines.Skip(Math.Max(0, lines.Length - maxLines));
            return string.Join(" | ", tail.Select(l => l.Trim()).Where(l => l.Length > 0));
        }

        private static string? NameToPath(string mavenName)
        {
            // Convert "group:artifact:version[:classifier]" to "group/artifact/version/artifact-version.jar"
            var parts = mavenName.Split(':');
            if (parts.Length < 3) return null;

            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? $"-{parts[3]}" : "";

            return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
        }

        private static string? FindJavaPath()
        {
            var candidates = new List<string>();

            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
                candidates.Add(Path.Combine(javaHome, "bin", "java"));
            }

            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (var rawDir in pathVar.Split(Path.PathSeparator))
                {
                    var dir = rawDir.Trim().Trim('"');
                    if (string.IsNullOrEmpty(dir)) continue;

                    candidates.Add(Path.Combine(dir, "java.exe"));
                    candidates.Add(Path.Combine(dir, "java"));
                }
            }

            // Well-known install locations: services often run with a stripped PATH
            // (especially on Windows), so a installed JDK would be invisible otherwise.
            if (OperatingSystem.IsWindows())
            {
                foreach (var programFiles in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                {
                    if (string.IsNullOrEmpty(programFiles)) continue;

                    // Covers "Java\jdk-21", "Eclipse Adoptium\jdk-21...", "Microsoft\jdk-21..." etc.
                    foreach (var vendorDir in SafeGetDirectories(Path.Combine(programFiles, "Java"))
                        .Concat(SafeGetDirectories(Path.Combine(programFiles, "Eclipse Adoptium")))
                        .Concat(SafeGetDirectories(Path.Combine(programFiles, "Microsoft"))))
                    {
                        candidates.Add(Path.Combine(vendorDir, "bin", "java.exe"));
                    }
                }
            }
            else
            {
                // Linux package managers (apt/yum) register JVMs here.
                foreach (var jvmDir in SafeGetDirectories("/usr/lib/jvm"))
                {
                    candidates.Add(Path.Combine(jvmDir, "bin", "java"));
                }
            }

            // On Linux/macOS the executable has no ".exe" suffix, so both names must be checked.
            return candidates.FirstOrDefault(File.Exists);
        }

        private static IEnumerable<string> SafeGetDirectories(string root)
        {
            try
            {
                return Directory.Exists(root)
                    ? Directory.EnumerateDirectories(root)
                    : Enumerable.Empty<string>();
            }
            catch
            {
                // Permission issues etc. - just skip this root.
                return Enumerable.Empty<string>();
            }
        }
    }

    public class EngineDownloadResult
    {
        public string MainClass { get; set; } = string.Empty;
        public List<string> JvmArgs { get; set; } = new();
        public List<string> GameArgs { get; set; } = new();
    }

    public class VanillaDownloadResult
    {
        public string MainClass { get; set; } = string.Empty;
        public List<string> JvmArgs { get; set; } = new();
        public List<string> GameArgs { get; set; } = new();
    }

    public class LoaderResult
    {
        public string MainClass { get; set; } = string.Empty;
        public List<string> JvmArgs { get; set; } = new();
        public List<string> GameArgs { get; set; } = new();
    }
}
