namespace UnderMineApi.DTOs;

public class BuildPresetDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string LoaderType { get; set; } = string.Empty;
    public string? LoaderVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
