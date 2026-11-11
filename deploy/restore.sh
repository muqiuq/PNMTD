#!/usr/bin/env bash
# Put a backup folder back: here, or into a fresh deployment (after ./init.sh).
#
#   ./restore.sh backups/pnmtd-20261111-033000
#   ./restore.sh <folder> --yes      without asking
#
# Everything since that backup is lost: the database is replaced, and so are the keys. PNMTD is
# stopped meanwhile; the sensors' reports in that time are lost too.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

FOLDER="" YES=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) YES=1; shift ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    -*) die "unknown option: $1 (try --help)" ;;
    *) [[ -z "$FOLDER" ]] || die "one folder, please"; FOLDER="$1"; shift ;;
  esac
done
[[ -n "$FOLDER" ]] || die "usage: ./restore.sh <backup folder>"
[[ -f "$FOLDER/database.pgdump" && -f "$FOLDER/keys.tar.gz" ]] || die "$FOLDER is not a backup folder (database.pgdump, keys.tar.gz)."

require_env
require_compose

if (( ! YES )); then
  echo "This replaces the database and the keys with ${FOLDER}. Everything since then is lost."
  read -r -p "Type restore to go on: " reply
  [[ "$reply" == "restore" ]] || die "Nothing changed."
fi

compose stop "$SERVICE" >/dev/null 2>&1 || true
is_running "$DB_SERVICE" || compose up -d --wait "$DB_SERVICE" >/dev/null
compose exec -T "$DB_SERVICE" dropdb -U pnmtd --if-exists --force pnmtd
compose exec -T "$DB_SERVICE" createdb -U pnmtd pnmtd
compose exec -T "$DB_SERVICE" pg_restore -U pnmtd -d pnmtd --no-owner --exit-on-error <"$FOLDER/database.pgdump"
# As root, without the service's cap_drop: the keys go back to the app's user.
docker run --rm -i -v "$(volume_name pnmtd-keys):/keys" --user 0 --entrypoint sh "$(app_image)" \
  -c 'find /keys -mindepth 1 -delete && tar -xzf - -C / && chown -R app:app /keys' <"$FOLDER/keys.tar.gz"
compose up -d "$SERVICE" >/dev/null
echo "Restored ${FOLDER}. PNMTD is starting; ./info.sh shows when it is healthy."
