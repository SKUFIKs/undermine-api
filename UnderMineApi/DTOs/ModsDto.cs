using System.ComponentModel.DataAnnotations;
using UnderMineApi.Domain.Entities;

namespace UnderMineApi.DTOs;

public class ModPresetModDto
{
    public Guid Id { get; set; }
    public long BuildPresetId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public string? ModrinthSlugOrId { get; set; }
    public string? CustomFileName { get; set; }
    public string? CustomFileS3Key { get; set; }
    public string? Sha256Hash { get; set; }
    public long? FileSize { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AddModrinthModRequest
{
    [Required]
    public string SlugOrId { get; set; } = string.Empty;

    public string TargetFolder { get; set; } = "mods";
}

public class ModrinthSearchRequest
{
    [Required]
    public string Query { get; set; } = string.Empty;

    [Required]
    public string McVersion { get; set; } = string.Empty;

    [Required]
    public string Loader { get; set; } = string.Empty;
}

public class ToggleModRequest
{
    public bool? Enabled { get; set; }
}
