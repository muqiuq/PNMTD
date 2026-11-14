#!/usr/bin/env bash
# Put a backup folder back: here, or into a fresh deployment (after ./init.sh).
#
#   ./restore.sh backups/pnmtd-20261111-033000
#   ./restore.sh <folder> --yes      without asking
#   ./restore.sh <folder> --force    and replace what the files folder holds
#
# Everything since that backup is lost: the database is replaced, and so are the keys. PNMTD is
# stopped meanwhile; the sensors' reports in that time are lost too. The backup's files.tar goes into
# the files folder (PNMTD_FILES_DIR), as the app's user (uid 1654): into an empty folder, or with --force
# in place of its contents. A backup without files.tar (from before files, or --no-files) leaves the
# folder as it is.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

FOLDER="" YES=0 FORCE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) YES=1; shift ;;
    --force) FORCE=1; shift ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    -*) die "unknown option: $1 (try --help)" ;;
    *) [[ -z "$FOLDER" ]] || die "one folder, please"; FOLDER="$1"; shift ;;
  esac
done
[[ -n "$FOLDER" ]] || die "usage: ./restore.sh <backup folder>"
[[ -f "$FOLDER/database.pgdump" && -f "$FOLDER/keys.tar.gz" ]] || die "$FOLDER is not a backup folder (database.pgdump, keys.tar.gz)."

require_env
require_compose

# Before anything changes: the files folder must take the backup's files.
has_files=0
[[ -f "$FOLDER/files.tar" ]] && has_files=1
if (( has_files )); then
  [[ -d "$(files_dir)" ]] || ensure_files_dir || die "Could not create the files folder $(files_dir) for the app's user (uid ${APP_UID})."
  files_state() { in_app sh -c "test -w ${FILES_MOUNT} || echo unwritable; ls -A ${FILES_MOUNT} | grep -vx .tmp | head -1"; }
  state="$(files_state)"
  if [[ "$state" == unwritable* ]]; then
    ensure_files_dir || true
    state="$(files_state)"
  fi
  [[ "$state" != unwritable* ]] || die "The app's user (uid ${APP_UID}) cannot write $(files_dir): sudo chown ${APP_UID}:${APP_UID} $(files_dir) && sudo chmod 0750 $(files_dir)"
  [[ -z "$state" || "$FORCE" == 1 ]] || die "The files folder $(files_dir) is not empty: --force replaces its contents with the backup's. Nothing changed."
fi

if (( ! YES )); then
  echo "This replaces the database, the keys$( (( has_files )) && echo " and the files") with ${FOLDER}. Everything since then is lost."
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
if (( has_files )); then
  # As the app's user, through the service's own mount: what it extracts is its own.
  in_app sh -c "find ${FILES_MOUNT} -mindepth 1 -delete && tar -xf - --no-overwrite-dir -C ${FILES_MOUNT}" <"$FOLDER/files.tar"
else
  echo "No files.tar in ${FOLDER}: the files folder is left as it is."
fi
compose up -d "$SERVICE" >/dev/null
echo "Restored ${FOLDER}. PNMTD is starting; ./info.sh shows when it is healthy."
