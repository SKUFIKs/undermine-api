using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;

namespace UnderMineApi.Services
{
    /// <summary>
    /// Manages file uploads to S3-compatible storage (MinIO / AWS S3) with deduplication.
    /// </summary>
    public class S3StorageService
    {
        private readonly IMinioClient _minioClient;
        private readonly string _bucketName;
        private readonly string _cdnBaseUrl;
        private readonly bool _publicRead;
        private readonly ILogger<S3StorageService> _logger;

        public S3StorageService(IConfiguration config, ILogger<S3StorageService> logger)
        {
            _logger = logger;
            _bucketName = config["S3:Bucket"] ?? "minecraft-builds";
            _cdnBaseUrl = config["S3:CdnBaseUrl"]?.TrimEnd('/') ?? "";
            // The launcher downloads files directly from the CDN over plain HTTP, so objects
            // must be readable anonymously. Disable only if a signed/proxied delivery is used.
            _publicRead = !bool.TryParse(config["S3:PublicRead"], out var publicRead) || publicRead;

            var endpoint = config["S3:Endpoint"] ?? "localhost:9000";
            var accessKey = config["S3:AccessKey"] ?? "minioadmin";
            var secretKey = config["S3:SecretKey"] ?? "minioadmin";
            var useSsl = bool.TryParse(config["S3:UseSsl"], out var ssl) && ssl;

            _minioClient = new MinioClient()
                .WithEndpoint(endpoint)
                .WithCredentials(accessKey, secretKey)
                .WithSSL(useSsl)
                .Build();
        }

        /// <summary>
        /// Ensures the bucket exists (and is readable by the launcher when public read is enabled).
        /// </summary>
        public async Task EnsureBucketAsync(CancellationToken ct = default)
        {
            try
            {
                var exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName), ct);
                if (!exists)
                {
                    await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName), ct);
                    _logger.LogInformation("Created S3 bucket: {Bucket}", _bucketName);
                }

                if (_publicRead)
                {
                    await _minioClient.SetPolicyAsync(
                        new SetPolicyArgs().WithBucket(_bucketName).WithPolicy(BuildPublicReadPolicy()), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure S3 bucket exists");
                throw;
            }
        }

        /// <summary>
        /// Read-only anonymous policy: the launcher fetches build files without credentials,
        /// so a freshly created bucket must not keep its default (private) access.
        /// </summary>
        private string BuildPublicReadPolicy() => $$"""
        {
          "Version": "2012-10-17",
          "Statement": [
            {
              "Effect": "Allow",
              "Principal": { "AWS": ["*"] },
              "Action": ["s3:GetObject"],
              "Resource": ["arn:aws:s3:::{{_bucketName}}/*"]
            }
          ]
        }
        """;

        /// <summary>
        /// Uploads a file to S3 with deduplication by SHA-256 hash.
        /// Returns the S3 key and whether the file was actually uploaded (false = deduplicated).
        /// </summary>
        public async Task<(string S3Key, bool Uploaded)> UploadFileAsync(
            string localPath, string s3Key, string sha256, CancellationToken ct = default)
        {
            // Check if file already exists (deduplication)
            try
            {
                var statArgs = new StatObjectArgs().WithBucket(_bucketName).WithObject(s3Key);
                var stat = await _minioClient.StatObjectAsync(statArgs, ct);

                // If file exists and has matching ETag/metadata, skip upload
                if (stat.MetaData.TryGetValue("x-amz-meta-sha256", out var existingHash) &&
                    existingHash == sha256)
                {
                    _logger.LogDebug("File deduplicated (already in S3): {S3Key}", s3Key);
                    return (s3Key, false);
                }
            }
            catch
            {
                // File doesn't exist, proceed with upload
            }

            // Upload with metadata - MinIO v6 API uses WithStreamData
            await using var fileStream = File.OpenRead(localPath);
            var uploadArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(s3Key)
                .WithStreamData(fileStream)
                .WithObjectSize(fileStream.Length)
                .WithHeaders(new Dictionary<string, string>
                {
                    ["x-amz-meta-sha256"] = sha256
                });

            await _minioClient.PutObjectAsync(uploadArgs, ct);
            _logger.LogDebug("Uploaded to S3: {S3Key}", s3Key);

            return (s3Key, true);
        }

        /// <summary>
        /// Gets the public URL for a file in S3.
        /// </summary>
        public string GetPublicUrl(string s3Key)
        {
            return string.IsNullOrEmpty(_cdnBaseUrl)
                ? $"/{_bucketName}/{s3Key}"
                : $"{_cdnBaseUrl}/{_bucketName}/{s3Key}";
        }

        /// <summary>
        /// Removes objects from S3. Failures are logged and swallowed so that deleting a build
        /// never fails because a single object is already gone.
        /// </summary>
        public async Task DownloadFileAsync(string s3Key, string localPath, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            using var stream = File.Create(localPath);
            var args = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(s3Key)
                .WithCallbackStream(s => s.CopyTo(stream));
            await _minioClient.GetObjectAsync(args, ct);
        }

        public async Task RemoveFileIfUnusedAsync(string s3Key, CancellationToken ct = default)
        {
            // Simple removal - caller is responsible for checking usage
            try
            {
                var args = new RemoveObjectArgs().WithBucket(_bucketName).WithObject(s3Key);
                await _minioClient.RemoveObjectAsync(args, ct);
            }
            catch
            {
                // Ignore if not found
            }
        }

        public async Task DeleteObjectsAsync(IEnumerable<string> s3Keys, CancellationToken ct = default)
        {
            foreach (var key in s3Keys.Where(k => !string.IsNullOrEmpty(k)).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _minioClient.RemoveObjectAsync(
                        new RemoveObjectArgs().WithBucket(_bucketName).WithObject(key), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete object from S3: {S3Key}", key);
                }
            }
        }

        /// <summary>
        /// Checks if a file exists in S3 by its hash (for deduplication).
        /// </summary>
        public async Task<bool> FileExistsAsync(string s3Key, string sha256, CancellationToken ct = default)
        {
            try
            {
                var stat = await _minioClient.StatObjectAsync(
                    new StatObjectArgs().WithBucket(_bucketName).WithObject(s3Key), ct);
                return stat.MetaData.TryGetValue("x-amz-meta-sha256", out var existingHash) && existingHash == sha256;
            }
            catch
            {
                return false;
            }
        }
    }
}
