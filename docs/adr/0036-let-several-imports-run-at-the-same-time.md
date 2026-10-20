# ADR-0036: Let several imports run at the same time

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui

## Context

The driver plans to import several mod runs at the same time: the campaign that fills a new archive, and the day when two mods publish a new version together.

Every run so far assumed it was alone in the database, and two things broke.

**A killed run keeps its row.** TD-51: the terminal of the import of WTT-Armory died on 2026-10-20, and the row of run `01a15b5f` stayed `running` until it was deleted by hand. The job marks a failure in its `catch` block, and a killed process reaches no `catch`. A sweep that closes the row of a run older than the current one cannot work here: with two runs open, the live one may have started first.

**Check-then-insert cannot survive two writers.** The archive keeps unique indexes on `mods (mod_guid, version)`, on `mod_datasets (mod_guid, content_hash)` and on the observation of a mod version on a release. Two runs of the same mod pass the same `AnyAsync` check, and the second one dies on a unique violation. The official import is worse: it deletes and rewrites every row of a release, so two of them at once would overwrite each other's rows.

The driver's answer on 2026-10-20 was that a start-time sweep is not acceptable, because parallel imports are the plan.

## Decision

**We will let several Archiver processes import into one database at the same time. Each run holds a lease, and each run takes a lock on what it writes.**

1. **A run holds a lease.** `archive_runs.heartbeat_at` states the last time the process said it was alive. The process writes it every 10 seconds, on its own connection. The import holds a long transaction, and a beat inside it would roll back with it.

2. **A run that is silent for 120 seconds is over.** The next Archiver to start closes it as `interrupted`. The row keeps the error and the result that say why. The test is the heartbeat, never the start time: among two open runs, the live one may be the older one. Twelve missed beats are tolerated, so a slow import, a garbage collection pause or a stalled disk does not look like a dead process.

3. **The lease is the only clock.** A run that is open and beating is never closed by another process, however long it has been open. A run whose process is wedged in a lock wait still owns its row, which is the truth about it.

4. **A run takes a lock on what it writes.** The locks are transaction scoped, so a process that dies while it waits holds nothing.
   - An official import holds the release exclusively, because it rewrites every row of that release.
   - A mod run holds the release shared, so many mod runs of one release import at once.
   - A mod run holds its mod exclusively, because the mod row, the dataset and the observation are one fact.

5. **A run that waited reads the work of the lock holder.** It finds the dataset, or the succeeded run of the same files. It records `unchanged`. It never imports the same content twice, and it never dies on a constraint.

6. **A success commits with the data it describes.** The run row says `succeeded` in the same transaction that writes the rows. A run marked after the commit leaves a window where the data is committed and the history still says `running`. The next run to take the lock reads that window and imports again.

## Options Considered

| Dimension        | A: a lease and transaction locks (chosen) | B: a start-up sweep by start time | C: one writer, driven by a queue |
|------------------|-------------------------------------------|-----------------------------------|----------------------------------|
| Parallel runs    | yes, by design | no: a live run started first is closed | yes, but one import at a time |
| Where the rule lives | the database, in two statements | the process | a component that does not exist |
| Complexity       | one column, two lock calls, one status | one statement | a queue, a scheduler and a new component |
| A killed process | its row is closed by the next run | its row is closed | the queue retries it |

Option D, retry on a unique violation, was rejected. A retry after a commit is the same work as a lock wait. A retry before it is a second import of the same content.

## Trade-off Analysis

The trade-off is between a history that is always true and an import that never has to wait. The archive takes the second, and the history is true within one run.

The lease is the part that costs the most, and it costs only when a process dies. A live run writes one small `UPDATE` every 10 seconds. The sweep is one statement over a partial index that holds the open runs alone.

The locks cost a wait in the one case where two runs touch one fact. That wait is what keeps the data correct, and it is also where the archive learns that the work is already done.

## Consequences

