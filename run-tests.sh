#!/usr/bin/env bash
# Every check, in one command (docs/webui-migration-plan.md §8).
#
#   ./run-tests.sh                  everything
#   ./run-tests.sh --only web       one group: web, dotnet, contract, deploy, upgrade, e2e, shell (several: --only web,shell)
#   ./run-tests.sh --list           what it would run
#
# Groups:
#   web       the UI in a node container: the npm package check, npm ci, the API types against
#             openapi.json, vue-tsc, ESLint, Prettier, Vitest, the build
#   dotnet    restore (locked), build, PNMTD.Tests on a throwaway PostgreSQL (tools/test-db.sh),
#             in a zone east and one west of UTC
#   contract  the image from the working tree against the golden sensor contract (tools/contract.sh)
#   deploy    deploy/ for real: init, up, sign-in, the user scripts, a sensor event, backup, restore
#   upgrade   a v0.12.1 installation moved to 1.0 by deploy/upgrade-1.0.sh, a failed attempt rolled
#             back first (tools/upgrade-test.sh); the v0.12.1 image is built from its git tag once
#   e2e       the browser tests (Playwright) against a seeded stack of their own (tools/e2e.sh)
#   shell     bash -n and shellcheck (in a container) over every script
#
# dotnet runs with the .NET SDK on this machine; node never does: the web group copies PNMTD.WebUI
# into a node container (the repository stays read-only), with npm's cache in a volume.
# Podman by default, ENGINE=docker for Docker.
set -uo pipefail
cd "$(dirname "$0")" || exit 1

ENGINE="${ENGINE:-podman}"
ALL_GROUPS=(web dotnet contract deploy upgrade e2e shell)
NODE_IMAGE="${NODE_IMAGE:-docker.io/library/node:24-slim}"
NPM_CACHE="${NPM_CACHE:-pnmtd-npm-cache}"
WEB_HOLDER="pnmtd-test-web-$$"
ONLY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --only) ONLY="${2:?--only needs a group}"; shift 2 ;;
    --list) printf '%s\n' "${ALL_GROUPS[@]}"; exit 0 ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

selected=("${ALL_GROUPS[@]}")
if [[ -n "$ONLY" ]]; then
  IFS=',' read -r -a selected <<<"$ONLY"
fi

mkdir -p .tmpcheck
log_dir=$(mktemp -d "$PWD/.tmpcheck/run-tests.XXXXXX")
failures=()
trap '"$ENGINE" rm -f "$WEB_HOLDER" >/dev/null 2>&1 || true' EXIT

# step NAME COMMAND…: runs it with its output in a log file, shown only on failure.
step() {
  local name="$1"; shift
  local log start=$SECONDS
  log="$log_dir/$(echo "$name" | tr -c 'A-Za-z0-9' '_').log"
  printf '  %-58s' "$name"
  if "$@" >"$log" 2>&1; then
    printf 'ok  (%ss)\n' $(( SECONDS - start ))
  else
    printf 'FAILED  (%ss)\n' $(( SECONDS - start ))
    sed 's/^/      /' "$log" | tail -40
    annotate "$name" "$log"
    failures+=("$name")
  fi
}

# On GitHub Actions, a failed step's reason as an annotation on the run's page: from the FAIL: line
# the test scripts end with, else the log's last lines.
annotate() {
  [[ "${GITHUB_ACTIONS:-}" == "true" ]] || return 0
  local name="$1" log="$2" reason
  reason="$(sed -n '/^FAIL/,$p' "$log")"
  [[ -n "$reason" ]] || reason="$(tail -5 "$log")"
  reason="${reason//'%'/%25}"; reason="${reason//$'\r'/}"; reason="${reason//$'\n'/%0A}"
  name="${name//'%'/%25}"; name="${name//:/%3A}"; name="${name//,/%2C}"
  echo "::error title=${name}::${reason}"
}

# --- containers -------------------------------------------------------------------------------

in_node() { "$ENGINE" run --rm -v "$PWD":/w:ro -w /w "$NODE_IMAGE" "$@"; }

