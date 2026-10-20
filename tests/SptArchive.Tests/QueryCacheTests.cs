using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SptArchive.Api.Caching;

namespace SptArchive.Tests;

/// <summary>ADR-0014 action item 3: one release never answers for another through the query cache.</summary>
public sealed class QueryCacheTests
{
    private static async Task<string?> SendAsync(QueryCacheMiddleware middleware, string query)
    {
        var body = Encoding.UTF8.GetBytes($$"""{"query":"{{query}}"}""");
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/graphql";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        return context.Response.Headers["X-Cache"];
    }

    [Fact]
    public async Task One_release_does_not_answer_for_another()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        // The inner app answers every request with a clean success, so only the key decides HIT or MISS.
        var middleware = new QueryCacheMiddleware(
            async context => await context.Response.WriteAsync("""{"data":{}}"""),
            cache,
            Options.Create(new QueryCacheOptions()),
            NullLogger<QueryCacheMiddleware>.Instance);

        var first = await SendAsync(middleware, "{ items(sptVersion: \\\"4.0.12\\\") { totalCount } }");
        var other = await SendAsync(middleware, "{ items(sptVersion: \\\"4.0.13\\\") { totalCount } }");
        var again = await SendAsync(middleware, "{ items(sptVersion: \\\"4.0.12\\\") { totalCount } }");

        Assert.Equal("MISS", first);
        Assert.Equal("MISS", other);
        Assert.Equal("HIT", again);
    }
}
