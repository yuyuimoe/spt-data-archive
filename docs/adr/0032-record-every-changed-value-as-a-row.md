# ADR-0032: Record every changed value as a row

**Status:** Proposed
**Date:** 2026-10-20
**Deciders:** Yui

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

## Decision

**We will store one row for every changed value, for every kind of data.**

1. **One change table.** `mod_item_changes` becomes `mod_change_paths`. Its `kind` column accepts `item`, `quest`, `trader`, `customization` and `assort`. The columns stay the same: the RFC 6901 path, the operation (`set` or `remove`), the old value and the new value. The foreign key stays on `(dataset_id, kind, template_id)` to `mod_templates`.

2. **One path per leaf.** A difference inside an array yields one path for each index, such as `/conditions/0/_props/value`. An array that grows yields a path only for each added index. An array that shrinks yields a `remove` path only for each missing index. The whole-array path disappears.

3. **Quests, traders and customizations get paths.** Their template row stays. Their full object stays in its table (ADR-0002). Each changed value becomes a path row under the template.

4. **Assort becomes a row type.** The template kind is `assort`. The template id is the offer key. The offer key is `<traderId>:<rootTemplateId>`. When one trader sells the same root template more than once, the key gains `@<first 8 hex digits of the canonical hash of its barter scheme>`.
   - The offer tree is stored in `mod_trader_assort_items`: one row for the root and one row for each attached part, with the instance ids of the run. The tree stays self-consistent because a dataset is written once.
   - The changes inside an offer are paths under the offer key. The root fields keep their own path, such as `/loyaltyLevel` and `/barterScheme/0/0/count`. A part is keyed by its slot and not by its position: `/parts/<slotId>/upd/StackObjectsCount`.

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

The second trade-off is the offer key. A key based on the root template is readable and stable. It fails in one case: a trader sells the identical template twice with the identical barter scheme. The key then needs a suffix, and a change to that barter scheme looks like a removal and an addition. We accept that case and state it in the runbook.

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

  # ADR-0032 decision 4. An assort offer is a template with a stable key.

  @unverified
  Scenario: An added offer is a template of the kind "assort"
    Given the dataset "D1" adds one offer of the template "T1" to the trader "TR1"
    When I read the templates of "D1"
    Then one template has the kind "assort"
    And its template id is "TR1:T1"
    And its operation is "added"

  @unverified
  Scenario: A barter count that rises from 9 to 10 is a path row
    Given the dataset "D1" changes the required count of one barter item of the offer "TR1:T1" from 9 to 10
    When I read the change paths of the offer "TR1:T1" in "D1"
    Then one path row names the barter entry
    And its old value is "9"
    And its new value is "10"

  @unverified
  Scenario: A part of an offer is keyed by its slot
    Given the dataset "D1" changes the durability of the part in the slot "mod_muzzle" of the offer "TR1:T1"
    When I read the change paths of the offer "TR1:T1" in "D1"
    Then the path names the slot "mod_muzzle"
    And the path names no array position

  @unverified
  Scenario: A trader that sells one template twice gets one key per offer
    Given the trader "TR1" sells the template "T1" in two offers with different barter schemes
    When I read the templates of the kind "assort" of the dataset "D1"
    Then the two offers have two different template ids
    And both ids start with "TR1:T1@"

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

1. [ ] Rename `mod_item_changes` to `mod_change_paths` and widen the kind check (migration).
2. [ ] Emit one path per leaf in `RunDiff`, and drop the whole-array path (closes TD-46).
3. [ ] Emit paths for quests, traders and customizations in `RunDiff`; keep the full object rows.
4. [ ] Add the `assort` template kind, the offer key rule and the offer tree import.
5. [ ] Extend the content hash input list in ADR-0029 amendment 4 when a hash-only kind becomes a row (no hash change expected: the rows replace the hash of the same file).
6. [ ] Close TD-45 for the kinds that now have rows; keep the open part for locations and bots.
7. [ ] Update the API surface and the query spec once the rows exist.

## Links

- ADR-0002 (jsonb next to promoted columns).
- ADR-0013 (dataset-keyed rows), ADR-0017 (a template operation above its paths), ADR-0022 (customizations).
- ADR-0027 (the run directory is transport), ADR-0028 (canonical form), ADR-0029 (content hash, amendment 4).
- `docs/specs/mod-registry.feature`, `docs/tech-debt.md` (TD-45, TD-46).
- `src/SptArchive.Core/Canonical/RunDiff.cs`, `src/SptArchive.Archiver/Runs/RunImporter.cs`.
