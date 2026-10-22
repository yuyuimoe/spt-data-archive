using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reqnroll;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Factions;
using static SptArchive.Tests.Specs.OfficialWorld;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/bot-and-faction-data.feature</c>. A scenario that is about a run owns a
/// database (TD-81), because the Archiver commits: the baseline is the official import of a source that the
/// steps write, and the run is a copy of that source that changes one file, imported against the baseline
/// (ADR-0013). A scenario that is about a faction reads the curated seed and the resolver, because ADR-0025,
/// decision 7, states that the seed lives in code with a test until a table holds it.
/// </summary>
[Binding]
[Scope(Feature = "Archive bots, maps and server mechanics")]
internal sealed class BotDataSteps(OfficialWorld world)
{
    private const string ModGuid = "com.example.mod";

    private readonly List<Action<string>> _edits = [];

    private string[] _maps = [];
    private string _changedMap = "";
    private string _botType = "";
    private IReadOnlyDictionary<string, BotTypeSeedRow> _seedRows = BotFactions.Seed;
    private FactionMembership _membership;
    private bool _baselineImported;
    private int _runCounter;

    [AfterScenario]
    public async Task DropDatabase() => await world.DisposeAsync();

    // ---- Background ------------------------------------------------------------------------------

    [Given("a migrated archive database")]
    public async Task GivenAMigratedArchiveDatabase()
    {
        world.WriteDefaultSource();
        await world.CreateDatabaseAsync();
    }

    [Given("a baseline export for the release {string}")]
    public void GivenABaselineExportForTheRelease(string release)
    {
        world.UseRelease(release);
        Assert.Equal("4.1.6", world.Release);
    }

    // ---- A change to a map (ADR-0041) ------------------------------------------------------------

    [Given("the baseline export holds the maps {string}, {string} and {string}")]
    public void GivenTheBaselineExportHoldsTheMaps(string first, string second, string third)
    {
        _maps = [first, second, third];

        // The list of the maps, and two files of each map: the base and the loose loot (ADR-0024).
        world.Write("locations/base.json", """{"locations":{},"paths":[]}""");

        foreach (var map in _maps)
        {
            world.Write($"locations/{map}/base.json", MapBase(map));
            world.Write($"locations/{map}/looseLoot.json", """{"spawnpointCount":{"mean":0,"std":0},"spawnpoints":[]}""");
        }
    }

    [Given("a mod dataset whose export changes only the map {string}")]
    public void GivenAModDatasetWhoseExportChangesOnlyTheMap(string map)
    {
        Assert.Contains(map, _maps);
        _changedMap = map;

        _edits.Add(root => EditJson(root, $"locations/{map}/base.json", file => file["Insurance"] = false));
    }

    [When("the Archiver imports the dataset")]
    public async Task WhenTheArchiverImportsTheDataset()
    {
        await EnsureBaselineAsync();
        var run = await ArchiveAsync(_edits);
        Assert.False(run.Failed, "The import failed: " + run.Failure);
    }

