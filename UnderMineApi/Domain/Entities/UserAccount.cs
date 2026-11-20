using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using UnderMineApi.Models;

namespace UnderMineApi.Domain.Entities
{
    [Table("user_accounts")]
    public class UserAccount
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [Required]
        [Column("player_uuid")]
        public required string Uuid { get; set; }

        [Required]
        [Column("username")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [Column("realname")]
        public string RealName { get; set; } = string.Empty;

        [Required]
        [Column("hash_password")]
        public string HashPassword { get; set; } = string.Empty;

        [Column("ip")]
        public string? Ip { get; set; }

        [Column("lastlogin")]
        public long? LastLogin { get; set; }

        [Required]
        [Column("regdate")]
        public long RegDate { get; set; }

        [Column("regip")]
        public string? RegIp { get; set; }

        [Column("email")]
        public string? Email { get; set; }

        [Column("device_id")]
        [MaxLength(128)]
        public string? DeviceId { get; set; }

        /// <summary>
        /// Registration subnet key ("192.0.2.0/24", "2001:db8::/64", ...) used by the per-IP
        /// registration limit. Null for accounts created before this column existed; those are
        /// still counted by their exact <see cref="RegIp"/>.
        /// </summary>
        [Column("regip_prefix")]
        [MaxLength(64)]
        public string? RegIpPrefix { get; set; }

        /// <summary>
        /// Incremented whenever the password is changed/reset. Every JWT carries this value, so
        /// old tokens stop working immediately after a password change (re-login required).
        /// </summary>
        [Column("token_version")]
        public int TokenVersion { get; set; }

        [Required]
        [Column("user_role")]
        public string Role { get; set; } = "user";

        public string? PublicKey { get; set; }

        public virtual required UserData Stats { get; set; }
    }
}