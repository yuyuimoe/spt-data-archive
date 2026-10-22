# AGENTS.md

Instructions for AI coding agents and for new developers. Claude Code, Codex, Cursor, Copilot and other agents read this file.

## Project

SPT Data Archive stores the data model of the SPT server (`sp-tushonka/server-csharp`) in PostgreSQL. It keeps official data and mod data in separate tables. A public read-only GraphQL API serves the data.

The code compiles (TD-01 is done). Ten migrations exist and are applied, and 410 tests pass against a real Postgres: 388 xUnit tests and the 22 Gherkin scenarios of `mod-api.feature`, which Reqnroll runs (TD-02, TD-06). The other feature files are not bound yet (TD-62).

## Read first

Read these files in this order before you change anything:

1. [docs/README.md](docs/README.md): the index of all documentation.
2. [docs/architecture/overview.md](docs/architecture/overview.md): how the system works.
3. [docs/adr/README.md](docs/adr/README.md): the decisions and their status. Check the status before you build on a decision.
4. [docs/specs/](docs/specs/README.md): the Gherkin specs. They define the behavior.
5. [docs/tech-debt.md](docs/tech-debt.md): known problems and the order to fix them.
6. [docs/glossary.md](docs/glossary.md): the words to use.

## Repository map

```
src/SptArchive.Core        Entities, ArchiveDbContext, migrations, cache keys. Shared.
src/SptArchive.Archiver    Job: reads the server JSON, writes PostgreSQL, exits.
src/SptArchive.Api         Read-only GraphQL API (HotChocolate) and the query cache.
docs/adr                   Architecture decision records, with Gherkin verification.
docs/architecture/c4       The C4 model in LikeC4 (.c4 files).
docs/specs                 Gherkin feature files.
docker/initdb              Creates the read-only database role.
docker/compose.yml         Throwaway Postgres for local work (podman or docker). Not the deployment stack.
scripts/check-docs.py      Checks Gherkin syntax, links and ADR structure.
scripts/ci.sh              Runs every check below, in order. The one command that proves a branch is clean.
scripts/requirements.txt   Python packages of check-docs.py (gherkin-official).
.github/workflows          The pipeline: it calls scripts/ci.sh on a push or a pull request.
compose.yaml               PostgreSQL, Redis, Api, Archiver and Caddy (the one public door).
```

## Commands

`dotnet build` passes with no warnings. The other commands have not been run against a real environment.

```bash
# Everything the repository checks, in order, stopping at the first failure (TD-09).
# It needs a throwaway PostgreSQL 17 with the roles of docker/initdb, and it will not guess one:
# it stops if the two variables are unset. Never point them at a database with archived data.
export ARCHIVE_TEST_CONNECTION="Host=127.0.0.1;Port=5432;Database=archive;Username=archive_owner;Password=dev_owner_password"
export ARCHIVE_TEST_READER_CONNECTION="Host=127.0.0.1;Port=5432;Database=archive;Username=archive_reader;Password=dev_reader_password"
scripts/ci.sh

# The docs check needs gherkin-official. ci.sh builds .venv from scripts/requirements.txt when
# python3 cannot import it. To do it by hand (a system Python may be externally managed, PEP 668):
python3 -m venv .venv && .venv/bin/pip install -r scripts/requirements.txt

# The same checks, one by one:
dotnet build SptArchive.slnx
dotnet test SptArchive.slnx             # 406 tests (22 are scenarios); needs the development database (below)

# The development database, with no .env file. Loopback only. "down -v" deletes the data.
podman compose -f docker/compose.yml up -d

# Migration. The design-time factory reads ARCHIVE_CONNECTION, so no application has to start.
export ARCHIVE_CONNECTION="Host=127.0.0.1;Port=5432;Database=archive;Username=archive_owner;Password=dev_owner_password"
dotnet ef migrations add <Name> --project src/SptArchive.Core --startup-project src/SptArchive.Archiver
dotnet ef database update --project src/SptArchive.Core --startup-project src/SptArchive.Archiver

docker compose up -d db redis api caddy
docker compose run --rm archiver        # one archive run; set Archive__SptVersion=4.0.13

python3 scripts/check-docs.py           # needs gherkin-official: see scripts/requirements.txt
npx likec4 validate docs/architecture/c4
```

