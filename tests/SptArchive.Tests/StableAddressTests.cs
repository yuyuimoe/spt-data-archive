using System.Text.Json;
using SptArchive.Core.Canonical;

namespace SptArchive.Tests;

/// <summary>
/// The address of a change names the object and not its place in an array (TD-72, ADR-0042 action item 7).
/// The same element at two different indexes is the same address, whatever the mod around it did.
/// </summary>
public sealed class StableAddressTests
{
    private const string File = "templates/items.json";

    private static string Slot(string name, params string[] filter) =>
        "{\"_name\":\"" + name + "\",\"_id\":\"slot_" + name + "\",\"_props\":{\"filters\":[{\"Filter\":[" + string.Join(",", filter.Select(f => "\"" + f + "\"")) + "]}]}}";

    private static string Item(params string[] slots) =>
        "{\"aaaaaaaaaaaaaaaaaaaaaaaa\":{\"_id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"_name\":\"A\",\"_props\":{\"Slots\":[" + string.Join(",", slots) + "]}}}";

    private static IReadOnlyList<string> Pointers(string baseline, string run, List<string>? warnings = null)
    {
        using var left = JsonDocument.Parse(baseline);
        using var right = JsonDocument.Parse(run);
        var differences = CanonicalForm.Compare(File, left.RootElement, right.RootElement, null, warnings);
        return differences.Select(RunDiff.ToPointer).ToArray();
    }

    [Fact]
    public void The_same_element_at_another_index_is_the_same_address()
    {
        // Mod one has the slot first, mod two has it last: the address must not move with the position.
        var first = Pointers(Item(Slot("a", "x"), Slot("b", "x")), Item(Slot("a", "x", "y"), Slot("b", "x")));
        var last = Pointers(Item(Slot("b", "x"), Slot("a", "x")), Item(Slot("b", "x"), Slot("a", "x", "y")));

        Assert.NotEmpty(first);
        Assert.Equal(first.Order(StringComparer.Ordinal), last.Order(StringComparer.Ordinal));
        Assert.All(first, pointer => Assert.Contains("/Slots/slot_a/", pointer, StringComparison.Ordinal));
    }

    [Fact]
    public void A_string_element_is_named_by_its_value()
    {
        var pointers = Pointers(
            """{"aaaaaaaaaaaaaaaaaaaaaaaa":{"_id":"aaaaaaaaaaaaaaaaaaaaaaaa","_props":{"Side":["Usec","Bear"]}}}""",
            """{"aaaaaaaaaaaaaaaaaaaaaaaa":{"_id":"aaaaaaaaaaaaaaaaaaaaaaaa","_props":{"Side":["Bear","Usec","Savage"]}}}""");

        Assert.Equal(["/aaaaaaaaaaaaaaaaaaaaaaaa/_props/Side/Savage"], pointers);
    }

    [Fact]
    public void Two_elements_that_share_an_identity_fall_back_to_the_ordinal_and_warn()
    {
        // Keying by "a" alone would collapse the two slots and lose the change of the second one.
        var warnings = new List<string>();
        var pointers = Pointers(
            """{"k":{"_props":{"Slots":[{"_name":"a","v":1},{"_name":"a","v":2}]}}}""",
            """{"k":{"_props":{"Slots":[{"_name":"a","v":1},{"_name":"a","v":3}]}}}""",
            warnings);

        Assert.Equal(["/k/_props/Slots/1/v"], pointers);
        Assert.Contains(warnings, warning => warning.Contains("share an identity", StringComparison.Ordinal));
    }

