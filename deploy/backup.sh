#!/usr/bin/env bash
# A full backup: a folder named after the moment it was taken.
#
#   ./backup.sh                       -> backups/pnmtd-20261111-033000/
#   ./backup.sh --out /srv/pnmtd-bak  another destination
#   ./backup.sh --keep 14             then delete all but the newest 14 backup folders
#   ./backup.sh --quiet               only complain, for cron
#   ./backup.sh --no-files            without the uploaded files, for a host that backs up their folder itself
#
# The folder holds database.pgdump (pg_dump --format=custom), keys.tar.gz (the Data Protection keys:
# without them, sessions end and the 2FA secrets cannot be read) and files.tar (the uploaded files'
# folder, PNMTD_FILES_DIR; uncompressed, as images and documents are compressed already). The files come
# after the database, so uploads in between are only extra files, which a restore lists as unknown. A
# file can also leave while the backup runs: the daily cleanup removes files marked deleted 7 days
# before, an Admin's delete at once. The dump may name it while the tar lacks it; after a restore its row
# is marked deleted (the cleanup's, expected and removed in time) or, rarely, an Admin's delete of those
# minutes shows as a missing file until it is deleted again. Folder 0700, files 0600: it holds password
# hashes. It does not hold .env — keep a copy of that elsewhere.
#
# Cron, daily at 03:30:
#   30 3 * * * /srv/pnmtd/deploy/backup.sh --quiet --keep 14 >>/srv/pnmtd/deploy/logs/backup.log 2>&1
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

OUT="" KEEP="" QUIET=0 FILES=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out)   OUT="${2:?--out needs a folder}"; shift 2 ;;
    --keep)  KEEP="${2:?--keep needs a number}"; shift 2 ;;
    --quiet) QUIET=1; shift ;;
    --no-files) FILES=0; shift ;;
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
if (( FILES )); then
  # The folder on this host, and the app's mount of it: a release from before 1.3, still running while ./update.sh backs
  # up, has no files to take.
  if [[ -d "$(files_dir)" ]] && in_app test -d "$FILES_MOUNT" >/dev/null 2>&1; then
    # As the app's user, who owns the folder (0750), through the service's own mount; .tmp/ holds uploads in progress only.
    # A file the cleanup or an Admin removes while tar walks the folder is no failure: GNU tar exits 1 for it (and its
    # folder "changed as we read it"), 2 only for a real one. Files are never changed otherwise; the check would see it.
    status=0
    in_app tar --warning=no-file-removed --warning=no-file-changed -cf - --exclude=./.tmp -C "$FILES_MOUNT" . \
      >"$folder/files.tar.part" || status=$?
    (( status <= 1 )) || die "files.tar failed (tar exited ${status}); the database and the keys are in ${folder}."
    mv "$folder/files.tar.part" "$folder/files.tar"
  else
    say "No files folder at $(files_dir): files.tar left out."
  fi
fi
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
