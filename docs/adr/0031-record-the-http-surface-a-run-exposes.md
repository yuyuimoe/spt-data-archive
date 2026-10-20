# ADR-0031: Record the HTTP surface a run exposes

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui

## Context

A run exports the tables and the files of a mod (ADR-0027, ADR-0029). Neither shows what the
mod's *code* does. Two mods in the archive show the gap:

- `MassivesoftWeapons` and `FemalePMCVoice` load, change no table row and ship no configuration
  file, so a run of either is indistinguishable from a run with no mod at all (TD-28).
- `Gunwright` answers `/client/game/profile/items/moving` and
  `/client/trading/api/getTraderAssort/`, and `WTT-Armory` answers `/client/friend/list`. These
  URLs already belong to the server. A table diff cannot say that a mod stands on them.

The server gives no way to ask. `HttpRouter` holds `IEnumerable<StaticRouter>` and
`IEnumerable<DynamicRouter>`, but the route list of a router is the protected field
`Router.handledRoutes`, filled on first use, and nothing exposes it. There is no introspection
endpoint.

A mod can enumerate the table because the exporter is itself a mod inside the process, and the
dependency injection container hands it both router collections. `Router.CanHandle` is public and
fills the protected field, so one call makes the field readable by reflection.

Measured with exporter 1.4.0 on the archived runs of 2026-10-20 (178 routes without a mod):

| Run                | Routes | From a mod |    Adds | Intercepts |
|--------------------|-------:|-----------:|--------:|-----------:|
| baseline           |    178 |          0 |       0 |          0 |
| `WTT-ServerCommonLib` (dependency of every run) | 178 | 12 | 12 | 0 |
| `ECOTI`            |    180 |          2 |       2 |          0 |
| `WTT-Armory`       |    179 |          1 |       0 |          1 |
| `Gunwright`        |    180 |          2 |       0 |          2 |
| `MapVariants`      |    185 |          7 |       4 |          3 |
| `FactoryClassic`   |    187 |          9 |       5 |          4 |
| `LotsOfLootRedux`, `AllTheClothes`, `Ledger`, `SVM`, `MassivesoftWeapons`, `FemalePMCVoice` | 178 | 0 | 0 | 0 |

`HttpRouter` runs every router that matches a URL and keeps the last answer, so the recorded
enumeration order decides who wins. `WTT-Armory` is enumerated after the core router (7 then 23)
and replaces the answer. `Gunwright` and `MapVariants` are enumerated before it (0 then 15), so
they observe or prepare a request and the core answer stands.

The second question is whether the archive should *call* these endpoints. A server was started
with `MapVariants` and without the exporter, and its routes were called:

| Endpoint                     | Answer                                                       |
|------------------------------|--------------------------------------------------------------|
| `/client/items`              | 4673 items, fields `_id, _name, _parent, _type, _props`       |
| `templates/items.json` of the same run | 4673 items, the same fields, the same key set     |
| `/mapvariants/managed`       | `{"locationIds": []}`                                         |
| `/wttcommonlib/riglayouts/get` | `{}`                                                        |
| `/wttcommonlib/zones/get`    | `[]`                                                          |

The game's own data endpoint returns the table that the run already exports, with no extra field.
The mod routes answer with save and session state, and answer empty when no profile exists.

## Decision

We will record the HTTP surface of every run, and will not call it.

1. The exporter writes `run/routes.json`: one row per route with `url`, `method` (`static` or
   `dynamic`), `order`, `router`, `assembly` and `mod`. `mod` is the GUID of the loaded mod that
   owns the assembly, and is absent for the server and its libraries.
2. A route whose URL the server also answers records that the mod **intercepts** it. A route with
   no such URL records that the mod **adds** it. No baseline run is needed: the owning assembly
   is the answer.
3. The archive never calls a route of a mod. A route is request and response, may need a session,
   may change state, and its answer is not the model of the archive (ADR-0002). A behaviour mod
   therefore stays a dataset with no rows, as ADR-0029 already says.
4. `run/routes.json` is evidence and never enters `content_hash` (ADR-0028). The enumeration order
   of an assembly is not content: a route change must not make a new dataset.
5. A missing route field is a warning that names the router, not a failure. The rest of the export
   is still worth having.

## Options Considered

### Option A: parse the module IL of every mod
Read `RouteAction` constructions from the assemblies on disk, as `strings` does today.
Catches a route that a mod would register on a release where it does not load. Misses routes that
are assembled at runtime, and cannot say which of two routers wins.

