using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using SptArchive.Core;

namespace SptArchive.Api.Caching;

/// <summary>
/// Caches complete GraphQL responses, runs one query for the requests that miss together, and lets a
/// client replay a request with an idempotency key (ADR-0008, ADR-0037).
/// </summary>
/// <remarks>
/// <para>
/// The cache key is the dataset version plus the SHA-256 of the request body (query, variables and
/// operation name), so identical requests are answered from Redis while the data is unchanged, and a new
/// Archiver run makes every old entry unreachable.
/// </para>
/// <para>
/// Safe here because the API is read-only and anonymous: the same request always produces the same
/// answer. This must be removed or made user-aware before any authentication or per-user data is added.
/// If the cache is unavailable the request is simply served uncached.
/// </para>
/// </remarks>
public sealed class QueryCacheMiddleware(
    RequestDelegate next,
    IDistributedCache cache,
    IOptions<QueryCacheOptions> options,
    ILogger<QueryCacheMiddleware> logger)
{
    /// <summary>The header a client sends to make a request replayable.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    private const int IdempotencyKeyMinLength = 8;
    private const int IdempotencyKeyMaxLength = 128;

    // A framed answer starts with this marker and then the SHA-256 of the request body that produced it,
    // so a key reused for a different request is detected instead of answered with the other answer.
    private static ReadOnlySpan<byte> FrameMagic => "SARC1"u8;
    private const int FrameHashOffset = 5;
    private const int FrameHashLength = 32;
    private const int FrameOverhead = FrameHashOffset + FrameHashLength;

    private readonly QueryCacheOptions _options = options.Value;
    private readonly SingleFlight _flights = new(options.Value.MaxFlights);

    /// <summary>Environment.TickCount64 until which the cache is skipped. Zero means "try it".</summary>
    private long _downUntil;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsCacheable(context.Request) || IsDown)
        {
            await next(context);
            return;
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(context);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkDown(exception, "Query cache unavailable; serving uncached");
            await next(context);
            return;
        }

        var idempotencyKey = context.Request.Headers[IdempotencyHeader].ToString();
        if (idempotencyKey.Length > 0 && !IsValidIdempotencyKey(idempotencyKey))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_INVALID",
                $"An idempotency key must be {IdempotencyKeyMinLength} to {IdempotencyKeyMaxLength} characters of A-Z, a-z, 0-9, '.', ':', '-' or '_'.");
            return;
        }

        var bodyHash = SHA256.HashData(body);

        string key;
        try
        {
            key = await KeyAsync(context, idempotencyKey, bodyHash);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkDown(exception, "Query cache unavailable; serving uncached");
            await next(context);
            return;
        }

        byte[]? stored;
        try
        {
            stored = await cache.GetAsync(key, context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkDown(exception, "Query cache unavailable; serving uncached");
            await next(context);
            return;
        }

        if (stored is not null)
        {
            if (idempotencyKey.Length == 0)
            {
                await WriteAnswerAsync(context, StatusCodes.Status200OK, stored, "HIT", replayed: false);
                return;
            }

            var frame = Unframe(stored, bodyHash, out var payload);
            if (frame is FrameRead.Payload)
            {
                await WriteAnswerAsync(context, StatusCodes.Status200OK, payload, "HIT", replayed: true);
                return;
            }

            if (frame is FrameRead.OtherBody)
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "IDEMPOTENCY_KEY_REUSED",
                    "This idempotency key was already used for a different request. Send a new key for a new request.");
                return;
            }

            // An unreadable entry is treated as a miss: it is overwritten below.
        }

        // Miss. One request runs the query and the rest wait for it, so a hot answer that expires does not
        // send a burst of identical queries to the database. The flight carries the body hash as well as the
        // key: two clients that share an idempotency key but ask different questions must never receive each
        // other's answer, and the one that loses the race is refused instead (IDEMPOTENCY_KEY_REUSED).
        var flightKey = $"{key}:{Convert.ToHexString(bodyHash)}";
        var (result, leader) = _flights.RunAsync(flightKey, () => ExecuteAsync(context, key, bodyHash, idempotencyKey));
        QueryAnswer answer;
        try
        {
            answer = await result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The request that ran the shared query failed or went away. Answer this one on its own, and
            // leave it uncached: one failure must not be handed to every waiter.
            logger.LogWarning(exception, "The shared query failed; serving this request on its own");
            await next(context);
            return;
        }

        await WriteAnswerAsync(context, answer.StatusCode, answer.Bytes, leader ? "MISS" : "SHARED", replayed: false);
    }

    /// <summary>
    /// Runs the GraphQL pipeline for one request, captures the answer and stores it when it is a clean
    /// success. Called once per cache key at a time.
    /// </summary>
    private async Task<QueryAnswer> ExecuteAsync(HttpContext context, string key, byte[] bodyHash, string idempotencyKey)
    {
        // The body feature is swapped, not the stream, so a middleware that wrapped the feature before
        // this one (response compression, ADR-0038) is put back exactly as it was. Setting Response.Body
        // would replace that feature and the answer would leave uncompressed.
        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>();
        var originalBody = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(captured));

        try
        {
            await next(context);
        }
        finally
        {
            if (originalFeature is not null)
            {
                context.Features.Set(originalFeature);
            }
            else
            {
                context.Response.Body = originalBody;
            }
        }

        var answer = new QueryAnswer(context.Response.StatusCode, context.Response.ContentType, captured.ToArray());

        if (answer.StatusCode == StatusCodes.Status200OK
            && IsCleanSuccess(answer.Bytes)
            && answer.Bytes.Length <= _options.MaxResponseBytes)
        {
            var replayable = idempotencyKey.Length > 0;
            try
            {
                await cache.SetAsync(
                    key,
                    replayable ? Frame(bodyHash, answer.Bytes) : answer.Bytes,
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = replayable ? _options.IdempotencyTtl() : _options.Ttl(),
                    },
                    // Not the leader's cancellation token: the answer is worth caching for the waiters even
                    // if the client that asked for it has gone.
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MarkDown(exception, "Could not store query result in the cache");
            }
        }

        return answer;
    }

    private async Task<string> KeyAsync(HttpContext context, string idempotencyKey, byte[] bodyHash)
    {
        var version = await cache.GetStringAsync(CacheKeys.DataVersion, context.RequestAborted) ?? "none";

        return idempotencyKey.Length == 0
            ? $"gql:{version}:{Convert.ToHexString(bodyHash)}"
            : $"gql:{version}:idem:{idempotencyKey}";
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        context.Request.Body.Position = 0;
        return buffer.ToArray();
    }

    private async Task WriteAnswerAsync(HttpContext context, int statusCode, byte[] payload, string cache, bool replayed)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-Cache"] = cache;

        if (replayed)
        {
            context.Response.Headers["Idempotency-Replayed"] = "true";
        }

        await context.Response.Body.WriteAsync(payload, context.RequestAborted);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            errors = new[] { new { message, extensions = new { code } } },
        });

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(payload, context.RequestAborted);
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

    // A key that anyone may send must not become a cache key of its own choosing, so the shape is fixed.
    private static bool IsValidIdempotencyKey(string value)
    {
        if (value.Length is < IdempotencyKeyMinLength or > IdempotencyKeyMaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or ':' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Frame(byte[] bodyHash, byte[] payload)
    {
        var framed = new byte[FrameOverhead + payload.Length];
        FrameMagic.CopyTo(framed);
        bodyHash.CopyTo(framed, FrameHashOffset);
        payload.CopyTo(framed, FrameOverhead);
        return framed;
    }

    private static FrameRead Unframe(byte[] stored, byte[] bodyHash, out byte[] payload)
    {
        payload = [];

        if (stored.Length < FrameOverhead || !stored.AsSpan(0, FrameHashOffset).SequenceEqual(FrameMagic))
        {
            return FrameRead.Corrupt;
        }

        if (!stored.AsSpan(FrameHashOffset, FrameHashLength).SequenceEqual(bodyHash))
        {
            return FrameRead.OtherBody;
        }

        payload = stored[FrameOverhead..];
        return FrameRead.Payload;
    }

    // A response with errors may be partial or transient (timeouts, depth limit), so it is never cached.
    private static bool IsCleanSuccess(ReadOnlySpan<byte> body) =>
        !body.IsEmpty && body.IndexOf("\"errors\""u8) < 0;

    private enum FrameRead
    {
        Payload,
        OtherBody,
        Corrupt,
    }
}
