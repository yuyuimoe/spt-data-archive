# ADR-0040: Filter a jsonb column by the value at a path

**Status:** Accepted
**Date:** 2026-10-21
**Ratified:** 2026-10-21 by Yui
**Deciders:** Yui

## Context

The Api shows nine kinds of jsonb column as `Any` values (TD-15): `props`, `data`, `base`, `dialogue`,
`suits`, `services`, `upd`, `barterScheme` and `metadata`, and the mod columns that carry JSON. Each
filter on such a column offered `eq` and `neq` and nothing else. Both compare the whole document. A data
browser cannot ask "the items whose `props.Slots` is 5" (TD-52).

The same two operations had a second defect (TD-55, audit finding F-08). They take a string, and the string
goes to the jsonb parameter as it is. A value such as `"x"` is not JSON text, so PostgreSQL refuses it with
`22P02`, and the client reads "Unexpected Execution Error" with no cause.

What the stack offers, checked on 2026-10-21 in `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3:

- `NpgsqlJsonDbFunctionsExtensions` holds six functions: `JsonContains` (`@>`), `JsonContained` (`<@`),
  `JsonExists` (`?`), `JsonExistAny`, `JsonExistAll` and `JsonTypeof`. It has no function for the operator
  `#>`, and none for jsonpath (`@?`, `@@`).
- `PgJsonTraversalExpression` renders `#>`, but it lives in a namespace named `Internal`. EF Core marks that
  namespace as an API that can change in any release (analyzer EF1001).
- `SqlFunctionExpression` and `HasDbFunction` are public EF Core API. They write a function call.
- PostgreSQL documents `jsonb_extract_path(doc, path...)` as equal to `doc #> path`. It takes the path as
  separate arguments. It takes an array only after the keyword `VARIADIC`, which EF Core cannot write.
- The schema already has a GIN index with `jsonb_path_ops` on `items.props`, `mod_items.props`,
  `customizations.props`, `mod_customizations.props`, `quests.data` and `mod_quests.data`. That index serves
  `@>`. It does not serve `#>` or `jsonb_extract_path`.

The Api limits each request: 1 MB, depth 10, 10 seconds and a cost limit (ADR-0008, ADR-0038). A new filter
must stay inside them.

## Decision

1. **Every jsonb filter gets two operations: `pathEq` and `pathNeq`.** Both take
   `{ path: [String!]!, value: Any }`. A client writes
   `items(where: { props: { pathEq: { path: ["Slots"], value: 5 } } })`. One input type serves every jsonb
   column, official and mod, so a new column gets the operations when it gets the type.

2. **A path is 1 to 10 segments.** A segment is an object key or an array index, written as text
   (`["Nested", "list", "1"]`). A segment has 1 to 256 characters and no NUL character. A bad path is
   `JSON_PATH_MALFORMED`.

3. **A value is any JSON value, and a comparison is a jsonb comparison.** A number compares by value (`5` equals
   `5.0`). An object compares by content and not by key order. A list compares in order. The type counts: the
   string `"5"` is not the number `5`. A number keeps its text on the way to the database, so a large integer is
   not rounded. `value: null` means JSON null. A query with no `value` is `JSON_VALUE_MALFORMED`, because a
   missing value is not the same as null. A value nested deeper than 32 levels is also `JSON_VALUE_MALFORMED`.

4. **`pathEq` never matches a document that lacks the path. `pathNeq` always matches it.** `pathNeq` is the C#
   operator `!=`: the row differs from the value, so it passes.

5. **The translation is `jsonb_extract_path(doc, segment, ...) = value`.** Core holds ten overloads of
   `JsonPathFunctions.Extract`, one for each path length. `ArchiveDbContext` maps each to the PostgreSQL
   function with a public `SqlFunctionExpression`. Every segment and the value are query parameters, so a path
   never becomes part of the statement text. A path from a client cannot make a new statement shape except
   by its length, so EF Core caches at most ten shapes.

6. **`eq` and `neq` keep their meaning and their type.** They compare the whole document and take JSON text. The
   Api now checks the text before the query. Text that is not JSON is `JSON_VALUE_MALFORMED`, and the message
   shows the way to write a string (`"\"x\""`). This closes TD-55.

7. **PostgreSQL has the last word on two limits.** A NUL character in a string and a number outside the numeric
   range pass the shape check and fail in `jsonb` (`22P05`, `22003`). An error filter turns these states and
   `22P02` into `JSON_VALUE_REJECTED`. The message does not repeat the text of the database.

