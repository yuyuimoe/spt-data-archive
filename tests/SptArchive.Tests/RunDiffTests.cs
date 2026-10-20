using SptArchive.Core.Canonical;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The diff of a run against the baseline of its release (ADR-0013, ADR-0017, ADR-0028). The trees are
/// tiny and built by the test, so a case states exactly what changed and nothing else.
/// </summary>
public sealed class RunDiffTests : IDisposable
{
    private const string NodeId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ItemId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string QuestId = "cccccccccccccccccccccccc";
    private const string TraderId = "579dc571d53a0658a154fbec";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-diff-" + Guid.NewGuid().ToString("N"));

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
    public void An_unchanged_run_contributes_nothing()
    {
        var diff = Diff();

        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Templates);
        Assert.Empty(diff.Locales);
        Assert.Empty(diff.UnstoredChanges);
    }

    [Fact]
    public void A_value_no_run_holds_constant_is_not_a_change()
    {
        // The restock clock and the generated assort ids differ between two boots of the same release.
        var diff = Diff(run: new()
        {
            ["traders/" + TraderId + "/base.json"] = """{"_id":"579dc571d53a0658a154fbec","name":"Peacekeeper","nextResupply":1792473833,"currency":"USD"}""",
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "7ffffffffffffffffffffff9", templateId: ItemId),
        });

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void A_changed_property_of_an_official_item_is_one_path_with_the_old_value()
    {
        var diff = Diff(run: new() { ["templates/items.json"] = Items(weight: 2.5) });

        var template = Assert.Single(diff.Templates);
        Assert.Equal(("item", ItemId, "modified"), (template.Kind, template.TemplateId, template.Op));

        var change = Assert.Single(template.Changes);
        Assert.Equal("/_props/Weight", change.Path);
        Assert.Equal(("set", "1.5", "2.5"), (change.Op, change.OldValue, change.NewValue));

        // A changed official item keeps no row of its own: the paths are the archive of the change.
        Assert.Null(template.NewJson);
    }

    [Fact]
    public void A_property_the_official_item_lacks_is_a_modification_without_an_old_value()
    {
        // ADR-0017: a new property is not an addition. The template is old; only the path is new.
        var diff = Diff(run: new()
        {
            ["templates/items.json"] = Items(weight: 1.5, extraProperty: ""","CanSellOnRagfair":true"""),
        });

        var template = Assert.Single(diff.Templates);
        Assert.Equal(ModTemplateOperation.Modified, template.Op);

        var change = Assert.Single(template.Changes);
        Assert.Equal("/_props/CanSellOnRagfair", change.Path);
        Assert.Equal((ModChangePathOperation.Set, null, "true"), (change.Op, change.OldValue, change.NewValue));
    }

    [Fact]
    public void A_property_the_mod_hides_is_a_remove_with_the_old_value()
    {
        var diff = Diff(run: new() { ["templates/items.json"] = Items(weight: 1.5, slotName: null) });

        var change = Assert.Single(Assert.Single(diff.Templates).Changes);
        Assert.Equal("/_props/Slots/0/_name", change.Path);
        Assert.Equal((ModChangePathOperation.Remove, "\"mod_slot\"", null), (change.Op, change.OldValue, change.NewValue));
    }

    [Fact]
    public void A_path_inside_an_array_keeps_its_index()
    {
        var diff = Diff(run: new()
        {
            ["templates/items.json"] = Items(weight: 1.5, slotName: "other_slot"),
        });

        var change = Assert.Single(Assert.Single(diff.Templates).Changes);
        Assert.Equal("/_props/Slots/0/_name", change.Path);
        Assert.Equal(("\"mod_slot\"", "\"other_slot\""), (change.OldValue, change.NewValue));
    }

    [Fact]
    public void An_added_item_states_its_parent_and_keeps_its_object()
    {
        var diff = Diff(run: new()
        {
            ["templates/items.json"] = Items(weight: 1.5, extra: AddedItem("dddddddddddddddddddddddd", "Item D")),
        });

        var template = Assert.Single(diff.Templates);

        // The row is keyed by the address of the item: its name, not the id this run minted (ADR-0034).
        Assert.Equal(
            ("item", ContentUlid.Create("item", "Item D"), "added"),
            (template.Kind, template.TemplateId, template.Op));
        Assert.Equal("dddddddddddddddddddddddd", template.RawId);
        Assert.Equal(NodeId, template.ParentId);

        // The object is the row. Path rows would repeat it, and an addition has no old value to state.
        Assert.Empty(template.Changes);
        Assert.Contains("\"Item D\"", template.NewJson);

        // The hash covers the canonical form, so it holds no id that a boot minted.
        Assert.DoesNotContain("dddddddddddddddddddddddd", template.CanonicalJson);
    }

    [Fact]
    public void A_run_that_mints_a_new_id_names_the_same_object_the_same_way()
    {
        // ECOTI's nine items had ids that were in no file of the mod and that appeared close to the
        // run (TD-48). Two runs of that content differ in the id and in nothing else.
        var one = Diff(run: new()
        {
            ["templates/items.json"] = Items(weight: 1.5, extra: AddedItem("dddddddddddddddddddddddd", "Cotton clip")),
        });

        var two = Diff(run: new()
        {
            ["templates/items.json"] = Items(weight: 1.5, extra: AddedItem("6ad5775e327076f93b3b36bf", "Cotton clip")),
        });

        var first = Assert.Single(one.Templates);
        var second = Assert.Single(two.Templates);

        Assert.Equal(first.TemplateId, second.TemplateId);
        Assert.NotEqual(first.RawId, second.RawId);
        Assert.Equal(DatasetHash.Compute(one, Files), DatasetHash.Compute(two, Files));

        // The id of the run is kept as a lookup and never as the name of the object.
        Assert.Contains(one.IdMap, entry => entry.RawId == "dddddddddddddddddddddddd" && entry.Address == first.TemplateId);
        Assert.Contains(one.IdMap, entry => entry.RawId == ItemId && entry.Pinned);
    }

    [Fact]
    public void A_removed_template_is_an_operation_without_a_row()
    {
        var diff = Diff(run: new() { ["templates/quests.json"] = "{}" });

        var template = Assert.Single(diff.Templates);
        Assert.Equal(("quest", QuestId, "removed"), (template.Kind, template.TemplateId, template.Op));
        Assert.Null(template.NewJson);
    }

    [Fact]
    public void A_changed_quest_keeps_its_object_and_states_its_path()
    {
        var diff = Diff(run: new()
        {
            ["templates/quests.json"] = """
                {"cccccccccccccccccccccccc":{"_id":"cccccccccccccccccccccccc","QuestName":"Q1 renamed","traderId":"579dc571d53a0658a154fbec","side":"Pmc"}}
                """,
        });

        var template = Assert.Single(diff.Templates);
        Assert.Equal(("quest", "modified"), (template.Kind, template.Op));
        Assert.Contains("Q1 renamed", template.NewJson);

        // ADR-0032, decision 3: the object stays (ADR-0002) and the changed value is a path too.
        var change = Assert.Single(template.Changes);
        Assert.Equal("/QuestName", change.Path);
        Assert.Equal((ModChangePathOperation.Set, "\"Q1\"", "\"Q1 renamed\""), (change.Op, change.OldValue, change.NewValue));
    }

    [Fact]
    public void A_changed_trader_field_is_one_path_under_the_trader()
    {
        var diff = Diff(run: new()
        {
            ["traders/" + TraderId + "/base.json"] = """{"_id":"579dc571d53a0658a154fbec","name":"Peacekeeper","nextResupply":1792518932,"currency":"EUR"}""",
        });

        var template = Assert.Single(diff.Templates, row => row.Kind == ModTemplateKind.Trader);
        Assert.Equal(ModTemplateOperation.Modified, template.Op);
        Assert.NotNull(template.NewJson);

        var change = Assert.Single(template.Changes);
        Assert.Equal("/currency", change.Path);
        Assert.Equal((ModChangePathOperation.Set, "\"USD\"", "\"EUR\""), (change.Op, change.OldValue, change.NewValue));
    }

    [Fact]
    public void A_longer_array_names_only_the_added_indexes()
    {
        // ADR-0032, decision 2 (TD-46): a filter that grows from 1 to 3 entries is two paths, not one.
        var baseline = new Dictionary<string, string>
        {
            ["templates/customization.json"] = """{"dddddddddddddddddddddddd":{"_id":"dddddddddddddddddddddddd","_name":"Hat","_props":{"Side":["Usec"]}}}""",
        };
        var run = new Dictionary<string, string>
        {
            ["templates/customization.json"] = """{"dddddddddddddddddddddddd":{"_id":"dddddddddddddddddddddddd","_name":"Hat","_props":{"Side":["Usec","Bear","Savage"]}}}""",
        };

        var changes = Assert.Single(Diff(run, baseline).Templates).Changes;
        Assert.Equal(["/_props/Side/1", "/_props/Side/2"], changes.Select(change => change.Path));
        Assert.All(changes, change => Assert.Equal((ModChangePathOperation.Set, null), (change.Op, change.OldValue)));
    }

    [Fact]
    public void A_changed_customization_names_each_changed_value()
    {
        var baseline = new Dictionary<string, string>
        {
            ["templates/customization.json"] = """{"dddddddddddddddddddddddd":{"_id":"dddddddddddddddddddddddd","_name":"Hat","_props":{"Side":["Usec","Bear"]}}}""",
        };
        var run = new Dictionary<string, string>
        {
            ["templates/customization.json"] = """{"dddddddddddddddddddddddd":{"_id":"dddddddddddddddddddddddd","_name":"Hat","_props":{"Side":["Usec"]}}}""",
        };

        var template = Assert.Single(Diff(run, baseline).Templates);
        Assert.Equal(ModTemplateKind.Customization, template.Kind);
        Assert.NotNull(template.NewJson);

        var change = Assert.Single(template.Changes);
        Assert.Equal("/_props/Side/1", change.Path);
        Assert.Equal(ModChangePathOperation.Remove, change.Op);
    }

    [Fact]
    public void An_offer_is_a_template_named_by_what_it_sells()
    {
        // The offer of the baseline sells Item B; the run sells the quest template instead. One offer
        // left and one arrived, and the trader itself did not change (ADR-0032, decision 4).
        var diff = Diff(run: new()
        {
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i1", templateId: QuestId),
        });

        Assert.DoesNotContain(diff.Templates, row => row.Kind == ModTemplateKind.Trader);
        Assert.Empty(diff.UnstoredChanges);

        var removed = Assert.Single(diff.Templates, row => row.Op == ModTemplateOperation.Removed);
        var added = Assert.Single(diff.Templates, row => row.Op == ModTemplateOperation.Added);

        Assert.Equal((ModTemplateKind.Assort, OfferOf(ItemId), TraderId), (removed.Kind, removed.TemplateId, removed.ParentId));
        Assert.Equal((ModTemplateKind.Assort, OfferOf(QuestId), TraderId), (added.Kind, added.TemplateId, added.ParentId));

        // The document names the offer by its address, and holds no id that the boot minted.
        Assert.Contains(added.TemplateId, added.NewJson);
        Assert.DoesNotContain("\"i1\"", added.NewJson);
        Assert.Equal(added.NewJson, added.CanonicalJson);

        // The id of the run is a lookup; the address is the name of the offer.
        Assert.Contains(diff.IdMap, entry => entry.Kind == ModTemplateKind.Assort && entry.RawId == "i1" && entry.Address == added.TemplateId);
    }

    [Fact]
    public void A_boot_that_renews_every_assort_id_changes_nothing()
    {
        // The server mints a new instance id for every offer on every boot (ADR-0028, 5,090 values in
        // 4.1.6). Two runs of one content differ in those ids and in nothing else (TD-48).
        var one = Diff(run: new()
        {
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "686e34616c2a18ed6b0eabfb", templateId: ItemId),
        });

        var two = Diff(run: new()
        {
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "7fff0000000000000000fff9", templateId: ItemId),
        });

        Assert.Empty(one.Templates);
        Assert.Empty(two.Templates);
        Assert.Equal(DatasetHash.Compute(one, Files), DatasetHash.Compute(two, Files));

        Assert.Equal(
            Assert.Single(one.IdMap, entry => entry.Kind == ModTemplateKind.Assort).Address,
            Assert.Single(two.IdMap, entry => entry.Kind == ModTemplateKind.Assort).Address);
    }

    [Fact]
    public void An_offer_that_becomes_more_expensive_keeps_its_name()
    {
        var diff = Diff(run: new()
        {
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i1", templateId: ItemId, count: 2),
        });

        var offer = Assert.Single(diff.Templates);
        Assert.Equal((ModTemplateKind.Assort, OfferOf(ItemId), ModTemplateOperation.Modified),
            (offer.Kind, offer.TemplateId, offer.Op));

        // The amount is not part of the name, so the price is one path and not a new offer.
        var change = Assert.Single(offer.Changes);
        Assert.Equal(("/barter/5448bd6b4bdc2dfc2f8b4569/count", "set", "1", "2"),
            (change.Path, change.Op, change.OldValue, change.NewValue));
    }

    [Fact]
    public void A_part_of_an_offer_is_named_by_the_slot_it_sits_in()
    {
        // The part reaches the offer through its slot, never through the id that the boot gave it, so a
        // renewed id is not a change and a real one states its path (ADR-0032, decision 4).
        var diff = Diff(
            baseline: new() { ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i1", templateId: ItemId, parts: Part("p1", QuestId, 1)) },
            run: new() { ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i9", templateId: ItemId, parts: Part("p7", QuestId, 2, parent: "i9")) });

        var offer = Assert.Single(diff.Templates);
        Assert.Equal(ModTemplateOperation.Modified, offer.Op);

        var change = Assert.Single(offer.Changes);
        Assert.Equal("/parts/mod_scope/upd/StackObjectsCount", change.Path);
        Assert.Equal(("1", "2"), (change.OldValue, change.NewValue));

        Assert.Contains("\"parts\"", offer.NewJson);
        Assert.DoesNotContain("\"p1\"", offer.NewJson);
        Assert.DoesNotContain("\"p7\"", offer.NewJson);
    }

    [Fact]
    public void A_part_that_the_baseline_lacks_is_one_path()
    {
        var diff = Diff(run: new()
        {
            ["traders/" + TraderId + "/assort.json"] = Assort(
                instanceId: "i1",
                templateId: ItemId,
                parts: Part("p1", QuestId, 1)),
        });

        var offer = Assert.Single(diff.Templates);

        // The part is a value the baseline does not have at all, so one path states it (ADR-0017).
        var change = Assert.Single(offer.Changes);
        Assert.Equal("/parts", change.Path);
        Assert.Equal("set", change.Op);
        Assert.Null(change.OldValue);
        Assert.Contains("mod_scope", change.NewValue);
    }

    /// <summary>A part attached to the offer of the file: one node with a slot and a count.</summary>
    private static string Part(string id, string templateId, int count, string parent = "i1") =>
        ",{\"_id\":\"" + id + "\",\"_tpl\":\"" + templateId + "\",\"parentId\":\"" + parent + "\",\"slotId\":\"mod_scope\""
        + ",\"upd\":{\"StackObjectsCount\":" + count + "}}";

    [Fact]
    public void A_locale_key_the_baseline_lacks_is_a_row_without_an_old_value()
    {
        var diff = Diff(run: new()
        {
            ["locales/global/en.json"] = """
                {"bbbbbbbbbbbbbbbbbbbbbbbb Name":"Item B","dddddddddddddddddddddddd Name":"Item D","home/title":"Home"}
                """,
        });

        Assert.Equal(2, diff.Locales.Count);
        Assert.All(diff.Locales, row => Assert.Equal(("global", "en"), (row.Scope, row.LanguageCode)));

        var added = Assert.Single(diff.Locales, row => row.Key == "dddddddddddddddddddddddd Name");
        Assert.Equal(("Item D", null), (added.Value, added.OldValue));

        var changed = Assert.Single(diff.Locales, row => row.Key == "home/title");
        Assert.Equal(("Home", null), (changed.Value, changed.OldValue));
    }

    [Fact]
    public void A_locale_value_the_baseline_holds_with_another_text_keeps_the_old_one()
    {
        var diff = Diff(run: new()
        {
            ["locales/global/en.json"] = """{"bbbbbbbbbbbbbbbbbbbbbbbb Name":"Item B (renamed)"}""",
        });

        var row = Assert.Single(diff.Locales);
        Assert.Equal(("Item B (renamed)", "Item B"), (row.Value, row.OldValue));
    }

    [Fact]
    public void The_table_hashes_and_row_counts_describe_what_the_dataset_holds()
    {
        var diff = Diff(run: new() { ["templates/items.json"] = Items(weight: 2.5) });

        Assert.True(diff.TableHashes.ContainsKey("templates/items.json"));
        Assert.Equal(1, diff.RowCounts["item"]);
        Assert.Equal(0, diff.RowCounts["locales"]);
    }

    [Fact]
    public void The_same_content_hashes_the_same()
    {
        // The comparison never reads a release, so the hash covers the contribution and not the run (ADR-0029).
        var first = DatasetHash.Compute(Diff(run: new() { ["templates/items.json"] = Items(weight: 2.5) }), Files);
        var second = DatasetHash.Compute(Diff(run: new() { ["templates/items.json"] = Items(weight: 2.5) }), Files);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_changed_configuration_changes_the_hash_without_changing_a_row()
    {
        var diff = Diff(run: new() { ["templates/items.json"] = Items(weight: 2.5) });

        var shipped = DatasetHash.Compute(diff, Files);
        var curated = DatasetHash.Compute(diff, [.. Files, new ContributionFile("ECOTI/config/config.json", "aa", "bb")]);

        Assert.NotEqual(shipped, curated);
    }

    [Fact]
    public void A_change_the_archive_keeps_no_row_for_still_separates_two_datasets()
    {
        var changed = Diff(run: new()
        {
            ["locations/woods/base.json"] = """{"_Id":"woods","Name":"Woods at night"}""",
        });

        Assert.Equal("locations/woods/base.json", Assert.Single(changed.UnstoredChanges.Keys));
        Assert.NotEqual(DatasetHash.Compute(changed, Files), DatasetHash.Compute(Diff(), Files));
    }

    private static readonly ContributionFile[] Files = [new("ECOTI/SptArchive.ECOTI.dll", "0123456789abcdef", null)];

    private RunContribution Diff(Dictionary<string, string>? run = null, Dictionary<string, string>? baseline = null)
    {
        var tree = new Dictionary<string, string>(BaselineTree, StringComparer.Ordinal);

        if (baseline is not null)
        {
            foreach (var (path, content) in baseline)
            {
                tree[path] = content;
            }
        }

        Write(Baseline, tree);
        Write(Run, tree);

        if (run is not null)
        {
            Write(Run, run);
        }

        return RunDiff.Compute(Run, Baseline);
    }

    private static void Write(string root, Dictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static Dictionary<string, string> BaselineTree => new()
    {
        ["templates/items.json"] = Items(weight: 1.5),
        ["templates/quests.json"] = """
            {"cccccccccccccccccccccccc":{"_id":"cccccccccccccccccccccccc","QuestName":"Q1","traderId":"579dc571d53a0658a154fbec","side":"Pmc"}}
            """,
        ["templates/customization.json"] = "{}",
        ["locales/global/en.json"] = """{"bbbbbbbbbbbbbbbbbbbbbbbb Name":"Item B"}""",
        ["traders/" + TraderId + "/base.json"] = """
            {"_id":"579dc571d53a0658a154fbec","name":"Peacekeeper","nextResupply":1792518932,"currency":"USD"}
            """,
        ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i1", templateId: ItemId),

        // A map that no row type holds yet (TD-45): a change to it is a hash and a warning.
        ["locations/woods/base.json"] = """{"_Id":"woods","Name":"Woods"}""",
    };

    private static string Items(
        double weight,
        string? slotName = "mod_slot",
        string extra = "",
        string extraProperty = "")
    {
        var weightText = weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Null means the run's slot has no name: the property is gone, and the array keeps its length.
        var slots = slotName is null ? "[{}]" : "[{\"_name\":\"" + slotName + "\"}]";

        return "{\n"
            + "  \"aaaaaaaaaaaaaaaaaaaaaaaa\":{\"_id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_name\":\"Node A\",\"_parent\":\"\",\"_type\":\"Node\",\"_props\":{}},\n"
            + "  \"bbbbbbbbbbbbbbbbbbbbbbbb\":{\"_id\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"_name\":\"Item B\",\"_parent\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_type\":\"Item\",\"_props\":{\"Weight\":" + weightText + ",\"Slots\":" + slots + extraProperty + "}}"
            + extra
            + "\n}";
    }

    /// <summary>An item that the run adds under the node of the baseline, with the id the run gives it.</summary>
    private static string AddedItem(string id, string name, double weight = 3) =>
        ",\"" + id + "\":{\"_id\":\"" + id + "\",\"_name\":\"" + name
        + "\",\"_parent\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_type\":\"Item\",\"_props\":{\"Weight\":"
        + weight.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

    /// <summary>One offer of the trader of the baseline: the item it sells, what it costs and its level.</summary>
    private static string Assort(
        string instanceId,
        string templateId,
        int count = 1,
        int loyaltyLevel = 2,
        string parts = "") =>
        "{\"items\":[{\"_id\":\"" + instanceId + "\",\"_tpl\":\"" + templateId + "\",\"parentId\":\"hideout\"}"
        + parts + "],"
        + "\"barter_scheme\":{\"" + instanceId + "\":[[{\"count\":" + count + ",\"_tpl\":\"5448bd6b4bdc2dfc2f8b4569\"}]]},"
        + "\"loyal_level_items\":{\"" + instanceId + "\":" + loyaltyLevel + "}}";

    /// <summary>The address of an offer of the trader of the baseline, from what it sells and pays with.</summary>
    private static string OfferOf(string templateId) =>
        ContentUlid.Create("assort", TraderId, templateId, "5448bd6b4bdc2dfc2f8b4569");
}
