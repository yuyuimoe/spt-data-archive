# ADR-0021: Archive a patching mod together with the mod it patches

**Status:** Proposed
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0013, ADR-0016, ADR-0020, ADR-0022

## Context

ADR-0020 recorded "MassivesoftWeapons added 0 items" and guessed that the mod builds its items from
bundles. That guess was wrong. The mod page 3127 is *MassivesoftWeapons Update Patch for SPT 4.1.x*:
it is a patch. The items come from the base mod, page 2588, which is built for SPT 4.0.13. A patch
exists because the base cannot run on a newer server; the base is not optional.

The first run of the patch alone (26 KB archive, one DLL, no data files) produced an export identical
to the dependency-only baseline. No error, no warning, and the archive recorded a dataset that changed
nothing. Read as "this mod does nothing", that statement is false.

Three more facts came out of the same session, and each one changes how a run must be curated.

1. **The Forge does not declare the base mod.** The required-dependency block of page 3127 names only
   MassivesoftCore 2.0.1. Page 2588 names MassivesoftCore 1.0.2. Neither page names the other page.
   A dependency set built from the page metadata alone is incomplete.
2. **The Forge can serve an artifact that is not the version the page names.** The download endpoint
   for 3127 "2.0.1" returned `MassivesoftWeapons-v1.1.zip` (26,362 bytes), whose DLL states version
   1.1.2 with GUID `com.massivesoft.massivesoftweapons`; the page states GUID
   `com.massivesoft.massivesoftweapons.415`. The same happened for the core: 3126 "2.0.1" returned
   `MassivesoftCore-v1.1.zip`. Both artifacts exist in the GitHub release *4.1.5* next to the correct
   ones (`MassivesoftWeapons-4-1-5.zip`, 8,898 bytes).
3. **A mod built for an older release does not necessarily load on a newer one.** The base mod 2588
   on the 4.1.6 image fails with `Could not load type 'SPTarkov.Server.Core.Models.Spt.Mod.AbstractModMetadata'`.
   The version gate in ModValidator rejects a mod that *references a newer* core assembly; it does not
   reject an old mod whose types no longer exist. The failure appears at load time, per mod, and the
   run continues without that mod.

So a run of the target alone is not evidence that the mod changes nothing. It is evidence that the
run was incomplete, and the archive has no way to say so today.

## Decision

We will treat a patch as a mod whose dependency set contains the mod it patches.

- The dependency set of a run is **curated by a human** and may contain another mod of the same
  catalog (the base). It is not limited to libraries the server needs to boot, and it is never derived
  from the Forge metadata alone. Page 3127 does not name page 2588, and a correct run still needs it.
- The baseline of that run is the **dependency-only export** with the base installed. Rows that the
  base adds are therefore baseline, not dataset, and the patch dataset holds only what the patch
  changes. This keeps ADR-0013 intact: a dataset is one mod's contribution.
- The dependency set records, per entry, the mod GUID, the version **read from the DLL**, the version
  the page claimed, and the SHA-256 of the downloaded archive. The target comparison uses the GUID,
  the DLL version and the artifact hash, never the page version.
- A mod that does not load is an observation with outcome `not_loaded` and the loader's reason. It is
  never an empty dataset.
- A target that loads and produces an export equal to its baseline is archived as a dataset with zero
  template rows, and the run logs a warning that nothing changed. Zero rows is a fact about that run,
  not a property of the mod. The curator must confirm the base is in the dependency set before the
  result is published.

## Options Considered

| Dimension | A. Run the target alone (today) | B. Base in the dependency set (chosen) | C. Merge base and patch into one "virtual mod" |
| --- | --- | --- | --- |
| Patch dataset | Empty, unexplained | The patch's own changes | The whole base content, attributed to the patch |
| Base attribution | Lost | Base rows stay in the baseline | Lost |
| Two patches of one base | Two empty datasets | Two comparable datasets | Two copies of the base |
| Matches ADR-0013 | Yes, but wrong result | Yes | **No**: a dataset would hold another mod's rows |
| Needs curation | No | Yes, one line per patch | Yes, and a merge rule |

