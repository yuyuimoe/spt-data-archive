using SptArchive.Core.Factions;

namespace SptArchive.Tests;

/// <summary>
/// The faction of a bot type is curation, not a join (ADR-0025). These tests hold the seed to its
/// promise: it covers every bot type file of 4.1.6, it names only factions that exist, and it stays
/// silent about nothing.
/// </summary>
public sealed class FactionTests
{
    /// <summary>The 57 file names under <c>bots/types/</c> of release 4.1.6.</summary>
    private static readonly string[] BotTypesOf416 =
    [
        "arenafighterevent", "assault", "bear", "bossboar", "bossboarsniper", "bossbully", "bossgluhar",
        "bosskillaagro", "bosskilla", "bossknight", "bosskojaniy", "bosskolontay", "bosspartisan",
        "bosssanitar", "bosstagillaagro", "bosstagilla", "bosszryachiy", "crazyassaultevent",
        "cursedassault", "exusec", "followerbigpipe", "followerbirdeye", "followerboarclose1",
        "followerboarclose2", "followerboar", "followerbully", "followergluharassault",
        "followergluharscout", "followergluharsecurity", "followerkojaniy", "followerkolontayassault",
        "followerkolontaysecurity", "followersanitar", "followerzryachiy", "gifter", "infectedassault",
        "infectedcivil", "infectedlaborant", "infectedpmc", "infectedtagilla", "marksman", "peacemaker",
        "pmcbear", "pmcbot", "pmcusec", "ravangezryachiyevent", "sectantoni", "sectantpredvestnik",
        "sectantpriest", "sectantprizrak", "sectantwarrior", "shooterbtr", "skier", "spiritspring",
        "spiritwinter", "tagillahelperagro", "usec",
    ];

    [Fact]
    public void The_seed_covers_every_bot_type_of_the_release()
    {
        var missing = BotTypesOf416.Where(type => !BotFactions.Seed.ContainsKey(type)).ToArray();
        var extra = BotFactions.Seed.Keys.Except(BotTypesOf416, StringComparer.Ordinal).ToArray();

        Assert.Empty(missing);
        Assert.Empty(extra);
        Assert.Equal(57, BotFactions.Seed.Count);
    }

    [Fact]
    public void The_seed_names_only_factions_that_exist()
    {
        var unknown = BotFactions.Seed
            .Where(row => row.Value.FactionId is not null && !BotFactions.Ids.Contains(row.Value.FactionId))
            .Select(row => $"{row.Key} -> {row.Value.FactionId}")
            .ToArray();

        Assert.Empty(unknown);
    }

    [Fact]
    public void Every_row_states_why()
    {
        Assert.All(BotFactions.Seed, row => Assert.False(string.IsNullOrWhiteSpace(row.Value.Evidence)));
    }

    [Fact]
    public void The_four_types_with_no_faction_say_so_and_stay_null()
    {
        var none = BotFactions.Seed
            .Where(row => row.Value.FactionId is null)
            .Select(row => row.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["peacemaker", "skier", "spiritspring", "spiritwinter"], none);
        Assert.All(none, type => Assert.StartsWith("explicit:", BotFactions.Seed[type].Evidence));
        Assert.All(none, type => Assert.Null(BotFactions.Resolve(type).FactionId));
    }

    [Theory]
    [InlineData("exusec", "rogue")]
    [InlineData("pmcbot", "raider")]
    [InlineData("assault", "savage")]
    [InlineData("sectantpriest", "cultist")]
    [InlineData("infectedpmc", "infected")]
    [InlineData("gifter", "gifter")]
    [InlineData("arenafighterevent", "arenafighter")]
    [InlineData("shooterbtr", "btr")]
    [InlineData("bossbully", "bossbully")]
    [InlineData("followerbully", "bossbully")]
    [InlineData("bosskillaagro", "bosskilla")]
    [InlineData("tagillahelperagro", "bosstagilla")]
    public void A_curated_row_resolves_the_type(string botType, string faction)
    {
        var membership = BotFactions.Resolve(botType);

        Assert.Equal(faction, membership.FactionId);
        Assert.Equal(FactionEvidence.Curated, membership.Evidence);
        Assert.True(membership.IsResolved);
    }

    [Fact]
    public void A_follower_resolves_to_its_boss_id_and_not_to_its_prefix()
    {
        // The crew of bossKnight: there is no bossBigpipe, so the prefix cannot find the squad.
        Assert.Equal("bossknight", BotFactions.Resolve("followerbigpipe").FactionId);
        Assert.Equal("bossknight", BotFactions.Resolve("followerBirdeye").FactionId);
        Assert.Equal("bossboar", BotFactions.Resolve("followerboarclose2").FactionId);
    }

    [Theory]
    [InlineData("infectedlooter", "infected")]
    [InlineData("sectantacolyte", "cultist")]
    [InlineData("exusecrebel", "rogue")]
    public void A_trusted_prefix_covers_a_type_no_row_names(string botType, string faction)
    {
        var membership = BotFactions.Resolve(botType);

        Assert.Equal(faction, membership.FactionId);
        Assert.Equal(FactionEvidence.Prefix, membership.Evidence);
    }

    [Theory]
    [InlineData("followernonsense")]
    [InlineData("bosswedge")]
    [InlineData("blackdivassault")]
    public void A_type_outside_the_trusted_families_is_unknown_not_guessed(string botType)
    {
        var membership = BotFactions.Resolve(botType);

        Assert.Null(membership.FactionId);
        Assert.Equal(FactionEvidence.Unknown, membership.Evidence);
        Assert.False(membership.IsResolved);
    }

    [Fact]
    public void The_data_casing_resolves_to_the_same_faction()
    {
        // A wave says "pmcBEAR"; the file is pmcbear.json.
        Assert.Equal(BotFactions.Resolve("pmcbear").FactionId, BotFactions.Resolve("pmcBEAR").FactionId);
        Assert.Equal(BotFactions.Resolve("bosskojaniy").FactionId, BotFactions.Resolve("bossKojaniy").FactionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_type_is_a_programming_error(string botType)
    {
        Assert.ThrowsAny<ArgumentException>(() => BotFactions.Resolve(botType));
    }
}
