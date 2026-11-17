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
#   E2E_NAME=pnmtd-e2e-2 E2E_PORT=7498 E2E_UDP_PORT=7497 tools/e2e.sh
#                                another stack beside this one (containers, volumes and folders named after it)
#   E2E_ENV=$'Redmine__Url=…\nRedmine__ApiKey=…' tools/e2e.sh
#                                settings of its own for the stack, one per line: a real Redmine or Zabbix to test
#                                against instead of the fakes
#   E2E_FAKES=0 tools/e2e.sh     without the fake Redmine and Zabbix (the integrations' specs skip themselves)
#
# Redmine and Zabbix (docs/audit-redmine-zabbix-plan.md §12) are `pnmtd dev fake-integrations` beside the stack, answering
# as Redmine 6.0 and Zabbix 7.0 did when they were recorded, unless E2E_ENV names real ones.
#
# AI is on against an address that never answers, with its scheduled uses off: the knowledge base's AI question opens
# (docs/locations-knowledge-plan.md §7.2), and nothing is sent.
#
# Screenshots for review land in PNMTD.WebUI/test-results/screens/.
set -euo pipefail
cd "$(dirname "$0")/.."

ENGINE="${ENGINE:-podman}"
KEEP="${KEEP:-0}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pnmtd-web-node-modules}"
version="$(sed -n 's/.*"@playwright\/test": "\([0-9.]*\)".*/\1/p' PNMTD.WebUI/package.json)"
PLAYWRIGHT_IMAGE="${PLAYWRIGHT_IMAGE:-mcr.microsoft.com/playwright:v${version}-noble}"

E2E_NAME="${E2E_NAME:-pnmtd-e2e}"
export ENGINE NAME="$E2E_NAME" DB_NAME="${E2E_NAME}-db" NETWORK="$E2E_NAME" PG_VOLUME="${E2E_NAME}-pgdata" \
  KEYS_VOLUME="${E2E_NAME}-keys" DOWNLOADS_VOLUME="${E2E_NAME}-downloads" PORT="${E2E_PORT:-7398}" UDP_PORT="${E2E_UDP_PORT:-7397}" WEB="" SEED=1 \
  FILES_DIR="$PWD/.tmpcheck/${E2E_NAME}-files"

# Settings of their own: start.sh adds the generated passwords. The tests sign in more often than
# the login rate limit allows from one address (10 a minute), and post reports to an Advanced sensor faster than one
# in 5 seconds; the .NET tests cover both limits.
mkdir -p .tmpcheck
export ENV_FILE="$PWD/.tmpcheck/${E2E_NAME}.env"
(umask 077 && printf '%s\n' 'Auth__LoginRateLimit__PermitLimit=1000' \
  'Ai__Enabled=true' 'Ai__ApiKey=e2e-no-provider' 'Ai__BaseUrl=http://127.0.0.1:9/api/v1' 'Ai__Guard__Url=http://127.0.0.1:9/api/alpha/decisions' \
  'Ai__Mail__Enabled=false' 'Mikrotik__Advisories__Enabled=false' 'Cookie__Enabled=true' 'Mcp__Enabled=true' \
  'Advanced__MinSecondsBetweenReports=0' >"$ENV_FILE")
FAKES="${E2E_FAKES:-$([[ -z "${E2E_ENV:-}" ]] && echo 1 || echo 0)}"
FAKES_NAME="${E2E_NAME}-fakes"
if [[ "$FAKES" == "1" ]]; then
  printf '%s\n' "Redmine__Url=http://${FAKES_NAME}:7390/redmine" 'Redmine__ApiKey=test-redmine-key-0123456789' \
    'Redmine__Projects=it-support network' 'Redmine__PublicUrl=https://redmine.example.com' \
    "Zabbix__Url=http://${FAKES_NAME}:7390/zabbix" 'Zabbix__ApiToken=test-zabbix-token-0123456789abcdef' \
    'Zabbix__PublicUrl=https://zabbix.example.com' >>"$ENV_FILE"
fi
[[ -z "${E2E_ENV:-}" ]] || printf '%s\n' "$E2E_ENV" >>"$ENV_FILE"

