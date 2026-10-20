# ADR-0003: Keep official data and mod data in separate tables

**Status:** Accepted
**Date:** 2026-10-19
**Amended:** 2026-10-19. ADR-0013 replaced the `mod_id` key with `dataset_id`, because two versions of
one mod can share one dataset. ADR-0018 states that mod data is not deleted in the first release, so the
cascade is not used. The table-pair decision did not change.
**Deciders:** Yui

## Context

The archive holds two kinds of data. Official data ships with the server. Mod data comes from mods.

The driver requires that the two kinds stay clearly apart. A mod must first be registered in a `mods` table. Everything that a mod adds must reference that mod.

## Decision

We will use one table pair per dataset: an official table (`items`) and a mod table (`mod_items`). Both have the same columns. Every mod table adds `mod_id`, a foreign key to `mods` with cascade delete. We will use the same pattern for locales, quests, traders, trader assort and quest assort.

## Options Considered

| Dimension               | Table pairs (chosen)                  | One table with a nullable `mod_id` | Separate schemas                   |
|-------------------------|---------------------------------------|------------------------------------|------------------------------------|
| Official data is safe   | Yes: mod writes cannot touch it       | No: same table, same keys          | Yes                                |
| Query all data at once  | Needs a UNION (no view yet)           | Simple                             | Needs a UNION                      |
| Primary keys            | Official: `id`. Mod: `(mod_id, id)`   | Must include `mod_id` for all rows | As table pairs                     |
| Remove one mod          | `DELETE FROM mods` cascades           | Delete by `mod_id`                 | Cascade                            |
| Complexity              | Medium: shared base classes in code   | Low                                | High                               |

## Trade-off Analysis

One table is simpler to query. It also lets mod data collide with official ids and makes the official dataset harder to replace atomically. The driver values a clear boundary, so table pairs win.

## Consequences

- **Easier:** The Archiver replaces the official tables without touching mod rows. A mod can be removed with one delete.
- **Harder:** A query over all items needs a UNION. The API exposes `items` and `modItems` as separate collections.
- **Harder:** Two mods can add the same item id. The mod primary key includes `mod_id`, so both rows can exist.
- **Revisit when:** Users often need one combined list. Add SQL views named `all_items`, `all_quests` and so on.

## Verification

```gherkin
@unverified
Feature: Official data and mod data stay apart

  Scenario: Deleting a mod removes only its rows
    Given the official item "A1" and the mod "mod-x" with the mod item "B1"
    When the mod "mod-x" is deleted
    Then the mod item "B1" no longer exists
    And the official item "A1" still exists

  Scenario: Two mods can add the same item id
    Given the mods "mod-x" and "mod-y"
    When both mods add an item with the id "C1"
    Then both rows exist in the mod items table

  Scenario: Mods can add stock to an official trader
    Given the official trader "T1"
    When the mod "mod-x" adds an assort row for the trader "T1"
    Then the row exists in the mod assort table
    And the official assort table has no new row
```

## Action Items

1. [ ] Decide whether `all_*` views are needed before the first public release.

## Links

- C4: `archive.db`
- Code: `src/SptArchive.Core/Entities/`, `src/SptArchive.Core/Data/ArchiveDbContext.cs`
- Spec: `docs/specs/mod-registry.feature`