8. **The three codes are fixed.** A client branches on `JSON_PATH_MALFORMED`, `JSON_VALUE_MALFORMED` and
   `JSON_VALUE_REJECTED`, as it branches on the release codes of ADR-0019. A bad filter is an error and never an
   empty list.

9. **The cache rule does not change.** The key is the hash of the request body (ADR-0008, ADR-0037). The path
   and the value are in the body, so a filtered answer is cached like any other and no filter shares an answer
   with another one. A test holds this.

10. **No index and no migration come with this decision.** See the consequences.

## Amendment 1 (2026-10-21)

The driver asked two things of this decision on 2026-10-21: a client must still be able to read a whole
document, and the cost of reading it must be stated and charged. The catalogue of paths, which would answer
"which fields may I filter", is deferred (TD-68). The whole-document field is not.

11. **A jsonb column is not exposed as its stored text.** The whole document is one field, `rawJson`, of the
    scalar `Any` (TD-15). The text of the column is not a field of its own: a value of 132 MB may not leave
    the Api as an unguarded string. The first type to take the field is `ModDatabaseObject` (ADR-0041),
    because it is the only table that holds a document of megabytes; the other types keep the field they
    have.

12. **`rawJson` costs 100 times a small field.** One field of one row costs 1, so a page of 200 rows that asks
    for the whole document costs more than `MaxFieldCost` (20,000) and is refused with `HC0047` before the
    query runs, while a page of 50 rows is served. The cost analysis bounds the shape of a request. The two
    limits below bound its data, because a weight is not a size.

13. **The size of a document is a column of its row, and the Api decides with it before it reads.** Every row
    of `mod_database_objects` states `bytes` (ADR-0041), and the resolver refuses a document larger than
    `RawJson:MaxValueBytes` (2 MB, the value of `Cache:MaxResponseBytes`) with `RAW_JSON_TOO_LARGE`, naming
    the size and the limit. A measurement decided this: reading one 132 MB document costs 138 ms and 175 MB
    of memory, because PostgreSQL keeps a value of that size outside the row and compressed, and assembling
    it reads all of it (ADR-0041, measurement). So the refusal comes before the read, and the statement that
    reads the document carries the condition in itself
    (`CASE WHEN bytes <= limit THEN data END`): a row that the resolver refuses is never assembled, even if a
    stale size disagrees with the document.

14. **One request carries at most 4 MB of whole documents, and the code is `RAW_JSON_REQUEST_LIMIT`.** The
    budget lives in the global state of the operation, so every field of one request pays into the same total
    and two clients never share one. A page of two hundred documents of two megabytes would be 400 MB, which
    neither the cache nor a client wants.

## Amendment 2 (2026-10-22)

The audit of 2026-10-21 found that a page of `modFiles` had no byte limit: the import stores the text of a
configuration file up to 2 MB, and the Api served it in the same page as the manifest, so
`modFiles(first: 200) { contentText }` could answer 400 MB (TD-59). The cost limit counts fields and objects
and never bytes, and `Cache:MaxResponseBytes` stops the cache from *storing* such an answer, not the Api from
building it. The amendements below bound the bytes. They change no other decision.

15. **The text of a configuration file is a guarded field of its own.** `contentText` and `content` are read
    by a resolver, and the stored text and document are never projected with the row: the statement that reads
    them carries the limit in itself (`kind = 'config' AND size <= limit`), so a text above the limit is never
    assembled in memory. A file that is not a configuration file answers `null`, because the import stores no
    text for any other kind (ADR-0029).

16. **A row pays for its bytes once, whichever field reads them.** The text and the document of a file come
    from the same stored bytes, so a client that asks for both pays the size of the file one time. The budget
    lives in the global state of the operation, as the budget of whole documents does (decision 14).

17. **The limits are `FileText:MaxValueBytes` (2 MB) and `FileText:MaxRequestBytes` (4 MB), and the codes are
    `CONTENT_TEXT_TOO_LARGE` and `CONTENT_TEXT_REQUEST_LIMIT`.** The per-value limit is the cap that the
    import already applies to one file, so a text that the archive holds is served and nothing larger is. A
    text costs 10 times a small field, so a page of two hundred files stays inside `MaxFieldCost` and the byte
    budget is what bounds such a page. The counted size is the size of the file on disk, which is an upper
    bound of the bytes of its text, so the count never underestimates.

Measured on 2026-10-21 before the change: 171 files, 4 of them with text, the largest 94,728 bytes
(`AllTheClothes/config/config.jsonc`), so no page was large on that archive. The limit is for the mod that
ships a 2 MB configuration.

