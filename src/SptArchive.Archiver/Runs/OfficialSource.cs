namespace SptArchive.Archiver.Runs;

/// <summary>
/// What an installation says about itself: the mods the server loaded and the release it ran
/// (ADR-0029). An official import holds the server's own data, so the installation must load no mod
/// (ADR-0033, decision 1).
/// </summary>
/// <remarks>
/// The checks read <c>run/</c>, which the exporter wrote, and no table. A source from an exporter
/// older than 1.2.0 states nothing, and then the archive cannot prove the rule: the import warns
/// and goes on, because a release archived once cannot be re-exported by an exporter that never
/// ran on it (TD-50).
/// </remarks>
public static class OfficialSource
{
    /// <summary>
    /// The mods the installation loaded, the exporter aside. Empty when it loaded nothing else.
    /// Null when the source does not say, which is the case for an exporter older than 1.2.0.
    /// </summary>
    public static IReadOnlyList<LoadedMod>? Mods(string databasePath) =>
        RunDirectory.TryReadLoadedMods(databasePath)?
            .Where(mod => !mod.IsTool)
            .ToArray();

    /// <summary>
    /// Refuses an installation that loaded a mod. The exporter itself is the tool that made the
    /// export, so it is the one mod an official source is allowed to carry.
    /// </summary>
    public static void EnsureNoMod(string databasePath)
    {
        var mods = Mods(databasePath);

        if (mods is null || mods.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{databasePath}' loaded {mods.Count} mod(s): " +
            string.Join(", ", mods.Select(mod => $"{mod.ModGuid} {mod.Version}")) +
            ". An official import holds the data of the server alone, so the mods folder of the installation " +
            "must hold nothing but the exporter (ADR-0033, decision 1). Remove the mods and export the " +
            "installation again.");
    }

    /// <summary>
    /// Refuses a source that states a release other than the one the run names. Every official row
    /// states the release of the files it came from (ADR-0014), and the row would be a lie.
    /// </summary>
    public static void EnsureRelease(string databasePath, string sptVersion)
    {
        var stated = RunDirectory.TryReadServerRelease(databasePath);

        if (stated is null || string.Equals(stated, sptVersion, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{databasePath}' states the release {stated} in run/release.json, but this run imports {sptVersion}. " +
            "Every official row states the release of the files it came from (ADR-0014). State the release " +
            "that the source belongs to.");
    }
}
