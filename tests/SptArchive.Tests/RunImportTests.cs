using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Canonical;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// Reading a run directory into the archive (TD-29, ADR-0013, ADR-0029). The trees are built by the
/// test, so the case states the contribution it expects and nothing else has to be read.
/// </summary>
public sealed class RunImportTests : IDisposable
{
    private const string Release = "999.1.0";
    private const string OtherRelease = "999.2.0";

    private const string NodeId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ItemId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string QuestId = "cccccccccccccccccccccccc";
    private const string AddedQuestId = "eeeeeeeeeeeeeeeeeeeeeeee";
    private const string TraderId = "579dc571d53a0658a154fbec";

    private const string TargetGuid = "com.example.thermal";
    private const string DependencyGuid = "com.example.commonlib";
    private const string TargetDirectory = "Thermal";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-run-" + Guid.NewGuid().ToString("N"));

    private string Baseline => Path.Combine(_root, "baseline");

    private string Run => Path.Combine(_root, "run");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_run_becomes_a_dataset_an_observation_and_the_rows_of_its_contribution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteBaseline();
        WriteRun();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await EnsureLanguageAsync(scope, "en", cancellationToken);

        var importer = Importer();
        var report = await ImportAsync(importer, scope);

        Assert.True(report.IsNewDataset);

        var dataset = await scope.Db.ModDatasets.SingleAsync(dataset => dataset.ModGuid == TargetGuid, cancellationToken);
        Assert.Equal(TargetGuid, dataset.ModGuid);
        Assert.Equal(report.ContentHash, dataset.ContentHash);
        Assert.Equal(ModConfigOrigin.Shipped, dataset.ConfigOrigin);
        Assert.Contains("templates/items.json", dataset.TableHashes);
        Assert.Equal(2, JsonDocument.Parse(dataset.RowCounts).RootElement.GetProperty("item").GetInt32());

        // The observation states the release and the pinned dependency set (ADR-0013).
        var datasetIds = await TestDatasetsAsync(scope, cancellationToken);
        var observation = await scope.Db.ModDataObservations.SingleAsync(row => datasetIds.Contains(row.DatasetId!.Value), cancellationToken);
        Assert.Equal((Release, ModObservationKind.Archive, ModObservationOutcome.Archived), (observation.SptVersion, observation.Kind, observation.Outcome));
        Assert.Equal(dataset.Id, observation.DatasetId);
        Assert.Contains(DependencyGuid, observation.DependencySet);
        Assert.DoesNotContain(RunDirectory.ToolModGuid, observation.DependencySet);
        Assert.False(string.IsNullOrWhiteSpace(observation.DependencySetHash));

        // The mod row states the version the run named, not the dataset (ADR-0013).
        var mod = await scope.Db.Mods.SingleAsync(row => row.ModGuid == TargetGuid, cancellationToken);
        Assert.Equal((TargetGuid, "3.4.0", ">=4.1.3 <4.2.0"), (mod.ModGuid, mod.Version, mod.SptVersionRange));

        var templates = await scope.Db.ModTemplates
            .Where(row => datasetIds.Contains(row.DatasetId))
            .OrderBy(row => row.TemplateId)
            .ToListAsync(cancellationToken);
        Assert.Equal(4, templates.Count);

        // A template the run made is keyed by its address; an official one keeps its own id (ADR-0034).
        var addedAddress = ContentUlid.Create("item", "Item D");
        var addedQuestAddress = ContentUlid.Create("quest", "Q2");

        Assert.Equal(ModTemplateOperation.Added, templates.Single(row => row.TemplateId == addedAddress).Op);
        Assert.Equal(NodeId, templates.Single(row => row.TemplateId == addedAddress).ParentId);
        Assert.Equal(ModTemplateOperation.Modified, templates.Single(row => row.TemplateId == ItemId).Op);
        Assert.Equal(ModTemplateOperation.Added, templates.Single(row => row.TemplateId == addedQuestAddress).Op);

        // The assort of a trader is not the trader: the offer the run added is a template of its own,
        // named by what it sells and what the player pays with (ADR-0032, decision 4).
        var offer = templates.Single(row => row.Kind == ModTemplateKind.Assort);
        Assert.Equal((ModTemplateOperation.Added, TraderId), (offer.Op, offer.ParentId));
        Assert.Equal(
            ContentUlid.Create(ModTemplateKind.Assort, TraderId, addedAddress, "5448bd6b4bdc2dfc2f8b4569"),
            offer.TemplateId);
        Assert.DoesNotContain(templates, row => row.Kind == ModTemplateKind.Trader);

