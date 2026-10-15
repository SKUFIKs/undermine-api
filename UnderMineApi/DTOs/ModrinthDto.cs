using System.Text.Json.Serialization;

namespace UnderMineApi.DTOs;

public class ModrinthSearchResult
{
    [JsonPropertyName("hits")]
    public List<ModrinthSearchHit> Hits { get; set; } = new();
}

public class ModrinthSearchHit
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = string.Empty;
}

public class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("date_published")]
    public DateTime DatePublished { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = string.Empty; // release, beta, alpha

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new();

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = new();
}

public class ModrinthFile
{
    [JsonPropertyName("hashes")]
    public ModrinthHashes Hashes { get; set; } = new();

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class ModrinthHashes
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
