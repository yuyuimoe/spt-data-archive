namespace SptArchive.Core.Entities;

/// <summary>
/// The result of loading one mod version in the isolated server container (ADR-0013): the rows the
/// mod adds and the official rows it changes. Two mod versions with the same content share one dataset.
/// </summary>
/// <remarks>
/// A dataset carries no server version. It is observed on a server release instead
/// (<see cref="ModDataObservation"/>), because one mod version can support several releases.
/// </remarks>
public class ModDataset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The mod that produced the content ("com.author.modname"). Not a version: versions share a dataset.</summary>
    public required string ModGuid { get; set; }

    /// <summary>Hash of the exported content. Two versions with the same hash share this dataset.</summary>
    public required string ContentHash { get; set; }

    /// <summary>Hash per exported table (jsonb), so a changed table is visible without a full diff.</summary>
    public required string TableHashes { get; set; }

    /// <summary>Row count per exported table (jsonb).</summary>
    public required string RowCounts { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One attempt to load one mod version on one server release. It points at a dataset, or it states
/// why there is none (ADR-0013). Silence is not an outcome.
/// </summary>
public class ModDataObservation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The mod version that was loaded. A row of <see cref="Mod"/>.</summary>
    public Guid ModId { get; set; }

    public Mod Mod { get; set; } = null!;

    /// <summary>The server release the attempt ran on.</summary>
    public required string SptVersion { get; set; }

    /// <summary>The dataset this attempt produced, or null when the outcome is not <see cref="ModObservationOutcome.Archived"/>.</summary>
    public Guid? DatasetId { get; set; }

    public ModDataset? Dataset { get; set; }

    public string Outcome { get; set; } = ModObservationOutcome.Archived;

    /// <summary>An archive run produces a dataset. A compatibility probe only checks the outcome (ADR-0016).</summary>
    public string Kind { get; set; } = ModObservationKind.Archive;

    /// <summary>The pinned dependency set as JSON: guid, version and source per entry (ADR-0013).</summary>
    public string? DependencySet { get; set; }

    /// <summary>Hash of <see cref="DependencySet"/>. The baseline cache is keyed by it and the server release.</summary>
    public string? DependencySetHash { get; set; }

    /// <summary>The run that made the attempt, when a run made it.</summary>
    public Guid? ArchiveRunId { get; set; }

    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The result of one mod data observation (ADR-0013).</summary>
public static class ModObservationOutcome
{
    public const string Archived = "archived";
    public const string NotLoaded = "not_loaded";
    public const string DependencyUnresolved = "dependency_unresolved";
    public const string OutsideDeclaredRange = "outside_declared_range";
    public const string Crashed = "crashed";
    public const string Timeout = "timeout";

    public static readonly string[] All =
    [
        Archived,
        NotLoaded,
        DependencyUnresolved,
        OutsideDeclaredRange,
        Crashed,
        Timeout,
    ];
}

/// <summary>Why an observation exists (ADR-0016).</summary>
public static class ModObservationKind
{
    /// <summary>A baseline and a target run. The observation may own a dataset.</summary>
    public const string Archive = "archive";

    /// <summary>One target run against a new server release. It writes no data rows.</summary>
    public const string Probe = "probe";

    public static readonly string[] All = [Archive, Probe];
}

/// <summary>Contract for every row that belongs to one mod dataset.</summary>
/// <remarks>
/// Rows point at the dataset and not at the mod version, so two versions with the same content
/// store their rows once (ADR-0013).
/// </remarks>
public interface IDatasetOwned
{
    Guid DatasetId { get; set; }

    ModDataset Dataset { get; set; }
}
