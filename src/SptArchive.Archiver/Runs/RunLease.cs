using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Data;

namespace SptArchive.Archiver.Runs;

/// <summary>
/// The lease of a run (ADR-0036). A run says it is alive while it works, and a run that says nothing
/// for longer than <see cref="Silence"/> is over. This is what lets one Archiver close the row of a
/// killed process without touching the row of a process that is still importing.
/// </summary>
public static class RunLease
{
    /// <summary>How often a live run writes its heartbeat.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a run may stay silent before another Archiver closes it. Twelve missed beats, because a
    /// slow import, a garbage collection pause or a stalled disk must not look like a dead process.
    /// </summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(120);

    /// <summary>The error text that the closing Archiver writes on the row of an abandoned run.</summary>
    public const string Abandoned =
        "The process of this run stopped without recording a result. Another Archiver closed the row "
        + "after the lease ran out (ADR-0036).";
}

/// <summary>
/// Says that one run is still alive (ADR-0036). It writes on its own connection, because the import
/// holds a long transaction: a beat inside that transaction would roll back with it, and a reader of
/// another process would never see it.
/// </summary>
public sealed class RunHeartbeat : IAsyncDisposable
{
    private readonly IDbContextFactory<ArchiveDbContext> _dbFactory;
    private readonly Guid _runId;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();

    private Task? _loop;

    private RunHeartbeat(IDbContextFactory<ArchiveDbContext> dbFactory, Guid runId, ILogger logger)
    {
        _dbFactory = dbFactory;
        _runId = runId;
        _logger = logger;
    }

    /// <summary>Writes the first beat, so the row is never silent, and then beats until it is disposed.</summary>
    public static async Task<RunHeartbeat> StartAsync(
        IDbContextFactory<ArchiveDbContext> dbFactory,
        Guid runId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var heartbeat = new RunHeartbeat(dbFactory, runId, logger);

        // The first beat is not optional: a row with no heartbeat is as silent as a dead one.
        await heartbeat.BeatAsync(cancellationToken);
        heartbeat._loop = Task.Run(heartbeat.LoopAsync, CancellationToken.None);

        return heartbeat;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(RunLease.Beat);

        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await BeatAsync(_stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The run is over and the lease is released.
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // A beat that arrives after the run ended must not make a finished run look open again.
            await db.Database.ExecuteSqlAsync(
                $"update archive_runs set heartbeat_at = now() where id = {_runId} and status = 'running'",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed beat must not fail the import: the worst case is that the run looks abandoned.
            _logger.LogWarning(
                exception,
                "Could not renew the lease of run {RunId}; another Archiver may close it as interrupted",
                _runId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // The loop stopped where it was told to.
            }
        }

        _stop.Dispose();
    }
}

/// <summary>Closes the rows of runs whose process died (TD-51, ADR-0036).</summary>
public static class StaleRuns
{
    /// <summary>
    /// Marks every run that is still <c>running</c> and has been silent too long as <c>interrupted</c>.
    /// The test is the heartbeat, never the start time: several runs may import at the same time, and
    /// the start time of a live run is older than the start time of a dead one.
    /// </summary>
    /// <returns>How many rows it closed.</returns>
    public static async Task<int> CloseAsync(
        ArchiveDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = RunResult.Interrupted(RunLease.Abandoned);

        try
        {
            var closed = await db.Database.ExecuteSqlAsync(
                $"""
                update archive_runs
                   set status = 'interrupted',
                       finished_at = now(),
                       error = {RunLease.Abandoned},
                       result = {result}::jsonb
                 where status = 'running'
                   and coalesce(heartbeat_at, started_at) < now() - make_interval(secs => {RunLease.Silence.TotalSeconds})
                """,
                cancellationToken);

            if (closed > 0)
            {
                logger.LogWarning(
                    "Closed {Count} run(s) whose process stopped without recording a result (ADR-0036)",
                    closed);
            }

            return closed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The rows of dead runs are bookkeeping, and the import in hand is the work. A sweep that
            // cannot run leaves a dead row in the history; it must not stop a live import.
            logger.LogWarning(
                exception,
                "Could not close the runs whose process stopped; the history keeps their row as running (ADR-0036)");

            return 0;
        }
    }
}
