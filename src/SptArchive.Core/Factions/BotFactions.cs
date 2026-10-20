namespace SptArchive.Core.Factions;

/// <summary>One row of the curated seed: the faction of a bot type, or the reason it has none.</summary>
/// <param name="FactionId">The faction id, or null when the archive decided that the type has none.</param>
/// <param name="Evidence">Short text that says why. It becomes the evidence column of the table.</param>
public readonly record struct BotTypeSeedRow(string? FactionId, string Evidence);

/// <summary>
/// Links a bot type to a faction, in four levels of authority (ADR-0025). The highest level that
/// answers wins: the mod's own config, the curated seed, a trusted prefix, and then nothing.
/// </summary>
/// <remarks>
/// The server data has no faction field. A bot type is a file name, and the only vocabulary in the
/// data is the <c>ScavRole/*</c> locale namespace, which names roles and not factions. The seed is
/// therefore curation, and it is complete on purpose: every bot type of a release has a row, and a
/// row either names a faction or says that the type has none. A missing row is not the same as an
/// unknown type, and the difference is what the coverage test protects.
/// </remarks>
public static class BotFactions
{
    public const string Usec = "usec";
    public const string Bear = "bear";
    public const string Savage = "savage";
    public const string Raider = "raider";
    public const string Rogue = "rogue";
    public const string Cultist = "cultist";
    public const string Infected = "infected";
    public const string ArenaFighter = "arenafighter";
    public const string Btr = "btr";
    public const string Gifter = "gifter";

    /// <summary>The faction ids of the base game. A boss squad uses the boss id as its faction id.</summary>
    public static IReadOnlySet<string> Ids { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Usec, Bear, Savage, Raider, Rogue, Cultist, Infected, ArenaFighter, Btr, Gifter,
        "bossbully", "bossgluhar", "bosskilla", "bossknight", "bosskojaniy", "bosskolontay",
        "bosspartisan", "bosssanitar", "bosstagilla", "bosszryachiy", "bossboar",
    };