## Trade-off Analysis

Option A needs no work and produces a false statement, which is the worst of the three. The archive
exists to say what a mod changes; "nothing" is a claim, and it must be true.

Option B adds one curation step, which the project already accepts: the mod source is human
curation outside the Archiver (locked decision, ADR-0013). It also makes the patch dataset comparable
across server releases, because the base rows are subtracted on both sides.

Option C would double the base content on every patch and break the rule that a dataset holds one
mod's contribution. It also grows without limit when a base has several patches.

Recording the artifact hash is not optional in either option. The page and the artifact disagreed in
two of the three Massivesoft downloads we tried, and a dataset keyed by a page version would file the
base under the patch's version number.

## Consequences

- A "zero change" result becomes an explicit warning instead of a silent dataset. TD-28 is rewritten.
- The dependency set carries more than a version: the artifact hash and the claimed version. The
  observations table stores the set as jsonb, so no migration is needed for that part (TD-31 covers
  the missing column on the target itself).
- Curation must record, for each patch, the base page and the base version. The catalog, not the
  Forge, is the source of truth for a run.
- A base mod that no longer loads on a newer release is a normal `not_loaded` observation, not a
  failed run and not an empty dataset.
- This ADR does not solve a patch that *replaces* a file inside the base instead of adding its own
  mod directory. That case is not observed yet; if it appears, it needs its own decision.

## Verification

Verified by run on 2026-10-20 on the 4.1.6 image, not by a test.

| Run | Result |
| --- | --- |
| Page 3127, "Download 2.0.1" | 26,362-byte zip; DLL version 1.1.2, GUID `com.massivesoft.massivesoftweapons` |
| Page 3126, "Download 2.0.1" | 44,499-byte zip; DLL version 1.0.2, GUID `com.massivesoft.massivesoftcore` |
| 3127 artifact alone, dependency-only baseline | 4 mods loaded; export equal to the baseline (0 items, 0 clothes, 0 quests) |
| GitHub 4.1.5 patch (`MassivesoftWeapons-4-1-5.zip`) with the Forge core | `not loaded: Could not load file or assembly 'MassivesoftCore, Version=1.0.0.0'` |
| Base 2588 (1,347,177,091 bytes) alone on 4.1.6 | `not loaded: Could not load type 'SPTarkov.Server.Core.Models.Spt.Mod.AbstractModMetadata'` |
| Base 2588 file list | `subs/<name>/db/*.json` plus `subs/<name>/bundles`: the items are data files of the base |

```gherkin
@unverified @adr-0021
Feature: Archive a patching mod with the mod it patches

  Scenario: A patch is archived with its base in the dependency set
    Given the base mod "com.massivesoft.massivesoftweapons" version "1.1.2"
    And the patch version of the same mod
    When the run loads the base and the patch together
    Then the baseline export contains the rows of the base
    And the dataset of the patch contains only the rows the patch adds or changes

  Scenario: A patch alone is a warning, not a zero result
    Given a run whose mod set has no base for the target mod
    When the export of the target equals the baseline export
    Then the run records a dataset with no template rows
    And the run logs that the target changed nothing

  Scenario: A mod built for an older release is recorded, not guessed
    Given a mod that references a type that the current release no longer has
    When the server loads the mod set
    Then the observation outcome is "not_loaded"
    And the observation states the loader's reason
```

These scenarios stay `@unverified` until TD-29 exists and the run model can record a dependency set.

## Action Items

- [ ] Yui ratifies this ADR, or rejects it and accepts that a patch run is not comparable.
- [ ] TD-31: store the artifact hash of the target mod version on the observation.
- [ ] TD-28: log the "no change" warning in the run model.
- [ ] Curate the Massivesoft pair in the catalog when the matched 4.1.x pair is available.

## Links

- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): the run, the dependency set and the baseline.
- [ADR-0016](0016-probe-mod-compatibility.md): the probe that compares export hashes.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter these runs use.
- [ADR-0022](0022-archive-player-customization.md): the seventh dataset the same session added.
- `docs/tech-debt.md`: TD-28 and TD-30.
