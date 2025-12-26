using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace API_UnderMine.Models
{
    [Table("config")]
    public class CoreData
    {
        [Key]
        [Column("id")] // Соответствует "Id" serial PRIMARY KEY из твоего SQL
        public int Id { get; set; }

        [Required]
        [Column("version")] // В SQL было "version"
        public string Version { get; set; } = string.Empty;

        [Required]
        [Column("forge")] // В SQL было "forge"
        public string ForgeVersion { get; set; } = string.Empty;

        [Required]
        [Column("installLink")] // В SQL было "installLink"
        public string InstallLink { get; set; } = string.Empty;

        [Column("confLink")]
        public string? ConfLink { get; set; } = string.Empty;

        [Column("kubejsLink")]
        public string? KubejsLink { get; set; } = string.Empty;

        [Column("shadersLink")]
        public string? ShadersLink { get; set; } = string.Empty;

        [Column("libraryExpectedChecksum")]
        public string? LibraryExpectedChecksum { get; set; } = string.Empty;

        [Column("versionExpectedChecksum")]
        public string? VersionExpectedChecksum { get; set; } = string.Empty;

        [Column("assetsExpectedChecksum")]
        public string? AssetsExpectedChecksum { get; set; } = string.Empty;

        [Column("uiVersion")] 
        public string? UiVersion { get; set; } = string.Empty;

        [Column("clientLink")]
        public string? ClientLink { get; set; } = string.Empty;

        [Column("manifestLink")]
        public string? ManifestLink { get; set; } = string.Empty;
    }
}
