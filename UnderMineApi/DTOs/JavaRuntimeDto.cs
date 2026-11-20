namespace UnderMineApi.DTOs;

/// <summary>
/// Describes a Java runtime archive available for download through the API. The launcher uses
/// the checksum and size to verify the archive before extracting it.
/// </summary>
public sealed class JavaRuntimeDto
{
    public int Major { get; set; }
    public string Os { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}
