using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using UnderMineApi.Options;

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
        private readonly bool _manageBucket;
        private readonly string _endpoint;
        private readonly bool _useSsl;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<S3StorageService> _logger;

        public S3StorageService(
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            ILogger<S3StorageService> logger)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _bucketName = config["S3:Bucket"] ?? "minecraft-builds";
            _cdnBaseUrl = config["S3:CdnBaseUrl"]?.TrimEnd('/') ?? "";
            // The launcher downloads files directly from the CDN over plain HTTP, so objects
            // must be readable anonymously. Disable only if a signed/proxied delivery is used.
            _publicRead = !bool.TryParse(config["S3:PublicRead"], out var publicRead) || publicRead;
            // Garage provisions the bucket, key and website once from Ansible. The API must not
            // try to create buckets or rewrite policies unless explicitly asked to (MinIO/dev).
            _manageBucket = RuntimeFlags.ManageS3Bucket(config);

            // MinioClient expects host[:port] without a scheme. Accept both
            // "http://127.0.0.1:3900" (compose-friendly) and "127.0.0.1:3900".
            var endpointValue = (config["S3:Endpoint"] ?? "localhost:9000").Trim();
            var useSsl = bool.TryParse(config["S3:UseSsl"], out var ssl) && ssl;

            if (Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpointUri) &&
                (endpointUri.Scheme == Uri.UriSchemeHttp || endpointUri.Scheme == Uri.UriSchemeHttps))
            {
                _endpoint = endpointUri.IsDefaultPort
                    ? endpointUri.Host
                    : $"{endpointUri.Host}:{endpointUri.Port}";
                useSsl = endpointUri.Scheme == Uri.UriSchemeHttps;
            }
            else
            {
                _endpoint = endpointValue.TrimEnd('/');
            }

            _useSsl = useSsl;
            var region = config["S3:Region"] ?? "us-east-1";

            // No weak default credentials: S3:AccessKey/SecretKey are validated at startup.
            var accessKey = config["S3:AccessKey"] ?? string.Empty;
            var secretKey = config["S3:SecretKey"] ?? string.Empty;

            _minioClient = new MinioClient()
                .WithEndpoint(_endpoint)
                .WithCredentials(accessKey, secretKey)
                .WithRegion(region)
                .WithSSL(_useSsl)
                .Build();
        }

        /// <summary>
        /// Ensures the bucket exists (and is readable by the launcher when public read is enabled).
        /// </summary>
        public async Task EnsureBucketAsync(CancellationToken ct = default)
        {
            if (!_manageBucket)
            {
                _logger.LogDebug(
                    "S3 bucket management is disabled (S3:ManageBucket=false); skipping bucket setup");
                return;
            }

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
        /// Connectivity probe for the health endpoint. Uses MinIO's dedicated health endpoint over
        /// plain HTTP: the Minio client operations can silently return default results when the
        /// server is unreachable, which made them unsuitable for a health check.
        /// </summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            var scheme = _useSsl ? "https" : "http";
            var url = $"{scheme}://{_endpoint}/minio/health/live";

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("S3/MinIO health probe timed out ({Url})", url);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "S3/MinIO health probe failed ({Url})", url);
                return false;
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

            // Upload with metadata - MinIO v6 API uses WithStreamData. A zero-length file
            // must be uploaded with ObjectSize=-1: the client rejects size 0, while -1 makes it
            // send an empty (chunked) body that MinIO stores as a real 0-byte object.
            await using var fileStream = File.OpenRead(localPath);
            var uploadArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(s3Key)
                .WithStreamData(fileStream)
                .WithObjectSize(fileStream.Length == 0 ? -1 : fileStream.Length)
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
        ///
        /// nginx exposes the bucket root as <c>/builds/</c> (it strips the bucket name), so the
        /// public URL must NOT include <c>S3:Bucket</c>: <c>https://cdn/builds/{buildId}/...</c>.
        /// Old manifests that still contain <c>/minecraft-builds/builds/...</c> keep working
        /// through the generic <c>location /</c> proxy in nginx.
        /// </summary>
        public string GetPublicUrl(string s3Key)
        {
            return string.IsNullOrEmpty(_cdnBaseUrl)
                ? $"/{s3Key}"
                : $"{_cdnBaseUrl}/{s3Key}";
        }

        /// <summary>Downloads an object from S3 to a local file.</summary>
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

        /// <summary>
        /// Downloads an object into memory, refusing objects larger than <paramref name="maxBytes"/>.
        /// Intended for the admin text editor, not for arbitrary build files.
        /// </summary>
        public async Task<byte[]> DownloadBytesAsync(
            string s3Key, long maxBytes, CancellationToken ct = default)
        {
            var stat = await _minioClient.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucketName).WithObject(s3Key), ct);

            if (stat.Size > maxBytes)
                throw new InvalidOperationException("Object is larger than the allowed size.");

            using var memory = new MemoryStream();
            var tooLarge = false;

            var args = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(s3Key)
                .WithCallbackStream(source =>
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (memory.Length + read > maxBytes)
                        {
                            tooLarge = true;
                            throw new InvalidOperationException("Object is larger than the allowed size.");
                        }

                        memory.Write(buffer, 0, read);
                    }
                });

            await _minioClient.GetObjectAsync(args, ct);

            if (tooLarge)
                throw new InvalidOperationException("Object is larger than the allowed size.");

            return memory.ToArray();
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
        /// Lists every object under a prefix. Recursive listing is used because the admin file
        /// manager navigates paths itself; empty folders are represented by keys ending in "/".
        /// </summary>
        public IAsyncEnumerable<S3ObjectInfo> ListObjectsAsync(
            string prefix,
            CancellationToken ct = default) =>
            ListObjectsInternalAsync(prefix, recursive: true, ct);

        /// <summary>
        /// Lists only one directory level. Used by the file manager: asking MinIO for every
        /// object under a build is very slow for large packs (thousands of assets).
        /// </summary>
        public IAsyncEnumerable<S3ObjectInfo> ListDirectoryAsync(
            string prefix,
            CancellationToken ct = default) =>
            ListObjectsInternalAsync(prefix, recursive: false, ct);

        private async IAsyncEnumerable<S3ObjectInfo> ListObjectsInternalAsync(
            string prefix,
            bool recursive,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var args = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix(prefix)
                .WithRecursive(recursive);

            await foreach (var item in _minioClient.ListObjectsEnumAsync(args, ct))
            {
                ct.ThrowIfCancellationRequested();

                DateTimeOffset? lastModified = item.LastModifiedDateTime.HasValue
                    ? new DateTimeOffset(item.LastModifiedDateTime.Value.ToUniversalTime())
                    : null;

                yield return new S3ObjectInfo(
                    item.Key,
                    (long)item.Size,
                    lastModified,
                    item.IsDir);
            }
        }

        /// <summary>
        /// Creates a folder marker. S3 has no real directories, so a tiny object whose key ends
        /// with "/" is used to keep empty folders visible in the admin file manager.
        /// </summary>
        public async Task PutDirectoryMarkerAsync(string s3Key, CancellationToken ct = default)
        {
            if (!s3Key.EndsWith('/'))
                throw new ArgumentException("Directory marker key must end with '/'.", nameof(s3Key));

            // A folder marker is a key ending in "/". MinIO's multipart path (ObjectSize=-1)
            // rejects such keys, so use a normal one-byte PUT; the listing code ignores its size.
            var bytes = new byte[] { 0 };
            using var stream = new MemoryStream(bytes);

            var args = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(s3Key)
                .WithStreamData(stream)
                .WithObjectSize(bytes.Length)
                .WithHeaders(new Dictionary<string, string>
                {
                    ["x-amz-meta-directory"] = "true"
                });

            await _minioClient.PutObjectAsync(args, ct);
        }

        /// <summary>Deletes every object under a prefix and returns the number removed.</summary>
        public async Task<int> DeletePrefixAsync(string prefix, CancellationToken ct = default)
        {
            var keys = new List<string>();
            await foreach (var item in ListObjectsAsync(prefix, ct))
                keys.Add(item.Key);

            await DeleteObjectsAsync(keys, ct);
            return keys.Count;
        }

        public async Task<bool> ObjectExistsAsync(string s3Key, CancellationToken ct = default)
        {
            try
            {
                await _minioClient.StatObjectAsync(
                    new StatObjectArgs().WithBucket(_bucketName).WithObject(s3Key), ct);
                return true;
            }
            catch
            {
                return false;
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

    public sealed record S3ObjectInfo(
        string Key,
        long Size,
        DateTimeOffset? LastModified,
        bool IsDirectory);
}
