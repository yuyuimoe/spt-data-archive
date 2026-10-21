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

2. **`mod_database_objects` holds the source of an added object.** Its key is
   `(dataset_id, kind, template_id, part)`. A location is a directory, so it holds one row for each of its
   files and `part` names the file (`base`, `looseLoot`). The other four kinds have one file and one value,
   so their `part` is empty and the row holds that value: the document of a bot type, the object of a
   hideout part, the number of a price, the entry of a handbook. No row holds more than one file, and no row
   holds a directory. The row keeps the source as the run wrote it, in `jsonb` (ADR-0002), and it states the
   size of that source in `bytes`, a column of its own, because the Api decides with it whether it may read
   the document at all (ADR-0040, amendment 1, and the measurement below). The table cascades from the
   dataset and from the template row.

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

10. **The Api exposes these rows as a view** (ADR-0032 action item 7). `modDatabaseObjects` and
    `modDatabaseObject` serve one row, its small facts as fields, `pathEq` over its document, and the whole
    document as `rawJson`, which costs a hundred times a small field and is refused above a limit of bytes
    (ADR-0040, amendment 1). The read-only role can read the table, and it cannot write it.

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

### What a document of that size costs, measured on 2026-10-21

PostgreSQL 17.11, `default_toast_compression = pglz`. The export of WTT-Armory holds 562 MB under
`locations/`, and its largest file, `lighthouse/looseLoot.json`, is 132,164,983 bytes (126 MiB).

| Form | Bytes | Note |
|------|-------|------|
| the file on disk | 132,164,983 | what the run wrote |
| `jsonb`, stored with pglz | 47,308,800 | the default, TOAST compresses every value above 2 KB |
| `jsonb`, stored with lz4 | 45,408,256 | `ALTER COLUMN … SET COMPRESSION lz4` |
| `text`, stored with pglz | 43,335,680 | a text column compresses a little better than jsonb |
| `jsonb` in memory | 174,699,552 | what one read assembles |
| gzip -6 of the file | 24,546,959 | 5.4 times the file |
| zstd -3 of the file | 14,267,278 | 9.3 times the file |

So **PostgreSQL does compress a jsonb column by default**, at about 2.8 times the file, and the answer to
"is it 126 MB in the row" is no: it is 45 MB of storage and 175 MB of memory per read.

Reading costs more than storing. On that row, `data -> 'spawnpointCount'` takes 138 ms with pglz and 56 ms
with lz4, and one `pathEq` filter over it 166 ms, because PostgreSQL detoasts the whole value for any part
of it. Casting the stored text back to jsonb costs 1,265 ms. The cast at import runs at about 70 MB/s, so the
562 MB of a map directory is roughly eight seconds of parsing, well inside the 120 seconds of lease silence
(ADR-0036), but no map run has been imported since the decision, so that number is a projection (TD-67).

Three answers follow, and this ADR takes the first.

1. **Keep `jsonb` and pay about twice the storage of gzip.** It is the only form in which `pathEq` is a
   database question with an error code, and the price of the other two answers is a filter that moves into
   the application.
2. **Store the file as a compressed `bytea` and decompress it in the request, with a cache of decompressed
   files on disk.** It halves the bytes and loses every filter, and it adds a second cache to invalidate on a
   container that writes nothing today (TD-69).
3. **Keep the file outside PostgreSQL**, in an object store, and keep the rows that name it.

The cluster setting `default_toast_compression = lz4` belongs with answer 1 and costs nothing: here it stored
4% fewer bytes and read 2.5 times faster than pglz.

## Consequences

- **Easier:** "What did this map mod change on woods?" has an answer with the old value and the new value.
  A map mod no longer stores a dataset with no row. A price and a handbook entry follow the same rule as an
  item, so a client learns one shape. An id that a run mints does not make a new dataset.
- **Easier:** The Api serves the rows, so "what did this map mod change on woods" is one GraphQL query.
- **Harder:** The archive keeps 45 MB where gzip would keep 24 MB, for every map file that a mod adds, because
  a filter is worth more than the bytes (TD-69).
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
5. [x] Expose the rows in the Api (ADR-0032 action item 7): `modDatabaseObjects`, `modDatabaseObject`,
   `pathEq` and `rawJson`, with the scenarios under "Objects that have no table of their own" in
   `docs/specs/mod-api.feature` and `ObjectApiTests`.
6. [x] Promote the size of the source to `mod_database_objects.bytes` (`20261021213335_AddObjectRowSize`).
7. [ ] Import one real map mod and measure the dataset, the storage and the time (TD-67). The sizes of the
   files are measured; the import is not.
8. [ ] Decide whether a large file leaves `jsonb` for a compressed value (TD-69), and whether the cluster
   sets `default_toast_compression = lz4`.

## Links

- Specs: `docs/specs/mod-registry.feature`, section "Change to data that has no mod table", and
  `docs/specs/mod-api.feature`, section "Objects that have no table of their own" (these run as tests).
- ADR-0002 (the source is kept whole), ADR-0003 (official data and mod data stay apart), ADR-0040 (a filter by
  path, and the whole-document field), ADR-0028
  (canonical form and volatile paths), ADR-0029 (the content hash), ADR-0032 (one row for each changed
  value), ADR-0034 (an object is named by what it is), ADR-0035 (official data comes from a mod-free run).
- Code: `src/SptArchive.Core/Canonical/DatabaseObjects.cs`,
  `src/SptArchive.Core/Entities/ModDatabaseObject.cs`, `src/SptArchive.Archiver/Runs/RunImporter.cs`.
- Tech debt: TD-45 (this decision), TD-47 (the hash of an unheld file), TD-52 (a filter on a jsonb column),
  TD-67 (the import of a real map is unmeasured), TD-69 (the storage of a large file).
