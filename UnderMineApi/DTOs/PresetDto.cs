using System.ComponentModel.DataAnnotations;

namespace UnderMineApi.DTOs;

public class BuildPresetUpsertDto
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string GameVersion { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^(vanilla|fabric|forge|neoforge)$")]
    public string LoaderType { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? LoaderVersion { get; set; }
}

public class BuildPresetDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string? LoaderVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
