# ADR-0044: Split every file that a run changes into one row for each object it holds

**Status:** Accepted
**Date:** 2026-10-21
**Deciders:** Yui

Ratified by Yui on 2026-10-21. The Context below is the correction that the measurement of that day made
to the first draft of this decision.

## Context

ADR-0041 gave a row to the five tables that the server keeps as one document: a location, a bot type, a
hideout part, a price and a handbook entry. ADR-0043 added the spawn point of a loose-loot file. Six kinds
of file already held rows before them:

- `templates/items.json`, `templates/quests.json` and `templates/customization.json`: one row for each id.
- `locales/<scope>/<language>.json`: one row for each key.
- A trader: one row for each of `base.json`, `dialogue.json`, `suits.json`, `services.json`,
  `assort.json` and `questassort.json`.
- A map: one row for each file of its directory, named by the file (`base`, `looseLoot`, `statics`,
  `staticLoot`, `staticContainers`, `staticAmmo`, `allExtracts`).

Seven files of a run still hold no row. Measured on 2026-10-21 in the baseline export of 4.1.6:

| File | Bytes | Root | The object is named by |
|---|---|---|---|
| `globals.json` | 704,308 | an object of 5 keys | the preset id, the `Role`, the `Name`, or the key |
| `settings.json` | 345,329 | an object of one key, `config` | the key |
| `templates/dialogue.json` | 4,693,993 | `elements`, a list of 53 | `Id`; a line by its `Id` |
| `templates/profiles.json` | 1,078,871 | 9 editions | the edition name |
| `templates/achievements.json` | 121,613 | a list of 79 | `id` |
| `templates/character.json` | 12,610 | a list of 546 strings | the string |
| `templates/customisationStorage.json` | 3,330 | a list of 93 | `id` |

Four of those seven changed in the 19 exports of the development machine, against that baseline:

| File | Run | Bytes of the changed copy |
|---|---|---|
| `globals.json` | `WTT-Armory` | 811,221 |
| `templates/achievements.json` | `WTT-Armory` | 131,331 |
| `templates/character.json` | `AllTheClothes`, `FemalePMCVoice` | 14,743 and 12,637 |
| `templates/customisationStorage.json` | `AllTheClothes`, `FemalePMCVoice` | 6,368 and 3,398 |

That is six files and 979,698 bytes. `templates/dialogue.json`, `templates/profiles.json` and
`settings.json` are byte-identical to the baseline in all 19 exports.

A client that asks "which item preset did this mod change" gets the canonical hash of `globals.json` and
nothing it can read, and that file holds 408 item presets in the baseline (`$.ItemPresets`, an object keyed by preset id; `WTT-Armory` holds 488). The same client that asks for a translation, an
item or an offer gets a row.

### The correction of 2026-10-21

The first draft of this decision stated that 107 changed files above 2,000,000 bytes take 1,348,928,941
bytes, and that `WTT-Armory` changes `templates/dialogue.json` (4,694,993 bytes) and `settings.json`
(345,329 bytes). The measurement found both statements wrong:

- The 1,348,928,941 bytes counted 70 translation files and 12 location files, which already hold a row for
  each key or for each file part, and the six whole-table exports of `templates.json`, `locales.json` and
  `traders.json`, which are not runs (decision 8).
- `templates/dialogue.json` and `settings.json` do not differ from the baseline in any of the 19 exports.

So the case for this decision is what a client can read, and not how many bytes move. The seven files hold
the largest documents of the server that a client cannot open.

## Decision

**We will give each object of every JSON file that a run changes a row, and the rows of one file the shape
that file has.**

1. **A list is a directory of objects.** A file whose root is a list is split by its objects, and each
   object is named by the key that it carries. The key differs by file: `Id` in a dialogue element, `id` in
   an achievement and in a storage entry, `_id` in an item. A list of strings, such as
   `templates/character.json`, is split by the string. A list whose objects carry no such key is split by
   its place, the way ADR-0043 names a spawn point that carries no `template.Id`.

