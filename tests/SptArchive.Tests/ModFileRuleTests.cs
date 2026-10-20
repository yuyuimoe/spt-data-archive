using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// Decision 2 of ADR-0029: what a configuration file is. The rule lives in <see cref="ModFileKind"/>
/// and is compiled into the exporter and into the Archiver, so this test covers both.
/// </summary>
public sealed class ModFileRuleTests
{
    [Theory]
    [InlineData("config/config.json", "A config directory names the file whatever the file is called.")]
    [InlineData("configs/anything.json", "The plural of the directory counts too.")]
    [InlineData("Presets/default.json", "A value mod keeps its preset in a Presets directory (ADR-0026).")]
    [InlineData("loader.json", "The loader of a value mod.")]
    [InlineData("config.json", "The name carries the word.")]
    [InlineData("db/Items/CustomWeaponPresets.json", "A db fragment whose name says preset.")]
    [InlineData("mod/ServerSettings.json", "A name with settings.")]
    [InlineData("options.json", "A name with option.")]
    [InlineData("mapping.jsonc", "A file with comments is a file a person edits.")]
    [InlineData("config/riser-sights.json", "ECOTI reads a deny list from its config directory.")]
    public void A_configuration_file_is_recognized(string path, string because)
    {
        Assert.True(ModFileKind.IsConfigFile(path), because);
        Assert.Equal(ModFileKind.Config, ModFileKind.Classify(path));
    }

    [Theory]
    [InlineData("db/Items/weapons.json")]
    [InlineData("db/locales/en.json")]
    [InlineData("Mod.dll")]
    [InlineData("bundles.json")]
    [InlineData("readme.md")]
    public void Table_data_and_code_are_not_configuration(string path)
    {
        Assert.False(ModFileKind.IsConfigFile(path));
    }

    [Theory]
    [InlineData("Mod.dll", ModFileKind.Assembly)]
    [InlineData("assets/thing.bundle", ModFileKind.Bundle)]
    [InlineData("bundles.json", ModFileKind.Bundle)]
    [InlineData("db/Items/weapons.json", ModFileKind.Data)]
    [InlineData("LICENSE.md", ModFileKind.Text)]
    [InlineData("THIRD-PARTY-NOTICES.txt", ModFileKind.Text)]
    [InlineData("Presets/default.json", ModFileKind.Config)]
    [InlineData("maschine-ArenaMapPort.Server.dll", ModFileKind.Assembly)]
    [InlineData("mod/graphics/shader.nb", ModFileKind.Other)]
    public void A_file_is_classified_by_its_path(string path, string kind)
    {
        Assert.Equal(kind, ModFileKind.Classify(path));
    }

    [Fact]
    public void A_configuration_file_wins_over_the_extension()
    {
        // config/items.json is a configuration file, not table data (ADR-0029, decision 2).
        Assert.Equal(ModFileKind.Config, ModFileKind.Classify("config/items.json"));
        Assert.Equal(ModFileKind.Config, ModFileKind.Classify("loader/loader.json"));
    }
}
