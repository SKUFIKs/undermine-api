using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace API_UnderMine_v0_1.Models
{
    [Table("authmeplugin")]
    public class McAuthAccount
    {
        // id (bigint, PK)
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        // username (varchar 255, not null)
        [Required]
        [Column("username")]
        public string Username { get; set; } = string.Empty;

        // realname (varchar 255, not null)
        [Required]
        [Column("realname")]
        public string RealName { get; set; } = string.Empty;

        // password (varchar 255, not null)
        [Required]
        [Column("password")]
        public string Password { get; set; } = string.Empty;

        // ip (varchar 40, nullable)
        [Column("ip")]
        public string? Ip { get; set; }

        // lastlogin (bigint, nullable)
        [Column("lastlogin")]
        public long? LastLogin { get; set; }

        // regdate (bigint, not null, default 0)
        [Required]
        [Column("regdate")]
        public long RegDate { get; set; }

        // regip (varchar 40, nullable)
        [Column("regip")]
        public string? RegIp { get; set; }

        // x, y, z (double, not null, default 0)
        [Required]
        [Column("x")]
        public double X { get; set; }

        [Required]
        [Column("y")]
        public double Y { get; set; }

        [Required]
        [Column("z")]
        public double Z { get; set; }

        // world (varchar 255, not null, default 'world')
        [Required]
        [Column("world")]
        public string World { get; set; } = "world";

        // yaw, pitch (double, nullable)
        [Column("yaw")]
        public double? Yaw { get; set; }

        [Column("pitch")]
        public double? Pitch { get; set; }

        // email (varchar 255, nullable)
        [Column("email")]
        public string? Email { get; set; }

        // islogged (smallint, not null, default 0)
        [Required]
        [Column("islogged")]
        public short IsLogged { get; set; }

        // hassession (smallint, not null, default 0)
        [Required]
        [Column("hassession")]
        public short HasSession { get; set; }

        // totp (varchar 32, nullable)
        [Column("totp")]
        public string? Totp { get; set; }

        // device_id (varchar 255, nullable)
        [Column("device_id")]
        public string? DeviceId { get; set; }

        // IsBanned (boolean, nullable, default false)
        [Column("IsBanned")]
        public bool? IsBanned { get; set; }
    }
}