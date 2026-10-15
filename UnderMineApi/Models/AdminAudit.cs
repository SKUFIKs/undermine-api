using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Models
{
    [Table("admin_audits")]
    public class AdminAudit
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Required]
        [Column("actor")]
        public string Actor { get; set; } = string.Empty;

        [Required]
        [Column("action")]
        public string Action { get; set; } = string.Empty;

        [Column("target_type")]
        public string? TargetType { get; set; }

        [Column("target_id")]
        public long? TargetId { get; set; }

        [Column("details")]
        public string? Details { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }
    }
}