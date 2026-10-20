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
        Assert.Equal((ModItemChangeOperation.Set, null, "true"), (change.Op, change.OldValue, change.NewValue));
    }

    [Fact]
    public void A_property_the_mod_hides_is_a_remove_with_the_old_value()
    {
        var diff = Diff(run: new() { ["templates/items.json"] = Items(weight: 1.5, slotName: null) });

        var change = Assert.Single(Assert.Single(diff.Templates).Changes);
        Assert.Equal("/_props/Slots/0/_name", change.Path);
        Assert.Equal((ModItemChangeOperation.Remove, "\"mod_slot\"", null), (change.Op, change.OldValue, change.NewValue));
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
            ["templates/items.json"] = Items(weight: 1.5, extra: """
                ,"dddddddddddddddddddddddd":{"_id":"dddddddddddddddddddddddd","_name":"Item D","_parent":"aaaaaaaaaaaaaaaaaaaaaaaa","_type":"Item","_props":{"Weight":3}}
                """),
        });

        var template = Assert.Single(diff.Templates);
        Assert.Equal(("item", "dddddddddddddddddddddddd", "added"), (template.Kind, template.TemplateId, template.Op));
        Assert.Equal(NodeId, template.ParentId);

        // The object is the row. Path rows would repeat it, and an addition has no old value to state.
        Assert.Empty(template.Changes);
        Assert.Contains("\"Item D\"", template.NewJson);
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
    public void A_changed_quest_carries_its_object_because_no_path_table_holds_quests()
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
        Assert.Empty(template.Changes);
    }

    [Fact]
    public void A_trader_whose_assort_changed_is_a_change_with_no_row_to_hold_it()
    {
        var diff = Diff(run: new()
        {
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i1", templateId: QuestId),
        });

        var template = Assert.Single(diff.Templates);
        Assert.Equal(("trader", TraderId, "modified"), (template.Kind, template.TemplateId, template.Op));
        Assert.Contains("\"Peacekeeper\"", template.NewJson);

        // The archive stores no assort row yet, so the file is named instead of dropped (docs/tech-debt.md).
        Assert.Equal($"traders/{TraderId}/assort.json", Assert.Single(diff.UnstoredChanges.Keys));
    }

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
            ["traders/" + TraderId + "/assort.json"] = Assort(instanceId: "i1", templateId: QuestId),
        });

        Assert.NotEqual(DatasetHash.Compute(changed, Files), DatasetHash.Compute(Diff(), Files));
    }

    private static readonly ContributionFile[] Files = [new("ECOTI/SptArchive.ECOTI.dll", "0123456789abcdef", null)];

    private RunContribution Diff(Dictionary<string, string>? run = null)
    {
        Write(Baseline, BaselineTree);
        Write(Run, BaselineTree);

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

    private static string Assort(string instanceId, string templateId) =>
        "{\"items\":[{\"_id\":\"" + instanceId + "\",\"_tpl\":\"" + templateId + "\",\"parentId\":\"hideout\"}],"
        + "\"barter_scheme\":{\"" + instanceId + "\":[[{\"count\":1,\"_tpl\":\"5448bd6b4bdc2dfc2f8b4569\"}]]},"
        + "\"loyal_level_items\":{\"" + instanceId + "\":2}}";
}
