using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using SptArchive.Archiver;
using SptArchive.Archiver.Importers;
using SptArchive.Archiver.Runs;
using SptArchive.Core.Data;

namespace SptArchive.Tests.Specs;

/// <summary>
/// What the steps of <c>docs/specs/archive-official-data.feature</c> share. A scenario owns a database that it
/// creates, migrates and drops (TD-81), because the real Archiver commits and the scenarios say what the
/// official tables hold after a run, a failed run and a second run. A scenario owns a copy of the server
/// folder too, and a run reads it through the real <see cref="ArchiveJob"/> and the five real importers.
/// </summary>
internal sealed class OfficialWorld
{
    public const string ExporterGuid = RunDirectory.ToolModGuid;

    /// <summary>The official tables that carry <c>spt_version</c> and that the importers write.</summary>
    public static readonly string[] OfficialTables =
    [
        "items", "customizations", "quests", "traders", "trader_assort_items", "trader_quest_assorts", "locale_entries",
    ];

    private readonly string _name = "official-data-" + Guid.NewGuid().ToString("N")[..12];

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sptarchive-official-spec-" + Guid.NewGuid().ToString("N"));

    /// <summary>The server's <c>SPT_Data/database</c> folder.</summary>
    public string Source => Path.Combine(Root, "database");

    public string ConnectionString { get; private set; } = "";

    public string Release { get; private set; } = "4.0.13";

    public bool Force { get; set; }

    public RecordingCache Cache { get; } = new();

    public LogSink Log { get; } = new();

    public CountingImporter[] Importers { get; private set; } = [];

    /// <summary>The last row that a step found, for the steps that say "that row".</summary>
    public object? Row { get; set; }

    public bool Created { get; private set; }

    /// <summary>The error of a run that failed: the critical entry of the job, where it sets the exit code 1.</summary>
    public Exception? Failure =>
        Log.Entries.LastOrDefault(entry => entry.Level == LogLevel.Critical).Exception;

    public bool Failed => Log.Entries.Any(entry => entry.Level == LogLevel.Critical);

    public async Task CreateDatabaseAsync()
    {
        var connection = await ArchiveDatabase.OwnDatabaseAsync(_name, TestContext.Current.CancellationToken);
        ConnectionString = new NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString;
        Created = true;
    }

    public ArchiveDbContext Open() => ArchiveDatabase.CreateContext(ConnectionString);

    public async Task DisposeAsync()
    {
        if (!Created)
        {
            return;
        }

        Environment.ExitCode = 0; // The job sets it when a run fails, and a failure is what some scenarios want.

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        await ArchiveDatabase.DropOwnDatabaseAsync(_name);
    }

    // ---- The files of the server database ------------------------------------------------------

    public void Write(string path, string content) => WriteIn(Source, path, content);

