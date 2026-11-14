#!/usr/bin/env bash
# The frozen sensor API contract (docs/webui-migration-plan.md §3.3, §8.1): hundreds of applications
# report to /event and /read, so every build must answer exactly as v0.12.1 does.
#
#   tools/contract.sh baseline          build the v0.12.1 image (pnmtd-contract:v0.12.1) from the git tag
#   tools/contract.sh make-fixture      create PNMTD.Contract.Tests/fixture/ with that image (done once, committed)
#   tools/contract.sh record            record PNMTD.Contract.Tests/golden/sensor-api.json from that image
#   tools/contract.sh make-import-fixture   the importer test's v0.12 database (PNMTD.Tests/Fixtures/)
#   tools/contract.sh build             build pnmtd:dev (or IMAGE) from the working tree
#   tools/contract.sh verify [IMAGE]    replay the contract against IMAGE (default pnmtd:dev) and compare
#
# Every run starts the image with a fresh copy of the fixture database, in Production mode (the
# 5-second rule is off in Development). The harness runs on the host with the .NET SDK and talks to
# the container over a loopback port, and for 1.0 images reads the stored state from PostgreSQL over
# another (DatabaseProbe). KEEP=1 leaves the containers running afterwards. ENGINE=docker for Docker (CI).
set -euo pipefail
cd "$(dirname "$0")/.."

ENGINE="${ENGINE:-podman}"
BASELINE_TAG="v0.12.1"
BASELINE_IMAGE="pnmtd-contract:${BASELINE_TAG}"
DEV_IMAGE="${DEV_IMAGE:-${IMAGE:-pnmtd:dev}}"
NAME="${NAME:-pnmtd-contract}"
VOLUME="${NAME}-data"
DB_NAME="${NAME}-db"
NETWORK="${NAME}-net"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-docker.io/library/postgres:18-alpine}"
PORT="${CONTRACT_PORT:-7399}"
DB_PORT="${CONTRACT_DB_PORT:-7398}"
DB_CONNECTION=""
FIXTURE_DIR="PNMTD.Contract.Tests/fixture"
KEEP="${KEEP:-0}"

# Under the repository: Podman on macOS cannot mount /tmp or /var/folders.
mkdir -p .tmpcheck
scratch=$(mktemp -d "$PWD/.tmpcheck/contract.XXXXXX")
cleanup() {
  rm -rf "$scratch"
  if [[ "$KEEP" != "1" ]]; then
    "$ENGINE" rm -f "$NAME" "$DB_NAME" >/dev/null 2>&1 || true
    "$ENGINE" volume rm -f "$VOLUME" >/dev/null 2>&1 || true
    "$ENGINE" network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

die() { echo "$@" >&2; exit 1; }

build_baseline() {
  echo "Building ${BASELINE_IMAGE} from ${BASELINE_TAG}…"
  mkdir -p "$scratch/src"
  git archive "$BASELINE_TAG" | tar -x -C "$scratch/src"
  "$ENGINE" build -q -t "$BASELINE_IMAGE" -f "$scratch/src/Dockerfile" "$scratch/src" >/dev/null
}

build_dev() {
  echo "Building ${DEV_IMAGE} from the working tree…"
  "$ENGINE" build -q -t "$DEV_IMAGE" -f Dockerfile . >/dev/null
}

wait_for_app() {
  local image="$1"
  for _ in $(seq 1 120); do
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${PORT}/read/ready-check/json" || true)" == "404" ]]; then
      return 0
    fi
    [[ -n "$("$ENGINE" ps -q --filter "name=^${NAME}$")" ]] || break
    sleep 0.5
  done
  "$ENGINE" logs --tail 40 "$NAME" >&2 || true
  die "${image} did not come up on port ${PORT}."
}

