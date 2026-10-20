namespace SptArchive.Core.Entities;

/// <summary>A language the server ships locales for. Code is the server's own code ("en", "ru", "po", ...).</summary>
public class Language
{
    public required string Code { get; set; }

    public required string Name { get; set; }
}

/// <summary>
/// One execution of the Archiver. Gives every dataset a provenance, and lets the job skip
/// work when the source files have not changed since the last successful run.
/// </summary>
public class ArchiveRun
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>
    /// The process of the run stopped without recording a result, and its lease ran out (ADR-0036).
    /// Another Archiver closed the row, because a killed process reaches no <c>catch</c> block.
    /// </summary>
    public const string Interrupted = "interrupted";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// The last time the process of the run said it was alive (ADR-0036). A run that goes silent for
    /// longer than the lease is over, and the next Archiver closes it as <see cref="Interrupted"/>.
    /// The column is what makes a sweep safe while other runs are importing: a live run keeps writing it.
    /// </summary>
    public DateTimeOffset? HeartbeatAt { get; set; }

    /// <summary>SHA-256 over the relative path and content hash of every source file.</summary>
    public required string SourceHash { get; set; }

    /// <summary>
    /// The server release this run imports (ADR-0014). It is not a foreign key to
    /// <c>spt_versions</c>: that row only becomes real when the import commits, and a failed run
    /// must not make an empty release look like the latest one.
    /// </summary>
    public string? SptVersion { get; set; }

    public string Status { get; set; } = Running;

    public string? Error { get; set; }

    /// <summary>What the run is: an official import, a mod run or a probe (ADR-0033).</summary>
    public string Kind { get; set; } = ArchiveRunKind.Official;

    /// <summary>One line that says why the run happened. A mod run and a probe state one.</summary>
    public string? Purpose { get; set; }

    /// <summary>The GUID of the mod the run is about. Null for an official import.</summary>
    public string? TargetModGuid { get; set; }

    /// <summary>The version of the mod the run is about. Null for an official import.</summary>
    public string? TargetVersion { get; set; }

    /// <summary>The server release of the baseline that the run was compared against.</summary>
    public string? BaselineRelease { get; set; }

    /// <summary>
    /// The hash of the dependency set of the baseline (ADR-0013): the half of the baseline cache key that
    /// names what the baseline loaded. The baseline path is not stored, because a path is not an identity.
    /// </summary>
    public string? BaselineHash { get; set; }

    /// <summary>
    /// What came out of the run, as jsonb (ADR-0033): counts and warnings. A failed run states its
    /// error and the stage that failed. Rows hold the same values; the copy exists for the run that has no dataset.
    /// </summary>
    public string? Result { get; set; }
}

/// <summary>The kinds of run (ADR-0033, decision 1).</summary>
public static class ArchiveRunKind
{
    /// <summary>An import of the official database of one release.</summary>
    public const string Official = "official";

    /// <summary>An import of the export of one mod run.</summary>
    public const string Mod = "mod";

    /// <summary>A check of one mod version against a new release (ADR-0016). It writes no data rows.</summary>
    public const string Probe = "probe";

    public static readonly string[] All = [Official, Mod, Probe];
}
