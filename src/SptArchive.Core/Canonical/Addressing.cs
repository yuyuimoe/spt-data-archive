using System.Collections.Immutable;
using System.Text.Json;

namespace SptArchive.Core.Canonical;

/// <summary>
/// The names of the objects a run holds (ADR-0034). An object that the baseline of the release holds
/// is <em>pinned</em>: its name is the id it already has. An object that the run made is
/// <em>derived</em>: its name is a <see cref="ContentUlid"/> of the fields that identify it. A run that
/// gives an object a new id on every boot therefore still names it the same way in every run.
/// </summary>
public sealed class Addressing
{
    /// <summary>An item template.</summary>
    public const string ItemKind = "item";

    /// <summary>A quest.</summary>
    public const string QuestKind = "quest";

    /// <summary>A customization item.</summary>
    public const string CustomizationKind = "customization";

    /// <summary>A trader.</summary>
    public const string TraderKind = "trader";

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, Named> _byRawId;

    private Addressing(
        Dictionary<string, Named> byRawId,
        ImmutableArray<AddressEntry> entries,
        ImmutableArray<string> warnings)
    {
        _byRawId = byRawId;
        Entries = entries;
        Warnings = warnings;
    }

    /// <summary>One row per known object: the id the run gave it, and the name the archive derived.</summary>
    public ImmutableArray<AddressEntry> Entries { get; }

    /// <summary>What the addressing could not do as the rule says, such as an object with no name.</summary>
    public ImmutableArray<string> Warnings { get; }

    /// <summary>The name of an object by the id the run gave it. An unknown id keeps its own text.</summary>
    public string Address(string rawId) => _byRawId.TryGetValue(rawId, out var named) ? named.Address : rawId;

    /// <summary>
    /// The name of the object that a value points at, or the value itself when it points at no object
    /// of this run. A stored value names an object by its address (ADR-0034, decision 6).
    /// </summary>
    public string NameOf(string value) => _byRawId.TryGetValue(value, out var named) ? named.Address : value;

    /// <summary>True when the baseline of the release holds this id, so the object keeps it as its name.</summary>
    public bool IsPinned(string rawId) => _byRawId.TryGetValue(rawId, out var named) && named.Pinned;

    /// <summary>
    /// The tokens for one file. Only the ids that the file can name are in them: a bot type is called
    /// <c>assault</c>, and that word is a value in many tables. A token map of every id would rewrite it.
    /// </summary>
    public IdTokens TokensFor(string relativePath)
    {
        var kinds = KindsNamedBy(relativePath);

        if (kinds.Length == 0)
        {
            return IdTokens.Blank([]);
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (rawId, named) in _byRawId)
        {
            if (kinds.Contains(named.Kind, StringComparer.Ordinal))
            {
                tokens[rawId] = "#" + named.Address;
            }
        }

        return IdTokens.Addressed(tokens);
    }

    /// <summary>Every id the run or the baseline knows, whatever the kind of its object.</summary>
    public IEnumerable<string> KnownIds => _byRawId.Keys;

    /// <summary>
    /// The token of every known id, in the form <c>#address</c>, for the tables that have no row type of
    /// their own: a bot type names an item by its id, so does a location, a price and a hideout recipe
    /// (ADR-0041). The map is new on every call, because a caller adds its own ids to it.
    /// </summary>
    public Dictionary<string, string> AddressTokens()
    {
        var tokens = new Dictionary<string, string>(_byRawId.Count, StringComparer.Ordinal);

        foreach (var (rawId, named) in _byRawId)
        {
            tokens[rawId] = "#" + named.Address;
        }

        return tokens;
    }

