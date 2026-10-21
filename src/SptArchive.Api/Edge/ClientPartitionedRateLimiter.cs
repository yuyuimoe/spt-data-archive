using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace SptArchive.Api.Edge;

/// <summary>
/// One token bucket per client address, in a map that has a ceiling (ADR-0038).
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core offers <see cref="PartitionedRateLimiter.Create{TPartitionKey,TResource}"/>, and its map
/// of partitions is never pruned. The partition key here is an address, which a client chooses, so that
/// map would grow with every address that reaches the Api. This class keeps the same behaviour with a
/// ceiling: it forgets a bucket that has been idle for <see cref="RateLimitOptions.IdleMinutes"/>, and it
/// drops the oldest buckets when a flood fills the map.
/// </para>
/// <para>
/// A bucket refills without a timer, on the request that reads it. A forgotten bucket therefore leaves
/// nothing running behind it.
/// </para>
/// </remarks>
public sealed class ClientPartitionedRateLimiter(IOptions<RateLimitOptions> options) : PartitionedRateLimiter<HttpContext>
{
    private const long SweepEveryMs = 60_000;
    private const long SweepWhenFullMs = 1_000;

    private readonly RateLimitOptions _options = options.Value;

    /// <summary>The proxies whose header the Api believes, read once (ADR-0039).</summary>
    private readonly TrustedProxy _trusted = TrustedProxy.From(options.Value);
    private readonly Dictionary<string, Entry> _clients = new(StringComparer.Ordinal);
    private readonly Lock _sweepLock = new();

    /// <summary><see cref="Environment.TickCount64"/> of the next sweep of the map.</summary>
    private long _nextSweep;

    /// <summary>The number of buckets the Api holds. A test reads it to see that the map is bounded.</summary>
    public int Count
    {
        get
        {
            lock (_sweepLock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>The endpoint the guard charges for. A health check must answer while the Api is busy.</summary>
    public const string GuardedPath = "/graphql";

    private static readonly RateLimitLease Exempt = new ExemptLease();

    protected override RateLimitLease AttemptAcquireCore(HttpContext context, int permitCount) =>
        IsGuarded(context) ? Bucket(context).Limiter.AttemptAcquire(permitCount) : Exempt;

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext context, int permitCount, CancellationToken cancellationToken) =>
        IsGuarded(context) ? Bucket(context).Limiter.AcquireAsync(permitCount, cancellationToken) : ValueTask.FromResult(Exempt);

    public override RateLimiterStatistics? GetStatistics(HttpContext context) => Bucket(context).Limiter.GetStatistics();

    private bool IsGuarded(HttpContext context) =>
        _options.Enabled && context.Request.Path.StartsWithSegments(GuardedPath);

    /// <summary>The name of the client the request is charged to, which is not the proxy (ADR-0039).</summary>
    public string ClientName(HttpContext context) => ClientAddress.Resolve(context, _options, _trusted);

    private Entry Bucket(HttpContext context)
    {
        var key = ClientName(context);
        var now = Environment.TickCount64;

        Entry? entry;
        lock (_sweepLock)
        {
            if (!_clients.TryGetValue(key, out entry))
            {
                entry = new Entry(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, _options.TokenLimit),
                    TokensPerPeriod = Math.Max(1, _options.TokensPerPeriod),
                    ReplenishmentPeriod = TimeSpan.FromSeconds(Math.Max(1, _options.ReplenishSeconds)),
                    QueueLimit = 0,          // a waiting request would hold a pooled database connection
                    AutoReplenishment = false,
                }));

                _clients[key] = entry;
            }

            entry.LastSeen = now;
            Sweep(now);
        }

        entry.Limiter.TryReplenish();   // no timer: the bucket refills on the request that reads it
        return entry;
    }

    private void Sweep(long now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        var full = _clients.Count > _options.MaxClients;
        _nextSweep = now + (full ? SweepWhenFullMs : SweepEveryMs);

        var idleSince = now - (long)TimeSpan.FromMinutes(Math.Max(1, _options.IdleMinutes)).TotalMilliseconds;
        foreach (var (key, entry) in _clients.ToArray())
        {
            if (entry.LastSeen < idleSince)
            {
                _clients.Remove(key);
            }
        }

        var excess = _clients.Count - Math.Max(1, _options.MaxClients);
        if (excess <= 0)
        {
            return;
        }

        // A flood of distinct addresses: drop the buckets that have been quiet longest.
        foreach (var (key, _) in _clients.OrderBy(pair => pair.Value.LastSeen).Take(excess).ToArray())
        {
            _clients.Remove(key);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sweepLock)
            {
                foreach (var entry in _clients.Values)
                {
                    entry.Limiter.Dispose();
                }

                _clients.Clear();
            }
        }

        base.Dispose(disposing);
    }

    private sealed class Entry(TokenBucketRateLimiter limiter)
    {
        public TokenBucketRateLimiter Limiter { get; } = limiter;

        /// <summary><see cref="Environment.TickCount64"/> of the last request charged to this client.</summary>
        public long LastSeen { get; set; }
    }

    /// <summary>A request the guard does not charge for: it is always allowed.</summary>
    private sealed class ExemptLease : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
