# ADR-0029: Archive the files a mod ships, and its configuration

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0013, ADR-0023, ADR-0026, ADR-0027, ADR-0028

## Context

The archive stores the rows that a mod run produces and nothing else. A dataset has
`TableHashes` and `RowCounts`. It has no copy of the files that the mod reads to decide what to
do. ADR-0026 decided that a value mod's preset is stored as one jsonb document under the key
`preset`. That decision is Accepted and it is not implemented. The problem is larger than SVM.

Two runs show it.

**A mod can switch its own change off in a config file.** Lots of Loot Redux 4.2.2 ships
`config.json` and `Presets/default.json`. The preset holds `LooseLootMultiplier.<map> = 7.5` for
13 maps and `StaticLootMultiplier.<map> = 2`, but also `General.AllowLootOverlay = false`. The run
against 4.1.6 loaded the mod ("Preset 'default' successfully loaded") and changed no table row.
The archive would record that as a mod that does nothing. It is a mod that was told to do nothing,
and the file that says so was thrown away with the container.

**A mod can build its rows from a config file.** ECOTI 3.4.0 (server GUID
`com.lennoxp90.coti.server`, Forge GUID `com.lennoxp90.coti`) ships a server assembly that calls
the server API: `CotiItemFactory` makes item templates, `CotiTraderAssort` adds them to
Peacekeeper's assort, `CotiLootDistribution` adds spawns, `CotiSlotLocale` adds translations. Which
rows appear is decided by two files:

| File | Keys that decide the rows |
| --- | --- |
| `config/config.json` | `trader.loyaltyLevel`, `priceUsd`, `buyLimit`; `riser.loot.weightFraction`, `onBots`, `botChance`; `loot.weightFraction`; `hostEditor.autoDiscover`; `flea.playerSellable` |
| `config/riser-sights.json` | a `deny` list of eight template ids that the mod refuses to treat as risers ("a riser on the riser") |

Change `loot.weightFraction` and the location rows change. Add an id to `deny` and one item stops
being modified. The dataset that the archive stores depends on files that the archive does not
store, so it cannot be reproduced and it cannot be explained.

The config is also not the file in the download. `CotiServerConfig` rewrites `config.json` with
`File.WriteAllTextAsync` when it fills a default in. The effective configuration is the one on
disk at the end of the run.

Nine of the seventeen mod directories that the project has already run ship a file of this kind:

```
AllTheClothes/config/config.jsonc          FactoryClassic/config/config.json
FemalePMCVoice/config/config.json          Gunwright/db/config.json
LotsOfLootRedux/config.json                LotsOfLootRedux/Presets/default.json
WTT-Armory/db/CustomWeaponPresets/WeaponPresets.json
WTT-ContentBackport/db/CustomItems/*_config*.json
WTT-PackNStrapServer/config/config.jsonc   WTT-ServerCommonLib/config.jsonc
```

The container knows all of this already. The server injects `IReadOnlyList<SptMod>` (the same
object that `LauncherV2Controller.LoadedMods()` serves), and `SptMod` holds `Directory` and
`ModMetadata`: the GUID, the version, the declared SPT range, the dependencies and the bundle
flag. The exporter writes none of it. The dependency set of a run is typed by hand in
`run_mod.sh` (ADR-0013), so the archive cannot prove its own dependency set.

## Decision

1. A run export gains one root that is not a table: `run/`. It holds the run's own account of
   itself:
   - `run/loaded.json`: one entry per loaded mod, with `directory`, `modGuid`, `name`, `author`,
     `version`, `sptVersion` (the declared range), `modDependencies` and `hasPrepatcher`, read from
     `IReadOnlyList<SptMod>`. `IModMetadata` of 4.1.x has no `isBundleMod`; a `bundles.json` in the
     mod root in the manifest answers that question, with a hash.
   - `run/files/<directory>.json`: the **manifest** of the mod directory. One entry per file:
     relative path, size, SHA-256 and kind. No file bytes.
   - `run/config/<directory>/<path>`: the **text** of each configuration file, as it is on disk
     when the server stops.
