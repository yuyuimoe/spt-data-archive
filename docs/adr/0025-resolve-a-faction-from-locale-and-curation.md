# ADR-0025: Resolve a faction from a curated map with a prefix fallback

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0014, ADR-0023, ADR-0024

## Context

The driver asked the API to resolve factions, because the archive is fetched for many reasons and
several mods add new factions and bots. The driver also said the answer "goes between prefix and
maps", and that "the factions are separated through boss ID".

The server data has no faction field. Measured on 4.1.6:

1. **Bot type identity is the file name.** A file under `bots/types/` has ten keys — `appearance`,
   `chances`, `difficulty`, `experience`, `firstName`, `generation`, `health`, `inventory`,
   `lastName`, `skills`. No `type` field and no faction field. The file name is the identity. There
   are 57 files.
2. **Location data names 35 of them.** The other 23 — most followers, the infected variants, three
   sectant variants, `shooterbtr`, `skier`, `spiritspring`, `spiritwinter`, `bear` and `usec` — are
   never named by a wave, a boss spawn or an escort in 4.1.6.
3. **A boss squad is visible, but the pairs are not reliable.** `BossLocationSpawn` states a
   `BossName` and a `BossEscortType`, and the pairs contradict the file names:
   `bossKilla` escorts `followerTagilla` — a bot type this release does not have;
   `bossTagilla` escorts `followerBully`; `bossKnight` escorts `exUsec`; `bossPartisan` escorts
   `sectantWarrior` and `exUsec`; `pmcUSEC` escorts `pmcBEAR`. One name out of 35 that the data
   references has no file at all.
4. **The prefix does not reach the squad.** `followerBigpipe` and `followerBirdeye` are the crew of
   `bossKnight`. There is no `bossBigpipe`. A rule cannot find that boss, and the driver's answer
   says the same: the squad is separated through the boss id, which is curation.
5. **Names are the client enum, lowercased.** A wave says `pmcBEAR` and the file is `pmcbear.json`.
6. **The one vocabulary in the data is a role namespace.** `locales/global/en.json` has 12 keys of the
   form `ScavRole/<Name>`: `Boss`, `Follower` ("Guard"), `PmcBot` ("Raider"), `ExUsec` ("Rogue"),
   `Marksman` ("Sniper"), `ArenaFighterEvent` ("Smuggler"), `Sectant`, and the infected roles. These
   are **roles**, not factions. `USEC`, `BEAR` and `Savage` are not in that list. Only 26 of the 57
   bot types are named by a locale key at all.
7. **A mod extends the namespace for its faction.** WTT - Black Division adds
   `"ScavRole/BlackDiv": "Black Div."`, `db/bots/types/bossWedge.json`,
   `db/bots/config/bossWedge.jsonc` (`"type": "bossWedge"`, `"isBoss": true`) and
   `db/bots/sharedConfig/blackDiv.jsonc` (`"type": "blackDiv"`).

Nothing in the data links `bossWedge` or `blackDiv` to `ScavRole/BlackDiv`.

## Decision

1. **Four levels of authority, highest first.**

   | Level | Source | Example |
   |---|---|---|
   | 1 | The mod itself, when it ships `db/bots/config/<type>.jsonc` with a `type` and a role | `blackDiv` from its own config (TD-29, TD-32) |
   | 2 | The curated seed for the release's own bot types | `exusec` → `rogue` |
   | 3 | A prefix family whose prefix names the faction | `infectedLooter` → `infected` |
   | 4 | Nothing. The faction is null | `peacemaker`, `skier` |

2. **The seed is complete, not partial.** Every bot type of a release has a row. A row either names a
   faction or names none with a reason. Silence is not allowed, because silence is indistinguishable
   from "nobody looked".
3. **A boss squad is one faction, named by the boss id.** `bossbully` is Reshala and his guard. Every
   follower is curated to its boss id, because the prefix cannot reach it (`followerBigpipe` →
   `bossknight`).
4. **Only four prefixes are trusted**, because in these the prefix is the faction:
   `boss<X>` → the faction `boss<x>`, but only when that faction exists;
   `infected<X>` → `infected`; `sectant<X>` → `cultist`; `exusec<X>` → `rogue`.
   There is no `follower<X>` rule. A follower needs a curated boss id.
5. **Roles stay separate from factions.** `ScavRole/*` is the release's own vocabulary and is imported
   as locale data. A faction is curation. A GUI groups bosses by role and a crew by faction.
6. **A missing membership is null in the API, never a guess.** The answer names the bot type. It is
   never an empty list.
7. **The seed lives in code with a test, not in a table only.** A new bot type in a new release fails
   the coverage test until somebody curates it. The database tables arrive with TD-32; the rule
   arrives now.

The seed for 4.1.6 holds 57 rows, 21 factions, 53 memberships and 4 deliberate nulls:

| Faction | Bot types |
|---|---|
| `usec` | `pmcusec`, `usec` |
| `bear` | `pmcbear`, `bear` |
| `savage` | `assault`, `marksman`, `cursedassault`, `crazyassaultevent` |
| `raider` | `pmcbot` |
| `rogue` | `exusec` |
| `cultist` | `sectantpriest`, `sectantwarrior`, `sectantpredvestnik`, `sectantprizrak`, `sectantoni` |
| `infected` | `infectedassault`, `infectedcivil`, `infectedlaborant`, `infectedpmc`, `infectedtagilla` |
| `arenafighter` | `arenafighterevent` |
| `btr` | `shooterbtr` |
| `gifter` | `gifter` |
| `bossbully` | `bossbully`, `followerbully` |
| `bossgluhar` | `bossgluhar`, `followergluharassault`, `followergluharscout`, `followergluharsecurity` |
| `bosskilla` | `bosskilla`, `bosskillaagro` |
| `bossknight` | `bossknight`, `followerbigpipe`, `followerbirdeye` |
| `bosskojaniy` | `bosskojaniy`, `followerkojaniy` |
| `bosskolontay` | `bosskolontay`, `followerkolontayassault`, `followerkolontaysecurity` |
| `bosspartisan` | `bosspartisan` |
| `bosssanitar` | `bosssanitar`, `followersanitar` |
| `bosstagilla` | `bosstagilla`, `bosstagillaagro`, `tagillahelperagro` |
| `bosszryachiy` | `bosszryachiy`, `followerzryachiy`, `ravangezryachiyevent` |
| `bossboar` | `bossboar`, `bossboarsniper`, `followerboar`, `followerboarclose1`, `followerboarclose2` |
| *none* | `peacemaker`, `skier`, `spiritspring`, `spiritwinter` |

`peacemaker` and `skier` are leftovers of the in-game traders that BSG cancelled: the driver says so
(2026-10-20) and the data agrees, because no location spawns them and no locale role names them. They
keep a row with a null faction so that a later release cannot hide a new bot type behind the same
silence.

## Options Considered

1. **A prefix rule only.** Free, and it produces `followerBigpipe` → `bossbigpipe`, which is not a
   faction, and `exusec` → `ex`, which is not a word. It also cannot say what `peacemaker` is.
2. **A curated map only.** Correct and it does not scale to a mod's six bot types, where the mod
   already states the family in its own config.
3. **Layered: mod declaration, then curated seed, then four trusted prefixes, then null.** Chosen.
   Each level is used only where it is reliable, and the highest level that answers wins.
4. **No factions at all.** Honest and useless: a GUI showing spawns wants to group them.

## Trade-off Analysis

The curated seed is 57 rows of human work for 4.1.6, and a release adds a handful of rows. The prefix
fallback removes the work for the infected, sectant and rogue families, where the name is the answer,
and for a new boss whose squad is named after it. The mod declaration is the only level that keeps
working when a mod adds a faction the archive has never seen.

The cost is that a faction stays unresolved until somebody writes a row, which the API states as null
with the bot type. That is the same trade the archive makes for a silent mod: an uncurated bot type is
visible, not invisible.

The escort data is not a source. It contradicts the file names in five pairs and names one bot type
that does not exist, so using it as a rule would import those mistakes as facts. The archive keeps the
pairs as data — they are in `locations` — and does not derive a faction from them.

## Consequences

- `BotFactions.Seed` in `SptArchive.Core` holds the map, and `BotFactions.Resolve` applies the four
  levels. `FactionTests` checks that the seed covers every bot type file of 4.1.6, that every named
  faction exists, and that the four nulls stay null.
- A new release with a new bot type fails that coverage test until somebody adds a row. That is the
  alarm, and it is louder than a silent null.
- The mod level needs TD-32 (the bots importer) and TD-29 (the export importer). Until then a mod bot
  type resolves by prefix or is null.
- `locations` keeps `BossName` and `BossEscortType` as they are, mistakes included. A consumer that
  wants a squad reads the faction, not the escort.
- One data fault is worth a ticket: `bossKilla` escorts `followerTagilla`, and 4.1.6 has no such bot
  type (TD-35).

## Verification

```gherkin
@adr-0025 @unverified
Feature: Resolve a bot to a faction

  Scenario: A curated seed row resolves an irregular name
    Given the release "4.1.6" with the curated seed
    When I resolve the bot type "exusec"
    Then the faction is "rogue"

  Scenario: A follower resolves to its boss id, not to its prefix
    When I resolve the bot type "followerbigpipe"
    Then the faction is "bossknight"
    And the faction is not "bossbigpipe"

  Scenario: A trusted prefix covers a bot type no row names
    When I resolve the bot type "infectedlooter"
    Then the faction is "infected"

  Scenario: A follower with no curated boss id stays unresolved
    When I resolve the bot type "followernonsense"
    Then the faction is null

  Scenario: A bot type nobody curates is null and named
    When I resolve the bot type "peacemaker"
    Then the faction is null
    And the answer names the bot type "peacemaker"

  Scenario: A new release with a new bot type fails the coverage check
    Given a release with the bot type "bossNewcomer" and no seed row
    When I import that release
    Then the run fails and names the bot type "bossNewcomer"

  Scenario: A client-only faction is absent from the data
    Given a mod with the facet "client" and no server part
    When I read the factions of a release
    Then the answer does not list that faction
    And the mod is still in the catalog
```

## Action Items

- [x] Yui decided the rule: curated seed with a prefix fallback, separated by boss id.
- [x] Seed the map: 21 factions, 57 rows, 4 deliberate nulls (`BotFactions.Seed`).
- [x] Test the seed against the 57 bot type files of 4.1.6 (`FactionTests`).
- [ ] Add `factions` and the bot type membership columns to the schema (TD-32).
- [ ] Fail the import when a release has a bot type with no seed row (TD-33).
- [ ] Resolve a mod's own bot types from its `db/bots/config` file (TD-29).

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): the raw object stays in jsonb.
- [ADR-0014](0014-keep-every-archived-spt-release.md): the release key.
- [ADR-0023](0023-classify-a-mod-by-the-parts-it-ships.md): a client-only mod has no dataset.
- [ADR-0024](0024-archive-the-whole-server-database.md): the tables that hold bots and locations.
