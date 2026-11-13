#!/usr/bin/env bash
# Builds the PNMT sensor service for Windows (docs/windows-sensor-plan.md §11) into dist/:
# pnmt-sensor-x64.exe and pnmt-sensor-arm64.exe, single-file and self-contained (.NET included),
# each with its .sha256 for Get-FileHash. Needs the .NET SDK of global.json; runs on macOS and Linux.
# The version is Directory.Build.props', as in the image and the GitHub release.
#
#   ./build-sensor-windows.sh                  both architectures
#   ./build-sensor-windows.sh x64              one: x64 or arm64
#   OUT=/tmp/agent ./build-sensor-windows.sh   another folder
set -euo pipefail
cd "$(dirname "$0")"

usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; }

case "${1:-}" in
  "") archs="x64 arm64" ;;
  x64 | arm64) archs="$1" ;;
  -h | --help) usage; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac
[[ $# -le 1 ]] || { usage >&2; exit 2; }

command -v dotnet >/dev/null 2>&1 || { echo "The .NET SDK is needed (dotnet, the version of global.json)." >&2; exit 1; }

out="${OUT:-dist}"
mkdir -p "$out"
# A build output, not for git: the folder ignores itself.
[[ -f "$out/.gitignore" ]] || printf '*\n' >"$out/.gitignore"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
echo "PNMT Sensor ${version} → ${out}/"
OUT="$out" ARCH="$archs" tools/agent.sh
