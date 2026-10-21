using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;

namespace SptArchive.Api.Edge;

/// <summary>
/// The guard in front of the Api: a rate limit per client, compression of every answer and three
/// security headers (ADR-0038).
/// </summary>
public static class EdgeGuard
{
    /// <summary>The error code of a request that the rate limit refuses.</summary>
    public const string RateLimitedCode = "RATE_LIMITED";

    private static readonly byte[] Refusal = JsonSerializer.SerializeToUtf8Bytes(new
    {
        errors = new[]
        {
            new
            {
                message = "Too many requests. Wait and send the request again.",
                extensions = new { code = RateLimitedCode },
            },
        },
    });

    /// <summary>Registers the rate limiter and the response compression.</summary>
    public static IServiceCollection AddArchiveEdge(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));
        services.AddSingleton<ClientPartitionedRateLimiter>();

        services.AddRateLimiter(limiter =>
        {
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.ContentType = "application/json; charset=utf-8";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await response.Body.WriteAsync(Refusal, cancellationToken);
            };
        });

        // The partitioned limiter is the whole policy: one bucket per client address, and only the query
        // endpoint is charged. Assigned through the options builder so the container is not built twice.
        services.AddOptions<RateLimiterOptions>()
            .Configure<ClientPartitionedRateLimiter>((options, clients) => options.GlobalLimiter = clients);

        services.AddResponseCompression(compression =>
        {
            // TLS ends at the proxy in front, so the Api speaks plain HTTP and compresses every answer
            // for every client (ADR-0038, ADR-0039). This switch stays false: the Api never answers TLS
            // itself, and a compressed answer over TLS would make a BREACH-style attack worth checking.
            compression.EnableForHttps = false;

            compression.Providers.Add<BrotliCompressionProvider>();
            compression.Providers.Add<GzipCompressionProvider>();

            compression.MimeTypes = ResponseCompressionDefaults.MimeTypes
                .Concat(["application/json", "application/graphql-response+json"]);
        });

        return services;
    }

    /// <summary>
    /// Sets the headers that tell a browser what not to do with the answer. First in the pipeline, so
    /// every answer carries them, including a refusal and an answer from the cache.
    /// </summary>
    public static IApplicationBuilder UseArchiveSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";

            await next(context);
        });
}
