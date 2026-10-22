using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// Several Archiver processes may import at the same time: the campaign that fills an empty archive, and
/// two mods that publish on the same day (ADR-0036). The tests that need a particular interleaving hold
/// the lock from the test itself, so the order is the one they ask for and not the one the machine
/// happens to produce.
/// </summary>
public sealed class ConcurrentRunTests : IAsyncLifetime
{
    /// <summary>This class commits its imports, so it writes into a database of its own
    /// (<see cref="ArchiveDatabase.OwnDatabaseAsync"/>): the shared archive must not hold its rows, because
    /// every other test that runs at the same time reads the shared one.</summary>
    private static string Database => ArchiveDatabase.OwnDatabaseAsync("concurrent").GetAwaiter().GetResult();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-parallel-" + Guid.NewGuid().ToString("N"));
    private readonly string _secondRoot = Path.Combine(Path.GetTempPath(), "sptarchive-parallel-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _modGuids =
    [
        "com.test.parallel." + Guid.NewGuid().ToString("N"),
        "com.test.parallel." + Guid.NewGuid().ToString("N"),
    ];

    private readonly List<string> _releases =
    [
        "993." + Random.Shared.Next(1, 1_000_000) + ".0",
        "992." + Random.Shared.Next(1, 1_000_000) + ".0",
    ];

    private string Release => _releases[0];

    public async ValueTask InitializeAsync() => await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Environment.ExitCode = 0; // The job sets it when a run fails, and a failed run is what some tests want.

        foreach (var root in new[] { _root, _secondRoot })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        await using var connection = new NpgsqlConnection(Database);
        await connection.OpenAsync();

        foreach (var modGuid in _modGuids)
        {
            foreach (var sql in new[]
            {
                "delete from mod_change_paths where dataset_id in (select id from mod_datasets where mod_guid = @g)",
                "delete from mod_templates where dataset_id in (select id from mod_datasets where mod_guid = @g)",
                "delete from mod_data_observations where mod_id in (select id from mods where mod_guid = @g)",
                "delete from mod_datasets where mod_guid = @g",
                "delete from mods where mod_guid = @g",
                "delete from archive_runs where target_mod_guid = @g",
            })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("g", modGuid);
                await command.ExecuteNonQueryAsync();
            }
        }

        foreach (var release in _releases)
        {
            foreach (var sql in new[]
            {
                "delete from archive_runs where spt_version = @r",
                "delete from items where spt_version = @r",
                "delete from spt_versions where version = @r",
            })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("r", release);
                await command.ExecuteNonQueryAsync();
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Two_imports_of_one_run_at_the_same_time_store_one_dataset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RunTree.Write(_root, _modGuids[0], Release, Release);
        await SeedOfficialDataAsync(Release, cancellationToken);

        // The lock of the mod is held here, so both jobs pass their own checks and queue on it: this is
        // the case where two Archivers read the same export, and both believe it is new.
        await using var holder = ArchiveDatabase.CreateContext(Database);
        await using var held = await holder.Database.BeginTransactionAsync(cancellationToken);
        await RunLocks.ModExclusiveAsync(holder, _modGuids[0], cancellationToken);

        var first = JobAsync(_root, _modGuids[0], Release, cancellationToken);
        var second = JobAsync(_root, _modGuids[0], Release, cancellationToken);

        await WaitForOpenRunsAsync(_modGuids[0], expected: 2, cancellationToken);
        await held.CommitAsync(cancellationToken);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(180), cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(Database);

        var runs = await db.ArchiveRuns
            .Where(run => run.TargetModGuid == _modGuids[0])
            .OrderBy(run => run.StartedAt)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(ArchiveRun.Succeeded, run.Status));
        Assert.All(runs, run => Assert.NotNull(run.FinishedAt));

        // Both runs are in the history, and the archive holds the content once.
        var dataset = Assert.Single(await db.ModDatasets.Where(row => row.ModGuid == _modGuids[0]).ToListAsync(cancellationToken));
        Assert.Single(await db.ModDataObservations.Where(row => row.DatasetId == dataset.Id).ToListAsync(cancellationToken));
        Assert.Single(await db.ModItems.Where(row => row.DatasetId == dataset.Id).ToListAsync(cancellationToken));

