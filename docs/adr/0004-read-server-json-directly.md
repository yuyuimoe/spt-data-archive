# ADR-0004: Read the official data directly from the server JSON files

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

The official data lives in `SPT_Data/database` inside the server repository. The server loads all of it into memory when it starts. Locations load lazily, so a full export through a running server uses a lot of memory.

The driver asked for the way with the least friction. It must be cheap to maintain and it must keep every value.

The driver also noted that the server libraries and WTT Lib exist in C#. They can read data in its original form.

## Decision

We will read the official JSON files directly with `System.Text.Json`. We will not start the server. We will keep the raw object of each record in `jsonb`.

## Options Considered

| Dimension          | Read files directly (chosen) | Export from a running server (mod) | Patch the server with HarmonyX | Fork the server           |
|--------------------|------------------------------|------------------------------------|--------------------------------|---------------------------|
| Memory use         | Low: one file at a time      | High: server loads everything      | Medium: custom load and unload | Medium                    |
| Maintenance        | Low                          | Medium                             | High: patches break on updates | High: must merge upstream |
| Value precision    | Exact: raw JSON              | Exact                              | Exact                          | Exact                     |
| Sees mod changes   | No (see ADR-0005)            | Yes                                | Yes                            | Yes                       |
| Needs server build | No                           | Yes                                | Yes                            | Yes                       |

A fifth option references the server libraries and reads the data into the server's own C# types. It gives typed access. It also ties the archive to one server version. We keep it as a later improvement.

## Trade-off Analysis

Direct reading depends only on the file layout. It is the cheapest option and it needs no running server. It cannot see what mods change at runtime. ADR-0005 covers that gap.

## Consequences

- **Easier:** The Archiver runs anywhere with a read-only copy of `SPT_Data`. A change in the hash of the files triggers a new import.
- **Harder:** A change in the file layout breaks an importer. The importers have no typed models, so a renamed property goes unnoticed.
- **Harder:** `items.json` and the loose-loot files use Git LFS. The checkout needs `git lfs pull`. The importers stop with a clear message when they find a pointer file.
- **Revisit when:** The server changes its file layout, or the archive needs data that only exists at runtime.

## Verification

```gherkin
@unverified
Feature: The Archiver reads the server files without the server

  Scenario: An import does not need a running server
    Given a read-only copy of the server "SPT_Data/database" folder
    And no server process is running
    When the Archiver runs
    Then the official tables hold the data from those files

  Scenario: A Git LFS pointer stops the import
    Given "templates/items.json" is a Git LFS pointer file
    When the Archiver runs
    Then the run fails with a message that asks for "git lfs pull"
    And the official tables keep their previous data
```

## Action Items

1. [ ] Run the item importer against real data after `git lfs pull`.
2. [ ] Decide later whether to read through the server's own C# types.

## Links

- C4: `archive.archiver`, `sptServer`
- Code: `src/SptArchive.Archiver/Importers/`
- Spec: `docs/specs/archive-official-data.feature`
