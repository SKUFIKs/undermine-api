using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Models
{
    [Table("user_bans")]
    public class UserBans
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Required]
        [Column("user_id")]
        public long UserId { get; set; } 

        [Required]
        [Column("reason")]
        public string Reason { get; set; } = "Нарушение правил";

        [Column("banned_by")]
        public string BannedBy { get; set; } = "System"; 

        [Column("created_at")]
        public long CreatedAt { get; set; } 

        [Column("expires_at")]
        public long? ExpiresAt { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
