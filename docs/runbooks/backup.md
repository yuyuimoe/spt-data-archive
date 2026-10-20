# Back up and restore the archive

ADR-0011 decides this: one custom-format `pg_dump` after each successful run, a copy off the host, and a restore test. The script is `scripts/backup.sh`. It needs `pg_dump`, `pg_restore` and `psql` on the PATH.

## Settings

Set them in the shell or in `.env`. `.env.example` lists them. Never put the password in a file in Git. Use `PGPASSWORD` or `~/.pgpass`.

| Variable | Default | Meaning |
|---|---|---|
| `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD` | 127.0.0.1, 5432, archive_owner | The database to dump. The user must read every table and create databases (for `verify`). |
| `ARCHIVE_DB_NAME` | archive | The database to dump. |
| `ARCHIVE_BACKUP_DIR` | ./backups | Local directory for dumps. |
| `ARCHIVE_BACKUP_COPY_DIR` | empty | Second directory, for example a mounted share on another host. Empty means no copy. |
| `ARCHIVE_BACKUP_KEEP_DAILY` | 7 | Keep the newest N dumps. |
| `ARCHIVE_BACKUP_KEEP_WEEKLY` | 4 | Also keep the newest dump of each of the last M weeks. |

## Take a dump

```bash
PGPASSWORD=... ARCHIVE_BACKUP_DIR=/srv/backups ARCHIVE_BACKUP_COPY_DIR=/mnt/share/archive scripts/backup.sh dump
```

The script writes `archive-<UTC date and time>.dump` and a `.sha256` file. It writes to a `.partial` name first and renames it when `pg_restore --list` can read the file. A failed `pg_dump` leaves no new file. It checks the copy against the checksum, then removes old dumps in both directories.

Run it after each successful archive run. The Archiver does not call it yet (see "Not done").

## Prove that a dump restores

```bash
scripts/backup.sh verify /srv/backups/archive-20261020T232537Z.dump
```

This checks the checksum and restores the dump into a scratch database. Then it compares the exact row count of every table with the source and drops the scratch database. It exits with an error if any count differs. Run it on the newest dump at least once a week. A source that changed after the dump shows as a difference: verify a dump soon after you take it.

## Restore

```bash
scripts/backup.sh restore /srv/backups/archive-20261020T232537Z.dump archive_new
```

The target database must not exist. The script creates it. A dump holds no roles (ADR-0011). If the role `archive_reader` exists on the server, the script grants it `SELECT` on every table, as `docker/initdb/01-reader-role.sh` does. On a new server, create the role first (run the initdb script or create it by hand). Then point the Api at the new database.

The server must run the same or a newer major PostgreSQL version than the one that made the dump.

## Measured on 2026-10-20

Throwaway PostgreSQL 17, 25 tables, `pg_dump` 18.6: 6,000 items, 40,000 locale entries and 2 mod datasets gave a dump of 285,344 bytes. The restored copy had the same counts in all 25 tables. `archive_reader` could read the restored tables and could not delete from them.

## Measured on 2026-10-21, on the real archive

The development database of `docker/compose.yml` held two releases, 9,255 items, 536,414 locale entries
and 7 mod datasets. `pg_dump` 18.6 against PostgreSQL 17 gave 43,490,841 bytes. `verify` read every table
of the restored copy in 6.4 seconds and found the same count in all 25 tables. `restore` into a new
database `archive_check` gave 9,255 items, and `archive_reader` read them and could not delete from them.

## Decisions

- **The script does not run itself.** A systemd timer runs `dump` and `verify`, not the Archiver. An
  Archiver that dies must not take the backup with it, and several imports may run at once (ADR-0036).
- **The off-host target is a directory.** `ARCHIVE_BACKUP_COPY_DIR` points at a mount, and `rclone` or
  `scp` can fill that mount. The script names no cloud service, because the deployment stack is not built.
- **Retention is 7 daily dumps and the newest dump of each of the last 4 weeks**, as the scenario in
  `backup-and-restore.feature` states.
- **The dump is not encrypted.** It holds published game data and published mod data, so secrecy is not
  the risk and integrity is; the checksum covers integrity. Use TLS on the transport to the second host.
- **Roles are not in the dump.** `docker/initdb/01-reader-role.sh` stays the only source of the roles.
  `restore` grants the reader when the role exists and warns when it does not.

## Not done

- No call from the Archiver or a timer. A maintainer runs the script.
- No encryption of the dump and no real off-host copy. The script copies to a directory that you mount.
- No scheduled restore test. See TD-03.