- A killed Archiver no longer leaves a `running` row behind, as soon as one more run starts. If no run ever starts again, the row stays `running`: the history is only as fresh as the last execution.
- Four imports at once read four exports of several hundred megabytes each. Nothing in the archive limits that; the machine does.
- Two official imports of the same files import once. The second one records `unchanged` and writes no row, so the history says which process did the work.
- The locks are per database. Two archives on two servers do not share them.
- A run that the sweep closed as `interrupted` and that then finishes its work records `succeeded` over it. The process was alive after all, and the later and truer statement wins.

## Verification

```gherkin
Feature: Import several mod runs at the same time

  # ADR-0036 decisions 1 to 3. A lease, not a start time.

  @unverified
  Scenario: The next run closes the run of a killed process
    Given a run whose row says "running" and whose last heartbeat is older than "120" seconds
    When another Archiver starts
    Then the row of the dead run says "interrupted"
    And its error names ADR-0036

  @unverified
  Scenario: A run that is still beating is left alone
    Given a run whose row says "running" and whose last heartbeat is now
    When another Archiver starts
    Then the row of that run still says "running"

  # ADR-0036 decisions 4 to 6. One lock, one write.

  @unverified
  Scenario: Two imports of one export store one dataset
    Given two Archiver processes read the same export of "com.example.thermal"
    When both import it at the same time
    Then both runs say "succeeded"
    And the archive holds one dataset of that mod
    And the archive holds one observation of that dataset
    And one of the two runs says "dataset_created"

  @unverified
  Scenario: Two mods of one release import at the same time
    Given two Archiver processes import two mods on the release "4.1.6"
    When both run at the same time
    Then both runs say "succeeded"
    And the archive holds one dataset of each mod
    And the release "4.1.6" is registered once

  @unverified
  Scenario: Two official imports of one source import once
    Given two Archiver processes read the same official source
    When both import it at the same time
    Then one run says "imported"
    And the other run says "unchanged"
    And the release holds one row set
```

## Action Items

1. [x] `archive_runs` holds `heartbeat_at`, with a partial index over the open runs. Migration `20261020224106_AddRunHeartbeat` also adds the check constraint on `status`, which now names `interrupted`.
2. [x] `SptArchive.Archiver.Runs.RunHeartbeat` writes the beat every 10 seconds on its own connection, and `StaleRuns.CloseAsync` closes the runs that went silent. A sweep that cannot run logs a warning and never stops the import.
3. [x] `SptArchive.Archiver.Runs.RunLocks` takes the three transaction-scoped advisory locks. `RunImporter.ApplyAsync` takes the release shared and the mod exclusive; `ArchiveJob` takes the release exclusive for an official import.
4. [x] The success of a run commits inside its transaction, so a run that a second process waits for is visible with its data.
5. [x] `SptVersionRegistry.EnsureAsync` survives a second import that registers the same release first.
6. [x] `ConcurrentRunTests` holds six facts. Two imports of one export store one dataset. Two mods of one release import together. A dead run is closed and a live run is kept. Two official imports of one source import once. A release registered twice stays one row.
7. [ ] The Runner (ADR-0030) closes its own row when the container ends, so a killed Runner does not wait for the next Archiver.
8. [ ] The run history of the Api shows `interrupted` and the lease (ADR-0033, action item 5).

## Links

- ADR-0013 (a dataset is content), ADR-0028 (volatile fields), ADR-0029 (a run states its release).
- ADR-0030 (the Runner owns the process), ADR-0033 (a run records itself).
- ADR-0035 (the official data of a release comes before any run on it; the shared lock enforces it).
- `docs/tech-debt.md` (TD-51, resolved here).
- `src/SptArchive.Archiver/ArchiveJob.cs`, `src/SptArchive.Archiver/Runs/RunLease.cs`, `src/SptArchive.Archiver/Runs/RunLocks.cs`, `src/SptArchive.Core/Data/SptVersionRegistry.cs`.
- `tests/SptArchive.Tests/ConcurrentRunTests.cs`.
- `docs/runbooks/mod-export.md` (how to run two imports at the same time).
