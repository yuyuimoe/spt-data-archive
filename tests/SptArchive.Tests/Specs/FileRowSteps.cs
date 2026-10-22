using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reqnroll;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Canonical;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/file-rows.feature</c> (ADR-0044). A scenario writes a baseline and a run of its
/// own, runs the real <see cref="RunImporter"/> over them, and reads the rows back from the archive. The
/// scenario writes inside one transaction that it rolls back, so the archive is never left with test data.
/// </summary>
[Binding]
public sealed class FileRowSteps : IDisposable
{
    // A release that no real archive holds, so a scenario never compares against the official data of a run
    // that another test committed.
    private const string Release = "999.5.0";
    private const string ModGuid = "com.example.filerows";

    private const string DialogueElementId = "683dafc7a7c0a468ac8a9b05";
    private const string PresetId = "5841474424597759ba49be91";

    private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _run = new(StringComparer.Ordinal);
    private readonly List<string> _addedIds = [];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-filerows-spec-" + Guid.NewGuid().ToString("N"));

    private TestScope? _scope;
    private RunImportPlan? _plan;
    private RunImportReport? _report;

    [AfterScenario]
    public async Task RollBack()
    {
        if (_scope is not null)
        {
            await _scope.DisposeAsync();
            _scope = null;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- The dialogue ---------------------------------------------------------------------------

    [Given("a baseline that holds the dialogue element {string} with one line")]
    public void GivenABaselineThatHoldsTheDialogueElement(string element)
    {
        _baseline["templates/dialogue.json"] = Dialogue(element, trigger: "hello");
    }

    [Given("a run that changes one field of that line")]
    public void GivenARunThatChangesOneFieldOfThatLine()
    {
        _run["templates/dialogue.json"] = Dialogue(DialogueElementId, trigger: "goodbye");
    }

    // ---- globals.json ---------------------------------------------------------------------------

    [Given("a baseline whose {string} holds the preset {string} with a price of {int}")]
    public void GivenABaselineWhoseFileHoldsThePreset(string file, string preset, int price) =>
        _baseline[file] = Globals(preset, price, botAngle: 10, scatter: 1);

    [Given("a run whose preset {string} holds a price of {int}")]
    public void GivenARunWhosePresetHoldsAPrice(string preset, int price) =>
        _run["globals.json"] = Globals(preset, price, botAngle: 10, scatter: 1);

    [Given("a baseline whose {string} holds the bot presets {string} {string} and {string} {string}")]
    public void GivenABaselineWhoseFileHoldsTheBotPresets(string file, string role, string difficulty, string otherRole, string otherDifficulty)
    {
        _baseline[file] = BotPresets((role, difficulty, 10), (otherRole, otherDifficulty, 10));
    }

    [Given("a run that changes one value of the preset {string} {string}")]
    public void GivenARunThatChangesOneValueOfThePreset(string role, string difficulty)
    {
        _run["globals.json"] = BotPresets((role, difficulty, 20), ("assault", "hard", 10));
    }

    // ---- settings.json --------------------------------------------------------------------------

    [Given("a baseline whose {string} holds a {string} object with the key {string} set to {int}")]
    public void GivenABaselineWhoseFileHoldsAnObjectWithTheKey(string file, string holder, string key, int value) =>
        _baseline[file] = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [holder] = new Dictionary<string, object> { [key] = value },
        });

