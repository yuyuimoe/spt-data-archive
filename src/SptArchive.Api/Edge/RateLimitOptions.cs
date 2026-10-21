namespace SptArchive.Api.Edge;

/// <summary>
/// Settings of the request guard in front of the Api (ADR-0038). The defaults are the values that the
/// security audit of 2026-10-21 recommends.
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
}
