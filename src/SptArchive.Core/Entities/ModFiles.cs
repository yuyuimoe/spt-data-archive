namespace SptArchive.Core.Entities;

/// <summary>
/// One file that a run loaded, recorded as a manifest entry and, for a configuration file, as text
/// (ADR-0029). Owned by the dataset, like every other mod row (ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// The path is relative to the run's <c>user/mods</c> directory and starts with the mod directory.
/// The mod directory is part of the path because one dataset loads a target mod and its pinned
/// dependencies, so <c>config/config.json</c> alone would name several files (ADR-0029).
/// </para>
/// <para>
/// <see cref="ContentText"/> holds the bytes that the mod read. A <c>.jsonc</c> file with comments is
/// not valid JSON, and a parsed copy without the comments would be a file that never existed.
/// </para>
/// </remarks>
public class ModFile
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;

    /// <summary>Path relative to the run's <c>user/mods</c> directory, with <c>/</c> as the separator.</summary>
    public required string Path { get; set; }

    /// <summary>"assembly", "bundle", "data", "config", "text" or "other".</summary>
    public required string Kind { get; set; }

    /// <summary>Bytes on disk when the run stopped.</summary>
    public long Size { get; set; }

    /// <summary>SHA-256 of the file bytes when the run stopped.</summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// SHA-256 of the file before the server started. It names the file that the mod found, so a
    /// different value says that the mod rewrote the file during its load. Null when no pre-boot
    /// snapshot exists (an official import, or a run outside the container runner).
    /// </summary>
    public string? ShippedSha256 { get; set; }

    /// <summary>The text, when the file is a configuration file and is at most 2 MB. Null otherwise.</summary>
    public string? ContentText { get; set; }

    /// <summary>The parsed document (jsonb), when the text parses. Null otherwise.</summary>
    public string? Content { get; set; }
}
