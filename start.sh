#!/usr/bin/env bash
# Build and run PNMTD in containers (Podman by default) for debugging on a laptop: PostgreSQL, the
# migrations and the app, on http://localhost:7327/. Production runs from deploy/ with Docker; this
# is not how it is deployed (docs/webui-migration-plan.md §6.2).
#
#   ./start.sh                     build and (re)start: http://localhost:7327/pnmt-admin/
#   WEB=dev ./start.sh             also Vite with HMR: http://localhost:5173/pnmt-admin/, edits of
#                                  PNMTD.WebUI are live (/api, /event and /read go to the app)
#   SEED=1 ./start.sh              on an empty database: demo data (50 hosts, 300 sensors, 90 days)
#   LEGACY_DIR=~/pnmt/pnmtd-data ./start.sh
#                                  on an empty database: import that v0.12 data folder (its pnmtd.db),
#                                  as the real upgrade does; the folder is mounted read-only
#   IMPORT=1 LEGACY_DIR=… ./start.sh
#                                  import again, replacing the database's contents
#   LEGACY_USERS=~/pnmt/webapp-data/users.json
#                                  with LEGACY_DIR: the WebApp's users too, with their passwords
#   SOURCE_TZ=Europe/Zurich        the zone the v0.12 data was written in (default UTC)
#   PORT=7400 WEB_PORT=5174        other host ports
#   UDP_PORT=5029                  another host port for UDP nodes (default 5028/udp)
#   WIREGUARD=1 ./start.sh         also the WireGuard gateway (pnmtd-wg: root, NET_ADMIN only), and the
#                                  WireGuard modes for MikroTik routers (docs/mikrotik-plan.md §3).
#                                  WG_PORT=51820 is its host port (UDP); WG_EXTERNAL_HOST names what
#                                  routers dial (default: this machine's name)
#   AGENT_FILES=dist ./start.sh    serve the agents built into dist/ (./build-sensor-linux.sh, ./build-sensor-windows.sh)
#                                  at /agent/v1/download/, mounted read-only; otherwise PNMTD fetches them from the
#                                  GitHub release of its version into the volume pnmtd-downloads-dev, as deployed
#   VERBOSE=1 ./start.sh           show the build log, which is otherwise only printed on failure
#   ENGINE=docker ./start.sh       Docker instead of Podman
#
# MikroTik updates take a router admin's password over plain HTTP here (localhost); deploy/ refuses that.
# Settings come from dev.env (gitignored, 0600, deploy/.env format: Smtp__Host=…, Identity=…, TZ=…),
# or the file ENV_FILE names (tools/e2e.sh).
# The first run creates it with a generated database password and a password for the first admin
# (admin, Auth__InitialAdmin__Password), which the server makes you change at the first sign-in.
# ./stop.sh stops everything again.
set -euo pipefail
cd "$(dirname "$0")"

ENGINE="${ENGINE:-podman}"
NAME="${NAME:-pnmtd}"
IMAGE="${IMAGE:-pnmtd:dev}"
PORT="${PORT:-7327}"
UDP_PORT="${UDP_PORT:-5028}"
DB_NAME="${DB_NAME:-pnmtd-db}"
NETWORK="${NETWORK:-pnmtd-dev}"
PG_VOLUME="${PG_VOLUME:-pnmtd-pgdata}"
KEYS_VOLUME="${KEYS_VOLUME:-pnmtd-keys-dev}"
DOWNLOADS_VOLUME="${DOWNLOADS_VOLUME:-pnmtd-downloads-dev}"
AGENT_FILES="${AGENT_FILES:-}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-docker.io/library/postgres:18-alpine}"
LEGACY_DIR="${LEGACY_DIR:-}"
LEGACY_USERS="${LEGACY_USERS:-}"
IMPORT="${IMPORT:-0}"
SEED="${SEED:-0}"
SOURCE_TZ="${SOURCE_TZ:-UTC}"
VERBOSE="${VERBOSE:-0}"
ENV_FILE="${ENV_FILE:-dev.env}"
WEB="${WEB:-}"
WEB_NAME="${WEB_NAME:-${NAME}-web}"
WIREGUARD="${WIREGUARD:-0}"
WG_NAME="${WG_NAME:-${NAME}-wg}"
WG_PORT="${WG_PORT:-51820}"
WG_EXTERNAL_HOST="${WG_EXTERNAL_HOST:-$(hostname)}"
WEB_PORT="${WEB_PORT:-5173}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pnmtd-web-node-modules}"
NPM_CACHE="${NPM_CACHE:-pnmtd-npm-cache}"
NODE_IMAGE="${NODE_IMAGE:-docker.io/library/node:24-slim}"

