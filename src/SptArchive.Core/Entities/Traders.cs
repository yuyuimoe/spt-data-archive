namespace SptArchive.Core.Entities;

// -------------------------------------------------------------------------------------------------
// Traders
// -------------------------------------------------------------------------------------------------

/// <summary>
/// Shared shape of a trader. The trader <c>base</c> object (loyalty levels, buy categories,
/// repair, insurance, ...) is large and nested, so it is kept whole in <see cref="Base"/> (jsonb).
/// What a trader sells is normalized into the assort tables below.
/// </summary>
public abstract class TraderBase
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string? Nickname { get; set; }

    public string? Currency { get; set; }

    public string? Location { get; set; }

    /// <summary>The complete <c>base.json</c> object (jsonb).</summary>
    public required string Base { get; set; }

    /// <summary>Dialogue lines (jsonb), when the trader has a dialogue file.</summary>
    public string? Dialogue { get; set; }

    /// <summary>Clothing offers (jsonb), when the trader sells customization.</summary>
    public string? Suits { get; set; }

    /// <summary>Special services such as the lighthouse or BTR services (jsonb).</summary>
    public string? Services { get; set; }
}

/// <summary>A trader that ships with the server, so it belongs to one server release (ADR-0014).</summary>
public abstract class OfficialTraderBase : TraderBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

public class Trader : OfficialTraderBase;

public class ModTrader : TraderBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}

// -------------------------------------------------------------------------------------------------
// Assort (what a trader sells)
// -------------------------------------------------------------------------------------------------

/// <summary>
/// One entry of a trader's assort. An offer is a tree: the root item has <c>ParentId = "hideout"</c>
/// and carries the barter scheme and loyalty level; child rows are the attached parts
/// (for example the mods of a weapon preset) and point at their parent through <see cref="ParentId"/>.
/// </summary>
public abstract class AssortItemBase
{
    public required string TraderId { get; set; }

    /// <summary>The instance id of this entry inside the assort (<c>_id</c>), unique per trader.</summary>
    public required string InstanceId { get; set; }

    /// <summary>The item template being sold (<c>_tpl</c>). Deliberately not a foreign key: it may be a modded item.</summary>
    public required string TemplateId { get; set; }

    public string? ParentId { get; set; }

    public string? SlotId { get; set; }

    /// <summary>Stack size, buy restrictions, durability, ... (jsonb).</summary>
    public string? Upd { get; set; }

    /// <summary>Loyalty level required to buy. Only set on root entries.</summary>
    public int? LoyaltyLevel { get; set; }

    /// <summary>What the trader wants in exchange (jsonb: alternatives of lists of {count, _tpl}). Only set on root entries.</summary>
    public string? BarterScheme { get; set; }
}

public abstract class OfficialAssortItemBase : AssortItemBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

public class TraderAssortItem : OfficialAssortItemBase;

/// <summary>
/// Assort entry added by a mod. <see cref="AssortItemBase.TraderId"/> may name an official trader
/// (mods often add stock to existing traders) or a trader added by a mod, so it is not a foreign key.
/// </summary>
public class ModTraderAssortItem : AssortItemBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}

// -------------------------------------------------------------------------------------------------
// Quest assort (which quest unlocks which offer)
// -------------------------------------------------------------------------------------------------

public abstract class QuestAssortBase
{
    public required string TraderId { get; set; }

    /// <summary>"started", "success" or "fail": the quest state that unlocks the offer.</summary>
    public required string Status { get; set; }

    /// <summary>Instance id of the assort entry that gets unlocked.</summary>
    public required string AssortInstanceId { get; set; }

    public required string QuestId { get; set; }
}

public abstract class OfficialQuestAssortBase : QuestAssortBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

public class TraderQuestAssort : OfficialQuestAssortBase;

public class ModTraderQuestAssort : QuestAssortBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}
