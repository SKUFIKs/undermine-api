using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio.Exceptions;
using UnderMineApi.Data;
using UnderMineApi.Domain.Entities;
using UnderMineApi.DTOs;

namespace UnderMineApi.Services;

/// <summary>
/// Backs the admin build editor: build settings, S3 file browsing, text editing, uploads,
/// downloads and renames. After every file change the S3 object, the build_files row and the
/// stored client manifest are updated together, so the launcher immediately sees the new file
/// without rebuilding the whole pack.
/// </summary>
public sealed class BuildEditorService
{
    public const long MaxEditableBytes = 2L * 1024 * 1024;
    public const long MaxUploadBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".json5", ".toml", ".yml", ".yaml",
        ".properties", ".cfg", ".conf", ".ini", ".xml", ".mcmeta",
        ".lang", ".md", ".csv", ".tsv", ".js", ".css", ".html",
        ".sh", ".bat", ".ps1", ".log", ".list"
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "license", "licence", "readme", "changelog", "notice"
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".toml"] = "text/plain; charset=utf-8",
        [".yml"] = "text/yaml; charset=utf-8",
        [".yaml"] = "text/yaml; charset=utf-8",
        [".properties"] = "text/plain; charset=utf-8",
        [".cfg"] = "text/plain; charset=utf-8",
        [".conf"] = "text/plain; charset=utf-8",
        [".ini"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".jar"] = "application/java-archive",
        [".zip"] = "application/zip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".ogg"] = "audio/ogg",
        [".mp3"] = "audio/mpeg"
    };

    private readonly AppDbContext _context;
    private readonly S3StorageService _s3;
    private readonly IConfiguration _config;
    private readonly AdminAuditService _audit;
    private readonly ILogger<BuildEditorService> _logger;

    public BuildEditorService(
        AppDbContext context,
        S3StorageService s3,
        IConfiguration config,
        AdminAuditService audit,
        ILogger<BuildEditorService> logger)
    {
        _context = context;
        _s3 = s3;
        _config = config;
        _audit = audit;
        _logger = logger;
    }

    public async Task<BuildEditorResponse> GetEditorAsync(long id, CancellationToken ct)
    {
        var build = await GetBuildAsync(id, tracking: false, ct);
        var files = BuildFileList(build);

        return new BuildEditorResponse
        {
            Id = build.Id,
            Name = build.Name,
            McVersion = build.McVersion,
            LoaderType = build.LoaderType,
            LoaderVersion = build.LoaderVersion,
            BuildPresetId = build.BuildPresetId,
            Status = build.Status,
            BuildKey = build.BuildId,
            FileCount = files.Count,
            TotalSize = files.Sum(f => f.Size),
            UpdatedAt = build.UpdatedAt,
            Files = files
        };
    }

    public async Task<ClientManifestResponse> GetManifestAsync(long id, CancellationToken ct)
    {
        var build = await GetBuildMetadataAsync(id, tracking: false, ct);
        return ParseManifest(build);
    }

    /// <summary>
    /// Returns one directory level: immediate subdirectories and files. Directories are derived
    /// from file paths and from S3 folder markers, so empty folders created in the editor remain
    /// visible.
    /// </summary>
    public async Task<BuildDirectoryResponse> GetDirectoryAsync(
        long id,
        string? path,
        CancellationToken ct)
    {
        var build = await GetBuildAsync(id, tracking: false, ct);
        EnsureEditable(build);

        var current = NormalizeDirectoryPath(path);
        var allFiles = BuildFileList(build);
        var prefix = BuildPathSafety.ToS3DirectoryPrefix(build.BuildId, current);

        var s3DirectoryTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var s3FileTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var directoryNames = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var item in _s3.ListDirectoryAsync(prefix, ct))
        {
            if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var raw = item.Key[prefix.Length..];
            if (raw.Length == 0)
                continue;

            var name = raw.TrimEnd('/');
            if (name.Length == 0)
                continue;

            var isDirectoryObject = item.IsDirectory || item.Key.EndsWith('/');
            if (isDirectoryObject)
            {
                directoryNames.Add(name);
                if (item.LastModified.HasValue)
                    UpdateMax(s3DirectoryTimes, name, item.LastModified.Value);
            }
            else if (item.LastModified.HasValue)
            {
                s3FileTimes[name] = item.LastModified.Value;
            }
        }

        var files = new List<BuildFileInfo>();

        foreach (var file in allFiles)
        {
            var relative = file.Path;
            if (current.Length > 0)
            {
                var directoryPrefix = current + "/";
                if (!relative.StartsWith(directoryPrefix, StringComparison.Ordinal))
                    continue;

                relative = relative[directoryPrefix.Length..];
            }

            var slash = relative.IndexOf('/');
            if (slash >= 0)
            {
                directoryNames.Add(relative[..slash]);
                continue;
            }

            file.Name = relative;
            file.ModifiedAt = s3FileTimes.TryGetValue(relative, out var modified)
                ? modified
                : null;
            files.Add(file);
        }

        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        var directories = directoryNames
            .Select(name => new BuildDirectoryEntry
            {
                Name = name,
                Path = current.Length == 0 ? name : current + "/" + name,
                ModifiedAt = s3DirectoryTimes.TryGetValue(name, out var modified)
                    ? modified
                    : null
            })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();

        return new BuildDirectoryResponse
        {
            Path = current,
            TotalFileCount = allFiles.Count,
            TotalSize = allFiles.Sum(f => f.Size),
            Breadcrumbs = BuildBreadcrumbs(current),
            Directories = directories,
            Files = files
        };
    }

    public async Task CreateDirectoryAsync(
        long id,
        string path,
        string actor,
        CancellationToken ct)
    {
        var normalized = NormalizeDirectoryPath(path);
        if (normalized.Length == 0)
            throw new BuildEditorException(400, "INVALID_PATH", "Directory path cannot be empty.");

        var build = await GetBuildAsync(id, tracking: false, ct);
        EnsureEditable(build);

        var manifest = ParseManifest(build);
        if (build.Files.Any(f => string.Equals(f.Path, normalized, StringComparison.Ordinal)) ||
            manifest.Files.Any(f => string.Equals(f.Path, normalized, StringComparison.Ordinal)))
        {
            throw new BuildEditorException(409, "FILE_ALREADY_EXISTS", "A file with this path already exists.");
        }

        var current = string.Empty;
        foreach (var segment in normalized.Split('/'))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            await _s3.PutDirectoryMarkerAsync(
                BuildPathSafety.ToS3DirectoryPrefix(build.BuildId, current), ct);
        }

        await _audit.LogAsync(
            actor, "create_build_directory", "build", build.Id,
            $"{build.BuildId}: {normalized}", ct);
    }

    public async Task DeleteDirectoryAsync(
        long id,
        string path,
        string actor,
        CancellationToken ct)
    {
        var normalized = NormalizeDirectoryPath(path);
        if (normalized.Length == 0)
            throw new BuildEditorException(400, "INVALID_PATH", "The build root cannot be deleted.");

        var build = await GetBuildAsync(id, tracking: true, ct);
        EnsureEditable(build);

        var manifest = ParseManifest(build);
        var prefix = normalized + "/";
        var manifestFiles = manifest.Files
            .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        var dbFiles = build.Files
            .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        var prefixKey = BuildPathSafety.ToS3DirectoryPrefix(build.BuildId, normalized);
        var markerExists = await _s3.ObjectExistsAsync(prefixKey, ct);

        if (!markerExists && manifestFiles.Count == 0 && dbFiles.Count == 0)
        {
            throw new BuildEditorException(
                404, "BUILD_DIRECTORY_NOT_FOUND", "The directory does not exist.");
        }

        // Update the manifest/DB first: if S3 deletion fails, the result is only an orphaned
        // object, not a manifest that points at missing files.
        _context.BuildFiles.RemoveRange(dbFiles);
        foreach (var file in manifestFiles)
            manifest.Files.Remove(file);

        build.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        build.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        await _s3.DeletePrefixAsync(prefixKey, ct);
        TryDeleteLocalDirectory(build, normalized);
        await _audit.LogAsync(
            actor, "delete_build_directory", "build", build.Id,
            $"{build.BuildId}: {normalized}", ct);
    }

    public async Task<BuildSettingsResponse> UpdateSettingsAsync(
        long id,
        UpdateBuildSettingsRequest request,
        string actor,
        CancellationToken ct)
    {
        var build = await GetBuildMetadataAsync(id, tracking: true, ct);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new BuildEditorException(400, "INVALID_NAME", "Build name cannot be empty.");

        if (name.Length > 100)
            throw new BuildEditorException(400, "INVALID_NAME", "Build name is too long (max 100).");

        if (request.BuildPresetId is long presetId)
        {
            var preset = await _context.BuildPresets
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == presetId, ct)
                ?? throw new BuildEditorException(400, "PRESET_NOT_FOUND", "The selected preset does not exist.");

            if (!string.Equals(preset.GameVersion, build.McVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(preset.LoaderType, build.LoaderType, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildEditorException(
                    400, "PRESET_MISMATCH",
                    "The preset's Minecraft version or loader does not match this build.");
            }
        }

        build.Name = name;
        build.LoaderVersion = string.IsNullOrWhiteSpace(request.LoaderVersion)
            ? null
            : request.LoaderVersion.Trim();
        build.BuildPresetId = request.BuildPresetId;
        build.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(build.ManifestJson))
        {
            try
            {
                var manifest = ParseManifest(build);
                manifest.DisplayName = build.Name;
                manifest.LoaderVersion = build.LoaderVersion;
                build.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            }
            catch (BuildEditorException)
            {
                // A broken manifest should not prevent changing the display name; file
                // operations still report the problem when they need the manifest.
                _logger.LogWarning("Build {BuildId} has an invalid manifest while updating settings", build.BuildId);
            }
        }

        await _context.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "update_build_settings", "build", build.Id, build.BuildId, ct);

        return new BuildSettingsResponse
        {
            Id = build.Id,
            Name = build.Name,
            LoaderVersion = build.LoaderVersion,
            BuildPresetId = build.BuildPresetId,
            UpdatedAt = build.UpdatedAt
        };
    }

    public async Task<BuildFileContentResponse> ReadFileAsync(
        long id,
        string path,
        CancellationToken ct)
    {
        var build = await GetBuildAsync(id, tracking: false, ct);
        var normalizedPath = NormalizePath(path);
        var file = FindFile(build, normalizedPath);

        byte[] bytes;
        try
        {
            bytes = await _s3.DownloadBytesAsync(file.S3Key, MaxEditableBytes, ct);
        }
        catch (ObjectNotFoundException)
        {
            throw new BuildEditorException(404, "BUILD_FILE_NOT_FOUND", "The file is missing from storage.");
        }
        catch (InvalidOperationException)
        {
            throw new BuildEditorException(
                413, "FILE_TOO_LARGE",
                $"Files larger than {MaxEditableBytes / 1024 / 1024} MB can only be downloaded.");
        }

        var isBinary = bytes.Any(b => b == 0);
        var content = string.Empty;

        if (!isBinary)
        {
            try
            {
                content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                isBinary = true;
            }
        }

        if (content.Length > 0 && content[0] == '\uFEFF')
            content = content[1..];

        return new BuildFileContentResponse
        {
            Path = normalizedPath,
            Size = file.Size,
            Sha256 = file.Sha256,
            Policy = file.Policy,
            IsBinary = isBinary,
            Content = content
        };
    }

    public async Task<BuildFileInfo> SaveTextFileAsync(
        long id,
        SaveBuildFileRequest request,
        string actor,
        CancellationToken ct)
    {
        var path = NormalizePath(request.Path);
        var content = request.Content ?? string.Empty;
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);

        if (bytes.Length > MaxEditableBytes)
        {
            throw new BuildEditorException(
                413, "FILE_TOO_LARGE",
                $"Text edits are limited to {MaxEditableBytes / 1024 / 1024} MB.");
        }

        if (!IsEditable(path, bytes.Length))
        {
            throw new BuildEditorException(
                400, "NOT_TEXT_FILE",
                "Only known text files can be edited as text. Upload a replacement for binary files.");
        }

        var build = await GetBuildAsync(id, tracking: true, ct);
        EnsureEditable(build);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        return await PersistBytesAsync(build, path, bytes, sha, "edit_build_file", actor, ct);
    }

    public async Task<BuildFileInfo> UploadFileAsync(
        long id,
        string? requestedPath,
        IFormFile file,
        string actor,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            throw new BuildEditorException(400, "FILE_REQUIRED", "No file was uploaded.");

        if (file.Length > MaxUploadBytes)
        {
            throw new BuildEditorException(
                413, "FILE_TOO_LARGE",
                $"Uploads are limited to {MaxUploadBytes / 1024 / 1024} MB.");
        }

        var path = ResolveUploadPath(requestedPath, file.FileName);
        var build = await GetBuildAsync(id, tracking: true, ct);
        EnsureEditable(build);

        var tempPath = Path.Combine(Path.GetTempPath(), $"undermine-upload-{Guid.NewGuid():N}");
        try
        {
            await using (var target = File.Create(tempPath))
                await file.CopyToAsync(target, ct);

            var size = new FileInfo(tempPath).Length;
            var sha = await ComputeSha256Async(tempPath, ct);

            return await PersistFileAsync(
                build, path, tempPath, sha, size, "upload_build_file", actor, ct);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task DeleteFileAsync(long id, string path, string actor, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        var build = await GetBuildAsync(id, tracking: true, ct);
        EnsureEditable(build);

        var manifest = ParseManifest(build);
        var dbFile = build.Files.FirstOrDefault(f =>
            string.Equals(f.Path, normalizedPath, StringComparison.Ordinal));
        var manifestFile = manifest.Files.FirstOrDefault(f =>
            string.Equals(f.Path, normalizedPath, StringComparison.Ordinal));

        if (dbFile == null && manifestFile == null)
            throw new BuildEditorException(404, "BUILD_FILE_NOT_FOUND", "The file does not exist.");

        var s3Key = dbFile?.S3Key ?? BuildPathSafety.ToS3Key(build.BuildId, normalizedPath);

        if (dbFile != null)
            _context.BuildFiles.Remove(dbFile);

        if (manifestFile != null)
            manifest.Files.Remove(manifestFile);

        build.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        build.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        await _s3.RemoveFileIfUnusedAsync(s3Key, ct);
        TryDeleteLocalFile(build, normalizedPath);
        await _audit.LogAsync(actor, "delete_build_file", "build", build.Id,
            $"{build.BuildId}: {normalizedPath}", ct);
    }

    public async Task<BuildFileInfo> RenameFileAsync(
        long id,
        RenameBuildFileRequest request,
        string actor,
        CancellationToken ct)
    {
        var from = NormalizePath(request.From);
        var to = NormalizePath(request.To);

        if (string.Equals(from, to, StringComparison.Ordinal))
            return FindFileInfo(await GetBuildAsync(id, tracking: false, ct), from);

        var build = await GetBuildAsync(id, tracking: true, ct);
        EnsureEditable(build);

        var manifest = ParseManifest(build);
        var dbFile = build.Files.FirstOrDefault(f =>
            string.Equals(f.Path, from, StringComparison.Ordinal));
        var manifestFile = manifest.Files.FirstOrDefault(f =>
            string.Equals(f.Path, from, StringComparison.Ordinal));

        if (dbFile == null && manifestFile == null)
            throw new BuildEditorException(404, "BUILD_FILE_NOT_FOUND", "The source file does not exist.");

        if (build.Files.Any(f => string.Equals(f.Path, to, StringComparison.Ordinal)) ||
            manifest.Files.Any(f => string.Equals(f.Path, to, StringComparison.Ordinal)))
        {
            throw new BuildEditorException(409, "FILE_ALREADY_EXISTS", "The destination path already exists.");
        }

        var sourceKey = dbFile?.S3Key ?? BuildPathSafety.ToS3Key(build.BuildId, from);
        var destinationKey = BuildPathSafety.ToS3Key(build.BuildId, to);
        var sha = dbFile?.Sha256 ?? manifestFile?.Sha256 ?? string.Empty;
        var size = dbFile?.Size ?? manifestFile?.Size ?? 0;
        var policy = dbFile?.Policy ?? manifestFile?.Policy ?? BuildManifestService.ClassifyFile(to);

        var tempPath = Path.Combine(Path.GetTempPath(), $"undermine-rename-{Guid.NewGuid():N}");
        try
        {
            await _s3.DownloadFileAsync(sourceKey, tempPath, ct);
            await _s3.UploadFileAsync(tempPath, destinationKey, sha, ct);
        }
        catch (ObjectNotFoundException)
        {
            throw new BuildEditorException(404, "BUILD_FILE_NOT_FOUND", "The source file is missing from storage.");
        }
        finally
        {
            TryDelete(tempPath);
        }

        if (dbFile == null)
        {
            dbFile = new BuildFile
            {
                BuildInstanceId = build.Id,
                Path = to,
                Sha256 = sha,
                Size = size,
                Policy = policy,
                S3Key = destinationKey
            };
            _context.BuildFiles.Add(dbFile);
            build.Files.Add(dbFile);
        }
        else
        {
            dbFile.Path = to;
            dbFile.S3Key = destinationKey;
        }

        if (manifestFile == null)
        {
            manifestFile = new ClientManifestFile
            {
                Path = to,
                Sha256 = sha,
                Size = size,
                Policy = policy
            };
            manifest.Files.Add(manifestFile);
        }

        manifestFile.Path = to;
        manifestFile.Sha256 = sha;
        manifestFile.Size = size;
        manifestFile.Policy = policy;
        manifestFile.Url = _s3.GetPublicUrl(destinationKey);

        build.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        build.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        await _s3.RemoveFileIfUnusedAsync(sourceKey, ct);
        TryMoveLocalFile(build, from, to);
        await _audit.LogAsync(actor, "rename_build_file", "build", build.Id,
            $"{build.BuildId}: {from} -> {to}", ct);

        return new BuildFileInfo
        {
            Path = to,
            Name = Path.GetFileName(to),
            Size = size,
            Sha256 = sha,
            Policy = policy,
            Url = _s3.GetPublicUrl(destinationKey),
            Editable = IsEditable(to, size),
            ContentType = GetContentType(to)
        };
    }

    public async Task<BuildDownloadInfo> GetDownloadAsync(long id, string path, CancellationToken ct)
    {
        var build = await GetBuildAsync(id, tracking: false, ct);
        var normalizedPath = NormalizePath(path);
        var file = FindFile(build, normalizedPath);

        return new BuildDownloadInfo(
            file.S3Key,
            Path.GetFileName(normalizedPath),
            GetContentType(normalizedPath));
    }

    public Task DownloadToAsync(
        BuildDownloadInfo info,
        string localPath,
        CancellationToken ct) =>
        _s3.DownloadFileAsync(info.S3Key, localPath, ct);

    private async Task<BuildFileInfo> PersistBytesAsync(
        BuildInstance build,
        string path,
        byte[] bytes,
        string sha,
        string auditAction,
        string actor,
        CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"undermine-edit-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            return await PersistFileAsync(
                build, path, tempPath, sha, bytes.LongLength, auditAction, actor, ct);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<BuildFileInfo> PersistFileAsync(
        BuildInstance build,
        string path,
        string localPath,
        string sha,
        long size,
        string auditAction,
        string actor,
        CancellationToken ct)
    {
        var manifest = ParseManifest(build);
        var s3Key = BuildPathSafety.ToS3Key(build.BuildId, path);
        var policy = BuildManifestService.ClassifyFile(path);

        await _s3.UploadFileAsync(localPath, s3Key, sha, ct);

        var dbFile = build.Files.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.Ordinal));

        if (dbFile == null)
        {
            dbFile = new BuildFile { BuildInstanceId = build.Id, Path = path };
            _context.BuildFiles.Add(dbFile);
            build.Files.Add(dbFile);
        }

        dbFile.Sha256 = sha;
        dbFile.Size = size;
        dbFile.Policy = policy;
        dbFile.S3Key = s3Key;

        var manifestFile = manifest.Files.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.Ordinal));
        if (manifestFile == null)
        {
            manifestFile = new ClientManifestFile();
            manifest.Files.Add(manifestFile);
        }

        manifestFile.Path = path;
        manifestFile.Sha256 = sha;
        manifestFile.Size = size;
        manifestFile.Policy = policy;
        manifestFile.Url = _s3.GetPublicUrl(s3Key);

        build.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        build.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        TryWriteLocalFile(build, path, localPath);
        await _audit.LogAsync(actor, auditAction, "build", build.Id,
            $"{build.BuildId}: {path} ({size} bytes)", ct);

        return new BuildFileInfo
        {
            Path = path,
            Name = Path.GetFileName(path),
            Size = size,
            Sha256 = sha,
            Policy = policy,
            Url = _s3.GetPublicUrl(s3Key),
            Editable = IsEditable(path, size),
            ContentType = GetContentType(path)
        };
    }

    /// <summary>
    /// Loads the build and its files as two separate queries. A single AsNoTracking query with
    /// Include triggered expensive navigation fix-up for large packs (4000+ files took ~38s);
    /// splitting it keeps the editor responsive.
    /// </summary>
    private async Task<BuildInstance> GetBuildAsync(long id, bool tracking, CancellationToken ct)
    {
        var build = await GetBuildMetadataAsync(id, tracking, ct);

        var filesQuery = _context.BuildFiles.AsQueryable();
        if (!tracking)
            filesQuery = filesQuery.AsNoTracking();

        build.Files = await filesQuery
            .Where(f => f.BuildInstanceId == id)
            .ToListAsync(ct);

        return build;
    }

    private async Task<BuildInstance> GetBuildMetadataAsync(long id, bool tracking, CancellationToken ct)
    {
        var query = _context.BuildInstances.AsQueryable();
        if (!tracking)
            query = query.AsNoTracking();

        var build = await query.FirstOrDefaultAsync(b => b.Id == id, ct);

        return build ?? throw new BuildEditorException(404, "BUILD_NOT_FOUND", "Build not found.");
    }

    private static void EnsureEditable(BuildInstance build)
    {
        if (!string.Equals(build.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new BuildEditorException(
                409, "BUILD_NOT_EDITABLE",
                "Files can only be edited after the build has completed.");
        }

        if (string.IsNullOrWhiteSpace(build.ManifestJson))
        {
            throw new BuildEditorException(
                409, "MANIFEST_NOT_READY",
                "This build has no client manifest yet.");
        }
    }

    private static ClientManifestResponse ParseManifest(BuildInstance build)
    {
        if (string.IsNullOrWhiteSpace(build.ManifestJson))
            throw new BuildEditorException(409, "MANIFEST_NOT_READY", "This build has no client manifest yet.");

        try
        {
            var manifest = JsonSerializer.Deserialize<ClientManifestResponse>(
                               build.ManifestJson, ManifestJsonOptions)
                           ?? throw new BuildEditorException(500, "MANIFEST_INVALID", "The build manifest is empty.");

            manifest.Files ??= new List<ClientManifestFile>();
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new BuildEditorException(500, "MANIFEST_INVALID",
                "The stored build manifest could not be parsed: " + ex.Message);
        }
    }

    private List<BuildFileInfo> BuildFileList(BuildInstance build)
    {
        var dbByPath = build.Files
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        ClientManifestResponse? manifest = null;

        try
        {
            manifest = ParseManifest(build);
        }
        catch (BuildEditorException)
        {
            // The file list can still be shown from the database; editing will fail later.
        }

        if (manifest?.Files is { Count: > 0 })
        {
            return manifest.Files
                .Select(f =>
                {
                    dbByPath.TryGetValue(f.Path, out var db);
                    var s3Key = db?.S3Key ?? BuildPathSafety.ToS3Key(build.BuildId, f.Path);
                    return CreateInfo(f.Path, db?.Size ?? f.Size, db?.Sha256 ?? f.Sha256,
                        db?.Policy ?? f.Policy, s3Key);
                })
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
        }

        return build.Files
            .Select(f =>
            {
                var s3Key = f.S3Key ?? BuildPathSafety.ToS3Key(build.BuildId, f.Path);
                return CreateInfo(f.Path, f.Size, f.Sha256, f.Policy, s3Key);
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
    }

    private BuildFileInfo FindFileInfo(BuildInstance build, string path)
    {
        var file = FindFile(build, path);
        return CreateInfo(path, file.Size, file.Sha256, file.Policy, file.S3Key);
    }

    private (string S3Key, long Size, string Sha256, string Policy) FindFile(
        BuildInstance build,
        string path)
    {
        var dbFile = build.Files.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.Ordinal));

        ClientManifestResponse? manifest = null;
        try
        {
            manifest = ParseManifest(build);
        }
        catch (BuildEditorException)
        {
            // Fall back to the database row below.
        }

        var manifestFile = manifest?.Files.FirstOrDefault(f =>
            string.Equals(f.Path, path, StringComparison.Ordinal));

        if (dbFile == null && manifestFile == null)
            throw new BuildEditorException(404, "BUILD_FILE_NOT_FOUND", "The file does not exist.");

        return (
            dbFile?.S3Key ?? BuildPathSafety.ToS3Key(build.BuildId, path),
            dbFile?.Size ?? manifestFile?.Size ?? 0,
            dbFile?.Sha256 ?? manifestFile?.Sha256 ?? string.Empty,
            dbFile?.Policy ?? manifestFile?.Policy ?? BuildManifestService.ClassifyFile(path));
    }

    private BuildFileInfo CreateInfo(
        string path,
        long size,
        string sha,
        string policy,
        string s3Key) =>
        new()
        {
            Path = path,
            Name = Path.GetFileName(path),
            Size = size,
            Sha256 = sha,
            Policy = policy,
            Url = _s3.GetPublicUrl(s3Key),
            Editable = IsEditable(path, size),
            ContentType = GetContentType(path)
        };

    private static string NormalizePath(string? path)
    {
        try
        {
            return BuildPathSafety.NormalizeRelativePath(path);
        }
        catch (BuildPathException ex)
        {
            throw new BuildEditorException(400, "INVALID_PATH", ex.Message);
        }
    }

    private static string NormalizeDirectoryPath(string? path)
    {
        try
        {
            return BuildPathSafety.NormalizeDirectoryPath(path);
        }
        catch (BuildPathException ex)
        {
            throw new BuildEditorException(400, "INVALID_PATH", ex.Message);
        }
    }

    private static void UpdateMax(
        Dictionary<string, DateTimeOffset> values,
        string key,
        DateTimeOffset value)
    {
        if (!values.TryGetValue(key, out var existing) || value > existing)
            values[key] = value;
    }

    private static List<BuildBreadcrumb> BuildBreadcrumbs(string current)
    {
        var breadcrumbs = new List<BuildBreadcrumb>
        {
            new() { Name = "/", Path = string.Empty }
        };

        if (current.Length == 0)
            return breadcrumbs;

        var accumulated = string.Empty;
        foreach (var segment in current.Split('/'))
        {
            accumulated = accumulated.Length == 0
                ? segment
                : accumulated + "/" + segment;

            breadcrumbs.Add(new BuildBreadcrumb
            {
                Name = segment,
                Path = accumulated
            });
        }

        return breadcrumbs;
    }

    private static string ResolveUploadPath(string? requestedPath, string originalFileName)
    {
        var fileName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new BuildEditorException(400, "INVALID_PATH", "The uploaded file has no name.");

        if (string.IsNullOrWhiteSpace(requestedPath))
            return NormalizePath(fileName);

        // The admin panel sends an exact target path; a trailing slash means "put the uploaded
        // file into this folder" for convenience.
        var target = requestedPath.Trim().Replace('\\', '/');
        if (target.EndsWith('/'))
            target += fileName;

        return NormalizePath(target);
    }

    private static bool IsEditable(string path, long size) =>
        size <= MaxEditableBytes &&
        (TextExtensions.Contains(Path.GetExtension(path)) ||
         TextFileNames.Contains(Path.GetFileNameWithoutExtension(path)));

    private static string GetContentType(string path) =>
        ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType)
            ? contentType
            : "application/octet-stream";

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryWriteLocalFile(BuildInstance build, string relativePath, string sourcePath)
    {
        try
        {
            var buildDir = GetLocalBuildDirectory(build);
            if (!Directory.Exists(buildDir)) return;

            var target = BuildPathSafety.ResolveUnderRoot(buildDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourcePath, target, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update local build file {Path} for {BuildId}",
                relativePath, build.BuildId);
        }
    }

    private void TryDeleteLocalFile(BuildInstance build, string relativePath)
    {
        try
        {
            var buildDir = GetLocalBuildDirectory(build);
            if (!Directory.Exists(buildDir)) return;

            var target = BuildPathSafety.ResolveUnderRoot(buildDir, relativePath);
            if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete local build file {Path} for {BuildId}",
                relativePath, build.BuildId);
        }
    }

    private void TryDeleteLocalDirectory(BuildInstance build, string relativePath)
    {
        try
        {
            var buildDir = GetLocalBuildDirectory(build);
            if (!Directory.Exists(buildDir)) return;

            var target = BuildPathSafety.ResolveUnderRoot(buildDir, relativePath);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete local build directory {Path} for {BuildId}",
                relativePath, build.BuildId);
        }
    }

    private void TryMoveLocalFile(BuildInstance build, string from, string to)
    {
        try
        {
            var buildDir = GetLocalBuildDirectory(build);
            if (!Directory.Exists(buildDir)) return;

            var source = BuildPathSafety.ResolveUnderRoot(buildDir, from);
            if (!File.Exists(source)) return;

            var destination = BuildPathSafety.ResolveUnderRoot(buildDir, to);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename local build file {Path} for {BuildId}",
                from, build.BuildId);
        }
    }

    private string GetLocalBuildDirectory(BuildInstance build) =>
        Path.Combine(_config["Build:RootPath"] ?? "builds", build.BuildId);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort for temp files.
        }
    }
}

public sealed record BuildDownloadInfo(string S3Key, string FileName, string ContentType);

public sealed class BuildEditorException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    public BuildEditorException(int statusCode, string errorCode, string message) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
