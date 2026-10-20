using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SptArchive.Archiver;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// An official import holds the server's own data, so the installation it reads must load no mod
/// (ADR-0033, decision 1; ADR-0035, decision 1). The exporter is the tool of the export, and it is the one
/// mod allowed.
/// </summary>
public sealed class OfficialImportTests : IAsyncLifetime
{
    private const string ModGuid = "com.example.thermal";
    private const string ExporterGuid = RunDirectory.ToolModGuid;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-official-" + Guid.NewGuid().ToString("N"));

    private readonly string _release = "994." + Random.Shared.Next(1, 1_000_000) + ".0";

    public async ValueTask InitializeAsync() => await ArchiveDatabase.EnsureMigratedAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Environment.ExitCode = 0; // The job sets it when the run fails, and a failure is what one test wants.

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        await using var connection = new NpgsqlConnection(ArchiveDatabase.OwnerConnectionString);
        await connection.OpenAsync();

        foreach (var sql in new[]
        {
            "delete from archive_runs where spt_version = @r",
            "delete from spt_versions where version = @r",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("r", _release);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void An_installation_that_loaded_a_mod_is_not_official_data()
    {
        WriteSource(mods: [Mod(ModGuid, "3.4.0")]);

        var error = Assert.Throws<InvalidOperationException>(() => OfficialSource.EnsureNoMod(_root));

        Assert.Contains(ModGuid, error.Message, StringComparison.Ordinal);
        Assert.Contains("3.4.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("the mods folder", error.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-0033", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installation_that_loaded_only_the_exporter_is_official_data()
    {
        WriteSource(mods: [Mod(ExporterGuid, "1.5.0")]);

        OfficialSource.EnsureNoMod(_root);

        Assert.Empty(OfficialSource.Mods(_root)!);
    }

    [Fact]
    public void A_source_of_an_exporter_older_than_1_2_0_states_no_mod()
    {
        // Nothing proves that such a source loaded no mod, and the job warns instead of refusing it:
        // a release archived once cannot be exported again by an exporter that never ran on it
        // (ADR-0035, decision 2).
        Directory.CreateDirectory(_root);

        Assert.Null(OfficialSource.Mods(_root));
        OfficialSource.EnsureNoMod(_root);
    }

    [Fact]
    public void A_source_that_states_another_release_is_refused()
    {
        WriteSource(mods: [Mod(ExporterGuid, "1.5.0")], release: "4.0.13");

        var error = Assert.Throws<InvalidOperationException>(() => OfficialSource.EnsureRelease(_root, _release));

        Assert.Contains("4.0.13", error.Message, StringComparison.Ordinal);
        Assert.Contains(_release, error.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-0014", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_states_the_same_release_is_accepted()
    {
        WriteSource(mods: [Mod(ExporterGuid, "1.5.0")], release: _release);

        OfficialSource.EnsureRelease(_root, _release);
        OfficialSource.EnsureNoMod(_root);
    }

    [Fact]
    public async Task The_job_writes_nothing_for_a_source_that_loaded_a_mod()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteSource(mods: [Mod(ExporterGuid, "1.5.0"), Mod(ModGuid, "3.4.0")], release: _release);

        await RunJobAsync(cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(ArchiveDatabase.OwnerConnectionString);

        Assert.Empty(await db.ArchiveRuns.Where(run => run.SptVersion == _release).ToListAsync(cancellationToken));
        Assert.Empty(await db.SptVersions.Where(row => row.Version == _release).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task The_job_imports_a_source_that_loaded_only_the_exporter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteSource(mods: [Mod(ExporterGuid, "1.5.0")], release: _release);

        await RunJobAsync(cancellationToken);

        await using var db = ArchiveDatabase.CreateContext(ArchiveDatabase.OwnerConnectionString);
        var run = Assert.Single(await db.ArchiveRuns.Where(row => row.SptVersion == _release).ToListAsync(cancellationToken));

        Assert.Equal(ArchiveRunKind.Official, run.Kind);
        Assert.Equal(ArchiveRun.Succeeded, run.Status);
    }

    private async Task RunJobAsync(CancellationToken cancellationToken)
    {
        var options = new ArchiveOptions
        {
            DatabasePath = _root,
            SptVersion = _release,
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

        await job.StartAsync(cancellationToken);
        await job.ExecuteTask!;
    }

    /// <summary>The two files that say what an installation is: the mods it loaded and its release.</summary>
    private void WriteSource(object[] mods, string? release = null)
    {
        Write("run/loaded.json", JsonSerializer.Serialize(mods));

        if (release is not null)
        {
            Write("run/release.json", JsonSerializer.Serialize(new { sptVersion = release }));
        }
    }

    private static object Mod(string modGuid, string version) => new
    {
        directory = modGuid[(modGuid.LastIndexOf('.') + 1)..],
        modGuid,
        name = modGuid,
        author = "Test",
        version,
        sptVersion = ">=4.0.0 <5.0.0",
    };

    private void Write(string path, string content)
    {
        var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
