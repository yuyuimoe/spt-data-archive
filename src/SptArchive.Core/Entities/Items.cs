namespace SptArchive.Core.Entities;

/// <summary>
/// Shared shape of an item template. Not an entity itself: EF Core maps these columns onto
/// each concrete type, so <see cref="Item"/> and <see cref="ModItem"/> get identical tables.
/// </summary>
/// <remarks>
/// The server's <c>_props</c> object has hundreds of optional fields that differ per item type,
/// so only the fields people filter and sort by are promoted to real columns. The full object
/// is kept in <see cref="Props"/> (jsonb, GIN-indexed) so nothing is lost.
/// </remarks>
public abstract class ItemBase
{
    /// <summary>24-character hex id (the server's "MongoId").</summary>
    public required string Id { get; set; }

    /// <summary>Internal name (<c>_name</c>), not localized.</summary>
    public required string Name { get; set; }

    /// <summary>Parent node in the item tree (<c>_parent</c>).</summary>
    public required string ParentId { get; set; }

    /// <summary>"Item" for real items, "Node" for categories (<c>_type</c>).</summary>
    public required string Type { get; set; }

    /// <summary>Prototype id (<c>_proto</c>), when present.</summary>
    public string? Prototype { get; set; }

    // Promoted from _props.
    public double? Weight { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public int? StackMaxSize { get; set; }

    /// <summary>The complete <c>_props</c> object as JSON (jsonb).</summary>
    public required string Props { get; set; }
}

/// <summary>An item that ships with the server, so it belongs to one server release (ADR-0014).</summary>
public abstract class OfficialItemBase : ItemBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

/// <summary>An item that ships with the server itself.</summary>
public class Item : OfficialItemBase;

/// <summary>An item added by a mod dataset.</summary>
public class ModItem : ItemBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}
