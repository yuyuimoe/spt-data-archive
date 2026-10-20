using System.Globalization;
using System.Text.Json;
using SptArchive.Core.Entities;

namespace SptArchive.Core.Canonical;

/// <summary>
/// Compares the export of a mod run with the baseline of its release and keeps what the mod
/// contributed (ADR-0013, ADR-0017). Both sides are read in the canonical form of ADR-0028, so a
/// value that no run holds constant is not a change and two runs of the same content compare equal.
/// </summary>
/// <remarks>
/// One rule runs through the result: a stored row must explain the hash. An added template carries
/// its object, a modified item carries the paths that differ with the old value from the baseline, and
/// a modified quest, customization or trader carries its object as the run left it, because no path
/// table holds those kinds. A table that changed and has no row type is reported, never dropped.
/// </remarks>
public static class RunDiff
{
    private const string ItemsTable = "templates/items.json";
    private const string QuestsTable = "templates/quests.json";
    private const string CustomizationsTable = "templates/customization.json";
    private const string TradersTable = "traders";
    private const string LocalesTable = "locales";

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static RunContribution Compute(string runPath, string baselinePath)
    {
        var run = Index(runPath);
        var baseline = Index(baselinePath);

        var templates = new List<TemplateContribution>();
        var locales = new List<LocaleContribution>();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unstored = new SortedDictionary<string, string>(StringComparer.Ordinal);

        CollectTemplateTable(run, baseline, ItemsTable, ModTemplateKind.Item, templates, hashes);
        CollectTemplateTable(run, baseline, QuestsTable, ModTemplateKind.Quest, templates, hashes);
        CollectTemplateTable(run, baseline, CustomizationsTable, ModTemplateKind.Customization, templates, hashes);
        CollectTraders(run, baseline, templates, hashes);
        CollectLocales(run, baseline, locales, hashes);

        // A file that changed and that no row type holds. Its canonical hash enters the dataset hash,
        // so two runs that differ only here are two datasets and not one (ADR-0029).
        foreach (var path in run.Keys.Union(baseline.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (IsStored(path) || !Differs(run, baseline, path))
            {
                continue;
            }

            unstored[path] = run.TryGetValue(path, out var file) ? CanonicalHash(path, file) : string.Empty;
        }

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, count) in templates.GroupBy(row => row.Kind, StringComparer.Ordinal)
                     .Select(group => (group.Key, group.Count())))
        {
            counts[kind] = count;
        }

        counts["locales"] = locales.Count;

