# Specifications

The `.feature` files in this folder describe what the system does. They are written in [Gherkin](https://cucumber.io/docs/gherkin/reference). They are the tests: Reqnroll runs the files that the test project links (ADR-0012). Today that is `mod-api.feature` only.

## Files

| File | Subject |
|------|---------|
| [archive-official-data.feature](archive-official-data.feature) | The Archiver copies the server data into PostgreSQL |
| [query-api.feature](query-api.feature) | The public read-only GraphQL API, its limits and its cache |
| [mod-api.feature](mod-api.feature) | The Api surface of a mod dataset: its templates, change paths, files, addresses and runs |
| [file-rows.feature](file-rows.feature) | One row for each object of the seven files that a run changes (ADR-0044) |
| [mod-registry.feature](mod-registry.feature) | Mod data stays apart from official data |
| [backup-and-restore.feature](backup-and-restore.feature) | Dumps and restores |

## Tags

| Tag | Meaning |
|-----|---------|
| `@unverified` | The code does this today, but nobody has run it. No test exists. Treat it as a claim, not a fact. |
| `@backlog` | The behavior is not built. |
| `@decision-needed` | The driver must make a decision first. The decision is in an ADR with the status "Proposed (decision needed)". |

When a scenario has a passing automated test, remove `@unverified`. A scenario with no tag has a passing test.

## How a scenario becomes a test

1. Link the file in `tests/SptArchive.Tests/SptArchive.Tests.csproj`: `<ReqnrollFeatureFiles Include="../../docs/specs/<name>.feature" Link="Specs/<name>.feature" />`.
2. Write the steps in `tests/SptArchive.Tests/Specs`. Call `ArchiveDatabase` and `ApiHarness`. Do not start a second harness.
3. Run `dotnet test SptArchive.slnx`. A step with no definition fails the scenario.
4. Remove `@unverified` from each scenario that passes. A file that the project does not link keeps its tags (TD-62).


## Rules for writing scenarios

1. Write one behavior per scenario. Give it a name that states the behavior.
2. Use the words of `docs/glossary.md`. Use the same word for the same thing.
3. Write what the user sees, not how the code does it.
4. Use a `Scenario Outline` when only the data changes.
5. Work on one slice at a time. Tag every scenario outside the slice with `@backlog`.
6. Run `python3 scripts/check-docs.py` to check the syntax.

## Slice to work on next

Done: one row for each object of the seven files that a run changes, and every scenario of `file-rows.feature`
runs as a test with Reqnroll (ADR-0044, TD-62). Done before it: filter a jsonb column by the value at a path
(ADR-0040, TD-52, TD-55). The section "Filter by a path" of `query-api.feature` is the behavior of that slice,
and a test proves each scenario, and the 24 scenarios of `mod-api.feature` run as tests (TD-06, ADR-0012).
`scripts/check-docs.py` refuses a tag that contradicts the test project (TD-80).
Next: the baseline cache (ADR-0013) and the binding of the other five feature files (TD-62).
Scenarios outside the slice stay `@unverified` or `@backlog`.
