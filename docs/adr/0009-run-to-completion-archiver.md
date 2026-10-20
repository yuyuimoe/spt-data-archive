# ADR-0009: Run the Archiver as a job that replaces the official data in one transaction

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

Only a scheduled job or a maintainer writes to the archive. The source files have no `updated_at` field, so the job cannot ask which rows changed.

Readers must never see half-imported data. The locale data alone has about half a million rows. The `en` file has 31,550 keys, and the server ships 17 languages.

## Decision

We will run the Archiver as a job that starts, does one run and exits. An external scheduler starts it. One run does these steps:

1. Hash all source files.
2. Stop if a succeeded run has the same hash, unless `Archive:Force` is true.
3. Record a run.
4. In one transaction, delete the official rows of that server release and write the new rows in batches.
5. Mark the run succeeded and publish the new dataset version.

## Options Considered

| Dimension            | Replace in one transaction (chosen) | Incremental upsert by diff       | Long-running worker with an internal timer |
|----------------------|-------------------------------------|----------------------------------|--------------------------------------------|
| Readers see          | Old data, then new data at commit   | Mixed data during the run        | Depends on the write method                |
| Failure behavior     | Full rollback                       | Partial update unless wrapped    | Depends                                    |
| Code complexity      | Low                                 | High: needs a diff per table     | Medium                                     |
| Write cost per run   | High: all rows                      | Low                              | Depends                                    |
| Scheduling           | cron, systemd timer, compose run    | Same                             | Inside the process                         |

## Trade-off Analysis

The source has no change markers, so a diff needs to compare every row anyway. Replacing the rows is simple and safe. The hash check avoids most of the cost, because most runs find no change.

## Consequences

- **Easier:** A failed run leaves the archive as it was. A run has a record with a status and an error.
- **Harder:** Deleting and inserting every row creates dead rows. Autovacuum must clean them. A very large run holds one long transaction.
- **Harder:** The Archiver must be the only writer of the official tables.
- **Revisit when:** A run takes too long, or the dead rows hurt performance. Then move to an incremental import.

## Verification

```gherkin
@unverified
Feature: An archive run is safe and idempotent

  Scenario: A failed run leaves the archive unchanged
    Given the official tables hold dataset "A"
    When a run fails halfway through the import
    Then the official tables still hold dataset "A"
    And the run has the status "failed" with an error message

  Scenario: An unchanged source is skipped
    Given a succeeded run exists for the current source hash
    When the Archiver runs again
    Then it writes no data
    And it exits with code 0

  Scenario: A forced run imports unchanged data
    Given a succeeded run exists for the current source hash
    When the Archiver runs with "Archive:Force" set to true
    Then it imports the data again

  Scenario: Readers keep the old data until the commit
    Given a reader queries the archive during a run
    When the run has not committed yet
    Then the reader sees the previous dataset
```

## Action Items

1. [ ] Measure the run time and the table bloat on real data.

## Links

- C4: `archive.archiver.archiveJob`, view `archiveRun`
- Code: `src/SptArchive.Archiver/ArchiveJob.cs`
- Spec: `docs/specs/archive-official-data.feature`
