#!/usr/bin/env bash
# The one-time move from a v0.12 installation (UPGRADING-1.0.md): the backend and the WebApp
# containers with their SQLite database and users.json, to this stack on PostgreSQL.
#
#   ./upgrade-1.0.sh --legacy /srv/pnmt
#   ./upgrade-1.0.sh --legacy /srv/pnmt --source-time-zone Europe/Zurich --copy-settings
#
#   --legacy DIR              the old installation: its docker-compose.yml, pnmtd-data/ (pnmtd.db)
#                             and pnmtwebapp-data/ (users.json)
#   --source-time-zone ZONE   the zone v0.12 wrote its timestamps in (its host's, default UTC)
#   --copy-settings           copy the old containers' settings (Smtp__*, Mailbox__*, Identity, …)
#                             into .env, and the WebApp's externalApiurl as PublicBaseUrl
#   --backend NAME, --webapp NAME
#                             the old containers, when DIR has no docker-compose.yml
#   --yes                     do not ask before stopping the old stack
#
# Run ./init.sh first, and this before `docker compose up` (or on a 1.0 database without hosts).
# It stops the old containers without removing them, archives the old data to backups/, imports it
# (all or nothing), and starts 1.0 on the published port. If the import stops, the old containers are
# started again: nothing in DIR is ever changed. The way back: UPGRADING-1.0.md, "Rollback".
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

LEGACY="" ZONE="UTC" COPY=0 YES=0 BACKEND="" WEBAPP=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --legacy) LEGACY="${2:?--legacy needs the folder of the old installation}"; shift 2 ;;
    --source-time-zone) ZONE="${2:?--source-time-zone needs an IANA zone}"; shift 2 ;;
    --copy-settings) COPY=1; shift ;;
    --backend) BACKEND="${2:?--backend needs a container}"; shift 2 ;;
    --webapp) WEBAPP="${2:?--webapp needs a container}"; shift 2 ;;
    --yes) YES=1; shift ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done
[[ -n "$LEGACY" ]] || die "--legacy: the folder of the v0.12 installation (try --help)."

require_env
require_compose
LEGACY="$(cd "$LEGACY" 2>/dev/null && pwd)" || die "--legacy: no such folder."
db_file="$LEGACY/pnmtd-data/pnmtd.db"
users_file="$LEGACY/pnmtwebapp-data/users.json"
[[ -f "$db_file" ]] || die "$db_file is missing: --legacy names the folder with pnmtd-data/ and pnmtwebapp-data/."
[[ -f "$users_file" ]] || echo "No $users_file: only the monitoring data is imported; ./add-user.sh adds users."

stamp="$(date +%Y%m%d-%H%M%S)"
mkdir -p logs "$(backup_dir)"
log="logs/upgrade-1.0-${stamp}.log"
step() { echo; echo "== $*"; }

# --- 1. checks, before anything changes ------------------------------------------------------------
step "Checks"
if [[ -f "$LEGACY/docker-compose.yml" ]]; then
  # The old project is named after its folder: this stack's COMPOSE_PROJECT_NAME must not apply.
  legacy_ps() { (unset COMPOSE_PROJECT_NAME; compose -f "$LEGACY/docker-compose.yml" --project-directory "$LEGACY" ps -aq "$1" 2>/dev/null | head -1); }
  [[ -n "$BACKEND" ]] || BACKEND="$(legacy_ps pnmtd)"
  [[ -n "$WEBAPP" ]] || WEBAPP="$(legacy_ps pnmtwebapp)"
fi
[[ -n "$BACKEND" ]] || die "The old backend container was not found: start the old stack, or name it with --backend."
docker inspect "$BACKEND" >/dev/null 2>&1 || die "No container $BACKEND."
old_image="$(docker inspect -f '{{.Config.Image}}' "$BACKEND")"
echo "Old backend: $(docker inspect -f '{{.Name}}' "$BACKEND" | tr -d /) ($old_image)"
[[ -z "$WEBAPP" ]] || echo "Old WebApp:  $(docker inspect -f '{{.Name}}' "$WEBAPP" | tr -d /) ($(docker inspect -f '{{.Config.Image}}' "$WEBAPP"))"
case "$old_image" in
  *:v0.12*|*:0.12*) ;;
  *) echo "Note: $old_image is not tagged v0.12.x; the import checks the database's version itself." ;;
esac

# Space: the archive and PostgreSQL together take about three times the SQLite file.
need_kb=$(( $(du -k "$db_file" | cut -f1) * 3 + 102400 ))
free_kb="$(df -Pk . | awk 'NR==2 {print $4}')"
(( free_kb > need_kb )) || die "Not enough free space here: $(( free_kb / 1024 )) MB free, about $(( need_kb / 1024 )) MB needed."

# A fresh 1.0 database: no hosts. A server that already started has seeded its first admin, which the
# import replaces (--reset); a database with hosts is someone's data, and is refused.
compose up -d --wait db >/dev/null
compose run --rm --no-deps -T pnmtd db migrate >"$log" 2>&1 || { cat "$log" >&2; die "The 1.0 database could not be migrated."; }
hosts="$(compose exec -T db psql -U pnmtd -d pnmtd -tAc 'SELECT count(*) FROM hosts' 2>/dev/null || echo 0)"
[[ "$hosts" == "0" ]] || die "The 1.0 database has ${hosts} hosts already: the upgrade only fills a new deployment."
reset=()
users="$(compose exec -T db psql -U pnmtd -d pnmtd -tAc 'SELECT count(*) FROM users' 2>/dev/null || echo 0)"
[[ "$users" == "0" ]] || reset=(--reset)

