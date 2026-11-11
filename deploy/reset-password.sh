#!/usr/bin/env bash
# Reset a user's password, for example an admin who forgot theirs. A temporary password is shown
# once (valid for 7 days, changed at the next sign-in); the user's sessions end and a lockout is
# lifted. Logged in the audit log as "cli".
#
#   ./reset-password.sh <username>
#   echo "$PASSWORD" | ./reset-password.sh <username> --password-stdin
#                                         a password of your choice (10–256 characters), not temporary
#   ./reset-password.sh <username> --unlock-only
#                                         only lift a lockout (5 failed sign-ins), keep the password
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

[[ $# -ge 1 && "$1" != -* ]] || { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 2; }
require_env
require_compose
user="$1"; shift
if [[ "${1:-}" == "--unlock-only" ]]; then
  [[ $# -eq 1 ]] || die "--unlock-only takes no other options"
  pnmtd_cmd users unlock "$user"
else
  pnmtd_cmd users reset-password "$user" "$@"
fi
