namespace SptArchive.Archiver;

/// <summary>Settings for the archive job, bound from the "Archive" configuration section.</summary>
public sealed class ArchiveOptions
{
    public const string Section = "Archive";

    /// <summary>
    /// Path to the server's <c>SPT_Data/database</c> directory. It is only ever read, so mounting it read-only is fine.
    /// </summary>
    public string DatabasePath { get; set; } = "/spt-data/database";

    /// <summary>
    /// The server release that the source files belong to, for example "4.0.13" (ADR-0014).
    /// The server keeps its own version in the compiled assembly, not in the database files,
    /// so the run must state it. Set <c>Archive__SptVersion</c> or <c>--spt-version</c>.
    /// </summary>
    public string? SptVersion { get; set; }

    /// <summary>Apply pending EF Core migrations before importing.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Import even if the source files are identical to the last successful run.</summary>
    public bool Force { get; set; }

    /// <summary>How many rows are written per SaveChanges call.</summary>
    public int BatchSize { get; set; } = 5000;
}
