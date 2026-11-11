#!/usr/bin/env bash
# A full backup: a folder named after the moment it was taken.
#
#   ./backup.sh                       -> backups/pnmtd-20261111-033000/
#   ./backup.sh --out /srv/pnmtd-bak  another destination
#   ./backup.sh --keep 14             then delete all but the newest 14 backup folders
#   ./backup.sh --quiet               only complain, for cron
#
# The folder holds database.pgdump (pg_dump --format=custom) and keys.tar.gz (the Data Protection
# keys: without them, sessions end and the 2FA secrets cannot be read). Folder 0700, files 0600: it
# holds password hashes. It does not hold .env — keep a copy of that elsewhere.
#
# Cron, daily at 03:30:
#   30 3 * * * /srv/pnmtd/deploy/backup.sh --quiet --keep 14 >>/srv/pnmtd/deploy/logs/backup.log 2>&1
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

OUT="" KEEP="" QUIET=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out)   OUT="${2:?--out needs a folder}"; shift 2 ;;
    --keep)  KEEP="${2:?--keep needs a number}"; shift 2 ;;
    --quiet) QUIET=1; shift ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done
[[ -z "$KEEP" || "$KEEP" =~ ^[1-9][0-9]*$ ]] || die "--keep: a number of backups, 1 or more."

require_env
require_compose
OUT="${OUT:-$(backup_dir)}"
KEEP="${KEEP:-$(env_value PNMTD_BACKUP_KEEP)}"

is_running "$DB_SERVICE" || compose up -d --wait "$DB_SERVICE" >/dev/null
stamp="$(date +%Y%m%d-%H%M%S)"
folder="${OUT%/}/pnmtd-${stamp}"
(umask 077 && mkdir -p "$folder")
chmod 700 "$folder"

compose exec -T "$DB_SERVICE" pg_dump -U pnmtd -d pnmtd --format=custom >"$folder/database.pgdump.part"
mv "$folder/database.pgdump.part" "$folder/database.pgdump"
docker run --rm -v "$(volume_name pnmtd-keys):/keys:ro" --entrypoint tar "$(app_image)" -czf - -C / keys >"$folder/keys.tar.gz"
chmod 600 "$folder"/*
say "Backup written: ${folder} ($(du -sh "$folder" | cut -f1))"

if [[ -n "$KEEP" ]]; then
  # Folders named pnmtd-<stamp> sort by time; only those are ever deleted.
  total=$(ls -1d "${OUT%/}"/pnmtd-[0-9]*-[0-9]* 2>/dev/null | wc -l | tr -d ' ')
  if (( total > KEEP )); then
    ls -1d "${OUT%/}"/pnmtd-[0-9]*-[0-9]* | sort | head -n "$(( total - KEEP ))" |
      while IFS= read -r dir; do
        rm -rf -- "$dir"
        say "Deleted old backup: ${dir}"
      done
  fi
fi
