using System.Text.Json;
using System.Text.RegularExpressions;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reqnroll;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/query-api.feature</c> that read the archive through the schema the Api serves
/// (<see cref="ApiHarness"/>): the read-only promise, official and mod collections, filter, sort and page,
/// the release argument (ADR-0014) and the filter by a path (ADR-0040). The steps that need an HTTP request,
/// such as the cache, the guard and the body limit, are in <see cref="QueryApiHttpSteps"/>.
/// The scenario writes inside one transaction that it rolls back (<see cref="QueryWorld"/>).
/// </summary>
[Binding]
public sealed partial class QueryApiSteps(QueryWorld world) : IDisposable
{
    // The release that "an archive database with official data" holds. The scenarios of the release argument
    // add or remove releases from here.
    private const string Latest = "4.0.13";

    private const string EveryFieldOfItem =
        "sptVersion id name parentId type prototype weight width height stackMaxSize props";

    private readonly Dictionary<string, Guid> _datasets = [];
    private readonly List<JsonElement> _answers = [];
    private NpgsqlConnection? _reader;
    private PostgresException? _refused;
    private string? _probeTable;
    private string _pathName = "";

    public void Dispose()
    {
        _reader?.Dispose();

        if (_probeTable is not null)
        {
            using var connection = new NpgsqlConnection(ArchiveDatabase.OwnerConnectionString);
            connection.Open();
            using var drop = new NpgsqlCommand($"drop table if exists {_probeTable}", connection);
            drop.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------------------------ The archive

    [Given("an archive database with official data and mod data")]
    public async Task GivenAnArchiveDatabase()
    {
        // Official data is a release. The scenarios add the rows that they need, so a count is never disturbed.
        var scope = await world.ScopeAsync();
        await SptVersionRegistry.EnsureAsync(scope.Db, Latest, TestContext.Current.CancellationToken);

        // Mod data is a dataset of a mod.
        await DatasetAsync("mod-x");
    }

    [Given("the official item {string} and the mod item {string} of the mod {string}")]
    public async Task GivenTheOfficialItemAndTheModItem(string official, string mod, string modGuid)
    {
        await AddItemAsync(official);
        await AddModItemAsync(mod, modGuid);
    }

    [Given("the mod item {string} of the mod {string}")]
    public Task GivenTheModItem(string mod, string modGuid) => AddModItemAsync(mod, modGuid);

    [Given("the official items {string}, {string} and {string}")]
    public async Task GivenTheOfficialItems(string first, string second, string third)
    {
        foreach (var name in new[] { first, second, third })
        {
            await AddItemAsync(name);
        }
    }

    [Given("the official item {string}")]
    public Task GivenTheOfficialItem(string name) => AddItemAsync(name);

    [Given("{int} official items")]
    public async Task GivenOfficialItems(int count)
    {
        var scope = await world.ScopeAsync();
        scope.Db.Items.AddRange(Enumerable.Range(0, count).Select(i => Item(Latest, $"item-{i:D3}", $"item-{i:D3}", "{}")));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the official customizations {string} for {string} and {string} for {string}")]
    public async Task GivenTheOfficialCustomizations(string first, string firstSide, string second, string secondSide)
    {
        var scope = await world.ScopeAsync();
        foreach (var (id, side) in new[] { (first, firstSide), (second, secondSide) })
        {
            scope.Db.Customizations.Add(new Customization
            {
                SptVersion = Latest,
                Id = id,
                Name = id,
                ParentId = "0",
                Type = "Item",
                Sides = [side],
                Props = "{}",
            });
        }

        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the locale entries of the entity {string} in the languages {string} and {string}")]
    public async Task GivenTheLocaleEntries(string entity, string first, string second)
    {
        var scope = await world.ScopeAsync();
        foreach (var language in new[] { first, second })
        {
            if (await scope.Db.Languages.FindAsync([language], TestContext.Current.CancellationToken) is null)
            {
                scope.Db.Languages.Add(new Language { Code = language, Name = language });
            }
        }

        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (var language in new[] { first, second })
        {
            scope.Db.LocaleEntries.Add(new LocaleEntry
            {
                SptVersion = Latest,
                Scope = "items",
                LanguageCode = language,
                Key = $"{entity} Name",
                Value = $"{entity} in {language}",
                EntityId = entity,
                Field = "Name",
            });
        }

        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Given("the traders {string} and {string} sell the item {string}")]
    public async Task GivenTheTradersSellTheItem(string first, string second, string item)
    {
        var scope = await world.ScopeAsync();
        foreach (var trader in new[] { first, second })
        {
            scope.Db.Traders.Add(new Trader { SptVersion = Latest, Id = trader, Name = trader, Base = "{}" });
        }

        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (var trader in new[] { first, second })
        {
            scope.Db.TraderAssortItems.Add(new TraderAssortItem
            {
                SptVersion = Latest,
                TraderId = trader,
                InstanceId = $"{trader}-offer",
                TemplateId = item,
            });
        }

        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ----------------------------------------------------------------------------------- Read only

    [When("I read the GraphQL schema")]
    public Task WhenIReadTheSchema() => RunAsync(
        "__schema",
        "{ __schema { queryType { name } mutationType { name } types { name kind inputFields { name type { name kind ofType { name } } } } } }");

    [Then("the schema has a Query type")]
    public void ThenTheSchemaHasAQueryType() =>
        Assert.Equal("Query", Schema().GetProperty("queryType").GetProperty("name").GetString());

    [Then("the schema has no Mutation type")]
    public void ThenTheSchemaHasNoMutationType() =>
        Assert.Equal(JsonValueKind.Null, Schema().GetProperty("mutationType").ValueKind);

    [Given("the Api connects as the role {string}")]
    public async Task GivenTheApiConnectsAsTheRole(string role)
    {
        await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);
        _reader = await ArchiveDatabase.OpenReaderAsync(TestContext.Current.CancellationToken);

        var current = await new NpgsqlCommand("select current_user", _reader).ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal(role, current);
    }

    [When("that role runs an INSERT on the table {string}")]
    public async Task WhenThatRoleRunsAnInsert(string table)
    {
        await using var insert = new NpgsqlCommand(
            $"insert into {table} (spt_version, id, name, parent_id, type, props) values ('4.0.13', 'x', 'x', '0', 'Item', '{{}}')",
            _reader);

        _refused = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Then("PostgreSQL rejects the statement with a permission error")]
    public void ThenPostgresRejectsTheStatement() => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, _refused!.SqlState);

    [Given("a migration creates a new table")]
    public async Task GivenAMigrationCreatesANewTable()
    {
        await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);

        // A migration runs as the owner of the schema, and the reader gets its privilege from the default
        // privileges of that owner (docker/initdb). The table must be committed for another role to see it,
        // so it is dropped when the scenario ends.
        _probeTable = $"spec_probe_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(ArchiveDatabase.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = new NpgsqlCommand($"create table {_probeTable} (id int primary key)", connection);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [When("the role {string} runs a SELECT on that table")]
    public async Task WhenTheRoleRunsASelect(string role)
    {
        await GivenTheApiConnectsAsTheRole(role);
        await using var select = new NpgsqlCommand($"select count(*) from {_probeTable}", _reader);
        _ = await select.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    [Then("the query succeeds")]
    public void ThenTheQuerySucceeds()
    {
        // The SELECT of the step above threw when the role could not read the table.
        Assert.NotNull(_probeTable);
    }

    // ------------------------------------------------------------ Filter, sort and page

    [When("I query {string} with the filter name starts with {string}")]
    public Task WhenIQueryWithTheNameFilter(string field, string prefix) =>
        RunAsync(field, $$"""{ {{field}}(where: { name: { startsWith: {{Literal(prefix)}} } }) { nodes { id name } } }""");

    [When("I query {string} with the id {string}")]
    public Task WhenIQueryWithTheId(string field, string id) =>
        RunAsync(field, $$"""{ {{field}}(id: {{Literal(id)}}) { id name } }""");

    [When("I query {string} with the side {string}")]
    public Task WhenIQueryWithTheSide(string field, string side) =>
        RunAsync(field, $$"""{ {{field}}(side: {{Literal(side)}}) { nodes { id sides } } }""");

    [When("I query {string} with the entity id {string} and the language {string}")]
    public Task WhenIQueryWithTheEntityAndLanguage(string field, string entity, string language) =>
        RunAsync(field, $$"""
            { {{field}}(where: { entityId: { eq: {{Literal(entity)}} }, languageCode: { eq: {{Literal(language)}} } }) {
                nodes { entityId languageCode value } } }
            """);

    [When("I query {string} with the template id {string}")]
    public Task WhenIQueryWithTheTemplateId(string field, string template) =>
        RunAsync(field, $$"""{ {{field}}(where: { templateId: { eq: {{Literal(template)}} } }) { nodes { traderId templateId } } }""");

    [When("I query {string} for the first {int} with the total count")]
    public Task WhenIQueryForTheFirstWithTheTotalCount(string field, int first) =>
        RunAsync(field, $"{{ {field}(first: {first}) {{ totalCount pageInfo {{ hasNextPage }} nodes {{ id }} }} }}");

    [Then("the result has {string}")]
    public void ThenTheResultHas(string name) => Assert.Contains(name, Labels());

    [Then("the result has {string} and {string}")]
    public void ThenTheResultHasBoth(string first, string second)
    {
        Assert.Contains(first, Labels());
        Assert.Contains(second, Labels());
    }

    [Then("the result does not have {string}")]
    public void ThenTheResultDoesNotHave(string name)
    {
        // An empty answer would pass for the wrong reason.
        Assert.NotEmpty(Labels());
        Assert.DoesNotContain(name, Labels());
    }

    [Then("the result is the item {string}")]
    public void ThenTheResultIsTheItem(string id) =>
        Assert.Equal(id, world.Response.GetProperty("data").GetProperty(world.Field).GetProperty("id").GetString());

    [Then("the result has only the {string} entries of {string}")]
    public void ThenTheResultHasOnlyTheEntries(string language, string entity)
    {
        var rows = Nodes().EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal(language, row.GetProperty("languageCode").GetString());
            Assert.Equal(entity, row.GetProperty("entityId").GetString());
        });
    }

    [Then("the result has one row for {string} and one row for {string}")]
    public void ThenTheResultHasOneRowForEach(string first, string second) =>
        Assert.Equal(
            new[] { first, second }.Order(),
            Nodes().EnumerateArray().Select(row => row.GetProperty("traderId").GetString()!).Order());

    [Then("the result has {int} nodes")]
    public void ThenTheResultHasNodes(int count) => Assert.Equal(count, Nodes().GetArrayLength());

    [Then("the total count is {int}")]
    public void ThenTheTotalCountIs(int count) =>
        Assert.Equal(count, world.Response.GetProperty("data").GetProperty(world.Field).GetProperty("totalCount").GetInt32());

    [Then("the result has a next page")]
    public void ThenTheResultHasANextPage() =>
        Assert.True(world.Response.GetProperty("data").GetProperty(world.Field).GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

    // ------------------------------------------------------------------------------------- Limits

    [When("I send a query that asks for a page larger than 200")]
    public Task WhenISendAQueryForALargePage() =>
        RunAsync("items", "{ items(first: 201) { nodes { id } } }");

    [When("I send a query that nests deeper than 10 levels")]
    public Task WhenISendADeepQuery() => RunAsync("a", Nested(11));

    [When("I send a query nested 11 levels deep")]
    public Task WhenISendAQueryNested() => RunAsync("a", Nested(11));

    [Then("the response has an error")]
    public void ThenTheResponseHasAnError()
    {
        Assert.True(world.Response.TryGetProperty("errors", out var errors), world.Response.GetRawText());
        Assert.NotEmpty(errors.EnumerateArray());
    }

    [Then("the database does not run the query")]
    public void ThenTheDatabaseDoesNotRunTheQuery()
    {
        // The depth rule refuses a query before any resolver runs. A page that is too large is refused when
        // the page is cut, after the resolver chose the release, so what must not run is the read of the rows.
        Assert.DoesNotContain(world.Commands, command => command.Contains("FROM items", StringComparison.Ordinal));
    }

    [Then("the response has an error and no data")]
    public void ThenTheResponseHasAnErrorAndNoData()
    {
        ThenTheResponseHasAnError();
        Assert.False(world.Response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object);
    }

    [When("I query one page of 200 rows and select every field of a row")]
    public Task WhenIQueryAFullPage() =>
        RunAsync("items", "{ items(first: 200) { nodes { " + EveryFieldOfItem + " } } }");

    [When("I send a query for twelve pages of 200 rows")]
    public Task WhenISendAQueryForTwelvePages() =>
        RunAsync("items", "{ " + string.Join(" ", Enumerable.Range(0, 12).Select(i => $"l{i}: items(first: 200) {{ nodes {{ id }} }}")) + " }");

    [Then("the response has no cost error")]
    public void ThenTheResponseHasNoCostError()
    {
        Assert.DoesNotContain("HC0047", world.Response.GetRawText());
        Assert.False(world.Response.TryGetProperty("errors", out var errors), errors.ToString());
    }

    [Then("the response has the error {string}")]
    public void ThenTheResponseHasTheError(string code) => Assert.Contains(code, world.Response.GetRawText());

    [When("I send an introspection query")]
    public Task WhenISendAnIntrospectionQuery() => RunAsync("__schema", "{ __schema { types { name } } }");

    [Then("the response has the type {string}")]
    public void ThenTheResponseHasTheType(string name) =>
        Assert.Contains(world.Response.GetProperty("data").GetProperty("__schema").GetProperty("types").EnumerateArray(), type => type.GetProperty("name").GetString() == name);

    // ------------------------------------------------------------------- Server version (ADR-0014)

    [Given("the archive holds the releases {string} and {string}")]
    public async Task GivenTheArchiveHoldsTheReleases(string older, string newer)
    {
        await HoldReleaseAsync(older);
        await HoldReleaseAsync(newer);
    }

    [Given("the archive holds the release {string}")]
    public Task GivenTheArchiveHoldsTheRelease(string release) => HoldReleaseAsync(release);

    [Given("the archive holds no release")]
    public async Task GivenTheArchiveHoldsNoRelease()
    {
        var scope = await world.ScopeAsync();
        await scope.ExecuteAsync("delete from spt_versions");
    }

    [When("I query {string} without a version argument")]
    public Task WhenIQueryWithoutAVersion(string field) =>
        RunAsync(field, $"{{ {field} {{ nodes {{ id name sptVersion }} }} }}");

    [When("I query {string} with the version {string}")]
    public Task WhenIQueryWithTheVersion(string field, string version) =>
        RunAsync(field, $"{{ {field}(sptVersion: {Literal(version)}) {{ nodes {{ id name sptVersion }} }} }}");

    [Then("the answer comes from {string}")]
    public void ThenTheAnswerComesFrom(string release) => ThenTheAnswerStatesTheVersion(release);

    [Then("the answer states the version {string}")]
    public void ThenTheAnswerStatesTheVersion(string release)
    {
        var rows = Nodes().EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(release, row.GetProperty("sptVersion").GetString()));
    }

    [Then("no row of {string} is in the answer")]
    public void ThenNoRowOfIsInTheAnswer(string release)
    {
        Assert.NotEmpty(Nodes().EnumerateArray());
        Assert.DoesNotContain(Nodes().EnumerateArray(), row => row.GetProperty("sptVersion").GetString() == release);
    }

    [Then("the error names the latest archived release")]
    public void ThenTheErrorNamesTheLatestRelease() =>
        Assert.Contains($"'{Latest}'", ApiHarness.Error(world.Response).Message);

    [Then("the response has no empty list")]
    public void ThenTheResponseHasNoEmptyList()
    {
        Assert.True(world.Response.TryGetProperty("errors", out _), world.Response.GetRawText());
        Assert.False(world.Response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(world.Field, out var list) && list.ValueKind == JsonValueKind.Object);
    }

    [Then("the error states that the argument takes one exact archived release")]
    public void ThenTheErrorStatesOneExactRelease() =>
        Assert.Contains("one exact archived release", ApiHarness.Error(world.Response).Message);

    [Then("the error code is not the error code of an unknown release")]
    public void ThenTheErrorCodeIsNotUnknown() =>
        Assert.NotEqual("SPT_VERSION_UNKNOWN", ApiHarness.Error(world.Response).Code);

    [Then("the error code is {string}")]
    public void ThenTheErrorCodeIs(string code) => Assert.Equal(code, ApiHarness.Error(world.Response).Code);

    [Then("the answer lists {string} before {string}")]
    public void ThenTheAnswerListsBefore(string first, string second)
    {
        var versions = world.Response.GetProperty("data").GetProperty("sptVersions").EnumerateArray()
            .Select(row => row.GetProperty("version").GetString()!).ToList();

        Assert.True(versions.IndexOf(first) >= 0 && versions.IndexOf(second) >= 0, string.Join(", ", versions));
        Assert.True(versions.IndexOf(first) < versions.IndexOf(second), string.Join(", ", versions));
    }

    // ------------------------------------------------------------ Filter by a path (ADR-0040)

    [Given(@"^the item ""([^""]*)"" has the property ""([^""]*)"" (-?[0-9][0-9.eE+-]*) in its ""props"" column$")]
    public Task GivenTheItemHasTheProperty(string item, string property, string number) =>
        AddItemAsync(item, Props((property, number)));

    [Given(@"^the item ""([^""]*)"" has the property ""([^""]*)"" with the (object|JSON value) (.+) in its ""props"" column$")]
    public Task GivenTheItemHasThePropertyWith(string item, string property, string kind, string json) =>
        AddItemAsync(item, Props((property, json)));

    // The scenario "A path reaches into an object and an array" reaches the list through the path "Nested", "list",
    // "1", so the property "Nested" is an object that holds the list under the name "list".
    [Given(@"^the item ""([^""]*)"" has the property ""([^""]*)"" with the list (.+) in its ""props"" column$")]
    public Task GivenTheItemHasThePropertyWithTheList(string item, string property, string json) =>
        AddItemAsync(item, Props((property, $"{{\"list\":{json}}}")));

    [Given(@"^the items ""([^""]*)"" and ""([^""]*)"" have the property ""([^""]*)"" ([0-9]+) in their ""props"" column$")]
    public async Task GivenTheItemsHaveTheProperty(string first, string second, string property, string number)
    {
        await AddItemAsync(first, Props((property, number)));
        await AddItemAsync(second, Props((property, number)));
    }

    [Given(@"^the item ""([^""]*)"" has the properties ""([^""]*)"" ([0-9.]+) and ""([^""]*)"" ([0-9.]+) in its ""props"" column$")]
    public Task GivenTheItemHasTheProperties(string item, string first, string firstValue, string second, string secondValue) =>
        AddItemAsync(item, Props((first, firstValue), (second, secondValue)));

    [Given(@"^the item ""([^""]*)"" has no property ""([^""]*)"" in its ""props"" column$")]
    public Task GivenTheItemHasNoProperty(string item, string property) =>
        AddItemAsync(item, Props(("Other", "1")));

    [Given(@"^the item ""([^""]*)"" has a property whose name holds quotes, commas, braces and ""--""$")]
    public Task GivenTheItemHasAWeirdProperty(string item)
    {
        _pathName = "we\"ird,key}'--";
        return AddItemAsync(item, $"{{{JsonSerializer.Serialize(_pathName)}:\"v'\\\\\"}}");
    }

    [Given(@"^the item ""([^""]*)"" has the JSON document (.+) in its ""props"" column$")]
    public Task GivenTheItemHasTheDocument(string item, string document) => AddItemAsync(item, document);

    [When(@"^I query ""items"" where ""props"" (.+)$")]
    public async Task WhenIQueryItemsWherePropsAsync(string condition)
    {
        var where = Condition(condition);
        await RunAsync("items", $$"""{ items(where: {{where}}) { nodes { id name } } }""");
    }

    [When(@"^I query ""items"" where the name starts with ""([^""]*)"" and ""props"" has the value ([0-9]+) at the path ""([^""]*)""$")]
    public Task WhenIQueryItemsWithTwoFilters(string prefix, string value, string path) =>
        RunAsync("items", $$"""
            { items(where: { name: { startsWith: {{Literal(prefix)}} }, props: { pathEq: { path: [{{Literal(path)}}], value: {{value}} } } }) { nodes { id name } } }
            """);

    [When("I query one page of 200 rows with a path filter and select every field of a row")]
    public Task WhenIQueryAFullPageWithAPathFilter() =>
        RunAsync("items", "{ items(first: 200, where: " + PathEq("pathEq", "[\"Slots\"]", "5") + ") { nodes { " + EveryFieldOfItem + " } } }");

    [When("I send a query with {int} path filters joined by {string}")]
    public Task WhenISendAQueryWithManyFilters(int count, string join)
    {
        var terms = string.Join(", ", Enumerable.Range(0, count).Select(i => PathEq("pathEq", $"[\"k{i}\"]", i.ToString())));
        return RunAsync("items", "{ items(where: { " + join + ": [" + terms + "] }) { nodes { name } } }");
    }

    [When("I query {string} with the path and the value in variables")]
    public async Task WhenIQueryWithVariables(string field)
    {
        var request = new OperationRequestBuilder()
            .SetDocument($$"""
                query ($path: [String!]!, $value: Any) {
                  {{field}}(where: { props: { pathEq: { path: $path, value: $value } } }) { nodes { id name } }
                }
                """)
            .SetVariableValues(new Dictionary<string, object?> { ["path"] = new[] { "Slots" }, ["value"] = 5 })
            .Build();

        await RunAsync(field, request);
    }

    [Then("the answer has the item {string}")]
    public void ThenTheAnswerHasTheItem(string item)
    {
        Assert.False(world.Response.TryGetProperty("errors", out var errors), errors.ToString());
        Assert.Contains(item, Labels());
    }

    [Then("the answer does not have the item {string}")]
    public void ThenTheAnswerDoesNotHaveTheItem(string item)
    {
        Assert.False(world.Response.TryGetProperty("errors", out var errors), errors.ToString());
        Assert.DoesNotContain(item, Labels());
    }

    [Then("the answer has the items {string} and {string}")]
    public void ThenTheAnswerHasTheItems(string first, string second)
    {
        ThenTheAnswerHasTheItem(first);
        ThenTheAnswerHasTheItem(second);
    }

    [Then("the database statement does not contain the name")]
    public void ThenTheStatementDoesNotContainTheName()
    {
        var select = Assert.Single(world.Commands, command => command.Contains("FROM items", StringComparison.Ordinal));
        Assert.DoesNotContain("ird,key", select, StringComparison.Ordinal);
    }

    [Then("the type {string} has the operations {string}, {string}, {string} and {string}")]
    public void ThenTheTypeHasTheOperations(string type, string first, string second, string third, string fourth)
    {
        var fields = InputTypes()[type].Select(field => field.GetProperty("name").GetString()!).ToList();
        Assert.All(new[] { first, second, third, fourth }, operation => Assert.Contains(operation, fields));
    }

    [Then("every jsonb column of the model has a filter field of that type")]
    public async Task ThenEveryJsonbColumnHasAFilterField()
    {
        var scope = await world.ScopeAsync();
        var types = InputTypes();

        // Every jsonb column of an exposed entity has a filter field of that type, so a new column cannot miss it,
        // and every table that holds one is exposed.
        var columns = scope.Db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.GetColumnType() == "jsonb").Select(p => (Entity: e.ClrType.Name, Property: p.Name)))
            .ToArray();
        Assert.NotEmpty(columns);

        var exposed = columns.Where(c => types.ContainsKey($"{c.Entity}FilterInput")).ToArray();
        Assert.Empty(columns.Select(c => c.Entity).Except(exposed.Select(c => c.Entity)).Distinct());

        foreach (var (entity, property) in exposed)
        {
            var field = types[$"{entity}FilterInput"].SingleOrDefault(f => f.GetProperty("name").GetString() == char.ToLowerInvariant(property[0]) + property[1..]);
            Assert.True(field.ValueKind != JsonValueKind.Undefined, $"{entity}.{property} has no filter field.");
            Assert.Equal("JsonOperationFilterInput", field.GetProperty("type").GetProperty("name").GetString());
        }
    }

