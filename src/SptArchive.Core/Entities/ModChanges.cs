namespace SptArchive.Core.Entities;

/// <summary>
/// One template that a mod dataset touches, with the operation it applies to it (ADR-0017).
/// A template is one item, quest or trader, identified by its template id.
/// </summary>
/// <remarks>
/// The operation lives here and not on the path rows. Under the ADR-0013 shape an added item and a
/// new property of an old item both had no old value, so the archive could not tell them apart.
/// </remarks>
public class ModTemplate
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;

    /// <summary>One of <see cref="ModTemplateKind.All"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>The template id, the quest id or the trader id.</summary>
    public required string TemplateId { get; set; }

    /// <summary>"added", "modified" or "removed".</summary>
    public required string Op { get; set; }

    /// <summary>The item <c>_parent</c> node of an added item. Null otherwise.</summary>
    public string? ParentId { get; set; }
}

/// <summary>
/// One changed value under a <see cref="ModTemplate"/> (ADR-0017, ADR-0032). The path is an RFC 6901
/// JSON pointer such as <c>/_props/Weight</c> or <c>/barter/&lt;template address&gt;/count</c>.
/// </summary>
/// <remarks>
/// The old value comes from the baseline of the run, which holds the official data of the release in
/// the same shape. An added template has no old value, and a removed template has no new value; the
/// Archiver enforces both. One row per leaf: a path that an object or an array holds is one row
/// (ADR-0032, decision 2).
/// </remarks>
public class ModChangePath
{
    public Guid DatasetId { get; set; }

    /// <summary>The kind of the parent template. The column repeats the parent key so the key can name it.</summary>
    public string Kind { get; set; } = ModTemplateKind.Item;

    public required string TemplateId { get; set; }

    /// <summary>The JSON pointer of the changed value.</summary>
    public required string Path { get; set; }

    /// <summary>"set" or "remove".</summary>
    public required string Op { get; set; }

    /// <summary>The value before the mod, as JSON. Null for an added template or a new property.</summary>
    public string? OldValue { get; set; }

    /// <summary>The value after the mod, as JSON. Null for a removed property.</summary>
    public string? NewValue { get; set; }

    public ModTemplate Template { get; set; } = null!;
}

/// <summary>What a mod dataset touched (ADR-0017, ADR-0032).</summary>
public static class ModTemplateKind
{
    public const string Item = "item";
    public const string Quest = "quest";
    public const string Trader = "trader";

    /// <summary>A player customization, which the server keeps apart from the item tree (ADR-0022).</summary>
    public const string Customization = "customization";

    /// <summary>
    /// One offer of a trader's assort, named by its address rather than by the instance id that the
    /// server renews on every boot (ADR-0032, decision 4). Its parent is the trader.
    /// </summary>
    public const string Assort = "assort";

    /// <summary>
    /// A map of the server database: <c>locations/&lt;map&gt;/*.json</c>, named by its directory (ADR-0041).
    /// A path under it starts with the part, the file of the map, such as <c>/base/Insurance</c>.
    /// </summary>
    public const string Location = "location";

    /// <summary>A bot type, or one of the two bot files, named by its path under <c>bots/</c> (ADR-0041).</summary>
    public const string Bot = "bot";

    /// <summary>One area, recipe or slot of the hideout, or the hideout settings (ADR-0041).</summary>
    public const string Hideout = "hideout";

    /// <summary>The price of one item in <c>templates/prices.json</c>, named by the address of the item (ADR-0041).</summary>
    public const string Price = "price";

    /// <summary>One category or one item entry of <c>templates/handbook.json</c> (ADR-0041).</summary>
    public const string Handbook = "handbook";

    /// <summary>One element of <c>templates/dialogue.json</c>, named by its <c>Id</c>. A changed line is a path (ADR-0044).</summary>
    public const string Dialogue = "dialogue";

    /// <summary>One item preset of <c>globals.json</c>, named by the preset id (ADR-0044).</summary>
    public const string Preset = "preset";

    /// <summary>One entry of <c>bot_presets</c> in <c>globals.json</c>, named by its <c>Role</c> (ADR-0044).</summary>
    public const string BotPreset = "bot-preset";

    /// <summary>One entry of <c>BotWeaponScatterings</c> in <c>globals.json</c>, named by its <c>Name</c> (ADR-0044).</summary>
    public const string Scattering = "scattering";

    /// <summary>A key of <c>globals.json</c> that holds no list, such as <c>config</c>, kept as one object (ADR-0044).</summary>
    public const string Global = "global";

    /// <summary><c>settings.json</c>, kept as one object whose changed keys are paths (ADR-0044).</summary>
    public const string Setting = "setting";

    /// <summary>One edition of <c>templates/profiles.json</c>, named by the edition (ADR-0044).</summary>
    public const string Profile = "profile";

    /// <summary>One object of <c>templates/achievements.json</c>, named by its <c>id</c> (ADR-0044).</summary>
    public const string Achievement = "achievement";

    /// <summary>One object of <c>templates/customisationStorage.json</c>, named by its <c>id</c> (ADR-0044).</summary>
    public const string Storage = "storage";

    /// <summary>One string of <c>templates/character.json</c>, named by the string (ADR-0044).</summary>
    public const string Character = "character";

    /// <summary>True for a kind that ADR-0044 reads out of one of the seven files that held no row.</summary>
    public static bool IsFileRow(string kind) =>
        kind is Dialogue or Preset or BotPreset or Scattering or Global or Setting or Profile or Achievement or Storage or Character;

    public static readonly string[] All =
    [
        Item, Quest, Trader, Customization, Assort, Location, Bot, Hideout, Price, Handbook,
        Dialogue, Preset, BotPreset, Scattering, Global, Setting, Profile, Achievement, Storage, Character,
    ];

    /// <summary>The kinds whose source is an object of the server database that has no table of its own (ADR-0041, ADR-0044).</summary>
    public static readonly string[] DatabaseObjects =
    [
        Location, Bot, Hideout, Price, Handbook,
        Dialogue, Preset, BotPreset, Scattering, Global, Setting, Profile, Achievement, Storage, Character,
    ];
}

/// <summary>The operation of one template under a dataset (ADR-0017).</summary>
public static class ModTemplateOperation
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Removed = "removed";

    public static readonly string[] All = [Added, Modified, Removed];
}

/// <summary>The operation of one changed path (ADR-0017).</summary>
public static class ModChangePathOperation
{
    public const string Set = "set";
    public const string Remove = "remove";

    public static readonly string[] All = [Set, Remove];
}
