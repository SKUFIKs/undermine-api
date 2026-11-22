using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace UnderMineApi.Services;

/// <summary>Validated ban evidence read into memory, ready to be persisted with the ban.</summary>
public sealed record UploadedBanEvidence(
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    byte[] Data);

/// <summary>Validation failure with a stable error code for the admin API.</summary>
public sealed class BanEvidenceException : Exception
{
    public BanEvidenceException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Validates uploaded ban evidence (screenshots, logs, documents) and reads it into memory.
/// Files are stored in PostgreSQL with the ban, not in the public build bucket, so they are
/// only reachable through the authenticated admin download endpoint.
/// </summary>
public class BanEvidenceService
{
    public const int MaxFiles = 5;
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const long MaxTotalBytes = 40 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".log"] = "text/plain",
            [".json"] = "application/json",
            [".csv"] = "text/csv",
            [".zip"] = "application/zip",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm"
        };

    public async Task<IReadOnlyList<UploadedBanEvidence>> ReadAsync(
        IReadOnlyList<IFormFile>? files,
        CancellationToken cancellationToken = default)
    {
        if (files == null || files.Count == 0)
            throw new BanEvidenceException("EVIDENCE_REQUIRED", "Attach at least one evidence file.");

        if (files.Count > MaxFiles)
            throw new BanEvidenceException(
                "TOO_MANY_EVIDENCE_FILES", $"Attach at most {MaxFiles} evidence files.");

        var result = new List<UploadedBanEvidence>(files.Count);
        long totalBytes = 0;

        foreach (var file in files)
        {
            var originalName = Path.GetFileName(file.FileName ?? string.Empty).Trim();
            var extension = Path.GetExtension(originalName).ToLowerInvariant();

            if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(extension))
                throw new BanEvidenceException("EVIDENCE_INVALID", "Every evidence file needs a name and extension.");

            if (!AllowedExtensions.TryGetValue(extension, out var contentType))
            {
                throw new BanEvidenceException(
                    "EVIDENCE_TYPE_NOT_ALLOWED",
                    $"File type '{extension}' is not allowed. Use images, PDF, text/log, JSON, CSV, ZIP or video.");
            }

            if (file.Length <= 0)
                throw new BanEvidenceException("EVIDENCE_EMPTY", $"Evidence file '{originalName}' is empty.");

            if (file.Length > MaxFileBytes)
                throw new BanEvidenceException(
                    "EVIDENCE_TOO_LARGE", $"Evidence file '{originalName}' exceeds the 10 MB limit.");

            byte[] data;
            await using (var source = file.OpenReadStream())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                long fileBytes = 0;

                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    fileBytes += read;
                    if (fileBytes > MaxFileBytes)
                    {
                        throw new BanEvidenceException(
                            "EVIDENCE_TOO_LARGE", $"Evidence file '{originalName}' exceeds the 10 MB limit.");
                    }

                    totalBytes += read;
                    if (totalBytes > MaxTotalBytes)
                    {
                        throw new BanEvidenceException(
                            "EVIDENCE_TOO_LARGE", "The evidence attachments exceed the 40 MB total limit.");
                    }

                    await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                data = memory.ToArray();
            }

            if (data.Length == 0)
                throw new BanEvidenceException("EVIDENCE_EMPTY", $"Evidence file '{originalName}' is empty.");

            result.Add(new UploadedBanEvidence(
                TruncateFileName(originalName),
                contentType,
                data.LongLength,
                Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                data));
        }

        return result;
    }

    private static string TruncateFileName(string fileName)
    {
        if (fileName.Length <= 255) return fileName;

        var extension = Path.GetExtension(fileName);
        var stemLength = Math.Max(1, 255 - extension.Length);
        return Path.GetFileNameWithoutExtension(fileName)[..stemLength] + extension;
    }
}
