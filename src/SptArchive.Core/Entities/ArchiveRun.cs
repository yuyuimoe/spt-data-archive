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

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

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
}
