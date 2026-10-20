using System.Globalization;

namespace SptArchive.Core.Entities;

/// <summary>
/// One server release that the archive holds (ADR-0014). Every official row states its release,
/// so the answer to "which version is this item from" is a column and not a guess.
/// </summary>
/// <remarks>
/// The version text sorts badly ("4.0.13" before "4.0.9"), so <see cref="VersionParts"/> holds the
/// numbers and every sort and "latest" question uses that column.
/// </remarks>
public class SptVersion
{
    /// <summary>How many numbers a server version can state. Missing numbers are written as 0.</summary>
    public const int PartCount = 4;

    /// <summary>The release key, for example "4.0.13".</summary>
    public required string Version { get; set; }

    /// <summary>The release key as numbers, always <see cref="PartCount"/> long. Sorts semantically.</summary>
    public required int[] VersionParts { get; set; }

    /// <summary>When the archive first imported this release.</summary>
    public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Reads a release key such as "4.0.13" or "4.1". Rejects anything that is not a release key:
    /// ranges ("^4.0.13", ">=4.0.0 &lt;4.1.0"), labels ("latest") and extra text (ADR-0019).
    /// </summary>
    public static int[] ParseParts(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new FormatException("A server version is required. Example: 4.0.13.");
        }

        var text = version.Trim();
        var segments = text.Split('.');

        if (segments.Length is < 1 or > PartCount)
        {
            throw NotAReleaseKey(text);
        }

        var parts = new int[PartCount];

        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw NotAReleaseKey(text);
            }

            parts[index] = value;
        }

        return parts;
    }

    private static FormatException NotAReleaseKey(string text) =>
        new($"'{text}' is not a server version. A server version is one release key: digits separated by dots, for example 4.0.13.");
}
