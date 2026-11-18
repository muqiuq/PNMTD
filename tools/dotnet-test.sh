#!/usr/bin/env bash
# dotnet test, its arguments passed on, as CI's build job runs it:
#
#   tools/dotnet-test.sh --solution PNMTD.slnx --no-build
#   PNMTD_TEST_TZ=America/New_York tools/dotnet-test.sh --project PNMTD.Tests/PNMTD.Tests.csproj --no-build
#
# The output as dotnet test writes it. On GitHub Actions, a failed run also puts each failed test (the first ten)
# on the run's page as an annotation: its name, the tests' zone, and the first lines of its message. The job's log
# needs a sign-in; the run's page doesn't.
set -uo pipefail
log=$(mktemp)
trap 'rm -f "$log"' EXIT

dotnet test "$@" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}

if [[ $status -ne 0 && "${GITHUB_ACTIONS:-}" == "true" ]]; then
  # "failed Name (12ms)", then the message indented by two spaces and its stack by four.
  sed $'s/\x1b\\[[0-9;]*m//g' "$log" | awk -v zone="${PNMTD_TEST_TZ:-Europe/Zurich}" '
    function escape(s, title) {
      gsub(/%/, "%25", s); gsub(/\r/, "", s); gsub(/\n/, "%0A", s)
      if (title) { gsub(/:/, "%3A", s); gsub(/,/, "%2C", s) }
      return s
    }
    function flush() {
      if (name != "") printf "::error title=%s::%s\n", escape("Test failed (" zone "): " name, 1), escape(message == "" ? "(no message)" : message, 0)
      name = ""; message = ""
    }
    /^failed / { flush(); name = substr($0, 8); sub(/ \([0-9][^)]*\)$/, "", name); lines = 0; next }
    name != "" && /^  [^ ]/ && !/^  from / {
      if (lines++ < 6) message = (message == "" ? "" : message "\n") substr($0, 3)
      next
    }
    name != "" && !/^  / { flush() }
    END { flush() }' | head -10
fi
exit "$status"
