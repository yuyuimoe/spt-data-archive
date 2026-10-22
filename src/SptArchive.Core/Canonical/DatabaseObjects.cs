using System.Globalization;
using System.Text.Json;
using SptArchive.Core.Entities;

namespace SptArchive.Core.Canonical;

/// <summary>One file of an added object, as the run wrote it. A location has one for each of its files.</summary>
/// <param name="Part">The file of a location, such as <c>looseLoot</c>. Empty for every other kind.</param>
public sealed record ObjectPart(string Part, string Data);

/// <summary>An object of the server database that the run adds, changes or removes (ADR-0041).</summary>
/// <param name="Template">
/// The operation, and for a modified object the changed values. Its address is the key of every row about
/// the object.
/// </param>
/// <param name="Parts">The source of an added object. A modified or a removed object has none.</param>
public sealed record ObjectContribution(TemplateContribution Template, IReadOnlyList<ObjectPart> Parts);

/// <summary>What a run contributes to the five tables that have no table of their own.</summary>
public sealed record ObjectChanges(
    IReadOnlyList<ObjectContribution> Objects,
    IReadOnlyList<Addressing.AddressEntry> IdMap,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a location, a bot type, a hideout part, a price and a handbook entry out of the files of a run
/// and of its baseline, and pairs them by address (ADR-0041).
/// </summary>
/// <remarks>
/// <para>
/// A file of these tables is hashed whole by <see cref="RunDiff"/> and stays in the content hash
/// (ADR-0029, amendment 4). The rows here are an addition: they say what moved, and they enter no hash.
/// </para>
/// <para>
/// Every value a row states is written in the form that names an id by the address of its object and
/// keeps no id that a run minted, in a key or in a value (ADR-0034, decision 5 and 6).
/// </para>
/// </remarks>
public static class DatabaseObjects
{
    private const string ContentPrefix = "#content:";
    private const string HandbookItems = "Items";

    private static readonly string[] HideoutIdFields = ["_id", "id"];
    private static readonly string[] HandbookIdFields = ["Id"];

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>True when a change to this file becomes rows of <see cref="ModTemplateKind.DatabaseObjects"/>.</summary>
    public static bool HoldsRows(string relativePath) => Classify(relativePath) is not null;

    /// <summary>
    /// Compares the files of the five tables. <paramref name="run"/> and <paramref name="baseline"/> map the
    /// path of each file inside its export to the file on disk.
    /// </summary>
    public static ObjectChanges Collect(
        IReadOnlyDictionary<string, string> run,
        IReadOnlyDictionary<string, string> baseline,
        Addressing addressing)
    {
        var objects = new List<ObjectContribution>();
        var idMap = new List<Addressing.AddressEntry>();
        var warnings = new List<string>();
        var known = addressing.KnownIds.ToArray();

        var groups = run.Keys.Concat(baseline.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Slot: Classify(path)))
            .Where(file => file.Slot is not null)
            .GroupBy(file => file.Slot!.Group, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var members = group.Select(file => (file.Path, Slot: file.Slot!)).ToArray();

            // An unchanged file cannot contribute a row, and a location is a directory of large files.
            if (members.All(member => run.ContainsKey(member.Path)
                    && baseline.ContainsKey(member.Path)
                    && RunDiff.SameBytes(run[member.Path], baseline[member.Path])))
            {
                continue;
            }

            var documents = new List<JsonDocument>();

            try
            {
                var reader = new GroupReader(members[0].Slot, known, documents, warnings);
                var runSide = reader.Read(members, run);
                var baselineSide = reader.Read(members, baseline);

                Pair(members[0].Slot.Kind, runSide, baselineSide, addressing, known, objects, idMap, warnings);
            }
            finally
            {
                foreach (var document in documents)
                {
                    document.Dispose();
                }
            }
        }

        return new ObjectChanges(
            objects
                .OrderBy(row => row.Template.Kind, StringComparer.Ordinal)
                .ThenBy(row => row.Template.TemplateId, StringComparer.Ordinal)
                .ToArray(),
            idMap,
            warnings);
    }

    /// <summary>Which table a file belongs to, the group of files that one object spans, and its part.</summary>
    private static Slot? Classify(string path)
    {
        if (!path.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = path.Split('/');

        switch (segments[0])
        {
            // A map is a directory of files. The file at the top is the one object "locations".
            case CanonicalFiles.Locations when segments.Length == 2:
                return new Slot(ModTemplateKind.Location, CanonicalFiles.Locations, Stem(segments[1]));

            case CanonicalFiles.Locations when segments.Length == 3:
                return new Slot(ModTemplateKind.Location, $"{CanonicalFiles.Locations}/{segments[1]}", Stem(segments[2]));

            case "bots" when segments.Length == 2 || (segments.Length == 3 && segments[1] == "types"):
                return new Slot(ModTemplateKind.Bot, path, string.Empty);

            case "hideout" when segments.Length == 2:
                return new Slot(ModTemplateKind.Hideout, path, string.Empty);

            case "templates" when path == "templates/prices.json":
                return new Slot(ModTemplateKind.Price, path, string.Empty);

            case "templates" when path == "templates/handbook.json":
                return new Slot(ModTemplateKind.Handbook, path, string.Empty);

            default:
                return null;
        }
    }

    private static string Stem(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    /// <summary>Pairs the objects of the two sides by address and writes one contribution for each that differs.</summary>
    private static void Pair(
        string kind,
        IReadOnlyDictionary<string, Obj> runSide,
        IReadOnlyDictionary<string, Obj> baselineSide,
        Addressing addressing,
        string[] known,
        List<ObjectContribution> objects,
        List<Addressing.AddressEntry> idMap,
        List<string> warnings)
    {
        var runByAddress = Address(runSide, baselineSide, isBaseline: false, addressing, known, idMap, warnings);
        var baselineByAddress = Address(baselineSide, baselineSide, isBaseline: true, addressing, known, idMap, warnings);

        // A category or a hideout part can be named by another object of the same file: the form that
        // writes a value names those by their address too.
        var names = addressing.AddressTokens();
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (address, obj) in runByAddress.Concat(baselineByAddress))
        {
            if (!obj.HasId || obj.Kind is not (ModTemplateKind.Hideout or ModTemplateKind.Handbook))
            {
                continue;
            }

            names.TryAdd(obj.RawKey, "#" + address);

            if (obj.Section == "Categories")
            {
                categories.TryAdd(obj.RawKey, address);
            }
        }

        var tokens = IdTokens.AddressedKeysAndValues(names);

        foreach (var address in runByAddress.Keys.Union(baselineByAddress.Keys, StringComparer.Ordinal)
                     .OrderBy(address => address, StringComparer.Ordinal))
        {
            var inRun = runByAddress.TryGetValue(address, out var after);
            var inBaseline = baselineByAddress.TryGetValue(address, out var before);

            if (inRun && !inBaseline)
            {
                objects.Add(new ObjectContribution(
                    new TemplateContribution(
                        kind,
                        address,
                        after!.RawKey,
                        ModTemplateOperation.Added,
                        ParentAddress(after, categories, addressing),
                        null,
                        null,
                        []),
                    after.Parts
                        .OrderBy(part => part.Key, StringComparer.Ordinal)
                        .Select(part => new ObjectPart(part.Key, part.Value.Value.GetRawText()))
                        .ToArray()));
                continue;
            }

            if (!inRun && inBaseline)
            {
                objects.Add(new ObjectContribution(
                    new TemplateContribution(kind, address, before!.RawKey, ModTemplateOperation.Removed, null, null, null, []),
                    []));
                continue;
            }

            var changes = ReadChanges(before!, after!, tokens, warnings);
            var parts = new List<ObjectPart>();

            // A spawn point that the run adds to a map it does not add is a row of its own, the way an
            // added item is, and not a path under the map (ADR-0043, decision 4).
            if (string.Equals(kind, ModTemplateKind.Location, StringComparison.Ordinal))
            {
                changes = PullSpawnPoints(changes, parts);
            }

            if (changes.Count == 0 && parts.Count == 0)
            {
                continue;
            }

            objects.Add(new ObjectContribution(
                new TemplateContribution(kind, address, after!.RawKey, ModTemplateOperation.Modified, null, null, null, changes),
                parts));
        }
    }

    /// <summary>
    /// Moves each added spawn point out of the changed values and into a row of its own. A changed spawn
    /// point keeps its paths under the name, and a removed one stays one path with the whole object as the
    /// old value (ADR-0043, decision 5).
    /// </summary>
    private static List<PathContribution> PullSpawnPoints(IReadOnlyList<PathContribution> changes, List<ObjectPart> parts)
    {
        var kept = new List<PathContribution>(changes.Count);

        foreach (var change in changes)
        {
            if (change is { Op: ModChangePathOperation.Set, OldValue: null, NewValue: not null }
                && IsSpawnPointPart(change.Path))
            {
                parts.Add(new ObjectPart(change.Path[1..], change.NewValue));
                continue;
            }

            kept.Add(change);
        }

        return kept;
    }

    /// <summary>True for the pointer that names one whole spawn point, and not a value inside it.</summary>
    private static bool IsSpawnPointPart(string pointer)
    {
        foreach (var list in new[] { CanonicalFiles.SpawnPoints, CanonicalFiles.SpawnPointsForced })
        {
            var prefix = "/" + CanonicalFiles.LooseLoot + "/" + list + "/";

            if (pointer.StartsWith(prefix, StringComparison.Ordinal)
                && !pointer.AsSpan(prefix.Length).Contains('/'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The changed values of one object, file by file. A path starts with the part of its file.</summary>
    private static IReadOnlyList<PathContribution> ReadChanges(Obj before, Obj after, IdTokens tokens, List<string> warnings)
    {
        var changes = new List<PathContribution>();

        foreach (var part in after.Parts.Keys.Union(before.Parts.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            // The part is the path of a file inside the object, with every segment escaped already, so
            // a spawn point with a slash in its name stays one segment (ADR-0043, decision 2).
            var prefix = part.Length == 0 ? string.Empty : "/" + part;
            var inRun = after.Parts.TryGetValue(part, out var now);
            var inBaseline = before.Parts.TryGetValue(part, out var then);

            if (inRun && inBaseline)
            {
                var differences = CanonicalForm.Compare(now.File, then.Value, now.Value, tokens, warnings);

                if (differences.Count > 0)
                {
                    changes.AddRange(RunDiff.ReadChanges(now.File, then.Value, now.Value, differences, tokens)
                        .Select(change => change with { Path = prefix + change.Path }));
                }

                continue;
            }

            // A file that only one side holds is one path: the whole file, in canonical form.
            var side = inRun ? now : then;
            using var canonical = CanonicalForm.CanonicalDocument(side.File, side.Value, tokens);
            var text = canonical.RootElement.GetRawText();

            changes.Add(inRun
                ? new PathContribution(prefix, ModChangePathOperation.Set, null, text)
                : new PathContribution(prefix, ModChangePathOperation.Remove, text, null));
        }

        return changes;
    }

    private static string? ParentAddress(Obj obj, Dictionary<string, string> categories, Addressing addressing)
    {
        if (!string.Equals(obj.Kind, ModTemplateKind.Handbook, StringComparison.Ordinal)
            || !obj.Parts.TryGetValue(string.Empty, out var part)
            || !part.Value.TryGetProperty("ParentId", out var parent)
            || parent.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var rawParent = parent.GetString()!;
        return categories.TryGetValue(rawParent, out var address) ? address : addressing.NameOf(rawParent);
    }

    /// <summary>
    /// Names every object of one side. An object keeps its id when the baseline holds it, and is named by
    /// what it is otherwise (ADR-0034). Two objects that would share a name are told apart by their order.
    /// </summary>
    private static Dictionary<string, Obj> Address(
        IReadOnlyDictionary<string, Obj> side,
        IReadOnlyDictionary<string, Obj> baseline,
        bool isBaseline,
        Addressing addressing,
        string[] known,
        List<Addressing.AddressEntry> idMap,
        List<string> warnings)
    {
        var named = new Dictionary<string, Obj>(side.Count, StringComparer.Ordinal);

        foreach (var obj in side.Values.OrderBy(obj => obj.RawKey, StringComparer.Ordinal))
        {
            var pinned = isBaseline || baseline.ContainsKey(obj.RawKey);
            var address = AddressOf(obj, pinned, addressing, known);
            var tie = 0;

            while (named.ContainsKey(address))
            {
                address = ContentUlid.FromSeed(ContentUlid.Seed(
                    obj.Kind,
                    [obj.Section, obj.RawKey, "tie", (++tie).ToString(CultureInfo.InvariantCulture)]));

                warnings.Add(
                    $"{obj.File}: '{obj.RawKey}' holds the same name and the same content as another {obj.Kind} " +
                    $"of the run, so its order names it ({tie}).");
            }

            named[address] = obj;

            // The way back from the id of the run to the address. A pinned object is its own id, a price
            // and a handbook item share the id map row of their item, and a content key holds no id.
            if (!isBaseline && !pinned && obj.HasId && NeedsIdMapRow(obj))
            {
                idMap.Add(new Addressing.AddressEntry(obj.Kind, obj.RawKey, address, Pinned: false));
            }
        }

        return named;
    }

    private static bool NeedsIdMapRow(Obj obj) => !IsNamedByItem(obj);

    /// <summary>A price and a handbook item entry are about an item, and are named as the item is.</summary>
    private static bool IsNamedByItem(Obj obj) =>
        string.Equals(obj.Kind, ModTemplateKind.Price, StringComparison.Ordinal)
        || (string.Equals(obj.Kind, ModTemplateKind.Handbook, StringComparison.Ordinal)
            && string.Equals(obj.Section, HandbookItems, StringComparison.Ordinal));

    private static string AddressOf(Obj obj, bool pinned, Addressing addressing, string[] known)
    {
        if (IsNamedByItem(obj))
        {
            return addressing.Address(obj.RawKey);
        }

        if (pinned && obj.HasId)
        {
            return obj.RawKey;
        }

        // A location and a bot type are named by their file name: the name is the identity (ADR-0034,
        // decision 3). Any other object has no name of its own, so its content names it, with no id in it.
        if (obj.Kind is ModTemplateKind.Location or ModTemplateKind.Bot)
        {
            return ContentUlid.Create(obj.Kind, obj.RawKey);
        }

        var identity = obj.HasId ? ContentPrefix + ContentHash(obj.Parts[string.Empty], known, obj.RawKey) : obj.RawKey;
        return ContentUlid.Create(obj.Kind, obj.Section, identity);
    }

    /// <summary>The hash of an object with no id in it: the own id and every other known id are tokens.</summary>
    private static string ContentHash(FilePart part, string[] known, string? ownId) =>
        CanonicalForm.Hash(part.File, part.Value, IdTokens.Seed(known, ownId));

    /// <summary>One RFC 6901 segment: the part of a location is a name, and a name may hold a slash.</summary>
    private static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private sealed record Slot(string Kind, string Group, string Part);

    /// <summary>One file of an object, and the value it holds.</summary>
    private readonly record struct FilePart(string File, JsonElement Value);

    /// <summary>
    /// One object of one side: the id the run gave it (or the content that stands in for one), and its files.
    /// </summary>
    private sealed class Obj(string kind, string section, string rawKey, bool hasId, string file)
    {
        public string Kind { get; } = kind;

        /// <summary>The list of a file that holds the object, so two lists do not share a name.</summary>
        public string Section { get; } = section;

        /// <summary>The id of the object, or <see cref="ContentPrefix"/> and a hash when it has none.</summary>
        public string RawKey { get; } = rawKey;

        public bool HasId { get; } = hasId;

        /// <summary>The first file that holds the object, for a message.</summary>
        public string File { get; } = file;

        public Dictionary<string, FilePart> Parts { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Reads the objects of one group of files, on one side.</summary>
    private sealed class GroupReader(Slot slot, string[] known, List<JsonDocument> documents, List<string> warnings)
    {
        public Dictionary<string, Obj> Read(
            IReadOnlyList<(string Path, Slot Slot)> members,
            IReadOnlyDictionary<string, string> files)
        {
            var objects = new Dictionary<string, Obj>(StringComparer.Ordinal);

            foreach (var (path, member) in members.Where(member => files.ContainsKey(member.Path)))
            {
                var root = Parse(files[path]);

                switch (slot.Kind)
                {
                    case ModTemplateKind.Location:
                        // The map is the directory, and each of its files is a part of the one object.
                        var map = member.Group == CanonicalFiles.Locations ? "locations" : member.Group[(CanonicalFiles.Locations.Length + 1)..];

                        if (!objects.TryGetValue(map, out var location))
                        {
                            objects[map] = location = new Obj(slot.Kind, CanonicalFiles.Locations, map, hasId: true, path);
                        }

                        // A loose-loot file is a directory of spawn points, and each one is a part of its
                        // own (ADR-0043, decision 1 and 3).
                        if (string.Equals(member.Part, CanonicalFiles.LooseLoot, StringComparison.Ordinal))
                        {
                            AddSpawnPoints(location, path, root);
                            break;
                        }

                        location.Parts[member.Part] = new FilePart(path, root);
                        break;

                    case ModTemplateKind.Bot:
                        var bot = path[("bots/".Length)..^".json".Length];
                        Keep(objects, new Obj(slot.Kind, "bots", bot, hasId: true, path), path, root);
                        break;

                    case ModTemplateKind.Price:
                        ReadPrices(objects, path, root);
                        break;

                    case ModTemplateKind.Handbook:
                        ReadLists(objects, path, root, HandbookIdFields, sectionPrefix: string.Empty);
                        break;

                    default:
                        ReadLists(objects, path, root, HideoutIdFields, sectionPrefix: Stem(path) + ".");
                        break;
                }
            }

            return objects;
        }

        /// <summary>
        /// Splits the loot of a map: one part for each spawn point, named by its <c>template.Id</c>, and one
        /// part for what is left of the file. The order of the two lists is not part of any name, so a run
        /// that only moves a spawn point in the list writes no row (ADR-0043, decision 6).
        /// </summary>
        private void AddSpawnPoints(Obj location, string path, JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                location.Parts[CanonicalFiles.LooseLoot] = new FilePart(path, root);
                return;
            }

            var rest = new List<JsonProperty>();

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array
                    || property.Name is not (CanonicalFiles.SpawnPoints or CanonicalFiles.SpawnPointsForced))
                {
                    rest.Add(property);
                    continue;
                }

                var taken = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var spawn in property.Value.EnumerateArray())
                {
                    var name = SpawnPointName(spawn);
                    var key = name;

                    if (taken.TryGetValue(name, out var count))
                    {
                        taken[name] = ++count;
                        key = name + "#" + count.ToString(CultureInfo.InvariantCulture);

                        warnings.Add(
                            $"{path}: the spawn point '{name}' of '{property.Name}' appears more than once, so " +
                            $"its order names the copy {count} (ADR-0043, decision 2).");
                    }
                    else
                    {
                        taken[name] = 1;
                    }

                    location.Parts[PartOfSpawn(property.Name, key)] = new FilePart(path, spawn);
                }
            }

            if (rest.Count == 0)
            {
                return;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in rest)
                {
                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            var remainder = JsonDocument.Parse(buffer.ToArray());
            documents.Add(remainder);
            location.Parts[CanonicalFiles.LooseLoot] = new FilePart(path, remainder.RootElement);
        }

        /// <summary>The name of one spawn point: the id its template carries, which the map author wrote.</summary>
        private static string SpawnPointName(JsonElement spawn)
        {
            if (spawn.ValueKind == JsonValueKind.Object)
            {
                if (spawn.TryGetProperty("template", out var template)
                    && template.ValueKind == JsonValueKind.Object
                    && template.TryGetProperty("Id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return id.GetString()!;
                }

                foreach (var field in new[] { "Id", "locationId" })
                {
                    if (spawn.TryGetProperty(field, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString()!;
                    }
                }
            }

            // A spawn point with no name of its own is named by its content, and it names no id (ADR-0034).
            return ContentPrefix + ContentHash(new FilePart(string.Empty, spawn), [], ownId: null)[..16];
        }

        /// <summary>The part of a map that holds one spawn point, with the name in one escaped segment.</summary>
        private static string PartOfSpawn(string list, string name) =>
            CanonicalFiles.LooseLoot + "/" + list + "/" + Escape(name);

        /// <summary>The price table maps the id of an item to a number: one object for each item.</summary>
        private void ReadPrices(Dictionary<string, Obj> objects, string path, JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{path}: the root is not an object, so no price row is read. Its canonical hash still enters the content hash.");
                return;
            }

            foreach (var price in root.EnumerateObject())
            {
                Keep(objects, new Obj(slot.Kind, "prices", price.Name, hasId: true, path), path, price.Value);
            }
        }

        /// <summary>
        /// A file of lists of objects, such as the recipes of the hideout or the items of the handbook. An
        /// element is named by its id. What is not a list stays together as one object named by the file.
        /// </summary>
        private void ReadLists(Dictionary<string, Obj> objects, string path, JsonElement root, string[] idFields, string sectionPrefix)
        {
            var stem = Stem(path);

            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadList(objects, path, root, idFields, stem);
                return;
            }

            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Array))
            {
                // A file with no list is one object: the settings of the hideout.
                Keep(objects, new Obj(slot.Kind, stem, stem, hasId: true, path), path, root);
                return;
            }

            var rest = new List<JsonProperty>();

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    ReadList(objects, path, property.Value, idFields, sectionPrefix + property.Name);
                }
                else
                {
                    rest.Add(property);
                }
            }

            if (rest.Count > 0)
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var property in rest)
                    {
                        property.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                var remainder = JsonDocument.Parse(buffer.ToArray());
                documents.Add(remainder);
                Keep(objects, new Obj(slot.Kind, stem, stem, hasId: true, path), path, remainder.RootElement);
            }
        }

        private void ReadList(Dictionary<string, Obj> objects, string path, JsonElement list, string[] idFields, string section)
        {
            foreach (var element in list.EnumerateArray())
            {
                var id = IdOf(element, idFields);

                if (id is null)
                {
                    // No id to name it by: its content is the name (ADR-0034, decision 4).
                    var hash = ContentHash(new FilePart(path, element), known, ownId: null);
                    warnings.Add($"{path}: an element of '{section}' has no id, so its content names it.");
                    Keep(objects, new Obj(slot.Kind, section, ContentPrefix + hash, hasId: false, path), path, element);
                    continue;
                }

                Keep(objects, new Obj(slot.Kind, section, id, hasId: true, path), path, element);
            }
        }

        /// <summary>Adds an object, and renames one whose id the file already holds so that no value is lost.</summary>
        private void Keep(Dictionary<string, Obj> objects, Obj obj, string path, JsonElement value)
        {
            var key = obj.RawKey;
            var copy = 1;

            while (objects.ContainsKey(key))
            {
                key = obj.RawKey + "#" + (++copy).ToString(CultureInfo.InvariantCulture);
                warnings.Add($"{path}: '{obj.RawKey}' appears more than once, so the copy {copy} is named by its order.");
            }

            var kept = key == obj.RawKey ? obj : new Obj(obj.Kind, obj.Section, key, obj.HasId, obj.File);
            kept.Parts[string.Empty] = new FilePart(path, value);
            objects[key] = kept;
        }

        private static string? IdOf(JsonElement element, string[] idFields)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var field in idFields)
            {
                if (element.TryGetProperty(field, out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return id.GetString();
                }
            }

            return null;
        }

        private JsonElement Parse(string file)
        {
            using var stream = File.OpenRead(file);
            var document = JsonDocument.Parse(stream, LenientJson);
            documents.Add(document);
            return document.RootElement;
        }
    }
}
