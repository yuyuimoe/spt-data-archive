Feature: Back up and restore the archive
  As a maintainer
  I want a dump after each successful run
  So that the archive survives the loss of the database host

  @backlog
  Scenario: A successful run produces a dump
    Given a successful archive run
    When the backup step runs
    Then a dump file exists with the date of the run in its name
    And a copy of the dump exists off the host

  @backlog
  Scenario: A failed run produces no dump
    Given a failed archive run
    When the backup step runs
    Then no new dump file is created

  @backlog
  Scenario: A dump restores into an empty database
    Given a dump file from a successful run
    When I restore it into a new empty database
    Then every table has the same row count as in the source database

  @backlog
  Scenario: A restored database keeps the read-only role
    Given a dump file and the role setup from "docker/initdb"
    When I restore the dump into a new database
    Then the role "archive_reader" can read every table
    And the role "archive_reader" cannot write to any table

  @backlog
  Scenario: Old dumps are pruned
    Given 7 daily dumps and 4 weekly dumps exist
    When a new dump is created
    Then the oldest daily dump is removed
    And the weekly dumps are kept
