# ADR-0016: Probe mod compatibility when a server release arrives

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

ADR-0013 records a mod version once, when the mod releases. A server release does not re-run the
mod, so the archive never learns whether an older mod version still works on the new server.

That is the question that users ask most: "does this mod work on 4.0.13".
A full archive run answers it at the price of two server starts per mod and a full rewrite of rows.
Most of that work is wasted, because the content usually does not change.

## Decision

We will probe every mod version whose declared range admits the new server release.

A **probe** is one target-container run without a baseline run. The probe compares the export hash
with the content hash of the stored dataset. It records an observation with the kind `probe` and an
outcome. A probe writes no data rows. A probe that finds different content archives a new dataset.

## Options Considered

| Dimension              | A. Do nothing                 | B. Probe on each release        | C. Full archive run on each release |
|------------------------|-------------------------------|----------------------------------|--------------------------------------|
| Cost per release       | Zero                          | One server start per mod         | Two server starts per mod            |
| Answers "does it work" | No                            | Yes                              | Yes                                  |
| New data rows          | None                          | Only when the content differs    | Always                               |
| Risk of a wrong answer | The archive stays silent      | The declared range can be stale  | The declared range can be stale      |

Option A leaves the most asked question open. Option C repeats work that changes nothing.
Option B answers the question and writes rows only when it must.

## Trade-off Analysis

A probe trusts the declared range of the mod. A mod author can declare a range that is too wide, or
forget to update it. The probe reports what the container does, not what the range says, so a
container that starts and loads the mod gives a positive answer even when the range is old.

The cost is bounded and predictable: one start per candidate mod per release. It runs on the same
runner as the archive, at a lower priority.

## Consequences

- **Easier:** The Api can answer "which mod versions loaded on 4.0.13", because the observation
  table already holds that fact.
- **Easier:** A mod that breaks on a new release is known before users report it.
- **Harder:** An observation now has a kind. A probe observation and an archive observation must not
  be confused, because only one of them may own a dataset that a baseline produced.
- **Harder:** The probe needs the dependency set of a mod, exactly as the archive does.
- **Revisit when:** The declared range of most mods proves stale, or the probe cost grows past the
  archive cost.

## Verification

```gherkin
Feature: Server releases are checked against the mods that claim support

  Scenario: A probe records an observation and writes no row
    Given the mod "com.example.mod" in the version "1.2.0" has the dataset "D1"
    When the Archiver probes that mod version on "4.0.13"
    Then the observation states the kind "probe" and the outcome "archived"
    And the observation points at the dataset "D1"
    And the mod tables hold no new row

  Scenario: A probe reports a mod that no longer loads
    Given the mod "com.example.mod" loaded on "4.0.12"
    And that mod crashes on "4.0.13"
    When the Archiver probes that mod version on "4.0.13"
    Then the observation states the outcome "crashed"
    And the dataset "D1" stays as it is

  Scenario: A probe archives content that changed
    Given the mod "com.example.mod" has the dataset "D1"
    When a probe on "4.0.13" exports different content
    Then the archive stores a new dataset
    And the observation of "4.0.13" points at the new dataset

  Scenario: A mod outside its declared range is not probed
    Given the mod "com.example.mod" declares support for ">=4.0.0 <4.1.0"
    When the archive probes on "4.1.0"
    Then no container starts for that mod
    And no observation exists for that pair
```

## Action Items

1. [ ] Add the column `kind` to the observation table, with the values `archive` and `probe`.
2. [ ] Add the probe job. It runs after a new server release is archived.
3. [ ] Add the "loaded on this release" field to the mod types of the Api.
4. [ ] Keep the probe out of the first slice.

## Links

- Related: [ADR-0013](0013-mod-ingestion-by-isolated-run.md), [ADR-0014](0014-keep-every-archived-spt-release.md)
- Spec: [docs/specs/mod-registry.feature](../specs/mod-registry.feature)
- Tech debt: TD-24
