using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reqnroll;
using SptArchive.Archiver;
using SptArchive.Core;
using SptArchive.Core.Entities;
using static SptArchive.Tests.Specs.OfficialWorld;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/archive-official-data.feature</c>. Every scenario runs the real
/// <see cref="ArchiveJob"/> with the five real importers over a server folder that it writes, against a
/// database of its own, and reads the rows back. A scenario that needs an earlier state (a previous dataset, an
/// earlier release) makes it with a real run first, in the step that names it.
/// </summary>
[Binding]
[Scope(Feature = "Archive the official server data")]
internal sealed class OfficialDataSteps(OfficialWorld world)
{
    private const string ModGuid = "com.example.thermal";
    private const string AddedClothingId = "888888888888888888888888";

    private JsonNode? _expected;
    private string? _localeKey;
    private string? _localeValue;
    private string? _previousSnapshot;
    private readonly Dictionary<string, string> _previousTables = new(StringComparer.Ordinal);
    private readonly List<string> _languages = [];
    private string? _unrelatedFolder;
    private JsonArray _assortItems = [];
    private JsonObject _barter = [];
    private JsonObject _loyalty = [];
    private long _runsBefore;
    private string? _modRun;

    [AfterScenario]
    public async Task DropDatabase() => await world.DisposeAsync();

    // ---- Background ----------------------------------------------------------------------------

    // The copy sits in a temporary folder that the scenario writes. Every run of the Archiver (world.RunAsync and the
    // import of a mod dataset) holds the folders it reads read-only, and the scenario fails when the run changes one.
    [Given("""^a copy of the server "SPT_Data/database" folder that the Archiver can only read$""")]
    public void GivenACopyOfTheServerFolder() => world.WriteDefaultSource();

    [Given("a migrated archive database")]
    public async Task GivenAMigratedArchiveDatabase() => await world.CreateDatabaseAsync();

    // ---- Items ---------------------------------------------------------------------------------

    [Given("the file {string} has an item {string} with \"Weight\" {double}, \"Width\" {int} and \"Height\" {int}")]
    public void GivenAnItem(string file, string id, double weight, int width, int height)
    {
        // The object holds more than the three promoted values, so that "complete" means something.
        var props = new JsonObject
        {
            ["Weight"] = weight,
            ["Width"] = width,
            ["Height"] = height,
            ["Name"] = "item_name",
            ["Slots"] = new JsonArray(new JsonObject { ["_name"] = "mod_scope", ["_required"] = false }),
            ["CanSellOnRagfair"] = true,
        };
        _expected = props.DeepClone();

        var items = world.Load(file);
        items[id] = Template(id, "Item A", Ids.Node, "Item", props);
        world.Save(file, items);
    }

    [Given("the file {string} has an entry with {string} {string}")]
    public void GivenAnEntryWithTheType(string file, string field, string value)
    {
        Assert.Equal("_type", field);

        var items = world.Load(file);
        items[Ids.ClothingNode] = Template(Ids.ClothingNode, "Category", "", value, new JsonObject());
        world.Save(file, items);
    }

    [Then("the row of that entry has the type {string}")]
    public async Task ThenTheRowOfThatEntryHasTheType(string type)
    {
        await using var db = world.Open();
        var item = await db.Items.SingleAsync(row => row.Id == Ids.ClothingNode, TestContext.Current.CancellationToken);

        Assert.Equal(type, item.Type);
    }

