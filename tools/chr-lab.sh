#!/usr/bin/env bash
# MikroTik CHR routers for the lab (docs/mikrotik-plan.md §12, phase M0). Each runs in QEMU in a
# container of its own: ARM64 with KVM where an image exists (7.20 on), x86_64 emulated otherwise.
#
#   tools/chr-lab.sh start <name> [<version>]   boot CHR <version> (default 7.25.1) as pnmtd-chr-<name>
#   tools/chr-lab.sh stop <name>|--all          remove it, or every lab router
#   tools/chr-lab.sh list                       the lab routers, their versions and SSH ports
#   tools/chr-lab.sh port <name>                the router's SSH port on 127.0.0.1
#   tools/chr-lab.sh ssh <name> [command…]      SSH as admin (the lab password), from inside its container
#   tools/chr-lab.sh import <name> <file.rsc>   upload a script as admin and /import it, as an admin would
#   tools/chr-lab.sh console <name>             the serial console (leave with Ctrl-C)
#
# Signed in once on the serial console as admin, with the password CHR_PASSWORD (default pnmt-lab).
# Images are cached in the volume pnmtd-chr-images. Podman by default, ENGINE=docker for Docker.
set -euo pipefail
# A script file given relative to where the command was run, before the cd below.
if [[ "${1:-}" == import && -n "${3:-}" && -f "$3" ]]; then
  set -- "$1" "$2" "$(cd "$(dirname "$3")" && pwd)/$(basename "$3")"
fi
cd "$(dirname "$0")"
ENGINE="${ENGINE:-podman}"
IMAGE="localhost/pnmtd-chr-lab:1"
PASSWORD="${CHR_PASSWORD:-pnmt-lab}"
PREFIX="pnmtd-chr-"

build() {
  "$ENGINE" image exists "$IMAGE" 2>/dev/null && [[ -z "${CHR_REBUILD:-}" ]] && return
  "$ENGINE" build -q -t "$IMAGE" chr-lab >/dev/null
}

need_name() { [[ -n "${1:-}" ]] || { echo "a router name is needed" >&2; exit 2; }; }

ssh_admin() {
  local name="$1"; shift
  "$ENGINE" exec -i "$PREFIX$name" sshpass -p "$PASSWORD" ssh -q -p 2222 \
    -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=15 \
    admin@127.0.0.1 "$@"
}

case "${1:-}" in
  start)
    need_name "${2:-}"
    name="$2"; version="${3:-7.25.1}"
    build
    "$ENGINE" rm -f "$PREFIX$name" >/dev/null 2>&1 || true
    devices=()
    if [[ -z "${CHR_NO_KVM:-}" ]]; then
      if [[ -e /dev/kvm ]] || { [[ "$ENGINE" == podman ]] && podman machine ssh test -e /dev/kvm 2>/dev/null; }; then
        devices=(--device /dev/kvm)
      fi
    fi
    "$ENGINE" run -d --name "$PREFIX$name" --label pnmtd-chr-lab=1 --entrypoint "" \
      ${devices[@]+"${devices[@]}"} -v pnmtd-chr-images:/images \
      -e CHR_VERSION="$version" -e CHR_ARCH="${CHR_ARCH:-auto}" -e CHR_MEMORY="${CHR_MEMORY:-1024}" -e CHR_NAME="$name" -e CHR_PASSWORD="$PASSWORD" \
      -p 127.0.0.1::2222 -p 127.0.0.1::8291 -p 127.0.0.1::8080 \
      "$IMAGE" python3 -u /lab/run.py >/dev/null
    printf 'booting %s (RouterOS %s)' "$name" "$version"
    for _ in $(seq 1 900); do
      if "$ENGINE" exec "$PREFIX$name" test -f /run/chr/ready 2>/dev/null; then
        echo
        echo "$name ready: RouterOS $("$ENGINE" exec "$PREFIX$name" cat /run/chr/ready), SSH on $("$ENGINE" port "$PREFIX$name" 2222/tcp)"
        exit 0
      fi
      if [[ -z "$("$ENGINE" ps -q --filter "name=^$PREFIX$name$")" ]]; then
        echo; "$ENGINE" logs --tail 40 "$PREFIX$name" >&2; exit 1
      fi
      printf '.'; sleep 2
    done
    echo; echo "$name did not become ready" >&2; exit 1
    ;;
  stop)
    if [[ "${2:-}" == --all ]]; then
      ids=$("$ENGINE" ps -aq --filter label=pnmtd-chr-lab=1)
      [[ -n "$ids" ]] && "$ENGINE" rm -f $ids >/dev/null
      echo "lab routers removed"
    else
      need_name "${2:-}"; "$ENGINE" rm -f "$PREFIX$2" >/dev/null && echo "$2 removed"
    fi
    ;;
  list)
    "$ENGINE" ps --filter label=pnmtd-chr-lab=1 --format '{{.Names}}' | while read -r c; do
      printf '%-24s %-22s %s\n' "${c#"$PREFIX"}" "$("$ENGINE" exec "$c" cat /run/chr/ready 2>/dev/null || echo booting)" \
        "$("$ENGINE" port "$c" 2222/tcp 2>/dev/null)"
    done
    ;;
  port)
    need_name "${2:-}"; "$ENGINE" port "$PREFIX$2" 2222/tcp | head -1 | sed 's/.*://'
    ;;
  ssh)
    need_name "${2:-}"; name="$2"; shift 2; ssh_admin "$name" "$@"
    ;;
  import)
    need_name "${2:-}"; [[ -f "${3:-}" ]] || { echo "a script file is needed" >&2; exit 2; }
    name="$2"; file="$(basename "$3")"
    "$ENGINE" cp "$3" "$PREFIX$name:/tmp/$file"
    "$ENGINE" exec "$PREFIX$name" sshpass -p "$PASSWORD" scp -q -P 2222 -o StrictHostKeyChecking=no \
      -o UserKnownHostsFile=/dev/null "/tmp/$file" "admin@127.0.0.1:$file"
    ssh_admin "$name" "/import file-name=$file verbose=yes"
    ;;
  console)
    need_name "${2:-}"; "$ENGINE" exec -it "$PREFIX$2" socat -,raw,echo=0 UNIX-CONNECT:/run/chr/serial.sock
    ;;
  *)
    awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 2
    ;;
esac
