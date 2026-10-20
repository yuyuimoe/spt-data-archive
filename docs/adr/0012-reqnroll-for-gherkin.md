# ADR-0012: Run the Gherkin specs with Reqnroll

**Status:** Proposed
**Date:** 2026-10-19
**Deciders:** Yui must confirm

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

The .NET 10 support of Reqnroll was not checked. Check it before adoption.

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

1. [ ] Check Reqnroll with .NET 10.
2. [ ] Add `tests/SptArchive.Specs` and link the feature files from `docs/specs`.

## Links

- `docs/specs/README.md`
