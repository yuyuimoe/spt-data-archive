using Microsoft.EntityFrameworkCore;
using Npgsql;
using SptArchive.Core.Entities;

namespace SptArchive.Core.Data;

/// <summary>
/// Writes the row that makes a server release real (ADR-0014). An official row cannot exist
/// without it, so the Archiver registers the release inside the import transaction: a failed run
/// rolls the row back and never makes an empty release look like the latest one.
/// </summary>
public static class SptVersionRegistry
{
    public static async Task EnsureAsync(ArchiveDbContext db, string version, CancellationToken cancellationToken)
    {
        var parts = SptVersion.ParseParts(version);

        if (await db.SptVersions.AnyAsync(row => row.Version == version, cancellationToken))
        {
            return;
        }

        // Two release keys with the same numbers are one release. Say so instead of failing on the index.
        var sameNumbers = await db.SptVersions
            .FirstOrDefaultAsync(row => row.VersionParts == parts, cancellationToken);

        if (sameNumbers is not null)
        {
            throw new InvalidOperationException(
                $"'{version}' states the same release as the archived '{sameNumbers.Version}'. Use one release key for one release.");
        }

        db.SptVersions.Add(new SptVersion { Version = version, VersionParts = parts });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two imports of one new release at the same time (ADR-0036): the other one registered it
            // between the check above and this insert, and its row is the release.
            db.ChangeTracker.Clear();

            if (await db.SptVersions.AnyAsync(row => row.Version == version, cancellationToken))
            {
                return;
            }

            throw;
        }
    }
}
