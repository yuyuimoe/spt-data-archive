# ADR-0032: Record every changed value as a row

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Depends on:** ADR-0034 (the address of an object; Proposed)

## Context

The archive stores what a mod did as rows. How much a row says depends on the kind of data.

| Kind | What the archive stores | Can a query name the change? |
|------|-------------------------|------------------------------|
| Locale | one row per scope, language and key, with the value and the old value | Yes. ECOTI: 459 rows for 27 keys. |
| Item | a template row with the operation, and one path row for each changed value | Yes. WTT-Armory: 1,337 templates, 733 paths. |
| Quest | a template row with the operation, and the whole quest object | No. WTT-Armory: 37 quest templates, 0 paths. |
| Trader | a template row with the operation, and the whole `base.json` | No. ECOTI: 2 trader templates, 0 paths. |
| Customization | a template row, and the whole customization object | No. AllTheClothes: 104 added, 234 modified, 0 paths. |
| Assort | nothing. The file becomes an unstored canonical hash (ADR-0029 amendment 4) and a warning line | No. The 9 extra Peacekeeper offers of ECOTI exist only as that hash. |
| Location, bot, hideout, price, handbook | nothing. Hash only | No. |

The driver requires a query for every kind: which locales (available today), which assorts, which trader changes and which quest changes. The driver also requires every value that moves. If an item goes from 9 to 10, the archive must say so.

Today it says so for items only.

A test also showed a second limit (TD-46). When an array changes length, the diff collapses the whole array into one path. WTT-Armory grows a slot filter from 30 to 39 entries. The archive says "this array changed" and cannot say which entry arrived.

A third limit has no row at all. A trader assorts stays a canonical hash. ECOTI adds nine offers to Peacekeeper and changes two traders, and the archive stores 733 change paths: none of them is an offer. The driver named the identity of an offer: the trader, the item and what the player pays with. A player buys the same item in more than one way. One example is a P90 for 90,000 roubles, or for two AA batteries, both at Mechanic. The measurement in the Decision section confirms that rule.

The driver named a fourth limit: the id of every object can be made at run time (ADR-0034). An offer is therefore named by an address that the archive derives. It is not named by an id that a run produces.

## Decision

**We will store one row for every changed value, for every kind of data.**

1. **One change table.** `mod_item_changes` becomes `mod_change_paths`. Its `kind` column accepts `item`, `quest`, `trader`, `customization` and `assort`. The columns stay the same: the RFC 6901 path, the operation (`set` or `remove`), the old value and the new value. The foreign key stays on `(dataset_id, kind, template_id)` to `mod_templates`.

2. **One path per leaf.** A difference inside an array yields one path for each index, such as `/conditions/0/_props/value`. An array that grows yields a path only for each added index. An array that shrinks yields a `remove` path only for each missing index. The whole-array path disappears.

3. **Quests, traders and customizations get paths.** Their template row stays. Their full object stays in its table (ADR-0002). Each changed value becomes a path row under the template.

4. **Assort becomes a row type. An offer is named by a content ULID.** The template kind is `assort`. The template id is a content ULID over three parts (ADR-0034): the address of the trader, the address of the root item, and the payment channel.
   - The payment channel is the set of templates that the player pays with, named by their addresses, sorted, unique and joined by `+`. It is the rouble template, the euro template, or a barter item. The amount is not part of the seed. An offer that becomes more expensive keeps its id, so the change is a `set` path on the amount.
   - Evidence, measured on the 4.1.6 baseline: 6,131 offers, 317 channels, 4,901 offers in roubles, 618 in euros and 156 in dollars. 144 pairs of (trader, root item) hold more than one offer. 167 offers are paid with two to five items at once.
   - The three parts name 6,100 offers. The remaining 23 groups hold two to five offers each, and they differ by preset: attached parts, durability, buy limit or loyalty level.
   - When a run holds more than one offer of one seed, the seed gains the canonical hash of the offer preset. The preset is the offer tree without instance ids, without amounts and without `upd`. A change to a preset field of such an offer therefore reads as a removal and an addition. That is 23 groups of 6,131 offers in 4.1.6.
   - Three of those 23 groups hold offers that are identical in every part. Their ids differ only by their order in canonical form. The archive states them as separate rows and cannot say which one changed. The runbook names that case.
   - The payment is addressed by its template and not by its position: `/barter/<template address>/count`. A position would name different payers in the next run.
   - The offer tree is stored in `mod_trader_assort_items`: one row for the root and one row for each attached part. The tree stays self-consistent because a dataset is written once.
   - A part is keyed by its slot and not by its position: `/parts/<slotId>/upd/StackObjectsCount`.
   - The same id matches a run against its baseline. Today the diff renumbers the assort instance ids by position (ADR-0028, decision 3), so an offer added at position 0 shifts every position after it. With the address the diff pairs real offers (ADR-0034, decision 8).
   - A quest unlock stays attached to the offer by the same id: `mod_trader_quest_assorts` holds the dataset, the offer id, the status and the quest address. The 4.1.6 baseline has 236 such links, 5 with the status `started` and 231 with `success`. The `questassort.json` file names offers by instance id, which the server renumbers on every boot. The address removes that dependency.

