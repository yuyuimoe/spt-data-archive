@bots @unverified
Feature: Archive bots, maps and server mechanics
  The server holds more than templates, traders and locales. It holds the bots, the maps and the
  mechanics in `globals.json`. Mods change all three, so the archive keeps all three (ADR-0024).
  A faction is not a field in that data, so the archive resolves it from a locale key and a curated
  map (ADR-0025).

  Background:
    Given a migrated archive database
    And a read-only server database for the release "4.1.6"

  @adr-0024 @unverified
  Scenario: A release keeps its bots
    When the Archiver imports the release
    Then the table "bot_types" has 57 rows for that release
    And the row for "bossbully" holds the whole `bots/types/bossbully.json` object
    And the row for "bosswedge" does not exist

  @adr-0024 @unverified
  Scenario: A release keeps its maps
    When the Archiver imports the release
    Then the table "locations" has 19 rows for that release
    And the row for "bigmap" states its name and its escape time limit
    And the table "location_files" has seven rows for "bigmap"
    And the row for the file "looseLoot" holds the whole file

  @adr-0024 @unverified
  Scenario: A release keeps the mechanics that value mods change
    When the Archiver imports the release
    Then the table "globals" has one row for that release
    And the row holds 107 groups under "config"
    And the group "SkillsSettings" is readable through the API

  @adr-0024 @unverified
  Scenario: A mod that changes one map stores one map
    Given a mod dataset whose export changes only the map "bigmap"
    When the Archiver imports the dataset
    Then the table "mod_locations" has one row for that dataset
    And the other 18 maps have no row for that dataset

  @adr-0025 @unverified
  Scenario: A wave resolves to a bot type and a role
    Given the release "4.1.6" with the map "bigmap"
    When I read the waves of that map
    Then the wave whose spawn type is "assault" names the bot type "assault"
    And the bot type names a role

  @adr-0025 @unverified
  Scenario: A curated faction resolves a mod bot type
    Given a curated row that puts the bot type "bossWedge" in the faction "blackdiv"
    And a mod dataset that adds the bot type "bossWedge"
    When I read the bot type "bossWedge" of that dataset
    Then the answer names the faction "blackdiv"
    And the answer names the dataset that supplied the bot type

  @adr-0025 @unverified
  Scenario: A bot type without a curated faction is not guessed
    Given no curated row puts the bot type "bossWedge" in a faction
    When I read the bot type "bossWedge"
    Then the faction is null
    And the answer names the bot type instead of returning an empty list

  @adr-0025 @unverified
  Scenario: A faction that lives only in the client is absent from the data
    Given a mod with the facet "client" and no server part, such as ISB Special Forces
    When I read the factions of a release
    Then the answer does not list that faction
    And the catalog holds the mod with its facets

  @adr-0023 @unverified
  Scenario: A client-only mod is recorded, not run
    Given a mod artifact with files under "BepInEx" and no "SPT_Runtime" directory
    When the Archiver catalogs the artifact
    Then the mod has the facet "client"
    And the mod has no dataset
    And the observation outcome is "no_server_data"
    And the `BepInEx/config` files are kept with the catalog row

  @adr-0023 @unverified
  Scenario: A mod with both roots gets both facets
    Given a mod artifact with files under "BepInEx" and a "db" tree under "SPT_Runtime/user/mods"
    When the Archiver catalogs the artifact
    Then the mod has the facets "client" and "server_data"

  @adr-0026 @unverified
  Scenario: A value mod that ships no preset changes nothing, and says so
    Given a value mod that needs a preset and ships none, such as SVM 2.2.3
    When the Archiver runs the mod
    Then the observation outcome is "loaded_but_disabled"
    And the observation holds the reason from the mod log
    And the dataset holds no "globals" row

  @unverified
  @backlog @adr-0025
  Scenario: The curated faction map is small enough to review
    When I list the curated faction rows of the release "4.1.6"
    Then every row states its evidence
    And the count is small enough for one person to read in one sitting
