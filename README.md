# SPT Data Archive

A Postgres archive of the data model behind the SPT server (`sp-tushonka/server-csharp`) and the mods that extend it,
with a public, read-only GraphQL API in front of it.

The server keeps its database as JSON files (an artefact of the upstream game using MongoDB). This project
normalizes those files into relational tables, keeps **official** data and **mod** data in separate tables,
and serves them fast.

> **Status: scaffold, runs end to end.** `dotnet build` passes with no warnings and `dotnet test` passes
> 236 tests. Seven migrations are applied and two real releases are archived: 4.0.13 (4,582 items, 677 clothes)
> and 4.1.6 (4,673 items, 536,414 translations, 558 quests, 12 traders, 10,880 assort rows, 728 clothes).
> The 4.1.6 data comes from an installation whose mods folder held nothing but the exporter, and the Archiver
> refuses any other source for official data (ADR-0035).
> The Api served the archive read-only. A container run exports every table of the server database
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

docker compose up -d db redis api
docker compose run --rm archiver       # re-run on a schedule; add --Archive__Force=true to re-import unchanged data
```

The API is then at `http://localhost:8080/graphql`. Example:

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
- **Backups.** Take a `pg_dump -Fc` after each successful run and keep it off the host:
  `docker compose exec -T db pg_dump -U archive_owner -Fc archive > archive-$(date +%F).dump`
- **Limits.** Page size is capped at 200, queries at depth 10 and 10 seconds. Put the API behind a rate limiter
  (Cloudflare, Traefik) as well.

## Known gaps

- **No Gherkin runner yet.** 236 xUnit tests check the schema, the import path, the canonical form and the
  run import, but no Reqnroll binding exists,
  so every scenario in `docs/specs` is still a claim (TD-06).
- **The Api has no server version argument.** The schema holds one row set per release, and two releases are
  imported (4.0.13 and 4.1.6), so an official query without a version mixes them (TD-21).
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
