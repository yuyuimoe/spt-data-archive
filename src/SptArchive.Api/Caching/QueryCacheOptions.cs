namespace SptArchive.Api.Caching;

/// <summary>
/// Settings of the query cache. The defaults are the measured values of ADR-0008 and ADR-0037.
/// </summary>
public sealed class QueryCacheOptions
{
    public const string Section = "Cache";

    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a cached answer lives. A new Archiver run already makes every old answer unreachable,
    /// because the dataset version is part of the cache key, so this only bounds how long an unused
    /// answer occupies Redis. One minute keeps the cache small and never serves data that is older than
    /// the release it was read from.
    /// </summary>
    public int TtlSeconds { get; set; } = 60;

    /// <summary>
    /// The spread applied to <see cref="TtlSeconds"/>, as a percentage. Keys that were stored together
    /// must not all expire in the same millisecond, because the requests that follow them would all miss
    /// at once. Ten percent spreads one minute over six seconds either side.
    /// </summary>
    public int TtlJitterPercent { get; set; } = 10;

    /// <summary>
    /// How long an <c>Idempotency-Key</c> keeps its answer for a replay. A client repeats a request
    /// because an answer went missing, which happens in seconds, not hours.
    /// </summary>
    public int IdempotencySeconds { get; set; } = 300;

    /// <summary>Larger request bodies are never cached (and never buffered).</summary>
    public int MaxRequestBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// The most requests that may wait for one query. Beyond this the Api stops collapsing and serves
    /// each request on its own, so a flood of distinct keys cannot grow the map with no bound.
    /// </summary>
    public int MaxFlights { get; set; } = 1024;

    /// <summary>
    /// How long the Api stops trying the cache after a failure. A cache that is down must cost one slow
    /// request, not one for every request, so the wait is paid once and then skipped.
    /// </summary>
    public int DownSeconds { get; set; } = 30;

    /// <summary>The lifetime of an answer, spread by the jitter so keys do not expire together.</summary>
    public TimeSpan Ttl() => Jittered(TtlSeconds);

    /// <summary>The lifetime of an answer that a client may replay with an idempotency key.</summary>
    public TimeSpan IdempotencyTtl() => Jittered(IdempotencySeconds);

    private TimeSpan Jittered(int seconds)
    {
        if (TtlJitterPercent <= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var spread = seconds * (TtlJitterPercent / 100.0);
        var offset = (Random.Shared.NextDouble() * 2 * spread) - spread;
        return TimeSpan.FromSeconds(seconds + offset);
    }
}
