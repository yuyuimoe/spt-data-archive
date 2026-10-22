using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Reqnroll;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Canonical;
using SptArchive.Core.Entities;
using static SptArchive.Tests.Specs.OfficialWorld;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/mod-registry.feature</c>. A scenario that is about the schema writes a row into the
/// database of the scenario and reads the error back. Every other scenario runs the real
/// <see cref="ArchiveJob"/>: the baseline is a real official import of the default source (ADR-0035), a run is a
/// copy of that source that the scenario changes and that "loaded" one mod, and the import reads the run against
/// the baseline (ADR-0013). Each scenario owns a database (TD-81), because the Archiver commits.
/// </summary>
[Binding]
[Scope(Feature = "Keep mod data apart from official data")]
internal sealed class ModRegistrySteps(OfficialWorld world)
{
    private const string DefaultModGuid = "com.example.mod";

    private const string Weight = "Weight";

    private readonly List<Action<string>> _edits = [];
    private readonly Dictionary<string, string> _official = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _added = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _datasets = new(StringComparer.Ordinal);
    private readonly List<ModRun> _runs = [];

    private string _modGuid = DefaultModGuid;
    private string _version = "1.0.0";
    private bool _baselineImported;
    private int _runCounter;
    private PostgresException? _error;
    private long _officialRowsBefore;
    private string _otherModGuid = "";
    private string _bandRaw = "";

    private ModRun Last => _runs[^1];

    [AfterScenario]
    public async Task DropDatabase() => await world.DisposeAsync();

    // ---- Background ----------------------------------------------------------------------------

    // The baseline of every run is the default source. It is imported as official data the first time a step
    // needs a run, so a Given may still change it (see EnsureBaselineAsync).
    [Given("a migrated archive database")]
    public async Task GivenAMigratedArchiveDatabase()
    {
        world.WriteDefaultSource();
        await world.CreateDatabaseAsync();
    }

    // ---- Schema guarantees ---------------------------------------------------------------------

    [When("I insert a mod item with a dataset id that is not in the table {string}")]
    public async Task WhenIInsertAModItem(string table)
    {
        Assert.Equal("mod_datasets", table);

        _error = await Capture(
            "insert into mod_items (dataset_id, id, name, parent_id, type, props) values (@d, @i, 'Mod item', 'node', 'Item', '{}')",
            ("d", Guid.CreateVersion7()),
            ("i", Id("orphan")));
    }

