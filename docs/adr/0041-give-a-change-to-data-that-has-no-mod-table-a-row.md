# ADR-0041: Give a change to data that has no mod table a row

**Status:** Proposed
**Date:** 2026-10-21
**Deciders:** Yui

## Context

Five tables of the SPT server database have no `mod_*` table: the locations, the bot types, the hideout,
the prices and the handbook. A location is a directory of files, one directory for each map, and each file
holds a part of that map (`base.json`, `looseLoot.json`). A bot type is one file under `bots/types`. The
hideout is a set of files under `hideout`. The prices are one file, `templates/prices.json`, and the
handbook is one file, `templates/handbook.json`.

Until this decision, a change to these files was measured and not stored. `RunDiff` compares the whole
file, the canonical form of the file enters the content hash (ADR-0029, amendment 4, TD-47), and no row
says what moved (TD-45). A map mod therefore stores a dataset whose answer to "what did this mod change in
the location woods?" is a hash. Measured on 2026-10-20: WTT-Armory changes 37 such files.

Three facts shape the decision.

- A value can be large. A location file of `MapVariants` is 126 MB.
- An id can be minted at boot. ADR-0034 already states that a raw id is never hashed, and that an object is
  named by what it is. A price, a handbook entry and a hideout part name another object, so the same rule
  applies to their values.
- The mod side already holds the rows for a change: `mod_templates` keyed by dataset and address,
  `mod_change_paths` with one row per changed leaf (ADR-0032, decision 1), and `mod_id_maps` (ADR-0034).
  Three `CHECK` constraints list the kinds that these tables accept, and none of them names these five.

## Decision

**We will give a change to a location, a bot type, a hideout part, a price and a handbook entry the same
rows as a change to an item: a template named by its address, one path row for each changed value, and the
source of an added object in a new table.**

1. **Five kinds join `mod_templates`, `mod_change_paths` and `mod_id_maps`:** `location`, `bot`, `hideout`,
   `price` and `handbook`. The three kind checks widen. No table is renamed and no row is copied
   (migration `20261021180615_AddDatabaseObjects`).

2. **`mod_database_objects` holds the source of an added object, one row for each file.** Its key is
   `(dataset_id, kind, template_id, part)`. `part` names the file of a location (`base`, `looseLoot`), and
   it is empty for the four kinds that have one file. A row is therefore never larger than one file. The
   row holds the object as the run wrote it, in `jsonb` (ADR-0002). The table cascades from the dataset and
   from the template row.

3. **A modified or a removed object has no row in `mod_database_objects`.** Its changed values are rows of
   `mod_change_paths`, one row for each leaf, as for an item (ADR-0032, decision 1). A path starts with the
   part of its file, then the RFC 6901 path inside that file: `/base/Insurance`,
   `/chances/weaponMods/mod_scope`, `/requirements/0/count`. A file that only one side holds is one path:
   the whole file in canonical form, with the operation `set` or `remove`.

4. **The template id is the address of the object** (ADR-0034). A location is named by its map name
   (`arena_airpit`). A bot type is named by its file path (`types/assault`). A hideout part is named by its
   recipe name. A price is named by the address of the item it prices, and a handbook entry by the address
   of the item it describes. An object that the baseline holds keeps the id of the baseline. An object that
   only the run holds is named by a content ULID. A minted raw id is never hashed, and `mod_id_maps` keeps
   the raw id of each run.

5. **A price is one value.** The price document of an item is a single path, so "the run prices A1 at 1200"
   is one row with the old value and the new value, and not one row for each field of the document.

6. **These rows enter no hash.** `RunDiff` keeps hashing the whole file in canonical form, so the content
   hash of a dataset does not move (ADR-0029, decision 1 and amendment 4). The rows say what moved, and a
   hash says that something moved.

7. **A volatile path stays out** (ADR-0028). `$.nextResupply` and `$.BossLocationSpawn[*].BossChance` change
   on every boot. A run that changes only those makes no row, so it does not make a new dataset.

8. **Every value is written in the addressed form** (ADR-0034, decisions 5 and 6). An id that names another
   object of the run becomes an address, in a key and in a value. A handbook entry states the address of its
   category as its parent. The same run archived twice therefore writes the same paths and the same values.

9. **No official table is touched** (ADR-0003). Every row is keyed by the dataset id, and no official table
   holds a location, a bot type, a hideout part, a price or a handbook entry (ADR-0035).

10. **The Api does not expose these rows yet.** That is ADR-0032 action item 7. The read-only role can read
    the table, and it cannot write it.

## Options Considered

