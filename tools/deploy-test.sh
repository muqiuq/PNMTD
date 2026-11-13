#!/usr/bin/env bash
# deploy/ for real, in a scratch copy with a project name of its own: init.sh, docker compose up,
# signing in as the first admin, add-user.sh, reset-password.sh, list-users.sh, reset-totp.sh, a sensor event,
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

# The containers' state and the app's last lines, then the reason: last, where run-tests.sh's tail
# and a CI log show it.
fail() {
  { compose ps -a; compose logs --tail 30 pnmtd; } >&2 2>/dev/null || true
  echo "FAIL: $*" >&2
  exit 1
}

# compose up --wait, its own output kept for the failure: which container, in which state.
up_wait() { compose up -d --wait >"$dir/up.out" 2>&1; }

# Podman names local images localhost/…, Docker docker.io/library/….
if docker image inspect localhost/pnmtd:dev >/dev/null 2>&1; then image=localhost/pnmtd; else image=pnmtd; fi

./init.sh --url "http://localhost:${PORT}" >/dev/null
[[ "$(stat -c %a .env 2>/dev/null || stat -f %Lp .env)" == "600" ]] || fail ".env is not 0600"
env_set PNMTD_IMAGE "$image"
env_set PNMTD_IMAGE_TAG dev
env_set PNMTD_PORT "$PORT"
# Not the default 5028/udp: a dev stack (start.sh) may hold it.
env_set PNMTD_UDP_PORT "${DEPLOY_TEST_UDP_PORT:-5092}"
env_set COMPOSE_PROJECT_NAME "$PROJECT"

up_wait || fail "the stack did not come up healthy: $(tail -3 "$dir/up.out")"
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

# reset-totp.sh (§4.6): 2FA turned on behind the app's back (the enrollment itself is in the
# integration tests), so the password alone no longer signs in; after the reset it does again.
sql() { compose exec -T db psql -U pnmtd -d pnmtd -tAc "$1"; }
sql "UPDATE users SET \"IsTotpEnabled\" = true, \"TotpSecretProtected\" = 'unreadable' WHERE \"NormalizedUsername\" = 'ops'" >/dev/null
[[ "$(login ops "$reset_password")" == "200" ]] && grep -q '"next":"totp"' "$dir/body" || fail "no code step with 2FA on: $(cat "$dir/body")"
./list-users.sh | grep -Eq '^ops +Viewer +active +2FA' || fail "list-users.sh shows no 2FA: $(./list-users.sh)"
./reset-totp.sh ops >"$dir/reset-totp.out" || fail "reset-totp.sh: $(cat "$dir/reset-totp.out")"
grep -q 'Turned 2FA off for ops' "$dir/reset-totp.out" || fail "reset-totp.sh: $(cat "$dir/reset-totp.out")"
[[ "$(login ops "$reset_password")" == "200" ]] && grep -q '"next":"done"' "$dir/body" || fail "2FA still asked after reset-totp.sh: $(cat "$dir/body")"
./info.sh | grep -q 'Admins without 2FA: admin\.' || fail "info.sh does not warn about the admin without 2FA: $(./info.sh)"

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

./restore.sh "$folder" --yes >"$dir/restore.out" 2>&1 || fail "restore.sh: $(tail -5 "$dir/restore.out")"
up_wait || fail "not healthy after the restore: $(tail -3 "$dir/up.out")"
[[ "$(read_raw)" == "True;before-backup" ]] || fail "read after the restore: $(read_raw)"
# The test sensor's events only: PNMT's own system sensors (the RouterOS advisories) write theirs at the start.
[[ "$(sql "SELECT count(*) FROM events WHERE \"SensorId\" = '22222222-2222-2222-2222-222222222222'")" == "1" ]] || fail "events after the restore"

echo "deploy/: init, up, backup, restore — all as expected."