die() { echo "$@" >&2; exit 1; }

case "$WEB" in
  ""|dev) ;;
  *) die "WEB=${WEB}: the only value is dev (Vite with HMR beside the app)." ;;
esac

# A setting of dev.env: its last line, without the quotes .env allows around a value.
setting() {
  sed -n "s/^$1=//p" "$ENV_FILE" | tail -1 | sed -E "s/^'(.*)'\$/\\1/; s/^\"(.*)\"\$/\\1/"
}

# A line appended to dev.env, which is edited by hand and may lack its final newline.
add_setting() {
  if [[ -s "$ENV_FILE" && -n "$(tail -c1 "$ENV_FILE")" ]]; then echo >> "$ENV_FILE"; fi
  printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
}

# 32 hex digits; a fixed read, since tr on an endless stream ends in SIGPIPE.
random_secret() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }

if [[ ! -f "$ENV_FILE" ]]; then
  (umask 077 && printf '%s\n' \
    '# Settings for ./start.sh, in deploy/.env format (ASP.NET names: Smtp__Host=…, Identity=…, TZ=…).' \
    '# Only what differs from the defaults needs a line.' > "$ENV_FILE")
  echo "$ENV_FILE created."
fi
if [[ -z "$(setting PNMTD_DB_PASSWORD)" ]]; then
  add_setting PNMTD_DB_PASSWORD "$(random_secret)"
fi
db_password="$(setting PNMTD_DB_PASSWORD)"
# Read by the server while the database has no users (as deploy/.env's).
if [[ -z "$(setting Auth__InitialAdmin__Password)" ]]; then
  add_setting Auth__InitialAdmin__Password "$(random_secret)"
fi

if [[ -n "$LEGACY_DIR" ]]; then
  LEGACY_DIR="$(cd "$LEGACY_DIR" && pwd)" || die "LEGACY_DIR: no such folder."
  [[ -f "$LEGACY_DIR/pnmtd.db" ]] || die "LEGACY_DIR has no pnmtd.db: point it at a v0.12 pnmtd-data folder."
elif [[ "$IMPORT" != "0" ]]; then
  die "IMPORT=1 needs LEGACY_DIR, the v0.12 data folder to import."
fi
if [[ -n "$LEGACY_USERS" ]]; then
  [[ -n "$LEGACY_DIR" ]] || die "LEGACY_USERS goes with LEGACY_DIR: the users are imported with the data."
  [[ -f "$LEGACY_USERS" ]] || die "LEGACY_USERS: no such file."
  LEGACY_USERS="$(cd "$(dirname "$LEGACY_USERS")" && pwd)/$(basename "$LEGACY_USERS")"
fi

# Something else publishing a port would stop the app at the very end: said here, before the build.
ports=("$PORT" "$UDP_PORT")
[[ "$WEB" == dev ]] && ports+=("$WEB_PORT")
for port in "${ports[@]}"; do
  holder=$("$ENGINE" ps --format '{{.Names}} {{.Ports}}' \
    | awk -v port=":${port}->" -v app="$NAME" -v web="$WEB_NAME" '$1 != app && $1 != web && index($0, port) { print $1; exit }')
  [[ -z "$holder" ]] || die "Port ${port} is taken by the container ${holder}: stop it, or choose others (PORT=… WEB_PORT=… UDP_PORT=… ./start.sh)."
done

# Under the repository: Podman on macOS cannot mount /tmp or /var/folders.
mkdir -p .tmpcheck
scratch=$(mktemp -d "$PWD/.tmpcheck/start.XXXXXX")
trap 'rm -rf "$scratch"' EXIT

echo "Building ${IMAGE}…"
if [[ "$VERBOSE" == "1" ]]; then
  "$ENGINE" build -t "$IMAGE" -f Dockerfile .
