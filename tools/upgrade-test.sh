#!/usr/bin/env bash
# The upgrade from v0.12 (deploy/upgrade-1.0.sh, §6.5) for real, as a rehearsal in a scratch folder:
#
#   1. a v0.12.1 installation: the backend (the contract's baseline image) with the importer's
#      fixture database, a stand-in for the WebApp (only its settings matter here), and a users.json
#      written as the WebApp hashes passwords; a sensor reports to it;
#   2. deploy/ copied beside it, init.sh, the image from the working tree (pnmtd:dev);
#   3. an upgrade that must fail (an unknown time zone): the old stack runs again, unchanged;
#   4. the upgrade: the sensor's state reads the same on the same port, the old user signs in with
#      the old password, the settings arrived (a password with $ and & included), the old
#      containers are stopped but kept, and the archive exists.
#
#   tools/upgrade-test.sh          KEEP=1 leaves both stacks running
#
# Needs docker and compose (on a Mac the docker CLI may talk to Podman), pnmtd:dev (tools/contract.sh
# build) and pnmtd-contract:v0.12.1 (tools/contract.sh baseline).
set -euo pipefail
cd "$(dirname "$0")/.."

PORT="${UPGRADE_TEST_PORT:-5094}"
KEEP="${KEEP:-0}"
PROJECT="pnmtd-upgradetest"

image_ref() {  # Podman names local images localhost/…, Docker docker.io/library/….
  if docker image inspect "localhost/$1" >/dev/null 2>&1; then echo "localhost/$1"; else echo "$1"; fi
}
dev_image="$(image_ref pnmtd:dev)"
legacy_image="$(image_ref pnmtd-contract:v0.12.1)"
docker image inspect "$legacy_image" >/dev/null 2>&1 || { echo "$legacy_image is missing: tools/contract.sh baseline" >&2; exit 1; }

mkdir -p .tmpcheck
dir=$(mktemp -d "$PWD/.tmpcheck/upgrade-test.XXXXXX")
# The old project is named after its folder, as on a real host.
legacy="$dir/pnmt-legacytest"
mkdir -p "$legacy/pnmtd-data" "$legacy/pnmtwebapp-data"
cp PNMTD.Tests/Fixtures/v0.12-sample.db "$legacy/pnmtd-data/pnmtd.db"
cp -R deploy/. "$dir/deploy/"

# `docker compose` (the plugin), or the standalone docker-compose, as deploy/lib.sh does.
dc() { if docker compose version >/dev/null 2>&1; then docker compose "$@"; else docker-compose "$@"; fi; }
lc() { (unset COMPOSE_PROJECT_NAME; dc -f "$legacy/docker-compose.yml" --project-directory "$legacy" "$@"); }
nc() { (cd "$dir/deploy" && COMPOSE_PROJECT_NAME="$PROJECT" dc "$@"); }
cleanup() {
  if [[ "$KEEP" != "1" ]]; then
    nc down -v >/dev/null 2>&1 || true
    lc down >/dev/null 2>&1 || true
    # The v0.12 containers wrote as root into the bind mounts.
    docker run --rm --user 0 -v "$dir:/d" --entrypoint rm "$dev_image" -rf /d/pnmt-legacytest >/dev/null 2>&1 || true
    rm -rf "$dir"
  fi
}
trap cleanup EXIT
# The 1.0 stack's state and last lines, then the reason: last, where run-tests.sh's tail and a CI
# log show it.
fail() {
  { nc ps -a; nc logs --tail 30 pnmtd; } >&2 2>/dev/null || true
  echo "FAIL: $*" >&2
  exit 1
}

# --- 1. the v0.12 installation -------------------------------------------------------------------
# $$ is a literal $ for compose. The stand-in WebApp only carries the WebApp's settings.
cat >"$legacy/docker-compose.yml" <<EOF
services:
  pnmtd:
    image: ${legacy_image}
    environment:
      - Jwt__Issuer=legacy
      - Jwt__Audience=legacy
      - Jwt__Key=legacy-signing-key-legacy-signing-key
      - Identity=Legacy Lab
      - Smtp__Host=smtp.example.org
      - Smtp__Password=s3cr\$\$t&pw
      - ASPNETCORE_URLS=http://*:80/
    ports:
      - "${PORT}:80"
    volumes:
      - ./pnmtd-data:/var/lib/pnmtd/
  pnmtwebapp:
    image: ${dev_image}
    entrypoint: ["sleep", "infinity"]
    environment:
      - apiurl=http://pnmtd
      - externalApiurl=http://localhost:${PORT}/
    volumes:
      - ./pnmtwebapp-data:/var/lib/pnmt/
EOF
# users.json as WebAppUser.SetPassword writes it: PBKDF2-HMAC-SHA256, 10,000 iterations, 32 bytes.
python3 - "$legacy/pnmtwebapp-data/users.json" <<'PY'
import base64, hashlib, json, os, sys, uuid
salt = os.urandom(16)
key = hashlib.pbkdf2_hmac('sha256', b'legacy-password-1', salt, 10000, 32)
json.dump([{"Id": str(uuid.uuid4()), "Username": "ops", "Name": "Ops Team", "IsAdmin": True,
            "PasswordHash": base64.b64encode(key).decode(), "Salt": base64.b64encode(salt).decode()}], open(sys.argv[1], 'w'))
