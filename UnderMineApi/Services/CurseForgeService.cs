using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace UnderMineApi.Services;

/// <summary>
/// The CurseForge API could not be reached or rejected the request. Inherits
/// <see cref="ModpackImportException"/> so both the admin controller (400) and the build worker
/// surface it as a readable, actionable import failure instead of a generic crash.
/// </summary>
public class CurseForgeApiException : ModpackImportException
{
    public CurseForgeApiException(string message) : base(message) { }
    public CurseForgeApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Resolved CurseForge file reference: where to download it from and what it should be.</summary>
public sealed class CurseForgeFileRef
{
    public int ProjectId { get; init; }
    public int FileId { get; init; }

    /// <summary>Bare file name as published on CurseForge, e.g. "jei-1.12.2-4.16.1.302.jar".</summary>
    public string FileName { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;
    public long? FileLength { get; init; }
    public string? Sha1 { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Resolves CurseForge <c>(projectID, fileID)</c> pairs found in a modpack manifest into
/// downloadable files.
///
/// A CurseForge modpack zip deliberately ships no jars and no URLs - only ids - so every file has
/// to be looked up through <c>api.curseforge.com</c>, which requires a free API key
/// (<c>https://console.curseforge.com/</c>), passed as <c>x-api-key</c>.
///
/// CurseForge occasionally returns a null <c>downloadUrl</c>; the CDN keeps every file reachable at
/// a stable address derived from the file id
/// (<c>mediafilez.forgecdn.net/files/{id/1000}/{id%1000}/{fileName}</c>), so that is used as the
/// fallback instead of dropping the mod.
/// </summary>
public class CurseForgeService
{
    /// <summary>Configuration key holding the API key (docker: CurseForge__ApiKey).</summary>
    public const string ApiKeyConfigKey = "CurseForge:ApiKey";

    private const string ApiBase = "https://api.curseforge.com/v1";
    private const string CdnBase = "https://mediafilez.forgecdn.net/files";
    private const string UserAgent = "UnderMineApi/1.0 (contact@api-undermine.ru)";

    private const int MaxAttempts = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<CurseForgeService> _logger;

    public CurseForgeService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<CurseForgeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>The configured API key, or null when nobody configured one yet.</summary>
    public string? ApiKey
    {
        get
        {
            var key = _config[ApiKeyConfigKey]?.Trim();
            return string.IsNullOrEmpty(key) ? null : key;
        }
    }

    public bool IsConfigured => ApiKey != null;

    /// <summary>Throws a readable error when the API key is missing, before any build is queued.</summary>
    public void EnsureConfigured()
    {
        if (IsConfigured) return;

        throw new CurseForgeApiException(
            "CurseForge API key is not configured. Set " + ApiKeyConfigKey +
            " (docker: CurseForge__ApiKey); a free key is issued at https://console.curseforge.com/");
    }

    /// <summary>
    /// Looks up one mod file. Retries rate limits (429) and transient 5xx responses with backoff,
    /// because a pack resolves hundreds of files in one build run.
    /// </summary>
    public async Task<CurseForgeFileRef> GetFileAsync(int projectId, int fileId, CancellationToken ct)
    {
        EnsureConfigured();

        var url = $"{ApiBase}/mods/{projectId}/files/{fileId}";

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-api-key", ApiKey);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd(UserAgent);

                response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxAttempts)
                    throw new CurseForgeApiException(
                        $"CurseForge API is unreachable for file {fileId}: {ex.Message}", ex);

                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                throw new CurseForgeApiException(
                    $"CurseForge has no file {fileId} for project {projectId} (the file may have been removed)");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new CurseForgeApiException(
                    $"CurseForge rejected the API key (HTTP {(int)status}). Check {ApiKeyConfigKey}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();

                if ((int)status == 429 && attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                    continue;
                }

                if ((int)status >= 500 && attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    continue;
                }

                throw new CurseForgeApiException(
                    $"CurseForge API returned HTTP {(int)status} for file {fileId}");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            finally
            {
                response.Dispose();
            }

            CurseForgeFileResponse? payload;
            try
            {
                payload = JsonSerializer.Deserialize<CurseForgeFileResponse>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new CurseForgeApiException("Unexpected response from the CurseForge API", ex);
            }

            var data = payload?.Data;
            if (data == null || string.IsNullOrWhiteSpace(data.FileName))
                throw new CurseForgeApiException(
                    $"CurseForge returned no usable file info for {projectId}/{fileId}");

            return new CurseForgeFileRef
            {
                ProjectId = projectId,
                FileId = fileId,
                FileName = data.FileName!,
                DownloadUrl = ResolveDownloadUrl(data, fileId),
                FileLength = data.FileLength,
                Sha1 = ExtractSha1(data.Hashes),
                GameVersions = data.GameVersions ?? (IReadOnlyList<string>)Array.Empty<string>()
            };
        }
    }

    /// <summary>Uses the reported URL, falling back to the id-derived CDN address.</summary>
    private static string ResolveDownloadUrl(CurseForgeFileDto data, int fileId)
    {
        if (!string.IsNullOrWhiteSpace(data.DownloadUrl))
            return data.DownloadUrl!;

        return BuildCdnUrl(fileId, data.FileName!);
    }

    /// <summary>
    /// The CurseForge CDN address of a file. Curated and verified: the legacy
    /// <c>media.forgecdn.net</c> host is gone (403), <c>mediafilez</c>/<c>edge</c> still serve.
    /// </summary>
    public static string BuildCdnUrl(int fileId, string fileName) =>
        $"{CdnBase}/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

    /// <summary>
    /// Reads the sha1 out of the hash list. CurseForge exposes the algorithm either as the enum
    /// name ("sha1") or as its numeric value ("1"), so both spellings are accepted.
    /// </summary>
    private static string? ExtractSha1(List<CurseForgeHashDto>? hashes)
    {
        foreach (var hash in hashes ?? Enumerable.Empty<CurseForgeHashDto>())
        {
            if (hash.Algo is not { } algoElement) continue;

            var algo = algoElement.ValueKind switch
            {
                JsonValueKind.String => algoElement.GetString(),
                JsonValueKind.Number => algoElement.GetRawText(),
                _ => null
            };

            var isSha1 = string.Equals(algo, "sha1", StringComparison.OrdinalIgnoreCase)
                         || algo == "1";
            if (isSha1 && !string.IsNullOrWhiteSpace(hash.Value))
                return hash.Value!.Trim().ToLowerInvariant();
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Response DTOs (subset of the CurseForge v1 file object)
    // ------------------------------------------------------------------

    private sealed class CurseForgeFileResponse
    {
        [JsonPropertyName("data")]
        public CurseForgeFileDto? Data { get; set; }
    }

    private sealed class CurseForgeFileDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("modId")]
        public int ModId { get; set; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("fileLength")]
        public long? FileLength { get; set; }

        [JsonPropertyName("hashes")]
        public List<CurseForgeHashDto>? Hashes { get; set; }

        [JsonPropertyName("gameVersions")]
        public List<string>? GameVersions { get; set; }
    }

    private sealed class CurseForgeHashDto
    {
        [JsonPropertyName("algo")]
        public JsonElement? Algo { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
