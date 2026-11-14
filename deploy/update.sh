#!/usr/bin/env bash
# Back up, then pull the release and recreate the containers. The new release migrates the
# database as it starts — after the backup, which is the way back. A missing files folder (new in 1.3)
# is created for the app's user first.
#
#   ./update.sh                  the tag .env names (PNMTD_IMAGE_TAG)
#   ./update.sh --tag v1.0.1     pin another release first
#   ./update.sh --no-backup      without the backup
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

TAG="" BACKUP=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="${2:?--tag needs a release}"; shift 2 ;;
    --no-backup) BACKUP=0; shift ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done

require_env
require_compose
if [[ -n "$TAG" ]]; then
  [[ "$TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] || die "\"$TAG\" is not an image tag."
  env_set PNMTD_IMAGE_TAG "$TAG"
  echo "Pinned PNMTD_IMAGE_TAG=${TAG}"
fi
if (( BACKUP )); then
  ./backup.sh || die "The backup failed: nothing was updated."
fi
compose pull
# The uploaded files' folder (docs/locations-knowledge-plan.md §8.5), which installations from before 1.3 lack: made for
# the app's user before the containers start, since Docker would make a missing one as root, and PNMTD couldn't write it.
files="$(files_dir)"
if [[ ! -d "$files" ]]; then
  if ensure_files_dir; then
    echo "Created the files folder ${files} for the app's user (uid ${APP_UID}, 0750)."
  else
    echo "Could not create the files folder ${files} for the app's user; uploads fail until it exists:" >&2
    echo "  sudo install -d -o ${APP_UID} -g ${APP_UID} -m 0750 ${files} && docker compose up -d" >&2
  fi
fi
compose up -d --force-recreate --wait
./info.sh
