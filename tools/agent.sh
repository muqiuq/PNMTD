#!/usr/bin/env bash
# Publishes the PNMT sensor service (docs/windows-sensor-plan.md §11): pnmt-sensor-x64.exe and
# pnmt-sensor-arm64.exe, single-file, self-contained, compressed, each with its SHA-256, into
# artifacts/agent/ (or $OUT). Runs on macOS and Linux (EnableWindowsTargeting); for a manual test on
# a Windows machine, for ./build-sensor-windows.sh (into dist/), and for the release job on sensor tags
# (.github/workflows/sensor.yml).
#
#   tools/agent.sh                both architectures
#   ARCH=x64 tools/agent.sh       one
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="${OUT:-artifacts/agent}"
ARCHS="${ARCH:-x64 arm64}"
mkdir -p "$OUT"

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

for arch in $ARCHS; do
  work="$(mktemp -d)"
  echo "Publishing win-${arch}…"
  # The single-file settings are in the project (AgentRuntime). Locked: the executables are built from
  # exactly the packages the repository pins.
  dotnet publish PNMTD.Sensor.Windows/PNMTD.Sensor.Windows.csproj -c Release -p:AgentRuntime="win-${arch}" -o "$work" \
    -p:RestoreLockedMode=true -v q -nologo
  file="pnmt-sensor-${arch}.exe"
  cp "$work/pnmt-sensor.exe" "$OUT/$file"
  rm -rf "$work"
  sum="$(sha256 "$OUT/$file")"
  printf '%s  %s\n' "$sum" "$file" > "$OUT/$file.sha256"
  size="$(wc -c < "$OUT/$file" | tr -d ' ')"
  echo "  $OUT/$file  $((size / 1024 / 1024)) MB  sha256 $sum"
done
