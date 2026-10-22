using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SptArchive.Archiver;
using SptArchive.Archiver.Importers;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// Every execution of the Archiver leaves a row, and a failed one too (ADR-0033). These tests run the
/// real <see cref="ArchiveJob"/>, which commits, so each test removes what it wrote. The mod GUID and
/// the release belong to the test, so the rows of a real import are never touched.
/// </summary>
public sealed class RunRecordTests : IAsyncLifetime
{
    /// <summary>This class commits its imports, so it writes into a database of its own
    /// (<see cref="ArchiveDatabase.OwnDatabaseAsync"/>): the shared archive must not hold its rows, because
    /// every other test that runs at the same time reads the shared one.</summary>
    private static string Database => ArchiveDatabase.OwnDatabaseAsync("run-record").GetAwaiter().GetResult();

    private const string AddedId = "dddddddddddddddddddddddd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-record-" + Guid.NewGuid().ToString("N"));
    private readonly string _modGuid = "com.test.runrecord." + Guid.NewGuid().ToString("N");
    private readonly string _release = "996." + Random.Shared.Next(1, 1_000_000) + ".0";
    private readonly string _otherRelease = "995." + Random.Shared.Next(1, 1_000_000) + ".0";

    private string Baseline => Path.Combine(_root, "baseline");

    private string Run => Path.Combine(_root, "run");

    public async ValueTask InitializeAsync() => await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Environment.ExitCode = 0; // The job sets it on a failure, and a failure is what some tests want.

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        await using var connection = new NpgsqlConnection(Database);
        await connection.OpenAsync();

        foreach (var sql in new[]
        {
            "delete from mod_change_paths where dataset_id in (select id from mod_datasets where mod_guid = @g)",
            "delete from mod_data_observations where mod_id in (select id from mods where mod_guid = @g)",
            "delete from mod_datasets where mod_guid = @g",
            "delete from mods where mod_guid = @g",
            "delete from archive_runs where target_mod_guid = @g or purpose like '%' || @g || '%'",
            "delete from items where spt_version in (@r1, @r2)",
            "delete from spt_versions where version in (@r1, @r2)",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("g", _modGuid);
            command.Parameters.AddWithValue("r1", _release);
            command.Parameters.AddWithValue("r2", _otherRelease);
            await command.ExecuteNonQueryAsync();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_mod_run_states_its_purpose_its_target_and_its_result()
    {
        Tree(baselineRelease: _release, runRelease: _release);

        await RunJobAsync();

        await using var db = ArchiveDatabase.CreateContext(Database);
        var run = await db.ArchiveRuns.SingleAsync(row => row.TargetModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveRunKind.Mod, run.Kind);
        Assert.Equal(ArchiveRun.Succeeded, run.Status);
        Assert.NotNull(run.FinishedAt);
        Assert.False(string.IsNullOrWhiteSpace(run.Purpose));
        Assert.Equal("3.4.0", run.TargetVersion);
        Assert.Equal(_release, run.SptVersion);
        Assert.Equal(_release, run.BaselineRelease);
        Assert.Equal(64, run.BaselineHash!.Length);

        using var result = JsonDocument.Parse(run.Result!);
        var dataset = await db.ModDatasets.SingleAsync(row => row.ModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(dataset.Id, result.RootElement.GetProperty("datasetId").GetGuid());
        Assert.Equal(dataset.ContentHash, result.RootElement.GetProperty("contentHash").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("templates").GetProperty("item.added").GetInt32());

        Assert.Equal(run.Id, dataset.CreatedByRunId);

        var observation = await db.ModDataObservations.SingleAsync(row => row.DatasetId == dataset.Id, TestContext.Current.CancellationToken);
        Assert.Equal(run.Id, observation.ArchiveRunId);
    }

    [Fact]
    public async Task A_run_that_fails_still_leaves_a_row_with_its_error_and_its_stage()
    {
        // The baseline was made on another release: the import refuses before it writes anything.
        Tree(baselineRelease: _otherRelease, runRelease: _release);

        await RunJobAsync();

        await using var db = ArchiveDatabase.CreateContext(Database);
        var run = await db.ArchiveRuns.SingleAsync(row => row.TargetModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveRun.Failed, run.Status);
        Assert.Contains(_otherRelease, run.Error!, StringComparison.Ordinal);

        using var result = JsonDocument.Parse(run.Result!);
        Assert.Equal("prepare", result.RootElement.GetProperty("stage").GetString());
        Assert.Equal(run.Error, result.RootElement.GetProperty("error").GetString());

        Assert.False(await db.ModDatasets.AnyAsync(row => row.ModGuid == _modGuid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_run_of_the_same_content_keeps_the_first_run_on_the_dataset()
    {
        Tree(baselineRelease: _release, runRelease: _release);

        await RunJobAsync();
        await RunJobAsync(force: true);

        await using var db = ArchiveDatabase.CreateContext(Database);
        var runs = await db.ArchiveRuns.Where(row => row.TargetModGuid == _modGuid).OrderBy(row => row.StartedAt).ToListAsync(TestContext.Current.CancellationToken);
        var dataset = await db.ModDatasets.SingleAsync(row => row.ModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(2, runs.Count);
        Assert.Equal(runs[0].Id, dataset.CreatedByRunId);

        // Content rows name no run; the observation names the latest one.
        var observation = await db.ModDataObservations.SingleAsync(row => row.DatasetId == dataset.Id, TestContext.Current.CancellationToken);
        Assert.Equal(runs[1].Id, observation.ArchiveRunId);

        using var second = JsonDocument.Parse(runs[1].Result!);
        Assert.Equal("dataset_reused", second.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_run_without_a_target_file_is_imported_and_its_observation_is_unverified()
    {
        Tree(baselineRelease: _release, runRelease: _release);

        await RunJobAsync();

        await using var db = ArchiveDatabase.CreateContext(Database);
        var observation = await db.ModDataObservations.SingleAsync(row => row.Dataset!.ModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.True(observation.TargetUnverified);
    }

    [Fact]
    public async Task A_run_that_states_its_target_needs_no_configured_guid_and_is_verified()
    {
        Tree(baselineRelease: _release, runRelease: _release, target: new { modGuid = _modGuid, version = "3.4.0", reason = "Thermal imager, shipped configuration" });

        await RunJobAsync(configuredGuid: null);

        await using var db = ArchiveDatabase.CreateContext(Database);
        var run = await db.ArchiveRuns.SingleAsync(row => row.TargetModGuid == _modGuid, TestContext.Current.CancellationToken);
        var observation = await db.ModDataObservations.SingleAsync(row => row.ArchiveRunId == run.Id, TestContext.Current.CancellationToken);

        Assert.False(observation.TargetUnverified);
        Assert.Equal("Thermal imager, shipped configuration", run.Purpose);
    }

    [Fact]
    public async Task A_configured_guid_that_disagrees_with_the_target_file_stops_the_import_and_names_both()
    {
        Tree(baselineRelease: _release, runRelease: _release, target: new { modGuid = _modGuid, version = "3.4.0", reason = "x" });

        await RunJobAsync(configuredGuid: "com.example.other");

        await using var db = ArchiveDatabase.CreateContext(Database);
        var run = await db.ArchiveRuns.SingleAsync(row => row.TargetModGuid == "com.example.other", TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveRun.Failed, run.Status);
        Assert.Contains(_modGuid, run.Error!, StringComparison.Ordinal);
        Assert.Contains("com.example.other", run.Error!, StringComparison.Ordinal);
        Assert.False(await db.ModDatasets.AnyAsync(row => row.ModGuid == _modGuid, TestContext.Current.CancellationToken));

        await db.ArchiveRuns.Where(row => row.Id == run.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_id_minted_after_the_run_started_is_a_warning_and_the_import_still_succeeds()
    {
        // The id decodes to one minute from now: the mod minted it while this run was running.
        var minted = MintTimeTests.IdMinted(DateTimeOffset.UtcNow.AddMinutes(1));
        Tree(baselineRelease: _release, runRelease: _release, addedItemId: minted);

        await RunJobAsync();

        await using var db = ArchiveDatabase.CreateContext(Database);
        var run = await db.ArchiveRuns.SingleAsync(row => row.TargetModGuid == _modGuid, TestContext.Current.CancellationToken);

        Assert.Equal(ArchiveRun.Succeeded, run.Status);

        using var result = JsonDocument.Parse(run.Result!);
        var warning = Assert.Single(result.RootElement.GetProperty("warnings").EnumerateArray()).GetString();
        Assert.Contains(minted, warning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hand_written_id_is_not_a_warning()
    {
        Tree(baselineRelease: _release, runRelease: _release, addedItemId: "not-a-mongo-id-but-written!");

        await RunJobAsync();

        await using var db = ArchiveDatabase.CreateContext(Database);
        var run = await db.ArchiveRuns.SingleAsync(row => row.TargetModGuid == _modGuid, TestContext.Current.CancellationToken);

        using var result = JsonDocument.Parse(run.Result!);
        Assert.Empty(result.RootElement.GetProperty("warnings").EnumerateArray());
    }

    private async Task RunJobAsync(bool force = false, string? configuredGuid = "")
    {
        await EnsureOfficialImportAsync();

        var options = new ArchiveOptions
        {
            RunPath = Run,
            BaselinePath = Baseline,
            RunModGuid = configuredGuid == "" ? _modGuid : configuredGuid,
            Force = force,
            ApplyMigrations = false,
        };

        var job = new ArchiveJob(
            new OwnerFactory(),
            [],
            new RunImporter(Options.Create(options), NullLogger<RunImporter>.Instance),
            new NullCache(),
            Options.Create(options),
            new NullLifetime(),
            NullLogger<ArchiveJob>.Instance);

        await job.StartAsync(TestContext.Current.CancellationToken);
        await job.ExecuteTask!;
    }

    /// <summary>
    /// The archive holds the official data of the release before it holds a run on it (ADR-0013). One
    /// official item is enough to say the release exists; the cleanup deletes it with the rest.
    /// </summary>
    private async Task EnsureOfficialImportAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = ArchiveDatabase.CreateContext(Database);

        if (await db.Items.AnyAsync(item => item.SptVersion == _release, cancellationToken))
        {
            return;
        }

        await SptVersionRegistry.EnsureAsync(db, _release, cancellationToken);

        db.Items.Add(new Item
        {
            Id = "ffffffffffffffffffffffff",
            Name = "Official Item of the test release",
            ParentId = "root",
            Type = "Item",
            Props = "{}",
            SptVersion = _release,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The two export directories of one mod run (ADR-0029), shared with the concurrency tests.</summary>
    private void Tree(string baselineRelease, string runRelease, object? target = null, string addedItemId = AddedId) =>
        RunTree.Write(_root, _modGuid, baselineRelease, runRelease, target, addedItemId);

    private sealed class OwnerFactory : IDbContextFactory<ArchiveDbContext>
    {
        public ArchiveDbContext CreateDbContext() => ArchiveDatabase.CreateContext(Database);
    }

    private sealed class NullCache : IDistributedCache
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

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class NullLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