5. **The old value comes from the baseline.** The new value comes from the run. A removed value has no new value. An added value has no old value. A volatile path (ADR-0028) never becomes a row, because it never differs.

6. **Locations, bots, hideout, prices and the handbook stay hash only.** Their row model (spawn points, loot pools, bot types) is a bigger design. A later ADR gives it rows. Until then the archive keeps the canonical hash and the warning.

7. **The content hash keeps covering every row** (ADR-0029 amendment 4). A value that moves from "unstored file" to "path row" does not change the statement that two datasets differ. It changes only how much the archive can say about the difference.

## Options Considered

| Dimension | A: a row for every kind (chosen) | B: a path row for every file | C: read the run directory at query time |
|-----------|----------------------------------|------------------------------|------------------------------------------|
| Complexity | medium: one table, one new row type | low | low |
| Cost | one migration, one importer, one slice | one migration | none |
| Answer quality | names the value | names a position | names the value |
| Durability | rows survive the run directory | rows survive it | needs the run directory |

Option B loses because a position is not an identity. The server renumbers the assort instance ids on every boot (ADR-0028), so `/items/3124/barter_scheme` names a different offer in the next run. A query built on such a path answers a different question after each run.

Option C loses because the run directory is transport and not a store (ADR-0027). The archive must outlive it.

## Trade-off Analysis

The main trade-off is row volume against answer quality.

An added item costs one template row. A changed value costs one path row. A quest with 40 changed fields costs 40 rows. The archive gains the ability to answer "which offer became more expensive" and "which quest condition changed", and it pays for that with rows.

For this project the trade is right. The archive exists to answer questions about mod data. A dataset that only holds a hash answers no question. Row volume is bounded by what a mod changes, and a mod that changes everything is rare.

The second trade-off is the identity of an offer. An address of trader, root item and payment channel survives a price change. It also survives a boot that makes new instance ids, which no id could do (ADR-0034). It fails in one case: a trader sells the same item through the same channel in several offers that differ only by preset. Decision 4 separates those offers by the hash of the preset. A change to such a preset then reads as a removal and an addition. That is 23 groups of 6,131 offers in 4.1.6. Three of those groups hold offers that are identical in every part, so the archive cannot say which one changed. We accept that case and state it in the runbook.

## Consequences

- **Easier:** the API answers "which items, quests, offers and translations did this mod add or change" and "which value changed from what to what".
- **Easier:** a change to a trader or a quest stops being a whole-object diff that a client must compute.
- **Harder:** `mod_change_paths` grows. A quest-heavy mod such as Ledger (45 added quests) writes more rows than today.
- **Harder:** the assort importer needs the offer tree, and the offer key needs a rule for duplicates.
- **Harder:** the migration renames a table that the tests and the Api name today.
- **Revisit when:** row volume blocks a query, or the location and bot row model arrives.

## Verification

```gherkin
Feature: Answer what a mod changed, value by value

  # ADR-0032 decision 2. A changed value is one row, and it names both sides.

  @unverified
  Scenario: A changed item value names the old value and the new value
    Given the dataset "D1" of the mod "mod-x" changes the weight of the item "A1" from 9 to 10
    When I read the change paths of the item "A1" in "D1"
    Then one path is "/_props/Weight"
    And its old value is "9"
    And its new value is "10"

  # ADR-0032 decision 3. Quests, traders and customizations stop at the object today.

  @unverified
  Scenario: A changed quest names each changed field
    Given the dataset "D1" changes one condition of the quest "Q1"
    When I read the change paths of the quest "Q1" in "D1"
    Then the changed field has a path row
    And no path row covers the whole quest

  @unverified
  Scenario: A changed trader names the field under the trader
    Given the dataset "D1" changes the insurance price coefficient of the trader "T1"
    When I read the change paths of the trader "T1" in "D1"
    Then one path row names the changed coefficient
    And its old value and its new value differ

  @unverified
  Scenario: A changed customization names each changed field
    Given the dataset "D1" changes the side of the customization "C1"
    When I read the change paths of the customization "C1" in "D1"
    Then one path row names the changed side

  # ADR-0032 decision 4. An assort offer is a template named by a content ULID.

  @unverified
  Scenario: An added offer is a template of the kind "assort"
    Given the dataset "D1" adds one offer of the item "T1", paid in roubles, to the trader "TR1"
    When I read the templates of "D1"
    Then one template has the kind "assort"
    And its template id is a content ULID of the trader, the item and the channel
    And its operation is "added"

  @unverified
  Scenario: A price that rises from 9 to 10 keeps the id of the offer
    Given the dataset "D1" changes the rouble count of the offer of the item "T1" at the trader "TR1" from 9 to 10
    When I read the change paths of that offer in "D1"
    Then one path row is "/barter/<rouble address>/count"
    And its old value is "9"
    And its new value is "10"
    And no offer of "D1" has the operation "removed"

  @unverified
  Scenario: A new instance id in the next boot keeps the id of the offer
    Given the trader "TR1" sells the item "T1" for roubles
    And the next boot gives every assort row a new instance id
    When I archive the next run of the same install
    Then the offer keeps its template id
    And the run adds no template

  @unverified
  Scenario: Two ways to pay for one item are two offers
    Given the trader "TR1" sells the item "T1" for roubles and for two batteries
    When I read the templates of the kind "assort" of the dataset "D1"
    Then two offers name the item "T1"
    And their template ids differ
    And the channel of one offer names the battery item

  @unverified
  Scenario: A part of an offer is keyed by its slot
    Given the dataset "D1" changes the durability of the part in the slot "mod_muzzle" of the offer of "T1" at "TR1"
    When I read the change paths of that offer in "D1"
    Then the path names the slot "mod_muzzle"
    And the path names no array position

  @unverified
  Scenario: A trader that sells one item twice through one channel gets one id per offer
    Given the trader "TR1" sells the item "T1" for roubles in two offers with different presets
    When I read the templates of the kind "assort" of the dataset "D1"
    Then the two offers have two different template ids
    And the next run of the same content gives the same two ids

  @unverified
  Scenario: A quest that unlocks an offer is linked through the offer id
    Given the quest "Q1" unlocks the offer of the item "T1" at the trader "TR1" when it succeeds
    When I read the offers of "D1" that a quest unlocks
    Then one offer is the offer of "T1"
    And its status is "success"
    And its quest is "Q1"

  # ADR-0032 decision 2, TD-46. A longer array names the added indexes.

  @unverified
  Scenario: A longer array names the added indexes
    Given the dataset "D1" adds nine entries to a slot filter of the item "A1"
    When I read the change paths of the item "A1" in "D1"
    Then each added entry has its own path
    And no path names the whole array

  # ADR-0032 decision 6. Some kinds keep the hash until their row model exists.

  @unverified
  Scenario: A changed location keeps the hash and no row
    Given the dataset "D1" changes one location file and nothing else
    When I read the tables of "D1"
    Then the template table is empty
    And the dataset holds the canonical hash of the location file
    And the run reports the file as unstored
```

