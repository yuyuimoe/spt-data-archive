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

## What a TD is

A TD is one known gap, written as one row. It is not a wish and not a plan: it states something the
code, the data or the process does not do today, with the evidence that shows it. A TD is the unit
of "we know this is wrong and have not fixed it", so that the knowledge survives the session that
found it.

| Field | Meaning |
| --- | --- |
| ID | `TD-nn`. Permanent: never reused and never renumbered, so a commit message, an ADR or a run log can name it years later. |
| Type | Where the gap is: **Architecture** (a decision or a boundary), **Code** (a defect or a missing piece), **Data** (the archived content), **Test** (nothing proves it), **Infrastructure** (build, deploy, backup). |
| Item | What is wrong **now**, with the evidence: the file, the run, the number, the error. Written so that a reader who was not in the session understands it. |
| Impact, Risk, Effort | 1 to 5, from the two tables above. Priority = (Impact + Risk) × (6 − Effort), so effort counts double: a cheap fix of a real problem comes first. |
| Why it matters | The consequence in one sentence: what breaks, or which question the archive cannot answer. |

Status is the position of the row, not a column: a row in **Register** is open, a row in
**Resolved items** is closed and says what closed it. An item moves down in the same change that
fixes it, and the change says what was run to prove it — a resolved item with no evidence is a
claim, not a result.

The assistant writes and scores the rows; the driver may re-score, reorder, split or reject them,
and decides when a slice takes one. A gap that is found and not fixed in the same change becomes a
row. A gap that blocks the current slice is not a row: it is the slice.

## Register

