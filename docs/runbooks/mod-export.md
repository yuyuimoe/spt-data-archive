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

## Problems

**A mod is rejected before the load.** The log prints one line and no mod loads at all, so the
exporter does not run and there is no export:

```
Mod: Manimal-ManimalIcebreaker requires: com.wtt.contentbackport to be ...
```

A missing dependency is not a partial run; it is no run (TD-36, ADR-0021). `run_mod.sh` stops the
container as soon as it sees the line and exits 1 with `NO EXPORT`. Add the dependency, and repeat
until the chain is complete.

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
| Lots of Loot Redux 4.2.2 | 642 MB | no change of a table: its multipliers act when a raid is generated |
| Icebreaker 1.1.3 | none | rejected before the load: it needs `com.wtt.contentbackport`, then `com.morebotsapi.tacticaltoaster` (TD-38) |

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
