# ADR-0010: Decide whether the archive keeps one dataset or many server versions

**Status:** Superseded by ADR-0014
**Date:** 2026-10-19
**Deciders:** Yui must decide, before the first real import

## Context

The server changes between versions. Items, quests and traders are added, changed or removed.

The archive today holds one dataset: the latest import. The Archiver replaces the official tables on every run (ADR-0009). Nothing records which server version the data came from.

Mod data (ADR-0005) has the same question: one version per mod, or all versions.

## Decision

Superseded. ADR-0014 chooses option B: a server version key on every official table, and every
archived release is kept. The text below records the state before the decision.

## Options Considered

| Dimension                    | A. Latest only (today)       | B. A server version key on official tables | C. One database per server version |
|------------------------------|------------------------------|--------------------------------------------|------------------------------------|
| Storage                      | One dataset                  | One dataset per version                    | One dataset per version            |
| Primary keys                 | Unchanged                    | Every official key gets `spt_version`      | Unchanged                          |
| Query a past version         | No                           | Yes: filter by version                     | Yes: choose a database             |
| Change to the API            | None                         | Every query needs a version argument       | Routing layer                      |
| Cost to change later         | Migrate every official table | Low                                        | Low                                |
| Archiver replace logic       | Delete all                   | Delete one version only                    | Delete all in the target database  |

## Trade-off Analysis

Option A is the cheapest now. Changing from A to B later means a migration of every official primary key and every query. Option B costs more now and fixes that. The choice depends on one question: does a user ever need an old version?

## Consequences

- **Easier (A):** The current schema and API stay as they are.
- **Harder (A):** Past versions are lost on each run.
- **Revisit when:** A user asks for data of an older server version.

## Verification

```gherkin
@backlog @decision-needed
Feature: Archive scope

  Scenario: Data states its server version
    Given an archive run for the server version "X"
    When a client reads an item from that run
    Then the item states the server version "X"

  Scenario: A new version does not delete an old version
    Given the archive holds the server version "X"
    When the Archiver imports the server version "Y"
    Then the data of both versions exists
```

## Action Items

1. [ ] Choose A, B or C and change the status to Accepted.
2. [ ] If B or C: add the version to the importers, the keys and the API before the first real import.

## Links

- Related: ADR-0003, ADR-0005, ADR-0009
- Code: `src/SptArchive.Core/Data/ArchiveDbContext.cs`
