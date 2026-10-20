using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SptArchive.Api.GraphQL;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>
/// The Api is public, so a client must not be able to burn the process with one request: a huge body,
/// a deep query or a wide query is refused before it reaches the database. These tests send the requests
/// to a real HTTP host built from the same <see cref="GraphQLSetup"/> as the Api.
/// </summary>
public sealed class ApiLimitsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddArchiveDatabase(ArchiveDatabase.ReaderConnectionString, readOnly: true);
        builder.Services.AddArchiveGraphQL();

        _app = builder.Build();
        _app.MapGraphQL();
        await _app.StartAsync(TestContext.Current.CancellationToken);

        var address = _app.Urls.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string query)
    {
        var json = JsonSerializer.Serialize(new { query });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/graphql", content, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_body_over_the_limit_is_refused()
    {
        var padding = new string(' ', GraphQLSetup.MaxRequestBytes + 1);

        var (status, body) = await PostAsync("{ sptVersions { version } }" + padding);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.DoesNotContain("\"data\"", body);
    }

    [Fact]
    public async Task A_query_deeper_than_the_limit_is_refused()
    {
        // Eleven levels of selection sets, one more than the depth rule allows.
        var query = "{ a " + string.Concat(Enumerable.Repeat("{ b ", 11)) + string.Concat(Enumerable.Repeat("}", 12));

        var (_, body) = await PostAsync(query);

        Assert.Contains("errors", body);
        Assert.DoesNotContain("\"data\":{", body);
    }

    [Fact]
    public async Task A_wide_query_over_the_cost_limit_is_refused()
    {
        // Twelve full pages of 200 rows in one request: shallow, but 2,400 objects.
        var lists = string.Join(" ", Enumerable.Range(0, 12).Select(i => $"l{i}: items(sptVersion: \"4.0.13\", first: 200) {{ nodes {{ id }} }}"));

        var (_, body) = await PostAsync("{ " + lists + " }");

        Assert.Contains("HC0047", body); // "The maximum allowed type cost was exceeded."
    }

    [Fact]
    public async Task A_normal_page_is_inside_the_cost_limit()
    {
        var (_, body) = await PostAsync("""{ items(sptVersion: "4.0.13", first: 200) { nodes { id } } }""");

        Assert.DoesNotContain("COST", body.ToUpperInvariant());
        Assert.DoesNotContain("HC0047", body);
    }

    [Fact]
    public async Task Introspection_stays_on()
    {
        var (status, body) = await PostAsync("{ __schema { queryType { name } } }");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"Query\"", body);
    }
}
