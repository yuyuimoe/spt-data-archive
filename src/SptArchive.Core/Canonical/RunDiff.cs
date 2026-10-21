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

        // Every object of the run gets one name, so a run that mints a new id on every boot still
        // names the same content the same way (ADR-0034).
        var addressing = Addressing.Build(runPath, baselinePath);

        var templates = new List<TemplateContribution>();
        var locales = new List<LocaleContribution>();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var unstored = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var idMap = new List<Addressing.AddressEntry>(addressing.Entries);
        var warnings = new List<string>(addressing.Warnings);

        CollectTemplateTable(run, baseline, ItemsTable, ModTemplateKind.Item, addressing, templates, hashes);
        CollectTemplateTable(run, baseline, QuestsTable, ModTemplateKind.Quest, addressing, templates, hashes);
        CollectTemplateTable(run, baseline, CustomizationsTable, ModTemplateKind.Customization, addressing, templates, hashes);
        CollectTraders(run, baseline, addressing, templates, hashes);

        // An offer is a template of its own, named by what it is (ADR-0032, decision 4), so the
        // assort of a trader becomes rows instead of one hash of the file.
        CollectOffers(run, baseline, addressing, templates, hashes, idMap, warnings);
        CollectLocales(run, baseline, addressing, locales, hashes);

        // A location, a bot type, a hideout part, a price and a handbook entry are rows in addition to the
        // file hash below: they say what moved, and they are no input of the content hash (ADR-0041).
        var objects = DatabaseObjects.Collect(run, baseline, addressing);
        idMap.AddRange(objects.IdMap);
        warnings.AddRange(objects.Warnings);

        // A file that changed and that no row type holds. Its canonical hash enters the dataset hash,
        // so two runs that differ only here are two datasets and not one (ADR-0029).
        foreach (var path in run.Keys.Union(baseline.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (IsStored(path) || !Differs(run, baseline, path, addressing))
            {
                continue;
            }

            unstored[path] = run.TryGetValue(path, out var file) ? CanonicalHash(path, file, addressing) : string.Empty;
        }

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, count) in templates.GroupBy(row => row.Kind, StringComparer.Ordinal)
                     .Select(group => (group.Key, group.Count())))
        {
            counts[kind] = count;
        }

        counts["locales"] = locales.Count;

        foreach (var group in objects.Objects.GroupBy(row => row.Template.Kind, StringComparer.Ordinal))
        {
            counts[group.Key] = group.Count();
        }

        return new RunContribution(
            Sort(templates),
            locales
                .OrderBy(row => row.Scope, StringComparer.Ordinal)
                .ThenBy(row => row.LanguageCode, StringComparer.Ordinal)
                .ThenBy(row => row.Key, StringComparer.Ordinal)
                .ToArray(),
            unstored,
            hashes,
            counts,
            idMap,

            // Both sides of a comparison state the same limit in the same words.
            warnings.Distinct(StringComparer.Ordinal).ToArray())
        {
            Objects = objects.Objects,
        };
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

        // The trader itself is a row, and so is each offer of its assort (ADR-0032, decision 4).
        return path.StartsWith(TradersTable + "/", StringComparison.Ordinal)
            && (Path.GetFileName(path) is "base.json" or "dialogue.json" or "suits.json" or "services.json"
                or Assort.AssortFile or Assort.QuestAssortFile);
    }

    private static string CanonicalHash(string path, string file, Addressing addressing)
    {
        using var document = Parse(file);
        return CanonicalForm.Hash(path, document.RootElement, addressing.TokensFor(path));
    }

    /// <summary>True when the two copies of one file differ by content, not by a value no run holds constant.</summary>
    private static bool Differs(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        string path,
        Addressing addressing)
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

        return CanonicalForm.Compare(
            path,
            baselineDocument.RootElement,
            runDocument.RootElement,
            addressing.TokensFor(path)).Count > 0;
    }

    internal static bool SameBytes(string left, string right)
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
        Addressing addressing,
        List<TemplateContribution> templates,
        IDictionary<string, string> hashes)
    {
        var hasRun = run.TryGetValue(tablePath, out var runFile);
        var hasBaseline = baseline.TryGetValue(tablePath, out var baselineFile);

        if (!hasRun && !hasBaseline)
        {
            return;
        }

        var tokens = addressing.TokensFor(tablePath);

        // An unchanged file cannot contribute a row; its hash is still worth storing.
        if (hasRun && hasBaseline && SameBytes(runFile!, baselineFile!))
        {
            using var unchanged = Parse(runFile!);
            hashes[tablePath] = CanonicalForm.Hash(tablePath, unchanged.RootElement, tokens);
            return;
        }

        using var runDocument = hasRun ? Parse(runFile!) : EmptyObject();
        using var baselineDocument = hasBaseline ? Parse(baselineFile!) : EmptyObject();

        var runRoot = runDocument.RootElement;
        var baselineRoot = baselineDocument.RootElement;

        hashes[tablePath] = CanonicalForm.Hash(tablePath, runRoot, tokens);

        // The rows are keyed by the address of the object, so a run that names it by another id
        // still names the same object (ADR-0034, decision 1).
        var runByAddress = ByAddress(runRoot, addressing);
        var baselineByAddress = ByAddress(baselineRoot, addressing);

        var addresses = runByAddress.Keys
            .Union(baselineByAddress.Keys, StringComparer.Ordinal)
            .OrderBy(address => address, StringComparer.Ordinal);

        foreach (var address in addresses)
        {
            var inRun = runByAddress.TryGetValue(address, out var runEntry);
            var inBaseline = baselineByAddress.TryGetValue(address, out var baselineEntry);

            if (inRun && !inBaseline)
            {
                // An addition states its parent, so a reader can place it in the item tree.
                var parent = kind == ModTemplateKind.Item ? Scalar(runEntry.Value, "_parent") : null;

                templates.Add(new TemplateContribution(
                    kind,
                    address,
                    runEntry.RawId,
                    ModTemplateOperation.Added,
                    parent is null ? null : addressing.NameOf(parent),
                    runEntry.Value.GetRawText(),
                    CanonicalText(tablePath, runEntry.Value, tokens),
                    []));
                continue;
            }

            if (!inRun && inBaseline)
            {
                templates.Add(new TemplateContribution(
                    kind,
                    address,
                    baselineEntry.RawId,
                    ModTemplateOperation.Removed,
                    null,
                    null,
                    null,
                    []));
                continue;
            }

            var differences = CanonicalForm.Compare(tablePath, baselineEntry.Value, runEntry.Value, tokens);

            if (differences.Count == 0)
            {
                continue;
            }

            // An item keeps no row of its own: its paths are the archive of the change. A quest or a
            // customization keeps its object as well (ADR-0002, ADR-0032 decision 3).
            var isItem = string.Equals(kind, ModTemplateKind.Item, StringComparison.Ordinal);

            templates.Add(new TemplateContribution(
                kind,
                address,
                runEntry.RawId,
                ModTemplateOperation.Modified,
                null,
                isItem ? null : runEntry.Value.GetRawText(),
                isItem ? null : CanonicalText(tablePath, runEntry.Value, tokens),
                ReadChanges(tablePath, baselineEntry.Value, runEntry.Value, differences, tokens)));
        }
    }

    /// <summary>The objects of a table by their address, with the id the run gave each one.</summary>
    private static Dictionary<string, (string RawId, JsonElement Value)> ByAddress(JsonElement table, Addressing addressing)
    {
        var byAddress = new Dictionary<string, (string, JsonElement)>(table.GetPropertyCount(), StringComparer.Ordinal);

        foreach (var property in table.EnumerateObject())
        {
            byAddress[addressing.Address(property.Name)] = (property.Name, property.Value);
        }

        return byAddress;
    }

    private static string CanonicalText(string relativePath, JsonElement value, IdTokens tokens)
    {
        using var document = CanonicalForm.CanonicalDocument(relativePath, value, tokens);
        return document.RootElement.GetRawText();
    }

    /// <summary>
    /// A trader is a directory of files, and the diff is per directory: <c>base.json</c>, the assort,
    /// the dialogue and the suits of one trader change together or not at all.
    /// </summary>
    private static void CollectTraders(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        Addressing addressing,
        List<TemplateContribution> templates,
        IDictionary<string, string> hashes)
    {
        var directories = DirectoryNames(run, TradersTable)
            .Union(DirectoryNames(baseline, TradersTable), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var traderId in directories)
        {
            var prefix = $"{TradersTable}/{traderId}/";

            // The assort of a trader is not the trader: its offers are rows of their own (CollectOffers).
            var runFiles = WithoutAssort(FilesUnder(run, prefix));
            var baselineFiles = WithoutAssort(FilesUnder(baseline, prefix));

            if (runFiles.Count > 0)
            {
                HashTraders(runFiles, addressing, hashes);
            }

            var changed = runFiles.Keys.Union(baselineFiles.Keys, StringComparer.Ordinal)
                .Any(path => Differs(run, baseline, path, addressing));

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
            string? sourceJson = null;
            string? canonicalJson = null;
            IReadOnlyList<PathContribution> changes = [];

            if (op != ModTemplateOperation.Removed && run.TryGetValue(basePath, out var baseFile))
            {
                using var document = Parse(baseFile);
                sourceJson = document.RootElement.GetRawText();
                var tokens = addressing.TokensFor(basePath);
                canonicalJson = CanonicalText(basePath, document.RootElement, tokens);

                // The paths of a trader are the changed values of its base.json (ADR-0032, decision 3).
                // The other files of the directory keep their canonical hash.
                if (op == ModTemplateOperation.Modified && baseline.TryGetValue(basePath, out var baselineBase))
                {
                    using var baselineDocument = Parse(baselineBase);
                    var differences = CanonicalForm.Compare(basePath, baselineDocument.RootElement, document.RootElement, tokens);
                    changes = ReadChanges(basePath, baselineDocument.RootElement, document.RootElement, differences, tokens);
                }
            }

            templates.Add(new TemplateContribution(
                ModTemplateKind.Trader,
                addressing.Address(traderId),
                traderId,
                op,
                null,
                sourceJson,
                canonicalJson,
                changes));
        }
    }

    /// <summary>
    /// The offers of every trader, by address (ADR-0032, decision 4). The server renews every assort
    /// instance id on each boot (ADR-0028), so an offer is named by its trader, its root item and its
    /// payment channel: the diff pairs two runs by what the offers are, not by where they sit.
    /// </summary>
    private static void CollectOffers(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        Addressing addressing,
        List<TemplateContribution> templates,
        IDictionary<string, string> hashes,
        List<Addressing.AddressEntry> idMap,
        List<string> warnings)
    {
        var traders = DirectoryNames(run, TradersTable)
            .Union(DirectoryNames(baseline, TradersTable), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var traderId in traders)
        {
            var prefix = $"{TradersTable}/{traderId}/";
            var assortPath = prefix + Assort.AssortFile;
            var questPath = prefix + Assort.QuestAssortFile;

            var runAssort = ReadAssort(run, paths: (assortPath, questPath), traderId, addressing, warnings);
            var oldAssort = ReadAssort(baseline, paths: (assortPath, questPath), traderId, addressing, warnings);

            if (runAssort is null && oldAssort is null)
            {
                continue;
            }

            if (runAssort is not null)
            {
                if (run.ContainsKey(assortPath))
                {
                    hashes[assortPath] = Assort.Hash(runAssort.CanonicalOffers());
                }

                if (run.ContainsKey(questPath))
                {
                    hashes[questPath] = Assort.Hash(runAssort.CanonicalLinks());
                }

                // The id the run minted for an offer is a lookup, never the name of the offer.
                foreach (var offer in runAssort.Offers)
                {
                    idMap.Add(new Addressing.AddressEntry(Assort.OfferKind, offer.RawId, offer.Address, Pinned: false));
                }
            }

            var traderAddress = addressing.Address(traderId);
            var offers = runAssort?.Offers.ToDictionary(offer => offer.Address, StringComparer.Ordinal)
                ?? new Dictionary<string, Offer>(StringComparer.Ordinal);
            var before = oldAssort?.Offers.ToDictionary(offer => offer.Address, StringComparer.Ordinal)
                ?? new Dictionary<string, Offer>(StringComparer.Ordinal);

            foreach (var address in offers.Keys
                         .Union(before.Keys, StringComparer.Ordinal)
                         .OrderBy(address => address, StringComparer.Ordinal))
            {
                var inRun = offers.TryGetValue(address, out var offer);
                var inBaseline = before.TryGetValue(address, out var original);

                if (inRun && !inBaseline)
                {
                    templates.Add(new TemplateContribution(
                        Assort.OfferKind,
                        address,
                        offer!.RawId,
                        ModTemplateOperation.Added,
                        traderAddress,
                        offer.Document,
                        offer.Document,
                        []));
                    continue;
                }

                if (!inRun && inBaseline)
                {
                    templates.Add(new TemplateContribution(
                        Assort.OfferKind,
                        address,
                        original!.RawId,
                        ModTemplateOperation.Removed,
                        traderAddress,
                        null,
                        null,
                        []));
                    continue;
                }

                using var left = JsonDocument.Parse(original!.Document);
                using var right = JsonDocument.Parse(offer!.Document);

                var differences = CanonicalForm.Compare(string.Empty, left.RootElement, right.RootElement);

                if (differences.Count == 0)
                {
                    continue;
                }

                templates.Add(new TemplateContribution(
                    Assort.OfferKind,
                    address,
                    offer.RawId,
                    ModTemplateOperation.Modified,
                    traderAddress,
                    offer.Document,
                    offer.Document,
                    ReadChanges(left.RootElement, right.RootElement, differences)));
            }
        }
    }

    /// <summary>One side of an assort, or null when that side holds neither file of the trader.</summary>
    private static Assort? ReadAssort(
        SortedDictionary<string, string> files,
        (string Assort, string Quests) paths,
        string traderId,
        Addressing addressing,
        List<string> warnings)
    {
        var hasAssort = files.TryGetValue(paths.Assort, out var assortFile);
        var hasQuests = files.TryGetValue(paths.Quests, out var questFile);

        if (!hasAssort && !hasQuests)
        {
            return null;
        }

        using var assort = hasAssort ? Parse(assortFile!) : null;
        using var quests = hasQuests ? Parse(questFile!) : null;

        var read = Assort.Read(
            addressing,
            traderId,
            assort?.RootElement,
            quests?.RootElement);

        warnings.AddRange(read.Warnings);
        return read;
    }

    /// <summary>A trader directory without the two files that hold offers.</summary>
    private static SortedDictionary<string, string> WithoutAssort(SortedDictionary<string, string> files) =>
        new(files
                .Where(pair => Path.GetFileName(pair.Key) is not (Assort.AssortFile or Assort.QuestAssortFile))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

    /// <summary>A locale file is a flat map of key to text; a key the baseline does not hold with the same text is a row.</summary>
    private static void CollectLocales(
        SortedDictionary<string, string> run,
        SortedDictionary<string, string> baseline,
        Addressing addressing,
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

            var tokens = addressing.TokensFor(path);

            if (hasBaseline && SameBytes(runFile!, baselineFile!))
            {
                using var unchanged = Parse(runFile!);
                hashes[path] = CanonicalForm.Hash(path, unchanged.RootElement, tokens);
                continue;
            }

            using var runDocument = Parse(runFile!);
            using var baselineDocument = hasBaseline ? Parse(baselineFile!) : EmptyObject();

            var runRoot = runDocument.RootElement;
            var baselineRoot = baselineDocument.RootElement;

            hashes[path] = CanonicalForm.Hash(path, runRoot, tokens);

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

    private static void HashTraders(
        SortedDictionary<string, string> files,
        Addressing addressing,
        IDictionary<string, string> hashes)
    {
        foreach (var (path, file) in files)
        {
            using var document = Parse(file);
            hashes[path] = CanonicalForm.Hash(path, document.RootElement, addressing.TokensFor(path));
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
    /// Turns the paths of a canonical comparison into rows, reading both values from the two documents
    /// that were compared (ADR-0032, decision 6).
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
                hasNew ? ModChangePathOperation.Set : ModChangePathOperation.Remove,
                oldValue,
                newValue));
        }

        return changes;
    }

    /// <summary>
    /// Turns the paths of <see cref="CanonicalForm.Compare"/> into rows: the pointer in RFC 6901 form,
    /// the operation, and the value from each side in canonical form, so a value names an object by its
    /// address (ADR-0034, decision 6).
    /// </summary>
    internal static IReadOnlyList<PathContribution> ReadChanges(
        string relativePath,
        JsonElement baseline,
        JsonElement run,
        IReadOnlyList<string> differences,
        IdTokens tokens)
    {
        using var baselineCanonical = CanonicalForm.CanonicalDocument(relativePath, baseline, tokens);
        using var runCanonical = CanonicalForm.CanonicalDocument(relativePath, run, tokens);

        var changes = new List<PathContribution>(differences.Count);

        foreach (var jsonPath in differences)
        {
            var pointer = ToPointer(jsonPath);
            TryResolve(baselineCanonical.RootElement, pointer, out var oldValue);
            var hasNew = TryResolve(runCanonical.RootElement, pointer, out var newValue);

            changes.Add(new PathContribution(
                pointer,
                hasNew ? ModChangePathOperation.Set : ModChangePathOperation.Remove,
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
