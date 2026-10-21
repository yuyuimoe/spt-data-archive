using System.Net;

namespace SptArchive.Api.Edge;

/// <summary>
/// The proxies whose <c>X-Forwarded-For</c> the Api believes (ADR-0039), read once from the settings.
/// </summary>
/// <remarks>
/// The settings hold one string per list, because that is what an environment variable is. This class
/// turns the strings into addresses and networks, and it is built once, so no request parses them again.
/// A value this class cannot read is ignored: an unreadable entry must not widen what the Api trusts.
/// </remarks>
public sealed class TrustedProxy
{
    private readonly IPAddress[] _addresses;
    private readonly IPNetwork[] _networks;

    private TrustedProxy(IPAddress[] addresses, IPNetwork[] networks)
    {
        _addresses = addresses;
        _networks = networks;
    }

    /// <summary>A list that trusts nothing.</summary>
    public static TrustedProxy None { get; } = new([], []);

    /// <summary>The number of addresses and networks the settings named.</summary>
    public int Count => _addresses.Length + _networks.Length;

    /// <summary>Reads the settings of one Api.</summary>
    public static TrustedProxy From(RateLimitOptions options) => Parse(options.TrustedProxies, options.TrustedNetworks);

    /// <summary>Reads the two lists. Commas and semicolons separate the entries.</summary>
    public static TrustedProxy Parse(string? proxies, string? networks)
    {
        var addresses = new List<IPAddress>();
        foreach (var entry in Split(proxies))
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(ClientAddress.Normalize(address));
            }
        }

        var parsed = new List<IPNetwork>();
        foreach (var entry in Split(networks))
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                parsed.Add(network);
            }
        }

        return new TrustedProxy([.. addresses], [.. parsed]);
    }

    /// <summary>True when the address is one of the proxies.</summary>
    public bool Contains(IPAddress address)
    {
        var candidate = ClientAddress.Normalize(address);

        foreach (var known in _addresses)
        {
            if (known.Equals(candidate))
            {
                return true;
            }
        }

        foreach (var network in _networks)
        {
            if (network.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
