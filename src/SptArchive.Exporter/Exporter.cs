using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using SptArchive.Core.Entities;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace SptArchive.Exporter;

/// <summary>
///     Metadata for the archive exporter. The exporter is the only mod in this container
///     that is not the subject of the run: it turns the in-memory database into a directory.
/// </summary>
public sealed class ExporterMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.sptarchive.exporter";
    public string Name { get; init; } = "SPT Archive Exporter";
    public string Author { get; init; } = "SPT Data Archive";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("1.2.0");
    public Range SptVersion { get; init; } = new(">=4.1.0 <5.0.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

/// <summary>
///     Dumps every table of the server, in the layout of <c>SPT_Data/database</c>, to a
///     directory, then stops the host. ADR-0013: the container exists to produce one export.
///     ADR-0027: the export is a database directory, so the Archiver reads one layout for an
///     install and for a run.
///     ADR-0029: the export also holds <c>run/</c>, the account that the run gives of itself.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public sealed class Exporter(
    TemplateTable templates,
    TradersTable traders,
    LocaleTable locales,
    BotTable bots,
    LocationTable locations,
    GlobalTable globals,
    HideoutTable hideout,
    MatchTable match,
    ServerTable server,
    SettingsTable settings,
    IReadOnlyList<SptMod> loadedMods
) : IOnLoad
{
    /// <summary>A configuration file larger than this is named in the manifest and not copied (ADR-0029).</summary>
    private const long MaxConfigTextBytes = 2 * 1024 * 1024;

    private string _root = string.Empty;

    /// <summary>
    ///     The key of a map is the name of its directory in the server database, not the
    ///     <c>Id</c> field of the data ("Private Area", "RezervBase", "Sandbox_high" are not
    ///     consistent). The server keeps the same mapping in <c>LocationTable</c>.
    /// </summary>
    private static readonly Dictionary<string, string> _mapIds = new()
    {
        ["Bigmap"] = "bigmap",
        ["Develop"] = "develop",
        ["Factory4Day"] = "factory4_day",
        ["Factory4Night"] = "factory4_night",
        ["Hideout"] = "hideout",
        ["Interchange"] = "interchange",
        ["Laboratory"] = "laboratory",
        ["Labyrinth"] = "labyrinth",
        ["Lighthouse"] = "lighthouse",
        ["PrivateArea"] = "privatearea",
        ["RezervBase"] = "rezervbase",
        ["Sandbox"] = "sandbox",
        ["SandboxHigh"] = "sandbox_high",
        ["Shoreline"] = "shoreline",
        ["Suburbs"] = "suburbs",
        ["TarkovStreets"] = "tarkovstreets",
        ["Terminal"] = "terminal",
        ["Town"] = "town",
        ["Woods"] = "woods",
    };

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _root = Environment.GetEnvironmentVariable("SPT_ARCHIVE_EXPORT_DIR") ?? "/opt/spt/user/export";
        Directory.CreateDirectory(_root);

        await DumpTemplatesAsync(cancellationToken);
        await DumpTradersAsync(cancellationToken);
        await DumpLocalesAsync(cancellationToken);
        await DumpBotsAsync(cancellationToken);
        await DumpLocationsAsync(cancellationToken);
        await DumpHideoutAsync(cancellationToken);

        await WriteAsync("globals.json", globals, cancellationToken);
        await WriteAsync("server.json", server, cancellationToken);
        await WriteAsync("settings.json", settings, cancellationToken);
        await WriteAsync("match.json", match, cancellationToken);

        await DumpRunAsync(cancellationToken);

        Console.WriteLine($"[exporter] wrote export to {_root}");
        Console.Out.Flush();
        Console.Error.Flush();

        // The container exists to produce this export; nothing else should run after it.
        Environment.Exit(0);
    }

    /// <summary>
    ///     One file per collection of the template table. The names are the names that the
    ///     server database uses, except <c>archivedQuests.json</c>, which has no property
    ///     (ADR-0027).
    /// </summary>
    private async Task DumpTemplatesAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("templates/items.json", templates.Items, cancellationToken);
        await WriteAsync("templates/quests.json", templates.Quests, cancellationToken);
        await WriteAsync("templates/handbook.json", templates.Handbook, cancellationToken);
        await WriteAsync("templates/prices.json", templates.Prices, cancellationToken);
        await WriteAsync("templates/achievements.json", templates.Achievements, cancellationToken);
        await WriteAsync("templates/customAchievements.json", templates.CustomAchievements, cancellationToken);
        await WriteAsync("templates/customization.json", templates.Customization, cancellationToken);
        await WriteAsync("templates/customisationStorage.json", templates.CustomisationStorage, cancellationToken);
        await WriteAsync("templates/profiles.json", templates.Profiles, cancellationToken);
        await WriteAsync("templates/character.json", templates.Character, cancellationToken);
        await WriteAsync("templates/dialogue.json", templates.Dialogue, cancellationToken);
        await WriteAsync("templates/repeatableQuests.json", templates.RepeatableQuests, cancellationToken);
        await WriteAsync("templates/prestige.json", templates.Prestige, cancellationToken);
        await WriteAsync("templates/locationServices.json", templates.LocationServices, cancellationToken);
        await WriteAsync("templates/defaultEquipmentPresets.json", templates.DefaultEquipmentPresets, cancellationToken);
    }

    private async Task DumpTradersAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, trader) in traders)
        {
            if (trader is null)
            {
                continue;
            }

            var directory = $"traders/{id}";
            await WriteAsync($"{directory}/base.json", trader.Base, cancellationToken);
            await WriteAsync($"{directory}/assort.json", trader.Assort, cancellationToken);
            await WriteAsync($"{directory}/dialogue.json", trader.Dialogue, cancellationToken);
            await WriteAsync($"{directory}/questassort.json", trader.QuestAssort, cancellationToken);
        }
    }

    /// <summary>
    ///     <see cref="LocaleTable.Global" /> holds lazy loads. Reading <c>Value</c> runs the
    ///     transformers, so a value that a mod rewrote is in the export (ADR-0027).
    /// </summary>
    private async Task DumpLocalesAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("locales/languages.json", locales.Languages, cancellationToken);

        foreach (var (code, language) in locales.Global)
        {
            await WriteAsync($"locales/global/{code}.json", Materialize(language), cancellationToken);
        }

        foreach (var (code, menu) in locales.Menu)
        {
            await WriteAsync($"locales/menu/{code}.json", menu, cancellationToken);
        }
    }

    private static object? Materialize(LazyLoad<GlobalLocaleDictionary>? language)
    {
        return language?.Value;
    }

    private async Task DumpBotsAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("bots/base.json", bots.Base, cancellationToken);
        await WriteAsync("bots/core.json", bots.Core, cancellationToken);

        foreach (var (id, type) in bots.Types)
        {
            await WriteAsync($"bots/types/{id}.json", type, cancellationToken);
        }
    }

    /// <summary>
    ///     One directory per location, with the same file names as the server database.
    ///     <see cref="Location" /> holds three overlays as lazy loads; they are read so that
    ///     the transformers of a mod are applied.
    /// </summary>
    private async Task DumpLocationsAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("locations/base.json", locations.Base, cancellationToken);

        var properties = typeof(LocationTable)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(Location));

        foreach (var property in properties)
        {
            if (property.GetValue(locations) is not Location location)
            {
                continue;
            }

            var map = _mapIds.TryGetValue(property.Name, out var known)
                ? known
                : property.Name.ToLowerInvariant();

            if (!_mapIds.ContainsKey(property.Name))
            {
                Console.WriteLine($"[exporter] unknown map property {property.Name}, writing it as {map}");
            }

            var directory = $"locations/{map}";

            await WriteAsync($"{directory}/base.json", location.Base, cancellationToken);
            await WriteAsync($"{directory}/looseLoot.json", location.LooseLoot?.Value, cancellationToken);
            await WriteAsync($"{directory}/staticLoot.json", location.StaticLoot?.Value, cancellationToken);
            await WriteAsync($"{directory}/staticContainers.json", location.StaticContainers?.Value, cancellationToken);
            await WriteAsync($"{directory}/staticAmmo.json", location.StaticAmmo, cancellationToken);
            await WriteAsync($"{directory}/statics.json", location.Statics, cancellationToken);
            await WriteAsync($"{directory}/allExtracts.json", location.AllExtracts, cancellationToken);
        }
    }

    private async Task DumpHideoutAsync(CancellationToken cancellationToken)
    {
        await WriteAsync("hideout/areas.json", hideout.Areas, cancellationToken);
        await WriteAsync("hideout/customAreas.json", hideout.CustomAreas, cancellationToken);
        await WriteAsync("hideout/customisation.json", hideout.Customisation, cancellationToken);
        await WriteAsync("hideout/production.json", hideout.Production, cancellationToken);
        await WriteAsync("hideout/qte.json", hideout.Qte, cancellationToken);
        await WriteAsync("hideout/settings.json", hideout.Settings, cancellationToken);
    }

    /// <summary>
    ///     <c>run/</c>: the account that a run gives of itself (ADR-0029). <c>loaded.json</c> names
    ///     every mod that loaded, which becomes the dependency set of the observation. The manifest of
    ///     each mod directory records every file with its size and hash, and the text of every
    ///     configuration file is copied as it is on disk.
    /// </summary>
    private async Task DumpRunAsync(CancellationToken cancellationToken)
    {
        var loaded = loadedMods
            .Select(mod => new
            {
                directory = DirectoryName(mod.Directory),
                modGuid = mod.ModMetadata.ModGuid,
                name = mod.ModMetadata.Name,
                author = mod.ModMetadata.Author,
                version = mod.ModMetadata.Version.ToString(),
                // The range the mod declares, not the release it runs on (ADR-0019).
                sptVersion = mod.ModMetadata.SptVersion.ToString(),
                modDependencies = mod.ModMetadata.ModDependencies?
                    .ToDictionary(entry => entry.Key, entry => entry.Value.ToString()),
                hasPrepatcher = mod.ModMetadata.HasPrepatcher,
            })
            .ToList();

        await WriteAsync("run/loaded.json", loaded, cancellationToken);

        foreach (var mod in loadedMods)
        {
            await DumpManifestAsync(mod.Directory, cancellationToken);
        }
    }

    /// <summary>
    ///     One manifest per mod directory, and the text of its configuration files. Paths in the
    ///     manifest are relative to the mod directory; the reader prefixes the directory, because the
    ///     <c>mod_files</c> key holds the whole path under <c>user/mods</c> (ADR-0029).
    /// </summary>
    private async Task DumpManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            Console.WriteLine($"[exporter] mod directory {directory} does not exist");
            return;
        }

        var name = root.Name;
        var files = new List<object>();
        var configs = 0;

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            var kind = ModFileKind.Classify(relative);

            files.Add(new
            {
                path = relative,
                size = file.Length,
                sha256 = await HashAsync(file.FullName, cancellationToken),
                kind,
            });

            if (kind != ModFileKind.Config || file.Length > MaxConfigTextBytes)
            {
                continue;
            }

            // Copied as bytes: a .jsonc file keeps its comments, and a re-serialized file would
            // not be the file that the mod read.
            var target = Path.Combine(_root, "run", "config", name, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.FullName, target, overwrite: true);
            configs++;
        }

        await WriteAsync($"run/files/{name}.json", files, cancellationToken);
        Console.WriteLine($"[exporter] run/files/{name}.json {files.Count} files, {configs} configuration files");
    }

    private static string DirectoryName(string directory)
    {
        return Path.GetFileName(directory.TrimEnd('/', '\\'));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    ///     Writes one file. The serializer streams to disk, because one export holds files of
    ///     more than 100 MB and a string of that size would double the peak memory.
    /// </summary>
    private async Task WriteAsync(string relativePath, object? value, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                value?.GetType() ?? typeof(object),
                JsonUtil.JsonSerializerOptionsNoIndent,
                cancellationToken
            );
        }

        Console.WriteLine($"[exporter] {relativePath} {new FileInfo(path).Length} bytes");
    }
}
