@adr-0044
Feature: Every file that a run changes holds a row for each of its objects
  The Archiver gives an object of each of seven files a row of its own: "globals.json", "settings.json",
  "templates/dialogue.json", "templates/profiles.json", "templates/achievements.json",
  "templates/character.json" and "templates/customisationStorage.json" (ADR-0044). A client reads what a
  mod changed in them, and the canonical hash of each file stays in the content hash.

  @unverified
  Scenario: A changed dialogue line is a path under its element
    Given a baseline that holds the dialogue element "683dafc7a7c0a468ac8a9b05" with one line
    And a run that changes one field of that line
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "dialogue" for that element
    And the table "mod_change_paths" holds one row whose path starts with "/Lines/"

  @unverified
  Scenario: A changed item preset is one row
    Given a baseline whose "globals.json" holds the preset "5841474424597759ba49be91" with a price of 1
    And a run whose preset "5841474424597759ba49be91" holds a price of 2
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "preset" named by the preset id
    And the table "mod_change_paths" holds the path "/price"

  @unverified
  Scenario: A bot preset is named by its role and its difficulty
    Given a baseline whose "globals.json" holds the bot presets "assault" "easy" and "assault" "hard"
    And a run that changes one value of the preset "assault" "easy"
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "bot-preset" named "assault/easy"
    And the table "mod_templates" holds no row for "assault/hard"

  @unverified
  Scenario: A file that the server reads whole is one object with paths
    Given a baseline whose "settings.json" holds a "config" object with the key "time" set to 1
    And a run whose "settings.json" holds the same key set to 2
    When the Archiver archives the run
    Then the table "mod_templates" holds one row of the kind "setting"
    And the table "mod_change_paths" holds the path "/config/time"

  @unverified
  Scenario: An achievement, a storage entry and a character string that a run adds are rows with their source
    Given a run that adds one object to "templates/achievements.json", one to "templates/customisationStorage.json" and one string to "templates/character.json"
    When the Archiver archives the run
    Then the table "mod_database_objects" holds one row of the kind "achievement", one of the kind "storage" and one of the kind "character"
    And each row holds the object as the run wrote it

  @unverified
  Scenario: A file that the run leaves unchanged makes no row
    Given a run whose "globals.json" is the same as the baseline
    When the Archiver archives the run
    Then the dataset holds no row of the kinds "preset", "bot-preset", "scattering" or "global"

  @unverified
  Scenario: The content hash does not move when a file gains rows
    Given a run that changes "globals.json"
    When the Archiver computes the content hash of the run
    Then the hash is the hash that the Archiver computed before "globals.json" held rows
    And the canonical hash of "globals.json" is in the table hashes of the dataset

  @unverified
  Scenario: A file with rows is not listed as a file with no row
    Given a run that changes "globals.json" and "templates/anUnknownTable.json"
    When the Archiver archives the run
    Then the files with no row are exactly "templates/anUnknownTable.json"
