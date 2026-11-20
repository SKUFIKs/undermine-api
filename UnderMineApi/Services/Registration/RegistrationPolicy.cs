using System.Text.RegularExpressions;

namespace UnderMineApi.Services.Registration;

public static class RegistrationPolicy
{
    // The launcher sends a lower-case hex SHA-256 (64 chars). The wider charset keeps room for
    // other clients while rejecting arbitrary text, whitespace and control characters.
    private static readonly Regex DeviceIdPattern =
        new("^[A-Za-z0-9._:-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryNormalizeDeviceId(
        string? raw,
        int minLength,
        int maxLength,
        out string normalized,
        out string error)
    {
        normalized = raw?.Trim() ?? string.Empty;
        error = string.Empty;

        // Never allow more than the database column can store (user_accounts.device_id is
        // varchar(128)); a higher configured max would otherwise fail on insert.
        maxLength = Math.Clamp(maxLength, 1, 128);
        minLength = Math.Clamp(minLength, 1, maxLength);

        if (normalized.Length == 0)
        {
            error = "Device ID is required.";
            return false;
        }

        if (normalized.Length < minLength || normalized.Length > maxLength)
        {
            error = $"Device ID must be between {minLength} and {maxLength} characters.";
            return false;
        }

        if (!DeviceIdPattern.IsMatch(normalized))
        {
            error = "Device ID contains invalid characters.";
            return false;
        }

        return true;
    }

    /// <summary>Log-safe device identifier (first 8 chars + length marker).</summary>
    public static string MaskDeviceId(string deviceId) =>
        deviceId.Length <= 8 ? "***" : deviceId[..8] + "…";
}
