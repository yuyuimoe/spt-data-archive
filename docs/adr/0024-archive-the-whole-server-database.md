# ADR-0024: Archive the whole server database, not seven tables

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0003, ADR-0014, ADR-0020, ADR-0022, ADR-0023

## Context

The goal of the project is a lower-level data API that a future GUI shapes itself, in the way
Tarkynator serves items, quests, trader assorts, crafts and achievements. The driver decided that the
archive keeps the whole server database, including bots and locations, because mods change them.

The server assembly names twelve `*Table` types, and they load the whole `SPT_Data/database` tree:

| Table type | Source path | Archive today |
| --- | --- | --- |
| `TemplateTable` | `templates/*.json` | items, quests, customization |
| `TradersTable` | `traders/` | traders, assort, quest assort |
| `LocaleTable` | `locales/` | locales |
| `GlobalTable` | `globals.json` | **no** |
| `BotTable` | `bots/` | **no** |
| `LocationTable` | `locations/` | **no** |
| `HideoutTable` | `hideout/` | **no** |
| `SettingsTable` | `settings.json` | **no** |
| `MatchTable` | `match/` | **no** |
| `ServerTable` | `server.json` | **no** |
| `ExpTable` | (in memory) | **no** |
| `SortingTable` | (in memory) | **no** |

`TemplateTable` alone holds fourteen keys on 4.1.6 and the archive imports three of them: `items`,
`quests` and `customization`. Unimported keys include `handbook`, `achievements` (71 rows plus 18
custom), `prices` (4121), `character` (467), `customisationStorage` (48), `profiles` (9),
`defaultEquipmentPresets` (12) and `repeatableQuests`.

The exporter already writes the whole `TemplateTable`, so those keys are captured on every run and
dropped by the Archiver. The other tables are not captured at all.

Size is the reason this is a decision and not a detail. On 4.1.6:

- `globals.json` is 1.1 MB. Its `config` holds 107 groups (`SkillsSettings`, `Health`, `RaidSettings`,
  `RagFair`, `TradingSettings`, `exp`, `handbook`, …). This is the surface that value mods such as SVM
  change.
- `bots/` is 8.3 MB: `base.json`, `core.json`, and 57 files under `bots/types/`.
- `locations/` is 569 MB: 19 map directories, each with seven files. `bigmap/looseLoot.json` alone is
  42 MB. The rest is `base.json`, `allExtracts`, `staticLoot`, `staticContainers`, `statics`,
  `staticAmmo`.
- `hideout/` is 476 KB: `areas`, `production` (the crafts Tarkynator shows), `settings`, `qte`,
  `customisation`, `customAreas`.

## Decision

1. The exporter serializes **all twelve tables** to the export directory, one file per table. It no
   longer picks three.
2. The Archiver imports every table as an official table and a `mod_*` table, following ADR-0003 and
   ADR-0014: official rows are keyed by `(spt_version, id)`, mod rows by `(dataset_id, id)`.
3. Each row keeps the full source object in `jsonb` (ADR-0002). A promoted column is added only when a
   query filters or sorts on it.
4. `locations` is stored as two tables:
   - `locations`, one row per map: `(spt_version, map_id)`, with the promoted `name`, `enabled`,
     `escape_time_limit`, `boss_spawn_count` and the whole `base.json` in `jsonb`;
   - `location_files`, one row per `(spt_version, map_id, file)`, with `content jsonb`. This holds the
     six overlays, including `looseLoot`.
5. `bots` is stored as `bot_types`, one row per type: `(spt_version, type_id)` where `type_id` is the
   lowercased file name, plus `config jsonb` from `bots/config/<type>.jsonc` and `type jsonb`.
6. A mod dataset stores only the rows that differ from the dependency-only baseline. A mod that
   changes one map stores one location row and the overlays it changes.
7. The API returns these tables raw by default (`JSON` as the saved text) and adds a typed view only
   where a query needs one.

## Options Considered

1. **Seven tables, as today.** The smallest change, and it leaves `globals`, `bots` and `locations` out,
   which are the three surfaces that mods in the catalog change.
2. **All tables, one table each.** Chosen. Twelve table types, one official table and one `mod_*` table
   each, with the full object in `jsonb` (ADR-0002).
3. **Keep the dump as files and serve the files.** Cheap to store, and it makes every query a scan of a
   660 MB directory. It also loses the release key that ADR-0014 requires on a row.
4. **All tables without the map overlays.** `looseLoot` is 42 MB per map and no consumer has asked for
   it. This is a defensible exclusion, and it is written down as the open point instead of done.

## Trade-off Analysis

Option 2 costs about 660 MB per run and minutes per import, and it buys the three surfaces that
mechanics and map mods touch. The heavy part is `locations`, and inside it one overlay. Storing rows
rather than files keeps the release key, the mod key and the delta rule in one place: a mod that
changes one map stores one map, which file storage cannot express. Postgres compresses the `jsonb`, so
the cost on disk is lower than the source size.

## Consequences

- One export grows from about 90 MB to about 660 MB, and one import takes minutes. The exporter is
  still the same run: the tables are already in memory.
- Postgres stores the heavy `jsonb` compressed. `looseLoot` for one map is the largest object in the
  database by a wide margin.
- Mods that change mechanics become visible for the first time: SVM changes `globals.config`, and the
  archive can then show which of the 107 groups a preset changes. The preset itself is curated data
  (ADR-0026) and SVM ships none, so this needs one reference preset to prove the pipeline.
- Mods that change maps become visible: bosses, waves, extracts and loot.
- Three mods the driver named have nothing to import, and the archive will say that with a facet
  (ADR-0023), not with an empty table: Skill Effects Extended and EFT Effect Manager are client
  plugins, and ISB Special Forces is 270 files under `BepInEx/`.

## Open point

`looseLoot` is the only part that is hard to justify: it is 42 MB per map, it holds loot spawn points,
and no GUI has asked for it. It is imported because the driver chose "everything" and because ADR-0002
says not to lose data. If storage becomes a problem, the honest change is an explicit exclusion, not a
silent truncation.

## Verification

```gherkin
@adr-0024 @unverified
Feature: Archive the whole server database

  Scenario: A release keeps every table
    Given a server database for release "4.1.6"
    When the Archiver imports it
    Then the table "globals" has one row for that release
    And the table "bot_types" has 57 rows for that release
    And the table "locations" has 19 rows for that release
    And the table "hideout_production" has the crafts of that release

  Scenario: A map keeps its overlays
    Given the map "bigmap" has seven files
    When the Archiver imports it
    Then the table "location_files" has seven rows for that map
    And the row for "looseLoot" holds the whole file

  Scenario: A mod that changes one map stores one map
    Given a mod dataset that changes the map "bigmap"
    When the Archiver imports the dataset
    Then the table "mod_locations" has one row
    And the other 18 maps have no mod row
```

## Action Items

- [ ] TD-32: extend the exporter to all twelve tables.
- [ ] TD-29: read an export directory into the mod tables.
- [ ] Write one migration that adds the new table pairs.
- [ ] Measure one full import and record the duration and the size.

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): promoted columns plus jsonb.
- [ADR-0003](0003-separate-official-and-mod-tables.md): official tables and mod tables.
- [ADR-0014](0014-keep-every-archived-spt-release.md): the release key.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter that reads the tables.
- [ADR-0023](0023-classify-a-mod-by-the-parts-it-ships.md): what a mod can contribute.