        // The id the run gave is kept as a lookup, and never as the name of a row (ADR-0034).
        var map = await scope.Db.ModIdMaps
            .Where(row => datasetIds.Contains(row.DatasetId))
            .ToListAsync(cancellationToken);
        Assert.Contains(map, row => row.RawId == "dddddddddddddddddddddddd" && row.Address == addedAddress);
        Assert.Contains(map, row => row.RawId == AddedQuestId && row.Address == addedQuestAddress);
        Assert.Contains(map, row => row.RawId == ItemId && row.Pinned);
        Assert.Contains(map, row => row.Kind == ModTemplateKind.Assort && row.RawId == "i2" && row.Address == offer.TemplateId);

        // The offer tree of a dataset is stored, one row for the offer itself (ADR-0032, decision 4).
        var assortRows = await scope.Db.ModTraderAssortItems
            .Where(row => datasetIds.Contains(row.DatasetId))
            .ToListAsync(cancellationToken);
        var node = Assert.Single(assortRows);
        Assert.Equal((TraderId, offer.TemplateId, addedAddress), (node.TraderId, node.InstanceId, node.TemplateId));
        Assert.Equal(4, node.LoyaltyLevel);

        using var barter = JsonDocument.Parse(node.BarterScheme!);
        Assert.Equal(1, barter.RootElement.GetProperty("5448bd6b4bdc2dfc2f8b4569").GetProperty("count").GetInt32());

        // A changed official item keeps its paths, and the old value is the baseline's (ADR-0017).
        var change = await scope.Db.ModChangePaths.SingleAsync(row => datasetIds.Contains(row.DatasetId), cancellationToken);
        Assert.Equal(ItemId, change.TemplateId);
        Assert.Equal(("/_props/Weight", ModChangePathOperation.Set, "1.5", "2"), (change.Path, change.Op, change.OldValue, change.NewValue));

        var added = await scope.Db.ModItems.SingleAsync(row => datasetIds.Contains(row.DatasetId), cancellationToken);
        Assert.Equal(addedAddress, added.Id);
        Assert.Equal(3d, added.Weight);
        Assert.Equal(dataset.Id, added.DatasetId);

        var quest = await scope.Db.ModQuests.SingleAsync(row => datasetIds.Contains(row.DatasetId), cancellationToken);
        Assert.Equal(addedQuestAddress, quest.Id);
        Assert.Equal("Q2", quest.QuestName);

        var locale = await scope.Db.ModLocaleEntries.SingleAsync(row => datasetIds.Contains(row.DatasetId), cancellationToken);
        Assert.Equal(("global", "en", "dddddddddddddddddddddddd Name", "Item D"), (locale.Scope, locale.LanguageCode, locale.Key, locale.Value));

