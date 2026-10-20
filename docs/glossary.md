# Glossary

Use these words in code, specs and docs. Use the same word for the same thing.

| Term | Meaning |
|------|---------|
| **SPT** | The single-player mod and its server. In the `server-csharp` repository the mod is named "Single Player Tushonka". |
| **Server database** | The JSON files in `SPT_Data/database` of the server repository. The server loads them at startup. |
| **Official data** | Data that ships with the server. It is stored in the tables without the `mod_` prefix. |
| **Mod data** | Data that a mod adds. It is stored in the `mod_*` tables. Each row belongs to one mod. |
| **Mod** | An extension of the server. A row in the `mods` table. |
| **MongoId** | A 24-character hexadecimal id. The upstream game uses it as a key. Items, quests and traders have one. |
| **Item template** | The definition of one kind of item. It has an id, a name, a parent and properties. The server file `templates/items.json` holds them. |
| **Template id** | The 24-character id of an item template. Every reference in the server data uses it. A mod keeps the id to change an item, or mints a new id to add one. |
| **Template operation** | What a mod does to one template id: `added`, `modified` or `removed`. One row in `mod_templates` for each touched template (ADR-0017). |
| **Contribution** | Everything a run adds and changes, measured against the baseline of its release: the template operations, the item paths with the old value from the baseline, and the locale entries (ADR-0013). A dataset stores the contribution and its content hash covers it. |
| **Content hash** | The SHA-256 of a contribution together with the configuration text, the manifest of the files the mod ships and the canonical hash of every changed file that has no row (ADR-0029, amendment 4). Two mod versions with the same hash share one dataset. |
| **Address** | The name the archive gives an object (ADR-0034). It is the id of the baseline when the baseline holds the object. Otherwise it is a content ULID, because the run made the object. |
| **Content ULID** | A 26-character id that the archive derives from a seed: the kind of the object and its identity fields (ADR-0034). The time half is zero, so a boot that makes new instance ids does not change it. |
| **Pinned** | An object that the baseline of a release holds, so its own id is its address (ADR-0034). Every official row is pinned. |
| **Offer** | One thing a trader sells: an item, the parts attached to it, what the player pays with and the loyalty level. An offer is a template named by its address (ADR-0032). |
| **Payment channel** | What the player pays an offer with: the templates of the payment, named by their addresses, sorted and joined by `+`. The amount is not part of it. |
| **Id map** | The rows of a dataset that pair the id a run gave an object with its address (ADR-0034). The map is a lookup and is not part of the content hash. |
| **Unstored change** | A file that the run changed and that no `mod_*` table holds: a trader's assort, a location, a bot type, a price table. Its canonical hash enters the content hash, so two datasets that differ only there stay apart, and the import names it in the log (TD-45). |
| **Added template** | A template id that the baseline does not have and the target export does. Its path rows have no old value. |
| **Modified template** | A template id that the baseline and the target export both have, with different content. |
| **Node** | An item template with the type "Node". It is a category in the item tree, not a real item. |
| **Props** | The `_props` object of an item template. It holds the properties of the item. The archive stores it as `jsonb`. |
| **Promoted column** | A column that holds a property that users filter by, for example `weight`. The full object also stays in `jsonb`. |
| **Locale** | A set of translated strings for one language. The server has one flat file per language. |
| **Locale entry** | One translated string. Its key often has the form `<entity id> <field>`. |
| **Entity id** | The 24-character id at the start of a locale key. It links a translation to an item, quest or trader. |
| **Trader** | A non-player seller. The folder `traders/<id>` of the server database describes one. |
| **Assort** | The list of offers of one trader. |
| **Offer** | A root entry of an assort. It has the parent `hideout`, a barter scheme and a loyalty level. |
| **Barter scheme** | What the trader wants in exchange for an offer. A list of alternatives. Each alternative lists items and counts. |
| **Loyalty level** | The level that a player needs before the trader sells the offer. |
| **Quest assort** | A map that states which quest state unlocks which offer. |
| **Customization** | One piece of clothing that a player can wear: a body, head, hands or feet. The server keeps them in `templates/customization.json`, apart from items. A customization names a client bundle but does not contain it (ADR-0022). |
| **Sides** | The player sides that may wear a customization: `Usec`, `Bear` and `Savage`. |
| **Archiver** | The job that reads the server database and writes the archive. It exits when the run ends. |
| **Archive run** | One execution of the Archiver. It has a row in `archive_runs`. |
| **Source hash** | A SHA-256 value over all source files. The Archiver uses it to skip unchanged data. |
| **Dataset** | One archived set of data. An official dataset belongs to one server version. A mod dataset belongs to one mod version. |
| **Server version** | The version string of the server, for example "4.0.13". It is the key of an official dataset. |
| **Server release** | One publication of the server. A server release has one server version. The archive keeps every server version (ADR-0014). |
| **Release key** | A server version that the archive holds, for example "4.0.13". The `sptVersion` argument takes one release key and nothing else (ADR-0019). |
| **Mod version** | One release of one mod, for example "1.0.0" of "com.example.mod". It is not a server version (ADR-0013). |
| **Mod dataset** | The content of one mod version: the rows it adds and the official rows it changes. Two mod versions with the same content share one mod dataset. |
| **Mod data observation** | One attempt to load one mod version on one server version. It points at a mod dataset, or it states why there is none. |
| **Outcome** | The result of one mod data observation: `archived`, `not_loaded`, `dependency_unresolved`, `outside_declared_range`, `crashed`, `timeout`, `no_server_data` or `loaded_but_disabled` (ADR-0023, ADR-0026). |
| **Mod change** | One property path that a mod dataset changes. A row in `mod_change_paths`, under one row of `mod_templates`. The path is a JSON pointer. |
| **Role** | The label the game shows for a kind of bot. The official vocabulary is the `ScavRole/*` keys of a locale: Boss, Guard, Raider, Rogue, Sniper, Smuggler, Sectant, Infected. A mod can add a key (ADR-0025). |
| **Faction** | A named group that a player recognises, such as USEC, BEAR, Savage, Black Division, or one boss squad. The archive resolves it from a curated seed, then from four trusted prefixes, and never from an escort pair, because no field holds it (ADR-0025). |
| **Bot squad** | A boss and the followers that spawn with it. One squad is one faction, and the faction id is the boss id: `bossbully` holds `followerbully` and `followerbigpipe` belongs to `bossknight`. |
| **Bot type** | One file under `bots/types/`. The name of the file is the identity, and it is the client spawn type lowercased: the wave says `pmcBEAR` and the file is `pmcbear.json`. |
| **Bot facet** | One part of what a mod ships: `client` (files under `BepInEx`), `server_data` (a `db` tree under `SPT_Runtime/user/mods`), `server_code` (a server `.dll`), `bundle` (assets that are not data). A mod has a set of facets (ADR-0023). |
| **db fragment** | A file under a mod's `db` tree that the server merges into its tables at load, for example `db/bots/types/bossWedge.json`. The fragment is provenance, not a dataset. |
| **Preset** | The configuration file that a value mod reads, for example the SVM preset that `Greed.exe` writes. The archive stores it file by file, with its text and its hash, and never authors one (ADR-0026, ADR-0029). |
| **Canonical form** | The one shape of a run that a comparison and a hash read: object keys sorted, array order kept, and the measured volatile paths replaced by `#volatile` (ADR-0028). It is never stored and never served; `jsonb` keeps what the server wrote. |
| **Volatile path** | A JSON path whose value a run of one release does not hold constant, for example `$.nextResupply`. It is a measured entry in `volatile-paths.json`, never a guess (ADR-0028). |
| **Behaviour mod** | A mod whose effect happens when a raid is generated, so no table of the export holds it: Lots of Loot Redux multiplies loot at generation time. The archive stores its config and records `archived` with zero rows; it does not run raids, so behaviour is out of scope (ADR-0028, decision 6). |
| **Config file** | A file inside a mod directory that the mod reads to choose what it does: `config/config.json`, `Presets/default.json`, `db/config.json`, or any `.jsonc`. The run archives its text and never edits it (ADR-0029). |
| **Curated configuration** | A configuration file that a person edits, or writes, after the mod shipped it. It runs as its own dataset, marked `curated` in `mod_datasets.config_origin`, and never replaces the run with the shipped file (ADR-0026, ADR-0029). |
| **Manifest** | The list of files that a mod directory holds, with the size and the SHA-256 of each. It records that a file exists and never its bytes (ADR-0029). |
| **Environment file** | The one `.env` at the repository root: the passwords, the installation path, the release, the image, and the directories of mods, exports and baselines. It is read by every tool and written by none; one run's choices are arguments, not entries (ADR-0030). |
| **Runner** | The tool that makes a run: it copies the mod set into a fresh directory, writes the pre-boot manifest, starts the container, waits for the export, and imports the result. It replaced the shell script that made the first nineteen exports (ADR-0030). |
| **Run directory** | The export of one container run: the ten table roots of the server database, and a `run` root that states which mods loaded and what each mod shipped (ADR-0027, ADR-0029). |
| **Target of a run** | The mod that the run exists for, as opposed to the dependencies it also loads. The run states it in `run/target.json`, with the version that loaded, so the archive proves the target instead of taking a typed value (ADR-0033, amended). The runner states the GUID in `SPT_ARCHIVE_TARGET_MOD_GUID` and the exporter writes the file. |
| **Official run** | A run or an import of the data of the server itself: it loads no mod (ADR-0033, decision 1). The Archiver refuses a source that loaded a mod, and refuses a run of a release that holds no official data (ADR-0035). |
| **Mod-free installation** | An installation whose `mods` folder holds nothing but the exporter. Its export is the only source of official data that the archive accepts without a warning (ADR-0035). |
| **Lease** | The promise that a run is still alive. A live run writes `archive_runs.heartbeat_at` every 10 seconds, and a run that is silent for 120 seconds is over (ADR-0036). |
| **Interrupted run** | A run whose process stopped without recording a result. Another Archiver closes its row as `interrupted` when the lease runs out, because a killed process reaches no `catch` block (ADR-0036). |
| **Route** | One URL that the server answers, with the router that answers it (ADR-0031). A run records every route in `run/routes.json`. |
| **Interception** | A route of a mod on a URL that the server also answers. The dispatcher runs every match and keeps the last answer, so the order decides whether the mod replaces the answer or prepares the request (ADR-0031). |
| **Dependency set** | The pinned mod versions that a run loads beside the target mod. The catalog states it, and the mod metadata is the fallback. It is frozen on the observation. |
| **Version range** | A semver expression that states which versions satisfy a requirement, for example ">=2.0.0 <2.5.0". The Archiver resolves one for a dependency set. The Api does not accept one (ADR-0019). |
| **Baseline cache** | The stored baseline export, keyed by the dependency set hash and the server version. Two mods with one dependency set share it. |
| **Orphan dataset** | A mod dataset that no observation points at. A correct run cannot produce one (ADR-0018). |
| **Exporter mod** | The server mod that writes the final in-memory database to JSON at the end of the load. |
| **Baseline** | The export of a container with the dependencies of a mod and without the mod itself. A mod dataset is the difference from the baseline. |
| **Compatibility probe** | One container run that states whether a mod version still loads on a new server version. A probe writes an observation and no row (ADR-0016). |
| **Dataset version** | The id of the run that produced the current dataset. The Api uses it in its cache keys. |
| **Git LFS pointer** | A small text file that stands in for a large file when `git lfs pull` has not run. It is not data. |
| **jsonb** | The PostgreSQL type that stores JSON in a binary form and supports indexes. |
| **GIN index** | A PostgreSQL index type. The archive uses it for containment queries on `jsonb`. |
| **ADR** | Architecture Decision Record. One file in `docs/adr` per decision. |
| **C4 model** | A set of diagrams at four levels: context, containers, components and code. The files are in `docs/architecture/c4`. |
| **Gherkin** | The Given, When, Then language of the files in `docs/specs`. |
| **Slice** | One small piece of work that goes from spec to design to code. The team works on one slice at a time. |
| **Driver** | The person who makes the decisions of the project. |
