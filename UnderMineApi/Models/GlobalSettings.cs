using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Models
{
    [Table("global_settings")]
    public class GlobalSetting
    {
        [Key]
        [Column("setting_key")] 
        public string Key { get; set; } = string.Empty;

        [Column("setting_value")] 
        public string Value { get; set; } = string.Empty;
    }
}
