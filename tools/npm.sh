#!/usr/bin/env bash
# npm for PNMTD.WebUI, in a container (docs/webui-migration-plan.md §6.1): node never runs on the host.
#
#   tools/npm.sh install --save-exact primevue@4.x.y
#   tools/npm.sh run gen:api        openapi.json → src/api/schema.d.ts
#   tools/npm.sh run lint
#   tools/npm.sh test               vitest
#   tools/npm.sh exec -- vite --version
#
# PNMTD.WebUI is bind-mounted; node_modules live in the volume pnmtd-web-node-modules (Linux
# binaries, never the Mac's), npm's cache in pnmtd-npm-cache. Whenever package-lock.json changed
# since the last install, npm ci runs first. ENGINE=docker uses Docker.
set -euo pipefail
cd "$(dirname "$0")/.."

ENGINE="${ENGINE:-podman}"
NODE_IMAGE="${NODE_IMAGE:-docker.io/library/node:24-slim}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pnmtd-web-node-modules}"
NPM_CACHE="${NPM_CACHE:-pnmtd-npm-cache}"

[[ $# -gt 0 ]] || { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 2; }

tty=()
[[ -t 0 && -t 1 ]] && tty=(-it)
# The script runs in the container's sh; "$@" are npm's arguments.
# shellcheck disable=SC2016
exec "$ENGINE" run --rm ${tty[@]+"${tty[@]}"} \
  -v "$PWD/PNMTD.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -v "${NPM_CACHE}:/root/.npm" \
  -e NPM_CONFIG_UPDATE_NOTIFIER=false -e NPM_CONFIG_FUND=false -e NPM_CONFIG_AUDIT=false -e NPM_CONFIG_IGNORE_SCRIPTS=true -w /web \
  "$NODE_IMAGE" sh -c '
    installed=node_modules/.installed-lock
    case "$1" in install|i|ci|uninstall|remove|rm|update|view|info|init) ;; *)
      if [ -f package-lock.json ] && ! cmp -s package-lock.json "$installed"; then
        echo "package-lock.json changed: npm ci" >&2
        find node_modules -mindepth 1 -maxdepth 1 -exec rm -rf {} + && npm ci --loglevel=error || exit 1
        cp package-lock.json "$installed"
      fi ;;
    esac
    npm "$@" || exit $?
    if [ -f package-lock.json ]; then cp package-lock.json "$installed"; fi' sh "$@"
