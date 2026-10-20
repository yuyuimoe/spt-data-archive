using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SptArchive.Archiver.Importers;
using SptArchive.Core.Canonical;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Runs;

/// <summary>
/// Reads one run directory into the archive (ADR-0013, ADR-0029): the contribution of the target mod
/// becomes a dataset, the release it ran on becomes an observation, and the rows are what the mod
/// added or changed against the baseline of that release.
/// </summary>
/// <remarks>
/// Reading and comparing are separate from writing, because the comparison reads two exports of
/// several hundred megabytes and the transaction should be open for the write only.
/// </remarks>
public sealed class RunImporter(IOptions<ArchiveOptions> options, ILogger<RunImporter> logger)
{
    private readonly ArchiveOptions _options = options.Value;

    /// <summary>
    /// Reads the run, its baseline and the difference between them, and decides the content hash.
    /// It touches no database, so a broken export fails before any transaction opens.
    /// </summary>
    public RunImportPlan Prepare()
    {
        if (string.IsNullOrWhiteSpace(_options.RunPath))
        {
            throw new InvalidOperationException("Archive:RunPath is not set, so there is no run to import.");
        }

        if (string.IsNullOrWhiteSpace(_options.RunModGuid))
        {
            throw new InvalidOperationException(
                "Archive:RunModGuid is not set. A run holds the target and its dependencies, and the archive " +
                "never guesses which one it is: a dependency is never the target (ADR-0021).");
        }

        if (string.IsNullOrWhiteSpace(_options.BaselinePath))
        {
            throw new InvalidOperationException(
                "Archive:BaselinePath is not set. A dataset holds what the mod contributed, which is the " +
                "difference between the run and the baseline of its release (ADR-0013).");
        }

        var run = RunDirectory.Read(_options.RunPath);

        // A run states its own release (TD-26). Official imports must be told; a run knows.
        var release = run.ServerRelease
            ?? throw new InvalidOperationException(
                $"'{run.RunPath}' states no release. Only an export of exporter 1.3.0 or later records it, and " +
                "a dataset is observed on a release (ADR-0014). Export the run again.");

        var target = run.Target(_options.RunModGuid);

        if (target.IsTool)
        {
            throw new InvalidOperationException(
                $"'{RunDirectory.ToolModGuid}' is the exporter, the tool of the run, and not a mod of the archive " +
                "(ADR-0029). Name the mod the run was made for.");
        }

        // The run does not record which mod it was made for, so the archive cannot prove the choice. When
        // another loaded mod declares the target as its dependency, the run probably archived the base
        // instead of the patch (ADR-0021), and the rows that follow are the base's.
        var dependants = run.Mods
            .Where(other => !other.IsTool
                && !string.Equals(other.ModGuid, target.ModGuid, StringComparison.OrdinalIgnoreCase)
                && other.Dependencies.Keys.Contains(target.ModGuid, StringComparer.OrdinalIgnoreCase))
            .Select(other => other.ModGuid)
            .ToArray();

        if (dependants.Length > 0)
        {
            logger.LogWarning(
                "{ModGuid} is declared as a dependency of {Dependants}. If this run was made for a patch, " +
                "the target is the patch and the base belongs in the dependency set (ADR-0021)",
                target.ModGuid,
                string.Join(", ", dependants));
        }

        var baseline = BaselineExport.Read(_options.BaselinePath, release);

        if (!baseline.ReleaseVerified)
        {
            logger.LogWarning(
                "The baseline '{Path}' states no release (exporter older than 1.3.0); using it for {Release} unverified",
                _options.BaselinePath,
                release);
        }

        var contribution = RunDiff.Compute(run.ExportPath, baseline.Run.ExportPath);

        var files = run.FilesFor(target, Guid.Empty);
        var contentHash = DatasetHash.Compute(
            contribution,
            files.Select(file => new ContributionFile(
                file.Path,
                file.Sha256,
                file.ContentText is null ? null : DatasetHash.HashText(file.ContentText))));

        var dependencySet = RunDirectory.DependencySetJson(run.Mods);

        return new RunImportPlan(
            run,
            target,
            release,
            contribution,
            contentHash,
            dependencySet,
            RunDirectory.HashDependencySet(dependencySet),
            files);
    }

