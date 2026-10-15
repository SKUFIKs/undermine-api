using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnderMineApi.DTOs;

/// <summary>
/// Pipeline scenario of a build. Selected by the build worker from the stored
/// import descriptor: a build with a modpack import descriptor runs the
/// <see cref="BuildType.ModpackImport"/> scenario, everything else is standard.
/// </summary>
public enum BuildType
{
    /// <summary>Engine download + optional build-preset content.</summary>
    Standard = 0,

    /// <summary>Full .mrpack modpack import: engine from index dependencies, index files, overrides.</summary>
    ModpackImport = 1
}

// ---------------------------------------------------------------------------
// modrinth.index.json - strict System.Text.Json DTOs (explicit names, no guessing)
// ---------------------------------------------------------------------------

/// <summary>Root of the <c>modrinth.index.json</c> manifest inside a .mrpack archive.</summary>
public class ModrinthIndex
{
    /// <summary>Game identifier, always "minecraft".</summary>
    [JsonPropertyName("game")]
    public string? Game { get; set; }

    /// <summary>Modpack version id chosen by the author.</summary>
    [JsonPropertyName("versionId")]
    public string? VersionId { get; set; }

    /// <summary>Human readable modpack name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Engine dependencies, e.g. "minecraft": "1.20.1", "fabric-loader": "0.15.7",
    /// "forge": "47.2.0", "neoforge": "21.1.55".
    /// </summary>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string>? Dependencies { get; set; }

    /// <summary>Files (mods, shaderpacks, resourcepacks, configs, ...) to download.</summary>
    [JsonPropertyName("files")]
    public List<ModrinthIndexFile> Files { get; set; } = new();
}

/// <summary>One downloadable file entry of the modpack index.</summary>
public class ModrinthIndexFile
{
    /// <summary>Relative in-game path, e.g. "mods/sodium-fabric-0.5.8.jar".</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Hashes of the expected content; contains sha512 and/or sha1.</summary>
    [JsonPropertyName("hashes")]
    public Dictionary<string, string>? Hashes { get; set; }

    /// <summary>Direct CDN download URLs; may be empty for Modrinth-hosted files.</summary>
    [JsonPropertyName("downloads")]
    public List<string>? Downloads { get; set; }

    /// <summary>Expected size in bytes.</summary>
    [JsonPropertyName("fileSize")]
    public long? FileSize { get; set; }

    /// <summary>Environment constraints; client == "unsupported" means server-only file.</summary>
    [JsonPropertyName("env")]
    public ModrinthFileEnv? Env { get; set; }

    [JsonIgnore]
    public string? Sha512 => Hashes != null && Hashes.TryGetValue("sha512", out var v) ? v : null;

    [JsonIgnore]
    public string? Sha1 => Hashes != null && Hashes.TryGetValue("sha1", out var v) ? v : null;

    /// <summary>True when the file is not needed on the client (pure server-side mod).</summary>
    [JsonIgnore]
    public bool IsClientUnsupported =>
        string.Equals(Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Environment section of an index file.</summary>
public class ModrinthFileEnv
{
    /// <summary>"required" | "optional" | "unsupported".</summary>
    [JsonPropertyName("client")]
    public string? Client { get; set; }

    [JsonPropertyName("server")]
    public string? Server { get; set; }
}

// ---------------------------------------------------------------------------
// Modrinth /v2/project/{id}/version response (subset used for modpack resolution)
// ---------------------------------------------------------------------------

public class ModrinthModpackVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version_number")]
    public string? VersionNumber { get; set; }

    [JsonPropertyName("version_type")]
    public string? VersionType { get; set; }

    [JsonPropertyName("date_published")]
    public DateTime DatePublished { get; set; }

    [JsonPropertyName("files")]
    public List<ModrinthModpackVersionFile> Files { get; set; } = new();
}

public class ModrinthModpackVersionFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("hashes")]
    public Dictionary<string, string>? Hashes { get; set; }
}

// ---------------------------------------------------------------------------
// manifest.json - CurseForge modpack manifest (strict System.Text.Json DTOs)
// ---------------------------------------------------------------------------

/// <summary>
/// Root of the CurseForge <c>manifest.json</c>. A CurseForge pack ships no jars and no
/// download URLs - only <c>(projectID, fileID)</c> pairs that have to be resolved through the
/// CurseForge API - plus an <c>overrides/</c> folder with configs.
/// </summary>
public class CurseForgeManifest
{
    [JsonPropertyName("manifestType")]
    public string? ManifestType { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("minecraft")]
    public CurseForgeMinecraft Minecraft { get; set; } = new();

    [JsonPropertyName("files")]
    public List<CurseForgeManifestFile> Files { get; set; } = new();

    /// <summary>Folder inside the archive holding the overrides (usually "overrides").</summary>
    [JsonPropertyName("overrides")]
    public string? Overrides { get; set; }
}

public class CurseForgeMinecraft
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("modLoaders")]
    public List<CurseForgeModLoader>? ModLoaders { get; set; }
}

/// <summary>e.g. {"id": "forge-14.23.5.2860", "primary": true}.</summary>
public class CurseForgeModLoader
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }
}

public class CurseForgeManifestFile
{
    [JsonPropertyName("projectID")]
    public int ProjectId { get; set; }

    [JsonPropertyName("fileID")]
    public int FileId { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}

// ---------------------------------------------------------------------------
// API request / stored descriptor
// ---------------------------------------------------------------------------

/// <summary>POST /admin/builds/import-modpack/modrinth body.</summary>
public class ImportModpackRequest
{
    /// <summary>Optional display name of the build; the modpack name is used when empty.</summary>
    [MaxLength(100)]
    public string? Name { get; set; }

    /// <summary>Modrinth modpack slug or project id, e.g. "fabulously-optimized".</summary>
    [Required]
    [MaxLength(200)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Optional concrete modpack version id; the latest release is used when omitted.</summary>
    [MaxLength(100)]
    public string? VersionId { get; set; }

    /// <summary>Optional server to pin to the imported build once it completes.</summary>
    public long? ServerId { get; set; }
}

/// <summary>
/// Import descriptor persisted as JSON in <c>build_instances.modpack_import</c>.
/// Its presence switches the build worker to the <see cref="BuildType.ModpackImport"/>
/// scenario and carries everything needed to re-run the build (retries included).
/// </summary>
public class ModpackImportInfo
{
    public const string SourceModrinth = "modrinth";
    public const string SourceFile = "file";

    /// <summary>"modrinth" or "file".</summary>
    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = SourceModrinth;

    /// <summary>Modrinth slug/id (sourceType == "modrinth").</summary>
    [JsonPropertyName("slugOrId")]
    public string? SlugOrId { get; set; }

    /// <summary>Pinned modpack version id, when the admin chose a specific one.</summary>
    [JsonPropertyName("versionId")]
    public string? VersionId { get; set; }

    /// <summary>Original uploaded file name (sourceType == "file").</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    /// <summary>Modpack name reported by the index (for logs / admin panel).</summary>
    [JsonPropertyName("modpackName")]
    public string? ModpackName { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static ModpackImportInfo? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ModpackImportInfo>(json);
}