## Options Considered

| Dimension                   | A. Containment `@>` | B. `#>` by the internal expression | C. `jsonb_extract_path` (chosen) | D. jsonpath `@@` | E. Filter in memory |
|-----------------------------|---------------------|-------------------------------------|-----------------------------------|------------------|---------------------|
| Public EF Core API only     | Yes                 | No (EF1001)                         | Yes                               | No function exists | Yes               |
| Reaches an array position   | No                  | Yes                                 | Yes                               | Yes              | Yes                 |
| Compares an object by content | No (it tests "contains") | Yes                           | Yes                               | Yes              | Yes                 |
| Served by an existing index | Yes (GIN `jsonb_path_ops`) | No                           | No                                | Yes, by the PostgreSQL docs; not run here | No |
| Reads the column into memory | No                 | No                                  | No                                | No               | Yes                 |
| Shapes in the EF Core cache | One                 | One for each path length            | One for each path length (10)     | One              | One                 |

Option A is the cheapest and it uses the index that exists. It loses on meaning. Containment tests
`{"list":[1,2]} @> {"list":[2]}`, which is true, so it cannot say "the second element is 2" and cannot say
"equals". A quest keeps its conditions in arrays, and a data browser must reach them. Option A also cannot
make `pathNeq` for a missing path without a second test.

Option B writes the operator itself. It rests on a type that EF Core marks as internal. A provider update can
break it without notice, and the build would need a suppressed warning.

Option D is the best match to the index and to the language of a path, and the provider has no function for
it. A raw SQL fragment would need our own escaping of every key into a jsonpath string.

Option E reads every row of a release into the Api. It breaks the 10 second limit and the memory goal.

Whole-document `eq` as `Any` (a breaking change) was also considered. It would make one convention for the
whole filter: the client writes JSON as JSON. It lost because the old form `eq: "{...}"` would stop matching
without an error, and that is the "empty list that means a wrong query" that ADR-0019 refuses. It is the one
open point for the driver.

## Trade-off Analysis

The trade-off is index support against meaning. Option A gets the index for free and answers a different
question. Option C answers the question the client asks, for every position in a document, and uses no index
today. The archive is small: 4,673 items, 558 quests and about 6,000 offers per release. A scan costs
milliseconds at that size. Meaning is hard to add later, and an index is easy to add later, so C wins.

Two conventions now share one input type: `eq` takes JSON text, and `pathEq` takes JSON as a GraphQL value.
This costs clarity. It keeps every existing query working. The message of `JSON_VALUE_MALFORMED` names the
other operation.

## Consequences

- **Easier:** A client asks about the contents of a document: `Slots`, a nested key, a position in a list, a
  whole sub-document. The Api reads this as JSON and filters it as JSON.
- **Easier:** A bad filter has a code. The text `"Unexpected Execution Error"` no longer answers a JSON value.
- **Harder:** A path filter has no index. `jsonb_extract_path` is not served by the GIN index. A scan of one
  release is cheap today and is not measured on the real archive (TD-64).
- **Harder:** There is one more shape of query for each path length (ten).
- **Harder:** `eq` and `pathEq` take values in two forms (TD-65).
- **Easier (amendment 1):** A client reads the whole document when it needs one, with a field that costs what
  it costs and says so.
- **Harder (amendment 1):** A document above 2 MB is not served at all: the client reads the small fields of
  the row, filters with `pathEq`, or reads the file (ADR-0029). The paths inside a document are not described
  anywhere, so a client still learns the shape from the data itself (TD-68).
- **Cost control:** Each filter term counts in the cost limit. A query with 3,000 `pathEq` terms is refused with
  `HC0047`, and the parser refuses a literal nested deeper than 200 levels. Both were run on 2026-10-21.
- **Revisit when:** a path filter is slow on the real archive. Then add a B-tree index on the expression of the
  hot path, or a GIN index and Option D, and write a new ADR for the index.

## Verification

The scenarios live in `docs/specs/query-api.feature`, under "Filter by a path". The tests are in
`tests/SptArchive.Tests/JsonPathFilterTests.cs` and `QueryCacheTests.cs`.