        // The files a dataset shipped, with the text of its configuration and the hash before boot (ADR-0029).
        var files = await scope.Db.ModFiles
            .Where(file => datasetIds.Contains(file.DatasetId))
            .OrderBy(file => file.Path)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.Equal(dataset.Id, file.DatasetId));

        var config = files.Single(file => file.Kind == ModFileKind.Config);
        Assert.Equal($"{TargetDirectory}/config/config.json", config.Path);
        Assert.Contains("\"weightFraction\"", config.ContentText);
        Assert.Equal(config.Sha256, config.ShippedSha256);

        // The official tables stay untouched: the Api must never mix a mod row into an official one (ADR-0003).
        Assert.Empty(await scope.Db.Items.Where(item => item.SptVersion == Release).ToListAsync(cancellationToken));
        Assert.Empty(await scope.Db.Quests.Where(quest => quest.SptVersion == Release).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_run_that_changes_no_table_still_stores_its_dataset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteBaseline();
        WriteRun(items: BaselineItems, quests: BaselineQuests, traderAssort: BaselineAssort, locales: BaselineLocales);

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await EnsureLanguageAsync(scope, "en", cancellationToken);

        var report = await ImportAsync(Importer(), scope);

        Assert.True(report.IsNewDataset);

        var datasetIds = await TestDatasetsAsync(scope, cancellationToken);
        var dataset = await scope.Db.ModDatasets.SingleAsync(dataset => dataset.ModGuid == TargetGuid, cancellationToken);
        Assert.Equal(0, JsonDocument.Parse(dataset.RowCounts).RootElement.GetProperty("locales").GetInt32());
        Assert.Empty(await scope.Db.ModTemplates.Where(row => datasetIds.Contains(row.DatasetId)).ToListAsync(cancellationToken));
        Assert.Empty(await scope.Db.ModItems.Where(row => datasetIds.Contains(row.DatasetId)).ToListAsync(cancellationToken));

        // The configuration and the manifest are what says a behaviour mod is not an empty run (ADR-0028).
        Assert.Equal(2, await scope.Db.ModFiles.CountAsync(file => datasetIds.Contains(file.DatasetId), cancellationToken));

        var observation = await scope.Db.ModDataObservations.SingleAsync(row => datasetIds.Contains(row.DatasetId!.Value), cancellationToken);
        Assert.Equal(ModObservationOutcome.Archived, observation.Outcome);
        Assert.Equal(dataset.Id, observation.DatasetId);
    }

    [Fact]
    public async Task The_same_run_imported_twice_is_written_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteBaseline();
        WriteRun();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await EnsureLanguageAsync(scope, "en", cancellationToken);

        var importer = Importer();
        await ImportAsync(importer, scope);

        var plan = importer.Prepare();
        Assert.True(await importer.IsCurrentAsync(scope.Db, plan, cancellationToken));

        var second = await ImportAsync(importer, scope);
        Assert.False(second.IsNewDataset);

        var datasetIds = await TestDatasetsAsync(scope, cancellationToken);
        Assert.Single(datasetIds);
        Assert.Equal(1, await scope.Db.ModDataObservations.CountAsync(row => datasetIds.Contains(row.DatasetId!.Value), cancellationToken));
        Assert.Equal(1, await scope.Db.ModItems.CountAsync(row => datasetIds.Contains(row.DatasetId), cancellationToken));
        Assert.Equal(2, await scope.Db.ModFiles.CountAsync(file => datasetIds.Contains(file.DatasetId), cancellationToken));
    }

    [Fact]
    public async Task A_curated_configuration_is_stated_on_the_dataset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteBaseline();
        WriteRun(origin: """{"configOrigin":"curated","reason":"the overlay is the feature"}""");

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await EnsureLanguageAsync(scope, "en", cancellationToken);

        await ImportAsync(Importer(), scope);

        var dataset = await scope.Db.ModDatasets.SingleAsync(dataset => dataset.ModGuid == TargetGuid, cancellationToken);
        Assert.Equal(ModConfigOrigin.Curated, dataset.ConfigOrigin);
        Assert.Equal("the overlay is the feature", dataset.ConfigOriginReason);
    }

    [Fact]
    public void A_run_that_states_no_release_is_refused()
    {
        WriteBaseline();
        WriteRun(release: null);

        var error = Assert.Throws<InvalidOperationException>(() => Importer().Prepare());

        Assert.Contains("states no release", error.Message);
        Assert.Contains("1.3.0", error.Message);
    }

    [Fact]
    public void A_baseline_of_another_release_is_refused()
    {
        WriteBaseline(release: OtherRelease);
        WriteRun();

        var error = Assert.Throws<InvalidOperationException>(() => Importer().Prepare());

        Assert.Contains(OtherRelease, error.Message);
        Assert.Contains("not the work of a mod", error.Message);
    }

    [Fact]
    public void The_exporter_is_never_the_target()
    {
        WriteBaseline();
        WriteRun();

        var error = Assert.Throws<InvalidOperationException>(
            () => Importer(modGuid: RunDirectory.ToolModGuid).Prepare());

        Assert.Contains("tool of the run", error.Message);
    }

    [Fact]
    public async Task A_target_that_another_mod_depends_on_is_still_the_target_of_this_run()
    {
        // The run states the mods it loaded, not which one it was made for, so the archive cannot prove
        // the choice: it warns and archives the mod that was named (TD-44).
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteBaseline();
        WriteRun();

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await EnsureLanguageAsync(scope, "en", cancellationToken);

        var importer = Importer(modGuid: DependencyGuid);
        var plan = importer.Prepare();

        var report = await importer.ApplyAsync(scope.Db, plan, Guid.CreateVersion7(), cancellationToken);

        var dataset = await scope.Db.ModDatasets.SingleAsync(row => row.ModGuid == DependencyGuid, cancellationToken);
        Assert.Equal(DependencyGuid, dataset.ModGuid);
        Assert.Equal(dataset.Id, report.DatasetId);
    }

    /// <summary>The datasets of this test's mods. The development database also holds real imports.</summary>
    private static async Task<Guid[]> TestDatasetsAsync(TestScope scope, CancellationToken cancellationToken) =>
        await scope.Db.ModDatasets
            .Where(dataset => dataset.ModGuid == TargetGuid || dataset.ModGuid == DependencyGuid)
            .Select(dataset => dataset.Id)
            .ToArrayAsync(cancellationToken);

    private static async Task EnsureLanguageAsync(TestScope scope, string code, CancellationToken cancellationToken)
    {
        if (!await scope.Db.Languages.AnyAsync(language => language.Code == code, cancellationToken))
        {
            scope.Db.Languages.Add(new Language { Code = code, Name = "English" });
            await scope.Db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<RunImportReport> ImportAsync(RunImporter importer, TestScope scope)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var plan = importer.Prepare();

        var archiveRun = new ArchiveRun { SourceHash = plan.ContentHash, SptVersion = plan.Release };
        scope.Db.ArchiveRuns.Add(archiveRun);
        await scope.Db.SaveChangesAsync(cancellationToken);

        return await importer.ApplyAsync(scope.Db, plan, archiveRun.Id, cancellationToken);
    }

    private RunImporter Importer(string modGuid = TargetGuid) =>
        new(
            Options.Create(new ArchiveOptions { RunPath = Run, RunModGuid = modGuid, BaselinePath = Baseline }),
            NullLogger<RunImporter>.Instance);

    private void WriteBaseline(string? release = Release)
    {
        Write(Baseline, BaselineTree);
        WriteRunAccount(Baseline, release, targetManifest: false);
    }

    private void WriteRun(
        string? release = Release,
        string? items = null,
        string? quests = null,
        string? traderAssort = null,
        string? locales = null,
        string? origin = null)
    {
        Write(Run, BaselineTree);
        Write(Run, new Dictionary<string, string>
        {
            ["templates/items.json"] = items ?? RunItems,
            ["templates/quests.json"] = quests ?? RunQuests,
            ["traders/" + TraderId + "/assort.json"] = traderAssort ?? RunAssort,
            ["locales/global/en.json"] = locales ?? RunLocales,
        });

        WriteRunAccount(Run, release, targetManifest: true, origin: origin);
    }

    /// <summary>The run account: which mods loaded, on which release, and the manifest of each (ADR-0029).</summary>
    private static void WriteRunAccount(string root, string? release, bool targetManifest, string? origin = null)
    {
        var mods = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                directory = "SptArchive.Exporter",
                modGuid = RunDirectory.ToolModGuid,
                name = "SPT Data Archive Exporter",
                author = "SptArchive",
                version = "1.4.0",
                sptVersion = ">=4.1.0 <5.0.0",
            }),
        };

        if (targetManifest)
        {
            mods.Add(JsonSerializer.Serialize(new
            {
                directory = TargetDirectory,
                modGuid = TargetGuid,
                name = "Thermal",
                author = "LennoxP90",
                version = "3.4.0",
                sptVersion = ">=4.1.3 <4.2.0",
                modDependencies = new Dictionary<string, string> { [DependencyGuid] = "^3.0.0" },
            }));

            mods.Add(JsonSerializer.Serialize(new
            {
                directory = "CommonLib",
                modGuid = DependencyGuid,
                name = "Common Lib",
                author = "WTT",
                version = "3.0.6",
                sptVersion = ">=4.0.0 <5.0.0",
            }));
        }

        var files = new Dictionary<string, string>
        {
            ["run/loaded.json"] = "[" + string.Join(",", mods) + "]",
        };

        if (release is not null)
        {
            files["run/release.json"] = $$"""{"sptVersion":"{{release}}","informationalVersion":"{{release}}-RELEASE"}""";
        }

        if (origin is not null)
        {
            files["run/origin.json"] = origin;
        }

        if (targetManifest)
        {
            var manifest = new[]
            {
                new { path = "SptArchive.Thermal.dll", size = 10, sha256 = new string('a', 64), kind = "assembly" },
                new { path = "config/config.json", size = 27, sha256 = new string('b', 64), kind = "config" },
            };

            files[$"run/files/{TargetDirectory}.json"] = JsonSerializer.Serialize(manifest);
            files["run/files/CommonLib.json"] = JsonSerializer.Serialize(
                new[] { new { path = "WTT-ServerCommonLib.dll", size = 20, sha256 = new string('c', 64), kind = "assembly" } });
            files[$"run/config/{TargetDirectory}/config/config.json"] = """{"weightFraction":0.6,"deny":[]}""";

            // The file as it was before the server started: the same bytes, so the mod did not rewrite it.
            files["run/pre-boot.json"] = JsonSerializer.Serialize(
                new[] { new { path = $"{TargetDirectory}/config/config.json", size = 27, sha256 = new string('b', 64) } });
        }

        Write(root, files);
    }

    private static void Write(string root, Dictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static readonly Dictionary<string, string> BaselineTree = new()
    {
        ["templates/items.json"] = BaselineItems,
        ["templates/quests.json"] = BaselineQuests,
        ["templates/customization.json"] = "{}",
        ["locales/global/en.json"] = BaselineLocales,
        ["traders/" + TraderId + "/base.json"] = """
            {"_id":"579dc571d53a0658a154fbec","name":"Peacekeeper","nextResupply":1792518932,"currency":"USD"}
            """,
        ["traders/" + TraderId + "/assort.json"] = BaselineAssort,
    };

    private static string BaselineItems =>
        "{\"aaaaaaaaaaaaaaaaaaaaaaaa\":{\"_id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_name\":\"Node A\",\"_parent\":\"\",\"_type\":\"Node\",\"_props\":{}},"
        + "\"bbbbbbbbbbbbbbbbbbbbbbbb\":{\"_id\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"_name\":\"Item B\",\"_parent\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_type\":\"Item\",\"_props\":{\"Weight\":1.5}}}";

    private static string BaselineQuests =>
        "{\"cccccccccccccccccccccccc\":{\"_id\":\"cccccccccccccccccccccccc\",\"QuestName\":\"Q1\",\"traderId\":\"579dc571d53a0658a154fbec\",\"side\":\"Pmc\"}}";

    private static string BaselineLocales => """{"bbbbbbbbbbbbbbbbbbbbbbbb Name":"Item B"}""";

    private static string BaselineAssort =>
        "{\"items\":[{\"_id\":\"i1\",\"_tpl\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"parentId\":\"hideout\"}],"
        + "\"barter_scheme\":{\"i1\":[[{\"count\":1,\"_tpl\":\"5448bd6b4bdc2dfc2f8b4569\"}]]},\"loyal_level_items\":{\"i1\":2}}";

    /// <summary>The run: one item changed, one added, one quest added, one locale added, one offer more.</summary>
    private static string RunItems =>
        "{\"aaaaaaaaaaaaaaaaaaaaaaaa\":{\"_id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_name\":\"Node A\",\"_parent\":\"\",\"_type\":\"Node\",\"_props\":{}},"
        + "\"bbbbbbbbbbbbbbbbbbbbbbbb\":{\"_id\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"_name\":\"Item B\",\"_parent\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_type\":\"Item\",\"_props\":{\"Weight\":2}},"
        + "\"dddddddddddddddddddddddd\":{\"_id\":\"dddddddddddddddddddddddd\",\"_name\":\"Item D\",\"_parent\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_type\":\"Item\",\"_props\":{\"Weight\":3}}}";

    private static string RunQuests =>
        "{\"cccccccccccccccccccccccc\":{\"_id\":\"cccccccccccccccccccccccc\",\"QuestName\":\"Q1\",\"traderId\":\"579dc571d53a0658a154fbec\",\"side\":\"Pmc\"},"
        + "\"eeeeeeeeeeeeeeeeeeeeeeee\":{\"_id\":\"eeeeeeeeeeeeeeeeeeeeeeee\",\"QuestName\":\"Q2\",\"traderId\":\"579dc571d53a0658a154fbec\",\"side\":\"Pmc\"}}";

    private static string RunLocales => """{"bbbbbbbbbbbbbbbbbbbbbbbb Name":"Item B","dddddddddddddddddddddddd Name":"Item D"}""";

    private static string RunAssort =>
        "{\"items\":[{\"_id\":\"i1\",\"_tpl\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"parentId\":\"hideout\"},"
        + "{\"_id\":\"i2\",\"_tpl\":\"dddddddddddddddddddddddd\",\"parentId\":\"hideout\"}],"
        + "\"barter_scheme\":{\"i1\":[[{\"count\":1,\"_tpl\":\"5448bd6b4bdc2dfc2f8b4569\"}]],\"i2\":[[{\"count\":1,\"_tpl\":\"5448bd6b4bdc2dfc2f8b4569\"}]]},"
        + "\"loyal_level_items\":{\"i1\":2,\"i2\":4}}";
}
