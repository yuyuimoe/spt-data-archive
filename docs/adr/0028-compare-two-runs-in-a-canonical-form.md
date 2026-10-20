# ADR-0028: Compare two runs in a canonical form

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0013, ADR-0017, ADR-0023, ADR-0026, ADR-0027

## Context

Two runs of the same release with no mod at all are not equal. The project ran SPT 4.1.6 in a
container twice (ADR-0027 layout, 642 MB each, about 40 minutes apart) and compared the two
exports. Thirteen files of the export differ. They fall into three classes:

| Class | Path | What happens |
| --- | --- | --- |
| Clock | `traders/<id>/base.json` → `.nextResupply` | The server writes the time of the next restock: `1792473833` and then `1792475577`. |
| Generated id | `traders/<id>/assort.json` → `.items[*]._id` | The server mints a new `MongoId` for every assort item at every boot. 5090 items, 40 differ in the first 40. The same ids are the keys of `barter_scheme` and `loyal_level_items`, so the shape is stable and only the names change. |
| Rotation | `locations/lighthouse/base.json` and `locations/woods/base.json` → `.BossLocationSpawn[1].BossChance` | The boss event rotates between maps: `35` and `0` swap. |

No mod ran. Every one of those differences is the server, not content.

A mod dataset is the set of rows that differ from the baseline (ADR-0013, ADR-0017). A dataset
also has a content hash, computed from its tables. If the comparison reads raw bytes or raw
values, then:

- a run of the same release produces a different hash than the run before it,
- a mod dataset holds hundreds of rows that show the noise and not the change,
- the archive cannot tell a mod's change from the next boot.

## Decision

One canonical form is the only input to a comparison, a diff and a hash.

1. Object keys are sorted. Array order is kept: the server's order is data.
2. A short list of paths is replaced by a fixed token before a comparison. The list is data,
   not code, and starts with the three classes that the two runs found:
   - `traders/*/base.json` → `$.nextResupply`
   - `traders/*/base.json` → `$.suits[*]._id`, `$.suits[*].tid` if a run shows them volatile
   - `traders/*/assort.json` → the generated ids (below)
   - `locations/*/base.json` → `$.BossLocationSpawn[*].BossChance`
### The measured list (2026-10-20)

The two baseline exports of SPT 4.1.6 have 13 files that differ on disk. Their values differ at
exactly four places, counted over the whole export:

| Path | Differences | Cause |
| --- | ---: | --- |
| `traders/*/assort.json` → `$.items[*]._id` | 5,090 | a new id per boot |
| `traders/*/assort.json` → `$.items[*].parentId` | 1,813 | a reference to one of those ids |
| `traders/*/base.json` → `$.nextResupply` | 10 | the restock clock |
| `locations/*/base.json` → `$.BossLocationSpawn[*].BossChance` | 2 | the boss event rotates |

The keys of `barter_scheme` and `loyal_level_items` are those same generated ids, so decision 3
covers them and they need no entry.

`$.suits[*]._id` and `$.suits[*].tid` **did not move** in either run, so the list does not carry
them: an entry that hides a real change is worse than noise. If a later release moves them, the
first run reports the path and the entry is added.

The list lives in `src/SptArchive.Core/Canonical/volatile-paths.json`, with the evidence of each
entry beside it. `CanonicalFormTests` holds the 13 files as its fixture.

3. The generated ids of an assort are renumbered, not deleted. Each id becomes `item[<n>]` by
   the position of the item in `items`, and every reference to the old id in the same document
   (`barter_scheme` keys, `loyal_level_items` keys, `parentId`) becomes the same token. The
   structure stays, the names stop moving.

   **Replaced on 2026-10-20 by ADR-0034 decision 8.** A position is not an identity: an offer that
   enters at the first place moves every name after it. An offer is named by its content, and the
   file is ordered by that address. See `SptArchive.Core.Canonical.Assort`.
4. The `jsonb` column keeps the value as the run wrote it (ADR-0002). The canonical form is
   only for comparison and for the hash. The archive never rewrites what the server said.
5. A run reports every path that differs from the previous run of the same release and is not
   on the list. It is a warning in the run log, and it names the path. A new volatile field is
   then visible in one run, and not as a mystery dataset.
6. A mod change is a row that differs after this canonicalization. A mod that changes only the
   behaviour of a run (for example a loot multiplier that acts when a raid is generated)
   produces no row, and the observation records that outcome (ADR-0023, ADR-0026).

## Why a hash of the run directory is not enough

The archive keeps SHA-256 sums, and it keeps three of them. Each one answers a different
question, and none of them replaces a diff.

