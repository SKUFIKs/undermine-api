using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace UnderMineApi.Extensions;

/// <summary>
/// Configures <see cref="ForwardedHeadersOptions"/> so that X-Forwarded-* headers are only trusted
/// when they come from an explicitly declared reverse proxy.
///
/// Without <c>ForwardedHeaders:KnownProxies</c> / <c>ForwardedHeaders:KnownNetworks</c> the safe
/// ASP.NET Core defaults (loopback only) are kept, which means a direct client cannot spoof its IP
/// by sending an X-Forwarded-For header. docker-compose declares the internal proxy subnet through
/// <c>ForwardedHeaders__KnownNetworks</c>.
/// </summary>
public static class ForwardedHeadersExtensions
{
    public static IServiceCollection AddConfiguredForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("ForwardedHeaders");

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Number of proxy hops to trust. nginx appends the real client address, so one hop is
            // enough; a larger value would let a client-controlled prefix of X-Forwarded-For win.
            var forwardLimit = section.GetValue<int?>("ForwardLimit");
            options.ForwardLimit = forwardLimit is > 0 ? forwardLimit : 1;

            var proxies = SplitList(section["KnownProxies"]);
            var networks = SplitList(section["KnownNetworks"]);

            // Nothing configured: keep the framework defaults (loopback). This is the safe case for
            // "API is accessed directly", e.g. local development without a reverse proxy.
            if (proxies.Count == 0 && networks.Count == 0)
                return;

            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (var raw in proxies)
            {
                if (!IPAddress.TryParse(raw, out var address))
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownProxies contains an invalid IP address: '{raw}'.");
                }

                options.KnownProxies.Add(address);
            }

            foreach (var raw in networks)
            {
                if (!TryParseNetwork(raw, out var network))
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownNetworks contains an invalid CIDR: '{raw}'. " +
                        "Expected a value like 172.28.0.0/24.");
                }

                options.KnownNetworks.Add(network);
            }
        });

        return services;
    }

    /// <summary>True when the middleware should run at all (default: enabled).</summary>
    public static bool IsForwardedHeadersEnabled(this IConfiguration configuration) =>
        configuration.GetValue("ForwardedHeaders:Enabled", true);

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool TryParseNetwork(string value, out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
    {
        network = null!;

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var prefix) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maxLength = prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxLength)
            return false;

        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength);
        return true;
    }
}
