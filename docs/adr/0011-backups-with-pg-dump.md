# ADR-0011: Back up with pg_dump after each successful run and keep copies off the host

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The archive can be rebuilt from the source files, but only while those files exist. Mod sources can disappear. The archive data is the only copy after that.

`pg_dump` writes a consistent snapshot of the database as one file. It runs in one transaction, so a running import or a running query does not affect it.

## Decision

We will take a custom-format dump (`pg_dump -Fc`) after each successful run. We will store it on a different host or service than the database. We will test a restore on a regular basis.

## Options Considered

| Dimension         | `pg_dump` after each run (chosen) | WAL archiving with pgBackRest | Volume snapshots   | No backup         |
|-------------------|-----------------------------------|-------------------------------|--------------------|-------------------|
| Complexity        | Low                               | High                          | Medium             | None              |
| Point-in-time     | One point per run                 | Any point                     | One point per snap | None              |
| Restore           | `pg_restore` into an empty DB     | Dedicated tool                | Restore the volume | Not possible      |
| Fits the workload | Yes: data changes once per run    | More than needed              | Host dependent     | No                |
| Portable          | Yes: also works as a public download | No                         | No                 | Not relevant      |

## Trade-off Analysis

The data changes only at the end of a run, so one dump per run captures every state that matters. Point-in-time recovery adds cost and gives nothing extra here.

## Consequences

- **Easier:** A restore needs one command. The dump can also be published so anyone can restore a copy.
- **Harder:** Dumps do not include roles. Keep the role setup in `docker/initdb` or dump globals with `pg_dumpall --globals-only`.
- **Harder:** A restore needs the same extensions and the same or a newer major PostgreSQL version.
- **Revisit when:** The data changes between runs, for example through an admin panel. Then consider WAL archiving.

## Verification

```gherkin
@backlog
Feature: Backups

  Scenario: A successful run produces a dump
    Given a successful archive run
    When the backup step runs
    Then a dump file exists with the date of the run in its name
    And a copy of the dump exists off the host

  Scenario: A dump restores into an empty database
    Given a dump file from a successful run
    When I restore it into a new empty database
    Then the restored tables have the same row counts as the source database
```

## Action Items

1. [ ] Add a backup script and call it after a successful run. Half done on 2026-10-21:
   `scripts/backup.sh` dumps, verifies and restores (`docs/runbooks/backup.md`), and a maintainer runs
   it by hand. Nothing calls it after a run, and no schedule exists (TD-03).
2. [ ] Add a scheduled restore test. `scripts/backup.sh verify` restores a dump into a scratch
   database and compares the row count of every table. Nothing runs it on a schedule.

## Links

- README section "Operations"
- Related: ADR-0009
