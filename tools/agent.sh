#!/usr/bin/env bash
# Publishes the PNMT agents, each with its SHA-256 (sha256sum's format: the hash, two spaces, the name),
# into artifacts/agent/ (or $OUT): single-file, self-contained, compressed, from a locked restore.
#   Windows (docs/windows-sensor-plan.md §11): pnmt-sensor-x64.exe and pnmt-sensor-arm64.exe; they build
#     on macOS and Linux too (EnableWindowsTargeting).
#   Linux (docs/linux-sensor-plan.md §12): pnmt-agent-linux-x64 and pnmt-agent-linux-arm64.
# For ./build-sensor-windows.sh and ./build-sensor-linux.sh (into dist/), and for the release job on
# sensor tags (.github/workflows/sensor.yml).
#
#   tools/agent.sh                      every platform, both architectures
#   PLATFORM=linux tools/agent.sh       one platform: windows, linux or all
#   ARCH=x64 tools/agent.sh             one architecture: x64 or arm64
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="${OUT:-artifacts/agent}"
PLATFORM="${PLATFORM:-all}"
ARCHS="${ARCH:-x64 arm64}"
case "$PLATFORM" in
  windows | linux | all) ;;
  *) echo "PLATFORM is windows, linux or all, not ${PLATFORM}" >&2; exit 2 ;;
esac
mkdir -p "$OUT"

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# publish PROJECT RUNTIME BUILT FILE: publishes PROJECT for RUNTIME and copies its BUILT executable to $OUT/FILE.
publish() {
  local project="$1" runtime="$2" built="$3" file="$4" work sum size
  work="$(mktemp -d)"
  echo "Publishing ${runtime}…"
  # The single-file settings are in the project (AgentRuntime). Locked: the executables are built from
  # exactly the packages the repository pins.
  dotnet publish "$project" -c Release -p:AgentRuntime="$runtime" -o "$work" -p:RestoreLockedMode=true -v q -nologo
  cp "$work/$built" "$OUT/$file"
  rm -rf "$work"
  sum="$(sha256 "$OUT/$file")"
  printf '%s  %s\n' "$sum" "$file" >"$OUT/$file.sha256"
  size="$(wc -c <"$OUT/$file" | tr -d ' ')"
  echo "  $OUT/$file  $((size / 1024 / 1024)) MB  sha256 $sum"
}

for arch in $ARCHS; do
  if [[ "$PLATFORM" == windows || "$PLATFORM" == all ]]; then
    publish PNMTD.Sensor.Windows/PNMTD.Sensor.Windows.csproj "win-${arch}" pnmt-sensor.exe "pnmt-sensor-${arch}.exe"
  fi
  if [[ "$PLATFORM" == linux || "$PLATFORM" == all ]]; then
    publish PNMTD.Sensor.Linux/PNMTD.Sensor.Linux.csproj "linux-${arch}" pnmt-agent "pnmt-agent-linux-${arch}"
  fi
done
