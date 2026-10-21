using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// A jsonb column is filtered by the value at a path (TD-52, ADR-0040), and a JSON scalar reaches the column
/// as JSON and not as a bare string (TD-55). Each test writes its rows inside a transaction that it rolls back.
/// </summary>
public sealed class JsonPathFilterTests
{
    // Higher than any real release and than the releases of ApiTests, so these rows are the "latest" release.
    private const string Release = "9991.0.1";

    private static async Task SeedAsync(TestScope scope, params (string Name, string Props)[] items)
    {
        await SptVersionRegistry.EnsureAsync(scope.Db, Release, TestContext.Current.CancellationToken);

        var number = 0;
        foreach (var (name, props) in items)
        {
            scope.Db.Items.Add(new Item
            {
                SptVersion = Release,
                Id = (++number).ToString("x24"),
                Name = name,
                ParentId = "0",
                Type = "Item",
                Props = props,
            });
        }

        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Items(string where) => $$"""{ items(where: {{where}}) { nodes { name } } }""";

    private static string PathEq(string path, string value) =>
        $$"""{ props: { pathEq: { path: {{path}}, value: {{value}} } } }""";

    private static async Task<string[]> NamesAsync(TestScope scope, string where)
    {
        var response = await ApiHarness.JsonAsync(scope, Items(where));
        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());

        return [.. ApiHarness.Nodes(response, "items").EnumerateArray().Select(n => n.GetProperty("name").GetString()!).Order()];
    }