2. **An object that holds objects is a directory too.** `globals.json` holds four parts that a mod edits:
   `ItemPresets`, 408 presets keyed by preset id; `bot_presets`, a list of 28 objects named by `Role` and `BotDifficulty`;
   `BotWeaponScatterings`, a list of 3 objects named by `Name`; and `LocationInfection`, an object of 10
   numbers. Each of them becomes a row for each object. `templates/profiles.json` holds 9 editions, and
   each edition is a row named by the edition.

3. **An object with no list stays one object, and a value inside it is a path.** `globals.json.config`
   holds 107 keys, `settings.json.config` holds the settings of the server, and the `StartPoints` of a
   dialogue element holds the entry points of that dialogue. Each stays one object, and a changed key
   inside it is a path, as a value of a bot file is today.

4. **A dialogue is a list of elements, and a line is the part.** `templates/dialogue.json` holds
   `elements`: 53 objects that carry `Id`, `Trader`, `Lines`, `StartPoints`, `SubTraders` and
   `localization`. The element is the object and the line is its part, so a changed line is a path under
   the element and the line. The trader is a value of the element and not its name, because one trader can
   hold more than one element.

5. **No file above the limit of the Api becomes one row.** A row holds one object. When an object itself is
   above `RawJson:MaxValueBytes`, its row states its size, its hash and its source link, and `rawJson`
   refuses it, as ADR-0041, amendment 1 states for a file.

6. **The content hash does not move.** The canonical hash of every file stays in the basis of the dataset
   hash (ADR-0029, amendment 4), so a dataset that the archive holds keeps its hash and this decision
   writes no new dataset (ADR-0013).

7. **A dataset of the archive is not rewritten.** An import before this decision keeps the rows it had, and
   this decision gives no way to rebuild them. That is TD-75, and it is the same limit that ADR-0043 met.

8. **A whole-table export is no run and needs no split.** The archive imports a run, and a run holds the
   files of the server in the layout of `SPT_Data` (ADR-0027). The three files of a whole-table export are a
   diagnostic of the exporter, and ADR-0045 gives the link to the source of a file.

9. **No official table is touched** (ADR-0003). Every row is keyed by the dataset id.

## Options Considered

| Dimension | A. The whole file in one row | B. The hash only (today) | C. One row for each object (chosen) |
|---|---|---|---|
| Answers "which item preset did the mod change" | No, and the row holds 408 presets | No | Yes, one path under the preset |
| Bytes for `globals.json` | 811,221, below the limit | 0 | The changed presets, and no copy of the rest |
| Bytes for `templates/dialogue.json` | 4,693,993, above the limit, refused | 0 | The changed lines |
| Work | None new | None | A reader for each file shape |
| A new file shape of a future release | Nothing | Nothing | One more reader, and the import stops until it exists |

Option A is enough for the four files of a real run that changed and are below the limit, and it costs one
row and no reader. Option C also covers the two files above or near it, and it answers with the object that
moved. The archive chooses C, because a change is a path over an object that has a name, and because
`templates/dialogue.json` and `templates/profiles.json` are at or above the limit of the Api.

## Trade-off Analysis

The trade-off is a reader for each file shape against the answer that a client gets, and the readers do not
share one shape. A dialogue is a list of objects of lists; a profile is an object of editions; a globals
file holds a list, a map by preset id, and an object of numbers. ADR-0041 already accepted one reader for
five tables, and this decision makes that reader a family.

The second side is the number of rows. `globals.json` is 408 rows for the presets and 28 for the bot
presets, where a whole-file row would be one. The archive writes a row only for an object that differs, so a
dataset pays for what its mod changed.

The third side is the guard. The archive refuses a run whose files it cannot account for (ADR-0029), so a
release that writes another file shape stops the import until the archive knows it. That is what a reader
that must explain every byte buys, and it is the same protection that ADR-0043 accepted.

The last trade-off is the one that the driver asked about: whether every value should become a column of a
table, with the relations of a normalized model. It should not, and the reason is measured:

- A change row already holds one value: its address, its path, its old value and its new value. There is no
  smaller detail of a change than one leaf, and the archive writes one row for each leaf today
  (ADR-0032, decision 2). A normalized table would not find a finer change.
