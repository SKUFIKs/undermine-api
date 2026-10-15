using System.ComponentModel.DataAnnotations;

namespace UnderMineApi.DTOs;

public class CreateBuildRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string McVersion { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^(vanilla|fabric|forge|neoforge)$")]
    public string Loader { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? LoaderVersion { get; set; }

    public long? BuildPresetId { get; set; }
}

public class BuildResponse
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string McVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string? LoaderVersion { get; set; }
    public long? BuildPresetId { get; set; }
    /// <summary>"Standard" or "ModpackImport" - pipeline scenario of this build.</summary>
    public string BuildType { get; set; } = "Standard";
    public string Status { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? FileCount { get; set; }
    public long? TotalSize { get; set; }

    // Live build progress, updated while the build worker processes this build.
    public int? ProgressPercent { get; set; }
    public string? CurrentStep { get; set; }
    public string? CurrentFile { get; set; }
    public long? BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public double? DownloadSpeed { get; set; } // bytes per second
    public int? DownloadedFiles { get; set; }
    public int? TotalFiles { get; set; }
}

public class BuildLogResponse
{
    public long Id { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ClientManifestResponse
{
    public string BuildId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string? LoaderVersion { get; set; }
    public int JavaVersion { get; set; } = 17;
    public string MainClass { get; set; } = string.Empty;
    public List<string> JvmArgs { get; set; } = new();
    public List<string> GameArgs { get; set; } = new();
    public string BaseUrl { get; set; } = string.Empty;
    public List<ClientManifestFile> Files { get; set; } = new();
}

public class ClientManifestFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Policy { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