# --- 2. settings ------------------------------------------------------------------------------------
# A value in single quotes: .env is read by compose, which would expand a $ in a password otherwise.
env_put() {
  local key="$1" value="$2"
  [[ "$value" != *"'"* && "$value" != *$'\n'* ]] || { echo "  $key: contains a quote or a line break; copy it by hand."; return 0; }
  ( umask 077; K="$key" V="'$value'" awk 'BEGIN { k = ENVIRON["K"]; v = ENVIRON["V"] }
      $0 ~ "^[[:space:]]*" k "=" { if (!done) print k "=" v; done = 1; next } { print } END { if (!done) print k "=" v }' .env >.env.tmp )
  chmod 600 .env.tmp && mv .env.tmp .env
}
if (( COPY )); then
  step "Settings from the old containers"
  copied=0
  while IFS= read -r line; do
    key="${line%%=*}" value="${line#*=}"
    case "$key" in
      Smtp__*|Pushover__*|Mailbox__*|UplinkCheck__*|Identity|Proxy|TZ|Development__*) ;;
      *) continue ;;
    esac
    [[ -n "$value" ]] || continue
    if [[ -n "$(env_value "$key")" ]]; then
      echo "  $key: kept (.env has it)"
    else
      env_put "$key" "$value"; echo "  $key: copied"; copied=$((copied + 1))
    fi
  done < <(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$BACKEND")
  if [[ -n "$WEBAPP" ]]; then
    url="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$WEBAPP" | sed -n 's/^externalApiurl=//p' | head -1)"
    if [[ -n "$url" && -z "$(env_value PublicBaseUrl)" ]]; then
      env_put PublicBaseUrl "${url%/}"; echo "  PublicBaseUrl: copied from the WebApp's externalApiurl"; copied=$((copied + 1))
    fi
  fi
  echo "  ${copied} copied. Not copied: Jwt__* and the WebApp's settings, which 1.0 no longer has."
fi

# --- 3. stop the old stack and archive its data -----------------------------------------------------
step "Pulling the 1.0 images (the old stack still runs)"
compose pull --quiet --ignore-pull-failures >/dev/null 2>&1 || true
docker image inspect "$(app_image)" >/dev/null 2>&1 || die "$(app_image) could not be pulled: nothing changed."
if (( ! YES )); then
  read -r -p "Stop the old containers now (sensors get connection errors until 1.0 answers)? [y/N] " reply
  [[ "$reply" == [yY] ]] || die "Nothing changed."
fi
step "Stopping the old stack (kept, not removed)"
old=("$BACKEND")
[[ -z "$WEBAPP" ]] || old+=("$WEBAPP")
docker stop "${old[@]}" >/dev/null
restart_old() {
  echo "Starting the old containers again…" >&2
  docker start "${old[@]}" >/dev/null || echo "Could not start them: docker start ${old[*]}" >&2
}
trap 'restart_old' ERR

archive="$(backup_dir)/upgrade-1.0-${stamp}.tar.gz"
step "Archiving the old data to ${archive}"
parts=(pnmtd-data)
[[ -d "$LEGACY/pnmtwebapp-data" ]] && parts+=(pnmtwebapp-data)
(umask 077 && docker run --rm --user 0:0 -v "$LEGACY:/legacy:ro" --entrypoint tar "$(app_image)" -czf - -C /legacy "${parts[@]}" >"$archive")

# --- 4. import ------------------------------------------------------------------------------------
step "Importing (one transaction: all of it, or nothing)"
mounts=(-v "$LEGACY/pnmtd-data:/legacy/pnmtd-data:ro")
args=(import-sqlite --from /legacy/pnmtd-data/pnmtd.db --source-time-zone "$ZONE" ${reset[@]+"${reset[@]}"})
if [[ -f "$users_file" ]]; then
  mounts+=(-v "$LEGACY/pnmtwebapp-data:/legacy/pnmtwebapp-data:ro")
  args+=(--users /legacy/pnmtwebapp-data/users.json)
fi
# As root inside the container (still without any capability): the old files belong to root.
if ! compose run --rm --no-deps -T --user 0:0 "${mounts[@]}" pnmtd "${args[@]}" >"$log" 2>&1; then
  grep -v '^info: \|^ *Executed DbCommand' "$log" >&2 || true
  trap - ERR
  restart_old
  die "The import stopped; the report above says why (also in ${log}). The old stack runs again, unchanged."
fi
grep -v '^info: \|^ *Executed DbCommand' "$log"

# --- 5. start 1.0 -------------------------------------------------------------------------------------
step "Starting 1.0"
compose up -d --wait
trap - ERR
./info.sh || true

port="$(env_value PNMTD_PORT)"
url="$(env_value PublicBaseUrl)"
cat <<EOF

The upgrade is done. Next (UPGRADING-1.0.md):
  - The sensors keep their URLs: 1.0 answers on port ${port:-5001} as v0.12 did.
  - Sign in at ${url:-http://<this host>:${port:-5001}}/pnmt-admin/ with your old password; 2FA is offered.
  - Redirect the old UI's address to /pnmt-admin/ on the backend's.
  - Rollback: docker compose down here, then docker compose up -d in ${LEGACY}.
  - The old containers are stopped, not removed; the old data is in ${archive}.
EOF