    /// <summary>Names every object of a run, against the baseline of its release.</summary>
    public static Addressing Build(string runRoot, string baselineRoot)
    {
        var documents = new List<JsonDocument>();

        try
        {
            var run = Read(runRoot, documents);
            var baseline = Read(baselineRoot, documents);

            return Name(run, baseline);
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }
        }
    }

    private static Addressing Name(Export run, Export baseline)
    {
        var known = new HashSet<string>(run.Ids(), StringComparer.Ordinal);
        known.UnionWith(baseline.Ids());

        // A seed holds the name of no other object, so the tie-break hash holds none either: a name
        // is derived from content, and content that held names would refer to itself (decision 2 and 4).
        var blank = IdTokens.Blank(known);

        var byRawId = new Dictionary<string, Named>(known.Count, StringComparer.Ordinal);
        var pinned = new List<AddressEntry>();

        // Pinned: the baseline of the release holds the id, so the object keeps it (decision 1).
        foreach (var rawId in known.Where(id => baseline.Find(id) is not null).OrderBy(id => id, StringComparer.Ordinal))
        {
            var kind = baseline.Find(rawId)!.Kind;
            byRawId[rawId] = new Named(rawId, kind, Pinned: true);
            pinned.Add(new AddressEntry(kind, rawId, rawId, Pinned: true));
        }

        var warnings = ImmutableArray.CreateBuilder<string>();
        var derived = run.Objects().Where(item => !byRawId.ContainsKey(item.RawId)).ToList();
        var entries = ImmutableArray.CreateBuilder<AddressEntry>();

        foreach (var byIdentity in derived.GroupBy(item => (item.Kind, Identity: IdentityOf(item, blank, warnings))))
        {
            var (kind, identity) = byIdentity.Key;

            // One name, one object: the identity alone names it.
            if (byIdentity.Count() == 1)
            {
                var only = byIdentity.Single();
                Name(only, ContentUlid.Seed(kind, [identity]), byRawId, entries);
                continue;
            }

            // One name, several objects: content separates them (decision 4). The measurement is
            // WTT-Armory: 7 name groups of 2, each pair differing in the prefab path.
            var byContent = byIdentity.GroupBy(
                item => ContentUlid.Seed(kind, [identity]) + "|" + CanonicalForm.Hash(item.RelativePath, item.Value, blank),
                StringComparer.Ordinal);

            foreach (var sameContent in byContent)
            {
                if (sameContent.Count() == 1)
                {
                    Name(sameContent.Single(), sameContent.Key, byRawId, entries);
                    continue;
                }

                // Content that is equal too: order alone is left. It is stable for one run and not
                // across a change of the set, which the rule accepts (decision 4).
                var ordinal = 0;

                foreach (var item in sameContent.OrderBy(item => item.RawId, StringComparer.Ordinal))
                {
                    warnings.Add(
                        $"{item.RelativePath}: '{item.RawId}' holds the same name and the same content as " +
                        $"another object of the run, so its order names it ({ordinal}).");

                    Name(item, sameContent.Key + "|" + ContentUlid.Ordinal(ordinal++), byRawId, entries);
                }
            }
        }

        var all = pinned.Concat(entries)
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.RawId, StringComparer.Ordinal)
            .ToImmutableArray();

        return new Addressing(byRawId, all, warnings.ToImmutable());
    }

    private static void Name(
        ExportedObject item,
        string seed,
        Dictionary<string, Named> byRawId,
        ImmutableArray<AddressEntry>.Builder entries)
    {
        var address = ContentUlid.FromSeed(seed);

        byRawId[item.RawId] = new Named(address, item.Kind, Pinned: false);
        entries.Add(new AddressEntry(item.Kind, item.RawId, address, Pinned: false));
    }

    /// <summary>
    /// The fields that identify an object, in the order of ADR-0034 decision 3: a name that the author
    /// writes. A trader is named by its directory, because a trader holds no name of its own.
    /// </summary>
    private static string IdentityOf(ExportedObject item, IdTokens blank, ImmutableArray<string>.Builder warnings)
    {
        var identity = item.Kind switch
        {
            ItemKind or CustomizationKind => Scalar(item.Value, "_name"),
            QuestKind => Scalar(item.Value, "QuestName"),
            _ => item.RawId,
        };

        if (identity is not null)
        {
            return identity;
        }

        // An object with no name has no identity to derive from. Its content names it, which is the
        // case that decision 5 says to revisit; the run states the fact and goes on.
        var content = CanonicalForm.Hash(item.RelativePath, item.Value, blank);

        warnings.Add(
            $"{item.RelativePath}: '{item.RawId}' is a {item.Kind} with no name, so its content names it. " +
            "The rule of ADR-0034 decision 3 expects a name.");

        return "#content:" + content;
    }

    private static string? Scalar(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>The kinds of object that a file can name. An empty list means no id can appear in it.</summary>
    private static string[] KindsNamedBy(string relativePath)
    {
        if (CanonicalFiles.IsTemplateTable(relativePath))
        {
            return relativePath switch
            {
                CanonicalFiles.Items => [ItemKind],
                CanonicalFiles.Quests => [ItemKind, QuestKind],
                _ => [ItemKind, CustomizationKind],
            };
        }

        // A trader points at the items it sells; its own id is in its base file and in its directory name.
        return relativePath.StartsWith(CanonicalFiles.Traders + "/", StringComparison.Ordinal)
            ? [ItemKind, TraderKind]
            : [];
    }

    private static Export Read(string root, List<JsonDocument> documents)
    {
        var export = new Export(root, documents);

        export.ReadTable(CanonicalFiles.Items, ItemKind);
        export.ReadTable(CanonicalFiles.Quests, QuestKind);
        export.ReadTable(CanonicalFiles.Customizations, CustomizationKind);
        export.ReadTraders();

        return export;
    }

    private sealed record Named(string Address, string Kind, bool Pinned);

    /// <summary>One row of the map: the raw id of the run, the name the archive derived, and its origin.</summary>
    public sealed record AddressEntry(string Kind, string RawId, string Address, bool Pinned);

    private sealed record ExportedObject(string Kind, string RawId, string RelativePath, JsonElement Value);

    /// <summary>One side of the comparison: the objects it holds, by id, and the file each came from.</summary>
    private sealed class Export(string root, List<JsonDocument> documents)
    {
        private readonly SortedDictionary<string, ExportedObject> _objects = new(StringComparer.Ordinal);

        public IEnumerable<ExportedObject> Objects() => _objects.Values;

        public IEnumerable<string> Ids() => _objects.Keys;

        public ExportedObject? Find(string rawId) => _objects.GetValueOrDefault(rawId);

        /// <summary>A table keyed by id: every property is an object, and holds its own id again.</summary>
        public void ReadTable(string relativePath, string kind)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                return;
            }

            using var stream = File.OpenRead(path);
            var document = JsonDocument.Parse(stream, LenientJson);
            documents.Add(document);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                Keep(new ExportedObject(kind, property.Name, relativePath, property.Value));
            }
        }

        /// <summary>A trader is a directory named by its id, and its object is <c>base.json</c>.</summary>
        public void ReadTraders()
        {
            var directory = Path.Combine(root, CanonicalFiles.Traders.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var trader in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
            {
                var basePath = Path.Combine(trader, "base.json");

                if (!File.Exists(basePath))
                {
                    continue;
                }

                using var stream = File.OpenRead(basePath);
                var document = JsonDocument.Parse(stream, LenientJson);
                documents.Add(document);

                var id = Path.GetFileName(trader);
                Keep(new ExportedObject(TraderKind, id, $"{CanonicalFiles.Traders}/{id}/base.json", document.RootElement));
            }
        }

        private void Keep(ExportedObject item) => _objects[item.RawId] = item;
    }
}
