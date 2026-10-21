using System.Text.Json;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The Api surface of the mod tables (ADR-0032, action item 7, and ADR-0033, action item 5), checked
/// against the schema that the Api serves. Each test writes its rows inside a transaction that it rolls
/// back, so the archive is never left with test data.
/// </summary>
public sealed class ModSurfaceTests
{
    // Higher than any real release, so these rows belong to the newest release inside the transaction.
    private const string Release = "9990.1.0";
    private const string ModGuid = "com.example.mod";

    // A content ULID, the address of an object that the run made (ADR-0034).
    private const string Address = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    // Far in the future, so the two runs of this transaction are the newest rows even when the database
    // holds archived runs of its own: a run history sorted by start time must not depend on the day the
    // suite runs. The year matches Release, which is higher than any real release for the same reason.
    private static readonly DateTimeOffset Start = new(2999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid Items = Guid.Parse("01a15b00-0000-7000-8000-000000000001");
    private static readonly Guid Behaviour = Guid.Parse("01a15b00-0000-7000-8000-000000000002");
    private static readonly Guid WithOffer = Guid.Parse("01a15b00-0000-7000-8000-000000000003");
    private static readonly Guid Removed = Guid.Parse("01a15b00-0000-7000-8000-000000000004");

    private sealed record Seed(Guid OfficialRun, Guid ModRun, Guid Mod, Guid Dataset);

    /// <summary>
    /// One dataset with an added item and a changed item, one dataset with no rows (a behaviour mod),
    /// one dataset that adds an offer, one that removes a property, one observation and two runs.
    /// </summary>
    private static async Task<Seed> SeedAsync(TestScope scope)
    {
        var ct = TestContext.Current.CancellationToken;
        await SptVersionRegistry.EnsureAsync(scope.Db, Release, ct);

        var officialRun = new ArchiveRun
        {
            StartedAt = Start,
            FinishedAt = Start.AddMinutes(1),
            SourceHash = "sha256:official-source",
            SptVersion = Release,
            Status = ArchiveRun.Succeeded,
            Kind = ArchiveRunKind.Official,
            Purpose = "the official import of the release",
        };

        var modRun = new ArchiveRun
        {
            StartedAt = Start.AddHours(1),
            FinishedAt = Start.AddHours(1).AddSeconds(30),
            SourceHash = "sha256:mod-source",
            SptVersion = Release,
            Status = ArchiveRun.Succeeded,
            Kind = ArchiveRunKind.Mod,
            Purpose = "measure what the mod adds to a trader",
            TargetModGuid = ModGuid,
            TargetVersion = "1.0.0",
            BaselineRelease = Release,
            BaselineHash = "sha256:baseline",
            Result = """{"templates":4,"warnings":[]}""",
        };

        var mod = new Mod { ModGuid = ModGuid, Name = "Example", Version = "1.0.0" };

        scope.Db.ArchiveRuns.AddRange(officialRun, modRun);
        scope.Db.Mods.Add(mod);
        scope.Db.ModDatasets.AddRange(
            Dataset(Items, ModGuid, """{"items":3}""", """{"items":3,"quests":0}""", modRun.Id),
            Dataset(Behaviour, "com.example.behaviour", "{}", "{}"),
            Dataset(WithOffer, "com.example.trader", """{"assort":1}""", """{"assort":1}"""),
            Dataset(Removed, "com.example.removed", """{"items":1}""", """{"items":1}"""));
        await scope.Db.SaveChangesAsync(ct);

        scope.Db.ModDataObservations.Add(new ModDataObservation
        {
            ModId = mod.Id,
            SptVersion = Release,
            DatasetId = Items,
            Outcome = ModObservationOutcome.Archived,
            Kind = ModObservationKind.Archive,
            ArchiveRunId = modRun.Id,
            DependencySet = """[{"guid":"com.example.dep","version":"2.0.0"}]""",
            DependencySetHash = "sha256:dependencies",
            ObservedAt = Start.AddHours(1),
        });

        scope.Db.ModTemplates.AddRange(
            Template(Items, ModTemplateKind.Item, "A1", ModTemplateOperation.Modified),
            Template(Items, ModTemplateKind.Item, "B1", ModTemplateOperation.Added, "node-weapons"),
            Template(WithOffer, ModTemplateKind.Assort, Address, ModTemplateOperation.Added),
            Template(Removed, ModTemplateKind.Item, "A1", ModTemplateOperation.Modified));

        scope.Db.ModChangePaths.AddRange(
            Path(Items, ModTemplateKind.Item, "A1", "/_props/Weight", ModChangePathOperation.Set, "0.5", "0.3"),
            Path(Items, ModTemplateKind.Item, "B1", "/_props/Weight", ModChangePathOperation.Set, null, "1.0"),
            Path(Items, ModTemplateKind.Item, "B1", "/_props/Name", ModChangePathOperation.Set, null, "\"Added\""),
            Path(Removed, ModTemplateKind.Item, "A1", "/_props/CanSellOnRagfair", ModChangePathOperation.Remove, "true", null));

        scope.Db.ModFiles.AddRange(
            new ModFile
            {
                DatasetId = Items,
                Path = "com.example.mod/db/items/weapons.json",
                Kind = ModFileKind.Data,
                Size = 512,
                Sha256 = "sha256:weapons",
                ShippedSha256 = "sha256:weapons",
            },
            new ModFile
            {
                DatasetId = Items,
                Path = "com.example.mod/config/config.json",
                Kind = ModFileKind.Config,
                Size = 128,
                Sha256 = "sha256:config",
                ContentText = """{"Loot":2}""",
                Content = """{"Loot":2}""",
            });

        scope.Db.ModIdMaps.Add(new ModIdMap
        {
            DatasetId = Items,
            Kind = ModTemplateKind.Item,
            RawId = "6a1f",
            Address = Address,
            Pinned = false,
        });

        await scope.Db.SaveChangesAsync(ct);
        return new Seed(officialRun.Id, modRun.Id, mod.Id, Items);
    }

    private static ModDataset Dataset(Guid id, string modGuid, string tableHashes, string rowCounts, Guid? createdByRunId = null) => new()
    {
        Id = id,
        ModGuid = modGuid,
        ContentHash = $"sha256:content-{id}",
        TableHashes = tableHashes,
        RowCounts = rowCounts,
        CreatedByRunId = createdByRunId,
    };

    private static ModTemplate Template(Guid dataset, string kind, string templateId, string op, string? parentId = null) => new()
    {
        DatasetId = dataset,
        Kind = kind,
        TemplateId = templateId,
        Op = op,
        ParentId = parentId,
    };

    private static ModChangePath Path(
        Guid dataset, string kind, string templateId, string path, string op, string? oldValue, string? newValue) => new()
    {
        DatasetId = dataset,
        Kind = kind,
        TemplateId = templateId,
        Path = path,
        Op = op,
        OldValue = oldValue,
        NewValue = newValue,
    };

    [Fact]
    public async Task A_client_finds_the_dataset_of_a_mod()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modDatasets(where: { modGuid: { eq: "{{ModGuid}}" } }) { nodes { modGuid contentHash configOrigin } } }""");

        var nodes = ApiHarness.Nodes(response, "modDatasets");
        Assert.Equal(1, nodes.GetArrayLength());
        Assert.Equal(ModGuid, nodes[0].GetProperty("modGuid").GetString());
        Assert.StartsWith("sha256:content-", nodes[0].GetProperty("contentHash").GetString());
        Assert.Equal(ModConfigOrigin.Shipped, nodes[0].GetProperty("configOrigin").GetString());
    }

    [Fact]
    public async Task A_dataset_is_read_by_its_id_and_states_its_row_counts_as_json()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var seed = await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modDataset(id: "{{seed.Dataset}}") { id modGuid createdByRunId rowCounts } }""");

        var dataset = response.GetProperty("data").GetProperty("modDataset");
        Assert.Equal(seed.Dataset.ToString("D"), dataset.GetProperty("id").GetString());
        Assert.Equal(ModGuid, dataset.GetProperty("modGuid").GetString());
        Assert.Equal(seed.ModRun.ToString("D"), dataset.GetProperty("createdByRunId").GetString());
        Assert.Equal(3, dataset.GetProperty("rowCounts").GetProperty("items").GetInt32());
    }

    [Fact]
    public async Task An_unknown_dataset_id_is_null()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            """{ modDataset(id: "01a15bff-0000-7000-8000-0000000000ff") { id } }""");

        Assert.Equal(JsonValueKind.Null, response.GetProperty("data").GetProperty("modDataset").ValueKind);
        Assert.False(response.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task A_dataset_that_changed_no_table_is_still_a_dataset()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        // One root field per request: two root fields of one document run at the same time, and the
        // test transaction holds one connection. A real request opens a connection of its own.
        var dataset = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modDataset(id: "{{Behaviour}}") { contentHash rowCounts } }""");
        var templates = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modTemplates(where: { datasetId: { eq: "{{Behaviour}}" } }) { nodes { templateId } } }""");

        var node = dataset.GetProperty("data").GetProperty("modDataset");
        Assert.Equal(JsonValueKind.Object, node.GetProperty("rowCounts").ValueKind);
        Assert.NotEmpty(node.GetProperty("contentHash").GetString()!);
        Assert.Equal(0, ApiHarness.Nodes(templates, "modTemplates").GetArrayLength());
    }

    [Fact]
    public async Task A_mod_field_takes_no_server_version()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        // A dataset carries no release (ADR-0013). The observation states the release of each attempt.
        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modDatasets(sptVersion: "{{Release}}") { nodes { id } } }""");

        var message = response.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains("sptVersion", message);

        var observations = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modDataObservations(where: { datasetId: { eq: "{{Items}}" } }) { nodes { sptVersion } } }""");
        Assert.Equal(Release, ApiHarness.Nodes(observations, "modDataObservations")[0].GetProperty("sptVersion").GetString());
    }

    [Fact]
    public async Task A_client_reads_the_templates_that_a_dataset_touched()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modTemplates(where: { datasetId: { eq: "{{Items}}" } }) { nodes { kind templateId op parentId } } }""");

        var rows = ApiHarness.Nodes(response, "modTemplates").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        var changed = rows.Single(r => r.GetProperty("templateId").GetString() == "A1");
        Assert.Equal(ModTemplateOperation.Modified, changed.GetProperty("op").GetString());

        var added = rows.Single(r => r.GetProperty("templateId").GetString() == "B1");
        Assert.Equal(ModTemplateOperation.Added, added.GetProperty("op").GetString());
        Assert.Equal("node-weapons", added.GetProperty("parentId").GetString());
    }

    [Fact]
    public async Task An_offer_is_a_template_of_its_own_kind()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modTemplates(where: { datasetId: { eq: "{{WithOffer}}" }, kind: { eq: "{{ModTemplateKind.Assort}}" } }) { nodes { templateId } } }""");

        var nodes = ApiHarness.Nodes(response, "modTemplates");
        Assert.Equal(1, nodes.GetArrayLength());
        Assert.Equal(Address, nodes[0].GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task A_changed_value_states_its_old_and_its_new_value_as_json()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modChangePaths(where: { datasetId: { eq: "{{Items}}" }, templateId: { eq: "A1" } }) { nodes { path op oldValue newValue } } }""");

        var node = ApiHarness.Nodes(response, "modChangePaths")[0];
        Assert.Equal("/_props/Weight", node.GetProperty("path").GetString());
        Assert.Equal(ModChangePathOperation.Set, node.GetProperty("op").GetString());

        // The values are JSON numbers and not strings: a client reads a document, not text (TD-15).
        Assert.Equal(JsonValueKind.Number, node.GetProperty("oldValue").ValueKind);
        Assert.Equal(0.5, node.GetProperty("oldValue").GetDouble());
        Assert.Equal(0.3, node.GetProperty("newValue").GetDouble());
    }

    [Fact]
    public async Task An_added_template_states_no_old_value()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modChangePaths(where: { datasetId: { eq: "{{Items}}" }, templateId: { eq: "B1" } }) { nodes { oldValue newValue } } }""");

        var rows = ApiHarness.Nodes(response, "modChangePaths").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("oldValue").ValueKind));
    }

    [Fact]
    public async Task A_removed_property_states_the_operation_remove_and_no_new_value()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modChangePaths(where: { datasetId: { eq: "{{Removed}}" } }) { nodes { path op oldValue newValue } } }""");

        var node = ApiHarness.Nodes(response, "modChangePaths")[0];
        Assert.Equal("/_props/CanSellOnRagfair", node.GetProperty("path").GetString());
        Assert.Equal(ModChangePathOperation.Remove, node.GetProperty("op").GetString());
        Assert.Equal(JsonValueKind.True, node.GetProperty("oldValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("newValue").ValueKind);
    }

    [Fact]
    public async Task A_client_reads_the_files_of_a_dataset()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFiles(where: { datasetId: { eq: "{{Items}}" } }) { nodes { path kind size sha256 shippedSha256 } } }""");

        var rows = ApiHarness.Nodes(response, "modFiles").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("com.example.mod/config/config.json", rows[0].GetProperty("path").GetString());
        Assert.Equal(ModFileKind.Config, rows[0].GetProperty("kind").GetString());
        Assert.Equal(128, rows[0].GetProperty("size").GetInt64());
        Assert.Equal("sha256:config", rows[0].GetProperty("sha256").GetString());
        Assert.Equal("sha256:weapons", rows[1].GetProperty("shippedSha256").GetString());
    }

    [Fact]
    public async Task A_configuration_file_states_its_text_and_its_document()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFile(datasetId: "{{Items}}", path: "com.example.mod/config/config.json") { kind contentText content } }""");

        var file = response.GetProperty("data").GetProperty("modFile");
        Assert.Equal(ModFileKind.Config, file.GetProperty("kind").GetString());
        Assert.Equal("""{"Loot":2}""", file.GetProperty("contentText").GetString());
        Assert.Equal(2, file.GetProperty("content").GetProperty("Loot").GetInt32());
    }

    [Fact]
    public async Task A_file_that_the_dataset_does_not_hold_is_null()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFile(datasetId: "{{Items}}", path: "com.example.mod/db/absent.json") { path } }""");

        Assert.Equal(JsonValueKind.Null, response.GetProperty("data").GetProperty("modFile").ValueKind);
        Assert.False(response.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task An_id_of_the_run_finds_its_address()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            """{ modIdMaps(where: { rawId: { eq: "6a1f" } }) { nodes { kind rawId address pinned } } }""");

        var node = ApiHarness.Nodes(response, "modIdMaps")[0];
        Assert.Equal(Address, node.GetProperty("address").GetString());
        Assert.False(node.GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task The_run_history_is_newest_first()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var seed = await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            """{ archiveRuns(first: 10) { nodes { id kind purpose status result } } }""");

        var rows = ApiHarness.Nodes(response, "archiveRuns").EnumerateArray().ToList();

        // Another test may leave runs of its own transaction in the table only if it committed; the two
        // rows of this transaction come first because they are the newest.
        Assert.Equal(seed.ModRun.ToString("D"), rows[0].GetProperty("id").GetString());
        Assert.Equal(seed.OfficialRun.ToString("D"), rows[1].GetProperty("id").GetString());
        Assert.Equal(ArchiveRunKind.Mod, rows[0].GetProperty("kind").GetString());
        Assert.Equal("measure what the mod adds to a trader", rows[0].GetProperty("purpose").GetString());
        Assert.Equal(4, rows[0].GetProperty("result").GetProperty("templates").GetInt32());
    }

    [Fact]
    public async Task A_run_is_read_by_its_id_and_states_its_target()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        var seed = await SeedAsync(scope);

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ archiveRun(id: "{{seed.ModRun}}") { kind status sptVersion purpose targetModGuid targetVersion baselineRelease } }""");

        var run = response.GetProperty("data").GetProperty("archiveRun");
        Assert.Equal(ArchiveRunKind.Mod, run.GetProperty("kind").GetString());
        Assert.Equal(ArchiveRun.Succeeded, run.GetProperty("status").GetString());
        Assert.Equal(Release, run.GetProperty("sptVersion").GetString());
        Assert.Equal(ModGuid, run.GetProperty("targetModGuid").GetString());
        Assert.Equal("1.0.0", run.GetProperty("targetVersion").GetString());
        Assert.Equal(Release, run.GetProperty("baselineRelease").GetString());
    }

    [Fact]
    public async Task A_json_column_of_a_mod_row_offers_comparison_only()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        // A text operation on jsonb becomes LIKE, which Postgres refuses, so the schema must not announce it.
        var refused = await ApiHarness.JsonAsync(
            scope,
            """{ modDatasets(where: { tableHashes: { startsWith: "{" } }) { nodes { id } } }""");
        var message = refused.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains("startsWith", message);
        Assert.Contains("does not exist", message);

        // Comparison compares the content of the documents, not their text.
        var matched = await ApiHarness.JsonAsync(
            scope,
            """{ modDatasets(where: { tableHashes: { eq: "{\"items\":3}" } }) { nodes { modGuid } } }""");
        Assert.Equal(ModGuid, ApiHarness.Nodes(matched, "modDatasets")[0].GetProperty("modGuid").GetString());
    }
}
