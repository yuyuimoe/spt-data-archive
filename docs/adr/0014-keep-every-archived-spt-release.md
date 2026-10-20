# ADR-0014: Keep every archived server release under one version key

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

ADR-0010 left the archive scope open. Today the Archiver replaces the official tables on every run
(ADR-0009), and nothing records which server version the data came from.

Two separate time axes exist. The server releases official data once per release.
A mod releases its data once per mod release that changes data. A mod version is not a server
version: one mod version can declare support for several server versions.

A mod dataset is mostly a set of changes to official rows. The old value of such a change only
exists in the official table of the server version that the mod was archived on.

## Decision

We will keep every archived server release and add the server version to the key of every official
table. The new table `spt_versions` records each release.

A mod dataset keeps no server version. An observation records the server version that the dataset
was seen on. A mod dataset can therefore apply to several server releases.

## Options Considered

| Dimension             | A. Latest only          | B. A version key on the official tables | C. One database per version |
|-----------------------|-------------------------|------------------------------------------|-----------------------------|
| Storage               | One official dataset    | One official dataset per release         | One official dataset per release |
| Official primary keys | Unchanged               | Every official key gains `spt_version`   | Unchanged                   |
| Query a past release  | No                      | Filter by version                        | Choose a database           |
| Change to the Api     | None                    | Every official query states a version    | A routing layer             |
| Mod data              | Latest only             | One dataset, many observations           | One dataset per database    |
| Cost to change later  | Migrate every table key | Low                                      | Low                         |

Option A loses the old value of every mod change when a new release arrives. That breaks the
change model of ADR-0013 on the second release. Option C needs a routing layer and one backup per
database, for data that is small.

## Trade-off Analysis

Option B is a one-time cost at the first migration. Its own cost is small: the official data of one
release is tens of megabytes, and a release happens a few times per year.

The real cost of Option B is on the read path. Every official query becomes ambiguous until it
states a server version. The Api must therefore have a `sptVersion` argument with the latest
archived release as its default. A query must never merge two releases.

## Consequences

- **Easier:** A mod change keeps its old value forever. "What did this mod change, against what"
  has an answer for every archived release.
- **Easier:** "What changed between 4.0.12 and 4.0.13" becomes a query, not a new feature.
- **Harder:** Every official table gains a key column, and every official index gains a prefix.
- **Harder:** The Api must state a version on every official query. The default is the latest
  release, and the response must show which release answered.
- **Harder:** Version strings must sort semantically. The text "4.0.13" sorts before "4.0.9",
  so `spt_versions` stores `version_parts` and sorts by that column.
- **Revisit when:** The official data of one release becomes too large to keep, or a user needs a
  release that was never archived.

## Verification

```gherkin
Feature: The archive keeps every server release

  Scenario: Data states its server version
    Given an archive run for the server version "4.0.13"
    When a client reads an item from that run
    Then the item states the server version "4.0.13"

  Scenario: A new release does not delete an old release
    Given the archive holds the official data of "4.0.12"
    When the Archiver imports the official data of "4.0.13"
    Then the archive holds the official data of both releases

  Scenario: A mod change keeps its old value after a new release
    Given the mod "com.example.mod" changes the weight of the official item "A1" on "4.0.12"
    When the Archiver imports the official data of "4.0.13"
    Then the change row still states the old value from "4.0.12"

  Scenario: An official query states a release
    Given the archive holds the official data of "4.0.12" and "4.0.13"
    When a client reads items without a server version
    Then the answer comes from the latest archived release
    And the answer states that release

  Scenario: Two releases are never merged in one answer
    Given the archive holds the official data of "4.0.12" and "4.0.13"
    When a client reads items of "4.0.12"
    Then no row of "4.0.13" is in the answer

  Scenario Outline: Releases sort by number, not by text
    Given the archive holds the releases "<older>" and "<newer>"
    When a client reads the latest archived release
    Then the answer states "<newer>"

    Examples:
      | older   | newer   |
      | 4.0.9   | 4.0.13  |
      | 4.0.13  | 4.1.0   |
      | 3.11.4  | 4.0.0   |

  Scenario: A mod dataset is not tied to one server release
    Given the dataset "D1" of the mod "com.example.mod"
    When the archive observes that mod version on "4.0.13"
    Then the observation states "4.0.13"
    And the dataset "D1" has no server version
```

## Action Items

1. [ ] Add `spt_versions` and the `spt_version` key column to every official table.
2. [x] Add the `sptVersion` argument to every official GraphQL field, defaulting to the latest. Done: `Releases.ResolveAsync`; each official row states its release in `sptVersion`, and `sptVersions` lists the releases.
3. [x] Add the version to the cache key, so one release does not answer for another. Done: the key hashes the request body, which holds the argument, and the data version covers a request that leaves it out.
4. [ ] Add the version-change query to the backlog, not to the first slice.

## Links

- Supersedes: [ADR-0010](0010-archive-scope-latest-or-versioned.md)
- Related: [ADR-0019](0019-reject-a-version-the-archive-does-not-hold.md) (what the argument does
  with a value that the archive does not hold)
- Related: [ADR-0009](0009-run-to-completion-archiver.md), [ADR-0013](0013-mod-ingestion-by-isolated-run.md)
- Spec: [docs/specs/archive-official-data.feature](../specs/archive-official-data.feature)
- C4: `archive.db`
- Code: `src/SptArchive.Core/Data/ArchiveDbContext.cs`
