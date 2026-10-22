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
    /// True when an object key that is a known id is written as its token too. Every form that feeds a
    /// comparison or a hash is one: a bot type keys an item id in <c>inventory.items.Backpack</c>, and a
    /// price is keyed by the id of the item it prices (ADR-0041).
    /// </summary>
    public bool NamesKeys { get; }

    /// <summary>What the own id of an object becomes.</summary>
    public string OwnIdToken { get; }

    /// <summary>How many ids this form names.</summary>
    public int Count => _byRawId.Count;

    /// <summary>
    /// A form that says only that a value or a key is a known id. It is the form of a seed or of a tie-break,
    /// where the name of the target must not enter: an address is derived from content, so content that
    /// held an address would refer to itself (ADR-0034, decision 2 and 4). A key is written as its token as
    /// well, because a raw id in a key would move the seed and the tie-break of the whole run (decision 5).
    /// </summary>
    public static IdTokens Blank(IEnumerable<string> knownIds)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in knownIds)
        {
            tokens[id] = AnonymousToken;
        }

        return new IdTokens(AnonymousToken, tokens, namesKeys: true);
    }

    /// <summary>
    /// A form that names every object by its address, in keys as well as in values. It is the form of a
    /// comparison and of the hash of a file, whose keys can be ids: a bot type keys an item id, and a price
    /// is keyed by the id of the item it prices (ADR-0041, decision 8; ADR-0034, decision 5).
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
