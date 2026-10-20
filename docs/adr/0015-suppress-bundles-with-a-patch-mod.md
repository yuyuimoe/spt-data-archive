# ADR-0015: Suppress bundle loading with a HarmonyX patch mod

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

Each mod container in ADR-0013 starts the server. The server loads Unity asset bundles when
`AbstractModMetadata.IsBundleMod` is true. Bundles cost gigabytes of disk and minutes of start-up
time. The archive reads the database only, so bundles are pure cost.

Three facts from the server source block the simple solutions:

- `IsBundleMod` is declared metadata, not a fact about the files. Omitting a `Bundles` folder does
  not turn it off.
- `ProgramStatics.MODS()` computes the bundle path from `BuildType`. A `BLEEDINGEDGE` build turns
  bundles off, but it also turns off mod loading.
- There is no runtime setting for bundles.

HarmonyX is part of the server and every mod may patch methods with it.

## Decision

We will ship one small **patch mod**. It installs a HarmonyX prefix patch on the bundle step of
`SptServerStartupService` and returns without loading bundles. It installs the patch at the load
stage before `PreSptModLoader`, so the patch is in place before the bundle step runs.

The run fails when the patch is not in `Harmony.GetPatchedMethods()`. A run that loads bundles
must not archive a result.

## Options Considered

| Dimension        | A. Build a server image without the guard | B. Reflection on `IsBundleMod` | B. HarmonyX prefix patch | C. Keep bundles on |
|------------------|-------------------------------------------|--------------------------------|--------------------------|--------------------|
| Server source    | Fork, rebuilt per release                 | Stock                          | Stock                    | Stock              |
| Failure mode     | Build fails in CI                         | Silent: a renamed field does nothing | Run fails loudly    | None               |
| Effort per mod   | None                                      | None                           | None                     | Gigabytes of disk  |
| Effort to start  | A build pipeline and a registry           | One mod                        | One mod                  | None               |
| Drift detection  | At build time                             | None                           | At run time              | Not applicable     |

Option A is correct but needs its own build pipeline and image registry. A server renames one
private field and Option B stops working without a sound. Option C costs gigabytes and minutes per
mod, 699 times.

## Trade-off Analysis

The patch mod keeps the server stock, so a new release needs no new image. The price is a runtime
dependency on a private method. That price is acceptable because the run fails at once when the
target moves, and the run is per mod, so one loud failure is cheap.

HarmonyX also reports a failed patch. That is the difference between this option and option B:
a rename becomes an error, not silence. Silence is the failure mode that this project cannot
afford, because it hides inside 699 successful-looking runs.

## Consequences

- **Easier:** A mod container starts in seconds and needs a few hundred megabytes.
- **Easier:** The archive needs no forked server image and no private registry.
- **Harder:** The patch mod is versioned against a server release. The catalog pins its version.
  An archive run records the hash of the patch mod that it used.
- **Harder:** The patch mod is infrastructure, not a mod under test. It must never appear in
  `mods`, in a dataset, in an observation or in a baseline.
- **Harder:** `ProgramStatics.MODS()` stays true, so the rest of the mod path still runs.
  This is intended: the exporter needs it.
- **Revisit when:** The server adds a supported switch for bundles, or a release moves the bundle
  step out of `SptServerStartupService`.

## Verification

```gherkin
Feature: A mod container never loads asset bundles

  Scenario: The patch is installed before the bundle step
    Given a server container with the patch mod
    When the server starts
    Then the bundle step of "SptServerStartupService" is patched
    And the container does not read a bundle file

  Scenario: A run fails when the patch does not apply
    Given a server release that moved the bundle step
    When the Archiver starts a mod container
    Then the run fails with a message that names the patch
    And the run writes no dataset

  Scenario: The patch mod is not archived as a mod
    Given the patch mod is in the container with the target mod
    When the Archiver compares the exports
    Then the table "mods" has no row for the patch mod
    And no dataset holds a row of the patch mod
    And no observation names the patch mod

  Scenario: The patch mod is present in every mod container
    Given the catalog holds 699 mod versions
    When the Archiver archives the next mod version
    Then the container holds the patch mod at its pinned version
    And the run records the hash of the patch mod
```

## Action Items

1. [ ] Write the patch mod and its own build output, pinned to a server commit.
2. [ ] Verify the load order with one real mod before the catalog run.
3. [ ] Add the patch assertion to the run, next to the GUID assertion of ADR-0013.
4. [ ] Add the patch mod hash to `archive_runs`.
5. [ ] Add the patch mod to the infrastructure denylist of the baseline diff.

## Links

- Related: [ADR-0013](0013-mod-ingestion-by-isolated-run.md)
- Spec: [docs/specs/mod-registry.feature](../specs/mod-registry.feature)
- C4: `archive.modRunner.bundlePatch`
- Tech debt: TD-20