- The properties of an item are not the properties of a release. A mod writes a key that the release never
  wrote, such as `_props.Slots.0._props.filters.0.Filter`, and a table of columns refuses it or grows a
  column for it. ADR-0002 keeps the source object whole, and this archive answers about a key that it does
  not know by a path over `jsonb`.
- The measured cost of the shape: 3,545 change rows take 53 MB inline against 22 MB with the addresses and
  the values pooled (ADR-0042). The pool pays from 1.6 rows for each address, and the archive holds one. A
  norm of five forms would pool more and would pay less; it needs the stable addresses that TD-72 asks for
  first.

So a table of columns is a decision for the values that a client filters and sorts, and this ADR does not
make it. TD-68 (a catalogue of the paths of a `jsonb` document) is the piece that makes a path searchable
without that table.

## Consequences

- **Easier:** a client reads the dialogue line, the profile, the preset and the achievement change of a
  dataset by object.
- **Easier:** a row states the id that the mod wrote, so a client follows one dialogue node or one preset.
- **Harder:** the Archiver owns a reader for each file shape, and a release can add one.
- **Harder:** the row count of a dataset grows with the objects that a mod changes, not with the files.
- **Revisit when:** a file shape changes in a release, or an object of a row is above the limit of `rawJson`.

## Verification

