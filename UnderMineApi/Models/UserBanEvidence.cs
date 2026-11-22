using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Models;

/// <summary>
/// One evidence file attached to a ban. Files are stored in PostgreSQL rather than the public
/// build bucket, so screenshots and documents are only reachable through the authenticated
/// admin download endpoint. The binary payload is intentionally absent from list DTOs.
/// </summary>
[Table("user_ban_evidence")]
public class UserBanEvidence
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("ban_id")]
    public long BanId { get; set; }

    [Required]
    [Column("file_name")]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [Column("content_type")]
    [MaxLength(100)]
    public string ContentType { get; set; } = "application/octet-stream";

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

    [ForeignKey(nameof(BanId))]
    public UserBans? Ban { get; set; }
}
