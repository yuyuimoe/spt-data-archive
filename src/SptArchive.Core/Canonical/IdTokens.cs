using System.Collections.Immutable;

namespace SptArchive.Core.Canonical;

/// <summary>
/// How a canonical form writes the id that a run gave an object (ADR-0034, decision 5). A raw id is
/// either anonymous or replaced by the address of the object it names, so a boot that makes new ids
/// changes no address and no hash.
/// </summary>
public sealed class IdTokens
{
    /// <summary>The own id of an object: it states nothing, because the object is named from the outside.</summary>
    public const string SelfToken = "#self";

    /// <summary>A value that is a known id, with no name for the object it points at.</summary>
    public const string AnonymousToken = "#id";

    private readonly IReadOnlyDictionary<string, string> _byRawId;

    private IdTokens(string selfToken, IReadOnlyDictionary<string, string> byRawId)
    {
        OwnIdToken = selfToken;
        _byRawId = byRawId;
    }

    /// <summary>What the own id of an object becomes.</summary>
    public string OwnIdToken { get; }

    /// <summary>How many ids this form names.</summary>
    public int Count => _byRawId.Count;

    /// <summary>
    /// A form that says only that a value is a known id. It is the form of a seed or of a tie-break,
    /// where the name of the target must not enter: an address is derived from content, so content
    /// that holds an address would refer to itself (ADR-0034, decision 2 and 4).
    /// </summary>
    public static IdTokens Blank(IEnumerable<string> knownIds)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in knownIds)
        {
            tokens[id] = AnonymousToken;
        }

        return new IdTokens(AnonymousToken, tokens);
    }

    /// <summary>A form that names every object by its address: the own id becomes <see cref="SelfToken"/>.</summary>
    public static IdTokens Addressed(IReadOnlyDictionary<string, string> addressByRawId) =>
        new(SelfToken, addressByRawId);

    /// <summary>The token of a value, or null when the value is not a known id and must stay as it is.</summary>
    public string? Token(string value) => _byRawId.TryGetValue(value, out var token) ? token : null;

    /// <summary>The token of a value, or the value itself.</summary>
    public string Map(string value) => Token(value) ?? value;
}