```gherkin
Feature: Every file that a run changes holds a row for each of its objects

  @unverified
  Scenario: A changed dialogue line is one path under its element and its line
    Given a baseline that holds the dialogue element "683dafc7a7c0a468ac8a9b05" with one line
    And a run that changes one field of that line
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "dialogue" for that element
    And the table "mod_change_paths" holds one row whose path starts with "/Lines/"
    And the row holds the old value and the new value

  @unverified
  Scenario: A changed item preset is one row
    Given a baseline whose "globals.json" holds the preset "5841474424597759ba49be91" with a price of 1
    And a run whose preset "5841474424597759ba49be91" holds a price of 2
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "preset" named by the preset id
    And the table "mod_change_paths" holds the path "/price"

  @unverified
  Scenario: A file that the server reads whole is one object with paths
    Given a baseline whose "settings.json" holds a "config" object with the key "time" set to 1
    And a run whose "settings.json" holds the same key set to 2
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "setting"
    And the table "mod_change_paths" holds the path "/config/time"

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

1. [x] Measure the files that hold no row over the 19 exports: the file, the bytes and the objects of each.
   Done on 2026-10-21: seven files, four of them changed in six copies, 979,698 bytes.
2. [x] Write the reader of the dialogue, the profile, the globals file, the achievements, the character, the
   customisation storage and the settings, and one test for each shape. Done on 2026-10-21; see amendment 1.
3. [x] Add the kind of each new row to `ModTemplateKind`, and the filter of the Api to it. Done on 2026-10-21:
   ten kinds, and the migration `WidenKindChecks` for the four kind checks. The Api filters on `kind` as a
   string, so it needed no change.
4. [ ] Add the scenarios to `docs/specs`, and remove `@unverified` when the tests pass. The eight scenarios of
   `docs/specs/file-rows.feature` exist and keep `@unverified`: the file is not bound to Reqnroll yet (TD-62). The
   xUnit tests in `FileRowTests`, `KindCheckTests` and `DatabaseObjectTests` prove the same behavior.
5. [ ] Rebuild the rows of a dataset that the archive holds (TD-75).
6. [ ] Link the source of a file (ADR-0045, TD-74).

## Amendment 1 (2026-10-21): what the readers do, and what the measurement corrected

Implemented in `DatabaseObjects.cs`, the reader of ADR-0041 and ADR-0043. A row is a template under the
dataset; an object that the run adds also has its source in `mod_database_objects`; a modified object has paths
and no source; a removed object has a template row and no source (the baseline holds the old value).

| File | Root shape | One row is | Kind | Name of the row |
|---|---|---|---|---|
| `globals.json` | object of 5 keys | one preset of `ItemPresets` | `preset` | the preset id (the key) |
| | | one entry of `bot_presets` | `bot-preset` | `Role/BotDifficulty` |
| | | one entry of `BotWeaponScatterings` | `scattering` | `Name` |
| | | one other key (`config`, `LocationInfection`) | `global` | the key |
| `settings.json` | `{"config": {...}}` | the whole file | `setting` | `settings` |
| `templates/dialogue.json` | `{"elements": [...]}` | one element; a line is a path | `dialogue` | `Id` |
| `templates/profiles.json` | object of editions | one edition | `profile` | the edition name |
| `templates/achievements.json` | list of objects | one object | `achievement` | `id` |
| `templates/customisationStorage.json` | list of objects | one object | `storage` | `id` |
| `templates/character.json` | list of strings | one string | `character` | the string |

Corrections of the Accepted text, each with its evidence:

- `$.ItemPresets` of `baseline/globals.json` holds 408 presets (an object keyed by preset id). The 488 of the
  first text is the count of `WTT-Armory`, which adds 80.
- `$.bot_presets` of the baseline holds 28 entries and only 22 distinct `Role` values: a role has one preset for
  each difficulty. `Role` alone would have merged them, so `Role/BotDifficulty` names a row (28 distinct).
- A list of strings is a row for each string, as decision 1 says. A row of the kind `character` has no path,
  because the same string is the same row.
- A dialogue line is a path under its element, and the path is positional until TD-72 gives it a stable address.
- A new kind is written in `mod_database_objects`, so `ck_mod_database_objects_kind` and the three other kind
  checks (`mod_templates`, `mod_change_paths`, `mod_id_maps`) are widened to the ten kinds by the migration
  `20261022012549_WidenKindChecks`. `Down` deletes the rows of the new kinds and restores the old lists. This
  amends ADR-0041, which named five kinds in the check.
- The content hash did not move. The canonical hash of a changed file stays in `UnstoredChanges`, as the file of
  a location does (ADR-0041), so `DatasetHash` reads the same input. `FilesWithoutRows` lists a file only when
  no reader holds it, so it no longer lists these seven.

Measured on 2026-10-21 over the 13 exports of the development machine that hold a `templates` directory,
against the baseline of 4.1.6. The dataset hash of every one is the same at `7f60f8c` and with the readers
(`WTT-Armory`: `930727a4…0f45` both times). The files of the six copies that changed:

| Export | File | Rows | Source bytes | Paths | Path bytes |
|---|---|---|---|---|---|
| `WTT-Armory` | `globals.json` | 80 `preset` added; 1 `global` (`config`) modified | 100,652 | 63 | 5,479 |
| `WTT-Armory` | `templates/achievements.json` | 8 added | 9,710 | 0 | 0 |
| `AllTheClothes` | `templates/customisationStorage.json` | 45 added | 2,993 | 0 | 0 |
| `AllTheClothes` | `templates/character.json` | 79 added | 2,054 | 0 | 0 |
| `FemalePMCVoice` | `templates/customisationStorage.json` | 1 added | 67 | 0 | 0 |
| `FemalePMCVoice` | `templates/character.json` | 1 added | 26 | 0 | 0 |

The six copies take 979,698 bytes as files and 120,981 bytes as rows and paths. No export changed
`templates/dialogue.json`, `templates/profiles.json` or `settings.json`, so those three readers are proven by
tests on small trees and not by a real change. No row has an ordinal name in these exports. The list
`FilesWithoutRows` was `globals.json` and `templates/achievements.json` for `WTT-Armory`, and
`templates/character.json` and `templates/customisationStorage.json` for `AllTheClothes` and `FemalePMCVoice`;
it is empty for all of them now.

The scenario "A file that no reader knows stops the import" is not built. A changed file that no reader holds
keeps its canonical hash and appears in `FilesWithoutRows`, as before; the import does not refuse it.

## Links

- ADR-0002: keep the source object whole.
- ADR-0029, amendment 4: the content hash covers every file.
- ADR-0032: one row for each changed value.
- ADR-0041: the five tables that have no table of their own.
- ADR-0042: the pool of addresses and values.
- ADR-0043: the spawn point of a loose-loot file.
- ADR-0045: the link to the source of a file.
- `docs/tech-debt.md`: TD-68, TD-72, TD-74, TD-75, TD-76.
