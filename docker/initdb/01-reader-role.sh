#!/bin/sh
# Runs once, when the Postgres data directory is first created.
# Creates the role the public API uses: it can read every table, now and in the future, and nothing else.
# Use an alphanumeric ARCHIVE_READER_PASSWORD (it is interpolated into SQL).
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE ROLE archive_reader LOGIN PASSWORD '${ARCHIVE_READER_PASSWORD}';
    GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO archive_reader;
    GRANT USAGE ON SCHEMA public TO archive_reader;
    GRANT SELECT ON ALL TABLES IN SCHEMA public TO archive_reader;
    ALTER DEFAULT PRIVILEGES FOR ROLE ${POSTGRES_USER} IN SCHEMA public GRANT SELECT ON TABLES TO archive_reader;
EOSQL
