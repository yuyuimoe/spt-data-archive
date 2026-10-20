namespace SptArchive.Core.Factions;

/// <summary>How the archive linked a bot type to a faction (ADR-0025).</summary>
public enum FactionEvidence
{
    /// <summary>The curated seed has a row. The row may name no faction on purpose.</summary>
    Curated,

    /// <summary>No seed row, but the name belongs to a family whose prefix is the faction.</summary>
    Prefix,

    /// <summary>No seed row and no trusted prefix. The faction is unknown, and it is not a guess.</summary>
    Unknown,
}

/// <summary>The faction of one bot type, with the reason the archive believes it (ADR-0025).</summary>
/// <param name="BotTypeId">The bot type id: the lowercased file name under <c>bots/types/</c>.</param>
/// <param name="FactionId">The faction id, or null when the archive does not know it.</param>
/// <param name="Evidence">Where the answer came from.</param>
public readonly record struct FactionMembership(string BotTypeId, string? FactionId, FactionEvidence Evidence)
{
    /// <summary>True when the archive knows the faction.</summary>
    public bool IsResolved => FactionId is not null;
}
