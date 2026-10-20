# ADR-0033: Record every run and why it happened

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui

## Context

A run is one server container that loads one mod version next to its dependencies. The export of that run is the input of a dataset.

The archive has a table for runs: `archive_runs`. One row holds the start time, the end time, the hash of the source files, the server release, the status and the error text.

That row does not say three things:

1. **Why** the run exists. A run on 4.1.6 for the mod ECOTI has another purpose than a curated rerun of the same mod with another configuration.
2. **Which mod** the run was made for. The exporter records which mods loaded, not which one is the target. `Archive:RunModGuid` is a value that a person types, and the importer can only check that the GUID is in the list of loaded mods. It cannot prove that the run was made for that mod (TD-44).
3. **What came out.** The dataset id, the counts and the warnings live in the log and in the dataset rows. A run that failed, or a run that produced no dataset, leaves nothing to read.

The export campaign made 19 mod runs. 3 of them are in the database. The other 16 exist only as directories on disk (ADR-0027 says the directory is transport and may be deleted).

The driver requires a history of every run: why it was made and what it produced.

## Decision

**We will record one row for every run, with its purpose, its target and its result.**

1. **`archive_runs` gains the columns that state the work.** `kind` (`official`, `mod`, `probe`) and `purpose` (one line of text). A mod run and a probe run state a purpose. The row also gains `target_mod_guid`, `target_version`, `baseline_release` and `baseline_hash`.

2. **`archive_runs` gains a result.** One jsonb column `result` holds the counts that say what came out: the dataset id, the content hash, templates per kind and operation, path rows, locale rows, files, unstored files and warnings. A failed run stores its error and its stage.
   The column copies values that rows also hold. We accept the copy because a run without a dataset, or a run that failed, has no other place to state what happened.

3. **The run states its own target.** The runner writes `run/target.json` next to `pre-boot.json` and `release.json`. The file states the GUID, the version and the reason of the target. The importer reads the target from there and not from a typed value. `Archive:RunModGuid` becomes a check: when the two differ, the import stops and names both.
   - A run directory from before this rule has no `target.json`. The importer then uses `Archive:RunModGuid` and marks the observation as unverified, in the same way that ADR-0029 treats a baseline without a release.
   - This closes TD-44 for every new run.

4. **A dataset names the run that first wrote it.** `mod_datasets` gains `created_by_run_id`. It is the run whose content created the dataset. A second run with the same content does not change it.

5. **A content row carries no run id.** `mod_templates`, `mod_change_paths`, `mod_items`, `mod_localizations` and the rest stay keyed by the dataset. Two reasons:
   - Rows are shared by every run that produces the same content (ADR-0013, ADR-0029). ECOTI on 4.1.6 and ECOTI on 4.1.7 hold the same content and share one row set. A run id on the row forces one copy per run. That breaks the sharing rule. Otherwise it names one run and hides the other, and that is false.
   - The link is already there in the right place. `mod_data_observations` holds one row per attempt, with `archive_run_id` and `dataset_id`. The chain dataset → observations → runs answers "which runs produced this dataset, when, on which release, with which dependency set".

6. **Every observation names its run.** `archive_run_id` becomes required for an observation that an Archiver run made. A probe keeps its run too.

7. **Runs are never deleted.** A run row is cheap, and the history is the point. A dataset that is deleted in a future prune keeps its run rows.

## Options Considered

| Dimension | A: run row + `created_by_run_id` (chosen) | B: a `run_id` on every content row | C: keep the history in the run directories |
|-----------|-------------------------------------------|------------------------------------|---------------------------------------------|
| Complexity | low: two columns, one jsonb, one file | medium: the key of every mod table changes | none |
| Storage | one row per run | one copy of every row per run | none |
| Correctness | the sharing rule holds | the sharing rule breaks | the history dies with the directory |
| Query | dataset → observations → runs | direct | not possible |

Option B loses on the sharing rule. Under ADR-0013 a dataset is content, and its rows belong to the content and not to the run that happened to write it. A `run_id` on a shared row is either duplicated or wrong.

Option C loses because a run directory is transport (ADR-0027). The archive is the store of record.

## Trade-off Analysis

The trade is one extra column on a table that a query joins anyway against a direct pointer from every row.

The direct pointer reads better in a first query: "which run changed this item". We answer that query with a join of two tables instead. The dataset names the run that created it, and the observation list names every run that reproduced it. Both facts are true, and neither one copies rows.

