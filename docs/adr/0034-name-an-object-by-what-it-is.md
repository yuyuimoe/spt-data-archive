# ADR-0034: Name an object by what it is

**Status:** Accepted
**Date:** 2026-10-20
**Deciders:** Yui

## Context

The archive names every object with an id that the server gives it. A mod-added item is named by its `_id`. An offer is named by its `_id`. A translation is named by the entity id inside its key. The archive treats that id as a stable fact about the object.

The driver named the limit: an id can be made at run time. A mod can call `MongoId.New()` while it builds an item. A mod that clones an item asks the server for a new id. The loader runs before the export. So an id is one of the things a run produces. It is not a fact about the object.

Measured on the 4.1.6 runs:

| Fact | Measurement |
|------|-------------|
| The server makes new instance ids on every boot | ADR-0028: two boots of one install differ in 5,090 `_id` values over 12 assort files |
| Most mods ship their ids as text | WTT-Armory: all 833 added item ids appear verbatim in the mod `db` files |
| A mod can ship no item file and still make items | ECOTI ships no item JSON. Its 9 items carry mint times from 2026-08-14 to 2026-10-19, and the export ran at 2026-10-20 19:12 |
| Authors also write ids by hand | 53 of those 833 ids are 24 hex characters with no valid MongoId time, such as `c556f40e…` (year 2076) |
| Nothing stops a run-time id | every server mod of the 19-run collection names the `MongoId` type |

So the archive has no evidence that an id is stable. It has a habit of assuming it.

Two breakages follow from that habit:

1. A mod that makes a new item id on each boot makes every later run look different. The content hash moves, so ADR-0029 stores a second dataset of the same content.
2. The offer key of ADR-0032 avoids one instance id. It still trusts the trader id and the item id. A mod that sells an item it made at run time puts a run-time id into that key.

## Decision

**We will name every mod object by an address that the archive derives, and keep the id of the run only as a lookup.**

1. **Two kinds of address.** An object that the baseline of the release holds is *pinned*: its address is its own id, because the baseline holds that id unchanged. An object that is new in this run is *derived*: its address is a content ULID.

2. **A content ULID names a derived object.** A ULID holds 26 Crockford characters and 128 bits. The time half is zero, because the archive does not know a birth time and must not state one. The 80 random bits are the first 10 bytes of `SHA-256(seed)`. One seed gives one id in every run. The id is stored in a `uuid` column, because the 128 bits map one to one. The API prints the ULID text.

3. **The seed holds identity and never an instance id.** The seed is `"sptarchive:v1|" + kind + "|" + identity fields`. The identity fields per kind are:

   | Kind | Identity fields | Measured |
   |------|-----------------|----------|
   | Item | `_name` | 833 added items, 826 distinct names |
   | Quest | `QuestName` | 14 added quests, 14 distinct names |
   | Customization | `_name` | 104 added customizations, 104 distinct names |
   | Trader | its directory name | 12 traders in the baseline |
   | Location, bot type | its file name | 13 locations, 57 bot types |

4. **A tie is broken by content, then by order.** The seed gains the canonical hash of the object without its ids. When several objects still match, the seed gains their index in canonical order.
   - Measured: WTT-Armory holds 7 name groups of 2. Each pair differs in the prefab path and nowhere else in content.

5. **No id enters a seed or a hash.** Each string leaf that equals a known id of the run becomes `#id`. The own id of the object becomes `#self`. A volatile path (ADR-0028) becomes `#volatile`. A boot that makes new ids therefore changes no address and no hash.

6. **A change value carries an address.** A change path and its old and new value name an object by its address. A path that names a payment template names the address of that template and not its instance id.

7. **The raw ids stay next to the addresses.** The jsonb keeps the object as the run wrote it (ADR-0002). A new table `mod_id_map` holds one row per raw id of a dataset: the kind, the raw id and the address. The map is not part of the content hash, because a boot changes the map by nature.

