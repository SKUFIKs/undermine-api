namespace UnderMineApi.Services.Registration;

/// <summary>
/// Registration abuse limits. All values are configurable through the <c>Registration</c>
/// configuration section / <c>Registration__*</c> environment variables.
/// </summary>
public sealed class RegistrationOptions
{
    public int MaxAccountsPerDevice { get; set; } = 2;
    public int MaxAccountsPerIp { get; set; } = 4;

    /// <summary>
    /// IPv4 subnet used for the per-IP limit. <c>32</c> means "exact address"; lower values
    /// (e.g. <c>24</c>) make it harder to bypass the limit by rotating addresses in one block.
    /// </summary>
    public int Ipv4PrefixLength { get; set; } = 32;

    /// <summary>
    /// IPv6 subnet used for the per-IP limit. A single user usually controls a whole /64, so
    /// the default groups addresses at /64 instead of treating them as separate clients.
    /// </summary>
    public int Ipv6PrefixLength { get; set; } = 64;

    public int DeviceIdMinLength { get; set; } = 16;
    public int DeviceIdMaxLength { get; set; } = 128;
}
