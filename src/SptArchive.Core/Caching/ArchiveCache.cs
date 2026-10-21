using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;

namespace SptArchive.Core.Caching;

/// <summary>
/// Registers the response cache that the Api reads and the Archiver announces a new dataset to.
/// </summary>
/// <remarks>
/// <para>
/// Redis when a connection string is configured, a per-process memory cache otherwise. The Archiver uses
/// the cache only to publish <see cref="CacheKeys.DataVersion"/>.
/// </para>
/// <para>
/// The cache is an optimization and never a dependency. StackExchange.Redis waits five seconds for a
/// connection by default, and so a dead Redis would add that wait to every request. One second is enough
/// for a cache on the same host or network, and the Api skips the cache for a short time after a failure.
/// </para>
/// </remarks>
public static class ArchiveCache
{
    /// <summary>The timeouts both applications use for Redis.</summary>
    public static ConfigurationOptions Options(string configuration)
    {
        var options = ConfigurationOptions.Parse(configuration);
        options.AbortOnConnectFail = false;   // keep trying in the background, never throw on connect
        options.ConnectTimeout = 1000;
        options.SyncTimeout = 1000;
        options.AsyncTimeout = 1000;
        options.ConnectRetry = 1;
        return options;
    }

    /// <summary>Adds the distributed cache. An empty connection string means the memory cache.</summary>
    public static IServiceCollection AddArchiveCache(this IServiceCollection services, string? redisConnection)
    {
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.ConfigurationOptions = Options(redisConnection);
                o.InstanceName = CacheKeys.RedisInstanceName;
            });
        }

        return services;
    }
}
