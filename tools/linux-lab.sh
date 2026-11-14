#!/usr/bin/env bash
# Linux servers for the lab (docs/linux-sensor-plan.md §12): Debian and Ubuntu in containers with systemd
# as PID 1, OpenSSH, sudo and a test user, to try pnmt-agent by hand. arm64 runs natively on Apple
# Silicon, amd64 is emulated.
#
#   tools/linux-lab.sh start <name> [debian-12|debian-13|ubuntu-22.04|ubuntu-24.04|ubuntu-26.04] [--arch amd64|arm64]
#                                         a server as pnmtd-linux-<name> (default debian-12, this machine's arch)
#   tools/linux-lab.sh install <name> [--from <PNMT URL>]
#                                         the agent from dist/ (./build-sensor-linux.sh), or downloaded from a
#                                         PNMT as the install line does it, its SHA-256 checked
#   tools/linux-lab.sh join <name> <token> [<PNMT URL>]
#                                         pnmt-agent join, by default http://host.containers.internal:<PNMTD_PORT>
#                                         (7327, ./start.sh's) with --allow-http
#   tools/linux-lab.sh ssh <name> [user]  SSH in as lab (or root), which the server's logins then show
#   tools/linux-lab.sh shell <name>       a root shell in it
#   tools/linux-lab.sh stop <name>|--all  remove it, or every lab server
#   tools/linux-lab.sh list               the lab servers, their systems and SSH ports
#
# root and lab have the password LAB_PASSWORD (default pnmt-lab). Images are built from linux-lab/ once
# per system and arch (LAB_REBUILD=1 builds them again). Podman by default, ENGINE=docker for Docker.
set -euo pipefail
cd "$(dirname "$0")"
ENGINE="${ENGINE:-podman}"
PASSWORD="${LAB_PASSWORD:-pnmt-lab}"
PREFIX="pnmtd-linux-"
LABEL="pnmtd-linux-lab=1"

usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; }

need_name() { [[ -n "${1:-}" ]] || { echo "a server name is needed" >&2; exit 2; }; }

running() { [[ -n "$("$ENGINE" ps -q --filter "name=^$PREFIX$1$")" ]] || { echo "no lab server $1 (tools/linux-lab.sh list)" >&2; exit 1; }; }

base_of() {
  case "$1" in
    debian-12) echo docker.io/library/debian:12 ;;
    debian-13) echo docker.io/library/debian:13 ;;
    ubuntu-22.04 | ubuntu-24.04 | ubuntu-26.04) echo "docker.io/library/ubuntu:${1#ubuntu-}" ;;
    *) echo "unknown system $1: debian-12, debian-13, ubuntu-22.04, ubuntu-24.04 or ubuntu-26.04" >&2; exit 2 ;;
  esac
}

host_arch() { case "$(uname -m)" in arm64 | aarch64) echo arm64 ;; *) echo amd64 ;; esac; }

# The agent's architecture inside a server: x64 or arm64.
agent_arch() { case "$("$ENGINE" exec "$PREFIX$1" uname -m)" in aarch64 | arm64) echo arm64 ;; *) echo x64 ;; esac; }

