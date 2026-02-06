using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnderMineApi.Models
{
    [Table("config")]
    public class CoreData
    {
        [Key]
        [Column("id")] 
        public int Id { get; set; }

        [Required]
        [Column("version")] 
        public string Version { get; set; } = string.Empty;

        [Required]
        [Column("forge")] 
        public string ForgeVersion { get; set; } = string.Empty;

        [Required]
        [Column("bootloaderType")]
        public string bootloaderType { get; set; } = string.Empty;

        [Required]
        [Column("installLink")] 
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
    [Table("TechnoConfig")]
    public class TechnoConfig
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [Column("version")]
        public string Version { get; set; } = string.Empty;

        [Required]
        [Column("forge")]
        public string ForgeVersion { get; set; } = string.Empty;

        [Required]
        [Column("bootloaderType")]
        public string bootloaderType { get; set; } = string.Empty;

        [Required]
        [Column("installLink")]
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
    [Table("VanilaConfig")]
    public class VanilaConfig
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Required]
        [Column("version")]
        public string Version { get; set; } = string.Empty;

        [Required]
        [Column("forge")]
        public string ForgeVersion { get; set; } = string.Empty;
        [Required]
        [Column("bootloaderType")] // только маленькие буквы
        public string bootloaderType { get; set; } = string.Empty;

        [Required]
        [Column("installLink")]
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
    public class NewsPart
    {
        public string Type { get; set; } = "Run"; 
        public string? Text { get; set; }
        public bool IsBold { get; set; }
        public string? Color { get; set; }
        public int? FontSize { get; set; }
    }
}
