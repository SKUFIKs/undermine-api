using System.Text;

namespace UnderMineApi.Services.Security;

/// <summary>
/// Password rules for new/changed passwords. BCrypt only uses the first 72 bytes, so anything
/// longer is silently truncated by the algorithm - we reject it explicitly instead.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxBytes = 72;

    public static bool TryValidate(string? password, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Password is required.";
            return false;
        }

        if (password.Length < MinLength)
        {
            error = $"Password must be at least {MinLength} characters long.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(password) > MaxBytes)
        {
            error = $"Password must be at most {MaxBytes} bytes (UTF-8).";
            return false;
        }

        return true;
    }
}
