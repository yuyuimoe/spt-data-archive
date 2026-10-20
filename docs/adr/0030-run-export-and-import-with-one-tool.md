# ADR-0030: Run, export and import with one .NET tool

**Status:** Proposed
**Date:** 2026-10-20
**Deciders:** Yui
**Related:** ADR-0013, ADR-0014, ADR-0020, ADR-0021, ADR-0027, ADR-0028, ADR-0029

## Context

Every mod export that the archive has was made by `/tmp/mods/run_mod.sh`. That file is 90 lines
of bash plus two inline `python3` programs, and it is in `/tmp`. It is not in Git. It is not
tested. One `rm -rf /tmp` deletes the tool that made the evidence for ADR-0021, ADR-0027 and
ADR-0029, and the fixtures that the tests compare against.

The script needs three runtimes to make one run:

| Step | Runtime | What it does |
| --- | --- | --- |
| copy the mod set, start the container, wait for the log line | bash | `podman run`, then poll the log |
| the pre-boot manifest | python3 | walk the mod directory, SHA-256 every file, write JSON |
| the origin of a curated configuration | python3 | write `run/origin.json` |

The C# code reads the same JSON that the python3 part writes. `RunDirectory` parses
`pre-boot.json`, `files/<mod>.json` and `origin.json`. `ModFileKind` says what a file is, and the
exporter links it so the two agree. So one rule is implemented twice, in two languages, and only
one of them has tests.

The run also depends on facts that no single place states. The release is a `-p:` build flag for
the exporter and a `-e` for the Archiver. The image name has an inline default of `4.1.6`. The
baseline path is a host setting that nothing sets. `run/release.json` was added by ADR-0029
because a directory name cannot be trusted to say which release made it.

The driver asked for a `.env` file that the runner reads and never writes, as the one place that
describes the environment. The driver also asked to move the runner into .NET, so that the
project has one ecosystem.

A child process cannot set the environment of its parent. A script that exports
`Archive__BaselinePath` therefore cannot hand it to a later `docker compose run --rm archiver`.
That is the second reason to move the runner into the process that also does the import.

## Decision

**One tool launches a run, exports it, and imports it.** A new console application,
`SptArchive.Runner`, joins the solution. It has these verbs:

| Verb | What it does |
| --- | --- |
| `export <mod>` | Copy the dependency set, the exporter and one target mod into a fresh run directory, write the pre-boot manifest, start the container, wait for the export, keep the log. |
| `baseline` | The same, with no target mod. One baseline per release (ADR-0013). |
| `import <run-dir>` | Read the run directory against its baseline and write the dataset rows (ADR-0029). |
| `runs` | List the runs on disk: release, mods, whether an import has happened. |

Options of `export`: `--curated-reason <text>`, `--as <dir-name>` (the curated run keeps the mod
directory name), `--release <version>`, `--extra <mod>`, `--dry-run`. A `--dry-run` prints the
exact `podman` argument vector and writes nothing, so the run stays inspectable by hand.

**`.env` at the repository root is the environment.** The Runner reads it. Compose reads it. The
Runner never writes it. `.env` stays ignored by Git (rule 11) and `.env.example` is the
committed contract.

| In `.env` (the environment) | An argument (one run) |
| --- | --- |
| `ARCHIVE_OWNER_PASSWORD`, `ARCHIVE_READER_PASSWORD` | `--curated-reason` |
| `SPT_DATA_PATH` | `--as` |
| `ARCHIVE_MODS_DIR`, `ARCHIVE_EXPORTS_DIR`, `ARCHIVE_BASELINES_DIR` | `--force` |
| `SPT_RELEASE` (the default release) | `--release` |
| `SPT_IMAGE` (the container image of that release) | `--baseline` |
| `API_PORT` | `--extra` |

The split follows from "read, never write". A value that changes per run and lives in the file
would make the Runner a writer, and would make the file a log that two runs race on.

