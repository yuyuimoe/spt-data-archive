namespace SptArchive.Core;

/// <summary>Cache keys shared by the Archiver (writer) and the Api (reader).</summary>
public static class CacheKeys
{
    /// <summary>
    /// Identifies the dataset currently in the database. The Archiver changes it after every successful
    /// run, and the Api includes it in every query-cache key, so cached answers from older data are
    /// never served and nothing has to be invalidated explicitly.
    /// </summary>
    public const string DataVersion = "archive:data-version";

    /// <summary>Prefix both apps pass to the Redis cache so they agree on key names.</summary>
    public const string RedisInstanceName = "spt-archive:";
}
