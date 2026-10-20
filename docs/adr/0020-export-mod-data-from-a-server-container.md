# ADR-0020: Export mod data from a server container, without a Harmony patch

**Status:** Proposed
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0013, ADR-0014, ADR-0015, ADR-0017

## Context

ADR-0013 says a throwaway container runs one mod version against a pinned dependency set, and an
exporter mod writes the in-memory database to JSON. ADR-0015 adds a HarmonyX patch that suppresses
the bundle loading step, because the archived run does not need client assets.

Nobody had built either of them (TD-12). The first real run, on six mods from sp-mod.com, showed
that three of the assumptions in ADR-0013 and ADR-0015 do not hold on SPT 4.1.6.

1. **The exporter is not a patch.** `TemplateTable`, `TradersTable` and `LocaleTable` are ordinary
   injectable services. A mod that implements `IOnLoad` at `OnLoadOrder.PostLoad` reads all three
   after every other mod has finished. No Harmony, no `PostSptModLoader + 1` arithmetic, no
   `GetPatchedMethods` assertion.
2. **The bundle step does not block a run.** The server started, imported the database and exported,
   with no bundle directory present. WTT-Armory requested 828 bundles that were not on disk; the
   server logged one line per bundle and continued. The patch in ADR-0015 solves a problem that this
   path does not have.
3. **The server version is a build argument.** `Build.props` takes `SptVersion` and the build injects
   it into `ProgramStatics`. A container therefore states its release at build time, which is what
   ADR-0014 needs. TD-26 said the Archiver could not learn the server version; for the container the
   value is chosen, not discovered.

The run also found a limit. The server's own HTTP API exports items and locales without a session,
but `/client/trading/api/traderSettings` and `/client/quest/list` return an empty body, because both
are built per profile. Traders and quests are only reachable from inside the process.

## Decision

We will export mod data with a mod, not with an HTTP client and not with a Harmony patch.

- The mod set in a container is: the pinned dependency set, one target mod version, and the exporter.
- The exporter implements `IOnLoad` at `OnLoadOrder.PostLoad`, injects the table services, writes
  `templates.json`, `traders.json` and `locales.json` to a mounted directory, and stops the process.
- The exporter runs last and does not modify a table.
- The run does not include bundles. A missing bundle is logged by the server and does not stop the
  export.
- The container image states the release it holds as a build argument. A mod whose
  `SptVersion` range excludes that release is not archived in that image; the run uses an image
  built from the release the mod names. Ledger requires `SPTarkov.Server.Core` 4.1.6, so a 4.1.5
  image cannot hold it.

ADR-0015 stays Accepted and unused. We do not delete the patch requirement; we record that the
container path built here does not need it.

## Options Considered

| Dimension | A. HTTP API only | B. HarmonyX patch (ADR-0015) | C. IOnLoad exporter |
| --- | --- | --- | --- |
| Items and locales | Works, no session needed | Works | Works |
| Traders and quests | **Empty body**, built per profile | Works | Works |
| Needs a patched server | No | Yes, pinned to a commit | No |
| Survives a server update | Likely | Only with the patch kept current | Yes, if the tables keep their names |
| Export order | After start | Before start | After every mod load |
| Verified on 4.1.6 | Items and locales only | Not built | **Yes, six mods** |

## Trade-off Analysis

Option C trades a Harmony dependency for a dependency on three class names in the server. If the
server renames `TemplateTable`, the exporter stops compiling, and we find out at build time rather
than at run time. That is the cheaper failure.

The exporter sees the tables that mods wrote, which is the data the archive wants. Option A sees the
same item data but cannot see a trader or a quest without inventing a player profile, so it cannot
serve ADR-0013.

## Consequences

- One export per run, from one process, in one order. The dataset is reproducible from the image, the
  mod set and the dependency set.
- The exporter ships in every container image and is not a mod in the catalog.
- Bot tables are **not** exported. A mod whose whole effect is bot behaviour, such as female PMC
  voice, produces an export identical to the baseline. TD-27 tracks this.
- A mod that needs its bundles to build items produces no items when the bundles are absent. TD-28
  tracks this. MassivesoftWeapons is the example.
- Export files are large: 27 MB of templates and 57 MB of locales for one dependency set. The
  Archiver compares hashes and does not store the JSON.

## Verification

Verified by run on 2026-10-20, not by a test.

| Run | What we saw |
| --- | --- |
| Image build, `SptVersion=4.1.6` | `Successfully tagged localhost/spt-server:4.1.6` |
| Server with no mods | `Database import finished`, `Server has started` |
| Six mods plus two dependencies | 8 mods loaded, Ledger included |
| Same set on a 4.1.5 image | Ledger rejected: `Could not load file or assembly 'SPTarkov.Server.Core, Version=4.1.6.0'` |
| Exporter | `templates.json` 26,953,616 bytes, `traders.json` 3,199,001, `locales.json` 56,873,849 |
| Per-mod runs, dependency-only baseline | WTT-Armory +833 items, 461 modified, +14 quests; WeebSights +21 items; Gunwright +1 trader, 579 offers; Ledger +45 quests, +1 trader, 36 offers; FemalePMCVoice 0; MassivesoftWeapons 0 |

```gherkin
@unverified @adr-0020
Feature: Export the database of a mod run

  Scenario: The exporter writes the tables after every mod has loaded
    Given a container with SPT 4.1.6 and the exporter mod
    When every mod has finished loading
    Then the exporter writes templates.json, traders.json and locales.json
    And the container stops after it writes them

  Scenario: A mod that needs a newer core assembly is not archived in an older image
    Given an image built for SPT 4.1.5
    And the mod Ledger, which references SPTarkov.Server.Core 4.1.6
    When the server loads the mod set
    Then the server does not load Ledger
    And the log names the assembly it could not load

  Scenario: A missing bundle does not stop the export
    Given a run with no bundle directory
    And a mod that requests bundles
    When the server loads the mod set
    Then the server logs one line for each missing bundle
    And the exporter still writes all three files
```

These scenarios stay `@unverified` until a test runs the container.

## Action Items

- [ ] Yui ratifies this ADR, or rejects it and returns to ADR-0015.
- [ ] Add the exporter to the image build, so a run needs no manual copy.
- [ ] Write the Archiver importer that reads an export directory: `mod_datasets`,
      `mod_data_observations`, `mod_templates`, `mod_item_changes` (TD-29).
- [ ] Extend the exporter to bot tables (TD-27).
- [ ] Decide what a run records when a mod loads and produces no data (TD-28).

## Links

- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): the run model this implements.
- [ADR-0014](0014-keep-every-archived-spt-release.md): the release key the image is built with.
- [ADR-0015](0015-suppress-bundles-with-a-patch-mod.md): the patch this path does not need.
- [ADR-0017](0017-record-a-template-operation.md): the change rows the diff produces.
- `docs/runbooks/mod-export.md`: the commands that produced the evidence.
