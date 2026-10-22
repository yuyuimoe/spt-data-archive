# ADR-0043: Split a loose-loot file into one row for each spawn point

**Status:** Proposed
**Date:** 2026-10-21
**Deciders:** Yui

## Context

ADR-0041, decision 2, holds the source of a location file in one `jsonb` row. ADR-0041, amendment 1,
refuses that row above the limit of the Api. So a loose-loot file of 132,164,983 bytes has no row, and the
archive states only that the file moved.

Measured on 2026-10-21 in the 19 exports of the development machine, against the baseline export:

- Twelve loose-loot files differ from the baseline. They take 553,046,567 bytes.
- Those files hold 29,352 spawn points. 28,931 of them are byte-identical to the baseline.
- `FactoryClassic` replaces the loot of two factory maps: 400 spawn points changed, and 614 were removed.
- `WTT-Armory` changes ten maps and adds 21 spawn points. It changes none and removes none. Those ten files
  take 548,447,155 bytes, and the 21 added spawn points take 44,347 bytes.

So the archive holds 548 MB for one mod to record 44 kB.

A spawn point is a small object with a name. Its `template.Id` is authored by the map:
`lighthouse key [2d5cd6a5-995d-49ba-b203-60bf90058743]`. The name stays when the spawn chance changes.
3,600 of the 3,609 names of the lighthouse file are unique. Four names repeat three or four times, at
different positions, and each of those spawn points holds other items.

The largest spawn point of that file takes 247,545 bytes, which is below the limit of 2,000,000 bytes of
the Api (ADR-0040, amendment 1). So the Api can serve one spawn point whole.

## Decision

**We will give each spawn point of a loose-loot file its own row, and store only the spawn points that the
run adds, changes or removes.**

1. **A loose-loot file is a directory of objects, not one object.** For each file
   `locations/<map>/looseLoot.json`, each entry of `spawnpoints` and each entry of `spawnpointsForced` is one
   object of kind `location`. The map stays the template row, as it is today (ADR-0041, decision 4), and the
   spawn point is a part of the file that the template holds.

2. **The name of a spawn point is its `template.Id`.** When the file repeats a name, its ordinal in file
   order names it, as ADR-0034 names a tie. The name is never hashed, and `mod_id_maps` keeps the raw
   `template.Id` and the raw `Root` of each run.

3. **The split covers every loose-loot file, whatever its size.** A row for the whole file would answer a
   different question for a different map, and a client that asks for the loot of a map must not learn the
   size of the file to know which field to read.

4. **An added spawn point is one row of `mod_database_objects`.** The row holds the spawn point whole, as
   the run wrote it, and states its size in `bytes`. It is served by `rawJson`, because 36,558 bytes on
   average and 247,545 bytes at most are below the limit. A row holds one spawn point and never the file.

5. **A changed spawn point is rows of `mod_change_paths`, one for each changed leaf.** The path starts with
   the file and the spawn point, then the RFC 6901 path inside it:
   `/looseLoot/spawnpoints/<name>/template/Items/0/upd/StackObjectsCount`. A removed spawn point is one row
   with the operation `remove` and the whole spawn point as the old value. `spawnpointCount` is two change
   paths, `mean` and `std`.

6. **The order of the array is not a change.** The archive compares the spawn points by name. A run that
   only moves a spawn point in the array makes no row. No export of the 19 reordered a spawn point.

7. **The file still enters the content hash** (ADR-0029, decision 1 and amendment 4, and ADR-0041,
   decision 6). The rows say what moved. The hash says that something moved, and it counts the order.

8. **A spawn point keeps the address of the baseline.** An id that names another object of the run becomes
   an address, in a key and in a value (ADR-0034, decisions 5 and 6). The same run archived twice therefore
   writes the same rows.

9. **No official table is touched** (ADR-0003). Every row is keyed by the dataset id.

## Options Considered

| Dimension | A. The whole file in one row | B. The hash only (today) | C. One row for each spawn point (chosen) |
|---|---|---|---|
| Answers "what did WTT add to lighthouse" | Yes, below the limit only | No | Yes, 2 rows |
| Bytes for the ten `WTT-Armory` maps | 548 MB, refused above the limit | 0 | 44 kB of added spawn points |
| Bytes for `FactoryClassic` | 4.6 MB, refused above the limit | 0 | 2.2 MB of values, and 302 removals |
| A page of loot of one map | One document of 132 MB | Nothing | One page of spawn points |
| Work | None new | None | A reader for the shape, a name to an address |

Option A buys no read above the limit, because the Api refuses the document before it reads it. Option B is
honest about the change and silent about its content. Option C costs one reader and answers with the object
that the map author wrote.

## Trade-off Analysis

The trade-off is one reader against the bytes of a file, and the bytes are not close.

