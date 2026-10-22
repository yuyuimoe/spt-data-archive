using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reqnroll;
using SptArchive.Core.Entities;
using Xunit;

namespace SptArchive.Tests.Specs;

/// <summary>
/// The steps of <c>docs/specs/backup-and-restore.feature</c> (ADR-0011). A scenario creates a throwaway
/// source database on the test server, migrates it with the real migrations, and runs the real
/// <c>scripts/backup.sh</c> as a process against it: real <c>pg_dump</c>, <c>pg_restore</c> and <c>psql</c>.
/// Every database a scenario creates has a name that starts with <c>spec_backup_</c> and is dropped in
/// <see cref="Cleanup"/>. The <c>archive</c> database of the test connection is never dumped or changed.
/// </summary>
[Binding]
public sealed class BackupSteps : IDisposable
{
    private const string Prefix = "spec_backup_";
    private const string ReaderRole = "archive_reader";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-backup-spec-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _databases = [];

    private string _source = "";
    private DateTimeOffset _runStartedAt;
    private string _backupDir = "";
    private string _copyDir = "";
    private string? _dump;
    private string _restored = "";
    private string _notADump = "";

    private ScriptResult _result = new(-1, "", "");
    private ScriptResult _verifyResult = new(-1, "", "");
    private ScriptResult _restoreResult = new(-1, "", "");
    private List<string> _scratchSeen = [];
    private Dictionary<string, long> _countsBefore = [];

    [AfterScenario]
    public async Task Cleanup()
    {
        foreach (var database in _databases)
        {
            await DropAsync(database);
        }

        _databases.Clear();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- A successful run produces a dump -------------------------------------------------------

    [Given("a successful archive run")]
    public async Task GivenASuccessfulArchiveRun()
    {
        await CreateSourceAsync();
        _runStartedAt = DateTimeOffset.UtcNow;
    }

    [When("the backup step runs")]
    public async Task WhenTheBackupStepRuns()
    {
        _result = await RunScriptAsync(["dump"], DumpEnvironment());
        Assert.True(_result.ExitCode == 0, $"backup.sh dump failed: {_result}");
    }

    [Then("a dump file exists with the date of the run in its name")]
    public void ThenADumpFileExistsWithTheDateOfTheRun()
    {
        var dumps = Directory.GetFiles(_backupDir, "archive-*.dump");
        var dump = Assert.Single(dumps);

        // The run and the dump may sit on both sides of midnight UTC, so both dates are right.
        var allowed = new[] { _runStartedAt, DateTimeOffset.UtcNow }
            .Select(date => $"archive-{date:yyyyMMdd}T").Distinct().ToArray();
        var name = Path.GetFileName(dump);
        Assert.Contains(allowed, prefix => name.StartsWith(prefix, StringComparison.Ordinal));

        Assert.True(new FileInfo(dump).Length > 0, "the dump is empty");
        Assert.True(File.Exists(dump + ".sha256"), "the dump has no checksum file");
        _dump = dump;
    }

    [Then("a copy of the dump exists off the host")]
    public void ThenACopyOfTheDumpExistsOffTheHost()
    {
        // "Off the host" is a second directory here: the script knows a mount, not a machine (ADR-0011).
        Assert.NotNull(_dump);
        var copy = Path.Combine(_copyDir, Path.GetFileName(_dump));
        Assert.True(File.Exists(copy), $"no copy at {copy}");
        Assert.Equal(Sha256(_dump), Sha256(copy));
        Assert.NotEqual(Path.GetFullPath(_backupDir), Path.GetFullPath(_copyDir));
    }

    // ---- A dump restores into an empty database -------------------------------------------------

    [Given("a dump file from a successful run")]
    public async Task GivenADumpFileFromASuccessfulRun()
    {
        await CreateSourceAsync();
        await TakeDumpAsync();
    }

    [When("I restore it into a new empty database")]
    public async Task WhenIRestoreItIntoANewEmptyDatabase() => await RestoreIntoNewDatabaseAsync();

    [Then("every table has the same row count as in the source database")]
    public async Task ThenEveryTableHasTheSameRowCount()
    {
        var source = await RowCountsAsync(_source);
        var restored = await RowCountsAsync(_restored);

        Assert.NotEmpty(source);
        Assert.Contains(source.Values, count => count > 0);
        Assert.Equal(source, restored);
    }

    // ---- A restored database keeps the read-only role -------------------------------------------

    [Given("a dump file and the role setup from {string}")]
    public async Task GivenADumpFileAndTheRoleSetupFrom(string initdb)
    {
        // The role comes from the init script of the server, never from the dump (ADR-0011).
        Assert.True(Directory.Exists(Path.Combine(RepositoryRoot(), initdb)), $"{initdb} is missing");
        Assert.Equal(1L, await ScalarAsync("postgres", $"select count(*) from pg_roles where rolname = '{ReaderRole}'"));

        await CreateSourceAsync();
        await TakeDumpAsync();
    }

    [When("I restore the dump into a new database")]
    public async Task WhenIRestoreTheDumpIntoANewDatabase() => await RestoreIntoNewDatabaseAsync();

    [Then("the role {string} can read every table")]
    public async Task ThenTheRoleCanReadEveryTable(string role)
    {
        RequireReaderRole(role);
        var tables = await TablesAsync(_restored);
        Assert.NotEmpty(tables);

        await using var connection = await OpenAsync(ReaderConnectionString(_restored));
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand($"select count(*) from {table}", connection);
            Assert.NotNull(await command.ExecuteScalarAsync());
        }
    }

