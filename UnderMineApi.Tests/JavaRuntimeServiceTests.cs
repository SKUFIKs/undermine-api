using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnderMineApi.Options;
using UnderMineApi.Services;
using Xunit;

namespace UnderMineApi.Tests;

public class JavaRuntimeServiceTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly string _assetJson;

        public int AssetRequests;
        public int ArchiveRequests;

        public StubHandler(byte[] archive, string assetJson)
        {
            _archive = archive;
            _assetJson = assetJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/v3/assets/latest/"))
            {
                Interlocked.Increment(ref AssetRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_assetJson, Encoding.UTF8, "application/json")
                });
            }

            Interlocked.Increment(ref ArchiveRequests);
            var content = new ByteArrayContent(_archive);
            content.Headers.ContentLength = _archive.Length;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
    }

    private static JavaRuntimeService CreateService(
        string root,
        HttpMessageHandler handler,
        int cacheHours = 24) =>
        new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new JavaRuntimeOptions
            {
                RootPath = root,
                AdoptiumApiBaseUrl = "https://api.adoptium.net",
                CacheHours = cacheHours,
                MinMajor = 8,
                MaxMajor = 64,
                MaxArchiveBytes = 50 * 1024 * 1024
            }),
            NullLogger<JavaRuntimeService>.Instance);

    private static string BuildAssetJson(byte[] archive)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

        return JsonSerializer.Serialize(new[]
        {
            new
            {
                binary = new
                {
                    os = "windows",
                    architecture = "x64",
                    image_type = "jre",
                    package = new
                    {
                        name = "jre.zip",
                        link = "https://github.com/adoptium/jre.zip",
                        checksum,
                        size = archive.Length
                    }
                },
                version = new
                {
                    semver = "21.0.7+6",
                    major = 21
                }
            }
        });
    }

    [Fact]
    public async Task GetAsync_DownloadsCachesAndReusesRuntime()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "undermine-api-java-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var archive = Encoding.UTF8.GetBytes("fake-jre-archive");
            var handler = new StubHandler(archive, BuildAssetJson(archive));
            var service = CreateService(root, handler);

            var first = await service.GetAsync(21, "windows", "x64", CancellationToken.None);

            Assert.NotNull(first);
            Assert.True(File.Exists(first!.FilePath));
            Assert.Equal(archive.Length, first.SizeBytes);
            Assert.Equal("application/zip", first.ContentType);
            Assert.Equal(1, handler.AssetRequests);
            Assert.Equal(1, handler.ArchiveRequests);
            Assert.StartsWith(root, first.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("jre.zip", first.FileName);

            var second = await service.GetAsync(21, "windows", "x64", CancellationToken.None);

            Assert.NotNull(second);
            Assert.Equal(first.FilePath, second!.FilePath);
            Assert.Equal(1, handler.AssetRequests);
            Assert.Equal(1, handler.ArchiveRequests);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GetAsync_RefreshesMetadataWithoutRedownloadingUnchangedArchive()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "undermine-api-java-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var archive = Encoding.UTF8.GetBytes("fake-jre-archive");
            var handler = new StubHandler(archive, BuildAssetJson(archive));
            var service = CreateService(root, handler, cacheHours: 0);

            Assert.NotNull(await service.GetAsync(21, "windows", "x64", CancellationToken.None));
            Assert.NotNull(await service.GetAsync(21, "windows", "x64", CancellationToken.None));

            Assert.Equal(2, handler.AssetRequests);
            Assert.Equal(1, handler.ArchiveRequests);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenUpstreamFails()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "undermine-api-java-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateService(root, new FailingHandler());

            var result = await service.GetAsync(17, "linux", "x64", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GetAsync_RejectsUnsupportedMajor()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "undermine-api-java-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = CreateService(root, new FailingHandler());

            Assert.Null(await service.GetAsync(7, "windows", "x64", CancellationToken.None));
            Assert.Null(await service.GetAsync(65, "windows", "x64", CancellationToken.None));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort for test cleanup.
        }
    }
}
