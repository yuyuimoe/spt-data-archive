Feature: Back up and restore the archive
  As a maintainer
  I want a dump after each successful run
  So that the archive survives the loss of the database host

  @unverified
  Scenario: A successful run produces a dump
    Given a successful archive run
    When the backup step runs
    Then a dump file exists with the date of the run in its name
    And a copy of the dump exists off the host

  @unverified
  Scenario: A failed run produces no dump
    Given a failed archive run
    When the backup step runs
    Then no new dump file is created

  @unverified
  Scenario: A dump restores into an empty database
    Given a dump file from a successful run
    When I restore it into a new empty database
    Then every table has the same row count as in the source database

  @unverified
  Scenario: A restored database keeps the read-only role
    Given a dump file and the role setup from "docker/initdb"
    When I restore the dump into a new database
    Then the role "archive_reader" can read every table
    And the role "archive_reader" cannot write to any table

  @unverified
  Scenario: Old dumps are pruned
    Given 7 daily dumps and 4 weekly dumps exist
    When a new dump is created
    Then the oldest daily dump is removed
    And the weekly dumps are kept

  @unverified
  Scenario: A dump that cannot be read is refused
    Given a file that is not a custom-format dump
    When I verify or restore it
    Then the script fails with a message
    And no database is created

  @unverified
  Scenario: A restore never overwrites a database
    Given a database that already exists
    When I restore a dump into that name
    Then the script fails and the database is unchanged

  @unverified
  Scenario: Verify proves the restore against the source
    Given a dump of the database
    When I verify the dump
    Then the dump is restored into a scratch database
    And the row count of every table equals the source
    And the scratch database is dropped

  @unverified
  Scenario: A copy that differs from the dump is an error
    Given a copy directory is set
    When the copy does not match the checksum of the dump
    Then the backup step fails
