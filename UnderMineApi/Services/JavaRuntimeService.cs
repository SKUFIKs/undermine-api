using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnderMineApi.Options;

namespace UnderMineApi.Services;

/// <summary>A cached archive ready to be streamed to a launcher.</summary>
public sealed record JavaRuntimeFile(
    string FilePath,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Version,
    int Major,
    string Os,
    string Arch);

/// <summary>
/// Resolves Temurin JRE archives on the server, caches them on disk and serves the cached file
/// to launchers. This keeps clients independent from Adoptium/GitHub availability and lets the
/// API expose a single, authenticated Java download URL.
/// </summary>
public sealed class JavaRuntimeService
{
    public const string HttpClientName = "java";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JavaRuntimeOptions _options;
    private readonly ILogger<JavaRuntimeService> _logger;
    private readonly string _rootPath;

    public JavaRuntimeService(
        IHttpClientFactory httpClientFactory,
        IOptions<JavaRuntimeOptions> options,
        ILogger<JavaRuntimeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _rootPath = Path.GetFullPath(_options.RootPath);
    }

    /// <summary>
    /// Returns a cached runtime, downloading it from Adoptium on the first request (or when the
    /// cache is stale). Returns null when no runtime could be prepared.
    /// </summary>
    public async Task<JavaRuntimeFile?> GetAsync(
        int major,
        string os,
        string arch,
        CancellationToken ct)
    {
        if (major < _options.MinMajor || major > _options.MaxMajor)
            return null;

        var key = $"{major}-{os}-{arch}";
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            return await EnsureCachedAsync(major, os, arch, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<JavaRuntimeFile?> EnsureCachedAsync(
        int major,
        string os,
        string arch,
        CancellationToken ct)
    {
        var directory = GetDirectory(major, os, arch);
        var metadataPath = Path.Combine(directory, "runtime.json");
        var cached = TryReadCached(directory, metadataPath, major, os, arch);

        if (cached != null && !IsExpired(cached))
            return ToFile(cached, directory);

        try
        {
            var fresh = await DownloadAsync(major, os, arch, directory, metadataPath, cached, ct);
            return ToFile(fresh, directory);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not prepare Java runtime {Major} for {Os}/{Arch}", major, os, arch);

            if (cached != null && CachedFileExists(cached, directory))
            {
                _logger.LogWarning(
                    "Serving stale cached Java runtime {Major} ({Os}/{Arch}) after upstream failure",
                    major, os, arch);
                return ToFile(cached, directory);
            }

            return null;
        }
    }

    private async Task<CachedJavaRuntime> DownloadAsync(
        int major,
        string os,
        string arch,
        string directory,
        string metadataPath,
        CachedJavaRuntime? cached,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var assetsUrl = BuildAssetsUrl(major, os, arch);

        using var response = await client.GetAsync(assetsUrl, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!JavaRuntimeCatalog.TryParseAsset(json, major, os, arch, out var asset, out var error))
            throw new InvalidOperationException(error);

        if (asset.SizeBytes > _options.MaxArchiveBytes)
        {
            throw new InvalidOperationException(
                $"Java archive is too large ({asset.SizeBytes} bytes, limit {_options.MaxArchiveBytes}).");
        }

        Directory.CreateDirectory(directory);

        // The cache is stale but Adoptium still offers the same archive: refresh the timestamp
        // instead of downloading the same ~50 MB again.
        if (cached != null &&
            string.Equals(cached.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase) &&
            CachedFileExists(cached, directory))
        {
            var refreshed = cached with
            {
                Version = asset.Version,
                DownloadedAt = DateTimeOffset.UtcNow
            };

            await WriteManifestAsync(metadataPath, refreshed, ct);
            _logger.LogInformation(
                "Java runtime {Major} ({Os}/{Arch}) is still current; refreshed cache timestamp",
                major, os, arch);
            return refreshed;
        }

        var archivePath = Path.Combine(directory, asset.FileName);
        var partialPath = archivePath + ".part";

        try
        {
            await DownloadFileAsync(client, asset, partialPath, ct);

            var actualSize = new FileInfo(partialPath).Length;
            if (actualSize != asset.SizeBytes)
            {
                throw new InvalidOperationException(
                    $"Java archive size mismatch: expected {asset.SizeBytes}, got {actualSize}.");
            }

            var actualHash = await ComputeSha256Async(partialPath, ct);
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Java archive checksum mismatch for {asset.FileName}.");
            }

            File.Move(partialPath, archivePath, overwrite: true);

            var manifest = new CachedJavaRuntime(
                Major: major,
                Os: os,
                Arch: arch,
                Version: asset.Version,
                FileName: asset.FileName,
                SizeBytes: asset.SizeBytes,
                Sha256: asset.Sha256,
                DownloadedAt: DateTimeOffset.UtcNow);

            await WriteManifestAsync(metadataPath, manifest, ct);

            _logger.LogInformation(
                "Cached Java runtime {Major} ({Os}/{Arch}) version {Version}: {File} ({Size} bytes)",
                major, os, arch, asset.Version, asset.FileName, asset.SizeBytes);

            return manifest;
        }
        catch
        {
            try
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch
            {
                // Best effort cleanup.
            }

            throw;
        }
    }

    private static async Task WriteManifestAsync(
        string metadataPath,
        CachedJavaRuntime manifest,
        CancellationToken ct)
    {
        var tempMetadata = metadataPath + ".tmp";
        await File.WriteAllTextAsync(
            tempMetadata,
            JsonSerializer.Serialize(manifest, JsonOptions),
            ct);
        File.Move(tempMetadata, metadataPath, overwrite: true);
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        JavaRuntimeAsset asset,
        string path,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(
            asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength != asset.SizeBytes)
        {
            throw new InvalidOperationException(
                $"Java archive size mismatch: expected {asset.SizeBytes}, server reported {contentLength}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;

            if (totalRead > asset.SizeBytes)
                throw new InvalidOperationException("Java archive exceeds the expected size.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string BuildAssetsUrl(int major, string os, string arch)
    {
        var baseUrl = _options.AdoptiumApiBaseUrl.TrimEnd('/');
        return $"{baseUrl}/v3/assets/latest/{major.ToString(CultureInfo.InvariantCulture)}/hotspot" +
               $"?os={Uri.EscapeDataString(os)}" +
               $"&architecture={Uri.EscapeDataString(arch)}" +
               "&image_type=jre&vendor=eclipse";
    }

    private string GetDirectory(int major, string os, string arch) =>
        Path.Combine(_rootPath, major.ToString(CultureInfo.InvariantCulture), $"{os}-{arch}");

    private CachedJavaRuntime? TryReadCached(
        string directory,
        string metadataPath,
        int major,
        string os,
        string arch)
    {
        try
        {
            if (!File.Exists(metadataPath))
                return null;

            var manifest = JsonSerializer.Deserialize<CachedJavaRuntime>(
                File.ReadAllText(metadataPath), JsonOptions);

            if (manifest == null ||
                manifest.Major != major ||
                !string.Equals(manifest.Os, os, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Arch, arch, StringComparison.OrdinalIgnoreCase) ||
                !JavaRuntimeCatalog.IsSafeArchiveName(manifest.FileName, out _) ||
                manifest.SizeBytes <= 0)
            {
                return null;
            }

            return CachedFileExists(manifest, directory) ? manifest : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CachedFileExists(CachedJavaRuntime manifest, string directory)
    {
        try
        {
            var path = Path.Combine(directory, manifest.FileName);
            return File.Exists(path) && new FileInfo(path).Length == manifest.SizeBytes;
        }
        catch
        {
            return false;
        }
    }

    private bool IsExpired(CachedJavaRuntime manifest) =>
        _options.CacheHours <= 0 ||
        DateTimeOffset.UtcNow - manifest.DownloadedAt > TimeSpan.FromHours(_options.CacheHours);

    private static JavaRuntimeFile ToFile(CachedJavaRuntime manifest, string directory) =>
        new(
            Path.Combine(directory, manifest.FileName),
            manifest.FileName,
            manifest.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? "application/zip"
                : "application/gzip",
            manifest.SizeBytes,
            manifest.Sha256,
            manifest.Version,
            manifest.Major,
            manifest.Os,
            manifest.Arch);

    private sealed record CachedJavaRuntime(
        int Major,
        string Os,
        string Arch,
        string Version,
        string FileName,
        long SizeBytes,
        string Sha256,
        DateTimeOffset DownloadedAt);
}
