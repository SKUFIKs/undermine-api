using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Domain.Entities;

public enum ModrinthSourceType
{
    Modrinth,
    CustomUpload
}

public enum ModTargetFolder
{
    mods,
    shaderpacks,
    resourcepacks
}

[Table("build_presets")]
public class BuildPreset
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Column("game_version")]
    [MaxLength(50)]
    public string GameVersion { get; set; } = string.Empty;

    [Required]
    [Column("loader_type")]
    [MaxLength(50)]
    public string LoaderType { get; set; } = string.Empty;

    [Column("loader_version")]
    [MaxLength(100)]
    public string? LoaderVersion { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<BuildPresetMod> Mods { get; set; } = new List<BuildPresetMod>();
}

[Table("build_preset_mods")]
public class BuildPresetMod
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [Column("build_preset_id")]
    public long BuildPresetId { get; set; }

    [Required]
    [Column("source_type")]
    [MaxLength(50)]
    public string SourceType { get; set; } = "Modrinth"; // Modrinth or CustomUpload

    [Required]
    [Column("target_folder")]
    [MaxLength(50)]
    public string TargetFolder { get; set; } = "mods"; // mods, shaderpacks, resourcepacks

    [Column("modrinth_slug_or_id")]
    [MaxLength(200)]
    public string? ModrinthSlugOrId { get; set; }

    [Column("custom_file_name")]
    [MaxLength(500)]
    public string? CustomFileName { get; set; }

    [Column("custom_file_s3_key")]
    [MaxLength(500)]
    public string? CustomFileS3Key { get; set; }

    [Column("sha256_hash")]
    [MaxLength(100)]
    public string? Sha256Hash { get; set; }

    [Column("file_size")]
    public long? FileSize { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("BuildPresetId")]
    public virtual BuildPreset BuildPreset { get; set; } = null!;
}
