namespace SptArchive.Core.Entities;

/// <summary>
/// The source of one object of the server database that a dataset adds and that has no <c>mod_*</c> table
/// of its own: a location, a bot type, a hideout part, a price or a handbook entry (ADR-0041).
/// </summary>
/// <remarks>
/// <para>
/// The template row states that the object exists and what the dataset did to it. This row keeps what
/// the run wrote, as it wrote it (ADR-0002). A modified object has no row here: its changed values
/// are rows of <see cref="ModChangePath"/> under the template, the way a modified item is.
/// </para>
/// <para>
/// A location is a directory of files, and a file can be 126 MB. One row holds one file, named by its
/// <see cref="Part"/>, so no value grows past the size of one file. Every other object has one part,
/// and its name is empty.
/// </para>
/// </remarks>
public class ModDatabaseObject
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;

    /// <summary>One of <see cref="ModTemplateKind.DatabaseObjects"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>The address of the object (ADR-0034). It is also the key of its <see cref="ModTemplate"/>.</summary>
    public required string TemplateId { get; set; }

    /// <summary>The file of a location, such as <c>base</c> or <c>looseLoot</c>. Empty for every other kind.</summary>
    public string Part { get; set; } = string.Empty;

    /// <summary>The object as the run wrote it, as JSON.</summary>
    public required string Data { get; set; }

    /// <summary>
    /// The size of <see cref="Data" /> in bytes, as the run wrote it. It is a fact of its own and not a length
    /// that a reader derives from the document: PostgreSQL stores a large value outside the row and compressed,
    /// so reading the size of the value costs 138 ms and 175 MB of memory on the 132 MB loose loot file of
    /// lighthouse, while reading this column costs nothing. The Api refuses a document that is too large to
    /// serve, and it decides with this number, before it reads a byte of the document (ADR-0041).
    /// </summary>
    public long Bytes { get; set; }

    public ModTemplate Template { get; set; } = null!;
}
