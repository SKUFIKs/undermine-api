using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using UnderMineApi.Domain.Entities;

namespace UnderMineApi.Models;

/// <summary>
/// A crash/log report uploaded by a launcher. Launcher-only failures carry a single launcher
/// log; Minecraft failures also include debug.log, latest.log and the newest crash report.
/// The report is tied to the player account when the upload is authenticated, otherwise to the
/// device id so support can still group reports from the same machine.
/// </summary>
[Table("client_reports")]
public class ClientReport
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("user_id")]
    public long? UserId { get; set; }

    [Required]
    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>"launcher" or "minecraft".</summary>
    [Required]
    [Column("kind")]
    [MaxLength(20)]
    public string Kind { get; set; } = "launcher";

    [Required]
    [Column("title")]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Column("summary")]
    [MaxLength(4000)]
    public string Summary { get; set; } = string.Empty;

    [Column("launcher_version")]
    [MaxLength(64)]
    public string? LauncherVersion { get; set; }

    [Column("minecraft_version")]
    [MaxLength(64)]
    public string? MinecraftVersion { get; set; }

    [Column("loader")]
    [MaxLength(64)]
    public string? Loader { get; set; }

    [Column("server_name")]
    [MaxLength(100)]
    public string? ServerName { get; set; }

    [Column("os")]
    [MaxLength(200)]
    public string? Os { get; set; }

    /// <summary>"new", "investigating" or "resolved".</summary>
    [Required]
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "new";

    [Column("admin_note")]
    [MaxLength(4000)]
    public string? AdminNote { get; set; }

    [Required]
    [Column("consent_version")]
    [MaxLength(32)]
    public string ConsentVersion { get; set; } = "1";

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("resolved_at")]
    public long? ResolvedAt { get; set; }

    [Column("resolved_by")]
    [MaxLength(100)]
    public string? ResolvedBy { get; set; }

    [Column("ip")]
    [MaxLength(64)]
    public string? Ip { get; set; }

    public List<ClientReportFile> Files { get; set; } = new();

    [ForeignKey(nameof(UserId))]
    public UserAccount? User { get; set; }
}

/// <summary>
/// One uploaded file. The payload lives in PostgreSQL (like ban evidence), so it is only
/// reachable through the authenticated admin download endpoint and is covered by DB backups.
/// </summary>
[Table("client_report_files")]
public class ClientReportFile
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("report_id")]
    public long ReportId { get; set; }

    /// <summary>"launcher_log", "minecraft_debug", "minecraft_crash" or "minecraft_latest".</summary>
    [Required]
    [Column("kind")]
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    [Required]
    [Column("file_name")]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [Column("content_type")]
    [MaxLength(100)]
    public string ContentType { get; set; } = "text/plain";

    [Column("size")]
    public long Size { get; set; }

    [Column("sha256")]
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [Required]
    [Column("data")]
    public byte[] Data { get; set; } = Array.Empty<byte>();

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [ForeignKey(nameof(ReportId))]
    public ClientReport? Report { get; set; }
}
