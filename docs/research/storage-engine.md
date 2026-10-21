# A storage engine for the archive: PostgreSQL, CouchDB or Cassandra

Status: research, 2026-10-21. The driver asked the question after ADR-0041: should the archive keep
PostgreSQL, move to Cassandra or Scylla, or move to CouchDB, whose model appends datasets?

Every number below comes from a command that ran on this machine on 2026-10-21. A number that comes from
documentation says so. The scripts are in `/tmp/dbcompare` and are not part of the repository.

## What the three servers were asked

The same two fixtures went to three servers:

- **A.** `/home/yui/work/sptmods/exports/WTT-Armory/locations/lighthouse/looseLoot.json`, 132,164,983
  bytes. One real map loot file of the game, and the largest document the archive holds.
- **B.** Locale rows shaped like `locale_entries`: `spt_version`, `scope`, `language_code`, `key` (40
  characters) and `value` (159 bytes). PostgreSQL holds the real table, 1,064,991 rows for two releases.
  CouchDB and Cassandra received 25,000 and 250,000 synthetic rows.

The servers: PostgreSQL 17.11 (`postgres:17-alpine`, the development database of the repository),
CouchDB 3.4.3 and Cassandra 5.0.9, each in one container, one node, with the default compression of that
server. Cassandra is the fair test and not Scylla: Cassandra 5 has SAI, the flexible second index, and
ScyllaDB has no equivalent.

## One document of 132 MB

| Server, storage | Bytes on disk | Read one small subfield |
|---|---|---|
| PostgreSQL `jsonb`, lz4 (ADR-0041) | **45,400,064** | **36 to 44 ms** |
| PostgreSQL `jsonb`, pglz (the default) | 47,300,608 | 127 to 131 ms |
| CouchDB 3.4.3, its own compression | 45,080,614 | 2,079 ms and 141,886,282 bytes on the wire |
| Cassandra 5.0.9, 127 chunks of 1 MB, LZ4 | 48,010,591 | 10,480 ms, and it needs all 127 chunks |
| PostgreSQL `text` + gzip -6 outside the database | 24,546,959 | not a database question |

CouchDB stores the file in the same number of bytes as PostgreSQL, within one percent. Cassandra stores
it in six percent more. **A different engine does not make the file smaller.**

Reading a subfield is where they separate. PostgreSQL sends back 32 bytes in 36 ms. CouchDB sends the
whole 132 MB to the client in two seconds. Cassandra reads every chunk in ten seconds and cannot answer
the question at all, because a chunk is a fragment of a value and not a field.

## Twenty versions of the same document

This is the question behind "CouchDB appends datasets by default". A new mod version is a new copy of
what the run changed.

| Server | Twenty copies | Factor of one copy |
|---|---|---|
| PostgreSQL, twenty rows | 20 × 45.4 MB | 20.0 |
| CouchDB, twenty documents | 901,611,608 bytes | **20.00** |
| Cassandra, twenty partitions | 960,145,122 bytes | **20.00** |

Every one of them stores twenty full copies. CouchDB keeps the old revision of a document until a
compaction, and its revision tree is a tree of full values, not a diff. The append that the driver
remembers is the append of **new documents**, and a new document is a new full value.

A small change is where CouchDB shows the cost of that: twenty revisions of a **200-byte** document grew
the database by **84 kB** before compaction, because each revision rewrites the block around it.

So the saving that the driver wants is not in the engine. It has to be in the model that the archive
keeps. See "Why the disk grows" below.

## The locale rows

| Server | Rows | Bytes on disk | Bytes per row |
|---|---|---|---|
| PostgreSQL, the real table, 3 indexes included | 1,064,991 | 448 MB | 440 |
| CouchDB | 25,000 | 11 MB | 456 |
| CouchDB | 250,000 | 109.5 MB | 459 |
| Cassandra, partition key + clustering key | 250,000 | 52,902,421 | 212 |
| Cassandra + SAI index on `value` | 250,000 | 154,459,088 | 618 |

The engines are within the same order of magnitude. Cassandra is the cheapest per row until a client
asks for a filter that needs the SAI index, and then it is the most expensive: the index added **97 MB
to a table of 53 MB**.

## What each server refuses

The API answers filters, ordering, pagination and joins that the client chooses. Measured against
1,064,991 rows in PostgreSQL and 250,000 in the other two:

| What a client asks | PostgreSQL | CouchDB | Cassandra |
|---|---|---|---|
| filter, order, page 2 of 100 | 43 ms, no index added | 776 ms for the first answer while it builds the index, then 15 to 18 ms; the index adds **15 MB to 11 MB of data** | **refused**: `SKIP` is a syntax error; the client carries a paging state |
| order by a column with no index | 42 ms | **refused**: `no_usable_index`, "try indexing by the sort fields" | **refused**: "Order by is currently only supported on the clustered columns of the PRIMARY KEY" |
| contains a piece of text, over the table | 53 ms | 345 ms over 25,000 rows | **refused**: "LIKE restriction is only supported on properly indexed columns" |
| a second access pattern | a second index or none | a second index (+15 MB) or a MapReduce view (**30 MB and 658 ms to build**, then 1 to 3 ms) | a second table with a copy of the data |
| filter without the partition key | 8 ms on a join of two tables | the Mango selector does it, by scan | **refused** unless `ALLOW FILTERING` |
| 200 rows and a counter in one transaction | one transaction | **no**: `_bulk_docs` answered 201 with one impossible document, and the other two were written | **no**: "Counter and non-counter mutations cannot exist in the same batch"; across partitions a batch is accepted and is not atomic |

The last line is the one that decides the question for the Archiver. ADR-0036 says that one import is
one transaction. Cassandra cannot do it above one partition. CouchDB cannot do it at all.

The index build is the other wall. On 250,000 documents CouchDB's first paged query hit
`fabric_worker_timeout` after 60 seconds, set the memory alarm, and the container was killed with exit
137 at an 8 GB limit. On 25,000 documents the same query was 776 ms. PostgreSQL answered the same shape
over 1,064,991 rows in 43 ms.

## Why the disk grows, measured

This is the real answer to "the disk will hurt fast". It is not the engine and it is not the document.

Between SPT 4.0.13 and SPT 4.1.6 the archive holds both releases in full:

| Table | 4.0.13 | 4.1.6 | Identical in both |
|---|---|---|---|
| `locale_entries` | 528,577 rows | 536,414 rows | **524,302 (97.7%)**; 4,139 changed value and 7,973 are new |
| `items` | 4,582 rows | 4,673 rows | 0 identical rows, but every one of the 4,582 matched items changed only by a few added paths (`PlayFuzeSound`, `effects_damage`, `effects_health`) |

`locale_entries` is 448 MB of the 609 MB of the development archive: 73 percent of it. A second release
costs another 224 MB, and almost all of that is a second copy of text that did not change.

The Archiver already does this correctly for mods: ADR-0029 makes a dataset the delta of a run against
its baseline, and the content hash means that an unchanged file is stored once. Official releases do not
get that treatment. Each one is a full copy.

**Ten releases at this rate cost about 2.2 GB of locale text, of which about 2.1 GB is a copy.**
Content addressing, or a delta against the previous release, would take that to tens of megabytes. No
engine does that for us; it is a model change in `SptArchive.Core`.

## Verdict

**Keep PostgreSQL. Do not move to Cassandra, ScyllaDB or CouchDB.**

1. No engine stores versions better. All three write twenty full copies for twenty versions, measured.
2. Storage per row and per document is a wash. PostgreSQL is within one percent of CouchDB on the 132 MB
   document and cheaper than CouchDB per locale row.
3. The API is a view over client-chosen filters, ordering, pagination and joins. Cassandra refuses four
   of those at the parser. CouchDB refuses one and bills an index for another.
4. The Archiver needs one transaction per import (ADR-0036). Two of the three engines cannot provide it.
5. The document read is the case for PostgreSQL, not against it: 36 ms against 2 seconds and 10 seconds.
6. CouchDB's first paged query over 250,000 documents killed an 8 GB container. PostgreSQL answers the
   same shape over a million rows in 43 ms.

`bson` through DocumentDB was examined separately in `docs/research/document-store.md` and does not
change this: BSON is a `varlena` too, so TOAST compresses it the same way, and it repeats every key name.

**What to do instead, in order:** (a) lz4 on every jsonb column, done in the migration
`SetToastCompressionLz4`; (b) remove the duplicate copies of official locale text, which is TD-71 and
the real disk answer; (c) the path catalogue of TD-68 so that a filter does not read every document.

## How to repeat these measurements

The scripts are `/tmp/dbcompare/couchdb/probe.py`, `/tmp/dbcompare/couchdb/probe3.py` and
`/tmp/dbcompare/cassandra/probe.py`. Each starts its own container, loads the fixtures and prints the
numbers above. The PostgreSQL numbers come from `psql` statements against the development database and
from the fixture loaded with `COPY`.
