# ADR-0042: Store each address and each value once, and join them to a change

**Status:** Proposed
**Date:** 2026-10-21
**Deciders:** Yui

## Context

`mod_change_paths` states one row for each changed value of a mod run (ADR-0032). The row names the
change in full: `(dataset_id, kind, template_id, path)` is the key, and `op`, `old_value` and
`new_value` hold the change itself.

The key repeats. A dataset is one version of one mod, so two versions of the same mod state the same
address again. Two mods that change the same thing state the same address again. The value repeats too:
a filter array of a hundred slots takes 27 values, and each of those values enters many rows.

Measured on 2026-10-21 in the development archive, PostgreSQL 17.11:

- `mod_change_paths` holds 3,545 rows in 2 datasets. Every row has its own address (3,545 addresses),
  and the 2 datasets share none of them.
- The rows hold 247 distinct new values. 3,298 of the 3,545 rows repeat a value that another row holds.
  The new values take 92,374 bytes; the distinct values take 7,113 bytes.
- The largest repeats are `"Bear"` in 234 rows, `"Usec"` in 156 rows, and one item id in 137 rows.

The probe that sizes each design is a copy of those 3,545 addresses into 50 datasets, so 177,250 rows.
That copy is the shape of one mod imported at fifty versions, and it is the best case for a pool: the
same addresses return. A table of fifty unrelated mods shares fewer addresses, and the saving that the
address pool gives falls toward zero. The value pool does not depend on that, because a repeated value
is a repeated value in either table.

| Shape | Rows | Total size | Same-address conflict query | Page of 200 changes, one dataset |
|---|---|---|---|---|
| Inline, today | 177,250 | 53 MB | 362 ms | 0.044 ms |
| Value pool behind a text id | 177,250 | 53 MB | — | — |
| Address pool and value pool behind `bigint` ids | 177,250 | 22 MB | 58 ms | 0.338 ms |

The text of those 177,250 rows, by column: `path` 7,159,800 bytes, `new_value` 4,618,700 bytes,
`template_id` 4,254,000 bytes, `kind` 927,900 bytes, `op` 531,750 bytes, `old_value` 56,350 bytes.
838 paths, 719 template ids, 249 values and 4 kinds serve all of them.

Two facts follow from that table.

- The address is the larger part. The value is 26 percent of the text, and the address is 65 percent.
- A pool behind a text id wins nothing. A value of 27 bytes costs 32 bytes as an MD5 id, so the value
  pool behind a text id measured 99.5 percent of the table it replaced. A pool wins only behind a
  small integer.

## Decision

We will hold the address of a change and the value of a change in two pool tables, and reduce
`mod_change_paths` to a join that names a dataset, an address and a value.

```sql
create table mod_change_addresses (
  address_id  bigint generated always as identity primary key,
  kind        text   not null,
  template_id text   not null,
  path        text   not null,
  unique (kind, template_id, path)
);

create table mod_change_values (
  value_id bigint generated always as identity primary key,
  doc      jsonb  not null unique,
  sha256   text   not null          -- the canonical hash, for a filter that cannot compare jsonb
);

create table mod_change_paths (
  dataset_id  uuid   not null references mod_datasets (id) on delete cascade,
  address_id  bigint not null references mod_change_addresses (address_id),
  op          text   not null,
  old_value_id bigint references mod_change_values (value_id),
  new_value_id bigint references mod_change_values (value_id),
  primary key (dataset_id, address_id)
);
```

The connection row is the many-to-many row that the driver described: a dataset and an address, with the
value that the dataset sets at that address. One address, many datasets. One address, many values. One
value, many addresses.

## Options Considered

| Dimension | A: inline (today) | B: value pool, text id | C: address and value pool, `bigint` ids | D: entity store with a validity interval |
|---|---|---|---|---|
| Complexity | None | One join | Two joins, two identities, a reference count | A new store, a new importer, a new API |
| Cost | 100 percent | 99.5 percent | 42 percent | Not measured |
| Scalability | One row for each dataset and address | One row for each dataset and address | The pools grow with the distinct data, not with the versions | Grows with the distinct data |
| Team familiarity | The table that exists | The table that exists plus one | Two joins in each query | A new model |
| Conflict query | 362 ms | — | 58 ms | — |

Option A loses 58 percent of the bytes and does not answer "who else sets this address" without reading
every value. Option B loses nothing and wins nothing, because the id costs as much as the value that it
replaces. Option D is a different decision: it replaces the dataset scoping of ADR-0013 with a version
interval, and it does not change the row shape that this ADR covers. It stays open as a separate
proposal.

