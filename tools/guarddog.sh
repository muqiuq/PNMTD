#!/usr/bin/env bash
# GuardDog over the web UI's packages, in a container (docs/webui-migration-plan.md §6.1, §7.1): the
# packages it downloads are unpacked there, never on the host. The check itself is
# tools/check-npm-guarddog.py; this is what the supply-chain workflow runs too.
#
#   tools/guarddog.sh                 every package of PNMTD.WebUI/package-lock.json (some minutes)
#   tools/guarddog.sh --batch 1/4     a quarter of them, as CI runs the four in parallel
#
# GuardDog's version is pinned here, as everything else is; bump it on purpose. pip's cache lives in
# the volume pnmtd-pip-cache. ENGINE=docker uses Docker.
set -euo pipefail
cd "$(dirname "$0")/.."

ENGINE="${ENGINE:-podman}"
PYTHON_IMAGE="${PYTHON_IMAGE:-docker.io/library/python:3.12-slim}"
PIP_CACHE="${PIP_CACHE:-pnmtd-pip-cache}"
GUARDDOG_VERSION="${GUARDDOG_VERSION:-3.2.0}"

# The repository read-only; the script writes its package.json files to the container's /tmp.
# shellcheck disable=SC2016
exec "$ENGINE" run --rm -v "$PWD:/w:ro" -v "${PIP_CACHE}:/root/.cache/pip" -w /w \
  -e PIP_ROOT_USER_ACTION=ignore -e PIP_DISABLE_PIP_VERSION_CHECK=1 -e GUARDDOG_VERSION="$GUARDDOG_VERSION" \
  "$PYTHON_IMAGE" sh -c 'pip install -q "guarddog==$GUARDDOG_VERSION" && exec python3 -I tools/check-npm-guarddog.py "$@"' sh "$@"
