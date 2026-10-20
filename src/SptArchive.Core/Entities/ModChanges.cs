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

    /// <summary>"item", "quest" or "trader".</summary>
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

    public static readonly string[] All = [Item, Quest, Trader, Customization, Assort];
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
