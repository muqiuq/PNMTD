# Deploying PNMTD

The production stack: the published image `uisach/pnmtd` and PostgreSQL, in two containers with two named volumes. Hundreds of applications report to it, so the address they use must stay the same across updates.

Nothing here builds from source. To debug on a laptop, use `../start.sh` instead.

**Upgrading from v0.12 (two containers, SQLite)?** Read [UPGRADING-1.0.md](UPGRADING-1.0.md) first. *(It arrives with the 1.0 release.)*

## First run

```sh
git clone https://github.com/muqiuq/PNMTD.git /srv/pnmtd
cd /srv/pnmtd/deploy

./init.sh --url https://pnmt.example.com   # writes .env with generated secrets
docker compose up -d
./info.sh
```

`init.sh` writes `.env` with mode `0600`. It contains every setting from [.env.example](.env.example), a generated database password, and the password of the first admin, which `init.sh` prints once.

`init.sh` refuses to overwrite an existing `.env` unless you pass `--force`:

- The secrets exist only in that file.
- The admin password is read only once, while the database has no users.
- `--force` keeps `PNMTD_DB_PASSWORD`, because the database keeps the password it was created with.

## The stack

| Container | What | Published |
|---|---|---|
| `db` | PostgreSQL 18 | nothing |
| `pnmtd` | the sensor API, the monitoring tasks and the admin UI | `PNMTD_PORT` (5001) → 8080, on `PNMTD_BIND_ADDRESS` (0.0.0.0) |

`pnmtd` runs hardened:

- As a non-root user, with a read-only root filesystem and every capability dropped.
- PING sensors use unprivileged ICMP sockets (`net.ipv4.ping_group_range`), so no capability is needed.

Startup and migrations:

- `pnmtd` starts once `db` is healthy.
- A new release applies its own database migrations as it starts.
- It refuses to start on a database that a newer release has migrated.

Local changes to the stack go into `docker-compose.override.yml`. Git ignores that file, so `git pull` can replace `docker-compose.yml`.

## Everyday operation

| Command | What it does |
|---|---|
| `./info.sh` | Shows whether it is running and healthy, what it runs, how it is reached, and what needs attention |
| `./info.sh --quiet` | Exits 0 when it is running and healthy, for monitoring |
| `./update.sh` | Takes a backup, then pulls and recreates the containers |
| `./update.sh --tag v1.0.1` | Pins another release first |
| `./backup.sh` | Writes a backup folder to `backups/` |
| `./backup.sh --keep 14` | …and deletes all but the newest 14 |
| `./restore.sh <folder>` | Restores a backup, here or into a fresh deployment |
| `./add-user.sh <name> [--role viewer\|user\|admin]` | Adds a user, and shows a temporary password once |
| `./list-users.sh` | Lists the users: role, state, 2FA, lockout, last sign-in |
| `./reset-password.sh <name>` | Gives a user a new temporary password, ends their sessions and lifts a lockout |
| `./reset-password.sh <name> --unlock-only` | Only lifts a lockout |
| `docker compose logs -f pnmtd` | Shows the app's log |
| `docker compose exec pnmtd pnmtd help` | Shows the app's command line (database, import, users) |

**Users** sign in at `/pnmt-admin/`:

- **Roles:**
  - **Viewer** reads everything except tokens and mail contents.
  - **User** also manages hosts, sensors, rules and zones.
  - **Admin** also manages users, the audit log and system health.
- **Temporary passwords:** a password from `add-user.sh` or `reset-password.sh` is valid for 7 days and must be changed at the first sign-in. With `--password-stdin`, the first line of standard input is a permanent password of your choice.
- **Lockout:** 5 wrong passwords in a row lock an account for 5 minutes, then 10, 20, 40 and at most 60. An admin unlocks it in the UI, or anyone on the server with `./reset-password.sh <name> --unlock-only`.
- **A lost admin password:** `./reset-password.sh <name>` on the server.

**Scheduled backups** (cron, daily at 03:30):

```
30 3 * * * /srv/pnmtd/deploy/backup.sh --quiet --keep 14 >>/srv/pnmtd/deploy/logs/backup.log 2>&1
```

A backup folder holds two files:

- `database.pgdump`, made with `pg_dump --format=custom`.
- `keys.tar.gz`, the Data Protection keys. Without them, sessions end and the 2FA secrets can no longer be read.

The folder is `0700` and its files are `0600`, because they hold password hashes. A backup does not include `.env`; keep a copy of that somewhere else.

A **new PostgreSQL major version** means `./backup.sh`, the new image in `docker-compose.yml`, then `./restore.sh`. Never just change the tag on the old volume.

## Behind a reverse proxy

- **Path prefixes:** forward every path unchanged, with no prefix stripped or added. The app needs `/event/`, `/read/`, `/api/` and `/pnmt-admin/` at the root of its host.
- **Settings:**
  - Set `Proxy=docker` in `.env` if the proxy runs in a Docker network, or else set it to the proxy's address. Without it, lockout and the audit log see the proxy's address instead of the client's.
  - With the proxy on the same host, set `PNMTD_BIND_ADDRESS=127.0.0.1`.
- **Sensor URLs:** they stay plain HTTP if they were before. The app never redirects them.
- **Restricting paths:** the app enforces sign-in itself. If you restrict paths at the proxy anyway, keep `/event/` and `/read/` open, and match them case-insensitively, because the app accepts `/EVENT/` too.

## What lives where

| Where | Holds | Backed up by |
|---|---|---|
| volume `pnmtd_pnmtd-pgdata` | the database | `./backup.sh` (`pg_dump`) |
| volume `pnmtd_pnmtd-keys` | Data Protection keys (sessions, 2FA secrets) | `./backup.sh` |
| `deploy/.env` | database password, first admin password, settings | you. It is not in any backup |
| `deploy/backups/` | backup folders | you |

The data lives in named volumes rather than bind mounts, because the app writes as a non-root user and a host folder's ownership would not match. Never back the volumes up by copying files from under a running database; use `./backup.sh`.
