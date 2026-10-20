namespace SptArchive.Core.Entities;

/// <summary>
/// Shared shape of one player customization: a body, head, hands or feet the player can wear.
/// The server keeps these in <c>templates/customization.json</c>, apart from <c>templates/items.json</c>,
/// so they get their own table pair (ADR-0022).
/// </summary>
/// <remarks>
/// The entry points at a client asset bundle (<c>_props.Prefab.path</c>). The archive stores the path,
/// not the bundle: it records data, not binaries.
/// </remarks>
public abstract class CustomizationBase
{
    /// <summary>24-character hex id (the server's "MongoId").</summary>
    public required string Id { get; set; }

    /// <summary>Internal name (<c>_name</c>), not localized.</summary>
    public required string Name { get; set; }

    /// <summary>Parent node in the customization tree (<c>_parent</c>).</summary>
    public required string ParentId { get; set; }

    /// <summary>"Item" for a wearable, "Node" for a category (<c>_type</c>).</summary>
    public required string Type { get; set; }

    /// <summary>The sides that may wear it: "Usec", "Bear", "Savage". An empty array means every side.</summary>
    public string[] Sides { get; set; } = [];

    /// <summary>"Body", "Head", "Hands" or "Feet"; null for a category node.</summary>
    public string? BodyPart { get; set; }

    /// <summary>The client bundle this entry needs (<c>_props.Prefab.path</c>).</summary>
    public string? PrefabPath { get; set; }

    /// <summary>True when the entry is offered without being unlocked (<c>_props.AvailableAsDefault</c>).</summary>
    public bool AvailableAsDefault { get; set; }

    /// <summary>The complete <c>_props</c> object as JSON (jsonb).</summary>
    public required string Props { get; set; }
}

/// <summary>A customization that ships with the server, so it belongs to one server release (ADR-0014).</summary>
public abstract class OfficialCustomizationBase : CustomizationBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

/// <summary>A customization that ships with the server itself.</summary>
public class Customization : OfficialCustomizationBase;

/// <summary>A customization added by a mod dataset.</summary>
public class ModCustomization : CustomizationBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}