# 1.0 images: PostgreSQL on a private network, the fixture loaded through `pnmtd import-sqlite`,
# exactly as an upgrade does (§6.5).
start_app_postgres() {
  local image="$1"
  "$ENGINE" rm -f "$NAME" "$DB_NAME" >/dev/null 2>&1 || true
  "$ENGINE" network create "$NETWORK" >/dev/null 2>&1 || true
  # Published on loopback: the harness reads what the cases stored (DatabaseProbe).
  "$ENGINE" run -d --name "$DB_NAME" --network "$NETWORK" -p "127.0.0.1:${DB_PORT}:5432" \
    -e POSTGRES_USER=pnmtd -e POSTGRES_DB=pnmtd -e POSTGRES_PASSWORD=contract \
    "$POSTGRES_IMAGE" -c fsync=off >/dev/null
  for _ in $(seq 1 120); do
    "$ENGINE" exec "$DB_NAME" pg_isready -q -h 127.0.0.1 -U pnmtd 2>/dev/null && break
    sleep 0.5
  done
  local db_env=(-e "Database__Host=${DB_NAME}" -e Database__Password=contract)
  "$ENGINE" run --rm --network "$NETWORK" "${db_env[@]}" -v "$PWD/${FIXTURE_DIR}:/fixture:ro" \
    "$image" import-sqlite --from /fixture/pnmtd.db >"$scratch/import.log" 2>&1 \
    || { cat "$scratch/import.log" >&2; die "The fixture import failed."; }
  "$ENGINE" run -d --name "$NAME" --network "$NETWORK" -p "127.0.0.1:${PORT}:8080" "${db_env[@]}" \
    "$image" >/dev/null
  wait_for_app "$image"
  DB_CONNECTION="Host=127.0.0.1;Port=${DB_PORT};Username=pnmtd;Password=contract;Database=pnmtd;Pooling=false"
}

# start IMAGE [fixture]: a fresh container and data volume, with a copy of the fixture database
# when asked. A named volume rather than a bind mount: SQLite wants a real file system, which a
# macOS folder shared into the Podman VM is not. A one-off root container fills it and hands it
# to the image's user (Docker's cp would leave the files to root).
start_app() {
  local image="$1" with_fixture="${2:-}" user
  "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
  "$ENGINE" volume rm -f "$VOLUME" >/dev/null 2>&1 || true
  "$ENGINE" volume create "$VOLUME" >/dev/null
  user="$("$ENGINE" image inspect -f '{{.Config.User}}' "$image")"
  if [[ -n "$with_fixture" ]]; then
    ls "$FIXTURE_DIR"/pnmtd.db* >/dev/null 2>&1 || die "No fixture: run tools/contract.sh make-fixture first."
  fi
  # shellcheck disable=SC2016 # expanded in the container
  "$ENGINE" run --rm --user 0 --entrypoint sh -v "${VOLUME}:/data" -v "$PWD/${FIXTURE_DIR}:/fixture:ro" "$image" -c '
    set -e
    if [ "$1" = fixture ]; then cp /fixture/pnmtd.db* /data/; fi
    if [ -n "$2" ]; then chown -R "$2" /data; fi' sh "$with_fixture" "$user"
  # v0.12.1 refuses to start without the Jwt settings.
  "$ENGINE" run -d --name "$NAME" -p "127.0.0.1:${PORT}:8080" \
    -v "${VOLUME}:/var/lib/pnmtd" \
    -e ASPNETCORE_URLS=http://+:8080 \
    -e Jwt__Issuer=contract -e Jwt__Audience=contract -e Jwt__Key=contract-signing-key \
    "$image" >/dev/null
  wait_for_app "$image"
}

harness() {
  local mode="$1" image="$2"
  CONTRACT_BASE_URL="http://127.0.0.1:${PORT}" CONTRACT_MODE="$mode" CONTRACT_IMAGE="$image" \
    CONTRACT_DB_CONNECTION="$DB_CONNECTION" \
    dotnet test --project PNMTD.Contract.Tests/PNMTD.Contract.Tests.csproj --output detailed
}

image_exists() { "$ENGINE" image inspect "$1" >/dev/null 2>&1; }