8. **The canonical form sorts assort items by address.** This replaces decision 3 of ADR-0028, which renumbers the instance ids by position. An offer that arrives at position 0 then shifts no other offer.

9. **A run that makes its own ids reports that fact.** An added object whose id was minted after the run started becomes a warning. The import does not fail. It states the fact.

10. **An official table keeps the raw id.** An official row belongs to a release (ADR-0014). The baseline of that release holds its id, so the row is pinned by definition.

## Options Considered

| Dimension | A: address of the content (chosen) | B: the id the run gives | C: the id, repaired by a comparison |
|-----------|------------------------------------|-------------------------|-------------------------------------|
| Complexity | medium: one function, one table | none | high: a repair pass |
| Stability | one object, one name, every run | lost on a run-time id | lost until the second run |
| Cost of a change | a new table and a migration | none | a comparison of two runs, and a guess |
| Answer quality | names the object that changed | names an object that may not exist twice | names the object, unless two are equal |
| Durability | the address survives the run | survives the run | needs the previous run |

Option B loses because the measurement shows that the server itself renews instance ids every boot. The archive would store "all offers changed" after each restart.

Option C loses because the repair pass guesses. Two added items can hold the same content, so "the object whose id moved" is ambiguous. It also needs the previous run, which ADR-0027 says may be deleted.

## Trade-off Analysis

The main trade-off is readability against stability.

An address is not the id that a server log prints or that a mod author recognises. A reader must pass through `mod_id_map` to reach that id. A rename of a derived object also makes it a new object, because the name is its identity.

For this project the trade is right. The archive exists to say what a mod contributes. A dataset that changes because the server restarted says nothing about the mod. The cost is one table and one function, and it is paid once.

## Consequences

- **Easier:** a rerun of the same mod content reuses the dataset. "Same content" stops meaning "the same ids".
- **Easier:** an offer keeps its name when another offer arrives first.
- **Easier:** a stored id is comparable across releases and across mod versions.
- **Harder:** every stored id becomes an address, so the fixtures and the tests that assert a raw id change.
- **Harder:** a reader needs `mod_id_map` to reach the id of a log line or a mod file.
- **Harder:** a mod that renames an item reads as one removal and one addition.
- **Revisit when:** a mod ships an item with no `_name`, or a query needs the raw id on the hot path.

## Verification

```gherkin
Feature: Name a mod object without the id the run gave it

  # ADR-0034 decision 2 and 5. One seed, one id, in every run.

  @unverified
  Scenario: A boot that makes a new item id keeps one dataset
    Given the mod "mod-x" builds the item "anpas29b_coti_clip_on_thermal_imager" with a new id on every boot
    When I archive two runs of that mod on the same release
    Then both runs name the item with one address
    And the second run shares the dataset of the first run

  # ADR-0034 decision 1. An object of the baseline keeps its own id.

  @unverified
  Scenario: An official object keeps its own id
    Given the run changes the weight of the item "5449016a4bdc2d6f028b456f" of the baseline
    When I read that item of the dataset
    Then its id is "5449016a4bdc2d6f028b456f"
    And its operation is "modified"

  # ADR-0034 decision 7. The raw id of the run stays readable.

  @unverified
  Scenario: The raw id of a run stays next to the address
    Given the run names an added item with the id "6ad5775e327076f93b3b36bf"
    When I read the id map of the dataset
    Then one row names that raw id and the address of the item
    And the jsonb of the item keeps the raw id

  # ADR-0034 decision 4. One name, two objects, two addresses.

  @unverified
  Scenario: Two objects with one name get one address each
    Given the mod adds two items that hold the name "[WTT-Armory]_(Magpul MBUS Gen3 flip-up rear sight)"
    And their content differs only in the prefab path
    When I read the added items of the dataset
    Then the two items hold two addresses
    And a second run of the same content gives the same two addresses

  # ADR-0034 decision 6. A change value never carries an instance id.

  @unverified
  Scenario: A change value names an address
    Given the run adds the template "T7" to a slot filter of an official item
    And the run gives "T7" a new instance id on every boot
    When I read the change path of that filter
    Then the new value names the address of "T7"
    And a second run writes the same value

  # ADR-0034 decision 8. Position stops naming an offer.

  @unverified
  Scenario: An offer keeps its name when another offer arrives first
    Given the trader "TR1" holds the offer of the template "T1" paid in roubles
    When a mod adds another offer at the first position of the assort of "TR1"
    Then the offer of "T1" keeps its template id
    And the added offer holds the operation "added"

  # ADR-0034 decision 9. The run states that it made an id.

  @unverified
  Scenario: A run that makes its own ids reports that fact
    Given the run starts at "2026-10-20T19:12:00Z"
    And an added item carries an id minted at "2026-10-20T19:13:00Z"
    When I read the result of that run
    Then the result holds a warning that names the item
    And the import did not fail
```

