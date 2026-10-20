# Architecture overview

This page explains how the SPT Data Archive works. The C4 files in `c4/` are the source of truth for the structure. The ADRs in `../adr` explain the decisions.

## Goal

The archive keeps the data model of the SPT server in PostgreSQL. It keeps official data and mod data apart. It serves the data through a public read-only GraphQL API.

## Quality goals

| Goal | Meaning |
|------|---------|
| Fast reads | Common queries use indexes. The Api caches repeated queries. |
| Low memory | The Archiver reads one file at a time. Redis has a fixed memory limit. |
| Read-only safety | No public path can change the data. The database role enforces it. |
| Lossless archive | Every source property is kept, even when it has no column. |
| Easy to maintain | One language, one solution, thin layers. |

## Constraints

- The database must be open source. MongoDB is not allowed (ADR-0001).
- All code is C# on .NET 10 (ADR-0007).
- Code comments are in English, because the team is international.
- Official data and mod data stay in separate tables (ADR-0003).

## Context (C4 level 1)

Two kinds of people use the system. An **API consumer** reads data. A **maintainer** starts archive runs.

The system reads from the **SPT Server repository**. A planned **Mod sources** system will add mod data. A planned **Edge proxy** will limit the request rate in front of the Api.

See [diagrams.md](diagrams.md) and `c4/views.c4` (view `index`).

## Building blocks (C4 level 2)

| Container | Technology | Job |
|-----------|------------|-----|
| **Api** | ASP.NET Core, HotChocolate | Answers GraphQL queries. Has no mutations. Uses the read-only database role. |
| **Archiver** | .NET Worker Service | Reads the server JSON, replaces the official rows of one release and exits. Uses the owner role. |
| **PostgreSQL** | PostgreSQL 17 | Stores all data. |
| **Redis** | Redis 7 | Stores cached responses and the dataset version. |

The three .NET projects map to the code like this:

| Project | Contains |
|---------|----------|
| `src/SptArchive.Core` | Entities, `ArchiveDbContext`, database registration, cache keys. Both containers use it. |
| `src/SptArchive.Archiver` | `ArchiveJob`, importers, source helpers. |
| `src/SptArchive.Api` | The GraphQL `Query` type, the query cache middleware, startup. |

## Runtime

### Archive run

1. A maintainer or a scheduler starts the Archiver, and states the server release (`Archive__SptVersion`).
2. The Archiver hashes every JSON file of the source.
3. The Archiver stops if a succeeded run of the same release has the same hash.
4. The Archiver records a new run, with the release.
5. In one transaction, the Archiver registers the release in `spt_versions`.
6. Each importer deletes the official rows of that release and writes new rows in batches.
7. The Archiver commits, marks the run succeeded and publishes the dataset version to Redis.

If any step fails, the transaction rolls back and the run is marked failed. See view `archiveRun` and ADR-0009.

### Query

1. A consumer sends a `POST /graphql` request.
2. The cache middleware reads the dataset version. It looks up the response by the hash of the request body.
3. On a hit, the middleware returns the stored response. The header is `X-Cache: HIT`.
4. On a miss, HotChocolate runs the query through EF Core. The middleware stores the response if it has no errors.

See views `queryCacheHit` and `queryCacheMiss` and ADR-0008.

## Data model

Every dataset has an official table and a mod table with the same columns. See the table in the root `README.md` and ADR-0002 and ADR-0003.

| Dataset | Official table | Mod table |
|---------|----------------|-----------|
| Items | `items` | `mod_items` |
| Translations | `locale_entries` | `mod_locale_entries` |
| Quests | `quests` | `mod_quests` |
| Traders | `traders` | `mod_traders` |
| Trader offers | `trader_assort_items` | `mod_trader_assort_items` |
| Quest unlocks | `trader_quest_assorts` | `mod_trader_quest_assorts` |

Every official row states its server release in `spt_version`, and that column leads every official key
(ADR-0014). Every `mod_*` row points at `mod_datasets` instead of a mod version (ADR-0013).

Support tables: `spt_versions`, `mods`, `languages`, `archive_runs`, `mod_datasets`,
`mod_data_observations`, `mod_templates` and `mod_change_paths`.

## Cross-cutting concerns

**Security.** The Api connects as `archive_reader`. That role can only run `SELECT`. The Archiver connects as `archive_owner`. PostgreSQL and Redis do not publish ports to the host. CORS allows any origin for `GET` and `POST`. The Api has no authentication and no rate limit (TD-05).

**Configuration.** Both apps read `appsettings.json` and environment variables. Use `__` for nested keys, for example `ConnectionStrings__Archive`.

| Key | Used by | Meaning |
|-----|---------|---------|
| `ConnectionStrings:Archive` | Both | Npgsql connection string |
| `ConnectionStrings:Redis` | Both | Redis address. Empty means an in-process memory cache. |
| `Archive:DatabasePath` | Archiver | Path to `SPT_Data/database` |
| `Archive:SptVersion` | Archiver | The server release of those files, for example `4.0.13`. Required. |
| `Archive:ApplyMigrations` | Archiver | Apply migrations before the import |
| `Archive:Force` | Archiver | Import even if the source is unchanged |
| `Archive:BatchSize` | Archiver | Rows per `SaveChanges` call |
| `Cache:Enabled`, `Cache:TtlMinutes`, `Cache:MaxRequestBytes` | Api | Query cache settings |

**Logging.** Both apps log to the console with Serilog.

**Memory.** The Archiver clears the EF Core change tracker after each batch. Redis uses `maxmemory 128mb` with LRU eviction. PostgreSQL uses `shared_buffers=256MB` in `compose.yaml`.

## Deployment

One Docker host runs `compose.yaml`. The Api publishes port 8080. The Archiver is a one-shot service in the profile `jobs`. See view `production`.

## Risks

See [docs/tech-debt.md](../tech-debt.md).