    [Then("the table {string} has one row of the kind {string} for the map {string} with the operation {string}")]
    public async Task ThenTheTableHasOneRowOfTheKindForTheMap(string table, string kind, string map, string operation)
    {
        Assert.Equal("mod_templates", table);
        Assert.Equal("location", kind);

        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.Kind == kind).ToListAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);

        // A location that the baseline holds keeps the id of the baseline, which is the name of its map
        // (ADR-0034, ADR-0041).
        Assert.Equal((map, operation), (row.TemplateId, row.Op));
    }

    [Then("no other map has a row of the kind {string} for that dataset")]
    public async Task ThenNoOtherMapHasARowOfTheKind(string kind)
    {
        await using var db = world.Open();
        var ids = await db.ModTemplates
            .Where(row => row.Kind == kind)
            .Select(row => row.TemplateId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_changedMap], ids);

        // The other maps of the baseline exist and have no row at all.
        Assert.Equal(2, _maps.Except(ids).Count());
        Assert.All(_maps.Except([_changedMap]), map => Assert.DoesNotContain(map, ids));
    }

    // ---- The faction of a bot type (ADR-0025) ----------------------------------------------------

    [Given("no curated row puts the bot type {string} in a faction")]
    public void GivenNoCuratedRowPutsTheBotTypeInAFaction(string botType)
    {
        _botType = botType;
        Assert.DoesNotContain(botType.ToLowerInvariant(), _seedRows.Keys);
    }

    [When("I read the bot type {string}")]
    public void WhenIReadTheBotType(string botType) => _membership = BotFactions.Resolve(botType);

    [Then("the faction is null")]
    public void ThenTheFactionIsNull()
    {
        Assert.Null(_membership.FactionId);

        // No trusted prefix matched either, so the archive says that it does not know the faction.
        Assert.Equal(FactionEvidence.Unknown, _membership.Evidence);
    }

    [Then("the answer names the bot type instead of returning an empty list")]
    public void ThenTheAnswerNamesTheBotType() => Assert.Equal(_botType.ToLowerInvariant(), _membership.BotTypeId);

    [When("I list the curated faction rows of the release {string}")]
    public void WhenIListTheCuratedFactionRowsOfTheRelease(string release)
    {
        Assert.Equal(world.Release, release);
        _seedRows = BotFactions.Seed;
    }

    [Then("every row states its evidence")]
    public void ThenEveryRowStatesItsEvidence()
    {
        Assert.NotEmpty(_seedRows);
        Assert.All(_seedRows, row => Assert.False(string.IsNullOrWhiteSpace(row.Value.Evidence), $"The row of '{row.Key}' states no evidence."));
    }

    [Then("the count is small enough for one person to read in one sitting")]
    public void ThenTheCountIsSmallEnoughForOnePersonToReadInOneSitting()
    {
        // The seed holds one row for each bot type file of 4.1.6 and nothing else (FactionTests).
        Assert.Equal(57, _seedRows.Count);
        Assert.True(_seedRows.Count <= 100, "The curated map holds " + _seedRows.Count + " rows.");
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>The baseline is the official import of the source, as the Archiver makes it (ADR-0035).</summary>
    private async Task EnsureBaselineAsync()
    {
        if (_baselineImported)
        {
            return;
        }

        _baselineImported = true;
        await world.RunAsync();
        Assert.False(world.Failed, "The official import of the baseline failed: " + world.Failure);
    }

    /// <summary>A copy of the source that "loaded" the mod and holds one edit, imported against the source.</summary>
    private async Task<ModRun> ArchiveAsync(IEnumerable<Action<string>> edits)
    {
        var root = Path.Combine(world.Root, "run-" + ++_runCounter);

        CopyDirectory(world.Source, root);

        foreach (var edit in edits)
        {
            edit(root);
        }

        var tool = Mod(ExporterGuid, "1.5.0");
        WriteIn(root, "run/loaded.json", JsonSerializer.Serialize(new[] { tool, Mod(ModGuid, "1.0.0") }));
        WriteIn(root, $"run/files/{ModGuid[(ModGuid.LastIndexOf('.') + 1)..]}.json", JsonSerializer.Serialize(new[]
        {
            new { path = "Mod.dll", size = 10, sha256 = new string('a', 64), kind = "assembly" },
        }));

        var run = new ModRun(new LogSink());
        var options = new ArchiveOptions
        {
            RunPath = root,
            BaselinePath = world.Source,
            RunModGuid = ModGuid,
            ApplyMigrations = false,
        };

        using (ReadOnlySource.Hold(root, world.Source))
        {
            await ArchiverHarness.RunAsync(
                world.ConnectionString,
                options,
                [],
                world.Cache,
                run.Log.For<ArchiveJob>(),
                TestContext.Current.CancellationToken,
                run.Log.For<RunImporter>());
        }

        if (run.Failed)
        {
            Environment.ExitCode = 0; // The job sets it when a run fails, and a failure is what some scenarios want.
        }

        return run;
    }

    private static string MapBase(string map) => new JsonObject
    {
        ["_Id"] = map,
        ["Name"] = map,
        ["Insurance"] = true,
        ["EscapeTimeLimit"] = 40,
    }.ToJsonString();

    private static void EditJson(string root, string path, Action<JsonNode> edit)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        var node = JsonNode.Parse(File.ReadAllText(full))!;

        edit(node);
        File.WriteAllText(full, node.ToJsonString());
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>One import of a run: what the Archiver logged.</summary>
    private sealed record ModRun(LogSink Log)
    {
        public bool Failed => Log.Entries.Any(entry => entry.Level == LogLevel.Critical);

        public Exception? Failure => Log.Entries.LastOrDefault(entry => entry.Level == LogLevel.Critical).Exception;
    }
}
