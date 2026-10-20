# ADR-0022: Archive player customization as a seventh dataset

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0002, ADR-0003, ADR-0014, ADR-0017, ADR-0020

## Context

The server keeps the clothes a player can wear in `templates/customization.json`, apart from
`templates/items.json`. On SPT 4.1.6 the file holds 728 entries: 706 `Item` entries and 22 `Node`
categories. An entry states the body part it covers (`Body`, `Head`, `Hands`, `Feet`), the sides that
may wear it (`Usec`, `Bear`, `Savage`), whether it is available by default, and the client bundle it
needs (`_props.Prefab.path`).

The archive had six table pairs: items, locales, quests, traders, trader assort and quest assort.
Player customization had no table. The exporter has always dumped it, because it serializes the whole
`TemplateTable`, so the data was captured on every run and then dropped by the Archiver.

Four mods from sp-mod.com show why this matters. Each was run alone against the same dependency-only
baseline (ADR-0013, ADR-0020):

| Mod | Items added | Customizations added |
| --- | --- | --- |
| All The Clothes 3.2.0 | 0 | **+104** |
| WTT - The Long Lost Heads of Yojenkz 2.1.0 | 0 | **+15** |
| WTT - W.A.A.C. 2.1.0 | +2 | **+65** |
| female pmc voice USEC 1.0.6 | 0 | +1 |

Three of the four add no item at all. Before this decision the archive recorded all three as "a mod
that changes nothing", which is wrong: they change the clothes a player can wear, and that is data
the end user can see.

The same runs also showed that the item dataset is already generic enough for a mod that adds new
item *categories*. WTT - Pack 'n' Strap 2.1.1 adds 49 items **and** four new nodes under `CompoundItem`
(`CustomContainerTemplate`, `CustomContainerItem`, `CustomSecureContainerItem`, `CustomBeltItem`).
Those rows fit `items` unchanged, because an item row stores any `_parent` and any `_props` object.
Customization is different: it is a different file with a different shape, so it needs its own table.

## Decision

We will archive player customization as a seventh dataset pair.

- The tables are `customizations` and `mod_customizations`. They follow the existing rules: an official
  row is keyed by `(spt_version, id)` and belongs to one release (ADR-0014); a mod row is keyed by
  `(dataset_id, id)` and belongs to one dataset (ADR-0013). Both carry the same columns.
- Promoted columns are `name`, `parent_id`, `type`, `sides` (`text[]`), `body_part`, `prefab_path` and
  `available_as_default`. The complete `_props` object stays in `props` (jsonb), as ADR-0002 requires.
- `sides` and `props` get a GIN index, so "which clothes may a Bear wear" and a containment query on
  the raw object both avoid a sequential scan.
- The Archiver imports `templates/customization.json`. A mod dataset reads the `customization` key of
  the exporter dump, which is the same map (TD-29).
- `mod_templates.kind` gains the value `customization`, so a mod that adds clothes produces a template
  row and a change row like any other dataset (ADR-0017).
- The other files in the dump stay out of the archive for now: `character`, `prices`, `handbook`,
  `dialogue`, `profiles`, `prestige`, `achievements`, `defaultEquipmentPresets`, `locationServices`.
  Each is a separate decision with its own table pair. The archive does not grow one generic
  "everything" table (ADR-0003 keeps official and mod data in parallel typed tables).

## Options Considered

| Dimension | A. Keep six datasets | B. Seventh typed pair (chosen) | C. One generic document table |
| --- | --- | --- | --- |
| A mod that adds only clothes | Looks like no change | **Dataset with rows** | Dataset with rows |
| Query shape | None | Typed, filterable by side and body part | JSON only, no filter columns |
| Schema cost | None | One pair, one migration | One table for everything |
| Matches ADR-0003 | Yes | Yes | **No**: it merges datasets |
| Matches ADR-0002 | Yes | Yes: promoted columns plus jsonb | Partly: jsonb only |

## Trade-off Analysis

Option A is cheapest and wrong. The four runs above show that a whole class of mods would be
invisible, and "the archive says a popular clothes mod changes nothing" is a false statement about
data the archive already captures.

Option B costs one table pair and one migration. It keeps the property the API depends on: a filter
on `sides` or `body_part` is an index lookup, not a JSON scan.

