using System.Collections.Concurrent;

namespace SptArchive.Api.Caching;

/// <summary>
/// Collapses concurrent requests for the same key into one execution. The first caller runs the work;
/// the others wait for its result instead of repeating it (ADR-0037).
/// </summary>
/// <remarks>
/// <para>
/// Without this, a cache stampede: when one hot answer expires, every request that arrives before the
/// first one finishes goes to the database. Measured on the real archive, a burst of 200 requests for a
/// cold answer ran 200 queries.
/// </para>
/// <para>
/// The map is bounded by <c>maxFlights</c>. A request beyond the bound runs its own work, so a flood of
/// distinct keys cannot grow memory without limit; it costs a query, which is what the Api did before.
/// </para>
/// </remarks>
public sealed class SingleFlight(int maxFlights)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<QueryAnswer>>> _flights = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="work"/> once for <paramref name="key"/>, or waits for the caller that
    /// already runs it.
    /// </summary>
    /// <returns>
    /// The shared task, and whether this caller is the one that runs the work. The second value is the
    /// leader flag: only the leader stores the answer, and only the leader writes the cache header for
    /// a miss.
    /// </returns>
    public (Task<QueryAnswer> Result, bool Leader) RunAsync(string key, Func<Task<QueryAnswer>> work)
    {
        while (true)
        {
            if (_flights.TryGetValue(key, out var running))
            {
                return (running.Value, false);
            }

            if (_flights.Count >= maxFlights)
            {
                return (work(), true);
            }

            var mine = new Lazy<Task<QueryAnswer>>(work, LazyThreadSafetyMode.ExecutionAndPublication);
            if (!_flights.TryAdd(key, mine))
            {
                continue;   // another caller won the race for this key; wait for it instead
            }

            // The entry lives only while the work runs, so a finished answer is never held twice.
            _ = mine.Value.ContinueWith(
                static (_, state) =>
                {
                    var (map, flightKey) = ((ConcurrentDictionary<string, Lazy<Task<QueryAnswer>>>, string))state!;
                    map.TryRemove(flightKey, out var _);
                },
                (_flights, key),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return (mine.Value, true);
        }
    }
}
