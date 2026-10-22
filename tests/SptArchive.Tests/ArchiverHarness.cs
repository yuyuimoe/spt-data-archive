using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>Opens a database as its owner, the way the Archiver does. A test that commits names its own
/// database, so that the rows of one class never reach the shared archive.</summary>
internal sealed class OwnerFactory(string connectionString) : IDbContextFactory<ArchiveDbContext>
{
    public ArchiveDbContext CreateDbContext() => ArchiveDatabase.CreateContext(connectionString);
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

/// <summary>
/// The two export directories that one mod run needs (ADR-0013, ADR-0029): a baseline of the release, and
/// the run itself, which adds one item. Every test that runs the real <see cref="ArchiveJob"/> builds them.
/// </summary>
internal static class RunTree
{
    public const string NodeId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ItemId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    public const string AddedId = "dddddddddddddddddddddddd";

    public static string Baseline(string root) => Path.Combine(root, "baseline");

    public static string Run(string root) => Path.Combine(root, "run");

    public static void Write(
        string root,
        string modGuid,
        string baselineRelease,
        string runRelease,
        object? target = null,
        string addedItemId = AddedId)
    {
        var baseline = Baseline(root);
        var run = Run(root);

        string Items(string added) =>
            "{\"" + NodeId + "\":{\"_id\":\"" + NodeId + "\",\"_name\":\"Node A\",\"_parent\":\"\",\"_type\":\"Node\",\"_props\":{}},"
            + "\"" + ItemId + "\":{\"_id\":\"" + ItemId + "\",\"_name\":\"Item B\",\"_parent\":\"" + NodeId + "\",\"_type\":\"Item\",\"_props\":{\"Weight\":1.5}}"
            + added + "}";

        var addedItem =
            ",\"" + addedItemId + "\":{\"_id\":\"" + addedItemId + "\",\"_name\":\"Item D\",\"_parent\":\"" + NodeId + "\",\"_type\":\"Item\",\"_props\":{\"Weight\":3}}";

        File(baseline, "templates/items.json", Items(string.Empty));
        File(baseline, "templates/customization.json", "{}");
        File(baseline, "templates/quests.json", "{}");
        File(run, "templates/items.json", Items(addedItem));
        File(run, "templates/customization.json", "{}");
        File(run, "templates/quests.json", "{}");

        var exporter = new { directory = "SptArchive.Exporter", modGuid = RunDirectory.ToolModGuid, name = "Exporter", author = "x", version = "1.4.0", sptVersion = ">=4.1.0" };
        var mod = new { directory = "Thermal", modGuid, name = "Thermal", author = "x", version = "3.4.0", sptVersion = ">=4.1.0" };

        File(baseline, "run/loaded.json", JsonSerializer.Serialize(new object[] { exporter }));
        File(baseline, "run/release.json", JsonSerializer.Serialize(new { sptVersion = baselineRelease }));
        File(run, "run/loaded.json", JsonSerializer.Serialize(new object[] { exporter, mod }));
        File(run, "run/release.json", JsonSerializer.Serialize(new { sptVersion = runRelease }));
        File(run, "run/files/Thermal.json", JsonSerializer.Serialize(new[] { new { path = "Thermal.dll", size = 10, sha256 = new string('a', 64), kind = "assembly" } }));

        if (target is not null)
        {
            File(run, "run/target.json", JsonSerializer.Serialize(target));
        }
    }

    public static void File(string root, string path, string content)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }
}
