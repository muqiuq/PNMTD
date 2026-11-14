# Deploying PNMTD

The production stack: the published image `uisach/pnmtd` and PostgreSQL, in two containers with named volumes, and a folder on this host for uploaded files. Hundreds of applications report to it, so the address they use must stay the same across updates.

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

`init.sh` writes `.env` with mode `0600`. It contains every setting from [.env.example](.env.example), a generated database password, and the password of the first admin, which `init.sh` prints once. It also creates the folder for uploaded files, `files/` ([Uploaded files](#uploaded-files)).

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
| `./update.sh` | Takes a backup, then pulls and recreates the containers; creates the files folder if it is missing |
| `./update.sh --tag v1.0.1` | Pins another release first |
| `./backup.sh` | Writes a backup folder to `backups/` |
| `./backup.sh --keep 14` | …and deletes all but the newest 14 |
| `./backup.sh --no-files` | …without the uploaded files, for a host that backs up their folder itself |
| `./restore.sh <folder>` | Restores a backup, here or into a fresh deployment |
| `./restore.sh <folder> --force` | …and replaces what the files folder holds |
| `./add-user.sh <name> [--groups <group,group>] [--admin]` | Adds a user, and shows a temporary password once. The rights come from the groups (*Operators* and *Viewers* to start with), or every right with `--admin` |
| `./list-users.sh` | Lists the users: admin or their groups, state, 2FA, lockout, last sign-in |
| `./reset-password.sh <name>` | Gives a user a new temporary password, ends their sessions and lifts a lockout |
| `./reset-password.sh <name> --unlock-only` | Only lifts a lockout |
| `./reset-totp.sh <name>` | Turns a user's 2FA off (a lost authenticator), ends their sessions and lifts a lockout |
| `./upgrade-1.0.sh --legacy <dir>` | Moves a v0.12 installation here, once ([UPGRADING-1.0.md](UPGRADING-1.0.md)) |
| `docker compose logs -f pnmtd` | Shows the app's log |
| `docker compose exec pnmtd pnmtd help` | Shows the app's command line (database, import, users) |

**Users** sign in at `/pnmt-admin/`:

- **Rights:** a user is an admin, with every right and alone managing users and groups, or has what their groups give: a level in each area (Monitoring *View* or *Edit*, Mail *Read*, Redmine *View* or *Link*, Zabbix *View* or *Details*, Servers *Manage*, …), listed with what each allows under *Documentation → Groups and rights*. The update to 1.4 made the old roles into the groups *Operators* (what *User* could do) and *Viewers* (what *Viewer* could). *Admin → Groups* changes them or adds others.
- **The audit log** (*Admin → Audit log*) records every change, sign-in and refusal: who, when, from where, and each field's old and new value (never a secret's). It keeps `Audit__KeepDays` (400) days and at most `Audit__MaxEntries` (500,000) entries; *Download CSV* exports what the filters show. A host's or sensor's *History* tab shows its own.
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

A backup folder holds three files:

- `database.pgdump`, made with `pg_dump --format=custom`.
- `keys.tar.gz`, the Data Protection keys. Without them, sessions end and the 2FA secrets can no longer be read.
- `files.tar`, the folder of uploaded files, taken after the database and uncompressed (images and documents are compressed already). Uploads in between are only extra files, listed as unknown after a restore. A file the daily cleanup (7 days after it was marked deleted) or an Admin removes while the backup runs may be named by the dump and missing from the tar: the cleanup's are marked deleted and expected; an Admin's delete of those minutes shows as missing until it is deleted again. `--no-files` leaves it out.

The folder is `0700` and its files are `0600`, because they hold password hashes. A backup does not include `.env`; keep a copy of that somewhere else.

`./restore.sh` puts `files.tar` into the files folder as the app's user. The folder must be empty, or `--force` replaces its contents; it refuses before it changes anything else. A backup without `files.tar` (an older one, or `--no-files`) leaves the folder as it is.

A **new PostgreSQL major version** means `./backup.sh`, the new image in `docker-compose.yml`, then `./restore.sh`. Never just change the tag on the old volume.

## Behind a reverse proxy

- **Path prefixes:** forward every path unchanged, with no prefix stripped or added. The app needs `/event/`, `/read/`, `/api/` and `/pnmt-admin/` at the root of its host.
- **Settings:**
  - Set `Proxy=docker` in `.env` if the proxy runs in a Docker network, or else set it to the proxy's address. Without it, lockout and the audit log see the proxy's address instead of the client's.
  - With the proxy on the same host, set `PNMTD_BIND_ADDRESS=127.0.0.1`.
- **Sensor URLs:** they stay plain HTTP if they were before. The app never redirects them.
- **Restricting paths:** the app enforces sign-in itself. If you restrict paths at the proxy anyway, keep `/event/` and `/read/` open, and match them case-insensitively, because the app accepts `/EVENT/` too. The agents of servers (Windows and Linux) need `/agent/` as well.
- **Uploads:** pages and locations take images up to 2 MB and attachments up to 25 MB, and firmware images are uploaded too. A proxy needs a body limit above them: `client_max_body_size 32m;` (nginx, in the example below), `request_body { max_size 32MB }` (Caddy); Traefik has no limit by default. A proxy's own 413 is named as such in the UI.
- **WebSockets:** the agents keep a WebSocket open on `/agent/v1/ws`, on the same address and port. Caddy and Traefik pass them as they are; nginx needs the `Upgrade` lines below and a `proxy_read_timeout` above the agents' 30 s ping. Pass the `Host` header on: agents sign the address they use, and PNMTD checks it against `PublicBaseUrl` or that header.

Each example below forwards one hostname to the app. For a proxy on the same host, set `PNMTD_BIND_ADDRESS=127.0.0.1` and `Proxy=docker`. Through the published port, the app sees requests coming from Docker's bridge, which `docker` trusts (172.16.0.0/12).

**Caddy:**

```
pnmt.example.com {
    request_body {
        max_size 32MB
    }
    reverse_proxy 127.0.0.1:5001
}
```

**nginx:**

```nginx
# The agents' WebSocket (map goes in the http block).
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name pnmt.example.com;
    # ssl_certificate …; ssl_certificate_key …;
    location / {
        proxy_pass http://127.0.0.1:5001;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 120s;
        client_max_body_size 32m;
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
- **Firmware uploads** go through the UI (Admin): images are stored in PostgreSQL and in every `./backup.sh`. A reverse proxy in front of the UI needs a body limit above the image size: the 32 MB of [Behind a reverse proxy](#behind-a-reverse-proxy) cover them.
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
- **Security advisories:** PNMTD reads MikroTik's release feed every hour (`Mikrotik__Advisories__*` in `.env`). A release whose changelog mentions a CVE or an exploit is assessed, by the AI too when it is on; when one rated critical is newer than a router's version on the same channel, the *RouterOS advisories* sensor reports 422 with the routers behind. *Admin → MikroTik & WireGuard* lists the releases, the *Routers* tab marks the routers, and a router's *Updates* tab highlights the security lines in the changelog.
- `./info.sh` shows the gateway's state and the endpoint; `docker compose exec pnmtd pnmtd wireguard status` the tunnels, `pnmtd mikrotik list` the routers.

## Servers with an agent

Servers report through PNMT's agent, on PNMT's address and port, and are managed on *Admin → Servers*. **Windows** servers and PCs run the PNMT sensor service ([docs/windows-sensor-plan.md](../docs/windows-sensor-plan.md)); **Linux** servers (Debian 12 and 13, also Proxmox VE and Ubuntu LTS) run the PNMT agent for Linux ([docs/linux-sensor-plan.md](../docs/linux-sensor-plan.md)). The settings are `Agents__*` in `.env` (`deploy/.env.example`).

- **The agents' files come from PNMT itself** (`/agent/v1/download/…`), so a server needs to reach only PNMT: PNMTD fetches its own version's files from the GitHub release `sensor-{version}` when first asked for, checks each against its `.sha256`, and keeps them in the volume `pnmtd-downloads` (in `sensor-{version}/`; an update fetches anew and deletes the older ones). *Admin → Servers → Download* shows each file's state and has *Fetch again*. GitHub's links, with the same hashes, are offered next to PNMT's.
- **A PNMT without internet:** put the release's files (`pnmt-agent-linux-x64`, `pnmt-agent-linux-arm64`, `pnmt-sensor-x64.exe`, `pnmt-sensor-arm64.exe`, each with its `.sha256`) into a folder on the host and mount it instead of the volume, in `docker-compose.override.yml`:

  ```yaml
  services:
    pnmtd:
      volumes:
        - /srv/pnmt-agent-files:/var/lib/pnmtd/downloads:ro
  ```

  PNMTD serves only files that match their `.sha256`. `Agents__ServeDownloads=false` turns the download paths off (the dialogs then offer GitHub only).

### Windows

One executable, `pnmt-sensor-x64.exe` or `pnmt-sensor-arm64.exe`, from the GitHub release `sensor-{version}` of PNMT's own version (*Admin → Servers → Download* links it with its SHA-256).

- **Nothing to open.** The agents connect to PNMT's existing address and port over HTTPS, with a WebSocket on `/agent/v1/ws`. Behind nginx, add the WebSocket lines above. Without a reverse proxy, the published port works as it is. Plain http:// works too, after a confirmation on the server (the window shows PNMT's key to compare with the *Add server* dialog; the command line needs `--allow-http`); it is not encrypted, so use it only on a network you trust.
- **Adding a server:** *Add server…* in a host's menu (Admin), with *Windows* as the platform, gives a token, `xxxx-xxxx`, valid 24 hours and once. On the server, run the executable as an administrator, enter PNMT's URL and the token. Unattended: `pnmt-sensor-x64.exe join --url https://pnmt.example.com --token xxxx-xxxx --quiet` (exit 0 when connected).
- **Unsigned.** The executable has no code signature: compare `Get-FileHash .\pnmt-sensor-x64.exe` with the SHA-256 PNMT shows; SmartScreen asks (*More info → Run anyway*); for scripts, `Unblock-File`.
- **Trust.** Each agent has a key in the server's machine store; PNMTD's signing key is under Data Protection in the keys volume, which `./backup.sh` includes. A restore without the keys volume means every server must re-join.
- **Updating:** run the newer executable on the server: its window offers *Update*, or `pnmt-sensor.exe update` on the command line. The identity and key stay.
- **Logins** need the audit policy: the server's *Access* tab lists what is missing, with the `auditpol` lines to fix it. The agent changes nothing itself.
- `docker compose exec pnmtd pnmtd agent simulate --url https://pnmt.example.com --token xxxx-xxxx` runs a fake Windows agent next to the server, for a look without Windows (`--platform linux` for a Linux one); `pnmtd agent tokens` lists the join tokens.

### Linux

One binary, `pnmt-agent`, with .NET included, a systemd service and a command line; no GUI.

- **Adding a server:** *Add server…* in a host's menu (Admin), with *Linux* as the platform, shows the token and **one line** to run as root on the server: it downloads the binary from PNMT, checks its SHA-256 with `sha256sum -c`, installs it to `/usr/local/sbin`, and runs `pnmt-agent join --url … --token …`, which writes the unit `pnmt-agent.service`, starts it, and exits 0 when connected. Toggles choose x64 or arm64, `wget` or `curl`, root or `sudo`, and PNMT or GitHub as the source. Over plain http:// the line has `--allow-http`, and `join` prints PNMT's key to compare.
- **What PNMT may change is decided on the server**, in `/etc/pnmt-agent/config.yaml` (root's, 0600): removing SSH keys (per account, with protected break-glass keys) and running bash scripts (as named accounts), both off by default. `pnmt-agent config check` shows the effective rights. A file not owned by root, writable by others or broken grants nothing. PNMT can't write it.
- **SSH keys:** the agent reports every `authorized_keys` file sshd uses and which key each SSH login used. *Admin → SSH keys* names each key and assigns it to a user; a key nobody registered raises 465. Removing a key needs `ssh_keys.remove: true` on that server; a backup is kept there, and `pnmt-agent keys restore` puts it back. PNMT can never add a key.
- **Logins** come from the journal. Debian 12 and later and Ubuntu keep it persistently; the server's *Access* tab lists what is missing (a volatile journal, sshd's `LogLevel` below `VERBOSE`, which hides SFTP sessions and refused keys) with the commands that fix it.
- **Updating:** `pnmt-agent update` on the server, after PNMT's update: it downloads PNMT's version from PNMT and checks it against a manifest PNMTD signed with the key pinned at the join.
- **Uninstalling:** `pnmt-agent uninstall` (`config.yaml` and the logs stay; `--purge` deletes them too). PNMT keeps the server and its history, marked uninstalled.
- **Scripts need D-Bus** on the server (`systemd-run` starts them in a unit of their own): Debian's and Ubuntu's standard installations have it; a minimal one may need `apt install dbus`.

## AI (optional)

PNMT can use AI models through [OpenRouter](https://openrouter.ai): to propose rules for mails no rule handles, to rate RouterOS releases, and to look over the monitoring state for problems and patterns (docs/ai-operations-plan.md). It is off until `.env` has `Ai__Enabled=true` and `Ai__ApiKey`.

- **The models:** a main model and a verification model from another vendor (by default `deepseek/deepseek-v4.1-flash-1017` and `z-ai/glm-5.3-flashx`), and TypeSafe's Jev (`typesafe/jev-1.14`), which screens outside text for prompt injection before any other model sees it. All three come through OpenRouter, with one key.
- **What is sent:** for mail analysis, the mail and the names of the sensors a rule could feed. The situation analysis is anonymised: no names, addresses or notes leave PNMT. OpenRouter is asked to use only providers that neither store nor train on prompts (`data_collection: deny`).
- **What it costs:** every request is in *Admin → AI*, with its tokens and cost. `Ai__Budget__Usd` (30 by default) limits each month; at 80 % and 90 % the *AI budget* sensor warns, and at 100 % AI work waits for the next month.
- **When it fails:** a request is retried every minute 5 times, then after 1 hour, 3 hours and every 24 hours. The *AI* sensor turns red (421) while requests keep failing; a notification rule on it tells you.
- **The situation analysis** runs every `Ai__Analysis__IntervalMinutes` (60) when something changed, and early on a storm of failures, when the uplink comes back or the WireGuard gateway goes down. Its findings go to three sensors on the host *PNMT*: *AI analysis: critical* (High), *warnings* (Normal) and *info* (Low). A notification rule on them, or a scope such as *at least High*, decides who hears of them; warnings and critical findings also wait under *Decisions*, where similar ones can be muted for 7 days.
- **Turning it off:** `Ai__Enabled=false`, or one use at a time (`Ai__Mail__Enabled`, `Ai__Analysis__Enabled`). *Admin → Debug → Test the AI* checks the key and every model.

## Redmine (optional)

PNMT shows the tickets of chosen Redmine projects (5.1 or later; docs/audit-redmine-zabbix-plan.md §6): an overview of the most important ones, a search, a page per ticket, and links from tickets to hosts, sensors, locations and knowledge base pages, with their panels there. It changes nothing in Redmine.

- **An account of its own:** a Redmine user *pnmt* with a role that may only *View issues* (and *View private notes* only if `Redmine__ShowPrivateNotes=true`), member of the projects PNMT shows. *Administration → Settings → API* enables the REST API; the user's API key (*My account → API access key*) goes into `Redmine__ApiKey`. It is sent only as a header, never in a URL, and never shown.
- **The settings:** `Redmine__Url`, `Redmine__ApiKey` and `Redmine__Projects` (ids or identifiers) turn it on; `Redmine__PublicUrl` when browsers reach Redmine at another address; `Redmine__TextFormatting` as in Redmine's settings, so descriptions render right.
- **Who sees it:** groups with Redmine *View* (tickets) or *Link* (linking them). *Viewers* get neither: tickets hold customers' and colleagues' text.
- **A copy of the linked tickets** (number, subject, status, assignee) is refreshed every `Redmine__SyncMinutes`, so panels work when Redmine is slow or down. When it doesn't answer, or refuses the key, twice in a row, the system sensor *Redmine* writes 429.
- **Checking it:** *Admin → System health → Integrations → Test* signs in with the key and looks for every project.

## Zabbix (optional)

PNMT reads Zabbix (6.0 or later; docs/audit-redmine-zabbix-plan.md §7): the open problems by severity and category, the hosts, a page per host, and **Zabbix sensors**: a Zabbix host attached to a PNMT host fails it from a severity up, with notifications as for any sensor. It changes nothing in Zabbix.

- **An account of its own,** with the least it needs:
  - a **user role** *PNMT API* of type *User*, with *API access* on and the *API methods* limited to an **allow list**: `hostgroup.get`, `host.get`, `problem.get`, `trigger.get`, `item.get`, and no frontend sections;
  - a **user group** *PNMT readers* with *Frontend access: Disabled* and **read** permission on the host groups PNMT should see;
  - a **user** *pnmt* in that group with that role, and an **API token** for it (*Users → API tokens*), without expiry or with a reminder to renew it. The token goes into `Zabbix__ApiToken`; PNMT sends it the way the version wants (in the request up to 6.2, as a header from 6.4), never in a URL, and never shows it.
- **The settings:** `Zabbix__Url` (the frontend's address) and `Zabbix__ApiToken` turn it on; `Zabbix__HostGroups` narrows the hosts further; `Zabbix__MinSeverity` (Average) is the default for new Zabbix sensors.
- **Who sees it:** groups with Zabbix *View* (*Viewers* too) or *Details* (also the inventory and acknowledgement messages, which name people). Attaching hosts needs Monitoring *Edit* as well.
- **Load:** one `problem.get` every `Zabbix__PollSeconds` (60) and the hosts every `Zabbix__HostSyncMinutes` (10), whatever the page views.
- **When Zabbix doesn't answer,** Zabbix sensors keep their state and write nothing; the system sensor *Zabbix* writes 430 once. If Zabbix also mails PNMT through a mail input, both alert: retire the mail rule once the host's Zabbix sensor is in.
- **Checking it:** *Admin → System health → Integrations → Test* reads the problems and hosts now and names the host groups it found.

## Uploaded files

Images and attachments of locations and knowledge base pages (docs/locations-knowledge-plan.md §5.5, §8.5) are files in a folder on this host, `files/` next to `docker-compose.yml`, mounted into the container at `/var/lib/pnmtd/files`. PostgreSQL holds only what each file is.

- **Moving it:** `PNMTD_FILES_DIR` in `.env`, for example `/srv/pnmtd-files` on a disk of its own. Start it with `./` or `/`: a bare name would be a Docker volume.
- **Ownership:** the app runs as the image's user `app`, uid 1654, and the container stays read-only; only the mount is writable. `./init.sh` creates the folder with owner `1654:1654` and mode `0750`, as root directly or else from a container of the image. Files are `0640` and folders `0750`: readable by root, or by a group you add. `./info.sh` checks that the folder exists, is mounted and the app can write it, and says how to fix it:
  - `sudo chown 1654:1654 files && sudo chmod 0750 files`;
  - rootless Podman maps the uid: `podman unshare chown 1654:1654 files`;
  - a folder that is missing at the first start is created by Docker, owned by root, and uploads fail until it is fixed.
- **An installation from before 1.3** has no folder yet. `git pull` brings the mount in `docker-compose.yml`, and `./update.sh` creates the folder for the app's user before it starts the new release. Updating by hand, create it first: `sudo install -d -o 1654 -g 1654 -m 0750 files`, then `docker compose up -d`.
- **SELinux** (Fedora, RHEL, Rocky): a container may not use a host folder until it is labelled. Label it for containers in `docker-compose.override.yml`, with `:z`:

  ```yaml
  services:
    pnmtd:
      volumes:
        - ./files:/var/lib/pnmtd/files:z
  ```

  Not `:Z`: a private label keeps out the containers `./backup.sh` and `./restore.sh` run against the same folder.
- **On the host, the files may be read, copied, measured and backed up** with any tool: that is why they are a folder. `./backup.sh --no-files` leaves them to that tool.
- **They must not be changed, renamed or deleted from the host.** The daily check notices: the *File storage* sensor on the host *PNMT* writes 428 for missing or changed files, and pages show *File removed*. Files nobody uploaded are listed under *Admin → Knowledge base → Files* and never deleted by PNMT. To delete a file (an image that shows what it shouldn't), use *Delete* there: it leaves the disk at once.
- **Space:** the folder's disk fills, not the database. *File storage* writes 428 below `KnowledgeBase__MinFreeMb` (1024) free, and *Admin → Knowledge base → Files* shows what takes the space. Unused uploads are deleted after a day, deleted files leave the disk 7 days later.

## What lives where

| Where | Holds | Backed up by |
|---|---|---|
| volume `pnmtd_pnmtd-pgdata` | the database, with the UDP nodes' keys, their command log and firmware images, and the MikroTik routers' configurations and (encrypted) tunnel keys | `./backup.sh` (`pg_dump`) |
| volume `pnmtd_pnmtd-keys` | Data Protection keys (sessions, 2FA secrets, PNMTD's SSH key and the tunnel keys) | `./backup.sh` |
| volume `pnmtd_pnmtd-downloads` | the agents' files PNMT serves, fetched from the GitHub release | not needed: fetched again |
| folder `deploy/files/` (`PNMTD_FILES_DIR`) | the images and attachments of locations and pages | `./backup.sh` (`files.tar`), or your own tools with `--no-files` |
| `deploy/.env` | database password, first admin password, settings | you. It is not in any backup |
| `deploy/backups/` | backup folders | you |

The data lives in named volumes rather than bind mounts, because the app writes as a non-root user and a host folder's ownership would not match. The uploaded files are the exception, so the host's tools can reach them; `./init.sh` gives their folder to the app's user. Never back the volumes up by copying files from under a running database; use `./backup.sh`.