## Action Items

1. [x] Rename `mod_item_changes` to `mod_change_paths` and widen the kind check (migration). Done: `20261020210129_AddChangePaths` renames the table and its constraints in place, so no row is copied and no row is dropped. The kind check of `mod_templates`, `mod_change_paths` and `mod_id_maps` now names `assort`.
2. [x] Emit one path per leaf in `RunDiff`, and drop the whole-array path (closes TD-46). Done: `RunDiff.ReadChanges` reads the path of each index, and `ToPointer` writes it as an RFC 6901 pointer. Corrected on 2026-10-21: `CanonicalForm.Compare` still reported one path for an array of a new length. It now reports one path for each added or missing index.
3. [x] Emit paths for quests, traders and customizations in `RunDiff`; keep the full object rows. Done: a modified quest and a modified customization state one path for each changed value, and keep their object row. A modified trader states the changed values of its `base.json`; the other files of the trader directory keep their canonical hash. Checked with unit tests on 2026-10-21. No real run was imported for this item.
4. [x] Add the `assort` template kind, the content ULID of an offer and the offer tree import (ADR-0034, action items 1 to 5). Done: `SptArchive.Core.Canonical.Assort` names every offer, `RunDiff.CollectOffers` pairs a run with its baseline by address, and `RunImporter` writes `mod_templates`, `mod_change_paths`, `mod_trader_assort_items`, `mod_trader_quest_assorts` and `mod_id_maps`.
   - Measured on 4.1.6 on 2026-10-20: `ECOTI` adds 18 offers over two traders. The import stores 18 `assort` rows and 18 offer rows, with 0 false changes.
   - One trader of that baseline holds 5,090 instance ids, and the run holds 5,099. The two sets share none. The diff still pairs its 6,131 offers with 0 changes.
   - The same baseline has 4 groups of offers that share a preset, so their order names them: three groups of two and one of three, nine offers of 6,131. The decision predicted three groups.
5. [x] Extend the content hash input list in ADR-0029 amendment 4 when a hash-only kind becomes a row (no hash change expected: the rows replace the hash of the same file). Checked: an assort file keeps its canonical hash in `table_hashes`. Its offers also enter the hash as rows, so a change is content twice and never drops out.
6. [x] Close TD-45 for the kinds that now have rows; keep the open part for locations and bots. Done for the assort: `ECOTI` no longer stores its two assort files as files with no row. TD-45 keeps the locations, bots, hideout, prices and handbook.
7. [ ] Update the API surface and the query spec once the rows exist.

## Links

- ADR-0002 (jsonb next to promoted columns).
- ADR-0013 (dataset-keyed rows), ADR-0017 (a template operation above its paths), ADR-0022 (customizations).
- ADR-0027 (the run directory is transport), ADR-0028 (canonical form), ADR-0029 (content hash, amendment 4).
- ADR-0034 (the address of an object; the offer id is built from three addresses).
- `docs/specs/mod-registry.feature`, `docs/tech-debt.md` (TD-45, TD-46).
- `src/SptArchive.Core/Canonical/RunDiff.cs`, `src/SptArchive.Archiver/Runs/RunImporter.cs`.