| Hash | Over | Question it answers |
| --- | --- | --- |
| Source hash | The files of an install | Did this release change on disk, so that a new import is needed (ADR-0004)? |
| Artifact hash | The file that was downloaded | Is this the artifact that the page offered (TD-31)? |
| `content_hash` | The canonical tables and the configuration of a dataset | Are these two datasets the same content (ADR-0013, ADR-0029)? |

A sum of the run directory as it lies on disk is not usable as the third one. The two baseline
runs prove it: 13 files and 42 paths differ, all of them the server. Every boot would produce a
new hash, so `mod_datasets` unique on `(mod_guid, content_hash)` would never fold two runs into
one dataset, and every run would add a dataset per mod.

A sum is also not a substitute for a difference. It says **that** two runs differ and not
**what** differs. The archive stores only the difference (ADR-0013, ADR-0017): the mod tables
hold rows that differ from the baseline, and `mod_item_changes` holds the changed paths. A
reader who needs "what did this mod change in the weight of item X" needs the rows. Hash-only
would mean keeping the full 642 MB table set of every run with a mod, which is the cost that
ADR-0013 rejected.

So the sum is over the canonical bytes, and the canonical form is the part that is hard. The
cost of hashing is the same as the cost of serializing: the Archiver materializes a table once,
writes the canonical bytes, and hashes the bytes it wrote.

## Options Considered

**A. Compare raw bytes (chosen until now).** Simple, and wrong: eleven to thirteen files
differ on every boot of one release, and the reason is not content.

**B. Drop the volatile fields from the archive.** The export would be stable and the archive
would hold less than the server said. It also breaks ADR-0002. Rejected.

**C. Freeze the clock and the random source in the container.** It hides the clock class and
not the rotation of the boss event, and it needs a patched server. Rejected.

**D. Canonicalize for comparison, keep the raw value (chosen).** The diff and the hash see one
stable shape. The archive keeps the truth.

## Trade-off Analysis

The list of volatile paths is a piece of knowledge that must be maintained with each release.
A wrong list entry hides a real change, which is worse than noise. For that reason the list is
short, each entry is a path with a measured example, and the run warns about paths that differ
that are not on the list.

Renumbering ids is more work than deleting them. Deleting them would break the link between an
assort item and its barter scheme, and the archive would store an assort that no reader can
join. The renumbering keeps the shape and the joins.

The canonical form is a second representation of the data. It must not leak into an answer: an
API must serve the raw value. A test asserts that the hash of the raw value is not used as the
dataset key.

## Consequences

- The Archiver needs one canonicalization step before it diffs or hashes (TD-29).
- `content_hash` of a dataset (ADR-0013) is computed over the canonical form.
- The list of volatile paths has a test with the two-run export as its evidence.
- A run warns about a path that differs and is not known, so a new release shows its new
  volatility in the first run.
- The two baseline exports of 4.1.6 stay as the fixture of that test.

## Verification

```gherkin
@adr-0028 @unverified
Feature: A comparison reads a canonical form
  Two runs of one release differ only where the server is not deterministic.

  Scenario: Two runs of one release have no difference after canonicalization
    Given the two baseline exports of SPT 4.1.6
    When I compare them in the canonical form
    Then no row differs
    And the difference of "traders/579dc571d53a0658a154fbec/assort.json" is empty
    And the difference of "traders/54cb50c76803fa8b248b4571/base.json" is empty

  Scenario: A changed value survives the canonical form
    Given the baseline export of SPT 4.1.6
    And a run with a mod that sets the width of an item to 3
    When I compare them in the canonical form
    Then the mod dataset has one changed row
    And the row names the item and the field "Width"

  Scenario: A volatile path that the list does not know is reported
    Given a run whose export differs from the baseline at "traders/<id>/base.json" at "$.suits[0]._id"
    When the run compares the two
    Then the run log names "$.suits[0]._id"
    And the row is not part of a mod dataset

  Scenario: The archive keeps the value that the server wrote
    Given the run wrote "nextResupply" as 1792473833
    When the archive stores the row
    Then the stored value of "nextResupply" is 1792473833
    And the canonical value is only used for the comparison and the hash
```

## Action Items

- [x] TD-39: write the canonical form, with the volatile path list and its test.
- [ ] Keep the two baseline exports of 4.1.6 as the fixture of that test.
- [ ] TD-29: diff and hash through the canonical form.
- [ ] Re-run two baselines after each release change and add a path when a new field moves.

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): the full source object stays in `jsonb`.
- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): a mod dataset is keyed by its content.
- [ADR-0017](0017-record-a-template-operation.md): a template operation above the change.
- [ADR-0023](0023-classify-a-mod-by-the-parts-it-ships.md): what a mod can contribute.
- [ADR-0026](0026-archive-a-value-mod-preset.md): a value mod and its preset.
- [ADR-0027](0027-archive-a-run-as-a-database-directory.md): the export is a database directory.