The Runner checks that the image and the release agree: the tag of `SPT_IMAGE` must end in
`SPT_RELEASE`. A mismatch fails before the container starts, and names both.

**The Runner passes values down; it does not set variables.** It is the parent of the container,
so it passes `-e SPT_ARCHIVE_EXPORT_DIR=...` on the command line and never inherits the ambient
environment. It calls the import in its own process, so a mod run needs no
`Archive__SptVersion` and no `Archive__BaselinePath` at all. Those two settings stay for the
compose path, where the Archiver is a container that only has its environment.

**The pre-boot manifest and `origin.json` move to C#.** `RunDirectory` gains the two writers. The
JSON formatting is not part of the contract; the entries and the hashes are. The existing exports
are the golden files: a run of the same file set must produce the same list of paths, sizes and
hashes as the script produced.

**The import becomes a callable module.** `ArchiveJob` keeps its place as the hosted service that
compose starts. The logic moves into an `ArchiveRunner` that takes the resolved options and the
source directory and returns an outcome. The Runner and the hosted service call the same module,
so there is no second import path.

**The shell script is retired.** When `export` and `import` reach parity, `run_mod.sh` is deleted
and its podman invocation moves into `docs/runbooks/mod-export.md` as the debugging form.

## Options Considered

### Option A: keep bash, source `.env` at the top of the script

The smallest change. The script gets a `set -a; . .env; set +a` line and a documented contract.
Rejected. It keeps three runtimes and two implementations of the classification and manifest
rules. A sourced `.env` also becomes the parent environment of the shell, which is the same
"who sets the variable" problem in a different place, and it leaks every secret in the file to
every child of the script.

### Option B: keep bash, move `run_mod.sh` into `scripts/`

Solves provenance: the file is in Git and reviewed. Rejected as the whole answer. It does not
remove the duplication, does not give the manifest rules tests, and does not answer 4b. It is a
step that this ADR includes, not the destination.

### Option C: one console application with verbs, `.env` read-only, containers by `Process`

Chosen. One runtime for the run. The rules that the reader needs are the rules the writer uses.
The tool is the parent of the container and the caller of the import, so the value reaches both
without an environment variable. It is testable with the same xUnit project, and the nineteen
real exports are the regression test.

### Option D: let the Archiver start the containers

Rejected. The Archiver owns the official tables (rule 3) and runs inside the deployment stack.
Giving it container control mixes deployment with data, and its image would need `podman` and a
socket. A separate tool keeps the Archiver a job that reads a directory.

### Option E: a .NET container library (Testcontainers and similar)

Rejected. Those libraries exist to give tests a container. The runner must start a container,
follow a log line and stop it, outside a test. `Process` is enough.

## Trade-off Analysis

| | Bash + python3 (today) | One .NET tool |
| --- | --- | --- |
| Runtimes to make a run | bash, python3, podman | dotnet, podman |
| Classification and manifest rules | written twice, tested once | one implementation, tested |
| Where the environment is stated | flags, inline defaults, a shell default | one file, read by the tool |
| A value for the import step | cannot cross the process boundary | passed as a call |
| Volume of code for the container step | about 20 lines | more; `Process`, log tailing, timeouts |
| Reading a failing run by hand | the script is the run | needs `--dry-run` and the runbook |

The cost is real: about 500 lines of C# to replace 90 lines of bash and 40 of python3. The gain
is that the archive's provenance tool is in Git, reviewed, and tested against its own output.
`--dry-run` keeps the shell-level readability that made the script easy to debug.

The Runner must still build the exporter against the assemblies of the release it will run on: a
4.1.6 build is refused by a 4.1.5 container. That stays a build step. The Runner fails with the
exact `dotnet build -p:SptAssemblyPath=` command when the exporter is missing or rejected.

## Consequences

- A mod run becomes two commands that share one file: `sptarchive export ECOTI` and
  `sptarchive import <export-dir>`. Neither needs a value set by hand.