# PNMTD.WebUI copied into one container that lives for the run: npm ci once, then every check in
# the same node_modules, without touching the working tree.
web_holder() {
  "$ENGINE" container inspect "$WEB_HOLDER" >/dev/null 2>&1 && return 0
  "$ENGINE" run -d --rm --name "$WEB_HOLDER" -v "$PWD":/w:ro -v "${NPM_CACHE}":/root/.npm \
    -e NPM_CONFIG_UPDATE_NOTIFIER=false -e NPM_CONFIG_FUND=false -e NPM_CONFIG_AUDIT=false -e NO_COLOR=1 \
    "$NODE_IMAGE" sleep 3600 >/dev/null || return 1
  "$ENGINE" exec "$WEB_HOLDER" sh -c 'mkdir -p /tmp/web && cd /w/PNMTD.WebUI \
    && tar --exclude=node_modules --exclude=dist --exclude=test-results -cf - . | tar -C /tmp/web -xf -'
}
in_web() { web_holder && "$ENGINE" exec -w /tmp/web "$WEB_HOLDER" "$@"; }

group_web() {
  echo "web"
  step "npm packages: pinned, npmjs.org, open-source licences" in_node node tools/check-npm-packages.mjs
  step "npm ci (locked, no install scripts)" in_web npm ci --ignore-scripts --loglevel=error
  step "API types generated from openapi.json" in_web npm run --silent check:api
  step "typecheck (vue-tsc)" in_web npm run --silent typecheck
  step "lint (ESLint)" in_web npm run --silent lint
  step "format (Prettier)" in_web npm run --silent format:check
  step "unit tests (Vitest)" in_web npm test --silent
  step "build (Vite)" in_web npm run --silent build
}

group_upgrade() {
  echo "upgrade"
  step "image from the working tree" env ENGINE="$ENGINE" tools/contract.sh build
  step "v0.12.1 image (from its git tag, once)" bash -c \
    "'$ENGINE' image inspect pnmtd-contract:v0.12.1 >/dev/null 2>&1 || ENGINE='$ENGINE' tools/contract.sh baseline"
  step "v0.12.1 → 1.0: rollback, then the upgrade" tools/upgrade-test.sh
}

group_e2e() {
  echo "e2e"
  step "browser tests (Playwright) against the image" env ENGINE="$ENGINE" tools/e2e.sh
}

group_dotnet() {
  echo "dotnet"
  step "test database (tools/test-db.sh)" tools/test-db.sh start
  step "restore (locked)" dotnet restore PNMTD.slnx --locked-mode
  step "build" dotnet build PNMTD.slnx --no-restore -v q
  step "no pending model changes" dotnet ef migrations has-pending-model-changes --project PNMTD/PNMTD.csproj --no-build
  step "tests, Europe/Zurich" env PNMTD_TEST_TZ=Europe/Zurich dotnet test --project PNMTD.Tests/PNMTD.Tests.csproj --no-build
  step "tests, America/New_York" env PNMTD_TEST_TZ=America/New_York dotnet test --project PNMTD.Tests/PNMTD.Tests.csproj --no-build
}

group_contract() {
  echo "contract"
  step "image from the working tree" env ENGINE="$ENGINE" tools/contract.sh build
  step "sensor API = v0.12.1 (golden contract)" env ENGINE="$ENGINE" tools/contract.sh verify
}

group_deploy() {
  echo "deploy"
  step "image from the working tree" env ENGINE="$ENGINE" tools/contract.sh build
  step "init, up, sign-in, user scripts, backup, restore" tools/deploy-test.sh
}

group_shell() {
  echo "shell"
  local scripts=(start.sh stop.sh run-tests.sh tools/*.sh deploy/*.sh)
  step "bash -n" bash -c 'for f in "$@"; do bash -n "$f" || exit 1; done' _ "${scripts[@]}"
  step "shellcheck" "$ENGINE" run --rm -v "$PWD:/w:ro" -w /w docker.io/koalaman/shellcheck:stable \
    --severity=warning --external-sources "${scripts[@]}"
}

for group in "${selected[@]}"; do
  case "$group" in
    web|dotnet|contract|deploy|upgrade|e2e|shell) "group_$group" ;;
    *) echo "unknown group: $group (${ALL_GROUPS[*]})" >&2; exit 2 ;;
  esac
done

rm -rf "$log_dir"
echo
if (( ${#failures[@]} )); then
  echo "FAILED: ${failures[*]}"
  exit 1
fi
echo "All green."
