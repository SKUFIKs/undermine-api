using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace API_UnderMine_v0_1.Models
{
    [Table("authmeplugin")]
    public class McAuthAccount
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] 
        public long Id { get; set; }

        [Required]
        [Column("username")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [Column("realname")]
        public string RealName { get; set; } = string.Empty;

        [Required]
        [Column("password")]
        public string PasswordHash { get; set; } = string.Empty;
        [Required]
        [Column("regip")]
        public string regip { get; set; } = string.Empty;
        [Required]
        [Column("ip")]
        public string ip { get; set; } = string.Empty;
        [Required]
        [Column("device_id")]
        public string deviceId { get; set; } = string.Empty;
        [Required]
        [Column("regdate")]
        public long regTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        [Required]
        [Column("lastlogin")]
        public long logTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        [Required]
        [Column("IsBanned")]
        public bool IsBanned { get; set; } = false;
    }
}
