using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SptArchive.Core.Canonical;

/// <summary>
/// The canonical form of a run. It is the only input to a comparison and to the hash of a
/// dataset (ADR-0028): object keys are sorted, array order is kept, and a short measured list of
/// paths whose value a run does not hold constant becomes <see cref="CanonicalForm.VolatileToken"/>.
/// The generated ids of an assort are renumbered by the position of their item, so the joins that
/// use them keep working (decision 3).
///
/// The archive stores the value that the server wrote (ADR-0002). This form is never served and
/// never stored in a <c>jsonb</c> column; it exists for a difference and a hash only.
/// </summary>
public static class CanonicalForm
{
    /// <summary>What a volatile value becomes. The server writes no such value, so it cannot be mistaken for data.</summary>
    public const string VolatileToken = "#volatile";

    private static readonly Lazy<ImmutableArray<VolatilePath>> Registered = new(VolatilePath.Load);

    /// <summary>The measured list of volatile paths, as shipped in <c>volatile-paths.json</c>.</summary>
    public static ImmutableArray<VolatilePath> VolatilePaths => Registered.Value;

    /// <summary>Writes the canonical bytes of one value, with the rules that apply to this file.</summary>
    /// <param name="relativePath">The path of the file inside the export, which selects the rules.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="ids">
    /// How to write a raw id (ADR-0034). Without it, the generated ids of an assort are renumbered by
    /// position and every other id stays as the run wrote it.
    /// </param>
    public static byte[] Canonicalize(string relativePath, JsonElement value, IdTokens? ids = null) =>
        WriteCanonical(relativePath, value, ids, rootIsTemplate: false);

    /// <summary>The SHA-256 of the canonical bytes, lowercase hex. This is a dataset hash (ADR-0013), not a file hash.</summary>
    public static string Hash(string relativePath, JsonElement value, IdTokens? ids = null) =>
        Convert.ToHexString(SHA256.HashData(Canonicalize(relativePath, value, ids))).ToLowerInvariant();

