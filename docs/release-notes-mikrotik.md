# PNMT 1.1: MikroTik routers and WireGuard

PNMT 1.1 monitors MikroTik routers, keeps their configuration history, updates them and runs commands on them. Routers behind NAT reach PNMT through a WireGuard tunnel of its own. All of it is off until you add a router: an installation without MikroTik routers runs exactly as before.

## What you get

- **Monitoring.** A router is a sensor of type *MikroTik router*. PNMT reads its version, identity, model, uptime, CPU, memory, storage and firmware every poll (5 minutes by default). Events:
  - 240 OK;
  - 241 restarted, 242 updated, 243 configuration changed, 244 update available;
  - 440 unreachable after the grace period, 441 access denied, 442 backups failing, 443 update failed.
- **Configuration backups.** An `/export` every day, stored only when it changed, with a diff against any earlier version. Secrets stay out unless a router opts in. Kept for a year, and at least 50 versions.
- **Updates.** From a router's *Updates* tab, or for several routers from *Admin → MikroTik & WireGuard*:
  - before anything runs, PNMT checks the password and the policies on every router;
  - then it exports, takes a binary backup (encrypted with the router's own admin password), downloads, reboots and verifies;
  - on a RouterBOARD, the firmware follows with a second reboot;
  - every step goes into the job log;
  - a batch runs one router at a time and stops at the first failure.
- **Remote commands.** A command or a short script for one router or many:
  - as `pnmt` (Users and Admins), the router allows reading only;
  - as the router's admin (Admins only), with exports before and after that show the change as a diff;
  - the output is kept as the router's history, and an Admin can redact a run;
  - from the server: `pnmtd mikrotik run`.
- **WireGuard.** Routers behind NAT dial PNMT; nothing has to be opened on them. PNMT's gateway allows them to reach nothing but PNMT, and no router can reach another.
- **Custom tunnels** for technicians: a `wg-quick` file or QR code that reaches WinBox and SSH on the routers you choose, and nothing else.

## Security in one paragraph

PNMT logs in to a router as `pnmt`, a user that the join script creates and that may read but not change anything. **A router's admin password is never stored.** It is typed for one update or one command run, held in memory while that run lasts, accepted over HTTPS only, and sent only to a router whose host key PNMT knows. So a compromised PNMT can read routers' configurations (without secrets), but cannot change a router. Tunnel keys and PNMT's SSH key are encrypted with the key ring on the `pnmtd-keys` volume. The SSH key can be rotated on the admin page: routers keep working until their new join script is pasted, and then the old key is retired.

## Updating from 1.0

In the `deploy/` folder: `git pull` for the new `docker-compose.yml` (the WireGuard gateway, which stays off until enabled), then `./update.sh --tag v1.1.0`. It takes a backup first. `.env` pins the image tag, so the update needs `--tag`. The database migrations run when the new image starts; nothing else changes until you add a router.

From v0.12, [deploy/UPGRADING-1.0.md](../deploy/UPGRADING-1.0.md) applies as it is and lands on 1.1.

## Getting started

- **A router PNMT can reach** (same network, VPN, port forwarding): add the sensor in *Direct SSH* mode, open its *Setup* tab, and paste the join script into the router's terminal. The next poll pins its host key.
- **Routers behind NAT:** enable the gateway once with `./init.sh --wireguard` and `docker compose up -d`. Forward the UDP port if PNMT is behind a router itself, then choose *WireGuard + SSH* for the router. The gateway needs the WireGuard kernel module (Linux 5.6 or later). [deploy/README.md](../deploy/README.md#mikrotik-routers-and-wireguard) has the details.
- **RouterOS 7.13 or later.** PNMT was tested on CHR 7.13 and 7.25.1.

## Measured

- **Polling:** 500 simulated routers at a 5-minute interval, with a real router's latency (150 ms to connect, 250 ms a command) and 8 SSH sessions at most. A first round with every router's first export takes 42 s, and a steady round 26 s. Unchanged routers add no event rows.
- **The gateway with 1,000 peers:** `wg syncconf` takes 111 ms for 1,000 new peers and 20 ms when nothing changed. `nft -f` takes 17 to 21 ms for 50 custom tunnels that each reach 200 routers.
- **Lab, on CHR 7.25.1 behind NAT:** an update from 7.13 to 7.25.1 took 41 seconds through the tunnel.

## Known limits

- **No firmware test on real hardware.** CHR has no RouterBOARD, so the firmware step is tested against a simulated router only.
- **Exit statuses:** RouterOS 7.25.1 sometimes reports exit status 0 for a failed script. PNMT also reads RouterOS's error line, so the outcome is still right.
- **One PNMT per router.** A second installation's join script replaces the first one's access.
- **Demo data:** the seed (`SEED=1 ./start.sh`) creates routers in Direct SSH mode only.
