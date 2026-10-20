using System.Text.Json;

namespace SptArchive.Archiver.Runs;

/// <summary>
/// The <c>result</c> column of <c>archive_runs</c> (ADR-0033, decision 2): the counts that say what a run
/// produced, or the error and the stage of a run that failed. Rows hold the same counts. The copy exists
/// for the run that has no dataset.
/// </summary>
public static class RunResult
{
    /// <summary>The stages a mod run passes, in order. A failed run names the one it stopped in.</summary>
    public static class Stage
    {
        public const string Prepare = "prepare";
        public const string Apply = "apply";
        public const string Commit = "commit";
        public const string Import = "import";
    }

    /// <summary>The result of a mod run that wrote, or reused, a dataset.</summary>
    public static string ForRun(RunImportPlan plan, RunImportReport report)
    {
        var templates = plan.Contribution.Templates
            .GroupBy(template => $"{template.Kind}.{template.Op}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var warnings = plan.Contribution.Warnings.Concat(plan.MintWarnings).ToArray();

        return JsonSerializer.Serialize(new
        {
            outcome = report.IsNewDataset ? "dataset_created" : "dataset_reused",
            datasetId = report.DatasetId,
            contentHash = report.ContentHash,
            targetVerified = plan.TargetVerified,
            templates,
            pathRows = plan.Contribution.Templates.Sum(template => template.Changes.Count),
            localeRows = plan.Contribution.Locales.Count,
            files = plan.Files.Count,
            unstoredFiles = plan.Contribution.UnstoredChanges.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            warnings,
        });
    }

    /// <summary>The result of a run that found its content already archived and wrote nothing.</summary>
    public static string Unchanged(RunImportPlan plan) =>
        JsonSerializer.Serialize(new { outcome = "unchanged", contentHash = plan.ContentHash });

    /// <summary>
    /// The result of an official import that found the work done by a run that started at the same time
    /// (ADR-0036). The files are the same, so the archive needs one row set, not two.
    /// </summary>
    public static string UnchangedOfficial() => JsonSerializer.Serialize(new { outcome = "unchanged" });

    /// <summary>
    /// The result of a run whose process stopped without recording one (ADR-0036). The stage is unknown,
    /// because the process never got to say where it was.
    /// </summary>
    public static string Interrupted(string error) =>
        JsonSerializer.Serialize(new { outcome = "interrupted", error });

    /// <summary>The result of an official import.</summary>
    public static string ForOfficial(IEnumerable<string> importers) =>
        JsonSerializer.Serialize(new { outcome = "imported", importers = importers.ToArray() });

    /// <summary>The result of a run that failed: the error and the stage it stopped in.</summary>
    public static string Failure(string stage, string error) =>
        JsonSerializer.Serialize(new { outcome = "failed", stage, error });
}
