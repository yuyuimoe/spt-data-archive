using Npgsql;

namespace SptArchive.Core.Data;

/// <summary>
/// The shape of the connection pool that one process opens (ADR-0038, amend. 2026-10-21).
/// </summary>
/// <remarks>
/// <para>
/// A pool that is larger than the server can serve does not add parallelism. Past the core count of the
/// database, an extra connection only waits its turn inside Postgres, and it costs a backend's memory.
/// A pool that is too small makes a burst wait in the Api instead. ADR-0038 keeps the arithmetic, and
/// the load test of 2026-10-21 measured which value wins.
/// </para>
/// <para>
/// The five settings here are the four timeouts that keep a pool healthy, plus its size: how long a
/// request waits for a free connection, how long an idle connection lives, how long any connection
/// lives, and the smallest number of connections kept warm.
/// </para>
/// </remarks>
public sealed class DatabasePoolOptions
{
    public const string Section = "Database";

    /// <summary>
    /// The most connections one process opens. Zero or less leaves the Npgsql default of 100.
    /// </summary>
    /// <remarks>
    /// 16 leaves 34 of the 50 connections of compose.yaml for the Archiver, migrations and an admin
    /// session, and it lets two Api instances fit under the same ceiling. The load test of 2026-10-21
    /// measured 3 to 4 backends busy at 4,000 clients and the same latency at a pool of 8, 16 and 30.
    /// </remarks>
    public int? MaxPoolSize { get; set; } = 16;

    /// <summary>
    /// Connections kept open while the Api is idle. A small floor pays the first connect once and not
    /// on the first request after a quiet period.
    /// </summary>
    public int MinPoolSize { get; set; }

    /// <summary>
    /// How long a request waits for a free connection before it fails. It must stay under the request
    /// budget of the Api (10 s), so a saturated pool answers with an error instead of a slow answer.
    /// </summary>
    public int AcquireTimeoutSeconds { get; set; } = 5;

    /// <summary>How long a connection may stay idle before the pool closes it.</summary>
    public int IdleLifetimeSeconds { get; set; } = 60;

    /// <summary>
    /// How long any connection may live. A bounded life recycles a connection that a restart, a failover
    /// or a load balancer left half dead.
    /// </summary>
    public int LifetimeSeconds { get; set; } = 1800;

    /// <summary>Writes the settings into a connection string.</summary>
    public void Apply(NpgsqlConnectionStringBuilder builder)
    {
        if (MaxPoolSize is > 0)
        {
            builder.MaxPoolSize = MaxPoolSize.Value;
        }

        builder.MinPoolSize = Math.Max(0, MinPoolSize);
        builder.Timeout = Math.Max(1, AcquireTimeoutSeconds);
        builder.ConnectionIdleLifetime = Math.Max(0, IdleLifetimeSeconds);
        builder.ConnectionLifetime = Math.Max(0, LifetimeSeconds);
    }
}
