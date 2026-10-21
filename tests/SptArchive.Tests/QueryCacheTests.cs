using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SptArchive.Api.Caching;

namespace SptArchive.Tests;

/// <summary>
/// The query cache holds two promises: it never answers a request with the data of another release
/// (ADR-0014, action item 3), and it is an optimization that never becomes a dependency.
/// </summary>
public sealed class QueryCacheTests
{
    private const string Query = "{\"query\":\"{ items(first: 1) { nodes { id } } }\"}";

    [Fact]
    public async Task One_release_does_not_answer_for_another()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        // The inner app answers every request with a clean success, so only the key decides HIT or MISS.
        var middleware = Middleware(cache, next: async context => await context.Response.WriteAsync("{\"data\":{}}"));

        var first = await SendAsync(middleware, "{ items(sptVersion: \"4.0.12\") { totalCount } }");
        var other = await SendAsync(middleware, "{ items(sptVersion: \"4.0.13\") { totalCount } }");
        var again = await SendAsync(middleware, "{ items(sptVersion: \"4.0.12\") { totalCount } }");

        Assert.Equal("MISS", first);
        Assert.Equal("MISS", other);
        Assert.Equal("HIT", again);
    }

    [Fact]
    public async Task A_filter_on_another_path_or_value_does_not_share_an_answer()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var middleware = Middleware(cache, next: async context => await context.Response.WriteAsync("{\"data\":{}}"));

        static string Filter(string path, string value) =>
            $$"""{ items(where: { props: { pathEq: { path: ["{{path}}"], value: {{value}} } } }) { nodes { id } } }""";

        var slotsFive = await SendAsync(middleware, Filter("Slots", "5"));
        var slotsThree = await SendAsync(middleware, Filter("Slots", "3"));
        var weightFive = await SendAsync(middleware, Filter("Weight", "5"));
        var again = await SendAsync(middleware, Filter("Slots", "5"));

        Assert.Equal(["MISS", "MISS", "MISS", "HIT"], [slotsFive, slotsThree, weightFive, again]);
    }

    [Fact]
    public async Task A_stored_answer_is_served_without_running_the_query()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var calls = 0;
        var middleware = Middleware(cache, next: async context =>
        {
            calls++;
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}");
        });

        await middleware.InvokeAsync(Request());
        var second = Request();
        await middleware.InvokeAsync(second);

        Assert.Equal(1, calls);                                   // the database ran once
        Assert.Equal("HIT", second.Response.Headers["X-Cache"]);
        Assert.Equal("{\"data\":{\"fresh\":true}}", Read(second.Response));
    }

    [Fact]
    public async Task A_miss_runs_the_query_and_stores_the_answer()
    {
        var cache = new FakeCache();
        var middleware = Middleware(cache, next: async context =>
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}"));

        var context = Request();
        await middleware.InvokeAsync(context);

        Assert.Equal("MISS", context.Response.Headers["X-Cache"]);
        Assert.Equal("{\"data\":{\"fresh\":true}}", Read(context.Response));
        Assert.Single(cache.Entries);                             // stored under the current data version
        Assert.StartsWith("gql:none:", cache.Entries.Keys.Single());   // no release announced yet
    }

    [Fact]
    public async Task An_answer_with_errors_is_not_stored()
    {
        var cache = new FakeCache();
        var middleware = Middleware(cache, next: async context =>
            await context.Response.WriteAsync("{\"errors\":[{\"message\":\"boom\"}]}"));

        await middleware.InvokeAsync(Request());

        Assert.Empty(cache.Entries);                              // a partial or transient answer never enters the cache
    }

    [Fact]
    public async Task A_dead_cache_costs_one_slow_request_and_not_every_request()
    {
        var cache = new FakeCache { Fail = true };
        var middleware = Middleware(cache, next: async context =>
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}"));

        for (var i = 0; i < 5; i++)
        {
            var context = Request();
            await middleware.InvokeAsync(context);

            // The answer still arrives, without the cache header.
            Assert.Equal("{\"data\":{\"fresh\":true}}", Read(context.Response));
            Assert.False(context.Response.Headers.ContainsKey("X-Cache"));
        }

        // One attempt, then the cooldown skips the cache: a dead Redis must not add its timeout to
        // every request.
        Assert.Equal(1, cache.Attempts);
    }

    [Fact]
    public async Task Requests_that_miss_together_run_one_query()
    {
        var cache = new FakeCache();
        var gate = new TaskCompletionSource();
        var calls = 0;
        var middleware = Middleware(cache, next: async context =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}");
        });

        // The first request parks inside the query, so every request below arrives before it finishes:
        // a cache stampede, if the cache had no protection.
        var contexts = new List<DefaultHttpContext>();
        var requests = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            var context = Request();
            contexts.Add(context);
            requests.Add(middleware.InvokeAsync(context));
        }

        gate.SetResult();
        await Task.WhenAll(requests);

        Assert.Equal(1, calls);                                    // one query, not twenty
        Assert.Equal(1, contexts.Count(c => c.Response.Headers["X-Cache"] == "MISS"));
        Assert.Equal(19, contexts.Count(c => c.Response.Headers["X-Cache"] == "SHARED"));
        Assert.All(contexts, c => Assert.Equal("{\"data\":{\"fresh\":true}}", Read(c.Response)));
    }

    [Fact]
    public async Task An_idempotency_key_replays_the_answer()
    {
        var cache = new FakeCache();
        var calls = 0;
        var middleware = Middleware(cache, next: async context =>
        {
            calls++;
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}");
        });

        var first = Request(idempotencyKey: "order-42-abc");
        await middleware.InvokeAsync(first);

        var replay = Request(idempotencyKey: "order-42-abc");
        await middleware.InvokeAsync(replay);

        Assert.Equal(1, calls);                                    // the query ran once
        Assert.Equal("HIT", replay.Response.Headers["X-Cache"]);
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal("{\"data\":{\"fresh\":true}}", Read(replay.Response));
        Assert.Equal(QueryCacheMiddleware.IdempotencyHeader, "Idempotency-Key");
    }

    [Fact]
    public async Task A_replayed_answer_lives_longer_than_a_plain_one()
    {
        var cache = new FakeCache();
        var middleware = Middleware(cache, next: async context =>
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}"));

        await middleware.InvokeAsync(Request());

        var lifetime = cache.LastOptions!.AbsoluteExpirationRelativeToNow!.Value.TotalSeconds;

        // One minute, spread by the jitter so that keys stored together do not expire together.
        Assert.InRange(lifetime, 54, 66);
    }

    [Fact]
    public async Task Two_requests_that_share_a_key_do_not_share_an_answer()
    {
        var cache = new FakeCache();
        var gate = new TaskCompletionSource();
        var first = true;
        var middleware = Middleware(cache, next: async context =>
        {
            // The first request parks inside its query, so the second arrives while it runs.
            if (first)
            {
                first = false;
                await gate.Task;
                await context.Response.WriteAsync("{\"data\":{\"answer\":\"first\"}}");
                return;
            }

            await context.Response.WriteAsync("{\"data\":{\"answer\":\"second\"}}");
        });

        var leader = Request("{\"query\":\"{ items(first: 1) { totalCount } }\"}", idempotencyKey: "order-42-abc");
        var leaderRun = middleware.InvokeAsync(leader);

        var other = Request("{\"query\":\"{ items(first: 2) { totalCount } }\"}", idempotencyKey: "order-42-abc");
        await middleware.InvokeAsync(other);

        gate.SetResult();
        await leaderRun;

        // The second request answers its own question. It never receives the answer of the first one,
        // even though both named the same idempotency key.
        Assert.Contains("second", Read(other.Response));
        Assert.DoesNotContain("first", Read(other.Response));
    }

    [Fact]
    public async Task A_key_reused_for_another_request_is_refused()
    {
        var cache = new FakeCache();
        var middleware = Middleware(cache, next: async context =>
            await context.Response.WriteAsync("{\"data\":{\"fresh\":true}}"));

        await middleware.InvokeAsync(Request(idempotencyKey: "order-42-abc"));

        var other = Request("{\"query\":\"{ items(first: 2) { totalCount } }\"}", idempotencyKey: "order-42-abc");
        await middleware.InvokeAsync(other);

        Assert.Equal(StatusCodes.Status409Conflict, other.Response.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", Read(other.Response));
    }

    [Theory]
    [InlineData("short")]                                     // under eight characters
    [InlineData("/../../etc/passwd")]                         // an arbitrary string would become a cache key
    [InlineData("space in key")]
    public async Task A_malformed_idempotency_key_is_refused(string key)
    {
        var cache = new FakeCache();
        var middleware = Middleware(cache, next: async context =>
            await context.Response.WriteAsync("{\"data\":{}}"));

        var context = Request(idempotencyKey: key);
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_INVALID", Read(context.Response));
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task An_answer_above_the_ceiling_is_served_and_not_stored()
    {
        var cache = new FakeCache();
        var big = "{\"data\":{\"text\":\"" + new string('x', 4096) + "\"}}";
        var options = new QueryCacheOptions { MaxResponseBytes = 1024 };
        var middleware = new QueryCacheMiddleware(
            async context => await context.Response.WriteAsync(big),
            cache,
            Options.Create(options),
            NullLogger<QueryCacheMiddleware>.Instance);

        var context = Request();
        await middleware.InvokeAsync(context);

        // The client still gets the whole answer. Only the cache is skipped, because every stored answer
        // is held in memory and an answer has no upper bound of its own (ADR-0038, F-06).
        Assert.Equal("MISS", context.Response.Headers["X-Cache"]);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public void The_lifetime_of_an_answer_is_one_minute_by_default()
    {
        var options = new QueryCacheOptions();

        Assert.Equal(60, options.TtlSeconds);
        Assert.Equal(300, options.IdempotencySeconds);
        Assert.InRange(options.Ttl().TotalSeconds, 54, 66);        // 60 s spread by 10 %
        Assert.InRange(options.IdempotencyTtl().TotalSeconds, 270, 330);
    }

    [Fact]
    public void The_jitter_can_be_switched_off()
    {
        var options = new QueryCacheOptions { TtlJitterPercent = 0 };

        Assert.Equal(TimeSpan.FromSeconds(60), options.Ttl());
        Assert.Equal(TimeSpan.FromSeconds(300), options.IdempotencyTtl());
    }

    private static QueryCacheMiddleware Middleware(IDistributedCache cache, RequestDelegate next) =>
        new(next, cache, Options.Create(new QueryCacheOptions()), NullLogger<QueryCacheMiddleware>.Instance);

    private static async Task<string?> SendAsync(QueryCacheMiddleware middleware, string query)
    {
        var context = Request($$"""{"query":"{{query}}"}""");
        await middleware.InvokeAsync(context);
        return context.Response.Headers["X-Cache"];
    }

    private static DefaultHttpContext Request(string? body = null, string? idempotencyKey = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? Query);
        var context = new DefaultHttpContext();
        if (idempotencyKey is not null)
        {
            context.Request.Headers[QueryCacheMiddleware.IdempotencyHeader] = idempotencyKey;
        }

        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/graphql";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Read(HttpResponse response)
    {
        response.Body.Position = 0;
        return new StreamReader(response.Body, Encoding.UTF8).ReadToEnd();
    }

    /// <summary>A cache in memory, with a switch that makes every operation throw.</summary>
    private sealed class FakeCache : IDistributedCache
    {
        public Dictionary<string, byte[]> Entries { get; } = [];
        public bool Fail { get; init; }
        public int Attempts { get; private set; }
        public DistributedCacheEntryOptions? LastOptions { get; private set; }

        public byte[]? Get(string key)
        {
            Attempts++;
            if (Fail) throw new InvalidOperationException("Redis is down");
            return Entries.TryGetValue(key, out var value) ? value : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Attempts++;
            if (Fail) throw new InvalidOperationException("Redis is down");
            Entries[key] = value;
            LastOptions = options;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => Entries.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