    /// <summary>True when this run is already archived: same dataset, same release.</summary>
    public async Task<bool> IsCurrentAsync(ArchiveDbContext db, RunImportPlan plan, CancellationToken cancellationToken)
    {
        var datasetId = await db.ModDatasets
            .Where(dataset => dataset.ModGuid == plan.Target.ModGuid && dataset.ContentHash == plan.ContentHash)
            .Select(dataset => (Guid?)dataset.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return datasetId is not null
            && await db.ModDataObservations.AnyAsync(
                observation => observation.DatasetId == datasetId
                    && observation.SptVersion == plan.Release
                    && observation.Kind == ModObservationKind.Archive,
                cancellationToken);
    }

    /// <summary>Writes the dataset, its observation and the rows. The caller owns the transaction.</summary>
    public async Task<RunImportReport> ApplyAsync(
        ArchiveDbContext db,
        RunImportPlan plan,
        Guid archiveRunId,
        CancellationToken cancellationToken)
    {
        var target = plan.Target;

        await SptVersionRegistry.EnsureAsync(db, plan.Release, cancellationToken);

        var mod = await EnsureModAsync(db, target, cancellationToken);
        var (dataset, isNew) = await EnsureDatasetAsync(db, plan, cancellationToken);

        if (isNew)
        {
            await WriteRowsAsync(db, dataset.Id, plan, cancellationToken);
        }

        await UpsertObservationAsync(db, mod.Id, dataset.Id, plan, archiveRunId, cancellationToken);

        if (plan.Contribution.IsEmpty)
        {
            logger.LogWarning(
                "The run of {ModGuid} {Version} changed no table: the dataset {Dataset} holds no row. " +
                "A mod that only changes behaviour leaves its trace here and nothing else (ADR-0028)",
                target.ModGuid,
                target.Version,
                dataset.Id);
        }

        foreach (var path in plan.Contribution.UnstoredChanges.Keys)
        {
            logger.LogWarning(
                "{Path} changed and the archive stores no row for it yet (docs/tech-debt.md): its canonical " +
                "hash is part of the content hash, so the dataset stays distinct",
                path);
        }

        foreach (var warning in plan.Contribution.Warnings)
        {
            logger.LogWarning("Addressing: {Warning}", warning);
        }

        return new RunImportReport(
            dataset.Id,
            isNew,
            plan.ContentHash,
            plan.Contribution.Templates.Count,
            plan.Contribution.Locales.Count,
            plan.Files.Count,
            plan.Contribution.UnstoredChanges.Keys.ToArray());
    }

    private async Task<Mod> EnsureModAsync(ArchiveDbContext db, LoadedMod target, CancellationToken cancellationToken)
    {
        var mod = await db.Mods.FirstOrDefaultAsync(
            row => row.ModGuid == target.ModGuid && row.Version == target.Version,
            cancellationToken);

        if (mod is not null)
        {
            return mod;
        }

        mod = new Mod
        {
            ModGuid = target.ModGuid,
            Name = target.Name,
            Version = target.Version,
            Author = string.IsNullOrWhiteSpace(target.Author) ? null : target.Author,
            SptVersionRange = string.IsNullOrWhiteSpace(target.DeclaredSptVersion) ? null : target.DeclaredSptVersion,
        };

        db.Mods.Add(mod);
        await db.SaveChangesAsync(cancellationToken);

        return mod;
    }

    private async Task<(ModDataset Dataset, bool IsNew)> EnsureDatasetAsync(
        ArchiveDbContext db,
        RunImportPlan plan,
        CancellationToken cancellationToken)
    {
        // Two versions with the same content are one dataset (ADR-0013). A dataset is append-only, so
        // an existing one is reused and never rewritten (ADR-0018).
        var existing = await db.ModDatasets.FirstOrDefaultAsync(
            dataset => dataset.ModGuid == plan.Target.ModGuid && dataset.ContentHash == plan.ContentHash,
            cancellationToken);

        if (existing is not null)
        {
            return (existing, false);
        }

        var dataset = new ModDataset
        {
            ModGuid = plan.Target.ModGuid,
            ContentHash = plan.ContentHash,
            TableHashes = JsonSerializer.Serialize(plan.Contribution.TableHashes),
            RowCounts = JsonSerializer.Serialize(plan.Contribution.RowCounts),
            ConfigOrigin = plan.Run.Origin.ConfigOrigin,
            ConfigOriginReason = plan.Run.Origin.Reason,
        };

        db.ModDatasets.Add(dataset);
        await db.SaveChangesAsync(cancellationToken);

        return (dataset, true);
    }

    private async Task WriteRowsAsync(
        ArchiveDbContext db,
        Guid datasetId,
        RunImportPlan plan,
        CancellationToken cancellationToken)
    {
        var templates = new List<ModTemplate>();
        var changes = new List<ModChangePath>();

        foreach (var template in plan.Contribution.Templates)
        {
            templates.Add(new ModTemplate
            {
                DatasetId = datasetId,
                Kind = template.Kind,
                TemplateId = template.TemplateId,
                Op = template.Op,
                ParentId = template.ParentId,
            });

            // Every kind states the paths of its changed values (ADR-0032, decision 1).
            foreach (var change in template.Changes)
            {
                changes.Add(new ModChangePath
                {
                    DatasetId = datasetId,
                    Kind = template.Kind,
                    TemplateId = template.TemplateId,
                    Path = change.Path,
                    Op = change.Op,
                    OldValue = change.OldValue,
                    NewValue = change.NewValue,
                });
            }
        }

        if (templates.Count > 0)
        {
            await AddAllAsync(db, templates, cancellationToken);
            logger.LogInformation("Templates: {Count}", templates.Count);
        }

        if (changes.Count > 0)
        {
            await AddAllAsync(db, changes, cancellationToken);
            logger.LogInformation("Changed values: {Count} paths", changes.Count);
        }

        var rows = plan.Contribution.Templates.Where(template => template.NewJson is not null).ToArray();

        await AddAllAsync(db, rows
            .Where(row => string.Equals(row.Kind, ModTemplateKind.Item, StringComparison.Ordinal))
            .Select(row => TemplateMapper.ModItem(Read(row, TemplateMapper.ReadItem), datasetId)), cancellationToken);

        await AddAllAsync(db, rows
            .Where(row => string.Equals(row.Kind, ModTemplateKind.Quest, StringComparison.Ordinal))
            .Select(row => TemplateMapper.ModQuest(Read(row, TemplateMapper.ReadQuest), datasetId)), cancellationToken);

        await AddAllAsync(db, rows
            .Where(row => string.Equals(row.Kind, ModTemplateKind.Customization, StringComparison.Ordinal))
            .Select(row => TemplateMapper.ModCustomization(Read(row, TemplateMapper.ReadCustomization), datasetId)),
            cancellationToken);

        await AddAllAsync(db, rows
            .Where(row => string.Equals(row.Kind, ModTemplateKind.Trader, StringComparison.Ordinal))
            .Select(row => TemplateMapper.ModTrader(ReadTrader(plan, row), datasetId)), cancellationToken);

        await WriteOffersAsync(db, datasetId, rows, cancellationToken);
        await WriteLocalesAsync(db, datasetId, plan, cancellationToken);
        await WriteFilesAsync(db, datasetId, plan, cancellationToken);
        await WriteIdMapAsync(db, datasetId, plan, cancellationToken);
    }

    /// <summary>
    /// The offer trees the dataset touches, and the quest unlocks that hang from them (ADR-0032).
    /// A dataset is written once, so the tree of one offer stays consistent with itself.
    /// </summary>
    private async Task WriteOffersAsync(
        ArchiveDbContext db,
        Guid datasetId,
        IEnumerable<TemplateContribution> rows,
        CancellationToken cancellationToken)
    {
        var offers = new List<ModTraderAssortItem>();
        var quests = new List<ModTraderQuestAssort>();

        foreach (var offer in rows.Where(row => string.Equals(row.Kind, ModTemplateKind.Assort, StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(offer.NewJson!);

            // The parent of an offer is the trader it belongs to.
            var traderId = offer.ParentId ?? string.Empty;

            foreach (var node in Assort.Rows(offer.TemplateId, document.RootElement))
            {
                offers.Add(new ModTraderAssortItem
                {
                    DatasetId = datasetId,
                    TraderId = traderId,
                    InstanceId = node.NodeId,
                    TemplateId = node.TemplateId,
                    ParentId = node.ParentNodeId,
                    SlotId = node.SlotId,
                    Upd = node.Upd,
                    BarterScheme = node.Barter,
                    LoyaltyLevel = node.LoyaltyLevel,
                });
            }

            if (document.RootElement.TryGetProperty("quests", out var unlocks) && unlocks.ValueKind == JsonValueKind.Object)
            {
                foreach (var unlock in unlocks.EnumerateObject())
                {
                    quests.Add(new ModTraderQuestAssort
                    {
                        DatasetId = datasetId,
                        TraderId = traderId,
                        Status = unlock.Name,
                        AssortInstanceId = offer.TemplateId,
                        QuestId = unlock.Value.GetString() ?? string.Empty,
                    });
                }
            }
        }

        if (offers.Count > 0)
        {
            await AddAllAsync(db, offers, cancellationToken);
            logger.LogInformation("Offer rows: {Count}", offers.Count);
        }

        if (quests.Count > 0)
        {
            await AddAllAsync(db, quests, cancellationToken);
            logger.LogInformation("Quest unlocks: {Count}", quests.Count);
        }
    }

    /// <summary>
    /// The way back from an id of the run to the address the archive named (ADR-0034, decision 7). The
    /// map is written once, with the dataset: a later run of the same content shares the dataset and keeps
    /// the map of the run that made it.
    /// </summary>
    private async Task WriteIdMapAsync(
        ArchiveDbContext db,
        Guid datasetId,
        RunImportPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Contribution.IdMap.Count == 0)
        {
            return;
        }

        await AddAllAsync(db, plan.Contribution.IdMap.Select(entry => new ModIdMap
        {
            DatasetId = datasetId,
            Kind = entry.Kind,
            RawId = entry.RawId,
            Address = entry.Address,
            Pinned = entry.Pinned,
        }), cancellationToken);

        logger.LogInformation("Id map: {Count}", plan.Contribution.IdMap.Count);
    }

    private async Task WriteLocalesAsync(
        ArchiveDbContext db,
        Guid datasetId,
        RunImportPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Contribution.Locales.Count == 0)
        {
            return;
        }

        // A locale entry states its language, and the language table is filled by an official import.
        var known = await db.Languages.Select(language => language.Code).ToListAsync(cancellationToken);
        var languages = known.ToHashSet(StringComparer.Ordinal);

        var rows = new List<ModLocaleEntry>();
        var skipped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var locale in plan.Contribution.Locales)
        {
            if (!languages.Contains(locale.LanguageCode))
            {
                skipped.Add(locale.LanguageCode);
                continue;
            }

            var (entityId, field) = LocaleImporter.SplitKey(locale.Key);

            rows.Add(new ModLocaleEntry
            {
                DatasetId = datasetId,
                Scope = locale.Scope,
                LanguageCode = locale.LanguageCode,
                Key = locale.Key,
                Value = locale.Value,
                EntityId = entityId,
                Field = field,
            });
        }

        if (skipped.Count > 0)
        {
            logger.LogWarning(
                "Skipped locale entries of {Codes}: the archive holds no language row for them. Import that release first",
                string.Join(", ", skipped));
        }

        await AddAllAsync(db, rows, cancellationToken);
        logger.LogInformation("Locales: {Count}", rows.Count);
    }

