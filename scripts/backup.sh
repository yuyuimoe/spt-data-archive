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
: "${ARCHIVE_BACKUP_COMPRESSION:=6}"   # gzip level; 6 is the floor (ADR-0011, amendment of 2026-10-21)
: "${ARCHIVE_BACKUP_KEEP_DAILY:=7}"
: "${ARCHIVE_BACKUP_KEEP_WEEKLY:=4}"
export PGHOST PGPORT PGUSER
[[ -n "${PGPASSWORD:-}" ]] && export PGPASSWORD

die() { echo "backup: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "$1 not found"; }
need pg_dump; need pg_restore; need psql

# A dump is never stored uncompressed (ADR-0011). The floor is level 6: a plain dump of the archive
# grows with every release, and the compressed form is about a tenth of it.
case "$ARCHIVE_BACKUP_COMPRESSION" in
  [0-9]|1[0-9])              method=gzip; level=$ARCHIVE_BACKUP_COMPRESSION ;;
  gzip:[0-9]|gzip:1[0-9])    method=gzip; level=${ARCHIVE_BACKUP_COMPRESSION#gzip:} ;;
  zstd:[0-9]|zstd:1[0-9])    method=zstd; level=${ARCHIVE_BACKUP_COMPRESSION#zstd:} ;;
  xz:[0-9]|xz:1[0-9])        method=xz;   level=${ARCHIVE_BACKUP_COMPRESSION#xz:} ;;
  *) die "ARCHIVE_BACKUP_COMPRESSION takes a level as N, gzip:N, zstd:N or xz:N; got '$ARCHIVE_BACKUP_COMPRESSION'" ;;
esac
(( level >= 6 )) || die "compression level $level is below the floor of 6 (ADR-0011); raise ARCHIVE_BACKUP_COMPRESSION"
export ARCHIVE_BACKUP_COMPRESSION

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
  else
    # On a new server the role does not exist yet, and a restore that says nothing leaves the Api unable to read.
    echo "backup: warning: role archive_reader is missing on this server, so $db has no read-only grant. Create the roles first (docker/initdb/01-reader-role.sh), then restore again." >&2
  fi
}

case "${1:-}" in
dump)
  mkdir -p "$ARCHIVE_BACKUP_DIR"
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  file="$ARCHIVE_BACKUP_DIR/archive-$stamp.dump"
  # Write to a temp name, so a half-written file never looks like a backup. -Z keeps it compressed.
  pg_dump -Fc -Z "$ARCHIVE_BACKUP_COMPRESSION" -d "$ARCHIVE_DB_NAME" -f "$file.partial" ||
    { rm -f "$file.partial"; die "pg_dump failed"; }
  pg_restore --list "$file.partial" >/dev/null || { rm -f "$file.partial"; die "dump is unreadable"; }
  mv "$file.partial" "$file"
  ( cd "$ARCHIVE_BACKUP_DIR" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256" )
  if [[ -n $ARCHIVE_BACKUP_COPY_DIR ]]; then
    mkdir -p "$ARCHIVE_BACKUP_COPY_DIR"
    cp "$file" "$file.sha256" "$ARCHIVE_BACKUP_COPY_DIR/"
    (cd "$ARCHIVE_BACKUP_COPY_DIR" && sha256sum -c "$(basename "$file").sha256" >/dev/null) || die "copy differs from the dump"
  fi
  # Retention keeps two pools apart, and it reads both units from the date in the file name, never from a
  # count of days as 86 400 seconds (ADR-0011, amendment of 2026-10-22):
  #
  #   daily   the newest dump of each of the KEEP_DAILY most recent days that hold a dump;
  #   weekly  the newest dump of each of the KEEP_WEEKLY most recent ISO weeks before the week of the
  #           oldest day that the daily pool kept.
  #
  # The day is the date itself, and the week is its ISO year and week (%G-%V): a leap year, a month of 30
  # or 31 days, a week that holds the turn of the year and a clock change all leave both units alone.
  for dir in "$ARCHIVE_BACKUP_DIR" ${ARCHIVE_BACKUP_COPY_DIR:+"$ARCHIVE_BACKUP_COPY_DIR"}; do
    declare -A keep=() day_seen=() week_seen=()
    mapfile -t all < <(cd "$dir" && ls -1 archive-*.dump 2>/dev/null | sort -r)
    nday=0; oldest_day=""; older=()
    for f in "${all[@]}"; do   # newest first
      day=${f#archive-}; day=${day:0:8}
      if [[ ! $day =~ ^[0-9]{8}$ ]]; then
        echo "backup: keep $f: its name holds no date" >&2; keep[$f]=1; continue
      fi
      if [[ -z ${day_seen[$day]:-} ]]; then
        day_seen[$day]=1; nday=$((nday+1))
        if (( nday <= ARCHIVE_BACKUP_KEEP_DAILY )); then keep[$f]=1; oldest_day=$day; continue; fi
      fi
      older+=("$f")
    done
    # The weekly pool starts one week before the week of the oldest day that the daily pool kept, so the two
    # pools never claim the same week and neither one counts the weeks of the other.
    cutoff=$(date -u -d "${oldest_day:0:4}-${oldest_day:4:2}-${oldest_day:6:2}" +%G-%V) \
      || { echo "backup: keep every dump of $dir: the days of its names are no dates" >&2; continue; }
    nweek=0
    for f in "${older[@]+"${older[@]}"}"; do   # newest first, outside the daily pool
      day=${f#archive-}; day=${day:0:8}
      if ! week=$(date -u -d "${day:0:4}-${day:4:2}-${day:6:2}" +%G-%V 2>/dev/null); then
        echo "backup: keep $f: $day is no date" >&2; keep[$f]=1; continue
      fi
      [[ $week < $cutoff ]] || continue
      [[ -z ${week_seen[$week]:-} ]] || continue
      week_seen[$week]=1; nweek=$((nweek+1))
      (( nweek <= ARCHIVE_BACKUP_KEEP_WEEKLY )) && keep[$f]=1
    done
    for f in "${all[@]}"; do [[ -n ${keep[$f]:-} ]] || rm -f "$dir/$f" "$dir/$f.sha256"; done
    unset keep day_seen week_seen older all
  done
  echo "backup: wrote $file ($(stat -c %s "$file") bytes, $method level $level)"
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