```gherkin
Feature: A client filters a jsonb column by the value at a path

  Scenario: A client finds the rows whose JSON document holds a value at a path
    Given the item "A1" has the property "Slots" 5 in its "props" column
    And the item "B1" has the property "Slots" 3 in its "props" column
    When I query "items" where "props" has the value 5 at the path "Slots"
    Then the answer has the item "A1"
    And the answer does not have the item "B1"

  Scenario: A path reaches into an object and an array
    Given the item "A1" has the property "Nested" with the list [1, 2] in its "props" column
    When I query "items" where "props" has the value 2 at the path "Nested", "list", "1"
    Then the answer has the item "A1"

  Scenario: The negation keeps the rows that lack the path
    Given the item "A1" has the property "Slots" 5 in its "props" column
    And the item "C1" has no property "Slots" in its "props" column
    When I query "items" where "props" has not the value 5 at the path "Slots"
    Then the answer has the item "C1"
    And the answer does not have the item "A1"

  Scenario: A bad path is refused with its own error code
    When I query "items" where "props" has the value 5 at the path with no segment
    Then the response has the error code "JSON_PATH_MALFORMED"
    And the response has no empty list

  Scenario: A whole-document comparison with text that is not JSON is refused
    When I query "items" where "props" equals the text "x"
    Then the response has the error code "JSON_VALUE_MALFORMED"
    And the response is not "Unexpected Execution Error"

  Scenario: A value the database refuses is an error and not an internal error
    When I query "items" where "props" has the number 1e999999 at the path "Slots"
    Then the response has the error code "JSON_VALUE_REJECTED"

  Scenario: A path filter extracts the path in the database and never matches text
    When I query "items" where "props" has the value 5 at the path "Slots", "x"
    Then the database runs one "jsonb_extract_path" over the column
    And the database runs no "LIKE" on the column

  Scenario: A filtered answer is cached like any other
    When I send a path filter for the value 5 and then for the value 3
    Then both responses have the header "X-Cache" with the value "MISS"
    When I send the filter for the value 5 again
    Then the response has the header "X-Cache" with the value "HIT"
```

The two limits of a text are the behaviour of `docs/specs/mod-api.feature`, under "The files":

```gherkin
Feature: Read a mod dataset through the Api

  Scenario: The text of one file above the limit of the Api is refused
    Given the Api serves at most 5 bytes of one configuration text
    When I query "modFile" with the dataset id of "D1" and the path of its configuration file
    Then the answer is the error "CONTENT_TEXT_TOO_LARGE"

  Scenario: A page of configuration texts above the limit of the Api is refused
    Given the Api serves at most 9 bytes of configuration text in one request
    When I query "modFiles" with the dataset id of "D1"
    Then the answer is the error "CONTENT_TEXT_REQUEST_LIMIT"
```

Both run inside `dotnet test` (ADR-0012), and the unit tests of the same limits are in
`tests/SptArchive.Tests/FileTextTests.cs`.

## Action Items

1. [x] Add `JsonPathFunctions` to Core and map it in `ArchiveDbContext`.
2. [x] Add `pathEq` and `pathNeq` to the jsonb filter type, with the handler and the three error codes.
3. [x] Check the text of `eq` and `neq` before the query (TD-55).
4. [x] Add the scenarios to `query-api.feature` and the tests that prove them.
5. [ ] The driver confirms the form of whole-document `eq`: keep JSON text (this ADR) or take a JSON value.
6. [ ] Measure a path filter on the real archive and decide on an index (TD-64).
7. [ ] Add ordered comparison (`gt`, `lt`) and a test for the existence of a path (TD-66).
8. [x] Add `rawJson` to `ModDatabaseObject` with the cost weight, the two limits and the two error codes
   (`RawJson.cs`, `ObjectApiTests`, three scenarios of `mod-api.feature`).
9. [x] Promote the size of the source to `mod_database_objects.bytes` (`20261021213335_AddObjectRowSize`).
10. [ ] Describe the shape of a document, so that a client filters it without reading it (TD-68).
11. [x] Bound the text of a configuration file in bytes, which is the same problem one table over (TD-59,
    amendment 2, done 2026-10-22).

## Links

- ADR-0002: the jsonb columns and the full source object
- ADR-0008, ADR-0037: the cache key, which is the hash of the body
- ADR-0014, ADR-0019: the model for an error code that a client branches on
- ADR-0038: the limits that a filter must stay inside
- TD-52 and TD-55 in `docs/tech-debt.md`; TD-64, TD-65 and TD-66 are new
- Finding F-08 in `docs/security/audit-2026-10-21.md`
- ADR-0041: the table that `rawJson` serves, and the measurement of a 132 MB document
- Code: `src/SptArchive.Api/GraphQL/JsonFilter.cs`, `JsonOperationHandler.cs`, `JsonFilterArguments.cs`,
  `JsonValueErrorFilter.cs`, `RawJson.cs`; `src/SptArchive.Core/Data/JsonPathFunctions.cs`
