# ADR-0017: Record one template operation for every item that a mod touches

**Status:** Accepted
**Date:** 2026-10-19
**Deciders:** Yui

## Context

ADR-0013 models a change to official data as one row for each changed property path.
That model cannot tell an addition from a modification.

An item template has one template id. Every reference in the server data uses that id.
The template id is the identity of the item.

A mod either changes the properties of an existing template id.
Or the mod defines a new item under a new template id.
A mod cannot do both under one id.

So against a baseline, one template id has exactly three possible states:
in both exports, only in the target export, or only in the baseline.

Under ADR-0013 all three states became path rows with `op = set`.
The old value was empty for an added item and for a new property of an old item.
These two cases have the same shape and different meanings.
No query separates them without a join against the official table.

## Decision

We will store the operation at the template level and the properties below it.

The table `mod_templates` holds one row for each template id that a dataset touches.
The row states the operation: `added`, `modified` or `removed`.
The table `mod_item_changes` holds the changed property paths under that row.

```sql
CREATE TABLE mod_templates (
    dataset_id  uuid NOT NULL REFERENCES mod_datasets(id) ON DELETE CASCADE,
    kind        text NOT NULL CHECK (kind IN ('item', 'quest', 'trader')),
    template_id text NOT NULL,                       -- the tpl id, quest id or trader id
    op          text NOT NULL CHECK (op IN ('added', 'modified', 'removed')),
    parent_id   text,                                -- the item "_parent" node; NULL otherwise
    PRIMARY KEY (dataset_id, kind, template_id)
);
CREATE INDEX ON mod_templates (template_id);
CREATE INDEX ON mod_templates (dataset_id, op);

CREATE TABLE mod_item_changes (
    dataset_id  uuid NOT NULL,
    kind        text NOT NULL DEFAULT 'item' CHECK (kind = 'item'),
    template_id text NOT NULL,
    path        text NOT NULL,                       -- RFC 6901: /_props/Weight
    op          text NOT NULL CHECK (op IN ('set', 'remove')),
    old_value   jsonb,
    new_value   jsonb,
    PRIMARY KEY (dataset_id, kind, template_id, path),
    FOREIGN KEY (dataset_id, kind, template_id)
        REFERENCES mod_templates (dataset_id, kind, template_id) ON DELETE CASCADE
);
```

The child repeats `kind` only so the foreign key names the primary key of the parent.

Two invariants follow from the domain fact, and the Archiver enforces both:

- An `added` template has no `old_value` and no `remove` path.
- A `removed` template has no `new_value` and no `set` path.

`removed` stays in the list although it is expected to be empty.
The diff can observe it, and a silent hole in the list is worse than an unused value.

## Options Considered

| Dimension             | A. Path rows only (ADR-0013) | B. Template row plus path rows | C. Full snapshot per changed template |
|-----------------------|------------------------------|--------------------------------|---------------------------------------|
| Addition is visible   | No, it looks like a change   | Yes, one indexed value         | Yes                                   |
| Storage of one addition | Paths only                 | Paths plus one row             | The complete template                 |
| Query "which mods add items" | Join with official   | One index scan                 | One index scan                        |
| Old value or nothing  | Ambiguous                    | Exact                          | Exact                                 |
| Write path            | One pass                     | Two passes, parent then child  | One pass, plus a diff at read time    |

Option A is the model in ADR-0013. It cannot answer the most common question about a mod.
Option C stores the full `_props` object again, which breaks rule 4 of `AGENTS.md`
only in spirit: the full object is already there. Its real cost is that a reader
must diff two large objects to answer a yes or no question.

## Trade-off Analysis

Option B adds one table and one pass in the write path. It removes an ambiguity
that no index can repair later.

The cost is a second row to keep consistent with the first. The write path
derives the template operation before it writes any path row.
The invariants above are cheap to assert inside the same transaction.

Two smaller gains come free. A client can ask for the category of an added item,
because `parent_id` is a promoted column (ADR-0002). And the count of added,
changed and removed templates needs no join against the official table.

## Consequences

- **Easier:** "Which mods add items" and "which mods only change existing items"
  are one index scan on `mod_templates`.
- **Easier:** An empty old value now has one meaning only.
- **Easier:** The row counts per operation are part of the dataset hashes (ADR-0013),
  so a change in the set of touched templates changes the content hash.
- **Harder:** The write path has two passes, and a failed second pass leaves a template row
  with no paths. The transaction must cover both.
- **Harder:** `kind` is in the primary key, so one dataset can hold an item and a quest
  with the same 24-character id. A collision is a data error, not a schema error.
- **Revisit when:** A mod gains a way to split one template id into two items,
  or the server lets a mod remove an item through an API.

## Verification

```gherkin
Feature: A mod dataset states the operation of every item it touches

  Scenario: An added item is one template row with no old value
    Given the official baseline has no template "B1"
    When a mod adds the item "B1" with the parent "node-weapons"
    Then the table "mod_templates" has one row with the operation "added"
    And that row states the parent "node-weapons"
    And every row of "mod_item_changes" for "B1" has no old value

  Scenario: A changed property is a modified template with an old value
    Given the official baseline has the item "A1" with the weight 0.5
    When a mod sets the weight of "A1" to 0.7
    Then the table "mod_templates" has one row with the operation "modified"
    And the row of the path "/_props/Weight" states the old value 0.5

  Scenario: An addition and a modification of the same id cannot both exist
    Given a mod writes the template id "A1"
    And the official baseline has the item "A1"
    When the Archiver stores that dataset
    Then the row of "A1" states the operation "modified"
    And no row states the operation "added" for "A1"

  Scenario: The operation of a template is filterable without official data
    Given the dataset "D1" adds the item "B1" and changes the item "A1"
    When a client asks for the templates of "D1" with the operation "added"
    Then the answer holds "B1" only
    And the query reads no official table

  Scenario: A new property of an old item is not an addition
    Given the official item "A1" has no property "_props/CanSellOnRagfair"
    When a mod sets that property to true
    Then the template "A1" states the operation "modified"
    And the row of that path states no old value

  Scenario: An added item states a parent that the baseline does not have
    Given a mod adds the item "B1" with the parent "node-weapons"
    And the official baseline has no node "node-weapons"
    When the Archiver stores the dataset
    Then the template row of "B1" states the parent "node-weapons"
    And the archive can answer that the parent does not resolve for a lone install
```

## Action Items

1. [ ] Add `mod_templates` with the composite key, and move `mod_item_changes` under it.
2. [ ] Change the dataset write path to derive the template operation before the paths.
3. [ ] Assert the two invariants inside the dataset transaction.
4. [ ] Add the parent check for an added item, and record it as a finding, not a failure.
5. [ ] Carry the template counts into `row_counts` and `table_hashes`, so a change of the
   touched set changes `content_hash`.

## Links

- Related: [ADR-0013](0013-mod-ingestion-by-isolated-run.md) (the dataset model),
  [ADR-0002](0002-hybrid-columns-and-jsonb.md) (promoted columns),
  [ADR-0018](0018-do-not-delete-mod-data-in-the-first-release.md)
- Spec: [docs/specs/mod-registry.feature](../specs/mod-registry.feature)
- C4: `archive.modRunner`
- Code: `src/SptArchive.Core/Entities/Mod.cs`