    /// <summary>
    /// The canonical form of a value that has no file rules: its keys sorted, its array order kept.
    /// The offer documents of an assort are built and compared this way (ADR-0032, decision 4).
    /// </summary>
    public static string CanonicalText(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(64, value.GetRawText().Length));
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true });

        Write(writer, value, new PathCursor(), Rules.None);
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The canonical form of one object of a file, as a document to read a value from (ADR-0034,
    /// decision 6). A path that <see cref="Compare"/> reports is a path of this form, so the value the
    /// archive stores for a change is the value at that path here.
    /// </summary>
    public static JsonDocument CanonicalDocument(string relativePath, JsonElement value, IdTokens? ids = null) =>
        JsonDocument.Parse(WriteCanonical(relativePath, value, ids, rootIsTemplate: true));

    /// <summary>
    /// Lists the paths that differ between two values of the same file after canonicalization.
    /// A path on the volatile list never differs, because both sides hold the token. Whatever this
    /// returns is content: a mod change, or a field whose volatility no entry covers yet.
    /// </summary>
    public static IReadOnlyList<string> Compare(
        string relativePath,
        JsonElement left,
        JsonElement right,
        IdTokens? ids = null,
        ICollection<string>? warnings = null)
    {
        var differences = new List<string>();

        // Each side is read with its own rules: the generated ids differ between the two runs, and
        // an id only becomes the token of its position when it is looked up in its own document.
        Compare(
            left,
            right,
            new PathCursor(),
            Rules.For(relativePath, left, ids, rootIsTemplate: true),
            Rules.For(relativePath, right, ids, rootIsTemplate: true),
            differences,
            warnings,
            relativePath);
        return differences;
    }

    /// <summary>
    /// The bytes of a value, with the rules of its file. <paramref name="rootIsTemplate"/> says that the
    /// value is one object of a table, so no table key sits above it (ADR-0034, decision 5).
    /// </summary>
    private static byte[] WriteCanonical(string relativePath, JsonElement value, IdTokens? ids, bool rootIsTemplate)
    {
        var rules = Rules.For(relativePath, value, ids, rootIsTemplate);
        var buffer = new ArrayBufferWriter<byte>(Math.Max(64, value.GetRawText().Length));
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true });

        Write(writer, value, new PathCursor(), rules);
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, PathCursor path, Rules rules)
    {
        if (rules.IsVolatile(path))
        {
            writer.WriteStringValue(VolatileToken);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in Sorted(element))
                {
                    path.Push(property.Name);
                    writer.WritePropertyName(rules.MapName(path, property.Name));
                    Write(writer, property.Value, path, rules);
                    path.Pop();
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    path.PushIndex(index++);
                    Write(writer, item, path, rules);
                    path.Pop();
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String when rules.MapValue(path, element.GetString()!) is { } mapped:
                writer.WriteStringValue(mapped);
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void Compare(
        JsonElement left,
        JsonElement right,
        PathCursor path,
        Rules leftRules,
        Rules rightRules,
        List<string> differences,
        ICollection<string>? warnings,
        string file)
    {
        if (leftRules.IsVolatile(path) || rightRules.IsVolatile(path))
        {
            return;
        }

        if (left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = Properties(left, path, leftRules);
            var rightProperties = Properties(right, path, rightRules);

            // A property that only one side has is a difference at its own path, and nothing below it is compared.
            foreach (var name in leftProperties.Keys
                         .Union(rightProperties.Keys, StringComparer.Ordinal)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                path.Push(name);

                if (leftProperties.TryGetValue(name, out var leftValue)
                    && rightProperties.TryGetValue(name, out var rightValue))
                {
                    Compare(leftValue, rightValue, path, leftRules, rightRules, differences, warnings, file);
                }
                else
                {
                    differences.Add(path.ToJsonPath());
                }

                path.Pop();
            }

            return;
        }

        if (left.ValueKind == JsonValueKind.Array && right.ValueKind == JsonValueKind.Array)
        {
            CompareArrays(left, right, path, leftRules, rightRules, differences, warnings, file);
            return;
        }

        if (left.ValueKind != right.ValueKind
            || CanonicalText(left, path, leftRules) != CanonicalText(right, path, rightRules))
        {
            differences.Add(path.ToJsonPath());
        }
    }

    /// <summary>
    /// Compares two arrays element by element. An element is named by its identity when every element of
    /// both sides has one and none repeats, so the same element at another index is the same address
    /// (TD-72). Otherwise the array falls back to the index, and says so in <paramref name="warnings"/>.
    /// </summary>
    private static void CompareArrays(
        JsonElement left,
        JsonElement right,
        PathCursor path,
        Rules leftRules,
        Rules rightRules,
        List<string> differences,
        ICollection<string>? warnings,
        string file)
    {
        var leftKeys = ArrayKeys(left, path, leftRules, out var leftReason);
        var rightKeys = ArrayKeys(right, path, rightRules, out var rightReason);

        if (leftKeys is not null && rightKeys is not null)
        {
            var leftByKey = new Dictionary<string, JsonElement>(leftKeys.Count, StringComparer.Ordinal);
            var rightByKey = new Dictionary<string, JsonElement>(rightKeys.Count, StringComparer.Ordinal);

            for (var index = 0; index < leftKeys.Count; index++)
            {
                leftByKey[leftKeys[index]] = left[index];
            }

            for (var index = 0; index < rightKeys.Count; index++)
            {
                rightByKey[rightKeys[index]] = right[index];
            }

            foreach (var key in leftByKey.Keys.Union(rightByKey.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                path.PushKey(key);

                if (leftByKey.TryGetValue(key, out var leftElement) && rightByKey.TryGetValue(key, out var rightElement))
                {
                    Compare(leftElement, rightElement, path, leftRules, rightRules, differences, warnings, file);
                }
                else
                {
                    differences.Add(path.ToJsonPath());
                }

                path.Pop();
            }

            return;
        }

        var before = differences.Count;
        var leftCount = left.GetArrayLength();
        var rightCount = right.GetArrayLength();

        for (var index = 0; index < Math.Min(leftCount, rightCount); index++)
        {
            path.PushIndex(index);
            Compare(left[index], right[index], path, leftRules, rightRules, differences, warnings, file);
            path.Pop();
        }

        // An index that only one side holds is a path of its own: an added index reads as a set,
        // a missing one as a remove (ADR-0032, decision 2).
        for (var index = Math.Min(leftCount, rightCount); index < Math.Max(leftCount, rightCount); index++)
        {
            path.PushIndex(index);
            differences.Add(path.ToJsonPath());
            path.Pop();
        }

        // Loud, never silent: a path that still holds an index is a path no other mod can share. One line
        // for each file, array shape and reason, so a file with ten thousand elements adds one line.
        if (differences.Count > before && warnings is not null && warnings.Count < MaxWarnings)
        {
            var warning = $"{file}: the array at {path.ToShape()} cannot be named by identity ({leftReason ?? rightReason}), so its changes are named by index.";

            if (!warnings.Contains(warning))
            {
                warnings.Add(warning);
            }
        }
    }

    /// <summary>The most lines the keying adds to one list, so a hostile file cannot fill a log.</summary>
    private const int MaxWarnings = 1000;

    /// <summary>The identity of each element as the canonical form writes it, or null when the array cannot be keyed.</summary>
    private static List<string>? ArrayKeys(JsonElement array, PathCursor path, Rules rules, out string? reason)
    {
        // The element is probed one step down, where an id of an array is never an own id of a template.
        path.PushIndex(0);

        try
        {
            return ArrayIdentity.Keys(array, value => rules.MapValue(path, value) ?? value, out reason);
        }
        finally
        {
            path.Pop();
        }
    }

    /// <summary>The properties of an object under the names the canonical form writes, so two runs merge.</summary>
    private static Dictionary<string, JsonElement> Properties(JsonElement element, PathCursor path, Rules rules)
    {
        var properties = new Dictionary<string, JsonElement>(element.GetPropertyCount(), StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            path.Push(property.Name);
            properties[rules.MapName(path, property.Name)] = property.Value;
            path.Pop();
        }

        return properties;
    }

    /// <summary>The value as the canonical form would write it, so a mapped id compares by its position.</summary>
    private static string CanonicalText(JsonElement element, PathCursor path, Rules rules) =>
        element.ValueKind == JsonValueKind.String
            ? rules.MapValue(path, element.GetString()!) ?? element.GetString()!
            : element.GetRawText();

    private static IEnumerable<JsonProperty> Sorted(JsonElement element) =>
        element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal);

    /// <summary>The rules that apply to one file: its volatile paths, and the names of its ids.</summary>
    private sealed class Rules
    {
        public static readonly Rules None = new([], null, [], []);

        private readonly ImmutableArray<string[]> _volatile;
        private readonly IdTokens? _tokens;
        private readonly ImmutableArray<string[]> _selfPaths;
        private readonly ImmutableArray<string[]> _namePaths;

        private Rules(
            ImmutableArray<string[]> volatilePaths,
            IdTokens? tokens,
            ImmutableArray<string[]> selfPaths,
            ImmutableArray<string[]> namePaths)
        {
            _volatile = volatilePaths;
            _tokens = tokens;
            _selfPaths = selfPaths;
            _namePaths = namePaths;
        }

        public static Rules For(string relativePath, JsonElement root, IdTokens? tokens = null, bool rootIsTemplate = false)
        {
            var volatilePaths = VolatilePath.Matching(relativePath);

            if (tokens is null && volatilePaths.IsDefaultOrEmpty)
            {
                return None;
            }

            // A table of templates repeats the own id of the object inside it: at the object level
            // the id sits one step down, and inside the whole table two (ADR-0034, decision 5).
            var isTable = tokens is not null && CanonicalFiles.IsTemplateTable(relativePath);

            return new Rules(
                volatilePaths is { IsDefault: true } ? [] : volatilePaths,
                tokens,
                isTable ? (rootIsTemplate ? SelfPathsAtRoot : SelfPaths) : [],
                isTable && !rootIsTemplate ? NamePaths : []);
        }

        private static readonly ImmutableArray<string[]> SelfPaths = [["_id"], ["*", "_id"]];

        /// <summary>One object seen on its own: the own id sits at the root, and nothing sits above it.</summary>
        private static readonly ImmutableArray<string[]> SelfPathsAtRoot = [["_id"]];

        private static readonly ImmutableArray<string[]> NamePaths = [["*"]];

        public bool IsVolatile(PathCursor path)
        {
            foreach (var pattern in _volatile)
            {
                if (path.Matches(pattern))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Names the id of a template by its address (ADR-0034, decision 5). An assort is not read here:
        /// its offers are named by their address before this (ADR-0032, decision 4).
        /// </summary>
        public string? MapValue(PathCursor path, string value)
        {
            if (_tokens is null)
            {
                return null;
            }

            return path.MatchesAny(_selfPaths) ? _tokens.OwnIdToken : _tokens.Token(value);
        }

        /// <summary>The key of a table of templates, which is an id, or any key when the tokens name keys.</summary>
        public string MapName(PathCursor path, string name) =>
            _tokens is not null && (_tokens.NamesKeys || path.MatchesAny(_namePaths)) ? _tokens.Map(name) : name;
    }

    /// <summary>The position in a document while it is written, as segments that a pattern can match.</summary>
    private sealed class PathCursor
    {
        private readonly List<string> _segments = new(8);

        public void Push(string name) => _segments.Add(name);

        /// <summary>An array step keeps its index for the report, and matches the <c>*</c> of a pattern.</summary>
        public void PushIndex(int index) => _segments.Add("[" + index.ToString(CultureInfo.InvariantCulture) + "]");

        /// <summary>An array step named by identity: a quoted segment, so a key may hold a dot or a bracket.</summary>
        public void PushKey(string key) => _segments.Add("['" + key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']");

        public void Pop() => _segments.RemoveAt(_segments.Count - 1);

        public bool Matches(string[] pattern)
        {
            if (pattern.Length != _segments.Count)
            {
                return false;
            }

            for (var index = 0; index < pattern.Length; index++)
            {
                if (pattern[index] != "*" && !string.Equals(pattern[index], _segments[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public bool MatchesAny(ImmutableArray<string[]> patterns)
        {
            foreach (var pattern in patterns)
            {
                if (Matches(pattern))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The position with every array step as <c>[*]</c>, so every element of one array reads the same.</summary>
        public string ToShape()
        {
            var text = "$";

            foreach (var segment in _segments)
            {
                text += segment.StartsWith('[') ? "[*]" : "." + segment;
            }

            return text;
        }

        /// <summary>"items[3]._id" reads better in a log than a list of segments.</summary>
        public string ToJsonPath()
        {
            var text = "$";

            foreach (var segment in _segments)
            {
                text += segment.StartsWith('[') ? segment : "." + segment;
            }

            return text;
        }
    }

    /// <summary>One measured path whose value is not constant, read from <c>volatile-paths.json</c>.</summary>
    public sealed record VolatilePath(string File, string Path, string Evidence)
    {
        private string[] FileSegments { get; } = File.Split('/', StringSplitOptions.RemoveEmptyEntries);

        private string[] PathSegments { get; } = Parse(Path);

        internal static ImmutableArray<string[]> Matching(string relativePath)
        {
            return VolatilePaths
                .Where(entry => Matches(relativePath, entry.FileSegments))
                .Select(entry => entry.PathSegments)
                .ToImmutableArray();
        }

        private static bool Matches(string relativePath, string[] pattern)
        {
            var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length != pattern.Length)
            {
                return false;
            }

            for (var index = 0; index < pattern.Length; index++)
            {
                if (pattern[index] != "*" && !string.Equals(pattern[index], segments[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>"$.BossLocationSpawn[*].BossChance" becomes four segments, the last an array step.</summary>
        private static string[] Parse(string path)
        {
            var body = path.TrimStart('$').TrimStart('.');
            var segments = new List<string>();

            foreach (var part in body.Split('.'))
            {
                var name = part.EndsWith("[*]", StringComparison.Ordinal) ? part[..^3] : part;
                segments.Add(name);

                if (name != part)
                {
                    segments.Add("*");
                }
            }

            return segments.ToArray();
        }

        internal static ImmutableArray<VolatilePath> Load()
        {
            var assembly = typeof(VolatilePath).Assembly;
            const string name = "SptArchive.Core.Canonical.volatile-paths.json";

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing; the build did not include volatile-paths.json.");

            using var document = JsonDocument.Parse(stream);

            return document.RootElement.GetProperty("entries")
                .EnumerateArray()
                .Select(entry => new VolatilePath(
                    entry.GetProperty("file").GetString()!,
                    entry.GetProperty("path").GetString()!,
                    entry.GetProperty("evidence").GetString()!))
                .ToImmutableArray();
        }
    }
}
