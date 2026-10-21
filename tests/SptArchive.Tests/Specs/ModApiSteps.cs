using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reqnroll;
using SptArchive.Core.Canonical;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/mod-api.feature</c>. They write rows through the harness of the other tests
/// (<see cref="ArchiveDatabase"/>) and read them back through the schema that the Api serves
/// (<see cref="ApiHarness"/>), so a scenario checks what a client of the Api sees. Reqnroll makes one
/// instance of this class for each scenario, so its fields are the state of one scenario. The scenario
/// writes inside one transaction that it rolls back, so the archive is never left with test data.
/// </summary>
[Binding]
public sealed class ModApiSteps
{
    // The server release that the mod ran on (scenario "An observation states the release that a mod version ran on").
    private const string Release = "4.1.6";

    private const string ConfigPath = "com.example.mod/config/config.json";
    private const string ConfigText = """{"Loot":2}""";

    // A year no real run reaches, so the runs of a scenario are the newest rows even if another test
    // committed a run to the same database.
    private static readonly DateTimeOffset Start = new(2999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The fields that a client reads from each Api field. A paged field answers its rows under "nodes".
    private static readonly Dictionary<string, string> Selections = new()
    {
        ["modDatasets"] = "id modGuid contentHash configOrigin rowCounts tableHashes createdByRunId",
        ["modDataset"] = "id modGuid contentHash configOrigin rowCounts tableHashes createdByRunId",
        ["modTemplates"] = "kind templateId op parentId",
        ["modChangePaths"] = "kind templateId path op oldValue newValue",
        ["modFiles"] = "path kind size sha256 shippedSha256 contentText content",
        ["modFile"] = "path kind size sha256 shippedSha256 contentText content",
        ["modIdMaps"] = "kind rawId address pinned",
        ["modDataObservations"] = "sptVersion outcome kind datasetId",
        ["archiveRuns"] = "id kind purpose status sptVersion result startedAt targetModGuid targetVersion",
        ["archiveRun"] = "id kind purpose status sptVersion result startedAt targetModGuid targetVersion baselineRelease",
        ["modDatabaseObjects"] = "datasetId kind templateId part bytes",
    };

    private readonly Dictionary<string, Guid> _datasets = [];
    private readonly Dictionary<string, ArchiveRun> _runs = [];
    private readonly Dictionary<string, string> _addresses = [];
    private readonly Dictionary<string, ModFile> _files = [];
    private readonly Dictionary<string, JsonElement> _responses = [];

    private TestScope? _scope;
    private int? _maxDocumentBytes;
    private ArchiveRun? _runOfD1;
    private ArchiveRun? _officialRun;
    private string _lastField = "";

    [AfterScenario]
    public async Task RollBack()
    {
        if (_scope is not null)
        {
            await _scope.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------------------ The archive

    [Given("the archive holds the mod dataset {string} of the mod {string}")]
    public async Task GivenTheArchiveHoldsTheModDatasetOfTheMod(string label, string modGuid)
    {
        var scope = await ScopeAsync();
        var ct = TestContext.Current.CancellationToken;
        await SptVersionRegistry.EnsureAsync(scope.Db, Release, ct);

        // A mod dataset is the content of one run (ADR-0033), and that run waits for the official data of its
        // release (ADR-0035), so the archive holds the official run too.
        _officialRun = new ArchiveRun
        {
            StartedAt = Start,
            FinishedAt = Start.AddMinutes(1),
            SourceHash = "sha256:official-source",
            SptVersion = Release,
            Status = ArchiveRun.Succeeded,
            Kind = ArchiveRunKind.Official,
            Purpose = "the official import of the release",
            Result = """{"items":1}""",
        };

        _runOfD1 = ModRun(Start.AddHours(1), modGuid, "1.0.0");

        var mod = new Mod { ModGuid = modGuid, Name = "Example", Version = "1.0.0" };
        var dataset = NewDataset(label, modGuid, """{"items":"sha256:items"}""", """{"items":2}""", _runOfD1.Id);

        scope.Db.ArchiveRuns.AddRange(_officialRun, _runOfD1);
        scope.Db.Mods.Add(mod);
        scope.Db.ModDatasets.Add(dataset);
        await scope.Db.SaveChangesAsync(ct);

        // The dataset holds the configuration that the mod read, and the observation states the release
        // that the mod version ran on (ADR-0013, ADR-0029).
        var config = new ModFile
        {
            DatasetId = dataset.Id,
            Path = ConfigPath,
            Kind = ModFileKind.Config,
            Size = ConfigText.Length,
            Sha256 = Sha256("config"),
            ContentText = ConfigText,
            Content = ConfigText,
        };
        _files[config.Path] = config;
        scope.Db.ModFiles.Add(config);

        scope.Db.ModDataObservations.Add(new ModDataObservation
        {
            ModId = mod.Id,
            SptVersion = Release,
            DatasetId = dataset.Id,
            Outcome = ModObservationOutcome.Archived,
            Kind = ModObservationKind.Archive,
            ArchiveRunId = _runOfD1.Id,
            DependencySet = """[{"guid":"com.example.dep","version":"2.0.0"}]""",
            DependencySetHash = "sha256:dependencies",
            ObservedAt = Start.AddHours(1),
        });

        await scope.Db.SaveChangesAsync(ct);
    }

    [Given("the dataset {string} adds the item {string} with the parent {string}")]
    public async Task GivenTheDatasetAddsTheItemWithTheParent(string dataset, string item, string parent)
    {
        var scope = await ScopeAsync();
        var address = Address(item, added: true);

        scope.Db.ModTemplates.Add(Template(dataset, ModTemplateKind.Item, address, ModTemplateOperation.Added, parent));

        // An added item has no old value: the baseline does not hold it (glossary: Added template).
        scope.Db.ModChangePaths.AddRange(
            Change(dataset, address, "/_props/Weight", ModChangePathOperation.Set, null, "1.0"),
            Change(dataset, address, "/_props/Name", ModChangePathOperation.Set, null, "\"Added\""));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the dataset {string} changes the weight of the official item {string} from {double} to {double}")]
    public async Task GivenTheDatasetChangesTheWeightOfTheOfficialItem(string dataset, string item, double from, double to)
    {
        var scope = await ScopeAsync();
        var address = Address(item, added: false);

        scope.Db.ModTemplates.Add(Template(dataset, ModTemplateKind.Item, address, ModTemplateOperation.Modified));
        scope.Db.ModChangePaths.Add(Change(
            dataset, address, "/_props/Weight", ModChangePathOperation.Set, Number(from), Number(to)));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the dataset {string} adds the location {string} with the file {string} holding:")]
    public async Task GivenTheDatasetAddsTheLocation(string dataset, string location, string part, string data)
    {
        var scope = await ScopeAsync();
        var ct = TestContext.Current.CancellationToken;
        var datasetId = _datasets[dataset];

        // ADR-0041: the template says that the object exists, and one row keeps the source of one file.
        scope.Db.ModTemplates.Add(new ModTemplate
        {
            DatasetId = datasetId,
            Kind = ModTemplateKind.Location,
            TemplateId = location,
            Op = ModTemplateOperation.Added,
        });

        scope.Db.ModDatabaseObjects.Add(new ModDatabaseObject
        {
            DatasetId = datasetId,
            Kind = ModTemplateKind.Location,
            TemplateId = location,
            Part = part,
            Data = data,
            Bytes = Encoding.UTF8.GetByteCount(data),
        });

        await scope.Db.SaveChangesAsync(ct);
    }

    [Given("the Api serves at most {int} bytes of one document")]
    public void GivenTheApiServesAtMostBytesOfOneDocument(int bytes) => _maxDocumentBytes = bytes;

    [Given("the dataset {string} holds the file {string}")]
    public async Task GivenTheDatasetHoldsTheFile(string dataset, string path)
    {
        var scope = await ScopeAsync();
        var file = new ModFile
        {
            DatasetId = _datasets[dataset],
            Path = path,
            Kind = ModFileKind.Data,
            Size = 512,
            Sha256 = Sha256(path),
            ShippedSha256 = Sha256(path),
        };
        _files[path] = file;
        scope.Db.ModFiles.Add(file);
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the Api is running")]
    public async Task GivenTheApiIsRunning()
    {
        // The harness builds the schema that the Api serves. A trivial query proves that it builds and answers.
        var response = await ApiHarness.JsonAsync(await ScopeAsync(), "{ __typename }");
        Assert.Equal("Query", response.GetProperty("data").GetProperty("__typename").GetString());
    }

    [Given("the dataset {string} of the behaviour mod {string}")]
    public async Task GivenTheDatasetOfTheBehaviourMod(string label, string modGuid)
    {
        // A behaviour mod changes nothing in a table: its dataset holds no row (glossary: Behaviour mod).
        var scope = await ScopeAsync();
        scope.Db.ModDatasets.Add(NewDataset(label, modGuid, "{}", "{}"));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the dataset {string} adds one offer for the trader {string}")]
    public async Task GivenTheDatasetAddsOneOfferForTheTrader(string label, string trader)
    {
        var scope = await ScopeAsync();
        var dataset = NewDataset(label, "com.example.trader", """{"assort":"sha256:assort"}""", """{"assort":1}""");
        scope.Db.ModDatasets.Add(dataset);

        // An offer is a template of its own kind, named by an address that the archive derived (ADR-0032).
        var address = ContentUlid.Create(ModTemplateKind.Assort, trader, "offer-1");
        scope.Db.ModTemplates.Add(Template(label, ModTemplateKind.Assort, address, ModTemplateOperation.Added));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the dataset {string} removes the property {string} of the item {string}")]
    public async Task GivenTheDatasetRemovesTheProperty(string label, string property, string item)
    {
        var scope = await ScopeAsync();
        scope.Db.ModDatasets.Add(NewDataset(label, "com.example.removed", """{"items":"sha256:items"}""", """{"items":1}"""));

        var address = Address(item, added: false);
        scope.Db.ModTemplates.Add(Template(label, ModTemplateKind.Item, address, ModTemplateOperation.Modified));
        scope.Db.ModChangePaths.Add(Change(label, address, "/" + property, ModChangePathOperation.Remove, "true", null));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the dataset {string} holds the id {string} for the address of {string}")]
    public async Task GivenTheDatasetHoldsTheIdForTheAddressOf(string dataset, string rawId, string item)
    {
        var scope = await ScopeAsync();

        // The mod minted the id for an object that the baseline does not hold, so the address is not pinned (ADR-0034).
        scope.Db.ModIdMaps.Add(new ModIdMap
        {
            DatasetId = _datasets[dataset],
            Kind = ModTemplateKind.Item,
            RawId = rawId,
            Address = Address(item, added: true),
            Pinned = false,
        });
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the run {string} is a mod run for the mod {string} in the version {string}")]
    public async Task GivenTheRunIsAModRunForTheMod(string label, string modGuid, string version)
    {
        var scope = await ScopeAsync();
        var run = ModRun(Start.AddHours(2), modGuid, version);
        _runs[label] = run;
        scope.Db.ArchiveRuns.Add(run);
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------------------- The query

    [When("I query {string} with the filter {word} equals {string}")]
    public Task WhenIQueryWithTheFilter(string field, string column, string value) =>
        QueryAsync(field, $$"""{{column}}: { eq: {{Literal(value)}} }""");

    [When("I query {string} with the id of {string}")]
    public Task WhenIQueryWithTheIdOf(string field, string label)
    {
        var id = field == "archiveRun" ? _runs[label].Id : _datasets[label];
        return QueryByIdAsync(field, id);
    }

    [When("I query {string} with the id of a run")]
    public Task WhenIQueryWithTheIdOfARun(string field) => QueryByIdAsync(field, _runOfD1!.Id);

    [When("I query {string} with an id that the archive does not hold")]
    public Task WhenIQueryWithAnIdThatTheArchiveDoesNotHold(string field) =>
        QueryByIdAsync(field, Guid.Parse("01a15bff-0000-7000-8000-0000000000ff"));

    [When("I query {string} with the dataset id of {string}")]
    public Task WhenIQueryWithTheDatasetIdOf(string field, string dataset) =>
        QueryAsync(field, DatasetFilter(dataset));

    [When("I query {string} with the dataset id of {string} and the kind {string}")]
    public Task WhenIQueryWithTheDatasetIdAndTheKind(string field, string dataset, string kind) =>
        QueryAsync(field, DatasetFilter(dataset) + $$""", kind: { eq: {{Literal(kind)}} }""");

    [When("I query {string} with the dataset id of {string} and the template id {string}")]
    public Task WhenIQueryWithTheDatasetIdAndTheTemplateId(string field, string dataset, string item) =>
        QueryAsync(field, DatasetFilter(dataset) + $$""", templateId: { eq: {{Literal(Address(item))}} }""");

    [When("I query {string} with the dataset id of {string} and the path of its configuration file")]
    public Task WhenIQueryWithTheDatasetIdAndTheConfigurationFile(string field, string dataset) =>
        QueryFileAsync(field, dataset, ConfigPath);

    [When("I query {string} with the dataset id of {string} and a path that the dataset does not hold")]
    public Task WhenIQueryWithTheDatasetIdAndAnAbsentPath(string field, string dataset) =>
        QueryFileAsync(field, dataset, "com.example.mod/db/absent.json");

    [When("I query {string} with the mod {string}")]
    public Task WhenIQueryWithTheMod(string field, string modGuid) =>
        QueryAsync(field, $$"""mod: { modGuid: { eq: {{Literal(modGuid)}} } }""");

    [When("I query {string}")]
    public Task WhenIQuery(string field) => QueryAsync(field, null);

    [When("I ask for the whole document of {string} in the dataset {string}")]
    public Task WhenIAskForTheWholeDocument(string location, string dataset) =>
        RunAsync("modDatabaseObjects", $$"""
            { modDatabaseObjects(first: 10, where: { datasetId: { eq: "{{_datasets[dataset]}}" }, templateId: { eq: {{Literal(location)}} } }) {
                nodes { part bytes rawJson }
              } }
            """);

    [When("I query {string} with the text operation {string} on {string}")]
    public Task WhenIQueryWithTheTextOperation(string field, string operation, string column) =>
        QueryAsync(field, $$"""{{column}}: { {{operation}}: "{" }""");

    [When("I query {string} where {string} equals the table hashes of {string}")]
    public async Task WhenIQueryWhereEqualsTheTableHashesOf(string field, string column, string dataset)
    {
        var scope = await ScopeAsync();
        var tableHashes = (await scope.Db.ModDatasets.FindAsync([_datasets[dataset]], TestContext.Current.CancellationToken))!.TableHashes;

        // The comparison is by the content of the document, so the filter value is the stored JSON text.
        await QueryAsync(field, $$"""{{column}}: { eq: {{Literal(tableHashes)}} }""");
    }

    [When("I read the field {string} of the schema")]
    public async Task WhenIReadTheFieldOfTheSchema(string field)
    {
        _lastField = field;
        _responses["__schema"] = await ApiHarness.JsonAsync(
            await ScopeAsync(),
            """{ __type(name: "Query") { fields { name args { name } } } }""");
    }

    // ------------------------------------------------------------------------------------- The dataset

    [Then("the result has one dataset")]
    public void ThenTheResultHasOneDataset() => Assert.Equal(1, Nodes().GetArrayLength());

    [Then("that dataset states its content hash and its config origin")]
    public void ThenThatDatasetStatesItsContentHashAndItsConfigOrigin()
    {
        var dataset = Nodes()[0];
        Assert.StartsWith("sha256:content-", dataset.GetProperty("contentHash").GetString());
        Assert.Equal(ModConfigOrigin.Shipped, dataset.GetProperty("configOrigin").GetString());
    }

    [Then("the result is the dataset {string}")]
    public void ThenTheResultIsTheDataset(string label) =>
        Assert.Equal(_datasets[label].ToString("D"), Row().GetProperty("id").GetString());

    [Then("the row counts per table are a JSON object")]
    public void ThenTheRowCountsPerTableAreAJsonObject()
    {
        var counts = Row().GetProperty("rowCounts");
        Assert.Equal(JsonValueKind.Object, counts.ValueKind);
        Assert.Equal(2, counts.GetProperty("items").GetInt32());
    }

    [Then("the result is null")]
    public void ThenTheResultIsNull() => Assert.Equal(JsonValueKind.Null, Row().ValueKind);

    [Then("the response has no errors")]
    public void ThenTheResponseHasNoErrors() => Assert.False(Response().TryGetProperty("errors", out _));

    [Then("the dataset {string} states its content hash")]
    public void ThenTheDatasetStatesItsContentHash(string label)
    {
        var dataset = _responses["modDataset"].GetProperty("data").GetProperty("modDataset");
        Assert.Equal(_datasets[label].ToString("D"), dataset.GetProperty("id").GetString());
        Assert.NotEmpty(dataset.GetProperty("contentHash").GetString()!);
        Assert.Equal(JsonValueKind.Object, dataset.GetProperty("rowCounts").ValueKind);
    }

    [Then("its template list is empty")]
    public void ThenItsTemplateListIsEmpty() =>
        Assert.Equal(0, ApiHarness.Nodes(_responses["modTemplates"], "modTemplates").GetArrayLength());

    [Then("it has no argument {string}")]
    public void ThenItHasNoArgument(string argument)
    {
        var field = _responses["__schema"].GetProperty("data").GetProperty("__type").GetProperty("fields")
            .EnumerateArray().Single(f => f.GetProperty("name").GetString() == _lastField);
        var names = field.GetProperty("args").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();

        // A field that has no argument at all would pass for the wrong reason.
        Assert.Contains("where", names);
        Assert.DoesNotContain(argument, names);
    }

    [Then("the field {string} states the server release of each attempt")]
    public async Task ThenTheFieldStatesTheServerReleaseOfEachAttempt(string field)
    {
        await QueryAsync(field, DatasetFilter("D1"));

        var rows = Nodes().EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(Release, row.GetProperty("sptVersion").GetString()));
    }

    // ------------------------------------------------------------------------------- The templates

    [Then("the result has {string} with the operation {string}")]
    public void ThenTheResultHasWithTheOperation(string item, string operation)
    {
        var row = Nodes().EnumerateArray().Single(r => r.GetProperty("templateId").GetString() == Address(item));
        Assert.Equal(operation, row.GetProperty("op").GetString());
    }

    [Then("the row of {string} states the parent {string}")]
    public void ThenTheRowStatesTheParent(string item, string parent)
    {
        var row = Nodes().EnumerateArray().Single(r => r.GetProperty("templateId").GetString() == Address(item));
        Assert.Equal(parent, row.GetProperty("parentId").GetString());
    }

    [Then("the result has one template")]
    public void ThenTheResultHasOneTemplate() => Assert.Equal(1, Nodes().GetArrayLength());

    [Then("its template id is an address and not an instance id")]
    public void ThenItsTemplateIdIsAnAddress()
    {
        var templateId = Nodes()[0].GetProperty("templateId").GetString();

        // An instance id is a MongoId that the run minted. An address is a content ULID that the archive derived.
        Assert.True(ContentUlid.IsContentUlid(templateId), $"'{templateId}' is not a content ULID.");
        Assert.DoesNotMatch(new Regex("^[0-9a-f]{24}$"), templateId!);
    }

    // ------------------------------------------------------------------------------------ The changes

    [Then("the result has the path {string}")]
    public void ThenTheResultHasThePath(string path) => Assert.Equal(path, Nodes()[0].GetProperty("path").GetString());

    [Then("the old value is the number {double} and the new value is the number {double}")]
    public void ThenTheOldValueIsTheNumber(double oldValue, double newValue)
    {
        var row = Nodes()[0];
        Assert.Equal(oldValue, row.GetProperty("oldValue").GetDouble());
        Assert.Equal(newValue, row.GetProperty("newValue").GetDouble());
    }

    [Then("both values are JSON numbers and not strings")]
    public void ThenBothValuesAreJsonNumbers()
    {
        // A client reads a document, not text (TD-15).
        var row = Nodes()[0];
        Assert.Equal(JsonValueKind.Number, row.GetProperty("oldValue").ValueKind);
        Assert.Equal(JsonValueKind.Number, row.GetProperty("newValue").ValueKind);
    }

    [Then("every row of the result states no old value")]
    public void ThenEveryRowStatesNoOldValue()
    {
        var rows = Nodes().EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("oldValue").ValueKind));
    }

    [Then("the result has that path with the operation {string}")]
    public void ThenTheResultHasThatPathWithTheOperation(string operation)
    {
        var row = Nodes()[0];
        Assert.Equal("/_props/CanSellOnRagfair", row.GetProperty("path").GetString());
        Assert.Equal(operation, row.GetProperty("op").GetString());
    }

    [Then("the row states no new value")]
    public void ThenTheRowStatesNoNewValue() =>
        Assert.Equal(JsonValueKind.Null, Nodes()[0].GetProperty("newValue").ValueKind);

    // -------------------------------------------------------------------------------------- The files

    [Then("the result states the path, the kind, the size and the SHA-256 of each file")]
    public void ThenTheResultStatesThePathTheKindTheSizeAndTheSha256()
    {
        var rows = Nodes().EnumerateArray().ToList();
        Assert.Equal(_files.Count, rows.Count);

        foreach (var row in rows)
        {
            var file = _files[row.GetProperty("path").GetString()!];
            Assert.Equal(file.Kind, row.GetProperty("kind").GetString());
            Assert.Equal(file.Size, row.GetProperty("size").GetInt64());
            Assert.Equal(file.Sha256, row.GetProperty("sha256").GetString());
        }
    }

    [Then("the file states the kind {string}")]
    public void ThenTheFileStatesTheKind(string kind) => Assert.Equal(kind, Row().GetProperty("kind").GetString());

    [Then("it states the text that the mod read")]
    public void ThenItStatesTheTextThatTheModRead() =>
        Assert.Equal(ConfigText, Row().GetProperty("contentText").GetString());

    [Then("it states the parsed document as JSON")]
    public void ThenItStatesTheParsedDocumentAsJson()
    {
        var content = Row().GetProperty("content");
        Assert.Equal(JsonValueKind.Object, content.ValueKind);
        Assert.Equal(2, content.GetProperty("Loot").GetInt32());
    }

    // ------------------------------------------------------------- Addresses and observations

    [Then("the result states the address of {string}")]
    public void ThenTheResultStatesTheAddressOf(string item) =>
        Assert.Equal(Address(item), Nodes()[0].GetProperty("address").GetString());

    [Then("it states that the address is not pinned")]
    public void ThenItStatesThatTheAddressIsNotPinned() => Assert.False(Nodes()[0].GetProperty("pinned").GetBoolean());

    [Then("the result states the server release {string}")]
    public void ThenTheResultStatesTheServerRelease(string release) =>
        Assert.Equal(release, Nodes()[0].GetProperty("sptVersion").GetString());

    [Then("it states the outcome {string}")]
    public void ThenItStatesTheOutcome(string outcome) =>
        Assert.Equal(outcome, Nodes()[0].GetProperty("outcome").GetString());

    [Then("it points at the dataset {string}")]
    public void ThenItPointsAtTheDataset(string label) =>
        Assert.Equal(_datasets[label].ToString("D"), Nodes()[0].GetProperty("datasetId").GetString());

    // -------------------------------------------------------------------------------------- The runs

    [Then("the newest run is first")]
    public void ThenTheNewestRunIsFirst()
    {
        var rows = Nodes().EnumerateArray().ToList();
        var starts = rows.Select(r => r.GetProperty("startedAt").GetDateTimeOffset()).ToList();

        Assert.Equal(starts.OrderByDescending(s => s), starts);
        Assert.Equal(_runOfD1!.Id.ToString("D"), rows[0].GetProperty("id").GetString());
        Assert.Equal(_officialRun!.Id.ToString("D"), rows[1].GetProperty("id").GetString());
    }

    [Then("each run states its kind, its purpose and its result as JSON")]
    public void ThenEachRunStatesItsKindItsPurposeAndItsResultAsJson()
    {
        // The rows of this scenario; a database that other work filled may hold more runs.
        var known = new[] { _runOfD1!, _officialRun! }.ToDictionary(r => r.Id.ToString("D"));
        var rows = Nodes().EnumerateArray().Where(r => known.ContainsKey(r.GetProperty("id").GetString()!)).ToList();
        Assert.Equal(known.Count, rows.Count);

        foreach (var row in rows)
        {
            var run = known[row.GetProperty("id").GetString()!];
            Assert.Equal(run.Kind, row.GetProperty("kind").GetString());
            Assert.Equal(run.Purpose, row.GetProperty("purpose").GetString());
            Assert.Equal(JsonValueKind.Object, row.GetProperty("result").ValueKind);
        }
    }

    [Then("the result states the status and the release of that run")]
    public void ThenTheResultStatesTheStatusAndTheReleaseOfThatRun()
    {
        var run = Row();
        Assert.Equal(ArchiveRun.Succeeded, run.GetProperty("status").GetString());
        Assert.Equal(Release, run.GetProperty("sptVersion").GetString());
    }

    [Then("it states the kind {string}")]
    public void ThenItStatesTheKind(string kind) => Assert.Equal(kind, Row().GetProperty("kind").GetString());

    [Then("it states the target mod GUID and the target version")]
    public void ThenItStatesTheTargetModGuidAndTheTargetVersion()
    {
        var run = _runs["R1"];
        Assert.Equal(run.TargetModGuid, Row().GetProperty("targetModGuid").GetString());
        Assert.Equal(run.TargetVersion, Row().GetProperty("targetVersion").GetString());
    }

    // ------------------------------------------------------- Objects that have no table of their own

    [Then("the answer has one object row for the part {string} of {int} bytes")]
    public void ThenTheAnswerHasOneObjectRowForThePartOfBytes(string part, int bytes)
    {
        var row = Assert.Single(Nodes().EnumerateArray());
        Assert.Equal(part, row.GetProperty("part").GetString());
        Assert.Equal("location", row.GetProperty("kind").GetString());

        // The size is a field and not a length that the client counts: it decides, before a byte of the
        // document is read, whether the Api serves it.
        Assert.Equal(bytes, row.GetProperty("bytes").GetInt64());
    }

    [Then("the whole document is the object that the run wrote")]
    public void ThenTheWholeDocumentIsTheObjectThatTheRunWrote()
    {
        var row = Assert.Single(Nodes().EnumerateArray());
        Assert.Equal("lighthouse", row.GetProperty("rawJson").GetProperty("Id").GetString());
    }

    // ------------------------------------------------------------------------------------- Limits

    [Then("the answer is the error {string}")]
    public void ThenTheAnswerIsTheError(string code)
    {
        var (answer, message) = ApiHarness.Error(Response());
        Assert.Equal(code, answer);
        Assert.Contains("bytes", message);
    }

    [Then("the response has an error that names {string}")]
    public void ThenTheResponseHasAnErrorThatNames(string name)
    {
        // A text operation on jsonb becomes LIKE, which Postgres refuses, so the schema must not announce it.
        var message = Response().GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains(name, message);
        Assert.Contains("does not exist", message);
    }

    [Then("the answer has the dataset {string}")]
    public void ThenTheAnswerHasTheDataset(string label) =>
        Assert.Contains(
            Nodes().EnumerateArray(),
            row => row.GetProperty("id").GetString() == _datasets[label].ToString("D"));

    // ------------------------------------------------------------------------------------ Helpers

    private async Task<TestScope> ScopeAsync() =>
        _scope ??= await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs one paged Api field and keeps the answer. The filter is the text inside <c>where: { ... }</c>;
    /// without one the query asks for the first page.
    /// </summary>
    private async Task QueryAsync(string field, string? filter)
    {
        var arguments = filter is null ? "(first: 10)" : $"(where: {{ {filter} }})";
        var query = $"{{ {field}{arguments} {{ nodes {{ {Selections[field]} }} }} }}";
        await RunAsync(field, query);
    }

    private Task QueryByIdAsync(string field, Guid id) =>
        RunAsync(field, $$"""{ {{field}}(id: "{{id}}") { {{Selections[field]}} } }""");

    private Task QueryFileAsync(string field, string dataset, string path) =>
        RunAsync(field, $$"""{ {{field}}(datasetId: "{{_datasets[dataset]}}", path: {{Literal(path)}}) { {{Selections[field]}} } }""");

    private async Task RunAsync(string field, string query)
    {
        // One root field per request: two root fields of one document run at the same time, and the test
        // transaction holds one connection. A real request opens a connection of its own.
        _lastField = field;
        _responses[field] = await ApiHarness.JsonAsync(
            await ScopeAsync(),
            query,
            rawJson: _maxDocumentBytes is { } bytes ? limits => limits.MaxValueBytes = bytes : null);
    }

    private JsonElement Response() => _responses[_lastField];

    private JsonElement Nodes() => ApiHarness.Nodes(Response(), _lastField);

    private JsonElement Row() => Response().GetProperty("data").GetProperty(_lastField);

    private string DatasetFilter(string dataset) => $$"""datasetId: { eq: "{{_datasets[dataset]}}" }""";

    /// <summary>A GraphQL string literal: a JSON string is one.</summary>
    private static string Literal(string value) => JsonSerializer.Serialize(value);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Sha256(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The address of an object that a scenario names by a label. An official object keeps the MongoId of the
    /// baseline. An added object has none, so the archive derives a content ULID (ADR-0034).
    /// </summary>
    private string Address(string label, bool? added = null)
    {
        if (_addresses.TryGetValue(label, out var known))
        {
            return known;
        }

        var address = added switch
        {
            true => ContentUlid.Create(ModTemplateKind.Item, "com.example.mod", label),
            false => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("official:" + label)).AsSpan(0, 12)),
            null => throw new InvalidOperationException($"The scenario names '{label}' before it adds or changes it."),
        };

        _addresses[label] = address;
        return address;
    }

    private ModDataset NewDataset(string label, string modGuid, string tableHashes, string rowCounts, Guid? createdByRunId = null)
    {
        var dataset = new ModDataset
        {
            ModGuid = modGuid,
            ContentHash = $"sha256:content-{label}",
            TableHashes = tableHashes,
            RowCounts = rowCounts,
            CreatedByRunId = createdByRunId,
        };
        _datasets[label] = dataset.Id;
        return dataset;
    }

    private static ArchiveRun ModRun(DateTimeOffset startedAt, string modGuid, string version) => new()
    {
        StartedAt = startedAt,
        FinishedAt = startedAt.AddSeconds(30),
        SourceHash = "sha256:mod-source",
        SptVersion = Release,
        Status = ArchiveRun.Succeeded,
        Kind = ArchiveRunKind.Mod,
        Purpose = "measure what the mod adds to a trader",
        TargetModGuid = modGuid,
        TargetVersion = version,
        BaselineRelease = Release,
        BaselineHash = "sha256:baseline",
        Result = """{"templates":2,"warnings":[]}""",
    };

    private ModTemplate Template(string dataset, string kind, string templateId, string operation, string? parentId = null) => new()
    {
        DatasetId = _datasets[dataset],
        Kind = kind,
        TemplateId = templateId,
        Op = operation,
        ParentId = parentId,
    };

    private ModChangePath Change(
        string dataset, string templateId, string path, string operation, string? oldValue, string? newValue) => new()
    {
        DatasetId = _datasets[dataset],
        Kind = ModTemplateKind.Item,
        TemplateId = templateId,
        Path = path,
        Op = operation,
        OldValue = oldValue,
        NewValue = newValue,
    };
}
