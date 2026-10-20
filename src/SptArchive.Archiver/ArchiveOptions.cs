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

    /// <summary>
    /// Path to the export of a baseline run: the same release with the exporter and its dependencies
    /// but no target mod (ADR-0013). A mod run is compared against it, so the rows the mod contributed
    /// are what the dataset stores. The baseline belongs to the host, not to the mod, so it is
    /// configured here and not inside the export; the runner that made it knows the path.
    /// Set <c>Archive__BaselinePath</c>.
    /// </summary>
    public string? BaselinePath { get; set; }

    /// <summary>
    /// Path to the export of one mod run (ADR-0029): the tables the mod left and the <c>run/</c> account
    /// that says which mods loaded, on which release. When it is set, the Archiver imports that run
    /// instead of an official installation, and <see cref="BaselinePath"/> becomes required.
    /// Set <c>Archive__RunPath</c>.
    /// </summary>
    public string? RunPath { get; set; }

    /// <summary>
    /// The GUID of the mod that the run is about. A run also loads its pinned dependencies, and a
    /// dependency is never the target (ADR-0021), so the run states which one it is.
    /// Set <c>Archive__RunModGuid</c>.
    /// </summary>
    public string? RunModGuid { get; set; }

    /// <summary>Apply pending EF Core migrations before importing.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Import even if the source files are identical to the last successful run.</summary>
    public bool Force { get; set; }

    /// <summary>How many rows are written per SaveChanges call.</summary>
    public int BatchSize { get; set; } = 5000;
}
