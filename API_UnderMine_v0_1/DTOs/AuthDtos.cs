namespace API_UnderMine_v0_1.DTOs;

public class AuthRequestDto
{
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string deviceID { get; set; } = string.Empty;
}

public class AuthResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long UserId { get; set; } 
    public string Login { get; set; } = string.Empty;

    public string ip { get; set; } = string.Empty;
    public string regip { get; set; } = string.Empty;

    public string deviceId { get; set; } = string.Empty;
}


public class LoginResponseDto : AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
}

public class ValidationResponseDto
{
    public bool Valid { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string Message { get; set; } = string.Empty;
}