# Upgrading from v0.12 to 1.0

PNMT 1.0 replaces the two containers of v0.12 (the `pnmtd` backend with SQLite, and the `pnmtwebapp` Blazor UI with `users.json`) with one container and PostgreSQL. `upgrade-1.0.sh` moves your data across in one step.

**What stays the same:**

- **Sensor URLs:** `http(s)://<host>[:5001]/event/…` and `/read/…`, which hundreds of scripts and devices use, keep working unchanged. 1.0 answers them byte for byte as v0.12.1 did.
- **Your data:** hosts, sensors, events, notification rules, DNS zones, mail rules, the mail log and key values.
- **Passwords:** the WebApp's users keep their passwords. Admins stay admins and everyone else becomes a *User*.

**What changes:**

- **One address for the UI:** `/pnmt-admin/` on the backend's address. Port 5000 is gone.
- **A database container:** PostgreSQL 18, in a volume of its own. `backup.sh` backs it up.
- **Settings in `deploy/.env`** instead of `docker-compose.yml`. The WebApp's `externalApiurl` becomes `PublicBaseUrl`.
- **Two-factor authentication** is offered at the first sign-in. The default user `pnmt` with the password `pnmt` must choose a new password.
- **Removed:** the JWT settings (`Jwt__*`), `token.txt` and `create-web-app-token.txt`.
- **New in 1.1, and off until used:** MikroTik routers and a WireGuard gateway for routers behind NAT ([docs/release-notes-mikrotik.md](../docs/release-notes-mikrotik.md)). The upgrade changes nothing for them; the gateway starts only with `./init.sh --wireguard`.

**Downtime** runs from stopping the old containers until 1.0 answers: usually a minute or two plus the import. Sensors that report during that time get a connection error. Heartbeat checks wait five minutes after the start, so heartbeats that missed the window don't all fail at once.

## Before you start

1. **Note the sensors' address.** It is usually `<host>:5001`, or the backend's hostname behind your reverse proxy, and it must not change.
2. **Note the server's time zone.** v0.12 wrote local times, so the import needs the zone they were written in (`--source-time-zone Europe/Zurich`, for example). In doubt, `date +%Z` on the old host tells you.
3. **Find the old installation's folder.** It holds `docker-compose.yml`, `pnmtd-data/` (with `pnmtd.db`) and `pnmtwebapp-data/` (with `users.json`), for example `/opt/pnmt`. If the backend and the WebApp live in folders of their own, each with its `docker-compose.yml` and `data/`, name both instead of `--legacy`: `--legacy-backend /opt/pnmtd-old --legacy-webapp /opt/pnmt.webapp`.
   - The script finds the old containers by their compose services (`pnmtd`, `pnmtwebapp`), else by the data folder they mount. `--backend` and `--webapp` name them otherwise.
   - Settings in a `config.json` in the data folders aren't copied. The script lists the file; put the settings 1.0 still uses into `.env` by hand, `"Smtp": {"Host": …}` as `Smtp__Host=…`.
4. **Check the version.** The old backend must be v0.12.x: `docker compose ps` in the old folder shows the image tag.
5. **Leave the old containers running** until the script stops them.

## Upgrade

```sh
git clone https://github.com/muqiuq/PNMTD.git /srv/pnmtd
cd /srv/pnmtd/deploy

./init.sh --url https://pnmt.example.com          # the address sensors and browsers use
./upgrade-1.0.sh --legacy /opt/pnmt --source-time-zone Europe/Zurich --copy-settings
# or, with the backend and the WebApp in folders of their own:
./upgrade-1.0.sh --legacy-backend /opt/pnmtd-old --legacy-webapp /opt/pnmt.webapp \
  --source-time-zone Europe/Zurich --copy-settings
```

Run `upgrade-1.0.sh` **before** `docker compose up`. It takes these steps, in this order:

1. **Checks** the old containers, the free disk space, and that the new database is empty. Nothing has changed yet if a check fails.
2. **Copies the settings** (with `--copy-settings`) from the running old containers into `.env`:
   - `Smtp__*`, `Pushover__*`, `Mailbox__*`, `UplinkCheck__*`, `Identity`, `Proxy` and `TZ`;
   - the WebApp's `externalApiurl` as `PublicBaseUrl`.

   Settings already in `.env` are kept. A value containing a single quote is reported for you to copy by hand.
3. **Pulls** the 1.0 and PostgreSQL images while the old stack still runs.
4. **Stops** the old containers after asking you (skip the question with `--yes`). It doesn't remove them.
5. **Archives** `pnmtd-data/` and `pnmtwebapp-data/` to `backups/upgrade-1.0-<time>.tar.gz`.
6. **Imports** the SQLite database and `users.json` in one transaction. The import checks every table: row counts, tokens and time-zone conversion. It commits only if all checks pass. Its report lists the users and any duplicate sensor tokens it had to replace.
7. **Starts** 1.0 on the old port.

The report is also saved in `logs/upgrade-1.0-<time>.log`.

**If the import stops,** the script starts the old containers again. Nothing in the old folder has changed, and the report says what stopped the import. Fix the cause (usually the time zone, or a database that isn't v0.12) and run the script again.

## After the upgrade

1. **Sign in** at `https://<your host>/pnmt-admin/` with your old username and password, and set up two-factor authentication when the app asks. `./list-users.sh` shows who was imported.
2. **Reverse proxy:**
   - Keep the backend's hostname pointing at port 5001.
   - Redirect the old UI's hostname to `https://<backend host>/pnmt-admin/`.
   - Set `Proxy` in `.env` if a proxy sits in front: see [README.md](README.md#behind-a-reverse-proxy).
3. **Backups:** add the cron line from [README.md](README.md#everyday-operation) and run `./backup.sh` once.
4. **Check** with `./info.sh`. It warns about anything that needs attention, such as an admin without 2FA or a missing `PublicBaseUrl`.
5. **A new port, 5028/udp,** is published for UDP nodes ([README.md](README.md#udp-nodes)). With no UDP nodes defined it never answers anything. `Udp__Enabled=false` in `.env` turns the listener off, and `PNMTD_UDP_PORT` moves it.

## Rollback

The old installation is untouched, and its containers are only stopped:

```sh
cd /srv/pnmtd/deploy && docker compose down       # stop 1.0 (its data stays in its volumes)
cd /opt/pnmt && docker compose up -d              # start v0.12 again (in both folders, if two)
```

Sensors that reported to 1.0 in the meantime are not in the v0.12 database. To try again later, remove 1.0's volumes first (`docker compose down -v`), then rerun `upgrade-1.0.sh`.

## Clean up

Once 1.0 has run for a while:

- **Remove the old containers:** `docker compose down` in the old folder.
- **Keep the archive** in `backups/` as long as you like.
- **Delete leftovers:**
  - the old folder;
  - `token.txt` and `create-web-app-token.txt`;
  - every `Jwt__*` setting;
  - the WebApp's DNS name, once nothing links to it.
