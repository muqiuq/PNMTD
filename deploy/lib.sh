#!/usr/bin/env bash
# Shared helpers for the deploy scripts. Sourced, never run.
#
#   . "$(dirname "$0")/lib.sh"
#
# Every script works the same way: read .env without sourcing it, find the app container, and run
# the `pnmtd` command line in it — in the running app, or in a one-shot container over the same volumes.

SERVICE=pnmtd
DB_SERVICE=db

say()  { (( ${QUIET:-0} )) || echo "$@"; }
die()  { echo "$@" >&2; exit 1; }

# Reading .env by sourcing it would execute a backtick or $( in a generated password.
env_value() {
  [[ -f .env ]] || return 0
  sed -n "s/^[[:space:]]*$1=//p" .env | tail -1 | sed -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'$/\1/"
}

# NAME=VALUE in .env: replaced where it is, else appended. Through a 0600 temporary file, so .env is
# never half written. VALUE is hex, a tag or a URL: never a |, & or backslash.
env_set() {
  (
    umask 077
    if grep -q "^[[:space:]]*$1=" .env; then
      sed "s|^[[:space:]]*$1=.*|$1=$2|" .env >.env.tmp
    else
      cat .env >.env.tmp
      [[ -z "$(tail -c1 .env)" ]] || echo >>.env.tmp
      printf '%s=%s\n' "$1" "$2" >>.env.tmp
    fi
  ) || return 1
  chmod 600 .env.tmp
  mv .env.tmp .env
}

# Hex, from the kernel's generator; a fixed read, since tr on an endless stream ends in SIGPIPE.
random_hex() { od -An -N"${1:-16}" -tx1 /dev/urandom | tr -d ' \n'; }

require_env() { [[ -f .env ]] || die "deploy/.env is missing — run ./init.sh first."; }

# `docker compose` (the plugin), or the standalone docker-compose.
compose() {
  if docker compose version >/dev/null 2>&1; then docker compose "$@"; else docker-compose "$@"; fi
}

require_compose() {
  command -v docker >/dev/null 2>&1 || die "docker is not installed."
  docker compose version >/dev/null 2>&1 || command -v docker-compose >/dev/null 2>&1 || die "docker compose is not available."
}

container_id() { compose ps -q "$1" 2>/dev/null; }

is_running() {
  local id
  id="$(container_id "${1:-$SERVICE}")"
  [[ -n "$id" ]] && [[ "$(docker inspect -f '{{.State.Running}}' "$id" 2>/dev/null)" == "true" ]]
}

# The `pnmtd` command line: in the running app, or in a one-shot container beside the database.
pnmtd_cmd() {
  if is_running "$SERVICE"; then
    compose exec -T "$SERVICE" pnmtd "$@"
  else
    is_running "$DB_SERVICE" || compose up -d --wait "$DB_SERVICE" >/dev/null
    compose run --rm --no-deps -T "$SERVICE" "$@"
  fi
}

# The value of KEY in KEY=VALUE lines on standard input (a command's machine-readable output).
field() { sed -n "s/^$1=//p" | head -1; }

# A secret on its own line, easy to copy, and never in a log file.
show_secret() {
  echo
  echo "    $1: $2"
  echo
}

# The compose project: COMPOSE_PROJECT_NAME from the environment or .env, else pnmtd (docker-compose.yml's
# name). A volume X of the stack is <project>_X, pnmtd_pnmtd-keys.
project_name() {
  local name="${COMPOSE_PROJECT_NAME:-}"
  [[ -n "$name" ]] || name="$(env_value COMPOSE_PROJECT_NAME)"
  echo "${name:-pnmtd}"
}

volume_name() { echo "$(project_name)_$1"; }

# As docker-compose.yml reads it: the environment first, then .env.
app_image() {
  local image="${PNMTD_IMAGE:-}" tag="${PNMTD_IMAGE_TAG:-}"
  [[ -n "$image" ]] || image="$(env_value PNMTD_IMAGE)"
  [[ -n "$tag" ]] || tag="$(env_value PNMTD_IMAGE_TAG)"
  echo "${image:-uisach/pnmtd}:${tag:-v1.14.0}"
}

# The uploaded files' folder (docs/locations-knowledge-plan.md §8.5): a folder on this host, mounted at FILES_MOUNT. Like
# docker-compose.yml: PNMTD_FILES_DIR from the environment, then .env, else ./files.
# shellcheck disable=SC2034 # used by the scripts that source this file
FILES_MOUNT=/var/lib/pnmtd/files
APP_UID=1654

files_dir() {
  local dir="${PNMTD_FILES_DIR:-}"
  [[ -n "$dir" ]] || dir="$(env_value PNMTD_FILES_DIR)"
  echo "${dir:-./files}"
}

# The folder, created and given to the app's user (uid 1654, 0750): directly as root, else as root in a one-shot container,
# which maps the uid as the app's container sees it (rootless Podman too). Fails when neither can.
ensure_files_dir() {
  local dir abs
  dir="$(files_dir)"
  mkdir -p "$dir" || return 1
  abs="$(cd "$dir" && pwd)"
  if [[ "$(id -u)" == "0" ]]; then
    chown "${APP_UID}:${APP_UID}" "$abs" && chmod 0750 "$abs"
  else
    docker run --rm --user 0 --entrypoint sh -v "${abs}:/files" "$(app_image)" \
      -c "chown ${APP_UID}:${APP_UID} /files && chmod 0750 /files" >/dev/null 2>&1
  fi
}

# A command as the app's user in its container, with the service's own mount of the files folder: in the running app, or
# in a one-shot container beside nothing. Its user owns the folder, which nobody else may read.
in_app() {
  if is_running "$SERVICE"; then
    compose exec -T "$SERVICE" "$@"
  else
    compose run --rm --no-deps -T --entrypoint "$1" "$SERVICE" "${@:2}"
  fi
}

backup_dir() {
  local dir
  dir="$(env_value PNMTD_BACKUP_DIR)"
  echo "${dir:-backups}"
}
