# ADR-0006: Serve the archive through a thin read-only GraphQL API

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

Users only read the archive. The driver wants a GraphQL API. GraphQL makes queries easy, and it lets the owner control which data leaves the system.

The driver first considered `pg_graphql`. It adds GraphQL to PostgreSQL through one SQL function, `graphql.resolve`. It is not an HTTP endpoint, so a server must still sit in front of it. Supabase would provide that server, but the driver judged Supabase too large for this API.

The driver then chose a thin wrapper in C#. Admin writes may come later through a static panel or through authorized services.

## Decision

We will build a thin ASP.NET Core API with HotChocolate over EF Core. It has one Query type and no Mutation type. It connects to PostgreSQL with a role that only has `SELECT`.

## Options Considered

| Dimension          | HotChocolate API (chosen)           | `pg_graphql` plus a proxy            | Supabase                    | REST API                   |
|--------------------|-------------------------------------|--------------------------------------|-----------------------------|----------------------------|
| Moving parts       | One service                         | Proxy service plus extension         | Many services               | One service                |
| Query limits       | In code: page size, depth, timeout  | Fewer controls in the app layer      | Fewer controls              | Fixed by endpoints         |
| Schema source      | EF Core entities                    | Generated from tables                | Generated from tables       | Hand-written               |
| Team familiarity   | High                                | Low                                  | Low                         | High                       |
| Extension in image | Not needed                          | Needs a Postgres image with it       | Included                    | Not needed                 |

## Trade-off Analysis

`pg_graphql` is simple to query, but it needs an HTTP layer anyway. A thin C# API gives the same GraphQL surface with explicit limits and no database extension. It also keeps the stack in one language (ADR-0007).

## Consequences

- **Easier:** Page size, depth, timeout and caching live in code that the team knows.
- **Harder:** The schema follows the C# entities, so a new table needs a new entity and a new Query field.
- **Harder:** JSON columns reach the client as JSON text. See `docs/tech-debt.md`.
- **Revisit when:** The team wants to remove the API project. Then re-check `pg_graphql`.

## Verification

```gherkin
@unverified
Feature: The public API is read-only

  Scenario: The schema has no mutations
    Given the running Api
    When a client reads the GraphQL schema
    Then the schema has a Query type
    And the schema has no Mutation type

  Scenario: The database refuses writes from the Api role
    Given the Api connects as the role "archive_reader"
    When that role tries to insert a row into "items"
    Then PostgreSQL rejects the statement with a permission error

  Scenario Outline: Query limits protect the database
    Given the running Api
    When a client sends a query that <violation>
    Then the Api returns an error
    And the database does not run the query

    Examples:
      | violation                        |
      | asks for a page larger than 200  |
      | nests deeper than 10 levels      |
```

## Action Items

1. [ ] Verify the HotChocolate 16 setup compiles (see `docs/tech-debt.md`).
2. [ ] Put the Api behind a rate-limiting proxy.

## Links

- C4: `archive.api`
- Code: `src/SptArchive.Api/`
- Spec: `docs/specs/query-api.feature`
