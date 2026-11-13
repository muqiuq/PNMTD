#!/usr/bin/env bash
# The browser tests (PNMTD.WebUI/e2e, Playwright) against the real image, everything in containers
# (docs/webui-migration-plan.md §8.6):
#
#   1. SEED=1 ./start.sh with names, volumes and a port of their own (http://localhost:7398/pnmt-admin/),
#      so the debugging stack and its data stay untouched: the demo data of `pnmtd dev seed`;
#   2. users: an admin with a temporary password (the sign-in tests start from one), and e2e-admin
#      and e2e-viewer with passwords of their own (the page tests);
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
  KEYS_VOLUME=pnmtd-e2e-keys PORT="${E2E_PORT:-7398}" UDP_PORT="${E2E_UDP_PORT:-7397}" WEB="" SEED=1

# Settings of their own: start.sh adds the generated passwords. The tests sign in more often than
# the login rate limit allows from one address (10 a minute); the .NET tests cover the limit.
mkdir -p .tmpcheck
export ENV_FILE="$PWD/.tmpcheck/e2e.env"
(umask 077 && printf '%s\n' 'Auth__LoginRateLimit__PermitLimit=1000' >"$ENV_FILE")

cleanup() {
  if [[ "$KEEP" != "1" ]]; then
    ./stop.sh >/dev/null 2>&1 || true
    "$ENGINE" volume rm -f "$PG_VOLUME" "$KEYS_VOLUME" >/dev/null 2>&1 || true
    rm -f "$ENV_FILE"
  fi
}
trap cleanup EXIT

log="$(mktemp "$PWD/.tmpcheck/e2e.XXXXXX")"
echo "Starting ${NAME} on port ${PORT}…"
./start.sh >"$log" 2>&1 || { cat "$log" >&2; exit 1; }

user="e2e-$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
"$ENGINE" exec "$NAME" pnmtd users add "$user" --role admin --display-name "E2E Admin" >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }
password="$(awk '/^    [A-Za-z0-9]+$/ {print $1}' "$log")"
[[ -n "$password" ]] || { cat "$log" >&2; echo "users add printed no temporary password" >&2; exit 1; }

# Another admin and a viewer with passwords of their own, for the page tests (no forced change).
admin_password="e2e-admin-$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
viewer_password="e2e-viewer-$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
echo "$admin_password" | "$ENGINE" exec -i "$NAME" pnmtd users add e2e-admin --role admin --display-name "Ada Admin" --password-stdin >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }
echo "$viewer_password" | "$ENGINE" exec -i "$NAME" pnmtd users add e2e-viewer --role viewer --display-name "Vera Viewer" --password-stdin >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }

# node_modules (with @playwright/test) are those of tools/npm.sh: installed if the lock changed.
tools/npm.sh exec -- playwright --version >/dev/null

rm -rf PNMTD.WebUI/test-results
# In the app container's network: PNMT is http://localhost:8080 for the browser, a secure context as HTTPS is in
# production, which WebCrypto (the backups' decryption, docs/mikrotik-backup-plan.md §3.6) needs. UDP goes to the
# port the app listens on inside its container.
"$ENGINE" run --rm --network "container:${NAME}" --ipc=host \
  -v "$PWD/PNMTD.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -w /web \
  -e "E2E_BASE_URL=http://localhost:8080" -e E2E_UDP_PORT=5028 -e "E2E_USER=${user}" -e "E2E_PASSWORD=${password}" \
  -e "E2E_ADMIN_PASSWORD=${admin_password}" -e "E2E_VIEWER_PASSWORD=${viewer_password}" -e CI=1 \
  "$PLAYWRIGHT_IMAGE" node_modules/.bin/playwright test "$@"