## Action Items

1. [x] Add `ContentUlid` to `SptArchive.Core/Canonical`, with the seed rules of decision 2 and 3. Done: `src/SptArchive.Core/Canonical/ContentUlid.cs`, `ContentUlidTests` pins three vectors against a second implementation and refuses a timed id.
2. [x] Add the addressing pass to `RunDiff`: a pinned id keeps its id, a new object gets a content ULID, a tie adds the content hash, a remaining tie adds the ordinal. Done: `Addressing` holds the entries and the warnings. The rows are keyed by the address, and the hash covers the canonical form, so the minted id never enters it (`RunDiffTests`).
3. [x] Add `mod_id_map` and write one row per raw id of a dataset from `RunImporter`. Done: migration `20261020203542_AddModIdMap`; the map of `ECOTI` holds 5,980 rows (4,682 items: 4,673 pinned and 9 derived) and the map of `WTT-Armory` 6,818.
4. [x] Replace the positional renumbering of `CanonicalForm` with the address sort (amends ADR-0028 decision 3). Done: `CanonicalForm` no longer reads an assort, so nothing is renumbered by position. `Canonical.Assort` names each offer by its trader, its item and its payment channel, and orders the file by that address. Measured on 4.1.6: one trader holds 5,090 instance ids in the baseline and 5,099 in the run, and the two sets share none. The diff pairs its 6,131 offers with 0 changes.
5. [x] Render ids as addresses in `mod_change_paths` and in the offer key of ADR-0032. Done: the old and new values of a change are read from the canonical form. The 733 paths of `WTT-Armory` therefore name the 833 added items by address. An offer is a template too. Its id is the content ULID of decision 3, over the three addresses of ADR-0032 decision 4.
6. [x] Warn when an added id carries a mint time after the start of the run (decision 9). Done: `MintTime` compares the mint time of every unpinned raw id with `archive_runs.started_at` of the Archiver run. A warning appears in the log and in `result.warnings`, and the import does not fail. Limit: the start is that of the Archiver run, not that of the container boot. A boot that ran earlier than the import is not caught, because the export does not record when the server started.
7. [x] Update the fixtures and the tests that assert a raw id; recount the test total in the docs. Done: 220 tests.
8. [ ] Name a location and a bot type by its file name, as decision 3 says, when their rows arrive (TD-45). Today the addressing covers items, quests, customizations and traders.

## Links

- ADR-0002 (the jsonb keeps the source), ADR-0013 (a dataset holds one mod version), ADR-0014 (an official row belongs to a release).
- ADR-0027 (the run directory is transport), ADR-0028 (canonical form, decision 3 amended here), ADR-0029 (content hash), ADR-0032 (the offer key uses this address).
- `src/SptArchive.Core/Canonical/CanonicalForm.cs`, `src/SptArchive.Core/Canonical/RunDiff.cs`, `src/SptArchive.Archiver/Runs/RunImporter.cs`.
- `docs/tech-debt.md` (TD-48), `docs/runbooks/mod-export.md`.
