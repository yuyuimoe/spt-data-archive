Feature: Read a mod dataset through the Api
  As a data browser
  I want to read a mod dataset, the templates it touched, its change paths and its files
  So that I can answer what a mod adds and changes without the server

  Background:
    Given the archive holds the mod dataset "D1" of the mod "com.example.mod"
    And the dataset "D1" adds the item "B1" with the parent "node-weapons"
    And the dataset "D1" changes the weight of the official item "A1" from 0.5 to 0.3
    And the dataset "D1" holds the file "com.example.mod/db/items/weapons.json"
    And the Api is running

  # -------------------------------------------------------------- The dataset
  # ADR-0013. A dataset is the content of one mod version and carries no server
  # release. The observation states the release that the content ran on.

  Scenario: A client finds the dataset of a mod
    When I query "modDatasets" with the filter modGuid equals "com.example.mod"
    Then the result has one dataset
    And that dataset states its content hash and its config origin

  Scenario: A dataset is read by its id
    When I query "modDataset" with the id of "D1"
    Then the result is the dataset "D1"
    And the row counts per table are a JSON object

  Scenario: An unknown dataset id returns null
    When I query "modDataset" with an id that the archive does not hold
    Then the result is null
    And the response has no errors

  Scenario: A dataset that changed no table is still a dataset
    Given the dataset "D2" of the behaviour mod "com.example.behaviour"
    When I query "modDataset" with the id of "D2"
    And I query "modTemplates" with the dataset id of "D2"
    Then the dataset "D2" states its content hash
    And its template list is empty

  Scenario: A mod query takes no server version argument
    When I read the field "modDatasets" of the schema
    Then it has no argument "sptVersion"
    And the field "modDataObservations" states the server release of each attempt

  # ------------------------------------------------------------ The templates
  # ADR-0017, ADR-0032. One template row states the operation of one touched
  # object, and one change path row states one changed value under it.

  Scenario: A client reads the templates that a dataset touched
    When I query "modTemplates" with the dataset id of "D1"
    Then the result has "A1" with the operation "modified"
    And the result has "B1" with the operation "added"
    And the row of "B1" states the parent "node-weapons"

  Scenario: An offer is a template of its own kind
    Given the dataset "D3" adds one offer for the trader "T1"
    When I query "modTemplates" with the dataset id of "D3" and the kind "assort"
    Then the result has one template
    And its template id is an address and not an instance id

  # -------------------------------------------------------------- The changes

  Scenario: A changed value states its old value and its new value
    When I query "modChangePaths" with the dataset id of "D1" and the template id "A1"
    Then the result has the path "/_props/Weight"
    And the old value is the number 0.5 and the new value is the number 0.3
    And both values are JSON numbers and not strings

  Scenario: An added template states no old value
    When I query "modChangePaths" with the dataset id of "D1" and the template id "B1"
    Then every row of the result states no old value

  Scenario: A removed property states the operation "remove"
    Given the dataset "D4" removes the property "_props/CanSellOnRagfair" of the item "A1"
    When I query "modChangePaths" with the dataset id of "D4"
    Then the result has that path with the operation "remove"
    And the row states no new value

  # ---------------------------------------------------------------- The files
  # ADR-0029. The manifest states that a file exists and never its bytes. The
  # text of a configuration file is stored, because it explains the run.

  Scenario: A client reads the files of a dataset
    When I query "modFiles" with the dataset id of "D1"
    Then the result states the path, the kind, the size and the SHA-256 of each file

  Scenario: A configuration file states its text
    When I query "modFile" with the dataset id of "D1" and the path of its configuration file
    Then the file states the kind "config"
    And it states the text that the mod read
    And it states the parsed document as JSON

  Scenario: The text of one file above the limit of the Api is refused
    Given the Api serves at most 5 bytes of one configuration text
    When I query "modFile" with the dataset id of "D1" and the path of its configuration file
    Then the answer is the error "CONTENT_TEXT_TOO_LARGE"

  Scenario: A page of configuration texts above the limit of the Api is refused
    Given the Api serves at most 9 bytes of configuration text in one request
    When I query "modFiles" with the dataset id of "D1"
    Then the answer is the error "CONTENT_TEXT_REQUEST_LIMIT"

  Scenario: A file that the dataset does not hold returns null
    When I query "modFile" with the dataset id of "D1" and a path that the dataset does not hold
    Then the result is null
    And the response has no errors

  # ------------------------------------------------- Addresses and observations
  # ADR-0034. A row of the id map is the way back from an id that a run minted
  # to the address that the archive gave the object.

  Scenario: A client finds the object that an id of the run names
    Given the dataset "D1" holds the id "6a1f" for the address of "B1"
    When I query "modIdMaps" with the filter rawId equals "6a1f"
    Then the result states the address of "B1"
    And it states that the address is not pinned

  Scenario: An observation states the release that a mod version ran on
    When I query "modDataObservations" with the mod "com.example.mod"
    Then the result states the server release "4.1.6"
    And it states the outcome "archived"
    And it points at the dataset "D1"

  # ----------------------------------------------------------------- The runs
  # ADR-0033. A run states why it happened, what it was about and what came out.

  Scenario: A client reads the history of the runs
    When I query "archiveRuns"
    Then the newest run is first
    And each run states its kind, its purpose and its result as JSON

  Scenario: A run is read by its id
    When I query "archiveRun" with the id of a run
    Then the result states the status and the release of that run

  Scenario: A run that a mod caused states the mod and its version
    Given the run "R1" is a mod run for the mod "com.example.mod" in the version "1.0.0"
    When I query "archiveRun" with the id of "R1"
    Then it states the kind "mod"
    And it states the target mod GUID and the target version

  # ------------------------------------------------------------------ Limits

  Scenario: A JSON column of a mod row offers comparison only
    When I query "modDatasets" with the text operation "startsWith" on "tableHashes"
    Then the response has an error that names "startsWith"
    When I query "modDatasets" where "tableHashes" equals the table hashes of "D1"
    Then the answer has the dataset "D1"

  # ------------------------------------------- Objects that have no table of their own
  # ADR-0041. A location, a bot type, a hideout part, a price and a handbook entry have no
  # mod_* row. Their small facts are fields, and the whole document is one field of its own:
  # it costs a hundred times a small field and it has a limit of bytes (ADR-0040, decision 8).

  Scenario: A client reads the source of an added location
    Given the dataset "D1" adds the location "lighthouse" with the file "base" holding:
      """
      {"Id":"lighthouse"}
      """
    When I query "modDatabaseObjects" with the dataset id of "D1"
    Then the answer has one object row for the part "base" of 19 bytes

  Scenario: The whole document is JSON and it is a field of its own
    Given the dataset "D1" adds the location "lighthouse" with the file "base" holding:
      """
      {"Id":"lighthouse"}
      """
    When I ask for the whole document of "lighthouse" in the dataset "D1"
    Then the whole document is the object that the run wrote

  Scenario: A document above the limit of the Api is refused
    Given the dataset "D1" adds the location "lighthouse" with the file "base" holding:
      """
      {"Id":"lighthouse"}
      """
    And the Api serves at most 10 bytes of one document
    When I ask for the whole document of "lighthouse" in the dataset "D1"
    Then the answer is the error "RAW_JSON_TOO_LARGE"
