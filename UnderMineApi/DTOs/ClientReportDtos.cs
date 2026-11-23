using Microsoft.AspNetCore.Http;

namespace UnderMineApi.DTOs;

/// <summary>Multipart form used by the launcher when it uploads a crash report.</summary>
public class ClientReportForm
{
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? LauncherVersion { get; set; }
    public string? MinecraftVersion { get; set; }
    public string? Loader { get; set; }
    public string? ServerName { get; set; }
    public string? Os { get; set; }
    public string? DeviceId { get; set; }
    public bool Consent { get; set; }
    public string? ConsentVersion { get; set; }

    public IFormFile? LauncherLog { get; set; }
    public IFormFile? MinecraftDebug { get; set; }
    public IFormFile? MinecraftCrash { get; set; }
    public IFormFile? MinecraftLatest { get; set; }
}

public class ClientReportFileDto
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

public class ClientReportListItemDto
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public string? Username { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? LauncherVersion { get; set; }
    public string? MinecraftVersion { get; set; }
    public string? Loader { get; set; }
    public string? ServerName { get; set; }
    public string Status { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

public class ClientReportDetailsDto : ClientReportListItemDto
{
    public string Summary { get; set; } = string.Empty;
    public string? Os { get; set; }
    public string? Ip { get; set; }
    public string? AdminNote { get; set; }
    public string ConsentVersion { get; set; } = string.Empty;
    public long? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public List<ClientReportFileDto> Files { get; set; } = new();
}

public class ClientReportPageDto
{
    public List<ClientReportListItemDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class UpdateClientReportDto
{
    public string? Status { get; set; }
    public string? AdminNote { get; set; }
}