elif ! "$ENGINE" build -t "$IMAGE" -f Dockerfile . >"$scratch/build.log" 2>&1; then
  cat "$scratch/build.log" >&2
  die "Build failed."
fi

"$ENGINE" network create "$NETWORK" >/dev/null 2>&1 || true

if [[ -z "$("$ENGINE" ps -q --filter "name=^${DB_NAME}$")" ]]; then
  "$ENGINE" rm -f "$DB_NAME" >/dev/null 2>&1 || true
  # The password by name only: its value stays out of the process list.
  POSTGRES_PASSWORD="$db_password" "$ENGINE" run -d --name "$DB_NAME" --network "$NETWORK" \
    -e POSTGRES_USER=pnmtd -e POSTGRES_DB=pnmtd -e POSTGRES_PASSWORD \
    -v "${PG_VOLUME}:/var/lib/postgresql" \
    "$POSTGRES_IMAGE" >/dev/null
fi
# Over TCP: the image's first-boot server listens on a socket only.
ready=0
for _ in $(seq 1 120); do
  if "$ENGINE" exec "$DB_NAME" pg_isready -q -h 127.0.0.1 -U pnmtd 2>/dev/null; then ready=1; break; fi
  sleep 0.5
done
if (( ! ready )); then
  "$ENGINE" logs --tail 30 "$DB_NAME" >&2 || true
  die "PostgreSQL in ${DB_NAME} did not become ready."
fi

# pnmtd, run once in the image beside the database.
pnmtd() {
  "$ENGINE" run --rm --network "$NETWORK" --env-file "$ENV_FILE" \
    -e "Database__Host=${DB_NAME}" -e "Database__Password=${db_password}" "$@"
}

echo "Applying migrations…"
pnmtd "$IMAGE" db migrate >"$scratch/migrate.log" 2>&1 || { cat "$scratch/migrate.log" >&2; die "Migrations failed."; }

hosts=$("$ENGINE" exec "$DB_NAME" psql -U pnmtd -d pnmtd -tAc 'SELECT count(*) FROM hosts' 2>/dev/null || echo 0)
imported=0
if [[ -n "$LEGACY_DIR" ]] && [[ "$hosts" == "0" || "$IMPORT" != "0" ]]; then
  echo "Importing ${LEGACY_DIR}/pnmtd.db (read-only; the importer works on a copy)…"
  reset=()
  [[ "$IMPORT" != "0" ]] && reset=(--reset)
  users=()
  if [[ -n "$LEGACY_USERS" ]]; then
    users=(-v "${LEGACY_USERS}:/legacy-users/users.json:ro")
  fi
  if ! pnmtd -v "${LEGACY_DIR}:/legacy:ro" ${users[@]+"${users[@]}"} "$IMAGE" \
      import-sqlite --from /legacy/pnmtd.db ${LEGACY_USERS:+--users /legacy-users/users.json} \
      --source-time-zone "$SOURCE_TZ" ${reset[@]+"${reset[@]}"} >"$scratch/import.log" 2>&1; then
    grep -v '^info: \|^ *Executed DbCommand' "$scratch/import.log" >&2 || true
    die "The import stopped; the report above says why. ${LEGACY_DIR} is untouched."
  fi
  imported=1
fi

seeded=0
if [[ "$SEED" != "0" && "$imported" == "0" ]]; then
  if [[ "$hosts" == "0" ]]; then
    echo "Seeding demo data…"
    pnmtd -e ASPNETCORE_ENVIRONMENT=Development "$IMAGE" dev seed >"$scratch/seed.log" 2>&1 \
      || { cat "$scratch/seed.log" >&2; die "The seed failed."; }
    seeded=1
  else
    echo "SEED=1: the database has hosts already, nothing seeded (./stop.sh --data empties it)."
  fi
fi

wireguard=()
if [[ "$WIREGUARD" != "0" ]]; then
  wireguard=(-e WireGuard__Enabled=true -e "WireGuard__Gateway=${WG_NAME}:1080" -e "WireGuard__PublishedPort=${WG_PORT}"
    -e "WireGuard__ExternalHost=${WG_EXTERNAL_HOST}")
