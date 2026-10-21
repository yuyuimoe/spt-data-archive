using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SptArchive.Api.Edge;

namespace SptArchive.Tests;

/// <summary>
/// The address a request is charged to when a proxy stands in front of the Api (ADR-0039). The rate limit
/// of ADR-0038 gives one bucket per client, and behind Caddy the peer of every request is the proxy, so
/// the client address has to come from the header that the proxy writes — and from nowhere else.
/// </summary>
public sealed class ClientAddressTests
{
    private static readonly RateLimitOptions BehindCaddy = new()
    {
        TrustForwardedFor = true,
        TrustedNetworks = "10.89.0.0/16",
    };

    private static TrustedProxy Trusted(RateLimitOptions options) => TrustedProxy.From(options);

    private static HttpContext Request(string? peer, params string[] forwardedFor)
    {
        var context = new DefaultHttpContext();
        if (peer is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        }

        if (forwardedFor.Length > 0)
        {
            context.Request.Headers[ClientAddress.ForwardedForHeader] = string.Join(", ", forwardedFor);
        }

        return context;
    }

    private static string Resolve(HttpContext context, RateLimitOptions options) =>
        ClientAddress.Resolve(context, options, Trusted(options));

    [Fact]
    public void Without_a_trusted_proxy_the_peer_address_is_the_client()
    {
        var context = Request("198.51.100.9", "203.0.113.7");

        Assert.Equal("198.51.100.9", Resolve(context, new RateLimitOptions()));
    }

    [Fact]
    public void A_trusted_proxy_names_the_client_it_forwarded_for()
    {
        var context = Request("10.89.0.5", "203.0.113.7");

        Assert.Equal("203.0.113.7", Resolve(context, BehindCaddy));
    }

    [Fact]
    public void A_proxy_that_is_not_listed_is_only_a_peer()
    {
        // The header is a claim by whoever connected. Only the listing makes it a fact.
        var context = Request("192.0.2.50", "10.0.0.1");

        Assert.Equal("192.0.2.50", Resolve(context, BehindCaddy));
    }

    [Fact]
    public void A_forged_entry_at_the_left_does_not_win()
    {
        // A client that prepends an address cannot move another client's bucket: the proxy writes the
        // real address at the right-hand end, and the walk starts there.
        var context = Request("10.89.0.5", "1.2.3.4", "203.0.113.7");

        Assert.Equal("203.0.113.7", Resolve(context, BehindCaddy));
    }

    [Fact]
    public void A_chain_of_trusted_proxies_is_walked_to_the_first_client()
    {
        var context = Request("10.89.0.5", "203.0.113.7", "10.89.0.6");

        Assert.Equal("203.0.113.7", Resolve(context, BehindCaddy));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void A_value_that_is_not_an_address_names_nothing(string value)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.89.0.5");
        context.Request.Headers[ClientAddress.ForwardedForHeader] = value;

        Assert.Equal("10.89.0.5", Resolve(context, BehindCaddy));
    }

    [Fact]
    public void A_request_without_a_peer_address_shares_one_bucket()
    {
        Assert.Equal(ClientAddress.Unknown, Resolve(new DefaultHttpContext(), BehindCaddy));
    }

    [Fact]
    public void An_ipv4_mapped_address_is_the_ipv4_address_it_holds()
    {
        var context = Request("::ffff:10.89.0.5", "203.0.113.7");

        Assert.Equal("203.0.113.7", Resolve(context, BehindCaddy));
    }

    [Fact]
    public void A_listed_proxy_address_is_trusted_by_exact_address()
    {
        var options = new RateLimitOptions { TrustForwardedFor = true, TrustedProxies = "10.89.0.5" };

        Assert.Equal("203.0.113.7", Resolve(Request("10.89.0.5", "203.0.113.7"), options));
        Assert.Equal("10.89.0.6", Resolve(Request("10.89.0.6", "203.0.113.7"), options));
    }

    [Fact]
    public void Several_proxies_arrive_as_one_comma_separated_value()
    {
        // The live test of 2026-10-21 found this: an environment variable is one string, and the
        // configuration binder fills an array only from indexed keys. A list of one string that the Api
        // cannot read trusts nothing, and then every client shares the bucket of the proxy.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:TrustForwardedFor"] = "true",
                ["RateLimit:TrustedNetworks"] = "10.77.0.0/24, 10.78.0.0/24",
                ["RateLimit:TrustedProxies"] = "192.0.2.7;192.0.2.8",
            })
            .Build();

        var options = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>()!;
        var trusted = Trusted(options);

        Assert.Equal(4, trusted.Count);
        Assert.Equal("203.0.113.7", Resolve(Request("10.77.0.9", "203.0.113.7"), options));
        Assert.Equal("203.0.113.7", Resolve(Request("10.78.0.9", "203.0.113.7"), options));
        Assert.Equal("203.0.113.7", Resolve(Request("192.0.2.8", "203.0.113.7"), options));
        Assert.Equal("198.51.100.1", Resolve(Request("198.51.100.1", "203.0.113.7"), options));
    }

    [Fact]
    public void An_entry_that_is_not_an_address_widens_nothing()
    {
        var options = new RateLimitOptions { TrustForwardedFor = true, TrustedNetworks = "not-a-network, 10.0.0.0/8" };
        var trusted = Trusted(options);

        Assert.Equal(1, trusted.Count);
        Assert.False(trusted.Contains(IPAddress.Parse("192.0.2.1")));
        Assert.True(trusted.Contains(IPAddress.Parse("10.1.2.3")));
    }
}