`WTT-Armory` adds 21 spawn points in ten maps. Option A holds 548 MB for those 21 spawn points, and it can
serve none of them. Option C holds 44 kB, and the Api serves each of them whole, because a spawn point is
smaller than the limit.

The split is not a saving when a mod replaces a map. `FactoryClassic` changes 400 spawn points and removes
614, so it writes 201 new spawn points of `factory4_day` and 302 removals, and the new values take 2.2 MB.
The file takes 2,225,344 bytes, which is above the limit, so option A would hold nothing that a client can
read. Option C holds the change, and each spawn point stays readable. The row count grows, and the bytes
stay near the size of the file.

The risk is the name. A map author who renames a spawn point makes one removed row and one added row, and
the address of that loot point moves. Measured on the 19 exports, no author renamed one. When a rename
arrives, the ordinal keeps the tie apart and the cost is two rows.

The other risk is the shape. A reader that knows a loose-loot file is a reader that breaks when a release
writes another shape. The archive already refuses a run that it cannot account for (ADR-0029), so a shape
that it does not know stops the import and does not corrupt it.

## Consequences

- **Easier:** a client asks which spawn points a dataset adds to a map, and reads each one whole.
- **Easier:** a mod that adds two loot points writes two rows, not 132 MB.
- **Harder:** the Archiver owns a reader for one file shape. Another file shape needs its own decision.
- **Harder:** a client that wants the file as one document follows the source link of the run, as
  ADR-0041, amendment 1 states.
- **Revisit when:** a spawn point itself grows above 2 MB, which no export measured, or a release moves the
  spawn points out of `locations/<map>/looseLoot.json`.

## Verification

```gherkin
Feature: A loose-loot file is one row for each spawn point

  @unverified
  Scenario: A mod that adds two spawn points writes two rows
    Given the baseline export of the map "lighthouse" with 3,609 spawn points
    And a run of a mod that adds two spawn points to that map
    When the Archiver archives the run
    Then the table "mod_database_objects" holds two rows for that map
    And no row states the 3,609 spawn points that did not change

  @unverified
  Scenario: A map that replaces its loot writes a removal for each spawn point that it loses
    Given the baseline export of the map "factory4_day" with 503 spawn points
    And a run of a mod that holds 199 spawn points of that map
    When the Archiver archives the run
    Then the table "mod_change_paths" holds 302 rows with the operation "remove"
    And each of those rows holds one whole spawn point as the old value

  @unverified
  Scenario: A spawn point is served whole
    Given a dataset that adds one spawn point of 247,545 bytes to the map "lighthouse"
    When a client asks for the objects of that dataset and that map as whole documents
    Then the answer holds that spawn point as JSON
    And the server did not refuse the document for its size

  @unverified
  Scenario: A renamed spawn point is one removal and one addition
    Given the baseline export of a map with a spawn point named "lighthouse key"
    And a run of a mod that names the same spawn point "lighthouse key 2"
    When the Archiver archives the run
    Then the table "mod_change_paths" holds one row with the operation "remove"
    And the table "mod_database_objects" holds one row for the new name

  @unverified
  Scenario: A reordered array makes no row
    Given a run that holds the same spawn points in another order
    When the Archiver archives the run
    Then the table "mod_change_paths" holds no row for that map
    And the content hash of the dataset differs from the dataset of the baseline
```

## Action Items

1. [ ] Write the reader of a loose-loot file, and the name to a part and a path segment. It reads the file
   as a stream, because the largest one takes 132 MB.
2. [ ] Give a spawn point its address, and keep `template.Id` and `Root` in `mod_id_maps` (ADR-0034).
3. [ ] Write the added spawn points in `mod_database_objects`, the changed ones in `mod_change_paths`, and
   the removed ones as one row with the operation `remove`.
4. [ ] Measure the import of the ten `WTT-Armory` maps and of `FactoryClassic`: the rows, the bytes, and the
   time against the 1,265 ms of a whole file.
5. [ ] Add the scenario tags to `docs/specs`, and remove `@unverified` when the tests pass.
6. [ ] Decide whether another file shape of a location needs its own rule: `chance.json` and the other
   files of a map are below the limit today.

## Links

- ADR-0003: official data and mod data stay apart.
- ADR-0029: the content hash. The file still enters it.
- ADR-0032: `mod_change_paths`, one row for each changed value.
- ADR-0034: the address of an object. A name is never hashed.
- ADR-0040, amendment 1: the limit of 2,000,000 bytes for a whole document.
- ADR-0041: the rows that have no mod table, and amendment 1, which refuses a file above the limit.
- ADR-0042: the pool behind the address of a change. This ADR gives the address that repeats.
- `docs/tech-debt.md`: TD-67, TD-69, TD-72.
