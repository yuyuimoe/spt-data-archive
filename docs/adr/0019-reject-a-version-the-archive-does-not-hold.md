# ADR-0019: Reject a server version that the archive does not hold

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

ADR-0014 adds a `sptVersion` argument to every official query. The default is the latest archived
release, and the response states which release answered. TD-21 tracks the argument itself.

ADR-0014 does not state what happens for a value that is not an archived release. Two bad inputs
reach the same code path, and they are different problems:

1. A server version that the archive does not hold, for example `9.9.9`. The caller asks a real
   question about a release that no run has archived.
2. A value that is not a server version at all, for example `^9.9.9`, `4.0` or `latest`. The caller
   uses another vocabulary. A nearby example is mod metadata: the `sptVersion` field there is a
   version range, and ADR-0013 resolves such a range for a dependency set.

A query that filters by version returns an empty list for both, because no official row can match.
An empty list is wrong in both cases. Every archived release has items, so `[]` states that release
`9.9.9` has no items. That is a false answer, not a missing one.

One plain error is also not enough. A caller cannot tell a gap in the archive from a bug in the
client, and the operator needs to tell them apart.

## Decision

We will accept exactly one archived release key, and reject every other value with an error that
names the value, the reason and the latest archived release.

- The argument takes one exact server version string, as stored in `spt_versions`, for example
  `4.0.13`.
- An unknown release is an error and not an empty list. The error names the value and the latest
  archived release.
- A value that is not a release key is an error. The error names the value and states that the
  argument takes one exact archived release.
- The two cases carry different error codes, so a client branches on the code and not on the text.

The Api does not resolve a version range.

An amendment of 2026-10-21 covers the archive that holds no release. A query that names no release
is then an error with its own code (`SPT_ARCHIVE_EMPTY`), and not an empty list, because the
argument has no default to fall back on and an empty answer would state something false. This
follows decision 2 and adds no new option.

## Options Considered

| Dimension                         | A. Empty list          | B. One error for both cases | C. Two errors, exact key only        |
|-----------------------------------|------------------------|-----------------------------|--------------------------------------|
| Answer for an unarchived release  | Looks like "no items"  | Names the value             | Names the value and the latest release |
| Answer for a range                | Looks like "no items"  | Names the value             | Names the value and the correct form |
| Client can branch on the answer   | No                     | No                          | Yes, by error code                   |
| Work in the Api                   | None                   | One range check             | One parse and one lookup             |
| Risk of a wrong answer            | A false statement      | A true error                | A true error                         |

Option A is the cheapest and the worst. It makes a missing release look like an empty one, and no
archived release is empty.

Option B is correct but undiagnosable. A client that sends `^4.0.0` gets the same answer as a client
that asks for a release that was never archived, and one message must explain both.

Option C costs one extra code path. It buys a client that can act. A client that sends a range must
stop sending ranges. A client that asks for `9.9.9` may ask the right question, and the operator may
want to archive that release.

## Trade-off Analysis

The main trade-off is the cost of a second error against the cost of a wrong answer. An empty list
for an unarchived release is a fact the archive cannot support, and a client cannot detect it.
A second error code is small work in one place.

Not accepting a range is the reversible choice. ADR-0013 already holds a range resolver for
dependency sets, inside the Archiver. A second resolver in the Api would be a second place for the
same rule to drift. Adding ranges later is backward compatible. Removing them later breaks clients.

## Consequences

- **Easier:** A wrong version is a visible failure and not a page of empty results.
- **Easier:** A client that sends a range learns the correct form from the error.
- **Harder:** The Api needs one parse step and one lookup in `spt_versions` before the query.
- **Harder:** Every client must handle two error codes and not one.
- **Revisit when:** Clients need "the newest release of the 4.0 line". Then the range becomes an
  argument of its own, and the response states the release that it resolved to.

## Verification

```gherkin
Feature: The Api rejects a server version that the archive does not hold

  Scenario: An unarchived release is an error and not an empty list
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" with the version "9.9.9"
    Then the response has an error that names "9.9.9"
    And the error names the latest archived release "4.0.13"
    And the response has no empty list

  Scenario: A version range is rejected
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" with the version "^9.9.9"
    Then the response has an error that names "^9.9.9"
    And the error states that the argument takes one exact archived release
    And the response has no empty list

  Scenario: A client can tell the two errors apart
    When I query "items" with the version "9.9.9"
    And I query "items" with the version "^9.9.9"
    Then both responses have an error
    And the error codes of the two responses are different

  Scenario: An empty archive is an error and not an empty list
    Given the archive holds no release
    When I query "items" without a version argument
    Then the response has an error
    And the error code is "SPT_ARCHIVE_EMPTY"

  Scenario: The default needs no version and no check
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" without a version argument
    Then the response has no error
    And the answer states the version "4.0.13"
```

## Action Items

1. [x] Add the `sptVersion` argument with its default (TD-21). Done on 2026-10-21: every official field
   of the Api takes `sptVersion`, and `Releases.ResolveAsync` answers from the latest archived release
   when the argument is left out.
2. [x] Return the two errors, with a different code for each, and name both codes in the docs. Done:
   `SPT_VERSION_UNKNOWN` and `SPT_VERSION_MALFORMED`, each with a message that names the value, and
   `query-api.feature` names both codes.
3. [x] Add the range case to `query-api.feature`. Done, with the case "4.0" and the word "latest".
4. [x] Treat a partial form such as "4.0" and the word "latest" as the range case. Done: a release key
   needs three or four numbers, and a key that the archive holds is always valid.
5. [x] Return `SPT_ARCHIVE_EMPTY` when the archive holds no release and the query names none (the
   amendment of 2026-10-21). Done, with a scenario in `query-api.feature` and a test in `ApiTests`.
   The driver confirmed the amendment on 2026-10-21.

## Links

- Related: [ADR-0014](0014-keep-every-archived-spt-release.md) (the version key and the argument),
  [ADR-0013](0013-mod-ingestion-by-isolated-run.md) (range resolution for a dependency set)
- Spec: [docs/specs/query-api.feature](../specs/query-api.feature)
- Tech debt: TD-21 in [docs/tech-debt.md](../tech-debt.md)
