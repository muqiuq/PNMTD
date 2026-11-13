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

app_image() {
  local image tag
  image="$(env_value PNMTD_IMAGE)"; tag="$(env_value PNMTD_IMAGE_TAG)"
  echo "${image:-uisach/pnmtd}:${tag:-v1.1.0}"
}

backup_dir() {
  local dir
  dir="$(env_value PNMTD_BACKUP_DIR)"
  echo "${dir:-backups}"
}
