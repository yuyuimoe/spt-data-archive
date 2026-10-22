# ADR-0044: Split every file that a run changes into one row for each object it holds

**Status:** Proposed
**Date:** 2026-10-21
**Deciders:** Yui

## Context

ADR-0041 gave a row to the five tables that the server keeps as one document: a location, a bot type, a
hideout part, a price and a handbook entry. ADR-0043 added the spawn point of a loose-loot file. Three
tables were already rows before them: the items of `templates/items.json`, the quests of
`templates/quests.json`, the customizations of `templates/customization.json`, the base of each trader and
each offer of an assortment, and every key of `locales/<scope>/<language>.json`.

So the archive reports the rest as a file that changed with no row of its own. Measured on 2026-10-21 in
the 19 exports of the development machine, against the baseline export of 4.1.6:

- 107 changed or new JSON files are above 2,000,000 bytes, and together they take 1,348,928,941 bytes.
  Of those, 70 are translation files, which the archive already holds as one row for each key.
- The `WTT-Armory` run changes `templates/dialogue.json` (4,694,993 bytes), `templates/profiles.json`
  (1,078,871 bytes), `globals.json` (704,308 bytes), `settings.json` (345,329 bytes) and the files of the
  hideout that hold no list.
- Six directories of the development machine hold a whole-table export: `templates.json` of 23,790,813
  bytes, `locales.json` of 51,429,107 bytes and `traders.json` of 2,614,662 bytes at the root. They hold
  the table that the server keeps, and not a run: the exporter writes them with the `export` verb, and the
  Archiver imports no run from them (ADR-0027, ADR-0029).

A client that asks "what did this mod do to the dialogue of a trader" gets a canonical hash of a 4.7 MB
file, and nothing it can read. The same client that asks for a translation gets the key.

## Decision

**We will give each object of every JSON file that a run changes a row, and the rows of one file the shape
that file has.**

1. **A file of lists is a directory of objects.** A file whose root is an array, or an object that holds
   arrays, is split by the objects of its lists, and each object is named by the id it carries. The rule
   that ADR-0041 wrote for a handbook and a hideout already does this; this decision applies it to every
   file that the archive reports in `FilesWithoutRows`.

2. **A file with no list is one object, named by its path.** `globals.json`, `settings.json` and a file of
   the top of a map stay one row, because the server reads them whole. A value inside it is a path, the way
   a value of a bot file is today.

3. **A dialogue file is split by its node.** `templates/dialogue.json` is an object whose keys are the ids
   of traders, and each of those is an object whose keys are the ids of dialogue nodes. The trader is the
   object and the node is its part, so a changed line is a path under the trader and the node.

4. **No file above the limit of the Api becomes one row.** A row holds one object. When an object itself is
   above `RawJson:MaxValueBytes`, its row states its size, its hash and its source link, and `rawJson`
   refuses it, as ADR-0041, amendment 1 states for a file.

5. **The content hash does not move.** The canonical hash of every file stays in the basis of the dataset
   hash (ADR-0029, amendment 4), so a dataset that the archive holds keeps its hash and this decision
   writes no new dataset (ADR-0013).

6. **A dataset of the archive is not rewritten.** An import before this decision keeps the rows it had, and
   this decision gives no way to rebuild them. That is TD-75, and it is the same limit that ADR-0043 met.

7. **A whole-table export is no run and needs no split.** The archive imports a run, and a run holds the
   files of the server in the layout of `SPT_Data` (ADR-0027). The three files of a whole-table export are
   a diagnostic of the exporter, and TD-74 gives the link to the source of a file.

8. **No official table is touched** (ADR-0003). Every row is keyed by the dataset id.

## Options Considered

| Dimension | A. The whole file in one row | B. The hash only (today) | C. One row for each object (chosen) |
|---|---|---|---|
| Answers "what did the mod change in the dialogue" | Only below 2 MB, and then one 4.7 MB document | No | Yes, one path for each changed line |
| Bytes for `templates/dialogue.json` | 4.7 MB, refused | 0 | The changed nodes |
| Answers "which translation changed" | One document for each language | No | Already one row for each key |
| Work | None new | None | A reader for each file shape |
| A new file shape of a future release | Nothing | Nothing | One more reader, and the import stops until it exists |

Option A buys no read for the files that matter, because each of them is above the limit. Option B is
honest about the change and silent about its content. Option C costs one reader for each file shape.

## Trade-off Analysis

