using System.Text.Json;
using SptArchive.Core.Canonical;

namespace SptArchive.Tests;

/// <summary>
/// The names of mod objects (ADR-0034, decision 1 to 4): an object of the baseline keeps its id, a new
/// object is named by the fields that identify it, and content and then order separate two objects
/// that hold one name. The trees are tiny and built by the test.
/// </summary>
public sealed class AddressingTests : IDisposable
{
    private const string NodeId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ItemId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string QuestId = "cccccccccccccccccccccccc";
    private const string TraderId = "579dc571d53a0658a154fbec";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-names-" + Guid.NewGuid().ToString("N"));

    private string Baseline => Path.Combine(_root, "baseline");

    private string Run => Path.Combine(_root, "run");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_object_of_the_baseline_keeps_the_id_it_has()
    {
        Write("baseline", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5)));
        Write("run", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 2.5)));

        var addressing = Addressing.Build(Run, Baseline);

        Assert.True(addressing.IsPinned(ItemId));
        Assert.Equal(ItemId, addressing.Address(ItemId));
        Assert.Equal(new Addressing.AddressEntry("item", ItemId, ItemId, Pinned: true), Assert.Single(addressing.Entries));
    }

    [Fact]
    public void A_new_object_is_named_by_its_name_and_not_by_the_id_the_run_gave_it()
    {
        var first = "dddddddddddddddddddddddd";
        var second = "6ad5775e327076f93b3b36bf";

        Write("baseline", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5)));
        Write("run", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Item(first, "Cotton clip", weight: 0.1)));

        var one = Addressing.Build(Run, Baseline);
        var address = one.Address(first);

        // The same content under a different id, which is what a run that mints its own ids produces.
        var otherRun = Path.Combine(_root, "run2");
        Write("run2", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Item(second, "Cotton clip", weight: 0.1)));

        var two = Addressing.Build(otherRun, Baseline);

        Assert.Equal(address, two.Address(second));
        Assert.NotEqual(first, address);
        Assert.NotEqual(second, address);
        Assert.True(ContentUlid.IsContentUlid(address));
        Assert.False(one.IsPinned(first));

        // The seed is the rule of decision 3, so the name is a function of the name of the object.
        Assert.Equal(ContentUlid.Create("item", "Cotton clip"), address);
    }

    [Fact]
    public void Two_objects_of_one_name_get_one_name_each()
    {
        // The measurement of decision 4: the pair of Magpul MBUS sights differs in the prefab path.
        const string name = "[WTT-Armory]_(Magpul MBUS Gen3 flip-up rear sight)";
        const string black = "682e8c4bc098b59f14dde097";
        const string flat = "6861847dfd5210a3e64677a3";

        Write("baseline", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5)));
        Write("run", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Item(black, name, weight: 0.2, prefab: "assets/mbus3rearblk.bundle"),
            Item(flat, name, weight: 0.2, prefab: "assets/mbus3rearfde.bundle")));

        var addressing = Addressing.Build(Run, Baseline);

        Assert.NotEqual(addressing.Address(black), addressing.Address(flat));
        Assert.Empty(addressing.Warnings);

        // A second run of the same content gives the same two names, whatever ids it gives the objects.
        var otherRun = Path.Combine(_root, "run2");
        Write("run2", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Item("111111111111111111111111", name, weight: 0.2, prefab: "assets/mbus3rearblk.bundle"),
            Item("222222222222222222222222", name, weight: 0.2, prefab: "assets/mbus3rearfde.bundle")));

        var again = Addressing.Build(otherRun, Baseline);

        Assert.Equal(
            new[] { addressing.Address(black), addressing.Address(flat) }.OrderBy(name2 => name2, StringComparer.Ordinal),
            new[] { again.Address("111111111111111111111111"), again.Address("222222222222222222222222") }
                .OrderBy(name2 => name2, StringComparer.Ordinal));
    }

    [Fact]
    public void Objects_of_one_name_and_one_content_are_named_by_order_and_the_run_says_so()
    {
        const string name = "Taped STANAG mag";

        Write("baseline", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5)));
        Write("run", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Item("111111111111111111111111", name, weight: 0.3),
            Item("222222222222222222222222", name, weight: 0.3)));

        var addressing = Addressing.Build(Run, Baseline);

        Assert.NotEqual(addressing.Address("111111111111111111111111"), addressing.Address("222222222222222222222222"));
        Assert.Contains(addressing.Warnings, warning => warning.Contains(name, StringComparison.Ordinal) || warning.Contains("same content", StringComparison.Ordinal));
    }

    [Fact]
    public void An_object_with_no_name_is_named_by_its_content_and_the_run_says_so()
    {
        Write("baseline", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5)));
        Write("run", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            """{"_id":"333333333333333333333333","_parent":"","_type":"Item","_props":{"Weight":0.4}}"""));

        var addressing = Addressing.Build(Run, Baseline);

        Assert.True(ContentUlid.IsContentUlid(addressing.Address("333333333333333333333333")));
        Assert.Contains(addressing.Warnings, warning => warning.Contains("no name", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_names_only_the_ids_of_the_kinds_it_holds()
    {
        // A quest id is an id; an item that holds that text in a property is not naming the quest.
        Write("run", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5, prefab: QuestId)));
        Write("run", CanonicalFiles.Quests, "{\"" + QuestId + "\":{\"_id\":\"" + QuestId + "\",\"QuestName\":\"A quest\"}}");

        var addressing = Addressing.Build(Run, Baseline);

        Assert.Null(addressing.TokensFor(CanonicalFiles.Items).Token(QuestId));

        // Both objects are new, so each is named by its identity; a file names the ids of its kinds only.
        var questAddress = addressing.Address(QuestId);
        Assert.True(ContentUlid.IsContentUlid(questAddress));
        Assert.Equal("#" + questAddress, addressing.TokensFor(CanonicalFiles.Quests).Token(QuestId));
        Assert.Equal("#" + addressing.Address(ItemId), addressing.TokensFor(CanonicalFiles.Items).Token(ItemId));
        Assert.Equal(IdTokens.SelfToken, addressing.TokensFor(CanonicalFiles.Items).OwnIdToken);
    }

    [Fact]
    public void The_own_id_of_an_object_never_enters_its_content_hash()
    {
        var first = Item("dddddddddddddddddddddddd", "Cotton clip", weight: 0.1);
        var second = Item("6ad5775e327076f93b3b36bf", "Cotton clip", weight: 0.1);

        Write("baseline", CanonicalFiles.Items, Items(Item(NodeId, "Node A", weight: 0)));
        Write("run", CanonicalFiles.Items, Items(Item(NodeId, "Node A", weight: 0), first));

        var addressing = Addressing.Build(Run, Baseline);
        var tokens = addressing.TokensFor(CanonicalFiles.Items);

        using var one = JsonDocument.Parse("{\"" + ItemId + "\":" + first + "}");
        using var two = JsonDocument.Parse("{\"" + ItemId + "\":" + second + "}");

        Assert.Equal(
            CanonicalForm.Hash(CanonicalFiles.Items, one.RootElement, tokens),
            CanonicalForm.Hash(CanonicalFiles.Items, two.RootElement, tokens));

        // And the object of the baseline is named, not restated: its key is its address.
        using var baseline = JsonDocument.Parse("{\"" + NodeId + "\":" + Item(NodeId, "Node A", weight: 0) + "}");
        var canonical = CanonicalForm.Canonicalize(CanonicalFiles.Items, baseline.RootElement, tokens);
        var text = System.Text.Encoding.UTF8.GetString(canonical);

        Assert.Contains("{\"#" + NodeId + "\":", text, StringComparison.Ordinal);
        Assert.DoesNotContain(NodeId + "\":{\"_id\":\"" + NodeId, text, StringComparison.Ordinal);
        Assert.Contains("\"_id\":\"" + IdTokens.SelfToken + "\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trader_of_the_baseline_keeps_its_directory_name_and_a_new_one_is_named_by_it()
    {
        Write("baseline", $"traders/{TraderId}/base.json", "{\"_id\":\"" + TraderId + "\",\"nickname\":\"Peacekeeper\"}");

        var newTrader = "68d85e2a7c4f1b03a9e61001";
        Write("run", $"traders/{TraderId}/base.json", "{\"_id\":\"" + TraderId + "\",\"nickname\":\"Peacekeeper\"}");
        Write("run", $"traders/{newTrader}/base.json", "{\"_id\":\"" + newTrader + "\",\"nickname\":\"Ledger\"}");

        var addressing = Addressing.Build(Run, Baseline);

        Assert.Equal(TraderId, addressing.Address(TraderId));
        Assert.Equal(ContentUlid.Create("trader", newTrader), addressing.Address(newTrader));
        Assert.Null(addressing.TokensFor(CanonicalFiles.Items).Token(newTrader));
        Assert.Equal(
            "#" + addressing.Address(newTrader),
            addressing.TokensFor($"traders/{TraderId}/assort.json").Token(newTrader));
    }

    [Fact]
    public void A_minted_id_in_a_key_does_not_name_an_object_that_has_no_name()
    {
        // TD-83. An object with no name is named by its content (decision 5), and that content names another
        // object of the run under an id the run minted. A boot that mints another id must not move the name.
        const string first = "dddddddddddddddddddddddd";
        const string second = "6ad5775e327076f93b3b36bf";
        const string nameless = "111111111111111111111111";

        Write("baseline", CanonicalFiles.Items, Items(Item(ItemId, "Item B", weight: 1.5)));
        Write("run", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Nameless(nameless, first),
            Item(first, "Cotton clip", weight: 0.1)));

        var one = Addressing.Build(Run, Baseline);

        var otherRun = Path.Combine(_root, "run2");
        Write("run2", CanonicalFiles.Items, Items(
            Item(ItemId, "Item B", weight: 1.5),
            Nameless(nameless, second),
            Item(second, "Cotton clip", weight: 0.1)));

        var two = Addressing.Build(otherRun, Baseline);

        Assert.True(ContentUlid.IsContentUlid(one.Address(nameless)));
        Assert.Equal(one.Address(nameless), two.Address(nameless));
    }

    /// <summary>Writes one file of an export, under the baseline or the run of this test.</summary>
    private void Write(string side, string relativePath, string json)
    {
        var path = Path.Combine(_root, side, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>A template table keyed by the id of each object, as the export writes it.</summary>
    private static string Items(params string[] items) =>
        "{" + string.Join(",", items.Select(item => "\"" + IdOf(item) + "\":" + item)) + "}";

    /// <summary>Every object these trees hold starts with its id, so the key is the text after the field.</summary>
    private static string IdOf(string item)
    {
        const string field = "{\"_id\":\"";
        return item.Substring(field.Length, item.IndexOf('"', field.Length) - field.Length);
    }

    private static string Item(string id, string name, double weight, string? prefab = null) =>
        "{\"_id\":\"" + id + "\",\"_name\":\"" + name + "\",\"_parent\":\"\",\"_type\":\"Item\",\"_props\":{"
        + "\"Weight\":" + weight.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (prefab is null ? string.Empty : ",\"Prefab\":{\"path\":\"" + prefab + "\"}")
        + "}}";

    /// <summary>An item that has no name: its content names it, and the content names another object by id.</summary>
    private static string Nameless(string id, string referencedId) =>
        "{\"_id\":\"" + id + "\",\"_parent\":\"\",\"_type\":\"Item\",\"_props\":{\"Mods\":{\"" + referencedId + "\":1}}}";
}