case "${1:-}" in
  baseline)
    build_baseline
    ;;
  make-fixture)
    image_exists "$BASELINE_IMAGE" || build_baseline
    start_app "$BASELINE_IMAGE"
    harness make-fixture "$BASELINE_IMAGE"
    "$ENGINE" stop -t 30 "$NAME" >/dev/null
    mkdir -p "$scratch/out"
    "$ENGINE" cp "${NAME}:/var/lib/pnmtd/." "$scratch/out/"
    rm -f "$FIXTURE_DIR"/pnmtd.db*
    cp "$scratch/out"/pnmtd.db* "$FIXTURE_DIR/"
    echo "Fixture written: $(ls "$FIXTURE_DIR" | tr '\n' ' ')"
    ;;
  make-import-fixture)
    # PNMTD.Tests' importer fixture: a real v0.12.1 database with data in every table.
    image_exists "$BASELINE_IMAGE" || build_baseline
    start_app "$BASELINE_IMAGE" fixture
    harness make-import-fixture "$BASELINE_IMAGE"
    "$ENGINE" stop -t 30 "$NAME" >/dev/null
    mkdir -p "$scratch/out"
    "$ENGINE" cp "${NAME}:/var/lib/pnmtd/." "$scratch/out/"
    out="PNMTD.Tests/Fixtures/v0.12-sample.db"
    mkdir -p "$(dirname "$out")"
    sqlite3 "$scratch/out/pnmtd.db" "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;" >/dev/null
    # Mail log rows, in v0.12's storage format: only an IMAP mailbox writes them otherwise.
    rule=$(sqlite3 "$scratch/out/pnmtd.db" "SELECT Id FROM mailinputrules LIMIT 1")
    sqlite3 "$scratch/out/pnmtd.db" "
      INSERT INTO maillogs (Id, Content, Created, \"From\", ProcessLog, Processed, ProcessedById, Subject, \"To\", MessageDate, RemoteId) VALUES
      ('6D1E9A57-0C2B-4F3B-9A11-3C4D5E6F7A81', 'Result: success', '2026-03-29 01:30:00.1234567', 'backup@example.com', 'matched', 1, '${rule}', 'Backup done', 'pnmt@example.com', '2026-03-29 01:29:59', 'imap-1'),
      ('6D1E9A57-0C2B-4F3B-9A11-3C4D5E6F7A82', 'unrelated', '2026-10-25 02:30:00', 'someone@example.com', '', 0, NULL, 'Hello', 'pnmt@example.com', '0001-01-01 00:00:00', 'imap-2');"
    cp "$scratch/out/pnmtd.db" "$out"
    echo "Written: $out"
    sqlite3 "$out" "SELECT 'events', COUNT(*) FROM events UNION ALL SELECT 'sensors', COUNT(*) FROM sensors UNION ALL SELECT 'notificationruleevent', COUNT(*) FROM notificationruleevent UNION ALL SELECT 'dnszoneentries', COUNT(*) FROM dnszoneentries UNION ALL SELECT 'dnszonelogentries', COUNT(*) FROM dnszonelogentries UNION ALL SELECT 'maillogs', COUNT(*) FROM maillogs UNION ALL SELECT 'keyvalues', COUNT(*) FROM keyvalues"
    ;;
  record)
    image_exists "$BASELINE_IMAGE" || build_baseline
    start_app "$BASELINE_IMAGE" fixture
    harness record "$BASELINE_IMAGE"
    echo "Recorded PNMTD.Contract.Tests/golden/sensor-api.json from ${BASELINE_IMAGE}."
    ;;
  build)
    build_dev
    ;;
  verify)
    image="${2:-$DEV_IMAGE}"
    image_exists "$image" || { [[ "$image" == "$DEV_IMAGE" ]] && build_dev; } || die "No image ${image}."
    # v0.12.1 reads SQLite; everything after it PostgreSQL.
    if [[ "$image" == "$BASELINE_IMAGE" ]]; then
      start_app "$image" fixture
    else
      start_app_postgres "$image"
      echo "Fixture imported: $(grep -c 'rows$' "$scratch/import.log" || true) tables."
    fi
    harness verify "$image"
    ;;
  *)
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
