using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>
/// The guard in front of the Api (ADR-0038, findings F-01 to F-03 and F-06 of the security audit of
/// 2026-10-21). Each test starts the Api in this process and talks to it as a client does, so it checks
/// the pipeline that the deployment runs and not a copy of it. No database is touched: every query here
/// is answered from the schema alone.
/// </summary>
public sealed class EdgeTests
{
    private const string ArchiveConnection = "Host=127.0.0.1;Port=5432;Database=archive;Username=archive_reader;Password=unused";
    private const string Query = """{"query":"{ __typename }"}""";

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static WebApplicationFactory<Program> Factory(params (string Key, string? Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Archive", ArchiveConnection);
            builder.UseSetting("ConnectionStrings:Redis", null);

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_answer_states_what_a_browser_must_not_do()
    {
        using var factory = Factory();

        foreach (var path in new[] { "/graphql", "/healthz" })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (path == "/graphql")
            {
                request.Content = Json(Query);
            }

            using var response = await Client(factory).SendAsync(request, Ct);

            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        }
    }

    [Fact]
    public async Task A_client_that_asks_too_often_is_refused()
    {
        using var factory = Factory(
            ("RateLimit:Enabled", "true"),
            ("RateLimit:TokenLimit", "2"),
            ("RateLimit:TokensPerPeriod", "2"),
            ("RateLimit:ReplenishSeconds", "600"));
        using var client = Client(factory);

        var answers = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            answers.Add(await client.PostAsync("/graphql", Json(Query), Ct));
        }

        Assert.Equal(HttpStatusCode.OK, answers[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, answers[1].StatusCode);

        var refused = answers[2];
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal("RATE_LIMITED",
            body.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_health_check_is_never_refused()
    {
        using var factory = Factory(
            ("RateLimit:Enabled", "true"),
            ("RateLimit:TokenLimit", "1"),
            ("RateLimit:TokensPerPeriod", "1"),
            ("RateLimit:ReplenishSeconds", "600"));
        using var client = Client(factory);

        // The bucket is spent on the query endpoint first, so the health check cannot be passing by luck.
        await client.PostAsync("/graphql", Json(Query), Ct);
        var refused = await client.PostAsync("/graphql", Json(Query), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        for (var i = 0; i < 5; i++)
        {
            var health = await client.GetAsync("/healthz", Ct);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task An_answer_is_compressed_when_the_client_asks_for_it(string encoding)
    {
        using var factory = Factory();
        using var client = Client(factory);

        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = Json(Query) };
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));

        await using var stream = encoding == "gzip"
            ? (Stream)new GZipStream(await response.Content.ReadAsStreamAsync(Ct), CompressionMode.Decompress)
            : new BrotliStream(await response.Content.ReadAsStreamAsync(Ct), CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var decoded = await reader.ReadToEndAsync(Ct);

        Assert.Equal("Query", JsonDocument.Parse(decoded).RootElement.GetProperty("data").GetProperty("__typename").GetString());
    }

    [Fact]
    public async Task The_cache_stores_one_answer_for_every_client()
    {
        using var factory = Factory();
        using var client = Client(factory);

        // First client accepts compression. The answer it stores must not be its compressed bytes.
        var compressed = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = Json(Query) };
        compressed.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var compressedAnswer = await client.SendAsync(compressed, Ct);
        Assert.Equal("gzip", Assert.Single(compressedAnswer.Content.Headers.ContentEncoding));
        Assert.Equal("MISS", Assert.Single(compressedAnswer.Headers.GetValues("X-Cache")));

        // Second client accepts nothing, gets the same stored answer, and reads it without decoding.
        using var plain = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = Json(Query) }, Ct);

        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal("HIT", Assert.Single(plain.Headers.GetValues("X-Cache")));

        var text = await plain.Content.ReadAsStringAsync(Ct);
        Assert.Equal("Query", JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("__typename").GetString());
    }

    [Fact]
    public async Task The_reader_pool_never_asks_for_more_connections_than_the_server_allows()
    {
        using var factory = Factory(("Database:MaxPoolSize", "30"));

        await using var scope = factory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ArchiveDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(Ct);

        var connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        Assert.Equal(30, connection.MaxPoolSize);
        Assert.Equal(5, connection.Timeout);
        Assert.Equal(1800, connection.ConnectionLifetime);
    }
}
