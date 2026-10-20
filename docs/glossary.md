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
| **Outcome** | The result of one mod data observation: `archived`, `not_loaded`, `dependency_unresolved`, `outside_declared_range`, `crashed` or `timeout`. |
| **Mod change** | One property path that a mod dataset changes. A row in `mod_item_changes`, under one row of `mod_templates`. The path is a JSON pointer. |
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