2. A file is a configuration file when its path has a segment `config`, `configs`, `presets` or
   `loader`, or its name contains `config`, `settings`, `preset`, `option` or `loader`, or its
   extension is `.jsonc` (a file with comments is a file a human edits). A configuration file
   larger than 2 MB is recorded in the manifest and not copied. The rule stays in one place.
3. One new table `mod_files`, owned by the dataset (ADR-0013):

   | Column | Meaning |
   | --- | --- |
   | `dataset_id` | The dataset. Primary key together with `path`. |
   | `path` | Relative to the run's `user/mods` directory, so it starts with the mod directory (`WTT-Armory/config/config.json`). `/` is the separator. The directory is part of the path because one dataset loads a target and its pinned dependencies, and both may ship a file of the same name. |
   | `kind` | `assembly`, `bundle`, `data`, `config`, `text` or `other`. |
   | `size` | Bytes on disk. |
   | `sha256` | Hash of the file bytes when the run stopped. |
   | `shipped_sha256` | Hash of the file bytes before the server started, when the runner took the snapshot. A different value says that the mod rewrote the file during its load (ECOTI rewrites its own `config.json`; the snapshot shows it). Null when no pre-boot snapshot exists, as in an official import. |
   | `content_text` | The text, when rule 2 copied the file. Null otherwise. |
   | `content` | The parsed document as jsonb, when the text parses. Null otherwise. |

   `content_text` holds what the mod read: a `.jsonc` file with comments is not valid JSON, and a
   parse that drops the comments would store a file that never existed. `content` is the extra.
   The pre-boot snapshot is written by the runner (`run/pre-boot.json`) and not by the exporter,
   because the exporter runs after the mods have loaded.
4. `content_hash` (ADR-0013) covers the canonical table content **and** the configuration text and
   the manifest. A changed config makes a new dataset for the same mod version. That is the same
   rule as "two versions with the same content share one dataset", read the other way.
5. `run/loaded.json` is the `dependency_set` of the observation (ADR-0013). The Archiver computes
   its hash. A mod that is absent from the file did not load, so a run states that rather than
   guessing it (TD-36).
6. The archive runs a mod with the configuration that the mod ships. That run is the dataset of
the mod version: it says what the mod does out of the box. A config that switches a change off is
archived, so the empty diff has a reason next to it. The archive never flips a key by itself.
7. A **curated configuration** is a second run of the same version, never a replacement. A person
   edits the shipped file, or writes the preset that the mod's own tool would write, and the run
   records that the configuration is curated. That is the `curated_preset` of ADR-0026, decision 4.
   - The label sits on `mod_datasets`, in one column `config_origin` with the values `shipped` and
     `curated`, and the run records the reason. It does not sit on a row: `ModTemplate.Kind` is
     already `item`, `quest` or `trader` (ADR-0017), so a third value there would mix two meanings.
     This replaces the wording of ADR-0026 that the produced rows "carry the kind
     curated_preset".
   - The curated text is archived like any other configuration, so the edit is auditable: the
     stored file differs from the shipped one, and the difference is exactly the edit.
   - The shipped run stays. Both datasets are kept (ADR-0018).
   - A curated run exists when the shipped configuration has no function without an edit. Lots of
     Loot Redux with `General.AllowLootOverlay = false` is such a case: the driver asked for the
     curated run on 2026-10-20, because the overlay is the feature and the mod is inert without it.
     A curated run is a driver's decision with a recorded reason, never the archive's guess.
8. Decision 1 of ADR-0026 is replaced by rules 1 to 5: the `presets/` tree and `loader.json` of a
   value mod are configuration files under this rule, and they are stored per file, not as one
   blob. Decisions 2 to 4 of ADR-0026 stand, with the label rule of decision 7 above.

Two limits of the snapshot, written here so that no one reads them as a bug:

1. The snapshot is taken when every mod has finished `PostLoad`. A mod that writes its
   configuration later (on the first request, or when a raid is generated) is stored as the text
   that it read, and the next run of the same version shows the difference. The archive records
   what the mod read, not what the download contained; the downloaded bytes are reachable only
   through the artifact hash (TD-31).
