using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
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
    public Version Version { get; init; } = new("1.1.0");
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
    SettingsTable settings
) : IOnLoad
{
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