cleanup() {
  if [[ "$KEEP" != "1" ]]; then
    "$ENGINE" rm -f "$FAKES_NAME" >/dev/null 2>&1 || true
    ./stop.sh >/dev/null 2>&1 || true
    "$ENGINE" volume rm -f "$PG_VOLUME" "$KEYS_VOLUME" "$DOWNLOADS_VOLUME" >/dev/null 2>&1 || true
    # The uploaded files: on Linux the app's user's (uid 1654), so they go from a container of the image.
    if [[ -d "$FILES_DIR" ]] && ! rm -rf "$FILES_DIR" 2>/dev/null; then
      "$ENGINE" run --rm --user 0 --entrypoint sh -v "$FILES_DIR:/files" "${IMAGE:-pnmtd:dev}" -c 'find /files -mindepth 1 -delete' >/dev/null 2>&1 || true
      rm -rf "$FILES_DIR"
    fi
    rm -f "$ENV_FILE"
  fi
}
trap cleanup EXIT

log="$(mktemp "$PWD/.tmpcheck/e2e.XXXXXX")"
echo "Starting ${NAME} on port ${PORT}…"
./start.sh >"$log" 2>&1 || { cat "$log" >&2; exit 1; }
if [[ "$FAKES" == "1" ]]; then
  "$ENGINE" rm -f "$FAKES_NAME" >/dev/null 2>&1 || true
  "$ENGINE" run -d --name "$FAKES_NAME" --network "$NETWORK" --entrypoint pnmtd "${IMAGE:-pnmtd:dev}" \
    dev fake-integrations --port 7390 --tls-port 7391 >"$log" 2>&1 || { cat "$log" >&2; exit 1; }
fi

user="e2e-$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
"$ENGINE" exec "$NAME" pnmtd users add "$user" --admin --display-name "E2E Admin" >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }
password="$(awk '/^    [A-Za-z0-9]+$/ {print $1}' "$log")"
[[ -n "$password" ]] || { cat "$log" >&2; echo "users add printed no temporary password" >&2; exit 1; }

# Another admin and a viewer with passwords of their own, for the page tests (no forced change).
admin_password="e2e-admin-$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
viewer_password="e2e-viewer-$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
echo "$admin_password" | "$ENGINE" exec -i "$NAME" pnmtd users add e2e-admin --admin --display-name "Ada Admin" --password-stdin >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }
echo "$viewer_password" | "$ENGINE" exec -i "$NAME" pnmtd users add e2e-viewer --groups Viewers --display-name "Vera Viewer" --password-stdin >"$log" 2>&1 \
  || { cat "$log" >&2; exit 1; }

# node_modules (with @playwright/test) are those of tools/npm.sh: installed if the lock changed.
tools/npm.sh exec -- playwright --version >/dev/null

# The fakes' TLS endpoints for the SSL certificate sensors (docs/ssl-certificate-plan.md §9), when they run.
fakes_env=()
if [[ "$FAKES" == "1" ]]; then
  fakes_env=(-e "E2E_FAKES_URL=http://${FAKES_NAME}:7390" -e "E2E_TLS_HOST=${FAKES_NAME}" -e E2E_TLS_PORT=7391)
fi

rm -rf PNMTD.WebUI/test-results
# In the app container's network: PNMT is http://localhost:8080 for the browser, a secure context as HTTPS is in
# production, which WebCrypto (the backups' decryption, docs/mikrotik-backup-plan.md §3.6) needs. UDP goes to the
# port the app listens on inside its container.
"$ENGINE" run --rm --network "container:${NAME}" --ipc=host \
  -v "$PWD/PNMTD.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -w /web \
  -e "E2E_BASE_URL=http://localhost:8080" -e E2E_UDP_PORT=5028 -e "E2E_USER=${user}" -e "E2E_PASSWORD=${password}" \
  -e "E2E_ADMIN_PASSWORD=${admin_password}" -e "E2E_VIEWER_PASSWORD=${viewer_password}" -e CI=1 ${fakes_env[@]+"${fakes_env[@]}"} \
  "$PLAYWRIGHT_IMAGE" node_modules/.bin/playwright test "$@"
