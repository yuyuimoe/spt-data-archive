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

The exporter reads the tables after every mod has loaded and writes three JSON files.

```bash
cd /tmp/exporter
dotnet build -c Release
podman run --rm --entrypoint /bin/sh \
  -v /tmp/mods/sptrun:/opt/spt/user:Z \
  -v /tmp/exporter/bin/Release/net10.0:/src:Z \
  localhost/spt-server:4.1.6 \
  -c "mkdir -p /opt/spt/user/mods/SptArchive.Exporter && cp /src/SptArchive.Exporter.dll /opt/spt/user/mods/SptArchive.Exporter/"
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
podman run --rm \
  -v /tmp/mods/sptrun:/opt/spt/user:Z \
  -e SPT_ARCHIVE_EXPORT_DIR=/opt/spt/user/export \
  -e SPT_IP=127.0.0.1 \
  localhost/spt-server:4.1.6
```

The container exits by itself. The exporter stops the process after it writes the files.

The run takes about four seconds. A run of thirty seconds or more that does not exit has a mod that
never finished loading.

## 5. Read the result

```
export/templates.json    items and quests
export/traders.json      traders and their assorts
export/locales.json      locale entries
```

Files inside the volume belong to the container user. Read them as they are; they are
world-readable. To write to the volume from the host, take it back first:

```bash
podman unshare chown -R 0:0 /tmp/mods/sptrun
```

## Problems

**A mod does not load.** The ModValidator is all-or-nothing: one bad mod stops the whole set. The log
names the mod and the reason. A common reason is a core-assembly version above the image version, for
example `Could not load file or assembly 'SPTarkov.Server.Core, Version=4.1.6.0'` on a 4.1.5 image.
Build the image for that release.

**`Could not find bundle ...`** The run has no bundles. The server logs one line per bundle and
continues. The export is complete without them. A mod that builds its items from bundles produces no
items, which is TD-28.

**The volume is not writable from the host.** The entrypoint changes the owner of `user/` to the
container user on every start. Run the `podman unshare chown` command above.

**No items from a mod that loaded.** Check that the mod ships data and not only code. A code-only mod
reads its data from a dependency or from bundles.
