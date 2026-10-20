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

  @backlog
  Scenario: An archive run does not delete a mod
    Given the mod "mod-x" in the version "1.0.0" has a dataset and an observation
    When an archive run succeeds
    Then the table "mods" still holds that mod version
    And the observation of that mod version still exists

  @backlog
  Scenario: A mod that leaves the catalog keeps its data
    Given the mod "mod-x" is no longer in the catalog
    When the Archiver synchronizes the catalog
    Then no row of that mod is deleted
    And the dataset of that mod version still exists

  @backlog
  Scenario: An orphan dataset fails the run
    Given the dataset "D1" of the mod "mod-x"
    And no observation points at "D1"
    When an archive run succeeds
    Then the run fails with a message that names "D1"
    And the table "mod_datasets" still holds "D1"

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

  @backlog
  Scenario: A mod version states its content once
    Given the mod "com.example.mod" in the version "1.0.0"
    When the Archiver archives that mod version
    Then the table "mod_datasets" has one row for that version
    And that row states the content hash, the table hashes and the row counts

  @backlog
  Scenario: Unchanged content reuses the dataset
    Given the mod version "1.0.0" has the dataset "D1"
    When the version "1.1.0" of that mod exports the same content
    Then the observation of "1.1.0" points at the dataset "D1"
    And the table "mod_datasets" has one row for that mod

  @backlog
  Scenario: A mod version is observed on a server version
    Given the mod "com.example.mod" in the version "1.0.0"
    When the Archiver archives that mod version on the server version "4.0.13"
    Then the observation states the mod version "1.0.0" and the server version "4.0.13"
    And the observation points at the dataset of that mod version

  @backlog
  Scenario: The server version of a dataset is not part of the dataset
    Given the dataset "D1" of the mod "com.example.mod"
    When the archive observes that mod version on "4.0.13" and on "4.0.14"
    Then the dataset "D1" stays one row
    And two observations point at "D1"

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

  @backlog
  Scenario: A mod that is silent is not a success
    Given the mod "com.example.mod" in the version "1.0.0"
    When the export of that mod version does not hold the mod GUID
    Then the run fails with a message that names the mod GUID
    And the observation points at no dataset

  # ---------------------------------------------------- Change to official data
  # ADR-0017. One row in "mod_templates" states the operation of a template.
  # The rows in "mod_item_changes" are the paths under that template.

  @backlog
  Scenario: A mod that changes an official item is archived
    Given a mod that changes the weight of the official item "A1"
    When the Archiver archives that mod on the server version "4.0.13"
    Then the table "mod_templates" has one row for "A1" with the operation "modified"
    And the table "mod_item_changes" states the path "_props/Weight"
    And that row states the old value from the official table of "4.0.13"
    And the official row of "A1" is not changed

  @backlog
  Scenario: A mod that adds an item is recorded as an addition
    Given a mod that adds the item "B1" with the parent "node-weapons"
    When the Archiver archives that mod
    Then the table "mod_templates" has one row for "B1" with the operation "added"
    And that row states the parent "node-weapons"
    And every path row of "B1" states no old value

  @backlog
  Scenario: A mod cannot add an item under the id of an official item
    Given the official item "A1"
    And a mod that writes the template id "A1"
    When the Archiver archives that mod
    Then the row of "A1" states the operation "modified"
    And no row states the operation "added" for "A1"

  @backlog
  Scenario: A new property of an old item is not an addition
    Given the official item "A1" has no property "_props/CanSellOnRagfair"
    When a mod sets that property to true
    Then the template "A1" states the operation "modified"
    And that path row states no old value

  @backlog
  Scenario: A mod that hides an official property is archived
    Given a mod that removes the property "_props/CanSellOnRagfair" of the official item "A1"
    When the Archiver archives that mod
    Then the template "A1" states the operation "modified"
    And the table "mod_item_changes" has a row with the path "_props/CanSellOnRagfair"
    And that row states the operation "remove"

  @backlog
  Scenario: The count of additions needs no official data
    Given the dataset "D1" adds "B1" and "B2" and changes "A1"
    When a client asks how many items that dataset adds
    Then the answer is 2
    And the query reads no official table

  @backlog
  Scenario: An added item states a parent that the baseline does not have
    Given a mod adds the item "B1" with the parent "node-weapons"
    And the official baseline has no node "node-weapons"
    When the Archiver archives that mod
    Then the template row of "B1" states the parent "node-weapons"
    And the archive answers that the parent does not resolve for a lone install

  # ------------------------------------------------ Dependency set and baseline
  # ADR-0013. The catalog states the dependencies. The metadata is the fallback.
  # The chosen set is frozen on the observation.

  @backlog
  Scenario: The dependency set comes from the catalog
    Given the catalog states that "mod-x" needs "com.dep.a" in the range ">=2.0.0 <3.0.0"
    And the catalog holds the versions "2.2.0" and "2.4.1" of "com.dep.a"
    When the Archiver archives "mod-x"
    Then the observation states the dependency "com.dep.a" in the version "2.4.1"
    And the observation states that the source of that entry is the catalog

  @backlog
  Scenario: A catalog gap is filled from the mod metadata
    Given the catalog states no requirement for "mod-x"
    And the metadata of "mod-x" needs "com.dep.a"
    When the Archiver archives "mod-x"
    Then the observation states that the source of that entry is the metadata
    And the archive states that the catalog has a gap for "mod-x"

  @backlog
  Scenario: A stored dependency set does not change when the catalog changes
    Given the observation of "mod-x" states the dependency "com.dep.a" in "2.4.1"
    When the catalog states that "2.5.0" also satisfies the range
    Then the observation still states "2.4.1"

  @backlog
  Scenario: Two mods with the same dependency set share one baseline
    Given the mods "mod-a" and "mod-b" declare the same dependency set
    When the Archiver archives both mod versions on "4.0.13"
    Then the Archiver starts one baseline container for both mods

  # ------------------------------------------------------ Isolation of the run

  @backlog
  Scenario: A mod outside its declared server range is skipped
    Given the mod "com.example.mod" declares support for ">=4.0.0 <4.1.0"
    And the archive archives the server version "4.1.0"
    When the Archiver considers that mod
    Then it starts no container for that mod
    And the observation states the outcome "outside_declared_range"

  @backlog
  Scenario: One bad mod does not stop another mod
    Given the mods "mod-a" and "mod-b"
    And "mod-a" crashes while it loads
    When the Archiver archives both mod versions
    Then the observation of "mod-a" states the outcome "crashed"
    And "mod-b" has a dataset

  @backlog
  Scenario: The container of a mod has no network
    Given a mod container is ready
    When the server starts in that container
    Then the container has no network interface
    And the mod cannot reach the internet

  @backlog
  Scenario: A mod container never reads an asset bundle
    Given a mod with the metadata flag "IsBundleMod" set to true
    When the Archiver starts a container for that mod
    Then the bundle step does not run
    And the container does not read a bundle file

  @backlog
  Scenario: A run fails when the bundle patch is not applied
    Given a target that Harmony must patch
    And the patch is not in "Harmony.GetPatchedMethods()"
    When the Archiver starts a container
    Then the run fails with a message that names the target
    And the archive does not record a dataset for that mod
