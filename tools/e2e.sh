#!/usr/bin/env bash
# The browser tests (PNMTD.WebUI/e2e, Playwright) against the real image, everything in containers
# (docs/webui-migration-plan.md §8.6):
#
#   1. ./start.sh with names, volumes and a port of their own (http://localhost:7398/pnmt-admin/),
#      so the debugging stack and its data stay untouched;
#   2. a fresh admin with a temporary password (`pnmtd users add`), as the tests start from one;
#   3. Playwright in its image, on the stack's network;
#   4. the stack and its volumes removed again.
#
#   tools/e2e.sh                 all of it
#   KEEP=1 tools/e2e.sh          leave the stack running afterwards
#   tools/e2e.sh -g "phone"      arguments go to `playwright test`
#
# Screenshots for review land in PNMTD.WebUI/test-results/screens/.
set -euo pipefail
cd "$(dirname "$0")/.."

ENGINE="${ENGINE:-podman}"
KEEP="${KEEP:-0}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pnmtd-web-node-modules}"
version="$(sed -n 's/.*"@playwright\/test": "\([0-9.]*\)".*/\1/p' PNMTD.WebUI/package.json)"
PLAYWRIGHT_IMAGE="${PLAYWRIGHT_IMAGE:-mcr.microsoft.com/playwright:v${version}-noble}"

export ENGINE NAME=pnmtd-e2e DB_NAME=pnmtd-e2e-db NETWORK=pnmtd-e2e PG_VOLUME=pnmtd-e2e-pgdata \
  KEYS_VOLUME=pnmtd-e2e-keys PORT="${E2E_PORT:-7398}" WEB=""

cleanup() {
  if [[ "$KEEP" != "1" ]]; then
    ./stop.sh >/dev/null 2>&1 || true
    "$ENGINE" volume rm -f "$PG_VOLUME" "$KEYS_VOLUME" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

mkdir -p .tmpcheck
log="$(mktemp "$PWD/.tmpcheck/e2e.XXXXXX")"
echo "Starting ${NAME} on port ${PORT}…"
./start.sh >"$log" 2>&1 || { cat "$log" >&2; exit 1; }

user="e2e-$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
"$ENGINE" exec "$NAME" pnmtd users add "$user" --role admin --display-name "E2E Admin" >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }
password="$(awk '/^    [A-Za-z0-9]+$/ {print $1}' "$log")"
[[ -n "$password" ]] || { cat "$log" >&2; echo "users add printed no temporary password" >&2; exit 1; }

# node_modules (with @playwright/test) are those of tools/npm.sh: installed if the lock changed.
tools/npm.sh exec -- playwright --version >/dev/null

rm -rf PNMTD.WebUI/test-results
"$ENGINE" run --rm --network "$NETWORK" --ipc=host \
  -v "$PWD/PNMTD.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -w /web \
  -e "E2E_BASE_URL=http://${NAME}:8080" -e "E2E_USER=${user}" -e "E2E_PASSWORD=${password}" -e CI=1 \
  "$PLAYWRIGHT_IMAGE" node_modules/.bin/playwright test "$@"
