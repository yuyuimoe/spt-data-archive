# Technical debt register

This file lists known debt in the scaffold. Each item has a score. A higher score means the team should fix it sooner.

## How to read the scores

**Priority = (Impact + Risk) × (6 − Effort)**

| Score  | 1                | 2             | 3                | 4                | 5                     |
|--------|------------------|---------------|------------------|------------------|-----------------------|
| Impact | Almost no effect | Small effect  | Slows the team   | Blocks features  | Blocks all work       |
| Risk   | Harmless         | Low           | Medium           | High             | Data loss or security |
| Effort | Under 1 hour     | About half a day | 1 to 2 days   | Several days     | More than a week      |

A low effort gives a high priority. The scores are estimates from the assistant. The driver can change them.

## Register

| ID | Type | Item | Impact | Risk | Effort | Priority | Why it matters |
|----|------|------|:------:|:----:|:------:|:--------:|----------------|
| TD-03 | Infrastructure | The backup script does not exist (ADR-0011). | 4 | 4 | 1 | 40 | The archive can be the only copy of mod data. |
| TD-20 | Infrastructure | The exporter and the bundle patch depend on private server internals: the load-order slots, `SptServerStartupService` and `ProgramStatics` (ADR-0013, ADR-0015). | 4 | 4 | 2 | 32 | A release can move them. Then a run fails late, or loads bundles without saying so. |
| TD-21 | Architecture | The Api has no server version argument (ADR-0014, ADR-0019). The schema now holds one row set per release, so every official query merges releases. | 4 | 4 | 2 | 32 | Every official field needs a default and a stated version, or two releases merge in one answer. |
| TD-22 | Architecture | No run asserts that every mod dataset is pointed at by an observation (ADR-0018). | 2 | 3 | 1 | 25 | An orphan dataset is a bug. Silence hides it. |
| TD-05 | Infrastructure | The Api has no rate limit of its own. The edge proxy is only planned. | 3 | 4 | 2 | 28 | A public API gets scraped. |
| TD-26 | Code | The Archiver cannot read the server version from an installation. A run fails unless `Archive__SptVersion` is set by hand. | 3 | 4 | 2 | 28 | A wrong or missing version labels the data wrongly, or stops the run. |
| TD-06 | Test | 34 xUnit tests exist, but no Reqnroll binding (ADR-0012), so no Gherkin scenario runs as a test. All scenarios are still `@unverified`. | 4 | 3 | 3 | 21 | Specs and code can drift apart without a test. |
| TD-09 | Infrastructure | No CI pipeline. | 3 | 3 | 2 | 24 | Nothing builds or checks the docs on each change. |
| TD-10 | Architecture | The query cache is safe only for anonymous access (ADR-0008). | 1 | 3 | 1 | 20 | Adding authentication would leak cached answers across users. |
| TD-11 | Infrastructure | The project is not a git repository. | 2 | 2 | 1 | 20 | Branches, reviews and history need one. |
| TD-25 | Architecture | Nothing checks that the parent of an added item exists in the baseline (ADR-0017). | 2 | 2 | 1 | 20 | The archive stores an item that a player cannot reach in the tree. |
| TD-24 | Architecture | The archive never learns whether an old mod version still works on a new server release (ADR-0016). | 2 | 1 | 2 | 12 | The most asked question about mods has no answer from the data. |
| TD-13 | Architecture | No views that combine official and mod data. | 3 | 1 | 2 | 16 | Users must query two collections. |
| TD-15 | Code | JSON columns reach GraphQL clients as text. | 2 | 1 | 2 | 12 | Clients must parse the text. |
| TD-16 | Architecture | The importers read raw JSON, not the server types (ADR-0004). | 2 | 2 | 3 | 12 | A renamed property goes unnoticed. |
| TD-17 | Architecture | A full replace creates dead rows and holds one long transaction (ADR-0009). | 2 | 2 | 3 | 12 | Table bloat and long locks can appear on large runs. |
| TD-18 | Code | Many parts of the server database are not imported (see the `@backlog` scenario in `archive-official-data.feature`). | 3 | 2 | 4 | 10 | The archive is not complete. |
| TD-19 | Architecture | The write path for admin changes is undefined. | 2 | 2 | 4 | 8 | The driver plans an admin panel or authorized services. |

