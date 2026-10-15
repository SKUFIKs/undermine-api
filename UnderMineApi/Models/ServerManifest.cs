using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Models
{
    [Table("server_manifests")]
    public class ServerManifest
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Required]
        [Column("name")]
        public string ServerName { get; set; } = string.Empty;

        [Required]
        [Column("type")]
        public string LoaderType { get; set; } = string.Empty;

        [Required]
        [Column("app_version")]
        public string AppVersion { get; set; } = string.Empty;

        [Column("ip_address")] 
        public string IpAddress { get; set; } = "127.0.0.1";

        [Column("is_active")]
        public bool IsActive { get; set; } = false;

        [Column("build_manifest", TypeName = "text")]
        public string? BuildManifest { get; set; }

        // Which build this server hands to the launcher:
        //   null  -> "auto": the newest completed build with matching MC / loader versions,
        //   ""    -> "none": no manifest at all,
        //   value -> pinned to a specific build (build_instances.build_id).
        [Column("build_id")]
        [MaxLength(64)]
        public string? BuildId { get; set; }

        public virtual ServerConfigDetails? Config { get; set; }
    }
}