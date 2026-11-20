using System.Net;
using System.Net.Sockets;

namespace UnderMineApi.Services.Registration;

/// <summary>
/// Normalizes client addresses for registration limits. IPv4-mapped IPv6 addresses are treated as
/// IPv4, and IPv6 clients are grouped by prefix (by default /64) so rotating the interface
/// identifier does not reset the account limit.
/// </summary>
public static class RegistrationIp
{
    public static IPAddress? Normalize(IPAddress? address)
    {
        if (address == null)
            return null;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>Returns a canonical subnet key such as <c>192.0.2.0/24</c> or <c>2001:db8::/64</c>.</summary>
    public static string ToPrefixKey(IPAddress? address, int ipv4PrefixLength, int ipv6PrefixLength)
    {
        address = Normalize(address);
        if (address == null)
            return "unknown";

        var isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var maxPrefix = isIpv4 ? 32 : 128;
        var prefix = Math.Clamp(isIpv4 ? ipv4PrefixLength : ipv6PrefixLength, 1, maxPrefix);

        var bytes = address.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        // Zero everything after the prefix; a non-byte-aligned prefix keeps the high bits of
        // the boundary byte and clears the rest.
        var zeroFrom = fullBytes + (remainingBits > 0 ? 1 : 0);
        for (var i = zeroFrom; i < bytes.Length; i++)
            bytes[i] = 0;

        if (remainingBits > 0 && fullBytes < bytes.Length)
            bytes[fullBytes] &= (byte)(0xFF << (8 - remainingBits));

        return $"{new IPAddress(bytes)}/{prefix}";
    }

    /// <summary>Short, log-safe representation of an address (never the full IPv6 address).</summary>
    public static string ToLogString(IPAddress? address)
    {
        address = Normalize(address);
        return address?.ToString() ?? "unknown";
    }
}
