namespace SptArchive.Core.Canonical;

/// <summary>
/// The files of an export that hold rows, by their path inside the export (ADR-0027). One place for
/// the names, so a rule that reads a file and a rule that names the same file cannot drift apart.
/// </summary>
public static class CanonicalFiles
{
    /// <summary>The item templates, keyed by template id.</summary>
    public const string Items = "templates/items.json";

    /// <summary>The quests, keyed by quest id.</summary>
    public const string Quests = "templates/quests.json";

    /// <summary>The customization items, keyed by customization id.</summary>
    public const string Customizations = "templates/customization.json";

    /// <summary>The directory of the translation files: <c>locales/&lt;scope&gt;/&lt;language&gt;.json</c>.</summary>
    public const string Locales = "locales";

    /// <summary>The directory of the traders: one directory per trader, named by its id.</summary>
    public const string Traders = "traders";

    /// <summary>The directory of the locations: one directory per map, named by its id.</summary>
    public const string Locations = "locations";

    /// <summary>The directory of the bot types: one file per type, named by its id.</summary>
    public const string BotTypes = "bots/types";

    /// <summary>
    /// The file of a map that holds its loot: <c>locations/&lt;map&gt;/looseLoot.json</c>. It is a directory of
    /// spawn points, and each one is a row (ADR-0043).
    /// </summary>
    public const string LooseLoot = "looseLoot";

    /// <summary>The list of the spawn points of a map: loot that may appear.</summary>
    public const string SpawnPoints = "spawnpoints";

    /// <summary>The list of the spawn points that always appear.</summary>
    public const string SpawnPointsForced = "spawnpointsForced";

    /// <summary>True for a table that holds one object per id, and the object carries that id again.</summary>
    public static bool IsTemplateTable(string relativePath) =>
        relativePath is Items or Quests or Customizations;
}
