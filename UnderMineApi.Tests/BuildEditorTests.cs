using System;
using System.IO;
using UnderMineApi.Services;
using Xunit;

namespace UnderMineApi.Tests;

public class BuildPathSafetyTests
{
    [Theory]
    [InlineData("mods/example.jar", "mods/example.jar")]
    [InlineData("config\\foo.json", "config/foo.json")]
    [InlineData("  assets/minecraft/lang/en_us.json  ", "assets/minecraft/lang/en_us.json")]
    public void NormalizeRelativePath_AcceptsValidPaths(string input, string expected)
    {
        Assert.Equal(expected, BuildPathSafety.NormalizeRelativePath(input));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("mods/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("mods//file.txt")]
    [InlineData("mods/./file.txt")]
    [InlineData("mods/file.")]
    public void NormalizeRelativePath_RejectsTraversalAndInvalidSegments(string path)
    {
        Assert.Throws<BuildPathException>(() => BuildPathSafety.NormalizeRelativePath(path));
    }

    [Fact]
    public void ResolveUnderRoot_StaysInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "undermine-build-editor-tests");

        var resolved = BuildPathSafety.ResolveUnderRoot(root, "mods/example.jar");

        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("mods", "example.jar"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToS3Key_UsesNormalizedPath()
    {
        Assert.Equal(
            "builds/abc123/mods/example.jar",
            BuildPathSafety.ToS3Key("abc123", "mods\\example.jar"));
    }
}

public class BuildFilePolicyTests
{
    [Theory]
    [InlineData("mods/example.jar", "FORCE_SYNC")]
    [InlineData("config/example.toml", "FORCE_SYNC")]
    [InlineData("shaderpacks/pack.zip", "FORCE_SYNC")]
    [InlineData("saves/world/level.dat", "PRESERVE")]
    [InlineData("options.txt", "DOWNLOAD_ONCE")]
    public void ClassifyFile_AppliesLauncherPolicies(string path, string expected)
    {
        Assert.Equal(expected, BuildManifestService.ClassifyFile(path));
    }
}
