using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HotChocolate.AspNetCore;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Reqnroll;
using SptArchive.Api.GraphQL;
using SptArchive.Core;
using SptArchive.Core.Data;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/query-api.feature</c> that send an HTTP request to the Api: the cache and its
/// idempotency key, the limits of the request, the guard (ADR-0008, ADR-0037, ADR-0038). Each scenario starts
/// the Api in this process (<see cref="WebApplicationFactory{TEntryPoint}"/>), so the requests pass the pipeline
/// that the deployment runs. The Api reads the test transaction of the scenario (see <see cref="Scoped"/>),
/// except where several requests run at the same time, because a transaction holds one connection.
/// </summary>
[Binding]
public sealed class QueryApiHttpSteps(QueryWorld world) : IDisposable
{
    private const string Query = """{"query":"{ sptVersions { version } }"}""";

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["ten"] = 10, ["twenty"] = 20,
    };

    private readonly List<(string Key, string? Value)> _settings = [];
    private readonly List<Answer> _answers = [];
    private readonly List<string> _warnings = [];
    private WebApplicationFactory<Program>? _factory;
    private string _query = Query;
    private string? _idempotencyKey;
    private string? _acceptEncoding;
    private bool _scoped = true;
    private IPAddress? _peer;
    private int _queriesRun;
    private int _slowSeconds;
    private TimeSpan _gate = TimeSpan.Zero;

    /// <summary>One answer of the Api: the status, the headers of the answer and of its content, and the body.</summary>
    private sealed record Answer(HttpStatusCode Status, Dictionary<string, string> Headers, byte[] Body, TimeSpan Elapsed)
    {
        public string Text => Encoding.UTF8.GetString(Body);

        /// <summary>The body after the content encoding is undone, as the client reads it.</summary>
        public string Decoded
        {
            get
            {
                if (!Headers.TryGetValue("Content-Encoding", out var encoding))
                {
                    return Text;
                }

                using var input = new MemoryStream(Body);
                using Stream decoder = encoding == "gzip"
                    ? new GZipStream(input, CompressionMode.Decompress)
                    : new BrotliStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(decoder, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }
    }

    public void Dispose() => _factory?.Dispose();

    // ----------------------------------------------------------------------------------- Setting up

    [Given("the cache is empty")]
    [Given("the cache holds no answer for the query")]
    public void GivenTheCacheIsEmpty()
    {
        // Each scenario starts its own Api with its own memory cache, so the cache is empty.
        Assert.Null(_factory);
    }

    [Given("a cached answer exists")]
    public async Task GivenACachedAnswerExists()
    {
        await SendAsync();
        await SendAsync();
        Assert.Equal("HIT", Last().Headers["X-Cache"]);
    }

    [Given("a query that returns GraphQL errors")]
    public void GivenAQueryThatReturnsErrors() =>
        _query = """{"query":"{ items(sptVersion: \"9.9.9\") { nodes { id } } }"}""";

    [Given("Redis is not reachable")]
    [Given("the cache is not reachable")]
    public void GivenRedisIsNotReachable()
    {
        // Nothing listens on port 1, so this is the real Redis client facing a Redis that is down (ADR-0008).
        _settings.Add(("ConnectionStrings:Redis", "127.0.0.1:1"));
    }

    [Given(@"^the Api allows a burst of (\w+) requests$")]
    public void GivenTheApiAllowsABurst(string number) => Burst(Numbers[number]);

    [Given("the Api believes the address of one proxy")]
    public void GivenTheApiBelievesTheAddressOfOneProxy()
    {
        _peer = IPAddress.Parse("10.89.0.5");
        _settings.Add(("RateLimit:TrustForwardedFor", "true"));
        _settings.Add(("RateLimit:TrustedProxies", "10.89.0.5"));

        if (!_settings.Any(setting => setting.Key == "RateLimit:TokenLimit"))
        {
            Burst(3);
        }
    }

    [Given("a client accepts {string}")]
    public void GivenAClientAccepts(string encoding) => _acceptEncoding = encoding;

    [Given("a client asked for a query with {string}")]
    public async Task GivenAClientAskedForAQueryWith(string header)
    {
        _acceptEncoding = header.Split(": ")[1];
        await SendAsync();
        _acceptEncoding = null;
        Assert.Equal("MISS", Last().Headers["X-Cache"]);
    }

    // ------------------------------------------------------------------------------- Sending

    [When("I send the same query twice")]
    [When("I send it twice")]
    public async Task WhenISendTheSameQueryTwice()
    {
        await SendAsync();
        await SendAsync();
    }

    [When("I send a query that asks for a release the archive does not hold, twice")]
    public async Task WhenISendAQueryForAMissingRelease()
    {
        GivenAQueryThatReturnsErrors();
        await SendAsync();
        await SendAsync();
    }

    [When("I send the same query")]
    [When("a client sends a query")]
    [When("the client sends a query")]
    [When("I send a valid query")]
    public Task WhenISendTheQuery() => SendAsync();

    [When("another client asks for the same query without accepting compression")]
    public Task WhenAnotherClientAsks() => SendAsync();

    [When("I send five queries")]
    public async Task WhenISendFiveQueries()
    {
        for (var i = 0; i < 5; i++)
        {
            await SendAsync();
        }
    }

    [When("the Archiver finishes a successful run")]
    public async Task WhenTheArchiverFinishesARun()
    {
        // The Archiver announces a run to the Api by writing a new data version to the cache (ArchiveJob).
        await Factory().Services.GetRequiredService<IDistributedCache>()
            .SetStringAsync(CacheKeys.DataVersion, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
    }

    [When("I send a query body larger than {int} KB")]
    public Task WhenISendABodyLargerThanKb(int kilobytes) => SendBodyAsync(kilobytes * 1024 + 1);

    [When(@"^I send a query body larger than ([0-9,]+) bytes$")]
    public Task WhenISendABodyLargerThan(string bytes) => SendBodyAsync(int.Parse(bytes.Replace(",", "")) + 1);

    [When("I request {string}")]
    public async Task WhenIRequest(string path)
    {
        using var client = NewClient();
        await RecordAsync(await client.GetAsync(path, TestContext.Current.CancellationToken), TimeSpan.Zero);
    }

    [When(@"^one client sends (\w+) requests in a row$")]
    public async Task WhenOneClientSendsRequests(string number)
    {
        for (var i = 0; i < Numbers[number]; i++)
        {
            await SendAsync();
        }
    }

    [When(@"^one client sends (\w+) requests to ""/healthz""$")]
    public async Task WhenOneClientSendsRequestsToHealth(string number)
    {
        // The bucket is spent on the query endpoint first, so the health check cannot pass by luck.
        for (var i = 0; i < 3; i++)
        {
            await SendAsync();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, Last().Status);
        _answers.Clear();

        using var client = NewClient();
        for (var i = 0; i < Numbers[number]; i++)
        {
            await RecordAsync(await client.GetAsync("/healthz", TestContext.Current.CancellationToken), TimeSpan.Zero);
        }
    }

    [When("I query {string} for {string} and then for {string}")]
    public async Task WhenIQueryForAndThenFor(string field, string first, string second)
    {
        foreach (var release in new[] { first, second })
        {
            _query = Body($"{{ {field}(sptVersion: \"{release}\") {{ nodes {{ id sptVersion }} }} }}");
            await SendAsync();
        }
    }

    [When("I send a path filter for the value {int} and then for the value {int}")]
    public async Task WhenISendPathFilters(int first, int second)
    {
        await SendFilterAsync(first);
        await SendFilterAsync(second);
    }

    [When("I send the filter for the value {int} again")]
    public Task WhenISendTheFilterAgain(int value) => SendFilterAsync(value);

    [When(@"^I send a query with the idempotency key ""([^""]*)""$")]
    public Task WhenISendAQueryWithTheKey(string key)
    {
        _idempotencyKey = key;
        return SendAsync();
    }

    [When("I send the same query with the same idempotency key")]
    public Task WhenISendTheSameQueryWithTheSameKey() => SendAsync();

    [When("I send another query with the same idempotency key")]
    public Task WhenISendAnotherQueryWithTheSameKey()
    {
        _query = Body("{ languages { nodes { code } } }");
        return SendAsync();
    }

    [When("twenty clients send the same query at the same time")]
    public async Task WhenTwentyClientsSendTheSameQuery()
    {
        // The leader parks in the request interceptor until every client has arrived at the cache, so the
        // other nineteen are waiting for its answer when it finishes.
        _scoped = false;
        _gate = TimeSpan.FromSeconds(1.5);

        // The factory builds its server on the first client. Twenty clients that ask at once would build twenty
        // servers, each with a cache of its own, so the clients are made before they send.
        var httpClients = Enumerable.Range(0, 20).Select(_ => NewClient()).ToArray();

        var clients = httpClients.Select(client => Task.Run(async () =>
        {
            var started = Stopwatch.StartNew();
            using var response = await client.PostAsync("/graphql", Content(), TestContext.Current.CancellationToken);
            return await ReadAsync(response, started.Elapsed);
        })).ToArray();

        _answers.AddRange(await Task.WhenAll(clients));
        Array.ForEach(httpClients, client => client.Dispose());
    }

    [Given("two clients behind the proxy each send four requests while the Api allows a burst of three")]
    public async Task GivenTwoClientsBehindTheProxy()
    {
        Burst(3);
        foreach (var client in new[] { "203.0.113.7", "203.0.113.8" })
        {
            for (var i = 0; i < 4; i++)
            {
                await SendAsync(forwardedFor: client, tag: client);
            }
        }
    }

    [When("a client behind the proxy sends a header that names another address")]
    public async Task WhenAClientSendsAForgedHeader()
    {
        // Caddy appends the address it saw to the header, so a forged entry stands to the left of the real one.
        for (var i = 0; i < 4; i++)
        {
            await SendAsync(forwardedFor: $"198.51.100.{50 + i}, 203.0.113.7", tag: "client");
        }

        // The forged entries named other addresses. A client at one of them still has its whole bucket.
        await SendAsync(forwardedFor: "198.51.100.50", tag: "named");
    }

    [Given("a query that runs longer than 10 seconds")]
    public void GivenASlowQuery() => _slowSeconds = 30;

    [When("I send it")]
    public async Task WhenISendIt()
    {
        // The database is made slow for real: before the Api reads, the connection sleeps. The Api stops the
        // request after its own limit of 10 seconds and cancels the sleep.
        var scope = await world.ScopeAsync();
        var started = Stopwatch.StartNew();
        var body = await SlowApi.RunAsync(scope, "{ sptVersions { version } }", _slowSeconds);
        _answers.Add(new Answer(HttpStatusCode.OK, [], Encoding.UTF8.GetBytes(body), started.Elapsed));
        world.Response = JsonDocument.Parse(body).RootElement.Clone();
    }

    [Then("the response has a timeout error")]
    public void ThenTheResponseHasATimeoutError()
    {
        Assert.True(world.Response.TryGetProperty("errors", out var errors), world.Response.GetRawText());
        Assert.Contains("timeout", errors[0].GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(Last().Elapsed.TotalSeconds, 9, 20);
    }

    // ------------------------------------------------------------------------------------ Reading

    [Then("the first response has the header {string} with the value {string}")]
    public void ThenTheFirstResponseHasTheHeader(string name, string value) => AssertHeader(_answers[0], name, value);

    [Then("the second response has the header {string} with the value {string}")]
    public void ThenTheSecondResponseHasTheHeader(string name, string value) => AssertHeader(_answers[1], name, value);

    [Then("both responses have the header {string} with the value {string}")]
    public void ThenBothResponsesHaveTheHeader(string name, string value)
    {
        Assert.Equal(2, _answers.Count);
        Assert.All(_answers, answer => AssertHeader(answer, name, value));
    }

    [Then("the response has the header {string} with the value {string}")]
    [Then("it has the header {string} with the value {string}")]
    public void ThenTheResponseHasTheHeaderWithTheValue(string name, string value) => AssertHeader(Last(), name, value);

    [Then("the response has the header {string}")]
    [Then("it has the header {string}")]
    public void ThenTheResponseHasTheHeader(string header)
    {
        // "Name: value" states a value, "Name" only that the header is there.
        var parts = header.Split(": ", 2);
        if (parts.Length == 2)
        {
            AssertHeader(Last(), parts[0], parts[1]);
        }
        else
        {
            Assert.True(Last().Headers.ContainsKey(parts[0]), $"No header '{parts[0]}'.");
        }
    }

    [Then("the response has no {string} header")]
    public void ThenTheResponseHasNoHeader(string name) => Assert.DoesNotContain(name, Last().Headers.Keys);

    [Then("both responses have the same body")]
    [Then("the second body is the same as the first body")]
    public void ThenBothHaveTheSameBody()
    {
        Assert.Equal(2, _answers.Count);
        Assert.Equal(_answers[0].Text, _answers[1].Text);
        Assert.Contains("\"data\"", _answers[0].Text);
    }

    [Then("the second response has the same body as the first")]
    public void ThenTheSecondHasTheSameBody()
    {
        ThenBothHaveTheSameBody();
    }

    [Then("the response has the data")]
    public void ThenTheResponseHasTheData()
    {
        Assert.Equal(HttpStatusCode.OK, Last().Status);
        Assert.True(world.Response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object, Last().Text);
        Assert.False(world.Response.TryGetProperty("errors", out _));
    }

    [Then("the Api logs a warning")]
    public void ThenTheApiLogsAWarning() => Assert.NotEmpty(_warnings);

    [Then("every response is a successful answer")]
    public void ThenEveryResponseIsSuccessful() =>
        Assert.All(_answers, answer =>
        {
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.Contains("\"data\"", answer.Text);
            Assert.DoesNotContain("\"errors\"", answer.Text);
        });

    [Then("no response has the header {string}")]
    public void ThenNoResponseHasTheHeader(string name) =>
        Assert.All(_answers, answer => Assert.DoesNotContain(name, answer.Headers.Keys));

    [Then("only one of them waited for the cache")]
    public void ThenOnlyOneWaited()
    {
        // The Redis client waits up to a second for a connection. The others skip the cache for 30 seconds.
        var slow = _answers.Where(answer => answer.Elapsed > TimeSpan.FromMilliseconds(700)).ToList();
        Assert.Single(slow);
        Assert.Same(_answers[0], slow[0]);
    }

    [Then("one query runs")]
    public void ThenOneQueryRuns() => Assert.Equal(1, _queriesRun);

    [Then("one response has the header {string} with the value {string}")]
    public void ThenOneResponseHasTheHeader(string name, string value) =>
        Assert.Single(_answers, answer => answer.Headers.GetValueOrDefault(name) == value);

    [Then("the other responses have the header {string} with the value {string}")]
    public void ThenTheOthersHaveTheHeader(string name, string value) =>
        Assert.Equal(19, _answers.Count(answer => answer.Headers.GetValueOrDefault(name) == value));

    [Then(@"^the response is a ""(\d+)""$")]
    public void ThenTheResponseIsA(string status) => Assert.Equal(int.Parse(status), (int)Last().Status);

    [Then("the response is not a successful answer")]
    public void ThenTheResponseIsNotSuccessful()
    {
        Assert.True(Last().Status != HttpStatusCode.OK || !Last().Text.Contains("\"data\":{"), Last().Text);
        Assert.DoesNotContain("\"data\":{", Last().Text);
    }

    [Then(@"^the third response status is (\d+)$")]
    public void ThenTheThirdResponseStatusIs(int status)
    {
        Assert.Equal(3, _answers.Count);
        Assert.Equal(status, (int)_answers[2].Status);
        Assert.Equal(HttpStatusCode.OK, _answers[0].Status);
        Assert.Equal(HttpStatusCode.OK, _answers[1].Status);
    }

    [Then(@"^the response status is (\d+)$")]
    public void ThenTheResponseStatusIs(int status) => Assert.Equal(status, (int)Last().Status);

    [Then(@"^every response status is (\d+)$")]
    public void ThenEveryResponseStatusIs(int status)
    {
        Assert.NotEmpty(_answers);
        Assert.All(_answers, answer => Assert.Equal(status, (int)answer.Status));
    }

    [Then("it decodes to the same JSON as the uncompressed answer")]
    public async Task ThenItDecodesToTheSameJson()
    {
        var compressed = Last();
        Assert.Equal("gzip", compressed.Headers["Content-Encoding"]);

        _acceptEncoding = null;
        await SendAsync();
        var plain = Last();

        Assert.False(plain.Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(JsonDocument.Parse(plain.Text).RootElement.GetRawText(), JsonDocument.Parse(compressed.Decoded).RootElement.GetRawText());
    }

    [Then("the answer is readable without decoding")]
    public void ThenTheAnswerIsReadableWithoutDecoding()
    {
        Assert.False(Last().Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(Last().Text).RootElement.GetProperty("data").ValueKind);
    }

    [Then("each client receives three answers and then a refusal")]
    public void ThenEachClientReceivesThree()
    {
        foreach (var client in new[] { "203.0.113.7", "203.0.113.8" })
        {
            var statuses = _answers.Where(answer => answer.Headers["X-Test-Tag"] == client).Select(answer => (int)answer.Status).ToList();
            Assert.Equal([200, 200, 200, 429], statuses);
        }
    }

    [Then("the request is charged to the address of the client")]
    public void ThenTheRequestIsChargedToTheClient()
    {
        var client = _answers.Where(answer => answer.Headers["X-Test-Tag"] == "client").Select(answer => (int)answer.Status).ToList();
        Assert.Equal([200, 200, 200, 429], client);

        // The address that the forged entry named was not charged.
        Assert.Equal(HttpStatusCode.OK, _answers.Single(answer => answer.Headers["X-Test-Tag"] == "named").Status);
    }

    [Then("the refusal says {string}")]
    public void ThenTheRefusalSays(string code)
    {
        var refused = _answers.First(answer => answer.Status == HttpStatusCode.TooManyRequests);
        using var body = JsonDocument.Parse(refused.Text);
        Assert.Equal(code, body.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------------------ Helpers

    private void Burst(int tokens)
    {
        _settings.RemoveAll(setting => setting.Key.StartsWith("RateLimit:Token", StringComparison.Ordinal) || setting.Key == "RateLimit:ReplenishSeconds");
        _settings.Add(("RateLimit:Enabled", "true"));
        _settings.Add(("RateLimit:TokenLimit", tokens.ToString()));
        _settings.Add(("RateLimit:TokensPerPeriod", tokens.ToString()));
        _settings.Add(("RateLimit:ReplenishSeconds", "600"));
    }

    private static string Body(string query) => JsonSerializer.Serialize(new { query });

    private Task SendFilterAsync(int value)
    {
        _query = Body($$"""{ items(where: { props: { pathEq: { path: ["Slots"], value: {{value}} } } }) { nodes { id } } }""");
        return SendAsync();
    }

    private Task SendBodyAsync(int bytes)
    {
        var padding = new string(' ', bytes);
        _query = Body("{ sptVersions { version } }" + padding);
        return SendAsync();
    }

    private StringContent Content() => new(_query, Encoding.UTF8, "application/json");

    private HttpClient NewClient() =>
        Factory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task SendAsync(string? forwardedFor = null, string? tag = null)
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = Content() };

        if (_acceptEncoding is not null)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(_acceptEncoding));
        }

        if (_idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", _idempotencyKey);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        var started = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var answer = await ReadAsync(response, started.Elapsed);

        if (tag is not null)
        {
            answer.Headers["X-Test-Tag"] = tag;
        }

        _answers.Add(answer);
        Publish(answer);
    }

    private async Task RecordAsync(HttpResponseMessage response, TimeSpan elapsed)
    {
        var answer = await ReadAsync(response, elapsed);
        _answers.Add(answer);
        Publish(answer);
        response.Dispose();
    }

    private static async Task<Answer> ReadAsync(HttpResponseMessage response, TimeSpan elapsed)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return new Answer(response.StatusCode, headers, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), elapsed);
    }

    /// <summary>The body of the last answer is what the shared steps ("the error code is") read.</summary>
    private void Publish(Answer answer)
    {
        try
        {
            world.Response = JsonDocument.Parse(answer.Decoded).RootElement.Clone();
        }
        catch (JsonException)
        {
            world.Response = default;
        }
    }

    private Answer Last() => _answers[^1];

    private static void AssertHeader(Answer answer, string name, string value)
    {
        Assert.True(answer.Headers.TryGetValue(name, out var actual), $"No header '{name}'.");
        Assert.Equal(value, actual);
    }

    private WebApplicationFactory<Program> Factory()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        var scope = _scoped ? world.ScopeAsync().GetAwaiter().GetResult() : null;
        var warnings = _warnings;
        var gate = _gate;
        var counter = () => Interlocked.Increment(ref _queriesRun);

        return _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Archive", ArchiveDatabase.ReaderConnectionString);
            builder.UseSetting("ConnectionStrings:Redis", null);

            foreach (var (key, value) in _settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                if (scope is not null)
                {
                    // The Api reads the transaction of the scenario, so it sees the rows that the steps wrote.
                    services.RemoveAll<IDbContextFactory<ArchiveDbContext>>();
                    services.AddSingleton<IDbContextFactory<ArchiveDbContext>>(new Scoped(scope));
                }

                // Program replaces the logging providers with Serilog, so the warnings are caught at the factory.
                services.AddSingleton<ILoggerFactory>(new LoggerFactory([new WarningProvider(warnings)]));

                if (_peer is { } peer)
                {
                    services.AddTransient<IStartupFilter>(_ => new PeerFilter(peer));
                }

                services.AddGraphQLServer().AddHttpRequestInterceptor(_ => new CountingInterceptor(counter, gate));
            });
        });
    }

    /// <summary>A context factory that hands out contexts on the open test transaction.</summary>
    private sealed class Scoped(TestScope scope) : IDbContextFactory<ArchiveDbContext>
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

    /// <summary>Counts the GraphQL requests that reach the executor, and holds each for a moment.</summary>
    private sealed class CountingInterceptor(Func<int> counter, TimeSpan gate) : DefaultHttpRequestInterceptor
    {
        public override async ValueTask OnCreateAsync(
            HttpContext context, IRequestExecutor requestExecutor, OperationRequestBuilder requestBuilder, CancellationToken cancellationToken)
        {
            counter();

            if (gate > TimeSpan.Zero)
            {
                await Task.Delay(gate, cancellationToken);
            }

            await base.OnCreateAsync(context, requestExecutor, requestBuilder, cancellationToken);
        }
    }

    /// <summary>Gives every request the address of the proxy as its peer, as Caddy does in the deployment.</summary>
    private sealed class PeerFilter(IPAddress peer) : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = peer;
                    return nextMiddleware();
                });
                next(app);
            };
    }

    private sealed class WarningProvider(List<string> warnings) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Catcher(warnings, categoryName);

        public void Dispose()
        {
        }

        private sealed class Catcher(List<string> warnings, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    lock (warnings)
                    {
                        warnings.Add($"{category}: {formatter(state, exception)}");
                    }
                }
            }
        }
    }
}

