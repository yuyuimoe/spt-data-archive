using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Data;

namespace SptArchive.Archiver.Runs;

/// <summary>
/// Serializes what two Archiver processes would otherwise write twice (ADR-0036). The locks are
/// transaction scoped, so a process that dies while it waits holds nothing.
/// </summary>
/// <remarks>
/// The archive keeps two unique indexes that a check-then-insert cannot protect when two runs import at
/// the same time: <c>mods (mod_guid, version)</c>, <c>mod_datasets (mod_guid, content_hash)</c> and the
/// observation of a mod version on a release. The loser of such a race would die on a unique violation
/// with a message about a constraint, which says nothing about the import. Waiting on a lock is the
/// honest answer: the work is done, so the waiter reuses it.
/// </remarks>
public static class RunLocks
{
    /// <summary>
    /// An official import of one release. Held for the whole transaction, so no mod run and no second
    /// official import of the same release can interleave with the delete-and-rewrite of its rows.
    /// </summary>
    public static Task ReleaseExclusiveAsync(ArchiveDbContext db, string release, CancellationToken cancellationToken) =>
        AcquireAsync(db, shared: false, "release:" + release, cancellationToken);

    /// <summary>
    /// A mod run on one release. Shared, so many mod runs of a release import at the same time, and an
    /// official import of that release can never half-replace the data they describe (ADR-0035).
    /// </summary>
    public static Task ReleaseSharedAsync(ArchiveDbContext db, string release, CancellationToken cancellationToken) =>
        AcquireAsync(db, shared: true, "release:" + release, cancellationToken);

    /// <summary>
    /// One mod. Its row, its dataset and its observation are written by one run at a time; a second run
    /// of the same content waits, then finds the dataset and writes no row.
    /// </summary>
    public static Task ModExclusiveAsync(ArchiveDbContext db, string modGuid, CancellationToken cancellationToken) =>
        AcquireAsync(db, shared: false, "mod:" + modGuid.ToLowerInvariant(), cancellationToken);

    private static async Task AcquireAsync(
        ArchiveDbContext db,
        bool shared,
        string key,
        CancellationToken cancellationToken)
    {
        // The lock takes a bigint and the key is text, so Postgres hashes it. A hash collision costs a
        // wait between two unrelated imports, never a wrong write.
        var sql = shared
            ? "select pg_advisory_xact_lock_shared(hashtextextended({0}, 0))"
            : "select pg_advisory_xact_lock(hashtextextended({0}, 0))";

        await db.Database.ExecuteSqlRawAsync(sql, [key], cancellationToken);
    }
}
