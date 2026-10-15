using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnderMineApi.DTOs;

namespace UnderMineApi.Services;

public class ModrinthService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModrinthService> _logger;

    public ModrinthService(HttpClient httpClient, ILogger<ModrinthService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.modrinth.com/v2/");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UnderMineApi/1.0 (contact@api-undermine.ru)");
    }

    public async Task<ModrinthSearchResult> SearchModsAsync(string query, string mcVersion, string loader, CancellationToken ct = default)
    {
        var facets = $"[[\"project_type:mod\"],[\"versions:{mcVersion}\"],[\"categories:{loader.ToLowerInvariant()}\"]]";
        var url = $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}";

        var response = await _httpClient.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var resetHeader = response.Headers.TryGetValues("X-Ratelimit-Reset", out var resetVals) ? resetVals.FirstOrDefault() : null;
            _logger.LogWarning("Modrinth rate limit hit. Reset at: {Reset}", resetHeader);
            throw new HttpRequestException("Rate limit exceeded", null, HttpStatusCode.TooManyRequests);
        }

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<ModrinthSearchResult>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return result ?? new ModrinthSearchResult();
    }

    public async Task<ModrinthVersion?> GetCompatibleVersionAsync(string idOrSlug, string mcVersion, string loader, CancellationToken ct = default)
    {
        var url = $"project/{Uri.EscapeDataString(idOrSlug)}/version?game_versions=[\"{Uri.EscapeDataString(mcVersion)}\"]&loaders=[\"{Uri.EscapeDataString(loader.ToLowerInvariant())}\"]";

        var response = await _httpClient.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Modrinth rate limit hit");
            throw new HttpRequestException("Rate limit exceeded", null, HttpStatusCode.TooManyRequests);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (versions == null || versions.Count == 0)
        {
            return null;
        }

        // Prefer stable (release) versions; if none, take latest by date
        var stable = versions.Where(v => v.VersionType.Equals("release", StringComparison.OrdinalIgnoreCase)).ToList();
        if (stable.Count > 0)
        {
            return stable.OrderByDescending(v => v.DatePublished).First();
        }

        return versions.OrderByDescending(v => v.DatePublished).First();
    }

    public async Task<(string LocalPath, ModrinthFile File)> DownloadVersionAsync(
        ModrinthVersion version, string downloadDir, CancellationToken ct = default)
    {
        // Select file with primary = true, otherwise first
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        if (file == null)
        {
            throw new InvalidOperationException("No downloadable files found for this version");
        }

        Directory.CreateDirectory(downloadDir);

        var localPath = Path.Combine(downloadDir, file.Filename);

        using var response = await _httpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(localPath);
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);

        // Compute SHA-256 during download
        using var sha256 = SHA256.Create();
        await using var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write);

        await httpStream.CopyToAsync(cryptoStream, ct);
        await cryptoStream.FlushFinalBlockAsync(ct);

        var hashBytes = sha256.Hash ?? Array.Empty<byte>();
        var computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        if (!string.IsNullOrEmpty(file.Hashes.Sha256) && computedHash != file.Hashes.Sha256.ToLowerInvariant())
        {
            try
            {
                File.Delete(localPath);
            }
            catch
            {
            }
            throw new InvalidOperationException($"Checksum mismatch for {file.Filename}: expected {file.Hashes.Sha256}, got {computedHash}");
        }

        return (localPath, file);
    }
}
