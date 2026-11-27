using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnderMineApi.Extensions;
using UnderMineApi.Options;
using UnderMineApi.Services;
using Xunit;

namespace UnderMineApi.Tests;

public class RuntimeFlagTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void Defaults_MatchPrimaryNodeBehaviour()
    {
        var configuration = Config();

        Assert.True(RuntimeFlags.RunBuildWorker(configuration));
        Assert.True(RuntimeFlags.RunMigrations(configuration));
        Assert.False(RuntimeFlags.ManageS3Bucket(configuration));
    }

    [Fact]
    public void ReplicaSwitches_DisableWorkerAndMigrations()
    {
        var configuration = Config(
            ("Build:WorkerEnabled", "false"),
            ("Database:MigrateOnStartup", "false"),
            ("S3:ManageBucket", "false"));

        Assert.False(RuntimeFlags.RunBuildWorker(configuration));
        Assert.False(RuntimeFlags.RunMigrations(configuration));
        Assert.False(RuntimeFlags.ManageS3Bucket(configuration));
    }

    [Fact]
    public void ManageBucket_CanBeEnabledForMinioOrDev()
    {
        var configuration = Config(("S3:ManageBucket", "true"));

        Assert.True(RuntimeFlags.ManageS3Bucket(configuration));
    }
}

public class S3PublicUrlTests
{
    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static S3StorageService Create(string cdnBaseUrl) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3:Endpoint"] = "localhost:9000",
                ["S3:AccessKey"] = "minioadmin",
                ["S3:SecretKey"] = "minioadmin",
                ["S3:Bucket"] = "minecraft-builds",
                ["S3:CdnBaseUrl"] = cdnBaseUrl
            }).Build(),
            new DummyHttpClientFactory(),
            NullLogger<S3StorageService>.Instance);

    [Fact]
    public void PublicUrl_DoesNotIncludeBucketName()
    {
        var service = Create("https://cdn.example.com/");

        Assert.Equal(
            "https://cdn.example.com/builds/abc/mods/example.jar",
            service.GetPublicUrl("builds/abc/mods/example.jar"));
    }

    [Fact]
    public void PublicUrl_IsRelativeWhenCdnIsNotConfigured()
    {
        var service = Create("");

        Assert.Equal("/builds/abc/mods/example.jar",
            service.GetPublicUrl("builds/abc/mods/example.jar"));
    }
}

public class S3ListedKeyTests
{
    [Theory]
    [InlineData("builds%2Fabc%2Fmods%2F", true, "builds/abc/mods/")]
    [InlineData("builds%2Fabc%2Fempty%20folder%2F", true, "builds/abc/empty folder/")]
    [InlineData("builds/abc/file.jar", false, "builds/abc/file.jar")]
    [InlineData("builds/abc/100%file.jar", false, "builds/abc/100%file.jar")]
    public void NormalizeListedKey_DecodesDirectoryEntries(string key, bool isDirectory, string expected)
    {
        Assert.Equal(expected, S3StorageService.NormalizeListedKey(key, isDirectory));
    }
}

public class S3BucketManagementTests
{
    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static S3StorageService Create(Dictionary<string, string?> extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["S3:Endpoint"] = "127.0.0.1:1",
            ["S3:AccessKey"] = "test",
            ["S3:SecretKey"] = "test",
            ["S3:Bucket"] = "test-bucket"
        };

        foreach (var pair in extra)
            settings[pair.Key] = pair.Value;

        return new S3StorageService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new DummyHttpClientFactory(),
            NullLogger<S3StorageService>.Instance);
    }

    [Fact]
    public async Task EnsureBucket_IsNoOpWhenManagementIsDisabled()
    {
        var service = Create(new Dictionary<string, string?>
        {
            ["S3:ManageBucket"] = "false"
        });

        // The endpoint is unreachable: this only succeeds because no S3 call is made.
        await service.EnsureBucketAsync();
    }

    [Fact]
    public async Task EnsureBucket_IsNoOpByDefault()
    {
        var service = Create(new Dictionary<string, string?>());

        await service.EnsureBucketAsync();
    }
}

public class ForwardedHeadersTests
{
    [Fact]
    public void ConfiguredNetwork_IsTrustedAndForwardLimitIsOne()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownNetworks"] = "172.28.0.0/24",
            ["ForwardedHeaders:ForwardLimit"] = "1"
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddConfiguredForwardedHeaders(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(1, options.ForwardLimit);
        var network = Assert.Single(options.KnownNetworks);
        Assert.Equal(24, network.PrefixLength);
        Assert.Equal("172.28.0.0", network.Prefix.ToString());
    }
}

public class CorsTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "UnderMineApi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void ProductionWithoutOrigins_IsDisabled()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.False(CorsExtensions.IsCorsEnabled(configuration, new FakeEnvironment()));
    }

    [Fact]
    public void DevelopmentWithoutOrigins_IsEnabled()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.True(CorsExtensions.IsCorsEnabled(
            configuration, new FakeEnvironment { EnvironmentName = "Development" }));
    }

    [Fact]
    public void InvalidOrigin_Throws()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CORS:AllowedOrigins"] = "not-a-valid-origin"
        }).Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddConfiguredCors(configuration, new FakeEnvironment()));
    }
}
