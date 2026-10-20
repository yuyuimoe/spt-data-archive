# ADR-0027: Archive a run as a database directory

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0013, ADR-0014, ADR-0020, ADR-0024

## Context

ADR-0020 puts an exporter inside the container. It dumps the tables after every mod has loaded.
It writes three flat files: `templates.json`, `traders.json` and `locales.json`.

The Archiver reads a different shape. Its importers read the layout of `SPT_Data/database`:

```
templates/items.json  templates/quests.json  templates/customization.json
locales/global/<code>.json
traders/<id>/base.json  traders/<id>/assort.json  traders/<id>/questassort.json
```

So the two halves do not meet. A dump from a mod run cannot be imported (TD-29).
The same is true for every table that has no exporter entry (TD-32).

The server of 4.1.6 has ten table classes: `BotTable`, `GlobalTable`, `HideoutTable`,
`LocaleTable`, `LocationTable`, `MatchTable`, `ServerTable`, `SettingsTable`,
`TemplateTable` and `TradersTable`. `TemplateTable` alone holds fifteen collections.
The database directory holds ten roots: `bots/`, `hideout/`, `locales/`, `locations/`,
`templates/`, `traders/`, `globals.json`, `match/metrics.json`, `server.json` and `settings.json`.

Two tables hold lazy loads. `LocaleTable.Global` is `Dictionary<string, LazyLoad<...>>`.
`Location` holds `LooseLoot`, `StaticLoot` and `StaticContainers` as `LazyLoad`.
A lazy load applies transformers when it builds its value. Both the locale transformers
(seen in the driver's `ODT-ItemInfo-4.0` fork) and the seasonal loot transformers of
`SeasonalEventService` work that way. A serializer that walks the wrapper writes
`{"value": ...}` and can miss the transformers.

## Decision

The export is a database directory. The exporter writes the paths, file names and
structure of `SPT_Data/database`, filled with the data of the run.

| Table | Export paths |
| --- | --- |
| `TemplateTable` | `templates/<name>.json`, one file per collection: `items`, `quests`, `handbook`, `prices`, `achievements`, `customAchievements`, `customization`, `customisationStorage`, `profiles`, `character`, `dialogue`, `repeatableQuests`, `prestige`, `locationServices`, `defaultEquipmentPresets` |
| `TradersTable` | `traders/<id>/base.json`, `assort.json`, `dialogue.json`, `questassort.json` |
| `LocaleTable` | `locales/languages.json`, `locales/global/<code>.json`, `locales/menu/<code>.json` |
| `BotTable` | `bots/base.json`, `bots/core.json`, `bots/types/<id>.json` |
| `LocationTable` | `locations/base.json`, `locations/<map>/base.json` and the six overlays: `looseLoot`, `staticLoot`, `staticContainers`, `staticAmmo`, `statics`, `allExtracts` |
| `HideoutTable` | `hideout/areas.json`, `customAreas.json`, `customisation.json`, `production.json`, `qte.json`, `settings.json` |
| `GlobalTable` | `globals.json` |
| `ServerTable` | `server.json` |
| `SettingsTable` | `settings.json` |
| `MatchTable` | `match.json` |

The Archiver reads one layout. An install and an export are the same input.
An official row comes from a run without mods. A mod dataset comes from a run with one mod
(ADR-0013). The directory does not say which one it is. The run does.

The exporter reads `.Value` of every lazy load before it writes. The export holds the data
after every transformer ran. A dump therefore shows the locale rewrites and the loot
rewrites that a mod made, and not only the entries that a mod added.

The key of a map is the name of its directory in the database: `bigmap`, `factory4_day`,
`privatearea`. It is not `Location.Base.Id`. That field is not consistent. It holds
`Interchange`, `Private Area`, `RezervBase` and `Sandbox_high` for maps whose directory is in
lower case. The server keeps the same mapping in `LocationTable._locationMappings`, so a key
of the archive is a key that the server also accepts.

Four deviations are deliberate and are written here so that no one reads them as a bug:

1. `templates/archivedQuests.json` exists in the install but has no property in
   `TemplateTable`, so the export does not hold it.
2. The install keeps a part of `MatchTable` in `match/metrics.json`. The export writes the
   whole table to one file, `match.json`.
3. `Suits` and `Services` of a trader stay inside `traders/<id>/base.json`, as the server
   reads them.
4. The server holds a locale file in a dictionary that ignores case. Two keys that differ
   only in case therefore collapse into one. The install of 4.1.6 has six such pairs in
   `locales/global/en.json` (`Arena/Widgets/activate object` and `arena/widgets/activate
   object`, and five more). Their values are equal. The export holds 31,549 keys and the file
   holds 31,550. The export is the state of the server, not the state of the file.

The source of the exporter lives in the repository, `src/SptArchive.Exporter`. It is not part
of the solution and it is not redistributed. It builds against an SPT install whose path a
build property gives (`-p:SptAssemblyPath=...`).

## What this ADR does not change

The run directory is a transport format, not a second database. PostgreSQL stays the only store
of record (ADR-0001), with promoted columns and `jsonb` next to them (ADR-0002), and official and
mod rows in separate tables (ADR-0003). This ADR decides the shape of the handover between one
container run and the Archiver. It decides nothing about storage:

- Which rows are stored, and how they are keyed, is ADR-0013, ADR-0017 and ADR-0024.
- The official import still reads the install directly (ADR-0004). An export is read only for a run
  with a mod, or for a release whose install is not on the machine that runs the Archiver.
- A run directory may be deleted after the import. It costs 642 MB for 4.1.6, and the archive holds
  what the run said. The directories that are worth keeping are the two baselines of ADR-0028,
  because they are the fixture of the canonical-form test.

The reason the run is database-shaped is not that a directory is a database. It is that the
Archiver then owns one set of importers, and a run can be read by the code that already reads an
install.

## Options Considered

**A. Keep the flat dumps and write a second set of importers.** The Archiver would learn
two input shapes. Every importer would exist twice. Rejected.

**B. Mirror the database layout (chosen).** One layout, one set of importers. A run
directory can be opened next to the install.

**C. Export the raw pre-load files from the assets directory.** This needs no exporter and
no container. It is also the state before any mod runs, so it cannot give a mod dataset.
Rejected by ADR-0013 and ADR-0020.

**D. Export one big file per run.** Simple to write, but it cannot be read per table, and a
reader must hold 660 MB in memory to read one item.

## Trade-off Analysis

The chosen option ties the archive to a file layout that SPT owns. If SPT renames a file or
moves a table, the exporter and the layout drift apart. The benefit is larger: one importer
per table, one vocabulary, and a directory that a person can compare with an install.

The alternative, a flat dump, has no coupling to SPT file names. But it needs a second
importer for every table, and two shapes that must stay equal.

Materializing lazy loads costs memory and time. The largest single file is
`locations/lighthouse/looseLoot.json` at 126 MB. Files are written one at a time, so the peak
is one file, not the sum.

The export is larger than the install because the install of a release and the export of a
run differ in one more way: the export holds the merged result of the database directory,
including the parts that the server assembled at load.

## Consequences

The first export of 4.1.6 with no mod has 642 MB: 4,673 items, 558 quests, 31,549
translations, 2 handbook roots, 12 traders, 57 bot types, 19 locations (one with a 126 MB
`looseLoot.json`) and the six parts of the hideout. The counts equal the install, except the
one locale key of the case collision above.

- TD-32 closes when every table has a place in the export.
- TD-29 becomes a small change: point the Archiver at a run directory instead of an install.
- The Archiver needs importers for the tables that it does not import yet: bots, locations,
  hideout, globals, settings, server and match (ADR-0024).
- The exporter is source in the repository from now on. A change of the SPT layout is a
  change of this project.
- `docs/runbooks/mod-export.md` shows the tree of a run directory.

## Verification

```gherkin
@adr-0027 @unverified
Feature: A run exports as a database directory
  The export of one container run has the file layout of the server database.

  Scenario: Every table of the release has a place in the export
    Given an export of SPT 4.1.6 with no mod
    Then the export has "globals.json", "server.json", "settings.json" and "match.json"
    And the export has "bots/base.json", "bots/core.json" and one file per bot type
    And the export has one directory per location, named as in the server database, with "base.json" and its overlays
    And the export has "locations/privatearea/base.json" and not "locations/Private Area"
    And the export has "templates/items.json", "templates/quests.json" and "templates/handbook.json"
    And the export has one directory per trader with "base.json" and "assort.json"
    And the export has "locales/global/en.json" with one entry per translation key

  Scenario: A lazy load is materialized before it is written
    Given a mod that prepends a value to a locale entry in a transformer
    And a run with that mod
    Then the export holds the changed locale entry
    And the export does not hold a "value" wrapper

  Scenario: A map mod adds a location to the export
    Given a run with a mod that adds the map "arena_airpit"
    Then the export has "locations/arena_airpit/base.json"
    And the export of the official run has no "locations/arena_airpit"
```

## Action Items

- [x] TD-32: extend the exporter to every table.
- [x] Move the exporter source into the repository, `src/SptArchive.Exporter`.
- [ ] TD-29: read a run directory into the mod tables.
- [ ] Verify the export of the next release against the layout, and record a change of a file
      name here.

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): the full source object stays in `jsonb`.
- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): one mod version per container run.
- [ADR-0014](0014-keep-every-archived-spt-release.md): every release is kept.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter runs inside the container.
- [ADR-0024](0024-archive-the-whole-server-database.md): every table of the server database is archived.
- [ADR-0028](0028-compare-two-runs-in-a-canonical-form.md): how two runs are compared.
