using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>Opens the development database as its owner, the way the Archiver does.</summary>
internal sealed class OwnerFactory : IDbContextFactory<ArchiveDbContext>
{
    public ArchiveDbContext CreateDbContext() => ArchiveDatabase.CreateContext(ArchiveDatabase.OwnerConnectionString);
}

/// <summary>A cache that stores nothing: the archive run announces its dataset and moves on.</summary>
internal sealed class NullCache : IDistributedCache
{
    public byte[]? Get(string key) => null;

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key)
    {
    }

    public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        Task.CompletedTask;
}

/// <summary>The host the job stops when the run is over. A test lets the process live.</summary>
internal sealed class NullLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
    }
}
