# ADR-0025: Resolve a faction from a locale key and a curated membership

**Status:** Proposed (decision needed)
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0014, ADR-0023, ADR-0024

## Context

The driver asked the API to resolve factions, because the archive will be fetched for many reasons and
several mods add new factions and bots. The data does not contain a faction.

What the data does contain, measured on 4.1.6:

1. **Bot type identity is the file name.** A file under `bots/types/` has ten keys —
   `appearance`, `chances`, `difficulty`, `experience`, `firstName`, `generation`, `health`,
   `inventory`, `lastName`, `skills`. There is no `type` field and no faction field. The name of the
   file is the identity. There are 57 files.
2. **Location waves name a spawn type.** Over the 19 maps there are 151 waves with
   `BotSide: "Savage"` and 3 with `BotSide: "Usec"`, and only two distinct `WildSpawnType` values
   (`assault` 140, `marksman` 14). The variety is in the boss spawns: 24 distinct `BossName` values
   (`bossBully`, `bossGluhar`, `pmcBEAR`, `pmcUSEC`, `exUsec`, `sectantPriest`, `gifter`, …).
3. **The names are the client enum, lowercased into a file name.** A wave says `pmcBEAR` and the file
   is `pmcbear.json`. The join is `name.ToLowerInvariant()`. The same holds for `bossKojaniy` →
   `bosskojaniy.json` and `arenaFighterEvent` → `arenafighterevent.json`.
4. **The one faction vocabulary is a locale namespace.** `locales/global/en.json` has 12 keys of the
   form `ScavRole/<Name>`: `Boss`, `Follower` ("Guard"), `PmcBot` ("Raider"), `ExUsec` ("Rogue"),
   `Marksman` ("Sniper"), `ArenaFighterEvent` ("Smuggler"), `Sectant`, and `infected*` ("Infected").
   These are **roles**, not factions. `USEC`, `BEAR` and `Savage` are not in that list.
5. **A mod extends that namespace for its faction.** WTT - Black Division adds one line:
   `"ScavRole/BlackDiv": "Black Div."`. The same mod adds `db/bots/types/bossWedge.json`,
   `db/bots/config/bossWedge.jsonc` (`"type": "bossWedge"`, `"isBoss": true`),
   `db/bots/sharedTypes/blackDiv.json`, `db/bots/sharedConfig/blackDiv.jsonc`
   (`"type": "blackDiv"`), and `db/CustomBotLoadouts/{blackdivassault,blackdivib,bosswedge}.json`.

Nothing in the data links `bossWedge` or `blackDiv` to `ScavRole/BlackDiv`. The only link is the
string `blackdiv` appearing in three file names. Skill Effects Extended and EFT Effect Manager add no
server data at all (ADR-0023), and ISB Special Forces is 270 files under `BepInEx/` with no server
part, so its faction exists only in the client.

## Decision

1. The archive keeps **two levels**: a **role** and a **faction**.
   - `roles`: the official `ScavRole/*` vocabulary of a release, keyed `(spt_version, role_id)` with
     the display name from the locale. Mods extend it through their merged locale.
   - `factions`: named groups a player recognises (`usec`, `bear`, `savage`, `blackdiv`, …), keyed
     `(dataset_id or spt_version, faction_id)`, with `display_name`, `role_id` nullable, and
     `source` one of `locale`, `curated`, `mod`.
2. `bot_types` rows carry `role_id` and `faction_id`, both **nullable**. The Archiver fills them from
   the curated map only. It never derives a faction from a file-name prefix.
3. The curated map is a table `faction_bot_types(faction_id, bot_type_id, evidence)` where `evidence`
   is a short text: the locale key, the mod that declares it, or the human who curated it.
4. A missing membership is `null` in the API, never a guess. The API says "unknown faction" and names
   the bot type.
5. The API resolves a faction for a map: `locationBossSpawns` and `locationWaves` join `bot_types`,
   then `faction_bot_types`, then `factions`. The answer names the release, or the mod dataset, that
   supplied the bot type.
6. A faction that only a client mod adds (ISB) has no dataset and no API row. The catalog records the
   mod and its facets, and the API returns nothing for it. This is a limit of the server data, and the
   archive states it rather than inventing it.

## Options Considered

1. **A prefix rule.** `blackDiv` and `blackdivassault` start with `blackdiv`, so a rule could group them.
   It guesses, and it breaks on the first faction whose assets do not share a prefix.
2. **A curated map with evidence.** Chosen. A human writes `bossWedge → blackdiv` once, with the reason,
   and the archive never infers.
3. **Ask the mod.** No SPT mod declares a faction. Black Division declares a locale key and four asset
   files, and the locale key is the closest thing to a declaration.
4. **No factions at all.** Honest and useless: a GUI showing spawns wants to group them.

## Trade-off Analysis

Curation is a small amount of human work and the only option that cannot be wrong silently. The
prefix rule is free and produces confident nonsense the first time a mod names its boss after its
author. Asking the mod would be best and the data does not support it, because the client holds the
enum that names the faction. The cost of curation is that a faction added by a mod stays unresolved
until somebody adds a row, which the API states as `null` instead of an empty list.

## Consequences

- The seed mapping is small and human: the 12 roles, plus `usec`, `bear` and `savage` for the base
  game, plus one row per faction a curated mod declares.
- `bossWedge` resolves to `blackDiv` only because a human wrote that row. If nobody writes it, the API
  returns `null` and the bot type, which is correct and testable.
- The role vocabulary is data and moves with a release. The faction vocabulary is curation and moves
  with the catalog.
- An API that groups "all bosses" uses `roles`; an API that groups "the Black Division" uses
  `factions`. A GUI needs both, and the archive must not confuse them.

## Verification

```gherkin
@adr-0025 @unverified
Feature: Resolve a bot to a faction

  Scenario: A wave resolves to its bot type and role
    Given the release "4.1.6" with the map "bigmap"
    When I read the waves of that map
    Then the wave with the spawn type "assault" names the bot type "assault"
    And the bot type has a role

  Scenario: A curated faction resolves a mod bot type
    Given a curated row that puts the bot type "bossWedge" in the faction "blackdiv"
    When I read the boss spawns of a map that spawns "bossWedge"
    Then the answer names the faction "blackdiv"
    And the answer names the dataset that supplied the bot type

  Scenario: An uncured bot type has no faction
    Given the bot type "bossWedge" has no curated faction row
    When I read that bot type
    Then the faction is null
    And the answer is not an empty list

  Scenario: A client-only faction is absent from the data
    Given a mod with the facet "client" and no server part
    When I read the factions of a release
    Then the answer does not list that faction
    And the mod is still in the catalog
```

## Action Items

- [ ] Yui decides: curated membership (this ADR), or a prefix rule, or no factions.
- [ ] Seed the curated map: 12 roles, `usec`, `bear`, `savage`, `blackdiv`.
- [ ] Add `roles`, `factions`, `faction_bot_types` to the schema.
- [ ] TD-33: nothing links a bot type to a faction today.

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): the raw object stays in jsonb.
- [ADR-0014](0014-keep-every-archived-spt-release.md): the release key.
- [ADR-0023](0023-classify-a-mod-by-the-parts-it-ships.md): a client-only mod has no dataset.
- [ADR-0024](0024-archive-the-whole-server-database.md): the tables that hold bots and locations.
