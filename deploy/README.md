# Deploying PNMTD

The production stack: the published image `uisach/pnmtd` and PostgreSQL, in two containers with two named volumes. Hundreds of applications report to it, so the address they use must stay the same across updates.

Nothing here builds from source. To debug on a laptop, use `../start.sh` instead.

**Upgrading from v0.12 (two containers, SQLite)?** Read [UPGRADING-1.0.md](UPGRADING-1.0.md) first: `./init.sh`, then `./upgrade-1.0.sh --legacy <old folder>` instead of `docker compose up`.

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
| | the UDP listener for IoT nodes | `PNMTD_UDP_PORT` (5028/udp), on `PNMTD_UDP_BIND_ADDRESS` (0.0.0.0) |
| `pnmtd-wg` | the WireGuard gateway for MikroTik routers, only with `COMPOSE_PROFILES=wireguard` | `PNMTD_WG_PORT` (51820/udp), on `PNMTD_WG_BIND_ADDRESS` (0.0.0.0) |

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
| `./reset-totp.sh <name>` | Turns a user's 2FA off (a lost authenticator), ends their sessions and lifts a lockout |
| `./upgrade-1.0.sh --legacy <dir>` | Moves a v0.12 installation here, once ([UPGRADING-1.0.md](UPGRADING-1.0.md)) |
| `docker compose logs -f pnmtd` | Shows the app's log |
| `docker compose exec pnmtd pnmtd help` | Shows the app's command line (database, import, users) |

**Users** sign in at `/pnmt-admin/`:

- **Roles:**
  - **Viewer** reads everything except tokens and mail contents.
  - **User** also manages hosts, sensors, rules and zones.
  - **Admin** also manages users, the audit log and system health.
- **Temporary passwords:** a password from `add-user.sh` or `reset-password.sh` is valid for 7 days and must be changed at the first sign-in. With `--password-stdin`, the first line of standard input is a permanent password of your choice.
- **Two-factor authentication (TOTP):** every user can turn it on under their account, with any authenticator app (Aegis, Google or Microsoft Authenticator, Bitwarden, 1Password, …). The app shows the instance as `PNMT (<Identity>)`, or as `Auth__TotpIssuer`.
  - `Auth__TotpPolicy=Optional` (the default) suggests it after each sign-in without it. `RequiredForAdmins` and `Required` allow nothing else until it is set up, from the next request on, sessions that are already open included.
  - `./info.sh` warns about admins without it.
  - There are no recovery codes. Someone who lost their authenticator asks an admin, who uses *Reset 2FA* in the user list or `./reset-totp.sh <name>` on the server.
- **Lockout:** 5 failures in a row, wrong passwords and wrong 2FA codes alike, lock an account for 5 minutes, then 10, 20, 40 and at most 60. An admin unlocks it in the UI, or anyone on the server with `./reset-password.sh <name> --unlock-only`.
- **A lost admin password or authenticator:** `./reset-password.sh <name>` or `./reset-totp.sh <name>` on the server.

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

Each example below forwards one hostname to the app. For a proxy on the same host, set `PNMTD_BIND_ADDRESS=127.0.0.1` and `Proxy=docker`. Through the published port, the app sees requests coming from Docker's bridge, which `docker` trusts (172.16.0.0/12).

**Caddy:**

```
pnmt.example.com {
    reverse_proxy 127.0.0.1:5001
}
```

**nginx:**

```nginx
server {
    listen 443 ssl;
    server_name pnmt.example.com;
    # ssl_certificate …; ssl_certificate_key …;
    location / {
        proxy_pass http://127.0.0.1:5001;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

**Traefik:** add the labels to the `pnmtd` service in `docker-compose.override.yml`, and join Traefik's network:

```yaml
services:
  pnmtd:
    labels:
      - traefik.enable=true
      - traefik.http.routers.pnmtd.rule=Host(`pnmt.example.com`)
      - traefik.http.routers.pnmtd.entrypoints=websecure
      - traefik.http.routers.pnmtd.tls.certresolver=letsencrypt
      - traefik.http.services.pnmtd.loadbalancer.server.port=8080
    networks: [default, traefik]
networks:
  traefik:
    external: true
