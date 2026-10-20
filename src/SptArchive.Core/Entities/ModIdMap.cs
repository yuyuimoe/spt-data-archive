namespace SptArchive.Core.Entities;

/// <summary>
/// The id that a run gave an object, and the address the archive derived for it (ADR-0034, decision 7).
/// </summary>
/// <remarks>
/// <para>
/// The address is the name of the object in every row of the dataset. This table is the way back: a
/// reader that holds an id from the run, from a log or from the game finds the object the archive named.
/// </para>
/// <para>
/// A pinned row names an object that the baseline of the release holds, so its address is its own id.
/// A derived row names an object that the run made, and its address is a content ULID.
/// </para>
/// <para>
/// The map is not part of the content hash: a boot that mints new ids changes the map and changes no
/// address (ADR-0034, decision 5 and 7).
/// </para>
/// </remarks>
public class ModIdMap
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;

    /// <summary>"item", "quest", "customization" or "trader".</summary>
    public required string Kind { get; set; }

    /// <summary>The id that the run gave the object.</summary>
    public required string RawId { get; set; }

    /// <summary>The name of the object: its own id when the baseline holds it, a content ULID otherwise.</summary>
    public required string Address { get; set; }

    /// <summary>True when the baseline of the release holds the id, so the address is that id.</summary>
    public bool Pinned { get; set; }
}
