# ADR-0023: Classify a mod by the parts it ships

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0013, ADR-0017, ADR-0020, ADR-0021

## Context

The Archiver needs to know what a mod can contribute before it runs the mod. The Forge page has a
category, but the category says nothing about where the code runs. Two mods that the Forge lists as
"Overhaul" behave in opposite ways:

- **SVM 2.2.3** (`fika.ghostfenixx.svm`) ships `ServerValueModifier.dll` for the server and `Greed.exe`
  for Windows. It changes server values.
- **ISB Special Forces 2.0.0** ships 270 files and **all 270 are under `BepInEx/`**. It changes the
  client only. A server run cannot see it at all.

The layout of the artifact does say where the code runs. SPT 4.x has two roots:

- `BepInEx/` holds client parts: `plugins/`, `patchers/`, `config/`.
- `SPT_Runtime/user/mods/<Mod>/` holds server parts: a `.dll`, and a `db/` tree of data fragments
  that the server merges into its tables at load.

A mod can have both roots. WTT - Black Division 1.3.1 ships `BepInEx/patchers/BlackDiv` **and**
`SPT_Runtime/user/mods/BlackDivServer/db/`. WTT - Content Backport 2.0.5 is the same shape: 305 `db/`
fragments and a client plugin with bundles.

The eleven mods the project has unpacked so far:

| Mod | `BepInEx/` | `SPT_Runtime/…/db/` | server `.dll` | bundles |
| --- | --- | --- | --- | --- |
| WTT - Armory 3.0.0 | no | no | yes | yes |
| WeebSights 1.4.1 | no | no | yes | yes |
| Gunwright 1.0.2 | no | no | yes | no |
| Ledger 1.1.0 | no | no | yes | no |
| MassivesoftWeapons 2.0.1 | no | no | yes | yes |
| female pmc voice 1.0.6 | no | no | yes | no |
| Skill Effects Extended 1.0.0 | yes | no | no | no |
| EFT Effect Manager 1.1.0 | yes | no | no | no |
| ISB Special Forces 2.0.0 | yes | no | no | no |
| Waypoints Expanded Navmesh 1.9.0 | yes | no | no | 12 navmesh |
| SVM 2.2.3 | no (a `.exe`) | no | yes | no |
| WTT - Black Division 1.3.1 | yes | yes | yes | no |
| WTT - Content Backport 2.0.5 | yes | yes | yes | yes |

## Decision

The catalog records a **set of facets**, not one kind. The facets come from the file list of the
artifact at ingest, never from the Forge page.

| Facet | Proof in the artifact | What the archive does |
| --- | --- | --- |
| `client` | files under `BepInEx/` | catalogue row only, plus the `BepInEx/config/*.cfg` files |
| `server_data` | a `db/` tree under `SPT_Runtime/user/mods/` | a mod dataset: the rows the server holds after it merges them |
| `server_code` | a `.dll` under `SPT_Runtime/user/mods/` | a mod dataset: the rows the code produces at run time |
| `bundle` | `.bundle`, or the asset files a bundle loads | nothing; the bundle is not data (ADR-0020, TD-28) |

Rules:

1. A mod with `client` and without `server_data` or `server_code` gets no dataset. Its observation
   has the outcome `no_server_data`.
2. A mod with `server_data` is archived through a server run, so the merged result is what the archive
   stores. The `db/` fragments are evidence of provenance, not a separate dataset.
3. The facets are a set. Black Division is `{client, server_data, server_code}`.
4. `bundle` never becomes a dataset row. A mod that only ships bundles gets a warning (TD-28).

## Options Considered

1. **The Forge category.** Cheap, and wrong: "Overhaul" covers both SVM and ISB.
2. **One kind per mod.** Simple, and false: Black Division and Content Backport are both at once.
3. **A set of facets from the file list.** Chosen. The file list is in the artifact and cannot lie.
4. **The mod metadata range only.** It says which SPT release the mod targets and nothing about where it
   runs.

## Trade-off Analysis

The facet set costs one directory scan per artifact and gives an answer that a test can check. One
kind would have been less to model, but the first mod that ships both roots breaks it, and two of the
eleven already do. The cost is that the catalog must store a set, so a row with three facets needs a
child table or an array; an array is enough because nothing queries a facet alone.

## Consequences

- The archive stops reporting client mods as "a mod that changes nothing". Skill Effects Extended and
  EFT Effect Manager change the game and change no server table, and the archive says so.
- ISB Special Forces is the hard case: a mod that the Forge and the players call a new faction, with
  no server file at all. The archive cannot show its data, and the observation says why.
- The `BepInEx/config/*.cfg` files are archived because a second project reads client configuration.
  That project reads the catalog, not the data tables.
- Waypoints Expanded Navmesh has the name of a locations mod and is a client navmesh mod. The facets
  make the difference visible: it is `{client, bundle}`, and it adds no location.
- SVM is `{server_code}` plus a Windows GUI. The GUI is not archivable and its preset is (ADR-0026).

## Verification

```gherkin
@adr-0023 @unverified
Feature: Classify a mod by the parts it ships

  Scenario: A client-only mod gets no dataset
    Given a mod artifact with files under "BepInEx" and no "SPT_Runtime" directory
    When the Archiver catalogs the artifact
    Then the mod has the facet "client"
    And the mod has no dataset
    And the observation outcome is "no_server_data"

  Scenario: A mod with both roots gets a dataset
    Given a mod artifact with files under "BepInEx" and a "db" tree under "SPT_Runtime/user/mods"
    When the Archiver catalogs the artifact
    Then the mod has the facets "client" and "server_data"

  Scenario: A bundle is not data
    Given a mod artifact whose server directory holds only bundles
    When the Archiver runs the mod
    Then the mod dataset is empty
    And the run logs a warning that names the bundles
```

## Action Items

- [ ] Add the facet columns to the mod catalog table.
- [ ] TD-32: the exporter dumps three tables. The facets need the other nine.
- [ ] TD-28: warn when a mod ships bundles and no data.

## Links

- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): one mod version per container.
- [ADR-0017](0017-record-a-template-operation.md): the operation a dataset records.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter.
- [ADR-0021](0021-archive-a-patch-mod-with-its-base.md): the dependency set.
