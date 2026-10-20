# Runbook: export mod data from a server container

This runbook produces one export per mod version. It follows ADR-0013 and ADR-0020.

All files stay outside the repository. The checkout under `~/Documents/repos/tarkov/sp-tushonka-server`
is a working copy and not part of this project.

## 1. Build the image for one release

The image states the release it holds. Build one image per release you archive.

```bash
cd ~/Documents/repos/tarkov/sp-tushonka-server
git fetch --depth 1 origin refs/tags/4.1.6:refs/tags/4.1.6
git checkout 4.1.6

podman build -t localhost/spt-server:4.1.6 \
  --build-arg SptVersion=4.1.6 \
  --build-arg SptBuildType=RELEASE \
  --build-arg SptCommit=T10NVR .
```

`SptBuildType=RELEASE` turns mods on. The server refuses to load mods when the build type is
`BLEEDINGEDGE`.

## 2. Build the exporter

The exporter reads every table after every mod has loaded and writes the layout of
`SPT_Data/database` (ADR-0027). The source is in this repository and it builds against an SPT
install, so it needs the assemblies of the image:

```bash
dotnet build src/SptArchive.Exporter -c Release -p:SptAssemblyPath=/tmp/sptapp
```

`/tmp/sptapp` is a copy of the assemblies of the image. Take them once:

```bash
podman run --rm --entrypoint /bin/sh localhost/spt-server:4.1.6 \
  -c "tar -C /opt/spt -cf - ." | tar -C /tmp/sptapp -xf - --strip-components=0
```

Install the result into the mod set:

```bash
mkdir -p /tmp/mods/all/SptArchive.Exporter
cp src/SptArchive.Exporter/bin/Release/net10.0/SptArchive.Exporter.dll /tmp/mods/all/SptArchive.Exporter/
```

The exporter must run in the container. It is not a mod in the catalog.