    /// <summary>
    /// The curated map for the 57 bot type files of release 4.1.6. A follower carries its boss id,
    /// because the prefix cannot reach the boss: <c>followerbigpipe</c> belongs to <c>bossknight</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, BotTypeSeedRow> Seed { get; } =
        new Dictionary<string, BotTypeSeedRow>(StringComparer.Ordinal)
        {
            // Player factions and the scav family.
            ["pmcusec"] = Row(Usec, "curated: player faction"),
            ["usec"] = Row(Usec, "curated: player faction"),
            ["pmcbear"] = Row(Bear, "curated: player faction"),
            ["bear"] = Row(Bear, "curated: player faction"),
            ["assault"] = Row(Savage, "curated: scav family"),
            ["marksman"] = Row(Savage, "locale ScavRole/Marksman"),
            ["cursedassault"] = Row(Savage, "curated: scav family"),
            ["crazyassaultevent"] = Row(Savage, "curated: scav event variant"),
            ["pmcbot"] = Row(Raider, "locale ScavRole/PmcBot"),
            ["exusec"] = Row(Rogue, "locale ScavRole/ExUsec"),
            ["arenafighterevent"] = Row(ArenaFighter, "locale ScavRole/ArenaFighterEvent"),
            ["shooterbtr"] = Row(Btr, "curated: one vehicle crew"),
            ["gifter"] = Row(Gifter, "curated: event character"),

            // Cultists and the infected.
            ["sectantpriest"] = Row(Cultist, "locale ScavRole/Sectant"),
            ["sectantwarrior"] = Row(Cultist, "locale ScavRole/Sectant"),
            ["sectantpredvestnik"] = Row(Cultist, "locale ScavRole/Sectant"),
            ["sectantprizrak"] = Row(Cultist, "locale ScavRole/Sectant"),
            ["sectantoni"] = Row(Cultist, "locale ScavRole/Sectant"),
            ["infectedassault"] = Row(Infected, "curated: infected family"),
            ["infectedcivil"] = Row(Infected, "curated: infected family"),
            ["infectedlaborant"] = Row(Infected, "curated: infected family"),
            ["infectedpmc"] = Row(Infected, "curated: infected family"),
            ["infectedtagilla"] = Row(Infected, "curated: infected family"),

            // Boss squads. The faction id is the boss id.
            ["bossbully"] = Bout("bossbully"),
            ["followerbully"] = Follower("bossbully"),
            ["bossgluhar"] = Bout("bossgluhar"),
            ["followergluharassault"] = Follower("bossgluhar"),
            ["followergluharscout"] = Follower("bossgluhar"),
            ["followergluharsecurity"] = Follower("bossgluhar"),
            ["bosskilla"] = Bout("bosskilla"),
            ["bosskillaagro"] = Row("bosskilla", "curated: event variant of bosskilla"),
            ["bossknight"] = Bout("bossknight"),
            ["followerbigpipe"] = Follower("bossknight"),
            ["followerbirdeye"] = Follower("bossknight"),
            ["bosskojaniy"] = Bout("bosskojaniy"),
            ["followerkojaniy"] = Follower("bosskojaniy"),
            ["bosskolontay"] = Bout("bosskolontay"),
            ["followerkolontayassault"] = Follower("bosskolontay"),
            ["followerkolontaysecurity"] = Follower("bosskolontay"),
            ["bosspartisan"] = Bout("bosspartisan"),
            ["bosssanitar"] = Bout("bosssanitar"),
            ["followersanitar"] = Follower("bosssanitar"),
            ["bosstagilla"] = Bout("bosstagilla"),
            ["bosstagillaagro"] = Row("bosstagilla", "curated: event variant of bosstagilla"),
            ["tagillahelperagro"] = Follower("bosstagilla"),
            ["bosszryachiy"] = Bout("bosszryachiy"),
            ["followerzryachiy"] = Follower("bosszryachiy"),
            ["ravangezryachiyevent"] = Row("bosszryachiy", "curated: event variant of bosszryachiy"),
            ["bossboar"] = Bout("bossboar"),
            ["bossboarsniper"] = Follower("bossboar"),
            ["followerboar"] = Follower("bossboar"),
            ["followerboarclose1"] = Follower("bossboar"),
            ["followerboarclose2"] = Follower("bossboar"),

            // Named by nothing: no location of 4.1.6 spawns these, and no locale role names them.
            ["peacemaker"] = NoFaction("not referenced by any location of 4.1.6 and no locale role"),
            ["skier"] = NoFaction("not referenced by any location of 4.1.6 and no locale role"),
            ["spiritspring"] = NoFaction("not referenced by any location of 4.1.6 and no locale role"),
            ["spiritwinter"] = NoFaction("not referenced by any location of 4.1.6 and no locale role"),
        };

    /// <summary>
    /// The faction of a bot type. The answer always states where it came from, and an unknown faction
    /// is a null that the caller must show, never an empty list (ADR-0025).
    /// </summary>
    public static FactionMembership Resolve(string botTypeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botTypeId);

        // The data names the type in the client's casing ("pmcBEAR"); the file name is lower case.
        var id = botTypeId.Trim().ToLowerInvariant();

        if (Seed.TryGetValue(id, out var row))
        {
            return new FactionMembership(id, row.FactionId, FactionEvidence.Curated);
        }

        var family = FamilyFaction(id);
        return family is null
            ? new FactionMembership(id, null, FactionEvidence.Unknown)
            : new FactionMembership(id, family, FactionEvidence.Prefix);
    }

    /// <summary>
    /// The four prefixes that are trusted, because in these the prefix is the faction. There is no
    /// <c>follower</c> rule: a follower needs a curated boss id, and guessing one invents a faction.
    /// </summary>
    private static string? FamilyFaction(string id)
    {
        if (id.StartsWith("boss", StringComparison.Ordinal))
        {
            // A boss squad is named by its boss id, so the faction exists only when it is known.
            return Ids.Contains(id) ? id : null;
        }

        if (id.StartsWith("infected", StringComparison.Ordinal))
        {
            return Infected;
        }

        if (id.StartsWith("sectant", StringComparison.Ordinal))
        {
            return Cultist;
        }

        return id.StartsWith("exusec", StringComparison.Ordinal) ? Rogue : null;
    }

    private static BotTypeSeedRow Row(string factionId, string evidence) => new(factionId, evidence);

    private static BotTypeSeedRow Bout(string bossId) => Row(bossId, $"curated: boss id {bossId}");

    private static BotTypeSeedRow Follower(string bossId) => Row(bossId, $"curated: follower of {bossId}");

    private static BotTypeSeedRow NoFaction(string reason) => new(null, $"explicit: {reason}");
}
