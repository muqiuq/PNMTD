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
- `pnmtd-wg` is healthy once `wg0` is up and its SOCKS port answers; it waits for the app's migrations first.
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
- **What PNMT sent** (*Admin → System health → Outbound*): every e-mail, Pushover message and webhook, simulated ones too, with its headers, its body, the provider's answer and an e-mail's SMTP conversation; and every call to Redmine. Two logs, the newest `Outbound__KeepNotifications` (10,000) notifications and `Outbound__KeepRedmine` (1,000) Redmine calls, each body cut at `Outbound__MaxBodyKiB` (256). PNMT's own keys are masked (Redmine's keys, Pushover's app token, the SMTP password, a Pushover user key but for its last 4 characters); webhook URLs are kept as they are. Admins only (*System: Manage*). They are in the backups.
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
  - Set `Proxy=docker` in `.env` if the proxy runs in a Docker network, or else set it to the proxy's address. Without it, lockout, the audit log and the connectors' networks see the proxy's address instead of the client's.
  - `Proxy` takes a list too, comma-separated, of `docker`, addresses and networks (`Proxy=docker,10.0.0.5`). Each entry is checked at the start, and a bad one stops it with a message naming the entry.
  - **What a trusted range means:** `docker` trusts all of 172.16.0.0/12. A client that reaches PNMT's port directly from that range, not through the proxy, can claim any address in `X-Forwarded-For`, and with it pass a connector caller's networks. Behind a proxy on the same host, bind PNMT to `127.0.0.1` (below), or name the proxy's own address instead of `docker`.
  - With the proxy on the same host, set `PNMTD_BIND_ADDRESS=127.0.0.1`.
- **Sensor URLs:** they stay plain HTTP if they were before. The app never redirects them.
- **Restricting paths:** the app enforces sign-in itself. If you restrict paths at the proxy anyway, keep `/event/` and `/read/` open, and match them case-insensitively, because the app accepts `/EVENT/` too. The agents of servers (Windows and Linux) need `/agent/` as well.
- **Uploads:** pages and locations take images up to 2 MB and attachments up to 25 MB, and firmware images are uploaded too. A proxy needs a body limit above them: `client_max_body_size 32m;` (nginx, in the example below), `request_body { max_size 32MB }` (Caddy); Traefik has no limit by default. A proxy's own 413 is named as such in the UI. Admins can also upload an agent's file on *Admin → Servers → Versions* (up to 300 MB, for a PNMT without internet): only if you use that, raise the limit to `310m` for `/api/v1/agents/versions/`.
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
- **The network map** (*Monitoring → Network map*, [docs/network-map-plan.md](../docs/network-map-plan.md)): a scan logs in to each router as `pnmt`, one at a time, and reads its interfaces, MAC tables, neighbors, ARP, DHCP leases, tunnels and Wi-Fi clients; nothing on a router changes. The schedule is off until someone turns it on in the *Scans* tab (then every 14 days at 02:00 server time by default). `NetworkMap__Enabled=false` stops all scans; `NetworkMap__Scan__*` sets each table's time and size limits and how long a scan waits for a busy router (see `.env.example`). From the server: `docker compose exec -T pnmtd pnmtd network scan <router> --raw` prints what a router returns, `pnmtd network status` the running and queued scans and the schedule.
- `./info.sh` shows the gateway's state and the endpoint; `docker compose exec pnmtd pnmtd wireguard status` the tunnels, `pnmtd mikrotik list` the routers.

## Servers with an agent

Servers report through PNMT's agent, on PNMT's address and port, and are managed on *Admin → Servers*. **Windows** servers and PCs run the PNMT sensor service ([docs/windows-sensor-plan.md](../docs/windows-sensor-plan.md)); **Linux** servers (Debian 12 and 13, also Proxmox VE and Ubuntu LTS) run the PNMT agent for Linux ([docs/linux-sensor-plan.md](../docs/linux-sensor-plan.md)). The settings are `Agents__*` in `.env` (`deploy/.env.example`).

