# SPT Data Archive

A Postgres archive of the data model behind the SPT server (`sp-tushonka/server-csharp`) and the mods that extend it,
with a public, read-only GraphQL API in front of it.

The server keeps its database as JSON files (an artefact of the upstream game using MongoDB). This project
normalizes those files into relational tables, keeps **official** data and **mod** data in separate tables,
and serves them fast.

> **Status: scaffold, runs end to end.** `dotnet build` passes with no warnings and `dotnet test` passes
> 272 tests. Seven migrations are applied and two real releases are archived: 4.0.13 (4,582 items, 677 clothes)
> and 4.1.6 (4,673 items, 536,414 translations, 558 quests, 12 traders, 10,880 assort rows, 728 clothes).
> The 4.1.6 data comes from an installation whose mods folder held nothing but the exporter, and the Archiver
> refuses any other source for official data (ADR-0035).
> The Api serves the archive read-only, one release at a time: every official field takes an optional
> `sptVersion` (ADR-0014, ADR-0019), and a jsonb column reaches the client as JSON (TD-15). A container run exports every table of the server database
> (642 MB for 4.1.6, nineteen real mod runs, see `docs/runbooks/mod-export.md`), and the Archiver now reads a
> run directory against the baseline of its release into a dataset: ECOTI 9 added items and 18 added offers in
> 27 templates; WTT-Armory 2,434 templates with 1,294 added and 461 changed items; Gunwright a dataset with no
> row, because its whole contribution is two HTTP routes (ADR-0028, ADR-0029, ADR-0031). Several imports may
> run at the same time against one archive (ADR-0036). No Gherkin scenario
> runs as a test yet and the Api has no server version argument (see [Known gaps](#known-gaps)).

## Documentation

Start with [AGENTS.md](AGENTS.md) (instructions for AI agents and new developers) and the [documentation index](docs/README.md).

| What | Where |
|------|-------|
| How it works, on one page | [docs/architecture/overview.md](docs/architecture/overview.md) |
| C4 diagrams (LikeC4 model and rendered diagrams) | [docs/architecture/c4/](docs/architecture/c4/), [diagrams.md](docs/architecture/diagrams.md) |
| Decisions, with Gherkin verification | [docs/adr/](docs/adr/README.md) |
| Behavior as Gherkin scenarios | [docs/specs/](docs/specs/README.md) |
| Known problems and the fix order | [docs/tech-debt.md](docs/tech-debt.md) |

## Layout

```
src/
  SptArchive.Core      Entities, EF Core DbContext, migrations. Shared by the other two projects.
  SptArchive.Archiver  Run-to-completion job: reads the server's JSON, writes Postgres, exits.
  SptArchive.Api       Thin read-only GraphQL API (HotChocolate) + Redis response cache.
  SptArchive.Exporter  Mod for the server container: dumps every table as a database directory, plus `run/` (ADR-0027, ADR-0029).
                       Not in the solution; it builds against an SPT install.
docs/                  ADRs, C4 model, Gherkin specs, tech-debt register, glossary.
docker/initdb          Creates the read-only Postgres role used by the API.
docker/compose.yml     Throwaway Postgres for local work. Not the deployment stack.
scripts/check-docs.py  Checks Gherkin syntax, links and ADR structure.
compose.yaml           Postgres, Redis, Api and (on demand) the Archiver.
```

## Schema

Every official row states its server release in `spt_version`, and that column is the first part of every
official key. The archive keeps every release (ADR-0014).

Every `mod_*` row points at a mod dataset, not at a mod version, because two versions with the same content
share one dataset (ADR-0013).

| Official                | Mods                        | Key                                                                    |
|-------------------------|-----------------------------|------------------------------------------------------------------------|
| `items`                 | `mod_items`                 | `(spt_version, id)` / `(dataset_id, id)`                               |
| `customizations`        | `mod_customizations`        | `(spt_version, id)` / `(dataset_id, id)`                               |
| `locale_entries`        | `mod_locale_entries`        | `(spt_version, scope, language_code, key)` / `(dataset_id, ...)`       |
| `quests`                | `mod_quests`                | `(spt_version, id)` / `(dataset_id, id)`                               |
| `traders`               | `mod_traders`               | `(spt_version, id)` / `(dataset_id, id)`                               |
| `trader_assort_items`   | `mod_trader_assort_items`   | `(spt_version, trader_id, instance_id)` / `(dataset_id, ...)`          |
| `trader_quest_assorts`  | `mod_trader_quest_assorts`  | `(spt_version, trader_id, status, assort_instance_id)` / `(dataset_id, ...)` |

Support tables: `spt_versions` (the releases the archive holds), `mods` (registered mod versions),
`mod_datasets` (the content of one mod), `mod_data_observations` (one load attempt per mod version and
server release, with its outcome), `mod_templates` and `mod_change_paths` (what a dataset changes in
official data), `mod_files` (the manifest of every file a dataset loaded, with the text of its configuration
files, ADR-0029), `languages`, and `archive_runs` (one row per Archiver execution, with the source hash and
the release).

Design decisions:

- **Hybrid columns + jsonb.** An item's `_props` has hundreds of optional fields that differ per item type, so
  `items` has real columns for what people filter on (`weight`, `width`, `height`, `stack_max_size`, `parent_id`, ...)
  and the full object in a GIN-indexed `props` jsonb column. Quests and traders work the same way. Nothing is lost.
- **Locales are flat rows.** Keys such as `"5422acb9af1c889c16000029 Name"` are split into `entity_id` + `field`,
  so translations join to any entity.
- **Mods can add stock to official traders**, so mod assort rows reference a trader id without a foreign key.
- **Assort offers are folded into one row per item**: the server splits them across `items`, `barter_scheme` and
  `loyal_level_items`; here a root offer carries its own barter scheme and loyalty level.

## How data gets in

**Official data**: the Archiver reads `SPT_Data/database` directly, so the server never has to run and no
memory is spent loading it. The run states its server release in `Archive__SptVersion`, and replaces only the
rows of that release. The whole run is one transaction, so readers keep seeing the previous release until the
new one commits, and a failure leaves the archive untouched. If no source file changed since the last successful
run of the same release, the job exits immediately.

**Mod data**: one mod version with its pinned dependencies runs in a throwaway server container, an exporter mod
writes the final in-memory database to a directory that has the layout of `SPT_Data/database` (ADR-0027), and the
difference from a baseline run is the dataset of that mod version. The export also holds `run/`: the mods that
loaded, the manifest of every file with its SHA-256, the text of every configuration file and every HTTP route it
answers (ADR-0029, ADR-0031). The Archiver reads that against the baseline of the same release:

```bash
Archive__RunPath=/exports/ECOTI \
Archive__RunModGuid=com.lennoxp90.coti.server \
Archive__BaselinePath=/exports/baseline \
dotnet run --project src/SptArchive.Archiver
```

A run names its own release, so `Archive__SptVersion` is not needed. Two runs of the same content share one
dataset; a second import of the same run exits immediately. The baseline cache and the catalog are not built.

Importers implement `IImporter` and are registered in `Archiver/Program.cs`; adding hideout, bots, locations or mods
means adding an importer, not changing the pipeline.

## Running it

To work on the schema without the full stack, start the development database only. It needs
no `.env` and it binds to loopback:

```bash
podman compose -f docker/compose.yml up -d     # docker works too; replace "podman" with "docker"
podman compose -f docker/compose.yml down -v   # -v also deletes the data
```

It creates `archive` with the owner `archive_owner` and the read-only `archive_reader`, and it
publishes port 5432 on `127.0.0.1`. Passwords are development defaults. Do not use this file in
a deployment.

For real data, use the full stack:

```bash
cp .env.example .env                  # fill in the passwords and SPT_DATA_PATH
# In the server repository, fetch the Git LFS files first (items.json and the loose loot files):
#   git lfs pull

# Apply the migration, then import one release (the server version is not in its database files):
export ARCHIVE_CONNECTION="Host=127.0.0.1;Port=5432;Database=archive;Username=archive_owner;Password=dev_owner_password"
dotnet ef database update --project src/SptArchive.Core --startup-project src/SptArchive.Archiver
dotnet test SptArchive.slnx          # uses the same database; every test rolls its work back

export ConnectionStrings__Archive="$ARCHIVE_CONNECTION"
export Archive__DatabasePath="$HOME/Documents/repos/tarkov/server-csharp/Libraries/SPTarkov.Server.Assets/SPT_Data/database"
export Archive__SptVersion=4.0.13
dotnet run --project src/SptArchive.Archiver

docker compose up -d db redis api caddy
docker compose run --rm archiver       # re-run on a schedule; add --Archive__Force=true to re-import unchanged data
```

The API is then at `https://localhost:8443/graphql`, behind the proxy that terminates TLS (ADR-0039). The
Api has no published port of its own, so nothing reaches it without the proxy. The certificate comes from
the local authority of Caddy: install it once
(`podman cp $(podman ps -qf name=caddy):/data/caddy/pki/authorities/local/root.crt ./caddy-root.crt`) and
pass it to a client with `--cacert ./caddy-root.crt --resolve api.sptarchive.local:8443:127.0.0.1`, or
answer the name `api.sptarchive.local` from `/etc/hosts`. Example:

```graphql
{
  items(where: { name: { startsWith: "weapon_" } }, first: 10) {
    totalCount
    nodes { id name weight width height }
  }
  translations(where: { entityId: { eq: "5422acb9af1c889c16000029" }, languageCode: { eq: "en" } }) {
    nodes { field value }
  }
}
```

## Operations

- **Read-only by construction.** The API has no mutations, uses no-tracking queries, and connects as `archive_reader`,
  a Postgres role that only has `SELECT`.
- **Caching.** Whole GraphQL responses are cached in Redis, keyed by the request body plus a dataset version that the
  Archiver changes after every run, so there is nothing to invalidate. Redis is capped at 128 MB with LRU eviction.
  Because the API is anonymous, identical requests always have identical answers; revisit this before adding auth.
  Measured on the development archive (ADR-0008): a page of 200 items with all 11 fields, 518 KB, answers in 1 ms
  from the cache and in 35 ms from Postgres, and 100 concurrent clients get 2,723 answers per second at p99 56 ms.
  A Redis that is down is skipped for 30 seconds after a failure, so it costs one slow request (3.8 s) and not
  every request. An answer lives one minute, spread by 10 % so answers stored together do not expire together.
  The requests that miss one answer at the same time share a single query, and the header `X-Cache` says
  `SHARED` for them (ADR-0037). A client may send an `Idempotency-Key` to replay a request later (ADR-0037),
  and an answer above 2 MB is answered but never stored.
- **A guard in front of the Api.** Each client address gets a token bucket: a burst of 100 requests, refilled at
  100 per 10 seconds (ADR-0038). A client that asks more gets `429` with `Retry-After` and the code
  `RATE_LIMITED`; `/healthz` is never charged. Answers are compressed with brotli or gzip when the client asks
  for it, and the compression sits outside the cache, so one uncompressed answer is stored for everyone.
  Every answer carries `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and
  `X-Frame-Options: DENY`. The Api asks Postgres for at most 16 connections, below the 50 that
  `compose.yaml` allows, so a burst waits instead of failing; a request waits at most 5 seconds for one.
  Behind the proxy the limit charges the client, not the proxy: the Api believes `X-Forwarded-For` from the
  one address of Caddy and from no other (ADR-0039).
  To try the cache locally, start one: `podman run -d --rm --name sptarchive-redis -p
  127.0.0.1:6379:6379 docker.io/library/redis:7-alpine redis-server --maxmemory 128mb --maxmemory-policy
  allkeys-lru --save "" --appendonly no`, and set `ConnectionStrings__Redis=127.0.0.1:6379` for both the Api and
  the Archiver.
- **Load tests.** `tests/load` holds four k6 scripts: `steady.js` (a hot answer at a fixed rate),
  `stampede.js` (200 clients that miss one answer together), `ttl.js` (when an answer expires) and
  `capacity.js` (how many clients the stack holds). `tests/load/README.md` explains the measured stack, which
  runs each container at 4 vCPU and 2 GB. A load test must raise the rate limit first
  (`RATELIMIT_ENABLED=false`), or it measures the limiter instead of the Api.
- **Capacity.** Measured on 2026-10-21 against the containers of `compose.yaml`:
  [docs/load/capacity-2026-10-21.html](docs/load/capacity-2026-10-21.html). The Api holds 6 000 clients — one
  request per second each, 5 999 answers per second of 53 KB — at p95 8.1 ms and p99 15.7 ms, with 2.7 of its
  4 cores and one core of PostgreSQL. Above that the generator, not the server, runs out of memory. The page
  also says where the time goes: 80 % of the requests are cache hits at a few milliseconds, one SQL statement
  holds 93 % of the database time, and the proxy costs a second core for TLS and its access log.
- **Backups.** `scripts/backup.sh dump` writes a `pg_dump -Fc` with a checksum, copies it to
  `ARCHIVE_BACKUP_COPY_DIR` and proves that it restores (`scripts/backup.sh verify`). Keep the copy off the
  host. See `docs/runbooks/backup.md` (ADR-0011, TD-03 for the missing schedule).
- **Limits.** Page size is capped at 200, a request body at 1 MB, and a query at depth 10, 10 seconds and a
  cost limit of 20,000 field cost and 2,000 objects — nine full pages of rows with every field. A JSON column
  offers comparison only. The Api keeps its own rate limit per client (ADR-0038) and TLS ends at the proxy in
  front of it (ADR-0039); a limit that a whole deployment shares still belongs to the edge in front of that
  (Pangolin in production).

## Known gaps

- **No Gherkin runner yet.** 297 xUnit tests check the schema, the import path, the canonical form and the
  run import, but no Reqnroll binding exists,
  so every scenario in `docs/specs` is still a claim (TD-06).
- **Mod ingestion is half built.** One run directory becomes a dataset, an observation, its offer rows and one
  `archive_runs` row, and the canonical comparison is the input to the hash (TD-29, ADR-0033). The baseline cache
  and the catalog are still action items of ADR-0013, and a change to a map, a bot type or a price is hashed but
  stored as no row (TD-45).
- **`items.json` is stored in Git LFS** upstream. The Archiver stops with a clear message if it finds the pointer
  file instead of the data.
- **Not imported yet**: bots, locations, globals, hideout, settings, match, server, handbook, prices,
  achievements, prestige, dialogue, the `menu`/`server`/`web` locale folders, the character template, and the
  side-specific trader suits. [ADR-0024](docs/adr/0024-archive-the-whole-server-database.md) decides that every
  server table is imported; TD-32 is the work.
- **No faction exists in the data.** A bot type is a file name, and the only faction vocabulary is the
  `ScavRole/*` locale keys, which name roles. Factions need a curated map
  ([ADR-0025](docs/adr/0025-resolve-a-faction-from-locale-and-curation.md), TD-33).
- **Client mods are not run.** Skill Effects Extended and EFT Effect Manager are BepInEx plugins, and ISB Special
  Forces ships 270 files with no server part. They get catalog rows and no dataset (ADR-0023).
- **An offer, an item, a quest, a customization and a trader are rows.** An assort file is read as offers, and an
  offer is named by its trader, its item and what the player pays with (ADR-0032, ADR-0034 decision 8).
- **A change to a table without a `mod_*` row is not stored.** A location, a bot type, a price and a handbook file
  leave no row; the canonical hash of the changed file keeps two datasets apart (TD-45).
- **JSON columns are exposed as JSON text** in GraphQL (`props`, `data`, `base`, ...), not as a native JSON scalar.
