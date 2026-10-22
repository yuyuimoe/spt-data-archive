using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Canonical;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// A run that changes a location, a bot type, a hideout part, a price and a handbook entry becomes rows
/// of the mod side, keyed by its dataset (ADR-0041, ADR-0003). The development database holds real
/// imports, so each case works inside a transaction that rolls back.
/// </summary>
public sealed class DatabaseObjectImportTests : IDisposable
{
    private const string Release = "999.3.0";
    private const string ModGuid = "com.example.objects";
    private const string OfficialItemId = "ffffffffffffffffffffffff";

    private static readonly string ItemD = ContentUlid.Create("item", "Item D");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-object-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task An_object_of_the_server_database_becomes_a_template_its_paths_and_the_source_of_an_added_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteTrees();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        var report = await ImportAsync(scope);

        Assert.Equal(14, report.ObjectCount);
        Assert.Empty(report.UnstoredChanges);

        var dataset = await scope.Db.ModDatasets.SingleAsync(row => row.ModGuid == ModGuid, cancellationToken);

        var templates = await scope.Db.ModTemplates
            .Where(row => row.DatasetId == dataset.Id && ModTemplateKind.DatabaseObjects.Contains(row.Kind))
            .ToListAsync(cancellationToken);
        Assert.Equal(14, templates.Count);
        Assert.Equal(
            [("bot", 3), ("handbook", 3), ("hideout", 3), ("location", 3), ("price", 2)],
            templates.GroupBy(row => row.Kind).OrderBy(group => group.Key).Select(group => (group.Key, group.Count())));

        // "What did this map mod change on woods?" has an answer: the value, and what it was before.
        var insurance = await scope.Db.ModChangePaths.SingleAsync(
            row => row.DatasetId == dataset.Id && row.Kind == "location" && row.TemplateId == "woods" && row.Path == "/base/Insurance",
            cancellationToken);
        Assert.Equal(("true", "false", ModChangePathOperation.Set), (insurance.OldValue, insurance.NewValue, insurance.Op));

        var price = await scope.Db.ModChangePaths.SingleAsync(
            row => row.DatasetId == dataset.Id && row.Kind == "price" && row.TemplateId == ObjectTree.ItemId,
            cancellationToken);
        Assert.Equal((string.Empty, "1000", "1200"), (price.Path, price.OldValue, price.NewValue));

        // The source of an added location is one row for each of its files, as the run wrote it.
        var airpit = ContentUlid.Create("location", "arena_airpit");
        var sources = await scope.Db.ModDatabaseObjects
            .Where(row => row.DatasetId == dataset.Id && row.TemplateId == airpit)
            .OrderBy(row => row.Part)
            .ToListAsync(cancellationToken);
        Assert.Equal(["base", "looseLoot"], sources.Select(row => row.Part));
        Assert.Equal("arena_airpit", JsonDocument.Parse(sources[0].Data).RootElement.GetProperty("_Id").GetString());

        // A price is a number, and a number is a document.
        var added = await scope.Db.ModDatabaseObjects.SingleAsync(
            row => row.DatasetId == dataset.Id && row.Kind == "price" && row.TemplateId == ItemD,
            cancellationToken);
        Assert.Equal((string.Empty, "4000"), (added.Part, added.Data));

        // A modified object has no source row of its own: its paths are the archive of the change. The one
        // row it keeps is the spawn point that the run adds, which is a row and not a path (ADR-0043).
        var spawn = Assert.Single(
            await scope.Db.ModDatabaseObjects
                .Where(row => row.DatasetId == dataset.Id && row.TemplateId == "woods")
                .ToListAsync(cancellationToken));
        Assert.Equal("looseLoot/spawnpoints/woods_2", spawn.Part);
        Assert.Equal(ModTemplateKind.Location, spawn.Kind);
        Assert.True(spawn.Bytes > 0);

        // The way back from the id of the run to the address (ADR-0034).
        Assert.True(await scope.Db.ModIdMaps.AnyAsync(
            row => row.DatasetId == dataset.Id && row.Kind == "location" && row.RawId == "arena_airpit" && row.Address == airpit,
            cancellationToken));

