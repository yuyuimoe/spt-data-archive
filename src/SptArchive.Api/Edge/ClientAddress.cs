using System.Net;

namespace SptArchive.Api.Edge;

/// <summary>
/// The address that a request is charged to, and the proxy in front of the Api is not that address
/// (ADR-0039).
/// </summary>
/// <remarks>
/// <para>
/// Behind Caddy every request arrives from the proxy, so a limiter that reads the peer address alone
/// would give every client in the world one shared bucket. Caddy writes the client address into
/// <c>X-Forwarded-For</c>, and this class reads it — but only when the peer itself is a trusted proxy.
/// A header from an untrusted peer is a claim by the client, so it is ignored.
/// </para>
/// <para>
/// The header can hold a chain. This class walks it from the right, which is the end that the nearest
/// proxy wrote, and returns the first address that is not itself a trusted proxy. A client that
/// prepends a forged address therefore does not win.
/// </para>
/// </remarks>
public static class ClientAddress
{
    /// <summary>The name of a request whose peer address is not known (a test server, a unix socket).</summary>
    public const string Unknown = "unknown";

    /// <summary>The header that a proxy uses to state the client it forwarded for.</summary>
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>The bucket name of a request.</summary>
    public static string Resolve(HttpContext context, RateLimitOptions options, TrustedProxy trusted)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null)
        {
            return Unknown;
        }

        if (!options.TrustForwardedFor || !trusted.Contains(peer))
        {
            return Format(peer);
        }

        var forwarded = context.Request.Headers[ForwardedForHeader];
        if (forwarded.Count == 0)
        {
            return Format(peer);
        }

        var entries = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = entries.Length - 1; index >= 0; index--)
        {
            if (!IPAddress.TryParse(entries[index], out var address))
            {
                // A value this class cannot read is not an address, so it names nothing to charge.
                continue;
            }

            if (trusted.Contains(address))
            {
                continue;
            }

            return Format(address);
        }

        // The whole chain is composed of proxies: the closest one is the best answer that remains.
        return Format(peer);
    }

    /// <summary>One name for one address: an IPv4-mapped IPv6 address is the IPv4 address it holds.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>The bucket name of a parsed address, normalised first.</summary>
    public static string Format(IPAddress address) => Normalize(address).ToString();
}
