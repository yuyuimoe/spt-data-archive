using SptArchive.Core.Canonical;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The seven files of a run that held no row: <c>globals.json</c>, <c>settings.json</c>,
/// <c>templates/dialogue.json</c>, <c>profiles.json</c>, <c>achievements.json</c>, <c>character.json</c> and
/// <c>customisationStorage.json</c> (ADR-0044). One test for each shape; the trees are tiny.
/// </summary>
public sealed class FileRowTests : IDisposable
{
    private const string ElementId = "683dafc7a7c0a468ac8a9b05";
    private const string PresetId = "5841474424597759ba49be91";
    private const string AchievementId = "674731d1170146228c0d222a";

    private static readonly ContributionFile[] Files = [new("Thermal/Thermal.dll", "0123456789abcdef", null)];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sptarchive-filerows-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- templates/dialogue.json ----------------------------------------------------------------

    [Fact]
    public void A_changed_dialogue_line_is_a_path_under_the_element_that_holds_it()
    {
        var diff = Diff(
            ("templates/dialogue.json", Dialogue("hello")),
            ("templates/dialogue.json", Dialogue("goodbye")));

        var element = Single(diff, ModTemplateKind.Dialogue, ElementId);
        Assert.Equal(ModTemplateOperation.Modified, element.Template.Op);

        var change = Assert.Single(element.Template.Changes);
        Assert.StartsWith("/Lines/", change.Path, StringComparison.Ordinal);
        Assert.Contains("hello", change.OldValue, StringComparison.Ordinal);
        Assert.Contains("goodbye", change.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dialogue_element_that_the_run_adds_is_a_row_with_its_source()
    {
        var added = """{"elements":[{"Id":"683dafc7a7c0a468ac8a9b05","Trader":"t","Lines":[{"Id":"l1","Trigger":"hello"}]},{"Id":"aaaaaaaaaaaaaaaaaaaaaaaa","Trader":"t","Lines":[]}]}""";
        var diff = Diff(("templates/dialogue.json", Dialogue("hello")), ("templates/dialogue.json", added));

        var row = Assert.Single(diff.Objects, o => o.Template.Op == ModTemplateOperation.Added);
        Assert.Equal(ModTemplateKind.Dialogue, row.Template.Kind);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", row.Template.RawId);
        Assert.Contains("\"Trader\"", Assert.Single(row.Parts).Data, StringComparison.Ordinal);
    }

    // ---- globals.json ---------------------------------------------------------------------------

    [Fact]
    public void A_changed_item_preset_is_one_row_named_by_the_preset_id()
    {
        var diff = Diff(("globals.json", Globals(price: 1)), ("globals.json", Globals(price: 2)));

        var preset = Single(diff, ModTemplateKind.Preset, PresetId);
        Assert.Equal(ModTemplateOperation.Modified, preset.Template.Op);

        var change = Assert.Single(preset.Template.Changes);
        Assert.Equal(("/price", "1", "2"), (change.Path, change.OldValue, change.NewValue));

        // Nothing else of the file moved, so nothing else is a row.
        Assert.Single(diff.Objects);
    }

    [Fact]
    public void A_bot_preset_is_named_by_its_role_and_difficulty_and_a_scattering_by_its_name()
    {
        var baseline = """{"config":{"a":1},"LocationInfection":{"Lighthouse":1},"bot_presets":[{"Role":"assault","BotDifficulty":"easy","VisibleAngle":10},{"Role":"assault","BotDifficulty":"hard","VisibleAngle":10}],"BotWeaponScatterings":[{"Name":"Default","Scatter":1}],"ItemPresets":{}}""";
        var run = """{"config":{"a":1},"LocationInfection":{"Lighthouse":1},"bot_presets":[{"Role":"assault","BotDifficulty":"easy","VisibleAngle":20},{"Role":"assault","BotDifficulty":"hard","VisibleAngle":10}],"BotWeaponScatterings":[{"Name":"Default","Scatter":3}],"ItemPresets":{}}""";
        var diff = Diff(("globals.json", baseline), ("globals.json", run));

        Assert.Equal("/VisibleAngle", Assert.Single(Single(diff, ModTemplateKind.BotPreset, "assault/easy").Template.Changes).Path);
        Assert.Equal("/Scatter", Assert.Single(Single(diff, ModTemplateKind.Scattering, "Default").Template.Changes).Path);
        Assert.Equal(2, diff.Objects.Count);
    }

    [Fact]
    public void An_object_with_no_list_in_globals_stays_one_row_and_a_key_is_a_path()
    {
        var baseline = """{"config":{"RagFair":{"enabled":true}},"LocationInfection":{"Lighthouse":1},"bot_presets":[],"BotWeaponScatterings":[],"ItemPresets":{}}""";
        var run = """{"config":{"RagFair":{"enabled":false}},"LocationInfection":{"Lighthouse":2},"bot_presets":[],"BotWeaponScatterings":[],"ItemPresets":{}}""";
        var diff = Diff(("globals.json", baseline), ("globals.json", run));

        Assert.Equal("/RagFair/enabled", Assert.Single(Single(diff, ModTemplateKind.Global, "config").Template.Changes).Path);
        Assert.Equal("/Lighthouse", Assert.Single(Single(diff, ModTemplateKind.Global, "LocationInfection").Template.Changes).Path);
    }

    [Fact]
    public void A_preset_that_the_run_adds_is_a_row_and_a_preset_that_it_removes_is_a_row()
    {
        var baseline = """{"config":{},"LocationInfection":{},"bot_presets":[],"BotWeaponScatterings":[],"ItemPresets":{"p1":{"price":1},"p2":{"price":2}}}""";
        var run = """{"config":{},"LocationInfection":{},"bot_presets":[],"BotWeaponScatterings":[],"ItemPresets":{"p1":{"price":1},"p3":{"price":3}}}""";
        var diff = Diff(("globals.json", baseline), ("globals.json", run));

        Assert.Equal(ModTemplateOperation.Removed, Single(diff, ModTemplateKind.Preset, "p2").Template.Op);

        var added = Assert.Single(diff.Objects, o => o.Template.Op == ModTemplateOperation.Added);
        Assert.Equal("p3", added.Template.RawId);
        Assert.Equal("""{"price":3}""", Assert.Single(added.Parts).Data);
    }

    // ---- settings.json --------------------------------------------------------------------------

    [Fact]
    public void A_settings_file_is_one_object_and_a_changed_key_is_a_path()
    {
        var diff = Diff(("settings.json", """{"config":{"time":1}}"""), ("settings.json", """{"config":{"time":2}}"""));

        var row = Assert.Single(diff.Objects);
        Assert.Equal(ModTemplateKind.Setting, row.Template.Kind);

        var change = Assert.Single(row.Template.Changes);
        Assert.Equal(("/config/time", "1", "2"), (change.Path, change.OldValue, change.NewValue));
    }

    // ---- templates/profiles.json ----------------------------------------------------------------

    [Fact]
    public void A_profile_edition_is_one_row_named_by_the_edition()
    {
        var baseline = """{"Standard":{"descriptionLocaleKey":"a","usec":{"character":{"Level":1}}},"Edge Of Darkness":{"descriptionLocaleKey":"b"}}""";
        var run = """{"Standard":{"descriptionLocaleKey":"a","usec":{"character":{"Level":5}}},"Edge Of Darkness":{"descriptionLocaleKey":"b"}}""";
        var diff = Diff(("templates/profiles.json", baseline), ("templates/profiles.json", run));

        var row = Single(diff, ModTemplateKind.Profile, "Standard");
        Assert.Equal("/usec/character/Level", Assert.Single(row.Template.Changes).Path);
        Assert.Single(diff.Objects);
    }

    // ---- templates/achievements.json and customisationStorage.json -------------------------------

    [Fact]
    public void A_changed_achievement_is_a_path_under_its_id_and_an_added_one_is_a_row()
    {
        var baseline = """[{"id":"674731d1170146228c0d222a","index":1,"rewards":[]}]""";
        var run = """[{"id":"674731d1170146228c0d222a","index":2,"rewards":[]},{"id":"bbbbbbbbbbbbbbbbbbbbbbbb","index":3,"rewards":[]}]""";
        var diff = Diff(("templates/achievements.json", baseline), ("templates/achievements.json", run));

        Assert.Equal("/index", Assert.Single(Single(diff, ModTemplateKind.Achievement, AchievementId).Template.Changes).Path);

        var added = Assert.Single(diff.Objects, o => o.Template.Op == ModTemplateOperation.Added);
        Assert.Equal(("bbbbbbbbbbbbbbbbbbbbbbbb", ModTemplateKind.Achievement), (added.Template.RawId, added.Template.Kind));
        Assert.Single(added.Parts);
    }

    [Fact]
    public void An_entry_of_the_customisation_storage_that_the_run_adds_is_a_row()
    {
        var baseline = """[{"id":"674731d1170146228c0d222a","source":"default","type":"dogTag"}]""";
        var run = """[{"id":"674731d1170146228c0d222a","source":"default","type":"dogTag"},{"id":"cccccccccccccccccccccccc","source":"default","type":"suite"}]""";
        var diff = Diff(("templates/customisationStorage.json", baseline), ("templates/customisationStorage.json", run));

        var added = Assert.Single(diff.Objects);
        Assert.Equal((ModTemplateKind.Storage, "cccccccccccccccccccccccc", ModTemplateOperation.Added), (added.Template.Kind, added.Template.RawId, added.Template.Op));
    }

    // ---- templates/character.json ---------------------------------------------------------------

    [Fact]
    public void A_string_of_the_character_list_is_a_row_named_by_the_string()
    {
        var diff = Diff(
            ("templates/character.json", """["Usec","Bear"]"""),
            ("templates/character.json", """["Usec","Bear","NewVoice"]"""));

        var added = Assert.Single(diff.Objects);
        Assert.Equal((ModTemplateKind.Character, "NewVoice", ModTemplateOperation.Added), (added.Template.Kind, added.Template.RawId, added.Template.Op));
        Assert.Equal("\"NewVoice\"", Assert.Single(added.Parts).Data);
    }

    // ---- A name that is empty or used twice -------------------------------------------------------

    [Fact]
    public void Two_objects_with_one_id_keep_both_by_their_order_and_the_run_says_so()
    {
        var baseline = """[{"id":"674731d1170146228c0d222a","index":1}]""";
        var run = """[{"id":"674731d1170146228c0d222a","index":1},{"id":"dupdupdupdupdupdupdupdup","index":2},{"id":"dupdupdupdupdupdupdupdup","index":3}]""";
        var diff = Diff(("templates/achievements.json", baseline), ("templates/achievements.json", run));

        Assert.Equal(2, diff.Objects.Count(o => o.Template.Op == ModTemplateOperation.Added));
        Assert.Contains(diff.Warnings, w => w.StartsWith("templates/achievements.json:", StringComparison.Ordinal) && w.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void An_object_with_no_name_is_named_by_its_content_and_the_run_says_so()
    {
        var baseline = """{"config":{},"LocationInfection":{},"bot_presets":[],"BotWeaponScatterings":[],"ItemPresets":{}}""";
        var run = """{"config":{},"LocationInfection":{},"bot_presets":[{"VisibleAngle":5}],"BotWeaponScatterings":[],"ItemPresets":{}}""";
        var diff = Diff(("globals.json", baseline), ("globals.json", run));

        var added = Assert.Single(diff.Objects);
        Assert.StartsWith("#content:", added.Template.RawId, StringComparison.Ordinal);
        Assert.Contains(diff.Warnings, w => w.StartsWith("globals.json:", StringComparison.Ordinal) && w.Contains("no name", StringComparison.Ordinal));
    }

    // ---- A file that stays without a row, and the hash that does not move ------------------------

    [Fact]
    public void A_file_that_no_reader_knows_stays_a_file_with_no_row()
    {
        var diff = Diff(("templates/anUnknownTable.json", """{"time":1}"""), ("templates/anUnknownTable.json", """{"time":2}"""));

        Assert.Equal("templates/anUnknownTable.json", Assert.Single(diff.FilesWithoutRows));
        Assert.Empty(diff.Objects);
    }

    [Fact]
    public void A_file_that_the_run_leaves_unchanged_makes_no_row()
    {
        var diff = Diff(("globals.json", Globals(1)), ("globals.json", Globals(1)));

        Assert.Empty(diff.Objects);
        Assert.Empty(diff.UnstoredChanges);
    }

    [Fact]
    public void The_content_hash_of_a_run_that_changes_globals_is_the_value_it_had_before_the_rows()
    {
        // Computed by RunDiff and DatasetHash at 7f60f8c, before globals.json held a row (ADR-0044, decision 6).
        var diff = Diff(("globals.json", Globals(1)), ("globals.json", Globals(2)));

        Assert.Contains("globals.json", diff.UnstoredChanges.Keys);
        Assert.Equal("4015e9aa6c7428bf1cc066b35d0667d59498e42ee040416f274f81369e065347", DatasetHash.Compute(diff, Files));
        Assert.Equal(DatasetHash.Compute(diff with { Objects = [] }, Files), DatasetHash.Compute(diff, Files));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static ObjectContribution Single(RunContribution diff, string kind, string templateId) =>
        Assert.Single(diff.Objects, row => row.Template.Kind == kind && row.Template.TemplateId == templateId);

    private static string Dialogue(string trigger) =>
        "{\"elements\":[{\"Id\":\"" + ElementId + "\",\"Trader\":\"t\",\"StartPoints\":{\"a\":1},\"Lines\":[{\"Id\":\"l1\",\"Trigger\":\"" + trigger + "\"}]}]}";

    private static string Globals(int price) =>
        "{\"config\":{\"a\":1},\"LocationInfection\":{\"Lighthouse\":1},\"bot_presets\":[],\"BotWeaponScatterings\":[],\"ItemPresets\":{\""
        + PresetId + "\":{\"_id\":\"" + PresetId + "\",\"price\":" + price + "}}}";

    /// <summary>A baseline and a run that differ by one file each, laid over the shared object tree.</summary>
    private RunContribution Diff((string Path, string Content) baseline, (string Path, string Content) run)
    {
        var baselineTree = ObjectTree.Baseline();
        baselineTree[baseline.Path] = baseline.Content;

        var runTree = ObjectTree.Baseline();
        runTree[run.Path] = run.Content;

        var baselineRoot = Path.Combine(_root, "baseline");
        var runRoot = Path.Combine(_root, "run");
        ObjectTree.Write(baselineRoot, baselineTree);
        ObjectTree.Write(runRoot, runTree);

        return RunDiff.Compute(runRoot, baselineRoot);
    }
}