Option C is the tempting one, and it is the one that breaks ADR-0003: official and mod rows would
share one table, and every query would have to filter on a kind column that nothing enforces. It also
loses typed filters for every dataset at once.

The new columns are chosen the way ADR-0002 says: promote what a reader filters or sorts by. Nobody
filters clothes by `WatchRotation`; they filter by side, body part and name. Everything else stays in
`props`.

## Consequences

- A mod whose only content is clothes now has a dataset with rows, a table hash and a change row.
- The archive stores the bundle *path* of a customization, not the bundle. A client still needs the
  mod's bundle to render the clothes; that was already true of every other dataset.
- `mod_templates` gains a fourth kind. A reader that assumed three kinds must accept four.
- The Api gains `customizations` and `modCustomizations` (and `customization(id)`), next to
  `items` and `modItems`. Both lists take a `side` argument: the generic HotChocolate list filter has
  no "contains" for an array (only "every element" and "no element"), and "clothes a Bear may wear"
  is the question people ask.
- The Archiver's official run imports one more file, `templates/customization.json` (14 KB on 4.1.6,
  728 rows). The cost is negligible.
- TD-29 must import the `customization` key of an export, or a mod dataset will still look empty for
  clothes-only mods.

## Verification

Verified by run on 2026-10-20 against the real server data and four real mods, not by a test yet.

| What | Result |
| --- | --- |
| `templates/customization.json` on SPT 4.1.6 | 728 entries: 706 `Item`, 22 `Node`; 421 wearable (`Body` 151, `Feet` 112, `Hands` 100, `Head` 58) |
| Archiver run, `Archive__SptVersion=4.1.6` | `Customizations: 728`; `select count(*) from customizations where spt_version='4.1.6'` gives 728 |
| 4.0.13 rows stay | `items` still holds 4,582 rows of 4.0.13 next to 4,673 of 4.1.6; `customizations` holds 677 rows of 4.0.13 next to 728 of 4.1.6 |
| All The Clothes 3.2.0 alone | `customization` 728 to 832 (+104), items unchanged |
| WTT - The Long Lost Heads of Yojenkz 2.1.0 alone | `customization` 728 to 743 (+15), items unchanged |
| WTT - W.A.A.C. 2.1.0 alone | `customization` 728 to 793 (+65), items 4,673 to 4,675 |
| WTT - Pack 'n' Strap 2.1.1 alone | items 4,673 to 4,722 (+49, four new category nodes), `customization` unchanged |

```gherkin
@unverified @adr-0022
Feature: Archive player customization

  Scenario: Clothes are imported as their own rows
    Given the file "templates/customization.json" has an entry "6746f06e05e15b1b8bb2a0a5" with "_props" "BodyPart" "Head"
    When the Archiver runs
    Then the table "customizations" has a row with the id "6746f06e05e15b1b8bb2a0a5"
    And that row states the release of the run
    And the "props" column holds the complete "_props" object of the entry

  Scenario: A customization states the sides that may wear it
    Given the file "templates/customization.json" has an entry with "_props" "Side" ["Usec", "Bear"]
    When the Archiver runs
    Then the row has the sides "Usec" and "Bear"
    And a filter on the sides finds the row

  Scenario: A mod that only adds clothes changes the archive
    Given a mod dataset whose export adds one entry to "customization"
    When the Archiver imports the dataset
    Then the table "mod_customizations" has the new row
    And the table "mod_templates" has a template row of the kind "customization"
```

These scenarios stay `@unverified` until the Gherkin bindings exist (TD-06) and TD-29 lands.

## Action Items

- [x] Yui ratified this ADR on 2026-10-20 (Q1a).
- [ ] TD-29: import the `customization` key of an export into `mod_customizations`.
- [ ] TD-06: bind the three scenarios to tests.

## Links

- [ADR-0002](0002-hybrid-columns-and-jsonb.md): promoted columns plus jsonb.
- [ADR-0003](0003-separate-official-and-mod-tables.md): parallel typed tables.
- [ADR-0014](0014-keep-every-archived-spt-release.md): the release key on official rows.
- [ADR-0017](0017-record-a-template-operation.md): the template and change rows a dataset produces.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter that captures the dump.
