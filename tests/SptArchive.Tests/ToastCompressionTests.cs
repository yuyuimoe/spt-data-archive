using Npgsql;

namespace SptArchive.Tests;

/// <summary>
/// The compression that a jsonb column uses in storage (ADR-0041). PostgreSQL compresses a value that
/// does not fit in its row, and lz4 stores a map file in fewer bytes and reads it about three times
/// faster than the default pglz.
/// </summary>
public sealed class ToastCompressionTests
{
    /// <summary>Every jsonb column of the archive, with the compression that its definition asks for.</summary>
    private static async Task<List<(string Table, string Column, char Compression)>> JsonbColumnsAsync()
    {
        await using var connection = await ArchiveDatabase.OpenReaderAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, a.attname, a.attcompression
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND format_type(a.atttypid, a.atttypmod) = 'jsonb'
            ORDER BY c.relname, a.attname
            """,
            connection);

        var rows = new List<(string, string, char)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetChar(2)));
        }

        return rows;
    }

    [Fact]
    public async Task Every_jsonb_column_asks_for_lz4()
    {
        await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);

        var columns = await JsonbColumnsAsync();

        // A test that finds no column would pass without proving anything.
        Assert.NotEmpty(columns);

        var notLz4 = columns.Where(c => c.Compression != 'l').Select(c => $"{c.Table}.{c.Column}").ToList();
        Assert.Empty(notLz4);
    }

    /// <summary>
    /// The definition says lz4, but that is not the same as the bytes on disk saying lz4: a server that
    /// was built without lz4 accepts the setting and stores pglz anyway. This test writes a value that is
    /// too large for its row and asks PostgreSQL which method it used.
    /// </summary>
    [Fact]
    public async Task A_value_that_does_not_fit_in_its_row_is_stored_with_lz4()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        await scope.ExecuteAsync("CREATE TEMP TABLE toast_probe (doc jsonb) ON COMMIT DROP;");
        await scope.ExecuteAsync("ALTER TABLE toast_probe ALTER COLUMN doc SET COMPRESSION lz4;");

        // The value is built inside the INSERT on purpose. A value that is copied from another toasted
        // column keeps the pointer of the source, and PostgreSQL then reports the method that the source
        // used, whatever the target column asked for.
        await scope.ExecuteAsync(
            """
            INSERT INTO toast_probe (doc)
            SELECT jsonb_agg(jsonb_build_object('k', v))
            FROM (SELECT md5(g::text) || md5((g + 1)::text) AS v FROM generate_series(1, 50000) g) s;
            """);

        var method = await scope.ScalarAsync<string>("SELECT pg_column_compression(doc) FROM toast_probe;");

        Assert.Equal("lz4", method);
    }

    [Fact]
    public async Task The_model_still_holds_its_whole_documents_as_jsonb()
    {
        await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);

        var columns = await JsonbColumnsAsync();

        // ADR-0002 keeps the full source object, so these columns must stay. If one of them disappears,
        // the compression question moved and this test should be looked at again.
        Assert.Contains(columns, c => c.Table == "items" && c.Column == "props");
        Assert.Contains(columns, c => c.Table == "mod_database_objects" && c.Column == "data");
        Assert.Contains(columns, c => c.Table == "mod_files" && c.Column == "content");
    }
}