## Trade-off Analysis

The trade-off is read speed against storage and against the shape of a question.

A change row costs 0.044 ms today and 0.338 ms after the change, for a page of 200 rows. Both numbers
are far below the cost of the rest of an answer, so the read speed is not the constraint.

What the change buys is the question "which datasets set the same address, and do they agree". Today a
client reads every `new_value` of a table and compares JSON. After the change the server compares two
`bigint` columns, and the measured query falls from 362 ms to 58 ms. That query is the one that tells a
modpack author that two mods touch the same item, and it is the reason to make the change.

The change also stops the growth that matters. Between two versions of one mod, most addresses are the
same. The address pool holds them once, and a version adds only the connection rows. The pool grows with
the distinct data of the archive, not with the number of imports.

The address pool is the weaker half of the win, because it depends on addresses returning. A table of
fifty unrelated mods shares few addresses and saves little. The value pool is the stronger half on
repeated values, and it is free of that dependency. This ADR keeps both because one join serves both,
and because a change that stores an address once is not more complex than a change that stores a value
once.

## Consequences

- **Easier:** a client asks which datasets set one address, and whether they agree, and gets an answer
  from two integer columns. A re-import of a known mod version adds connection rows and no new address.
- **Harder:** every read of a change path joins two tables. The value rows carry a reference count, or a
  deletion reads the join, because a value that no row uses must go and a value that one row uses must
  stay. The content hash of ADR-0029 must enter the same input as today, and a pool that hides a value
  from the hash is a bug.
- **Harder:** the schema of `mod_change_paths` changes, so the API surface of ADR-0032 item 7 changes
  with it, and the old rows convert.
- **Revisit when:** a third of the addresses of a new import are new. That is the signal that the pool
  costs more than it saves.

## Verification

```gherkin
Feature: A changed value is stored once and joined to every change

  @unverified
  Scenario: Two mods that set the same address share one address row
    Given a dataset that sets the path "/_props/Price" of the item "5449016a4bdc2d6f028b456f" to 3000
    And a second dataset that sets the same path of the same item to 3500
    When the Archiver archives both runs
    Then the table "mod_change_addresses" holds one row for that item and that path
    And the table "mod_change_paths" holds one row for each dataset, and both name that address

  @unverified
  Scenario: Two datasets that set the same value share one value row
    Given two datasets that set the same address to the same value 3000
    When the Archiver archives both runs
    Then the table "mod_change_values" holds one row for that value
    And both connection rows name that value

  @unverified
  Scenario: A second version of one mod adds no address
    Given a dataset of a mod version that sets 2,792 addresses
    And the next version of that mod sets the same 2,792 addresses with other values
    When the Archiver archives the second version
    Then the table "mod_change_addresses" holds the same number of rows as before the import

  @unverified
  Scenario: The content hash does not depend on the pool
    Given a mod run that sets 100 values, 40 of which repeat
    When I compute the content hash of that run with and without the value pool
    Then the hash is the same
```

## Action Items

1. [ ] Add the migration that creates `mod_change_addresses` and `mod_change_values`, and that converts
   `mod_change_paths` to the two foreign keys. The conversion keeps the primary key of each row, so no
   row is lost.
2. [ ] Write the address and the value in `RunImporter` in one transaction, and reuse a row that exists.
   Two imports of the same value at the same time must not fail (ADR-0036).
3. [ ] Add the reference count, or the rule that a value row is never deleted, and say which in the code.
4. [ ] Change the API of ADR-0032 item 7 so that a client reads the address and the value as fields, and
   keep the whole document reachable.
5. [ ] Add the conflict query to the API: one address, the datasets that set it, and whether they agree.
6. [ ] Measure the pool on the real runs before the merge: the distinct addresses of `WTT-Armory` and
   `AllTheClothes` across their versions, and the share that returns.

## Links

- ADR-0002: the full source object stays. The value pool holds what the source holds.
- ADR-0013: a dataset is one version of one mod. The dataset id scopes a connection row.
- ADR-0028: the canonical form and the volatile paths. The value pool stores the value as it was read,
  and the hash column is the canonical hash.
- ADR-0029: the content hash. The pool must not change its input.
- ADR-0032: `mod_change_paths`. This ADR changes its columns and not its rows.
- ADR-0034: the address of an object. The `template_id` of an address is a content ULID.
- ADR-0041: the rows that have no mod table. Their changes use the same connection rows.
- `docs/tech-debt.md`: TD-72, TD-73.
- `docs/research/storage-engine.md`: the measurements of each engine.
