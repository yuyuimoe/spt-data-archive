# DocumentDB and the `bson` type in PostgreSQL

**Date:** 2026-10-21. **Question asked by the driver:** can we put DocumentDB into our PostgreSQL and turn the
`jsonb` column into `bson`?

**Answer in one line:** it is possible, it is a young extension, it solves no problem that we have today, and
it adds a second query language under a GraphQL view that we already own. Keep `jsonb`. Revisit this note when
a client needs a sub-field of a document as a filtered, sorted column, or when a projection must read one
value of a document without reading the document.

## What DocumentDB is

- It is two PostgreSQL extensions, `pg_documentdb_core` and `pg_documentdb`, and a gateway that speaks the
  MongoDB wire protocol. Documents are stored in PostgreSQL tables, in PostgreSQL's own storage layer. There is
  no separate storage engine.
- `pg_documentdb_core` adds the type `documentdb_core.bson` (the code also calls it `pgbson`). It is a
  `varlena` type like `jsonb`, so **TOAST stores it out of line and compresses it above 2 KB**, exactly as it
  does for `jsonb`.
- Indexes: GIN (single path, wildcard, composite, hashed), a RUM extension for ordered and position-aware
  search, and HNSW for vectors.
- Field access: `bson_expression_get` reads one field. An index-only scan can answer a projection from an
  index, which is the one thing `jsonb` cannot do.
- Supported releases: PostgreSQL 15 to 18 (15 with the extension only, 16 and newer with the packaged
  gateway). Licence: MIT. Release status: pre-GA. The metadata of the repository marks the current release
  `0.118-0` as unreleased, and the release workflow marks published releases as prereleases.

Sources: `github.com/documentdb/documentdb` (project wiki: overview, extension architecture, glossary),

## What it would give us

1. **A field-level projection that an index can answer.** Our slowest read is not a scan; it is the read of one
   whole document to answer a question about one value of it. Measured on 2026-10-21: 138 ms and 175 MB of
   memory for the 132 MB loose loot file of lighthouse (ADR-0041).
2. **A document query language that a client may already know** (MQL), beside GraphQL.

## What it would cost us

1. **A storage type that saves nothing.** BSON repeats every key name in every document and keeps no
   dictionary, so it is not smaller than `jsonb`. It also compresses with the same TOAST, so the 45 MB that a
   132 MB map file takes today (ADR-0041) does not become 24 MB. Only gzip or zstd does that, and neither is a
   type that PostgreSQL can filter.
2. **A second query language under our view.** The Api is one GraphQL schema over EF Core and SQL. The
   filters, the release argument, the cache key, the cost limit and the error codes are all in that path
   (`JsonFilter`, `RawJson`, `QueryCacheMiddleware`). A `bson` column needs a new set of functions
   (`bson_expression_get` instead of `jsonb_extract_path`), and the MongoDB gateway answers a different
   protocol than the one we publish.
3. **No EF Core provider.** The model, the migrations and the projections are EF Core over Npgsql. A column of
   a custom type would be mapped through a value converter at best, and its functions through hand-written
   mappings, as `JsonPathFunctions` already does for the ten path lengths.
4. **A pre-GA dependency under a data archive.** The archive must read the same data for years, and the
   decisions that keep it readable are the conservative ones (ADR-0002: the source object stays whole, in a
   form that any PostgreSQL reads).

## What we do instead

- Keep `jsonb` and its filters (ADR-0040).
- Keep the size of a document as a column of its row, so a refusal costs no read (ADR-0041, decision 2).
- Charge a whole document in the cost analysis and bound the bytes of one request (`rawJson`, ADR-0040
  amendment 1).
- If storage becomes the cost, compress the *file* with zstd outside the filtered column, or move it outside
  PostgreSQL (TD-69). A type is not the answer to a storage bill.

## When to read this note again

- A client asks for a sort or a range on a sub-field of a document at a scale where a scan is too slow (TD-64).
- A page must return one value of each of 200 documents of 132 MB without reading the 200 documents.
- The archive starts to serve a MongoDB client, which is the protocol DocumentDB speaks.