/// <summary>
/// The Api over a database that is slow for real: the connection sleeps before the first statement of a request
/// reads, and the sleep ends when the Api cancels the request at its limit of 10 seconds.
/// </summary>
internal static class SlowApi
{
    public static async Task<string> RunAsync(TestScope scope, string query, int sleepSeconds)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ArchiveDbContext>>(new Slow(scope, sleepSeconds));
        services.AddArchiveGraphQL();

        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorProvider>()
            .GetExecutorAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(new OperationRequestBuilder().SetDocument(query).Build(), TestContext.Current.CancellationToken);

        using var stream = new MemoryStream();
        var writer = System.IO.Pipelines.PipeWriter.Create(stream);
        await new HotChocolate.Transport.Formatters.JsonResultFormatter(indented: false)
            .FormatAsync(result, writer, default, TestContext.Current.CancellationToken);
        await writer.CompleteAsync();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class Slow(TestScope scope, int sleepSeconds) : IDbContextFactory<ArchiveDbContext>
    {
        public ArchiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseNpgsql(scope.Connection)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new Sleep(sleepSeconds))
                .Options;

            var db = new ArchiveDbContext(options);
            db.Database.UseTransaction(scope.Transaction);
            return db;
        }
    }

    private sealed class Sleep(int seconds) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken)
        {
            await using var sleep = command.Connection!.CreateCommand();
            sleep.Transaction = command.Transaction;
            sleep.CommandText = $"select pg_sleep({seconds})";
            await sleep.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
    }
}
