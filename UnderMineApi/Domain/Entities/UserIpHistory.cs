using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Domain.Entities
{
    /// <summary>
    /// Aggregated history of addresses a user has authenticated from. One row per
    /// (user, IP); the row is updated on every successful login instead of inserting a new
    /// record, so the table stays small on mobile networks that rotate addresses.
    /// </summary>
    [Table("user_ip_history")]
    public class UserIpHistory
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        [Column("user_id")]
        public long UserId { get; set; }

        [Required]
        [Column("ip")]
        [MaxLength(64)]
        public string Ip { get; set; } = string.Empty;

        [Column("first_seen")]
        public long FirstSeen { get; set; }

        [Column("last_seen")]
        public long LastSeen { get; set; }

        [Column("seen_count")]
        public int SeenCount { get; set; }
    }
}
