#!/usr/bin/env bash
# deploy/ for real, in a scratch copy with a project name of its own: init.sh, docker compose up,
# signing in as the first admin, add-user.sh, reset-password.sh, list-users.sh, a sensor event,
# backup.sh, another event, restore.sh, and the first event is what /read says again.
# Runs the image tools/contract.sh build made (pnmtd:dev). Needs docker and compose; on a Mac the
# docker CLI may talk to Podman. KEEP=1 leaves the stack running.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT="${PROJECT:-pnmtd-deploytest}"
PORT="${DEPLOY_TEST_PORT:-5091}"
KEEP="${KEEP:-0}"
export COMPOSE_PROJECT_NAME="$PROJECT"

mkdir -p .tmpcheck
dir=$(mktemp -d "$PWD/.tmpcheck/deploy-test.XXXXXX")
cp -R deploy/. "$dir/"
cd "$dir"
# shellcheck source=deploy/lib.sh
. ./lib.sh
cleanup() {
  if [[ "$KEEP" != "1" ]]; then
    compose down -v >/dev/null 2>&1 || true
    cd / && rm -rf "$dir"
  fi
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; compose logs --tail 40 pnmtd >&2 || true; exit 1; }

# Podman names local images localhost/…, Docker docker.io/library/….
if docker image inspect localhost/pnmtd:dev >/dev/null 2>&1; then image=localhost/pnmtd; else image=pnmtd; fi

./init.sh --url "http://localhost:${PORT}" >/dev/null
[[ "$(stat -c %a .env 2>/dev/null || stat -f %Lp .env)" == "600" ]] || fail ".env is not 0600"
env_set PNMTD_IMAGE "$image"
env_set PNMTD_IMAGE_TAG dev
env_set PNMTD_PORT "$PORT"
env_set COMPOSE_PROJECT_NAME "$PROJECT"

compose up -d --wait >/dev/null 2>&1 || fail "the stack did not come up healthy"
./info.sh --quiet || fail "info.sh --quiet says it is not healthy"

# The hardened container protects itself (§3.6): the API needs a session, / and old routes are empty 404s.
status() { curl -s -o /dev/null -w '%{http_code}' "http://localhost:${PORT}$1"; }
[[ "$(status /api/v1/hosts)" == "401" ]] || fail "/api/v1/hosts without a session: $(status /api/v1/hosts)"
[[ "$(status /)" == "404" && "$(status /hosts)" == "404" ]] || fail "/ or /hosts is not a 404"
curl -fsS "http://localhost:${PORT}/api/v1/system/info" | grep -q '"identity"' || fail "/api/v1/system/info"

# Sign-in as a browser does it (§4.5, §4.7): the first admin with init.sh's password must change it
# first; unsafe calls carry the XSRF-TOKEN cookie in X-XSRF-TOKEN.
jar="$dir/cookies.txt"
api() {  # api METHOD PATH [JSON] → the status, the body in $dir/body
  local token
  token="$(awk '$6 == "XSRF-TOKEN" {print $7}' "$jar" 2>/dev/null | tail -1)"
  curl -s -o "$dir/body" -w '%{http_code}' -b "$jar" -c "$jar" -X "$1" -H 'Content-Type: application/json' \
    ${token:+-H "X-XSRF-TOKEN: $token"} ${3:+--data "$3"} "http://localhost:${PORT}$2"
}
login() { rm -f "$jar"; api POST /api/v1/auth/login "{\"username\":\"$1\",\"password\":\"$2\"}"; }
admin_password="$(env_value Auth__InitialAdmin__Password)"
[[ "$(login admin "$admin_password")" == "200" ]] || fail "the first admin's sign-in: $(cat "$dir/body")"
grep -q '"mustChangePassword":true' "$dir/body" || fail "the first admin need not change the password: $(cat "$dir/body")"
[[ "$(api GET /api/v1/hosts)" == "403" ]] || fail "a session with a password to change reads hosts"
[[ "$(api POST /api/v1/account/password "{\"currentPassword\":\"$admin_password\",\"newPassword\":\"deploy-test-password-1\"}")" == "204" ]] \
  || fail "changing the first admin's password: $(cat "$dir/body")"
[[ "$(api GET /api/v1/hosts)" == "200" ]] || fail "hosts after the password change: $(cat "$dir/body")"

# The user scripts against the running stack (§4.10).
./add-user.sh ops --role viewer --display-name "Ops Team" >"$dir/add-user.out" || fail "add-user.sh: $(cat "$dir/add-user.out")"
ops_password="$(awk '/^    [A-Za-z0-9]+$/ {print $1}' "$dir/add-user.out")"
[[ -n "$ops_password" ]] || fail "add-user.sh printed no password: $(cat "$dir/add-user.out")"
[[ "$(login ops "$ops_password")" == "200" ]] || fail "sign-in with add-user.sh's password: $(cat "$dir/body")"
./reset-password.sh ops >"$dir/reset.out" || fail "reset-password.sh: $(cat "$dir/reset.out")"
reset_password="$(awk '/^    [A-Za-z0-9]+$/ {print $1}' "$dir/reset.out")"
[[ "$(login ops "$ops_password")" == "401" ]] || fail "the old password still signs in after reset-password.sh"
[[ "$(login ops "$reset_password")" == "200" ]] || fail "sign-in with reset-password.sh's password: $(cat "$dir/body")"
./list-users.sh | grep -Eq '^ops +Viewer +active' || fail "list-users.sh: $(./list-users.sh)"

sql() { compose exec -T db psql -U pnmtd -d pnmtd -tAc "$1"; }
sql "INSERT INTO hosts (\"Id\",\"Created\",\"Enabled\",\"Name\") VALUES ('11111111-1111-1111-1111-111111111111', now(), true, 'h');
     INSERT INTO sensors (\"Id\",\"ParentId\",\"Type\",\"Name\",\"Created\",\"Enabled\",\"Ignore\",\"Interval\",\"GracePeriod\",\"SecretWriteToken\",\"SecretReadToken\")
     VALUES ('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111',10,'s',now(),true,false,86400,86400,'wtok-deploy','rtok-deploy');" >/dev/null

read_raw() { curl -fsS "http://localhost:${PORT}/read/rtok-deploy/raw"; }
curl -fsS -o /dev/null "http://localhost:${PORT}/event/wtok-deploy/200/before-backup" || fail "event before the backup"
[[ "$(read_raw)" == "True;before-backup" ]] || fail "read before the backup: $(read_raw)"

./backup.sh --quiet --keep 3 || fail "backup.sh"
folder="$(ls -1d backups/pnmtd-* | tail -1)"
[[ -s "$folder/database.pgdump" && -s "$folder/keys.tar.gz" ]] || fail "the backup folder is incomplete"
[[ "$(stat -c %a "$folder" 2>/dev/null || stat -f %Lp "$folder")" == "700" ]] || fail "the backup folder is not 0700"

sleep 6   # the sensor API's 5-second rule
curl -fsS -o /dev/null "http://localhost:${PORT}/event/wtok-deploy/500/after-backup" || fail "event after the backup"
[[ "$(read_raw)" == "False;after-backup" ]] || fail "read after the backup: $(read_raw)"

./restore.sh "$folder" --yes >/dev/null 2>&1 || fail "restore.sh"
compose up -d --wait >/dev/null 2>&1 || fail "not healthy after the restore"
[[ "$(read_raw)" == "True;before-backup" ]] || fail "read after the restore: $(read_raw)"
[[ "$(sql 'SELECT count(*) FROM events')" == "1" ]] || fail "events after the restore"

echo "deploy/: init, up, backup, restore — all as expected."
