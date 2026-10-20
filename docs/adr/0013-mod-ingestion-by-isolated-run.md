# ADR-0013: Ingest mod data by running one mod in an isolated server container

**Status:** Accepted
**Date:** 2026-10-19
**Amended:** 2026-10-19. The dependency set is now decided (see "The dependency set of a run").
The change model moved to [ADR-0017](0017-record-a-template-operation.md).
Deletion moved to [ADR-0018](0018-do-not-delete-mod-data-in-the-first-release.md).
The decision below did not change.
**Confirmed:** The driver confirmed that two mod versions with the same content share one dataset.
The data model below states that constraint.
**Deciders:** Yui

## Context

ADR-0005 left the choice of mod ingestion open. It named four options. The server source
(`sp-tushonka/server-csharp`) now decides between them. These facts come from that source:

- `ProgramStatics.MODS()` is not a setting. `ProgramStatics.Initialize` computes it from `BuildType`.
  Turning it off also turns off mod loading.
- `AbstractModMetadata.IsBundleMod` is declared metadata. It does not describe the files of the mod.
- `ModValidator.ValidateMods` is all or nothing. One missing dependency or one failed range check
  returns an empty mod set, not a partial one.
- `ModValidator.ValidateCoreAssemblyReference` throws for a mod that is built against a newer core.
  At startup that exception becomes a blocked console read.
- `App.InitializeAsync` runs the `IOnLoad` services in `OnLoadOrder` sequence.
  `OnLoadOrder.PostSptModLoader` is the last slot, so a service after it sees all mod data.
- `TestMod.cs` shows the house pattern: an order value one past a named slot.

The catalog holds about 699 mods. No mod states which database rows it wrote.
The server merges data into one shared database. The origin of a row is lost after the merge.

## Decision

We will run one mod version, with its pinned dependencies, in its own throwaway server container.

An **exporter mod** at `OnLoadOrder.PostSptModLoader + 1` writes the final in-memory database to
JSON. It then calls `IHostApplicationLifetime.StopApplication`.

The Archiver runs the same container set twice:

1. A **baseline** run with the dependencies and without the target mod.
2. A **target** run with the dependencies and the target mod.

The difference between the two outputs is the data of that mod version.
A baseline is content addressed, so mods with the same dependency set share one baseline result.

## Options Considered

| Dimension              | 1. Read the mod files             | 2. Read through mod APIs        | 3. Exporter mod in a container       | 4. Patch or fork the server     |
|------------------------|-----------------------------------|---------------------------------|--------------------------------------|---------------------------------|
| Mods it can read       | Mods with data files only         | Mods that use those APIs        | Every mod that registers data        | Every mod                       |
| Values it sees         | File values, before other mods    | Library values                  | Final values, after every mod        | Final values                    |
| Attribution of a row   | The file states it                | The call states it              | Requires a baseline run              | Requires a baseline run         |
| Memory and time        | Low                               | Low                             | High: one server per mod             | Medium                          |
| Reuses existing code   | Yes                               | Partly                          | The importers, on a different input  | No                              |
| Maintenance            | File format drift                 | Library version drift           | Server internals drift               | Server internals drift          |

Option 1 reads what a mod ships, not what the server ends up with. Option 2 covers only the mods
that use a known API. Option 4 patches the server in a way that CI cannot see.
Option 3 is the only option that covers every mod and sees the final values.

## Trade-off Analysis

The cost is the number of runs. Each mod version needs two server starts, at roughly a minute each.
The gain is complete and exact coverage with no per-mod code.

The alternative is a cheaper read of the mod files. That option fails on the mods that build data
in code, which is a growing share of the catalog. It also shows pre-merge values, which are not the
values a player sees.

Memory is not a real cost. Containers run one at a time with a hard limit, and each one dies at once.
The exporter removes the need to change `Program.cs`. It is an ordinary mod at the last load stage.

## Consequences

