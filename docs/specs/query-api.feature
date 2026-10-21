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

  # ------------------------------------------------------------ The guard

  @unverified
  Scenario: A client that asks too often is refused
    Given the Api allows a burst of two requests
    When one client sends three requests in a row
    Then the third response status is 429
    And the error code is "RATE_LIMITED"
    And the response has the header "Retry-After"

  @unverified
  Scenario: The health check is never refused
    Given the Api allows a burst of two requests
    When one client sends ten requests to "/healthz"
    Then every response status is 200

  @unverified
  Scenario: An answer is compressed when the client asks for it
    Given a client accepts "gzip"
    When the client sends a query
    Then the response has the header "Content-Encoding: gzip"
    And it decodes to the same JSON as the uncompressed answer

  @unverified
  Scenario: The cache stores one answer for every client
    Given a client asked for a query with "Accept-Encoding: gzip"
    When another client asks for the same query without accepting compression
    Then the answer is readable without decoding
    And the response has the header "X-Cache: HIT"

  @unverified
  Scenario: Every answer carries the security headers
    When a client sends a query
    Then the response has the header "X-Content-Type-Options: nosniff"
    And it has the header "Referrer-Policy: no-referrer"
    And it has the header "X-Frame-Options: DENY"

  @unverified
  Scenario: A client behind the proxy is charged to its own address
    Given the Api believes the address of one proxy
    And two clients behind the proxy each send four requests while the Api allows a burst of three
    Then each client receives three answers and then a refusal

  @unverified
  Scenario: A forged forwarding header moves no bucket
    Given the Api believes the address of one proxy
    When a client behind the proxy sends a header that names another address
    Then the request is charged to the address of the client
    And the refusal says "RATE_LIMITED"

  @unverified
  Scenario: The proxy terminates TLS and states HSTS
    When a client asks for the query endpoint over HTTPS
    Then the answer has the header "Strict-Transport-Security" for one year and a half
    And the answer names no server
    And a plain request is answered with a redirect to HTTPS

  # ------------------------------------------- Filter by a path (ADR-0040)

  Scenario: A client finds the rows whose JSON document holds a value at a path
    Given the item "A1" has the property "Slots" 5 in its "props" column
    And the item "B1" has the property "Slots" 3 in its "props" column
    When I query "items" where "props" has the value 5 at the path "Slots"
    Then the answer has the item "A1"
    And the answer does not have the item "B1"

  Scenario: A path reaches into an object and an array
    Given the item "A1" has the property "Nested" with the list [1, 2] in its "props" column
    When I query "items" where "props" has the value 2 at the path "Nested", "list", "1"
    Then the answer has the item "A1"
    When I query "items" where "props" has the value 2 at the path "Nested", "list", "0"
    Then the answer does not have the item "A1"

  Scenario Outline: A path filter compares a JSON value of any type
    Given the item "A1" has the property "<property>" with the JSON value <json> in its "props" column
    When I query "items" where "props" has the value <json> at the path "<property>"
    Then the answer has the item "A1"

    Examples:
      | property | json    |
      | Slots    | 5       |
      | Weight   | 0.5     |
      | Name     | "Bear"  |
      | Flag     | true    |
      | Nothing  | null    |

  Scenario: A path filter compares an object or a list by its content
    Given the item "A1" has the property "Nested" with the object {"a": 1, "b": [1, 2]} in its "props" column
    When I query "items" where "props" has the object {b: [1, 2], a: 1} at the path "Nested"
    Then the answer has the item "A1"
    When I query "items" where "props" has the list [2, 1] at the path "Nested", "b"
    Then the answer does not have the item "A1"

  Scenario: A path filter does not mistake one JSON type for another
    Given the item "A1" has the property "Slots" 5 in its "props" column
    When I query "items" where "props" has the string "5" at the path "Slots"
    Then the answer does not have the item "A1"

  Scenario: A number is compared by its value and not by its spelling
    Given the item "A1" has the property "Slots" 5.0 in its "props" column
    When I query "items" where "props" has the value 5 at the path "Slots"
    Then the answer has the item "A1"

  Scenario: A large integer is not rounded
    Given the item "A1" has the property "Id" 12345678901234567890 in its "props" column
    And the item "B1" has the property "Id" 12345678901234567891 in its "props" column
    When I query "items" where "props" has the value 12345678901234567890 at the path "Id"
    Then the answer has the item "A1"
    And the answer does not have the item "B1"

  Scenario: The negation keeps the rows that lack the path
    Given the item "A1" has the property "Slots" 5 in its "props" column
    And the item "B1" has the property "Slots" 3 in its "props" column
    And the item "C1" has no property "Slots" in its "props" column
    When I query "items" where "props" has not the value 5 at the path "Slots"
    Then the answer has the items "B1" and "C1"
    And the answer does not have the item "A1"

  Scenario: Two path filters narrow one answer
    Given the item "A1" has the properties "Slots" 5 and "Weight" 0.5 in its "props" column
    And the item "B1" has the properties "Slots" 5 and "Weight" 2 in its "props" column
    When I query "items" where "props" has the value 5 at the path "Slots" and the value 0.5 at the path "Weight"
    Then the answer has the item "A1"
    And the answer does not have the item "B1"

  Scenario: A path filter reads its path and value from variables
    Given the item "A1" has the property "Slots" 5 in its "props" column
    When I query "items" with the path and the value in variables
    Then the answer has the item "A1"

  Scenario: A path segment is data and never part of the statement
    Given the item "A1" has a property whose name holds quotes, commas, braces and "--"
    When I query "items" where "props" has its value at the path of that name
    Then the answer has the item "A1"
    And the database statement does not contain the name

  Scenario: Every JSON column offers the same filter
    When I read the GraphQL schema
    Then the type "JsonOperationFilterInput" has the operations "eq", "neq", "pathEq" and "pathNeq"
    And every jsonb column of the model has a filter field of that type

  Scenario: A path filter extracts the path in the database and never matches text
    When I query "items" where "props" has the value 5 at the path "Slots", "x"
    Then the database runs one "jsonb_extract_path" over the column
    And the database runs no "LIKE" on the column

  Scenario Outline: A bad path is refused with its own error code
    When I query "items" where "props" has the value 5 at the path <path>
    Then the response has the error code "JSON_PATH_MALFORMED"
    And the response has no empty list

    Examples:
      | path                                |
      | no segment                          |
      | an empty segment                    |
      | a segment of more than 256 chars    |
      | more than 10 segments               |
      | a segment with a NUL character      |

  Scenario: The longest accepted path is 10 segments of 256 characters
    When I query "items" where "props" has the value 5 at a path of 10 segments
    And I query "items" where "props" has the value 5 at a path of one segment of 256 characters
    Then neither response has an error

  Scenario: A path filter without a value is refused
    When I query "items" where "props" has a path and no value
    Then the response has the error code "JSON_VALUE_MALFORMED"

  Scenario: A value nested too deep is refused
    When I query "items" where "props" has a list nested 64 levels deep at the path "Slots"
    Then the response has the error code "JSON_VALUE_MALFORMED"

  Scenario Outline: A value the database refuses is an error and not an internal error
    When I query "items" where "props" has the value <value> at the path "Slots"
    Then the response has the error code "JSON_VALUE_REJECTED"
    And the response is not "Unexpected Execution Error"

    Examples:
      | value       |
      | 1e999999    |
      | "a\u0000b"  |

  Scenario: A whole-document comparison with text that is not JSON is refused
    When I query "items" where "props" equals the text "x"
    Then the response has the error code "JSON_VALUE_MALFORMED"
    And the error says that the value is JSON text and shows the quoted form
    And the response is not "Unexpected Execution Error"

  Scenario Outline: A whole-document comparison takes a JSON scalar written as JSON text
    Given the item "A1" has the JSON document <document> in its "props" column
    And the item "B1" has the JSON document {"Slots": 5} in its "props" column
    When I query "items" where "props" equals the text <text>
    Then the answer has the item "A1"
    When I query "items" where "props" differs from the text <text>
    Then the answer has the item "B1"
    And the answer does not have the item "A1"

    Examples:
      | document | text     |
      | "x"      | "\"x\""  |
      | 5        | "5"      |
      | true     | "true"   |
      | null     | "null"   |

  Scenario: A filter term on a JSON column keeps the other filters
    Given the items "A1" and "A2" have the property "Slots" 5 in their "props" column
    And the item "B1" has the property "Slots" 3 in its "props" column
    When I query "items" where the name starts with "A" and "props" has the value 5 at the path "Slots"
    Then the answer has the items "A1" and "A2"

  Scenario: A filtered answer is cached like any other
    When I send a path filter for the value 5 and then for the value 3
    Then both responses have the header "X-Cache" with the value "MISS"
    When I send the filter for the value 5 again
    Then the response has the header "X-Cache" with the value "HIT"

  Scenario: A path filter on a full page is inside the cost limit
    When I query one page of 200 rows with a path filter and select every field of a row
    Then the response has no cost error

  Scenario: A filter with thousands of terms is refused by the cost limit
    When I send a query with 3000 path filters joined by "or"
    Then the response has the error "HC0047"

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
