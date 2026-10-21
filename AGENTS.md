# AGENTS.md

Instructions for AI coding agents and for new developers. Claude Code, Codex, Cursor, Copilot and other agents read this file.

## Project

SPT Data Archive stores the data model of the SPT server (`sp-tushonka/server-csharp`) in PostgreSQL. It keeps official data and mod data in separate tables. A public read-only GraphQL API serves the data.

The code compiles (TD-01 is done). Seven migrations exist and are applied, and 280 tests pass against a real Postgres (TD-02, and the first half of TD-06). No Gherkin scenario runs as a test yet, and the Api has no server version argument (TD-06, TD-21).

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
compose.yaml               PostgreSQL, Redis, Api and the Archiver.
```

## Commands

`dotnet build` passes with no warnings. The other commands have not been run against a real environment.

```bash
dotnet build SptArchive.slnx
dotnet test SptArchive.slnx             # 280 tests; needs the development database (below)

# The development database, with no .env file. Loopback only. "down -v" deletes the data.
podman compose -f docker/compose.yml up -d

# Migration. The design-time factory reads ARCHIVE_CONNECTION, so no application has to start.
export ARCHIVE_CONNECTION="Host=127.0.0.1;Port=5432;Database=archive;Username=archive_owner;Password=dev_owner_password"
dotnet ef migrations add <Name> --project src/SptArchive.Core --startup-project src/SptArchive.Archiver
dotnet ef database update --project src/SptArchive.Core --startup-project src/SptArchive.Archiver

docker compose up -d db redis api
docker compose run --rm archiver        # one archive run; set Archive__SptVersion=4.0.13

python3 scripts/check-docs.py           # needs: pip install gherkin-official
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

- `dotnet build` passes with no warnings you introduced.
- Tests for the slice pass. No scenario of the slice has the tag `@unverified` or `@backlog`.
- `python3 scripts/check-docs.py` passes.
- `npx likec4 validate docs/architecture/c4` passes.
- Docs match the code.

## Current state

- **Done:** the solution builds, seven migrations are applied, and 280 tests pass (TD-01, TD-02, TD-04, TD-05,
  TD-07, TD-15, TD-21, TD-29, TD-39, TD-40, TD-44, TD-50, TD-51, TD-53, TD-54).
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
  columns are `Any` values, so a client reads JSON as JSON (TD-15), and such a column offers comparison only,
  because the text operations would become `LIKE` against jsonb (TD-52). A request is bounded by 1 MB, depth
  10, 10 seconds and a cost limit that serves nine full pages and refuses eleven.
- **The Api holds its measured load.** With Redis, a page of 200 items with all 11 fields answers in 1 ms
  from the cache and in 35 ms from Postgres, and 100 concurrent clients get 2,723 answers per second at
  p99 56 ms (ADR-0008). `tests/load` holds the k6 scripts: a hot answer at 100 requests per second answers at
  p95 11 ms, 200 clients that miss one answer together run one query (ADR-0037), and an answer expires after
  60 to 65 seconds. A cache that is down is skipped for 30 seconds, so it costs one slow request and not every
  request. The Api asks Postgres for at most 30 connections (`Database:MaxPoolSize`), below the 50 that
  `compose.yaml` allows: 60 concurrent distinct queries all answered and the role never held more than 30
  connections (TD-53 is done).
- **The edge is guarded.** A token bucket per client address allows a burst of 100 and then 10 requests per
  second, and answers `429` with `Retry-After` and the code `RATE_LIMITED` beyond that; `/healthz` is never
  charged. A bucket map is bounded, and only `/graphql` costs a token (ADR-0038). Answers are compressed with
  brotli or gzip, and the compression sits outside the cache, so one uncompressed answer is stored for every
  client: a 514 KB page leaves as 109 KB with gzip and 59 KB with brotli. Every answer carries `nosniff`,
  `no-referrer` and `X-Frame-Options: DENY`. A limit that a whole deployment shares is still the proxy's job.
- **Backups.** `scripts/backup.sh dump | verify FILE | restore FILE DBNAME` writes a custom-format dump with
  a checksum, proves that it restores, and prunes old files. The dump is compressed, gzip level 6 or higher,
  and the script refuses a lower level (ADR-0011). `docs/runbooks/backup.md` is the runbook. Nothing runs it
  on a schedule yet (TD-03).
- **Next slice:** the Api surface for the mod tables (ADR-0032 action item 7), so a client reads a dataset, its
  change paths and its files. Then the Gherkin scenarios with Reqnroll (TD-06, ADR-0012), the rows of a
  location and a bot type (TD-45), JSON filtering by path (TD-52) and the baseline cache.
- **Open decisions:** ADR-0038 (a rate limit, compression and the pool cap at the Api) is Proposed and awaits
  Yui. ADR-0012 (Reqnroll for Gherkin), ADR-0030 (one .NET tool for run, export and import), ADR-0036 (several
  imports at the same time) and ADR-0037 (one query per answer, an idempotency key) are Accepted. ADR-0005 and
  ADR-0010 are superseded; ADR-0001 to 0004, 0006 to 0009, 0011 to 0029 and 0031 to 0037 are Accepted.
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
