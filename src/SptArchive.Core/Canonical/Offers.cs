using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SptArchive.Core.Canonical;

/// <summary>
/// The offers of one trader's assort, each named by what it is (ADR-0032, decision 4): the address of the
/// trader, the address of the root item and the payment channel.
/// </summary>
/// <remarks>
/// An assort file names its offers by instance ids that the server renews on every boot (ADR-0028,
/// decision 3), so the ids of two runs never match. The address is built from the offer itself: whose
/// offer it is, what it sells and what the player pays with. An offer that becomes more expensive keeps
/// its name, and the change is one path on its amount.
/// </remarks>
public sealed class Assort
{
    /// <summary>The ULID kind of an offer, and the template kind of its row (ADR-0032, decision 4).</summary>
    public const string OfferKind = "assort";

    /// <summary>The file that holds the offers of a trader.</summary>
    public const string AssortFile = "assort.json";

    /// <summary>The file that holds the quest unlocks of the offers of a trader.</summary>
    public const string QuestAssortFile = "questassort.json";

    private readonly Dictionary<string, Offer> _byAddress;
    private readonly Dictionary<string, Offer> _byRawId;

    private Assort(
        string traderRawId,
        string traderAddress,
        IReadOnlyList<Offer> offers,
        IReadOnlyList<AssortLink> links,
        IReadOnlyList<string> warnings)
    {
        TraderRawId = traderRawId;
        TraderAddress = traderAddress;
        Offers = offers;
        Links = links;
        Warnings = warnings;

        _byAddress = offers.ToDictionary(offer => offer.Address, StringComparer.Ordinal);
        _byRawId = offers.ToDictionary(offer => offer.RawId, StringComparer.Ordinal);
    }

    /// <summary>The trader directory the assort belongs to, as the run names it.</summary>
    public string TraderRawId { get; }

    /// <summary>The address of that trader (ADR-0034): its own id in the baseline, a content ULID otherwise.</summary>
    public string TraderAddress { get; }

    /// <summary>Every offer of the file, ordered by address so one content always reads the same way.</summary>
    public IReadOnlyList<Offer> Offers { get; }

    /// <summary>The quest unlocks of the file, ordered by offer, status and quest.</summary>
    public IReadOnlyList<AssortLink> Links { get; }

    /// <summary>What the file holds and the archive cannot name, in the words the run log uses.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The offer of this address, or null when the file does not hold it.</summary>
    public Offer? Find(string address) => _byAddress.GetValueOrDefault(address);

    /// <summary>The offer this run names by this id, or null. The id is a lookup, never an identity.</summary>
    public Offer? ByRawId(string rawId) => _byRawId.GetValueOrDefault(rawId);

    /// <summary>
    /// The canonical form of <c>assort.json</c>: its offers in the order of their addresses. Two boots
    /// that renew every instance id write the same text.
    /// </summary>
    public string CanonicalOffers()
    {
        var array = new JsonArray();

        foreach (var offer in Offers)
        {
            array.Add(JsonNode.Parse(offer.Document));
        }

        return Plain(new JsonObject { ["offers"] = array });
    }

    /// <summary>
    /// The canonical form of <c>questassort.json</c>: its links in the order of offer, status and quest.
    /// </summary>
    public string CanonicalLinks()
    {
        var links = new JsonArray();

        foreach (var link in Links)
        {
            links.Add(new JsonObject
            {
                ["offer"] = link.OfferAddress,
                ["quest"] = link.QuestAddress,
                ["status"] = link.Status,
            });
        }

        return Plain(new JsonObject { ["links"] = links });
    }

