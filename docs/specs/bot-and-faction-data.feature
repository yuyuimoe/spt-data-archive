@bots
Feature: Archive bots, maps and server mechanics
  The server holds more than templates, traders and locales. It holds the bots, the maps and the
  mechanics in `globals.json`. Mods change all three, so the archive keeps all three (ADR-0024).
  A faction is not a field in that data, so the archive resolves it from a curated seed and four
  trusted prefixes (ADR-0025).
  The archive keeps an object of these tables as rows of the dataset whose run changed it: a
  template row, one path row for each changed value, and the source of an added object
  (ADR-0041, ADR-0044). No official table holds such an object yet (TD-18).

  Background:
    Given a migrated archive database
    And a baseline export for the release "4.1.6"

  # ------------------------------------- The objects of a release
  # ADR-0024. A release's own bots, maps and mechanics hold no official row yet: the official import
  # reads the templates, the traders and the locales, and nothing else (TD-18). Each scenario here
  # states the behavior that ADR-0024 decided and that the archive does not have.

  @adr-0024 @unverified @backlog
  Scenario: A release keeps its bots
    Given the baseline export holds 57 bot types under "bots/types"
    When the Archiver imports the release as official data
    Then the archive holds one row for each bot type of that release
    And the row for the bot type "bossbully" holds the whole file "bots/types/bossbully.json"
    And no row exists for the bot type "bosswedge", because only a mod adds it

  @adr-0024 @unverified @backlog
  Scenario: A release keeps its maps
    Given the baseline export holds the map "bigmap" with its seven files
    When the Archiver imports the release as official data
    Then the archive holds one row for the map "bigmap" with its name and its escape time limit
    And the archive holds one row for each file of that map
    And the row of the file "looseLoot" holds the whole file

  @adr-0024 @unverified @backlog
  Scenario: A release keeps the mechanics that value mods change
    Given the baseline export holds the file "globals.json" with 107 groups under "config"
    When the Archiver imports the release as official data
    Then the archive holds one row for the file "globals.json"
    And a client reads the group "SkillsSettings" of that row through the Api

  # ------------------------------------- The objects of a run
  # ADR-0041. A map, a bot type and a global have no mod_* table of their own. A change to one is a
  # template row of its kind, and the object itself is the source of an added row.

  Scenario: A mod that changes one map stores one map
    Given the baseline export holds the maps "bigmap", "woods" and "factory4_day"
    And a mod dataset whose export changes only the map "bigmap"
    When the Archiver imports the dataset
    Then the table "mod_templates" has one row of the kind "location" for the map "bigmap" with the operation "modified"
    And no other map has a row of the kind "location" for that dataset

  # ADR-0025. The archive stores a wave as the value of the map document, and a client reads it with
  # a path filter (ADR-0040). Nothing in the data states a role, and no field of the Api resolves a
  # wave, so the second half of the scenario has no model yet (TD-32, TD-68).

  @adr-0025 @unverified @backlog
  Scenario: A wave resolves to a bot type and a role
    Given the baseline export holds the map "bigmap" with a wave of the spawn type "assault"
    When I read the waves of that map
    Then the wave whose spawn type is "assault" names the bot type "assault"
    And the bot type names a role

  # ADR-0025. The curated seed and the resolver answer for a bot type of the release. A bot type that
  # a mod adds is not in the seed, and its own config is the only source of its faction (level 1).
  # No curated table and no field of the Api state either one yet.

  @adr-0025 @unverified @backlog
  Scenario: A curated faction resolves a mod bot type
    Given a curated row that puts the bot type "bossWedge" in the faction "blackdiv"
    And a mod dataset that adds the bot type "bossWedge"
    When I read the bot type "bossWedge" of that dataset
    Then the answer names the faction "blackdiv"
    And the answer names the dataset that supplied the bot type

  @adr-0025
  Scenario: A bot type without a curated faction is not guessed
    Given no curated row puts the bot type "bossWedge" in a faction
    When I read the bot type "bossWedge"
    Then the faction is null
    And the answer names the bot type instead of returning an empty list

  # ADR-0023. A mod is catalogued by the parts it ships, and a client-only mod has no server part.
  # Neither the catalog nor the facet of a row exists yet (ADR-0023 action items, TD-62).

  @adr-0025 @adr-0023 @unverified @backlog
  Scenario: A faction that lives only in the client is absent from the data
    Given a mod with the facet "client" and no server part, such as ISB Special Forces
    When I read the factions of a release
    Then the answer does not list that faction
    And the catalog holds the mod with its facets

  @adr-0023 @unverified @backlog
  Scenario: A client-only mod is recorded, not run
    Given a mod artifact with files under "BepInEx" and no "SPT_Runtime" directory
    When the Archiver catalogs the artifact
    Then the mod has the facet "client"
    And the mod has no dataset
    And the observation outcome is "no_server_data"
    And the files of "BepInEx/config" are kept with the catalog row

  @adr-0023 @unverified @backlog
  Scenario: A mod with both roots gets both facets
    Given a mod artifact with files under "BepInEx" and a "db" tree under "SPT_Runtime/user/mods"
    When the Archiver catalogs the artifact
    Then the mod has the facets "client" and "server_data"

  # ADR-0026. A value mod that ships no preset loads and disables itself. The archive records the run
  # that loaded it as "archived" today, and no row states the preset, so this behavior is not built.

  @adr-0026 @unverified @backlog
  Scenario: A value mod that ships no preset changes nothing, and says so
    Given a value mod that needs a preset and ships none, such as SVM 2.2.3
    When the Archiver runs the mod
    Then the observation outcome is "loaded_but_disabled"
    And the observation holds the reason from the mod log
    And the dataset holds no row of the kind "global"

  @adr-0025
  Scenario: The curated faction map is small enough to review
    When I list the curated faction rows of the release "4.1.6"
    Then every row states its evidence
    And the count is small enough for one person to read in one sitting
