using System.Text.Json;
using SptArchive.Core.Canonical;

namespace SptArchive.Tests;

/// <summary>
/// An offer of a trader's assort, named by what it is (ADR-0032, decision 4). The trees are built by
/// the test, so each case states the offer it reads and the name it expects.
/// </summary>
/// <remarks>
/// The rule the cases hold: the name of an offer is built from the trader, the item it sells and what
/// the player pays with. It is never built from the instance id that a boot minted, and never from an
/// amount, so a restart or a price change keeps the name.
/// </remarks>
public sealed class OfferTests : IDisposable
{
    private const string Trader = "579dc571d53a0658a154fbec";
    private const string ItemA = "111111111111111111111111";
    private const string ItemB = "222222222222222222222222";
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string Euros = "569668774bdc2da2298b4568";
    private const string Quest = "555555555555555555555555";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-offer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_offer_is_named_by_its_trader_its_item_and_what_the_player_pays_with()
    {
        var assort = Read(OneOffer());

        var offer = Assert.Single(assort.Offers);

        Assert.Equal(ContentUlid.Create("assort", Trader, ItemA, Roubles), offer.Address);
        Assert.True(ContentUlid.IsContentUlid(offer.Address));
        Assert.Equal(("579dc571d53a0658a154fbec", ItemA, Roubles), (offer.TraderAddress, offer.RootTemplateAddress, offer.Channel));

        // The id of the run is a lookup, and the document states the address instead.
        Assert.Equal("minted1", offer.RawId);
        Assert.Contains(offer.Address, offer.Document);
        Assert.DoesNotContain("minted1", offer.Document);
    }

    [Fact]
    public void The_amount_is_not_part_of_the_name()
    {
        var cheap = Assert.Single(Read(OneOffer(count: 9)).Offers);
        var expensive = Assert.Single(Read(OneOffer(count: 10)).Offers);

        // An offer that becomes more expensive keeps its name, so the change is one path (ADR-0032).
        Assert.Equal(cheap.Address, expensive.Address);
        Assert.NotEqual(cheap.Document, expensive.Document);
    }

    [Fact]
    public void A_boot_that_mints_a_new_instance_id_keeps_the_name()
    {
        var first = Assert.Single(Read(OneOffer(instanceId: "686e34616c2a18ed6b0eabe7")).Offers);
        var second = Assert.Single(Read(OneOffer(instanceId: "7fff0000000000000000fff9")).Offers);

        Assert.Equal(first.Address, second.Address);
        Assert.Equal(first.Document, second.Document);
        Assert.NotEqual(first.RawId, second.RawId);
    }

    [Fact]
    public void The_channel_is_what_the_player_pays_with()
    {
        var inRoubles = Assert.Single(Read(OneOffer(payment: Roubles)).Offers);
        var inEuros = Assert.Single(Read(OneOffer(payment: Euros)).Offers);

        // Paying with another template makes another offer; the address says which one it is.
        Assert.NotEqual(inRoubles.Address, inEuros.Address);
    }

    [Fact]
    public void Two_offers_of_one_item_and_one_price_are_told_apart_by_the_preset()
    {
        var assort = Read(TwoOffers(levelOfFirst: 2, levelOfSecond: 4));

        Assert.Equal(2, assort.Offers.Count);
        Assert.Equal(2, assort.Offers.Select(offer => offer.Address).Distinct().Count());
        Assert.Empty(assort.Warnings);
    }

    [Fact]
    public void Two_offers_identical_in_every_part_are_named_by_their_order()
    {
        var assort = Read(TwoOffers(levelOfFirst: 2, levelOfSecond: 2));

        Assert.Equal(2, assort.Offers.Count);
        Assert.Equal(2, assort.Offers.Select(offer => offer.Address).Distinct().Count());

        // Order is not content, and the file says so rather than pretending to know (ADR-0032).
        Assert.Contains(assort.Warnings, line => line.Contains("only their order tells them apart", StringComparison.Ordinal));
    }

    [Fact]
    public void A_quest_unlock_is_attached_to_the_offer_it_unlocks()
    {
        var assort = Read(OneOffer(), questAssort: """{"success":{"minted1":"555555555555555555555555"},"fail":{}}""");

        var offer = Assert.Single(assort.Offers);
        var link = Assert.Single(assort.Links);

        // The quest arrived with this run, so it is named by what it is, not by the id it carries.
        var questAddress = ContentUlid.Create("quest", "Q");
        Assert.Equal((offer.Address, "success", questAddress), (link.OfferAddress, link.Status, link.QuestAddress));

        // The unlock sits in the offer, so a change to it is a path under the offer (ADR-0032).
        Assert.Contains("\"quests\":{\"success\":\"" + questAddress + "\"}", offer.Document);
        Assert.Contains(offer.Address, assort.CanonicalLinks());
        Assert.Contains(questAddress, assort.CanonicalLinks());
        Assert.DoesNotContain(Quest, assort.CanonicalLinks());
    }

