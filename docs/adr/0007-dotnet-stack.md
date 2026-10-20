# ADR-0007: Build every project in C# on .NET 10

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The SPT server is written in C#. It has libraries that change server data. WTT Lib also has a C# library that adds items to the server.

The driver wants C# as the first language of this project. The driver also wants to use these libraries to read data in its original form, not to build mods.

The server repository targets .NET 10.

## Decision

We will write the Core library, the Archiver and the Api in C# on .NET 10. We will use EF Core with Npgsql for data access and HotChocolate for GraphQL.

## Options Considered

| Dimension                   | C# on .NET 10 (chosen) | TypeScript on Node | Python               |
|-----------------------------|------------------------|--------------------|----------------------|
| Can reference server libraries | Yes                 | No                 | No                   |
| GraphQL server              | HotChocolate           | Many               | Many                 |
| Same language as the server | Yes                    | No                 | No                   |
| Team familiarity            | High                   | Medium             | Medium               |

## Trade-off Analysis

The other languages have good GraphQL tools. Only C# can use the server libraries directly. The driver also works in C# every day, so it costs the least to maintain.

## Consequences

- **Easier:** The Archiver can later read data through the server types (ADR-0004, ADR-0005).
- **Harder:** The team must follow .NET and package updates. The server repository also targets .NET 10, so both move together.
- **Revisit when:** The project drops the server libraries for good, and a different language fits the team better.

## Verification

```gherkin
@unverified
Feature: The solution is a .NET 10 solution

  Scenario: Every project targets .NET 10
    Given the solution "SptArchive.slnx"
    When I read the target framework of each project
    Then every project targets "net10.0"
```

## Action Items

1. [ ] Build the solution (see `docs/tech-debt.md`).

## Links

- Code: `Directory.Build.props`, `src/*/*.csproj`