```

With Traefik, set `Proxy=docker`. The sensors' old `:5001` URLs keep working on the published port as long as `PNMTD_BIND_ADDRESS` is not `127.0.0.1`.

## UDP nodes

Small devices report over PNMT-UDP, a very low-data protocol on 5028/udp ([docs/udp-protocol.md](../docs/udp-protocol.md); the admin UI has the same documentation with live examples under *Admin → UDP nodes → Protocol* and on every node).

- **On by default.** The listener answers only valid packets from nodes defined in PNMT, so with no nodes it never answers anything. `Udp__Enabled=false` in `.env` turns it off; the port then stays published but closed.
- **The port.** `PNMTD_UDP_PORT=5029` in `.env` moves the published port and the listening port together. `PNMTD_UDP_BIND_ADDRESS` is separate from HTTP's bind address: HTTP is often bound to 127.0.0.1 behind a reverse proxy, UDP must be reachable from the devices.
- **Device ids.** A new node gets a random free id of 2 header bytes (1–65,535) unless one is entered. `Udp__DeviceIdBytes=1` in `.env` keeps ids at one byte (at most 255 nodes, the smallest packets); `4` uses the full 32-bit range.
- **No reverse proxy is involved.** Caddy, nginx and Traefik proxy HTTP; devices send straight to the published UDP port. Nothing needs TLS: every keyed suite authenticates.
- **Firewalls.** Docker's published ports bypass `ufw`, so 5028/udp is usually reachable as soon as the stack runs. A cloud security group still needs a rule for it.
- **Firmware uploads** go through the UI (Admin): images are stored in PostgreSQL and in every `./backup.sh`. A reverse proxy in front of the UI needs a body limit above the image size: `client_max_body_size 20m;` (nginx), `request_body { max_size 20MB }` (Caddy); Traefik has no limit by default.
- **Device keys are stored in plaintext**, like the sensor tokens: treat every backup as a credential. `./backup.sh` writes its folder 0700 and its files 0600. If a backup leaks, regenerate the nodes' keys in the UI.
- **Rootless Podman** with slirp4netns hides the devices' addresses (answers still arrive); pasta keeps them.
- `./info.sh` shows the listener's port; `docker compose exec pnmtd pnmtd udp node --server 127.0.0.1:5028 --id <id> --key <key>` runs a simulated device next to the server.

## MikroTik routers and WireGuard

PNMTD monitors MikroTik routers (RouterOS 7.13 or later) and backs up their configuration ([docs/mikrotik-plan.md](../docs/mikrotik-plan.md)). A router is added as a sensor of the type *MikroTik router*; its *Setup* tab shows a join script, which the router's admin pastes once. PNMTD then logs in as `pnmt`, a user that may read but not change anything.

- **Direct SSH works without any change here**: PNMTD connects to an address it can reach, as the read-only user. Enter PNMTD's address as the router sees it, so `pnmt` may log in from nowhere else.
- **WireGuard** lets routers behind NAT dial PNMTD; nothing has to be opened on them. It is off by default, because it needs a second service, a capability and an open port:
  - `./init.sh --wireguard` (or in `.env`: `COMPOSE_PROFILES=wireguard` and `WireGuard__Enabled=true`), then `docker compose up -d`.
  - The service `pnmtd-wg` is the same image, run as root with `NET_ADMIN` and `DAC_READ_SEARCH` only: it owns the interface `wg0` and its firewall, and reads the key ring (mounted read-only) to decrypt the tunnel keys. The app stays non-root, read-only and without capabilities.
  - It needs the WireGuard kernel module, built into Linux from 5.6 (`modprobe wireguard` on older kernels; rootless Podman needs it loaded on the host). Hosts without it (Synology DSM, some NAS) use Direct SSH.
- **Behind NAT or a router:** forward the external UDP port on the upstream router to `PNMTD_WG_PORT` on this host, and set `PNMTD_WG_EXTERNAL_HOST` (a DNS name is best) and `PNMTD_WG_EXTERNAL_PORT` in `.env`. Set there, they win over the admin page; *Admin → MikroTik & WireGuard* shows the endpoint in effect and where each part came from.
- **Firewalls:** the external port must be reachable from the routers. Docker's published ports bypass `ufw`; a cloud security group needs a rule.
- **The tunnel network** (default 10.253.0.0/16), the keepalive, the MTU and the management ports are set on the admin page. A change applies to new join scripts; the page lists the routers that need theirs pasted again.
- **Backups are credentials.** The database holds the routers' configurations (without secrets unless a router opts in) and the tunnel keys, encrypted with the key ring on `pnmtd-keys`. `./backup.sh` covers both, so a restored installation works with every router's old script.
- **The routers' configuration backups** ([docs/mikrotik-backup-plan.md](../docs/mikrotik-backup-plan.md)):
  - **Schedule:** each router exports on a schedule of its own: off, every few hours, daily (the default), weekly, monthly, or up to once a year. *Admin → MikroTik & WireGuard → Backups* sets what new routers start with, and *Routers → Backup settings…* changes many at once. With the schedule off, *Back up now* and the exports of updates and commands still take one.
  - **Encryption, one way, per router:** with a backup key set up once on the *Backups* tab, a router's backups are stored in the [age](https://age-encryption.org) format, which PNMT can write but not read. Only the public key (`age1…`) is on the server, so a copy of the database, or of a `./backup.sh` folder, reveals no encrypted configuration. A PNMT that is taken over can still read the routers' current configuration live; only their history is protected.
  - **The private key is yours alone:** keep two copies. Losing it loses every backup encrypted with it, and PNMT cannot help. Setting the key needs HTTPS.
  - **Reading encrypted backups:** in a router's *Backups* tab with *Load key…* (the key stays in that browser tab, for 15 minutes without use, and is never sent), or offline with the age tool (`apt install age`, Homebrew, or Windows builds from its site):

    ```sh
    age -d -i pnmt-backups.age-key router-20261113-0300-v12.rsc.gz.age | gunzip > router.rsc
    age -d -i pnmt-backups.age-key router-20261113-0300-v13.backup.age > router.backup
    ```

  - **Change detection** compares keyed digests, under a key in the key ring on `pnmtd-keys`. Restored without that volume, each router's next export counts as a change once.
- **Updates and commands that change a router** take the router admin's password, typed for each run: PNMTD keeps it in memory while the run lasts and never stores it, so it can change no router on its own. The password is accepted over HTTPS only (`Proxy` in `.env` when a reverse proxy terminates TLS). Commands as `pnmt` (read-only) need no password; their text and output are kept as the router's history. From the server: `docker compose exec -T pnmtd pnmtd mikrotik run <router> --command '/ip address print'` (add `--admin admin --password-stdin` and the password on standard input to run as the admin).
- `./info.sh` shows the gateway's state and the endpoint; `docker compose exec pnmtd pnmtd wireguard status` the tunnels, `pnmtd mikrotik list` the routers.

## AI (optional)

PNMT can use AI models through [OpenRouter](https://openrouter.ai): to propose rules for mails no rule handles, to rate RouterOS releases, and to look over the monitoring state for problems and patterns (docs/ai-operations-plan.md). It is off until `.env` has `Ai__Enabled=true` and `Ai__ApiKey`.

- **The models:** a main model and a verification model from another vendor (by default `deepseek/deepseek-v4.1-flash-1017` and `z-ai/glm-5.3-flashx`), and TypeSafe's Jev (`typesafe/jev-1.14`), which screens outside text for prompt injection before any other model sees it. All three come through OpenRouter, with one key.
- **What is sent:** for mail analysis, the mail and the names of the sensors a rule could feed. The situation analysis is anonymised: no names, addresses or notes leave PNMT. OpenRouter is asked to use only providers that neither store nor train on prompts (`data_collection: deny`).
- **What it costs:** every request is in *Admin → AI*, with its tokens and cost. `Ai__Budget__Usd` (30 by default) limits each month; at 80 % and 90 % the *AI budget* sensor warns, and at 100 % AI work waits for the next month.
- **When it fails:** a request is retried every minute 5 times, then after 1 hour, 3 hours and every 24 hours. The *AI* sensor turns red (421) while requests keep failing; a notification rule on it tells you.
- **Turning it off:** `Ai__Enabled=false`, or one use at a time (`Ai__Mail__Enabled`, `Ai__Analysis__Enabled`). *Admin → Debug → Test the AI* checks the key and every model.

## What lives where

| Where | Holds | Backed up by |
|---|---|---|
| volume `pnmtd_pnmtd-pgdata` | the database, with the UDP nodes' keys, their command log and firmware images, and the MikroTik routers' configurations and (encrypted) tunnel keys | `./backup.sh` (`pg_dump`) |
| volume `pnmtd_pnmtd-keys` | Data Protection keys (sessions, 2FA secrets, PNMTD's SSH key and the tunnel keys) | `./backup.sh` |
| `deploy/.env` | database password, first admin password, settings | you. It is not in any backup |
| `deploy/backups/` | backup folders | you |

The data lives in named volumes rather than bind mounts, because the app writes as a non-root user and a host folder's ownership would not match. Never back the volumes up by copying files from under a running database; use `./backup.sh`.