Build it against the assemblies of the release it will run on. A build against one release is
refused by another: the exporter built against 4.1.6 does not load on 4.1.5 ("was built for a
different version of SPT"). Take `/tmp/sptapp` from the image of the release you are exporting.

## 3. Lay out the mod set

One target mod version and its pinned dependencies, in `user/mods/<Name>/`.

```
user/mods/
  WTT-ServerCommonLib/     dependency set
  MassivesoftCore/         dependency set
  SptArchive.Exporter/     exporter
  WTT-Armory/              the target mod version
```

The download link of a mod is one redirect. A plain HTTP client gets it, and no browser is needed:

```bash
curl -s -o /dev/null -D - -A 'Mozilla/5.0' \
  https://sp-mod.com/mod/download/2246/wtt-armory/3.0.0 | grep -i '^location:'
```

## 4. Run the export

```bash
podman run --rm --userns=keep-id \
  -v /tmp/mods/sptrun:/opt/spt/user:Z \
  -e SPT_ARCHIVE_EXPORT_DIR=/opt/spt/user/export \
  -e SPT_IP=127.0.0.1 \
  localhost/spt-server:4.1.6
```

`--userns=keep-id` makes the container user the host user, so the export lands in a directory the
host can read and delete without `podman unshare`. Without it, `user/` ends up owned by the container
root and every later command needs `podman unshare chown -R 0:0`.

The container exits by itself. The exporter stops the process after it writes the files.

The run takes about four seconds. A run of thirty seconds or more that does not exit has a mod that
never finished loading.

## 5. Read the result

The export is a database directory, so it can be compared with the install (ADR-0027):

```
export/globals.json  server.json  settings.json  match.json
export/templates/    items, quests, handbook, prices, customization, achievements, ...
export/traders/      one directory per trader: base, assort, dialogue, questassort
export/locales/      languages.json, global/<code>.json, menu/<code>.json
export/bots/         base.json, core.json, types/<id>.json
export/locations/    base.json and one directory per map with base and six overlays
export/hideout/      areas, customAreas, customisation, production, qte, settings
```

The first export of 4.1.6 with no mod is 642 MB. `locations/lighthouse/looseLoot.json` alone is
126 MB.

With `--userns=keep-id` the files belong to the host user. Without it, take the volume back first:

```bash
podman unshare chown -R 0:0 /tmp/mods/sptrun
```

### `run/`, the account of the run

Since exporter 1.2.0 the export also holds `run/` (ADR-0029); 1.3.0 adds `release.json`:

| Path | What it holds |
| --- | --- |
| `run/loaded.json` | one entry per loaded mod: `directory`, `modGuid`, `name`, `author`, `version`, the declared SPT range, its declared dependencies and `hasPrepatcher` |
| `run/files/<directory>.json` | the manifest of that mod directory: one entry per file with `path`, `size`, `sha256` and `kind` (`assembly`, `bundle`, `data`, `config`, `text`, `other`) |
| `run/config/<directory>/<path>` | the text of every configuration file, copied as bytes, comments included |
| `run/pre-boot.json` | written by `run_mod.sh` before the container starts: the same manifest of `user/mods`. A file whose two hashes differ was rewritten by a mod during its load. |
| `run/origin.json` | written by `run_mod.sh` when `CURATED_REASON` is set: `shipped` or `curated`, with the reason (ADR-0029, decision 7) |
| `run/routes.json` | since exporter 1.4.0: every HTTP route the process answers, with `url`, `method`, `order`, `router`, `assembly` and the `mod` GUID that owns the assembly (ADR-0031) |
| `run/release.json` | since exporter 1.3.0: the release the run was made on, read from the server assembly (`4.1.6-RELEASE+T10NVR.0000000000` is `4.1.6`), with the informational version and the assembly name |

A configuration file is one whose path has a segment `config`, `configs`, `presets` or `loader`, or
whose name contains `config`, `settings`, `preset`, `option` or `loader`, or whose extension is
`.jsonc`. Above 2 MB it stays in the manifest and is not copied. The rule is
`ModFileKind.IsConfigFile`, one file compiled into the exporter and into the Archiver.

A curated run keeps the mod directory name, or every path in its manifest would differ from the
shipped run by the name alone:

```bash
MOD_DIR_NAME=LotsOfLootRedux \
CURATED_REASON="The overlay is the feature; the shipped preset has AllowLootOverlay = false" \
  ./run_mod.sh LotsOfLootRedux-Curated
```

### Pair a run with its baseline

The Archiver compares a mod run against a **baseline** of the same release: the same container, the
same dependencies, no target mod. The baseline is a fact about the host and not about the mod, so it
is configuration and not a file inside the export:

```bash
export Archive__BaselinePath=/home/yui/work/sptmods/exports/baseline
```

The Archiver refuses a baseline whose `run/release.json` states another release, and says that a
difference between two releases is not the work of a mod. A baseline exported before 1.3.0 states
no release; it is used and the run reports that it verified nothing.

## Routes

`run/routes.json` answers "which endpoints does this mod add", and it needs no baseline:
the owning assembly is the answer (ADR-0031). Two kinds exist.

- **Adds.** The URL is new. `ECOTI` adds `/coti/hosts`, `MapVariants` adds
  `/mapvariants/managed`, and the WTT library adds twelve, including `/files/image`.
- **Intercepts.** The URL already belongs to the server. `Gunwright` answers
  `/client/game/profile/items/moving`, `WTT-Armory` answers `/client/friend/list`.

`HttpRouter` runs every router that matches and keeps the last answer, so `order` decides the
winner. In the archived runs `WTT-Armory` comes after the core router (7, then 23) and replaces
the answer; `Gunwright` and `MapVariants` come before it (0, then 15), so they see the request and
the core answer stands.

A run without a mod has 178 routes. A mod that answers nothing — `LotsOfLootRedux`,
`AllTheClothes`, `Ledger`, `SVM`, `MassivesoftWeapons`, `FemalePMCVoice` — leaves the count
unchanged, which is a measured fact about the mod and not an absence of evidence.

Do not call these routes to get data. `/client/items` of a running server returned the same 4673
items, with the same fields and the same key set, as `templates/items.json` of the same run; the
mod routes answered `{}` or `[]` without a profile. The archive stores tables, not answers.

## Problems

**A mod is rejected before the load.** The log prints one line and no mod loads at all, so the
exporter does not run and there is no export:

```
Mod: Manimal-ManimalIcebreaker requires: com.wtt.contentbackport to be ...
```

A missing dependency is not a partial run; it is no run (TD-36, ADR-0021). `run_mod.sh` stops the
container as soon as it sees the line and exits 1 with `NO EXPORT`. Add the dependency, and repeat
until the chain is complete.

**The exporter itself is rejected.** `Mod `SptArchive.Exporter` was built for a different version of
SPT and was not` — the exporter is built against one release's assemblies. Rebuild it against the
image you are exporting (`/tmp/sptapp` from that image), then run again.

**A mod does not load.** The log prints `not loaded. Reason: ...` and the run continues with the other
mods. Two reasons appear often:

- A core assembly newer than the image: `Could not load file or assembly 'SPTarkov.Server.Core,
  Version=4.1.6.0'` on a 4.1.5 image. Build the image for that release.
- A type that the current release no longer has, from a mod built for an older release:
  `Could not load type 'SPTarkov.Server.Core.Models.Spt.Mod.AbstractModMetadata'`. The version gate
  only rejects a mod that references a *newer* core. Use the patched build of that mod (ADR-0021).

**`Could not find bundle ...`** The run has no bundles. The server logs one line per bundle and
continues. The export is complete without them. A mod that builds its items from bundles produces no
items, which is TD-28.

**The volume is not writable from the host.** The entrypoint changes the owner of `user/` to the
container user on every start. Run the `podman unshare chown` command above.

**No items from a mod that loaded.** Check that the mod ships data and not only code. A code-only mod
reads its data from a dependency, from bundles, or from the mod it patches.

**A mod named "Update Patch".** It is a patch (ADR-0021). It does nothing without the base mod, and
the Forge does not declare that dependency. Put the base in the mod set beside the patch, or the run
returns an export equal to the baseline. MassivesoftWeapons is the example: page 3127 patches page
2588, which is built for 4.0.13.

**The download is not the version the page names.** Check the DLL, not the page. The download endpoint
of page 3127 "2.0.1" served `MassivesoftWeapons-v1.1.zip` (26,362 bytes), whose DLL states 1.1.2 with
GUID `com.massivesoft.massivesoftweapons`, while the page states GUID
`com.massivesoft.massivesoftweapons.415` (TD-30). Read the version and the GUID from the DLL, and keep
the SHA-256 of the archive.

## Runs recorded on 2026-10-20

Image `localhost/spt-server:4.1.6`, dependency set `WTT-ServerCommonLib 3.0.6` + `MassivesoftCore
1.0.2`, baseline = that set without a target.

| Target | Items | Clothes | Quests | Traders |
| --- | --- | --- | --- | --- |
| baseline | 4,673 | 728 | 558 | 12 |
| WTT-Armory 3.0.0 | +833 (461 modified) | 0 | +14 | 0 |
| WTT-PackNStrapServer 2.1.1 | +49 (4 new category nodes) | 0 | 0 | 0 |
| WeebSights 1.4.1 | +21 | 0 | 0 | 0 |
| All The Clothes 3.2.0 | 0 | **+104** | 0 | 0 |
| WTT The Long Lost Heads 2.1.0 | 0 | **+15** | 0 | 0 |
| WTT W.A.A.C. 2.1.0 | +2 | **+65** | 0 | 0 |
| female pmc voice USEC 1.0.6 | 0 | +1 | 0 | 0 |
| Gunwright 1.3.0 | 0 | 0 | 0 | +1, 579 offers |
| Ledger 1.1.0 | 0 | 0 | +45 | +1, 36 offers |
| MassivesoftWeapons 1.1.2 (patch alone) | 0 | 0 | 0 | 0 |
| MassivesoftWeapons base 1.1.2 (4.0.13 build) | not loaded: missing `AbstractModMetadata` | | | |

## Runs recorded on 2026-10-20, full database

Image `localhost/spt-server:4.1.6`, exporter 1.1.0, dependency set `WTT-ServerCommonLib 3.0.6` +
`MassivesoftCore 1.0.2`.

| Target | Export | What the dump shows |
| --- | --- | --- |
| baseline, no mod | 642 MB | 4,673 items, 558 quests, 31,549 translations, 12 traders, 57 bot types, 19 locations, 6 hideout parts |
| Factory Classic 1.0.2 + Map Variants 1.0.3 | 642 MB | `factory4_day` and `factory4_night` replaced, 7 files each |
| Arena Map Port 0.2.3 | 642 MB | no arena map: the mod needs a real Escape from Tarkov: Arena install (TD-37) |
| Lots of Loot Redux 4.2.2 | 642 MB | no change of a table. `Presets/default.json` holds `LooseLootMultiplier.* = 7.5` and `StaticLootMultiplier.* = 2`, but also `General.AllowLootOverlay = false`, so the mod had nothing to apply. The archive keeps the preset and the reason (ADR-0029, TD-40). |
| Icebreaker 1.1.3 | none | rejected before the load: it needs `com.wtt.contentbackport`, then `com.morebotsapi.tacticaltoaster` (TD-38) |

## Runs recorded on 2026-10-20, with `run/`

Image `localhost/spt-server:4.1.6`, exporter 1.2.0, dependency set `WTT-ServerCommonLib 3.0.6` +
`MassivesoftCore 1.0.2`.

| Target | Export | `run/` | What the dump shows |
| --- | --- | --- | --- |
| baseline, no mod | 642 MB | 3 mods loaded, 8 files | the same 4,673 items as the install; pre-boot and post-boot hashes are equal, so no mod rewrote a file |
| Lots of Loot Redux 4.2.2, shipped | 642 MB | 4 files, 2 configuration files | no table change. The manifest holds `Presets/default.json` with `AllowLootOverlay = false`, and it is stored as text, so the empty diff has its reason next to it |
| Lots of Loot Redux 4.2.2, curated (`AllowLootOverlay = true`) | 642 MB | 4 files, 2 configuration files, `origin.json` says `curated` | still no table change, and the two manifests differ in exactly one file: `Presets/default.json` (5,294 → 5,293 bytes). The mod applies its multipliers when a raid is generated, so it is a behaviour mod and not a data mod (ADR-0028). Recording it as such is the point of the curated run. |
| ECOTI 3.4.0 | 642 MB | 46 files, 2 configuration files | a config-driven dataset: **+9 items**, **+9 Peacekeeper assort rows**, **+27 English translations**, and changed `prices.json` and `handbook.json`. `config/config.json` decides them (`trader.priceUsd`, `loot.weightFraction`, `loot.enabled`) |

Re-ran ECOTI with exporter 1.3.1 (same day, same contribution: +9 items, +9 assort rows, +27
translations, so the exporter change moved no table). The run now names its release
(`release.json` → `4.1.6`) and the manifest classifies **two** configuration files, not three:
`wwwroot/vendor/GLTFLoader.js` used to count as configuration because its name contains "loader".
A file under a web root is served to a browser, so the rule now checks the web root first
(ADR-0029, amendment of 2026-10-20).

ECOTI's own `config.json` has the same pre-boot and post-boot hash, so ECOTI 3.4.0 reads its
configuration and does not rewrite it. Its server code contains a `File.WriteAllTextAsync` path, but
the run did not take it. This is the case the pre-boot snapshot exists for: the next release of a mod
that does rewrite its file shows the difference without anyone reading its source.

## Two runs of one release are not equal

Two runs of the baseline, no mod, 40 minutes apart: 13 files differ.

- `traders/<id>/base.json` → `.nextResupply`: the clock of the next restock.
- `traders/<id>/assort.json` → `.items[*]._id`: a new `MongoId` for every assort item at every
  boot. The same ids key `barter_scheme` and `loyal_level_items`, so the shape is stable.
- `locations/lighthouse/base.json` and `locations/woods/base.json` →
  `.BossLocationSpawn[1].BossChance`: the boss event rotates (`35` and `0`).

A comparison must therefore read a canonical form (ADR-0028, TD-39). A byte diff of two runs
shows noise that no mod made.

## Keep the data, drop the assets

An artifact is mostly assets. WTT - Content Backport 2.0.5 is 6.1 GB compressed; its `db` tree, its
server `.dll` and its `BepInEx/config` are **127 MB**. Unpack only what the server reads:

```bash
7z x -o2512 2512.bin \
  "SPT_Runtime/user/mods/WTT-ContentBackport/db/*" \
  "SPT_Runtime/user/mods/WTT-ContentBackport/*.dll" \
  "SPT_Runtime/user/mods/WTT-ContentBackport/*.json*" \
  "BepInEx/config/*"
```

Then delete the rest of the unpacked tree and the archive:

```bash
find <unpacked> -type f \( -name '*.bundle' -o -name '*.png' -o -name '*.obj' -o -name '*.jpg' \
  -o -name '*.dds' -o -name '*.wav' -o -name '*.ogg' -o -name '*.fbx' \) -delete
```

Measured on 2026-10-19: eleven mods took 14.7 GB and take 54 MB after the cleanup. The exports stay
(1.1 GB for ten runs). A cleaned artifact is enough to classify a mod (ADR-0023), to read its `db`
fragments, and to run it: a bundle that is gone only removes what ADR-0020 already records as out of
reach (TD-28).

Re-download takes about an hour for the five large mods, so keep `db`, `.dll` and config, and expect
to fetch bundles again if a run needs them.