## Rules

1. **The Api never writes.** It has no mutations. It connects with a role that can only read. Do not add a write path to the Api.
2. **Official data and mod data stay apart.** Each dataset has an official table and a `mod_*` table. Do not put mod rows in an official table (ADR-0003).
3. **The Archiver owns the official tables.** Only the Archiver deletes or writes them.
4. **Do not lose data.** Keep the full source object in `jsonb`. A promoted column is an extra, not a replacement (ADR-0002).
5. **Respect the ADR status.** An *Accepted* ADR is a decision. Do not change it. A *Proposed* ADR needs the driver's confirmation. A *Proposed (decision needed)* ADR has no decision. Ask the driver. Do not guess.
6. **Write a new ADR** for any change that affects more than one component, the data model or an external system.
7. **Specs are the tests.** Do not change a scenario to make a test pass. Ask the driver first.
8. **Keep the C4 model current.** Change `docs/architecture/c4` in the same change as the structure.
9. **Write code comments in English.** The team is international.
10. **Write docs in Simplified Technical English.** Use short sentences, active voice and simple words. Use one word for one thing.
11. **Do not commit secrets.** `.env` is ignored. Use `.env.example` for new settings.
12. **Do not claim that something works unless you ran it.** Say what you ran and what you saw.

## Workflow

Work on **one slice at a time**. A slice goes from spec to design to code:

1. Write or update the Gherkin scenarios in `docs/specs`. Tag all scenarios outside the slice with `@backlog`.
2. Place the slice in the C4 model. Write an ADR if the slice needs a decision.
3. Write the failing test first, then the code, then refactor.
4. Update the tags, the C4 files, the ADR index and `docs/tech-debt.md`.
5. Run the checks in the next section.

Stop and ask the driver when a step needs a decision. Do not skip a step without an explicit yes from the driver.

## Definition of done

- `scripts/ci.sh` passes. It runs the four checks below in one command. A check it does not cover is a gap to report.
- `dotnet build` passes with no warnings you introduced.
- Tests for the slice pass. No scenario of the slice has the tag `@unverified` or `@backlog`.
- `python3 scripts/check-docs.py` passes.
- `npx likec4 validate docs/architecture/c4` passes.
- Docs match the code.

## Current state

- **CI is a script, and the pipeline runs it.** `scripts/ci.sh` runs the build, the tests, `check-docs.py` and
  `likec4 validate`, and stops at the first failure (TD-09). `.github/workflows/ci.yml` calls it on a `postgres:17`
  service container; the run of `02a976c` on `github.com/yuyuimoe/spt-data-archive` passed every step in 89 seconds
  (run `39647853715`, TD-60). On an empty database every step passes and every test passes, so a clean machine and
  an archive machine agree.
- **One feature file runs as a test.** `Reqnroll.xunit.v3` 3.3.4 runs the 19 scenarios of `docs/specs/mod-api.feature`
  inside `dotnet test` (ADR-0012, TD-06). The other five feature files stay unbound and keep their tags (TD-62).
- **Done:** the solution builds, ten migrations are applied, and 410 tests pass (TD-01, TD-02, TD-04, TD-05,
  TD-06, TD-07, TD-15, TD-21, TD-29, TD-39, TD-40, TD-44, TD-45, TD-50, TD-51, TD-52, TD-53, TD-54, TD-55), 384 xUnit
  tests and 22 Gherkin scenarios.
  The exporter dumps every table of a run in the layout of the server database (TD-32, ADR-0027) and `run/`:
  the loaded mods, the manifest of every file, the text of every configuration file and every HTTP route
  (ADR-0029, ADR-0031). The Archiver reads a run directory against the baseline of its release into
  `mod_datasets`, `mod_data_observations`, `mod_templates`, `mod_change_paths`, `mod_files`, `mod_id_maps`, the
  `mod_*` rows (TD-29) and one `archive_runs` row for every execution, with its kind, purpose, target, baseline and
  result (ADR-0033). Every row is keyed by the address the archive derived for the object, and the id map keeps
  the id the run gave it (ADR-0034). Official data comes only from an installation that loaded no mod, and a run
  waits for the official data of its release (ADR-0035). Several imports may run at the same time: a run holds a
  lease, and a transaction-scoped advisory lock covers what it writes (ADR-0036).
