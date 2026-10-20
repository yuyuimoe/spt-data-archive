# ADR-0001: Use PostgreSQL as the archive database

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The server keeps its database as JSON files. The upstream game uses MongoDB, so the data has Mongo-style ids and nested documents.

The archive has a read-heavy workload. Only a scheduled job or a maintainer writes to it. Users only read.

The driver set one hard limit: no MongoDB and no MongoDB-compatible store. MongoDB is not open source, because its license is the SSPL.

## Decision

We will store the archive in PostgreSQL.

## Options Considered

| Dimension               | PostgreSQL                         | MongoDB                      | SQLite                                   | MySQL or MariaDB                         |
|-------------------------|------------------------------------|------------------------------|------------------------------------------|------------------------------------------|
| License                 | Open source                        | SSPL, not open source        | Public domain                            | Open source                              |
| Nested data             | `jsonb` with GIN indexes           | Native documents             | JSON functions, no GIN-style index       | JSON columns, indexing needs extra steps |
| Reads during writes     | MVCC: writers do not block readers | Good                         | One writer at a time                     | Good                                     |
| Access control          | Roles and grants                   | Roles                        | File permissions only                    | Roles and grants                         |
| Search                  | Full-text search and trigrams      | Text indexes                 | FTS5                                     | Full-text indexes                        |
| Team familiarity        | High                               | Not relevant                 | Medium                                   | Medium                                   |

MongoDB lost on the license rule. SQLite lost because it has no server-side roles, so the read-only guarantee would depend on file permissions alone. MySQL and MariaDB lost on JSON indexing.

The alternatives were assessed after the driver chose PostgreSQL.

## Trade-off Analysis

The data is nested, but its shape is stable enough to normalize. PostgreSQL gives relational tables for the stable parts and `jsonb` for the variable parts. This is the main reason it wins over a pure document store.

## Consequences

- **Easier:** One store handles relations, JSON and search. Roles enforce read-only access.
- **Harder:** The nested server JSON must be mapped to tables. See ADR-0002.
- **Revisit when:** The data grows too large for one PostgreSQL node, or the license rule changes.

## Verification

```gherkin
@unverified
Feature: PostgreSQL is the only durable data store

  Scenario: Only PostgreSQL keeps data on disk
    Given the stack is defined in compose.yaml
    When I list the services that mount a persistent volume
    Then the only service in the list is the PostgreSQL service

  Scenario: Raw properties use a GIN index
    Given the database is migrated
    When I read the index definitions of the "items" table
    Then the "props" column has a GIN index with the "jsonb_path_ops" operator class
```

## Action Items

1. [ ] Generate the first EF Core migration (see `docs/tech-debt.md`).

## Links

- C4: `archive.db`
- Code: `src/SptArchive.Core/Data/ArchiveDbContext.cs`, `compose.yaml`
