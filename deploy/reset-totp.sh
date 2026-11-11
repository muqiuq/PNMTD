#!/usr/bin/env bash
# Turn off a user's two-factor authentication, for someone who lost their authenticator app (there
# are no recovery codes). Their sessions end and a lockout is lifted; the next sign-in offers the
# setup again. Logged in the audit log as "cli". Admins can do the same in the UI (Users, Reset 2FA).
#
#   ./reset-totp.sh <username>
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

[[ $# -eq 1 && "$1" != -* ]] || { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 2; }
require_env
require_compose
pnmtd_cmd users reset-totp "$1"
