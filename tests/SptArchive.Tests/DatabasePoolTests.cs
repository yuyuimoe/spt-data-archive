using Npgsql;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>
/// The connection pool of the reader (TD-53, ADR-0038 amended on 2026-10-21). The size keeps the fleet
/// under the max_connections of the server, and the four timeouts keep a saturated pool from looking
/// like a hang.
/// </summary>
public sealed class DatabasePoolTests
{
    private static NpgsqlConnectionStringBuilder Applied(DatabasePoolOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder("Host=db;Database=archive;Username=archive_reader");
        options.Apply(builder);
        return builder;
    }

    [Fact]
    public void The_defaults_cap_the_pool_and_bound_its_timeouts()
    {
        var connection = Applied(new DatabasePoolOptions());

        Assert.Equal(16, connection.MaxPoolSize);
        Assert.Equal(0, connection.MinPoolSize);
        Assert.Equal(5, connection.Timeout);
        Assert.Equal(60, connection.ConnectionIdleLifetime);
        Assert.Equal(1800, connection.ConnectionLifetime);
    }

    [Fact]
    public void A_pool_that_is_left_open_keeps_the_npgsql_default()
    {
        var connection = Applied(new DatabasePoolOptions { MaxPoolSize = null });

        Assert.Equal(100, connection.MaxPoolSize);
    }

    [Fact]
    public void An_acquire_timeout_of_zero_still_waits()
    {
        // A zero would mean "wait forever", which turns a saturated pool into a request that never
        // answers. One second is the smallest honest value.
        Assert.Equal(1, Applied(new DatabasePoolOptions { AcquireTimeoutSeconds = 0 }).Timeout);
    }

    [Fact]
    public void A_negative_size_is_not_a_size()
    {
        var connection = Applied(new DatabasePoolOptions { MinPoolSize = -4, IdleLifetimeSeconds = -1 });

        Assert.Equal(0, connection.MinPoolSize);
        Assert.Equal(0, connection.ConnectionIdleLifetime);
    }

    [Fact]
    public void The_pool_fits_under_the_connections_the_server_allows()
    {
        // compose.yaml starts Postgres with max_connections=50 for one Api instance. The pool of one
        // process leaves a fifth of them for the Archiver, migrations and an admin session.
        var pool = Applied(new DatabasePoolOptions()).MaxPoolSize;

        Assert.True(pool <= 50 - (50 / 5), $"{pool} of 50 connections leaves too little for anything else");
    }
}
