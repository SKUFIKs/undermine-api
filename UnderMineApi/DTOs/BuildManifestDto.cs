namespace UnderMineApi.DTOs;

/// <summary>
/// Represents a build manifest for client synchronization.
/// </summary>
public record BuildManifestDto(
    string BuildId,
    string DisplayName,
    string GameVersion,
    string LoaderType,
    string LoaderVersion,
    int JavaVersion,
    string MainClass,
    List<string> JvmArgs,
    List<string> GameArgs,
    List<ManifestFileDto> Files);

/// <summary>
/// Represents a single file entry in the build manifest.
/// </summary>
public record ManifestFileDto(
    string Path,
    string Sha256,
    long Size,
    string Policy,
    string Url);

/// <summary>
/// Synchronization policy for manifest files.
/// </summary>
public static class SyncPolicy
{
    public const string ForceSync = "FORCE_SYNC";
    public const string DownloadOnce = "DOWNLOAD_ONCE";
    public const string Preserve = "PRESERVE";
}
