# Architecture Decision Records

An ADR records one decision, the reason for it and what it causes. Each ADR ends with Gherkin scenarios that state what must stay true.

## Index

| ADR | Decision | Status |
|-----|----------|--------|
| [0001](0001-use-postgresql.md) | Use PostgreSQL as the archive database | Accepted |
| [0002](0002-hybrid-columns-and-jsonb.md) | Store variable data as jsonb next to a few real columns | Accepted |
| [0003](0003-separate-official-and-mod-tables.md) | Keep official data and mod data in separate tables | Accepted |
| [0004](0004-read-server-json-directly.md) | Read the official data directly from the server JSON files | Accepted |
| [0005](0005-mod-ingestion-strategy.md) | Choose how to ingest mod data | Superseded by ADR-0013 |
| [0006](0006-thin-readonly-graphql-api.md) | Serve the archive through a thin read-only GraphQL API | Accepted |
| [0007](0007-dotnet-stack.md) | Build every project in C# on .NET 10 | Accepted |
| [0008](0008-redis-response-cache.md) | Cache complete GraphQL responses in Redis with a dataset version in the key | Accepted |
| [0009](0009-run-to-completion-archiver.md) | Run the Archiver as a job that replaces the official data in one transaction | Accepted |
| [0010](0010-archive-scope-latest-or-versioned.md) | Decide whether the archive keeps one dataset or many server versions | Superseded by ADR-0014 |
| [0011](0011-backups-with-pg-dump.md) | Back up with pg_dump after each successful run | Accepted |
| [0012](0012-reqnroll-for-gherkin.md) | Run the Gherkin specs with Reqnroll | Accepted |
| [0013](0013-mod-ingestion-by-isolated-run.md) | Ingest mod data by running one mod in an isolated server container | Accepted |
| [0014](0014-keep-every-archived-spt-release.md) | Keep every archived server release under one version key | Accepted |
| [0015](0015-suppress-bundles-with-a-patch-mod.md) | Suppress bundle loading with a HarmonyX patch mod | Accepted |
| [0016](0016-probe-mod-compatibility.md) | Probe mod compatibility when a server release arrives | Accepted |
| [0017](0017-record-a-template-operation.md) | Record one template operation for every item that a mod touches | Accepted |
| [0018](0018-do-not-delete-mod-data-in-the-first-release.md) | Do not delete mod data in the first release | Accepted |
| [0019](0019-reject-a-version-the-archive-does-not-hold.md) | Reject a server version that the archive does not hold | Accepted |
| [0020](0020-export-mod-data-from-a-server-container.md) | Export mod data from a server container, without a Harmony patch | Accepted |
| [0021](0021-archive-a-patch-mod-with-its-base.md) | Archive a patching mod together with the mod it patches | Accepted |
| [0022](0022-archive-player-customization.md) | Archive player customization as a seventh dataset | Accepted |
| [0023](0023-classify-a-mod-by-the-parts-it-ships.md) | Classify a mod by the parts it ships | Accepted |
| [0024](0024-archive-the-whole-server-database.md) | Archive the whole server database, not seven tables | Accepted |
| [0025](0025-resolve-a-faction-from-locale-and-curation.md) | Resolve a faction from a curated map with a prefix fallback | Accepted |
| [0026](0026-archive-a-value-mod-preset.md) | Archive a value mod's preset, and say when it did nothing | Accepted |
| [0027](0027-archive-a-run-as-a-database-directory.md) | Archive a run as a database directory | Accepted |
| [0028](0028-compare-two-runs-in-a-canonical-form.md) | Compare two runs in a canonical form | Accepted |
| [0029](0029-archive-mod-files-and-config.md) | Archive the files a mod ships, and its configuration | Accepted |
| [0030](0030-run-export-and-import-with-one-tool.md) | Run, export and import with one .NET tool | Accepted |
| [0031](0031-record-the-http-surface-a-run-exposes.md) | Record the HTTP surface a run exposes | Accepted |
| [0032](0032-record-every-changed-value-as-a-row.md) | Record every changed value as a row | Accepted |
| [0033](0033-record-every-run-and-why-it-happened.md) | Record every run and why it happened | Accepted |
| [0034](0034-name-an-object-by-what-it-is.md) | Name an object by what it is | Accepted |
| [0035](0035-take-official-data-from-a-mod-free-installation.md) | Take official data from an installation that loaded no mod | Accepted |
| [0036](0036-let-several-imports-run-at-the-same-time.md) | Let several imports run at the same time | Accepted |
| [0037](0037-survive-a-burst-on-one-answer.md) | Survive a burst on one answer, and let a client repeat a request | Accepted |
| [0038](0038-refuse-a-flood-and-compress-an-answer.md) | Refuse a flood at the Api, and compress what it answers | Proposed |
| [0039](0039-terminate-tls-at-a-proxy.md) | Terminate TLS at a proxy, and name the client there | Proposed |
| [0040](0040-filter-a-jsonb-column-by-path.md) | Filter a jsonb column by the value at a path | Proposed |
| [0041](0041-give-a-change-to-data-that-has-no-mod-table-a-row.md) | Give a change to data that has no mod table a row | Proposed |
| [0042](0042-store-each-address-and-each-value-once.md) | Store each address and each value once, and join them to a change | Accepted |
| [0043](0043-split-a-loose-loot-file-into-one-row-for-each-spawn-point.md) | Split a loose-loot file into one row for each spawn point | Accepted |
| [0044](0044-split-every-file-that-a-run-changes-into-one-row-for-each-object.md) | Split every file that a run changes into one row for each object | Accepted |
| [0045](0045-record-where-a-file-of-a-run-came-from.md) | Record where a file of a run came from, and serve the link | Accepted |

## Status meanings

- **Accepted:** The driver made this decision. Do not change the behavior without a new ADR.
- **Proposed:** The assistant wrote the design and the driver has not confirmed it. The code follows it. Ask the driver before you build on it.
- **Proposed (decision needed):** No decision exists. Do not guess. Ask the driver.
- **Deprecated:** The decision no longer applies.
- **Superseded by ADR-NNNN:** A newer ADR replaced it.

## Rules

1. Write a new ADR for any change that affects more than one component, the data model or an external system.
2. Copy `0000-template.md`. Use the next free number. Use a short file name in lower case with hyphens.
3. Never edit the decision of an Accepted ADR. Write a new ADR and mark the old one as Superseded.
4. Write the Verification scenarios as Gherkin. Use the tags `@unverified`, `@backlog` and `@decision-needed` as in `docs/specs/README.md`.
5. Update the index in this file in the same change.
6. Run `python3 scripts/check-docs.py` before you finish.