- **Easier:** Every mod that loads gets a data model, with no per-mod integration work.
  Coverage and exact values come from one mechanism.
- **Easier:** A failed mod cannot stop another mod. One mod per container contains the blast radius.
  This matters because `ValidateMods` returns no mods at all when one mod of the set is bad.
- **Harder:** The archive depends on server start-up internals. A renamed service or order slot
  breaks the exporter until someone updates it. See TD-20.
- **Harder:** The cost is two server starts per mod version. The catalog must state the
  dependency set of a mod, or the run cannot build it. See "The dependency set of a run".
- **Harder:** The archive observes a mod only when the mod releases. It does not know whether an
  old version still works on a newer server. The compatibility probe in ADR-0016 answers that
  question cheaply, but it is not part of the first slice.
- **Revisit when:** The exporter cannot see a dataset that a mod writes, or a server release
  removes the load-order slot that the exporter uses.

### Data model that this ADR fixes

A **mod dataset** is the content of one mod version. It holds the rows that the mod adds and the
changes that the mod makes to official rows.

A **mod data observation** is one attempt to load one mod version on one server version.
A mod version has at most one observation per server version. The observation points at a dataset
or states why there is none.

An observation with no dataset has an outcome other than `archived`: `not_loaded`,
`dependency_unresolved`, `outside_declared_range`, `crashed` or `timeout`.
Silence is not an outcome. A run that does not see the mod GUID in the export fails.

Datasets are shared by `(mod_guid, content_hash)`, and `mod_datasets` states that pair as a unique
constraint. A new mod version with unchanged content adds an observation that points at the
existing dataset. Nothing deletes a dataset in the first release
([ADR-0018](0018-do-not-delete-mod-data-in-the-first-release.md)). A dataset with no observation
is a bug, and the run fails on it.

The change model has two levels ([ADR-0017](0017-record-a-template-operation.md)).
`mod_templates` holds one row for each template that the dataset touches, with the operation
`added`, `modified` or `removed`. `mod_item_changes` holds the changed paths under that row.
The path is an RFC 6901 JSON pointer. The old value comes from the official table of the server
version in the observation.

### The dependency set of a run

The baseline run needs the dependencies of the target mod. That set comes from the catalog.
The catalog states the requirements of a mod with a version range. The Archiver resolves each range
to the newest version that satisfies it.

If the catalog does not state a requirement, the Archiver reads the mod metadata once and uses it.
The source of each entry is recorded, so a catalog gap is visible and not hidden.

The chosen set is frozen on the observation, as `dependency_set` (a JSON array of guid, version
and source) and `dependency_set_hash`. It sits on the observation and not on the dataset, because
two different dependency sets can produce the same content. A dataset is then one row, and a
provenance column on it would describe only one of the two runs. The baseline cache is keyed by
`(dependency_set_hash, spt_version)`.

## Verification