fi
"$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
# The agents' files (docs/linux-sensor-plan.md §4.4): a volume, as deployed, or dist/ read-only.
downloads=(-v "${DOWNLOADS_VOLUME}:/var/lib/pnmtd/downloads")
if [[ -n "$AGENT_FILES" ]]; then
  [[ -d "$AGENT_FILES" ]] || die "AGENT_FILES=${AGENT_FILES} is not a folder."
  downloads=(-v "$(cd "$AGENT_FILES" && pwd):/var/lib/pnmtd/downloads:ro")
fi
# As hardened as in deploy/docker-compose.yml, so what fails there fails here first.
"$ENGINE" run -d --name "$NAME" --hostname "$NAME" --network "$NETWORK" \
  -p "${PORT}:8080" \
  -p "${UDP_PORT}:5028/udp" \
  --env-file "$ENV_FILE" \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "Database__Host=${DB_NAME}" -e "Database__Password=${db_password}" \
  -e Mikrotik__AllowPasswordsOverHttp=true \
  ${wireguard[@]+"${wireguard[@]}"} \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  --sysctl "net.ipv4.ping_group_range=0 2147483647" \
  -v "${KEYS_VOLUME}:/var/lib/pnmtd/keys" \
  "${downloads[@]}" \
  "$IMAGE" >/dev/null

for _ in $(seq 1 120); do
  if curl -fsS -o /dev/null "http://localhost:${PORT}/healthz" 2>/dev/null; then break; fi
  if [[ -z "$("$ENGINE" ps -q --filter "name=^${NAME}$")" ]]; then
    "$ENGINE" logs --tail 40 "$NAME" >&2 || true
    die "Container ${NAME} exited during startup."
  fi
  sleep 0.25
done
curl -fsS -o /dev/null "http://localhost:${PORT}/healthz" || { "$ENGINE" logs --tail 40 "$NAME" >&2; die "${NAME} is up but /healthz did not answer."; }

# The WireGuard gateway (WIREGUARD=1), as in deploy/docker-compose.yml: the same image as root with
# NET_ADMIN only, the keys volume read-only. It waits for the app's migrations by itself.
"$ENGINE" rm -f "$WG_NAME" >/dev/null 2>&1 || true
if [[ "$WIREGUARD" != "0" ]]; then
  echo "Starting ${WG_NAME} (the WireGuard gateway)…"
  "$ENGINE" run -d --name "$WG_NAME" --hostname "$WG_NAME" --network "$NETWORK" \
    -p "${WG_PORT}:51820/udp" \
    -e "Database__Host=${DB_NAME}" -e "Database__Password=${db_password}" -e WireGuard__Enabled=true \
    --user 0:0 --read-only --tmpfs /tmp --cap-drop ALL --cap-add NET_ADMIN --cap-add DAC_READ_SEARCH --security-opt no-new-privileges \
    --sysctl net.ipv4.ip_forward=1 \
    -v "${KEYS_VOLUME}:/var/lib/pnmtd/keys:ro" \
    "$IMAGE" wireguard gateway >/dev/null
  for _ in $(seq 1 60); do
    if "$ENGINE" logs "$WG_NAME" 2>&1 | grep -q "Synced "; then break; fi
    if [[ -z "$("$ENGINE" ps -q --filter "name=^${WG_NAME}$")" ]]; then
      "$ENGINE" logs --tail 30 "$WG_NAME" >&2 || true
      die "${WG_NAME} exited during startup: the WireGuard kernel module, NET_ADMIN?"
    fi
    sleep 0.5
  done
fi

