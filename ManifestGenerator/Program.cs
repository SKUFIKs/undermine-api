using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManifestGenerator;

public class ManifestFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("policy")]
    public string Policy { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public class BuildManifest
{
    [JsonPropertyName("buildId")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("loaderType")]
    public string LoaderType { get; set; } = string.Empty;

    [JsonPropertyName("loaderVersion")]
    public string LoaderVersion { get; set; } = string.Empty;

    [JsonPropertyName("javaVersion")]
    public int JavaVersion { get; set; } = 17;

    [JsonPropertyName("mainClass")]
    public string MainClass { get; set; } = string.Empty;

    [JsonPropertyName("jvmArgs")]
    public List<string> JvmArgs { get; set; } = new();

    [JsonPropertyName("gameArgs")]
    public List<string> GameArgs { get; set; } = new();

    [JsonPropertyName("files")]
    public List<ManifestFileEntry> Files { get; set; } = new();
}

public static class Program
{
    private const string ForceSync = "FORCE_SYNC";
    private const string DownloadOnce = "DOWNLOAD_ONCE";
    private const string Preserve = "PRESERVE";

    private static readonly HashSet<string> ForceSyncDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "libraries", "mods", "runtime", "versions", "assets", "natives"
    };

    private static readonly HashSet<string> DownloadOnceDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "defaultconfigs"
    };

    private static readonly HashSet<string> PreserveDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "screenshots", "resourcepacks", "shaderpacks", "logs", "crash-reports",
        "saves", "backups", "local", "emblems", "playerdata", "stats", "advancements"
    };

    private static readonly HashSet<string> PreserveFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "servers.dat", "usercache.json", "usernamecache.json",
        "realms_persistence.json", "launcher_profiles.json", "launcher_accounts.json"
    };

    public static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: ManifestGenerator <buildPath> <outputPath> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --name <displayName>        Display name of the build");
            Console.WriteLine("  --game-version <version>    Minecraft version (e.g. 1.20.1)");
            Console.WriteLine("  --loader <type>             Loader type: vanilla, fabric, forge, neoforge");
            Console.WriteLine("  --loader-version <version>  Loader version (e.g. 47.2.0)");
            Console.WriteLine("  --java <version>            Java version (default: 17)");
            Console.WriteLine("  --main-class <class>        Main class for launch");
            Console.WriteLine("  --cdn-base <url>            Base URL for file downloads");
            Console.WriteLine("  --build-id <id>             Build ID (default: random GUID)");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  ManifestGenerator ./builds/underMine ./builds/underMine/manifest.json --name \"UnderMine 1.20.1\" --game-version 1.20.1 --loader forge --loader-version 47.2.0 --cdn-base https://cdn.example.com/builds/underMine");
            return;
        }

        var buildPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);

        if (!Directory.Exists(buildPath))
        {
            Console.Error.WriteLine($"Error: Build directory not found: {buildPath}");
            Environment.Exit(1);
        }

        var options = ParseOptions(args.Skip(2).ToList());

        Console.WriteLine($"Generating manifest for: {buildPath}");
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine();

        var manifest = GenerateManifest(buildPath, options);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(manifest, jsonOptions);

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, json);

        Console.WriteLine();
        Console.WriteLine($"Manifest generated successfully!");
        Console.WriteLine($"  Build ID: {manifest.BuildId}");
        Console.WriteLine($"  Display Name: {manifest.DisplayName}");
        Console.WriteLine($"  Game Version: {manifest.GameVersion}");
        Console.WriteLine($"  Loader: {manifest.LoaderType} {manifest.LoaderVersion}");
        Console.WriteLine($"  Java Version: {manifest.JavaVersion}");
        Console.WriteLine($"  Main Class: {manifest.MainClass}");
        Console.WriteLine($"  Files: {manifest.Files.Count}");
        Console.WriteLine($"    FORCE_SYNC: {manifest.Files.Count(f => f.Policy == ForceSync)}");
        Console.WriteLine($"    DOWNLOAD_ONCE: {manifest.Files.Count(f => f.Policy == DownloadOnce)}");
        Console.WriteLine($"    PRESERVE: {manifest.Files.Count(f => f.Policy == Preserve)}");
    }

    private static Dictionary<string, string> ParseOptions(List<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--") && i + 1 < args.Count)
            {
                options[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return options;
    }

    private static BuildManifest GenerateManifest(string buildPath, Dictionary<string, string> options)
    {
        var buildId = options.GetValueOrDefault("build-id", Guid.NewGuid().ToString());
        var displayName = options.GetValueOrDefault("name", Path.GetFileName(buildPath));
        var gameVersion = options.GetValueOrDefault("game-version", "1.20.1");
        var loaderType = options.GetValueOrDefault("loader", "vanilla");
        var loaderVersion = options.GetValueOrDefault("loader-version", "");
        var javaVersion = int.Parse(options.GetValueOrDefault("java", "17"));
        var mainClass = options.GetValueOrDefault("main-class", GetDefaultMainClass(loaderType));
        var cdnBase = options.GetValueOrDefault("cdn-base", "").TrimEnd('/');

        var manifest = new BuildManifest
        {
            BuildId = buildId,
            DisplayName = displayName,
            GameVersion = gameVersion,
            LoaderType = loaderType,
            LoaderVersion = loaderVersion,
            JavaVersion = javaVersion,
            MainClass = mainClass,
            JvmArgs = GetDefaultJvmArgs(),
            GameArgs = GetDefaultGameArgs()
        };

        var files = Directory.EnumerateFiles(buildPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"Found {files.Count} files to process");

        int processed = 0;
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(buildPath, file).Replace('\\', '/');
            var fileInfo = new FileInfo(file);
            var policy = ClassifyFile(relativePath);
            var url = $"{cdnBase}/{relativePath}";

            string sha256;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                var hash = sha.ComputeHash(stream);
                sha256 = Convert.ToHexString(hash).ToLowerInvariant();
            }

            manifest.Files.Add(new ManifestFileEntry
            {
                Path = relativePath,
                Sha256 = sha256,
                Size = fileInfo.Length,
                Policy = policy,
                Url = url
            });

            processed++;
            if (processed % 100 == 0)
            {
                Console.WriteLine($"  Processed {processed}/{files.Count} files...");
            }
        }

        Console.WriteLine($"  Processed {processed}/{files.Count} files...");

        return manifest;
    }

    private static string ClassifyFile(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return ForceSync;

        var firstDir = segments[0];

        if (PreserveDirs.Contains(firstDir)) return Preserve;
        if (PreserveFiles.Contains(segments[^1])) return Preserve;
        if (ForceSyncDirs.Contains(firstDir)) return ForceSync;
        if (DownloadOnceDirs.Contains(firstDir)) return DownloadOnce;

        // Default: FORCE_SYNC for files in root or unknown dirs
        return ForceSync;
    }

    private static string GetDefaultMainClass(string loaderType) => loaderType.ToLowerInvariant() switch
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
}