The trade-off is a reader for each file shape against the answer a client gets, and the readers do not
share one shape. A dialogue is an object of objects of objects; a profile is an object of keys; a hideout
list is an array of ids. ADR-0041 already accepted one reader for five tables, and this decision makes that
reader a family.

The other side is the guard. The archive refuses a run whose files it cannot account for (ADR-0029), so a
release that writes another file shape stops the import until the archive knows it. That is what a reader
that must explain every byte buys, and it is the same protection that ADR-0043 accepted.

Option C is not a saving of bytes when a mod rewrites a file, the way ADR-0043 was not a saving for
`FactoryClassic`. It is a saving of bytes when a mod changes one line of a dialogue of 4.7 MB, and it is the
difference between a readable change and a hash.

The last trade-off is the one the driver asked about: whether every value should become a column of a
table, with the relations of a normalized model. It should not, and the reason is measured:

- A change row already holds one value: its address, its path, its old value and its new value. There is no
  smaller detail of a change than one leaf, and the archive writes one row for each leaf today
  (ADR-0032, decision 2). A normalized table would not find a finer change.
- The properties of an item are not the properties of a release. A mod writes a key the release never
  wrote, such as `_props.Slots.0._props.filters.0.Filter`, and a table of columns refuses it or grows a
  column for it. ADR-0002 keeps the source object whole, and this archive answers about a key it does not
  know by a path over `jsonb`.
- The measured cost of the shape: 3,545 change rows take 53 MB inline against 22 MB with the addresses and
  the values pooled (ADR-0042). The pool pays from 1.6 rows for each address, and the archive holds one.
  A norm of five forms would pool more and would pay less; it needs the stable addresses that TD-72 asks
  for first.

So a table of columns is a decision for the values that a client filters and sorts, and this ADR does not
make it. TD-68 (a catalogue of the paths of a `jsonb` document) is the piece that makes a path searchable
without that table.

## Consequences

- **Easier:** a client reads the dialogue, the profile and the settings change of a dataset by object.
- **Easier:** a row states the id that the mod wrote, so a client follows one dialogue node.
- **Harder:** the Archiver owns a reader for each file shape, and a release can add one.
- **Harder:** the row count of a dataset grows with the objects that a mod changes, not with the files.
- **Revisit when:** a file shape changes in a release, or an object of a row is above the limit of `rawJson`.

## Verification

```gherkin
Feature: Every file that a run changes holds a row for each of its objects

  @unverified
  Scenario: A changed dialogue line is one path under its trader and its node
    Given a baseline that holds the dialogue node "firstMeeting" of the trader "579dc571d53a0658a154fbec"
    And a run that changes one line of that node
    When the Archiver archives the run
    Then the table "mod_change_paths" holds one row for that trader and that node
    And the row holds the old line and the new line

  @unverified
  Scenario: A file that the server reads whole is one object with paths
    Given a baseline whose "globals.json" holds "{"time": 1}"
    And a run whose "globals.json" holds "{"time": 2}"
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "global"
    And the table "mod_change_paths" holds the path "/time"

  @unverified
  Scenario: A file that no reader knows stops the import
    Given a run that writes "templates/anUnknownTable.json", which the archive does not know
    When the Archiver archives the run
    Then the run fails and states that file
    And the archive writes no dataset

  @unverified
  Scenario: A whole-table export is not a run
    Given a directory that holds "templates.json", "locales.json" and "traders.json" and no "run" directory
    When the Archiver is asked to archive it as a run
    Then it refuses it
    And no dataset is written
```

## Action Items

1. [ ] Measure `FilesWithoutRows` over the 19 exports: the file, the bytes and the objects of each.
2. [ ] Write the reader of the dialogue, the profile, the achievements, the prestige, the default equipment
   presets, the custom achievements, the character, the location services and the files that stay whole,
   and one test for each shape.
3. [ ] Add the kind of each new row to `ModTemplateKind`, and the filter of the Api to it.
4. [ ] Add the scenarios to `docs/specs`, and remove `@unverified` when the tests pass.
5. [ ] Rebuild the rows of a dataset that the archive holds (TD-75).
6. [ ] Link the source of a file (ADR-0045, TD-74).

## Links

- ADR-0002: keep the source object whole.
- ADR-0029, amendment 4: the content hash covers every file.
- ADR-0032: one row for each changed value.
- ADR-0041: the five tables that have no table of their own.
- ADR-0042: the pool of addresses and values.
- ADR-0043: the spawn point of a loose-loot file.
- ADR-0045: the link to the source of a file.
- `docs/tech-debt.md`: TD-68, TD-72, TD-74, TD-75.