PY

lc up -d >/dev/null 2>&1 || fail "the v0.12 stack did not start"
base="http://localhost:${PORT}"
for _ in $(seq 1 120); do
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "$base/read/ready-check/json" || true)" == "404" ]] && break
  sleep 0.5
done
# simple01 of the fixture (PNMTD.Tests/Fixtures/v0.12-sample.db).
write=e091b83-a12e7dcc-98b2-661d-5e2d-9c58a73834c8
read=fe11e12-e85d9750-56d3-0be9-7a06-5407b75f1175
curl -fsS -o /dev/null "$base/event/$write/200/before-upgrade" || fail "v0.12 took no event"
before="$(curl -fsS "$base/read/$read/raw")"
[[ "$before" == "True;before-upgrade" ]] || fail "v0.12 reads: $before"

# --- 2. the new deployment beside it ---------------------------------------------------------------
cd "$dir/deploy"
# shellcheck source=deploy/lib.sh
. ./lib.sh
./init.sh >/dev/null
env_set PNMTD_IMAGE "${dev_image%:dev}"
env_set PNMTD_IMAGE_TAG dev
env_set PNMTD_PORT "$PORT"
# Not the default 5028/udp: a dev stack (start.sh) may hold it.
env_set PNMTD_UDP_PORT "${UPGRADE_TEST_UDP_PORT:-5095}"
env_set COMPOSE_PROJECT_NAME "$PROJECT"
export COMPOSE_PROJECT_NAME="$PROJECT"

# --- 3. an upgrade that stops: the old stack must run again -------------------------------------------
if ./upgrade-1.0.sh --legacy "$legacy" --source-time-zone Nowhere/Atlantis --yes >"$dir/failed.out" 2>&1; then
  fail "an upgrade with an unknown time zone succeeded: $(cat "$dir/failed.out")"
fi
grep -q 'The old stack runs again' "$dir/failed.out" || fail "no rollback message: $(tail -20 "$dir/failed.out")"
for _ in $(seq 1 60); do
  [[ "$(curl -s "$base/read/$read/raw" || true)" == "True;before-upgrade" ]] && break
  sleep 0.5
done
[[ "$(curl -s "$base/read/$read/raw" || true)" == "True;before-upgrade" ]] || fail "v0.12 does not answer again after the failed upgrade"

# --- 4. the upgrade ---------------------------------------------------------------------------------
./upgrade-1.0.sh --legacy "$legacy" --source-time-zone Europe/Zurich --copy-settings --yes >"$dir/upgrade.out" 2>&1 \
  || fail "the upgrade: $(tail -40 "$dir/upgrade.out")"
grep -q 'The upgrade is done' "$dir/upgrade.out" || fail "$(tail -20 "$dir/upgrade.out")"

after="$(curl -fsS "$base/read/$read/raw")" || fail "1.0 does not answer /read on port $PORT"
[[ "$after" == "$before" ]] || fail "the sensor reads \"$after\" after the upgrade, \"$before\" before"
sleep 6   # the sensor API's 5-second rule
curl -fsS -o /dev/null "$base/event/$write/200/after-upgrade" || fail "1.0 takes no event with the old URL"
[[ "$(curl -fsS "$base/read/$read/raw")" == "True;after-upgrade" ]] || fail "the event after the upgrade"

status="$(curl -s -o "$dir/login.json" -w '%{http_code}' -H 'Content-Type: application/json' \
  -d '{"username":"ops","password":"legacy-password-1"}' "$base/api/v1/auth/login")"
[[ "$status" == "200" ]] || fail "the WebApp user cannot sign in with the old password: $status $(cat "$dir/login.json")"
grep -q '"mustChangePassword":false' "$dir/login.json" || fail "the old password counts as temporary: $(cat "$dir/login.json")"
grep -q '"role":"Admin"' "$dir/login.json" || fail "IsAdmin did not become Admin: $(cat "$dir/login.json")"

[[ "$(env_value Identity)" == "Legacy Lab" ]] || fail "Identity was not copied: $(grep Identity .env)"
[[ "$(env_value PublicBaseUrl)" == "http://localhost:${PORT}" ]] || fail "externalApiurl did not become PublicBaseUrl"
docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$(nc ps -q pnmtd)" | grep -qx 'Smtp__Password=s3cr$t&pw' \
  || fail "the SMTP password did not arrive unchanged"
curl -fsS "$base/api/v1/system/info" | grep -q '"identity":"Legacy Lab"' || fail "1.0 does not run with the copied Identity"

[[ "$(docker inspect -f '{{.State.Status}}' "$(lc ps -aq pnmtd)")" == "exited" ]] || fail "the old backend is not stopped and kept"
ls backups/upgrade-1.0-*.tar.gz >/dev/null 2>&1 || fail "no archive of the old data"
[[ -s "$(ls -1 backups/upgrade-1.0-*.tar.gz | tail -1)" ]] || fail "the archive is empty"

echo "Upgrade rehearsal: failed import rolled back; data, URLs, users and settings carried over."
