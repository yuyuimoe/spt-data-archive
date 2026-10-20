using Microsoft.EntityFrameworkCore;
using Npgsql;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>
/// The database the tests run against. The default is the development database of
/// <c>docker/compose.yml</c>; set <c>ARCHIVE_TEST_CONNECTION</c> to point somewhere else.
/// </summary>
/// <remarks>
/// The tests apply pending migrations once, then each test works inside a transaction that it
/// rolls back. The archive is never left with test rows.
/// </remarks>
public static class ArchiveDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _migrated;

    public static string OwnerConnectionString =>
        Environment.GetEnvironmentVariable("ARCHIVE_TEST_CONNECTION")
        ?? "Host=127.0.0.1;Port=5432;Database=archive;Username=archive_owner;Password=dev_owner_password";

    /// <summary>The read-only role of the Api. It must be able to read and must not be able to write.</summary>
    public static string ReaderConnectionString =>
        Environment.GetEnvironmentVariable("ARCHIVE_TEST_READER_CONNECTION")
        ?? "Host=127.0.0.1;Port=5432;Database=archive;Username=archive_reader;Password=dev_reader_password";

    public static async Task EnsureMigratedAsync(CancellationToken cancellationToken = default)
    {
        if (_migrated)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_migrated)
            {
                return;
            }

            await using var db = CreateContext(OwnerConnectionString);
            await db.Database.MigrateAsync(cancellationToken);
            _migrated = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static ArchiveDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ArchiveDbContext(options);
    }

    /// <summary>
    /// Opens one connection with one transaction and one context on it. Disposing the scope rolls
    /// everything back, so the tests can write without cleaning up.
    /// </summary>
    public static async Task<TestScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync();

        var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);

        var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseNpgsql(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        var db = new ArchiveDbContext(options);
        await db.Database.UseTransactionAsync(transaction);

        return new TestScope(connection, transaction, db);
    }

    public static async Task<NpgsqlConnection> OpenReaderAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ReaderConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

/// <summary>One connection, one transaction and one context. Disposing it rolls the work back.</summary>
public sealed class TestScope(NpgsqlConnection connection, NpgsqlTransaction transaction, ArchiveDbContext db)
    : IAsyncDisposable
{
    public ArchiveDbContext Db { get; } = db;

    public NpgsqlConnection Connection { get; } = connection;

    public async Task<int> ExecuteAsync(string sql, params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, Connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a query that returns one value.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, Connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        var result = await command.ExecuteScalarAsync();
        return (T)result!;
    }

    /// <summary>Runs a query that returns one array value, such as a key column list.</summary>
    public async Task<string[]> StringArrayAsync(string sql, params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, Connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetFieldValue<string[]>(0) : [];
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
