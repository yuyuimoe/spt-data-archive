using System.Text;
using System.Text.Json;
using SptArchive.Api.GraphQL;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The rows of <c>mod_database_objects</c> as the GraphQL view serves them (ADR-0041, ADR-0040 decision 8):
/// the small facts of a row cost what a field costs, and the whole document is one field that costs a
/// hundred times as much and answers an error above a limit of bytes.
/// </summary>
public sealed class ObjectApiTests
{
    // The mod that the archive holds, and the two files of its lighthouse map.
    private const string ModGuid = "com.example.maps";
    private const string Lighthouse = "lighthouse";
    private const string DatasetId = "01a15b33-5bca-7a04-8279-0792258f0158";

    [Fact]
    public async Task A_location_states_one_row_for_each_file()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"Id":"lighthouse","Loot":[]}"""), ("looseLoot", """{"spawnpoints":[]}"""));

        var response = await ApiHarness.JsonAsync(
            scope,
            "{ modDatabaseObjects(first: 10) { nodes { kind templateId part bytes } } }");

        var nodes = ApiHarness.Nodes(response, "modDatabaseObjects").EnumerateArray().ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.Equal(["base", "looseLoot"], nodes.Select(n => n.GetProperty("part").GetString()!).ToArray());
        Assert.All(nodes, n => Assert.Equal("location", n.GetProperty("kind").GetString()));

        // The size is a fact of the row, so a client learns that a document is 132 MB before it asks for it.
        Assert.Equal(
            [Encoding.UTF8.GetByteCount("""{"Id":"lighthouse","Loot":[]}"""), Encoding.UTF8.GetByteCount("""{"spawnpoints":[]}""")],
            nodes.Select(n => n.GetProperty("bytes").GetInt64()).ToArray());
    }

    [Fact]
    public async Task The_whole_document_comes_as_json_and_the_stored_text_is_not_a_field()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"Id":"lighthouse","Loot":[{"a":1}]}"""));

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""
            {
              modDatabaseObject(datasetId: "{{DatasetId}}", kind: "location", templateId: "{{Lighthouse}}", part: "base") {
                bytes
                rawJson
              }
            }
            """);

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        var row = response.GetProperty("data").GetProperty("modDatabaseObject");
        Assert.Equal(1, row.GetProperty("rawJson").GetProperty("Loot")[0].GetProperty("a").GetInt32());

        // A string of the stored document is not a field of its own: a client reads JSON, or nothing.
        var type = await ApiHarness.JsonAsync(
            scope,
            """{ __type(name: "ModDatabaseObject") { fields { name } } }""");
        var fields = type.GetProperty("data").GetProperty("__type").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("rawJson", fields);
        Assert.Contains("bytes", fields);
        Assert.DoesNotContain("data", fields);
    }

    [Fact]
    public async Task A_page_of_whole_documents_is_refused_before_the_query_runs()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"Id":"lighthouse"}"""));

        // Two hundred documents cost 200 x 100, which is more than the field cost of a query. The cost is
        // decided when the query is validated, so no document is read and the database is not touched.
        var commands = new List<string>();
        var response = await ApiHarness.JsonAsync(
            scope,
            "{ modDatabaseObjects(first: 200) { nodes { templateId rawJson } } }",
            commands);

        Assert.Empty(commands);
        var (code, _) = ApiHarness.Error(response);
        Assert.Equal("HC0047", code);
    }

    [Fact]
    public async Task A_small_page_of_whole_documents_is_served()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"Id":"lighthouse"}"""));

        // Ten documents cost 1 000, well inside the limit of the field cost: the field is expensive, not banned.
        var response = await ApiHarness.JsonAsync(
            scope,
            "{ modDatabaseObjects(first: 10) { nodes { templateId rawJson } } }");

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        Assert.Single(ApiHarness.Nodes(response, "modDatabaseObjects").EnumerateArray());
    }

    [Fact]
    public async Task A_document_above_the_limit_is_refused_and_states_its_size()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"Id":"lighthouse"}"""));

        // A limit of ten bytes, and the document is larger. The refusal names both numbers, so a client
        // learns what to ask for instead of receiving an empty answer.
        var response = await ApiHarness.JsonAsync(
            scope,
            "{ modDatabaseObjects(first: 10) { nodes { rawJson } } }",
            rawJson: limits => limits.MaxValueBytes = 10);

        var (code, message) = ApiHarness.Error(response);
        Assert.Equal(RawJsonErrors.ValueTooLargeCode, code);
        Assert.Contains(Encoding.UTF8.GetByteCount("""{"Id":"lighthouse"}""").ToString(), message);
        Assert.Contains("10", message);
    }

    [Fact]
    public async Task A_request_that_carries_too_many_documents_is_refused()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"a":1}"""), ("looseLoot", """{"b":2}"""));

        // Each document is 7 bytes and the request may carry 10, so one of the two is refused. The cost
        // analysis allows the query: it bounds the shape, and this bounds the bytes.
        var response = await ApiHarness.JsonAsync(
            scope,
            "{ modDatabaseObjects(first: 10) { nodes { rawJson } } }",
            rawJson: limits =>
            {
                limits.MaxValueBytes = 1_000;
                limits.MaxRequestBytes = 10;
            });

        var (code, _) = ApiHarness.Error(response);
        Assert.Equal(RawJsonErrors.RequestLimitCode, code);
    }

    [Fact]
    public async Task A_path_filter_reads_the_value_at_a_path_in_the_database()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("base", """{"a":{"b":5}}"""), ("looseLoot", """{"a":{"b":6}}"""));

        var commands = new List<string>();
        var response = await ApiHarness.JsonAsync(
            scope,
            """
            {
              modDatabaseObjects(first: 10, where: { data: { pathEq: { path: ["a", "b"], value: 5 } } }) {
                nodes { part }
              }
            }
            """,
            commands);

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        Assert.Equal("base", Assert.Single(ApiHarness.Nodes(response, "modDatabaseObjects").EnumerateArray()).GetProperty("part").GetString());
        Assert.Contains(commands, c => c.Contains("jsonb_extract_path(", StringComparison.Ordinal));
    }

    private static async Task SeedAsync(TestScope scope, params (string Part, string Data)[] files)
    {
        var token = TestContext.Current.CancellationToken;
        var dataset = new ModDataset
        {
            Id = Guid.Parse(DatasetId),
            ModGuid = ModGuid,
            ContentHash = "sha256:maps",
            TableHashes = """{"locations":"sha256:locations"}""",
            RowCounts = """{"locations":1}""",
        };

        scope.Db.ModDatasets.Add(dataset);
        scope.Db.ModTemplates.Add(new ModTemplate
        {
            DatasetId = dataset.Id,
            Kind = ModTemplateKind.Location,
            TemplateId = Lighthouse,
            Op = ModTemplateOperation.Added,
        });

        foreach (var (part, data) in files)
        {
            scope.Db.ModDatabaseObjects.Add(new ModDatabaseObject
            {
                DatasetId = dataset.Id,
                Kind = ModTemplateKind.Location,
                TemplateId = Lighthouse,
                Part = part,
                Data = data,
                Bytes = Encoding.UTF8.GetByteCount(data),
            });
        }

        await scope.Db.SaveChangesAsync(token);
    }
}
