namespace SptArchive.Archiver.Runs;

/// <summary>
/// The export of a run with no target mod: the server as the release ships it, after it loaded its
/// own database. A mod run is compared against it, so only the work of the mod remains (ADR-0013).
/// </summary>
/// <remarks>
/// The path comes from configuration (<c>Archive:BaselinePath</c>), because the baseline is a fact
/// about the host and not about the mod: the same baseline serves every mod run of its release. Later
/// the baseline cache of ADR-0013 fills the same option, keyed by the dependency set and the release.
/// </remarks>
public sealed record BaselineExport(RunDirectory Run, bool ReleaseVerified)
{
    /// <summary>
    /// Reads the baseline and checks that it belongs to <paramref name="sptVersion"/>.
    /// </summary>
    /// <remarks>
    /// A baseline of another release is refused, not used: its difference from the run would be the
    /// difference between the two releases, and every mod would appear to change the game. A baseline
    /// of an exporter older than 1.3.0 states no release; it is used, and the caller warns, because
    /// the alternative is to refuse every export that already exists.
    /// </remarks>
    public static BaselineExport Read(string path, string sptVersion)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"The baseline '{path}' is not a directory. Set Archive:BaselinePath (Archive__BaselinePath) " +
                "to the export of a baseline run: a run of the same release with the exporter and the " +
                "dependencies but no target mod (docs/runbooks/mod-export.md).");
        }

        var run = RunDirectory.Read(path);

        if (run.ServerRelease is null)
        {
            return new BaselineExport(run, ReleaseVerified: false);
        }

        if (!string.Equals(run.ServerRelease, sptVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The baseline '{path}' was made on server release {run.ServerRelease}, and this run " +
                $"states {sptVersion}. A difference between two releases is not the work of a mod " +
                "(ADR-0014), so the run needs the baseline of its own release.");
        }

        return new BaselineExport(run, ReleaseVerified: true);
    }
}
