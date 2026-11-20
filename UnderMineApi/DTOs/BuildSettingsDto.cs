namespace UnderMineApi.DTOs;

public class BuildSettingsResponse
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LoaderVersion { get; set; }
    public long? BuildPresetId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