        var counts = JsonDocument.Parse(dataset.RowCounts).RootElement;
        Assert.Equal(3, counts.GetProperty("location").GetInt32());
    }

    [Fact]
    public async Task The_content_hash_the_import_stores_is_the_hash_without_the_object_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteTrees();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        var report = await ImportAsync(scope);

        var plan = Importer().Prepare();
        var withoutObjects = plan.Contribution with { Objects = [] };
        var files = plan.Files.Select(file => new ContributionFile(
            file.Path,
            file.Sha256,
            file.ContentText is null ? null : DatasetHash.HashText(file.ContentText)));

        Assert.Equal(DatasetHash.Compute(withoutObjects, files), report.ContentHash);
    }

    [Fact]
    public async Task The_same_run_imported_twice_writes_the_objects_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteTrees();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await ImportAsync(scope);

        var dataset = await scope.Db.ModDatasets.SingleAsync(row => row.ModGuid == ModGuid, cancellationToken);
        var before = await scope.Db.ModDatabaseObjects.CountAsync(row => row.DatasetId == dataset.Id, cancellationToken);

        var second = await ImportAsync(scope);

        Assert.False(second.IsNewDataset);
        Assert.Equal(before, await scope.Db.ModDatabaseObjects.CountAsync(row => row.DatasetId == dataset.Id, cancellationToken));
        Assert.True(before > 0);
    }

    [Fact]
    public async Task The_result_of_a_run_counts_the_objects_and_names_no_file_without_a_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteTrees();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        var importer = Importer();
        var plan = importer.Prepare();
        var report = await ImportAsync(scope);

        using var result = JsonDocument.Parse(RunResult.ForRun(plan, report));
        var root = result.RootElement;

        Assert.Equal(0, root.GetProperty("unstoredFiles").GetArrayLength());
        Assert.Equal(2, root.GetProperty("objects").GetProperty("location.modified").GetInt32());
        Assert.Equal(1, root.GetProperty("objects").GetProperty("location.added").GetInt32());
        Assert.True(root.GetProperty("pathRows").GetInt32() > 0);
    }

    [Fact]
    public async Task An_object_row_belongs_to_the_dataset_and_no_official_table_holds_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteTrees();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await ImportAsync(scope);

        // The official tables hold the one item that the import was compared against, and nothing else (ADR-0003).
        Assert.Equal(
            [OfficialItemId],
            await scope.Db.Items.Where(item => item.SptVersion == Release).Select(item => item.Id).ToListAsync(cancellationToken));
    }

    // ---- The table (ADR-0041) -------------------------------------------------------------------

    [Fact]
    public async Task A_source_row_needs_its_template_row()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var dataset = await InsertDatasetAsync(scope);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            "insert into mod_database_objects (dataset_id, kind, template_id, part, data) values ($1, 'bot', 'types/x', '', '{}')",
            dataset));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task A_source_row_states_a_kind_of_the_server_database()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var dataset = await InsertDatasetAsync(scope);

        await scope.ExecuteAsync(
            "insert into mod_templates (dataset_id, kind, template_id, op) values ($1, 'item', 'T1', 'added')",
            dataset);

        var error = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            "insert into mod_database_objects (dataset_id, kind, template_id, part, data) values ($1, 'item', 'T1', '', '{}')",
            dataset));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task A_location_holds_one_source_row_for_each_file_and_leaves_with_its_template()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var dataset = await InsertDatasetAsync(scope);

        await InsertLocationAsync(scope, dataset);
        await scope.ExecuteAsync(
            "insert into mod_database_objects (dataset_id, kind, template_id, part, data) values ($1, 'location', 'L1', 'looseLoot', '{}')",
            dataset);
        Assert.Equal(2L, await scope.ScalarAsync<long>("select count(*) from mod_database_objects where dataset_id = $1", dataset));

        await scope.ExecuteAsync("delete from mod_templates where dataset_id = $1", dataset);
        Assert.Equal(0L, await scope.ScalarAsync<long>("select count(*) from mod_database_objects where dataset_id = $1", dataset));
    }

    [Fact]
    public async Task One_file_of_a_location_is_stored_once()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var dataset = await InsertDatasetAsync(scope);

        await InsertLocationAsync(scope, dataset);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => scope.ExecuteAsync(
            "insert into mod_database_objects (dataset_id, kind, template_id, part, data) values ($1, 'location', 'L1', 'base', '{}')",
            dataset));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task The_reader_can_read_the_source_rows_and_cannot_write_them()
    {
        await using var connection = await ArchiveDatabase.OpenReaderAsync(TestContext.Current.CancellationToken);

        await using var read = new NpgsqlCommand("select count(*) from mod_database_objects", connection);
        Assert.NotNull(await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        await using var write = new NpgsqlCommand(
            "insert into mod_database_objects (dataset_id, kind, template_id, part, data) values (gen_random_uuid(), 'bot', 'x', '', '{}')",
            connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static async Task<Guid> InsertDatasetAsync(TestScope scope)
    {
        var dataset = Guid.CreateVersion7();

        await scope.ExecuteAsync(
            "insert into mod_datasets (id, mod_guid, content_hash, table_hashes, row_counts, config_origin, created_at) " +
            "values ($1, 'com.example.object-schema', $2, '{}'::jsonb, '{}'::jsonb, 'shipped', now())",
            dataset,
            "hash-" + dataset.ToString("N"));

        return dataset;
    }

    private static async Task InsertLocationAsync(TestScope scope, Guid dataset)
    {
        await scope.ExecuteAsync(
            "insert into mod_templates (dataset_id, kind, template_id, op) values ($1, 'location', 'L1', 'added')",
            dataset);
        await scope.ExecuteAsync(
            "insert into mod_database_objects (dataset_id, kind, template_id, part, data) values ($1, 'location', 'L1', 'base', '{\"a\":1}')",
            dataset);
    }

    private void WriteTrees()
    {
        RunTree.Write(_root, ModGuid, Release, Release);
        ObjectTree.Write(RunTree.Baseline(_root), ObjectTree.Baseline());
        ObjectTree.Write(RunTree.Run(_root), ObjectTree.RunOverBaseline());
    }

    private RunImporter Importer() =>
        new(
            Options.Create(new ArchiveOptions { RunPath = RunTree.Run(_root), RunModGuid = ModGuid, BaselinePath = RunTree.Baseline(_root) }),
            NullLogger<RunImporter>.Instance);

    private async Task<RunImportReport> ImportAsync(TestScope scope)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var importer = Importer();
        var plan = importer.Prepare();

        await EnsureOfficialReleaseAsync(scope, plan.Release, cancellationToken);

        var archiveRun = new ArchiveRun
        {
            SourceHash = plan.ContentHash,
            SptVersion = plan.Release,
            Kind = ArchiveRunKind.Mod,
            Status = ArchiveRun.Succeeded,
        };
        scope.Db.ArchiveRuns.Add(archiveRun);
        await scope.Db.SaveChangesAsync(cancellationToken);

        return await importer.ApplyAsync(scope.Db, plan, archiveRun.Id, cancellationToken);
    }

    /// <summary>The archive holds the official data of a release before it holds what a mod did to it.</summary>
    private static async Task EnsureOfficialReleaseAsync(TestScope scope, string release, CancellationToken cancellationToken)
    {
        if (await scope.Db.Items.AnyAsync(item => item.SptVersion == release, cancellationToken))
        {
            return;
        }

        await SptVersionRegistry.EnsureAsync(scope.Db, release, cancellationToken);

        scope.Db.Items.Add(new Item
        {
            Id = OfficialItemId,
            Name = "Official Item",
            ParentId = "root",
            Type = "Item",
            Props = "{}",
            SptVersion = release,
        });
        await scope.Db.SaveChangesAsync(cancellationToken);
    }
}
