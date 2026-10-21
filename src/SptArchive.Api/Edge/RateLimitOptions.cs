namespace SptArchive.Api.Edge;

/// <summary>
/// The settings of the request guard in front of the Api (ADR-0038). The defaults are the values that the
/// security audit of 2026-10-21 and the load test of the same day recommend.
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    /// <summary>Master switch. A load test raises the limit or turns it off, so it measures the Api.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The size of the bucket: how many requests one client may send at once.</summary>
    public int TokenLimit { get; set; } = 100;

    /// <summary>How many tokens a bucket gains per <see cref="ReplenishSeconds"/>.</summary>
    public int TokensPerPeriod { get; set; } = 100;

    /// <summary>The refill period. With the defaults one client gets 10 requests per second.</summary>
    public int ReplenishSeconds { get; set; } = 10;

    /// <summary>
    /// The most client buckets the Api keeps. An address is chosen by the client, so a map of buckets
    /// would be an unbounded allocation without this ceiling.
    /// </summary>
    public int MaxClients { get; set; } = 50_000;

    /// <summary>How long an unused bucket is kept before it is forgotten.</summary>
    public int IdleMinutes { get; set; } = 10;

    /// <summary>
    /// When true, the Api charges a request to the client that the proxy names in
    /// <c>X-Forwarded-For</c>, and not to the proxy. It is off by default: a header is a claim by the
    /// client until a trusted proxy states it, and Caddy names the client (ADR-0039).
    /// </summary>
    public bool TrustForwardedFor { get; set; }

    /// <summary>
    /// The proxy addresses whose <c>X-Forwarded-For</c> the Api believes, separated by commas.
    /// </summary>
    /// <remarks>
    /// A string and not an array on purpose: a configuration value that comes from one environment
    /// variable is one string. The binder reads an array only from indexed keys such as
    /// <c>RateLimit__TrustedProxies__0</c>, so a plain <c>RateLimit__TrustedProxies=10.0.0.5</c> would
    /// bind to an empty array and the header would be silently ignored (found by the live test of
    /// 2026-10-21).
    /// </remarks>
    public string TrustedProxies { get; set; } = string.Empty;

    /// <summary>The proxy networks, in CIDR form, whose <c>X-Forwarded-For</c> the Api believes.</summary>
    public string TrustedNetworks { get; set; } = string.Empty;
}