- **The Api answers one release at a time.** Every official field takes an optional `sptVersion`, and an
  answer comes from the latest archived release when the argument is left out (ADR-0014, ADR-0019). A bad
  value is an error with its own code and never an empty list: `SPT_VERSION_UNKNOWN`,
  `SPT_VERSION_MALFORMED`, and `SPT_ARCHIVE_EMPTY` when the archive holds no release at all. The jsonb
  columns are `Any` values, so a client reads JSON as JSON (TD-15). A client filters such a column whole
  (`eq`, `neq`, as JSON text) or by the value at a path: `props: { pathEq: { path: ["Slots"], value: 5 } }`
  (ADR-0040, TD-52). The path filter becomes `jsonb_extract_path(doc, segment, ...) = value` with every segment
  and the value as a parameter, and a column offers no text matching, because that would become `LIKE` against
  jsonb. A bad path or value is an error with its own code and never an empty list: `JSON_PATH_MALFORMED`,
  `JSON_VALUE_MALFORMED` and `JSON_VALUE_REJECTED` (TD-55). A path filter has no index and no timing yet
  (TD-64). A request is bounded by 1 MB, depth 10, 10 seconds and a cost limit that serves nine full pages and
  refuses eleven.
- **The Api serves the rows that have no `mod_*` table of their own.** `modDatabaseObjects` and
  `modDatabaseObject` serve one row of `mod_database_objects` — a location, a bot type, a hideout part, a price
  or a handbook entry that a dataset adds (ADR-0041) — with the small facts as fields, `pathEq` over the
  document and the whole document as `rawJson`. A row states its size in `bytes`, and the Api decides with it
  before it reads anything: a document above `RawJson:MaxValueBytes` (2 MB) answers `RAW_JSON_TOO_LARGE`, and
  one request carries at most `RawJson:MaxRequestBytes` (4 MB) of whole documents, or
  `RAW_JSON_REQUEST_LIMIT`. The cost limit charges `rawJson` 100 times a small field, so a page of 200 whole
  documents is refused before it runs (ADR-0040, amendment 1). Measured: PostgreSQL compresses a jsonb value by
  default, 132 MB to 45 MB, but a read assembles 175 MB in 138 ms, which is why the refusal comes first. A row of a map is one file of the map, and for a loose-loot file it is one spawn
  point, named by the `template.Id` that the map author wrote: its `part` reads `looseLoot/spawnpoints/<name>`
  (ADR-0043).
- **The Api reads a mod dataset.** `modDatasets` and `modDataset` serve the content of one mod version, with
  `modTemplates` (the operation of each touched object), `modChangePaths` (one row per changed value, with the
  old and the new value as JSON), `modFiles` and `modFile` (the manifest and the text of a configuration file),
  `modIdMaps` (the id of a run to the address the archive derived, ADR-0034) and `modDataObservations` (the
  releases a mod version ran on, ADR-0013). `archiveRuns` and `archiveRun` serve the history, newest first
  (ADR-0032 item 7, ADR-0033 item 5). A mod field takes no `sptVersion`: a dataset carries no release. Measured
  on the development archive on 2026-10-21: 5 datasets, ECOTI with 27 templates and 12,129 id map rows,
  WTT-Armory with 2,792 change paths, and 19 runs.
