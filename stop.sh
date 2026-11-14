#!/usr/bin/env bash
# Stop and remove whatever ./start.sh started: the app, its PostgreSQL and their network. The data
# lives in named volumes and the files folder and is kept, unless --data is given; then each is asked about.
set -euo pipefail
cd "$(dirname "$0")"

# The same names, and overrides, as start.sh.
ENGINE="${ENGINE:-podman}"
NAME="${NAME:-pnmtd}"
DB_NAME="${DB_NAME:-pnmtd-db}"
WEB_NAME="${WEB_NAME:-${NAME}-web}"
WG_NAME="${WG_NAME:-${NAME}-wg}"
NETWORK="${NETWORK:-pnmtd-dev}"
PG_VOLUME="${PG_VOLUME:-pnmtd-pgdata}"
KEYS_VOLUME="${KEYS_VOLUME:-pnmtd-keys-dev}"
DOWNLOADS_VOLUME="${DOWNLOADS_VOLUME:-pnmtd-downloads-dev}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pnmtd-web-node-modules}"
FILES_DIR="${FILES_DIR:-.pnmtd-files}"
IMAGE="${IMAGE:-pnmtd:dev}"

remove_data=0
for arg in "$@"; do
  case "$arg" in
    --data) remove_data=1 ;;
    -h|--help)
      echo "Usage: $0 [--data]"
      echo "  --data   also offer to delete the volumes, one question each:"
      echo "           ${PG_VOLUME} (the database), ${KEYS_VOLUME} (sessions and 2FA secrets),"
      echo "           ${DOWNLOADS_VOLUME} (the agents' files), ${WEB_MODULES_VOLUME} (the web UI's node_modules),"
      echo "           and the folder ${FILES_DIR} (the uploaded files)"
      exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 1 ;;
  esac
done

container_exists() { [[ -n "$("$ENGINE" ps -aq --filter "name=^$1$" 2>/dev/null)" ]]; }

remove_container() {
  if container_exists "$1"; then
    "$ENGINE" rm -f -t 30 "$1" >/dev/null
    echo "Stopped and removed container $1."
  elif [[ "${2:-}" != "quiet" ]]; then
    echo "Container $1 is not running."
  fi
}

offer_volume() {
  local volume="$1" what="$2"
  "$ENGINE" volume inspect "$volume" >/dev/null 2>&1 || return 0
  if (( remove_data )); then
    read -r -p "Delete volume ${volume}, ${what}? [y/N] " reply
    if [[ "$reply" == [yY] ]]; then
      "$ENGINE" volume rm "$volume" >/dev/null && echo "Removed volume ${volume}."
    else
      echo "Kept volume ${volume}."
    fi
  else
    echo "Volume ${volume} kept — run '$0 --data' to delete it."
  fi
}

# The files folder: on Linux its contents are the app's user's (uid 1654), so they go from a container of the image.
offer_folder() {
  local folder="$1" what="$2"
  [[ -d "$folder" ]] || return 0
  if (( remove_data )); then
    read -r -p "Delete folder ${folder}, ${what}? [y/N] " reply
    if [[ "$reply" == [yY] ]]; then
      if ! rm -rf -- "$folder" 2>/dev/null; then
        "$ENGINE" run --rm --user 0 --entrypoint sh -v "$(cd "$folder" && pwd):/files" "$IMAGE" -c 'find /files -mindepth 1 -delete' >/dev/null
        rm -rf -- "$folder"
      fi
      echo "Removed folder ${folder}."
    else
      echo "Kept folder ${folder}."
    fi
  else
    echo "Folder ${folder} kept — run '$0 --data' to delete it."
  fi
}

# The app before its database, so it does not log a lost connection on the way out.
remove_container "$WEB_NAME" quiet
remove_container "$WG_NAME" quiet
remove_container "$NAME"
remove_container "$DB_NAME"
"$ENGINE" network rm "$NETWORK" >/dev/null 2>&1 || true

# Only now: a volume in use cannot go.
offer_volume "$PG_VOLUME" "the database (a LEGACY_DIR import can fill it again)"
offer_volume "$KEYS_VOLUME" "the Data Protection keys (sessions end, 2FA must be set up again)"
offer_volume "$DOWNLOADS_VOLUME" "the agents' files (fetched again from GitHub)"
offer_volume "$WEB_MODULES_VOLUME" "the web UI's node_modules (npm ci fills it again)"
offer_folder "$FILES_DIR" "the uploaded images and attachments"
