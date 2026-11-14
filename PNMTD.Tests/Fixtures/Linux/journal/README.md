# Journal lines for the Linux agent's parsers (docs/linux-sensor-plan.md §5.6, §14)

Captured in L0 (2026-11-14) with `journalctl -o json -t sshd -t sshd-session -t sshd-auth -t sudo -t sudo-rs -t su -t login -t systemd-logind`
in Podman containers (systemd as PID 1, dbus and logind running, `LogLevel VERBOSE` unless noted), after the same scenario on each:
SSH logins with a key (deploy and root), a command, SFTP, a key that isn't authorized, a password accepted and refused, two invalid
users (password and keyboard-interactive), root by password; sudo (a success, a command, a wrong password, a user not in sudoers);
su from root to deploy, and two failed su to root. The keys and passwords were made for the capture only.

| File | System | OpenSSH | systemd | sudo |
|---|---|---|---|---|
| `debian-12.jsonl` | Debian 12 (bookworm) | 9.2 (`sshd`) | 252 | 1.9.13 |
| `debian-13.jsonl` | Debian 13 (trixie) | 10.0 (`sshd-session`; no `sshd-auth` lines) | 257 | 1.9.16 |
| `debian-13-info.jsonl` | Debian 13, `LogLevel INFO` (the default): no SFTP line, no failed key | 10.0 | 257 | 1.9.16 |
| `ubuntu-22.04.jsonl` | Ubuntu 22.04 LTS | 8.9 (`sshd`) | 249 | 1.9.9 |
| `ubuntu-24.04.jsonl` | Ubuntu 24.04 LTS | 9.6 (`sshd`) | 255 | 1.9.15 |
| `ubuntu-26.04.jsonl` | Ubuntu 26.04 LTS | 10.2 (`sshd-session`) | 259 | 1.9.17 |
| `ubuntu-26.04-sudo-rs.jsonl` | Ubuntu 26.04 with sudo-rs (its default `sudo` since 25.10): identifier `sudo-rs`, `user :  PWD=…` | | | sudo-rs |

Not in the containers: console logins (`login`), which need a getty; their lines are written into the tests by hand from login(1)'s formats.