- `ArchiveJob` shrinks to a hosted wrapper. The import logic is callable and therefore testable
  without a host.
- The solution gains a fourth project and the C4 model gains a `runner` container next to the mod
  runner. The Api and the Archiver keep their roles.
- Secrets stay in `.env`. The Runner prints its resolved settings with every password masked, so a
  log can be pasted into an issue.
- TD-42 ("the runner that made every export is in `/tmp`") closes when the script is deleted.

## Verification

```gherkin
@adr-0030 @unverified
Feature: One tool makes a run, exports it and imports it
  The runner reads the environment from one file and takes one run's choices as arguments.

  Scenario: A run takes its release and its image from the environment file
    Given the environment file states release "4.1.6" and the image "localhost/spt-server:4.1.6"
    When I export the mod "ECOTI"
    Then the run's release.json states "4.1.6"
    And the container that ran was "localhost/spt-server:4.1.6"

  Scenario: An image of another release is refused before a container starts
    Given the environment file states release "4.1.6" and the image "localhost/spt-server:4.1.5"
    When I export the mod "ECOTI"
    Then the export fails and names both releases
    And no container started

  Scenario: A choice of one run is an argument, and the environment file is not touched
    Given the environment file has a recorded modification time
    When I export the mod "LotsOfLootRedux" as "LotsOfLootRedux" with a curated reason
    Then run/origin.json states "curated" with that reason
    And the environment file has the same content and modification time

  Scenario: The pre-boot manifest lists the same files as the export that the script made
    Given the mod set of the run "LotsOfLootRedux"
    When the runner writes the pre-boot manifest
    Then it lists the same paths, sizes and hashes as run/pre-boot.json of that export

  Scenario: The import takes the baseline from the environment file, and refuses a foreign release
    Given the environment file names a baseline directory of release "4.1.5"
    When I import a run of release "4.1.6"
    Then the import fails and says that the difference is not the work of a mod

  Scenario: A dry run starts nothing and writes nothing
    When I export the mod "ECOTI" with a dry run
    Then it prints the podman argument vector
    And no run directory is created
```

## Action Items

- [ ] Create `src/SptArchive.Runner` with the verbs `export`, `baseline`, `import`, `runs`, and
      add it to `SptArchive.slnx`.
- [ ] Read `.env` from the repository root, resolve the settings, print them with masked
      passwords, and fail when `SPT_IMAGE` does not name `SPT_RELEASE`.
- [ ] Write `run/pre-boot.json` and `run/origin.json` from `RunDirectory`, and test both against
      the exports that the script made.
- [ ] Extract `ArchiveRunner` from `ArchiveJob`, so the hosted service and the tool import through
      one module.
- [ ] Add `--dry-run` and the exact exporter build command to the failure message.
- [ ] Delete `/tmp/mods/run_mod.sh` when the verbs reach parity, and record the raw podman form in
      `docs/runbooks/mod-export.md`.
- [ ] Add the `runner` container to the C4 model and the row to `docs/adr/README.md`.
- [ ] Extend `.env.example` to the full contract, with placeholders only.

## Links

- [ADR-0013](0013-mod-ingestion-by-isolated-run.md): one throwaway container per mod version.
- [ADR-0014](0014-keep-every-archived-spt-release.md): every release is kept, keyed by `spt_version`.
- [ADR-0020](0020-export-mod-data-from-a-server-container.md): the exporter runs inside the server.
- [ADR-0021](0021-archive-a-patch-mod-with-its-base.md): a curated dependency set, base included.
- [ADR-0027](0027-archive-a-run-as-a-database-directory.md): the export is a database directory.
- [ADR-0028](0028-compare-two-runs-in-a-canonical-form.md): two runs are compared in canonical form.
- [ADR-0029](0029-archive-mod-files-and-config.md): the run directory, `mod_files`, `config_origin`.
- [Runbook: export a mod](../runbooks/mod-export.md): the commands the tool replaces.
