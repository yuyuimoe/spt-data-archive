#!/usr/bin/env bash
# Back up and restore the archive database (ADR-0011).
#
#   scripts/backup.sh dump                 write one custom-format dump, copy it, prune old ones
#   scripts/backup.sh verify FILE          prove that FILE restores: restore to a scratch database and compare row counts
#   scripts/backup.sh restore FILE DBNAME  restore FILE into a new, empty database DBNAME
#
# Settings come from the environment (see .env.example). The password is never an argument:
# PostgreSQL tools read PGPASSWORD. Nothing here writes to the source database.
set -euo pipefail

: "${PGHOST:=127.0.0.1}" "${PGPORT:=5432}" "${PGUSER:=archive_owner}"
: "${ARCHIVE_DB_NAME:=archive}"
: "${ARCHIVE_BACKUP_DIR:=./backups}"
: "${ARCHIVE_BACKUP_COPY_DIR:=}"       # second location, for example a mounted share; empty = no copy
: "${ARCHIVE_BACKUP_KEEP_DAILY:=7}"
: "${ARCHIVE_BACKUP_KEEP_WEEKLY:=4}"
export PGHOST PGPORT PGUSER
[[ -n "${PGPASSWORD:-}" ]] && export PGPASSWORD

die() { echo "backup: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "$1 not found"; }
need pg_dump; need pg_restore; need psql

# "table<TAB>count" for every user table, exact counts, sorted.
row_counts() { # DBNAME
  local db=$1 t
  psql -X -qAt -d "$db" -c "select quote_ident(schemaname)||'.'||quote_ident(tablename) from pg_tables where schemaname='public' order by 1" |
    while read -r t; do printf '%s\t%s\n' "$t" "$(psql -X -qAt -d "$db" -c "select count(*) from $t")"; done
}

do_restore() { # FILE DBNAME
  local file=$1 db=$2
  [[ -f $file ]] || die "no such file: $file"
  [[ $(psql -X -qAt -d postgres -c "select 1 from pg_database where datname='$db'") == 1 ]] && die "database $db exists; restore needs a new one"
  # Fail early, and cheaply, on a file that is not a readable dump.
  pg_restore --list "$file" >/dev/null || die "$file is not a readable custom-format dump"
  psql -X -q -d postgres -c "create database \"$db\"" || die "cannot create $db"
  # The dump holds no roles (ADR-0011). Ownership and grants are skipped; docker/initdb/01-reader-role.sh sets the roles.
  pg_restore --no-owner --no-privileges --exit-on-error -d "$db" "$file"
  # Same grants as docker/initdb/01-reader-role.sh, when the role exists on this server.
  if [[ $(psql -X -qAt -d postgres -c "select 1 from pg_roles where rolname='archive_reader'") == 1 ]]; then
    psql -X -q -v ON_ERROR_STOP=1 -d "$db" <<SQL
GRANT CONNECT ON DATABASE "$db" TO archive_reader;
GRANT USAGE ON SCHEMA public TO archive_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO archive_reader;
SQL
  fi
}

case "${1:-}" in
dump)
  mkdir -p "$ARCHIVE_BACKUP_DIR"
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  file="$ARCHIVE_BACKUP_DIR/archive-$stamp.dump"
  # Write to a temp name, so a half-written file never looks like a backup.
  pg_dump -Fc -d "$ARCHIVE_DB_NAME" -f "$file.partial" || { rm -f "$file.partial"; die "pg_dump failed"; }
  pg_restore --list "$file.partial" >/dev/null || { rm -f "$file.partial"; die "dump is unreadable"; }
  mv "$file.partial" "$file"
  ( cd "$ARCHIVE_BACKUP_DIR" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256" )
  if [[ -n $ARCHIVE_BACKUP_COPY_DIR ]]; then
    mkdir -p "$ARCHIVE_BACKUP_COPY_DIR"
    cp "$file" "$file.sha256" "$ARCHIVE_BACKUP_COPY_DIR/"
    (cd "$ARCHIVE_BACKUP_COPY_DIR" && sha256sum -c "$(basename "$file").sha256" >/dev/null) || die "copy differs from the dump"
  fi
  # Retention: newest N daily dumps, plus the newest dump of each of the last M ISO weeks. Prune every directory.
  for dir in "$ARCHIVE_BACKUP_DIR" ${ARCHIVE_BACKUP_COPY_DIR:+"$ARCHIVE_BACKUP_COPY_DIR"}; do
    declare -A keep=(); declare -A seen=()
    mapfile -t all < <(cd "$dir" && ls -1 archive-*.dump 2>/dev/null | sort -r)
    i=0; nweek=0
    for f in "${all[@]}"; do   # newest first
      i=$((i+1)); d=${f#archive-}; d=${d:0:8}
      w=$(date -u -d "${d:0:4}-${d:4:2}-${d:6:2}" +%G-%V)
      (( i <= ARCHIVE_BACKUP_KEEP_DAILY )) && keep[$f]=1
      if [[ -z ${seen[$w]:-} ]]; then   # first seen = newest dump of that week
        seen[$w]=1; nweek=$((nweek+1))
        (( nweek <= ARCHIVE_BACKUP_KEEP_WEEKLY )) && keep[$f]=1
      fi
    done
    for f in "${all[@]}"; do [[ -n ${keep[$f]:-} ]] || rm -f "$dir/$f" "$dir/$f.sha256"; done
    unset keep seen all
  done
  echo "backup: wrote $file ($(stat -c %s "$file") bytes)"
  ;;
restore)
  [[ $# -eq 3 ]] || die "usage: backup.sh restore FILE DBNAME"
  do_restore "$2" "$3"
  echo "backup: restored $2 into $3"
  ;;
verify)
  [[ $# -eq 2 ]] || die "usage: backup.sh verify FILE"
  file=$2
  [[ -f $file.sha256 ]] && { (cd "$(dirname "$file")" && sha256sum -c "$(basename "$file").sha256" >/dev/null) || die "checksum mismatch"; }
  scratch="archive_verify_$$"
  trap 'psql -X -q -d postgres -c "drop database if exists \"$scratch\"" >/dev/null 2>&1 || true' EXIT
  do_restore "$file" "$scratch"
  row_counts "$ARCHIVE_DB_NAME" > "/tmp/backup-src.$$"
  row_counts "$scratch" > "/tmp/backup-restored.$$"
  if diff -u "/tmp/backup-src.$$" "/tmp/backup-restored.$$"; then
    echo "backup: verified $file: $(wc -l < "/tmp/backup-src.$$") tables, same row counts as $ARCHIVE_DB_NAME"
    rm -f "/tmp/backup-src.$$" "/tmp/backup-restored.$$"
  else
    rm -f "/tmp/backup-src.$$" "/tmp/backup-restored.$$"
    die "row counts differ (source may have changed since the dump)"
  fi
  ;;
*) die "usage: backup.sh dump | verify FILE | restore FILE DBNAME" ;;
esac
