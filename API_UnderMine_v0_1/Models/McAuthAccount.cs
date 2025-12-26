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
        public string Password { get; set; } = string.Empty;

        [Column("ip")]
        public string? Ip { get; set; }

        [Column("lastlogin")]
        public long? LastLogin { get; set; }

        [Required]
        [Column("regdate")]
        public long RegDate { get; set; }

        [Column("regip")]
        public string? RegIp { get; set; }

        [Required]
        [Column("x")]
        public double X { get; set; }

        [Required]
        [Column("y")]
        public double Y { get; set; }

        [Required]
        [Column("z")]
        public double Z { get; set; }

        [Required]
        [Column("world")]
        public string World { get; set; } = "world";

        [Column("yaw")]
        public double? Yaw { get; set; }

        [Column("pitch")]
        public double? Pitch { get; set; }

        [Column("email")]
        public string? Email { get; set; }

        [Required]
        [Column("islogged")]
        public short IsLogged { get; set; }

        [Required]
        [Column("hassession")]
        public short HasSession { get; set; }

        [Column("totp")]
        public string? Totp { get; set; }

        [Column("device_id")]
        public string? DeviceId { get; set; }

        [Column("IsBanned")]
        public bool? IsBanned { get; set; }
    }
    
}