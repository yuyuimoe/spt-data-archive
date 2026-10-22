# ADR-0045: Record where a file of a run came from, and serve the link

**Status:** Accepted
**Date:** 2026-10-21
**Deciders:** Yui

Ratified by Yui on 2026-10-21. The exporter release that fills the field is action item 1, and no run of
the archive states a source until it exists.

## Context

ADR-0041, amendment 1 refuses a row for a file above `RawJson:MaxValueBytes`, and ADR-0043 splits a
loose-loot file so that its objects stay readable. Both leave one question open: a client that wants the
file whole has nowhere to read it.

The measurement of 2026-10-21 says how often that happens. In the 19 exports of the development machine,
139 of the 668 distinct files are above 2,000,000 bytes, and they take 1,949 MB of the 2,028 MB of the
archive. So the largest part of what a mod changes is the part that the Api refuses, and the driver asked
for the link to the source in the answer.

A run states where it came from, partly. `run/target.json` names the mod guid and the reason of the run
(ADR-0033, decision 3, exporter 1.5.0), and `run/loaded.json` names the directory, the name, the author,
the version and the declared release of every dependency. Neither states a URL, and `mod_files` has no
column for one (TD-74).

Most mods come from GitHub, and a GitHub file has an address that holds a commit and a path, so a link to
the exact bytes is a link to the version of the run. A mod that comes from another place has whatever
address its author published, or none.

## Decision

**We will record the address and the commit that a run came with, store the link of each file, and serve it
beside the file.**

1. **The exporter states the source of the run.** Exporter 1.6.0 writes `source` and `sourceCommit` to
   `run/target.json` from `SPT_ARCHIVE_SOURCE_URL` and `SPT_ARCHIVE_SOURCE_COMMIT`. It states an empty
   source when the operator gave none, and it never guesses one.

2. **The Archiver stores the link of each file.** `mod_files` gains `source_url`, which is the source of the
   run, the commit when the run states one, and the path of the file inside the mod directory. A run with no
   source writes no link, and the column is null. The link is a fact of the run and enters no hash
   (ADR-0029).

3. **A dataset states where it came from.** `mod_datasets` gains `source_url` and `source_commit`, the
   values of the run that created it. A dataset that the archive already holds keeps them (ADR-0018).

4. **The Api serves the link.** `modFiles` and `modFile` answer `sourceUrl`, and `modDataset` answers
   `sourceUrl` and `sourceCommit`. A file above the limit of `rawJson` answers its `bytes`, its `sha256` and
   its `sourceUrl`, so a client reads the size and follows the link without reading a byte of the text.

5. **A refused link is a link to nothing.** The archive does not fetch the link, does not check it and does
   not copy the file. The link states where the run came from; it is not a promise that the address answers.

6. **No official table is touched** (ADR-0003).

## Options Considered

| Dimension | A. No link (today) | B. The link of the run, per file (chosen) | C. The archive downloads the file |
|---|---|---|---|
| A file of 132 MB that the Api refuses | Nothing | The size, the hash and the address of its source | A second copy of the file |
| Answers "which version of the mod wrote this" | No | Yes, when the run states a commit | Yes |
| Work | None | A column, a field and an exporter setting | A downloader, a store and a rule for a dead link |
| A mod that is not on GitHub | Nothing | Nothing, and the run states no source | The bytes, when it can reach them |

Option C is the archive of a source that the archive does not own. It costs a downloader, a place to keep
the bytes, and a rule for a link that dies, and it answers with bytes that TD-69 and TD-74 already refuse
for their size. Option B answers with an address that the mod author published.

## Trade-off Analysis

The trade-off is a promise the archive cannot keep against a link it can state. A link is a string that may
stop answering, and a client that follows it learns what the author does today and not what the run held.
The hash beside the link is the check: a client that downloads the file compares `sha256` with the column,
and a file that moved is a file that fails the comparison.

The other side is honesty. A run with no source writes no link, and the Api answers null. Most mods of the
development machine come from the forge of SPT, whose page states a download and a repository; a run
imported from a downloaded archive states whatever the operator gave, and a run with an empty environment
states nothing.

The cost is one column, one field and one setting of the exporter, and the exporter must be rebuilt against
the assemblies of the release it runs on, as ADR-0033 states.

## Consequences

- **Easier:** a client that wants a file the Api refuses follows one link and checks one hash.
- **Easier:** a dataset states the version of the mod it came from.
- **Harder:** the value of `source_url` depends on a setting that a run may not give, so a reader must hold
  null.
- **Harder:** the exporter must be released again (1.6.0) before any of it is filled.
- **Revisit when:** the archive holds the bytes itself (TD-69), and a link is no longer needed for a file
  that the archive can serve.

## Verification

```gherkin
Feature: The link to the source of a file

  @unverified
  Scenario: A run that states its source stores a link for each file
    Given a run whose target states the source "https://github.com/owner/repo" and the commit "abc123"
    When the Archiver archives the run
    Then the row of the file "Armory/package.json" states the address of that file at that commit
    And the dataset states the same source and the same commit

  @unverified
  Scenario: A run with no source stores no link
    Given a run that states no source
    When the Archiver archives the run
    Then the column "source_url" of each file is null
    And the Api answers null for the source of the dataset

  @unverified
  Scenario: A file above the limit answers its size, its hash and its link
    Given a dataset with a file of 132,164,983 bytes and its source
    When a client asks for that file
    Then the answer holds the bytes, the hash and the link
    And the answer holds no text
```

## Action Items

1. [ ] Exporter 1.6.0: read `SPT_ARCHIVE_SOURCE_URL` and `SPT_ARCHIVE_SOURCE_COMMIT`, write them to
   `run/target.json`, and state an empty source when they are unset.
2. [ ] Migration: `mod_files.source_url`, `mod_datasets.source_url` and `mod_datasets.source_commit`.
3. [ ] `RunDirectory` reads the two fields, and `RunImporter` writes the link of each file.
4. [ ] The Api serves the three fields, and the cost limit charges them as small fields.
5. [ ] Add the scenarios to `docs/specs`, and remove `@unverified` when the tests pass.
6. [ ] Fill the field of the runs that the archive already holds, if their release allows it.

## Links

- ADR-0029: the content hash. The link enters no hash.
- ADR-0033: the target of a run, and exporter 1.5.0.
- ADR-0040, amendment 1: the limit of `rawJson`.
- ADR-0041: the rows that a file above the limit does not get.
- ADR-0043: the split of a loose-loot file.
- ADR-0044: the other files that have no row.
- `docs/tech-debt.md`: TD-69, TD-74.
