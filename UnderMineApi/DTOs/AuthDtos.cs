namespace UnderMineApi.DTOs;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

public class AuthRequestDto
{
    [Required]
    public string Login { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("deviceID")]
    public string DeviceId { get; set; } = string.Empty;
}

public class AuthResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long UserId { get; set; } 
    public string Login { get; set; } = string.Empty;
}


public class LoginResponseDto : AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
}

public class ValidationResponseDto
{
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;
}