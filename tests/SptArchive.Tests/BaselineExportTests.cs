using SptArchive.Archiver.Runs;

namespace SptArchive.Tests;

/// <summary>
/// The baseline is the server without the mod (ADR-0013), and the pairing with the run is checked
/// before any table is read: a baseline of another release would report the release as a mod change.
/// </summary>
public sealed class BaselineExportTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "runs", name);

    [Fact]
    public void A_baseline_of_the_same_release_is_accepted_and_verifies_the_release()
    {
        var baseline = BaselineExport.Read(Fixture("baseline"), "4.1.6");

        Assert.True(baseline.ReleaseVerified);
        Assert.Equal("4.1.6", baseline.Run.ServerRelease);

        // A baseline has no target: the exporter and the dependencies loaded, nothing else.
        Assert.DoesNotContain(
            baseline.Run.Mods,
            mod => !mod.IsTool && mod.ModGuid != "com.massivesoft.massivesoftcore" && mod.ModGuid != "com.wtt.commonlib");
    }

    [Fact]
    public void A_baseline_of_another_release_is_refused_by_naming_both_releases()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => BaselineExport.Read(Fixture("baseline-415"), "4.1.6"));

        Assert.Contains("4.1.5", error.Message);
        Assert.Contains("4.1.6", error.Message);
        Assert.Contains("not the work of a mod", error.Message);
    }

    [Fact]
    public void A_baseline_of_an_older_exporter_is_used_and_says_that_it_verified_nothing()
    {
        // LotsOfLootRedux was exported before exporter 1.3.0, so its run/ states no release.
        var baseline = BaselineExport.Read(Fixture("LotsOfLootRedux"), "4.1.6");

        Assert.False(baseline.ReleaseVerified);
        Assert.Null(baseline.Run.ServerRelease);
    }

    [Fact]
    public void A_path_that_is_not_a_directory_names_the_setting_to_correct()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => BaselineExport.Read(Fixture("no-such-baseline"), "4.1.6"));

        Assert.Contains("Archive:BaselinePath", error.Message);
        Assert.Contains("docs/runbooks/mod-export.md", error.Message);
    }

    [Fact]
    public void A_baseline_that_is_a_run_without_an_account_of_itself_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sptarchive-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => BaselineExport.Read(directory, "4.1.6"));

            Assert.Contains("run/loaded.json", error.Message);
            Assert.Contains("export the run again", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
