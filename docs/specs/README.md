# Specifications

The `.feature` files in this folder describe what the system does. They are written in [Gherkin](https://cucumber.io/docs/gherkin/reference). They are the tests, once a BDD runner exists (ADR-0012).

## Files

| File | Subject |
|------|---------|
| [archive-official-data.feature](archive-official-data.feature) | The Archiver copies the server data into PostgreSQL |
| [query-api.feature](query-api.feature) | The public read-only GraphQL API, its limits and its cache |
| [mod-api.feature](mod-api.feature) | The Api surface of a mod dataset: its templates, change paths, files, addresses and runs |
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

Read a mod dataset through the Api (ADR-0032, action item 7, and ADR-0033, action item 5).
The scenarios of `mod-api.feature` are the behavior of that slice.
After it: the Gherkin scenarios bound with Reqnroll (TD-06), the rows of a location and a bot type (TD-45),
a JSON filter by path (TD-52), and the baseline cache (ADR-0013).
Scenarios outside the slice stay `@unverified` or `@backlog`.