2. A configuration that a mod reads from a path the rule of decision 2 does not name stays in the
   manifest and not in the text. The manifest still names it with its hash, so a reader sees that
   it exists and a later run can copy it.

## Options Considered

**A. Keep the preset as one jsonb document per dataset (ADR-0026, decision 1).** It stores one
mod's shape. It cannot name a file, cannot hash a file, and cannot say which file changed between
two runs. Rejected as the general rule; kept as the special case it describes.

**B. Store the whole mod directory, bytes included.** The archive would hold 6.1 GB of the WTT
content backport and 14.7 GB of assets for eleven mods (TD-34). The bytes of a bundle do not
decide a row; the manifest of the bundle does. Rejected.

**C. Read the configuration from the download artifact, not from the container.** The artifact is
the pre-run state, and a mod may write its own defaults into the file at load, so the artifact and
the state that produced the rows can differ. It also puts the downloader inside the Archiver, which
ADR-0013 keeps out. Rejected. The pre-boot snapshot answers the same question without the artifact:
`shipped_sha256` differs from `sha256` exactly when a mod rewrote a file. ECOTI 3.4.0 turned out not
to rewrite its own file; the runner records it either way.

**D. Store a manifest of every file and the text of every configuration file (chosen).** The
archive can reproduce the decision that produced the rows, prove which files a mod shipped, and
attribute a part of a mod (ADR-0023) with evidence rather than a human label.

## Trade-off Analysis

The manifest is cheap: 2512 ships 392 files, ECOTI ships 308, so a dataset grows by a few hundred
short rows and the export by a few kilobytes. The configuration text is smaller than a manifest of
the same files. Nothing is copied twice except a config file, and a config file is not in `db/`.

The cost is a rule that guesses what a configuration is. A mod can read a file that the rule does
not name, for example `riser-sights.json` under `config/`, which the rule catches only through its
directory. A mod can also keep parameters inside a `db/` fragment, which the rule catches only
through the file name. For that reason the manifest names every file with its hash: a reader sees
that a file exists, and a run can copy it later when a person says it matters. The rule is one
function, so a miss is one change and not a redesign.

Hashing the configuration into `content_hash` makes two datasets where a reader may expect one.
That is correct: the rows differ because the paths differ. The cost is that a player's tweak to one
price creates a dataset that only that player produced, and the archive must say so rather than
merge it. The archive runs one defined configuration per dataset, which is the shipped one.

## Consequences

- A mod that changes no row has a dataset with zero data rows and its configuration rows. The
  observation stays `archived`: the archive holds something, and what it holds is true. No new
  outcome value is needed for a mod whose effect happens when a raid is generated (ADR-0028,
  decision 6).
- The archive can answer "which config produced these 173 items" for ECOTI, and "the overlay is
  off" for Lots of Loot Redux.
- `run/loaded.json` gives the dependency set without a hand-typed list, and it names the version of
  every dependency, including the ones the Forge page does not declare (TD-30, TD-38).
- The exported file set is a fact, so a facet (ADR-0023) can be checked: a mod with no assembly
  and no `db/` ships bundles only.
- The API gains `modFiles(datasetId:)` next to the paired official and mod queryables, with the
  configuration text and without the file bytes. The text is readable as it was written, comments
  included, and `content` answers a query like `$.trader.priceUsd` when the file parses.
- A dataset says whether its configuration is the shipped one or a curated one, so a reader can ask
  "what does this mod do out of the box" and get an answer that the curated run does not mix into.
- An install has no `run/` root. An official run is one with no mod in `run/loaded.json`.
- TD-31 (no artifact hash) stays open: the manifest hashes the installed files, not the download.

## Verification

The runs of 2026-10-20 on 4.1.6 produced the evidence below. No scenario runs as a test yet
(TD-06), and no importer reads a run directory yet (TD-29), so the database half stays unverified.

