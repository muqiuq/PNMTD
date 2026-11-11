#!/usr/bin/env bash
# Build and run PNMTD in containers (Podman by default) for debugging on a laptop: PostgreSQL, the
# migrations and the app, on http://localhost:7327/. Production runs from deploy/ with Docker; this
# is not how it is deployed (docs/webui-migration-plan.md §6.2).
#
#   ./start.sh                     build and (re)start
#   LEGACY_DIR=~/pnmt/pnmtd-data ./start.sh
#                                  on an empty database: import that v0.12 data folder (its pnmtd.db),
#                                  as the real upgrade does; the folder is mounted read-only
#   IMPORT=1 LEGACY_DIR=… ./start.sh
#                                  import again, replacing the database's contents
#   SOURCE_TZ=Europe/Zurich        the zone the v0.12 data was written in (default UTC)
#   PORT=7400 ./start.sh           another host port
#   VERBOSE=1 ./start.sh           show the build log, which is otherwise only printed on failure
#   ENGINE=docker ./start.sh       Docker instead of Podman
#
# Settings come from dev.env (gitignored, 0600, deploy/.env format: Smtp__Host=…, Identity=…, TZ=…).
# The first run creates it with a generated database password. ./stop.sh stops everything again.
set -euo pipefail
cd "$(dirname "$0")"

ENGINE="${ENGINE:-podman}"
NAME="${NAME:-pnmtd}"
IMAGE="${IMAGE:-pnmtd:dev}"
PORT="${PORT:-7327}"
DB_NAME="${DB_NAME:-pnmtd-db}"
NETWORK="${NETWORK:-pnmtd-dev}"
PG_VOLUME="${PG_VOLUME:-pnmtd-pgdata}"
KEYS_VOLUME="${KEYS_VOLUME:-pnmtd-keys-dev}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-docker.io/library/postgres:18-alpine}"
LEGACY_DIR="${LEGACY_DIR:-}"
IMPORT="${IMPORT:-0}"
SOURCE_TZ="${SOURCE_TZ:-UTC}"
VERBOSE="${VERBOSE:-0}"

die() { echo "$@" >&2; exit 1; }

# A setting of dev.env: its last line, without the quotes .env allows around a value.
setting() {
  sed -n "s/^$1=//p" dev.env | tail -1 | sed -E "s/^'(.*)'\$/\\1/; s/^\"(.*)\"\$/\\1/"
}

# A line appended to dev.env, which is edited by hand and may lack its final newline.
add_setting() {
  if [[ -s dev.env && -n "$(tail -c1 dev.env)" ]]; then echo >> dev.env; fi
  printf '%s=%s\n' "$1" "$2" >> dev.env
}

# 32 hex digits; a fixed read, since tr on an endless stream ends in SIGPIPE.
random_secret() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }

if [[ ! -f dev.env ]]; then
  (umask 077 && printf '%s\n' \
    '# Settings for ./start.sh, in deploy/.env format (ASP.NET names: Smtp__Host=…, Identity=…, TZ=…).' \
    '# Only what differs from the defaults needs a line.' > dev.env)
  echo "dev.env created."
fi
if [[ -z "$(setting PNMTD_DB_PASSWORD)" ]]; then
  add_setting PNMTD_DB_PASSWORD "$(random_secret)"
fi
db_password="$(setting PNMTD_DB_PASSWORD)"

if [[ -n "$LEGACY_DIR" ]]; then
  LEGACY_DIR="$(cd "$LEGACY_DIR" && pwd)" || die "LEGACY_DIR: no such folder."
  [[ -f "$LEGACY_DIR/pnmtd.db" ]] || die "LEGACY_DIR has no pnmtd.db: point it at a v0.12 pnmtd-data folder."
elif [[ "$IMPORT" != "0" ]]; then
  die "IMPORT=1 needs LEGACY_DIR, the v0.12 data folder to import."
fi

# Something else publishing the port would stop the app at the very end: said here, before the build.
holder=$("$ENGINE" ps --format '{{.Names}} {{.Ports}}' | awk -v port=":${PORT}->" -v app="$NAME" '$1 != app && index($0, port) { print $1; exit }')
[[ -z "$holder" ]] || die "Port ${PORT} is taken by the container ${holder}: stop it, or choose another (PORT=… ./start.sh)."

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
  "$ENGINE" run --rm --network "$NETWORK" --env-file dev.env \
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
  if ! pnmtd -v "${LEGACY_DIR}:/legacy:ro" "$IMAGE" \
      import-sqlite --from /legacy/pnmtd.db --source-time-zone "$SOURCE_TZ" ${reset[@]+"${reset[@]}"} >"$scratch/import.log" 2>&1; then
    grep -v '^info: \|^ *Executed DbCommand' "$scratch/import.log" >&2 || true
    die "The import stopped; the report above says why. ${LEGACY_DIR} is untouched."
  fi
  imported=1
fi

"$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
# As hardened as in deploy/docker-compose.yml, so what fails there fails here first.
"$ENGINE" run -d --name "$NAME" --hostname "$NAME" --network "$NETWORK" \
  -p "${PORT}:8080" \
  --env-file dev.env \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "Database__Host=${DB_NAME}" -e "Database__Password=${db_password}" \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  --sysctl "net.ipv4.ping_group_range=0 2147483647" \
  -v "${KEYS_VOLUME}:/var/lib/pnmtd/keys" \
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

echo
echo "─────────────────────────────────────────────"
echo "PNMTD running on http://localhost:${PORT}/  (Development)"
echo "Sensor API:  http://localhost:${PORT}/event/<write token>/200/hello"
echo "             http://localhost:${PORT}/read/<read token>/json"
echo "API:         http://localhost:${PORT}/api/v1/  (document: PNMTD.WebUI/src/api/openapi.json)"
if (( imported )); then
  echo "Imported:    ${LEGACY_DIR}/pnmtd.db"
  grep -E '^ +[a-z]+ +[0-9]+ rows$|Warning' "$scratch/import.log" | sed 's/^ */             /' || true
elif [[ -n "$LEGACY_DIR" ]]; then
  echo "Data:        as it was. IMPORT=1 imports ${LEGACY_DIR} again, replacing it."
fi
echo "Database:    ${DB_NAME} (volume ${PG_VOLUME}); psql: ${ENGINE} exec -it ${DB_NAME} psql -U pnmtd"
echo "Logs:        ${ENGINE} logs -f ${NAME}"
echo "─────────────────────────────────────────────"
echo "Rerun for code changes. ./stop.sh stops it; ./stop.sh --data also offers to delete the data."
