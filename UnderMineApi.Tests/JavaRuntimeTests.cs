using UnderMineApi.Services;
using Xunit;

namespace UnderMineApi.Tests;

public class JavaRuntimeCatalogTests
{
    private const string ValidAssetJson = """
    [
      {
        "binary": {
          "os": "windows",
          "architecture": "x64",
          "image_type": "jre",
          "package": {
            "name": "OpenJDK21U-jre_x64_windows_hotspot_21.0.7_6.zip",
            "link": "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.7%2B6/OpenJDK21U-jre_x64_windows_hotspot_21.0.7_6.zip",
            "checksum": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "size": 50123456
          }
        },
        "version": {
          "semver": "21.0.7+6",
          "major": 21
        }
      }
    ]
    """;

    [Theory]
    [InlineData("Windows", "AMD64", "windows", "x64")]
    [InlineData("win", "x86_64", "windows", "x64")]
    [InlineData("linux", "arm64", "linux", "aarch64")]
    [InlineData("macos", "aarch64", "mac", "aarch64")]
    [InlineData("osx", "x86", "mac", "x32")]
    [InlineData("linux", "arm", "linux", "arm")]
    public void NormalizePlatform_MapsAliases(
        string os, string arch, string expectedOs, string expectedArch)
    {
        var ok = JavaRuntimeCatalog.TryNormalizePlatform(
            os, arch, out var normalizedOs, out var normalizedArch, out var error);

        Assert.True(ok, error);
        Assert.Equal(expectedOs, normalizedOs);
        Assert.Equal(expectedArch, normalizedArch);
    }

    [Theory]
    [InlineData("freebsd", "x64")]
    [InlineData("linux", "sparc")]
    [InlineData("", "x64")]
    public void NormalizePlatform_RejectsUnknownValues(string os, string arch)
    {
        Assert.False(JavaRuntimeCatalog.TryNormalizePlatform(
            os, arch, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ParseAsset_ReadsAllFields()
    {
        var ok = JavaRuntimeCatalog.TryParseAsset(
            ValidAssetJson, 21, "windows", "x64", out var asset, out var error);

        Assert.True(ok, error);
        Assert.Equal("OpenJDK21U-jre_x64_windows_hotspot_21.0.7_6.zip", asset.FileName);
        Assert.Equal(50123456, asset.SizeBytes);
        Assert.Equal("21.0.7+6", asset.Version);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            asset.Sha256);
    }

    [Theory]
    [InlineData("http://github.com/adoptium/file.zip")]
    [InlineData("https://evil.example.com/file.zip")]
    public void ParseAsset_RejectsUntrustedDownloadUrl(string link)
    {
        var json = ValidAssetJson.Replace(
            "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.7%2B6/OpenJDK21U-jre_x64_windows_hotspot_21.0.7_6.zip",
            link);

        Assert.False(JavaRuntimeCatalog.TryParseAsset(
            json, 21, "windows", "x64", out _, out var error));
        Assert.Contains("untrusted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAsset_RejectsPathTraversalFileName()
    {
        var json = ValidAssetJson.Replace(
            "OpenJDK21U-jre_x64_windows_hotspot_21.0.7_6.zip",
            "../evil.zip");

        Assert.False(JavaRuntimeCatalog.TryParseAsset(
            json, 21, "windows", "x64", out _, out _));
    }

    [Fact]
    public void ParseAsset_RejectsWrongPlatformOrVersion()
    {
        Assert.False(JavaRuntimeCatalog.TryParseAsset(
            ValidAssetJson, 21, "linux", "x64", out _, out _));

        Assert.False(JavaRuntimeCatalog.TryParseAsset(
            ValidAssetJson, 17, "windows", "x64", out _, out _));
    }

    [Fact]
    public void ParseAsset_RejectsInvalidChecksum()
    {
        var json = ValidAssetJson.Replace(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "not-a-checksum");

        Assert.False(JavaRuntimeCatalog.TryParseAsset(
            json, 21, "windows", "x64", out _, out _));
    }

    [Theory]
    [InlineData("jre.zip", true)]
    [InlineData("jre.tar.gz", true)]
    [InlineData("jre.exe", false)]
    [InlineData("../jre.zip", false)]
    [InlineData("", false)]
    public void SafeArchiveName_OnlyAllowsArchiveExtensions(string name, bool expected)
    {
        Assert.Equal(expected, JavaRuntimeCatalog.IsSafeArchiveName(name, out _));
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("objects.githubusercontent.com", true)]
    [InlineData("api.adoptium.net", true)]
    [InlineData("evil.example.com", false)]
    public void DownloadHost_IsAllowlisted(string host, bool expected)
    {
        Assert.Equal(expected, JavaRuntimeCatalog.IsAllowedDownloadHost(host));
    }
}
