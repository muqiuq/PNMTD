#!/usr/bin/env bash
# Every check, in one command (docs/webui-migration-plan.md §8).
#
#   ./run-tests.sh                  everything
#   ./run-tests.sh --only dotnet    one group: dotnet, contract, deploy, shell (several: --only dotnet,shell)
#   ./run-tests.sh --list           what it would run
#
# Groups:
#   dotnet    restore (locked), build, PNMTD.Tests on a throwaway PostgreSQL (tools/test-db.sh),
#             in a zone east and one west of UTC
#   contract  the image from the working tree against the golden sensor contract (tools/contract.sh)
#   deploy    deploy/ for real: init, up, a sensor event, backup, another event, restore, check
#   shell     bash -n and shellcheck (in a container) over every script
#
# dotnet runs with the .NET SDK on this machine; node never does (phase 5 adds a web group that
# runs in a container). Podman by default, ENGINE=docker for Docker.
set -uo pipefail
cd "$(dirname "$0")" || exit 1

ENGINE="${ENGINE:-podman}"
ALL_GROUPS=(dotnet contract deploy shell)
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
    failures+=("$name")
  fi
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
  step "init, up, event, backup, event, restore" tools/deploy-test.sh
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
    dotnet|contract|deploy|shell) "group_$group" ;;
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
