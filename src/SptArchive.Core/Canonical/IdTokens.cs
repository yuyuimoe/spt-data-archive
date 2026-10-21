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

    private IdTokens(string selfToken, IReadOnlyDictionary<string, string> byRawId, bool namesKeys)
    {
        OwnIdToken = selfToken;
        _byRawId = byRawId;
        NamesKeys = namesKeys;
    }

    /// <summary>
    /// True when an object key that is a known id is written as its token too. A table of templates always
    /// does. A bot type or a location is keyed by item ids as well, such as the weight of each backpack in
    /// <c>inventory.items.Backpack</c> (ADR-0041).
    /// </summary>
    public bool NamesKeys { get; }

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

        return new IdTokens(AnonymousToken, tokens, namesKeys: false);
    }

    /// <summary>A form that names every object by its address: the own id becomes <see cref="SelfToken"/>.</summary>
    public static IdTokens Addressed(IReadOnlyDictionary<string, string> addressByRawId) =>
        new(SelfToken, addressByRawId, namesKeys: false);

    /// <summary>
    /// A form that names every object by its address, in keys as well as in values. It is the form of the
    /// rows of an object of the server database, whose keys are ids (ADR-0041). It is never the form of a
    /// hash: the hash of ADR-0029 is the one of <see cref="Addressed"/> and <see cref="Blank"/>.
    /// </summary>
    public static IdTokens AddressedKeysAndValues(IReadOnlyDictionary<string, string> addressByRawId) =>
        new(SelfToken, addressByRawId, namesKeys: true);

    /// <summary>
    /// A seed form: every known id is anonymous except the own id of the object, if it has one, which is <see cref="SelfToken"/>.
    /// An address is derived from content, and content that held the id the run minted for the object would
    /// make the address depend on that id (ADR-0034, decision 5).
    /// </summary>
    public static IdTokens Seed(IEnumerable<string> knownIds, string? ownId)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in knownIds)
        {
            tokens[id] = AnonymousToken;
        }

        if (ownId is not null)
        {
            tokens[ownId] = SelfToken;
        }

        return new IdTokens(SelfToken, tokens, namesKeys: true);
    }

    /// <summary>The token of a value, or null when the value is not a known id and must stay as it is.</summary>
    public string? Token(string value) => _byRawId.TryGetValue(value, out var token) ? token : null;

    /// <summary>The token of a value, or the value itself.</summary>
    public string Map(string value) => Token(value) ?? value;
}
