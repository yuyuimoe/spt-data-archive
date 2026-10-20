# ADR-0026: Archive a value mod's preset, and say when it did nothing

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0013, ADR-0017, ADR-0020, ADR-0024

## Context

SVM 2.2.3 changes server values. It is a server mod with a Windows GUI, and the GUI writes a preset
that the server mod reads. The project ran it in a container against SPT 4.1.6. The log says:

```
Mod: SVM version: 2.2.3 (GUID: fika.ghostfenixx.svm | targets SPT: ~4.1.3) loaded
[SVM] Initialization cancelled: Preset or/and Loader is not found or null
Mod is disabled
```

The artifact explains it: `Loader/loader.json` holds `{"CurrentlySelectedPreset": null}` and
`Presets/` is empty. A preset is produced by `Greed.exe`, a WPF application that exists only on
Windows, and the project runs the server headless on Linux.

So the mod loads, reports success, and changes nothing. Recording that as "archived, no changes" is
the same error the archive made for the clothes mods (ADR-0022): the run is honest, the label is wrong.

## Decision

1. The archive stores what the mod ships as a **preset artifact**: the `Presets/` tree, `Loader/` and
   any configuration file, as one jsonb document per mod dataset, under the key `preset`.
2. The observation outcome for a mod that loads and then disables itself is `loaded_but_disabled`, and
   the observation carries the reason text from the mod log.
3. The archive does not author a preset. If the mod ships none, the preset is empty, the `globals`
   diff is empty, and the run says so.
4. If a preset is later supplied by a human, the rows it produces are labelled `curated_preset` in
   `mod_templates`, which keeps them apart from rows a real run produced.

## Options Considered

1. **Record the run as "no changes".** What happens today, and it is indistinguishable from a mod that
   changes nothing because it is broken.
2. **Author a preset so the run changes something.** It produces real rows and a fabricated
   configuration, which the archive would then serve as if a player had chosen it.
3. **Store what the mod ships, record `loaded_but_disabled`, and never author a preset.** Chosen.
4. **Do not archive value mods.** It drops a large share of the catalog: SVM is one of the most used mods
   on the Forge.

## Trade-off Analysis

Option 3 keeps the archive truthful and keeps the mod visible. The price is that the `globals` diff for
SVM is empty, so the pipeline that reads `globals` has no real example until somebody supplies a preset.
That gap is visible in an action item rather than hidden in a run log, and a curated preset later is
one labelled row set, not a change of design.

## Consequences

- A value mod is visible in the archive as a mod that changes nothing *in this run*, with the reason.
  That is true and it is checkable against the log.
- Proving that the pipeline can see `globals.config` needs one curated preset for one mod. That is a
  separate, explicit action, not a hidden default.
- The player's real configuration never reaches the archive. A second project that reads client
  configuration must read it from the player, not from here.
- `loaded_but_disabled` joins `no_server_data` (ADR-0023) as an outcome that is neither success nor
  failure.

## Verification

```gherkin
@adr-0026 @unverified
Feature: Record a mod that loads and disables itself

  Scenario: A value mod without a preset changes nothing
    Given a value mod that needs a preset
    And the mod artifact has no preset
    When the Archiver runs the mod
    Then the observation outcome is "loaded_but_disabled"
    And the observation holds the reason from the mod log
    And the mod dataset holds no "globals" row

  Scenario: A curated preset is labelled
    Given a value mod with a curated preset
    When the Archiver runs the mod
    Then the mod dataset holds the "globals" rows the preset changes
    And those rows carry the kind "curated_preset"
```

## Action Items

- [ ] Add the `preset` key to the dataset document.
- [ ] Add the outcome values `loaded_but_disabled` and `no_server_data` to the glossary and the check.
- [ ] Decide later whether to author one reference preset to prove the `globals` diff.

## Links

- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): one mod version per container.
- [ADR-0017](0017-record-a-template-operation.md): the operation a dataset records.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter.
- [ADR-0024](0024-archive-the-whole-server-database.md): the `globals` table.
