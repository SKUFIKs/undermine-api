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
        public string? DeviceId { get; set; }

        [Required]
        [Column("user_role")]
        public string Role { get; set; } = "user";

        public string? PublicKey { get; set; }

        public virtual required UserData Stats { get; set; }
    }
}