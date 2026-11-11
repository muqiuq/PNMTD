#!/usr/bin/env bash
# A throwaway PostgreSQL for the integration tests (PNMTD.Tests):
#
#   tools/test-db.sh [start]   start it (port 55432), or say it is running
#   tools/test-db.sh stop      remove it
#
# The tests use it by default; PNMTD_TEST_DB names another server. Each test run creates its own
# database (pnmtd_test) on it, so nothing has to be cleaned up between runs.
set -euo pipefail
ENGINE="${ENGINE:-podman}"
NAME="${TEST_DB_NAME:-pnmtd-test-pg}"
PORT="${TEST_DB_PORT:-55432}"
IMAGE="${POSTGRES_IMAGE:-docker.io/library/postgres:18-alpine}"
case "${1:-start}" in
  start)
    if [[ -n "$("$ENGINE" ps -q --filter "name=^${NAME}$")" ]]; then
      echo "$NAME is running on port $PORT"
    else
      "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
      # Durability off: test data only, and much faster.
      "$ENGINE" run -d --name "$NAME" -p "127.0.0.1:${PORT}:5432" -e POSTGRES_PASSWORD=test \
        "$IMAGE" -c fsync=off -c synchronous_commit=off -c full_page_writes=off >/dev/null
      for _ in $(seq 1 60); do
        "$ENGINE" exec "$NAME" pg_isready -q -h 127.0.0.1 -U postgres 2>/dev/null && break
        sleep 0.5
      done
      echo "$NAME started on port $PORT"
    fi
    echo "PNMTD_TEST_DB='Host=127.0.0.1;Port=${PORT};Username=postgres;Password=test' (the tests' default)"
    ;;
  stop)
    "$ENGINE" rm -f "$NAME" >/dev/null && echo "$NAME removed"
    ;;
  *) echo "usage: $0 [start|stop]" >&2; exit 2 ;;
esac
