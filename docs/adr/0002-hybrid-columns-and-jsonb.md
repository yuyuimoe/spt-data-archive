# ADR-0002: Store variable data as jsonb next to a few real columns

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The server's item model has hundreds of optional properties that differ per item type. Quests have deeply nested conditions and rewards. Trader base objects have nested loyalty levels, buy categories and repair data.

The driver asked for a normalized database. A fully normalized model would need a column or table for every property.

Users filter and sort by a small set of fields: weight, size, parent, trader, location.

## Decision

We will promote the fields that users filter by to real columns. We will keep the complete original object in a `jsonb` column with a GIN index. We will normalize structures that users join on: locales and trader assort.

## Options Considered

| Dimension        | Fully normalized                  | Hybrid (chosen)                 | Documents only (one jsonb per row) |
|------------------|-----------------------------------|---------------------------------|------------------------------------|
| Complexity       | Very high: hundreds of columns    | Medium                          | Low                                |
| Query speed      | Best for every field              | Best for promoted fields        | Slower, GIN only                   |
| Lossless         | Only if every field is modeled    | Yes                             | Yes                                |
| Server changes   | Each new field needs a migration  | New fields land in jsonb safely | New fields land in jsonb safely    |
| Team familiarity | High                              | High                            | Medium                             |

## Trade-off Analysis

Full normalization is exact, but it breaks with every server update. Documents only is simple, but it gives up relational joins and fast filters. The hybrid keeps nothing out of the archive and still makes the common queries fast.

## Consequences

- **Easier:** The importers never lose data. A new server property needs no migration.
- **Harder:** A query on a non-promoted property must use `jsonb` operators. The GraphQL API returns these columns as JSON text (see `docs/tech-debt.md`).
- **Revisit when:** A non-promoted property becomes a frequent filter. Promote it to a column then.

## Verification

```gherkin
@unverified
Feature: Item properties are archived without loss

  Scenario: An item keeps every property it had in the source
    Given a source item with properties "Weight", "Width", "Height" and "RarePropertyX"
    When the item importer runs
    Then the row has the real columns "weight", "width" and "height"
    And the "props" column contains "RarePropertyX" with its original value

  Scenario Outline: Whole numbers written as decimals still fill integer columns
    Given a source item whose "<field>" is <value>
    When the item importer runs
    Then the column "<column>" holds <stored>

    Examples:
      | field        | value | column         | stored |
      | Width        | 2.0   | width          | 2      |
      | Height       | 1     | height         | 1      |
      | StackMaxSize | 0.5   | stack_max_size | null   |
```

## Action Items

1. [ ] Decide which extra properties deserve a column when real data is available.

## Links

- Code: `src/SptArchive.Core/Entities/Items.cs`, `src/SptArchive.Archiver/Importers/ItemImporter.cs`
- Spec: `docs/specs/archive-official-data.feature`
