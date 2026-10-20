using HotChocolate;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// Chooses the one server release an official query reads (ADR-0014, ADR-0019). The Api never
/// resolves a range and never answers an unarchived release with an empty list.
/// </summary>
public static class Releases
{
    /// <summary>The value is not a server version at all, for example <c>^4.0.13</c> or <c>latest</c>.</summary>
    public const string MalformedCode = "SPT_VERSION_MALFORMED";

    /// <summary>The value is a server version that no run has archived.</summary>
    public const string UnknownCode = "SPT_VERSION_UNKNOWN";

    /// <summary>The archive holds no release, so there is no latest one to answer from.</summary>
    public const string EmptyCode = "SPT_ARCHIVE_EMPTY";

    /// <summary>The releases of the archive, newest first. Sorts by the numbers and not by the text.</summary>
    public static async Task<List<SptVersion>> ListAsync(ArchiveDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.SptVersions.AsNoTracking().ToListAsync(cancellationToken);

        return [.. rows.OrderByDescending(row => row.VersionParts, PartsComparer.Instance)];
    }

    /// <summary>
    /// Returns the release key that a query answers from: the requested one, or the latest when no
    /// value is given. Throws a GraphQL error with its own code for each kind of bad value.
    /// </summary>
    public static async Task<string> ResolveAsync(ArchiveDbContext db, string? requested, CancellationToken cancellationToken)
    {
        var releases = await ListAsync(db, cancellationToken);
        var latest = releases.FirstOrDefault()?.Version;

        if (requested is null)
        {
            return latest ?? throw Fail(EmptyCode, "The archive holds no release yet, so there is no latest release to answer from.");
        }

        // The key is compared as stored. A key that the archive holds is always valid.
        if (releases.Any(row => row.Version == requested))
        {
            return requested;
        }

        if (!IsReleaseKey(requested))
        {
            throw Fail(
                MalformedCode,
                $"'{requested}' is not a server version. The sptVersion argument takes one exact archived release, for example '{latest ?? "4.0.13"}'. Ranges and labels such as 'latest' are not accepted.");
        }

        throw Fail(
            UnknownCode,
            latest is null
                ? $"The server version '{requested}' is not archived. The archive holds no release yet."
                : $"The server version '{requested}' is not archived. The latest archived release is '{latest}'.");
    }

    // Three or four numbers separated by dots, the form of a release key ("4.0.13"). "4.0" is a prefix, not a release.
    private static bool IsReleaseKey(string value)
    {
        try
        {
            return SptVersion.ParseParts(value) is not null
                && value.Trim() == value
                && value.Split('.').Length is >= 3 and <= SptVersion.PartCount;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static GraphQLException Fail(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());

    private sealed class PartsComparer : IComparer<int[]>
    {
        public static readonly PartsComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            for (var index = 0; index < SptVersion.PartCount; index++)
            {
                var order = x![index].CompareTo(y![index]);
                if (order != 0)
                {
                    return order;
                }
            }

            return 0;
        }
    }
}
