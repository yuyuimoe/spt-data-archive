# ADR-0005: Choose how to ingest mod data

**Status:** Superseded by ADR-0013
**Date:** 2026-10-19
**Deciders:** Yui must decide

## Context

The mod tables exist. Nothing fills them yet.

Mods add items, quests, traders, locales and trader stock. Some mods use an API such as WTT Lib. Other mods ship plain JSON files. Some mods build their data in code at startup.

The driver listed three paths:

- Read mods that use an API through that API.
- Read the JSON files of mods that do not use an API.
- Add a server mod that exports data while the server starts.

The driver also asked for the path with the least friction, the lowest cost and the exact values.

## Decision

Superseded. ADR-0013 chooses option 3 with a container per mod and a baseline run.
The text below records the state before the decision.

The assistant suggests starting with direct JSON reading (Option 1). It reuses the existing importers and has no runtime cost. Add Option 2 only for mods that Option 1 cannot read.

## Options Considered

| Dimension               | 1. Read mod JSON files        | 2. Read through WTT Lib and server libraries | 3. Exporter mod on a running server | 4. Patch or fork the server |
|-------------------------|-------------------------------|----------------------------------------------|-------------------------------------|-----------------------------|
| Mods that it can read   | Mods with data files only     | Mods that use those APIs                     | All mods that register data         | All mods                    |
| Memory use              | Low                           | Low to medium                                | High: the server loads everything   | Medium                      |
| Reuses existing code    | Yes                           | Partly                                       | No                                  | No                          |
| Maintenance             | Low                           | Medium: tied to library versions             | Medium                              | High                        |
| Exact values            | Yes, but not the final values | Yes                                          | Yes: final values after all mods    | Yes                         |

"Not the final values" means that Option 1 sees the file data, not what other mods change later at startup.

## Trade-off Analysis

The decision balances coverage against cost. Option 3 has the best coverage and the highest memory cost. Option 1 is the cheapest and misses mods that build data in code.

## Consequences

- **Easier (any option):** The schema is ready. See ADR-0003.
- **Harder:** Mods that modify official items instead of adding new ones have no table yet. See `docs/tech-debt.md`.
- **Revisit when:** A target mod cannot be read with the chosen option.

## Verification

```gherkin
@backlog @decision-needed
Feature: Mod data is archived under a registered mod

  Scenario: A mod must exist before its data is archived
    Given a mod that is not in the mods table
    When the Archiver ingests data of that mod
    Then the run registers the mod first
    And every row it writes references that mod

  Scenario: A mod with data files is archived
    Given a mod folder with an items file in the server database format
    When the Archiver ingests that mod
    Then the mod items table holds the items of the file
    And each locale entry of the mod is stored in the mod locale table
```

## Action Items

1. [ ] Choose an option and change the status of this ADR to Accepted.
2. [ ] Write the Gherkin scenarios for the chosen option in `docs/specs/mod-registry.feature`.

## Links

- C4: `modSources` (planned)
- Spec: `docs/specs/mod-registry.feature`
- Related: ADR-0003, ADR-0004