# Vite with HMR (WEB=dev), its node_modules in a volume of their own (Linux binaries, not the
# Mac's). npm ci runs whenever package-lock.json changed since the volume was filled. Vite proxies
# the API to the app by its name on the network; tools/vite-dev.config.mjs adds a polling watcher,
# since the VM passes no file events through the mount.
start_web() {
  echo "Starting ${WEB_NAME} (Vite)…"
  "$ENGINE" rm -f "$WEB_NAME" >/dev/null 2>&1 || true
  # shellcheck disable=SC2016 # expanded in the container
  "$ENGINE" run -d --name "$WEB_NAME" --network "$NETWORK" -p "${WEB_PORT}:5173" \
    -e NPM_CONFIG_UPDATE_NOTIFIER=false -e NPM_CONFIG_FUND=false -e NPM_CONFIG_AUDIT=false \
    -e "PNMTD_API_URL=http://${NAME}:8080" \
    -v "$PWD/PNMTD.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -v "${NPM_CACHE}:/root/.npm" \
    -v "$PWD/tools/vite-dev.config.mjs:/pnmtd/vite-dev.config.mjs:ro" -w /web \
    "$NODE_IMAGE" sh -c '
      if ! cmp -s package-lock.json node_modules/.installed-lock; then
        find node_modules -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        npm ci --ignore-scripts --loglevel=error && cp package-lock.json node_modules/.installed-lock || exit 1
      fi
      mkdir -p node_modules/.pnmtd-dev && cp /pnmtd/vite-dev.config.mjs node_modules/.pnmtd-dev/vite.config.mjs || exit 1
      exec node_modules/.bin/vite --config node_modules/.pnmtd-dev/vite.config.mjs --host 0.0.0.0 --port 5173 --strictPort' >/dev/null
  for _ in $(seq 1 240); do
    if curl -fsS -o /dev/null "http://localhost:${WEB_PORT}/pnmt-admin/" 2>/dev/null; then return 0; fi
    if [[ -z "$("$ENGINE" ps -q --filter "name=^${WEB_NAME}$")" ]]; then break; fi
    sleep 0.5
  done
  "$ENGINE" logs --tail 30 "$WEB_NAME" >&2 || true
  die "${WEB_NAME} did not come up."
}

if [[ "$WEB" == dev ]]; then
  start_web
elif [[ -n "$("$ENGINE" ps -aq --filter "name=^${WEB_NAME}$" 2>/dev/null)" ]]; then
  "$ENGINE" rm -f "$WEB_NAME" >/dev/null 2>&1 || true
  echo "Removed ${WEB_NAME}: this start is without WEB=dev."
fi

echo
echo "─────────────────────────────────────────────"
echo "PNMTD running on http://localhost:${PORT}/pnmt-admin/  (Development)"
if [[ "$WEB" == dev ]]; then
  echo "Vite (HMR):  http://localhost:${WEB_PORT}/pnmt-admin/  (edits of PNMTD.WebUI are live)"
fi
echo "Sensor API:  http://localhost:${PORT}/event/<write token>/200/hello"
echo "             http://localhost:${PORT}/read/<read token>/json"
echo "API:         http://localhost:${PORT}/api/v1/  (document: PNMTD.WebUI/src/api/openapi.json)"
echo "UDP nodes:   127.0.0.1:${UDP_PORT}/udp; a simulated device:"
echo "             ${ENGINE} exec ${NAME} pnmtd udp node --server 127.0.0.1:5028 --id <device id> --key <key>"
if [[ "$WIREGUARD" != "0" ]]; then
  echo "WireGuard:   ${WG_NAME} on ${WG_PORT}/udp; routers dial ${WG_EXTERNAL_HOST}:${WG_PORT}"
  echo "             ${ENGINE} exec ${NAME} pnmtd wireguard status"
fi
echo "Sign-in:     admin, with Auth__InitialAdmin__Password from ${ENV_FILE} until you change it"
echo "             (only while the database had no users: imported users keep their passwords)"
if (( seeded )); then
  echo "Seeded:      $(grep '^Seeded' "$scratch/seed.log")"
fi
if (( imported )); then
  echo "Imported:    ${LEGACY_DIR}/pnmtd.db"
  grep -E '^ +[a-z_]+ +[0-9]+ rows$|^ +User |Warning' "$scratch/import.log" | sed 's/^ */             /' || true
elif [[ -n "$LEGACY_DIR" ]]; then
  echo "Data:        as it was. IMPORT=1 imports ${LEGACY_DIR} again, replacing it."
fi
echo "Database:    ${DB_NAME} (volume ${PG_VOLUME}); psql: ${ENGINE} exec -it ${DB_NAME} psql -U pnmtd"
echo "Logs:        ${ENGINE} logs -f ${NAME}"
echo "─────────────────────────────────────────────"
echo "Rerun for .NET changes$([[ "$WEB" == dev ]] || echo ' and web changes (or WEB=dev ./start.sh for live web edits)'). ./stop.sh stops it; ./stop.sh --data also offers to delete the data."