    [Then("the table {string} has a row with the id {string}")]
    public async Task ThenTheTableHasARowWithTheId(string table, string id)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        world.Row = table switch
        {
            "items" => await db.Items.SingleAsync(row => row.Id == id && row.SptVersion == world.Release, token),
            "customizations" => await db.Customizations.SingleAsync(row => row.Id == id && row.SptVersion == world.Release, token),
            _ => throw new ArgumentException($"The steps do not know the table '{table}'."),
        };
    }

    [Then("that row has the weight {double}, the width {int} and the height {int}")]
    public void ThenThatRowHasTheWeight(double weight, int width, int height)
    {
        var item = Assert.IsType<Item>(world.Row);

        Assert.Equal(weight, item.Weight);
        Assert.Equal(width, item.Width);
        Assert.Equal(height, item.Height);
    }

    [Then("the {string} column holds the complete {string} object of the item")]
    public void ThenThePropsColumnHoldsTheCompleteObject(string column, string property)
    {
        Assert.Equal("props", column);
        Assert.Equal("_props", property);

        AssertSameJson(_expected!, Assert.IsType<Item>(world.Row).Props);
    }

    // ---- Customization -------------------------------------------------------------------------

    [Given("the file {string} has an entry {string} with the body part {string} and the sides {string} and {string}")]
    public void GivenAClothingEntry(string file, string id, string bodyPart, string side, string otherSide)
    {
        var props = new JsonObject
        {
            ["Name"] = "Head A",
            ["BodyPart"] = bodyPart,
            ["Side"] = new JsonArray(side, otherSide),
            ["AvailableAsDefault"] = false,
            ["Prefab"] = new JsonObject { ["path"] = "assets/content/characters/head_a.bundle", ["rcid"] = "" },
            ["WatchPrefab"] = new JsonObject { ["path"] = "", ["rcid"] = "" },
        };
        _expected = props.DeepClone();

        var clothes = world.Load(file);
        clothes[id] = Template(id, "Head A", Ids.ClothingNode, "Item", props);
        world.Save(file, clothes);
    }

    [Then("that row has the body part {string} and the sides {string} and {string}")]
    public void ThenThatRowHasTheBodyPart(string bodyPart, string side, string otherSide)
    {
        var clothing = Assert.IsType<Customization>(world.Row);

        Assert.Equal(bodyPart, clothing.BodyPart);
        Assert.Equal([side, otherSide], clothing.Sides);
    }

    [Then("the {string} column holds the complete {string} object of the entry")]
    public void ThenThePropsColumnHoldsTheCompleteObjectOfTheEntry(string column, string property)
    {
        Assert.Equal("props", column);
        Assert.Equal("_props", property);

        AssertSameJson(_expected!, Assert.IsType<Customization>(world.Row).Props);
    }

    [Given("the archive holds the release {string}")]
    public async Task GivenTheArchiveHoldsTheRelease(string release) => await ImportOlderReleaseAsync(release);

    [When("the Archiver imports the release {string}")]
    public async Task WhenTheArchiverImportsTheRelease(string release) => await world.RunAsync(release);

    [Then("the table {string} has rows for {string}")]
    public async Task ThenTheTableHasRowsFor(string table, string release) =>
        Assert.True(await world.CountAsync(table, release) > 0, $"'{table}' holds no row of {release}.");

    [Then("the rows of {string} are unchanged")]
    public async Task ThenTheRowsOfAreUnchanged(string release) =>
        Assert.Equal(_previousSnapshot, await world.SnapshotAsync(release));

    // ---- A mod that adds clothes ---------------------------------------------------------------

    [Given("a mod dataset whose export adds one entry to {string}")]
    public async Task GivenAModDatasetThatAddsAnEntry(string table)
    {
        Assert.Equal("customization", table);

        // The archive holds the official data of a release before it holds a run on it (ADR-0013), so the
        // baseline is imported as the official data first. The run is a copy of it that loaded one mod.
        await world.RunAsync();
        Assert.False(world.Failed, "The official import of the baseline failed.");

        _modRun = Path.Combine(world.Root, "run");
        CopyDirectory(world.Source, _modRun);

        var mod = Mod(ModGuid, "3.4.0");
        WriteIn(_modRun, "run/loaded.json", JsonSerializer.Serialize(new object[] { Mod(ExporterGuid, "1.5.0"), mod }));
        WriteIn(_modRun, "run/files/thermal.json", JsonSerializer.Serialize(new[]
        {
            new { path = "Thermal.dll", size = 10, sha256 = new string('a', 64), kind = "assembly" },
        }));

        var clothes = JsonNode.Parse(File.ReadAllText(Path.Combine(_modRun, "templates", "customization.json")))!.AsObject();
        clothes[AddedClothingId] = Template(AddedClothingId, "Thermal Hoodie", Ids.ClothingNode, "Item", new JsonObject
        {
            ["Name"] = "Thermal Hoodie",
            ["BodyPart"] = "Body",
            ["Side"] = new JsonArray("Usec"),
            ["Prefab"] = new JsonObject { ["path"] = "assets/thermal/hoodie.bundle", ["rcid"] = "" },
        });
        WriteIn(_modRun, "templates/customization.json", clothes.ToJsonString());
    }

    [When("the Archiver imports the dataset")]
    public async Task WhenTheArchiverImportsTheDataset()
    {
        var options = new ArchiveOptions
        {
            RunPath = _modRun,
            BaselinePath = world.Source,
            RunModGuid = ModGuid,
            ApplyMigrations = false,
        };

        using (ReadOnlySource.Hold(_modRun!, world.Source))
        {
            await ArchiverHarness.RunAsync(world.ConnectionString, options, [], world.Cache, world.Log.For<ArchiveJob>(), TestContext.Current.CancellationToken);
        }

        Assert.False(world.Failed, "The import of the mod run failed: " + world.Failure);
    }

    [Then("""^the table "(.*)" has the new row, found through the table "(.*)" \(ADR-0034\) and not by the mod id$""")]
    public async Task ThenTheTableHasTheNewRow(string table, string mapTable)
    {
        Assert.Equal("mod_customizations", table);
        Assert.Equal("mod_id_maps", mapTable);

        // The archive keys a mod row by the address it derived for the object, and the id map keeps the id that
        // the run gave it (ADR-0034). The new row is found through that map.
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;
        var map = await db.ModIdMaps.SingleAsync(row => row.RawId == AddedClothingId, token);
        var row = await db.ModCustomizations.SingleAsync(clothing => clothing.Id == map.Address, token);

        Assert.Equal("Thermal Hoodie", row.Name);
        Assert.Equal(1, await db.ModCustomizations.CountAsync(token));
        Assert.NotEqual(AddedClothingId, map.Address);
        Assert.False(await db.ModCustomizations.AnyAsync(clothing => clothing.Id == AddedClothingId, token), "A row is keyed by the mod id.");

        _expected = JsonValue.Create(map.Address);
    }

    [Then("the table {string} has a template row of the kind {string}")]
    public async Task ThenTheTableHasATemplateRowOfTheKind(string table, string kind)
    {
        Assert.Equal("mod_templates", table);

        await using var db = world.Open();
        var row = await db.ModTemplates.SingleAsync(template => template.Kind == kind, TestContext.Current.CancellationToken);

        Assert.Equal(_expected!.GetValue<string>(), row.TemplateId);
        Assert.Equal("added", row.Op);
    }

    // ---- Locales -------------------------------------------------------------------------------

    [Given("the file {string} has the key {string}")]
    public void GivenTheFileHasTheKey(string file, string key)
    {
        _localeKey = key;
        world.Write(file, new JsonObject { [key] = "a value" }.ToJsonString());
    }

    [Then(@"^the locale entry has the entity id (\S+)$")]
    public async Task ThenTheLocaleEntryHasTheEntityId(string entity)
    {
        var entry = await LocaleEntryAsync();

        Assert.Equal(entity == "null" ? null : entity, entry.EntityId);
    }

    [Then(@"^the locale entry has the field (\S+)$")]
    public async Task ThenTheLocaleEntryHasTheField(string field)
    {
        var entry = await LocaleEntryAsync();

        Assert.Equal(field == "null" ? null : field, entry.Field);
    }

    private async Task<LocaleEntry> LocaleEntryAsync()
    {
        await using var db = world.Open();

        return await db.LocaleEntries.SingleAsync(
            entry => entry.SptVersion == world.Release && entry.Key == _localeKey,
            TestContext.Current.CancellationToken);
    }

    [Given("the file {string} lists the languages {string} and {string}")]
    public void GivenTheFileListsTheLanguages(string file, string first, string second)
    {
        _languages.AddRange([first, second]);
        world.Write(file, new JsonObject { [first] = "Language " + first, [second] = "Language " + second }.ToJsonString());
    }

    [Given("the folder {string} has a file for each of them")]
    public void GivenTheFolderHasAFileForEach(string folder)
    {
        foreach (var code in _languages)
        {
            world.Write($"{folder}/{code}.json", new JsonObject { ["greeting"] = "hello in " + code }.ToJsonString());
        }
    }

    [Then("the table {string} has the codes {string} and {string}")]
    public async Task ThenTheTableHasTheCodes(string table, string first, string second)
    {
        Assert.Equal("languages", table);

        await using var db = world.Open();
        var codes = await db.Languages.Select(language => language.Code).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(first, codes);
        Assert.Contains(second, codes);
    }

    [Then("the table {string} has entries for both languages")]
    public async Task ThenTheTableHasEntriesForBothLanguages(string table)
    {
        Assert.Equal("locale_entries", table);

        await using var db = world.Open();

        foreach (var code in _languages)
        {
            Assert.True(
                await db.LocaleEntries.AnyAsync(entry => entry.SptVersion == world.Release && entry.LanguageCode == code, TestContext.Current.CancellationToken),
                $"No locale entry for the language '{code}'.");
        }
    }

    [Given("the file {string} lists the language {string} besides {string}")]
    public void GivenTheFileListsTheLanguage(string file, string code, string listed)
    {
        // The listed language has a file (the default source writes en.json), so its entries prove that the run
        // went on past the language without a file.
        Assert.True(File.Exists(Path.Combine(world.Source, "locales", "global", listed + ".json")), $"The source holds no file for '{listed}'.");

        world.Write(file, new JsonObject { [listed] = "Language " + listed, [code] = "Language " + code }.ToJsonString());
    }

    [Given("the folder {string} has no file {string}")]
    public void GivenTheFolderHasNoFile(string folder, string file)
    {
        var path = Path.Combine(world.Source, folder.Replace('/', Path.DirectorySeparatorChar), file);

        Assert.False(File.Exists(path), $"The source holds '{file}', which the scenario says it does not.");
    }

    [Then("the table {string} has no entry for {string}")]
    public async Task ThenTheTableHasNoEntryFor(string table, string code)
    {
        Assert.Equal("locale_entries", table);

        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        Assert.False(await db.LocaleEntries.AnyAsync(entry => entry.LanguageCode == code, token));
        Assert.True(await db.LocaleEntries.AnyAsync(entry => entry.LanguageCode == "en", token), "The run imported no listed language.");
    }

    [Given("a locale file has the same key twice with different values")]
    public void GivenALocaleFileHasTheSameKeyTwice()
    {
        _localeKey = "Duplicated/Key";
        _localeValue = "first value";

        // A JSON object cannot hold the key twice in a serializer, so the text is written by hand.
        world.Write("locales/global/en.json", """{ "Duplicated/Key": "first value", "Duplicated/Key": "second value" }""");
    }

    [Then("the table {string} has one entry for that key")]
    public async Task ThenTheTableHasOneEntryForThatKey(string table)
    {
        Assert.Equal("locale_entries", table);

        await using var db = world.Open();
        var entries = await db.LocaleEntries.Where(entry => entry.Key == _localeKey).ToListAsync(TestContext.Current.CancellationToken);

        world.Row = Assert.Single(entries);
    }

    [Then("its value is the first value")]
    public void ThenItsValueIsTheFirstValue() =>
        Assert.Equal(_localeValue, Assert.IsType<LocaleEntry>(world.Row).Value);

    // ---- Quests --------------------------------------------------------------------------------

    [Given("the file {string} has the quest {string}")]
    public void GivenTheFileHasTheQuest(string file, string id)
    {
        _expected = new JsonObject
        {
            ["QuestName"] = "Debut",
            ["traderId"] = Ids.Trader,
            ["location"] = "5704e554d2720bac5b8b456e",
            ["type"] = "Elimination",
            ["side"] = "Pmc",
            ["restartable"] = false,
            ["conditions"] = new JsonObject
            {
                ["AvailableForFinish"] = new JsonArray(new JsonObject { ["conditionType"] = "CounterCreator", ["value"] = 5 }),
            },
        };

        world.Write(file, new JsonObject { [id] = _expected.DeepClone() }.ToJsonString());
        _localeKey = id;
    }

    [Then("the table {string} has a row for that quest")]
    public async Task ThenTheTableHasARowForThatQuest(string table)
    {
        Assert.Equal("quests", table);

        await using var db = world.Open();
        world.Row = await db.Quests.SingleAsync(quest => quest.Id == _localeKey && quest.SptVersion == world.Release, TestContext.Current.CancellationToken);
    }

    [Then("the row has the quest name, the trader id, the location, the type and the side")]
    public void ThenTheRowHasTheQuestFacts()
    {
        var quest = Assert.IsType<Quest>(world.Row);

        Assert.Equal("Debut", quest.QuestName);
        Assert.Equal(Ids.Trader, quest.TraderId);
        Assert.Equal("5704e554d2720bac5b8b456e", quest.Location);
        Assert.Equal("Elimination", quest.Type);
        Assert.Equal("Pmc", quest.Side);
    }

    [Then("the {string} column holds the complete quest object, including its conditions")]
    public void ThenTheDataColumnHoldsTheQuest(string column)
    {
        Assert.Equal("data", column);

        var data = Assert.IsType<Quest>(world.Row).Data;

        AssertSameJson(_expected!, data);
        Assert.Contains("CounterCreator", data, StringComparison.Ordinal);
    }

    // ---- Traders -------------------------------------------------------------------------------

    [Given("the trader folder {string} has the files {string} and {string}")]
    public void GivenTheTraderFolderHasTheFiles(string trader, string baseFile, string assortFile)
    {
        _localeKey = trader;
        _expected = new JsonObject
        {
            ["name"] = "Skier",
            ["nickname"] = "S",
            ["currency"] = "RUB",
            ["location"] = "bigmap",
            ["insurance"] = new JsonObject { ["availability"] = true, ["min_payment"] = 1000 },
        };

        world.Write($"traders/{trader}/{baseFile}", _expected.ToJsonString());
        world.Write($"traders/{trader}/{assortFile}", """{ "items": [], "barter_scheme": {}, "loyal_level_items": {} }""");
    }

    [Then("the table {string} has a row for that trader")]
    public async Task ThenTheTableHasARowForThatTrader(string table)
    {
        Assert.Equal("traders", table);

        await using var db = world.Open();
        world.Row = await db.Traders.SingleAsync(trader => trader.Id == _localeKey && trader.SptVersion == world.Release, TestContext.Current.CancellationToken);
    }

    [Then("the {string} column holds the complete content of {string}")]
    public void ThenTheBaseColumnHoldsTheContent(string column, string file)
    {
        Assert.Equal("base", column);
        Assert.Equal("base.json", file);

        AssertSameJson(_expected!, Assert.IsType<Trader>(world.Row).Base);
    }

    [Given("a trader folder has no {string}")]
    public void GivenATraderFolderHasNoBase(string file)
    {
        Assert.Equal("base.json", file);

        // The folder holds an assort file, so that skipping it is a decision and not an empty folder.
        _unrelatedFolder = "6000000000000000000000aa";
        world.Write($"traders/{_unrelatedFolder}/assort.json", """{ "items": [ { "_id": "x1", "_tpl": "t", "parentId": "hideout", "slotId": "hideout" } ] }""");
    }

    [Then("the table {string} has no row for that folder")]
    public async Task ThenTheTableHasNoRowForThatFolder(string table)
    {
        Assert.Equal("traders", table);

        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        Assert.False(await db.Traders.AnyAsync(trader => trader.Id == _unrelatedFolder, token));
        Assert.False(await db.TraderAssortItems.AnyAsync(row => row.TraderId == _unrelatedFolder, token));
        Assert.True(await db.Traders.AnyAsync(trader => trader.Id == Ids.Trader, token), "The run imported no trader at all.");
    }

    // ---- Offers --------------------------------------------------------------------------------

    [Given("the trader assort has a root item {string} with a barter scheme of {int} of the item {string} and the loyalty level {int}")]
    public void GivenTheAssortHasARootItem(string root, int count, string payment, int level)
    {
        // The shape of the server: a list of alternatives, each a list of payments of a count and an item template.
        AddRoot(root);
        _barter[root] = new JsonArray(new JsonArray(new JsonObject { ["count"] = count, ["_tpl"] = payment }));
        _loyalty[root] = level;
        WriteAssort();
    }

    [Given("the trader assort has a child item {string} with the parent {string} and the slot {string}")]
    public void GivenTheAssortHasAChildItem(string child, string parent, string slot)
    {
        AddRoot(parent);
        _assortItems.Add(new JsonObject { ["_id"] = child, ["_tpl"] = Ids.Item, ["parentId"] = parent, ["slotId"] = slot });
        WriteAssort();
    }

    private void AddRoot(string root)
    {
        if (_assortItems.Any(item => item!["_id"]!.GetValue<string>() == root))
        {
            return;
        }

        _assortItems.Add(new JsonObject { ["_id"] = root, ["_tpl"] = Ids.Item, ["parentId"] = "hideout", ["slotId"] = "hideout" });
    }

    private void WriteAssort() =>
        world.Write($"traders/{Ids.Trader}/assort.json", new JsonObject
        {
            ["items"] = _assortItems.DeepClone(),
            ["barter_scheme"] = _barter.DeepClone(),
            ["loyal_level_items"] = _loyalty.DeepClone(),
        }.ToJsonString());

    [Then("the table {string} has a row for {string}")]
    public async Task ThenTheTableHasARowFor(string table, string key)
    {
        await using var db = world.Open();
        var token = TestContext.Current.CancellationToken;

        switch (table)
        {
            case "trader_assort_items":
                world.Row = await db.TraderAssortItems.SingleAsync(row => row.InstanceId == key && row.SptVersion == world.Release, token);
                break;
            case "spt_versions":
                Assert.True(await db.SptVersions.AnyAsync(row => row.Version == key, token), $"'spt_versions' holds no row for {key}.");
                break;
            default:
                throw new ArgumentException($"The steps do not know the table '{table}'.");
        }
    }

    [Then("that row has a barter scheme of {int} of the item {string} and the loyalty level {int}")]
    public void ThenThatRowHasTheBarterScheme(int count, string payment, int level)
    {
        var row = Assert.IsType<TraderAssortItem>(world.Row);
        var scheme = JsonNode.Parse(row.BarterScheme!)!.AsArray();

        Assert.Single(scheme);
        Assert.Single(scheme[0]!.AsArray());
        Assert.Equal(count, scheme[0]![0]!["count"]!.GetValue<int>());
        Assert.Equal(payment, scheme[0]![0]!["_tpl"]!.GetValue<string>());
        Assert.Equal(level, row.LoyaltyLevel);
    }

    [Then("the row for {string} has the parent {string} and the slot {string}")]
    public async Task ThenTheRowForHasTheParent(string child, string parent, string slot)
    {
        await using var db = world.Open();
        var row = await db.TraderAssortItems.SingleAsync(item => item.InstanceId == child && item.SptVersion == world.Release, TestContext.Current.CancellationToken);

        Assert.Equal(parent, row.ParentId);
        Assert.Equal(slot, row.SlotId);
    }

    [Then("the row for {string} has no loyalty level")]
    public async Task ThenTheRowForHasNoLoyaltyLevel(string child)
    {
        await using var db = world.Open();
        var row = await db.TraderAssortItems.SingleAsync(item => item.InstanceId == child && item.SptVersion == world.Release, TestContext.Current.CancellationToken);

        Assert.Null(row.LoyaltyLevel);
    }

    [Given("the file {string} of a trader maps the state {string} and the offer {string} to the quest {string}")]
    public void GivenTheQuestAssortMaps(string file, string state, string offer, string quest) =>
        world.Write($"traders/{Ids.Trader}/{file}", new JsonObject { [state] = new JsonObject { [offer] = quest } }.ToJsonString());

    [Then("the table {string} has a row with the status {string}, the offer {string} and the quest {string}")]
    public async Task ThenTheTableHasAQuestAssortRow(string table, string status, string offer, string quest)
    {
        Assert.Equal("trader_quest_assorts", table);

        await using var db = world.Open();
        var row = await db.TraderQuestAssorts.SingleAsync(unlock => unlock.SptVersion == world.Release, TestContext.Current.CancellationToken);

        Assert.Equal(status, row.Status);
        Assert.Equal(offer, row.AssortInstanceId);
        Assert.Equal(quest, row.QuestId);
    }

    // ---- Running the Archiver ------------------------------------------------------------------

    [When("the Archiver runs")]
    public async Task WhenTheArchiverRuns()
    {
        _runsBefore = await RunCountAsync();
        await world.RunAsync();
    }

    [When("the Archiver runs and the run succeeds")]
    public async Task WhenTheArchiverRunsAndSucceeds()
    {
        await world.RunAsync();

        Assert.False(world.Failed, "The run failed: " + world.Failure);
    }

    [When("the Archiver runs with {string} set to true")]
    public async Task WhenTheArchiverRunsWithForce(string option)
    {
        Assert.Equal("Archive:Force", option);

        world.Force = true;
        _runsBefore = await RunCountAsync();
        await world.RunAsync();
    }

    [Then("the run succeeds")]
    public void ThenTheRunSucceeds() => Assert.False(world.Failed, "The run failed: " + world.Failure);

    [Then("the run fails")]
    public void ThenTheRunFails() => Assert.True(world.Failed, "The run did not fail.");

    private async Task<long> RunCountAsync() => await world.ScalarAsync<long>("select count(*) from archive_runs");

    // ---- A Git LFS pointer ---------------------------------------------------------------------

    [Given("the file {string} is a Git LFS pointer")]
    public async Task GivenTheFileIsAGitLfsPointer(string file)
    {
        await ArchiveThePreviousDatasetAsync();

        world.Write(file, "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 41822318\n");
    }

    [Then("the error message asks for {string}")]
    public void ThenTheErrorMessageAsksFor(string text) =>
        Assert.Contains(text, world.Failure!.Message, StringComparison.Ordinal);

    [Then("the table {string} keeps its previous rows")]
    public async Task ThenTheTableKeepsItsPreviousRows(string table)
    {
        var now = await world.SnapshotTableAsync(table, world.Release);

        Assert.Equal(_previousTables[table], now);
        Assert.False(now.EndsWith(":0", StringComparison.Ordinal), $"'{table}' held no row before the run, so the check proves nothing.");
    }

    // ---- Run control ---------------------------------------------------------------------------

    [Given("a succeeded run exists with the hash of the current source files")]
    public async Task GivenASucceededRunExists()
    {
        await world.RunAsync();
        Assert.False(world.Failed, "The first run failed: " + world.Failure);

        await using var db = world.Open();
        var run = await db.ArchiveRuns.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveRun.Succeeded, run.Status);
        Assert.Equal(64, run.SourceHash.Length);
    }

    [Then("it imports nothing")]
    public async Task ThenItImportsNothing()
    {
        Assert.All(world.Importers, importer => Assert.Equal(0, importer.Calls));
        Assert.Equal(_runsBefore, await RunCountAsync());
        Assert.Contains(world.Log.Entries, entry => entry.Message.Contains("nothing to do", StringComparison.Ordinal));
    }

    // The exit code is not read: Environment.ExitCode is process-global, and scenarios run in parallel. The job sets it to
    // 1 where it logs the critical entry, so the log and the run record say what the code would.
    [Then("""^the run reports success: the log holds no critical entry and the table "(.*)" holds no failed run$""")]
    public async Task ThenTheRunReportsSuccess(string table)
    {
        Assert.Equal("archive_runs", table);
        Assert.False(world.Failed, "The job logged a critical failure: " + world.Failure);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.Level >= LogLevel.Error);

        await using var db = world.Open();
        var statuses = await db.ArchiveRuns.Select(run => run.Status).ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(statuses);
        Assert.All(statuses, status => Assert.Equal(ArchiveRun.Succeeded, status));
    }

    [Then("it imports all data again")]
    public async Task ThenItImportsAllDataAgain()
    {
        Assert.False(world.Failed, "The forced run failed: " + world.Failure);
        Assert.Equal(["locales", "items", "customization", "quests", "traders"], world.Importers.Select(importer => importer.Name));
        Assert.All(world.Importers, importer => Assert.Equal(1, importer.Calls));

        await using var db = world.Open();
        var runs = await db.ArchiveRuns.Where(run => run.Status == ArchiveRun.Succeeded).CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_runsBefore + 1, runs);
    }

    [Given("the official tables hold a previous dataset")]
    public async Task GivenTheOfficialTablesHoldAPreviousDataset() => await ArchiveThePreviousDatasetAsync();

    [Given("a source file is not valid JSON")]
    public void GivenASourceFileIsNotValidJson() =>
        // The quests come after the items in the order of the importers, so the items are already replaced
        // inside the transaction when the run stops. Only the rollback brings the previous rows back.
        world.Write("templates/quests.json", "{ \"555555555555555555555555\": { not json");

    [Then("the official tables hold the previous dataset")]
    public async Task ThenTheOfficialTablesHoldThePreviousDataset()
    {
        foreach (var table in OfficialTables)
        {
            Assert.Equal(_previousTables[table], await world.SnapshotTableAsync(table, world.Release));
        }

        Assert.Equal(_previousSnapshot, await world.SnapshotAsync(world.Release));
    }

    [Then("the run has the status {string} and an error message")]
    public async Task ThenTheRunHasTheStatus(string status)
    {
        await using var db = world.Open();
        var run = await db.ArchiveRuns.OrderByDescending(row => row.StartedAt).FirstAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, run.Status);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
    }

    [Then("the table {string} has a row with the status {string} and a finish time")]
    public async Task ThenTheRunIsRecorded(string table, string status)
    {
        Assert.Equal("archive_runs", table);

        await using var db = world.Open();
        var run = await db.ArchiveRuns.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, run.Status);
        Assert.NotNull(run.FinishedAt);

        world.Row = run;
    }

    [Then("the cache key {string} holds the id of that run")]
    public void ThenTheCacheKeyHoldsTheId(string key)
    {
        var run = Assert.IsType<ArchiveRun>(world.Row);

        Assert.Equal(CacheKeys.DataVersion, key);
        Assert.Equal(run.Id, Guid.Parse(world.Cache.Text(key)!));
    }

    [Given("the cache is not reachable")]
    public void GivenTheCacheIsNotReachable() => world.Cache.Down = true;

    [Then("the Archiver logs a warning")]
    public void ThenTheArchiverLogsAWarning() =>
        Assert.Contains(
            world.Log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Could not publish the new dataset version", StringComparison.Ordinal));

    // ---- Server version ------------------------------------------------------------------------

    [Given("the server reports the version {string}")]
    public void GivenTheServerReportsTheVersion(string version) => world.UseRelease(version);

    [Then("every official row of that run states {string}")]
    public async Task ThenEveryOfficialRowStates(string version)
    {
        var tables = new List<string>();

        await using (var db = world.Open())
        {
            tables = await db.Database
                .SqlQueryRaw<string>("select table_name as \"Value\" from information_schema.columns where table_schema = 'public' and column_name = 'spt_version'")
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        // The tables that carry the release are found in the schema, so a table that a later importer adds is checked too.
        Assert.All(OfficialTables, table => Assert.Contains(table, tables));

        foreach (var table in tables)
        {
            var others = await world.ScalarAsync<long>($"select count(*) from \"{table}\" where spt_version is distinct from @v", ("v", version));

            Assert.True(others == 0, $"'{table}' holds {others} row(s) that do not state {version}.");
        }

        foreach (var table in OfficialTables)
        {
            Assert.True(await world.CountAsync(table, version) > 0, $"'{table}' holds no row of {version}.");
        }
    }

    [Given("the archive holds the official data of {string}")]
    public async Task GivenTheArchiveHoldsTheOfficialData(string release) => await ImportOlderReleaseAsync(release);

    [When("the Archiver imports the server version {string}")]
    public async Task WhenTheArchiverImportsTheServerVersion(string release) => await world.RunAsync(release);

    [Then("the archive holds the official data of both versions")]
    public async Task ThenTheArchiveHoldsBothVersions()
    {
        await using var db = world.Open();
        var versions = await db.SptVersions.Select(row => row.Version).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, versions.Count);

        foreach (var version in versions)
        {
            foreach (var table in OfficialTables)
            {
                Assert.True(await world.CountAsync(table, version) > 0, $"'{table}' holds no row of {version}.");
            }
        }
    }

    [Then("no row of {string} is changed or deleted")]
    public async Task ThenNoRowOfIsChanged(string release) =>
        Assert.Equal(_previousSnapshot, await world.SnapshotAsync(release));

    // ---- An official source (ADR-0035) ---------------------------------------------------------

    [Given("a source that loaded the mod {string} version {string}")]
    public async Task GivenASourceThatLoadedAMod(string modGuid, string version)
    {
        // The release that the scenario imports holds rows already, and the refused run must leave them.
        world.UseRelease("4.1.6");
        await ArchiveThePreviousDatasetAsync();

        world.WriteLoadedMods(Mod(modGuid, version));
    }

    [When("the Archiver imports it as official data of {string}")]
    public async Task WhenTheArchiverImportsItAsOfficialData(string release) => await world.RunAsync(release);

    [Then("the message names {string}")]
    public void ThenTheMessageNames(string text) =>
        Assert.Contains(text, world.Failure!.Message, StringComparison.Ordinal);

    [Given("a source without {string}")]
    public void GivenASourceWithout(string file)
    {
        var path = Path.Combine(world.Source, file.Replace('/', Path.DirectorySeparatorChar));

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Then("the log warns that nothing proves the source loaded no mod")]
    public void ThenTheLogWarns() =>
        Assert.Contains(
            world.Log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("nothing proves it loaded no mod", StringComparison.Ordinal));

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>A real run of the current source, and the rows it left, for a scenario that says what must stay.</summary>
    private async Task ArchiveThePreviousDatasetAsync()
    {
        await world.RunAsync();
        Assert.False(world.Failed, "The run that makes the previous dataset failed: " + world.Failure);

        _previousSnapshot = await world.SnapshotAsync(world.Release);

        foreach (var table in OfficialTables)
        {
            _previousTables[table] = await world.SnapshotTableAsync(table, world.Release);
        }
    }

    /// <summary>
    /// The archive holds an older release: a real run of data that differs from the data of the newer one
    /// (a lighter item, another name for the clothes), so that a newer import which touched the old rows
    /// would change them. The source goes back to the current data afterwards.
    /// </summary>
    private async Task ImportOlderReleaseAsync(string release)
    {
        var newer = world.Release;

        world.UseRelease(release);
        world.WriteDefaultSource(itemWeight: 0.4, clothingName: "Body A (older)");
        await ArchiveThePreviousDatasetAsync();

        world.UseRelease(newer);
        world.WriteDefaultSource();
    }

    private static void AssertSameJson(JsonNode expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(expected, JsonNode.Parse(actual)),
            $"The column does not hold the complete object.\nExpected: {expected.ToJsonString()}\nActual:   {actual}");

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