case "${1:-}" in
  start)
    need_name "${2:-}"
    name="$2"; system="debian-12"; arch="$(host_arch)"
    shift 2
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --arch) arch="${2:?--arch needs amd64 or arm64}"; shift 2 ;;
        debian-* | ubuntu-*) system="$1"; shift ;;
        *) usage >&2; exit 2 ;;
      esac
    done
    [[ "$arch" == amd64 || "$arch" == arm64 ]] || { echo "--arch is amd64 or arm64" >&2; exit 2; }
    base="$(base_of "$system")"
    image="localhost/pnmtd-linux-lab:${system}-${arch}"
    if ! "$ENGINE" image exists "$image" 2>/dev/null || [[ -n "${LAB_REBUILD:-}" ]]; then
      echo "building $image from $base"
      "$ENGINE" build -q --platform "linux/$arch" --build-arg BASE="$base" --build-arg LAB_PASSWORD="$PASSWORD" -t "$image" linux-lab >/dev/null
    fi
    "$ENGINE" rm -f "$PREFIX$name" >/dev/null 2>&1 || true
    "$ENGINE" run -d --name "$PREFIX$name" --hostname "$name" --label "$LABEL" --label "pnmtd-linux-system=$system" \
      --platform "linux/$arch" --systemd=always -p 127.0.0.1::22 "$image" >/dev/null
    printf 'starting %s (%s, %s)' "$name" "$system" "$arch"
    for _ in $(seq 1 60); do
      state="$("$ENGINE" exec "$PREFIX$name" systemctl is-system-running 2>/dev/null || true)"
      if [[ "$state" == running || "$state" == degraded ]]; then
        echo
        echo "$name ready ($state): SSH on $("$ENGINE" port "$PREFIX$name" 22/tcp | head -1), root and lab with the password $PASSWORD"
        exit 0
      fi
      printf '.'; sleep 1
    done
    echo; echo "$name did not finish booting: tools/linux-lab.sh shell $name, then systemctl --failed" >&2; exit 1
    ;;
  install)
    need_name "${2:-}"; name="$2"; running "$name"
    arch="$(agent_arch "$name")"; file="pnmt-agent-linux-$arch"
    if [[ "${3:-}" == --from ]]; then
      url="${4:?--from needs the URL of PNMT}"; url="${url%/}"
      "$ENGINE" exec "$PREFIX$name" sh -c "wget -qO /tmp/pnmt-agent '$url/agent/v1/download/$file' \
        && wget -qO /tmp/pnmt-agent.sha256 '$url/agent/v1/download/$file.sha256' \
        && echo \"\$(cut -d' ' -f1 /tmp/pnmt-agent.sha256)  /tmp/pnmt-agent\" | sha256sum -c --quiet \
        && install -m 0755 /tmp/pnmt-agent /usr/local/sbin/pnmt-agent && rm /tmp/pnmt-agent /tmp/pnmt-agent.sha256"
    else
      [[ -f "../dist/$file" ]] || { echo "../dist/$file is missing: ./build-sensor-linux.sh $arch" >&2; exit 1; }
      "$ENGINE" cp "../dist/$file" "$PREFIX$name:/usr/local/sbin/pnmt-agent"
      "$ENGINE" exec "$PREFIX$name" sh -c 'chown root:root /usr/local/sbin/pnmt-agent && chmod 0755 /usr/local/sbin/pnmt-agent'
    fi
    "$ENGINE" exec "$PREFIX$name" pnmt-agent version
    ;;
  join)
    need_name "${2:-}"; name="$2"; running "$name"
    token="${3:?a join token is needed (Admin → Servers → Add server → Linux)}"
    url="${4:-http://host.containers.internal:${PNMTD_PORT:-7327}}"
    flags=()
    [[ "$url" == http://* ]] && flags=(--allow-http)
    "$ENGINE" exec "$PREFIX$name" pnmt-agent join --url "$url" --token "$token" ${flags[@]+"${flags[@]}"}
    ;;
  ssh)
    need_name "${2:-}"; name="$2"; running "$name"; user="${3:-lab}"
    port="$("$ENGINE" port "$PREFIX$name" 22/tcp | head -1 | sed 's/.*://')"
    echo "password: $PASSWORD" >&2
    exec ssh -p "$port" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR "$user@127.0.0.1"
    ;;
  shell)
    need_name "${2:-}"; running "$2"; exec "$ENGINE" exec -it "$PREFIX$2" bash -l
    ;;
  stop)
    if [[ "${2:-}" == --all ]]; then
      ids="$("$ENGINE" ps -aq --filter "label=$LABEL")"
      if [[ -n "$ids" ]]; then
        # shellcheck disable=SC2086 # one id per word
        "$ENGINE" rm -f $ids >/dev/null
      fi
      echo "lab servers removed"
    else
      need_name "${2:-}"; "$ENGINE" rm -f "$PREFIX$2" >/dev/null && echo "$2 removed"
    fi
    ;;
  list)
    "$ENGINE" ps --filter "label=$LABEL" --format '{{.Names}}' | while read -r c; do
      system="$("$ENGINE" inspect -f '{{index .Config.Labels "pnmtd-linux-system"}}' "$c" 2>/dev/null || true)"
      machine="$("$ENGINE" exec "$c" uname -m 2>/dev/null || echo unknown)"
      agent="$("$ENGINE" exec "$c" systemctl is-active pnmt-agent 2>/dev/null || true)"
      ssh_port="$("$ENGINE" port "$c" 22/tcp 2>/dev/null | head -1 || true)"
      printf '%-20s %-14s %-8s %-12s %s\n' "${c#"$PREFIX"}" "${system:-unknown}" "$machine" "agent ${agent:-none}" "$ssh_port"
    done
    ;;
  -h | --help | help)
    usage
    ;;
  *)
    usage >&2; exit 2
    ;;
esac