        return new RunContribution(
            Sort(templates),
            locales
                .OrderBy(row => row.Scope, StringComparer.Ordinal)
                .ThenBy(row => row.LanguageCode, StringComparer.Ordinal)
                .ThenBy(row => row.Key, StringComparer.Ordinal)
                .ToArray(),
            unstored,
            hashes,
            counts);
    }

    /// <summary>A stable order, so the same content always writes the same rows in the same order.</summary>
    private static TemplateContribution[] Sort(List<TemplateContribution> templates) =>
        templates
            .OrderBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.TemplateId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every JSON file of an export by its path inside it. The run account itself is not a table.</summary>
    private static SortedDictionary<string, string> Index(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"'{root}' is not a directory, so there is no export to compare.");
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.StartsWith("run/", StringComparison.Ordinal))
            {
                continue;
            }

            files[relative] = file;
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{root}' holds no JSON file. An export has the layout of SPT_Data/database (ADR-0027); " +
                "check Archive:BaselinePath and the export directory.");
        }

        return files;
    }

    /// <summary>True when a change to this file becomes a row the dataset stores.</summary>
    private static bool IsStored(string path)
    {
        if (path is ItemsTable or QuestsTable or CustomizationsTable)
        {
            return true;
        }

        if (path.StartsWith(LocalesTable + "/", StringComparison.Ordinal)
            && path.EndsWith(".json", StringComparison.Ordinal)
            && path.Count(c => c == '/') == 2)
        {
            return true;
        }

        // The trader itself is a row; its assort is not, yet (docs/tech-debt.md).
        return path.StartsWith(TradersTable + "/", StringComparison.Ordinal)
            && Path.GetFileName(path) is "base.json" or "dialogue.json" or "suits.json" or "services.json";
    }

    private static string CanonicalHash(string path, string file)
    {
        using var document = Parse(file);
        return CanonicalForm.Hash(path, document.RootElement);
    }

    /// <summary>True when the two copies of one file differ by content, not by a value no run holds constant.</summary>
    private static bool Differs(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        string path)
    {
        var inRun = run.TryGetValue(path, out var runFile);
        var inBaseline = baseline.TryGetValue(path, out var baselineFile);

        if (!inRun || !inBaseline)
        {
            return true;
        }

        if (SameBytes(runFile!, baselineFile!))
        {
            return false;
        }

        using var runDocument = Parse(runFile!);
        using var baselineDocument = Parse(baselineFile!);

        return CanonicalForm.Compare(path, baselineDocument.RootElement, runDocument.RootElement).Count > 0;
    }

    private static bool SameBytes(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);

        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    /// <summary>A table of templates keyed by id: items, quests or customizations.</summary>
    private static void CollectTemplateTable(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        string tablePath,
        string kind,
        List<TemplateContribution> templates,
        IDictionary<string, string> hashes)
    {
        var hasRun = run.TryGetValue(tablePath, out var runFile);
        var hasBaseline = baseline.TryGetValue(tablePath, out var baselineFile);

        if (!hasRun && !hasBaseline)
        {
            return;
        }

        // An unchanged file cannot contribute a row; its hash is still worth storing.
        if (hasRun && hasBaseline && SameBytes(runFile!, baselineFile!))
        {
            using var unchanged = Parse(runFile!);
            hashes[tablePath] = CanonicalForm.Hash(tablePath, unchanged.RootElement);
            return;
        }

        using var runDocument = hasRun ? Parse(runFile!) : EmptyObject();
        using var baselineDocument = hasBaseline ? Parse(baselineFile!) : EmptyObject();

        var runRoot = runDocument.RootElement;
        var baselineRoot = baselineDocument.RootElement;

        hashes[tablePath] = CanonicalForm.Hash(tablePath, runRoot);

        var ids = runRoot.EnumerateObject().Select(property => property.Name)
            .Union(baselineRoot.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var inRun = runRoot.TryGetProperty(id, out var runTemplate);
            var inBaseline = baselineRoot.TryGetProperty(id, out var baselineTemplate);

            if (inRun && !inBaseline)
            {
                // An addition states its parent, so a reader can place it in the item tree.
                templates.Add(new TemplateContribution(
                    kind,
                    id,
                    ModTemplateOperation.Added,
                    kind == ModTemplateKind.Item ? Scalar(runTemplate, "_parent") : null,
                    runTemplate.GetRawText(),
                    []));
                continue;
            }

            if (!inRun && inBaseline)
            {
                templates.Add(new TemplateContribution(kind, id, ModTemplateOperation.Removed, null, null, []));
                continue;
            }

            var differences = CanonicalForm.Compare(tablePath, baselineTemplate, runTemplate);

            if (differences.Count == 0)
            {
                continue;
            }

            // An item keeps its paths, because the archive stores no row for an item the mod changed.
            // A quest or customization stores its own object instead: no path table holds those kinds.
            var isItem = string.Equals(kind, ModTemplateKind.Item, StringComparison.Ordinal);

            templates.Add(new TemplateContribution(
                kind,
                id,
                ModTemplateOperation.Modified,
                null,
                isItem ? null : runTemplate.GetRawText(),
                isItem ? ReadChanges(baselineTemplate, runTemplate, differences) : []));
        }
    }

    /// <summary>
    /// A trader is a directory of files, and the diff is per directory: <c>base.json</c>, the assort,
    /// the dialogue and the suits of one trader change together or not at all.
    /// </summary>
    private static void CollectTraders(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        List<TemplateContribution> templates,
        IDictionary<string, string> hashes)
    {
        var directories = DirectoryNames(run, TradersTable)
            .Union(DirectoryNames(baseline, TradersTable), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var traderId in directories)
        {
            var prefix = $"{TradersTable}/{traderId}/";
            var runFiles = FilesUnder(run, prefix);
            var baselineFiles = FilesUnder(baseline, prefix);

            if (runFiles.Count > 0)
            {
                HashTraders(runFiles, hashes);
            }

            var changed = runFiles.Keys.Union(baselineFiles.Keys, StringComparer.Ordinal)
                .Any(path => Differs(run, baseline, path));

            if (!changed)
            {
                continue;
            }

            var op = runFiles.Count == 0
                ? ModTemplateOperation.Removed
                : baselineFiles.Count == 0
                    ? ModTemplateOperation.Added
                    : ModTemplateOperation.Modified;

            // The trader as the run leaves it, so the row explains the change however deep it sits.
            var basePath = prefix + "base.json";
            string? newJson = null;

            if (op != ModTemplateOperation.Removed && run.TryGetValue(basePath, out var baseFile))
            {
                using var document = Parse(baseFile);
                newJson = document.RootElement.GetRawText();
            }

            templates.Add(new TemplateContribution(ModTemplateKind.Trader, traderId, op, null, newJson, []));
        }
    }

    /// <summary>A locale file is a flat map of key to text; a key the baseline does not hold with the same text is a row.</summary>
    private static void CollectLocales(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        List<LocaleContribution> locales,
        IDictionary<string, string> hashes)
    {
        var paths = run.Keys
            .Union(baseline.Keys, StringComparer.Ordinal)
            .Where(path => path.StartsWith(LocalesTable + "/", StringComparison.Ordinal)
                && path.EndsWith(".json", StringComparison.Ordinal)
                && path.Count(c => c == '/') == 2)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var segments = path.Split('/');
            var (scope, languageCode) = (segments[1], Path.GetFileNameWithoutExtension(segments[2]));

            var hasRun = run.TryGetValue(path, out var runFile);
            var hasBaseline = baseline.TryGetValue(path, out var baselineFile);

            if (!hasRun)
            {
                continue;
            }

            if (hasBaseline && SameBytes(runFile!, baselineFile!))
            {
                using var unchanged = Parse(runFile!);
                hashes[path] = CanonicalForm.Hash(path, unchanged.RootElement);
                continue;
            }

            using var runDocument = Parse(runFile!);
            using var baselineDocument = hasBaseline ? Parse(baselineFile!) : EmptyObject();

            var runRoot = runDocument.RootElement;
            var baselineRoot = baselineDocument.RootElement;

            hashes[path] = CanonicalForm.Hash(path, runRoot);

            foreach (var property in runRoot.EnumerateObject())
            {
                // The "menu" files are nested objects, which need their own mapping. Text only, here.
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString() ?? string.Empty;
                var inBaseline = baselineRoot.TryGetProperty(property.Name, out var baselineValue)
                    && baselineValue.ValueKind == JsonValueKind.String;

                if (inBaseline && string.Equals(baselineValue.GetString(), value, StringComparison.Ordinal))
                {
                    continue;
                }

                locales.Add(new LocaleContribution(
                    scope,
                    languageCode,
                    property.Name,
                    value,
                    inBaseline ? baselineValue.GetString() : null));
            }
        }
    }

    private static void HashTraders(SortedDictionary<string, string> files, IDictionary<string, string> hashes)
    {
        foreach (var (path, file) in files)
        {
            using var document = Parse(file);
            hashes[path] = CanonicalForm.Hash(path, document.RootElement);
        }
    }

    private static IEnumerable<string> DirectoryNames(SortedDictionary<string, string> files, string table) =>
        files.Keys
            .Where(path => path.StartsWith(table + "/", StringComparison.Ordinal))
            .Select(path => path.Split('/'))
            .Where(segments => segments.Length >= 3)
            .Select(segments => segments[1])
            .Distinct(StringComparer.Ordinal);

    private static SortedDictionary<string, string> FilesUnder(SortedDictionary<string, string> files, string prefix) =>
        new(files.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

    /// <summary>
    /// Turns the paths of <see cref="CanonicalForm.Compare"/> into rows: the pointer in RFC 6901 form,
    /// the operation, and the value from each side.
    /// </summary>
    private static IReadOnlyList<PathContribution> ReadChanges(
        JsonElement baseline,
        JsonElement run,
        IReadOnlyList<string> differences)
    {
        var changes = new List<PathContribution>(differences.Count);

        foreach (var jsonPath in differences)
        {
            var pointer = ToPointer(jsonPath);
            TryResolve(baseline, pointer, out var oldValue);
            var hasNew = TryResolve(run, pointer, out var newValue);

            changes.Add(new PathContribution(
                pointer,
                hasNew ? ModItemChangeOperation.Set : ModItemChangeOperation.Remove,
                oldValue,
                newValue));
        }

        return changes;
    }

    /// <summary>
    /// "$._props.Weight" becomes "/_props/Weight" and "$.StackSlots[0]._max_count" becomes
    /// "/StackSlots/0/_max_count" (ADR-0017 stores a JSON pointer).
    /// </summary>
    internal static string ToPointer(string jsonPath)
    {
        var body = jsonPath.StartsWith('$') ? jsonPath[1..] : jsonPath;
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var character in body)
        {
            switch (character)
            {
                case '.':
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }

                    break;

                case '[':
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }

                    break;

                case ']':
                    break;

                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return string.Concat(segments.Select(segment =>
            "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
    }

    /// <summary>Reads the value at a pointer, and says whether the side holds it at all.</summary>
    private static bool TryResolve(JsonElement root, string pointer, out string? value)
    {
        value = null;
        var current = root;

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(name, out var property))
            {
                current = property;
                continue;
            }

            return false;
        }

        value = current.GetRawText();
        return true;
    }

    private static string? Scalar(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonDocument Parse(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonDocument.Parse(stream, LenientJson);
    }

    private static JsonDocument EmptyObject() => JsonDocument.Parse("{}");
}
