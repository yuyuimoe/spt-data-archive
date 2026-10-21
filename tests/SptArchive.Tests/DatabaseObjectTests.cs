using System.Text.Json;
using SptArchive.Core.Canonical;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The rows of a location, a bot type, a hideout part, a price and a handbook entry (ADR-0041). The
/// trees are tiny and built by <see cref="ObjectTree"/>, so a case states which value moved.
/// </summary>
public sealed class DatabaseObjectTests : IDisposable
{
    private static readonly ContributionFile[] Files = [new("Thermal/Thermal.dll", "0123456789abcdef", null)];

    /// <summary>The address of the item that the run adds: its name names it (ADR-0034).</summary>
    private static readonly string ItemD = ContentUlid.Create("item", "Item D");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-objects-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- The content hash does not move (ADR-0029, amendment 4) ---------------------------------

    [Fact]
    public void The_content_hash_of_the_same_run_is_the_value_it_had_before_the_objects_had_rows()
    {
        // The golden value was computed by RunDiff and DatasetHash on 2026-10-21, before a single object
        // row existed. A row that enters the hash would move it, and a dataset that the archive already
        // stores would stop being the dataset of its own content.
        Assert.Equal(
            "4327747589fa2f1b93d637a37975abfe669353426ca68cfc70f94b837d645aef",
            DatasetHash.Compute(Diff(), Files));
    }

    [Fact]
    public void The_hash_is_the_same_with_the_object_rows_and_without_them()
    {
        var diff = Diff();

        Assert.NotEmpty(diff.Objects);
        Assert.Equal(DatasetHash.Compute(diff with { Objects = [] }, Files), DatasetHash.Compute(diff, Files));
    }

    [Fact]
    public void A_file_with_rows_keeps_its_canonical_hash_in_the_hash_basis()
    {
        var diff = Diff();

        foreach (var path in new[]
                 {
                     "locations/woods/base.json", "locations/arena_airpit/base.json", "bots/types/assault.json",
                     "hideout/production.json", "templates/prices.json", "templates/handbook.json",
                 })
        {
            Assert.Contains(path, diff.UnstoredChanges.Keys);
        }

        // A file that only has rows is not reported as a file with no row.
        Assert.Empty(diff.FilesWithoutRows);
    }

    [Fact]
    public void A_changed_file_of_another_table_is_still_a_file_with_no_row()
    {
        var baseline = ObjectTree.Baseline();
        baseline["globals.json"] = """{"time":1}""";

        var run = ObjectTree.RunOverBaseline();
        run["globals.json"] = """{"time":2}""";

        var diff = Diff(baseline, run);

        Assert.Equal("globals.json", Assert.Single(diff.FilesWithoutRows));
        Assert.Contains("globals.json", diff.UnstoredChanges.Keys);
    }

