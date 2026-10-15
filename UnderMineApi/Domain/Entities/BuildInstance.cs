using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Domain.Entities;

[Table("build_instances")]
public class BuildInstance
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Column("mc_version")]
    [MaxLength(50)]
    public string McVersion { get; set; } = string.Empty;

    [Required]
    [Column("loader_type")]
    [MaxLength(50)]
    public string LoaderType { get; set; } = string.Empty; // vanilla, fabric, forge, neoforge

    [Column("loader_version")]
    [MaxLength(100)]
    public string? LoaderVersion { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "pending"; // pending, downloading, installing, hashing, uploading, completed, failed

    // Live build progress (written by the build worker while a build is being processed,
    // read by the admin panel while it polls /builds).
    [Column("progress_percent")]
    public int? ProgressPercent { get; set; }

    [Column("current_step")]
    [MaxLength(300)]
    public string? CurrentStep { get; set; }

    [Column("current_file")]
    [MaxLength(800)]
    public string? CurrentFile { get; set; }

    [Column("bytes_downloaded")]
    public long? BytesDownloaded { get; set; }

    [Column("total_bytes")]
    public long? TotalBytes { get; set; }

    [Column("download_speed")]
    public double? DownloadSpeed { get; set; } // bytes per second

    [Column("downloaded_files")]
    public int? DownloadedFiles { get; set; }

    [Column("total_files")]
    public int? TotalFiles { get; set; }

    [Column("build_id")]
    [MaxLength(100)]
    public string BuildId { get; set; } = string.Empty; // GUID for S3 path

    [Column("manifest_json")]
    public string? ManifestJson { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [Column("build_preset_id")]
    public long? BuildPresetId { get; set; }

    // JSON import descriptor (ModpackImportInfo). Non-null switches the build worker
    // to the BuildType.ModpackImport scenario (.mrpack modpack import).
    [Column("modpack_import")]
    public string? ModpackImport { get; set; }

    // Navigation properties
    public virtual ICollection<BuildFile> Files { get; set; } = new List<BuildFile>();
    [ForeignKey("BuildPresetId")]
    public virtual BuildPreset? BuildPreset { get; set; }
}

[Table("build_files")]
public class BuildFile
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("build_id")]
    public long BuildInstanceId { get; set; }

    [Required]
    [Column("path")]
    [MaxLength(500)]
    public string Path { get; set; } = string.Empty;

    [Required]
    [Column("sha256")]
    [MaxLength(100)]
    public string Sha256 { get; set; } = string.Empty;

    [Column("size")]
    public long Size { get; set; }

    [Required]
    [Column("policy")]
    [MaxLength(50)]
    public string Policy { get; set; } = "FORCE_SYNC";

    [Column("s3_key")]
    [MaxLength(500)]
    public string? S3Key { get; set; }

    [ForeignKey("BuildInstanceId")]
    public virtual BuildInstance BuildInstance { get; set; } = null!;
}
