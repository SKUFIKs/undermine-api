using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using UnderMineApi.Domain.Entities;

namespace UnderMineApi.Models
{
    [Table("user_data")]
    public class UserData
    {
        [Key]
        [Column("user_id")]
        public long AccountId { get; set; }
        [Required]
        [Column("play_time")]
        public long PlayTime { get; set; }
        [Required]
        [Column("murders")]
        public int Murders { get; set; }
        [Required]
        [Column("deaths")]
        public int Deaths { get; set; }

        [ForeignKey("AccountId")]
        public virtual UnderMineApi.Domain.Entities.UserAccount Account { get; set; } = null!;
    } 
}
