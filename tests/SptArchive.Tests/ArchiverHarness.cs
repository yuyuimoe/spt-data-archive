using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SptArchive.Archiver;
using SptArchive.Archiver.Importers;
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

/// <summary>A cache that keeps what the Archiver writes, so a test reads the key back. It can also be down.</summary>
internal sealed class RecordingCache : IDistributedCache
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    /// <summary>When set, every write fails the way an unreachable Redis does.</summary>
    public bool Down { get; set; }

    public string? Text(string key) => _values.TryGetValue(key, out var value) ? System.Text.Encoding.UTF8.GetString(value) : null;

    public byte[]? Get(string key) => _values.GetValueOrDefault(key);

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => _values.Remove(key);

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (Down)
        {
            throw new InvalidOperationException("The cache is not reachable.");
        }

        _values[key] = value;
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }
}

/// <summary>What the Archiver logged: the level, the text and the exception of every entry.</summary>
internal sealed class LogSink
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public ILogger<T> For<T>() => new SinkLogger<T>(this);

    private sealed class SinkLogger<T>(LogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink.Entries)
            {
                sink.Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}

/// <summary>Runs a real importer and counts the calls, so a test can say that a run imported nothing.</summary>
internal sealed class CountingImporter(IImporter inner) : IImporter
{
    public int Calls { get; private set; }

    public string Name => inner.Name;

    public Task ImportAsync(ImportContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return inner.ImportAsync(context, cancellationToken);
    }
}

/// <summary>Builds the real <see cref="ArchiveJob"/> and runs it to its end, the way the process does.</summary>
internal static class ArchiverHarness
{
    /// <summary>The five importers of <c>Program.cs</c>, in its order.</summary>
    public static IImporter[] Importers() =>
    [
        new LocaleImporter(NullLogger<LocaleImporter>.Instance),
        new ItemImporter(NullLogger<ItemImporter>.Instance),
        new CustomizationImporter(NullLogger<CustomizationImporter>.Instance),
        new QuestImporter(NullLogger<QuestImporter>.Instance),
        new TraderImporter(NullLogger<TraderImporter>.Instance),
    ];

    public static async Task RunAsync(
        string connectionString,
        ArchiveOptions options,
        IEnumerable<IImporter> importers,
        IDistributedCache cache,
        ILogger<ArchiveJob> logger,
        CancellationToken cancellationToken,
        ILogger<RunImporter>? runLogger = null)
    {
        var job = new ArchiveJob(
            new OwnerFactory(connectionString),
            importers,
            new RunImporter(Options.Create(options), runLogger ?? NullLogger<RunImporter>.Instance),
            cache,
            Options.Create(options),
            new NullLifetime(),
            logger);

        await job.StartAsync(cancellationToken);
        await job.ExecuteTask!;
    }
}
