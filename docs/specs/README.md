# Specifications

The `.feature` files in this folder describe what the system does. They are written in [Gherkin](https://cucumber.io/docs/gherkin/reference). They are the tests, once a BDD runner exists (ADR-0012).

## Files

| File | Subject |
|------|---------|
| [archive-official-data.feature](archive-official-data.feature) | The Archiver copies the server data into PostgreSQL |
| [query-api.feature](query-api.feature) | The public read-only GraphQL API, its limits and its cache |
| [mod-registry.feature](mod-registry.feature) | Mod data stays apart from official data |
| [backup-and-restore.feature](backup-and-restore.feature) | Dumps and restores |

## Tags

| Tag | Meaning |
|-----|---------|
| `@unverified` | The code does this today, but nobody has run it. No test exists. Treat it as a claim, not a fact. |
| `@backlog` | The behavior is not built. |
| `@decision-needed` | The driver must make a decision first. The decision is in an ADR with the status "Proposed (decision needed)". |

When a scenario has a passing automated test, remove `@unverified`. A scenario with no tag has a passing test.

## Rules for writing scenarios

1. Write one behavior per scenario. Give it a name that states the behavior.
2. Use the words of `docs/glossary.md`. Use the same word for the same thing.
3. Write what the user sees, not how the code does it.
4. Use a `Scenario Outline` when only the data changes.
5. Work on one slice at a time. Tag every scenario outside the slice with `@backlog`.
6. Run `python3 scripts/check-docs.py` to check the syntax.

## Slice to work on next

Record the path of every changed value of a quest, a trader and a customization (ADR-0032, action item 3).
The `@unverified` scenarios of `mod-registry.feature` that name a change path are the behavior of that slice.
After it: the Api server version argument (ADR-0014, ADR-0019; TD-21), then the Gherkin scenarios bound
with Reqnroll (TD-06). Scenarios outside the slice stay `@unverified` or `@backlog`.
