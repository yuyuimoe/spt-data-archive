using SptArchive.Archiver.Runs;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// A run reads its own account of itself (ADR-0029): which mods loaded, the files each one shipped
/// with their hashes, the hashes from before the server started, and whether a person changed the
/// configuration. The fixtures are the <c>run/</c> directories of the real runs of 4.1.6.
/// </summary>
public sealed class RunDirectoryTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "runs");

    [Fact]
    public void The_run_states_the_mods_it_loaded_and_the_version_each_one_declared()
    {
        var run = Read("ECOTI");

        var target = run.Target("com.lennoxp90.coti.server");

        Assert.Equal("ECOTI", target.Directory);
        Assert.Equal("3.4.0", target.Version);
        Assert.Equal(">=4.1.3 <4.2.0", target.DeclaredSptVersion);
        Assert.Equal("LennoxP90", target.Author);
        Assert.False(target.HasPrepatcher);

        // The exporter and the dependency loaded too, and neither is the target (ADR-0013, ADR-0021).
        Assert.Equal(4, run.Mods.Count);
        Assert.Contains(run.Mods, mod => mod.IsTool);
        Assert.Contains(run.Mods, mod => mod.Directory == "WTT-ServerCommonLib");
    }

    [Fact]
    public void The_exporter_is_not_part_of_the_dependency_set()
    {
        var run = Read("ECOTI");

        var json = RunDirectory.DependencySetJson(run.Mods);

        Assert.DoesNotContain(RunDirectory.ToolModGuid, json, StringComparison.Ordinal);
        Assert.Contains("com.wtt.commonlib", json, StringComparison.Ordinal);
        Assert.Contains("3.0.6", json, StringComparison.Ordinal);

        // Sorted by GUID and free of the exporter's own entry, so two runs of the same set hash equally.
        Assert.Equal(json, RunDirectory.DependencySetJson(run.Mods.Reverse().ToList()));
        Assert.Equal(64, RunDirectory.HashDependencySet(json).Length);
    }

    [Fact]
    public void A_manifest_entry_states_the_file_its_hash_and_what_it_is()
    {
        var run = Read("LotsOfLootRedux");
        var target = run.Target("wtf.archangel.lotsoflootredux");

        var files = run.FilesFor(target, Guid.CreateVersion7());

        // The path of a manifest entry is relative to the mod directory; the stored one starts with it.
        var preset = Assert.Single(files, file => file.Path == "LotsOfLootRedux/Presets/default.json");
        Assert.Equal(ModFileKind.Config, preset.Kind);
        Assert.Equal(5294, preset.Size);
        Assert.Equal("cc9c900dc159b32be295e6857ab3554817a3c48599e7edb9879f8d6625d84154", preset.Sha256);

        var assembly = Assert.Single(files, file => file.Kind == ModFileKind.Assembly);
        Assert.Equal("LotsOfLootRedux/LotsofLoot.dll", assembly.Path);
        Assert.All(files, file => Assert.StartsWith("LotsOfLootRedux/", file.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void A_configuration_file_is_stored_as_text_and_as_json()
    {
        var run = Read("LotsOfLootRedux-Curated");
        var target = run.Target("wtf.archangel.lotsoflootredux");

        var files = run.FilesFor(target, Guid.CreateVersion7());
        var preset = Assert.Single(files, file => file.Path == "LotsOfLootRedux/Presets/default.json");

        Assert.NotNull(preset.ContentText);
        Assert.Contains("\"AllowLootOverlay\": true", preset.ContentText, StringComparison.Ordinal);
        Assert.Equal(preset.ContentText, preset.Content);

        // A .dll has no text and no jsonb, whatever its bytes say.
        var assembly = Assert.Single(files, file => file.Kind == ModFileKind.Assembly);
        Assert.Null(assembly.ContentText);
        Assert.Null(assembly.Content);
    }

    [Fact]
    public void A_curated_run_states_that_a_person_changed_the_configuration()
    {
        var shipped = Read("LotsOfLootRedux");
        var curated = Read("LotsOfLootRedux-Curated");

        Assert.Equal(ModConfigOrigin.Shipped, shipped.Origin.ConfigOrigin);
        Assert.Null(shipped.Origin.Reason);

        Assert.Equal(ModConfigOrigin.Curated, curated.Origin.ConfigOrigin);
        Assert.Contains("AllowLootOverlay", curated.Origin.Reason!, StringComparison.Ordinal);

        // The two datasets differ in one file, and its text is what differs (ADR-0029, decision 7).
        var shippedPreset = Assert.Single(
            shipped.FilesFor(shipped.Target("wtf.archangel.lotsoflootredux"), Guid.CreateVersion7()),
            file => file.Path == "LotsOfLootRedux/Presets/default.json");
        var curatedPreset = Assert.Single(
            curated.FilesFor(curated.Target("wtf.archangel.lotsoflootredux"), Guid.CreateVersion7()),
            file => file.Path == "LotsOfLootRedux/Presets/default.json");

        Assert.NotEqual(shippedPreset.Sha256, curatedPreset.Sha256);
        Assert.NotEqual(shippedPreset.ContentText, curatedPreset.ContentText);
    }

    [Fact]
    public void A_file_the_mod_did_not_rewrite_has_one_hash_from_before_the_start_and_after_it()
    {
        var run = Read("ECOTI");

        var files = run.FilesFor(run.Target("com.lennoxp90.coti.server"), Guid.CreateVersion7());

        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.NotNull(file.ShippedSha256));
        Assert.All(files, file => Assert.Equal(file.Sha256, file.ShippedSha256));
    }

    [Fact]
    public void A_run_without_an_account_of_itself_is_rejected_instead_of_guessed()
    {
        var path = Path.Combine(Root, "ECOTI", "not-a-run");

        var error = Assert.Throws<InvalidOperationException>(() => RunDirectory.Read(path));

        Assert.Contains("run/loaded.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_is_never_the_target()
    {
        var run = Read("ECOTI");

        var error = Assert.Throws<InvalidOperationException>(() => run.Target("com.nobody.missing"));

        // The message names what did load, so a wrong GUID is one line of output and not a hunt.
        Assert.Contains("com.lennoxp90.coti.server 3.4.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("dependency is never the target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_states_the_release_the_server_had()
    {
        // The exporter reads it from the server's own assembly, so the release is a fact of the run
        // and not of the directory name (TD-26).
        var run = Read("baseline");

        Assert.Equal("4.1.6", run.ServerRelease);
    }

    [Fact]
    public void A_run_of_an_older_exporter_states_no_release()
    {
        // Exported before exporter 1.3.0: the reader knows no release rather than assuming one.
        var run = Read("ECOTI");

        Assert.Null(run.ServerRelease);
    }

    private static RunDirectory Read(string name) => RunDirectory.Read(Path.Combine(Root, name));
}
