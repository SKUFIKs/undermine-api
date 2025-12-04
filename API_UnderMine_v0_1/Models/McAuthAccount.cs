using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace API_UnderMine_v0_1.Models
{
    [Table("authme")]
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
        [Column("reg_time")]
        public DateTime regTime { get; set; } = DateTime.UtcNow;
        [Required]
        [Column("log_time")]
        public DateTime logTime { get; set; } = DateTime.UtcNow;
        [Required]
        [Column("IsBanned")]
        public bool IsBanned { get; set; } = false;
    }
}