    [Fact]
    public void The_tree_of_an_offer_becomes_one_row_per_node()
    {
        var assort = Read(OneOffer(parts: Part("part1", ItemB, slot: "mod_scope")));
        var offer = Assert.Single(assort.Offers);

        using var document = JsonDocument.Parse(offer.Document);
        var rows = Assort.Rows(offer.Address, document.RootElement);

        Assert.Equal(2, rows.Count);
        Assert.Equal((offer.Address, (string?)null, ItemA), (rows[0].NodeId, rows[0].ParentNodeId, rows[0].TemplateId));
        Assert.Equal(offer.Address + "/mod_scope", rows[1].NodeId);
        Assert.Equal(offer.Address, rows[1].ParentNodeId);
        // The part holds an item this run added, so its template is the address of that item.
        Assert.Equal(("mod_scope", ContentUlid.Create("item", "Item B")), (rows[1].SlotId, rows[1].TemplateId));

        // No row is keyed by an id that a boot minted.
        Assert.All(rows, row => Assert.DoesNotContain("part1", row.NodeId));
    }

    [Fact]
    public void A_node_that_hangs_from_an_id_the_file_lacks_is_reported()
    {
        var assort = Read(OneOffer(parts: Part("part1", ItemB, parent: "999999999999999999999999")));

        Assert.Contains(
            assort.Warnings,
            line => line.Contains("which the file does not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void An_offer_with_more_than_one_payment_option_says_so()
    {
        var assort = Read(OneOffer(barter: "[[{\"count\":1,\"_tpl\":\"" + Roubles + "\"}],[{\"count\":2,\"_tpl\":\"" + Euros + "\"}]]"));

        Assert.Contains(assort.Warnings, line => line.Contains("payment options", StringComparison.Ordinal));
    }

    /// <summary>An offer read with the addressing of one run against the baseline of its release.</summary>
    private Assort Read(string assort, string? questAssort = null)
    {
        var run = Path.Combine(_root, "run");
        var baseline = Path.Combine(_root, "baseline");

        Write(run, "templates/items.json", "{\"" + ItemA + "\":{\"_id\":\"" + ItemA + "\",\"_name\":\"Item A\"},"
            + "\"" + ItemB + "\":{\"_id\":\"" + ItemB + "\",\"_name\":\"Item B\"}}");
        Write(run, "templates/quests.json", "{\"" + Quest + "\":{\"_id\":\"" + Quest + "\",\"QuestName\":\"Q\"}}");
        Write(baseline, "templates/items.json", "{\"" + ItemA + "\":{\"_id\":\"" + ItemA + "\",\"_name\":\"Item A\"}}");

        var addressing = Addressing.Build(run, baseline);

        using var document = JsonDocument.Parse(assort);
        using var quests = questAssort is null ? null : JsonDocument.Parse(questAssort);

        return Assort.Read(addressing, Trader, document.RootElement, quests?.RootElement);
    }

    private static void Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string OneOffer(
        string instanceId = "minted1",
        string payment = Roubles,
        int count = 9,
        int loyaltyLevel = 2,
        string parts = "",
        string? barter = null) =>
        "{\"items\":[{\"_id\":\"" + instanceId + "\",\"_tpl\":\"" + ItemA + "\",\"parentId\":\"hideout\"}" + parts + "],"
        + "\"barter_scheme\":{\"" + instanceId + "\":"
        + (barter ?? "[[{\"count\":" + count + ",\"_tpl\":\"" + payment + "\"}]]") + "},"
        + "\"loyal_level_items\":{\"" + instanceId + "\":" + loyaltyLevel + "}}";

    /// <summary>Two offers of one item, paid for with the same template: they differ only by the preset.</summary>
    private static string TwoOffers(int levelOfFirst, int levelOfSecond) =>
        "{\"items\":[{\"_id\":\"minted1\",\"_tpl\":\"" + ItemA + "\",\"parentId\":\"hideout\"},"
        + "{\"_id\":\"minted2\",\"_tpl\":\"" + ItemA + "\",\"parentId\":\"hideout\"}],"
        + "\"barter_scheme\":{"
        + "\"minted1\":[[{\"count\":5,\"_tpl\":\"" + Roubles + "\"}]],"
        + "\"minted2\":[[{\"count\":5,\"_tpl\":\"" + Roubles + "\"}]]},"
        + "\"loyal_level_items\":{\"minted1\":" + levelOfFirst + ",\"minted2\":" + levelOfSecond + "}}";

    private static string Part(string id, string templateId, int count = 1, string? slot = null, string? parent = null) =>
        ",{\"_id\":\"" + id + "\",\"_tpl\":\"" + templateId + "\",\"parentId\":\"" + (parent ?? "minted1") + "\""
        + (slot is null ? string.Empty : ",\"slotId\":\"" + slot + "\"")
        + ",\"upd\":{\"StackObjectsCount\":" + count + "}}";
}
