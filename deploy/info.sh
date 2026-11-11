#!/usr/bin/env bash
# Is it running, what does it run, how is it reached — and what needs attention.
#
#   ./info.sh           the overview
#   ./info.sh --quiet   exit 0 when running and healthy, for monitoring
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

QUIET=0
[[ "${1:-}" == "--quiet" ]] && QUIET=1

require_env
require_compose

port="$(env_value PNMTD_PORT)"; port="${port:-5001}"
health="not running"
if is_running "$SERVICE"; then
  health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}running{{end}}' "$(container_id "$SERVICE")")"
fi
if (( QUIET )); then
  [[ "$health" == "healthy" || "$health" == "running" ]] && curl -fsS -o /dev/null "http://127.0.0.1:${port}/healthz"
  exit $?
fi

image="$(env_value PNMTD_IMAGE)"; image="${image:-uisach/pnmtd}"
tag="$(env_value PNMTD_IMAGE_TAG)"; tag="${tag:-v1.0.0}"
url="$(env_value PublicBaseUrl)"
echo "PNMTD"
echo "  Image:      ${image}:${tag}"
echo "  State:      ${health}"
echo "  Health:     $(curl -fsS "http://127.0.0.1:${port}/healthz" 2>/dev/null || echo 'no answer')"
echo "  Published:  $(env_value PNMTD_BIND_ADDRESS):${port}"
echo "  Sensors:    ${url:-http://<this host>:${port}}/event/<token>/<code>/<message>"
echo "  Backups:    $(ls -1d "$(env_value PNMTD_BACKUP_DIR || echo backups)"/pnmtd-* 2>/dev/null | wc -l | tr -d ' ') in $(env_value PNMTD_BACKUP_DIR)"

warnings=()
[[ -n "$url" ]] || warnings+=("PublicBaseUrl is empty: the UI shows sensor URLs with whatever address the browser used.")
[[ "$url" == https://* || -z "$url" ]] || warnings+=("PublicBaseUrl is plain HTTP: passwords and session cookies travel unencrypted.")
[[ -n "$(env_value Proxy)" || "$(env_value PNMTD_BIND_ADDRESS)" != "127.0.0.1" ]] || warnings+=("Bound to 127.0.0.1 (a reverse proxy?) but Proxy is empty: lockout and the audit log see the proxy's address.")
ls -1d "$(env_value PNMTD_BACKUP_DIR || echo backups)"/pnmtd-* >/dev/null 2>&1 || warnings+=("No backup yet: ./backup.sh, and a cron line (README.md).")
if is_running "$DB_SERVICE"; then
  # Who could manage users with a password alone (§4.6).
  admins="$(compose exec -T "$DB_SERVICE" psql -U pnmtd -d pnmtd -tAc \
    "SELECT string_agg(\"Username\", ', ' ORDER BY \"NormalizedUsername\") FROM users WHERE \"Role\" = 'Admin' AND \"IsActive\" AND NOT \"IsTotpEnabled\"" \
    2>/dev/null || true)"
  if [[ -n "$admins" ]]; then
    case "$(env_value Auth__TotpPolicy | tr '[:upper:]' '[:lower:]')" in
      required|requiredforadmins) warnings+=("Admins without 2FA yet: ${admins}. Auth__TotpPolicy makes them set it up before anything else.") ;;
      *) warnings+=("Admins without 2FA: ${admins}. They can set it up under their account; Auth__TotpPolicy=RequiredForAdmins makes them.") ;;
    esac
  fi
fi
if (( ${#warnings[@]} )); then
  echo
  echo "Warnings"
  printf '  - %s\n' "${warnings[@]}"
fi