    [Then("the database runs one {string} over the column")]
    public void ThenTheDatabaseRunsOne(string function)
    {
        var select = Assert.Single(world.Commands, command => command.Contains("FROM items", StringComparison.Ordinal));
        Assert.Contains($"{function}(", select);
        Assert.DoesNotContain("Slots", select); // the path is a parameter and not part of the statement
    }

    [Then("the database runs no {string} on the column")]
    public void ThenTheDatabaseRunsNo(string keyword)
    {
        var select = Assert.Single(world.Commands, command => command.Contains("FROM items", StringComparison.Ordinal));
        Assert.DoesNotContain(keyword, select, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the response has the error code {string}")]
    public void ThenTheResponseHasTheErrorCode(string code) => Assert.Equal(code, ApiHarness.Error(world.Response).Code);

    [Then("the response is not {string}")]
    public void ThenTheResponseIsNot(string text) =>
        Assert.DoesNotContain(text, ApiHarness.Error(world.Response).Message);

    [Then("neither response has an error")]
    public void ThenNeitherResponseHasAnError()
    {
        Assert.Equal(2, _answers.Count);
        Assert.All(_answers, answer => Assert.False(answer.TryGetProperty("errors", out var errors), errors.ToString()));
    }

    [Then("the error says that the value is JSON text and shows the quoted form")]
    public void ThenTheErrorShowsTheQuotedForm()
    {
        var message = ApiHarness.Error(world.Response).Message;
        Assert.Contains("JSON text", message);
        Assert.Contains("\"\\\"x\\\"\"", message);
    }

    [Then("the error comes from the schema and not from the database")]
    public void ThenTheErrorComesFromTheSchema()
    {
        Assert.Contains("does not exist", world.Response.GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.DoesNotContain(world.Commands, command => command.Contains("FROM items", StringComparison.Ordinal));
    }

    [When("I query {string} and select {string}")]
    public Task WhenIQueryAndSelect(string field, string column) =>
        RunAsync(field, $"{{ {field} {{ nodes {{ {column} }} }} }}");

    [Then("the field {string} is a JSON object")]
    public void ThenTheFieldIsAJsonObject(string column) =>
        Assert.Equal(JsonValueKind.Object, Nodes()[0].GetProperty(column).ValueKind);

    [Then("it is not an escaped string")]
    public void ThenItIsNotAnEscapedString() =>
        Assert.Equal(0.5, Nodes()[0].GetProperty("props").GetProperty("Weight").GetDouble());

    // ------------------------------------------------------------------------------------- Helpers

    /// <summary>The words of a condition on the column "props", turned into the text inside <c>where: { ... }</c>.</summary>
    private string Condition(string text)
    {
        // "has the value 5 at the path "Slots" and the value 0.5 at the path "Weight""
        var terms = text.Split(" and the value ", StringSplitOptions.None);
        if (terms.Length > 1)
        {
            var filters = terms.Select((term, index) => PathFilter(index == 0 ? term : "has the value " + term)).ToList();
            return $"{{ and: [ {string.Join(", ", filters)} ] }}";
        }

        return text switch
        {
            _ when text.StartsWith("equals the text ", StringComparison.Ordinal) =>
                $"{{ props: {{ eq: {text["equals the text ".Length..]} }} }}",
            _ when text.StartsWith("differs from the text ", StringComparison.Ordinal) =>
                $"{{ props: {{ neq: {text["differs from the text ".Length..]} }} }}",
            "has a path and no value" => """{ props: { pathEq: { path: ["Slots"] } } }""",
            _ when text.StartsWith("equals the property of ", StringComparison.Ordinal) =>
                // The whole document, compared by its content.
                $"{{ props: {{ eq: {Literal("""{"Weight":0.5}""")} }} }}",
            _ => PathFilter(text),
        };
    }

    private string PathFilter(string text)
    {
        var negated = text.StartsWith("has not the value ", StringComparison.Ordinal);
        var operation = negated ? "pathNeq" : "pathEq";

        var deep = DeepList().Match(text);
        if (deep.Success)
        {
            var levels = int.Parse(deep.Groups[1].Value);
            return PathEq(operation, "[\"Slots\"]", new string('[', levels) + new string(']', levels));
        }

        var shaped = Shaped().Match(text);
        if (shaped.Success)
        {
            return PathEq(operation, $"[{shaped.Groups["path"].Value}]", shaped.Groups["value"].Value);
        }

        var sized = Sized().Match(text);
        if (sized.Success)
        {
            var path = sized.Groups["many"].Success
                ? "[" + string.Join(", ", Enumerable.Repeat("\"k\"", int.Parse(sized.Groups["many"].Value))) + "]"
                : $"[\"{new string('k', int.Parse(sized.Groups["long"].Value))}\"]";
            return PathEq(operation, path, sized.Groups["value"].Value);
        }

        var named = Named().Match(text);
        if (named.Success)
        {
            return PathEq(operation, $"[{JsonSerializer.Serialize(_pathName)}]", "\"v'\\\\\"");
        }

        var failure = Bad().Match(text);
        if (failure.Success)
        {
            return PathEq(operation, BadPath(failure.Groups["path"].Value), "5");
        }

        throw new InvalidOperationException($"The scenario states a condition that no step knows: {text}");
    }

    private static string PathEq(string operation, string path, string value) =>
        $"{{ props: {{ {operation}: {{ path: {path}, value: {value} }} }} }}";

    private static string BadPath(string name) => name switch
    {
        "no segment" => "[]",
        "an empty segment" => """["a", ""]""",
        "a segment of more than 256 chars" => $"[\"{new string('k', 257)}\"]",
        "more than 10 segments" => "[" + string.Join(", ", Enumerable.Repeat("\"k\"", 11)) + "]",
        "a segment with a NUL character" => """["a\u0000b"]""",
        _ => throw new InvalidOperationException($"No bad path is named '{name}'."),
    };

    private async Task HoldReleaseAsync(string release)
    {
        var scope = await world.ScopeAsync();
        await SptVersionRegistry.EnsureAsync(scope.Db, release, TestContext.Current.CancellationToken);

        // One row for each release, so an answer shows which release it was read from.
        scope.Db.Items.Add(Item(release, $"item-{release}", $"item-{release}", "{}"));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> DatasetAsync(string modGuid)
    {
        if (_datasets.TryGetValue(modGuid, out var known))
        {
            return known;
        }

        var scope = await world.ScopeAsync();
        var dataset = new ModDataset
        {
            ModGuid = modGuid,
            ContentHash = $"sha256:content-{modGuid}",
            TableHashes = "{}",
            RowCounts = "{}",
        };
        scope.Db.ModDatasets.Add(dataset);
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return _datasets[modGuid] = dataset.Id;
    }

    private async Task AddItemAsync(string name, string props = "{}")
    {
        var scope = await world.ScopeAsync();
        scope.Db.Items.Add(Item(Latest, name, name, props));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddModItemAsync(string name, string modGuid)
    {
        var datasetId = await DatasetAsync(modGuid);
        var scope = await world.ScopeAsync();
        scope.Db.ModItems.Add(new ModItem { DatasetId = datasetId, Id = name, Name = name, ParentId = "0", Type = "Item", Props = "{}" });
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Item Item(string release, string id, string name, string props) => new()
    {
        SptVersion = release,
        Id = id,
        Name = name,
        ParentId = "0",
        Type = "Item",
        Props = props,
    };

    private static string Props(params (string Name, string Json)[] properties) =>
        "{" + string.Join(",", properties.Select(p => $"{JsonSerializer.Serialize(p.Name)}:{p.Json}")) + "}";

    private Task RunAsync(string field, string query) => RunAsync(field, new OperationRequestBuilder().SetDocument(query).Build());

    private async Task RunAsync(string field, IOperationRequest request)
    {
        // One root field per request: the test transaction holds one connection.
        world.Commands.Clear();
        world.Field = field;
        world.Response = JsonDocument.Parse(await ApiHarness.RunAsync(await world.ScopeAsync(), request, world.Commands)).RootElement.Clone();
        _answers.Add(world.Response);
    }

    private JsonElement Schema() => world.Response.GetProperty("data").GetProperty("__schema");

    private Dictionary<string, JsonElement[]> InputTypes() =>
        Schema().GetProperty("types").EnumerateArray()
            .Where(type => type.GetProperty("kind").GetString() == "INPUT_OBJECT")
            .ToDictionary(type => type.GetProperty("name").GetString()!, type => type.GetProperty("inputFields").EnumerateArray().ToArray());

    private JsonElement Nodes() => ApiHarness.Nodes(world.Response, world.Field);

    /// <summary>The ids and the names of the rows in the answer.</summary>
    private HashSet<string> Labels()
    {
        Assert.False(world.Response.TryGetProperty("errors", out var errors), errors.ToString());

        var labels = new HashSet<string>();
        foreach (var row in Nodes().EnumerateArray())
        {
            foreach (var column in new[] { "id", "name" })
            {
                if (row.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    labels.Add(value.GetString()!);
                }
            }
        }

        return labels;
    }

    private static string Nested(int levels) => "{ a " + string.Concat(Enumerable.Repeat("{ b ", levels)) + string.Concat(Enumerable.Repeat("}", levels + 1));

    /// <summary>A GraphQL string literal: a JSON string is one.</summary>
    private static string Literal(string value) => JsonSerializer.Serialize(value);

    [GeneratedRegex("""^has a list nested (\d+) levels deep at the path "Slots"$""")]
    private static partial Regex DeepList();

    [GeneratedRegex("""^has (?:not )?the (?:value|object|list|string) (?<value>.+?) at the path (?<path>"[^"]*"(?:, "[^"]*")*)$""")]
    private static partial Regex Shaped();

    [GeneratedRegex("""^has the value (?<value>\d+) at (?:a path of (?<many>\d+) segments|a path of one segment of (?<long>\d+) characters)$""")]
    private static partial Regex Sized();

    [GeneratedRegex("""^has its value at the path of that name$""")]
    private static partial Regex Named();

    [GeneratedRegex("""^has the value 5 at the path (?<path>no segment|an empty segment|a segment of more than 256 chars|more than 10 segments|a segment with a NUL character)$""")]
    private static partial Regex Bad();
}