    private async Task WriteFilesAsync(
        ArchiveDbContext db,
        Guid datasetId,
        RunImportPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var file in plan.Files)
        {
            file.DatasetId = datasetId;
        }

        await AddAllAsync(db, plan.Files, cancellationToken);
        logger.LogInformation("Files: {Count}", plan.Files.Count);
    }

    private async Task UpsertObservationAsync(
        ArchiveDbContext db,
        Guid modId,
        Guid datasetId,
        RunImportPlan plan,
        Guid archiveRunId,
        CancellationToken cancellationToken)
    {
        // One observation per mod version and release: a second run of the same pair replaces the pointer.
        var observation = await db.ModDataObservations.FirstOrDefaultAsync(
            row => row.ModId == modId && row.SptVersion == plan.Release && row.Kind == ModObservationKind.Archive,
            cancellationToken);

        if (observation is null)
        {
            observation = new ModDataObservation
            {
                ModId = modId,
                SptVersion = plan.Release,
                Kind = ModObservationKind.Archive,
            };

            db.ModDataObservations.Add(observation);
        }

        observation.DatasetId = datasetId;
        observation.Outcome = ModObservationOutcome.Archived;
        observation.DependencySet = plan.DependencySet;
        observation.DependencySetHash = plan.DependencySetHash;
        observation.ArchiveRunId = archiveRunId;
        observation.ObservedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The trader as the run leaves it, with the optional files its directory holds. Those files are the
    /// trader's own; the assort is not read, because no row set holds it yet (docs/tech-debt.md).
    /// </summary>
    private static TraderValues ReadTrader(RunImportPlan plan, TemplateContribution template)
    {
        using var document = JsonDocument.Parse(template.NewJson!);

        // The directory is named by the id of the run; the row is named by the address (ADR-0034).
        var directory = Path.Combine(plan.Run.ExportPath, "traders", template.RawId);

        return TemplateMapper.ReadTrader(
            template.TemplateId,
            document.RootElement,
            ReadOptional(directory, "dialogue.json"),
            ReadOptional(directory, "suits.json"),
            ReadOptional(directory, "services.json"));
    }

    private static string? ReadOptional(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static TValues Read<TValues>(TemplateContribution template, Func<string, JsonElement, TValues> read)
    {
        using var document = JsonDocument.Parse(template.NewJson!);
        return read(template.TemplateId, document.RootElement);
    }

    private async Task AddAllAsync<T>(ArchiveDbContext db, IEnumerable<T> rows, CancellationToken cancellationToken)
        where T : class
    {
        var pending = 0;

        foreach (var row in rows)
        {
            db.Add(row);

            if (++pending >= _options.BatchSize)
            {
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}

/// <summary>Everything a run import needs, read before the transaction opens.</summary>
public sealed record RunImportPlan(
    RunDirectory Run,
    LoadedMod Target,
    string Release,
    RunContribution Contribution,
    string ContentHash,
    string DependencySet,
    string DependencySetHash,
    IReadOnlyList<ModFile> Files);

/// <summary>What one run import wrote, for the log and for the caller.</summary>
public sealed record RunImportReport(
    Guid DatasetId,
    bool IsNewDataset,
    string ContentHash,
    int TemplateCount,
    int LocaleCount,
    int FileCount,
    IReadOnlyList<string> UnstoredChanges);
