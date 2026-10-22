Feature: Keep mod data apart from official data
  As a maintainer
  I want every piece of mod data to belong to one archived mod version
  So that I can tell official data from mod data and keep them apart

  Background:
    Given a migrated archive database

  # ------------------------------------------------------ Schema guarantees

  # ADR-0013 keyed the mod tables by dataset, not by mod version: two versions with the same
  # content share one dataset, so the dataset is what a mod row belongs to.

  @unverified
  Scenario: A mod item must belong to a dataset
    When I insert a mod item with a dataset id that is not in the table "mod_datasets"
    Then the database rejects the row with a foreign key error

  # ADR-0018. Nothing deletes mod data in the first release. A dataset is always created
  # together with an observation, so a dataset with no observation is a bug, not a state.

  @unverified
  @backlog
  Scenario: An archive run does not delete a mod
    Given the mod "mod-x" in the version "1.0.0" has a dataset and an observation
    When an archive run succeeds
    Then the table "mods" still holds that mod version
    And the observation of that mod version still exists

  @unverified
  @backlog
  Scenario: A mod that leaves the catalog keeps its data
    Given the mod "mod-x" is no longer in the catalog
    When the Archiver synchronizes the catalog
    Then no row of that mod is deleted
    And the dataset of that mod version still exists

  @unverified
  @backlog
  Scenario: An orphan dataset fails the run
    Given the dataset "D1" of the mod "mod-x"
    And no observation points at "D1"
    When an archive run succeeds
    Then the run fails with a message that names "D1"
    And the table "mod_datasets" still holds "D1"

  @unverified
  @backlog
  Scenario: A successful run leaves no orphan dataset
    When an archive run succeeds
    Then every row of "mod_datasets" is pointed at by at least one observation

  @unverified
  Scenario: A manual removal of a mod does not touch official data
    Given the official item "A1" and the mod "mod-x" with the mod item "B1"
    When the operator removes the mod "mod-x" with the runbook statement
    Then the official item "A1" still exists
    And the runbook states that the dataset stays until a prune policy exists

  @unverified
  Scenario: Two mods can add the same item id
    Given the mods "mod-x" and "mod-y"
    When both mods add an item with the id "C1"
    Then both rows exist in the table "mod_items"

  @unverified
  Scenario: Two versions of one mod can be registered
    When I register the mod "com.example.mod" in the versions "1.0.0" and "1.1.0"
    Then both versions exist in the table "mods"

  @unverified
  Scenario: The same mod version cannot be registered twice
    Given the mod "com.example.mod" in the version "1.0.0" is registered
    When I register it again with the same version
    Then the database rejects the row with a unique constraint error

  @unverified
  Scenario: A mod can add stock to an official trader
    Given the official trader "T1"
    When the mod "mod-x" adds an assort row for the trader "T1"
    Then the table "mod_trader_assort_items" has that row
    And the table "trader_assort_items" has no new row

  @unverified
  Scenario: The official Archiver run does not touch mod rows
    Given the mod "mod-x" has a mod item "B1"
    When the Archiver imports the official data
    Then the mod item "B1" still exists

  # ------------------------------------------------- Dataset and observation
  # ADR-0013. A dataset is the content of one mod version. An observation is one
  # attempt to load that mod version on one server version.

  @unverified
  Scenario: A mod version states its content once
    Given the mod "com.example.mod" in the version "1.0.0"
    When the Archiver archives that mod version
    Then the table "mod_datasets" has one row for that version
    And that row states the content hash, the table hashes and the row counts

  @unverified
  Scenario: Unchanged content reuses the dataset
    Given the mod version "1.0.0" has the dataset "D1"
    When the version "1.1.0" of that mod exports the same content
    Then the observation of "1.1.0" points at the dataset "D1"
    And the table "mod_datasets" has one row for that mod

  @unverified
  Scenario: A mod version is observed on a server version
    Given the mod "com.example.mod" in the version "1.0.0"
    When the Archiver archives that mod version on the server version "4.0.13"
    Then the observation states the mod version "1.0.0" and the server version "4.0.13"
    And the observation points at the dataset of that mod version

  @unverified
  Scenario: The server version of a dataset is not part of the dataset
    Given the dataset "D1" of the mod "com.example.mod"
    When the archive observes that mod version on "4.0.13" and on "4.0.14"
    Then the dataset "D1" stays one row
    And two observations point at "D1"

  @unverified
  @backlog
  Scenario Outline: A mod that does not load is recorded as an outcome
    Given the mod "com.example.mod" in the version "1.0.0"
    When the Archiver runs that mod version and the result is "<outcome>"
    Then the observation states the outcome "<outcome>"
    And the observation points at no dataset

    Examples:
      | outcome                |
      | not_loaded             |
      | dependency_unresolved  |
      | outside_declared_range |
      | crashed                |
      | timeout                |

  # ADR-0021. A patch is a mod whose dependency set holds the mod it patches.
  # The Forge does not declare that dependency, so a human curates it.

  @unverified
  @backlog
  Scenario: A patch is archived with its base in the dependency set
    Given the base mod "com.example.weapons" in the version "1.1.2"
    And the patch "com.example.weapons.415" in the version "1.1.2"
    When the archive runs the patch with the base in its dependency set
    Then the observation of the patch states the base in the dependency set
    And the dataset of the patch holds only the rows the patch adds or changes

  @unverified
  Scenario: A run that changes nothing says so
    Given the mod "com.example.weapons.415" in the version "1.1.2"
    When the export of that mod version equals its baseline export
    Then the run stores a dataset with no template rows
    And the run logs that the mod version changed nothing

  @unverified
  Scenario: A mod that is silent is not a success
    Given the mod "com.example.mod" in the version "1.0.0"
    When the export of that mod version does not hold the mod GUID
    Then the run fails with a message that names the mod GUID
    And the observation points at no dataset

  # ---------------------------------------------------- Change to official data
  # ADR-0017. One row in "mod_templates" states the operation of a template.
  # The rows in "mod_change_paths" are the paths under that template.

  @unverified
  Scenario: A mod that changes an official item is archived
    Given a mod that changes the weight of the official item "A1"
    When the Archiver archives that mod on the server version "4.0.13"
    Then the table "mod_templates" has one row for "A1" with the operation "modified"
    And the table "mod_change_paths" states the path "_props/Weight"
    And that row states the old value from the official table of "4.0.13"
    And the official row of "A1" is not changed

  @unverified
  Scenario: A mod that adds an item is recorded as an addition
    Given a mod that adds the item "B1" with the parent "node-weapons"
    When the Archiver archives that mod
    Then the table "mod_templates" has one row for "B1" with the operation "added"
    And that row states the parent "node-weapons"
    And every path row of "B1" states no old value

  @unverified
  Scenario: A mod cannot add an item under the id of an official item
    Given the official item "A1"
    And a mod that writes the template id "A1"
    When the Archiver archives that mod
    Then the row of "A1" states the operation "modified"
    And no row states the operation "added" for "A1"

  @unverified
  Scenario: A new property of an old item is not an addition
    Given the official item "A1" has no property "_props/CanSellOnRagfair"
    When a mod sets that property to true
    Then the template "A1" states the operation "modified"
    And that path row states no old value

  @unverified
  Scenario: A mod that hides an official property is archived
    Given a mod that removes the property "_props/CanSellOnRagfair" of the official item "A1"
    When the Archiver archives that mod
    Then the template "A1" states the operation "modified"
    And the table "mod_change_paths" has a row with the path "_props/CanSellOnRagfair"
    And that row states the operation "remove"

  @unverified
  @backlog
  Scenario: The count of additions needs no official data
    Given the dataset "D1" adds "B1" and "B2" and changes "A1"
    When a client asks how many items that dataset adds
    Then the answer is 2
    And the query reads no official table

  @unverified
  @backlog
  Scenario: An added item states a parent that the baseline does not have
    Given a mod adds the item "B1" with the parent "node-weapons"
    And the official baseline has no node "node-weapons"
    When the Archiver archives that mod
    Then the template row of "B1" states the parent "node-weapons"
    And the archive answers that the parent does not resolve for a lone install

  # ------------------------------------- Change to data that has no mod table
  # ADR-0041. A location, a bot type, a hideout part, a price and a handbook entry are objects of
  # the server database that have no mod_* table. A change to one is a template row, its path
  # rows, and (for an added object) its source in "mod_database_objects". The rows come in addition
  # to the canonical hash of the file (ADR-0029, amendment 4), so the content hash does not move.

  @unverified
  Scenario: A change to an old location names the map and the value
    Given the baseline holds the location "woods" with "Insurance" true in its base file
    And a mod run sets "Insurance" to false
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "location" for "woods" with the operation "modified"
    And the table "mod_change_paths" states the path "/base/Insurance" for it
    And that row states the old value "true" and the new value "false"

  @unverified
  Scenario: A boss chance that rotates on every boot is not a change
    Given the baseline holds the location "woods" with a boss chance of 35
    And a mod run holds the boss chance 0 and changes nothing else of that map
    When the Archiver archives that run
    Then no row of the kind "location" exists for "woods"

  @unverified
  Scenario: A map that a mod adds is a location with its files
    Given a mod run holds the location "arena_airpit" and the baseline does not
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "location" with the operation "added"
    And its template id is a content ULID of the kind "location" and the name "arena_airpit"
    And the table "mod_database_objects" holds one row for each file of the map, named by its part
    And the table "mod_id_maps" pairs "arena_airpit" with that address

  @unverified
  Scenario: A change to a bot type names the file and the value
    Given the baseline holds the bot type "assault" with "mod_scope" 50
    And a mod run sets "mod_scope" to 80
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "bot" for "types/assault" with the operation "modified"
    And the table "mod_change_paths" states the path "/chances/weaponMods/mod_scope" with the old value 50 and the new value 80

  @unverified
  Scenario: A hideout part is named by its id when the baseline holds it
    Given the baseline holds the hideout recipe "R1" that asks for 2 of an item
    And a mod run asks for 3
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "hideout" for "R1" with the operation "modified"
    And the table "mod_change_paths" states the path "/requirements/0/count" with the old value 2 and the new value 3

  @unverified
  Scenario: A hideout part that a mod adds is named by what it is
    Given a mod run adds a hideout recipe and the id of that recipe is minted at boot
    When I archive two runs of that mod, and the second run mints another id
    Then both runs name the recipe with one content ULID
    And the id map of each run pairs its own raw id with that address

  @unverified
  Scenario: A price is named by the item it prices
    Given the baseline prices the item "A1" at 1000
    And a mod run prices it at 1200 and prices the added item "B1" at 4000
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "price" for "A1" with the operation "modified"
    And its path row is the whole value, with the old value 1000 and the new value 1200
    And the price of "B1" is named by the address of the item "B1"

  @unverified
  Scenario: A handbook entry names its category by address
    Given a mod run adds the handbook category "K2" and the handbook entry of the added item "B1" under it
    When the Archiver archives that run
    Then the table "mod_templates" has one row of the kind "handbook" for the entry of "B1"
    And that row states the address of "K2" as its parent
    And "mod_database_objects" holds the entry as the run wrote it

  @unverified
  Scenario: An item that the run minted is named by its address in every changed value
    Given a mod run adds the item "B1" with an id that the run minted
    And a bot type of that run holds the id of "B1" as a key and as a value
    When the Archiver archives that run
    Then the path of that key and the new value name the address of "B1"
    And a second run that mints another id for "B1" writes the same paths and the same values

  @unverified
  Scenario: The address of a change names the element and not its place in the array
    Given the item "B1" has the slots "a" and "b", in that order
    And a second mod run holds the same slots in the order "b" and "a"
    When both runs add the filter entry "X" to the slot "a"
    Then the table "mod_change_paths" holds the same path for both runs
    And that path names the slot "a" and holds no index of the slot

  @unverified
  Scenario: A list that only moves its elements makes no row, and the file hash still records it
    Given a mod run whose location file holds the same spawn points in another order
    When the Archiver archives that run
    Then the table "mod_change_paths" holds no row for that list
    And the canonical hash of that file is part of the content hash of the dataset

  @unverified
  Scenario: An array element with no identity keeps its index and the run warns
    Given an array whose elements have none of the identity fields
    When the Archiver archives a run that changes the second element
    Then the path of the change holds the index of that element
    And the warnings of the run state the file and the shape of the element

  @unverified
  Scenario: Two elements with one identity keep their index and the run warns
    Given an array with two elements that share the same "_name"
    When the Archiver archives a run that changes the second element
    Then the path of the change holds the index of that element
    And the first element keeps its own path
    And the warnings of the run state that two elements share the identity

  @unverified
  Scenario: The rows of these objects do not move the content hash
    Given a mod run that changes a location, a bot type, a hideout part, a price and a handbook entry
    When I compute the content hash of that run
    Then the hash equals the hash that the same run had before these objects had rows
    And the hash is the same with and without the object rows

  @unverified
  Scenario: A changed file that holds no row is still reported
    Given a mod run that changes "globals.json" and nothing that has a row
    When the Archiver archives that run
    Then the canonical hash of "globals.json" is part of the content hash
    And the result of the run names "globals.json" as a file with no row

  @unverified
  Scenario: An object row belongs to the dataset and never to an official table
    Given a mod run that adds the location "arena_airpit"
    When the Archiver archives that run
    Then the row of "arena_airpit" is keyed by the dataset id
    And no official table holds a row of that location

  # ------------------------------------------------ Dependency set and baseline
  # ADR-0013. The catalog states the dependencies. The metadata is the fallback.
  # The chosen set is frozen on the observation.

  @unverified
  @backlog
  Scenario: The dependency set comes from the catalog
    Given the catalog states that "mod-x" needs "com.dep.a" in the range ">=2.0.0 <3.0.0"
    And the catalog holds the versions "2.2.0" and "2.4.1" of "com.dep.a"
    When the Archiver archives "mod-x"
    Then the observation states the dependency "com.dep.a" in the version "2.4.1"
    And the observation states that the source of that entry is the catalog

  @unverified
  @backlog
  Scenario: A catalog gap is filled from the mod metadata
    Given the catalog states no requirement for "mod-x"
    And the metadata of "mod-x" needs "com.dep.a"
    When the Archiver archives "mod-x"
    Then the observation states that the source of that entry is the metadata
    And the archive states that the catalog has a gap for "mod-x"

  @unverified
  @backlog
  Scenario: A stored dependency set does not change when the catalog changes
    Given the observation of "mod-x" states the dependency "com.dep.a" in "2.4.1"
    When the catalog states that "2.5.0" also satisfies the range
    Then the observation still states "2.4.1"

  @unverified
  @backlog
  Scenario: Two mods with the same dependency set share one baseline
    Given the mods "mod-a" and "mod-b" declare the same dependency set
    When the Archiver archives both mod versions on "4.0.13"
    Then the Archiver starts one baseline container for both mods

  # ------------------------------------------------------ Isolation of the run

  @unverified
  @backlog
  Scenario: A mod outside its declared server range is skipped
    Given the mod "com.example.mod" declares support for ">=4.0.0 <4.1.0"
    And the archive archives the server version "4.1.0"
    When the Archiver considers that mod
    Then it starts no container for that mod
    And the observation states the outcome "outside_declared_range"

  @unverified
  @backlog
  Scenario: One bad mod does not stop another mod
    Given the mods "mod-a" and "mod-b"
    And "mod-a" crashes while it loads
    When the Archiver archives both mod versions
    Then the observation of "mod-a" states the outcome "crashed"
    And "mod-b" has a dataset

  @unverified
  @backlog
  Scenario: The container of a mod has no network
    Given a mod container is ready
    When the server starts in that container
    Then the container has no network interface
    And the mod cannot reach the internet

  @unverified
  @backlog
  Scenario: A mod container never reads an asset bundle
    Given a mod with the metadata flag "IsBundleMod" set to true
    When the Archiver starts a container for that mod
    Then the bundle step does not run
    And the container does not read a bundle file

  @unverified
  @backlog
  Scenario: A run fails when the bundle patch is not applied
    Given a target that Harmony must patch
    And the patch is not in "Harmony.GetPatchedMethods()"
    When the Archiver starts a container
    Then the run fails with a message that names the target
    And the archive does not record a dataset for that mod