### Option B: dump the route table from inside the process
Chosen. Sees only what actually registered, with the real order and the owning assembly.

### Option C: call every mod route in the run and store the answers
Gives the most data. Needs a profile for most routes, breaks `content_hash` determinism on routes
that return time or random state, and can change the state it is measuring.

### Option D: do nothing, stay with the table diff
Leaves the mods that only touch routing invisible, and cannot separate "no effect" from "effect
that is not a row".

## Trade-off Analysis

Option B reads a protected field by reflection, which is a coupling to the internals of the server
and breaks silently when the field is renamed. The cost is bounded: the failure is a warning, the
rest of the export is unaffected, and the field has one name in one class. Option A has the
opposite problem: it is not coupled to a private member but it can only guess.

Option C is rejected on the ground that already applies to presets (ADR-0026): an answer is not a
model. It would also make the same mod produce a different `content_hash` on every run, which is
the property the whole dataset scheme rests on.

Option B does not answer "what does the mod do". It answers "what does the mod answer", which is
the question a user asks when a mod breaks a client call after a server release: the route still
registers, and the interception still happens, or it does not.

## Consequences

A run states its routes, so a mod that changes only a route is no longer invisible. The two
bundle-loaded mods of the archive stay invisible for a different reason: they register no route,
which is now a measured fact rather than an absence.

`run/routes.json` is smaller than a table and diffable, so the route inventory of a release can be
compared (ADR-0013 datasets stay as they are).

The exporter depends on the name of a protected field. When a server release renames it, the
archive loses the route list until the exporter is updated, and says so in the run log.

## Verification

```gherkin
@adr-0031 @unverified
Feature: Record the HTTP surface of a run

  Scenario: A run states every route it answers
    Given the exporter ran in a container
    When I read run/routes.json
    Then there is one row per route with url, method, order, router and assembly

  Scenario: A route of a mod names the mod
    Given an export of the Gunwright run
    When I read run/routes.json
    Then /client/game/profile/items/moving has the mod com.deadwolf.spt.gunwright
    And /client/items has no mod

  Scenario: A route on a URL of the server is an interception
    Given an export of the WTT-Armory run
    When I read run/routes.json
    Then /client/friend/list has two rows
    And the row of com.wtt.armory has the higher order

  Scenario: A mod that adds a route adds no table row
    Given an export of the ECOTI run
    When I read run/routes.json
    Then /coti/hosts has the mod com.lennoxp90.coti.server

  Scenario: A mod that changes nothing shows no route
    Given an export of the MassivesoftWeapons run
    When I read run/routes.json
    Then no row names a mod other than a dependency

  Scenario: The archive does not call a route
    Given a run of a mod that answers /mapvariants/managed
    When the Archiver imports the run
    Then no request is made to the container
    And the dataset holds no row from an answer

  Scenario: A renamed route field is a warning
    Given a server release whose Router no longer has the field handledRoutes
    When the exporter dumps the route table
    Then the run log names the router
    And the run still produces its tables and its manifests

  Scenario: The route inventory does not change the content hash
    Given two runs of one mod that differ only in the order of two routers
    When the Archiver hashes both
    Then the content hash is equal
```

## Action Items

- [x] Exporter 1.4.0 writes `run/routes.json` and prints the URLs that two routers answer.
- [x] Measured the route inventory of the archived runs (table above).
- [x] Measured that `/client/items` returns the exported table and no more.
- [ ] Teach the Archiver to read `run/routes.json` with the rest of the run (TD-29).
- [ ] Store the route inventory per dataset, or state in `docs/specs` why not.
- [x] Note the reflection on `handledRoutes` in `docs/tech-debt.md` (TD-43).
- [ ] Re-run the two bundle-loaded mods on a release that loads their bundles (TD-28).

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): the jsonb row is the source object, not a view.
- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): one mod version per container.
- [ADR-0016](0016-probe-mod-compatibility.md): a probe answers "does it still work".
- [ADR-0027](0027-archive-a-run-as-a-database-directory.md): the export is a database directory.
- [ADR-0028](0028-compare-two-runs-in-a-canonical-form.md): what enters the content hash.
- [ADR-0029](0029-archive-mod-files-and-config.md): what a run states about itself.
- [ADR-0030](0030-run-export-and-import-with-one-tool.md): the runner that makes the runs.
- [docs/runbooks/mod-export.md](../runbooks/mod-export.md): how a run is made and read.
