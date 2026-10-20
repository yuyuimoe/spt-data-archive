namespace SptArchive.Core.Entities;

/// <summary>
/// One translated string. The server stores locales as flat key/value files per language, with keys
/// such as <c>"5422acb9af1c889c16000029 Name"</c>. Entity keys are split into <see cref="EntityId"/>
/// and <see cref="Field"/> so translations can be joined to items, quests and traders; UI strings
/// that are not tied to an entity keep both null.
/// </summary>
public abstract class LocaleEntryBase
{
    /// <summary>Which locale file family the entry came from: "global" (more to come: menu, server, web).</summary>
    public required string Scope { get; set; }

    public required string LanguageCode { get; set; }

    /// <summary>The full original key, exactly as in the source file.</summary>
    public required string Key { get; set; }

    public required string Value { get; set; }

    /// <summary>The 24-hex entity id when the key has the form "&lt;id&gt; &lt;Field&gt;".</summary>
    public string? EntityId { get; set; }

    /// <summary>The field part of the key ("Name", "ShortName", "Description", ...).</summary>
    public string? Field { get; set; }
}

/// <summary>A translation that ships with the server, so it belongs to one server release (ADR-0014).</summary>
public abstract class OfficialLocaleEntryBase : LocaleEntryBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

public class LocaleEntry : OfficialLocaleEntryBase;

public class ModLocaleEntry : LocaleEntryBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}
