namespace SptArchive.Api.Caching;

/// <summary>
/// One answer of the GraphQL pipeline, held in memory only long enough to be cached and shared with
/// the requests that were waiting for it (ADR-0037).
/// </summary>
public sealed record QueryAnswer(int StatusCode, string? ContentType, byte[] Bytes);
