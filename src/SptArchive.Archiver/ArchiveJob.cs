using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using SptArchive.Archiver.Importers;
using SptArchive.Archiver.Runs;
using SptArchive.Core;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver;

/// <summary>
/// One archive run: fingerprint the source files, skip if nothing changed, otherwise replace the
/// official tables inside a single transaction and announce the new dataset to the Api cache.
/// The process exits when the run is over, so any scheduler can drive it.
/// </summary>
public sealed class ArchiveJob(
    IDbContextFactory<ArchiveDbContext> dbFactory,
    IEnumerable<IImporter> importers,
    RunImporter runImporter,
    IDistributedCache cache,
    IOptions<ArchiveOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<ArchiveJob> logger) : BackgroundService
{
    private readonly ArchiveOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Archive run cancelled");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Archive run failed");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // One export of one mod run is a different job from an official installation (ADR-0029):
        // it states its own release, and it needs the baseline of that release.
        if (!string.IsNullOrWhiteSpace(_options.RunPath))
        {
            await ImportRunAsync(cancellationToken);
            return;
        }

        if (!Directory.Exists(_options.DatabasePath))
        {
            throw new DirectoryNotFoundException($"Archive:DatabasePath '{_options.DatabasePath}' does not exist.");
        }

        // Fail before any database work: every official row must state its release (ADR-0014),
        // and the server keeps its version in its assembly, not in the database files.
        var sptVersion = RequireSptVersion();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        if (_options.ApplyMigrations)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        var sourceHash = await SourceHasher.ComputeAsync(_options.DatabasePath, cancellationToken);

        // The same files of a different release are a different dataset, so the release is part of the check.
        if (!_options.Force && await db.ArchiveRuns.AnyAsync(
                run => run.Status == ArchiveRun.Succeeded
                    && run.SourceHash == sourceHash
                    && run.SptVersion == sptVersion,
                cancellationToken))
        {
            logger.LogInformation(
                "The source files of {SptVersion} are unchanged since the last successful run; nothing to do (use Archive:Force=true to override)",
                sptVersion);
            return;
        }

        var archiveRun = new ArchiveRun
        {
            SourceHash = sourceHash,
            SptVersion = sptVersion,
            Kind = ArchiveRunKind.Official,
            Purpose = $"Import of the official database of {sptVersion}",
        };
        db.ArchiveRuns.Add(archiveRun);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var imported = new List<string>();

        try
        {
            // One transaction for the whole run: readers keep seeing the previous dataset until it commits,
            // and a failure half way through leaves the archive exactly as it was.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // The release row commits with the data it describes, so a failed run leaves no empty release.
            await SptVersionRegistry.EnsureAsync(db, sptVersion, cancellationToken);

            var context = new ImportContext(db, _options.DatabasePath, sptVersion, _options.BatchSize);

            foreach (var importer in importers)
            {
                logger.LogInformation("Importing {Importer}...", importer.Name);
                var stopwatch = Stopwatch.StartNew();

                await importer.ImportAsync(context, cancellationToken);

                logger.LogInformation("Imported {Importer} in {Elapsed}", importer.Name, stopwatch.Elapsed);
                imported.Add(importer.Name);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkRunAsync(db, archiveRun.Id, ArchiveRun.Failed, exception.Message, RunResult.Failure(RunResult.Stage.Import, exception.Message));
            throw;
        }

        await MarkRunAsync(db, archiveRun.Id, ArchiveRun.Succeeded, error: null, RunResult.ForOfficial(imported));
        await AnnounceNewDatasetAsync(archiveRun.Id);

        logger.LogInformation("Archive run {RunId} finished", archiveRun.Id);
    }

    /// <summary>
    /// Imports the export of one mod run (TD-29). The run row is written first, so a run that fails while
    /// it reads the export still leaves a record (ADR-0033). The comparison with the baseline happens
    /// before the transaction opens, because it reads two exports and the transaction should stay short.
    /// </summary>
    private async Task ImportRunAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        if (_options.ApplyMigrations)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        // The run states what is known so far. The plan fills in the rest, or the failure says why it could not.
        var archiveRun = new ArchiveRun
        {
            SourceHash = string.Empty,
            Kind = ArchiveRunKind.Mod,
            TargetModGuid = string.IsNullOrWhiteSpace(_options.RunModGuid) ? null : _options.RunModGuid.Trim(),
            Purpose = $"Import of the run at '{_options.RunPath}'",
        };
        db.ArchiveRuns.Add(archiveRun);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var stage = RunResult.Stage.Prepare;

        try
        {
            var plan = runImporter.Prepare(archiveRun.StartedAt);

            await DescribeRunAsync(db, archiveRun.Id, plan);

            logger.LogInformation(
                "Importing the run of {ModGuid} {Version} on {Release}",
                plan.Target.ModGuid,
                plan.Target.Version,
                plan.Release);

            if (!_options.Force && await runImporter.IsCurrentAsync(db, plan, cancellationToken))
            {
                logger.LogInformation(
                    "The run of {ModGuid} {Version} on {Release} is already archived with this content; nothing to do (use Archive:Force=true to override)",
                    plan.Target.ModGuid,
                    plan.Target.Version,
                    plan.Release);

                await MarkRunAsync(db, archiveRun.Id, ArchiveRun.Succeeded, error: null, RunResult.Unchanged(plan));
                return;
            }

            stage = RunResult.Stage.Apply;
            RunImportReport report;

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                report = await runImporter.ApplyAsync(db, plan, archiveRun.Id, cancellationToken);

                stage = RunResult.Stage.Commit;
                await transaction.CommitAsync(cancellationToken);
            }

            logger.LogInformation(
                "Dataset {DatasetId} ({State}): {Templates} templates, {Locales} locale entries, {Files} files",
                report.DatasetId,
                report.IsNewDataset ? "new" : "already known",
                report.TemplateCount,
                report.LocaleCount,
                report.FileCount);

            await MarkRunAsync(db, archiveRun.Id, ArchiveRun.Succeeded, error: null, RunResult.ForRun(plan, report));
            await AnnounceNewDatasetAsync(archiveRun.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkRunAsync(db, archiveRun.Id, ArchiveRun.Failed, exception.Message, RunResult.Failure(stage, exception.Message));
            throw;
        }
    }

    /// <summary>Copies what the plan knows about the run onto its row, as soon as the plan exists.</summary>
    private async Task DescribeRunAsync(ArchiveDbContext db, Guid runId, RunImportPlan plan)
    {
        await db.ArchiveRuns
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.SourceHash, plan.ContentHash)
                .SetProperty(run => run.SptVersion, plan.Release)
                .SetProperty(run => run.TargetModGuid, plan.Target.ModGuid)
                .SetProperty(run => run.TargetVersion, plan.Target.Version)
                .SetProperty(run => run.BaselineRelease, plan.BaselineRelease)
                .SetProperty(run => run.BaselineHash, plan.BaselineHash)
                .SetProperty(run => run.Purpose,
                    plan.Purpose ?? $"Import of the run of {plan.Target.ModGuid} {plan.Target.Version} on {plan.Release}"));
    }

    /// <summary>Reads and checks <see cref="ArchiveOptions.SptVersion"/>.</summary>
    private string RequireSptVersion()
    {
        if (string.IsNullOrWhiteSpace(_options.SptVersion))
        {
            throw new InvalidOperationException(
                "Archive:SptVersion is not set. Every official row states its server release (ADR-0014), "
                + "so the run must name it, for example Archive__SptVersion=4.0.13.");
        }

        var version = _options.SptVersion.Trim();
        SptVersion.ParseParts(version); // Rejects a range or a label with a message that says what is wrong.
        return version;
    }

    // Bookkeeping must not depend on the (rolled back) import transaction, and must not hide the original error.
    private async Task MarkRunAsync(ArchiveDbContext db, Guid runId, string status, string? error, string? result)
    {
        try
        {
            await db.ArchiveRuns
                .Where(run => run.Id == runId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, status)
                    .SetProperty(run => run.Error, error)
                    .SetProperty(run => run.Result, result)
                    .SetProperty(run => run.FinishedAt, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not record the result of run {RunId}", runId);
        }
    }

    /// <summary>
    /// Changes the dataset version the Api folds into its cache keys. If the cache is down the archive
    /// is still valid, so this never fails the run; cached answers just expire on their own TTL.
    /// </summary>
    private async Task AnnounceNewDatasetAsync(Guid runId)
    {
        try
        {
            await cache.SetStringAsync(CacheKeys.DataVersion, runId.ToString("N"));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not publish the new dataset version to the cache");
        }
    }
}
