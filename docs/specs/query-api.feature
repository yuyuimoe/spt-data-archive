Feature: Query the archive through a read-only GraphQL API
  As an API consumer
  I want to filter, sort and page archive data with GraphQL
  So that I can get official and mod data without access to the server

  Background:
    Given an archive database with official data and mod data
    And the Api is running

  # -------------------------------------------------------------- Read only

  @unverified
  Scenario: The schema has no mutations
    When I read the GraphQL schema
    Then the schema has a Query type
    And the schema has no Mutation type

  @unverified
  Scenario: The database role cannot write
    Given the Api connects as the role "archive_reader"
    When that role runs an INSERT on the table "items"
    Then PostgreSQL rejects the statement with a permission error

  @unverified
  Scenario: The reader role can read tables created by later migrations
    Given a migration creates a new table
    When the role "archive_reader" runs a SELECT on that table
    Then the query succeeds

  # ------------------------------------------------------ Official vs. mods

  @unverified
  Scenario: Official items and mod items are separate collections
    Given the official item "A1" and the mod item "B1" of the mod "mod-x"
    When I query "items"
    Then the result has "A1"
    And the result does not have "B1"
    When I query "modItems"
    Then the result has "B1"
    And the result does not have "A1"

  @unverified
  Scenario: A mod item states the mod it belongs to
    Given the mod item "B1" of the mod "mod-x"
    When I query "modItems" and select the field "mod"
    Then the result shows the mod "mod-x"

  # ------------------------------------------------- Filter, sort and page

  @unverified
  Scenario: Items can be filtered by name
    Given the official items "weapon_a", "weapon_b" and "ammo_c"
    When I query "items" with the filter name starts with "weapon_"
    Then the result has "weapon_a" and "weapon_b"
    And the result does not have "ammo_c"

  @unverified
  Scenario: A single item can be read by id
    Given the official item "A1"
    When I query "item" with the id "A1"
    Then the result is the item "A1"

  @unverified
  Scenario: An unknown id returns null
    When I query "item" with the id "does-not-exist"
    Then the result is null
    And the response has no errors

  @unverified
  Scenario: Clothes can be filtered by the side that wears them
    Given the official customizations "head_bear" for "Bear" and "head_usec" for "Usec"
    When I query "customizations" with the side "Bear"
    Then the result has "head_bear"
    And the result does not have "head_usec"

  @unverified
  Scenario: Translations are found by entity
    Given the locale entries of the entity "A1" in the languages "en" and "ru"
    When I query "translations" with the entity id "A1" and the language "en"
    Then the result has only the "en" entries of "A1"

  @unverified
  Scenario: Trader offers can be found by item
    Given the traders "T1" and "T2" sell the item "A1"
    When I query "assortItems" with the template id "A1"
    Then the result has one row for "T1" and one row for "T2"

  @unverified
  Scenario: Paged results report the total
    Given 120 official items
    When I query "items" for the first 50 with the total count
    Then the result has 50 nodes
    And the total count is 120
    And the result has a next page

  # ------------------------------------------------------------------ Limits

  @unverified
  Scenario Outline: A query that breaks a limit is rejected
    When I send a query that <violation>
    Then the response has an error
    And the database does not run the query

    Examples:
      | violation                       |
      | asks for a page larger than 200 |
      | nests deeper than 10 levels     |

  @unverified
  Scenario: A slow query is stopped
    Given a query that runs longer than 10 seconds
    When I send it
    Then the response has a timeout error

  # ------------------------------------------------------------------ Cache

  @unverified
  Scenario: A repeated query is answered from the cache
    Given the cache is empty
    When I send the same query twice
    Then the first response has the header "X-Cache" with the value "MISS"
    And the second response has the header "X-Cache" with the value "HIT"
    And both responses have the same body

  @unverified
  Scenario: A new archive run makes old cached answers unreachable
    Given a cached answer exists
    When the Archiver finishes a successful run
    And I send the same query
    Then the response has the header "X-Cache" with the value "MISS"

  @unverified
  Scenario: Responses with errors are not cached
    Given a query that returns GraphQL errors
    When I send it twice
    Then both responses have the header "X-Cache" with the value "MISS"

  @unverified
  Scenario: A large request body is not cached
    When I send a query body larger than 32 KB
    Then the response has no "X-Cache" header

  @unverified
  Scenario: The Api works when Redis is down
    Given Redis is not reachable
    When I send a valid query
    Then the response has the data
    And the Api logs a warning

  # -------------------------------------------------- Server version (ADR-0014)

  @unverified
  Scenario: An official query without a version answers from the latest release
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" without a version argument
    Then the answer comes from "4.0.13"
    And the answer states the version "4.0.13"

  @unverified
  Scenario: An official query with a version answers from that release
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" with the version "4.0.12"
    Then the answer states the version "4.0.12"
    And no row of "4.0.13" is in the answer

  @unverified
  Scenario: An unknown version is an error, not an empty answer
    When I query "items" with the version "9.9.9"
    Then the response has an error that names "9.9.9"
    And the error names the latest archived release
    And the response has no empty list

  @unverified
  Scenario: A version range is rejected
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" with the version "^9.9.9"
    Then the response has an error that names "^9.9.9"
    And the error states that the argument takes one exact archived release
    And the error code is not the error code of an unknown release
    And the response has no empty list

  @unverified
  Scenario: A short version is not a release key
    Given the archive holds the release "4.0.13"
    When I query "items" with the version "4.0"
    Then the response has an error that names "4.0"
    And the error code is "SPT_VERSION_MALFORMED"

  @unverified
  Scenario: The two version errors carry fixed error codes
    When I query "items" with the version "9.9.9"
    Then the error code is "SPT_VERSION_UNKNOWN"
    When I query "items" with the version "latest"
    Then the error code is "SPT_VERSION_MALFORMED"

  @unverified
  Scenario: An empty archive is an error and not an empty list
    Given the archive holds no release
    When I query "items" without a version argument
    Then the response has an error
    And the error code is "SPT_ARCHIVE_EMPTY"

  @unverified
  Scenario: A client lists the archived releases
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "sptVersions"
    Then the answer lists "4.0.13" before "4.0.12"

  @unverified
  Scenario: One release does not answer for another release
    Given the archive holds the releases "4.0.12" and "4.0.13"
    When I query "items" for "4.0.12" and then for "4.0.13"
    Then both responses have the header "X-Cache" with the value "MISS"

  @unverified
  Scenario: The same query twice is answered from the cache
    Given the cache holds no answer for the query
    When I send the same query twice
    Then the first response has the header "X-Cache" with the value "MISS"
    And the second response has the header "X-Cache" with the value "HIT"
    And the second body is the same as the first body

  @unverified
  Scenario: An error is never cached
    When I send a query that asks for a release the archive does not hold, twice
    Then both responses have the header "X-Cache" with the value "MISS"

  @unverified
  Scenario: A cache that is down costs one slow request
    Given the cache is not reachable
    When I send five queries
    Then every response is a successful answer
    And no response has the header "X-Cache"
    And only one of them waited for the cache

  @unverified
  Scenario: Requests that miss together run one query
    Given the cache holds no answer for the query
    When twenty clients send the same query at the same time
    Then one query runs
    And one response has the header "X-Cache" with the value "MISS"
    And the other responses have the header "X-Cache" with the value "SHARED"

  @unverified
  Scenario: A client repeats a request with an idempotency key
    When I send a query with the idempotency key "order-42-abc"
    And I send the same query with the same idempotency key
    Then the second response has the same body as the first
    And it has the header "Idempotency-Replayed" with the value "true"

  @unverified
  Scenario: An idempotency key cannot answer a different request
    When I send a query with the idempotency key "order-42-abc"
    And I send another query with the same idempotency key
    Then the response is a "409"
    And the error code is "IDEMPOTENCY_KEY_REUSED"

  @unverified
  Scenario: A malformed idempotency key is refused
    When I send a query with the idempotency key "short"
    Then the response is a "400"
    And the error code is "IDEMPOTENCY_KEY_INVALID"

  # ----------------------------------------------------------------- Limits

  @unverified
  Scenario: A request body over 1 MB is refused
    When I send a query body larger than 1,000,000 bytes
    Then the response is not a successful answer

  @unverified
  Scenario: A query deeper than 10 levels is refused
    When I send a query nested 11 levels deep
    Then the response has an error and no data

  @unverified
  Scenario: A full page that selects every field is inside the cost limit
    When I query one page of 200 rows and select every field of a row
    Then the response has no cost error

  @unverified
  Scenario: A wide query over the cost limit is refused
    When I send a query for twelve pages of 200 rows
    Then the response has the error "HC0047"

  @unverified
  Scenario: The schema can be read
    When I send an introspection query
    Then the response has the type "Query"

  # ----------------------------------------------------------------- Health

  @unverified
  Scenario: The health endpoint answers
    When I request "/healthz"
    Then the response status is 200

  # ---------------------------------------------------------- Not built yet

  @backlog
  Scenario: Official and mod items can be read in one list
    Given the official item "A1" and the mod item "B1"
    When I query "allItems"
    Then the result has "A1" and "B1"
    And each row states whether it is official or from a mod

  @unverified
  Scenario: A JSON column offers comparison and not text matching
    Given the item "A1" has the property "Weight" 0.5 in its "props" column
    When I query "items" with the text operation "startsWith" on "props"
    Then the response has an error that names "startsWith"
    And the error comes from the schema and not from the database
    When I query "items" where "props" equals the property of "A1"
    Then the answer has the item "A1"

  @unverified
  Scenario: JSON properties are returned as JSON values
    Given the item "A1" has the property "Weight" 0.5 in its "props" column
    When I query "items" and select "props"
    Then the field "props" is a JSON object
    And it is not an escaped string