- **The Api holds its measured load.** With Redis, a page of 200 items with all 11 fields answers in 1 ms
  from the cache and in 35 ms from Postgres, and 100 concurrent clients get 2,723 answers per second at
  p99 56 ms (ADR-0008). `tests/load` holds the k6 scripts: a hot answer at 100 requests per second answers at
  p95 11 ms, 200 clients that miss one answer together run one query (ADR-0037), and an answer expires after
  60 to 65 seconds. A cache that is down is skipped for 30 seconds, so it costs one slow request and not every
  request. The Api asks Postgres for at most 16 connections (`Database:MaxPoolSize`), below the 50 that
  `compose.yaml` allows, and waits 5 seconds for one. The load test measured the pool as no constraint: at
  4 000 clients PostgreSQL held 1.2 connections busy on average and 4 at the most, and 8, 16 and 30 gave the
  same throughput (TD-53 is done, ADR-0038 amended on 2026-10-21).
- **The edge is guarded.** A token bucket per client address allows a burst of 100 and then 10 requests per
  second, and answers `429` with `Retry-After` and the code `RATE_LIMITED` beyond that; `/healthz` is never
  charged. A bucket map is bounded, and only `/graphql` costs a token (ADR-0038). Answers are compressed with
  brotli or gzip, and the compression sits outside the cache, so one uncompressed answer is stored for every
  client: a 514 KB page leaves as 109 KB with gzip and 59 KB with brotli. Every answer carries `nosniff`,
  `no-referrer` and `X-Frame-Options: DENY`.
- **TLS ends at the proxy.** `compose.yaml` runs Caddy, and it is the one public door: the Api publishes no
  port. Caddy signs its own certificate with a local authority on a machine with no public name, sets `HSTS`,
  and does not compress (ADR-0039). It costs CPU: 1.7 of its 4 cores at 6 000 clients, mostly TLS and the
  JSON access log (TD-56). Behind it the limit must charge the client and not the proxy, so the Api believes
  `X-Forwarded-For` from one address, the fixed address of Caddy, and walks the header from the right to the
  first address that is not a proxy. Trusting a whole network instead collapses every client into one bucket
  (TD-57). A limit that a whole deployment shares is still the job of the edge in front of Caddy.
- **The capacity is measured.** `docs/load/capacity-2026-10-21.html`: 6 000 clients, one request per second
  each, at p95 8.1 ms and p99 15.7 ms, with 2.7 of the Api's 4 cores and 0.9 of PostgreSQL's. Above that the
  generator of `tests/load` dies of its own 2 GB before the Api does, so the ceiling of the deployment is
  projected near 8 900 clients and not measured (TD-58).
- **Backups.** `scripts/backup.sh dump | verify FILE | restore FILE DBNAME` writes a custom-format dump with
  a checksum, proves that it restores, and prunes old files. The dump is compressed, gzip level 6 or higher,
  and the script refuses a lower level (ADR-0011). `docs/runbooks/backup.md` is the runbook. Nothing runs it
  on a schedule yet (TD-03).
- **Gherkin runs as tests.** Reqnroll (`Reqnroll.xunit.v3` 3.3.4, ADR-0012) runs the 22 scenarios of
  `docs/specs/mod-api.feature` inside `dotnet test`, from `tests/SptArchive.Tests/Specs`. The test project links
  each bound feature file by name, and a file that it does not link keeps its `@unverified` tags (TD-62).
- **Next slice:** the readers of the seven files that still hold no row (ADR-0044, accepted 2026-10-21,
  TD-76). Measured on 2026-10-21 in the baseline of 4.1.6: `globals.json` 704,308 B with 488 item presets,
  `templates/dialogue.json` 4,693,993 B with 53 elements, `templates/profiles.json` 1,078,871 B with 9
  editions, `settings.json` 345,329 B, `templates/achievements.json` 121,613 B with 79 objects, and two
  small lists. Four of the seven changed in the 19 exports of the development machine, in six copies of
  979,698 B, and `templates/dialogue.json` and `settings.json` changed in none of them. A stable address
  (TD-72) waits behind it: 3,499 of the 3,545 change paths are positional, so no address repeats and the
  pool of ADR-0042 pays nothing yet. ADR-0043 is Accepted, implemented and measured in ADR-0044: a
  loose-loot file is a directory of spawn points, and the ten files that `WTT-Armory` moves take
  548,447,155 bytes while its 36 spawn-point rows take 56,301 bytes (the largest is 3,583, which is 558
  times below the limit of `rawJson`). `FactoryClassic` replaces two maps and writes 404 rows of 4,623,791
  bytes and 1,050 removal paths of 19,394,570 bytes. The baseline cache and the catalogue of the paths
  inside a jsonb document (TD-68) follow. No map mod is imported since ADR-0041, so the dataset of one is a
  projection (TD-67), and a path filter over those rows reads every document of the table (TD-70). A dataset
  that the archive already holds keeps the rows it had, because a dataset is never rewritten (TD-75).
