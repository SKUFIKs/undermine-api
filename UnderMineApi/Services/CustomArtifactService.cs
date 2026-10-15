using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace UnderMineApi.Services;

public class CustomArtifactService
{
    private readonly S3StorageService _s3Storage;
    private readonly ILogger<CustomArtifactService> _logger;

    public CustomArtifactService(S3StorageService s3Storage, ILogger<CustomArtifactService> logger)
    {
        _s3Storage = s3Storage;
        _logger = logger;
    }

    public async Task<(string S3Key, string Sha256, long Size, string FileName)> UploadCustomArtifactAsync(
        IFormFile file, string targetFolder, CancellationToken ct = default)
    {
        if (file.Length == 0)
        {
            throw new ArgumentException("File is empty");
        }

        if (file.Length > 200 * 1024 * 1024) // 200MB
        {
            throw new ArgumentException("File size exceeds 200MB limit");
        }

        var fileName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // Validate by target folder and extension
        if (targetFolder == "mods")
        {
            if (extension != ".jar")
            {
                throw new ArgumentException("Mods must be .jar files");
            }
        }
        else if (targetFolder == "shaderpacks")
        {
            if (extension != ".zip")
            {
                throw new ArgumentException("Shaderpacks must be .zip files");
            }
        }
        else if (targetFolder == "resourcepacks")
        {
            if (extension != ".zip" && extension != ".jar")
            {
                throw new ArgumentException("Resourcepacks must be .zip files");
            }
        }
        else
        {
            throw new ArgumentException("Invalid target folder");
        }

        // Compute SHA-256 and save to temp
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await file.CopyToAsync(stream, ct);
            }

            string sha256;
            await using (var shaStream = File.OpenRead(tempPath))
            using (var sha = SHA256.Create())
            {
                var hash = await sha.ComputeHashAsync(shaStream, ct);
                sha256 = Convert.ToHexString(hash).ToLowerInvariant();
            }

            var s3Key = $"custom-artifacts/{sha256}{extension}";
            await _s3Storage.UploadFileAsync(tempPath, s3Key, sha256, ct);

            var size = new FileInfo(tempPath).Length;
            return (s3Key, sha256, size, fileName);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }
}