    [Given("a run whose {string} holds the same key set to {int}")]
    public void GivenARunWhoseFileHoldsTheSameKey(string file, int value) =>
        _run[file] = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["config"] = new Dictionary<string, object> { ["time"] = value },
        });

    // ---- The three lists that a run adds to -----------------------------------------------------

    [Given("a run that adds one object to {string}, one to {string} and one string to {string}")]
    public void GivenARunThatAddsOneObjectToEach(string achievements, string storage, string character)
    {
        const string achievementId = "aaaaaaaaaaaaaaaaaaaaaaaa";
        const string storageId = "cccccccccccccccccccccccc";
        const string voice = "NewVoice";

        // The baseline holds one other entry of each file, so the run adds one and changes nothing else.
        _baseline[achievements] = """[{"id":"bbbbbbbbbbbbbbbbbbbbbbbb","index":1,"rewards":[]}]""";
        _baseline[storage] = """[{"id":"dddddddddddddddddddddddd","source":"default","type":"dogTag"}]""";
        _baseline[character] = """["Usec"]""";

        _run[achievements] = $$"""[{"id":"bbbbbbbbbbbbbbbbbbbbbbbb","index":1,"rewards":[]},{"id":"{{achievementId}}","index":2,"rewards":[]}]""";
        _run[storage] = $$"""[{"id":"dddddddddddddddddddddddd","source":"default","type":"dogTag"},{"id":"{{storageId}}","source":"default","type":"suite"}]""";
        _run[character] = $$"""["Usec","{{voice}}"]""";

        _addedIds.AddRange([achievementId, storageId, voice]);
    }

    // ---- A run that changes a file, or that leaves it alone --------------------------------------

    [Given("a run whose {string} is the same as the baseline")]
    public void GivenARunWhoseFileIsTheSameAsTheBaseline(string file)
    {
        _baseline[file] = Globals(PresetId, price: 1, botAngle: 10, scatter: 1);
        _run[file] = _baseline[file];
    }

    [Given("a run that changes {string}")]
    public void GivenARunThatChanges(string file)
    {
        if (file == "globals.json")
        {
            _baseline[file] = Globals(PresetId, price: 1, botAngle: 10, scatter: 1);
            _run[file] = Globals(PresetId, price: 2, botAngle: 10, scatter: 1);
            return;
        }

        _baseline[file] = """{"time":1}""";
        _run[file] = """{"time":2}""";
    }

    [Given("a run that changes {string} and {string}")]
    public void GivenARunThatChangesTwo(string first, string second)
    {
        GivenARunThatChanges(first);
        GivenARunThatChanges(second);
    }

    // ---- The Archiver ---------------------------------------------------------------------------

    [When("the Archiver archives the run")]
    public async Task WhenTheArchiverArchivesTheRun() => await ArchiveAsync();

    [When("the Archiver computes the content hash of the run")]
    public async Task WhenTheArchiverComputesTheContentHash() => await ArchiveAsync();

    private async Task ArchiveAsync()
    {
        WriteTrees();

        var cancellationToken = TestContext.Current.CancellationToken;
        var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        _scope = scope;

        var importer = Importer();
        _plan = importer.Prepare();

        await EnsureOfficialReleaseAsync(scope, _plan.Release, cancellationToken);

        var archiveRun = new ArchiveRun
        {
            SourceHash = _plan.ContentHash,
            SptVersion = _plan.Release,
            Kind = ArchiveRunKind.Mod,
            Status = ArchiveRun.Succeeded,
        };
        scope.Db.ArchiveRuns.Add(archiveRun);
        await scope.Db.SaveChangesAsync(cancellationToken);

        _report = await importer.ApplyAsync(scope.Db, _plan, archiveRun.Id, cancellationToken);
    }

    // ---- The rows -------------------------------------------------------------------------------

    [Then("the table {string} holds one row of the kind {string} for that element")]
    public async Task ThenTheTableHoldsOneRowOfTheKindForThatElement(string table, string kind)
    {
        Assert.Equal("mod_templates", table);
        var row = Assert.Single(await RowsAsync(table, kind));
        Assert.Equal(DialogueElementId, row);
    }

    [Then("the table {string} holds one row of the kind {string} named by the preset id")]
    public async Task ThenTheTableHoldsOneRowOfTheKindNamedByThePresetId(string table, string kind)
    {
        var row = Assert.Single(await RowsAsync(table, kind));
        Assert.Equal(PresetId, row);
    }

    [Then("the table {string} holds one row of the kind {string} named {string}")]
    public async Task ThenTheTableHoldsOneRowOfTheKindNamed(string table, string kind, string name)
    {
        var row = Assert.Single(await RowsAsync(table, kind));
        Assert.Equal(name, row);
    }

    [Then("the table {string} holds one row of the kind {string}")]
    public async Task ThenTheTableHoldsOneRowOfTheKind(string table, string kind) =>
        Assert.Single(await RowsAsync(table, kind));

    [Then("the table {string} holds no row for {string}")]
    public async Task ThenTheTableHoldsNoRowFor(string table, string name)
    {
        Assert.DoesNotContain(name, await RowsAsync(table, kind: null));
    }

    [Then("the table {string} holds one row whose path starts with {string}")]
    public async Task ThenTheTableHoldsOneRowWhosePathStartsWith(string table, string prefix)
    {
        Assert.Equal("mod_change_paths", table);
        var paths = await _scope!.Db.ModChangePaths
            .Where(row => row.DatasetId == DatasetId)
            .Select(row => row.Path)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith(prefix, Assert.Single(paths), StringComparison.Ordinal);
    }

    [Then("the table {string} holds the path {string}")]
    public async Task ThenTheTableHoldsThePath(string table, string path)
    {
        Assert.Equal("mod_change_paths", table);
        var paths = await _scope!.Db.ModChangePaths
            .Where(row => row.DatasetId == DatasetId)
            .Select(row => row.Path)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(path, paths);
    }

    [Then("the table {string} holds one row of the kind {string}, one of the kind {string} and one of the kind {string}")]
    public async Task ThenTheTableHoldsOneRowOfEachKind(string table, string first, string second, string third)
    {
        Assert.Equal("mod_database_objects", table);

        var kinds = await _scope!.Db.ModDatabaseObjects
            .Where(row => row.DatasetId == DatasetId)
            .Select(row => row.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { first, third, second }.OrderBy(kind => kind, StringComparer.Ordinal), kinds.OrderBy(kind => kind, StringComparer.Ordinal));
    }

    [Then("each row holds the object as the run wrote it")]
    public async Task ThenEachRowHoldsTheObjectAsTheRunWroteIt()
    {
        var rows = await _scope!.Db.ModDatabaseObjects
            .Where(row => row.DatasetId == DatasetId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_addedIds.Count, rows.Count);
        foreach (var id in _addedIds)
        {
            Assert.Contains(rows, row => row.Data.Contains(id, StringComparison.Ordinal));
        }
    }

    [Then("the dataset holds no row of the kinds {string}, {string}, {string} or {string}")]
    public async Task ThenTheDatasetHoldsNoRowOfTheKinds(string first, string second, string third, string fourth)
    {
        var kinds = new[] { first, second, third, fourth };
        var templates = await _scope!.Db.ModTemplates
            .Where(row => row.DatasetId == DatasetId && kinds.Contains(row.Kind))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(templates);
    }

    [Then("the files with no row are exactly {string}")]
    public void ThenTheFilesWithNoRowAreExactly(string path) =>
        Assert.Equal([path], _plan!.Contribution.FilesWithoutRows);

    // ---- The hash -------------------------------------------------------------------------------

    [Then("the hash is the hash that the Archiver computed before {string} held rows")]
    public void ThenTheHashIsTheHashBeforeTheFileHeldRows(string file)
    {
        // The rows of an object are an addition to the dataset, never an input of its content hash
        // (ADR-0041, decision 5; ADR-0044, decision 6).
        var files = _plan!.Files.Select(row => new ContributionFile(
            row.Path,
            row.Sha256,
            row.ContentText is null ? null : DatasetHash.HashText(row.ContentText)));

        Assert.Equal(DatasetHash.Compute(_plan.Contribution with { Objects = [] }, files), _report!.ContentHash);
        Assert.Contains(file, _plan.Contribution.UnstoredChanges.Keys);
    }

    [Then("the canonical hash of {string} is in the table hashes of the dataset")]
    public async Task ThenTheCanonicalHashIsInTheTableHashes(string file)
    {
        var dataset = await _scope!.Db.ModDatasets.SingleAsync(row => row.Id == DatasetId, TestContext.Current.CancellationToken);
        var hashes = JsonDocument.Parse(dataset.TableHashes).RootElement;

        Assert.True(hashes.TryGetProperty(file, out var stored), dataset.TableHashes);
        Assert.Equal(_plan!.Contribution.UnstoredChanges[file], stored.GetString());
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private Guid DatasetId => _report!.DatasetId;

    /// <summary>The name of every row of one kind, from the table that the scenario names.</summary>
    private async Task<List<string>> RowsAsync(string table, string? kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = _scope!.Db;

        return table switch
        {
            "mod_templates" => await db.ModTemplates
                .Where(row => row.DatasetId == DatasetId && (kind == null || row.Kind == kind))
                .Select(row => row.TemplateId)
                .ToListAsync(cancellationToken),
            "mod_database_objects" => await db.ModDatabaseObjects
                .Where(row => row.DatasetId == DatasetId && (kind == null || row.Kind == kind))
                .Select(row => row.TemplateId)
                .ToListAsync(cancellationToken),
            "mod_change_paths" => await db.ModChangePaths
                .Where(row => row.DatasetId == DatasetId && (kind == null || row.Kind == kind))
                .Select(row => row.Path)
                .ToListAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "The scenario names another table."),
        };
    }

    private RunImporter Importer() =>
        new(
            Options.Create(new ArchiveOptions
            {
                RunPath = RunTree.Run(_root),
                RunModGuid = ModGuid,
                BaselinePath = RunTree.Baseline(_root),
            }),
            NullLogger<RunImporter>.Instance);

    private void WriteTrees()
    {
        RunTree.Write(_root, ModGuid, Release, Release);

        foreach (var (path, content) in _baseline)
        {
            RunTree.File(RunTree.Baseline(_root), path, content);
        }

        foreach (var (path, content) in _run)
        {
            RunTree.File(RunTree.Run(_root), path, content);
        }
    }

    /// <summary>The archive holds the official data of a release before it holds what a mod did to it (ADR-0035).</summary>
    private static async Task EnsureOfficialReleaseAsync(TestScope scope, string release, CancellationToken cancellationToken)
    {
        await SptVersionRegistry.EnsureAsync(scope.Db, release, cancellationToken);

        if (await scope.Db.Items.AnyAsync(item => item.SptVersion == release, cancellationToken))
        {
            return;
        }

        scope.Db.Items.Add(new Item
        {
            Id = "ffffffffffffffffffffffff",
            Name = "Official Item",
            ParentId = "root",
            Type = "Item",
            Props = "{}",
            SptVersion = release,
        });

        await scope.Db.SaveChangesAsync(cancellationToken);
    }

    // ---- The documents that the scenarios write --------------------------------------------------

    private static string Dialogue(string element, string trigger) =>
        JsonSerializer.Serialize(new
        {
            elements = new[]
            {
                new
                {
                    Id = element,
                    Trader = "t",
                    Lines = new[] { new { Id = "l1", Trigger = trigger, Actions = Array.Empty<object>() } },
                },
            },
        });

    private static string Globals(string preset, int price, int botAngle, int scatter) =>
        JsonSerializer.Serialize(new
        {
            config = new { },
            LocationInfection = new { },
            bot_presets = Array.Empty<object>(),
            BotWeaponScatterings = new[] { new { Name = "Default", Scatter = scatter } },
            ItemPresets = new Dictionary<string, object> { [preset] = new { price } },
        });

    private static string BotPresets((string Role, string Difficulty, int Angle) first, (string Role, string Difficulty, int Angle) second) =>
        JsonSerializer.Serialize(new
        {
            config = new { },
            LocationInfection = new { },
            bot_presets = new[]
            {
                new { Role = first.Role, BotDifficulty = first.Difficulty, VisibleAngle = first.Angle },
                new { Role = second.Role, BotDifficulty = second.Difficulty, VisibleAngle = second.Angle },
            },
            BotWeaponScatterings = Array.Empty<object>(),
            ItemPresets = new { },
        });
}
