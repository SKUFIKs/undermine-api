using System;
using System.IO;
using System.Linq;

namespace UnderMineApi.Services;

/// <summary>
/// Validates build-relative file paths and resolves them under the local build directory.
/// The admin editor accepts paths from the browser, so they must never escape the build root
/// or the S3 key prefix.
/// </summary>
public static class BuildPathSafety
{
    public const int MaxPathLength = 500;

    private static readonly char[] CrossPlatformInvalidChars =
        { '<', '>', ':', '"', '|', '?', '*' };

    public static string NormalizeRelativePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new BuildPathException("Path is empty.");

        var path = rawPath.Trim().Replace('\\', '/');

        if (path.StartsWith('/') || Path.IsPathRooted(path))
            throw new BuildPathException("Path must be relative to the build root.");

        if (path.Length > MaxPathLength)
            throw new BuildPathException($"Path is longer than {MaxPathLength} characters.");

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length == 0)
            throw new BuildPathException("Path is empty.");

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                throw new BuildPathException("Path contains an invalid segment.");

            if (segment.IndexOfAny(CrossPlatformInvalidChars) >= 0 ||
                segment.Any(char.IsControl) ||
                segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new BuildPathException($"Path segment '{segment}' contains invalid characters.");
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>Normalizes a directory path; empty or "/" means the build root.</summary>
    public static string NormalizeDirectoryPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        var trimmed = rawPath.Trim();
        if (trimmed is "/" or "\\")
            return string.Empty;

        return NormalizeRelativePath(trimmed);
    }

    public static string ToS3DirectoryPrefix(string buildKey, string relativeDirectory)
    {
        var normalized = NormalizeDirectoryPath(relativeDirectory);
        return normalized.Length == 0
            ? $"builds/{buildKey}/"
            : $"builds/{buildKey}/{normalized}/";
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSeparator = rootFull + Path.DirectorySeparatorChar;

        var fullPath = Path.GetFullPath(
            Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison))
            throw new BuildPathException("Path escapes the build directory.");

        return fullPath;
    }

    public static string ToS3Key(string buildKey, string relativePath) =>
        $"builds/{buildKey}/{NormalizeRelativePath(relativePath)}";
}

public sealed class BuildPathException : Exception
{
    public BuildPathException(string message) : base(message) { }
}
