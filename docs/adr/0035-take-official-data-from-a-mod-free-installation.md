# ADR-0035: Take official data from an installation that loaded no mod

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui

## Context

`official` means the data that the server ships. ADR-0033, decision 1, says that an official run loads no mod, and that nothing checked it (TD-50).

Nothing checked it, and it was not true. The 4.1.6 release entered the archive as official data from an export of an installation that had loaded two library mods: `com.wtt.commonlib` 3.0.6 and `com.massivesoft.massivesoftcore` 1.0.2. The archive measured that source against a run of the same release with an empty mods folder:

- both sources hold 300 files and the same 4,673 items;
- 13 files differ only in the volatile fields that ADR-0028 covers (`BossChance`, `nextResupply` and the generated assort ids);
- all 18 locale files hold 4 strings that the two mods add: `SALVAGE_ZONE`, `CANT_SALVAGE_WHILE_MOVING`, `SALVAGING_OBJECTIVE` and `YOU_NEED_ITEM_TO_SALVAGE`.

So the official table of 4.1.6 held 4 mod strings per language. A mod dataset is the difference between a run and the official data of its release (ADR-0013). A difference against data that already holds mod strings is not the contribution of the mod.

The driver stated two rules on 2026-10-20: an official import cannot happen when mods are loaded, and the official import of a release must exist before any modded import.

## Decision

**We will take official data only from an installation that loaded no mod. We will also import the official data of a release before any run on that release.**

1. **An official import reads a source that loaded no mod.** The Archiver reads `run/loaded.json`. It stops when the list holds a mod other than the exporter. The exporter is the tool that wrote the export, so it is the one mod that an official source may carry.

2. **A source that states nothing imports with a warning.** A source without `run/loaded.json` cannot prove the rule. That is an export of an exporter older than 1.2.0, or a bare `SPT_Data/database` folder. The import warns and goes on, because a release archived once cannot be exported again by an exporter that never ran on it.

3. **The release of the source must be the release of the run.** `run/release.json` and `Archive__SptVersion` must agree (ADR-0014). A bare database folder states no release, so the operator states it.

4. **A run needs the official data of its release.** The importer stops when the archive holds no official row of that release. The check reads the official `items` of the release, and not a succeeded official run: the driver removed the run rows of the earlier imports on 2026-10-20, and the data that those rows described is still there and still true.

5. **The official data of 4.1.6 comes from a mod-free installation.** The driver ordered the replacement, and the archive took it on the same day.

## Options Considered

| Dimension        | A: refuse a modded source, warn on a silent one (chosen) | B: warn on both | C: refuse both |
|------------------|----------------------------------------------------------|-----------------|----------------|
| Complexity       | low: one file read, one check | low | low |
| Correctness      | the rule holds for every source that states its mods | the rule never holds | the rule holds, and an old release cannot enter |
| Re-import        | a native export imports | a modded export imports silently | a bare database folder is refused for ever |
| Cost of a source | one more container start | none | a release without an export is lost |

Option B leaves TD-50 open: the archive would keep accepting the kind of source that made the error.

Option C loses the only source that exists for some releases. The 4.0.13 release entered the archive as a database folder.

The ordering check has two options: read a succeeded `archive_runs` row, or read the official rows of the release. It reads the rows. A run row is history, and the driver deleted it once. The rows that it described stayed, and they are what a run is compared against.

## Trade-off Analysis

The trade-off is between a rule that always holds and a rule that applies to every release. The archive takes the second. It refuses a source that it can prove is wrong, and it warns about a source that it cannot see. The warning is the operator's work, and the run row states that it happened.

## Consequences

- **Easier:** a query for official data needs no filter for mod rows. Every official row comes from a source with an empty mods folder, or from a source that said nothing and was recorded as unverified.
- **Easier:** a mod dataset is a contribution again. Its difference is measured against data that the server alone produced.
- **Harder:** an installation that runs mods needs a second, empty installation to export its release. Measured cost: one container start.
- **Harder:** a source that cannot prove itself enters with a warning. An operator must read the log.
- **Revisit when:** the Runner (ADR-0030) exists. The Runner starts the container, so it can refuse a modded official run before the export, and not after.

## Verification

```gherkin
Feature: Take official data from an installation that loaded no mod

  # ADR-0035 decisions 1 and 5. A modded source is not official data.

  @unverified
  Scenario: An official import refuses a source that loaded a mod
    Given a source that loaded the mod "com.wtt.commonlib" version "3.0.6"
    When the Archiver imports it as official data of "4.1.6"
    Then the run fails
    And the message names "com.wtt.commonlib"
    And the table "items" keeps its previous rows

  @unverified
  Scenario: An official import accepts a source that loaded only the exporter
    Given a source whose "run/loaded.json" lists the exporter alone
    When the Archiver imports it as official data of "4.1.6"
    Then the run succeeds
    And every new row states the release "4.1.6"

  # ADR-0035 decision 2. A source that says nothing is not refused.

  @unverified
  Scenario: A source that states no mods imports with a warning
    Given a source without "run/loaded.json"
    When the Archiver imports it as official data of "4.1.6"
    Then the run succeeds
    And the log warns that nothing proves the source loaded no mod

  # ADR-0035 decision 3. The source and the run name the same release.

  @unverified
  Scenario: An official import refuses another release than the source states
    Given a source whose "run/release.json" states "4.1.5"
    When the Archiver imports it as official data of "4.1.6"
    Then the run fails
    And the message names "4.1.5" and "4.1.6"

  # ADR-0035 decision 4. The official data comes before the runs on it.

  @unverified
  Scenario: A run needs the official data of its release
    Given a run of the mod "com.example.thermal" on the release "4.2.0"
    And the archive holds no official row of "4.2.0"
    When the import runs
    Then the import stops
    And the message names "4.2.0"
    And the archive stores no dataset
```

## Action Items

1. [x] `SptArchive.Archiver.Runs.OfficialSource` reads `run/loaded.json` and `run/release.json`, and `ArchiveJob` calls it before any database work. `OfficialImportTests` holds the facts.
2. [x] `RunImporter.EnsureOfficialReleaseAsync` refuses a run whose release holds no official row, before the transaction opens.
3. [x] The official data of 4.1.6 came from a mod-free installation: 4,673 items, 536,414 locale entries, 12 traders and 10,880 assort rows, and 0 `SALVAGE_*` keys.
4. [x] The importer no longer crashes on a trader whose `assort.json` or `questassort.json` holds `null`. The server writes `null` for a trader with no offer (Ref, 4.1.6), and `null` is not a missing file. Without this fix a native source could not enter the archive at all.
5. [ ] The Runner of ADR-0030 refuses a modded official run before the export, and writes a source that states its mods.
6. [ ] Refuse a source that states nothing once every release has an export of exporter 1.2.0 or later. Decision 2 is then no longer needed.

## Links

- ADR-0013 (a dataset is content), ADR-0014 (every row states its release), ADR-0028 (volatile fields).
- ADR-0029 (a run names its release), ADR-0030 (the Runner).
- ADR-0033 (a run records itself, and `official` loads no mod).
- `docs/tech-debt.md` (TD-50, resolved here).
- `src/SptArchive.Archiver/Runs/OfficialSource.cs`, `src/SptArchive.Archiver/ArchiveJob.cs`, `src/SptArchive.Archiver/Runs/RunImporter.cs`, `src/SptArchive.Archiver/Importers/TraderImporter.cs`.
- `docs/runbooks/mod-export.md` (how to export a mod-free installation), `docs/specs/archive-official-data.feature`.
