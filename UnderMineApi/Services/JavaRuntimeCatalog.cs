using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnderMineApi.Services;

/// <summary>An archive entry returned by the Adoptium assets API.</summary>
public sealed record JavaRuntimeAsset(
    string DownloadUrl,
    string FileName,
    long SizeBytes,
    string Sha256,
    string Version);

/// <summary>
/// Pure validation/parsing helpers for the Java runtime download. Kept separate from
/// <see cref="JavaRuntimeService"/> so platform handling and the upstream JSON can be tested
/// without network access.
/// </summary>
public static class JavaRuntimeCatalog
{
    private static readonly Regex Sha256Pattern =
        new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.adoptium.net",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    public static bool TryNormalizePlatform(
        string? os,
        string? arch,
        out string normalizedOs,
        out string normalizedArch,
        out string error)
    {
        normalizedOs = os?.Trim().ToLowerInvariant() switch
        {
            "windows" or "win" => "windows",
            "linux" => "linux",
            "mac" or "macos" or "osx" or "darwin" => "mac",
            _ => string.Empty
        };

        normalizedArch = arch?.Trim().ToLowerInvariant() switch
        {
            "x64" or "x86_64" or "amd64" => "x64",
            "aarch64" or "arm64" => "aarch64",
            "x32" or "x86" or "i386" or "i686" => "x32",
            "arm" or "arm32" or "aarch32" => "arm",
            _ => string.Empty
        };

        error = normalizedOs.Length == 0
            ? "Unsupported operating system. Use windows, linux or mac."
            : normalizedArch.Length == 0
                ? "Unsupported architecture. Use x64, aarch64, x32 or arm."
                : string.Empty;

        return error.Length == 0;
    }

    /// <summary>
    /// Parses the first entry of an Adoptium <c>/v3/assets/latest</c> response and validates the
    /// download URL, checksum and file name before they are used on disk.
    /// </summary>
    public static bool TryParseAsset(
        string json,
        int major,
        string os,
        string arch,
        out JavaRuntimeAsset asset,
        out string error)
    {
        asset = default!;
        error = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                error = "Adoptium did not return any Java runtime assets.";
                return false;
            }

            var first = root[0];
            if (!first.TryGetProperty("binary", out var binary) ||
                !binary.TryGetProperty("package", out var package))
            {
                error = "Adoptium response is missing the binary package.";
                return false;
            }

            var packageOs = binary.TryGetProperty("os", out var osElement) ? osElement.GetString() : null;
            var packageArch = binary.TryGetProperty("architecture", out var archElement)
                ? archElement.GetString()
                : null;

            if (packageOs != null && !string.Equals(packageOs, os, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Adoptium returned a runtime for {packageOs} instead of {os}.";
                return false;
            }

            if (packageArch != null && !string.Equals(packageArch, arch, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Adoptium returned a runtime for {packageArch} instead of {arch}.";
                return false;
            }

            var link = package.TryGetProperty("link", out var linkElement)
                ? linkElement.GetString()
                : null;
            var name = package.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var checksum = package.TryGetProperty("checksum", out var checksumElement)
                ? checksumElement.GetString()
                : null;
            var size = package.TryGetProperty("size", out var sizeElement) &&
                       sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;

            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !IsAllowedDownloadHost(uri.Host))
            {
                error = "Adoptium returned an untrusted download URL.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(checksum) || !Sha256Pattern.IsMatch(checksum))
            {
                error = "Adoptium returned an invalid SHA-256 checksum.";
                return false;
            }

            if (!IsSafeArchiveName(name, out var safeName))
            {
                error = "Adoptium returned an invalid archive name.";
                return false;
            }

            if (size <= 0)
            {
                error = "Adoptium did not report the archive size.";
                return false;
            }

            var version = string.Empty;
            if (first.TryGetProperty("version", out var versionElement))
            {
                if (versionElement.TryGetProperty("major", out var majorElement) &&
                    majorElement.TryGetInt32(out var versionMajor) &&
                    versionMajor != major)
                {
                    error = $"Adoptium returned Java {versionMajor} instead of Java {major}.";
                    return false;
                }

                if (versionElement.TryGetProperty("semver", out var semverElement))
                    version = semverElement.GetString() ?? string.Empty;
            }

            asset = new JavaRuntimeAsset(link!, safeName, size, checksum.ToLowerInvariant(), version);
            return true;
        }
        catch (JsonException)
        {
            error = "Could not parse the Adoptium response.";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = "Could not parse the Adoptium response.";
            return false;
        }
    }

    public static bool IsAllowedDownloadHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return AllowedHosts.Contains(host) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeArchiveName(string? name, out string safeName)
    {
        safeName = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Do not silently sanitize: the cache path is built from this name.
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
            return false;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return false;

        safeName = name;
        return true;
    }
}