| ID | Type | Item | Impact | Risk | Effort | Priority | Why it matters |
|----|------|------|:------:|:----:|:------:|:--------:|----------------|
| TD-36 | Architecture | A run whose mod fails validation produces no export at all. The server loads no mod when one mod is rejected, so the exporter does not run either, and the container serves until the runner stops it. `run_mod.sh` stops early and says so, and a successful run now states its loaded mods in `run/loaded.json` (ADR-0029), but a rejected run still has no such file and is not recorded as `not_loaded` or `dependency_unresolved` (ADR-0021). | 3 | 3 | 1 | 30 | A dependency mistake looks like a broken pipeline and not a fact about the mod. |
| TD-37 | Data | Arena Map Port 0.2.3 needs a real Escape from Tarkov: Arena installation. Without it the log says "The 3 Arena maps stay unregistered" and the export has no `arena_airpit`, `arena_bay5` or `arena_equator` (ADR-0023). | 3 | 2 | 2 | 20 | A map mod is archived as a client-only facet while its maps exist on the author's disk. |
| TD-38 | Data | Mod dependencies are deeper than a Forge page says. Icebreaker 1.1.3 needs `com.wtt.contentbackport` and then `com.morebotsapi.tacticaltoaster`, which its page names in prose and not as a dependency (ADR-0021). | 3 | 3 | 3 | 18 | The catalog must resolve a chain, or a run stops after two downloads. |
| TD-03 | Infrastructure | The backup script does not exist (ADR-0011). | 4 | 4 | 1 | 40 | The archive can be the only copy of mod data. |
| TD-20 | Code | The exporter depends on ten injected table types and on `OnLoadOrder.PostLoad` (ADR-0020, ADR-0027). The bundle patch of ADR-0015 is not used. | 3 | 3 | 2 | 24 | A release can rename a table. The exporter then fails to compile, which is the cheaper failure. |
| TD-22 | Architecture | No run asserts that every mod dataset is pointed at by an observation (ADR-0018). | 2 | 3 | 1 | 25 | An orphan dataset is a bug. Silence hides it. |
| TD-05 | Infrastructure | The Api has no rate limit of its own. The edge proxy is only planned. | 3 | 4 | 2 | 28 | A public API gets scraped. |
| TD-26 | Code | An **installation** names no release, so an official import still fails unless `Archive__SptVersion` is set by hand. A **run** now names its own: exporter 1.3.0 writes `run/release.json` from the server assembly (`4.1.6-RELEASE+T10NVR.0000000000` → `4.1.6`), and `BaselineExport` refuses a baseline of another release. | 2 | 3 | 2 | 22 | A wrong or missing version on the official half labels the data wrongly, or stops the run. |
| TD-45 | Data | A change to a table without a `mod_*` row is hashed and not stored: every location, bot, hideout, price and handbook file. Measured on 2026-10-20: WTT-Armory changes 37 such files. The assort of a trader left this row on 2026-10-20: an offer is a template named by its address (ADR-0032 decision 4), so `ECOTI` stores its 18 added offers as rows instead of two files with no row. | 3 | 3 | 3 | 18 | "What does this mod add to Peacekeeper?" has no answer for a map mod, and a map mod stores a dataset with no row at all. |
| TD-43 | Architecture | The exporter reads `Router.handledRoutes` by reflection to dump `run/routes.json` (ADR-0031). The field is protected and lazily filled, and no public member exposes it, so a server release that renames it stops the route inventory without stopping the run: the exporter warns and the file is missing. | 1 | 2 | 5 | 7 | A release silently loses the route list, and the archive cannot tell a mod with no route from a run that could not read the routes. |
| TD-42 | Infrastructure | The tool that made every mod export is `/tmp/mods/run_mod.sh`: 90 lines of bash and two inline `python3` programs, outside Git, untested. It wrote the pre-boot manifest and `origin.json`, which `RunDirectory` (C#) reads, so the classification and manifest rules exist twice and only one copy is tested. The release, the image and the baseline path are stated in four different places. | 4 | 3 | 4 | 24 | One `rm -rf /tmp` deletes the tool that produced the evidence for ADR-0021, ADR-0027 and ADR-0029 and the test fixtures. On 2026-10-20 it also reported `exit=0` and "saved" for the `FactoryClassic` run in which the server rejected every mod and wrote no export: the success test was `[ -d export ]`, and the pre-boot manifest had already created that directory. The good export it replaced is gone. Fixed in place (the test now requires `run/loaded.json`, which only the exporter writes), but the fix lives outside Git with the rest of the script. |
| TD-28 | Architecture | A patch mod that is archived without the mod it patches changes nothing. MassivesoftWeapons 1.1.2 (mod 3127) added 0 items and logged no error, because the items come from the base mod (mod 2588, SPT 4.0.13) which was not in the dependency set (ADR-0021). | 3 | 3 | 2 | 15 | A silent zero looks like a working run with an empty result, and the Forge does not declare the base mod as a dependency. Measured 2026-10-20: both bundle-only mods (`MassivesoftWeapons`, `FemalePMCVoice`) load and register no HTTP route (ADR-0031), so the missing rows are their only trace and the case stays open. |
| TD-31 | Architecture | Nothing stores the artifact hash of the target mod version. The Forge serves different content under one version number (TD-30), so a version alone cannot say which build a dataset came from (ADR-0021). | 3 | 3 | 1 | 30 | Two builds of one version look identical in the archive. |
| TD-34 | Infrastructure | The artifacts are large and kept by hand: WTT Content Backport 2.0.5 is 6.1 GB compressed with 305 `db` fragments, and the eleven unpacked mods need about 7 GB on disk. Nothing states what is kept and what is deleted (ADR-0023). | 2 | 3 | 1 | 25 | A re-download takes an hour; keeping bundles fills the disk. Keep `db`, `.dll` and config, drop bundles. |
| TD-35 | Data | The location data of 4.1.6 names a bot type that the release does not have: `bossKilla` escorts `followerTagilla`, and there is no `followertagilla.json`. Four other escort pairs contradict the file names (`bossTagilla` escorts `followerBully`, `pmcUSEC` escorts `pmcBEAR`), so escort data cannot define a faction (ADR-0025). | 1 | 3 | 1 | 24 | A consumer that reads the pairs instead of the faction imports the mistake. |
| TD-06 | Test | 239 xUnit tests exist, but no Reqnroll binding (ADR-0012), so no Gherkin scenario runs as a test. All scenarios are still `@unverified`. | 4 | 3 | 3 | 21 | Specs and code can drift apart without a test. |
| TD-09 | Infrastructure | No CI pipeline. | 3 | 3 | 2 | 24 | Nothing builds or checks the docs on each change. |
| TD-10 | Architecture | The query cache is safe only for anonymous access (ADR-0008). | 1 | 3 | 1 | 20 | Adding authentication would leak cached answers across users. |
| TD-30 | Data | The Forge download link can serve an artifact that is not the version the page names. Mod 3127 offers "2.0.1"; the archive it serves contains `MassivesoftWeapons.dll` version 1.1.2 with GUID `com.massivesoft.massivesoftweapons`, while the page states `com.massivesoft.massivesoftweapons.415`. | 3 | 4 | 3 | 21 | Keying a dataset by the page version files content under a version it does not have. Read the version and GUID from the DLL (ADR-0013, ADR-0021). |
| TD-25 | Architecture | Nothing checks that the parent of an added item exists in the baseline (ADR-0017). | 2 | 2 | 1 | 20 | The archive stores an item that a player cannot reach in the tree. |
| TD-24 | Architecture | The archive never learns whether an old mod version still works on a new server release (ADR-0016). | 2 | 1 | 2 | 12 | The most asked question about mods has no answer from the data. |
| TD-13 | Architecture | No views that combine official and mod data. | 3 | 1 | 2 | 16 | Users must query two collections. |
| TD-16 | Architecture | The importers read raw JSON, not the server types (ADR-0004). | 2 | 2 | 3 | 12 | A renamed property goes unnoticed. |
| TD-17 | Architecture | A full replace creates dead rows and holds one long transaction (ADR-0009). | 2 | 2 | 3 | 12 | Table bloat and long locks can appear on large runs. |
| TD-18 | Code | Many parts of the server database are not imported (see the `@backlog` scenario in `archive-official-data.feature`). | 3 | 2 | 4 | 10 | The archive is not complete. |
| TD-19 | Architecture | The write path for admin changes is undefined. | 2 | 2 | 4 | 8 | The driver plans an admin panel or authorized services. |
| TD-49 | Data | An offer states its payments as a list of options, and the archive keeps one amount per template. An offer with two options therefore stores the sum of what each option costs, and cannot say which option grants what. Measured on 4.1.6 on 2026-10-20: 6,131 offers, every one of them with exactly one option, so no dataset is wrong today. The import warns when it meets one. | 2 | 2 | 2 | 16 | A mod that sells one item for roubles or for euros would store a price that is neither. |

## Resolved items

| ID | Item | Resolved by |
|----|------|-------------|
| TD-46 | An array whose length changes became one path that holds the whole array. | ADR-0032, decision 2. `CanonicalForm.Compare` now reports one path for each index that only one side holds: an added index reads as a `set` with no old value, a missing one as a `remove`. ADR-0032 had marked this done before the code did it. Checked on 2026-10-21 with a unit test: a `Side` array that grows from 1 to 3 entries gives `/_props/Side/1` and `/_props/Side/2`. The WTT-Armory `Filter` arrays (30 to 39 ids) were not re-imported. |
| TD-21 | The Api has no server version argument. | ADR-0014 and ADR-0019. Every official field takes an optional `sptVersion`: one exact archived release, or the latest. An unknown release gives `SPT_VERSION_UNKNOWN`, a value that is not a release key gives `SPT_VERSION_MALFORMED`. The cache key holds the argument. |
| TD-15 | JSON columns reach GraphQL clients as text. | The jsonb columns are `Any` values (JSON), parsed from the stored text in the resolver. The stored data does not change. The value is opaque: a client cannot select a sub-field. |
| TD-51 | A run that is stopped leaves its row in the status `running`. | ADR-0036, proposed by Yui's plan on 2026-10-20 that several imports run at the same time: a run holds a lease. The Archiver writes `archive_runs.heartbeat_at` every 10 seconds on its own connection, and `StaleRuns.CloseAsync` closes a run that has been silent for 120 seconds as `interrupted`, with the error and the result that say so. The test is the heartbeat and never the start time, because among two open runs the live one may be the older one. Measured: an import of WTT-Armory while a second Archiver started kept its row `running`, a row silent for 12 minutes was closed as `interrupted` on the next start, and two imports of one export left one dataset, two `succeeded` runs and one observation. 6 facts in `ConcurrentRunTests`. |
| TD-50 | An official import can hold mod data. | ADR-0035, decided by Yui on 2026-10-20: official data comes only from an installation that loaded no mod. `SptArchive.Archiver.Runs.OfficialSource` reads `run/loaded.json` and refuses a source that loaded a mod other than the exporter, and `RunImporter.EnsureOfficialReleaseAsync` refuses a run of a release that holds no official row. Measured: the baseline export of 4.1.6 (2 library mods loaded) is now refused with a message that names both; the native export of the same release entered as official data with 4,673 items, 536,414 locale entries, 12 traders and 10,880 assort rows, and 0 `SALVAGE_*` keys in place of the 4 that the mods added. The first native import failed on trader `638f541a29ffd1183d187f57`, whose `assort.json` and `questassort.json` hold `null`: the importer read a null root as an object. Fixed, and `ImportTests` holds the case. 230 tests. |
| TD-44 | A run states the mods it loaded, not which one it was made for. | ADR-0033, decision 3, as amended and ratified by Yui on 2026-10-20: the exporter writes `run/target.json` from `SPT_ARCHIVE_TARGET_MOD_GUID` and `SPT_ARCHIVE_TARGET_REASON` (exporter 1.5.0), and takes the version from the mod that loaded, so the file cannot state a version that did not run. The importer reads it, refuses a configured GUID that disagrees, and marks an observation unverified when the file is absent. Measured: ECOTI re-exported with 1.5.0 imports with `targetVerified: true` and without `Archive:RunModGuid`; a second import of the same directory reuses the dataset and stores a second run row with outcome `unchanged`. A directory written before 1.5.0 stays unverified, which is the truth about it. |
| TD-39 | Nothing canonicalized a run before it compared or hashed it (ADR-0028). | `SptArchive.Core.Canonical.CanonicalForm` sorts keys, keeps array order, replaces the two measured volatile paths with `#volatile` from `volatile-paths.json`, and renumbers the 5,090 generated assort ids to `item[<n>]` with the 1,813 `parentId` references and the `barter_scheme` and `loyal_level_items` keys that follow them. `CanonicalFormTests` (22 tests) holds the two baseline exports of 4.1.6 as its fixture: 13 files differ on disk, 0 differ after canonicalization, and a `$.suits[0]._id` difference that no entry covers is still reported. The run importer now calls it for every comparison and every dataset hash. |
| TD-48 | A stored id was the id the run gave the object, and a run minted that id itself. | ADR-0034, implemented in `SptArchive.Core.Canonical.Addressing`, and ADR-0032 decision 4 for the offer. A template row is keyed by an address: the baseline id when the baseline holds the object, a content ULID built from the identity otherwise; `mod_id_map` keeps the id of the run as a lookup. Measured on 4.1.6 on 2026-10-20: Peacekeeper holds 5,090 assort instance ids in the baseline and 5,099 in the run, the two sets share none, and the diff pairs its 6,131 offers with 0 changes; `ECOTI` adds 18 offers, stored as 18 rows; the `mod_id_map` of that dataset holds 12,129 rows. The limit that remains: two offers that share a preset are named by their order, measured 4 groups of 9 offers of 6,131. |
| TD-47 | The importer hashed more than ADR-0029 stated. | ADR-0029 amendment 4, ratified by Yui on 2026-10-20: the content hash covers the contribution rows, the locale rows, the canonical hash of every changed file that has no row (TD-45), the configuration text and the manifest. `DatasetHash.Compute` does exactly that, and `RunDiffTests` proves that a change to a configuration or to an unheld file changes the hash while a volatile path does not. |
| TD-29 | Nothing reads a run directory into a dataset. | `SptArchive.Core.Canonical.RunDiff` compares the export of a run with the baseline of its release: per template id for items, quests and customizations, per trader directory, and per locale key; `SptArchive.Archiver.Runs.RunImporter` writes the dataset, the observation and the rows. Measured on 4.1.6 against the 642 MB baseline: ECOTI 9 added items and 18 added offers in 27 templates, and 459 locale entries; WTT-Armory 1,337 templates and 733 item paths with the old values from the baseline; Gunwright a dataset with 0 rows, because its whole contribution is two HTTP routes (ADR-0031). 220 tests. What remains is TD-44, TD-45, TD-46 and TD-49. |
| TD-40 | The exporter writes the manifest but nothing imports a run, so no `mod_files` row exists. | Migration `AddModFiles` and the run importer: ECOTI stores 46 files, 2 of them configuration files with their text and the hash before boot; WTT-Armory stores 110. The text is what explains a dataset, and a curated configuration is a separate dataset through `config_origin` (ADR-0029). |
| TD-32 | The exporter serializes three of the twelve `*Table` types. | ADR-0024 and ADR-0027: the exporter now dumps all ten table classes of 4.1.6 in the layout of `SPT_Data/database`, and its source is in `src/SptArchive.Exporter`. The first export is 642 MB: 4,673 items, 558 quests, 31,549 translations, 12 traders, 57 bot types, 19 locations with their overlays, and the six hideout parts. A run with Factory Classic and Map Variants changed exactly the two Factory maps (7 files each) plus one boss chance on Lighthouse and Woods, which is the noise of ADR-0028. |
| TD-27 | The exporter does not dump bot tables. | ADR-0027: `bots/base.json`, `bots/core.json` and one file per bot type leave the container (57 types for 4.1.6). |
| TD-41 | `ModObservationOutcome` lacked `no_server_data` and `loaded_but_disabled`. | Migration `AddModFiles` and `ModObservationOutcome`: both values are in the enum and in `ck_mod_data_observations_outcome`. `SchemaTests` inserts each value and proves that an invented one is rejected. |
| TD-33 | Nothing in the server data links a bot type to a faction. | ADR-0025, decided by Yui: a curated seed with a prefix fallback, separated by boss id. `BotFactions.Seed` in `SptArchive.Core` holds 57 rows, 21 factions and 4 deliberate nulls; `BotFactions.Resolve` applies mod declaration, seed, four trusted prefixes, then null. `FactionTests` (27 tests) holds the seed to the 57 bot type files of 4.1.6, so a new type fails the test until somebody curates it. The `factions` table arrives with TD-32. |
| TD-01 | The solution has never been compiled. | The solution builds clean: 0 warnings, 0 errors. Two defects had to be fixed. (1) The Api resolved EF Core 10.0.4 through Npgsql while Core compiled against 10.0.13, so the Api failed with CS1705; one version per package now comes from `Directory.Packages.props`. (2) HotChocolate 16 removed `RegisterDbContext<T>(DbContextKind)`; the Api now calls `RegisterDbContextFactory<ArchiveDbContext>()`, which binds to the pooled factory that `AddArchiveDatabase` already registers. The Api was also started and answered `/healthz`; its schema built and the resolvers reached EF Core. |
| TD-02 | No EF Core migration is committed. | `src/SptArchive.Core/Migrations/20261020003324_Initial` exists and is applied to the development database. It carries what ADR-0013, ADR-0014 and ADR-0017 require: `spt_versions`, `spt_version` as the first column of every official key, `mod_datasets` with a unique `(mod_guid, content_hash)`, `mod_data_observations`, `mod_templates` and `mod_item_changes`, and `dataset_id` instead of `mod_id` in the six `mod_*` tables. The reader role reads the new tables and cannot write them. |
| TD-04 | The item importer never ran on real data. | The local server clone `SPTarkov.Server.Assets/SPT_Data/database` was imported as release 4.0.13: 4,582 items, 528,577 translations, 558 quests, 12 traders and 5,790 assort rows in 26 seconds. A second run with the same release skipped on the source hash. The Api then answered `items`, `traders` and `translations` through `archive_reader`. |
| TD-07 | NuGet package versions are floating (`10.0.*`, `16.*`, `*`). | `Directory.Packages.props` with central package management and transitive pinning. |
| TD-08 | The archive scope is undecided. | ADR-0014. The work moved into TD-02 and TD-21. |
| TD-11 | The project is not a git repository. | `git init` and a first commit `258411d` (92 files). A later commit `d27e75c` records the mod export pipeline. |
| TD-12 | Mod ingestion is not built. | ADR-0013. The work is in its action items. |
| TD-14 | Mods that change official items have no model. | ADR-0013 and ADR-0017. `mod_templates` holds the operation, `mod_change_paths` holds the paths. |
| TD-23 | Nothing states the dependency set of a mod. | ADR-0013. The catalog supplies it, the metadata is the fallback, and the run freezes it on the observation. The choice of catalog remains open. |

## Remediation plan

Work on one phase at a time. Work on the phases beside feature work.

### Phase 0: Make the project buildable (one slice)

- TD-01: Done. `dotnet build` passes with no warnings.
- TD-02: Done. The first migration is applied and tested.
- TD-07: Done. The versions are pinned in `Directory.Packages.props`.
- TD-11: Done. The repository is under git.

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
- TD-36: Record a rejected mod as `not_loaded` or `dependency_unresolved`, not as a missing export.
- TD-28: Record that a loaded mod produced no data, instead of storing a dataset equal to the baseline.

### Phase 3: Prepare for the public

- TD-05: Put the Api behind a rate-limiting proxy.
- TD-10: Add a test that fails when authentication is added without a change to the cache.

### Phase 4: Extend

- TD-13 to TD-19, in the order the users ask for them.