    public static void WriteIn(string root, string path, string content)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string path) => File.ReadAllText(Path.Combine(Source, path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A file of the source as a JSON object that a step may change and <see cref="Save"/> again.</summary>
    public JsonObject Load(string path) => JsonNode.Parse(Read(path))!.AsObject();

    public void Save(string path, JsonNode node) => Write(path, node.ToJsonString());

    /// <summary>
    /// The server states its release in <c>run/release.json</c> when the exporter wrote the source, and the
    /// run names the same release (ADR-0014, ADR-0035). The two are always written together.
    /// </summary>
    public void UseRelease(string release)
    {
        Release = release;
        Write("run/release.json", JsonSerializer.Serialize(new { sptVersion = release }));
    }

    public static object Mod(string modGuid, string version) => new
    {
        directory = modGuid[(modGuid.LastIndexOf('.') + 1)..],
        modGuid,
        name = modGuid,
        author = "Test",
        version,
        sptVersion = ">=4.0.0 <5.0.0",
    };

    public void WriteLoadedMods(params object[] mods) =>
        Write("run/loaded.json", JsonSerializer.Serialize(new object[] { Mod(ExporterGuid, "1.5.0") }.Concat(mods)));

    /// <summary>
    /// The smallest server database that every importer accepts: a node and an item, a node and a piece of
    /// clothing, a quest, one language, one trader with one offer and one quest link. A scenario changes the
    /// part it is about.
    /// </summary>
    public void WriteDefaultSource(double itemWeight = 1.5, string clothingName = "Body A")
    {
        Directory.CreateDirectory(Source);

        Write("templates/items.json", new JsonObject
        {
            [Ids.Node] = Template(Ids.Node, "Node", "", "Node", new JsonObject()),
            [Ids.Item] = Template(Ids.Item, "Item B", Ids.Node, "Item", new JsonObject { ["Weight"] = itemWeight, ["Width"] = 1, ["Height"] = 2 }),
        }.ToJsonString());

        Write("templates/customization.json", new JsonObject
        {
            [Ids.ClothingNode] = Template(Ids.ClothingNode, "Customization", "", "Node", new JsonObject { ["Side"] = new JsonArray("Savage") }),
            [Ids.Clothing] = Template(Ids.Clothing, clothingName, Ids.ClothingNode, "Item", new JsonObject
            {
                ["BodyPart"] = "Body",
                ["Side"] = new JsonArray("Usec", "Bear"),
                ["Prefab"] = new JsonObject { ["path"] = "assets/content/characters/body_a.bundle", ["rcid"] = "" },
            }),
        }.ToJsonString());

        Write("templates/quests.json", new JsonObject
        {
            [Ids.Quest] = new JsonObject
            {
                ["QuestName"] = "A quest",
                ["traderId"] = Ids.Trader,
                ["location"] = "bigmap",
                ["type"] = "Elimination",
                ["side"] = "Pmc",
                ["restartable"] = true,
                ["conditions"] = new JsonObject { ["AvailableForFinish"] = new JsonArray() },
            },
        }.ToJsonString());

        Write("locales/languages.json", """{ "en": "English" }""");
        Write("locales/global/en.json", $$"""{ "{{Ids.Item}} Name": "Item B", "kg": "kg" }""");

        Write($"traders/{Ids.Trader}/base.json", $$"""{ "name": "Prapor", "nickname": "P", "currency": "RUB", "location": "bigmap" }""");
        Write($"traders/{Ids.Trader}/assort.json", $$"""
            {
              "items": [ { "_id": "offer1", "_tpl": "{{Ids.Item}}", "parentId": "hideout", "slotId": "hideout" } ],
              "barter_scheme": { "offer1": [[ { "count": 1, "_tpl": "{{Ids.Item}}" } ]] },
              "loyal_level_items": { "offer1": 1 }
            }
            """);
        Write($"traders/{Ids.Trader}/questassort.json", $$"""{ "started": { "offer1": "{{Ids.Quest}}" } }""");

        WriteLoadedMods();
        UseRelease(Release);
    }

    public static JsonObject Template(string id, string name, string parent, string type, JsonObject props) => new()
    {
        ["_id"] = id,
        ["_name"] = name,
        ["_parent"] = parent,
        ["_type"] = type,
        ["_props"] = props,
    };

    public static class Ids
    {
        public const string Node = "aaaaaaaaaaaaaaaaaaaaaaaa";
        public const string Item = "bbbbbbbbbbbbbbbbbbbbbbbb";
        public const string ClothingNode = "cccccccccccccccccccccccc";
        public const string Clothing = "777777777777777777777777";
        public const string Quest = "555555555555555555555555";
        public const string Trader = "54cb57776803fa99248b456e";
    }

    // ---- A run -----------------------------------------------------------------------------------

    /// <summary>One execution of the Archiver over <see cref="Source"/>, as the release says.</summary>
    public async Task RunAsync(string? release = null)
    {
        if (release is not null && release != Release)
        {
            UseRelease(release);
        }

        Importers = ArchiverHarness.Importers().Select(importer => new CountingImporter(importer)).ToArray();

        var options = new ArchiveOptions
        {
            DatabasePath = Source,
            SptVersion = Release,
            ApplyMigrations = false,
            Force = Force,
        };

        using (ReadOnlySource.Hold(Source))
        {
            await ArchiverHarness.RunAsync(
                ConnectionString,
                options,
                Importers,
                Cache,
                Log.For<ArchiveJob>(),
                TestContext.Current.CancellationToken);
        }

        if (Failed)
        {
            Environment.ExitCode = 0;
        }
    }

    // ---- Reading the archive ---------------------------------------------------------------------

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public Task<long> CountAsync(string table, string release) =>
        ScalarAsync<long>($"select count(*) from {Whitelisted(table)} where spt_version = @r", ("r", release));

    /// <summary>
    /// One text for the rows of a release in every official table. Two equal texts mean that no row of the
    /// release was added, changed or deleted, in any column.
    /// </summary>
    public async Task<string> SnapshotAsync(string release)
    {
        var parts = new List<string>();

        foreach (var table in OfficialTables)
        {
            parts.Add(await SnapshotTableAsync(table, release));
        }

        return string.Join('|', parts);
    }

    public Task<string> SnapshotTableAsync(string table, string release) =>
        ScalarAsync<string>(
            $"select coalesce(md5(string_agg(t::text, E'\\n' order by t::text)), '-') || ':' || count(*) from {Whitelisted(table)} t where spt_version = @r",
            ("r", release));

    private static string Whitelisted(string table) =>
        OfficialTables.Concat(["spt_versions", "archive_runs", "languages"]).Contains(table, StringComparer.Ordinal)
            ? table
            : throw new ArgumentException($"The scenario names the table '{table}', which the steps do not know.");
}

/// <summary>
/// Makes folders read-only for as long as it lives: the folders hold no writable file or directory, so a write by
/// the Archiver fails the run. On release it proves that nothing changed, by a digest of every file, so the proof
/// holds for a user who ignores permissions (root) too.
/// </summary>
internal sealed class ReadOnlySource : IDisposable
{
    private readonly string[] _roots;
    private readonly string[] _digests;

    private ReadOnlySource(string[] roots)
    {
        _roots = roots;
        _digests = roots.Select(Digest).ToArray();

        foreach (var root in roots)
        {
            SetWritable(root, writable: false);
        }
    }

    public static ReadOnlySource Hold(params string[] roots) => new(roots);

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            SetWritable(root, writable: true);
        }

        for (var index = 0; index < _roots.Length; index++)
        {
            Assert.True(_digests[index] == Digest(_roots[index]), $"The Archiver changed the read-only folder '{_roots[index]}'.");
        }
    }

    private static void SetWritable(string root, bool writable)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(file, writable ? FileAttributes.Normal : FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(file, writable ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserRead);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return; // A directory attribute on Windows does not stop a write into it.
        }

        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root);

        foreach (var directory in directories)
        {
            File.SetUnixFileMode(
                directory,
                writable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static string Digest(string root)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path) + "\n"));

            if (File.Exists(path))
            {
                hash.AppendData(File.ReadAllBytes(path));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