        var outcomes = runs.Select(Outcome).ToArray();
        Assert.Single(outcomes, outcome => outcome == "dataset_created");
    }

    [Fact]
    public async Task Two_mods_of_one_release_import_at_the_same_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RunTree.Write(_root, _modGuids[0], Release, Release);
        RunTree.Write(_secondRoot, _modGuids[1], Release, Release);
        await SeedOfficialDataAsync(Release, cancellationToken);

        // Two different mods share nothing but the release, and the release must not stop them.
        await Task.WhenAll(
                JobAsync(_root, _modGuids[0], Release, cancellationToken),
                JobAsync(_secondRoot, _modGuids[1], Release, cancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(180), cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(Database);

        var runs = await db.ArchiveRuns
            .Where(run => run.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-5) && run.Kind == ArchiveRunKind.Mod)
            .Where(run => run.TargetModGuid == _modGuids[0] || run.TargetModGuid == _modGuids[1])
            .ToListAsync(cancellationToken);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(ArchiveRun.Succeeded, run.Status));
        Assert.Equal(2, await db.ModDatasets.CountAsync(row => row.ModGuid == _modGuids[0] || row.ModGuid == _modGuids[1], cancellationToken));
        Assert.Single(await db.SptVersions.Where(row => row.Version == Release).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_run_whose_process_died_is_closed_as_interrupted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RunTree.Write(_root, _modGuids[0], Release, Release);
        await SeedOfficialDataAsync(Release, cancellationToken);

        // The row of a process that was killed: it still says running, and its last beat is long past.
        var dead = await SeedRunningAsync(Release, DateTimeOffset.UtcNow - RunLease.Silence - TimeSpan.FromMinutes(10), cancellationToken);

        await RunJobAsync(_root, _modGuids[0], Release, cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(Database);
        var row = await db.ArchiveRuns.SingleAsync(run => run.Id == dead, cancellationToken);

        Assert.Equal(ArchiveRun.Interrupted, row.Status);
        Assert.NotNull(row.FinishedAt);
        Assert.Contains("ADR-0036", row.Error!, StringComparison.Ordinal);
        Assert.Equal("interrupted", Outcome(row));
    }

    [Fact]
    public async Task A_run_that_is_still_beating_is_left_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RunTree.Write(_root, _modGuids[0], Release, Release);
        await SeedOfficialDataAsync(Release, cancellationToken);

        // An import of another process, running right now: old start, fresh beat. Its start time must
        // never be the reason to close it, or a parallel run would be killed by the next one to start.
        var live = await SeedRunningAsync(Release, DateTimeOffset.UtcNow, cancellationToken);

        await RunJobAsync(_root, _modGuids[0], Release, cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(Database);
        var row = await db.ArchiveRuns.SingleAsync(run => run.Id == live, cancellationToken);

        Assert.Equal(ArchiveRun.Running, row.Status);
        Assert.Null(row.FinishedAt);
    }

    [Fact]
    public async Task Two_official_imports_of_one_source_import_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A source that states the exporter and the release, and holds no table: the release enters the
        // archive and the importers find nothing to read.
        RunTree.File(_root, "run/loaded.json", JsonSerializer.Serialize(new[]
        {
            new { directory = "SptArchive.Exporter", modGuid = RunDirectory.ToolModGuid, name = "Exporter", author = "x", version = "1.5.0", sptVersion = ">=4.1.0" },
        }));
        RunTree.File(_root, "run/release.json", JsonSerializer.Serialize(new { sptVersion = Release }));

        await using var holder = ArchiveDatabase.CreateContext(Database);
        await using var held = await holder.Database.BeginTransactionAsync(cancellationToken);
        await RunLocks.ReleaseExclusiveAsync(holder, Release, cancellationToken);

        var first = OfficialJobAsync(cancellationToken);
        var second = OfficialJobAsync(cancellationToken);

        await WaitForOpenRunsAsync(_modGuids[0], expected: 2, cancellationToken, official: true);
        await held.CommitAsync(cancellationToken);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(180), cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(Database);

        var runs = await db.ArchiveRuns
            .Where(run => run.SptVersion == Release && run.Kind == ArchiveRunKind.Official)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(ArchiveRun.Succeeded, run.Status));

        // The files are the same, so one import, and the other says it changed nothing.
        Assert.Single(runs.Select(Outcome), outcome => outcome == "imported");
        Assert.Single(runs.Select(Outcome), outcome => outcome == "unchanged");
        Assert.Single(await db.SptVersions.Where(row => row.Version == Release).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task An_import_registers_a_release_that_another_import_registered_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fresh = _releases[1];

        await DropReleaseAsync(fresh);

        // Another import registers the release and has not committed. The late import is between its own
        // check and its own insert, which is the window that a check-then-insert cannot survive.
        await using var blocker = ArchiveDatabase.CreateContext(Database);
        await using var held = await blocker.Database.BeginTransactionAsync(cancellationToken);

        blocker.SptVersions.Add(new SptVersion { Version = fresh, VersionParts = SptVersion.ParseParts(fresh) });
        await blocker.SaveChangesAsync(cancellationToken);

        await using var late = ArchiveDatabase.CreateContext(Database);
        var registering = SptVersionRegistry.EnsureAsync(late, fresh, cancellationToken);

        // The insert of the late import waits on the unique index, and the violation comes after the commit.
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        await held.CommitAsync(cancellationToken);
        await registering.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(Database);
        Assert.Single(await db.SptVersions.Where(row => row.Version == fresh).ToListAsync(cancellationToken));
    }

    /// <summary>Removes a release the test owns, so a left-over row cannot stand in for the race.</summary>
    private static async Task DropReleaseAsync(string release)
    {
        await using var connection = new NpgsqlConnection(Database);
        await connection.OpenAsync();

        foreach (var sql in new[]
        {
            "delete from archive_runs where spt_version = @r",
            "delete from items where spt_version = @r",
            "delete from spt_versions where version = @r",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("r", release);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>One official import of <see cref="_root"/>, started but not awaited.</summary>
    private Task OfficialJobAsync(CancellationToken cancellationToken)
    {
        var options = new ArchiveOptions
        {
            DatabasePath = _root,
            SptVersion = Release,
            ApplyMigrations = false,
        };

        var job = new ArchiveJob(
            new OwnerFactory(Database),
            [],
            new RunImporter(Options.Create(options), NullLogger<RunImporter>.Instance),
            new NullCache(),
            Options.Create(options),
            new NullLifetime(),
            NullLogger<ArchiveJob>.Instance);

        return RunAsync(job, cancellationToken);
    }

    private Task JobAsync(string root, string modGuid, string release, CancellationToken cancellationToken)
    {
        var options = new ArchiveOptions
        {
            RunPath = RunTree.Run(root),
            BaselinePath = RunTree.Baseline(root),
            RunModGuid = modGuid,
            ApplyMigrations = false,
        };

        var job = new ArchiveJob(
            new OwnerFactory(Database),
            [],
            new RunImporter(Options.Create(options), NullLogger<RunImporter>.Instance),
            new NullCache(),
            Options.Create(options),
            new NullLifetime(),
            NullLogger<ArchiveJob>.Instance);

        return RunAsync(job, cancellationToken);
    }

    private async Task RunJobAsync(string root, string modGuid, string release, CancellationToken cancellationToken)
    {
        await SeedOfficialDataAsync(release, cancellationToken);
        await JobAsync(root, modGuid, release, cancellationToken);
    }

    private static async Task RunAsync(ArchiveJob job, CancellationToken cancellationToken)
    {
        await job.StartAsync(cancellationToken);
        await job.ExecuteTask!;
    }

    /// <summary>
    /// Waits until <paramref name="expected"/> runs are open, which is the moment they are past their own
    /// checks and waiting on a lock. The wait is bounded, so a broken lock fails the test instead of hanging it.
    /// </summary>
    private async Task WaitForOpenRunsAsync(
        string modGuid,
        int expected,
        CancellationToken cancellationToken,
        bool official = false)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (true)
        {
            await using var db = ArchiveDatabase.CreateContext(Database);

            var open = official
                ? await db.ArchiveRuns.CountAsync(
                    run => run.Kind == ArchiveRunKind.Official
                        && run.SptVersion == Release
                        && run.Status == ArchiveRun.Running,
                    cancellationToken)
                : await db.ArchiveRuns.CountAsync(
                    run => run.TargetModGuid == modGuid && run.Status == ArchiveRun.Running,
                    cancellationToken);

            if (open == expected)
            {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Expected {expected} open run(s) of '{modGuid}', saw {open}. The jobs are not waiting where the test put them.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    /// <summary>The row a killed process left: still running, and its last beat is the one given.</summary>
    private async Task<Guid> SeedRunningAsync(string release, DateTimeOffset heartbeat, CancellationToken cancellationToken)
    {
        await using var db = ArchiveDatabase.CreateContext(Database);

        var run = new ArchiveRun
        {
            SourceHash = string.Empty,
            SptVersion = release,
            Kind = ArchiveRunKind.Mod,
            TargetModGuid = _modGuids[0],
            TargetVersion = "3.4.0",
            Purpose = "A run whose process was killed while it imported",
            Status = ArchiveRun.Running,
            StartedAt = heartbeat - TimeSpan.FromMinutes(5),
            HeartbeatAt = heartbeat,
        };

        db.ArchiveRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        return run.Id;
    }

    /// <summary>The archive holds the official data of a release before it holds a run on it (ADR-0035).</summary>
    private async Task SeedOfficialDataAsync(string release, CancellationToken cancellationToken)
    {
        await using var db = ArchiveDatabase.CreateContext(Database);

        if (await db.Items.AnyAsync(item => item.SptVersion == release, cancellationToken))
        {
            return;
        }

        await SptVersionRegistry.EnsureAsync(db, release, cancellationToken);

        db.Items.Add(new Item
        {
            Id = "ffffffffffffffffffffffff",
            Name = "Official Item of the test release",
            ParentId = "root",
            Type = "Item",
            Props = "{}",
            SptVersion = release,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Outcome(ArchiveRun run)
    {
        if (string.IsNullOrWhiteSpace(run.Result))
        {
            return null;
        }

        using var document = JsonDocument.Parse(run.Result);

        return document.RootElement.TryGetProperty("outcome", out var outcome) ? outcome.GetString() : null;
    }
}