```gherkin
@adr-0029 @unverified
Feature: Archive the files and the configuration of a mod

  Scenario: A run states the mods that loaded
    Given a run with ECOTI 3.4.0 and WTT-ServerCommonLib 3.0.6
    Then "run/loaded.json" holds "com.lennoxp90.coti.server" version "3.4.0"
    And it holds "com.wtt.commonlib" version "3.0.6"
    And it names the SPT range that each mod declares
    And the observation's dependency set is the same list, with its hash

  Scenario: A dependency that a mod declares is in the run
    Given a run whose target mod declares a dependency
    Then the entry for the target names the dependency GUID and its version range
    And a mod that declares no dependency has an empty dependency list

  Scenario: A configuration file is archived with its text
    Given a run with ECOTI 3.4.0
    Then the manifest holds "config/config.json" of kind "config"
    And the stored text holds "weightFraction"
    And the manifest holds "config/riser-sights.json"
    And the stored document parses to a "deny" list of eight ids

  Scenario: A configuration file with comments keeps its comments
    Given a run with a mod that ships a ".jsonc" configuration
    Then the manifest holds that file of kind "config"
    And the stored text is the file as it is on disk, comments included

  Scenario: The manifest names every file, and copies the configuration only
    Given a run with ECOTI 3.4.0, which ships 46 files
    Then the manifest has 46 entries, each with a size and a SHA-256
    And three of them are of kind "config" and are stored as text
    And the rest are named and not copied

  Scenario: A file that the mod rewrote at load is visible
    Given a run with a pre-boot snapshot
    Then a file whose "shipped_sha256" differs from its "sha256" was rewritten during the load
    And a file whose two hashes are equal was read as it shipped

  Scenario: A changed configuration makes a new dataset
    Given the shipped run of Lots of Loot Redux 4.2.2 with "AllowLootOverlay" false
    When the same version runs with "AllowLootOverlay" true and the same directory name
    Then the two manifests have the same paths
    And exactly one file differs: "Presets/default.json"
    And the two runs have different content hashes
    And the two datasets are distinct

  Scenario: A curated configuration is labelled and keeps the shipped one
    Given the shipped run of Lots of Loot Redux 4.2.2 with "AllowLootOverlay" false
    When a curated run of the same version turns "AllowLootOverlay" on
    Then "run/origin.json" says "curated" and states the reason
    And the curated dataset has "config_origin" "curated"
    And the shipped dataset still has "config_origin" "shipped"
    And both datasets exist
    And the run records whether the overlay changed a table row

  Scenario: A configuration decides the rows that the mod makes
    Given a run with ECOTI 3.4.0 on 4.1.6 without mods other than the pinned set
    Then the run adds 9 items, 9 Peacekeeper assort rows and 27 English translations
    And the configuration that decided those rows is in the dataset
```

## Action Items

- [x] Write `run/` in the exporter: `loaded.json`, the manifest per mod directory, the config text.
- [x] Add the `mod_files` table, `config_origin` on `mod_datasets` and the `content_hash` rule over
      the manifest and the config (migration `AddModFiles`).
- [x] Add the outcome values `no_server_data` and `loaded_but_disabled` to
      `ModObservationOutcome` and to the check constraint (TD-41).
- [x] Re-run Lots of Loot Redux (shipped and curated) and ECOTI on 4.1.6, and record the result in
      the runbook.
- [ ] Compute `dependency_set` and `dependency_set_hash` from `loaded.json` (ADR-0013, TD-36).
- [ ] Add `modFiles` to the API and to the Gherkin of `query-api.feature`.
- [ ] Read `run/pre-boot.json` and `run/origin.json` into `mod_files.shipped_sha256` and
      `mod_datasets.config_origin` (TD-29).

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): the source object stays next to the columns.
- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): a dataset is keyed by its content.
- [ADR-0023](0023-classify-a-mod-by-the-parts-it-ships.md): the parts that a mod ships.
- [ADR-0026](0026-archive-a-value-mod-preset.md): a value mod's preset, and a mod that disables itself.
- [ADR-0027](0027-archive-a-run-as-a-database-directory.md): the export is a database directory.
- [ADR-0028](0028-compare-two-runs-in-a-canonical-form.md): a change is a row that differs after canonicalization.
