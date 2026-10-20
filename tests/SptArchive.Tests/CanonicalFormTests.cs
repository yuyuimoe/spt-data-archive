using System.Text;
using System.Text.Json;
using SptArchive.Core.Canonical;

namespace SptArchive.Tests;

/// <summary>
/// The canonical form of ADR-0028, against the fixture it names: the two baseline exports of
/// SPT 4.1.6, cut down to the 13 files that differ between them. Every one of those files differs
/// on disk, and none of them differs after canonicalization. The files that are byte-identical in
/// the two runs are not in the fixture, because the canonical form is a function of the bytes.
/// </summary>
public sealed class CanonicalFormTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "canonical");

    public static TheoryData<string> DifferingFiles()
    {
        var data = new TheoryData<string>();
        var baseline = Path.Combine(Root, "baseline");

        foreach (var file in Directory.EnumerateFiles(baseline, "*.json", SearchOption.AllDirectories)
                     .Select(file => Path.GetRelativePath(baseline, file).Replace('\\', '/'))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            data.Add(file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DifferingFiles))]
    public void Two_runs_of_one_release_are_equal_in_the_canonical_form(string relativePath)
    {
        Assert.False(
            File.ReadAllBytes(Path.Combine(Root, "baseline", relativePath))
                .AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(Root, "baseline.run1", relativePath))),
            $"{relativePath} is expected to differ on disk, or the fixture is not the disagreement");

        // An assort is read as offers and not as a document with ids in it (ADR-0032, decision 4).
        if (IsOfferFile(relativePath))
        {
            Assert.Equal(OffersText("baseline", relativePath), OffersText("baseline.run1", relativePath));
            return;
        }

        using var left = Read("baseline", relativePath);
        using var right = Read("baseline.run1", relativePath);

        Assert.Equal(
            CanonicalForm.Canonicalize(relativePath, left.RootElement),
            CanonicalForm.Canonicalize(relativePath, right.RootElement));

        Assert.Equal(
            CanonicalForm.Hash(relativePath, left.RootElement),
            CanonicalForm.Hash(relativePath, right.RootElement));

        Assert.Empty(CanonicalForm.Compare(relativePath, left.RootElement, right.RootElement));
    }

    [Fact]
    public void The_fixture_is_the_thirteen_files_that_differ()
    {
        Assert.Equal(13, DifferingFiles().Count);
    }

    [Fact]
    public void The_instance_ids_of_a_boot_are_not_content()
    {
        // Two boots of one release renew every assort instance id (ADR-0028), so the ids of the two
        // fixtures differ on disk. An offer is named by what it is, and the ids stay out (ADR-0032).
        const string file = "traders/579dc571d53a0658a154fbec/assort.json";

        var baseline = OffersText("baseline", file);
        var other = OffersText("baseline.run1", file);

        Assert.Equal(baseline, other);

        using var source = Read("baseline", file);
        var instanceIds = source.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("_id").GetString()!)
            .ToArray();

        Assert.NotEmpty(instanceIds);
        Assert.All(instanceIds, id => Assert.DoesNotContain(id, baseline));

        // The offers are ordered by address, and each one states the address it is named by.
        using var canonical = JsonDocument.Parse(baseline);
        var offers = canonical.RootElement.GetProperty("offers").EnumerateArray()
            .Select(offer => offer.GetProperty("_id").GetString()!)
            .ToArray();

        Assert.Equal(offers.OrderBy(id => id, StringComparer.Ordinal), offers);
    }

    /// <summary>The canonical form of an assort file: the offers, in the order of their addresses.</summary>
    private static string OffersText(string side, string relativePath)
    {
        var traderId = relativePath.Split('/')[1];
        var root = Path.Combine(Root, side);

        using var assort = Read(side, relativePath);
        var questPath = $"traders/{traderId}/{Assort.QuestAssortFile}";

        using var quests = File.Exists(Path.Combine(root, questPath))
            ? Read(side, questPath)
            : null;

        var read = Assort.Read(
            Addressing.Build(root, root),
            traderId,
            assort.RootElement,
            quests?.RootElement);

        return Path.GetFileName(relativePath) == Assort.QuestAssortFile
            ? read.CanonicalLinks()
            : read.CanonicalOffers();
    }

    private static bool IsOfferFile(string relativePath) =>
        relativePath.StartsWith("traders/", StringComparison.Ordinal)
        && Path.GetFileName(relativePath) is Assort.AssortFile or Assort.QuestAssortFile;

    [Fact]
    public void A_difference_that_no_entry_covers_is_reported()
    {
        using var left = JsonDocument.Parse("""
            {"suits":[{"_id":"6ad78419a7635f000115e858","tid":"5696576f0f6f6a0b4c2f3a1b"}],"nextResupply":1792518932}
            """);
        using var right = JsonDocument.Parse("""
            {"suits":[{"_id":"6ad6d7dabceea800019b9c87","tid":"5696576f0f6f6a0b4c2f3a1b"}],"nextResupply":1792473833}
            """);

        var differences = CanonicalForm.Compare("traders/54cb50c76803fa8b248b4571/base.json", left.RootElement, right.RootElement);

        // The restock clock is on the list. The id of a suit is not, because no run has moved it yet.
        Assert.Equal(["$.suits[0]._id"], differences);
    }

    [Fact]
    public void A_boss_chance_does_not_differ()
    {
        const string file = "locations/lighthouse/base.json";

        using var left = JsonDocument.Parse("""{"BossLocationSpawn":[{"BossName":"bossKilla","BossChance":0}]}""");
        using var right = JsonDocument.Parse("""{"BossLocationSpawn":[{"BossName":"bossKilla","BossChance":35}]}""");

        Assert.Empty(CanonicalForm.Compare(file, left.RootElement, right.RootElement));
        Assert.Equal(
            CanonicalForm.Hash(file, left.RootElement),
            CanonicalForm.Hash(file, right.RootElement));
    }

    [Fact]
    public void A_property_that_only_one_run_has_is_reported_at_its_own_path()
    {
        using var left = JsonDocument.Parse("""{"a":{"b":1},"gone":true}""");
        using var right = JsonDocument.Parse("""{"a":{"b":1}}""");

        Assert.Equal(["$.gone"], CanonicalForm.Compare("templates/items.json", left.RootElement, right.RootElement));
    }

    [Fact]
    public void The_order_of_keys_does_not_change_the_canonical_form()
    {
        using var left = JsonDocument.Parse("""{"b":1,"a":{"d":2,"c":3}}""");
        using var right = JsonDocument.Parse("""{"a":{"c":3,"d":2},"b":1}""");

        Assert.Equal(
            CanonicalForm.Canonicalize("templates/items.json", left.RootElement),
            CanonicalForm.Canonicalize("templates/items.json", right.RootElement));
    }

    [Fact]
    public void The_order_of_an_array_is_data()
    {
        using var left = JsonDocument.Parse("""{"a":[1,2]}""");
        using var right = JsonDocument.Parse("""{"a":[2,1]}""");

        Assert.NotEqual(
            CanonicalForm.Canonicalize("templates/items.json", left.RootElement),
            CanonicalForm.Canonicalize("templates/items.json", right.RootElement));
    }

    [Fact]
    public void The_canonical_form_leaves_the_value_the_server_wrote_alone()
    {
        using var source = JsonDocument.Parse("""{"nextResupply":1792518932,"name":"Prapor"}""");

        var canonical = Encoding.UTF8.GetString(CanonicalForm.Canonicalize("traders/54cb50c76803fa8b248b4571/base.json", source.RootElement));

        Assert.Contains(CanonicalForm.VolatileToken, canonical);
        Assert.DoesNotContain("1792518932", canonical);

        // ADR-0002: the archive stores what the server said. Only the comparison and the hash see the token.
        Assert.Equal(1792518932, source.RootElement.GetProperty("nextResupply").GetInt64());
        Assert.Equal("Prapor", source.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void The_shipped_volatile_list_has_measured_evidence()
    {
        var entries = CanonicalForm.VolatilePaths;

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.File));
            Assert.StartsWith("$.", entry.Path, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(entry.Evidence));
        });

        Assert.Contains(entries, entry => entry.Path == "$.nextResupply");
        Assert.Contains(entries, entry => entry.Path == "$.BossLocationSpawn[*].BossChance");

        // The suit ids of a trader did not move in the two runs, so the list does not carry them (ADR-0028, decision 2).
        Assert.DoesNotContain(entries, entry => entry.Path.Contains("suits", StringComparison.Ordinal));
    }

    private static JsonDocument Read(string run, string relativePath)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, run, relativePath));

        // A JSON file may start with a byte order mark; the exporter does not write one, a mod may.
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;

        return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, offset, bytes.Length - offset));
    }
}