| Dimension | A. Hash only (today) | B. One row per object, whole document | C. Rows per changed value, and a source table per file (chosen) | D. Five `mod_*` tables, one for each kind |
|-----------|----------------------|---------------------------------------|------------------------------------------------------------------|-------------------------------------------|
| Answers "what changed on woods" | No | Only with a client-side diff | Yes, per value, with the old value | Yes, but a column set is needed for each kind |
| Cost of a 126 MB location | 0 | 126 MB in one row | 126 MB in one row for each file | 126 MB in a schema of columns |
| Reuses the existing rows | — | No | Yes: `mod_templates`, `mod_change_paths`, `mod_id_maps` | No |
| New code for each kind | 0 | 0 | One reader for each table | One entity, one importer and one Api type for each kind |
| Id minted at boot | Not relevant | Raw id is stored | Address, per ADR-0034 | Raw id is a primary key, so the key moves |

A keeps TD-45. B answers only "this map was changed", and a client needs the whole baseline to say what
changed. D repeats ADR-0003 for five tables and gives each of them its own key problem, while the rows that
already exist hold a changed value for every kind. C wins.

## Trade-off Analysis

The trade-off is storage against an answerable question. A dataset of a map mod now keeps a copy of every
file it adds, and a copy of the changed values of every file it modifies. The archive already keeps the
source of an item and the manifest of every file (ADR-0002, ADR-0029), so the rule does not change: the
source object is kept whole, and a promoted column is an extra.

The second trade-off is that one file becomes one row. A 126 MB `jsonb` value is heavy for a row, and
`jsonb` has a 1 GB limit. The alternative, one row for each top-level key of the file, splits it further at
the cost of a reader that must reassemble it. The archive keeps the file whole, so a later split is a
change to the importer alone.

## Consequences

- **Easier:** "What did this map mod change on woods?" has an answer with the old value and the new value.
  A map mod no longer stores a dataset with no row. A price and a handbook entry follow the same rule as an
  item, so a client learns one shape. An id that a run mints does not make a new dataset.
- **Harder:** A dataset of a map mod grows by the size of the files it adds. A location is a directory, so
  the importer reads every file of a changed map even when one file changed. No index serves a query on
  `mod_change_paths` by path yet (TD-45 predecessor, TD-52).
- **Revisit when:** a location file passes 1 GB, or a client asks for a filtered query over these rows and
  a scan is too slow, or the Api exposes them and the answer needs a projection per kind.

## Verification

```gherkin
Feature: A change to data that has no mod table becomes a row

  @unverified
  Scenario: A change to an old location names the map and the value
    Given the baseline holds the location "woods" with "Insurance" true in its base file
    And a mod run sets "Insurance" to false
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "location" for "woods" with the operation "modified"
    And the table "mod_change_paths" states the path "/base/Insurance" for it
    And that row states the old value "true" and the new value "false"

  @unverified
  Scenario: A map that a mod adds keeps each file as its own row
    Given a mod run holds the location "arena_airpit" and the baseline does not
    When the Archiver archives that run
    Then the table "mod_database_objects" holds one row for each file of the map, named by its part
    And its template id is a content ULID of the kind "location" and the name "arena_airpit"

  @unverified
  Scenario: A boss chance that rotates on every boot is not a change
    Given the baseline holds the location "woods" with a boss chance of 35
    And a mod run holds the boss chance 0 and changes nothing else of that map
    When the Archiver archives that run
    Then no row of the kind "location" exists for "woods"

  @unverified
  Scenario: The rows of these objects do not move the content hash
    Given a mod run that changes a location, a bot type, a hideout part, a price and a handbook entry
    When I compute the content hash of that run
    Then the hash is the same with and without the object rows
```

## Action Items

1. [x] Widen the three kind checks and add `mod_database_objects` (`20261021180615_AddDatabaseObjects`).
2. [x] Read the five tables of a run and of its baseline, and pair them by address
   (`SptArchive.Core.Canonical.DatabaseObjects`).
3. [x] Write the templates, the paths, the source rows and the id map in the import transaction
   (`RunImporter`).
4. [x] Count the five kinds in the result of a run (`RunResult`).
5. [ ] Expose the rows in the Api (ADR-0032 action item 7).
6. [ ] Measure the size of the dataset of a real map mod, and the time to import it.

## Links

- Specs: `docs/specs/mod-registry.feature`, section "Change to data that has no mod table".
- ADR-0002 (the source is kept whole), ADR-0003 (official data and mod data stay apart), ADR-0028
  (canonical form and volatile paths), ADR-0029 (the content hash), ADR-0032 (one row for each changed
  value), ADR-0034 (an object is named by what it is), ADR-0035 (official data comes from a mod-free run).
- Code: `src/SptArchive.Core/Canonical/DatabaseObjects.cs`,
  `src/SptArchive.Core/Entities/ModDatabaseObject.cs`, `src/SptArchive.Archiver/Runs/RunImporter.cs`.
- Tech debt: TD-45 (this decision), TD-47 (the hash of an unheld file), TD-52 (a filter on a jsonb column).