- **The agents' files come from PNMT itself** (`/agent/v1/download/…`), so a server needs to reach only PNMT: PNMTD fetches the files of the version the agents get from the GitHub release `sensor-{version}` when first asked for, checks each against its `.sha256`, and keeps them in the volume `pnmtd-downloads` (in `sensor-{version}/`). GitHub's links, with the same hashes, are offered next to PNMT's.
- **Which version the agents get** is chosen per platform on *Admin → Servers → Versions* (docs/agent-update-plan.md): PNMT's own version by default, or another one, here or in GitHub's list of releases; one newer than PNMT after a warning. The tab also shows each file's state, fetches a version, deletes one, and lets admins upload a file. **Choosing starts nothing:** a server gets the version when its admin runs the update there. PNMT has no way to start an update, apart from a script the server lets PNMT run (`pnmt-agent update` in a script that runs as root does update the agent).
- **What the download folder keeps:** the chosen versions, PNMT's own, versions with an uploaded file (until deleted), and the newest `Agents__KeepVersions` (2) others; older ones are deleted and can be fetched again. GitHub's list comes from `Agents__ReleasesApiUrl`, derived from `Agents__AgentDownloadBaseUrl` for GitHub; set it empty for a mirror, and fetch a version by its number instead.
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
- **Updating:** `pnmt-agent update` on the server, after PNMT's update: it downloads the version Linux agents get from PNMT and checks it against a manifest PNMTD signed with the key pinned at the join. It exits 0 when there is nothing newer, so a schedule of your own on the server (cron, a systemd timer) can run it; PNMT doesn't.
- **Uninstalling:** `pnmt-agent uninstall` (`config.yaml` and the logs stay; `--purge` deletes them too). PNMT keeps the server and its history, marked uninstalled.
- **Scripts need D-Bus** on the server (`systemd-run` starts them in a unit of their own): Debian's and Ubuntu's standard installations have it; a minimal one may need `apt install dbus`.

## SSL certificates

A sensor of the type *SSL certificate* reads the TLS certificate of `host:port` (docs/ssl-certificate-plan.md): it fails a set number of days before expiry and once expired, and, as each sensor says, when the certificate, its key or its issuer changes, until a person accepts the change. Certificates of public CAs, of private CAs added on *Admin → Certificates*, and self-signed ones (*pinned*) are checked alike.

- **Who:** the rights area *Certificates*. *View* sees a sensor's *Certificate* tab and the private CAs, *Edit* creates and changes certificate sensors (with Monitoring *Edit*), checks now and accepts a change, *Manage* adds and removes private CAs. **The update grants it to no group:** admins have it, and give *Edit* to whoever sets up certificate sensors.
- **What leaves PNMT:** a TCP connection to the sensor's endpoint and a TLS handshake, nothing else. PNMT never fetches a URL a certificate names (no AIA, OCSP or CRL downloads), so a server can't make it call an internal address.
- **The trust store** is the image's (Debian's `ca-certificates`): a new public root arrives with an image update. An internal CA needs no update: add its root on *Admin → Certificates*.
- **Old devices:** endpoints that speak only TLS 1.0 or 1.1, or present keys weaker than OpenSSL's security level, fail the handshake and turn *unreachable*; the message says so.
- **The settings,** all optional:

  | Setting | Default | |
  |---|---|---|
  | `Certificates__Enabled` | `true` | `false`: no checks and no evaluations; the sensors keep their states. |
  | `Certificates__WarnDays` | `14` | The warning's default for new sensors. |
  | `Certificates__TimeoutSeconds` | `15` | The connection and the handshake together. |
  | `Certificates__RetryMinutes` | `15` | The next check after a failed one. |
  | `Certificates__FailuresBeforeUnreachable` | `2` | Failed checks in a row before *unreachable* (489). |
  | `Certificates__FailingCheckMinutes` | `60` | The check's rhythm while a certificate's verdict fails. |
  | `Certificates__MaxConcurrent` | `8` | Checks at once. |
  | `Certificates__KeepVersions` | `50` | Certificates kept per sensor (the current and the accepted one always). |

## Advanced sensors

A sensor of the type *Advanced* takes a JSON object that an app POSTs to `/report` with the sensor's write token (docs/advanced-sensor-plan.md, docs/advanced-report.md). PNMT stores it, and one evaluator judges every report by the sensor's rules within a second; an event is written when the verdict changes.

