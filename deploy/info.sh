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
tag="$(env_value PNMTD_IMAGE_TAG)"; tag="${tag:-v1.8.1}"
url="$(env_value PublicBaseUrl)"
echo "PNMTD"
echo "  Image:      ${image}:${tag}"
echo "  State:      ${health}"
echo "  Health:     $(curl -fsS "http://127.0.0.1:${port}/healthz" 2>/dev/null || echo 'no answer')"
echo "  Published:  $(env_value PNMTD_BIND_ADDRESS):${port}"
echo "  Sensors:    ${url:-http://<this host>:${port}}/event/<token>/<code>/<message>"
udp_port="$(env_value PNMTD_UDP_PORT)"; udp_port="${udp_port:-5028}"
case "$(env_value Udp__Enabled | tr '[:upper:]' '[:lower:]')" in
  false|0) echo "  UDP nodes:  off (Udp__Enabled=false)" ;;
  *) udp_bind="$(env_value PNMTD_UDP_BIND_ADDRESS)"
     udp_host="${url#*://}"; udp_host="${udp_host%%/*}"; udp_host="${udp_host%%:*}"
     echo "  UDP nodes:  ${udp_bind:-0.0.0.0}:${udp_port}/udp (devices send to ${udp_host:-<this host>}:${udp_port})" ;;
esac
case "$(env_value WireGuard__Enabled | tr '[:upper:]' '[:lower:]')" in
  true|1)
    wg_port="$(env_value PNMTD_WG_PORT)"; wg_port="${wg_port:-51820}"
    wg_host="$(env_value PNMTD_WG_EXTERNAL_HOST)"; wg_ext_port="$(env_value PNMTD_WG_EXTERNAL_PORT)"
    wg_url_host="${url#*://}"; wg_url_host="${wg_url_host%%/*}"; wg_url_host="${wg_url_host%%:*}"
    wg_state="$(is_running pnmtd-wg && echo running || echo 'not running (COMPOSE_PROFILES=wireguard?)')"
    echo "  WireGuard:  gateway ${wg_state}, ${wg_port}/udp; routers dial ${wg_host:-${wg_url_host:-<set PNMTD_WG_EXTERNAL_HOST>}}:${wg_ext_port:-$wg_port}" ;;
  *) echo "  WireGuard:  off (MikroTik routers by Direct SSH only)" ;;
esac
# Redmine and Zabbix (docs/audit-redmine-zabbix-plan.md §8.4): on or off, and where; never the key or the token.
redmine_url="$(env_value Redmine__Url)"
if [[ -n "$redmine_url" && -n "$(env_value Redmine__ApiKey)" && -n "$(env_value Redmine__Projects)" ]]; then
  echo "  Redmine:    ${redmine_url}, projects $(env_value Redmine__Projects)"
else
  echo "  Redmine:    off"
fi
zabbix_url="$(env_value Zabbix__Url)"
if [[ -n "$zabbix_url" && -n "$(env_value Zabbix__ApiToken)" ]]; then
  zabbix_groups="$(env_value Zabbix__HostGroups)"
  echo "  Zabbix:     ${zabbix_url}, ${zabbix_groups:-all host groups the token sees}"
else
  echo "  Zabbix:     off"
fi
# Cookie (docs/cookie-plan.md §19): available or off, and whether it may write to Redmine; never a key.
if [[ "$(env_value Cookie__Enabled)" == "true" ]]; then
  cookie_write="$([[ -n "$(env_value Redmine__WriteApiKey)" ]] && echo "writes tickets and notes" || echo "no Redmine__WriteApiKey: no tickets or notes")"
  cookie_privacy="$(env_value Cookie__Privacy)"
  echo "  Cookie:     available (started on Admin → Cookie), privacy ${cookie_privacy:-Code}, ${cookie_write}"
else
  echo "  Cookie:     off"
fi
# MCP (docs/mcp-plan.md): on or off; never a token.
if [[ "$(env_value Mcp__Enabled)" == "true" ]]; then
  mcp_hours="$(env_value Mcp__WriteHours)"
  echo "  MCP:        on at /mcp (read & write tokens for ${mcp_hours:-12} hours)"
else
  echo "  MCP:        off"
fi
echo "  Backups:    $(ls -1d "$(env_value PNMTD_BACKUP_DIR || echo backups)"/pnmtd-* 2>/dev/null | wc -l | tr -d ' ') in $(env_value PNMTD_BACKUP_DIR)"
files="$(files_dir)"
files_size=""
# Measured as the app's user, who alone may read it; never a mount of a missing folder, which Docker would create as root.
[[ -d "$files" ]] && files_size="$(in_app du -sh "$FILES_MOUNT" 2>/dev/null | cut -f1 || true)"
echo "  Files:      ${files}${files_size:+ (${files_size})}"

warnings=()
if is_running "$SERVICE" && ! curl -fsS -o /dev/null "http://127.0.0.1:${port}/healthz" 2>/dev/null; then
  warnings+=("The app runs but does not answer /healthz: is the database up? docker compose logs --tail 50 pnmtd db")
fi
[[ -n "$url" ]] || warnings+=("PublicBaseUrl is empty: the UI shows sensor URLs with whatever address the browser used.")
[[ "$url" == https://* || -z "$url" ]] || warnings+=("PublicBaseUrl is plain HTTP: passwords and session cookies travel unencrypted.")
[[ -n "$(env_value Proxy)" || "$(env_value PNMTD_BIND_ADDRESS)" != "127.0.0.1" ]] || warnings+=("Bound to 127.0.0.1 (a reverse proxy?) but Proxy is empty: lockout and the audit log see the proxy's address.")
ls -1d "$(env_value PNMTD_BACKUP_DIR || echo backups)"/pnmtd-* >/dev/null 2>&1 || warnings+=("No backup yet: ./backup.sh, and a cron line (README.md).")
# The uploaded files' folder (docs/locations-knowledge-plan.md §8.5): there, and writable by the app's user, as PNMTD sees it.
if [[ ! -d "$files" ]]; then
  warnings+=("The files folder ${files} is missing: uploads fail. Create it for the app's user, then start again: sudo install -d -o ${APP_UID} -g ${APP_UID} -m 0750 ${files} && docker compose up -d (./update.sh does both).")
elif is_running "$SERVICE" && ! compose exec -T "$SERVICE" sh -c "grep -qs ' ${FILES_MOUNT} ' /proc/mounts" >/dev/null 2>&1; then
  warnings+=("The app runs without the files folder mounted: uploads fail. Its docker-compose.yml is older than 1.3? git pull in this folder, then ./update.sh.")
elif is_running "$SERVICE" && ! compose exec -T "$SERVICE" sh -c "test -w ${FILES_MOUNT}" >/dev/null 2>&1; then
  warnings+=("PNMTD (uid ${APP_UID}) cannot write the files folder ${files}: sudo chown ${APP_UID}:${APP_UID} ${files} && sudo chmod 0750 ${files} (rootless Podman: podman unshare chown ${APP_UID}:${APP_UID} ${files}; SELinux: README.md).")
fi
if is_running "$DB_SERVICE"; then
  # Who could manage users with a password alone (§4.6).
  admins="$(compose exec -T "$DB_SERVICE" psql -U pnmtd -d pnmtd -tAc \
    "SELECT string_agg(\"Username\", ', ' ORDER BY \"NormalizedUsername\") FROM users WHERE \"IsAdmin\" AND \"IsActive\" AND NOT \"IsTotpEnabled\"" \
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
