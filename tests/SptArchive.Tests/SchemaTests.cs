using Microsoft.EntityFrameworkCore;
using Npgsql;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The schema guarantees of the first migration: one release key on every official table
/// (ADR-0014), one dataset key on every mod table (ADR-0013), a template row above every change
/// (ADR-0017), and a reader role that can only read.
/// </summary>
public sealed class SchemaTests
{
    private const string PrimaryKeyColumns = """
        select array_agg(a.attname order by k.ord)
        from pg_constraint p
        join pg_class c on c.oid = p.conrelid
        join lateral unnest(p.conkey) with ordinality as k(attnum, ord) on true
        join pg_attribute a on a.attrelid = c.oid and a.attnum = k.attnum
        where p.contype = 'p' and c.relname = $1
        """;

    [Fact]
    public async Task The_initial_migration_is_applied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await ArchiveDatabase.EnsureMigratedAsync(cancellationToken);
        await using var db = ArchiveDatabase.CreateContext(ArchiveDatabase.OwnerConnectionString);

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);

        Assert.Empty(pending);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("customizations")]
    [InlineData("locale_entries")]
    [InlineData("quests")]
    [InlineData("traders")]
    [InlineData("trader_assort_items")]
    [InlineData("trader_quest_assorts")]
    public async Task Every_official_table_states_its_release_in_the_key(string table)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var columns = await scope.StringArrayAsync(PrimaryKeyColumns, table);

        Assert.Equal("spt_version", columns[0]);
    }

    [Theory]
    [InlineData("mod_items")]
    [InlineData("mod_customizations")]
    [InlineData("mod_locale_entries")]
    [InlineData("mod_quests")]
    [InlineData("mod_traders")]
    [InlineData("mod_trader_assort_items")]
    [InlineData("mod_trader_quest_assorts")]
    [InlineData("mod_files")]
    [InlineData("mod_id_maps")]
    [InlineData("mod_templates")]
    [InlineData("mod_change_paths")]
    public async Task Every_mod_table_is_keyed_by_its_dataset(string table)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var columns = await scope.StringArrayAsync(PrimaryKeyColumns, table);

        Assert.Equal("dataset_id", columns[0]);
        Assert.DoesNotContain("spt_version", columns);
    }

    [Fact]
    public async Task An_official_row_needs_a_release_that_the_archive_holds()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            "insert into items (spt_version, id, name, parent_id, type, props) values ($1, $2, $3, $4, $5, '{}')",
            "1.2.3", "000000000000000000000001", "Item A", "node", "Item"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task A_mod_row_needs_a_dataset_that_exists()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            "insert into mod_items (dataset_id, id, name, parent_id, type, props) values ($1, $2, $3, $4, $5, '{}')",
            Guid.CreateVersion7(), "000000000000000000000002", "Mod item", "node", "Item"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task Two_datasets_can_add_the_same_item_id()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await InsertDatasetAsync(scope, first, "com.example.a", "hash-a");
        await InsertDatasetAsync(scope, second, "com.example.b", "hash-b");

        for (var index = 0; index < 2; index++)
        {
            await scope.ExecuteAsync(
                "insert into mod_items (dataset_id, id, name, parent_id, type, props) values ($1, $2, $3, $4, $5, '{}')",
                index == 0 ? first : second, "000000000000000000000003", "B1", "node", "Item");
        }

        var count = await scope.ScalarAsync<long>(
            "select count(*) from mod_items where id = $1", "000000000000000000000003");

        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("assort")]
    [InlineData("location")]
    [InlineData("bot")]
    [InlineData("hideout")]
    [InlineData("price")]
    [InlineData("handbook")]
    public async Task A_change_path_states_a_kind_the_archive_holds(string kind)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var dataset = Guid.CreateVersion7();
        await InsertDatasetAsync(scope, dataset, "com.example.b", "hash-b");
        await scope.ExecuteAsync(
            "insert into mod_templates (dataset_id, kind, template_id, op) values ($1, $2, 'T1', 'modified')",
            dataset,
            kind);

        await scope.ExecuteAsync(
            """
            insert into mod_change_paths (dataset_id, kind, template_id, path, op, old_value, new_value)
            values ($1, $2, 'T1', '/_props/Weight', 'set', '1', '2')
            """,
            dataset,
            kind);

        var rows = await scope.Db.ModChangePaths.CountAsync(
            row => row.DatasetId == dataset,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task A_change_path_of_an_unknown_kind_is_refused()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var dataset = Guid.CreateVersion7();
        await InsertDatasetAsync(scope, dataset, "com.example.c", "hash-c");

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            """
            insert into mod_change_paths (dataset_id, kind, template_id, path, op, old_value, new_value)
            values ($1, 'spawnpoint', 'T1', '/_props/Weight', 'set', '1', '2')
            """,
            dataset));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task A_change_path_needs_its_template_row()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var dataset = Guid.CreateVersion7();
        await InsertDatasetAsync(scope, dataset, "com.example.a", "hash-a");

        // mod_change_paths has no foreign key of its own to mod_datasets: it names the parent key.
        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            """
            insert into mod_change_paths (dataset_id, kind, template_id, path, op, old_value, new_value)
            values ($1, 'item', 'A1', '/_props/Weight', 'set', '1', '2')
            """,
            dataset));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    /// <summary>An observation names its run (ADR-0033), so a test that inserts one needs a run first.</summary>
    private static async Task<Guid> InsertRunAsync(TestScope scope)
    {
        var runId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            "insert into archive_runs (id, started_at, source_hash, status, kind) values ($1, now(), 'test', 'running', 'mod')",
            runId);
        return runId;
    }

    [Fact]
    public async Task An_observation_outcome_must_be_one_of_the_known_values()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var modId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            "insert into spt_versions (version, version_parts, archived_at) values ('9.9.9', $1, now())",
            new[] { 9, 9, 9, 0 });
        await scope.ExecuteAsync(
            "insert into mods (id, mod_guid, name, version, archived_at) values ($1, 'com.example.a', 'Example', '1.0.0', now())",
            modId);
        var runId = await InsertRunAsync(scope);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            """
            insert into mod_data_observations (id, mod_id, spt_version, outcome, kind, observed_at, archive_run_id)
            values ($1, $2, '9.9.9', 'fine', 'archive', now(), $3)
            """,
            Guid.CreateVersion7(), modId, runId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Theory]
    [InlineData(ModObservationOutcome.NoServerData)]
    [InlineData(ModObservationOutcome.LoadedButDisabled)]
    public async Task An_outcome_that_states_a_mod_did_nothing_is_a_known_value(string outcome)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        Assert.Contains(outcome, ModObservationOutcome.All);

        var modId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            "insert into spt_versions (version, version_parts, archived_at) values ('9.9.9', $1, now())",
            new[] { 9, 9, 9, 0 });
        await scope.ExecuteAsync(
            "insert into mods (id, mod_guid, name, version, archived_at) values ($1, 'com.example.b', 'Example', '1.0.0', now())",
            modId);
        var runId = await InsertRunAsync(scope);

        await scope.ExecuteAsync(
            """
            insert into mod_data_observations (id, mod_id, spt_version, outcome, kind, observed_at, archive_run_id)
            values ($1, $2, '9.9.9', $3, 'archive', now(), $4)
            """,
            Guid.CreateVersion7(), modId, outcome, runId);
    }

    [Fact]
    public async Task A_dataset_uses_the_shipped_configuration_unless_it_says_otherwise()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var shipped = Guid.CreateVersion7();
        await InsertDatasetAsync(scope, shipped, "com.example.shipped", "hash-shipped");

        var origin = await scope.ScalarAsync<string>(
            "select config_origin from mod_datasets where id = $1", shipped);
        Assert.Equal(ModConfigOrigin.Shipped, origin);

        // ADR-0029 decision 7: a curated configuration is a second dataset, named as such.
        var curated = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into mod_datasets (id, mod_guid, content_hash, table_hashes, row_counts, config_origin,
                                      config_origin_reason, created_at)
            values ($1, 'com.example.shipped', 'hash-curated', '{}'::jsonb, '{}'::jsonb, 'curated',
                    'The overlay is the feature of the mod', now())
            """,
            curated);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            "update mod_datasets set config_origin = 'invented' where id = $1", shipped));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task A_file_needs_a_dataset_that_exists()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            """
            insert into mod_files (dataset_id, path, kind, size, sha256)
            values ($1, 'Example/config/config.json', 'config', 10, 'abc')
            """,
            Guid.CreateVersion7()));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task A_file_states_its_kind()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var dataset = Guid.CreateVersion7();
        await InsertDatasetAsync(scope, dataset, "com.example.files", "hash-files");

        foreach (var kind in ModFileKind.All)
        {
            await scope.ExecuteAsync(
                """
                insert into mod_files (dataset_id, path, kind, size, sha256)
                values ($1, $2, $3, 10, 'abc')
                """,
                dataset, $"Example/{kind}", kind);
        }

        // The text is the extra: a configuration file keeps what the mod read.
        await scope.ExecuteAsync(
            """
            insert into mod_files (dataset_id, path, kind, size, sha256, content_text, content)
            values ($1, 'Example/config/config.json', 'config', 10, 'abc', '{"a":1}', '{"a":1}'::jsonb)
            """,
            dataset);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            """
            insert into mod_files (dataset_id, path, kind, size, sha256)
            values ($1, 'Example/bundle.bundle', 'asset', 10, 'abc')
            """,
            dataset));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Two_datasets_can_hold_the_same_file_path()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await InsertDatasetAsync(scope, first, "com.example.first", "hash-first");
        await InsertDatasetAsync(scope, second, "com.example.second", "hash-second");

        foreach (var dataset in new[] { first, second })
        {
            await scope.ExecuteAsync(
                """
                insert into mod_files (dataset_id, path, kind, size, sha256)
                values ($1, 'Example/db/Items/x.json', 'data', 10, 'abc')
                """,
                dataset);
        }

        var count = await scope.ScalarAsync<long>(
            "select count(*) from mod_files where path = 'Example/db/Items/x.json'");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task The_reader_role_can_read_and_cannot_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await ArchiveDatabase.EnsureMigratedAsync(cancellationToken);
        await using var connection = await ArchiveDatabase.OpenReaderAsync(cancellationToken);

        await using (var read = new NpgsqlCommand("select count(*) from items", connection))
        {
            await read.ExecuteScalarAsync(cancellationToken); // must not throw
        }

        await using var write = new NpgsqlCommand(
            "insert into spt_versions (version, version_parts, archived_at) values ('8.8.8', $1, now())", connection);
        write.Parameters.Add(new NpgsqlParameter { Value = new[] { 8, 8, 8, 0 } });

        var error = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(cancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);

        await using var writeFile = new NpgsqlCommand(
            """
            insert into mod_files (dataset_id, path, kind, size, sha256)
            values ('00000000-0000-0000-0000-000000000000', 'Example/x.dll', 'assembly', 1, 'abc')
            """, connection);

        var fileError = await Assert.ThrowsAsync<PostgresException>(
            () => writeFile.ExecuteNonQueryAsync(cancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, fileError.SqlState);
    }

    private static Task<int> InsertDatasetAsync(TestScope scope, Guid id, string modGuid, string contentHash) =>
        scope.ExecuteAsync(
            """
            insert into mod_datasets (id, mod_guid, content_hash, table_hashes, row_counts, created_at)
            values ($1, $2, $3, '{}'::jsonb, '{}'::jsonb, now())
            """,
            id, modGuid, contentHash);
}

