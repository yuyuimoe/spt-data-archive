Feature: Archive the official server data
  As a maintainer
  I want the Archiver to copy the server database into PostgreSQL
  So that users can query official data without running the server

  Background:
    Given a read-only copy of the server "SPT_Data/database" folder
    And a migrated archive database

  # ------------------------------------------------------------------ Items

  @unverified
  Scenario: Items are imported with promoted columns and raw properties
    Given the file "templates/items.json" has an item "5449016a4bdc2d6f028b456f" with "Weight" 0.5, "Width" 1 and "Height" 1
    When the Archiver runs
    Then the table "items" has a row with the id "5449016a4bdc2d6f028b456f"
    And that row has the weight 0.5, the width 1 and the height 1
    And the "props" column holds the complete "_props" object of the item

  @unverified
  Scenario: Categories are imported as nodes
    Given the file "templates/items.json" has an entry with "_type" "Node"
    When the Archiver runs
    Then the row of that entry has the type "Node"

  @unverified
  Scenario: A Git LFS pointer is not archived as data
    Given the file "templates/items.json" is a Git LFS pointer
    When the Archiver runs
    Then the run fails
    And the error message asks for "git lfs pull"
    And the table "items" keeps its previous rows

  # --------------------------------------------------------- Customization

  @unverified
  Scenario: Clothes are imported with their body part and sides
    Given the file "templates/customization.json" has an entry "6746f06e05e15b1b8bb2a0a5" with the body part "Head" and the sides "Usec" and "Bear"
    When the Archiver runs
    Then the table "customizations" has a row with the id "6746f06e05e15b1b8bb2a0a5"
    And that row has the body part "Head" and the sides "Usec" and "Bear"
    And the "props" column holds the complete "_props" object of the entry

  @unverified
  Scenario: Clothes of an older release stay when a newer one is imported
    Given the archive holds the release "4.0.13"
    When the Archiver imports the release "4.1.6"
    Then the table "customizations" has rows for "4.1.6"
    And the rows of "4.0.13" are unchanged

  @unverified
  Scenario: A clothes-only mod changes the archive
    Given a mod dataset whose export adds one entry to "customization"
    When the Archiver imports the dataset
    Then the table "mod_customizations" has the new row
    And the table "mod_templates" has a template row of the kind "customization"

  # ---------------------------------------------------------------- Locales

  @unverified
  Scenario Outline: Locale keys are split into an entity and a field
    Given the file "locales/global/en.json" has the key "<key>"
    When the Archiver runs
    Then the locale entry has the entity id <entity>
    And the locale entry has the field <field>

    Examples:
      | key                                | entity                     | field         |
      | 5422acb9af1c889c16000029 Name      | 5422acb9af1c889c16000029   | Name          |
      | 5422acb9af1c889c16000029 ShortName | 5422acb9af1c889c16000029   | ShortName     |
      | Voip/DisabledForOffline            | null                       | null          |
      | kg                                 | null                       | null          |

  @unverified
  Scenario: Every listed language is imported
    Given the file "locales/languages.json" lists the languages "en" and "ru"
    And the folder "locales/global" has a file for each of them
    When the Archiver runs
    Then the table "languages" has the codes "en" and "ru"
    And the table "locale_entries" has entries for both languages

  @unverified
  Scenario: A language without a file is skipped
    Given the file "locales/languages.json" lists the language "xx"
    And the folder "locales/global" has no file "xx.json"
    When the Archiver runs
    Then the run succeeds
    And the table "locale_entries" has no entry for "xx"

  @unverified
  Scenario: The first of two duplicated keys wins
    Given a locale file has the same key twice with different values
    When the Archiver runs
    Then the table "locale_entries" has one entry for that key
    And its value is the first value

  # ----------------------------------------------------------------- Quests

  @unverified
  Scenario: Quests keep their complete object
    Given the file "templates/quests.json" has the quest "5936d90786f7742b1420ba5b"
    When the Archiver runs
    Then the table "quests" has a row for that quest
    And the row has the quest name, the trader id, the location, the type and the side
    And the "data" column holds the complete quest object, including its conditions

  # ---------------------------------------------------------------- Traders

  @unverified
  Scenario: A trader is imported with its optional files
    Given the trader folder "54cb57776803fa99248b456e" has the files "base.json" and "assort.json"
    When the Archiver runs
    Then the table "traders" has a row for that trader
    And the "base" column holds the complete content of "base.json"

  @unverified
  Scenario: A trader folder without base.json is skipped
    Given a trader folder has no "base.json"
    When the Archiver runs
    Then the run succeeds
    And the table "traders" has no row for that folder

  @unverified
  Scenario: Assort offers carry their barter scheme and loyalty level
    Given the trader assort has a root item "R1" with the barter scheme "S1" and the loyalty level 2
    When the Archiver runs
    Then the table "trader_assort_items" has a row for "R1"
    And that row has the barter scheme "S1" and the loyalty level 2

  @unverified
  Scenario: Child items of an offer point at their parent
    Given the trader assort has a child item "C1" with the parent "R1" and the slot "mod_scope"
    When the Archiver runs
    Then the row for "C1" has the parent "R1" and the slot "mod_scope"
    And the row for "C1" has no loyalty level

  @unverified
  Scenario: A quest can unlock an offer
    Given the file "questassort.json" of a trader maps the state "success" and the offer "R1" to the quest "Q1"
    When the Archiver runs
    Then the table "trader_quest_assorts" has a row with the status "success", the offer "R1" and the quest "Q1"

  # ------------------------------------------------------------ Run control

  @unverified
  Scenario: An unchanged source is skipped
    Given a succeeded run exists with the hash of the current source files
    When the Archiver runs
    Then it imports nothing
    And it exits with code 0

  @unverified
  Scenario: A forced run imports unchanged data
    Given a succeeded run exists with the hash of the current source files
    When the Archiver runs with "Archive:Force" set to true
    Then it imports all data again

  @unverified
  Scenario: A failed run leaves the previous dataset in place
    Given the official tables hold a previous dataset
    And a source file is not valid JSON
    When the Archiver runs
    Then the run fails
    And the official tables hold the previous dataset
    And the run has the status "failed" and an error message

  @unverified
  Scenario: A successful run is recorded and announced
    When the Archiver runs and the run succeeds
    Then the table "archive_runs" has a row with the status "succeeded" and a finish time
    And the cache key "archive:data-version" holds the id of that run

  @unverified
  Scenario: The Archiver finishes when the cache is down
    Given the cache is not reachable
    When the Archiver runs
    Then the run succeeds
    And the Archiver logs a warning

  # -------------------------------------------------- Server version (ADR-0014)

  @unverified
  Scenario: The run states the server version of the data
    Given the server reports the version "4.0.13"
    When the Archiver runs
    Then the table "spt_versions" has a row for "4.0.13"
    And every official row of that run states "4.0.13"

  @unverified
  Scenario: A new server version keeps the old one
    Given the archive holds the official data of "4.0.12"
    When the Archiver imports the server version "4.0.13"
    Then the archive holds the official data of both versions
    And no row of "4.0.12" is changed or deleted

  # ------------------------------------------------ Official source (ADR-0035)

  @unverified
  Scenario: An official import refuses a source that loaded a mod
    Given a source that loaded the mod "com.wtt.commonlib" version "3.0.6"
    When the Archiver imports it as official data of "4.1.6"
    Then the run fails
    And the message names "com.wtt.commonlib"
    And the table "items" keeps its previous rows

  @unverified
  Scenario: A source that states no mods imports with a warning
    Given a source without "run/loaded.json"
    When the Archiver imports it as official data of "4.1.6"
    Then the run succeeds
    And the log warns that nothing proves the source loaded no mod

  @unverified
  Scenario: An official import refuses a trader file that holds null
    Given the trader "638f541a29ffd1183d187f57" has "null" in "questassort.json"
    And the same trader has "null" in "assort.json"
    When the Archiver imports the server version "4.1.6"
    Then the run succeeds
    And the archive stores no offer and no quest link for that trader
    And every other trader of that release keeps its rows

  # ---------------------------------------------------------- Not built yet

  @backlog
  Scenario Outline: More parts of the server database are archived
    Given the server database has "<part>"
    When the Archiver runs
    Then the archive holds "<part>" in its own tables

    Examples:
      | part                                   |
      | hideout areas and production           |
      | bots                                   |
      | locations and loose loot               |
      | globals and settings                   |
      | handbook, prices and customization     |
      | achievements and prestige              |
      | trader dialogue templates              |
      | menu, server and web locales           |
      | bear and usec trader suits             |