    /// <summary>The SHA-256 of a canonical text, lowercase hex. A file hash, not a dataset hash.</summary>
    public static string Hash(string canonicalText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText))).ToLowerInvariant();

    /// <summary>
    /// The rows of one offer tree: the offer itself and every part attached to it (ADR-0032, decision 4).
    /// The importer writes them to <c>mod_trader_assort_items</c>, which stays self-consistent because a
    /// dataset is written once.
    /// </summary>
    public static IReadOnlyList<AssortNodeRow> Rows(string offerAddress, JsonElement document)
    {
        var rows = new List<AssortNodeRow>
        {
            new(
                NodeId: offerAddress,
                OfferId: offerAddress,
                ParentNodeId: null,
                TemplateId: Scalar(document, "_tpl") ?? string.Empty,
                SlotId: Scalar(document, "slotId"),
                Upd: Nested(document, "upd"),
                Barter: Nested(document, "barter"),
                LoyaltyLevel: Number(document, "loyalLevel")),
        };

        if (document.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Object)
        {
            foreach (var part in parts.EnumerateObject().OrderBy(part => part.Name, StringComparer.Ordinal))
            {
                var at = part.Name.LastIndexOf('/');
                var parentPath = at < 0 ? null : part.Name[..at];

                rows.Add(new AssortNodeRow(
                    NodeId: offerAddress + "/" + part.Name,
                    OfferId: offerAddress,
                    ParentNodeId: parentPath is null ? offerAddress : offerAddress + "/" + parentPath,
                    TemplateId: Scalar(part.Value, "_tpl") ?? string.Empty,
                    SlotId: Scalar(part.Value, "slotId"),
                    Upd: Nested(part.Value, "upd"),
                    Barter: null,
                    LoyaltyLevel: null));
            }
        }

        return rows;
    }

    /// <summary>
    /// Reads one side of a run. <paramref name="assort"/> is <c>assort.json</c> and
    /// <paramref name="questAssort"/> is <c>questassort.json</c>; either may be absent.
    /// </summary>
    public static Assort Read(
        Addressing addressing,
        string traderRawId,
        JsonElement? assort,
        JsonElement? questAssort) =>
        new Reader(addressing, traderRawId, assort, questAssort).Build();

    /// <summary>The canonical form of a document with its keys sorted, which is how a value is compared.</summary>
    private static string Plain(JsonNode node)
    {
        using var parsed = JsonDocument.Parse(node.ToJsonString());
        return CanonicalForm.CanonicalText(parsed.RootElement);
    }

    private static string? Scalar(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
        : null;

    private static string? Nested(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value.GetRawText()
            : null;

    /// <summary>One node of an assort file: an offer, or a part of one.</summary>
    private sealed record Node(
        string RawId,
        string TemplateId,
        string TemplateAddress,
        string? ParentRawId,
        string SlotId,
        JsonElement Value);

    /// <summary>One node and the path of the slots that reaches it from its offer.</summary>
    private sealed record NodePath(Node Node, string Path);

    /// <summary>What an offer is: the item it sells and what the player pays with.</summary>
    private sealed record Description(string RootTemplateAddress, string Channel);

    /// <summary>The one pass that names the offers of one file, for one side of a run.</summary>
    private sealed class Reader
    {
        /// <summary>Guards a file that points a node at itself, or in a circle.</summary>
        private const int MaximumDepth = 32;

        private readonly Addressing _addressing;
        private readonly string _traderRawId;
        private readonly JsonElement? _assort;
        private readonly JsonElement? _questAssort;
        private readonly List<string> _warnings = [];
        private readonly Dictionary<string, Node> _nodes;
        private readonly Dictionary<string, Description> _described = new(StringComparer.Ordinal);
        private readonly string _traderAddress;

        public Reader(Addressing addressing, string traderRawId, JsonElement? assort, JsonElement? questAssort)
        {
            _addressing = addressing;
            _traderRawId = traderRawId;
            _assort = assort;
            _questAssort = questAssort;
            _traderAddress = addressing.Address(traderRawId);
            _nodes = IndexNodes();
        }

        public Assort Build()
        {
            var roots = Roots();
            var links = ReadLinks();

            // One seed per offer, then one address per seed. A seed that names more than one offer gains
            // the canonical hash of the preset, then the ordinal (ADR-0032 decision 4, ADR-0034 decision 4).
            var seeds = new Dictionary<string, List<Node>>(StringComparer.Ordinal);

            foreach (var root in roots)
            {
                var seed = SeedFor(root);

                if (!seeds.TryGetValue(seed, out var holders))
                {
                    seeds[seed] = holders = [];
                }

                holders.Add(root);
            }

            var addresses = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (seed, holders) in seeds)
            {
                Name(seed, holders, addresses);
            }

            var offers = new List<Offer>(roots.Count);

            foreach (var root in roots)
            {
                var description = Describe(root);
                var address = addresses[root.RawId];

                offers.Add(new Offer(
                    address,
                    root.RawId,
                    _traderAddress,
                    description.RootTemplateAddress,
                    description.Channel,
                    Document(address, description, root, links)));
            }

            offers.Sort((left, right) => string.CompareOrdinal(left.Address, right.Address));

            var named = offers.ToDictionary(offer => offer.RawId, offer => offer.Address, StringComparer.Ordinal);

            var resolved = links
                .Where(link => named.ContainsKey(link.OfferRawId))
                .Select(link => new AssortLink(link.OfferRawId, named[link.OfferRawId], link.Status, link.QuestAddress))
                .OrderBy(link => link.OfferAddress, StringComparer.Ordinal)
                .ThenBy(link => link.Status, StringComparer.Ordinal)
                .ThenBy(link => link.QuestAddress, StringComparer.Ordinal)
                .ToArray();

            return new Assort(_traderRawId, _traderAddress, offers, resolved, _warnings);
        }

        /// <summary>Hands every offer of one seed its address, splitting the seed when it holds several.</summary>
        private void Name(string seed, List<Node> holders, Dictionary<string, string> addresses)
        {
            if (holders.Count == 1)
            {
                addresses[holders[0].RawId] = ContentUlid.FromSeed(seed);
                return;
            }

            var presets = holders.ToDictionary(holder => holder.RawId, Preset, StringComparer.Ordinal);

            foreach (var byPreset in presets
                         .GroupBy(pair => pair.Value, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var same = byPreset.Select(pair => pair.Key).ToList();

                if (same.Count == 1)
                {
                    addresses[same[0]] = ContentUlid.FromSeed(
                        ContentUlid.Seed(Assort.OfferKind, SeedFields(same[0]).Append(byPreset.Key)));
                    continue;
                }

                // Offers identical in every part: only their order is left, and order is not content.
                same.Sort(StringComparer.Ordinal);

                for (var index = 0; index < same.Count; index++)
                {
                    addresses[same[index]] = ContentUlid.FromSeed(ContentUlid.Seed(
                        Assort.OfferKind,
                        SeedFields(same[index]).Append(byPreset.Key).Append(ContentUlid.Ordinal(index))));
                }

                _warnings.Add(
                    $"traders/{_traderRawId}: {same.Count} offers sell one item, pay alike and share a preset, " +
                    "so only their order tells them apart. The archive names them by that order, and the stock " +
                    "and the amount are not part of a preset (ADR-0032, decision 4).");
            }
        }

        private IEnumerable<string> SeedFields(string rootRawId)
        {
            var description = Describe(_nodes[rootRawId]);
            return [_traderAddress, description.RootTemplateAddress, description.Channel];
        }

        private string SeedFor(Node root)
        {
            var description = Describe(root);
            return ContentUlid.Seed(Assort.OfferKind, [_traderAddress, description.RootTemplateAddress, description.Channel]);
        }

        private Description Describe(Node root)
        {
            if (_described.TryGetValue(root.RawId, out var known))
            {
                return known;
            }

            var payments = PaymentTemplates(root.RawId);

            if (payments.Count == 0)
            {
                _warnings.Add(
                    $"traders/{_traderRawId}: offer '{root.RawId}' sells '{root.TemplateAddress}' and states no payment, " +
                    "so its channel is empty.");
            }

            var channel = string.Join(
                "+",
                payments.Select(_addressing.Address).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal));

            var description = new Description(root.TemplateAddress, channel);
            _described[root.RawId] = description;
            return description;
        }

        private Dictionary<string, Node> IndexNodes()
        {
            var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);

            if (_assort is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return nodes;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || Scalar(item, "_id") is not { Length: > 0 } id
                    || Scalar(item, "_tpl") is not { Length: > 0 } template)
                {
                    continue;
                }

                nodes[id] = new Node(
                    id,
                    template,
                    _addressing.Address(template),
                    Scalar(item, "parentId"),
                    Scalar(item, "slotId") ?? string.Empty,
                    item);
            }

            return nodes;
        }

        /// <summary>Every node whose parent is not one of the nodes of this file: an offer, not a part.</summary>
        private List<Node> Roots()
        {
            var roots = new List<Node>();

            foreach (var node in _nodes.Values.OrderBy(node => node.RawId, StringComparer.Ordinal))
            {
                if (RootOf(node, out var steps) is null)
                {
                    _warnings.Add(
                        $"traders/{_traderRawId}: the tree above '{node.RawId}' is more than {MaximumDepth} deep, " +
                        "so the archive reads it as its own offer.");
                    roots.Add(node);
                    continue;
                }

                if (steps == 0)
                {
                    // An offer hangs from the mark the server uses ("hideout"). A node that names a real
                    // id the file does not hold is a broken tree, and the archive says so.
                    if (node.ParentRawId is { Length: 24 } parent)
                    {
                        _warnings.Add(
                            $"traders/{_traderRawId}: node '{node.RawId}' hangs from '{parent}', which the file " +
                            "does not hold, so the archive reads it as an offer of its own.");
                    }

                    roots.Add(node);
                }
            }

            return roots;
        }

        /// <summary>The offer a node hangs from, and how many steps above it that is; null when too deep.</summary>
        private Node? RootOf(Node node, out int steps)
        {
            var current = node;
            steps = 0;

            while (current.ParentRawId is { } parent && _nodes.TryGetValue(parent, out var above))
            {
                if (++steps > MaximumDepth)
                {
                    return null;
                }

                current = above;
            }

            return current;
        }

        /// <summary>The templates the player pays with, in every payment option the offer states.</summary>
        private List<string> PaymentTemplates(string offerRawId)
        {
            var templates = new List<string>();
            var options = Payments(offerRawId);

            if (options is not { } list)
            {
                return templates;
            }

            foreach (var option in list.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var payment in option.EnumerateArray())
                {
                    if (payment.ValueKind == JsonValueKind.Object
                        && Scalar(payment, "_tpl") is { Length: > 0 } template
                        && !templates.Contains(template, StringComparer.Ordinal))
                    {
                        templates.Add(template);
                    }
                }
            }

            return templates;
        }

        private JsonElement? Payments(string offerRawId)
        {
            if (_assort is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("barter_scheme", out var scheme)
                || scheme.ValueKind != JsonValueKind.Object
                || !scheme.TryGetProperty(offerRawId, out var options)
                || options.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return options;
        }

        private int? LoyaltyLevel(string offerRawId)
        {
            if (_assort is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("loyal_level_items", out var levels)
                || levels.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return Number(levels, offerRawId);
        }

        /// <summary>The parts of an offer by the path of their slots, in the order the path sorts.</summary>
        private List<NodePath> Subtree(Node root)
        {
            var parts = new List<NodePath>();
            var taken = new HashSet<string>(StringComparer.Ordinal);

            Walk(root, string.Empty);

            parts.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
            return parts;

            void Walk(Node parent, string parentPath)
            {
                foreach (var child in Children(parent.RawId))
                {
                    var slot = child.SlotId.Length == 0 ? child.TemplateAddress : child.SlotId;
                    var path = parentPath.Length == 0 ? slot : parentPath + "/" + slot;

                    if (!taken.Add(path))
                    {
                        // Two parts of one slot are told apart by the item in them, which is what differs.
                        var candidate = path + "#" + child.TemplateAddress;
                        var index = 1;

                        while (!taken.Add(candidate))
                        {
                            candidate = path + "#" + child.TemplateAddress + "#" + index;
                            index++;
                        }

                        path = candidate;
                    }

                    parts.Add(new NodePath(child, path));
                    Walk(child, path);
                }
            }
        }

        private IEnumerable<Node> Children(string rawId) =>
            _nodes.Values
                .Where(node => string.Equals(node.ParentRawId, rawId, StringComparison.Ordinal))
                .OrderBy(node => node.SlotId, StringComparer.Ordinal)
                .ThenBy(node => node.TemplateAddress, StringComparer.Ordinal)
                .ThenBy(node => node.RawId, StringComparer.Ordinal);

        /// <summary>
        /// The preset of an offer: its tree without instance ids, without amounts and without
        /// <c>upd</c> (ADR-0032, decision 4). Two offers of one seed differ here, or they are one offer.
        /// </summary>
        private string Preset(Node root)
        {
            var parts = new JsonObject();

            foreach (var part in Subtree(root))
            {
                parts[part.Path] = Copy(part.Node, skip: ["upd"]);
            }

            var payments = new JsonArray();

            foreach (var template in PaymentTemplates(root.RawId)
                         .Select(_addressing.Address)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                payments.Add(template);
            }

            var preset = new JsonObject
            {
                ["_tpl"] = root.TemplateAddress,
                ["parts"] = parts,
                ["barter"] = payments,
            };

            if (LoyaltyLevel(root.RawId) is { } level)
            {
                preset["loyalLevel"] = level;
            }

            return Plain(preset);
        }

        /// <summary>
        /// The offer as the archive stores it: its tree by address, the payments by template, the loyalty
        /// level and its quest unlocks. Every id in it is an address, so the document is the same in the
        /// next boot (ADR-0034, decision 6).
        /// </summary>
        private string Document(string address, Description description, Node root, List<AssortLink> links)
        {
            var document = Copy(root, skip: []);
            document["_id"] = address;
            document["_tpl"] = description.RootTemplateAddress;

            if (Subtree(root) is { Count: > 0 } subtree)
            {
                var parts = new JsonObject();

                foreach (var part in subtree)
                {
                    var copy = Copy(part.Node, skip: []);
                    copy["_tpl"] = part.Node.TemplateAddress;
                    parts[part.Path] = copy;
                }

                document["parts"] = parts;
            }

            if (Barter(root) is { Count: > 0 } barter)
            {
                document["barter"] = barter;
            }

            if (LoyaltyLevel(root.RawId) is { } level)
            {
                document["loyalLevel"] = level;
            }

            var quests = new JsonObject();

            foreach (var link in links
                         .Where(link => string.Equals(link.OfferRawId, root.RawId, StringComparison.Ordinal))
                         .OrderBy(link => link.Status, StringComparer.Ordinal))
            {
                quests[link.Status] = link.QuestAddress;
            }

            if (quests.Count > 0)
            {
                document["quests"] = quests;
            }

            return Plain(document);
        }

        /// <summary>The payments by the address of the template that is paid, never by the position.</summary>
        private JsonObject Barter(Node root)
        {
            var barter = new JsonObject();

            if (Payments(root.RawId) is not { } options)
            {
                return barter;
            }

            if (options.GetArrayLength() != 1)
            {
                // Measured 0 offers with more than one option in 4.1.6 (docs/tech-debt.md). The counts
                // are added, so the difference stays content and only its detail is lost.
                _warnings.Add(
                    $"traders/{_traderRawId}: offer '{root.RawId}' states {options.GetArrayLength()} payment options, " +
                    "and the archive keeps one amount per template (docs/tech-debt.md).");
            }

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var payment in option.EnumerateArray())
                {
                    if (payment.ValueKind != JsonValueKind.Object
                        || Scalar(payment, "_tpl") is not { Length: > 0 } template)
                    {
                        continue;
                    }

                    var paidWith = _addressing.Address(template);
                    counts[paidWith] = counts.GetValueOrDefault(paidWith) + (Long(payment, "count") ?? 0);
                }
            }

            foreach (var (paidWith, count) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                barter[paidWith] = new JsonObject { ["count"] = count };
            }

            return barter;
        }

        private List<AssortLink> ReadLinks()
        {
            var links = new List<AssortLink>();

            if (_questAssort is not { ValueKind: JsonValueKind.Object } root)
            {
                return links;
            }

            foreach (var status in root.EnumerateObject())
            {
                if (status.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var link in status.Value.EnumerateObject())
                {
                    if (link.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!_nodes.ContainsKey(link.Name))
                    {
                        _warnings.Add(
                            $"traders/{_traderRawId}: '{status.Name}' unlocks offer '{link.Name}', " +
                            "which the assort of this file does not hold.");
                        continue;
                    }

                    links.Add(new AssortLink(
                        link.Name,
                        string.Empty,
                        status.Name,
                        _addressing.Address(link.Value.GetString()!)));
                }
            }

            return links;
        }

        /// <summary>The node as the run wrote it, without the ids that a boot may renew.</summary>
        private static JsonObject Copy(Node node, string[] skip)
        {
            var copy = new JsonObject();

            foreach (var property in node.Value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (property.Name is "_id" or "parentId" || skip.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                copy[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }

            return copy;
        }

        private static long? Long(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
            : null;
    }
}

/// <summary>One offer, named by its address. Its document is the canonical text that a change is read from.</summary>
public sealed record Offer(
    string Address,
    string RawId,
    string TraderAddress,
    string RootTemplateAddress,
    string Channel,
    string Document);

/// <summary>One quest unlock of an offer (ADR-0032, decision 4).</summary>
public sealed record AssortLink(string OfferRawId, string OfferAddress, string Status, string QuestAddress);

/// <summary>One row of an offer tree: the offer itself, or a part attached to it.</summary>
public sealed record AssortNodeRow(
    string NodeId,
    string OfferId,
    string? ParentNodeId,
    string TemplateId,
    string? SlotId,
    string? Upd,
    string? Barter,
    int? LoyaltyLevel);
