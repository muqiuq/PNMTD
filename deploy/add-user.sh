#!/usr/bin/env bash
# Add a user who signs in to the admin UI. A temporary password is generated and shown once: it is
# valid for 7 days and must be changed at the first sign-in.
#
#   ./add-user.sh <username> --groups Operators    the rights of their groups (several: --groups Operators,Helpdesk)
#   ./add-user.sh <username> --admin               every right, and users and groups
#   ./add-user.sh <username>                       no groups: only their own account, until an admin adds them to one
#   ./add-user.sh <username> --display-name "Ana Muster"
#   echo "$PASSWORD" | ./add-user.sh <username> --password-stdin
#                                                  a password of your choice (10–256 characters), not temporary
#
# Groups and their rights are managed in the admin UI (Admin → Groups); ./list-users.sh shows who is in which.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

[[ $# -ge 1 && "$1" != -* ]] || { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 2; }
require_env
require_compose
pnmtd_cmd users add "$@"