    [Then("the database rejects the row with a foreign key error")]
    public void ThenTheDatabaseRejectsWithForeignKey() =>
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, _error?.SqlState);

    [Then("the database rejects the row with a unique constraint error")]
    public void ThenTheDatabaseRejectsWithUnique() =>
        Assert.Equal(PostgresErrorCodes.UniqueViolation, _error?.SqlState);

    [When("I register the mod {string} in the versions {string} and {string}")]
    public async Task WhenIRegisterTheModInTheVersions(string guid, string first, string second)
    {
        await RegisterAsync(guid, first);
        await RegisterAsync(guid, second);
        _modGuid = guid;
    }

    [Then("both versions exist in the table {string}")]
    public async Task ThenBothVersionsExist(string table)
    {
        Assert.Equal("mods", table);

        await using var db = world.Open();
        var versions = await db.Mods.Where(mod => mod.ModGuid == _modGuid).Select(mod => mod.Version).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["1.0.0", "1.1.0"], versions.Order(StringComparer.Ordinal));
    }

    [Given("the mod {string} in the version {string} is registered")]
    public async Task GivenTheModIsRegistered(string guid, string version)
    {
        _modGuid = guid;
        _version = version;
        await RegisterAsync(guid, version);
    }

    [When("I register it again with the same version")]
    public async Task WhenIRegisterItAgain() => _error = await RegisterAsync(_modGuid, _version, capture: true);

    [Given("the mods {string} and {string}")]
    public void GivenTheMods(string first, string second)
    {
        _modGuid = first;
        _otherModGuid = second;
    }

    [When("both mods add an item with the id {string}")]
    public async Task WhenBothModsAddAnItem(string label)
    {
        await EnsureBaselineAsync();

        foreach (var guid in new[] { _modGuid, _otherModGuid })
        {
            _added[label] = label;
            await ArchiveAsync(guid, "1.0.0", [AddItem(Id(label), label, Ids.Node)]);
            Assert.False(Last.Failed, "The import of " + guid + " failed: " + Last.Failure);
        }
    }

    [Then("both rows exist in the table {string}")]
    public async Task ThenBothRowsExist(string table)
    {
        Assert.Equal("mod_items", table);

        await using var db = world.Open();
        var rows = await db.ModItems.Where(item => item.Name == "C1").ToListAsync(TestContext.Current.CancellationToken);
        var datasets = await db.ModDatasets.Where(dataset => dataset.ModGuid == _modGuid || dataset.ModGuid == _otherModGuid).CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(row => row.Id).Distinct());
        Assert.Equal(2, rows.Select(row => row.DatasetId).Distinct().Count());
        Assert.Equal(2, datasets);
    }

    // ---- Mods and official rows ----------------------------------------------------------------

    [Given("the official trader {string}")]
    public void GivenTheOfficialTrader(string label) => _official[label] = Ids.Trader;

    [When("the mod {string} adds an assort row for the trader {string}")]
    public async Task WhenTheModAddsAnAssortRow(string guid, string label)
    {
        await EnsureBaselineAsync();
        _officialRowsBefore = await world.CountAsync("trader_assort_items", world.Release);

        // A new offer: the same item for another payment, so no offer of the baseline has its name.
        await ArchiveAsync(guid, "1.0.0",
        [
            root => EditJson(root, $"traders/{_official[label]}/assort.json", assort =>
            {
                assort["items"]!.AsArray().Add(new JsonObject { ["_id"] = "offer2", ["_tpl"] = Ids.Item, ["parentId"] = "hideout", ["slotId"] = "hideout" });
                assort["barter_scheme"]!["offer2"] = new JsonArray(new JsonArray(new JsonObject { ["count"] = 3, ["_tpl"] = "5449016a4bdc2d6f028b456f" }));
                assort["loyal_level_items"]!["offer2"] = 3;
            }),
        ]);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the table {string} has that row")]
    public async Task ThenTheTableHasThatRow(string table)
    {
        Assert.Equal("mod_trader_assort_items", table);

        await using var db = world.Open();
        var rows = await db.ModTraderAssortItems.ToListAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(Ids.Trader, row.TraderId);
        Assert.Equal(3, row.LoyaltyLevel);
        Assert.Equal(Ids.Item, row.TemplateId);
    }

    [Then("the table {string} has no new row")]
    public async Task ThenTheTableHasNoNewRow(string table)
    {
        Assert.Equal("trader_assort_items", table);
        Assert.True(_officialRowsBefore > 0, "The baseline holds no offer, so the check proves nothing.");
        Assert.Equal(_officialRowsBefore, await world.CountAsync(table, world.Release));
    }

    [Given("the mod {string} has a mod item {string}")]
    public async Task GivenTheModHasAModItem(string guid, string label)
    {
        await EnsureBaselineAsync();
        _added[label] = label;
        _modGuid = guid;

        await ArchiveAsync(guid, "1.0.0", [AddItem(Id(label), label, Ids.Node)]);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [When("the Archiver imports the official data")]
    public async Task WhenTheArchiverImportsTheOfficialData()
    {
        // The same files again: Force makes the import replace the official rows of the release (ADR-0036).
        world.Force = true;
        await world.RunAsync();
        Assert.False(world.Failed, "The official import failed: " + world.Failure);
    }

    [Then("the mod item {string} still exists")]
    public async Task ThenTheModItemStillExists(string label)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        Assert.True(await db.ModItems.AnyAsync(item => item.Id == ContentUlid.Create("item", label), token));
        Assert.Equal(1, await db.ModItems.CountAsync(token));
        Assert.Equal(1, await db.ModDatasets.CountAsync(token));
    }

    // ---- Dataset and observation ---------------------------------------------------------------

    [Given("the mod {string} in the version {string}")]
    public void GivenTheModInTheVersion(string guid, string version)
    {
        _modGuid = guid;
        _version = version;
    }

    [When("the Archiver archives that mod version")]
    public async Task WhenTheArchiverArchivesThatModVersion()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the table {string} has one row for that version")]
    public async Task ThenTheTableHasOneRowForThatVersion(string table)
    {
        Assert.Equal("mod_datasets", table);

        await using var db = world.Open();
        var mod = await db.Mods.SingleAsync(row => row.ModGuid == _modGuid && row.Version == _version, TestContext.Current.CancellationToken);
        var observation = await db.ModDataObservations.SingleAsync(row => row.ModId == mod.Id, TestContext.Current.CancellationToken);
        var datasets = await db.ModDatasets.Where(dataset => dataset.ModGuid == _modGuid).ToListAsync(TestContext.Current.CancellationToken);

        // A dataset is keyed by the content and the mod: the version reaches it through the observation.
        Assert.Equal(observation.DatasetId, Assert.Single(datasets).Id);
    }

    [Then("that row states the content hash, the table hashes and the row counts")]
    public async Task ThenThatRowStatesTheHashes()
    {
        await using var db = world.Open();
        var dataset = await db.ModDatasets.SingleAsync(row => row.ModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(64, dataset.ContentHash.Length);

        using var hashes = JsonDocument.Parse(dataset.TableHashes);
        Assert.NotEmpty(hashes.RootElement.EnumerateObject());
        Assert.All(hashes.RootElement.EnumerateObject(), property => Assert.False(string.IsNullOrWhiteSpace(property.Value.GetString())));

        using var counts = JsonDocument.Parse(dataset.RowCounts);
        Assert.Equal(1, counts.RootElement.GetProperty("item").GetInt32());
    }

    [When("the Archiver archives that mod version on the server version {string}")]
    public async Task WhenTheArchiverArchivesThatModVersionOn(string release)
    {
        Assert.Equal(world.Release, release);

        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the observation states the mod version {string} and the server version {string}")]
    public async Task ThenTheObservationStates(string version, string release)
    {
        await using var db = world.Open();
        var observation = await db.ModDataObservations.Include(row => row.Mod).SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((_modGuid, version, release), (observation.Mod.ModGuid, observation.Mod.Version, observation.SptVersion));
    }

    [Then("the observation points at the dataset of that mod version")]
    public async Task ThenTheObservationPointsAtTheDataset()
    {
        await using var db = world.Open();
        var observation = await db.ModDataObservations.SingleAsync(TestContext.Current.CancellationToken);
        var dataset = await db.ModDatasets.SingleAsync(row => row.ModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(dataset.Id, observation.DatasetId);
    }

    [Given("the mod version {string} has the dataset {string}")]
    public async Task GivenTheModVersionHasTheDataset(string version, string label)
    {
        await EnsureBaselineAsync();
        _version = version;

        await ArchiveAsync(_modGuid, version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
        await RememberDatasetAsync(label);
    }

    [When("the version {string} of that mod exports the same content")]
    public async Task WhenTheVersionExportsTheSameContent(string version)
    {
        _version = version;
        await ArchiveAsync(_modGuid, version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the observation of {string} points at the dataset {string}")]
    public async Task ThenTheObservationOfPointsAtTheDataset(string version, string label)
    {
        await using var db = world.Open();
        var observation = await db.ModDataObservations
            .Include(row => row.Mod)
            .SingleOrDefaultAsync(row => row.Mod.ModGuid == _modGuid && row.Mod.Version == version, TestContext.Current.CancellationToken);

        Assert.True(observation is not null, $"No observation states the version {version}.");
        Assert.Equal(_datasets[label], observation.DatasetId);
    }

    [Then("the table {string} has one row for that mod")]
    public async Task ThenTheTableHasOneRowForThatMod(string table)
    {
        Assert.Equal("mod_datasets", table);

        await using var db = world.Open();

        Assert.Equal(1, await db.ModDatasets.CountAsync(dataset => dataset.ModGuid == _modGuid, TestContext.Current.CancellationToken));
    }

    [Given("the dataset {string} of the mod {string}")]
    public async Task GivenTheDatasetOfTheMod(string label, string guid)
    {
        await EnsureBaselineAsync();
        _modGuid = guid;

        await ArchiveAsync(guid, _version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
        await RememberDatasetAsync(label);
    }

    [When("the archive observes that mod version on {string} and on {string}")]
    public async Task WhenTheArchiveObservesThatModVersionOnAnd(string first, string second)
    {
        Assert.Equal(world.Release, first);

        // The first release has its observation; the run is current there, and the import says so.
        await ArchiveAsync(_modGuid, _version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);

        // The second release is another official import of the same files, and a run on it.
        world.UseRelease(second);
        await world.RunAsync();
        Assert.False(world.Failed, "The official import of " + second + " failed: " + world.Failure);

        await ArchiveAsync(_modGuid, _version, DefaultContent());
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the dataset {string} stays one row")]
    public async Task ThenTheDatasetStaysOneRow(string label)
    {
        await using var db = world.Open();
        var datasets = await db.ModDatasets.Where(dataset => dataset.ModGuid == _modGuid).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_datasets[label], Assert.Single(datasets).Id);
    }

    [Then("two observations point at {string}")]
    public async Task ThenTwoObservationsPointAt(string label)
    {
        await using var db = world.Open();
        var observations = await db.ModDataObservations.Where(row => row.DatasetId == _datasets[label]).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["4.0.13", "4.0.14"], observations.Select(row => row.SptVersion).Order(StringComparer.Ordinal));
    }

    [When("the export of that mod version equals its baseline export")]
    public async Task WhenTheExportEqualsTheBaseline()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, []);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the run stores a dataset with no template rows")]
    public async Task ThenTheRunStoresADatasetWithNoTemplateRows()
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;
        var dataset = await db.ModDatasets.SingleAsync(row => row.ModGuid == _modGuid, token);

        Assert.Equal(0, await db.ModTemplates.CountAsync(row => row.DatasetId == dataset.Id, token));
        Assert.Equal(0, await db.ModChangePaths.CountAsync(row => row.DatasetId == dataset.Id, token));
        Assert.Equal(0, await db.ModItems.CountAsync(row => row.DatasetId == dataset.Id, token));
    }

    [Then("the run logs that the mod version changed nothing")]
    public void ThenTheRunLogsThatTheModVersionChangedNothing() =>
        Assert.Contains(
            Last.Log.Entries,
            entry => entry.Message.Contains("changed no table", StringComparison.Ordinal)
                && entry.Message.Contains(_modGuid, StringComparison.Ordinal)
                && entry.Message.Contains(_version, StringComparison.Ordinal));

    [When("the export of that mod version does not hold the mod GUID")]
    public async Task WhenTheExportDoesNotHoldTheModGuid()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, DefaultContent(), loadsMod: false);
    }

    [Then("the run fails with a message that names the mod GUID")]
    public void ThenTheRunFailsWithAMessage()
    {
        Assert.True(Last.Failed, "The run did not fail.");
        Assert.Contains(_modGuid, Last.Failure!.Message, StringComparison.Ordinal);
    }

    [Then("the observation points at no dataset")]
    public async Task ThenTheObservationPointsAtNoDataset()
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        // The run records no observation at all, so no observation points at a dataset.
        Assert.Empty(await db.ModDataObservations.Where(row => row.DatasetId != null).ToListAsync(token));
        Assert.Empty(await db.ModDatasets.Where(dataset => dataset.ModGuid == _modGuid).ToListAsync(token));

        var run = await db.ArchiveRuns.Where(row => row.Kind == ArchiveRunKind.Mod).SingleAsync(token);
        Assert.Equal(ArchiveRun.Failed, run.Status);
    }

    // ---- A change to an official item ----------------------------------------------------------

    [Given("a mod that adds the item {string} with the parent {string}")]
    public void GivenAModThatAddsTheItem(string label, string parent)
    {
        // The parent is a node of the baseline, so that the row names a parent the archive holds.
        _added[label] = label;
        EditJson(world.Source, "templates/items.json", items => items[parent] = Template(parent, parent, "", "Node", new JsonObject()));
        _edits.Add(AddItem(Id(label), label, parent));
    }

    [When("the Archiver archives that mod")]
    public async Task WhenTheArchiverArchivesThatMod()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, _edits);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the table {string} has one row for {string} with the operation {string}")]
    public async Task ThenTheTableHasOneRowFor(string table, string label, string operation)
    {
        Assert.Equal("mod_templates", table);

        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.TemplateId == TemplateId(label)).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(operation, Assert.Single(rows).Op);
    }

    [Then("that row states the parent {string}")]
    public async Task ThenThatRowStatesTheParent(string parent)
    {
        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.Kind == "item").ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(parent, Assert.Single(rows).ParentId);
    }

    [Then("every path row of {string} states no old value")]
    public async Task ThenEveryPathRowStatesNoOldValue(string label)
    {
        await using var db = world.Open();
        var rows = await db.ModChangePaths.Where(row => row.TemplateId == TemplateId(label)).ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(rows, row => Assert.Null(row.OldValue));
    }

    [Given("the official item {string}")]
    public void GivenTheOfficialItem(string label)
    {
        _official[label] = Ids.Item;
        EditJson(world.Source, "templates/items.json", items => items[Ids.Item]!["_name"] = label);
    }

    [Given("a mod that writes the template id {string}")]
    public void GivenAModThatWritesTheTemplateId(string label) =>
        _edits.Add(root => EditJson(root, "templates/items.json", items =>
        {
            // A whole template under the id of the official item, as a mod that adds an item of that id writes it.
            items[_official[label]] = Template(_official[label], "Mod " + label, Ids.Node, "Item", new JsonObject { [Weight] = 9, ["Width"] = 3, ["Height"] = 3 });
        }));

    [Then("the row of {string} states the operation {string}")]
    public async Task ThenTheRowOfStatesTheOperation(string label, string operation)
    {
        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.TemplateId == _official[label]).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(operation, Assert.Single(rows).Op);
    }

    [Then("no row states the operation {string} for {string}")]
    public async Task ThenNoRowStatesTheOperation(string operation, string label)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        Assert.False(await db.ModTemplates.AnyAsync(row => row.Op == operation && row.TemplateId == _official[label], token));
        Assert.Equal(1, await db.ModTemplates.CountAsync(token));
    }

    [Given("the official item {string} has no property {string}")]
    public void GivenTheOfficialItemHasNoProperty(string label, string property)
    {
        _official[label] = Ids.Item;
        Assert.False(world.Load("templates/items.json")[Ids.Item]!["_props"]!.AsObject().ContainsKey(property.Split('/')[^1]));
        EditJson(world.Source, "templates/items.json", items => items[Ids.Item]!["_name"] = label);
    }

    [When("a mod sets that property to true")]
    public async Task WhenAModSetsThatPropertyToTrue()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, [root => EditJson(root, "templates/items.json", items => items[Ids.Item]!["_props"]!["CanSellOnRagfair"] = true)]);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the template {string} states the operation {string}")]
    public async Task ThenTheTemplateStatesTheOperation(string label, string operation)
    {
        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.TemplateId == _official[label]).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(operation, Assert.Single(rows).Op);
    }

    [Then("that path row states no old value")]
    public async Task ThenThatPathRowStatesNoOldValue()
    {
        await using var db = world.Open();
        var row = await db.ModChangePaths.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/_props/CanSellOnRagfair", row.Path);
        Assert.Null(row.OldValue);
        Assert.Equal("true", row.NewValue);
    }

    // ---- A change to data that has no mod table ------------------------------------------------

    [Given("the baseline holds the location {string} with {string} true in its base file")]
    public void GivenTheBaselineHoldsTheLocation(string map, string field)
    {
        Assert.Equal("woods", map);

        world.Write("locations/woods/base.json", ObjectTree.Baseline()["locations/woods/base.json"]);
        Assert.True(world.Load("locations/woods/base.json")[field]!.GetValue<bool>());
    }

    [Given("a mod run sets {string} to false")]
    public void GivenAModRunSetsToFalse(string field) =>
        _edits.Add(root => EditJson(root, "locations/woods/base.json", map => map[field] = false));

    [When("the Archiver archives that run")]
    public async Task WhenTheArchiverArchivesThatRun()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, _edits);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the table {string} has one row of the kind {string} for {string} with the operation {string}")]
    public async Task ThenTheTableHasOneRowOfTheKindFor(string table, string kind, string label, string operation)
    {
        Assert.Equal("mod_templates", table);

        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.Kind == kind && row.TemplateId == TemplateId(label)).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(operation, Assert.Single(rows).Op);
    }

    [Then("the table {string} states the path {string} for it")]
    public async Task ThenTheTableStatesThePathForIt(string table, string path)
    {
        Assert.Equal("mod_change_paths", table);

        await using var db = world.Open();
        var rows = await db.ModChangePaths.Where(row => row.Kind == "location").ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(path, Assert.Single(rows).Path);
    }

    [Then("that row states the old value {string} and the new value {string}")]
    public async Task ThenThatRowStatesTheOldAndNewValue(string oldValue, string newValue)
    {
        await using var db = world.Open();
        var row = await db.ModChangePaths.SingleAsync(path => path.Kind == "location", TestContext.Current.CancellationToken);

        Assert.Equal((oldValue, newValue), (row.OldValue, row.NewValue));
    }

    [Given("the baseline holds the location {string} with a boss chance of {int}")]
    public void GivenTheBaselineHoldsTheLocationWithABossChance(string map, int chance)
    {
        Assert.Equal("woods", map);

        world.Write("locations/woods/base.json", ObjectTree.Baseline()["locations/woods/base.json"]);
        EditJson(world.Source, "locations/woods/base.json", file => file["BossLocationSpawn"]![0]!["BossChance"] = chance);
    }

    [Given("a mod run holds the boss chance {int} and changes nothing else of that map")]
    public void GivenAModRunHoldsTheBossChance(int chance) =>
        _edits.Add(root => EditJson(root, "locations/woods/base.json", file => file["BossLocationSpawn"]![0]!["BossChance"] = chance));

    [Then("no row of the kind {string} exists for {string}")]
    public async Task ThenNoRowOfTheKindExists(string kind, string map)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        Assert.False(await db.ModTemplates.AnyAsync(row => row.Kind == kind && row.TemplateId == map, token));
        Assert.False(await db.ModChangePaths.AnyAsync(row => row.Kind == kind, token));
    }

    [Given("a mod run holds the location {string} and the baseline does not")]
    public void GivenAModRunHoldsTheLocation(string map)
    {
        Assert.False(File.Exists(Path.Combine(world.Source, "locations", map, "base.json")));

        _edits.Add(root => AddLocation(root, map));
    }

    [Then("the table {string} has one row of the kind {string} with the operation {string}")]
    public async Task ThenTheTableHasOneRowOfTheKind(string table, string kind, string operation)
    {
        Assert.Equal("mod_templates", table);

        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.Kind == kind).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(operation, Assert.Single(rows).Op);
    }

    [Then("its template id is a content ULID of the kind {string} and the name {string}")]
    public async Task ThenItsTemplateIdIsAContentUlid(string kind, string name)
    {
        await using var db = world.Open();
        var row = await db.ModTemplates.SingleAsync(template => template.Kind == kind, TestContext.Current.CancellationToken);

        Assert.Equal(ContentUlid.Create(kind, name), row.TemplateId);
    }

    [Then("the table {string} holds one row for each file of the map, named by its part")]
    public async Task ThenTheTableHoldsOneRowForEachFile(string table)
    {
        Assert.Equal("mod_database_objects", table);

        await using var db = world.Open();
        var rows = await db.ModDatabaseObjects.ToListAsync(TestContext.Current.CancellationToken);
        var files = Directory.GetFiles(Path.Combine(Last.Root, "locations", "arena_airpit")).Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);

        Assert.Equal(["base", "looseLoot"], files);
        Assert.Equal(files, rows.Select(row => row.Part).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal(ContentUlid.Create("location", "arena_airpit"), row.TemplateId));
    }

    [Then("the table {string} pairs {string} with that address")]
    public async Task ThenTheTablePairsWithThatAddress(string table, string name)
    {
        Assert.Equal("mod_id_maps", table);

        await using var db = world.Open();
        var map = await db.ModIdMaps.SingleAsync(row => row.RawId == name, TestContext.Current.CancellationToken);

        Assert.Equal(("location", ContentUlid.Create("location", name)), (map.Kind, map.Address));
    }

    [Given("the baseline holds the bot type {string} with {string} {int}")]
    public void GivenTheBaselineHoldsTheBotType(string type, string field, int value)
    {
        Assert.Equal("assault", type);

        world.Write("bots/types/assault.json", new JsonObject { ["chances"] = new JsonObject { ["weaponMods"] = new JsonObject { [field] = value } } }.ToJsonString());
    }

    [Given("a mod run sets {string} to {int}")]
    public void GivenAModRunSetsTo(string field, int value) =>
        _edits.Add(root => EditJson(root, "bots/types/assault.json", file => file["chances"]!["weaponMods"]![field] = value));

    [Then("the table {string} states the path {string} with the old value {int} and the new value {int}")]
    public async Task ThenTheTableStatesThePathWithTheValues(string table, string path, int oldValue, int newValue)
    {
        Assert.Equal("mod_change_paths", table);

        await using var db = world.Open();
        var row = await db.ModChangePaths.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((path, oldValue.ToString(), newValue.ToString()), (row.Path, row.OldValue, row.NewValue));
    }

    [Given("the baseline holds the hideout recipe {string} that asks for {int} of an item")]
    public void GivenTheBaselineHoldsTheRecipe(string label, int count)
    {
        _official[label] = ObjectTree.RecipeId;
        world.Write("hideout/production.json", ObjectTree.Baseline()["hideout/production.json"]);
        EditJson(world.Source, "hideout/production.json", file => file["recipes"]![0]!["requirements"]![0]!["count"] = count);
    }

    [Given("a mod run asks for {int}")]
    public void GivenAModRunAsksFor(int count) =>
        _edits.Add(root => EditJson(root, "hideout/production.json", file => file["recipes"]![0]!["requirements"]![0]!["count"] = count));

    [Given("the baseline prices the item {string} at {int}")]
    public void GivenTheBaselinePricesTheItem(string label, int price)
    {
        _official[label] = Ids.Item;
        world.Write("templates/prices.json", new JsonObject { [Ids.Item] = price }.ToJsonString());
    }

    [Given("a mod run prices it at {int} and prices the added item {string} at {int}")]
    public void GivenAModRunPricesIt(int price, string label, int addedPrice)
    {
        _added[label] = label;
        _edits.Add(AddItem(ObjectTree.AddedItemId, label, Ids.Node));
        _edits.Add(root => EditJson(root, "templates/prices.json", file =>
        {
            file[Ids.Item] = price;
            file[ObjectTree.AddedItemId] = addedPrice;
        }));
    }

    [Then("its path row is the whole value, with the old value {int} and the new value {int}")]
    public async Task ThenItsPathRowIsTheWholeValue(int oldValue, int newValue)
    {
        await using var db = world.Open();
        var row = await db.ModChangePaths.SingleAsync(path => path.Kind == "price" && path.TemplateId == Ids.Item, TestContext.Current.CancellationToken);

        Assert.Equal((string.Empty, oldValue.ToString(), newValue.ToString()), (row.Path, row.OldValue, row.NewValue));
    }

    [Then("the price of {string} is named by the address of the item {string}")]
    public async Task ThenThePriceIsNamedByTheAddress(string priced, string item)
    {
        Assert.Equal(priced, item);

        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.Kind == "price" && row.TemplateId == TemplateId(item)).ToListAsync(TestContext.Current.CancellationToken);
        var itemRow = await db.ModTemplates.SingleAsync(row => row.Kind == "item", TestContext.Current.CancellationToken);

        Assert.Equal(ModTemplateOperation.Added, Assert.Single(rows).Op);
        Assert.Equal(itemRow.TemplateId, rows[0].TemplateId);
    }

    [Given("a mod run adds the handbook category {string} and the handbook entry of the added item {string} under it")]
    public void GivenAModRunAddsTheCategory(string category, string label)
    {
        _added[label] = label;
        _official[category] = ObjectTree.AddedCategoryId;
        world.Write("templates/handbook.json", ObjectTree.Baseline()["templates/handbook.json"]);

        _edits.Add(AddItem(ObjectTree.AddedItemId, label, Ids.Node));
        _edits.Add(root => EditJson(root, "templates/handbook.json", file =>
        {
            file["Categories"]!.AsArray().Add(new JsonObject { ["Id"] = ObjectTree.AddedCategoryId, ["ParentId"] = ObjectTree.CategoryId, ["Icon"] = "/new.png", ["Color"] = "", ["Order"] = "200" });
            file["Items"]!.AsArray().Add(new JsonObject { ["Id"] = ObjectTree.AddedItemId, ["ParentId"] = ObjectTree.AddedCategoryId, ["Price"] = 4000 });
        }));
    }

    [Then("the table {string} has one row of the kind {string} for the entry of {string}")]
    public async Task ThenTheTableHasOneRowForTheEntry(string table, string kind, string label)
    {
        Assert.Equal("mod_templates", table);

        await using var db = world.Open();
        var rows = await db.ModTemplates.Where(row => row.Kind == kind).ToListAsync(TestContext.Current.CancellationToken);

        // The category is a row of the kind too; the entry is the one that is named by the item.
        var entry = Assert.Single(rows, row => row.TemplateId == TemplateId(label));
        Assert.Equal(ModTemplateOperation.Added, entry.Op);
    }

    [Then("that row states the address of {string} as its parent")]
    public async Task ThenThatRowStatesTheAddressAsItsParent(string category)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;
        var map = await db.ModIdMaps.SingleAsync(row => row.RawId == _official[category] && row.Kind == "handbook", token);
        var entry = await db.ModTemplates.SingleAsync(row => row.Kind == "handbook" && row.TemplateId == TemplateId("B1"), token);

        Assert.Equal(map.Address, entry.ParentId);
        Assert.NotEqual(_official[category], entry.ParentId);
    }

    [Then("{string} holds the entry as the run wrote it")]
    public async Task ThenTheTableHoldsTheEntry(string table)
    {
        Assert.Equal("mod_database_objects", table);

        await using var db = world.Open();
        var row = await db.ModDatabaseObjects.SingleAsync(item => item.Kind == "handbook" && item.TemplateId == TemplateId("B1"), TestContext.Current.CancellationToken);
        var written = new JsonObject { ["Id"] = ObjectTree.AddedItemId, ["ParentId"] = ObjectTree.AddedCategoryId, ["Price"] = 4000 };

        Assert.True(JsonNode.DeepEquals(written, JsonNode.Parse(row.Data)), "The row holds " + row.Data);
    }

    [Given("a mod run adds the item {string} with an id that the run minted")]
    public void GivenAModRunAddsTheItemWithAMintedId(string label)
    {
        _added[label] = label;
        _bandRaw = ObjectTree.AddedItemId;
        EditJson(world.Source, "bots/types/assault.json", _ => { }, create: new JsonObject
        {
            ["chances"] = new JsonObject { ["weaponMods"] = new JsonObject { ["mod_scope"] = 50 } },
            ["inventory"] = new JsonObject { ["items"] = new JsonObject { ["Backpack"] = new JsonObject { [Ids.Item] = 1 } } },
        });
        _edits.Add(AddItem(_bandRaw, label, Ids.Node));
    }

    [Given("a bot type of that run holds the id of {string} as a key and as a value")]
    public void GivenABotTypeHoldsTheId(string label)
    {
        Assert.Equal("B1", label);

        _edits.Add(root => EditJson(root, "bots/types/assault.json", file =>
        {
            file["inventory"]!["items"]!["Backpack"]![_bandRaw] = 1;
            file["inventory"]!["favourite"] = _bandRaw;
        }));
    }

    [Then("the path of that key and the new value name the address of {string}")]
    public async Task ThenThePathNamesTheAddress(string label)
    {
        var address = ContentUlid.Create("item", label);

        await using var db = world.Open();
        var rows = await db.ModChangePaths.Where(row => row.Kind == "bot").ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(rows, row => row.Path.Contains(address, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.NewValue is not null && row.NewValue.Contains(address, StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Path.Contains(_bandRaw, StringComparison.Ordinal) || (row.NewValue ?? "").Contains(_bandRaw, StringComparison.Ordinal));
    }

    [Then("a second run that mints another id for {string} writes the same paths and the same values")]
    public async Task ThenASecondRunWritesTheSame(string label)
    {
        var first = PlanPaths(Last.Root, _modGuid);
        var other = "dddddddddddddddddddddd99";

        // The second run holds the item under another id, and the bot type names that id in the same two places.
        var edits = _edits.ToList();
        edits.Add(root => EditJson(root, "templates/items.json", items =>
        {
            var item = items[_bandRaw]!.DeepClone();
            items.AsObject().Remove(_bandRaw);
            item["_id"] = other;
            items[other] = item;
        }));
        edits.Add(root => EditJson(root, "bots/types/assault.json", file =>
        {
            var backpack = file["inventory"]!["items"]!["Backpack"]!.AsObject();
            backpack.Remove(_bandRaw);
            backpack[other] = 1;
            file["inventory"]!["favourite"] = other;
        }));

        await ArchiveAsync(_modGuid, "1.0.1", edits);
        Assert.False(Last.Failed, "The second import failed: " + Last.Failure);

        var second = PlanPaths(Last.Root, _modGuid);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);

        // What each run wrote is the same: the rows of every dataset of the mod are the paths of the plan.
        await using var db = world.Open();
        var rows = await db.ModChangePaths.ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(rows);

        foreach (var dataset in rows.GroupBy(row => row.DatasetId))
        {
            Assert.Equal(first, dataset.Select(row => $"{row.Kind}|{row.TemplateId}|{row.Path}|{row.Op}|{row.OldValue}|{row.NewValue}").Order(StringComparer.Ordinal));
        }
    }

    [Then("both runs write one dataset")]
    public async Task ThenBothRunsWriteOneDataset()
    {
        // The id that each run minted is not content (ADR-0034, decision 5), so the two runs are one
        // dataset and not two with the same rows (ADR-0013).
        await using var db = world.Open();

        var datasets = await db.ModDatasets
            .Where(dataset => dataset.ModGuid == _modGuid)
            .Select(dataset => dataset.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        var datasetId = Assert.Single(datasets);

        var datasetsInTheRows = await db.ModChangePaths
            .Select(row => row.DatasetId)
            .Distinct()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([datasetId], datasetsInTheRows);
    }

    [Given("the item {string} has the slots {string} and {string}, in that order")]
    public void GivenTheItemHasTheSlots(string label, string first, string second)
    {
        _official[label] = Id(label);
        _bandRaw = first + "," + second;

        EditJson(world.Source, "templates/items.json", items =>
            items[Id(label)] = Template(Id(label), label, Ids.Node, "Item", new JsonObject { ["Slots"] = new JsonArray(Slot(first), Slot(second)) }));
    }

    [Given("a second mod run holds the same slots in the order {string} and {string}")]
    public void GivenASecondModRunHoldsTheSlots(string first, string second)
    {
        Assert.Equal(_bandRaw, second + "," + first);
        _otherModGuid = _modGuid;
    }

    [When("both runs add the filter entry {string} to the slot {string}")]
    public async Task WhenBothRunsAddTheFilterEntry(string entry, string slot)
    {
        await EnsureBaselineAsync();

        var names = _bandRaw.Split(',');
        var item = _official["B1"];

        Action<string> AddFilter(string[] order) => root => EditJson(root, "templates/items.json", items =>
        {
            var slots = new JsonArray(order.Select(name => (JsonNode)Slot(name, name == slot ? entry : null)).ToArray());
            items[item]!["_props"]!["Slots"] = slots;
        });

        await ArchiveAsync(_modGuid, "1.0.0", [AddFilter(names)]);
        Assert.False(Last.Failed, "The first import failed: " + Last.Failure);
        await ArchiveAsync(_modGuid, "1.1.0", [AddFilter(names.Reverse().ToArray())]);
        Assert.False(Last.Failed, "The second import failed: " + Last.Failure);
    }

    [Then("the table {string} holds the same path for both runs")]
    public void ThenTheTableHoldsTheSamePath(string table)
    {
        Assert.Equal("mod_change_paths", table);

        var first = PlanPaths(_runs[^2].Root, _modGuid);
        var second = PlanPaths(_runs[^1].Root, _modGuid);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);

        // What each run contributes is in the table: every path of the plan is a row.
        using var db = world.Open();
        var stored = db.ModChangePaths.AsEnumerable().Select(row => $"{row.Kind}|{row.TemplateId}|{row.Path}|{row.Op}|{row.OldValue}|{row.NewValue}").ToHashSet(StringComparer.Ordinal);

        Assert.All(first, path => Assert.Contains(path, stored));
    }

    [Then("that path names the slot {string} and holds no index of the slot")]
    public async Task ThenThatPathNamesTheSlot(string slot)
    {
        await using var db = world.Open();
        var rows = await db.ModChangePaths.ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            var segments = row.Path.Split('/');
            var after = segments[Array.IndexOf(segments, "Slots") + 1];

            Assert.Equal(slot, after);
        }
    }

    [Given("a mod run whose location file holds the same spawn points in another order")]
    public void GivenAModRunWhoseLocationFileHoldsTheSameSpawnPoints()
    {
        world.Write("locations/woods/base.json", ObjectTree.Baseline()["locations/woods/base.json"]);
        world.Write("locations/woods/looseLoot.json", ObjectTree.RunOverBaseline()["locations/woods/looseLoot.json"]);

        _edits.Add(root => EditJson(root, "locations/woods/looseLoot.json", file =>
        {
            var points = file["spawnpoints"]!.AsArray();
            var reversed = points.Select(point => point!.DeepClone()).Reverse().ToArray();

            points.Clear();

            foreach (var point in reversed)
            {
                points.Add(point);
            }
        }));
    }

    [Then("the table {string} holds no row for that list")]
    public async Task ThenTheTableHoldsNoRowForThatList(string table)
    {
        Assert.Equal("mod_change_paths", table);

        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        Assert.Empty(await db.ModChangePaths.ToListAsync(token));
        Assert.Empty(await db.ModDatabaseObjects.ToListAsync(token));
    }

    [Then("the canonical hash of that file is part of the content hash of the dataset")]
    public async Task ThenTheCanonicalHashIsPartOfTheContentHash()
    {
        await using var db = world.Open();
        var dataset = await db.ModDatasets.SingleAsync(TestContext.Current.CancellationToken);

        using var hashes = JsonDocument.Parse(dataset.TableHashes);
        Assert.True(hashes.RootElement.TryGetProperty("locations/woods/looseLoot.json", out var hash), "The table hashes do not name the file: " + dataset.TableHashes);
        Assert.False(string.IsNullOrWhiteSpace(hash.GetString()));
    }

    [Given("an array whose elements have none of the identity fields")]
    public void GivenAnArrayWithoutIdentity()
    {
        EditJson(world.Source, "templates/items.json", items =>
            items[Ids.Item]!["_props"]!["Paths"] = new JsonArray(new JsonObject { ["Source"] = "a" }, new JsonObject { ["Source"] = "b" }));
    }

    [Given("an array with two elements that share the same {string}")]
    public void GivenAnArrayWithTwoElementsThatShare(string field)
    {
        EditJson(world.Source, "templates/items.json", items =>
            items[Ids.Item]!["_props"]!["Paths"] = new JsonArray(new JsonObject { [field] = "a", ["v"] = 1 }, new JsonObject { [field] = "a", ["v"] = 2 }));
    }

    [When("the Archiver archives a run that changes the second element")]
    public async Task WhenTheArchiverArchivesARunThatChangesTheSecondElement()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version,
        [
            root => EditJson(root, "templates/items.json", items =>
            {
                var second = items[Ids.Item]!["_props"]!["Paths"]![1]!.AsObject();
                second[second.ContainsKey("v") ? "v" : "Source"] = second.ContainsKey("v") ? 3 : "c";
            }),
        ]);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the path of the change holds the index of that element")]
    public async Task ThenThePathHoldsTheIndex()
    {
        await using var db = world.Open();
        var row = await db.ModChangePaths.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Contains("/Paths/1/", row.Path, StringComparison.Ordinal);
    }

    [Then("the warnings of the run state the file and the shape of the element")]
    public async Task ThenTheWarningsStateTheFileAndTheShape()
    {
        var warnings = await WarningsAsync();

        var warning = Assert.Single(warnings);
        Assert.Contains("templates/items.json", warning, StringComparison.Ordinal);
        Assert.Contains("Paths", warning, StringComparison.Ordinal);
        Assert.Contains("cannot be named by identity", warning, StringComparison.Ordinal);
    }

    [Then("the first element keeps its own path")]
    public async Task ThenTheFirstElementKeepsItsOwnPath()
    {
        await using var db = world.Open();
        var row = await db.ModChangePaths.SingleAsync(TestContext.Current.CancellationToken);

        // The change of the second element is not written under the first one.
        Assert.DoesNotContain("/Paths/0/", row.Path, StringComparison.Ordinal);
        Assert.Contains("/Paths/1/v", row.Path, StringComparison.Ordinal);
    }

    [Then("the warnings of the run state that two elements share the identity")]
    public async Task ThenTheWarningsStateThatTwoElementsShareTheIdentity()
    {
        var warnings = await WarningsAsync();

        Assert.Contains(warnings, warning => warning.Contains("share an identity", StringComparison.Ordinal) && warning.Contains("templates/items.json", StringComparison.Ordinal));
    }

    [Given("a mod run that changes a location, a bot type, a hideout part, a price and a handbook entry")]
    public void GivenAModRunThatChangesFiveObjects()
    {
        var baseline = ObjectTree.Baseline();

        foreach (var (path, content) in baseline.Where(file => !file.Key.StartsWith("templates/items", StringComparison.Ordinal)
                     && file.Key is not ("templates/quests.json" or "templates/customization.json")))
        {
            world.Write(path, content);
        }

        _edits.Add(root =>
        {
            var run = ObjectTree.Run();

            foreach (var (path, content) in run.Where(file => !file.Key.StartsWith("templates/items", StringComparison.Ordinal)))
            {
                WriteIn(root, path, content);
            }
        });
    }

    [When("I compute the content hash of that run")]
    public async Task WhenIComputeTheContentHash()
    {
        await EnsureBaselineAsync();
        await ArchiveAsync(_modGuid, _version, _edits);
        Assert.False(Last.Failed, "The import failed: " + Last.Failure);
    }

    [Then("the hash equals the hash that the same run had before these objects had rows")]
    public async Task ThenTheHashEqualsTheEarlierHash()
    {
        // Before these objects had rows, a contribution held no object and the hash covered the rest. That is the
        // contribution without its objects, hashed with the same files.
        var plan = NewImporter(Last.Root, _modGuid).Prepare();

        Assert.NotEmpty(plan.Contribution.Objects);

        var without = plan.Contribution with { Objects = [] };
        var files = plan.Files.Select(file => new ContributionFile(
            file.Path,
            file.Sha256,
            file.ContentText is null ? null : DatasetHash.HashText(file.ContentText)));

        Assert.Equal(DatasetHash.Compute(without, files), plan.ContentHash);
    }

    [Then("the hash is the same with and without the object rows")]
    public async Task ThenTheHashIsTheSameWithAndWithoutTheObjectRows()
    {
        var plan = NewImporter(Last.Root, _modGuid).Prepare();
        var files = plan.Files.Select(file => new ContributionFile(
            file.Path,
            file.Sha256,
            file.ContentText is null ? null : DatasetHash.HashText(file.ContentText)));

        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;
        var dataset = await db.ModDatasets.SingleAsync(token);

        // The archive holds the object rows, and the stored hash is the one of the contribution without them.
        Assert.True(await db.ModTemplates.AnyAsync(row => ModTemplateKind.DatabaseObjects.Contains(row.Kind), token));
        Assert.Equal(DatasetHash.Compute(plan.Contribution with { Objects = [] }, files), dataset.ContentHash);
    }

    [Given("a mod run that adds the location {string}")]
    public void GivenAModRunThatAddsTheLocation(string map) => _edits.Add(root => AddLocation(root, map));

    [Then("the row of {string} is keyed by the dataset id")]
    public async Task ThenTheRowIsKeyedByTheDatasetId(string map)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;
        var dataset = await db.ModDatasets.SingleAsync(token);
        var address = ContentUlid.Create("location", map);

        Assert.Equal(dataset.Id, (await db.ModTemplates.SingleAsync(row => row.TemplateId == address, token)).DatasetId);
        Assert.All(await db.ModDatabaseObjects.Where(row => row.TemplateId == address).ToListAsync(token), row => Assert.Equal(dataset.Id, row.DatasetId));
        Assert.Equal(dataset.Id, (await db.ModIdMaps.SingleAsync(row => row.RawId == map, token)).DatasetId);
    }

    [Then("no official table holds a row of that location")]
    public async Task ThenNoOfficialTableHoldsTheLocation()
    {
        foreach (var table in OfficialTables)
        {
            var rows = await world.ScalarAsync<long>(
                $"select count(*) from {table} t where t::text like '%arena_airpit%' or t::text like '%' || @a || '%'",
                ("a", ContentUlid.Create("location", "arena_airpit")));

            Assert.True(rows == 0, $"'{table}' holds {rows} row(s) of the location.");
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>The baseline is the official import of the default source, as the Archiver makes it (ADR-0035).</summary>
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

    private async Task<ModRun> ArchiveAsync(string modGuid, string version, IEnumerable<Action<string>> edits, bool loadsMod = true)
    {
        var root = Path.Combine(world.Root, "run-" + ++_runCounter);

        CopyDirectory(world.Source, root);

        foreach (var edit in edits)
        {
            edit(root);
        }

        var tool = Mod(ExporterGuid, "1.5.0");
        var mods = loadsMod ? new[] { tool, Mod(modGuid, version) } : [tool];
        WriteIn(root, "run/loaded.json", JsonSerializer.Serialize(mods));

        if (loadsMod)
        {
            var directory = modGuid[(modGuid.LastIndexOf('.') + 1)..];
            WriteIn(root, $"run/files/{directory}.json", JsonSerializer.Serialize(new[]
            {
                new { path = "Mod.dll", size = 10, sha256 = new string('a', 64), kind = "assembly" },
            }));
        }

        var run = new ModRun(root, new LogSink());
        var options = new ArchiveOptions
        {
            RunPath = root,
            BaselinePath = world.Source,
            RunModGuid = modGuid,
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

        _runs.Add(run);
        return run;
    }

    private RunImporter NewImporter(string root, string modGuid) =>
        new(Options.Create(new ArchiveOptions { RunPath = root, RunModGuid = modGuid, BaselinePath = world.Source }), NullLogger<RunImporter>.Instance);

    /// <summary>What a run contributes as change paths, one text for each path, as the archive would write them.</summary>
    private IReadOnlyList<string> PlanPaths(string root, string modGuid)
    {
        var plan = NewImporter(root, modGuid).Prepare();

        return plan.Contribution.Templates
            .Concat(plan.Contribution.Objects.Select(row => row.Template))
            .SelectMany(template => template.Changes.Select(change =>
                $"{template.Kind}|{template.TemplateId}|{change.Path}|{change.Op}|{change.OldValue}|{change.NewValue}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<string[]> WarningsAsync()
    {
        await using var db = world.Open();
        var run = await db.ArchiveRuns.Where(row => row.Kind == ArchiveRunKind.Mod).OrderByDescending(row => row.StartedAt).FirstAsync(TestContext.Current.CancellationToken);

        using var result = JsonDocument.Parse(run.Result!);
        return result.RootElement.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToArray();
    }

    private async Task RememberDatasetAsync(string label)
    {
        await using var db = world.Open();

        _datasets[label] = (await db.ModDatasets.SingleAsync(dataset => dataset.ModGuid == _modGuid, TestContext.Current.CancellationToken)).Id;
    }

    private string TemplateId(string label) =>
        _official.TryGetValue(label, out var raw) ? raw
        : _added.ContainsKey(label) ? ContentUlid.Create("item", label)
        : label;

    private IEnumerable<Action<string>> DefaultContent() =>
        _edits.Count > 0 ? _edits : [AddItem(Id("Mod item"), "Mod item", Ids.Node)];

    /// <summary>A mod adds an item: a template under a raw id that the run gave it.</summary>
    private static Action<string> AddItem(string rawId, string name, string parent) =>
        root => EditJson(root, "templates/items.json", items =>
            items[rawId] = Template(rawId, name, parent, "Item", new JsonObject { [Weight] = 3 }));

    private static void AddLocation(string root, string map)
    {
        var run = ObjectTree.Run();

        WriteIn(root, $"locations/{map}/base.json", run["locations/arena_airpit/base.json"].Replace("arena_airpit", map, StringComparison.Ordinal));
        WriteIn(root, $"locations/{map}/looseLoot.json", run["locations/arena_airpit/looseLoot.json"]);
    }

    private static JsonObject Slot(string name, string? filter = null) => new()
    {
        ["_name"] = name,
        ["_props"] = new JsonObject
        {
            ["filters"] = new JsonArray(new JsonObject
            {
                ["Filter"] = filter is null ? new JsonArray("Y") : new JsonArray("Y", filter),
            }),
        },
    };

    /// <summary>A 24-character hex id that the label always gives.</summary>
    private static string Id(string label) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(label)))[..24].ToLowerInvariant();

    private static void EditJson(string root, string path, Action<JsonNode> edit, JsonNode? create = null)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        var node = create ?? JsonNode.Parse(File.ReadAllText(full))!;

        edit(node);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
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

    private async Task<PostgresException?> RegisterAsync(string guid, string version, bool capture = false)
    {
        await using var db = world.Open();
        db.Mods.Add(new Mod { ModGuid = guid, Name = guid, Version = version });

        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.False(capture, "The database accepted the mod version twice.");
            return null;
        }
        catch (DbUpdateException exception) when (capture && exception.InnerException is PostgresException error)
        {
            return error;
        }
    }

    private async Task<PostgresException?> Capture(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(world.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            Assert.Fail("The database accepted the row.");
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }

    /// <summary>One import of a run: its folder and what the Archiver logged.</summary>
    private sealed record ModRun(string Root, LogSink Log)
    {
        public bool Failed => Log.Entries.Any(entry => entry.Level == LogLevel.Critical);

        public Exception? Failure => Log.Entries.LastOrDefault(entry => entry.Level == LogLevel.Critical).Exception;
    }
}
