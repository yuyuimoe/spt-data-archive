# ADR-0018: Do not delete mod data in the first release

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

ADR-0013 states that deleting a mod deletes its observations.
It also states that the next successful run deletes a dataset with no observation.

That pair of sentences describes an operation that no component has.
The Api has no mutations (ADR-0006). The Archiver owns the official tables only.
The write path for admin changes is still undefined (TD-19).

Deletion is also the only normal operation that can produce an orphan dataset.
A dataset is always created together with an observation, in one transaction.
If nothing deletes, then a dataset with no observation is a bug, not a state.

## Decision

We will not delete mod data in the first release.

The Archiver never deletes a mod row, a dataset or an observation.
The catalog synchronization only adds rows and updates versions.
The orphan-dataset query becomes a consistency check that fails an archive run.

A mod that must be removed for legal or safety reasons is deleted by one documented
`psql` statement, run by the operator and recorded in the runbook.

## Options Considered

| Dimension                | A. Delete one mod version | B. Delete one mod GUID | C. No delete in the first release |
|--------------------------|---------------------------|-------------------------|------------------------------------|
| Rows it removes          | One observation           | Every version and row   | None                               |
| Orphans it can produce   | Yes                       | Yes                     | No                                 |
| Admin surface it needs   | A mutation or a job       | A mutation or a job     | None                               |
| Risk of a wrong delete   | One version               | Every version of a mod  | A documented SQL statement         |
| Work before the mod slice| A write path              | A write path and a sweep | Nothing                           |

Option A is the eventual operation, and it is the smallest of the three.
Option B has a larger blast radius for the same amount of new code.
Option C removes the whole write path from the first release.

## Trade-off Analysis

The archive is append-only in the first release. That is a simpler rule than a delete
with a share rule, and it turns TD-22 from a feature into an assertion.

The cost is honest and must be written down: a mod that must be pulled from the archive
needs a manual step. That step already exists in every incident response, so the archive
adds no new risk. What it loses is self-service removal.

The alternative is to build a write path before anyone needs it, and to carry the
orphan sweep for a state that a correct run cannot reach.

## Consequences

- **Easier:** A dataset with no observation is a bug. The check is one query in
  the run transaction, and it fails the run.
- **Easier:** No write path in the Api, so rule 1 of `AGENTS.md` stays intact.
- **Easier:** TD-22 drops out of the plan and becomes one assertion.
- **Harder:** Removal of a mod requires a database console and a runbook entry.
- **Harder:** The archive grows without bound until a prune policy exists (ADR-0014).
- **Revisit when:** A user asks to remove their own mod, or a takedown happens
  for the first time.

## Verification

```gherkin
Feature: The archive never deletes mod data by itself

  Scenario: An archive run does not delete a mod
    Given the mod "com.example.mod" in the version "1.0.0" has a dataset
    When an archive run succeeds
    Then the table "mods" still holds that mod version
    And the observation of that mod version still exists

  Scenario: A mod that leaves the catalog keeps its data
    Given the mod "com.example.mod" is no longer in the catalog
    When the Archiver synchronizes the catalog
    Then no row of that mod is deleted
    And the dataset of that mod version still exists

  Scenario: An orphan dataset fails the run
    Given a dataset "D1" that no observation points at
    When an archive run succeeds
    Then the run fails with a message that names "D1"
    And the table "mod_datasets" still holds "D1"

  Scenario: A successful run leaves no orphan dataset
    When an archive run succeeds
    Then every row of "mod_datasets" is pointed at by at least one observation
```

## Action Items

1. [ ] Remove the delete scenario and the orphan-delete scenario from `mod-registry.feature`.
2. [ ] Write the orphan check as an assertion in the run transaction, not as a sweep.
3. [ ] Write the manual removal statement into the runbook, and test it on a copy.
4. [ ] Add TD-22 to the register as an assertion that is not written yet.

## Links

- Related: [ADR-0013](0013-mod-ingestion-by-isolated-run.md),
  [ADR-0006](0006-thin-readonly-graphql-api.md),
  [ADR-0017](0017-record-a-template-operation.md)
- Spec: [docs/specs/mod-registry.feature](../specs/mod-registry.feature)
- C4: `archive.db`
- Code: `src/SptArchive.Archiver/`
