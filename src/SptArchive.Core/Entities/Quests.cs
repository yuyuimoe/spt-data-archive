namespace SptArchive.Core.Entities;

/// <summary>
/// Shared shape of a quest. Conditions, rewards and the many optional fields are deeply nested and
/// vary per quest, so the complete quest object lives in <see cref="Data"/> (jsonb) and only the
/// fields used to find quests are promoted to columns.
/// </summary>
public abstract class QuestBase
{
    public required string Id { get; set; }

    /// <summary>Human readable quest name added by the server (<c>QuestName</c>).</summary>
    public string? QuestName { get; set; }

    /// <summary>Id of the trader that gives the quest.</summary>
    public required string TraderId { get; set; }

    public string? Location { get; set; }

    public string? Type { get; set; }

    /// <summary>"Pmc", "Scav" or "Any".</summary>
    public string? Side { get; set; }

    public bool Restartable { get; set; }

    /// <summary>The complete quest object as JSON (jsonb).</summary>
    public required string Data { get; set; }
}

/// <summary>A quest that ships with the server, so it belongs to one server release (ADR-0014).</summary>
public abstract class OfficialQuestBase : QuestBase
{
    /// <summary>The server release this row belongs to. Part of the primary key.</summary>
    public required string SptVersion { get; set; }
}

public class Quest : OfficialQuestBase;

public class ModQuest : QuestBase, IDatasetOwned
{
    public Guid DatasetId { get; set; }

    public ModDataset Dataset { get; set; } = null!;
}
