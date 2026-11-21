namespace UnderMineApi.Services;

/// <summary>
/// Sanitizes values before they reach a log sink. User-controlled strings can
/// contain CR/LF and forge additional log lines; remove both line-ending forms
/// on every platform.
/// </summary>
public static class LogSanitizer
{
    public static string Clean(string? value) =>
        (value ?? string.Empty)
            .Replace(Environment.NewLine, string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

    public static string Clean(object? value) => Clean(value?.ToString());
}
