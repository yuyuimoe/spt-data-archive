# Specifications

The `.feature` files in this folder describe what the system does. They are written in [Gherkin](https://cucumber.io/docs/gherkin/reference). They are the tests: Reqnroll runs the files that the test project links (ADR-0012). Five files are linked today (see the slice below).

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

Done in the last slices: four files run as tests with Reqnroll.
They are `archive-official-data.feature` (28 of 38 tests), `mod-registry.feature` (28 of 55), `backup-and-restore.feature`
(9) and `query-api.feature` (73 of 76). They run against throwaway databases and the real script and Api (TD-62). Done before them: one row for
one object of the seven files that a run changes (ADR-0044), and the filter of a jsonb column by the value at a
path (ADR-0040, TD-52, TD-55). Six feature files are linked from `tests/SptArchive.Tests`: `mod-api.feature`
(24 scenarios), `file-rows.feature` (8), `backup-and-restore.feature` (9), `query-api.feature` (76, of which 3
keep `@unverified`) and `archive-official-data.feature` (38, of which 10 are skipped: one scenario whose title
contradicts its steps, and the 9 examples of the `@backlog` outline) and `mod-registry.feature` (55, of which
27 are skipped: 17 `@backlog` scenarios with 21 tests, and 6 scenarios that need a decision of the driver). `scripts/check-docs.py` refuses a tag that contradicts the test project (TD-80).
Next: the baseline cache (ADR-0013). `bot-and-faction-data` (12 scenarios) stays unbound and tagged `@unverified`:
the tables it names do not exist (TD-62).
Scenarios outside the slice stay `@unverified` or `@backlog`.