    [Fact]
    public void A_run_that_changes_none_of_the_five_tables_contributes_no_object()
    {
        var diff = Diff(ObjectTree.Baseline(), ObjectTree.Baseline());

        Assert.Empty(diff.Objects);
        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void The_object_counts_are_in_the_row_counts_of_the_dataset()
    {
        var diff = Diff();

        Assert.Equal(
            (3, 3, 3, 2, 3),
            (diff.RowCounts["location"], diff.RowCounts["bot"], diff.RowCounts["hideout"], diff.RowCounts["price"], diff.RowCounts["handbook"]));
        Assert.Equal(14, diff.Objects.Count);
    }

    // ---- A location -----------------------------------------------------------------------------

    [Fact]
    public void A_changed_value_of_an_old_location_is_a_path_under_the_part_of_its_file()
    {
        var woods = Object(Diff(), "location", "woods");

        Assert.Equal(ModTemplateOperation.Modified, woods.Template.Op);

        var insurance = Assert.Single(woods.Template.Changes, change => change.Path == "/base/Insurance");
        Assert.Equal((ModChangePathOperation.Set, "true", "false"), (insurance.Op, insurance.OldValue, insurance.NewValue));

        // The boss chance rotates on every boot (ADR-0028): it moved in the run and is not a change.
        Assert.DoesNotContain(woods.Template.Changes, change => change.Path.Contains("BossChance", StringComparison.Ordinal));

        // A modified object keeps no source of its own: its paths are the archive of the change.
        Assert.Empty(woods.Parts);
    }

    [Fact]
    public void A_boss_chance_that_rotates_on_every_boot_makes_no_row()
    {
        // The chance is volatile (ADR-0028), so a run that only rotates it changed nothing (ADR-0041).
        var run = ObjectTree.Baseline();
        run["locations/woods/base.json"] =
            """{"_Id":"woods","Name":"Woods","Insurance":true,"BossLocationSpawn":[{"BossName":"bossBully","BossChance":0}]}""";

        var diff = Diff(ObjectTree.Baseline(), run);

        Assert.Empty(diff.Objects);
    }

    [Fact]
    public void An_entry_that_the_run_appends_to_a_list_of_a_location_is_one_path_by_its_index()
    {
        var woods = Object(Diff(), "location", "woods");

        var spawn = Assert.Single(woods.Template.Changes, change => change.Path == "/looseLoot/spawnpoints/1");
        Assert.Equal((ModChangePathOperation.Set, null), (spawn.Op, spawn.OldValue));

        // The item that the spawn point names is named by its address and not by the id the run gave it.
        Assert.Contains($"\"_tpl\":\"#{ItemD}\"", spawn.NewValue);
        Assert.DoesNotContain(ObjectTree.AddedItemId, spawn.NewValue);
    }

    [Fact]
    public void A_location_that_the_run_adds_is_named_by_its_directory_and_keeps_every_file()
    {
        var diff = Diff();
        var address = ContentUlid.Create("location", "arena_airpit");

        var airpit = Object(diff, "location", address);

        Assert.Equal((ModTemplateOperation.Added, "arena_airpit"), (airpit.Template.Op, airpit.Template.RawId));
        Assert.True(ContentUlid.IsContentUlid(address));

        // One row for each file, as the run wrote it (ADR-0002).
        Assert.Equal(["base", "looseLoot"], airpit.Parts.Select(part => part.Part));
        Assert.Equal("""{"_Id":"arena_airpit","Name":"Airpit","Insurance":false}""", airpit.Parts[0].Data);

        // The directory name is the identity, and the map gives the way back from it to the address.
        Assert.Contains(diff.IdMap, entry => entry == new Addressing.AddressEntry("location", "arena_airpit", address, false));
    }

    [Fact]
    public void The_file_at_the_top_of_the_locations_is_the_one_object_locations()
    {
        var table = Object(Diff(), "location", "locations");

        var path = Assert.Single(table.Template.Changes);
        Assert.Equal("/base/paths/1", path.Path);
        Assert.Contains("\"Destination\":\"arena_airpit\"", path.NewValue);
    }

    // ---- A bot type -----------------------------------------------------------------------------

    [Fact]
    public void A_changed_value_of_a_bot_type_names_the_file_and_the_value()
    {
        var assault = Object(Diff(), "bot", "types/assault");

        var scope = Assert.Single(assault.Template.Changes, change => change.Path == "/chances/weaponMods/mod_scope");
        Assert.Equal((ModChangePathOperation.Set, "50", "80"), (scope.Op, scope.OldValue, scope.NewValue));
    }

    [Fact]
    public void A_key_that_names_an_item_the_run_minted_is_written_as_its_address()
    {
        var assault = Object(Diff(), "bot", "types/assault");

        var backpack = Assert.Single(assault.Template.Changes, change => change.Path.StartsWith("/inventory/items/Backpack/", StringComparison.Ordinal));
        Assert.Equal($"/inventory/items/Backpack/#{ItemD}", backpack.Path);
        Assert.DoesNotContain(ObjectTree.AddedItemId, backpack.Path);
    }

    [Fact]
    public void A_bot_file_that_is_no_type_is_named_by_its_path_under_bots()
    {
        var core = Object(Diff(), "bot", "core");

        var path = Assert.Single(core.Template.Changes);
        Assert.Equal(("/SAVAGE_KILL_DIST", "100", "150"), (path.Path, path.OldValue, path.NewValue));
    }

    [Fact]
    public void A_bot_type_that_the_run_adds_is_named_by_its_file_name()
    {
        var diff = Diff();
        var address = ContentUlid.Create("bot", "types/newbot");

        var added = Object(diff, "bot", address);

        Assert.Equal(ModTemplateOperation.Added, added.Template.Op);
        Assert.Equal(string.Empty, Assert.Single(added.Parts).Part);
        Assert.Contains(diff.IdMap, entry => entry.Kind == "bot" && entry.RawId == "types/newbot" && entry.Address == address);
    }

    // ---- A hideout part -------------------------------------------------------------------------

    [Fact]
    public void A_hideout_part_that_the_baseline_holds_is_named_by_its_id()
    {
        var diff = Diff();

        var area = Object(diff, "hideout", ObjectTree.AreaId);
        var enabled = Assert.Single(area.Template.Changes);
        Assert.Equal(("/enabled", "true", "false"), (enabled.Path, enabled.OldValue, enabled.NewValue));

        var recipe = Object(diff, "hideout", ObjectTree.RecipeId);
        var count = Assert.Single(recipe.Template.Changes);
        Assert.Equal(("/requirements/0/count", "2", "3"), (count.Path, count.OldValue, count.NewValue));
    }

    [Fact]
    public void A_hideout_part_that_the_run_adds_is_named_by_what_it_is()
    {
        var diff = Diff();

        var added = Assert.Single(diff.Objects, row => row.Template.Kind == "hideout" && row.Template.Op == ModTemplateOperation.Added);

        Assert.True(ContentUlid.IsContentUlid(added.Template.TemplateId));
        Assert.Equal(ObjectTree.AddedRecipeId, added.Template.RawId);
        Assert.Contains(
            diff.IdMap,
            entry => entry == new Addressing.AddressEntry("hideout", ObjectTree.AddedRecipeId, added.Template.TemplateId, false));
    }

    [Fact]
    public void A_hideout_element_with_no_id_is_named_by_its_content_and_the_run_says_so()
    {
        var run = ObjectTree.RunOverBaseline();
        run["hideout/areas.json"] = """[{"_id":"e1e1e1e1e1e1e1e1e1e1e1e1","type":1,"enabled":true,"requirements":[]},{"type":99}]""";

        var diff = Diff(ObjectTree.Baseline(), run);

        var added = Assert.Single(diff.Objects, row => row.Template.Kind == "hideout" && row.Template.Op == ModTemplateOperation.Added && row.Template.RawId.StartsWith("#content:", StringComparison.Ordinal));

        Assert.True(ContentUlid.IsContentUlid(added.Template.TemplateId));
        Assert.Contains(diff.Warnings, warning => warning.Contains("has no id", StringComparison.Ordinal));

        // A name made of content is not an id of the run, so the id map has no row for it.
        Assert.DoesNotContain(diff.IdMap, entry => entry.Kind == "hideout" && entry.RawId.StartsWith("#content:", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_hideout_elements_with_one_id_keep_both_and_the_run_says_so()
    {
        var run = ObjectTree.RunOverBaseline();
        run["hideout/areas.json"] = """[{"_id":"e1e1e1e1e1e1e1e1e1e1e1e1","type":1,"enabled":true,"requirements":[]},{"_id":"e9e9e9e9e9e9e9e9e9e9e9e9","type":5},{"_id":"e9e9e9e9e9e9e9e9e9e9e9e9","type":6}]""";

        var diff = Diff(ObjectTree.Baseline(), run);

        var added = diff.Objects
            .Where(row => row.Template.Kind == "hideout" && row.Template.RawId.StartsWith("e9e9e9e9e9e9e9e9e9e9e9e9", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, added.Length);
        Assert.NotEqual(added[0].Template.TemplateId, added[1].Template.TemplateId);
        Assert.Contains(diff.Warnings, warning => warning.Contains("more than once", StringComparison.Ordinal));
    }

    // ---- A price and a handbook entry -----------------------------------------------------------

    [Fact]
    public void A_price_is_named_by_the_item_it_prices()
    {
        var diff = Diff();

        var changed = Object(diff, "price", ObjectTree.ItemId);
        var whole = Assert.Single(changed.Template.Changes);

        // The value is the whole object, so its pointer is the root.
        Assert.Equal((string.Empty, ModChangePathOperation.Set, "1000", "1200"), (whole.Path, whole.Op, whole.OldValue, whole.NewValue));

        var added = Object(diff, "price", ItemD);
        Assert.Equal(ModTemplateOperation.Added, added.Template.Op);
        Assert.Equal("4000", Assert.Single(added.Parts).Data);

        // The item has an id map row already, and a price does not repeat it.
        Assert.DoesNotContain(diff.IdMap, entry => entry.Kind == "price");
    }

    [Fact]
    public void A_handbook_entry_names_its_category_by_address()
    {
        var diff = Diff();

        var price = Object(diff, "handbook", ObjectTree.ItemId);
        var change = Assert.Single(price.Template.Changes);
        Assert.Equal(("/Price", "1000", "1200"), (change.Path, change.OldValue, change.NewValue));

        var category = Assert.Single(diff.Objects, row => row.Template.Kind == "handbook" && row.Template.RawId == ObjectTree.AddedCategoryId);
        Assert.True(ContentUlid.IsContentUlid(category.Template.TemplateId));
        Assert.Equal(ModTemplateOperation.Added, category.Template.Op);

        var entry = Object(diff, "handbook", ItemD);
        Assert.Equal(ModTemplateOperation.Added, entry.Template.Op);
        Assert.Equal(category.Template.TemplateId, entry.Template.ParentId);
        Assert.Contains($"\"Price\":4000", Assert.Single(entry.Parts).Data);

        // The category has no id map row of an item; the entry of an item has none of its own.
        Assert.Contains(diff.IdMap, row => row.Kind == "handbook" && row.RawId == ObjectTree.AddedCategoryId);
        Assert.DoesNotContain(diff.IdMap, row => row.Kind == "handbook" && row.RawId == ObjectTree.AddedItemId);
    }

    // ---- A boot that mints other ids ------------------------------------------------------------

    [Fact]
    public void A_boot_that_mints_another_id_for_an_item_changes_no_address_no_path_and_no_value()
    {
        const string Minted = "ffffffffffffffffffffffff";

        var first = Diff();

        var again = ObjectTree.RunOverBaseline()
            .ToDictionary(pair => pair.Key, pair => pair.Value.Replace(ObjectTree.AddedItemId, Minted, StringComparison.Ordinal));
        var second = Diff(ObjectTree.Baseline(), again, Path.Combine(_root, "second"));

        Assert.Equal(Describe(first), Describe(second));

        // The id map is the one place that names the id of the run, and it does.
        Assert.Contains(second.IdMap, entry => entry.RawId == Minted && entry.Address == ItemD);
        Assert.DoesNotContain(second.IdMap, entry => entry.RawId == ObjectTree.AddedItemId);
    }

    [Fact]
    public void A_hideout_recipe_that_the_run_names_by_another_id_keeps_its_address()
    {
        const string Minted = "a9a9a9a9a9a9a9a9a9a9a9a9";

        var first = Diff();

        var again = ObjectTree.RunOverBaseline()
            .ToDictionary(pair => pair.Key, pair => pair.Value.Replace(ObjectTree.AddedRecipeId, Minted, StringComparison.Ordinal));
        var second = Diff(ObjectTree.Baseline(), again, Path.Combine(_root, "second"));

        var before = Assert.Single(first.Objects, row => row.Template.Kind == "hideout" && row.Template.Op == ModTemplateOperation.Added);
        var after = Assert.Single(second.Objects, row => row.Template.Kind == "hideout" && row.Template.Op == ModTemplateOperation.Added);

        Assert.Equal(before.Template.TemplateId, after.Template.TemplateId);
        Assert.Equal((ObjectTree.AddedRecipeId, Minted), (before.Template.RawId, after.Template.RawId));
    }

    [Fact]
    public void The_content_hash_of_an_unstored_file_still_holds_the_id_the_run_minted()
    {
        // A known limit, not a wish (TD-60). The canonical hash of a file with no table of its own writes
        // the ids of the run as they are, so a mod that mints an id on every boot moves the content hash
        // of its own dataset. The rows above do not have this fault. Changing the hash would make every
        // stored dataset a stranger to its own content, so it needs the driver (ADR-0041, question 1).
        const string Minted = "ffffffffffffffffffffffff";

        var first = DatasetHash.Compute(Diff(), Files);

        var again = ObjectTree.RunOverBaseline()
            .ToDictionary(pair => pair.Key, pair => pair.Value.Replace(ObjectTree.AddedItemId, Minted, StringComparison.Ordinal));
        var second = DatasetHash.Compute(Diff(ObjectTree.Baseline(), again, Path.Combine(_root, "second")), Files);

        Assert.NotEqual(first, second);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static ObjectContribution Object(RunContribution diff, string kind, string templateId) =>
        Assert.Single(diff.Objects, row => row.Template.Kind == kind && row.Template.TemplateId == templateId);

    /// <summary>Everything a reader sees of the rows, with no id the run gave in it.</summary>
    private static string Describe(RunContribution diff) =>
        JsonSerializer.Serialize(diff.Objects.Select(row => new
        {
            row.Template.Kind,
            row.Template.TemplateId,
            row.Template.Op,
            row.Template.ParentId,
            Parts = row.Parts.Select(part => part.Part),
            Changes = row.Template.Changes.Select(change => new { change.Path, change.Op, change.OldValue, change.NewValue }),
        }));

    private RunContribution Diff() => Diff(ObjectTree.Baseline(), ObjectTree.RunOverBaseline());

    private RunContribution Diff(Dictionary<string, string> baseline, Dictionary<string, string> run, string? root = null)
    {
        root ??= _root;
        var baselineRoot = Path.Combine(root, "baseline");
        var runRoot = Path.Combine(root, "run");

        ObjectTree.Write(baselineRoot, baseline);
        ObjectTree.Write(runRoot, run);

        return RunDiff.Compute(runRoot, baselineRoot);
    }
}