The second trade is the copied result. A count in `result` can drift from the rows. We accept the drift because the copy covers the case that rows cannot: a run with no dataset. The import writes both in one transaction, so they never disagree at rest.

## Consequences

- **Easier:** a question about the history is one table. "Which runs did we make on 4.1.6, for which mods, with which outcome" needs no log file.
- **Easier:** a failed or empty run stops being invisible.
- **Easier:** the target of a run becomes a fact of the run, and not a value that a person types.
- **Harder:** the runner must write `target.json`, which makes the un-Gitted bash runner (TD-42) part of the contract until ADR-0030 replaces it.
- **Harder:** the importer has one more validation and one more failure mode.
- **Revisit when:** the Runner of ADR-0030 exists, because it then records the run before the container starts.

## Verification

```gherkin
Feature: Keep a record of every run and why it happened

  # ADR-0033 decisions 1 and 2. A run states its work and its result.

  @unverified
  Scenario: A mod run states its purpose, its target and its result
    Given a run of the mod "com.example.thermal" version "3.4.0" on the release "4.1.6"
    When the import succeeds
    Then the run has the kind "mod"
    And the run has a purpose
    And the run names the target "com.example.thermal"
    And the run names the dataset it produced

  @unverified
  Scenario: A run that fails still leaves a row
    Given a run whose baseline was made on another release
    When the import is refused
    Then the run has the status "failed"
    And the run states the reason in its error
    And the run produces no dataset

  # ADR-0033 decision 3. The run directory states the target.

  @unverified
  Scenario: The target comes from the run directory
    Given a run directory whose "run/target.json" names the mod "com.example.thermal"
    When the import runs with another target
    Then the import stops
    And the message names both mods

  @unverified
  Scenario: A run directory without a target is imported unverified
    Given a run directory written before the target file existed
    When the import runs with the target "com.example.thermal"
    Then the import continues
    And the observation is marked unverified

  # ADR-0033 decisions 4 and 5. Provenance sits on the dataset and the observation.

  @unverified
  Scenario: A dataset names the run that first produced it
    Given the dataset "D1" was created by the run "R1"
    When I read the dataset "D1"
    Then it names the run "R1"

  @unverified
  Scenario: A second run of the same content keeps the first run on the dataset
    Given the run "R1" created the dataset "D1"
    When the run "R2" imports the same content on another release
    Then the dataset "D1" still names the run "R1"
    And two observations point at the dataset "D1"

  @unverified
  Scenario: A content row names no run
    Given the dataset "D1" was produced by the runs "R1" and "R2"
    When I read a template row of "D1"
    Then the row names no run
    And the dataset names the run that created it

  # ADR-0033 decision 6. An observation always names its run.

  @unverified
  Scenario: An observation names the run that made it
    Given a probe of the mod "com.example.thermal" on the release "4.1.7"
    When the probe finishes
    Then the observation names its run
    And the run has the kind "probe"
```

## Action Items

1. [x] Migration `AddRunRecord`: `archive_runs` gains `kind`, `purpose`, `target_mod_guid`, `target_version`, `baseline_release`, `baseline_hash` and `result`; `mod_datasets` gains `created_by_run_id`; `mod_data_observations.archive_run_id` is required and gains `target_unverified`.
2. [x] The Archiver writes the run row before the work starts. It sets the status and the result after the transaction ends, in both the success path and the failure path (`RunRecordTests`). The result is not inside the import transaction, because a failed import rolls that transaction back and the failure must still be recorded. A mod run records itself before it reads the export; an official import records itself after the config checks and the skip test.
3. [x] The importer reads `run/target.json`, refuses a mismatch and marks an observation unverified when the file is absent. Reader side only, tested with fixtures.
4. [ ] The runner writes `run/target.json` (TD-42; ADR-0030 takes it over). Open: nothing writes the file yet.
5. [ ] A `runs` verb or an Api field lists the history.
6. [ ] Decide whether the 16 unimported run directories of the export campaign are re-imported to appear in the history.

## Links

- ADR-0009 (one transaction), ADR-0013 (dataset is content), ADR-0016 (probes).
- ADR-0027 (the directory is transport), ADR-0029 (a run names its release).
- ADR-0030 (one tool for run, export and import), ADR-0032 (what a dataset holds).
- `docs/tech-debt.md` (TD-42, TD-44).
- `src/SptArchive.Core/Entities/ArchiveRun.cs`, `src/SptArchive.Archiver/ArchiveJob.cs`, `src/SptArchive.Archiver/Runs/RunDirectory.cs`.
