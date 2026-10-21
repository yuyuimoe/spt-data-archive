# ADR-0012: Run the Gherkin specs with Reqnroll

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The specs in `docs/specs` are written in Gherkin. The team wants them to run as tests, so that a spec and its test cannot drift apart.

The project has no test project and no BDD runner yet. Agents that follow the NoVibe flow build each slice test-first from the Gherkin scenarios.

## Decision

We will use Reqnroll as the BDD runner for .NET, with xUnit as the test framework. Tests that need PostgreSQL will use a real PostgreSQL container, not a mock.

## Options Considered

| Dimension               | Reqnroll                            | SpecFlow                 | Plain xUnit with comments |
|-------------------------|-------------------------------------|--------------------------|---------------------------|
| Runs `.feature` files   | Yes                                 | Yes                      | No                        |
| Maintenance status      | Active, open source                 | Not maintained           | Not relevant              |
| .NET 10 support         | Expected, check before adopting     | Unlikely                 | Yes                       |
| Spec and test stay linked | Yes                               | Yes                      | No                        |

The .NET 10 support of Reqnroll was not checked when this ADR was written. The amendment of 2026-10-21 below
records the check.

## Trade-off Analysis

A runner adds a dependency, but it makes the Gherkin files executable. Without it, the specs are only documents and they drift.

## Consequences

- **Easier:** A scenario fails when the behavior changes.
- **Harder:** Tests that use PostgreSQL need a container, which slows them down.
- **Revisit when:** Reqnroll does not support .NET 10, or the team prefers another runner.

## Verification

```gherkin
@backlog
Feature: Specs are executable

  Scenario: Every scenario without the @backlog tag has a step definition
    Given the feature files in "docs/specs"
    When the test project runs
    Then no scenario without the "@backlog" tag is reported as pending
```

## Action Items

1. [x] Check Reqnroll with .NET 10 (amendment of 2026-10-21).
2. [x] Link the feature files from `docs/specs`. The tests live in `tests/SptArchive.Tests`, not in a new
   project (amendment of 2026-10-21).
3. [x] Bind `mod-api.feature` (19 scenarios).
4. [ ] Bind the other feature files, one slice at a time. Their scenarios keep `@unverified` or `@backlog`
   until then (TD-62).
5. [ ] Decide whether `ModSurfaceTests` stays. It asserts the same facts as `mod-api.feature` in plain xUnit
   (the driver decides; nothing was removed).

## Amendment of 2026-10-21: what was built

The first slice (TD-06) bound `docs/specs/mod-api.feature`. This amendment records the facts. It does not
change the decision: Reqnroll on xUnit, against a real PostgreSQL.

1. **Package.** `Reqnroll.xunit.v3` 3.3.4, pinned in `Directory.Packages.props` with the other test packages.
   It runs on .NET 10 with `xunit.v3` 4.0.2. It brings `Reqnroll` and `Reqnroll.Tools.MsBuild.Generation` at
   the same version, and `Gherkin` 35.0.0.
2. **Project.** The scenarios run in `tests/SptArchive.Tests`, and `tests/SptArchive.Specs` was not created.
   The steps must call the harness of the existing tests: `ArchiveDatabase`, `ApiHarness` and the other
   helpers are `internal` to that assembly. A second project would copy them or force a shared library for
   no gain. The generated tests are plain xUnit tests in the same assembly, so `dotnet test SptArchive.slnx`
   runs them with the other tests.
3. **Where the feature files live.** They stay in `docs/specs`. The project links each bound file by name:
   `<ReqnrollFeatureFiles Include="../../docs/specs/mod-api.feature" Link="Specs/mod-api.feature" />`. A file
   that is not listed does not run. The generated code goes to `obj/`
   (`ReqnrollUseIntermediateOutputPathForCodeBehind`), never next to the feature file. The step
   definitions are in `tests/SptArchive.Tests/Specs`, and the settings are in
   `tests/SptArchive.Tests/reqnroll.json`.
4. **A step with no definition fails.** `missingOrPendingStepsOutcome` is `Error`. Measured before the steps
   existed: the 19 scenarios failed with `MissingStepDefinitionException`, and 313 of the 314 other tests passed.
   The 314th, `ApiLimitsTests.A_normal_page_is_inside_the_cost_limit`, asks for the release 4.0.13, which the
   empty test database on port 5433 does not hold. It failed the same way before this change.
5. **How a scenario is tagged verified.** A scenario of a linked file that passes has no tag. The author removes
   `@unverified` in the same change that binds the scenario. A scenario of a file that is not linked keeps its
   tag, because no test exists for it. Nothing checks this rule yet (TD-62).
6. **The database.** A scenario writes through `ArchiveDatabase.BeginAsync`, in one transaction that
   an `[AfterScenario]` hook rolls back. It reads through `ApiHarness`, the schema that the Api serves. No
   container starts for a scenario, and no mock replaces the database.
7. **Invariant globalization.** `Directory.Build.props` turns it on. Reqnroll asks for the culture `en-US` at
   start, and the runtime refused it, so every test of the assembly failed in its fixture. The test project
   sets `PredefinedCulturesOnly` to `false`. The name then resolves to the invariant data, and the tests keep
   the invariant culture.
8. **What the scenarios leave implicit.** `mod-api.feature` does not state the configuration file, the
   observation or the runs that some scenarios read. The first step (`the archive holds the mod dataset`)
   writes them: the official run, the mod run that made the dataset, the shipped configuration file
   `com.example.mod/config/config.json` and the observation for the release 4.1.6. A scenario about the
   history of the runs relies on that. The dates of the runs are in the year 2999, so a run that another
   test committed to the same database never comes first.
9. **Result.** 19 of 19 scenarios of `mod-api.feature` pass and carry no `@unverified` tag, and the 314 other
   tests are unchanged. Two changes of a step made two scenarios fail (a wrong new value, a pinned address), so the
   assertions are not empty.

## Links

- `docs/specs/README.md`
- `docs/tech-debt.md` (TD-06, TD-62)