```gherkin
Feature: Mod data is archived by an isolated run of one mod

  Scenario: The exporter writes the database after every mod has loaded
    Given a container with the mod "com.example.mod" and its dependencies
    When the exporter runs at the load stage after "PostSptModLoader"
    Then the export holds the data of every loaded mod
    And the host stops after the export is written

  Scenario: Only the difference belongs to the target mod
    Given a baseline export without the target mod
    And a target export with the target mod
    When the Archiver compares the two exports
    Then the mod dataset of that mod version holds only the added rows
    And no row of a dependency is in that dataset

  Scenario: A mod that changes an official item is recorded as a change
    Given a mod that changes the "Weight" of the official item "A1"
    When the Archiver stores the data of that mod
    Then the table "mod_item_changes" holds the path "/_props/Weight"
    And that row holds the old value and the new value
    And the official row of "A1" stays unchanged

  Scenario: A mod version with unchanged content reuses the dataset
    Given the mod "com.example.mod" in the version "1.0.0" has the dataset "D1"
    When the version "1.1.0" of that mod exports the same content
    Then the observation of "1.1.0" points at the dataset "D1"
    And no new dataset exists

  Scenario: A mod that does not load is recorded as an outcome
    Given the mod "com.example.mod" fails its own load
    When the Archiver runs that mod
    Then the observation states the outcome "not_loaded"
    And the observation has no dataset
    And the run does not report success for that mod

  Scenario: An unresolved dependency does not remove the outcome
    Given the mod "com.example.mod" needs a dependency that the container does not have
    When the Archiver runs that mod
    Then the observation states the outcome "dependency_unresolved"
    And the exporter does not report the data of that mod

  Scenario: A mod outside its declared server range is skipped and recorded
    Given the mod "com.example.mod" declares support for ">=4.0.0 <4.1.0"
    And the container runs the server version "4.1.0"
    When the Archiver considers that mod
    Then it does not start a container for that mod
    And the observation states the outcome "outside_declared_range"

  Scenario: One bad mod does not stop the other mods
    Given the mods "mod-a" and "mod-b"
    And "mod-a" throws while it loads
    When the Archiver archives both mods
    Then "mod-a" has the outcome "crashed"
    And "mod-b" has a dataset

  Scenario: A dataset that no observation points at fails the run
    Given the dataset "D1" of the mod "com.example.mod"
    And no observation points at "D1"
    When an archive run succeeds
    Then the run fails with a message that names "D1"
    And the table "mod_datasets" still holds "D1"

  Scenario: The dependency set of a run is frozen on the observation
    Given the catalog states that "com.example.mod" needs "com.dep.a" ">=2.0.0"
    When the Archiver archives that mod version on "4.0.13"
    Then the observation states the dependency "com.dep.a" in the version "2.4.1"
    And the observation states that the source of that entry is the catalog

  Scenario: A catalog gap is filled from the mod metadata and the source is recorded
    Given the catalog states no requirement for "com.example.mod"
    And the metadata of that mod needs "com.dep.a" ">=2.0.0"
    When the Archiver archives that mod version
    Then the observation states that the source of that entry is the metadata

  Scenario: Two mods with the same dependency set share one baseline
    Given the mods "mod-a" and "mod-b" declare the same dependency set
    When the Archiver archives both mod versions on "4.0.13"
    Then the Archiver starts one baseline container for both mods
```

## Action Items

1. [ ] Add `spt_versions`, `mod_datasets`, `mod_data_observations` and `mod_item_changes`,
   and replace `mod_id` with `dataset_id` in the six `mod_*` tables.
2. [ ] Write the exporter mod in its own solution, against a pinned server commit.
3. [ ] Add the container runner to the Archiver: one mod, no network, read-only mount, hard kill.
4. [ ] Write the baseline cache, keyed by the dependency set hash.
5. [ ] Verify the load order of the exporter with one real mod, before the catalog run.
6. [ ] Write the orphan-dataset check as an assertion in the run transaction (ADR-0018).
7. [ ] Add `dependency_set` and `dependency_set_hash` to `mod_data_observations`, and key the
   baseline cache by them with the server version.
8. [ ] Resolve a version range to the newest matching version, and record the source of each entry.

## Links

- Supersedes: [ADR-0005](0005-mod-ingestion-strategy.md)
- Related: [ADR-0003](0003-separate-official-and-mod-tables.md),
  [ADR-0014](0014-keep-every-archived-spt-release.md),
  [ADR-0015](0015-suppress-bundles-with-a-patch-mod.md),
  [ADR-0017](0017-record-a-template-operation.md),
  [ADR-0018](0018-do-not-delete-mod-data-in-the-first-release.md)
- Spec: [docs/specs/mod-registry.feature](../specs/mod-registry.feature)
- C4: `archive.modRunner`, `archive.modRunner.exporterMod`
- Code: `src/SptArchive.Core/Entities/Mod.cs`
