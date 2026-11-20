using System.ComponentModel.DataAnnotations;

namespace UnderMineApi.DTOs;

public class UpdateBuildSettingsRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? LoaderVersion { get; set; }

    public long? BuildPresetId { get; set; }
}

public class BuildEditorResponse
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string McVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string? LoaderVersion { get; set; }
    public long? BuildPresetId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string BuildKey { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<BuildFileInfo> Files { get; set; } = new();
}

public class BuildFileInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Policy { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Editable { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public DateTimeOffset? ModifiedAt { get; set; }
}

public class BuildDirectoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset? ModifiedAt { get; set; }
}

public class BuildBreadcrumb
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public class BuildDirectoryResponse
{
    public string Path { get; set; } = string.Empty;
    public int TotalFileCount { get; set; }
    public long TotalSize { get; set; }
    public List<BuildBreadcrumb> Breadcrumbs { get; set; } = new();
    public List<BuildDirectoryEntry> Directories { get; set; } = new();
    public List<BuildFileInfo> Files { get; set; } = new();
}

public class CreateDirectoryRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;
}

public class BuildFileContentResponse
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Policy { get; set; } = string.Empty;
    public bool IsBinary { get; set; }
    public string Content { get; set; } = string.Empty;
}

public class SaveBuildFileRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}

public class RenameBuildFileRequest
{
    [Required]
    public string From { get; set; } = string.Empty;

    [Required]
    public string To { get; set; } = string.Empty;
}