    [Fact]
    public async Task Rows_are_found_by_the_value_at_a_path()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""), ("B1", """{"Slots":3}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, PathEq("[\"Slots\"]", "5")));
    }

    [Fact]
    public async Task A_path_reaches_into_an_object_and_an_array()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Nested":{"list":[1,2]}}"""), ("B1", """{"Nested":{"list":[2,1]}}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, PathEq("[\"Nested\", \"list\", \"1\"]", "2")));
        Assert.Equal(["B1"], await NamesAsync(scope, PathEq("[\"Nested\", \"list\", \"0\"]", "2")));
    }

    [Theory]
    [InlineData("Slots", "5")]
    [InlineData("Weight", "0.5")]
    [InlineData("Name", "\"Bear\"")]
    [InlineData("Flag", "true")]
    [InlineData("Nothing", "null")]
    public async Task A_scalar_of_every_JSON_type_is_found(string property, string json)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", $$"""{"{{property}}":{{json}}}"""), ("B1", """{"Other":1}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, PathEq($"[\"{property}\"]", json)));
    }

    [Fact]
    public async Task One_JSON_type_is_not_mistaken_for_another()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5,"Flag":true}"""));

        Assert.Empty(await NamesAsync(scope, PathEq("[\"Slots\"]", "\"5\"")));
        Assert.Empty(await NamesAsync(scope, PathEq("[\"Flag\"]", "1")));
        Assert.Empty(await NamesAsync(scope, PathEq("[\"Slots\"]", "null")));
    }

    [Fact]
    public async Task A_number_is_compared_by_its_value_and_not_by_its_spelling()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5.0}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, PathEq("[\"Slots\"]", "5")));
    }

    [Fact]
    public async Task The_negation_keeps_the_rows_that_lack_the_path()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""), ("B1", """{"Slots":3}"""), ("C1", """{"Other":1}"""));

        var names = await NamesAsync(scope, """{ props: { pathNeq: { path: ["Slots"], value: 5 } } }""");

        Assert.Equal(["B1", "C1"], names);
    }

    [Fact]
    public async Task Two_path_filters_narrow_one_answer()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5,"Weight":0.5}"""), ("B1", """{"Slots":5,"Weight":2}"""));

        var where = $"{{ and: [ {PathEq("[\"Slots\"]", "5")}, {PathEq("[\"Weight\"]", "0.5")} ] }}";

        Assert.Equal(["A1"], await NamesAsync(scope, where));
    }

    [Fact]
    public async Task A_path_and_a_value_may_come_from_variables()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""), ("B1", """{"Slots":3}"""));

        var request = new OperationRequestBuilder()
            .SetDocument("""
                query ($path: [String!]!, $value: Any) {
                  items(where: { props: { pathEq: { path: $path, value: $value } } }) { nodes { name } }
                }
                """)
            .SetVariableValues(new Dictionary<string, object?> { ["path"] = new[] { "Slots" }, ["value"] = 5 })
            .Build();

        var response = JsonDocument.Parse(await ApiHarness.RunAsync(scope, request)).RootElement;

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        Assert.Equal(["A1"], ApiHarness.Nodes(response, "items").EnumerateArray().Select(n => n.GetProperty("name").GetString()!));
    }

    [Fact]
    public async Task A_filter_term_on_a_JSON_column_keeps_the_other_filters()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""), ("A2", """{"Slots":5}"""), ("B1", """{"Slots":3}"""));

        var names = await NamesAsync(scope, """{ name: { startsWith: "A" }, props: { pathEq: { path: ["Slots"], value: 5 }, pathNeq: { path: ["Nested"], value: 1 } } }""");

        Assert.Equal(["A1", "A2"], names);
    }

    [Fact]
    public async Task A_path_segment_is_data_and_never_part_of_the_statement()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"we\"ird,key}'--":"v'\\"}"""));

        var commands = new List<string>();
        var response = await ApiHarness.JsonAsync(scope, Items(PathEq("[\"we\\\"ird,key}'--\"]", "\"v'\\\\\"")), commands);

        Assert.Equal(["A1"], ApiHarness.Nodes(response, "items").EnumerateArray().Select(n => n.GetProperty("name").GetString()!));
        Assert.DoesNotContain(commands, c => c.Contains("ird,key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_JSON_column_offers_the_same_filter()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

        var schema = await ApiHarness.JsonAsync(
            scope,
            "{ __schema { types { name kind inputFields { name type { name kind ofType { name } } } } } }");
        var types = schema.GetProperty("data").GetProperty("__schema").GetProperty("types").EnumerateArray()
            .Where(t => t.GetProperty("kind").GetString() == "INPUT_OBJECT")
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("inputFields").EnumerateArray().ToArray());

        string[] operations = ["and", "or", "eq", "neq", "pathEq", "pathNeq"];
        Assert.Equal(operations.Order(), types["JsonOperationFilterInput"].Select(f => f.GetProperty("name").GetString()!).Order());

        // Every jsonb column of an exposed entity has a filter field of that type, so a new column cannot
        // miss it, and every table that holds one is exposed: the GraphQL view serves the rows of a table that
        // has no mod_* row of its own as well (ADR-0041, ADR-0032 action item 7). A new table is in this test
        // on the day it is mapped.
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

    [Fact]
    public async Task A_path_filter_extracts_the_path_in_the_database_and_never_matches_text()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var commands = new List<string>();
        var response = await ApiHarness.JsonAsync(scope, Items(PathEq("[\"Slots\", \"x\"]", "5")), commands);
        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());

        var select = Assert.Single(commands, c => c.Contains("FROM items", StringComparison.Ordinal));
        Assert.Contains("jsonb_extract_path(", select);
        Assert.DoesNotContain("LIKE", select, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Slots", select); // the path is a parameter and not part of the statement
    }

    [Fact]
    public async Task A_value_that_is_an_object_or_a_list_compares_by_content()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Nested":{"a":1,"b":[1,2]}}"""), ("B1", """{"Nested":{"a":1}}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, PathEq("[\"Nested\"]", "{ b: [1, 2], a: 1 }")));
        Assert.Equal(["A1"], await NamesAsync(scope, PathEq("[\"Nested\", \"b\"]", "[1, 2]")));
        Assert.Empty(await NamesAsync(scope, PathEq("[\"Nested\", \"b\"]", "[2, 1]")));
    }

    [Fact]
    public async Task A_value_nested_too_deep_is_refused()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var deep = new string('[', 64) + new string(']', 64);
        var response = await ApiHarness.JsonAsync(scope, Items(PathEq("[\"Slots\"]", deep)));

        Assert.Equal("JSON_VALUE_MALFORMED", ApiHarness.Error(response).Code);
    }

    [Fact]
    public async Task A_large_integer_is_not_rounded()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Id":12345678901234567890}"""), ("B1", """{"Id":12345678901234567891}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, PathEq("[\"Id\"]", "12345678901234567890")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"a\", \"\"]")]
    [InlineData("[\"a\\u0000b\"]")]
    public async Task A_bad_path_is_refused_with_its_own_error_code(string path)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var response = await ApiHarness.JsonAsync(scope, Items(PathEq(path, "5")));

        Assert.Equal("JSON_PATH_MALFORMED", ApiHarness.Error(response).Code);
        Assert.False(response.GetProperty("data").GetProperty("items").ValueKind == JsonValueKind.Object);
    }

    [Theory]
    [InlineData(11, 1, "JSON_PATH_MALFORMED")]
    [InlineData(10, 1, null)]
    [InlineData(1, 257, "JSON_PATH_MALFORMED")]
    [InlineData(1, 256, null)]
    public async Task The_size_of_a_path_is_bounded(int segments, int length, string? code)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var path = "[" + string.Join(", ", Enumerable.Repeat($"\"{new string('k', length)}\"", segments)) + "]";
        var response = await ApiHarness.JsonAsync(scope, Items(PathEq(path, "5")));

        if (code is null)
        {
            Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        }
        else
        {
            Assert.Equal(code, ApiHarness.Error(response).Code);
        }
    }

    [Fact]
    public async Task A_missing_value_is_refused()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var response = await ApiHarness.JsonAsync(scope, Items("""{ props: { pathEq: { path: ["Slots"] } } }"""));

        Assert.Equal("JSON_VALUE_MALFORMED", ApiHarness.Error(response).Code);
    }

    [Theory]
    [InlineData("1e999999")]
    [InlineData("\"a\\u0000b\"")]
    public async Task A_value_the_database_refuses_is_an_error_and_not_an_internal_error(string value)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var response = await ApiHarness.JsonAsync(scope, Items(PathEq("[\"Slots\"]", value)));

        var (code, message) = ApiHarness.Error(response);
        Assert.Equal("JSON_VALUE_REJECTED", code);
        Assert.DoesNotContain("Unexpected Execution Error", message);
    }

    [Fact]
    public async Task A_whole_document_comparison_with_text_that_is_not_JSON_is_refused()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var response = await ApiHarness.JsonAsync(scope, Items("""{ props: { eq: "x" } }"""));

        var (code, message) = ApiHarness.Error(response);
        Assert.Equal("JSON_VALUE_MALFORMED", code);
        Assert.Contains("JSON text", message);
        Assert.Contains("\"\\\"x\\\"\"", message); // the message shows how to write a string
        Assert.DoesNotContain("Unexpected Execution Error", message);
    }

    [Theory]
    [InlineData("\"x\"", "\"\\\"x\\\"\"")]
    [InlineData("5", "\"5\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("null", "\"null\"")]
    public async Task A_whole_document_comparison_takes_a_JSON_scalar_written_as_JSON_text(string document, string text)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", document), ("B1", """{"Slots":5}"""));

        Assert.Equal(["A1"], await NamesAsync(scope, $"{{ props: {{ eq: {text} }} }}"));
        Assert.Equal(["B1"], await NamesAsync(scope, $"{{ props: {{ neq: {text} }} }}"));
    }

    [Fact]
    public async Task A_full_page_with_a_path_filter_is_inside_the_cost_limit()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        const string everyFieldOfItem =
            "sptVersion id name parentId type prototype weight width height stackMaxSize props";

        var response = await ApiHarness.RunAsync(
            scope,
            "{ items(first: 200, where: " + PathEq("[\"Slots\"]", "5") + ") { nodes { " + everyFieldOfItem + " } } }");

        Assert.DoesNotContain("HC0047", response);
        Assert.Contains("\"nodes\"", response);
    }

    [Fact]
    public async Task A_filter_with_thousands_of_terms_is_refused_by_the_cost_limit()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("A1", """{"Slots":5}"""));

        var terms = string.Join(", ", Enumerable.Range(0, 3000).Select(i => PathEq($"[\"k{i}\"]", i.ToString())));
        var response = await ApiHarness.RunAsync(scope, "{ items(where: { or: [" + terms + "] }) { nodes { name } } }");

        Assert.Contains("HC0047", response); // "The maximum allowed field cost was exceeded."
    }
}