- **The path:** `/report` is on PNMT's address and port, next to `/event`. A reverse proxy that passes only `/event` and `/read` to PNMT needs `/report` too (POST, bodies up to `Advanced__MaxReportBytes`; gzip is accepted). Like `/event`, it needs no sign-in and sets no cookie.
- **The token** travels in `Authorization: Bearer`, so it stays out of access logs; `/report/<token>` exists for tools that can't set a header, as on `/event`.
- **Who:** Monitoring's rights. *View* sees the reports, *Details* also those of sensors marked *names people*, *Edit* changes rules and fields and deletes reports (audited).
- **The reports are in the backups** (`pg_dump`). A report that carried a secret can be deleted on the sensor's *Data* tab, one or all.
- **The settings,** all optional:

  | Setting | Default | |
  |---|---|---|
  | `Advanced__MaxReportBytes` | `262144` | The largest report, decompressed; a larger one is refused (413) and fails the sensor (496). |
  | `Advanced__MinSecondsBetweenReports` | `5` | One report per sensor in this many seconds; a sooner one gets 429. `0`: no limit. |
  | `Advanced__KeepReports` | `500` | Reports kept per sensor (identical ones count once). |
  | `Advanced__KeepDays` | `30` | Reports older than this go, hourly. |
  | `Advanced__MaxBytesPerSensor` | `52428800` | At most this many bytes of reports per sensor. The newest two reports always stay. |
  | `Advanced__SampleDays` | `30` | Days of charted values. |
  | `Advanced__EvaluateSeconds` | `30` | The evaluator's timer: missing reports and age rules. Reports wake it at once. |

## 3rd Party Connectors