- **Storage is jsonb with lz4, and three engines were compared.** The migration `SetToastCompressionLz4` sets
  lz4 on the 27 jsonb columns and `compose.yaml` sets it for the columns a later migration adds
  (ADR-0041, ratified 2026-10-21). Measured against CouchDB 3.4.3 and Cassandra 5.0.9 on the same 132 MB map
  file: PostgreSQL stores it in 45,400,064 bytes against 45,080,614 and 48,010,591, reads one subfield in 36 ms
  against 2.0 s and 10.5 s, and is the only one of the three that answers an arbitrary filter, an order, an
  offset page and a join, and writes 200 rows and a counter in one transaction. Each of the three wrote exactly
  20.0 copies for 20 versions, so no engine deduplicates and TD-71 is the real disk cost: 97.7% of the locale
  entries of 4.1.6 are byte-identical to one of 4.0.13, and `locale_entries` is 448 MB of the 609 MB of the
  development archive. The measurements are in [docs/research/storage-engine.md](docs/research/storage-engine.md).
- **Open decisions:** ADR-0038 (a rate limit, compression and the pool cap at the Api), ADR-0039 (TLS at a
  proxy), ADR-0040 (a jsonb filter by path, the whole-document field and its limits; the form of `eq` stays
  open, TD-65) and ADR-0041 (a row for a change to a location, a bot type, a hideout part, a price and a
  handbook entry, and amendment 1, which refuses a copy of a file above the limit of the Api, TD-74) are
  Proposed and await Yui.
  ADR-0012 (Reqnroll for Gherkin) and ADR-0030 (one .NET tool for run, export and import) are Accepted.
  ADR-0036 (several imports at the same time) and ADR-0037 (one query per answer, an idempotency
  key) are Accepted. ADR-0042 (an address pool and a value pool behind the change rows) is Accepted, and its
  measurement says the pool pays from 1.6 change rows for each address: the archive holds one, so the
  storage waits for a stable address (TD-72). ADR-0043 (one row for each spawn point of a loose-loot file) is
  Accepted and implemented, ratified by Yui on 2026-10-21. ADR-0044 (one row for each object of every file
  that a run changes) and ADR-0045 (the link to the source of a file, which waits for exporter 1.6.0) are
  Accepted, ratified by Yui on 2026-10-21, and neither is implemented (TD-76). ADR-0005 and ADR-0010 are
  superseded; ADR-0001 to 0004, 0006 to 0009, 0011 to 0029 and 0031 to 0037 are Accepted.
- **Not built:** the baseline cache and the catalog (ADR-0013 action items). The container runner, the exporter
  and the importer exist and ran on nineteen real mod runs (`docs/runbooks/mod-export.md`), including a curated
  run of one mod (ADR-0029).
- **Environment:** NuGet is reachable and the packages are pinned. `items.json` is in Git LFS upstream; run
  `git lfs pull` in the server repository before you import. An **installation** names no release, so an
  official import must state it (`Archive__SptVersion`, TD-26), and its source must be an installation whose mods
  folder holds nothing but the exporter (ADR-0035). A **run** names its own: exporter 1.3.0 writes
  `run/release.json` from the server assembly, 1.4.0 `run/routes.json` from the dispatcher (ADR-0031) and 1.5.0
  `run/target.json` from `SPT_ARCHIVE_TARGET_MOD_GUID` (ADR-0033), and
  `Archive:BaselinePath` (`Archive__BaselinePath`) points at
  the baseline export that the run is compared against. Build the exporter against the assemblies of the
  release it runs on: a build for 4.1.6 is refused by 4.1.5.
