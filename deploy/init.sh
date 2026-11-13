#!/usr/bin/env bash
# Write a ready-to-run .env: every setting of .env.example, with generated secrets.
#
#   ./init.sh                                  a new installation
#   ./init.sh --url https://pnmt.example.com   …with the address sensors use (PublicBaseUrl)
#   ./init.sh --force                          write it again; the database password is kept
#   ./init.sh --wireguard [--wg-external-host vpn.example.com] [--wg-external-port 51999]
#                                              …with the WireGuard gateway for MikroTik routers, and what
#                                              routers dial when this host is behind NAT
#
# The secrets exist only in .env, and Auth__InitialAdmin__Password is read exactly once — when the
# database has no users yet. So an existing .env is never replaced without --force, and --force keeps
# PNMTD_DB_PASSWORD: the database keeps the password it was created with.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

URL="" FORCE=0 WIREGUARD=0 WG_HOST="" WG_PORT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --url)   URL="${2:?--url needs an address}"; shift 2 ;;
    --force) FORCE=1; shift ;;
    --wireguard) WIREGUARD=1; shift ;;
    --wg-external-host) WG_HOST="${2:?--wg-external-host needs a name}"; WIREGUARD=1; shift 2 ;;
    --wg-external-port) WG_PORT="${2:?--wg-external-port needs a port}"; WIREGUARD=1; shift 2 ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done
[[ -z "$URL" || "$URL" =~ ^https?://[^/[:space:]]+$ ]] || die "--url: an origin such as https://pnmt.example.com, without a path."

db_password=""
if [[ -f .env ]]; then
  (( FORCE )) || die ".env exists. ./init.sh --force writes it again (the database password is kept)."
  db_password="$(env_value PNMTD_DB_PASSWORD)"
fi
[[ -n "$db_password" ]] || db_password="$(random_hex 24)"
admin_password="$(random_hex 10)"

(umask 077 && cp .env.example .env.tmp)
mv .env.tmp .env
env_set PNMTD_DB_PASSWORD "$db_password"
env_set Auth__InitialAdmin__Password "$admin_password"
[[ -z "$URL" ]] || env_set PublicBaseUrl "$URL"
if (( WIREGUARD )); then
  [[ -z "$WG_PORT" || "$WG_PORT" =~ ^[0-9]{1,5}$ ]] || die "--wg-external-port: a port number."
  env_set WireGuard__Enabled true
  env_set COMPOSE_PROFILES wireguard
  [[ -z "$WG_HOST" ]] || env_set PNMTD_WG_EXTERNAL_HOST "$WG_HOST"
  [[ -z "$WG_PORT" ]] || env_set PNMTD_WG_EXTERNAL_PORT "$WG_PORT"
fi
mkdir -p logs backups

echo "Wrote .env (0600). Start the stack with: docker compose up -d"
echo "The first admin, created at the first start (change the password at the first sign-in):"
show_secret "admin" "$admin_password"
echo "It is in .env as Auth__InitialAdmin__Password, read only while the database has no users."