A connector lets an outside system (a ticketing system, a shop's back office, a scheduler) run one script, or one function in it, on one server with an agent, through PNMT and nothing else (docs/connectors-plan.md; for the callers' developers, docs/connectors-api.md, also on the area's *Documentation* tab).

- **The path:** `/connector/v1/<slug>` is on PNMT's address and port, next to `/event`. A reverse proxy that passes only some paths needs `/connector/` too (POST and GET, bodies up to `Connectors__MaxBodyBytes`), and a read timeout above `Connectors__MaxWaitSeconds` (110 s): a caller in wait mode waits that long for the outcome. It needs no sign-in and sets no cookie.
- **Callers:** each outside system is a *caller*, with one key (`pnmt_cx_…`, shown again on its page, each display in the audit log), the networks it calls from (at least one; anywhere is `0.0.0.0/0` and `::/0`), and the connectors granted to it.
- **A caller's networks are only as good as `Proxy`.** Behind a reverse proxy, without `Proxy` every call seems to come from the proxy; with `Proxy=docker`, anything that reaches PNMT's port from 172.16.0.0/12 directly can claim an address (see *Behind a reverse proxy*). The caller's page shows the address PNMT sees for your browser.
- **The server decides last:** a connector can't run what the agent's window (Windows) or `/etc/pnmt-agent/config.yaml` (Linux) refuses. A function in a script needs an agent of 1.15 or later.
- **Who:** the rights area *3rd Party Connectors*, which the update gives no group: *View* sees every call and its output, *Manage* makes connectors and callers and shows their keys. *Manage* is as strong as running scripts on servers, for the callers it lets in.
- **The review:** a connector can have each call checked by the typesafe model (Jev, `Ai__*`), and decides itself whether a call is refused or runs unreviewed when the review can't run.
- **What is kept:** the newest `Connectors__KeepCalls` calls of all connectors, with their arguments (never a secret one), their review, and the runs they started with their output; they are in the backups (`pg_dump`). The keys: their hashes are in the database, their copies for display under Data Protection in the `pnmtd-keys` volume. A restore without that volume leaves the keys working, but they can't be shown: rotate them then.
- **The system sensor** *3rd Party Connectors* (created with the first connector) fails with 433 when a valid key calls from outside its networks, the review refuses a call, or an address is blocked for wrong keys; it is OK again (233) after `Connectors__QuietMinutes`.
- **The settings,** all optional:

  | Setting | Default | |
  |---|---|---|
  | `Connectors__Enabled` | `true` | `false`: `/connector` answers 404, and nothing is queued. |
  | `Connectors__MaxBodyBytes` | `65536` | The largest call. |
  | `Connectors__MaxValueChars` | `4000` | The longest value of an argument. |
  | `Connectors__MaxWaitSeconds` | `110` | The longest a connector may let a caller wait; keep it under the proxy's read timeout. |
  | `Connectors__PatternTimeoutMs` | `100` | For patterns that need backtracking. |
  | `Connectors__ReviewTimeoutSeconds` | `15` | The review's answer within this, or it couldn't run. |
  | `Connectors__WorkerParallelism` | `4` | Reviews at once. |
  | `Connectors__MaxRunningCalls` | `10` | Connector calls running at once over all connectors, 1 to 100; more wait in the queue, oldest first. Runs started in PNMT don't count. |
  | `Connectors__FailedKeysPerAddress` | `20` | Wrong keys from one address within 10 minutes before it is blocked. |
  | `Connectors__BlockMinutes` | `15` | How long. |
  | `Connectors__IdempotencyHours` | `24` | How long an `Idempotency-Key` answers its first call. |
  | `Connectors__QuietMinutes` | `60` | 433 → 233 after this long without another refusal of those kinds. |
  | `Connectors__KeepCalls` | `100000` | The newest calls kept, with their runs; active calls always. At 100 000 calls with small outputs a few hundred MB; large outputs (up to 1 MiB each) are what grows. |

## AI (optional)

PNMT can use AI models through [OpenRouter](https://openrouter.ai): to propose rules for mails no rule handles and to rate RouterOS releases (docs/ai-operations-plan.md). Cookie, the assistant, uses the same key (see *Cookie* below). It is off until `.env` has `Ai__Enabled=true` and `Ai__ApiKey`.

- **The models:** a main model and a verification model from another vendor (by default `deepseek/deepseek-v4.1-flash-1017` and `z-ai/glm-5.3-flashx`), and TypeSafe's Jev (`typesafe/jev-1.14`), which screens outside text for prompt injection before any other model sees it. All three come through OpenRouter, with one key.
- **What is sent:** for mail analysis, the mail and the names of the sensors a rule could feed; for Cookie, the case book's texts (tickets, mails, notes, pages not marked sensitive) with people, e-mail addresses and phone numbers replaced by placeholders and host names kept (docs/cookie-plan.md §13.2); for the access watch, each login's time, way, device family, whether the account is an admin, the host, and the account and source address as placeholders (docs/cookie-access-plan.md §9). *Admin → AI* keeps every request as it went out. OpenRouter is asked to use only providers that neither store nor train on prompts (`data_collection: deny`).
- **What it costs:** every request is in *Admin → AI*, with its tokens and cost. `Ai__Budget__Usd` (30 by default) limits each month; at 80 % and 90 % the *AI budget* sensor warns, and at 100 % AI work waits for the next month.
- **When it fails:** a request is retried every minute 5 times, then after 1 hour, 3 hours and every 24 hours. The *AI* sensor turns red (421) while requests keep failing; a notification rule on it tells you.
- **The situation analysis is gone since 1.5:** Cookie's status replaces it (docs/cookie-plan.md §12).
- **The token limits:** each kind of request has the most tokens its answer may have, `Ai__Tokens__*` in `.env` (deploy/.env.example lists them with their defaults). On a reasoning model the reasoning counts too; a request that runs out ends as *invalid output* with "no message content" in *Admin → AI*.
- **Turning it off:** `Ai__Enabled=false`, or the mail analysis alone with `Ai__Mail__Enabled=false`. *Admin → Debug → Test the AI* checks the key and every model.

## Redmine (optional)

PNMT shows the tickets of chosen Redmine projects (5.1 or later; docs/audit-redmine-zabbix-plan.md §6): an overview of the most important ones, a search, a page per ticket, and links from tickets to hosts, sensors, locations and knowledge base pages, with their panels there. It changes nothing in Redmine.

- **An account of its own:** a Redmine user *pnmt* with a role that may only *View issues* (and *View private notes* only if `Redmine__ShowPrivateNotes=true`), member of the projects PNMT shows. *Administration → Settings → API* enables the REST API; the user's API key (*My account → API access key*) goes into `Redmine__ApiKey`. It is sent only as a header, never in a URL, and never shown.
- **The settings:** `Redmine__Url`, `Redmine__ApiKey` and `Redmine__Projects` (ids or identifiers) turn it on; `Redmine__PublicUrl` when browsers reach Redmine at another address; `Redmine__TextFormatting` as in Redmine's settings, so descriptions render right.
- **Who sees it:** groups with Redmine *View* (tickets) or *Link* (linking them). *Viewers* get neither: tickets hold customers' and colleagues' text.
- **A copy of the linked tickets** (number, subject, status, assignee) is refreshed every `Redmine__SyncMinutes`, so panels work when Redmine is slow or down. When it doesn't answer, or refuses the key, twice in a row, the system sensor *Redmine* writes 429.
- **One queue, and pages that answer at once** (docs/redmine-queue-outbound-plan.md): every read and write goes through one background task, at most `Redmine__Parallelism` (4) calls at once, a button a person pressed first, then Cookie's writes, then pages someone waits for, then the rest. What it read is kept: a Redmine page opens at once from it, says *Refreshed 2 min ago*, and is read again behind the page; ⟳ reads it now. A page someone has open stays at most `Redmine__RefreshSeconds` (60) old; the overview of all projects is read every `Redmine__SyncMinutes` anyway, other pages again when someone opens them. Nothing of it survives a restart: the overview is read first after a start.
- **Checking it:** *Admin → System health → Integrations → Test* signs in with the key and looks for every project. *System health → Outbound → Redmine* shows every call to Redmine: what for, who asked, how long it waited in the queue and took, the headers (the key masked) and the answer.
- **Without the menu entry:** `Redmine__HideMenu=true` leaves *Redmine* out of the drawer. The tickets panels, chips and ticket pages keep working.

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
- **Without the menu entry:** `Zabbix__HideMenu=true` leaves *Zabbix* out of the drawer. Zabbix sensors keep working, and the Zabbix pages stay reachable through their links.

## Cookie (optional)

Cookie is PNMT's assistant (docs/cookie-plan.md). PNMT keeps a case book, keeps the ticket deadlines and sends reminders itself; Cookie explains the status, writes personal summaries, answers questions, and **proposes actions** (packages nine and nine point five). **Everything Cookie wants to send or write goes through code's checks and Seraph, the reviewer, and starts at *Propose*:** a person approves it in the decisions inbox until an admin sets an action to *Auto*.

- **Turning it on:** `Cookie__Enabled=true`. From then on the case book runs: a case per host with bad sensors or Zabbix problems, and a case per ticket of the projects watched on *Admin → Cookie → Redmine*. **Cookie itself stays stopped** until someone with *Cookie: Manage* starts it on *Admin → Cookie*; after a restart it resumes if it was running.
- **Cookie's rights:** it reads with a group chosen on *Admin → Cookie → Rights*, never with the admin flag. Without a group it pauses and says so.
- **The duties** (reminders and escalations on tickets, the ticket after 2 days without progress, its closing note) are sent by code from template texts, whether Cookie runs or not. **Each is off until switched on** on *Admin → Cookie → Actions*. **Tickets and notes need `Redmine__WriteApiKey`**, the key of a second Redmine account that may create issues and add notes in the watched projects; without it, those duties say they aren't possible. Messages go by e-mail (the SMTP settings) and Pushover (`Pushover__ApiKey`), to the addresses and keys people set on *Account* (admins on *Admin → Users*), routed by the availability plan (*Admin → Availability plan*): the assignee by their Redmine user, the project's escalation group, and whoever is responsible that day. Outside working hours, Cookie reaches nobody.
- **The limits:** every message, note and ticket goes through the outbox, which counts the limits of *Admin → Cookie → Actions* (up to ceilings in code). What is over a limit is held back, and an admin can send it from *Admin → Cookie → Overview*.
- **Privacy:** with `Cookie__Privacy=Code` (the default), people's names, e-mail addresses and phone numbers become placeholders before a request leaves PNMT, and come back when the answer is shown. Host names and IP addresses stay. Pages marked sensitive never reach Cookie. Requests go only to zero-data-retention endpoints (`Cookie__Zdr`) with fp8 or better (`Cookie__MinQuantization`).
- **What it costs:** Cookie has a budget of its own, `Cookie__BudgetUsd` (30 USD a month), apart from `Ai__Budget__Usd`. A round in which nothing changed makes no call. Every request is in *Admin → AI*'s request log under `cookie.*`.
- **The token limits:** every call of Cookie's model, Seraph's and the big brother's has the most tokens its answer may have, `Cookie__Tokens__*` in `.env` (deploy/.env.example lists them with their defaults). The reasoning counts too.
- **Who sees what:** everyone sees the status and their own summary on the dashboard; *Cookie: Use* opens Cookie's window (cases, trace, decisions, chat), within the person's other rights; *Cookie: Instruct* stops a case, mutes and suppresses, and asks the big brother; *Cookie: Manage* is *Admin → Cookie*. The full trace, with reasoning and tool results, is for *Manage* and for whoever may see everything Cookie's group sees.
- **Cookie's actions** (*Admin → Cookie → Actions*): Pushover, e-mail, a ticket, a private note or a public answer on a ticket, a note on a host or sensor, and the event of an *AI every time* mail rule. Each has a level (*Off*, *Propose*, *Auto*) and a confidence threshold. **Seraph** (`Cookie__SeraphModel`) reviews everything that leaves PNMT or reaches people; without its answer, a person decides. Every proposal and what came of it is in Cookie's decision log, which people rate *right* or *wrong*; once a day the ratings become lessons (*Admin → Cookie → Lessons*).
- **Mail rules set to *AI every time*** are decided by Cookie while `Cookie__Enabled=true`: the mails wait for Cookie's next round, and while Cookie is stopped they wait, visible in the mail log.
- **The big brother** (`Cookie__BigBrother__*`) is off until switched on on *Admin → Cookie → Actions*: two strong models that propose a fix for a problem that matters and that nobody has taken care of for a working day, with a budget of their own (`Cookie__BigBrother__BudgetUsd`). It advises only.

## MCP (optional)

PNMT's MCP server (docs/mcp-plan.md) lets each person's own AI client (Claude Code, VS Code, Cursor) read and work in PNMT **with that person's rights, capped:** never the admin flag, never a *Manage* level, never approvals or PNMT's settings.

- **Turning it on:** `Mcp__Enabled=true`. The server answers at `/mcp`; **offer it on the internal name only**, never on the public one. Behind nginx, give `/mcp` a location of its own with `proxy_buffering off` (answers may stream) and `proxy_read_timeout 120s`.
- **The tokens:** every user has two, on *Account → Security*, under *MCP access*. **Read-only** stays on until 7 days after its user was last online in PNMT (`Mcp__ReadDaysAfterOnline`); **read & write** for 12 hours (`Mcp__WriteHours`) and needs 2FA on the account. A token's value is shown once, when it is made; PNMT keeps only its hash. Changing the password, 2FA or *Sign out everywhere* turns both off.
- **Deleting** works only in a window its user opens on Account → Security (*Allow deleting for 10 minutes*), at most 20 objects (`Mcp__DeleteMinutes`, `Mcp__DeletesPerWindow`).
- **Talking to Cookie** (`cookie_ask`) needs Jev (`Ai__Guard__*`): each message is screened first, and a read-only token can only ask questions.
- **Strikes:** 3 refused requests within 24 hours (`Mcp__StrikesToBlock`) block the token and alert every active admin by Pushover and e-mail, to the key and address on their user.
- **The log:** *Admin → MCP → Log* (the right *MCP: View*) keeps the newest 10,000 requests (`Mcp__LogEntries`) with their arguments and results; what changed is in the audit log as *via MCP*. *Admin → MCP → Documentation* lists every tool, for everyone.
- **Signing in:** independently of MCP, PNMT now asks for the password at least every 30 days and for the 2FA code once a day, at 04:00 (`Auth__LoginDays`, `Auth__TotpDays`, `Auth__ResetHour`; 0 turns a rule off). The day's code is asked for over the current page.

## Menu entries

A deployment that doesn't use a part of PNMT can leave its entries out of the drawer, for everybody:

| Setting | Leaves out |
|---|---|
| `Mail__HideMenu=true` | *Mail inputs* and *Mail log* |
| `Redmine__HideMenu=true` | *Integrations → Redmine* |
| `Zabbix__HideMenu=true` | *Integrations → Zabbix* |

Only the entries go: the features keep working, their pages stay reachable through links (a ticket chip, a Zabbix sensor's page), and rights decide who may open them, as before.

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
| volume `pnmtd_pnmtd-pgdata` | the database, with the UDP nodes' keys, their command log and firmware images, the MikroTik routers' configurations and (encrypted) tunnel keys, the advanced sensors' reports, and the connectors' calls with their runs' output | `./backup.sh` (`pg_dump`) |
| volume `pnmtd_pnmtd-keys` | Data Protection keys (sessions, 2FA secrets, PNMTD's SSH key, the tunnel keys, and the connector callers' keys for display) | `./backup.sh` |
| volume `pnmtd_pnmtd-downloads` | the agents' files PNMT serves, fetched from the GitHub release | not needed: fetched again |
| folder `deploy/files/` (`PNMTD_FILES_DIR`) | the images and attachments of locations and pages | `./backup.sh` (`files.tar`), or your own tools with `--no-files` |
| `deploy/.env` | database password, first admin password, settings | you. It is not in any backup |
| `deploy/backups/` | backup folders | you |

The data lives in named volumes rather than bind mounts, because the app writes as a non-root user and a host folder's ownership would not match. The uploaded files are the exception, so the host's tools can reach them; `./init.sh` gives their folder to the app's user. Never back the volumes up by copying files from under a running database; use `./backup.sh`.