    [Then("the role {string} cannot write to any table")]
    public async Task ThenTheRoleCannotWriteToAnyTable(string role)
    {
        RequireReaderRole(role);
        var tables = await TablesAsync(_restored);
        Assert.NotEmpty(tables);

        await using var connection = await OpenAsync(ReaderConnectionString(_restored));
        foreach (var table in tables)
        {
            foreach (var statement in new[] { $"insert into {table} default values", $"delete from {table}", $"truncate {table}" })
            {
                await using var command = new NpgsqlCommand(statement, connection);
                var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
            }
        }
    }

    // ---- A dump that cannot be read is refused --------------------------------------------------

    [Given("a file that is not a custom-format dump")]
    public async Task GivenAFileThatIsNotACustomFormatDump()
    {
        Directory.CreateDirectory(_root);
        _notADump = Path.Combine(_root, "archive-not-a-dump.dump");
        await File.WriteAllTextAsync(_notADump, "-- a plain SQL file is not a custom-format dump\nselect 1;\n");
        _source = Prefix + Guid.NewGuid().ToString("N");
    }

    [When("I verify or restore it")]
    public async Task WhenIVerifyOrRestoreIt()
    {
        _restored = Prefix + "refused_" + Guid.NewGuid().ToString("N");
        _scratchSeen = await DatabaseNamesAsync("archive_verify_");

        // verify compares against ARCHIVE_DB_NAME, which would only be read after the file is accepted.
        _verifyResult = await RunScriptAsync(["verify", _notADump], BaseEnvironment());
        _restoreResult = await RunScriptAsync(["restore", _notADump, _restored], BaseEnvironment());
    }

