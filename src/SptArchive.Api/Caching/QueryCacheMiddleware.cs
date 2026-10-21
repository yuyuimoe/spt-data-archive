using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using SptArchive.Core;

namespace SptArchive.Api.Caching;

public sealed class QueryCacheOptions
{
    public const string Section = "Cache";

    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Safety net only: cached answers are already invalidated when the Archiver publishes a new dataset version,
    /// so this just bounds how long an unused answer occupies Redis.
    /// </summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>Larger request bodies are never cached (and never buffered).</summary>
    public int MaxRequestBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// How long the Api stops trying the cache after a failure. A cache that is down must cost one slow
    /// request, not one for every request, so the wait is paid once and then skipped.
    /// </summary>
    public int DownSeconds { get; set; } = 30;
}

/// <summary>
/// Caches complete GraphQL responses. The cache key is the SHA-256 of the request body (query, variables and
/// operation name) combined with the current dataset version, so identical requests are answered from Redis
/// while the data is unchanged, and a new Archiver run makes every old entry unreachable.
/// </summary>
/// <remarks>
/// Safe here because the API is read-only and anonymous: the same request always produces the same answer.
/// This must be removed or made user-aware before any authentication or per-user data is added.
/// If the cache is unavailable the request is simply served uncached.
/// </remarks>
public sealed class QueryCacheMiddleware(
    RequestDelegate next,
    IDistributedCache cache,
    IOptions<QueryCacheOptions> options,
    ILogger<QueryCacheMiddleware> logger)
{
    private readonly QueryCacheOptions _options = options.Value;

    /// <summary>Environment.TickCount64 until which the cache is skipped. Zero means "try it".</summary>
    private long _downUntil;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsCacheable(context.Request) || IsDown)
        {
            await next(context);
            return;
        }

        string key;
        try
        {
            context.Request.EnableBuffering();
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            context.Request.Body.Position = 0;

            var version = await cache.GetStringAsync(CacheKeys.DataVersion, context.RequestAborted) ?? "none";
            key = $"gql:{version}:{Convert.ToHexString(SHA256.HashData(buffer.ToArray()))}";

            var cached = await cache.GetAsync(key, context.RequestAborted);
            if (cached is not null)
            {
                context.Response.Headers["X-Cache"] = "HIT";
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.Body.WriteAsync(cached, context.RequestAborted);
                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkDown(exception, "Query cache unavailable; serving uncached");
            context.Request.Body.Position = 0;
            await next(context);
            return;
        }

        // Miss: run the query while capturing the response, then cache it if it was a clean success.
        var originalBody = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Response.Body = captured;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = captured.ToArray();

        if (context.Response.StatusCode == StatusCodes.Status200OK && IsCleanSuccess(bytes))
        {
            try
            {
                await cache.SetAsync(
                    key,
                    bytes,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.TtlMinutes) },
                    context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MarkDown(exception, "Could not store query result in the cache");
            }
        }

        context.Response.Headers["X-Cache"] = "MISS";
        await originalBody.WriteAsync(bytes, context.RequestAborted);
    }

    private bool IsDown => Environment.TickCount64 < Volatile.Read(ref _downUntil);

    private void MarkDown(Exception exception, string message)
    {
        Volatile.Write(ref _downUntil, Environment.TickCount64 + (_options.DownSeconds * 1000L));
        logger.LogWarning(exception, "{Message}; skipping the cache for {Seconds} seconds", message, _options.DownSeconds);
    }

    private bool IsCacheable(HttpRequest request) =>
        _options.Enabled
        && HttpMethods.IsPost(request.Method)
        && request.Path.Equals("/graphql", StringComparison.OrdinalIgnoreCase)
        && request.ContentLength is > 0 && request.ContentLength <= _options.MaxRequestBytes
        && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    // A response with errors may be partial or transient (timeouts, depth limit), so it is never cached.
    private static bool IsCleanSuccess(ReadOnlySpan<byte> body) =>
        !body.IsEmpty && body.IndexOf("\"errors\""u8) < 0;
}