    [Fact]
    public void An_element_whose_identity_is_empty_falls_back_to_the_ordinal_and_warns()
    {
        var warnings = new List<string>();
        var pointers = Pointers(
            """{"k":{"Slots":[{"_name":"","v":1}]}}""",
            """{"k":{"Slots":[{"_name":"","v":2}]}}""",
            warnings);

        Assert.Equal(["/k/Slots/0/v"], pointers);
        Assert.Contains(warnings, warning => warning.Contains("no identity", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_with_a_slash_a_dot_or_a_quote_survives_the_pointer()
    {
        var pointers = Pointers(
            """{"k":{"L":["a/b.c'd"]}}""",
            """{"k":{"L":["a/b.c'd","x"]}}""");

        Assert.Equal(["/k/L/x"], pointers);

        var removed = Pointers("""{"k":{"L":["a/b.c'd","x"]}}""", """{"k":{"L":["x"]}}""");
        Assert.Equal(["/k/L/a~1b.c'd"], removed);
    }

    [Fact]
    public void An_element_without_identity_falls_back_to_its_ordinal_and_warns()
    {
        var warnings = new List<string>();
        var pointers = Pointers(
            """{"k":{"_props":{"paths":[{"Source":"a"},{"Source":"b"}]}}}""",
            """{"k":{"_props":{"paths":[{"Source":"a"},{"Source":"c"}]}}}""",
            warnings);

        Assert.Equal(["/k/_props/paths/1/Source"], pointers);
        Assert.Contains(warnings, warning => warning.Contains("no identity", StringComparison.Ordinal));
    }

    [Fact]
    public void An_element_inserted_at_the_start_leaves_the_path_of_every_other_element()
    {
        var strings = Pointers("""{"k":{"L":["a","b","c"]}}""", """{"k":{"L":["z","a","b","c"]}}""");
        Assert.Equal(["/k/L/z"], strings);

        var objects = Pointers(
            """{"k":{"Slots":[{"_name":"a","v":1},{"_name":"b","v":2}]}}""",
            """{"k":{"Slots":[{"_name":"z","v":0},{"_name":"a","v":1},{"_name":"b","v":3}]}}""");
        Assert.Equal(["/k/Slots/b/v", "/k/Slots/z"], objects.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_list_inside_an_element_that_has_an_identity_is_named_by_identity_too()
    {
        var pointers = Pointers(
            Item(Slot("a", "x", "y"), Slot("b", "x")),
            Item(Slot("b", "x"), Slot("a", "y", "x", "w")));

        // filters is a list of objects with no identity field and stays at index 0, with a warning (below).
        Assert.Equal(["/aaaaaaaaaaaaaaaaaaaaaaaa/_props/Slots/slot_a/_props/filters/0/Filter/w"], pointers);
    }

    [Fact]
    public void A_list_that_cannot_be_named_adds_one_warning_for_the_file_and_the_shape()
    {
        var warnings = new List<string>();
        Pointers(
            """{"k":{"A":{"P":[{"s":1},{"s":2}]},"B":{"P":[{"s":1},{"s":2}]}}}""",
            """{"k":{"A":{"P":[{"s":9},{"s":8}]},"B":{"P":[{"s":9},{"s":7}]}}}""",
            warnings);

        // Two arrays differ, at two shapes; each shape is one line, however many elements changed.
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Contains(File, warning, StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_over_a_real_export_names_no_change_of_the_clothes_mod_by_index()
    {
        var root = Environment.GetEnvironmentVariable("SPT_EXPORTS_DIR") ?? "/home/yui/work/sptmods/exports";
        var run = Path.Combine(root, "AllTheClothes");
        var baseline = Path.Combine(root, "baseline");

        Assert.SkipUnless(Directory.Exists(run) && Directory.Exists(baseline), "The real exports are not on this machine.");

        var diff = RunDiff.Compute(run, baseline);
        var paths = diff.Templates.Concat(diff.Objects.Select(row => row.Template)).SelectMany(row => row.Changes).ToArray();

        Assert.NotEmpty(paths);
        // Measured on 2026-10-21: 408 of 454 paths of this run held an index before the keying (TD-72).
        Assert.DoesNotContain(paths, change => change.Path.Split('/').Any(segment => segment.Length > 0 && segment.All(char.IsDigit)));
    }
}
