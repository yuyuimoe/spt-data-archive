using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SptArchive.Api.GraphQL;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The server release argument (TD-21, ADR-0014, ADR-0019) and the JSON values (TD-15), checked
/// against the schema that the Api serves. Each test writes its rows inside a transaction that it
/// rolls back, and the executor reads through the same connection.
/// </summary>
public sealed class ApiTests
{
    // Higher than any real release, so these rows are the "latest" inside the test transaction.
    private const string Old = "9990.0.1";
    private const string New = "9990.0.2";

    private static async Task SeedAsync(TestScope scope)
    {
        foreach (var version in new[] { Old, New })
        {
            await SptVersionRegistry.EnsureAsync(scope.Db, version, TestContext.Current.CancellationToken);
        }

        scope.Db.Items.Add(Row(Old, "000000000000000000000001", "OldOnly"));
        scope.Db.Items.Add(Row(New, "000000000000000000000002", "NewOnly"));
        await scope.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Item Row(string release, string id, string name) => new()
    {
        SptVersion = release,
        Id = id,
        Name = name,
        ParentId = "0",
        Type = "Item",
        Props = """{"Weight":0.5,"Nested":{"list":[1,2]}}""",
    };

    private static async Task<string> RunAsync(TestScope scope, string query)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ArchiveDbContext>>(new ScopeFactory(scope));
        services.AddArchiveGraphQL();

        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(query, TestContext.Current.CancellationToken);

        // The wire format of the Api: the same formatter that answers an HTTP request.
        using var stream = new MemoryStream();
        var writer = System.IO.Pipelines.PipeWriter.Create(stream);
        await new HotChocolate.Transport.Formatters.JsonResultFormatter(indented: false)
            .FormatAsync(result, writer, default, TestContext.Current.CancellationToken);
        await writer.CompleteAsync();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<JsonElement> JsonAsync(TestScope scope, string query)
    {
        var result = await RunAsync(scope, query);
        return JsonDocument.Parse(result).RootElement.Clone();
    }

    private static string[] Names(JsonElement response) =>
        [.. response.GetProperty("data").GetProperty("items").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("name").GetString()!)];

    private static (string Code, string Message) Error(JsonElement response)
    {
        var error = response.GetProperty("errors")[0];
        return (error.GetProperty("extensions").GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task A_jsonb_column_offers_comparison_only()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        // A text operation on a jsonb column becomes LIKE, which Postgres refuses; the schema must not
        // announce it at all, so the client learns at query validation instead of from a failed request.
        var refused = await JsonAsync(scope, """{ items(where: { props: { startsWith: "{" } }) { nodes { name } } }""");
        var message = refused.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains("startsWith", message);
        Assert.Contains("does not exist", message);

        // Comparison does work, and compares the content of the documents, not their text.
        var matched = await JsonAsync(
            scope,
            """{ items(where: { props: { eq: "{\"Nested\":{\"list\":[1,2]},\"Weight\":0.5}" } }) { nodes { name } } }""");
        Assert.Equal(["NewOnly"], Names(matched));

        // A text column keeps every operation, including the pattern ones.
        var text = await JsonAsync(scope, """{ items(where: { name: { contains: "New" } }) { nodes { name } } }""");
        Assert.Equal(["NewOnly"], Names(text));
    }

    [Fact]
    public async Task No_version_answers_from_the_latest_release()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, "{ items { nodes { name sptVersion } } }");

        Assert.Equal(["NewOnly"], Names(response));
        Assert.Equal(New, response.GetProperty("data").GetProperty("items").GetProperty("nodes")[0].GetProperty("sptVersion").GetString());
    }

    [Fact]
    public async Task A_version_answers_from_that_release_only()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, $$"""{ items(sptVersion: "{{Old}}") { nodes { name sptVersion } } }""");

        Assert.Equal(["OldOnly"], Names(response));
        Assert.Equal(Old, response.GetProperty("data").GetProperty("items").GetProperty("nodes")[0].GetProperty("sptVersion").GetString());
    }

    [Fact]
    public async Task The_version_also_scopes_a_single_row_lookup()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, $$"""{ item(id: "000000000000000000000001", sptVersion: "{{New}}") { name } }""");

        Assert.Equal(JsonValueKind.Null, response.GetProperty("data").GetProperty("item").ValueKind);
    }

    [Fact]
    public async Task An_unknown_version_is_an_error_that_names_it_and_the_latest_release()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, """{ items(sptVersion: "9999.9.9") { nodes { name } } }""");

        var (code, message) = Error(response);
        Assert.Equal(Releases.UnknownCode, code);
        Assert.Contains("'9999.9.9'", message);
        Assert.Contains($"'{New}'", message);
        Assert.False(response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object);
    }

    [Theory]
    [InlineData("^9990.0.1")]
    [InlineData(">=4.0.0 <4.1.0")]
    [InlineData("latest")]
    [InlineData("4.0")]
    [InlineData("")]
    public async Task A_value_that_is_not_a_release_key_is_a_different_error(string value)
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, $$"""{ items(sptVersion: "{{value}}") { nodes { name } } }""");

        var (code, message) = Error(response);
        Assert.Equal(Releases.MalformedCode, code);
        Assert.NotEqual(Releases.UnknownCode, code);
        Assert.Contains($"'{value}'", message);
        Assert.Contains("one exact archived release", message);
    }

    [Fact]
    public async Task The_release_list_is_newest_first()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, "{ sptVersions { version } }");

        var versions = response.GetProperty("data").GetProperty("sptVersions").EnumerateArray().Select(v => v.GetProperty("version").GetString()).ToList();
        Assert.Equal([New, Old], versions.Take(2));
    }

    [Fact]
    public async Task A_jsonb_column_is_a_json_value_and_not_a_string()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope);

        var response = await JsonAsync(scope, "{ items { nodes { props } } }");

        var props = response.GetProperty("data").GetProperty("items").GetProperty("nodes")[0].GetProperty("props");
        Assert.Equal(JsonValueKind.Object, props.ValueKind);
        Assert.Equal(0.5, props.GetProperty("Weight").GetDouble());
        Assert.Equal(2, props.GetProperty("Nested").GetProperty("list")[1].GetInt32());
    }

    private sealed class ScopeFactory(TestScope scope) : IDbContextFactory<ArchiveDbContext>
    {
        public ArchiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseNpgsql(scope.Connection)
                .UseSnakeCaseNamingConvention()
                .Options;

            var db = new ArchiveDbContext(options);
            db.Database.UseTransaction(scope.Transaction);
            return db;
        }
    }
}