## Resolved items

| ID | Item | Resolved by |
|----|------|-------------|
| TD-01 | The solution has never been compiled. | The solution builds clean: 0 warnings, 0 errors. Two defects had to be fixed. (1) The Api resolved EF Core 10.0.4 through Npgsql while Core compiled against 10.0.13, so the Api failed with CS1705; one version per package now comes from `Directory.Packages.props`. (2) HotChocolate 16 removed `RegisterDbContext<T>(DbContextKind)`; the Api now calls `RegisterDbContextFactory<ArchiveDbContext>()`, which binds to the pooled factory that `AddArchiveDatabase` already registers. The Api was also started and answered `/healthz`; its schema built and the resolvers reached EF Core. |
| TD-02 | No EF Core migration is committed. | `src/SptArchive.Core/Migrations/20261020003324_Initial` exists and is applied to the development database. It carries what ADR-0013, ADR-0014 and ADR-0017 require: `spt_versions`, `spt_version` as the first column of every official key, `mod_datasets` with a unique `(mod_guid, content_hash)`, `mod_data_observations`, `mod_templates` and `mod_item_changes`, and `dataset_id` instead of `mod_id` in the six `mod_*` tables. The reader role reads the new tables and cannot write them. |
| TD-04 | The item importer never ran on real data. | The local server clone `SPTarkov.Server.Assets/SPT_Data/database` was imported as release 4.0.13: 4,582 items, 528,577 translations, 558 quests, 12 traders and 5,790 assort rows in 26 seconds. A second run with the same release skipped on the source hash. The Api then answered `items`, `traders` and `translations` through `archive_reader`. |
| TD-07 | NuGet package versions are floating (`10.0.*`, `16.*`, `*`). | `Directory.Packages.props` with central package management and transitive pinning. |
| TD-08 | The archive scope is undecided. | ADR-0014. The work moved into TD-02 and TD-21. |
| TD-12 | Mod ingestion is not built. | ADR-0013. The work is in its action items. |
| TD-14 | Mods that change official items have no model. | ADR-0013 and ADR-0017. `mod_templates` holds the operation, `mod_item_changes` holds the paths. |
| TD-23 | Nothing states the dependency set of a mod. | ADR-0013. The catalog supplies it, the metadata is the fallback, and the run freezes it on the observation. The choice of catalog remains open. |

## Remediation plan

Work on one phase at a time. Work on the phases beside feature work.

### Phase 0: Make the project buildable (one slice)

- TD-01: Done. `dotnet build` passes with no warnings.
- TD-02: Done. The first migration is applied and tested.
- TD-07: Done. The versions are pinned in `Directory.Packages.props`.
- TD-11: Run `git init` and make the first commit.

Done when `dotnet build` passes and the first migration is committed.

### Phase 1: Verify what exists

- TD-06: The test project exists and 34 tests pass. Bind the Gherkin scenarios with Reqnroll.
- TD-04: Done. The importers ran on the local server clone.
- TD-09: Add a CI pipeline that builds, runs the tests and runs `scripts/check-docs.py`.
- TD-03: Add the backup script.

Done when no scenario of the first three feature files has the tag `@unverified`.

### Phase 2: Make the open decisions

Done. ADR-0013, ADR-0014, ADR-0015, ADR-0016, ADR-0017 and ADR-0018 replace the two open decisions.
ADR-0005 and ADR-0010 are now superseded.
Phase 0 must run after ADR-0014, because the first migration carries the version key.

- TD-23: The dependency source is decided. Choose the catalog itself before the mod slice starts.
- TD-20: Verify the load order and the patch on one real mod, before 699 containers exist.
- TD-25: Check the parent of an added item, once the first mod with an added item is archived.

### Phase 3: Prepare for the public

- TD-05: Put the Api behind a rate-limiting proxy.
- TD-10: Add a test that fails when authentication is added without a change to the cache.

### Phase 4: Extend

- TD-13 to TD-19, in the order the users ask for them.
