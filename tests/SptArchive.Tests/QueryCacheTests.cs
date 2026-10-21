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

    private static QueryCacheMiddleware Middleware(IDistributedCache cache, RequestDelegate next) =>
        new(next, cache, Options.Create(new QueryCacheOptions()), NullLogger<QueryCacheMiddleware>.Instance);

    private static async Task<string?> SendAsync(QueryCacheMiddleware middleware, string query)
    {
        var context = Request($$"""{"query":"{{query}}"}""");
        await middleware.InvokeAsync(context);
        return context.Response.Headers["X-Cache"];
    }

    private static DefaultHttpContext Request(string? body = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? Query);
        var context = new DefaultHttpContext();
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
