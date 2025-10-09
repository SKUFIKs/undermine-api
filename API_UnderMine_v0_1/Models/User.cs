using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace API_UnderMine_v0_1.Models
{
 
    public class User
    {
        public long Id { get; set; }
        public string Login { get; set; } = string.Empty;      // player_name
        public string PasswordHash { get; set; } = string.Empty; // password_hash
        public string? LastIp { get; set; }                   // last_ip
    }

}
