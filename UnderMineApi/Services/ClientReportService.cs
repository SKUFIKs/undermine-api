using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Models;

namespace UnderMineApi.Services;

/// <summary>Validation failure with a stable error code for the public report endpoint.</summary>
public sealed class ClientReportException : Exception
{
    public ClientReportException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record ReportUploadFile(
    string Kind,
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    byte[] Data);

/// <summary>
/// Validates and stores launcher crash reports. Files are kept in PostgreSQL together with the
/// report so they are covered by database backups and reachable only through the authenticated
/// admin download endpoint. Only plain-text logs are accepted; the server renames the stored
/// form field to a fixed file kind and never trusts the client-side path.
/// </summary>
public sealed class ClientReportService
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const long MaxTotalBytes = 30 * 1024 * 1024;
    public const int MaxFiles = 4;
    public const string CurrentConsentVersion = "1";

    public static readonly IReadOnlySet<string> AllowedKinds =
        new HashSet<string>(StringComparer.Ordinal) { "launcher", "minecraft" };

    public static readonly IReadOnlySet<string> AllowedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "new", "investigating", "resolved" };

    private static readonly Regex DeviceIdPattern =
        new("^[A-Za-z0-9._:-]{16,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AppDbContext _context;
    private readonly ILogger<ClientReportService> _logger;

    public ClientReportService(AppDbContext context, ILogger<ClientReportService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<long> CreateAsync(
        ClientReportForm form,
        long? userId,
        string? ip,
        CancellationToken cancellationToken = default)
    {
        if (form == null)
            throw new ClientReportException("REPORT_REQUIRED", "The report form is missing.");

        if (!form.Consent)
            throw new ClientReportException(
                "CONSENT_REQUIRED",
                "The report can only be sent after the player explicitly consents.");

        var kind = SanitizeLine(form.Kind, 20).ToLowerInvariant();
        if (!AllowedKinds.Contains(kind))
            throw new ClientReportException("INVALID_KIND", "Kind must be 'launcher' or 'minecraft'.");

        var title = SanitizeLine(form.Title, 200);
        if (title.Length < 3)
            throw new ClientReportException("TITLE_REQUIRED", "A short report title is required.");

        var summary = SanitizeMultiline(form.Summary, 4000);
        var launcherVersion = SanitizeLine(form.LauncherVersion, 64);
        var minecraftVersion = SanitizeLine(form.MinecraftVersion, 64);
        var loader = SanitizeLine(form.Loader, 64);
        var serverName = SanitizeLine(form.ServerName, 100);
        var os = SanitizeLine(form.Os, 200);
        var consentVersion = SanitizeLine(form.ConsentVersion, 32);
        if (consentVersion.Length == 0)
            consentVersion = CurrentConsentVersion;

        var deviceId = SanitizeLine(form.DeviceId, 128);
        if (deviceId.Length == 0 && userId == null)
            throw new ClientReportException(
                "DEVICE_ID_REQUIRED",
                "Anonymous reports require a device id.");

        if (deviceId.Length > 0 && !DeviceIdPattern.IsMatch(deviceId))
            throw new ClientReportException(
                "INVALID_DEVICE_ID",
                "The device id must be 16–128 characters (letters, digits, '.', '_', ':', '-').");

        var files = new List<ReportUploadFile>(MaxFiles);

        var launcherLog = await ReadFileAsync(form.LauncherLog, "launcher_log", cancellationToken);
        if (launcherLog == null)
            throw new ClientReportException(
                "LAUNCHER_LOG_REQUIRED",
                "The launcher log is required for every report.");
        files.Add(launcherLog);

        if (kind == "minecraft")
        {
            var debug = await ReadFileAsync(form.MinecraftDebug, "minecraft_debug", cancellationToken);
            var crash = await ReadFileAsync(form.MinecraftCrash, "minecraft_crash", cancellationToken);
            var latest = await ReadFileAsync(form.MinecraftLatest, "minecraft_latest", cancellationToken);

            foreach (var file in new[] { debug, crash, latest })
            {
                if (file != null)
                    files.Add(file);
            }

            if (files.Count == 1)
                throw new ClientReportException(
                    "MINECRAFT_FILES_REQUIRED",
                    "A Minecraft report must include at least one Minecraft log or crash report.");
        }

        if (files.Count > MaxFiles)
            throw new ClientReportException("TOO_MANY_FILES", $"A report can contain at most {MaxFiles} files.");

        var totalBytes = files.Sum(f => f.Size);
        if (totalBytes > MaxTotalBytes)
            throw new ClientReportException(
                "FILES_TOO_LARGE",
                "The attached files exceed the 30 MB total limit.");

        // Abuse guard on top of the rate limiter: a device/IP/account can produce at most
        // 10 reports per day. A normal player never reaches this; a spam script does.
        var since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
        var recentReports = await _context.ClientReports
            .AsNoTracking()
            .CountAsync(r =>
                r.CreatedAt >= since &&
                ((userId != null && r.UserId == userId) ||
                 (ip != null && r.Ip == ip) ||
                 (deviceId.Length > 0 && r.DeviceId == deviceId)),
                cancellationToken);

        if (recentReports >= 10)
            throw new ClientReportException(
                "REPORT_QUOTA",
                "Too many reports from this device in the last 24 hours. Please try again tomorrow.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var report = new ClientReport
        {
            UserId = userId,
            DeviceId = deviceId,
            Kind = kind,
            Title = title,
            Summary = summary,
            LauncherVersion = NullIfEmpty(launcherVersion),
            MinecraftVersion = NullIfEmpty(minecraftVersion),
            Loader = NullIfEmpty(loader),
            ServerName = NullIfEmpty(serverName),
            Os = NullIfEmpty(os),
            Status = "new",
            ConsentVersion = consentVersion,
            CreatedAt = now,
            Ip = string.IsNullOrWhiteSpace(ip) ? null : ip.Length > 64 ? ip[..64] : ip
        };

        foreach (var file in files)
        {
            report.Files.Add(new ClientReportFile
            {
                Kind = file.Kind,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Size = file.Size,
                Sha256 = file.Sha256,
                Data = file.Data,
                CreatedAt = now
            });
        }

        _context.ClientReports.Add(report);
        await _context.SaveChangesAsync(cancellationToken);

        // Never log the title/summary: they can contain player-provided text.
        _logger.LogInformation(
            "Client report {ReportId} received: kind {Kind}, user {UserId}, files {FileCount}, bytes {Bytes}.",
            report.Id, LogSanitizer.Clean(report.Kind), userId, files.Count, totalBytes);

        return report.Id;
    }

    private static async Task<ReportUploadFile?> ReadFileAsync(
        IFormFile? file,
        string kind,
        CancellationToken cancellationToken)
    {
        if (file == null)
            return null;

        var originalName = SanitizeFileName(file.FileName);
        var extension = Path.GetExtension(originalName).ToLowerInvariant();

        if (extension is not (".log" or ".txt"))
            throw new ClientReportException(
                "FILE_TYPE_NOT_ALLOWED",
                "Only .log and .txt files are accepted.");

        if (file.Length <= 0)
            throw new ClientReportException("EMPTY_FILE", "One of the attached files is empty.");

        if (file.Length > MaxFileBytes)
            throw new ClientReportException(
                "FILE_TOO_LARGE",
                $"Each attached file must be at most {MaxFileBytes / (1024 * 1024)} MB.");

        byte[] data;
        await using (var source = file.OpenReadStream())
        using (var memory = new MemoryStream((int)Math.Min(file.Length, MaxFileBytes)))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                readTotal += read;
                if (readTotal > MaxFileBytes)
                    throw new ClientReportException(
                        "FILE_TOO_LARGE",
                        $"Each attached file must be at most {MaxFileBytes / (1024 * 1024)} MB.");

                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            data = memory.ToArray();
        }

        if (data.Length == 0)
            throw new ClientReportException("EMPTY_FILE", "One of the attached files is empty.");

        if (string.IsNullOrWhiteSpace(originalName))
            originalName = kind + extension;

        return new ReportUploadFile(
            kind,
            TruncateFileName(originalName),
            "text/plain; charset=utf-8",
            data.LongLength,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            data);
    }

    private static string SanitizeLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = new string(value
            .Where(c => !char.IsControl(c))
            .ToArray())
            .Trim();

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    private static string SanitizeMultiline(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = new string(value
            .Where(c => c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c))
            .ToArray())
            .Trim();

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name;
    }

    private static string TruncateFileName(string fileName)
    {
        if (fileName.Length <= 255)
            return fileName;

        var extension = Path.GetExtension(fileName);
        var stemLength = Math.Max(1, 255 - extension.Length);
        return Path.GetFileNameWithoutExtension(fileName)[..stemLength] + extension;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
