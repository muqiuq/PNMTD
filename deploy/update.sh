#!/usr/bin/env bash
# Back up, then pull the release and recreate the containers. The new release migrates the
# database as it starts — after the backup, which is the way back.
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
compose up -d --force-recreate --wait
./info.sh
