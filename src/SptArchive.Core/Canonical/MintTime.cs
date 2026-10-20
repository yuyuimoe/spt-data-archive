using System.Globalization;

namespace SptArchive.Core.Canonical;

/// <summary>
/// Reads the time a MongoId was minted and finds the objects that a run made while it ran (ADR-0034,
/// decision 9). A mod that mints its ids at boot gives its objects a new id on every run. The archive
/// names such an object by its content, so the import does not fail. It states the fact in a warning.
/// </summary>
public static class MintTime
{
    /// <summary>
    /// A mint time must fall inside this window after the start of the run to count. A run lasts minutes,
    /// and a hand-written id that happens to decode to a far future date is not a mint time.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // MongoDB shipped in 2009. An earlier time is a hand-written id, and not a creation time.
    private static readonly DateTimeOffset Earliest = new(2009, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The creation time that a MongoId holds: the first 4 bytes, big-endian seconds since the Unix epoch.
    /// Null when the id is not 24 lowercase hex characters, or when the time is not plausible.
    /// </summary>
    public static DateTimeOffset? CreatedAt(string rawId)
    {
        if (rawId.Length != 24 || !rawId.All(IsLowerHex))
        {
            return null;
        }

        var seconds = long.Parse(rawId.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var time = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return time < Earliest ? null : time;
    }

    /// <summary>
    /// One warning for each object that the run named by its content (not pinned) and whose id was minted
    /// after the run started. A pinned object keeps the id of the baseline, which an earlier run minted.
    /// </summary>
    public static IReadOnlyList<string> Warnings(
        IEnumerable<Addressing.AddressEntry> entries,
        DateTimeOffset runStarted)
    {
        var warnings = new List<string>();

        foreach (var entry in entries.Where(entry => !entry.Pinned))
        {
            var minted = CreatedAt(entry.RawId);

            if (minted is null || minted <= runStarted || minted > runStarted + Window)
            {
                continue;
            }

            warnings.Add(
                $"{entry.Kind} '{entry.RawId}' was minted at {minted:O}, after the run started at {runStarted:O}. " +
                $"The mod makes its own ids, so the archive names the object '{entry.Address}' by its content.");
        }

        return warnings;
    }

    private static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';
}
