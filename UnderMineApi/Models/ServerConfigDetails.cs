using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace UnderMineApi.Models
{
    [Table("server_configs")]
    public class ServerConfigDetails
    {
        [Key]
        [Column("server_id")]
        [ForeignKey("Server")]
        public long ServerId { get; set; }

        [Required]
        [Column("type_version")]
        public string TypeVersion { get; set; } = string.Empty;

        [Column("config_hash_sum")]
        public string? ConfigHashSum { get; set; } = string.Empty;

        [JsonIgnore] 
        public virtual ServerManifest Server { get; set; } = null!;
    }
}