    [Then("the script fails with a message")]
    public void ThenTheScriptFailsWithAMessage()
    {
        foreach (var result in new[] { _verifyResult, _restoreResult })
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("is not a readable custom-format dump", result.Stderr);
        }
    }

    [Then("no database is created")]
    public async Task ThenNoDatabaseIsCreated()
    {
        Assert.DoesNotContain(_restored, await DatabaseNamesAsync(Prefix));
        Assert.Equal(_scratchSeen, await DatabaseNamesAsync("archive_verify_"));
    }

    // ---- A restore never overwrites a database --------------------------------------------------

    [Given("a database that already exists")]
    public async Task GivenADatabaseThatAlreadyExists()
    {
        await CreateSourceAsync();
        await TakeDumpAsync();

        // The database changes after the dump, so a restore that overwrote it would lose this table.
        await ExecuteAsync(_source, "create table spec_marker(id int primary key, note text)");
        await ExecuteAsync(_source, "insert into spec_marker values (1, 'written after the dump')");
        _countsBefore = await RowCountsAsync(_source);
    }

    [When("I restore a dump into that name")]
    public async Task WhenIRestoreADumpIntoThatName()
    {
        Assert.NotNull(_dump);
        _result = await RunScriptAsync(["restore", _dump, _source], BaseEnvironment());
    }

    [Then("the script fails and the database is unchanged")]
    public async Task ThenTheScriptFailsAndTheDatabaseIsUnchanged()
    {
        Assert.NotEqual(0, _result.ExitCode);
        Assert.Contains("exists; restore needs a new one", _result.Stderr);

        Assert.Equal(_countsBefore, await RowCountsAsync(_source));
        Assert.Equal("written after the dump", await ScalarAsync(_source, "select note from spec_marker where id = 1"));
    }

    // ---- Verify proves the restore against the source -------------------------------------------

    [Given("a dump of the database")]
    public async Task GivenADumpOfTheDatabase()
    {
        await CreateSourceAsync();
        await TakeDumpAsync();
    }

    [When("I verify the dump")]
    public async Task WhenIVerifyTheDump()
    {
        Assert.NotNull(_dump);
        var before = await DatabaseNamesAsync("archive_verify_");

        // The scratch database lives for a moment only, so a poller records the names that appear while verify runs.
        using var stop = new CancellationTokenSource();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var poller = Task.Run(async () =>
        {
            await using var connection = await OpenAsync(AdminConnectionString());
            while (!stop.IsCancellationRequested)
            {
                foreach (var name in await DatabaseNamesAsync(connection, "archive_verify_"))
                {
                    seen.Add(name);
                }

                await Task.Delay(10);
            }
        });

        _result = await RunScriptAsync(["verify", _dump], BaseEnvironment());
        await stop.CancelAsync();
        await poller;

        _scratchSeen = seen.Except(before).ToList();
    }

    [Then("the dump is restored into a scratch database")]
    public void ThenTheDumpIsRestoredIntoAScratchDatabase()
    {
        Assert.True(_result.ExitCode == 0, $"backup.sh verify failed: {_result}");
        Assert.NotEmpty(_scratchSeen);
    }

    [Then("the row count of every table equals the source")]
    public async Task ThenTheRowCountOfEveryTableEqualsTheSource()
    {
        var tables = await TablesAsync(_source);
        Assert.Matches($@"verified .*: {tables.Count} tables, same row counts as {Regex.Escape(_source)}", _result.Stdout);
        Assert.DoesNotContain("@@", _result.Stdout); // a diff of the two count lists would print hunks
    }

    [Then("the scratch database is dropped")]
    public async Task ThenTheScratchDatabaseIsDropped()
    {
        var left = await DatabaseNamesAsync("archive_verify_");
        Assert.Empty(_scratchSeen.Intersect(left));
    }

    // ---- A copy that differs from the dump is an error ------------------------------------------

    [Given("a copy directory is set")]
    public async Task GivenACopyDirectoryIsSet()
    {
        await CreateSourceAsync();
        Directory.CreateDirectory(_copyDir);
    }

    [When("the copy does not match the checksum of the dump")]
    public async Task WhenTheCopyDoesNotMatchTheChecksumOfTheDump()
    {
        // The medium is the fault: a "cp" in front of the real one writes the file and then damages the copy,
        // as a failing share would. The script, its checksum and its check are the real ones.
        var shims = Path.Combine(_root, "shims");
        Directory.CreateDirectory(shims);
        var shim = Path.Combine(shims, "cp");
        await File.WriteAllTextAsync(shim, """
            #!/usr/bin/env bash
            PATH="$ORIGINAL_PATH" cp "$@"
            for arg in "$@"; do
              if [[ $arg == *.dump ]]; then printf 'damaged' >> "${@: -1}/$(basename "$arg")"; fi
            done
            """.Replace("\r", "") + "\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var environment = DumpEnvironment();
        var original = Environment.GetEnvironmentVariable("PATH") ?? "";
        environment["ORIGINAL_PATH"] = original;
        environment["PATH"] = shims + Path.PathSeparator + original;

        _result = await RunScriptAsync(["dump"], environment);
    }

    [Then("the backup step fails")]
    public void ThenTheBackupStepFails()
    {
        Assert.NotEqual(0, _result.ExitCode);
        Assert.Contains("copy differs from the dump", _result.Stderr);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private async Task CreateSourceAsync()
    {
        Directory.CreateDirectory(_root);
        _backupDir = Path.Combine(_root, "backups");
        _copyDir = Path.Combine(_root, "copy");

        _source = Prefix + Guid.NewGuid().ToString("N");
        await ExecuteAsync("postgres", $"create database \"{_source}\"");
        _databases.Add(_source);

        await using var db = ArchiveDatabase.CreateContext(ConnectionString(_source));
        await db.Database.MigrateAsync();

        var started = DateTimeOffset.UtcNow;
        db.ArchiveRuns.Add(new ArchiveRun
        {
            StartedAt = started,
            FinishedAt = started.AddMinutes(1),
            SourceHash = "sha256:backup-spec",
            SptVersion = "999.6.0",
            Status = ArchiveRun.Succeeded,
            Kind = ArchiveRunKind.Official,
            Purpose = "the run that the backup scenario dumps",
            Result = """{"items":1}""",
        });
        db.Mods.Add(new Mod { ModGuid = "com.example.backup", Name = "Backup example", Version = "1.0.0" });
        db.Mods.Add(new Mod { ModGuid = "com.example.backup.other", Name = "Other example", Version = "2.0.0" });
        await db.SaveChangesAsync();
    }

    private async Task TakeDumpAsync()
    {
        var result = await RunScriptAsync(["dump"], DumpEnvironment());
        Assert.True(result.ExitCode == 0, $"backup.sh dump failed: {result}");
        _dump = Assert.Single(Directory.GetFiles(_backupDir, "archive-*.dump"));
    }

    private async Task RestoreIntoNewDatabaseAsync()
    {
        Assert.NotNull(_dump);
        _restored = Prefix + "restored_" + Guid.NewGuid().ToString("N");
        _databases.Add(_restored);

        Assert.DoesNotContain(_restored, await DatabaseNamesAsync(Prefix));
        _result = await RunScriptAsync(["restore", _dump, _restored], BaseEnvironment());
        Assert.True(_result.ExitCode == 0, $"backup.sh restore failed: {_result}");
    }

    private static void RequireReaderRole(string role) => Assert.Equal(ReaderRole, role);

    private Dictionary<string, string> BaseEnvironment()
    {
        var connection = new NpgsqlConnectionStringBuilder(ArchiveDatabase.OwnerConnectionString);
        return new Dictionary<string, string>
        {
            ["PGHOST"] = connection.Host!,
            ["PGPORT"] = connection.Port.ToString(),
            ["PGUSER"] = connection.Username!,
            ["PGPASSWORD"] = connection.Password!,
            ["ARCHIVE_DB_NAME"] = _source,
        };
    }

    private Dictionary<string, string> DumpEnvironment()
    {
        var environment = BaseEnvironment();
        environment["ARCHIVE_BACKUP_DIR"] = _backupDir;
        environment["ARCHIVE_BACKUP_COPY_DIR"] = _copyDir;
        return environment;
    }

    private static async Task<ScriptResult> RunScriptAsync(string[] arguments, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "backup.sh"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The script reads its settings from the environment; nothing of the developer's shell may leak in.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("PG", StringComparison.Ordinal)
            || name.StartsWith("ARCHIVE_BACKUP", StringComparison.Ordinal) || name == "ARCHIVE_DB_NAME").ToList())
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "backup.sh")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("scripts/backup.sh was not found above " + AppContext.BaseDirectory);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- The test server ------------------------------------------------------------------------

    private static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(ArchiveDatabase.OwnerConnectionString) { Database = database, Pooling = false }.ConnectionString;

    private static string ReaderConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(ArchiveDatabase.ReaderConnectionString) { Database = database, Pooling = false }.ConnectionString;

    private static string AdminConnectionString() => ConnectionString("postgres");

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = await OpenAsync(ConnectionString(database));
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var connection = await OpenAsync(ConnectionString(database));
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<string>> DatabaseNamesAsync(string prefix)
    {
        await using var connection = await OpenAsync(AdminConnectionString());
        return await DatabaseNamesAsync(connection, prefix);
    }

    private static async Task<List<string>> DatabaseNamesAsync(NpgsqlConnection connection, string prefix)
    {
        await using var command = new NpgsqlCommand("select datname from pg_database where starts_with(datname, @prefix) order by 1", connection);
        command.Parameters.AddWithValue("prefix", prefix);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>The tables of the public schema, quoted, as the script lists them.</summary>
    private static async Task<List<string>> TablesAsync(string database)
    {
        await using var connection = await OpenAsync(ConnectionString(database));
        await using var command = new NpgsqlCommand(
            "select quote_ident(schemaname) || '.' || quote_ident(tablename) from pg_tables where schemaname = 'public' order by 1", connection);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>The exact row count of every table. It does not use the script, so the script is not its own witness.</summary>
    private static async Task<Dictionary<string, long>> RowCountsAsync(string database)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(ConnectionString(database));
        foreach (var table in await TablesAsync(database))
        {
            await using var command = new NpgsqlCommand($"select count(*) from {table}", connection);
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return counts;
    }

    private static async Task DropAsync(string database)
    {
        // Only a name that this class created ever reaches this method.
        Assert.StartsWith(Prefix, database);
        await ExecuteAsync("postgres", $"drop database if exists \"{database}\" with (force)");
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr)
    {
        public override string ToString() => $"exit {ExitCode}\nstdout: {Stdout}\nstderr: {Stderr}";
    }
}
