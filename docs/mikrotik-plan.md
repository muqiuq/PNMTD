# Plan: MikroTik routers and WireGuard tunnels (RouterOS 7.13+)

| | |
|---|---|
| Status | **Revision 3, released in v1.1.0**: M0 to M7 are built (§13.1). Details still marked *(M0)* belong to later phases and are verified there on a real RouterOS. The other RouterOS facts were checked against the manual and the per-version changelogs (§4.1) |
| Date | 2026-11-12 |
| Revision | 3: **remote commands** (§8): a command or script, run on one router or several, as the read-only `pnmt` user, or as the router's admin with the password typed for that run. **The admin password is therefore needed for updates and for commands run as an admin** (decision 8, §16.1).<br><br>2: **the review decisions of revision 1 (§16.1)**:<ul><li>three connection modes: **WireGuard + SSH**, **WireGuard only**, and **Direct SSH**;</li><li>a router joins with **one script**, generated when it is added in the UI, that contains everything, **including the router's WireGuard private key**;</li><li>**the admin password is needed only for updates**;</li><li>**custom tunnels**: extra WireGuard peers with a downloadable configuration;</li><li>**the tunnel network is configured in the web UI**, on one admin page that also monitors it;</li><li>**the external address and port** of the WireGuard endpoint can also be set in environment variables, for a PNMTD behind NAT.</li></ul>Revision 1 had a copy-back of the router's public key and a second onboarding path that used the admin password |
| Scope | PNMTD monitors and manages MikroTik routers running RouterOS 7.13 or later, and runs a WireGuard network for them.<ul><li>A router is added in the UI. PNMT then shows a **join script**, which the router's admin runs once on the router.</li><li>Through a **WireGuard tunnel that the router opens**, or directly over SSH, PNMTD logs in as a **read-only user, `pnmt`**.</li><li>It monitors the version, uptime and resources.</li><li>It **backs the configuration up** on a schedule and keeps each changed version with a diff.</li><li>It **installs RouterOS updates** when an Admin supplies the router's admin password for that one job.</li><li>It **runs commands** on routers: read-only as `pnmt`, or as the router's admin with the password typed for that run.</li></ul>**Custom tunnels** give people (or other devices) a WireGuard configuration of their own, for management access to the routers through PNMT |
| Non-goals | RouterOS 6, which has no WireGuard and a different syntax. Changing a router's configuration from PNMT other than by the join script, updates, and commands run as the router's admin. An interactive terminal in the browser. Restoring backups onto a router. Transporting packages: the router downloads them from MikroTik itself. Access through custom tunnels to the networks behind the routers (only the routers themselves, §3.5). IPv6 inside the tunnel network. SNMP, the RouterOS API, REST, and router-side push scripts. Any change to the sensor API (`/event`, `/read`, frozen by docs/webui-migration-plan.md §3.3) |
| References | docs/webui-migration-plan.md (architecture, conventions, roles). docs/udp-iot-plan.md, the pattern for device integrations: a sensor type with a 1:1 table, a permanent log, and Admin-only jobs. The RouterOS manual (manual.mikrotik.com; help.mikrotik.com is frozen) on WireGuard, users, SSH keys, SSH, export, backup, upgrades and scripting. The changelog of each release at `https://upgrade.mikrotik.com/routeros/<version>/CHANGELOG`. The `wg-quick` configuration format. SSH.NET |

---

## 1. Summary

**Today:** a MikroTik can report to PNMT only the way any device can. A scheduler script calls `/tool fetch` on `/event/<token>/…`, or a PING sensor pings it. PNMT knows nothing about its version, configuration or updates, and it has no way into a router behind NAT.

**Target:** a router is a sensor of a new type, `MIKROTIK`. The flow:

1. **Adding a router** in the UI shows its **join script**.
2. **The router's admin pastes it** into the router's terminal, or imports it as a `.rsc` file.
3. **The router dials PNMTD's WireGuard port**, and PNMTD logs in through the tunnel as `pnmt`, a user that may read but not change anything.
4. **From then on**, PNMTD reads the version, uptime, resources and firmware every 5 minutes, and exports the configuration every day.
5. **An Admin can update** one router or a fleet by typing the router's admin password for that job.
6. **A command can be run** on one router or many: by anyone with CanWrite as `pnmt`, which can only read, or by an Admin as the router's admin, with the password typed for that run.

**Custom tunnels** put a technician's laptop into the same WireGuard network, so that it can reach the routers' WinBox, SSH or WebFig through PNMT.

| Topic | Decision |
|---|---|
| Connection modes | **WireGuard + SSH** (the default): the tunnel and the `pnmt` user, with every feature. **WireGuard only**: the tunnel alone; PNMT watches it, and custom tunnels can use it. **Direct SSH**: no tunnel, for routers that PNMTD can already reach, with every feature (§3.1) |
| The WireGuard network | **One network per installation, configured on the admin page:** the prefix, keepalive, MTU, and the management ports for custom tunnels. **The external address and port that routers dial are set in the UI or in environment variables** (`PNMTD_WG_EXTERNAL_HOST`, `PNMTD_WG_EXTERNAL_PORT`), and the environment wins. A PNMTD behind NAT or a router therefore works (§3.3) |
| Where the tunnels end | In a small **WireGuard gateway**, `pnmtd-wg`: the same image, run as a second service with only `NET_ADMIN`, and opt-in through a compose profile. It owns `wg0` and its firewall, takes its peers from PostgreSQL, and passes PNMTD's SSH connections into the tunnel over SOCKS5. **The app container stays non-root, read-only and without capabilities** (§3.4) |
| Joining a router | **One script**, generated when the router is added. It contains the WireGuard interface **with the router's private key** (generated by PNMTD), the peer, the address and the route, the firewall rules, and the `pnmt` group, user and SSH key. One paste, nothing to copy back. **No admin password.** The script can be pasted again: it replaces what it created before (§4.3) |
| The `pnmt` user | It belongs to a group with **only the `ssh` and `read` policies**. It **may log in only from PNMTD's tunnel address**, and only with PNMTD's SSH key. It can read the whole configuration except secrets, and can change nothing (§4.2) |
| Custom tunnels | **Extra WireGuard peers**, created by an Admin, optionally for a PNMT user, who can then **download the configuration** (a `wg-quick` file, or a QR code for a phone). Through the gateway they reach **the management ports of the routers they are allowed to reach**, and nothing else (§3.5) |
| Monitoring | At every Interval (default 5 minutes), PNMTD reads: version and channel, uptime and restarts, CPU, memory, storage, board, serial number, RouterBOARD firmware, and the tunnel's handshake. With WireGuard only, it watches the handshake alone. These become events 240–244 and 440–443 on the router's sensor (§5) |
| Latest versions | **Fetched centrally** from MikroTik's update server every 6 hours, per channel, then compared with every router. No router needs rights for this (§5.4) |
| Configuration backups | **`/export terse` as the read-only user, daily and before every update.** A new version is stored only when the configuration changed. The UI shows a diff between any two versions. Secrets stay hidden unless a router opts into them (§6) |
| Updates | **One of the two uses of the admin password**, with commands run as an admin. Admin only, for one router or several. The password is entered for the job, **held in memory only**, never stored or logged, and dropped when the job ends. The steps: export and binary backup, download, reboot, wait, verify, RouterBOARD firmware, reboot, verify. Every step goes to a permanent job log (§7) |
| Remote commands | **A command or a short script, on one router or several**, in the SSH modes. **As `pnmt`** (CanWrite, no password), the router's policies allow only reading, and with *Diagnostics* ping and traceroute. **As the router's admin** (Admin), the password is typed for the run and handled as for updates. An export before and after shows what the command changed. The text and the output are kept as a history, which an Admin can redact (§8) |
| One admin page | **Admin → MikroTik & WireGuard** has five tabs: an overview that **monitors** the gateway, the endpoint and every peer; the **network** settings; the routers, their updates and commands; the custom tunnels; and the jobs (§11.2) |
| PNMT model | A router is a sensor of the new type **`MIKROTIK` (80)** on a host, with a 1:1 table, `mikrotik_routers`. Every tunnel, router or custom, is a row of `wg_peers` (§9.2) |
| Secrets in PNMTD | These are **encrypted with Data Protection**, like the TOTP secrets, with the key ring on `pnmtd-keys`: PNMTD's SSH private key, the gateway's WireGuard private key, and **the private keys of the routers and custom tunnels**. The last are kept so that their scripts and configurations can be shown again. `backup.sh` already covers all of it (§9.6) |
| Default | **Direct SSH needs no change to the deployment. WireGuard is off** until the compose profile `wireguard` is enabled. It needs a capability, a kernel module and a new open port (§9.5) |

---

## 2. Requirements, starting point and alternatives

### 2.1 Requirements

| # | Requirement | Where |
|---|---|---|
| R1 | Manage MikroTik routers on RouterOS 7.13 or later | §4.1 |
| R2 | Routers connect to PNMTD over WireGuard, and PNMTD reaches them over SSH through it | §3.2, §3.4, §4.5 |
| R3 | A read-only `pnmt` user on the router | §4.2 |
| R4 | Monitor the version, uptime and more | §5 |
| R5 | Back the configuration up automatically, with whatever rights that needs | §4.2 (the rights matrix), §6 |
| R6 | Install updates with the admin password | §7 |
| R7 *(review)* | Three modes only: WireGuard + SSH, WireGuard only, and also Direct SSH | §3.1 |
| R8 *(review)* | A router is added in the UI, which shows **one script** that joins it: the user, all WireGuard private keys, everything | §4.3 |
| R9 *(review)* | **The admin password only for updates** | §4.3, §6.5, §7.2 |
| R10 *(review)* | **Custom tunnels**, with their configuration downloadable | §3.5 |
| R11 *(review)* | **The network configurable in the web UI, on one admin page** that monitors and configures it | §3.3, §11.2 |
| R12 *(review)* | **The external address and port of the WireGuard endpoint in environment variables**, for a PNMTD behind NAT | §3.3, §9.5 |
| R13 *(review 2)* | **Run a command on a router remotely** | §8 |

### 2.2 Design goals

1. **Least privilege on the router.** PNMTD's standing access is read-only, from one address, and by key. The operations that change a router, an update and a command run as an admin, need a password that a person types for that job.
2. **Nothing to open on the router.** The router dials out, and its WAN stays closed.
3. **The app container stays hardened.** Whatever needs `NET_ADMIN` runs in a separate, opt-in service.
4. **Deny by default in the tunnel network.** Routers reach PNMTD and nothing else. Custom tunnels reach only what they are allowed.
5. **Fits PNMT.** A router is a sensor, so hosts, notifications, the dashboard and the uptime bars work without special cases.
6. **Safe to repeat.** The join script can be pasted again. An update checks before it acts. A PNMTD restart in the middle of a job fails safely.

### 2.3 Alternatives

The review settled the transport: **WireGuard (+ SSH), WireGuard only, and Direct SSH. Nothing else is built.** For the record:

| Option | Why not |
|---|---|
| The router pushes (`/tool fetch` on a scheduler) | PNMTD could neither decide when an update runs nor use a password for it, and exports are large for RouterOS scripts. The heartbeat part already works today with a HEARTBEAT sensor, and the documentation mentions it |
| SSTP, OpenVPN, L2TP/IPsec, ZeroTier, Tailscale, Back To Home | Certificates, heavier servers, extra packages or a third-party relay, with nothing gained over WireGuard |
| RouterOS API (8728), REST, SNMP as the management protocol | No `/export`, extra policies and services. SSH covers export, SFTP and every read (§4.5) |

### 2.4 What the code base already provides

The UDP-node integration (docs/udp-iot-plan.md) solved most of the structural questions. This plan reuses its patterns rather than inventing new ones.

| Building block | Where | Used for |
|---|---|---|
| A sensor type with a 1:1 table, created and validated by `SensorService` | `SensorType.UDP_NODE`, `udp_nodes`, `SensorService.ValidateAsync` | `MIKROTIK = 80` and `mikrotik_routers` (§9.1) |
| Event writing with the sensor API's rules: an advisory lock, the dedupe and the 2000-event limit | `PNMTD/Udp/SensorEventWriter.cs` | The poller's events. It moves out of `Udp/` to a neutral namespace (§5.3) |
| Timer-based monitoring tasks, one run at a time, paused while PNMTD's own uplink is down | `PingCheckTask`, `RunGuard`, `TaskRunDecisionMaker` | `RouterPoller` (§5.2), plus the same pause while the gateway is down |
| Task health on System health | `SystemService.TasksAsync` with the `LAST_*` key values (`KeyValueKeyEnums`) | `LAST_MIKROTIK_POLL_RUN` and the gateway's heartbeat |
| Secrets encrypted with Data Protection, with the key ring on `pnmtd-keys` | `TotpSecrets` (`PNMTD/Auth/TwoFactor.cs`) | Every private key (§9.6) |
| Admin-only jobs per device, with a permanent log that outlives the device | `UdpOtaService`, `udp_ota_jobs`, `udp_command_log` (`ON DELETE SET NULL`) | Update jobs and `mikrotik_job_log` (§7) |
| Audit log entries | `AuthEvents.Udp*` in `UserAccounts.cs`, written through `UdpViews.Audit` | `AuthEvents.Mikrotik*` and `WireGuard*` (§9.6) |
| Roles and policies, with a test matrix of every endpoint and an anonymous allowlist | `Policies`, `PNMTD.Tests/Hosting/EndpointAuditTests.cs` | The new endpoints. **None of them is anonymous** (§10) |
| An options section with DI setup | `UdpOptions`, `AddUdpNodes` | `WireGuardOptions`, `MikrotikOptions` (§9.4) |
| The command line in the image | `PNMTD/Cli/CommandLine.cs`, `pnmtd udp …` | `pnmtd mikrotik …`, and `pnmtd wireguard gateway` as the gateway's entry point (§12) |
| The hardened compose service, plus backup, info and init scripts | `deploy/docker-compose.yml`, `backup.sh`, `info.sh`, `init.sh` | The gateway service (§9.5). **`backup.sh` needs no change**: the database dump and the keys volume hold everything |
| Per-type subpages with their own routes, an admin page with tabs, copy fields, QR codes and a timeline | `SensorView.vue` (UDP tabs), `UdpNodesView.vue`, `CopyField.vue`, `QrCode.vue`, `CommandLogTimeline.vue` | The router page, the admin page, and custom tunnel downloads (§11) |
| Demo data | `pnmtd dev seed` | Seeded routers and tunnels (§12) |

**What is missing:**

- an SSH client;
- any outbound connection that PNMTD manages, rather than one it answers;
- **X25519 key generation** for WireGuard, and Ed25519 for SSH. .NET 10 has neither, so a library is needed (§9.3);
- a text diff;
- a job that holds a secret only in memory;
- anything that runs with `NET_ADMIN`, or programs a firewall.

---

## 3. Connection and the WireGuard network

### 3.1 Modes

| Mode | PNMTD connects to | Features | Needs |
|---|---|---|---|
| **WireGuard + SSH** (default) | The router's tunnel address, through the gateway | **All of them:** monitoring, backups, updates, remote commands, custom tunnel access | The compose profile (§9.5), and a router that can reach PNMTD's external WireGuard endpoint |
| **WireGuard only** | – (no login) | **The tunnel's state** (handshake, endpoint, traffic) as the sensor's state, and custom tunnel access. No version, backups, updates or commands | As above |
| **Direct SSH** | A host name or address and port that the admin enters | Monitoring, backups, updates and remote commands. No custom tunnel access | A route from the PNMTD container to the router, and SSH allowed from PNMTD's address |

- **The mode can be changed later.** The join script changes with it, and pasting the new script on the router switches over. For example, a router can start as WireGuard only and get the `pnmt` user later.
- **The join script of each mode** is in §4.3.

### 3.2 The tunnel network

```
router, behind NAT                               PNMTD host (maybe behind NAT itself)
 interface pnmt (WireGuard)                       pnmtd-wg: wg0 10.253.0.1/16 and 10.253.0.2
   10.253.0.17/32 ── UDP to the external endpoint ──► published port → listen port 51820
   peer: PNMTD, allowed-address 10.253.0.0/30       peers: routers and custom tunnels, a /32 each
   route 10.253.0.0/30 via pnmt                     firewall: deny by default (§3.5)
   persistent-keepalive 25 s                         SOCKS5 :1080, compose network only
 firewall: accept tcp/22 from 10.253.0.1               ▲
 (accept management ports from 10.253.0.2)             │ SOCKS5 CONNECT 10.253.0.17:22
 user pnmt: address=10.253.0.1/32                   pnmtd (the app): SSH.NET
                                                     
technician's laptop (custom tunnel) 10.253.0.40 ──► gateway: allowed? → SNAT to 10.253.0.2 → router :8291
```

- **One prefix per installation**, default `10.253.0.0/16`, set on the admin page (§3.3).
- **The server block: the first /30 of the prefix.**
  - **.1, the server address**, is where PNMTD's SSH connections come from. The `pnmt` user may log in only from it.
  - **.2, the access address**, is where custom tunnels' traffic comes from (the gateway rewrites it, §3.5), so a router can tell the two apart.
- **Routers and custom tunnels** get the next free addresses after the server block, and keep them for their lifetime.
- **The router routes only the server block into the tunnel** (`10.253.0.0/30`). Its LANs, default route and routing rules stay untouched. The only possible collision is a LAN that contains these addresses, and then the prefix is changed.
- **The router dials PNMTD's external endpoint** (§3.3). `persistent-keepalive` (default 25 seconds) keeps the NAT mapping open, so PNMTD can reach the router at any time.
- **The gateway's side has no endpoint for its peers.** It learns each endpoint from the peer's handshake, and follows it when the peer's public address changes.
- **Keys:** PNMTD generates every key pair: the gateway's, each router's and each custom tunnel's. Each private key is encrypted with Data Protection (§9.6).
  - A router's private key goes into its join script (§4.3), and a custom tunnel's into its configuration file (§3.5).
  - Both can be shown again, and both can be **regenerated**. A regenerated key replaces the old one in the gateway at once, so the router or tunnel stays offline until it gets the new script or file.
- **Tunnel state.** Every 15 seconds the gateway reads `wg show wg0 dump`, and stores each peer's latest handshake, endpoint, byte counters and **endpoint changes** (§9.2).
  - A live tunnel with a keepalive handshakes at least every 2 minutes.
  - A handshake older than the stale limit (default 3 minutes) means the tunnel is down.
  - An endpoint that keeps changing between two addresses means **two devices share one key** (a copied script), and the admin page warns about it (§9.6).

### 3.3 Network settings: the UI and the environment

**Everything about the tunnel network is set on the admin page** (Admin, audit-logged), with sensible defaults. **The external endpoint can also come from the environment**, because PNMTD may run behind NAT or a router, where only the operator knows what is reachable from outside. **A value set in the environment wins.** The admin page shows it locked, with the variable's name.

| Setting | Default | Set by | Notes |
|---|---|---|---|
| **External host** | `PublicBaseUrl`'s host | `PNMTD_WG_EXTERNAL_HOST`, or the UI | What routers and custom tunnels dial (`endpoint-address`, `Endpoint`). **A DNS name is recommended**, so a new public IP address needs no new scripts |
| **External port** | the published port | `PNMTD_WG_EXTERNAL_PORT`, or the UI | Different from the published port when an upstream router forwards, for example, `51999 → 51820` |
| Published port | 51820 | `PNMTD_WG_PORT` (compose) | The host port mapped to the gateway's listen port. Shown read-only |
| Published address | 0.0.0.0 | `PNMTD_WG_BIND_ADDRESS` (compose) | Shown read-only |
| Listen port | 51820 | fixed, inside the gateway's container | Shown read-only |
| **Prefix** | `10.253.0.0/16` | the UI | Changing it **re-addresses every peer**. Every router needs its new join script and every custom tunnel its new file. The UI lists the peers concerned and asks for the new prefix to be typed as confirmation |
| **Keepalive** | 25 s | the UI | Used in scripts and files |
| **MTU** | 1420 | the UI | For links with a smaller MTU (PPPoE, LTE) |
| **Stale handshake** | 180 s | the UI | When a tunnel counts as down |
| **Management ports** | 22, 8291 (WinBox), 80, 443 | the UI | What custom tunnels may reach on routers (§3.5) |

- **A changed endpoint, keepalive or MTU** applies to new scripts and files at once. **Routers that joined before keep their old values**, because the read-only user cannot change them.
  - With WireGuard + SSH, the poll reads the router's peer settings, so the admin page lists exactly which routers are **outdated**, and their Setup tab offers the new script.
  - With WireGuard only, PNMT lists the routers whose script was generated before the change.
- **The overview shows the endpoint in effect and where each value came from** (environment, UI or default). It also warns when no external host is known: `PublicBaseUrl` is empty and nothing is set.

### 3.4 The WireGuard gateway (`pnmtd-wg`)

**Why a second service:** the app runs as a non-root user with `cap_drop: [ALL]`, `read_only` and `no-new-privileges` (`deploy/docker-compose.yml`). WireGuard interfaces and firewall rules need `CAP_NET_ADMIN`. A non-root process could not use that capability even if the container had it. So the interface lives in a service of its own that has that one capability and nothing else.

| Aspect | Design |
|---|---|
| Image | **The same image**, started as `pnmtd wireguard gateway` (a CLI command, like `pnmtd udp node`). The runtime stage of the Dockerfile adds `wireguard-tools`, `iproute2` and `nftables`, a few MB |
| Privileges | Root inside its own container, with `cap_drop: [ALL]`, `cap_add: [NET_ADMIN, DAC_READ_SEARCH]`, `read_only` and `no-new-privileges`. `DAC_READ_SEARCH` because root without capabilities cannot read the key ring, which the app's non-root user owns (0600); found in M3, see §13.1. It needs the kernel's WireGuard module, which has been in Linux since 5.6 |
| Configuration | **Read from PostgreSQL**, with the app's database settings:<ul><li>the network (`wg_network`) and the gateway's private key, decrypted with the key ring, which is mounted read-only from `pnmtd-keys`;</li><li>the peers (`wg_peers`: enabled, not expired, and for routers an enabled sensor in a WireGuard mode);</li><li>the custom tunnels' access rules.</li></ul>It applies peers with `wg syncconf`, which changes only what differs and keeps live sessions, and the firewall with one atomic `nft -f`. It reacts to `LISTEN wireguard_changed` (the app sends a `NOTIFY` after every change) and resyncs fully every 30 seconds |
| Start order | The **nftables ruleset with a drop policy is loaded before `wg0` exists**, so there is never a moment in which forwarding is open |
| Data path for PNMTD | **A SOCKS5 server** on the compose network, with CONNECT only (RFC 1928) and a username and password (RFC 1929). The password is a random secret in `wg_network`. It connects only to the tunnel address of a router in WireGuard + SSH mode, and only to that router's SSH port. SSH.NET and .NET's `HttpClient` both speak SOCKS5. About 200 lines |
| Status | Every 15 seconds it writes the peers' handshakes, endpoints, counters and endpoint changes to `wg_peer_status`, and its own heartbeat (version, errors) to `wg_network.gateway_seen_at` |
| When it is down | No heartbeat for 60 seconds means the gateway is down. **After a restart**, tunnels without a fresh handshake get one stale-handshake period to reconnect before any event (M3: a router behind NAT took minutes). **The poller then writes no events for tunnel routers**, just as the uplink guard does while PNMTD's own link is down (`TaskRunDecisionMaker`). The admin page's overview and System health show the gateway as down (§16.2 question 6) |

**Considered and rejected:**

- **Sharing the gateway's network namespace with the app** (`network_mode: service:pnmtd-wg`). It needs no proxy code. But every published port would move to the gateway, the base compose file would change for everyone, and restarting the gateway would cut the app off the network.
- **A userspace WireGuard with a userspace TCP stack** (wireguard-go with netstack). It needs no capability and no kernel module. But it adds a Go component to a .NET project, cannot forward traffic for custom tunnels, and its peers cannot change without a restart.
- **WireGuard on the host, set up by the operator.** It works, but nothing could be configured in the UI.

### 3.5 Custom tunnels

A custom tunnel is a WireGuard peer that is not a router. It is typically a technician's laptop or phone, which gets into routers behind NAT with WinBox, SSH or WebFig, through PNMT.

- **Created by an Admin** on the admin page with:
  - a name and notes;
  - an **owner** (optional): a PNMT user, who then sees the tunnel under *Account → Tunnels* and may download it;
  - **access:** all routers, or selected routers, each only if its *management access* is on (below);
  - an **expiry** (optional). After it, the gateway drops the peer and the tunnel is disabled.
- **PNMTD generates the key pair** and an address. **The configuration is ready for download at once:**
  - as a **`wg-quick` file** (`<name>.conf`), for the WireGuard apps on Windows, macOS and Linux, or for `wg-quick up`;
  - as a **QR code**, for the mobile apps, with the existing `QrCode.vue`.

  ```ini
  # PNMT tunnel "alice-laptop" (pnmt.example.com). Contains a private key: keep it like a password.
  [Interface]
  PrivateKey = <generated>
  Address = 10.253.0.40/32
  MTU = 1420

  [Peer]
  PublicKey = <the gateway's public key>
  Endpoint = vpn.example.com:51820
  AllowedIPs = 10.253.0.0/16          # only the tunnel network: the laptop's other traffic is untouched
  PersistentKeepalive = 25
  ```

- **Downloads** are audit-logged, and so is every change. *Regenerate keys* makes earlier downloads useless.
- **What it reaches**, enforced by the gateway's nftables:
  - **forward: drop by default**;
  - allow *custom tunnel → router* on the **management ports** (§3.3), for the routers in its access list that have management access on;
  - replies (`ct state established,related`);
  - nothing else. **Routers cannot reach each other or any custom tunnel, and custom tunnels cannot reach each other** or the compose network.
- **Address rewriting.** The gateway rewrites the source of custom-tunnel traffic towards routers to the **access address** (.2). This has two effects:
  - the router needs only the server block in its `allowed-address`, and one firewall rule;
  - the `pnmt` user, bound to .1, can never be used through a custom tunnel, even with its key.
- **Management access on the router** (a router setting, off by default, Admin). It adds one firewall rule to the join script: accept the management ports from the access address on the `pnmt` interface. Routers in WireGuard only mode can have it, and remote access is then their whole purpose. Routers in Direct SSH mode cannot.
- **Monitoring:** the admin page lists every custom tunnel with its handshake, endpoint, traffic and expiry. Custom tunnels are not sensors (M8).
- **Which router answers on which address:** the routers list shows each tunnel address, with a *Copy WinBox address* button. Name resolution inside the tunnel is M8.

### 3.6 Direct SSH

- **Address and port:** a host name, IPv4 or IPv6 address, and a port (default 22).
- **The source address:** the `pnmt` user's `address` restriction should be PNMTD's source address as the router sees it. The admin enters it, because behind Docker's NAT PNMTD cannot know it. Left empty, the user is not restricted by address, and the UI warns about that.
- **Host key:** pinned at the first contact, as with WireGuard (§4.4). Without a tunnel, that first contact is not authenticated, so the UI shows the fingerprint for the admin to compare with the router.
- **The join script** creates only the `pnmt` user. Opening SSH to PNMTD in the router's firewall is up to the admin, and the script prints a commented example line.

---

## 4. Access on the router

### 4.1 The version floor: 7.13

**The request sets the floor, and it fits.** Everything this plan relies on exists in 7.13:

| Feature | Since | Used for |
|---|---|---|
| WireGuard, with `private-key=` supplied, and a host name as `endpoint-address` (retried after a failed lookup since 7.10) | 7.1 | The tunnel (§3.2) |
| `/file add … contents=` | 7.9 | Creating the key file in the join script (§4.3) |
| `:rndstr` with better randomness (it existed before) | 7.12 | The `pnmt` user's throwaway password |
| Ed25519 public keys for users | 7.12 | PNMTD's SSH key (§4.2) |
| `:serialize to=json` | **7.13** | Structured reads (§4.5) |

**Known changes after 7.13** that the implementation must handle:

| Version | Change | Consequence |
|---|---|---|
| 7.14 | "Fixed configuration export when the user does not have the sniff policy" | **M0: on 7.13 the export works without `sniff`**; only the CALEA sections come out as `#error exporting "/ip/firewall/calea"` lines, which are stable. No extra policy (§15) |
| 7.14 | One WireGuard interface per listen port | The join script picks a port that no other interface uses (§4.3) |
| 7.14.2 | `/user ssh-keys add` requires the `policy` policy | The admin who runs the script has it |
| 7.20.0–7.20.2 | Non-interactive command execution was broken; 7.20.3 fixed it | **PNMTD's reads and remote commands may fail on those three versions.** M0 tests them (§15) |
| 7.21 | `/ip ssh always-allow-password-login` became `password-authentication` (default `yes-if-no-key`). `print` hides sensitive values too, unless `show-sensitive` is given | PNMTD never sets either parameter. It reads only values that are not sensitive |
| 7.22 | Fixed private key generation when a WireGuard interface is created | Not relevant: the script supplies the key |
| 7.24.0 | A regression in WireGuard peer enable and disable, and in private key handling; 7.24.1 fixed both | The fleet list flags 7.24.0, and M0 tests a supplied key on it |
| 7.25 | The SSH login-name parameters broke; 7.25.1 fixed them | PNMTD uses no login-name suffix unless M0 shows it needs one (§4.5) |

- **M0 runs every command of §4–§7** on 7.13, on 7.20.2 and on the current stable release (7.25.1 at the time of writing), with the rights of §4.2 and over a non-interactive SSH exec channel. It records the output of each as test fixtures (§14).
- **The first login checks the version.** Below 7.13, a router in an SSH mode gets 441 "RouterOS 7.12.1 is below 7.13", and backups and updates stay off (§16.2 question 9).

### 4.2 The `pnmt` user, and what each operation needs

```
/user group add name=pnmt-read policy=ssh,read comment="pnmt:7f3a"
/user add name=pnmt group=pnmt-read address=10.253.0.1/32 password=[:rndstr length=32] comment="pnmt:7f3a"
/file add name=pnmt.pub contents="ssh-ed25519 AAAA… pnmt@<Identity>"
/user ssh-keys import user=pnmt public-key-file=pnmt.pub
/file remove pnmt.pub
```

- **Policies:** only `ssh` and `read`. The user has no `write`, `policy`, `reboot`, `ftp`, `sensitive`, `sniff`, `api`, `rest-api`, `web`, `winbox`, `telnet` or `local`. RouterOS 7 has 17 policies in all. Only a router's opt-ins add one: `sensitive` for secrets in exports (§6.6), and `test` for diagnostics (§8.4).
- **`address`:** the user may log in only from the server address (§3.2), or in Direct SSH mode from PNMTD's source address (§3.6). A stolen SSH key is useless from anywhere else, including from a custom tunnel.
- **Authentication by key.** **One key pair per PNMT installation**, Ed25519, which RouterOS accepts for users since 7.12. The key comment names the instance, so the router's admin can recognize it.
  - The private key is protected with Data Protection (§9.6).
  - The key is imported from a file, because `/file add` (7.9) and `import` both exist on 7.13. `/user ssh-keys add key="…"` is confirmed only from 7.14.2 on.
- **The password is random**, generated on the router with `:rndstr`, so it is never stored and is not in the script. Nobody needs it:
  - by default, RouterOS refuses a password over SSH once the user has a key (`always-allow-password-login=no`, and from 7.21 `password-authentication=yes-if-no-key`);
  - the group has no other way in (`winbox`, `web`, `api`, `ftp`, `telnet` and `local` are all missing).
- **Rotating the SSH key** (Admin, §10) creates a new pair. Every router's join script then contains the new key, and the admin page lists the routers that still accept only the old one. PNMTD offers both keys until the old one is retired, because a read-only user cannot add keys for itself.

**What each operation needs:** the answer to "might backups need more rights?"

| Operation | Commands (a sketch; M0 confirms each) | Policies | Runs as |
|---|---|---|---|
| Poll the status | `/system resource`, `/system routerboard`, `/system package update`, `/system identity`, `/system health`, the `pnmt` WireGuard peer, the `pnmt-read` group, each as `:serialize to=json` of `print as-value` | `ssh`, `read` | `pnmt` |
| **Export the configuration, without secrets** | `/export terse`. RouterOS 7 hides sensitive values by default | `ssh`, `read`. 7.13 may also need `sniff` (§4.1, *M0*) | **`pnmt`: no extra rights** |
| Export with secrets | `/export terse show-sensitive`. Without the `sensitive` policy, the values come out as `*****` | `+ sensitive` | `pnmt`, only if the router opts in (§6.6) |
| **Binary backup** (`.backup`) | `/system backup save … password=…`, SFTP download, `/file remove` | **M5 on 7.25.1: `write`, `policy`, `sensitive` and `test` for the save, `ftp` for SFTP** (not `reboot`) | **The router's admin, inside an update job** (§6.5) |
| Check for updates on the router | `/system package update check-for-updates` | **M5 on 7.25.1: `policy`** (refused to `pnmt`) | Not needed: PNMTD checks centrally (§5.4) |
| Install an update | `set channel` (`write`), `check-for-updates` (`policy`), `download`, then `/system reboot` (`reboot`; over exec it asks nothing and reboots at once) | **`ssh`, `read`, `write`, `policy`, `reboot`**; PNMTD checks them before a job exists (§7.2) | The router's admin (§7) |
| RouterBOARD firmware | `/system routerboard upgrade`, then a reboot | `write`, `reboot` *(M0)* | The router's admin, inside the update job |
| **Scan for the network map** (docs/network-map-plan.md §4.1) | `/interface`, `/interface ethernet` and `monitor … once`, the bridges, VLANs and bonds, `/interface bridge host`, `/ip neighbor` and its discovery settings, `/ip arp`, `/ip dhcp-server lease`, `/ip address`, the WireGuard prints without keys and the EoIP, GRE and IPIP tunnels, the Wi-Fi registration tables; each as `:serialize to=json` of `print as-value`. Confirmed on CHR 7.13 and 7.25.1 as a user of the `pnmt-read` group | `ssh`, `read` | `pnmt` |
| **Run a command** (§8) | The text, as typed | Those of the user it runs as. `pnmt`: `ssh`, `read` | `pnmt`, or the router's admin with the password typed for the run |
| Ping, traceroute | `/ping`, `/tool traceroute` | `test` | `pnmt` with *Diagnostics* (§8.4), or the router's admin |
| Join, change the mode, repair, remove | The join script and the removal script (§4.3, §4.6) | Everything the script creates: `write`, `policy` | **The router's admin, in the terminal**. PNMTD sees no password |

**So: automatic backups of the configuration as text need no more rights than monitoring. Only binary backups and secrets do.** Binary backups happen only inside update jobs, and secrets only by opting in. The single exception may be `sniff` on 7.13, which M0 settles.

### 4.3 Joining: one script

**Adding a router in the UI** creates the sensor, its address, and its keys. The gateway gets the peer at once. The router's **Setup** tab then shows the **join script**, with *Copy* and *Download as .rsc* buttons. The admin either:

- pastes it into a terminal on the router (WinBox, WebFig or SSH) while signed in as an admin; or
- uploads the `.rsc` file and runs `/import file-name=pnmt-join.rsc`.

**WireGuard + SSH** (the full script, as `JoinScript` generates it; the waits and the move are what M0 found necessary, §13.1):

```routeros
# PNMT join script for "office-gw" ("pnmt.example.com", generated 2026-11-13). Mode: WireGuard + SSH.
# It contains this router's WireGuard private key: keep it like a password.
# Everything it creates carries the comment "pnmt:7f3a" (this installation): pasting it again replaces it,
# and so does what an earlier PNMT installation created (a comment pnmt:…).
/ip firewall filter remove [find where comment~"^pnmt:"]
/ip route remove [find where comment~"^pnmt:"]
/ip address remove [find where comment~"^pnmt:"]
/interface wireguard peers remove [find where comment~"^pnmt:"]
/interface wireguard remove [find where comment~"^pnmt:"]
/user ssh-keys remove [find where user="pnmt"]
/user remove [find where comment~"^pnmt:"]
/user group remove [find where comment~"^pnmt:"]
{ :local n 0; :while ([:len [/interface find where name="pnmt"]] > 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
{ :local port 13231; :while ([:len [/interface wireguard find listen-port=$port]] > 0) do={ :set port ($port + 1) }; /interface wireguard add name=pnmt listen-port=$port mtu=1420 private-key="<the router's private key>" comment="pnmt:7f3a" }
{ :local n 0; :while ([:len [/interface find where name="pnmt"]] = 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/interface wireguard peers add interface=pnmt public-key="<the gateway's public key>" endpoint-address="vpn.example.com" endpoint-port=51820 allowed-address=10.253.0.0/30 persistent-keepalive=25s comment="pnmt:7f3a"
/ip address add address=10.253.0.17/32 interface=pnmt comment="pnmt:7f3a"
/ip route add dst-address=10.253.0.0/30 gateway=pnmt comment="pnmt:7f3a"
/ip firewall filter add chain=input in-interface=pnmt src-address=10.253.0.1 protocol=tcp dst-port=22 action=accept comment="pnmt:7f3a"
/ip firewall filter add chain=input in-interface=pnmt src-address=10.253.0.2 protocol=tcp dst-port=22,8291,80,443 action=accept comment="pnmt:7f3a"
{ :local n 0; :while ([:len [/ip firewall filter find where comment="pnmt:7f3a" and invalid]] > 0 and $n < 50) do={ :delay 100ms; /ip firewall filter set [find where comment="pnmt:7f3a" and invalid] in-interface=pnmt; :set n ($n + 1) } }
{ :local all [/ip firewall filter find where dynamic=no]; :if ([/ip firewall filter get [:pick $all 0] comment] != "pnmt:7f3a") do={ /ip firewall filter move [find where comment="pnmt:7f3a"] destination=[:pick $all 0] } }
/user group add name=pnmt-read policy=ssh,read comment="pnmt:7f3a"
/user add name=pnmt group=pnmt-read address=10.253.0.1/32 password=[:rndstr length=32] comment="pnmt:7f3a"
/file remove [find name="pnmt-1.pub"]
{ :local n 0; :while ([:len [/file find where name="pnmt-1.pub"]] > 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/file add name="pnmt-1.pub" contents="ssh-ed25519 AAAA… pnmt-567ee5@pnmt.example.com"
{ :local n 0; :while ([:len [/file find where name="pnmt-1.pub"]] = 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/user ssh-keys import user=pnmt public-key-file="pnmt-1.pub"
/file remove [find name="pnmt-1.pub"]
:foreach s in=[/ip service find where name="ssh" and disabled=yes] do={ :put "PNMT: warning: the SSH service is disabled. Enable it with: /ip service enable ssh" }
:put "PNMT: joined as 10.253.0.17, dialling vpn.example.com:51820. PNMT logs in within a minute."
```

**What each mode's script contains:**

| Part | WireGuard + SSH | WireGuard only | Direct SSH |
|---|---|---|---|
| Removing what an earlier paste created (unless the option is off, below) | ✓ | ✓ | ✓ |
| WireGuard interface **with the private key**, the peer, the address and the route | ✓ | ✓ | – |
| Firewall: SSH from the server address | ✓ | – | – (a commented example) |
| Firewall: management ports from the access address, with management access | ✓ | ✓ | – |
| The `pnmt` group, user and SSH key (`address` = the server address, or PNMTD's source address) | ✓ | – | ✓ |
| `sensitive` in the group, with *Include secrets* (§6.6) | ✓ | – | ✓ |
| `test` in the group, with *Diagnostics* (§8.4) | ✓ | – | ✓ |

**Two options** (docs/mikrotik-join-options-plan.md §2), in the Setup tab next to the script, both on by default. Each router keeps the ones its script was last shown with, and with both on the script is exactly the one above:

- ***Firewall rules*** (WireGuard + SSH, and WireGuard only with management access): off, the script leaves the filter list alone. It adds, removes and moves no rule, and shows the rules as comments, and the comparison leaves the firewall out.
- ***Remove what an earlier script created***: off, the script removes nothing. It adds what is missing and sets what is there, found by name or by its comment, and imports PNMTD's key only if the user doesn't have it yet. What it no longer needs stays on the router.

**Rules:**

- **One paste, nothing to copy back, no admin password.** The admin who pastes it is signed in as an admin already. The Setup tab follows the steps live: *waiting for the router → tunnel up → logged in → ready*. The last two apply to the SSH modes.
- **The script holds a secret, the router's WireGuard private key**, per the review decision (§16.1). Therefore:
  - showing or downloading it needs CanWrite, and every view and download is audit-logged;
  - it can be shown again any time, because the key is kept encrypted (§9.6);
  - *Regenerate keys* (CanWrite) makes every earlier copy useless (§3.2).

  §4.4 covers what someone with a copy could do.
- **It is safe to paste again.** It first removes everything with its own comment, then creates everything anew, so it also **repairs** a router and **applies changes**: a new mode, endpoint, keepalive, MTU, management access, secrets, diagnostics or SSH key. Only objects whose comment starts with `pnmt:` are removed: this installation's, and those of an earlier PNMT installation, so a router can move between installations (§13.1). M0 confirmed the order of removal and that `gateway=pnmt` works as an interface route; `place-before=0` fails on an empty filter list, so the rules are added and then moved to the top. Without removals, it sets instead (J1 on 7.13 and 7.25.1, docs/mikrotik-join-options-plan.md §2.3).
- **It checks before it changes anything else.** If `/ip service ssh` is disabled, uses another port, or is limited to certain addresses, the script tells the admin and adjusts only what PNMT needs. PNMTD reads the SSH port at the first login *(M0)*.
- **The listen port** only has to be free, because the router dials out. Since 7.14, two WireGuard interfaces cannot share one, so the script takes the first free port from 13231 up.
- **Strings are quoted** by RouterOS's rules: names, comments and the endpoint. A test covers it (§14).

### 4.4 Host key pinning, and copies of the script

- **The first successful login pins the host key** (its type and SHA-256 fingerprint). It also stores the identity, model and serial number, which the Setup tab shows, so the admin can see that the right router answered.
- **A changed host key refuses the connection** and gives 441 "Host key changed". An Admin can accept the new key in the UI, which is audit-logged, for example after the router was replaced or reinstalled.
- **An admin password is only ever sent to a pinned host key** (§7.2).
- **A copy of the join script** lets another device join in the router's place. It can do no harm to the real router, but it could pose as the router:
  - **If it joins after the real router,** its host key differs: 441, and no update goes to it.
  - **If it joins first,** its identity and serial number on the Setup tab are not the router's. The update dialog shows them again before the admin types a password.
  - **If both run at once,** they take turns at the gateway, and the endpoint changes give it away (§3.2).
  - **The remedy is *Regenerate keys*.** The audit log shows who viewed the script.

### 4.5 Talking to RouterOS over SSH

- **SSH.NET** (MIT): exec channels and SFTP, Ed25519 and RSA keys, SOCKS5, and an event for checking the host key.
- **One session per router at a time** (a semaphore per router). **At most `MaxConcurrentSessions` at once** (default 8).
- **Timeouts:** 15 seconds to connect, 60 seconds per command, 120 seconds for an export.
- **Structured reads, as JSON.** A poll is one exec of a short script that prints one JSON document per menu: `:put [:serialize to=json [/system/resource/print as-value]]`, and so on. `:serialize` exists from 7.13, which is the floor.
  - Not `:put` of an `as-value` array directly: it prints `key=value;key=value`, but list values are joined with `;` too, so the output is ambiguous.
  - The values inside are still RouterOS formats, so `PNMTD.Lib` has parsers for durations such as `1w2d03:04:05`, sizes such as `12.5MiB`, and versions such as `7.16.1`, `7.17beta2` and `7.15rc3`, compared correctly.
- **Clean output:** RouterOS accepts login-name parameters (`pnmt+ct80w`: `c` no colours, `t` no terminal detection, `e` dumb mode, `<N>w` the width). JSON over a non-interactive exec should not need them. Width has only been honoured for non-interactive commands since 7.22, and 7.25 broke the parameters until 7.25.1, so PNMTD uses them only if M0 shows they are needed.
- **Output is untrusted.** The router may be compromised, so:
  - output is limited in size (16 MiB for an export, 1 MiB otherwise);
  - nothing is ever evaluated;
  - the UI escapes everything, as Vue does by default.
- **No input from users reaches PNMTD's own commands.** They are constants. The only variable part is the channel (an enum) during updates. **Remote commands are the one deliberate exception** (§8): their text goes to the router as typed, in a session of its own, and is never combined with PNMTD's commands.

### 4.6 Removing a router

**Deleting the sensor** removes its peer from the gateway at once, and the router loses the tunnel. The Setup tab, and the deletion dialog, also show a **removal script**, which removes everything with the installation's comment: the interface, peer, address, route, firewall rules, user, group and key. PNMTD cannot remove these itself, because the `pnmt` user has no write rights. Backups and job logs stay (§9.2).

---

## 5. Monitoring

### 5.1 What is read

| Value | From | Shown | Used for |
|---|---|---|---|
| Version, channel | `/system resource` `version`, `/system package update` `channel` | Status strip, routers list | 242, 244, update jobs |
| Uptime, boot time | `uptime`; boot time = now − uptime | Status strip | **241 after a restart** |
| CPU load, memory, storage (free and total), architecture, CPU | `/system resource` | Status strip | Later: threshold checks (M8) |
| Flash wear (`write-sect-total`, `bad-blocks`) | `/system resource` | Detail | Later |
| Identity, model, board, serial number | `/system identity`, `/system routerboard` | Header, routers list | Confirming the router (§4.4) |
| RouterBOARD firmware: current and upgrade | `/system routerboard` | Status strip | Update jobs (§7.3) |
| Temperature, voltage (where present) | `/system health` | Detail | Later |
| **The router's own PNMT settings**: endpoint, keepalive, MTU, the group's policies, the management rule | The `pnmt` interface and peer, the `pnmt-read` group, the tagged firewall rules | Setup tab | **"Script outdated"** (§3.3) |
| The tunnel from the gateway's side: handshake, endpoint, counters, endpoint changes | `wg_peer_status` | Status strip | **440 with a reason**, and the whole state in WireGuard only mode |

The latest reading is stored with the router (`mikrotik_routers`, plus a `resources` jsonb column for everything else). **There is no time series** in this plan: events record the changes. Graphs are M8.

### 5.2 Schedule and concurrency

- **The Interval** is the poll interval (default 300 seconds, minimum 60). **The GracePeriod** is how long polls may fail before the router counts as unreachable (default 600 seconds, so a reboot rarely gives 440).
- **`RouterPoller`** is a hosted service in the style of `PingCheckTask`: a timer every 10 seconds, `RunGuard`, and `TaskRunDecisionMaker`. It picks the routers that are due and spreads them with jitter, so 500 routers do not all start in the same second. It runs under `BackgroundTasks:Enabled`, like the other monitoring tasks.
- **In WireGuard only mode,** a poll is a look at `wg_peer_status`, with no login.
- **Pauses:**
  - while PNMTD's uplink is down (the existing guard);
  - for routers in a WireGuard mode, while the gateway is down (§3.4);
  - for a router whose job is running, an update or a command: the job owns the router, also during its reboots (§7.3, §8.2).
- **Exports run on their own schedule** (§6), in the same session as a poll whenever both are due.
- **`HeartbeatCheckTask` does not cover `MIKROTIK`.** The poller writes its own failures, each with a reason. A stopped poller shows on System health as a late task (`LAST_MIKROTIK_POLL_RUN`).
- **The load:** 500 routers every 5 minutes is under 2 SSH sessions a second, each 1–2 seconds long, so about 3 at a time.

### 5.3 Events and status codes

**The sensor's state is the worst current condition**, written at every poll. The existing dedupe keeps a steady state at two rows that only move their time. **Changes** (a restart, an update, a configuration change, a new version, a failed job) are single events in between.

| Code | Name | Example message | When |
|---|---|---|---|
| **240** | `MIKROTIK_OK` | "RouterOS 7.16.1 (stable) · hAP ax³", plus " · 7.16.2 available" when there is an update. In WireGuard only mode: "Tunnel up (WireGuard only)" | Every successful poll. The message changes only with the version, the model or an available update, so the dedupe holds |
| **241** | `MIKROTIK_RESTARTED` | "Restarted at 10:42 (RouterOS 7.16.1)" | The boot time moved by more than 2 minutes |
| **242** | `MIKROTIK_UPDATED` | "Updated 7.15.3 → 7.16.1" | The version changed, by a PNMT job or by anyone else |
| **243** | `MIKROTIK_CONFIG_CHANGED` | "Configuration changed: +12 −3 lines (version 42)" | A backup found a change (§6.2) |
| **244** | `MIKROTIK_UPDATE_AVAILABLE` | "7.16.2 available on stable (7.16.1 installed)" | Once for each new version (`notified_update_version`) |
| **440** | `MIKROTIK_UNREACHABLE` | "Unreachable for 12 min: no WireGuard handshake since 10:04" / "SSH: connection timed out" | Polls failed, or the handshake was stale, for the GracePeriod |
| **441** | `MIKROTIK_ACCESS_DENIED` | "SSH login as pnmt refused" / "Host key changed" / "The pnmt group lacks read" / "RouterOS 7.12.1 is below 7.13" | At once, because it does not heal by itself |
| **442** | `MIKROTIK_BACKUP_FAILED` | "Export failed twice: timed out after 120 s" | While the last 2 backups failed. It counts as the current condition, so it stays until a backup succeeds |
| **443** | `MIKROTIK_UPDATE_FAILED` | "Update to 7.16.2 failed at Download: not enough storage" | A job failed (§7.5). A single event: the next poll writes the current condition again |

- **All of them are new constants in `PNMTStatusCodes`**, and the Documentation page lists them (`SystemService.StatusCodes`). The ranges 240–244 and 440–443 are free today.
- **The order of conditions:** 440 before 441 before 442 before 240.
- **"Script outdated" is not an event.** It is a hint on the Setup tab and the admin page (§3.3), because the router still works.
- **Notifications:** the most common rule, *failures on change and back to OK*, fires only on 44x transitions and on the way back to 240. The changes 241–244 are success codes, so they do not disturb it. A rule of type *always* or *on change* also sees them, and the documentation explains this.
- **The event source** is `mikrotik:<tunnel address>` or `mikrotik:<host>`, so the dedupe compares like with like.
- **Event writing** uses `SensorEventWriter` (advisory lock, dedupe, 2000 events). It moves from `PNMTD/Udp/` to `PNMTD/Services/Events/`, unchanged.

### 5.4 Latest versions

- **`VersionCatalog`**, a hosted service, fetches the newest version of each channel from MikroTik's update server every 6 hours: `https://upgrade.mikrotik.com/routeros/NEWESTa7.<channel>`, for `stable`, `long-term`, `testing` and `development`. The results go to `mikrotik_settings.latest_versions`.
  - The answer is plain text: the version and its release time as a Unix timestamp, such as `7.25.1 1793623193`.
  - When this plan was written, stable was 7.25.1, long-term 7.24.6, and testing equal to stable.
- **Each router is compared with its own channel**, so a router on long-term is not told about stable.
- **The changelog** of the target version is shown in the Updates tab. It is fetched from `https://upgrade.mikrotik.com/routeros/<version>/CHANGELOG`: plain text, starting with "What's new in <version> (<date>):", where the date format varies between releases.
- **If the check fails**, System health shows it, and no 244 is written. Nothing else depends on it.
- `Mikrotik:VersionCheck:Enabled=false` turns it off, for example for an installation without internet access.

---

## 6. Configuration backups

Backups exist in the SSH modes only. A router in WireGuard only mode has no login.

### 6.1 Export

- **`/export terse`**, as `pnmt`. Terse output has one line per item with its full menu path (`/ip firewall filter add chain=input …`), so a diff names its own context.
- **When:**
  - every `backup_interval_hours` (default 24, from 1 to 8760), or never: the schedule may be off (docs/mikrotik-backup-plan.md §2);
  - before every update, as step 2 of §7.3;
  - before and after a command run as an admin, if chosen (§8.2);
  - on demand (CanWrite);
  - once after the first login, as version 1.
- **Limits:** 120 seconds and 16 MiB. Exports are typically 10–200 KB.

### 6.2 Change detection

- **Normalised before hashing:**
  - CRLF becomes LF;
  - the first line, `# 2026-11-12 03:00:01 by RouterOS 7.16.1`, is dropped, as is its older form with another date format;
  - the `# software id`, `# model` and `# serial number` lines stay.
- **A keyed hash (HMAC-SHA256) of the normalised text** is compared with the last stored export (docs/mikrotik-backup-plan.md §3.7; built as a plain SHA-256 first):
  - **Equal:** nothing is stored, and only `last_export_at` moves.
  - **Different:** a new version is stored, with the line counts against the previous one, and **243** is written. Version 1 gives no 243.
- **The join script's own objects** are in the export too. Pasting the script again with a change therefore shows up as a configuration change, which is correct.
- **M0 checks** that two exports a minute apart are identical after normalisation, and lists any lines that change on their own, if there are any.

### 6.3 Storage and retention

- Stored in **PostgreSQL**, as gzip (`mikrotik_backups.content`), so `backup.sh` includes them. About 20 KB per version and router. A router may have them encrypted one way, as age of the gzip (docs/mikrotik-backup-plan.md §3).
- **Retention:** every version younger than `KeepDays` (default 365), and at least the last `KeepVersions` (default 50) for each router, whatever their age. An hourly task deletes the rest.
- **Backups outlive the sensor** (`ON DELETE SET NULL`, with the router's name kept), as the UDP command log does. An Admin can delete them explicitly.

### 6.4 Diffs and downloads

- **A unified diff between any two versions**, by default the newest and the one before it. It is computed on the server (a small Myers diff in `PNMTD.Lib`) and colour-coded in the UI; for encrypted versions in the browser, with the same diff (docs/mikrotik-backup-plan.md §3.6).
- **Download** as `<identity>-<date>-v<version>.rsc`. Restoring is the admin's job: `/import` on the router, or copying single lines.
- **Who** may see them: §10 and §16.2 question 5.

### 6.5 Binary backups: only inside updates

- **`/system backup save` needs write rights** (§4.2), and the admin password is needed only for updates (§16.1). So **binary backups are taken only as step 3 of an update job** (an option, on by default), and never on schedule or on demand.
- **The steps:** save it as `pnmt-<job>.backup`, download it over SFTP, check its size, and remove it from the router's storage.
- **Encryption:** with the admin password of the job (`password=` of `backup save`) *(M0)*. **PNMTD stores nothing that decrypts it.** Restoring needs the password that was used, and the UI notes who took the backup and when.
- **What it is for:** it restores the same model, including MAC addresses and user passwords. It is not a document to read, and it is not diffed.

### 6.6 Secrets in exports

- **Off by default.** Without `show-sensitive`, the export contains no secrets (confirmed in M2 on 7.13 and 7.25.1; with `show-sensitive` but without the policy `sensitive`, RouterOS writes `private-key=*****`, so PNMTD adds `show-sensitive` only when the poll sees `sensitive` in the group, and only then marks the backup as holding secrets): no WireGuard private keys, PPP secrets, Wi-Fi passphrases or IPsec keys. User passwords are never exported in any case.
- **Per router, *Include secrets in exports*** (Admin, audit-logged) adds `sensitive` to the group in the join script, and `show-sensitive` to the export. **It takes effect when the admin pastes the new script.** Until then, the poll sees that the group lacks `sensitive`, and the Setup tab says so.
- **The trade-off, shown next to the switch:** with secrets, an export restores the router completely, but every PNMT backup becomes as sensitive as the routers' own secrets. Exports with secrets are marked, and only Admins may see or download them (§10).

---

## 7. Updates

Updates exist in the SSH modes only.

### 7.1 Who and what

- **Admin only**, as with UDP firmware. Started from the router's Updates tab, or for several routers from the admin page (§11.2).
- **The inputs:**
  - the routers;
  - the target channel (default: each router's current one);
  - the router admin's **username and password**, one set for the whole batch (§16.2 question 10);
  - the options: a binary backup first (on), the RouterBOARD firmware afterwards (on), and for a batch, one router at a time with a stop at the first failure (on).
- **"Now" only.** Maintenance windows are M8.
- **PNMT never transports packages.** The router downloads them from MikroTik and checks their signatures itself. Unlike UDP OTA, no firmware bytes pass through PNMTD.

### 7.2 The admin password

**This is how a router's admin password is handled wherever it is used: in updates, and in commands run as an admin (§8.2).**

- **In transit:** from the browser to the API over HTTPS. **The API refuses a password over plain HTTP** (`Request.IsHttps`, behind a proxy with the `Proxy` setting) unless `Mikrotik:AllowPasswordsOverHttp=true`, for development. From PNMTD to the router it travels over SSH, only to a pinned host key (§4.4), and with WireGuard inside the tunnel.
- **Checked before the job exists.** PNMTD logs in with the password and reads the user's group and its policies. It refuses at once with what is missing, for example "wrong password" or "admin lacks reboot". The UI shows this per router, with the router's identity and serial number (§4.4).
- **In memory only:** `CredentialVault`, a singleton, maps the job, or the batch, to the username and password.
  - It is never serialized. The job row records only "credentials supplied by Alice at 10:41".
  - It is cleared when the job ends, is cancelled, or reaches `CredentialLifetimeMinutes` (default 60).
  - A restart of PNMTD loses it, by design.
- **Never in a log.** No request body is logged. Errors are mapped to messages that contain no input, and the audit entry contains no password. **A test checks this** (§14).
- **The UI** keeps the password only in the dialog's field (`autocomplete="off"`), never in a store or local storage.

### 7.3 The steps

| # | Step | As | What (a sketch, M0) | Fails when |
|---|---|---|---|---|
| 1 | **Check** | `pnmt` | A poll: reachable, at least 7.13, the target newer than the installed version, enough free storage | Unreachable, below 7.13, already up to date (ends as *Skipped*), too little storage |
| 2 | **Export** | `pnmt` | §6.1, trigger *BeforeUpdate* | The export fails |
| 3 | **Binary backup** (option) | admin | §6.5 | Save or download fails |
| 4 | **Download** | admin | `/system package update set channel=…`, `check-for-updates`, `download`. Then `status` is polled until the package is downloaded | An error status (for example storage), or 10 minutes |
| 5 | **Reboot** | admin | `/system reboot` over a non-interactive exec: M0 found that it asks nothing and reboots at once (7.25.1, as admin; as `pnmt` it does nothing and exits 0). M5 found the same on 7.13 | – |
| 6 | **Wait** | `pnmt` | A fresh handshake (WireGuard + SSH), then a login | No return within `RebootTimeoutMinutes` (default 15) |
| 7 | **Verify** | `pnmt` | The installed version equals the target | Still the old version: "the package was not applied", with a pointer to `/log` |
| 8 | **Firmware** (option, RouterBOARD only) | admin | If `current-firmware ≠ upgrade-firmware`: `/system routerboard upgrade` (which asks for confirmation interactively *(M0)*), a reboot, a wait, and a check. **The new firmware always needs this second reboot**, even with `auto-upgrade=yes`, which only saves the `upgrade` command | As in steps 5–7 |
| 9 | **Done** | – | Event **242**, a fresh export, and the password dropped | – |

- **Job states:** `Queued → Running (step n) → Succeeded | Skipped | Failed | Cancelled`.
- **The job log** (`mikrotik_job_log`, permanent): one row per step and outcome, with the time and the router's answer (at most 64 KiB, and never the password). The UI shows it as a timeline, as it does for UDP commands.
- **Cancelling** is possible until step 5 starts. After that, a cancel only stops the waiting.
- **The downtime** shown before the start: one reboot, two with the firmware, typically 1–5 minutes each. Custom tunnels to the router are interrupted for that time as well.
- **A second method, and an optional storage check** (added later, docs/ai-operations-plan.md §12.2): *Install* replaces steps 4 and 5 with `/system package update install`, which downloads, installs and reboots in one command. Verified on a CHR (7.24.5 to 7.25.1): over SSH it prints its progress, asks nothing, and returns with exit 0 when RouterOS reboots. Step 1's storage check can be turned off for a job, for routers with little flash that update anyway. Each router keeps its method and its storage choice as the default for the next update.
- **Security advisories** (docs/ai-operations-plan.md §10): MikroTik's release feed marks the routers behind a release with a security fix; a critical one raises an event on the *RouterOS advisories* sensor.

### 7.4 Several routers

- **One batch, one set of credentials**, checked on every router before anything starts. Routers that are refused are listed with the reason, and the rest may go ahead.
- **One router at a time by default, with a stop at the first failure:** the first router is the canary. Up to *N* in parallel is an option.
- **The routers list** shows the installed version, the latest version of the channel and the firmware, so "everything on stable that is behind" is one filter and one selection.

### 7.5 Failures

| Situation | Outcome |
|---|---|
| A wrong password or missing policies | Refused before the job is created (§7.2) |
| Not enough storage | Fails at step 1 or 4, and nothing has changed |
| **A failure after step 4** (a download) | **The downloaded package installs at the router's next reboot**, whoever causes it. The job log and the 443 message say so, with how to remove it (`/file remove` of the `.npk`) |
| The router does not return | 443 after the timeout. Shortly after, the poller gives 440 |
| The router returns on the old version | 443 "not applied". The `pnmt` user reads `/log` and the job log keeps the relevant lines |
| PNMTD restarts during a job | **As built (M5): the job fails at the restart**, because the password is gone with it. Before the reboot: "start the update again". From the reboot on, the message says that the update goes on without PNMTD, and the next poll shows the version (242). No 443 is written, because PNMTD does not know the outcome |
| A downgrade or a channel change | A downgrade is not offered. A channel change is offered, with a warning for testing |

---

## 8. Remote commands

**A RouterOS command or a short script, typed in PNMT and run on one router or several, with the output shown and kept in PNMT.** Remote commands exist in the SSH modes only. A router in WireGuard only mode has no login.

### 8.1 Two ways to run a command

| Run as | Rights on the router | Password | Who in PNMT | For |
|---|---|---|---|---|
| **`pnmt`** (the default) | The `pnmt-read` group: `ssh` and `read`, plus `test` with *Diagnostics* (§8.4). **The router refuses everything else** with "not enough permissions" | None | CanWrite | Looking: `print`, `/log print`, `monitor … once`, `/export` of one menu, and with *Diagnostics* `/ping` and `/tool traceroute` |
| **An admin of the router** | The admin's group | **Typed for the run**, held in memory only, exactly as for updates (§7.2) | **Admin** | Changing: anything that admin may do, such as `/ip dns cache flush`, a firewall rule, or `:execute {/system reboot}` |

- **PNMTD does not check, filter or rewrite the command.** It sends the text to the router as typed, in an SSH session of its own, and **the router enforces the rights of the user**. That is why a run as `pnmt` needs no password: a user without `write` cannot change the router, whatever the text says.
- **The admin password now has two uses: updates, and commands run as an admin** (decision 8, §16.1). Still nothing that can change a router is stored in PNMTD, so a compromised PNMTD still cannot change one (§9.6).
- Examples: `/ip dhcp-server lease print`, `/log print where topics~"error"`, `/interface wireguard peers print detail`, `/system script run backup-to-nas` (as an admin).

### 8.2 Running a command

- **The inputs:**
  - the routers, one or several;
  - **the text:** one command or several lines, at most 16 KiB;
  - **the user:** `pnmt`, or an admin with a username and password, one set for the batch, as with updates (§16.2 question 10);
  - **the timeout:** 60 seconds by default, from 5 seconds to 10 minutes;
  - **the options:**
    - **Back up before and after** (admin runs; on by default). An export before and one after (§6.1, triggers `BeforeCommand` and `AfterCommand`), so the result shows **exactly what the command changed** as a diff, and the 243 of that change names who ran the command and as whom (not its text: Viewers see events, and an event cannot be redacted);
    - **One router at a time, stop at the first failure** (on by default for admin runs, off for `pnmt` runs). The first router is the canary, as with updates (§7.4). Otherwise up to `Commands:Parallelism` routers run at once (default 4), within `MaxConcurrentSessions`.
- **A run is a job of kind `Command`** (§9.2): one job per router, all of them in one batch. The steps:

| # | Step | As | What |
|---|---|---|---|
| 1 | **Check** | `pnmt` | Reachable, the host key pinned, at least 7.13, and no update running on the router. **For admin runs, the credentials are checked on every router of the batch before anything runs**, and the user's group is shown (§7.2) |
| 2 | Export before (option) | `pnmt` | §6.1 |
| 3 | **Run** | the chosen user | One SSH exec of the text, in a session of its own. The output (stdout and stderr) is read until the command ends or the timeout |
| 4 | Export after (option) | `pnmt` | §6.1, with the line counts against step 2 |
| 5 | **Done** | – | The outcome. The password is dropped when the batch ends |

- **Outcomes:**
  - *Succeeded*;
  - *Failed*: RouterOS reported an error, by a non-zero exit status or by one of its error lines ("bad command name", "expected end of command", "syntax error", "not enough permissions", "failure:", …; *M0* records what each version gives);
  - *Timed out*, with the output read so far;
  - *Skipped*: step 1 failed;
  - *Cancelled*.
- **Several lines run as one script.** M0 found that a plain exec runs each line on its own (a block cannot span lines, a `:local` ends with its line), goes on after an error, and ends with the last line's exit status. So PNMTD wraps the text in `:parse` (`RouterOsScript.AsOneScript`): it is parsed as a whole (a syntax error runs nothing), runs as one script, and stops at the first error with exit status 1. **M6:** one exec of 64 KB ran on 7.25.1, so the 16 KiB limit (up to 48 KiB once non-ASCII is escaped) fits. **7.25.1 reports exit status 0 for a failed script in about one run of ten** (over OpenSSH as over SSH.NET; 7.13 gave 1 every time), so RouterOS's error line, which comes last, decides too.
- **Commands that ask a question** ("Reboot, yes? [y/N]") get no answer, because there is no terminal. *M0* records what the common ones do over exec. The UI suggests `:execute {…}` for reboots, as §7.3 does.
- **Commands that never end** (`/ping` without `count`, `/tool torch`, `monitor` without `once`) end at the timeout, with their output kept. The editor hints at `count=`, `duration=` and `once`.
- **The router is busy while a command runs:**
  - the poller pauses for it, as during an update (§5.2);
  - **a command is refused while an update runs on the router**, and an update waits for a running command;
  - a command that reboots the router gives 241. One that cuts off PNMT's access (the `pnmt` interface, user or firewall rule) gives 440 or 441, until the join script is pasted again. **The dialog warns when the text mentions the installation's tag or the `pnmt` objects.**
- **Cancelling** closes the SSH channel. Whatever the router has already done stays done.

### 8.3 Output and history

- **The output is untrusted**, like everything from a router (§4.5):
  - it is read up to 1 MiB;
  - terminal escape sequences are removed;
  - it is never evaluated, and the UI escapes it.
- **Stored permanently with the job:**
  - the text;
  - the user it ran as: the username, never the password;
  - who started it, and when;
  - the outcome and the exit status;
  - the output up to `Commands:MaxStoredOutputBytes` (default 256 KiB). The rest is cut, and marked as cut.

  So **the history is an audit trail of what was done to each router.** It outlives the router, as the job log does.
- **The text may contain a secret**, for example `/user set admin password=…`, and the dialog says that the text is stored. **An Admin can redact a job.** Its text and output are then replaced by "redacted by Alice at 10:41", and the redaction is audit-logged.
- **A batch's results are grouped by identical output.** Fifty routers that print the same thing are one row, and the three that differ stand out.
- **Run again** fills the dialog with an earlier job's text and routers. **Saved commands**, a library of named snippets, are M8, and so are scheduled commands.
- **Who sees what:**
  - the history and output of `pnmt` runs need CanWrite, like export contents (§16.2 question 5);
  - those of admin runs need Admin.

### 8.4 Diagnostics: the `test` policy

- **Ping and traceroute need the `test` policy**, which `pnmt` does not have. ***Diagnostics***, a per-router setting (Admin, audit-logged, off by default), adds `test` to the `pnmt-read` group in the join script, as *Include secrets* adds `sensitive` (§6.6).
  - **It takes effect when the admin pastes the new script.** Until then, the poll sees that the group lacks `test`, and the Setup tab says so.
- **The trade-off, shown next to the switch:** `test` also allows the bandwidth test, which can fill a link, and a wireless scan, which interrupts Wi-Fi clients. *M0* confirms that it allows no configuration change and no `/tool fetch`.
- **Without it,** ping and traceroute need an admin run.

### 8.5 Considered and rejected

- **A standing user with write rights**, for example `pnmt-admin` with PNMTD's key, so that changes need no password. This is convenient, but a compromised PNMTD could then change every router, and design goal 1 rules that out. It could become an opt-in per router later (§16.2 question 11).
- **An interactive terminal in the browser** (a pseudo-terminal over a WebSocket). WinBox and SSH through a custom tunnel already give one (§3.5). A terminal also cannot run on 50 routers at once, and leaves no history.
- **Filtering commands in PNMTD**, with an allowlist or a "read-only" check. RouterOS's syntax makes such checks unreliable, and the router's policies already enforce the user's rights exactly.

---

## 9. Integration into PNMTD

### 9.1 A router is a sensor

- **A new sensor type, `MIKROTIK = 80`** (`PNMTD.Lib/Models/Enum/SensorType.cs`), on a host, with a name, Interval, GracePeriod, Enabled and Ignore. A host may be the router itself, or a site with several routers.
- **Its settings and state** are in the 1:1 table `mikrotik_routers`, and in WireGuard modes also in a `wg_peers` row (§9.2). Both are created and validated through `SensorService`, as `udp_nodes` is. `SensorRequest` and `SensorDetail` get a `mikrotik` part.
- **The sensor keeps its write and read tokens** (the columns are required), so `/event` works for it as for any sensor.
- **Sensor type rules:** an Interval is required (at least 60 seconds). Parameters are not used.
- **Disabling the sensor** stops polling and backups, and removes the peer from the gateway.

### 9.2 Data model (one EF migration, `AddMikrotikAndWireGuard`)

**`wg_network`**: one row, the network of §3.3.

| Column | Type | Notes |
|---|---|---|
| `prefix` | cidr | `10.253.0.0/16` |
| `server_address`, `access_address` | inet | .1 and .2 of the server block (§3.2) |
| `public_key`, `private_key_protected` | text | The gateway's key pair; the private key under Data Protection |
| `keepalive_seconds`, `mtu`, `stale_handshake_seconds` | int | 25, 1420, 180 |
| `external_host`, `external_port` | text null, int null | As set in the UI. The environment wins (§3.3) |
| `management_ports` | int[] | 22, 8291, 80, 443 |
| `socks_secret_protected` | text | §3.4 |
| `gateway_seen_at`, `gateway_version`, `gateway_error` | | The gateway's heartbeat |
| `changed_at`, `changed_by` | | |

**`wg_peers`**: every tunnel, router or custom.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `kind` | smallint | `Router` or `Custom` |
| `name`, `notes` | text | The router's sensor name, or the custom tunnel's name |
| `address` | inet, unique | Inside the prefix, after the server block |
| `public_key` | text, unique | |
| `private_key_protected` | text | Data Protection. For the join script and the configuration downloads (§3.2) |
| `keys_created_at`, `keys_created_by` | | Regenerating sets them anew |
| `enabled` | bool | |
| `sensor_id` | uuid null, FK, `ON DELETE CASCADE` | Router |
| `owner_user_id` | uuid null, FK, `ON DELETE SET NULL` | Custom: may download (§3.5) |
| `access_all_routers` | bool | Custom |
| `expires_at` | timestamptz null | Custom |
| `created_at`, `created_by` | | |

**`wg_peer_access`**: `peer_id`, `router_sensor_id`. A custom tunnel's selected routers.

**`wg_peer_status`**: written by the gateway only. `peer_id` (PK); `endpoint`; `latest_handshake`; `rx_bytes` and `tx_bytes`; `endpoint_changes_24h`; `updated_at`.

**`mikrotik_routers`**: 1:1 with `sensors`, deleted with the sensor.

| Column | Type | Notes |
|---|---|---|
| `sensor_id` | uuid PK, FK | |
| `mode` | smallint | `WireGuardSsh`, `WireGuardOnly` or `DirectSsh` |
| `wg_peer_id` | uuid null, FK | The WireGuard modes |
| `address`, `ssh_port`, `pnmt_source_address` | text null, int, text null | Direct SSH (§3.6). The port defaults to 22, and the first login corrects it if needed |
| `management_access` | bool | false (§3.5) |
| `include_secrets` | bool | false (§6.6) |
| `diagnostics` | bool | false (§8.4) |
| `backup_interval_hours` | int | 24 |
| `setup_state` | smallint | `AwaitingRouter`, `TunnelUp`, `Ready` or `NeedsAttention` |
| `script_generated_at` | timestamptz | For "script outdated" in WireGuard only mode (§3.3) |
| `applied` | jsonb | What the router has, read by the poll: endpoint, keepalive, MTU, policies, the management rule, the SSH key (§5.1) |
| `host_key_type`, `host_key_fingerprint`, `host_key_pinned_at`, `pending_host_key_fingerprint` | | §4.4 |
| `identity`, `model`, `board_name`, `serial_number`, `architecture` | text null | |
| `version`, `channel`, `firmware_current`, `firmware_upgrade` | text null | The firmware columns are null on CHR and x86 |
| `booted_at` | timestamptz null | 241 |
| `resources` | jsonb | The latest reading (§5.1) |
| `last_poll_at`, `last_success_at`, `failing_since`, `last_error` | | |
| `notified_update_version` | text null | 244, once for each version |
| `last_export_at`, `last_export_hash`, `backup_failures` | | §6.2, 442 |
| `created` | timestamptz | |

**`mikrotik_settings`**: one row.

- `ssh_keys` (jsonb: id, public key, protected private key and created, for rotation, §4.2);
- `latest_versions` (jsonb, for each channel: version and release time) and `versions_checked_at`.

**`mikrotik_backups`**:

- `id`; `sensor_id` (null, `ON DELETE SET NULL`); `router_name` (a snapshot);
- `kind` (`Export` or `Binary`); `trigger` (`FirstLogin`, `Schedule`, `Manual`, `BeforeUpdate`, `BeforeCommand` or `AfterCommand`); `job_id` (null), for the last three;
- `created_at` and `created_by`; `routeros_version`;
- `content` (bytea, gzip for exports); `size`; `sha256` (of the normalised export);
- `includes_secrets`; `lines_added` and `lines_removed`; `encrypted` (binary).

**`mikrotik_jobs`**:

- `id`; `batch_id`; `kind` (`Update` or `Command`); `sensor_id` (null, `SET NULL`); `router_name`;
- `state`; `step`; `requested_by`;
- `created_at`, `started_at` and `finished_at`;
- for updates: `from_version`, `to_version` and `channel`; `options` (jsonb); `error`;
- for commands (§8): `command` (text, at most 16 KiB); `run_as` (`pnmt`, or the admin's username); `timeout_seconds`; `exit_status`; `output` (at most 256 KiB) and `output_truncated`; `backup_before_id` and `backup_after_id`; `redacted_at` and `redacted_by`. A redaction empties `command` and `output`.

Jobs are permanent. Only a redaction changes a finished one.

**`mikrotik_job_log`**: `id` (bigserial); `job_id`; `at`; `step`; `outcome` (`Started`, `Ok`, `Failed` or `Info`); `message`; `output` (at most 64 KiB). Permanent, and nothing deletes it.

### 9.3 Server components

```
PNMTD.Lib/WireGuard/             pure: no I/O
  WireGuardKeys.cs               X25519 key pairs, Base64 validation
  AddressPlan.cs                 prefix, server block, allocation, re-addressing
  WgQuickConfig.cs               the custom tunnel file (§3.5)
  NftRuleset.cs                  the gateway's ruleset from peers and access rules, as text (§3.5)
PNMTD.Lib/Mikrotik/              pure: testable with recorded output
  RouterOsVersion.cs             parse and compare 7.16.1, 7.17beta2, 7.15rc3; the 7.13 floor
  RouterOsValues.cs              the JSON of :serialize, durations, sizes
  ExportText.cs                  normalise, hash, line counts (§6.2)
  CommandOutput.cs               terminal escapes, error lines, grouping a batch by output (§8.3)
  JoinScript.cs                  the join script of each mode and the removal script, with RouterOS quoting
  OpenSshKeys.cs                 Ed25519 key pairs, the OpenSSH public key format
PNMTD/WireGuard/
  WireGuardOptions.cs, WireGuardSetup.cs
  NetworkService.cs              settings with their sources (environment, UI, default), the endpoint in effect
  PeerService.cs                 routers' and custom tunnels' peers, keys (Data Protection), NOTIFY
  Gateway/WireGuardGateway.cs    `pnmtd wireguard gateway`: nft, wg syncconf, peer status, heartbeat (§3.4)
  Gateway/Socks5Server.cs
PNMTD/Mikrotik/
  MikrotikOptions.cs, MikrotikSetup.cs
  RouterSession.cs               SSH.NET: direct or through SOCKS5, host key check, exec with timeouts, SFTP
  RouterReader.cs                the poll script, and its output parsed into a RouterReading
  RouterPoller.cs                hosted: due routers, bounded parallelism, state and events (§5)
  BackupRunner.cs, BackupRetentionTask.cs
  VersionCatalog.cs              hosted, every 6 h (§5.4)
  JobRunner.cs                   hosted: runs update and command jobs step by step (§7.3, §8.2)
  CredentialVault.cs             admin passwords in memory, per job or batch, with a lifetime (§7.2)
PNMTD/Services/Events/SensorEventWriter.cs   moved from PNMTD/Udp/, unchanged
PNMTD/Services/Domain/MikrotikRouterService.cs, MikrotikBackupService.cs, MikrotikJobService.cs,
                                 MikrotikCommandService.cs
PNMTD/Api/V1/WireGuardControllers.cs, MikrotikControllers.cs, Contracts/WireGuard.cs, Contracts/Mikrotik.cs
PNMTD/Cli/MikrotikCommand.cs, WireGuardCommand.cs
```

**Packages** (central package management, lock files):

- **SSH.NET** (MIT);
- **BouncyCastle.Cryptography** (MIT), for X25519 (WireGuard) and Ed25519 (SSH) key generation, unless SSH.NET already brings it in;
- **DiffPlex** (Apache-2.0), or a small Myers diff in `PNMTD.Lib` instead.

**Hosting:**

- `RouterPoller`, `VersionCatalog` and `BackupRetentionTask` run under `BackgroundTasks:Enabled`.
- `JobRunner` does not, so that the integration tests can run jobs. UDP's listener is outside it for the same reason.
- `Mikrotik:Enabled=false` turns all of them off.

### 9.4 Configuration

**In the database, set on the admin page:** the network of §3.3, except what the environment sets.

**In `appsettings` and the environment:**

```jsonc
"WireGuard": {
  "Enabled": false,              // true with the compose profile (§9.5); Direct SSH works without
  "ExternalHost": null,          // PNMTD_WG_EXTERNAL_HOST: wins over the UI (§3.3)
  "ExternalPort": null,          // PNMTD_WG_EXTERNAL_PORT: wins over the UI; default: PublishedPort
  "PublishedPort": 51820,        // PNMTD_WG_PORT, passed on by the compose file
  "ListenPort": 51820,           // inside the gateway's container
  "Gateway": "pnmtd-wg:1080",    // the SOCKS5 endpoint
  "GatewayDownSeconds": 60
},
"Mikrotik": {
  "Enabled": true,               // poller, backups, jobs
  "MinimumVersion": "7.13",
  "Ssh": { "Username": "pnmt", "ConnectTimeoutSeconds": 15, "CommandTimeoutSeconds": 60,
           "ExportTimeoutSeconds": 120, "MaxConcurrentSessions": 8 },
  "Backups": { "DefaultIntervalHours": 24, "KeepVersions": 50, "KeepDays": 365, "MaxExportBytes": 16777216 },
  "Updates": { "RebootTimeoutMinutes": 15, "DownloadTimeoutMinutes": 10, "CredentialLifetimeMinutes": 60 },
  "Commands": { "MaxCommandBytes": 16384, "DefaultTimeoutSeconds": 60, "MaxTimeoutSeconds": 600,
                "MaxOutputBytes": 1048576, "MaxStoredOutputBytes": 262144, "Parallelism": 4 },
  "VersionCheck": { "Enabled": true, "BaseUrl": "https://upgrade.mikrotik.com/routeros/", "IntervalHours": 6 },
  "AllowPasswordsOverHttp": false  // development only (§7.2)
}
```

### 9.5 Deployment

**`deploy/.env`** (the new lines in `.env.example`):

```sh
# --- WireGuard (MikroTik routers, custom tunnels) ---------------------------------------------------
# Turns the gateway on. Direct SSH to routers works without it.
COMPOSE_PROFILES=wireguard
WireGuard__Enabled=true
# The port published on this host, and the address it is published on.
PNMTD_WG_PORT=51820
PNMTD_WG_BIND_ADDRESS=0.0.0.0
# What routers and tunnels dial. Set them when this host is behind NAT or a router: the external
# address (a DNS name is best) and the port forwarded to PNMTD_WG_PORT. Empty: PublicBaseUrl's host
# and PNMTD_WG_PORT. Set here, they win over the admin page.
PNMTD_WG_EXTERNAL_HOST=
PNMTD_WG_EXTERNAL_PORT=
```

**`deploy/docker-compose.yml`** gets one service behind the profile, and three variables for the app:

```yaml
  pnmtd:
    environment:
      # … as today, plus:
      WireGuard__PublishedPort: ${PNMTD_WG_PORT:-51820}
      WireGuard__ExternalHost: ${PNMTD_WG_EXTERNAL_HOST:-}
      WireGuard__ExternalPort: ${PNMTD_WG_EXTERNAL_PORT:-}

  pnmtd-wg:
    profiles: [wireguard]                      # COMPOSE_PROFILES=wireguard in .env turns it on
    image: ${PNMTD_IMAGE:-uisach/pnmtd}:${PNMTD_IMAGE_TAG:-v1.0.0}
    command: ["wireguard", "gateway"]          # the entrypoint is `dotnet /app/PNMTD.dll`
    user: "0:0"
    restart: unless-stopped
    depends_on: { db: { condition: service_healthy } }
    ports:
      - "${PNMTD_WG_BIND_ADDRESS:-0.0.0.0}:${PNMTD_WG_PORT:-51820}:51820/udp"
    environment: { Database__Host: db, Database__Name: pnmtd, Database__Username: pnmtd,
                   Database__Password: "${PNMTD_DB_PASSWORD}" }
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    cap_add: [NET_ADMIN, DAC_READ_SEARCH]       # DAC_READ_SEARCH: the key ring belongs to the app's user
    security_opt: [no-new-privileges:true]
    sysctls: { net.ipv4.ip_forward: "1" }      # custom tunnels only; nftables drops everything else (§3.5)
    volumes:
      - pnmtd-keys:/var/lib/pnmtd/keys:ro      # decrypts the private keys; never writes the key ring
```

- **The app service** is otherwise unchanged: still non-root, without capabilities, and read-only.
- **Turned on** with `./init.sh --wireguard [--wg-external-host vpn.example.com --wg-external-port 51999]`, or by editing `.env`.
- **Behind NAT:** forward the external port (UDP) on the upstream router to `PNMTD_WG_PORT` on this host, and set the two external variables. The admin page shows the endpoint in effect, and where each part came from.
- **Host requirements:**
  - the WireGuard kernel module: built into Linux from 5.6; `modprobe wireguard` on older kernels;
  - rootless Podman needs the module loaded on the host;
  - hosts without it (Synology DSM, some NAS) use Direct SSH (§15).
- **Firewall:** the external port must be reachable from the routers and tunnels. Docker's published ports bypass `ufw`, but a cloud security group needs a rule.
- **The Dockerfile:** `wireguard-tools`, `iproute2` and `nftables` in the runtime stage. The gateway's Data Protection uses `DisableAutomaticKeyGeneration`, because its mount is read-only.
- **`info.sh`:**
  - the WireGuard line: gateway up, the endpoint in effect, peers up out of all;
  - a line for the MikroTik routers: ready, failing, behind on updates, scripts outdated.
- **`backup.sh`: no change.** The database holds the network, the peers, the routers, backups, jobs and the protected keys, and the keys volume decrypts them. After a restore, every router and tunnel works with its old script or file.
- **`deploy/README.md`:** a WireGuard and MikroTik section covering the modes, the profile, NAT and the external variables, the kernel module, firewalls, and that backups are credentials. `UPGRADING`: nothing changes until the profile is enabled.
- **`start.sh`:** `WIREGUARD=1 ./start.sh` also starts the gateway container, and `CHR=1` a lab router (§12).

### 9.6 Security

| Threat | Mitigation |
|---|---|
| **PNMTD is compromised** | The attacker gets **read access** to every router in an SSH mode: their configurations without secrets, unless a router opts in (§6.6). They also get the tunnel keys, and so access to the routers' management ports where management access is on, but no password for them. They cannot change a router, because nothing that can is stored, and commands they run as `pnmt` can only read. All private keys are encrypted with Data Protection |
| A database dump leaks, without the keys volume | No private key can be decrypted. The exports are readable, and they contain no secrets unless a router opted in. Backups are credentials, as stated for UDP keys already (`deploy/README.md`) |
| **A copy of a join script, or of a tunnel file** | It holds a private key (decision 1, §16.1). Viewing and downloading need CanWrite or ownership, and are audit-logged. Host key pinning, the identity check and endpoint flapping expose a second device (§4.4). *Regenerate keys* revokes the copy. A custom tunnel can also expire |
| The admin password leaks | HTTPS only (§7.2), memory only, a lifetime, never logged, and a test for it. It is sent only over SSH to a pinned host key. PNMT Admin role, and an audit entry for every use |
| A compromised router attacks PNMTD, other routers or tunnels | The forward chain drops by default: routers reach nothing. Nothing listens on the gateway's tunnel addresses, and SOCKS refuses connections arriving through `wg0`. Router output is size-limited and never evaluated (§4.5) |
| A custom tunnel reaches more than it should | It reaches only the management ports of routers in its access list that have management access, enforced by the gateway. The `pnmt` user is bound to the server address, which a tunnel's traffic never carries (§3.5) |
| The SOCKS port is abused from the compose network | A username and password, and only the SSH ports of routers' tunnel addresses |
| Command injection | PNMTD's own commands are constants, and the join script's strings are quoted by RouterOS rules, with a test for the quoting (§4.3). A remote command is arbitrary by design, but it runs in a session of its own, with the rights of the user it runs as, and is never combined with PNMTD's commands (§8.1) |
| **A remote command does harm** (a typo, the wrong routers, a malicious User) | As `pnmt`, the router allows only reading. As an admin: the Admin role, the password typed per run, the credentials checked on every router first, one router at a time with a stop at the first failure, an export before and after with the diff, a warning when the text touches PNMT's own objects, and a permanent history with an audit entry (§8) |
| A secret in a command's text or output | The dialog says that both are stored. An Admin can redact a job, and only Admins see admin runs (§8.3) |
| MikroTik's version information is spoofed | Fetched over HTTPS, and used only for display and 244. The router downloads and verifies packages itself (§7.1) |
| The join script changes more than expected | One interface, peer, address, route and one or two firewall rules, plus a group, user and key, all tagged. The script prints what it adjusts, and a removal script exists (§4.3, §4.6) |

**Audit log** (new `AuthEvents`):

- `WireGuardNetworkChanged`;
- `WireGuardTunnelCreated`, `WireGuardTunnelChanged`, `WireGuardTunnelDeleted`, `WireGuardTunnelDownloaded`;
- `WireGuardKeysRegenerated` (a router or a tunnel);
- `MikrotikJoinScriptViewed` (and downloaded);
- `MikrotikHostKeyAccepted`, `MikrotikSecretsChanged`, `MikrotikManagementAccessChanged`, `MikrotikDiagnosticsChanged`;
- `MikrotikBackupDownloaded` (binary or with secrets);
- `MikrotikUpdateStarted`, `MikrotikUpdateCancelled`;
- `MikrotikCommandStarted`, `MikrotikCommandCancelled`, `MikrotikCommandRedacted`;
- `MikrotikSshKeyRotated`.

---

## 10. API v1

The conventions of the migration plan (§3.7) apply. **No endpoint is anonymous**, so `EndpointAuditTests.AnonymousAllowlist` stays as it is, and every row below joins its role matrix.

**For each router:**

| Method | Path | Policy | Notes |
|---|---|---|---|
| GET | `/api/v1/sensors/{id}` | CanRead | `SensorDetail.mikrotik`: mode, setup state, the latest reading, versions, the tunnel, "script outdated", the last backup, the open job |
| POST, PUT | `/api/v1/sensors` | CanWrite | `SensorRequest.mikrotik`: mode, address, port, source address (Direct SSH), backup interval. Creating one allocates the address and keys and notifies the gateway. `managementAccess`, `includeSecrets` and `diagnostics` need **Admin** |
| GET | `/api/v1/sensors/{id}/mikrotik/join-script?format=text\|rsc` | CanWrite | It contains the router's private key (§4.3). Audit-logged |
| GET | `/api/v1/sensors/{id}/mikrotik/removal-script` | CanWrite | §4.6 |
| POST | `/api/v1/sensors/{id}/mikrotik/regenerate-keys` | CanWrite | §3.2. Audit-logged |
| POST | `/api/v1/sensors/{id}/mikrotik/poll` | CanWrite | Poll now |
| POST | `/api/v1/sensors/{id}/mikrotik/host-key/accept` | **Admin** | Accepts `pending_host_key_fingerprint`. Audit-logged |
| GET | `/api/v1/sensors/{id}/mikrotik/backups` | CanRead | The list: time, trigger, version, size, line counts, secrets, kind. No content |
| POST | `/api/v1/sensors/{id}/mikrotik/backups` | CanWrite | Export now |
| GET | `/api/v1/sensors/{id}/mikrotik/backups/{backupId}` | CanWrite | The content. **Admin** if it includes secrets |
| GET | `/api/v1/sensors/{id}/mikrotik/backups/{backupId}/diff?against=` | CanWrite | A unified diff, by default against the previous version |
| GET | `/api/v1/sensors/{id}/mikrotik/backups/{backupId}/download` | CanWrite | `.rsc`. **Admin** for binary backups and secrets, audit-logged |
| GET | `/api/v1/sensors/{id}/mikrotik/jobs` | CanRead | The router's jobs, each with its log |

**For the admin page (§11.2):**

| Method | Path | Policy | Notes |
|---|---|---|---|
| GET | `/api/v1/wireguard/status` | Admin | The gateway (up, seen at, version, error), the endpoint in effect with the source of each part, peers up out of all, and warnings |
| GET | `/api/v1/wireguard/network` | Admin | Every setting of §3.3 with its value, source (`environment`, `ui` or `default`) and whether it may be edited |
| PUT | `/api/v1/wireguard/network` | Admin | Changes the settings from the UI. A setting locked by the environment is refused. A new prefix needs `confirmPrefix` equal to it, and the response lists the peers that need a new script or file. Audit-logged |
| GET | `/api/v1/wireguard/peers` | Admin | Every peer, router or custom, with its status (handshake, endpoint, counters, endpoint changes) and whether its script is outdated |
| GET, POST | `/api/v1/wireguard/tunnels` | Admin | Custom tunnels: list, create (`name`, `notes`, `ownerUserId?`, `accessAllRouters`, `routerIds[]`, `expiresAt?`). Audit-logged |
| PUT, DELETE | `/api/v1/wireguard/tunnels/{id}` | Admin | Edit (also enable or disable), delete. Audit-logged |
| POST | `/api/v1/wireguard/tunnels/{id}/regenerate-keys` | Admin | Audit-logged |
| GET | `/api/v1/wireguard/tunnels/{id}/config?format=conf\|qr` | Admin | The `wg-quick` file, or the QR code as SVG. Audit-logged |
| GET | `/api/v1/mikrotik/status` | Admin | The version catalog with its last check, and counts per state, version and mode |
| GET | `/api/v1/mikrotik/routers` | Admin | The fleet, paged, with search (name, host, identity, serial number), filters (state, version, channel, behind on updates, mode, script outdated) and sorting |
| POST | `/api/v1/mikrotik/jobs` | Admin | `{ sensorIds[], kind: "Update", channel?, username, password, options }`. It checks the credentials on every router first (§7.2), and the response lists the routers refused, with reasons. Audit-logged |
| GET | `/api/v1/mikrotik/jobs`, `/api/v1/mikrotik/jobs/{id}` | Admin | Paged. Filters: kind (`Update` or `Command`), state, batch, router, time |
| DELETE | `/api/v1/mikrotik/jobs/{id}` | Admin | Cancels a job (§7.3) |
| POST | `/api/v1/mikrotik/ssh-keys/rotate` | Admin | §4.2. Audit-logged |

**For remote commands (§8):**

| Method | Path | Policy | Notes |
|---|---|---|---|
| POST | `/api/v1/mikrotik/commands` | CanWrite; **Admin** with `runAs: "admin"` | `{ sensorIds[], command, runAs: "pnmt"\|"admin", username?, password?, timeoutSeconds?, options }`. Creates a batch, one job per router. An admin run is refused over plain HTTP, like an update, and checks the credentials on every router first (§7.2). The response lists the routers refused, with reasons. Audit-logged |
| GET | `/api/v1/mikrotik/commands` | CanWrite | Paged. Filters: router, batch, who, outcome, time. Admin runs only for Admins |
| GET | `/api/v1/mikrotik/commands/{id}`, `/api/v1/mikrotik/commands/batches/{batchId}` | CanWrite | A job with its output and the diff of its backups; a batch with its results grouped by output. Admin runs only for Admins |
| DELETE | `/api/v1/mikrotik/commands/{id}` | CanWrite | Cancels a queued or running job: one's own `pnmt` runs, or any as an Admin. Audit-logged |
| POST | `/api/v1/mikrotik/commands/{id}/redact` | **Admin** | §8.3. Audit-logged |

**For a tunnel's owner:**

| Method | Path | Policy | Notes |
|---|---|---|---|
| GET | `/api/v1/account/tunnels` | CanRead | The custom tunnels the signed-in user owns, with their status and expiry |
| GET | `/api/v1/account/tunnels/{id}/config?format=conf\|qr` | CanRead | Only the user's own tunnels, and only while they are enabled and not expired. Audit-logged |

The OpenAPI document and the generated UI types (`openapi.json`, `schema.d.ts`) are updated by the build as usual.

---

## 11. Web UI

### 11.1 The router's sensor page

- **The sensor form:** the type *MikroTik router*; the mode (WireGuard + SSH, WireGuard only, Direct SSH); in Direct SSH mode the address, port and source address; the poll interval (Interval), the GracePeriod, and the backup interval. *Management access*, *Include secrets* and *Diagnostics* appear for Admins.
- **The status strip** (like `UdpStatusStrip`):
  - the state;
  - RouterOS version and channel, with *7.16.2 available* as a link to the Updates tab;
  - model and serial number, uptime, CPU, memory, storage, firmware;
  - the tunnel (address, last handshake, endpoint);
  - the last backup.

  WireGuard only shows the tunnel alone.
- **Tabs with their own routes**, as UDP nodes have: `/sensors/:id/{events,setup,backups,updates,commands}`. The SSH modes have all five; WireGuard only has Events and Setup.
  - **Setup:** the steps with their live state (*waiting for the router → tunnel up → logged in → ready*), and the join script with *Copy* and *Download .rsc*, hidden until *Show* is clicked, because it holds a key. A banner appears when the script is outdated, saying what changed. A difference that is intended can be acknowledged there, or from the status strip, with a reason; it then stays in the list, greyed out, until what it covers changes (docs/mikrotik-join-options-plan.md §3). Next to the script, its two options (§4.3). Below: the host key with identity and serial number, *Regenerate keys*, and the removal script.
  - **Backups:** the versions (time, trigger, version, +/− lines, size, a secrets badge, binary or export). *View*, *Diff with the previous*, a diff between any two selected versions, and *Download*. *Back up now* (CanWrite).
  - **Updates:** installed, latest for the channel, the changelog, and the firmware. *Update…* (Admin) opens a dialog that asks for the channel, the username and the password, and sets the options. It shows the router's identity and serial number, what will happen, and the expected downtime. Below it, the job history, each job with its log as a timeline (like `CommandLogTimeline`).
  - **Commands:** a monospace editor for the text, *Run as* (`pnmt`; or an admin, for Admins, with the username and password), the timeout, the options, and *Run*. The result follows below it: the outcome, the duration, the output with *Copy* and *Download .txt*, and with backups before and after, their diff. Then the router's history, with *Run again*, and *Redact* for Admins. The editor hints at `count=` for ping, `once` for monitors and `:execute` for reboots, and warns when the text touches PNMT's objects (§8.2).

### 11.2 Admin → MikroTik & WireGuard

**One admin page** that monitors and configures everything: `/admin/mikrotik`, role Admin, in `navigation.ts` next to *UDP nodes*. Its tabs, each with its own route:

| Tab | Monitors | Configures |
|---|---|---|
| **Overview** | Whether the gateway is running, when it was last seen, its errors. The endpoint in effect, and where each part came from. Peers up out of all. Routers by state and version, and those behind on updates or with outdated scripts. Tunnels with flapping endpoints or near their expiry. The latest versions per channel. Warnings: no external host, the gateway never seen, the host requirements | – |
| **Network** | – | The settings of §3.3. Values set by the environment are shown locked, with the variable's name. A change shows its effect first: which routers and tunnels need a new script or file |
| **Routers** | The fleet table: name, host, mode, state, version, channel, latest, firmware, uptime, last backup, tunnel address and handshake, script up to date. Filters such as *behind on updates*. ***Add router***: an existing host, or a new one made in the dialog, then the router form of §11.1 | Multi-select → *Update…* (§7.4) or *Run command…* (§8.2) |
| **Custom tunnels** | Each tunnel: owner, address, handshake, endpoint, traffic, expiry, access | Create, edit, disable, delete, regenerate keys; *Download .conf* and *QR code* |
| **Jobs** | Every update and command job and batch, with its log. A command batch's results are grouped by output (§8.3) | Cancel, *Run again*, *Redact* (Admin) |

The SSH public key with *Rotate…*, and the version catalog with its last check, are in a *Settings* section of the Network tab.

### 11.3 Other places

- **Account → Tunnels:** a PNMT user's own custom tunnels, with *Download .conf* and *QR code*, for any role.
- **The dashboard:** routers are sensors, so they appear without changes. Their last message carries the version (§5.3).
- **The Documentation page:** the sensor type and the status codes, from `SystemService`.
- **System health:** the poller as a task, the gateway, and the version check.

---

## 12. Tooling

- **`pnmtd wireguard gateway`:** the gateway (§3.4). It runs only with `NET_ADMIN`, and says so otherwise.
- **`pnmtd wireguard status`:** the network, the endpoint in effect with its sources, and the peers with their handshakes.
- **`pnmtd wireguard tunnel add <name> [--owner <user>] [--routers all|<names>] [--expires <date>] [--out file.conf]`:** for the server.
- **`pnmtd mikrotik list`:** every router with its mode, state, version and setup state.
- **`pnmtd mikrotik poll <name|id> [--raw]`:** one poll, printing what was read, or the raw output with `--raw`. This is for debugging the parser against a real router.
- **`pnmtd mikrotik backup <name|id> [--print]`:** a backup now, as *Back up now* does (a version only if the configuration changed); `--print` writes the newest stored export to standard output.
- **`pnmtd mikrotik run <name|id>… (--command <text> | --file <script.rsc>) [--admin <user> --password-stdin] [--timeout <seconds>]`:** a remote command from the server (§8), stored as a job like one from the UI. It prints the output, and exits with 0 when every router succeeded.
- **`pnmtd mikrotik script <name|id> [--removal]`:** prints the join or the removal script. Logged.
- **`pnmtd mikrotik accept-host-key <name|id>`:** for the server (Admin, logged).
- **`tools/chr-lab.sh`:** lab routers. MikroTik's **CHR** (Cloud Hosted Router, a free image whose free licence limits each interface to 1 Mbit/s, which is plenty) runs in QEMU in a container, in a version of choice (`CHR_VERSION=7.13`).
  - It configures CHR through its serial console: the join script is pasted there, as an admin would.
  - It can put CHR on a network without a route to PNMTD, so only the tunnel connects them, and behind a NAT of its own, so the external endpoint can differ from the published one.
  - A second container with `wg-quick` plays a technician's laptop with a custom tunnel.
  - `CHR=1 ./start.sh` and `CHR=1 ./run-tests.sh` use it.
- **`pnmtd dev seed`:** adds a few routers in each mode and two custom tunnels, with readings, backups and jobs. They are answered by a built-in simulator that replays the M0 fixtures (Development only), so the UI has content without a real router.

---

## 13. Phases

Effort is a rough estimate for one developer. Each phase ends green in `./run-tests.sh`, including the sensor contract.

| # | Phase | Scope | Done when | Effort |
|---|---|---|---|---|
| M0 | Lab and spike | `tools/chr-lab.sh` with CHR 7.13, 7.20.2 and the current stable. Every command of §4–§7 with the rights of §4.2, over a non-interactive exec channel:<ul><li>`/export` as `read` only on 7.13 (the `sniff` fix of 7.14);</li><li>exec on 7.20.2;</li><li>the join script: a supplied private key, the removal order, the route, `place-before=0`;</li><li>the key import;</li><li>`:serialize` output;</li><li>export normalisation;</li><li>the policies of `backup save`, and `backup save` with a password;</li><li>the update and reboot path without prompts;</li><li>remote commands: several lines in one exec, exit statuses and error lines, questions such as the reboot prompt, and what `test` allows (§8).</li></ul>Also WireGuard and nftables in Docker and rootless Podman, and SSH.NET through SOCKS5. The outputs recorded as fixtures | Every *(M0)* item confirmed or replaced, with the changes recorded in this plan. Fixtures committed | 2–3 d |
| M1 | Monitoring over Direct SSH | `MIKROTIK`, `mikrotik_routers`, `mikrotik_settings`, the SSH key (Data Protection), the join script for Direct SSH, host key pinning, `RouterSession`, `RouterReader`, `RouterPoller`, `VersionCatalog`, events 240–242, 244, 440 and 441, the `SensorEventWriter` move. API; the sensor form, the status strip and the Setup tab; the Documentation page; `pnmtd mikrotik list/poll/script` | A CHR in Direct SSH mode, joined by its script, is green with its version. Stopped, it gives 440 after the GracePeriod. Restarted, it gives 241. A newer stable gives 244 | 4–5 d |
| M2 | Backups | Exports on schedule, normalisation, versions, retention, diffs, downloads, `include_secrets`, 243 and 442, the Backups tab | A change on the CHR gives 243 and a diff that shows it. An unchanged configuration stores nothing. Retention keeps what §6.3 says. With secrets off, no stored export contains one | 3–4 d |
| M3 | WireGuard + SSH, WireGuard only | `wg_network`, `wg_peers`, `wg_peer_status`, X25519 keys, the address plan, **network settings from the UI and the environment**, the gateway (nft with drop, `wg syncconf`, SOCKS5, status, heartbeat), the join scripts with the private key, "script outdated", the endpoint flapping warning, the compose profile and the external variables, the Dockerfile packages, `init.sh`, `info.sh`, the README, `WIREGUARD=1 ./start.sh`. **The admin page: Overview, Network, Routers.** Pause while the gateway is down | A CHR behind its own NAT, with no route to PNMTD except the external endpoint, joins with **one paste** and is monitored and backed up. A second CHR in WireGuard only mode shows its tunnel's state. With `PNMTD_WG_EXTERNAL_PORT` set, the scripts carry it and the Network tab shows it locked. Restarting the gateway writes no events. Neither CHR can reach the other | 6–8 d |
| M4 | Custom tunnels | Tunnels with owners, access and expiry; the nft forward rules and the access address; management access in the join script; `.conf` and QR downloads; *Account → Tunnels*; the Custom tunnels tab; `pnmtd wireguard tunnel add` | The lab's laptop container, with a downloaded file, reaches WinBox and SSH on a CHR with management access, and nothing on a CHR without it, nor the other tunnels or the compose network. It cannot log in as `pnmt`. An expired tunnel is dropped | 3–4 d |
| M5 | Updates | Jobs, `CredentialVault`, the steps of §7.3, firmware, batches, cancelling, the job log, 242 and 443, audit; the Updates tab, and *Update…* and Jobs on the admin page | A CHR goes from 7.13 to the current stable from the UI, with an export and a binary backup first and every step in the job log. A wrong password is refused before a job exists. A router that does not return fails after the timeout. A PNMTD restart during the job fails it, and the password is gone | 4–5 d |
| M6 | Remote commands | Command jobs, runs as `pnmt` and as an admin, the options (backups before and after, one at a time), outcomes, the history, redaction, *Diagnostics*, the warning about PNMT's objects. The Commands tab, *Run command…* on the admin page, results grouped by output, `pnmtd mikrotik run` | On a CHR, `/ip address print` as `pnmt` shows the addresses, and `/ip address add …` as `pnmt` is refused by the router. As an admin it succeeds, and the result shows the diff. A batch over three CHRs stops at the first failure. A timeout keeps the partial output. The marker password appears nowhere | 3–4 d |
| M7 | Hardening and release | SSH key rotation, re-addressing after a prefix change, Playwright, a load test, `pnmtd dev seed`, `deploy/README.md`, the README, release notes | The targets of §14 are met, and the docs are complete | 2–3 d |
| M8 | *Later* | Child sensors with thresholds (CPU, memory, storage, temperature), interfaces, PPPoE and WireGuard peers on the router. Collecting `/log` (login failures). Graphs. Maintenance windows and staged rollouts for updates. Drift alerts against a "golden" export. Custom tunnels as sensors. Name resolution in the tunnel network. Access through custom tunnels to the networks behind routers. A userspace WireGuard for hosts without the module | – | – |

**Total for M0–M7: about 27–36 days.**

### 13.1 M0 results, and M1 as built (2026-11-13)

**The lab** (`tools/chr-lab.sh`): CHR 7.13 and 7.25.1, x86_64 under QEMU emulation. The ARM64 CHR images (from 7.20 on) panic at boot under KVM on Apple Silicon ("No working init found"), so the lab falls back to x86_64. The recorded outputs are fixtures in `PNMTD.Tests/Fixtures/Mikrotik/<version>/`, and `PNMTD_CHR_LAB=r13,r25` runs the lab test (`MikrotikLabTests`): a router added through the API, joined with the generated script, polled over real SSH, and removed. **Not yet run: 7.20.2** (the broken non-interactive exec of §4.1); `tools/chr-lab.sh start r20 7.20.2` and the lab test cover it.

**What M0 found, and what the code does about it:**

| Finding | Versions | Consequence |
|---|---|---|
| A non-interactive exec parses every line on its own: a `{ … }` block cannot span lines, a `:local` ends with its line, an error does not stop the next line, and the exit status is the last line's | 7.13, 7.25.1 | The poll script is one line. Remote commands are wrapped in `:parse`, which runs them as one script and stops at the first error with exit status 1 (§8.2) |
| A menu that does not exist (`/system routerboard` on CHR and x86) is a syntax error that fails the whole exec | both | Optional menus are parsed at run time: `[[:parse "…"]]` inside `:do … on-error={}` |
| `:serialize to=json` turns strings that look like numbers into numbers: `7.130000`, a port `22.000000`. Durations come as `"1970-01-01 00:00:43"` on 7.13 and `"00:02:38"` on 7.25.1. `/system resource`'s version is `"7.13 (stable)"` on 7.13 and `"7.25.1"` on 7.25.1 | both | Versions are read with a prefix (`("v" . [/system/resource/get version])`), numbers are normalised, and both duration forms are parsed |
| `proplist` fails on menus with one item ("bad parameter proplist") and on unknown properties; the SSH key's comment is `key-owner` on 7.13 and `info` on 7.25.1 | both | `proplist` only on the lists that could hold secrets (WireGuard, peers), with properties every version has |
| A read-only user sees WireGuard private keys as `*****` (7.13) or not at all (7.25.1), in prints and in the export | both | And the poll names its properties anyway: no secret can reach the stored reading |
| `/export` works for `ssh,read` on 7.13; only CALEA gives `#error exporting` lines. Two exports a minute apart differ only in their first line | both | No `sniff`; the normaliser drops the first line (§6.2) |
| A write as `pnmt` fails with "not enough permissions (9)" and exit status 1; `check-for-updates` too; `/system reboot` does nothing and exits 0 | both | The central version check (§5.4) is right; remote commands rely on the router's refusal (§8.1) |
| `:rndstr length=32`, `/file add … contents=`, `ssh-keys import` of an Ed25519 key, WireGuard with a supplied private key and a host name as endpoint, `gateway=pnmt` | both | As planned |
| `place-before=0` fails on an empty filter list ("no such item") | both | The rules are added, then moved before the first rule that is not dynamic |
| 7.25 removes the key file itself after `import`, so `/file remove pnmt.pub` fails | 7.25.1 | `/file remove [find name=…]` |
| A new file or interface appears a moment after its `add`: an immediate `import` says the file does not exist, and a firewall rule on a new interface can stay invalid | 7.13 | The script waits for each to appear (and an old one to go), and sets an invalid rule's interface again. Eight pastes in a row came out clean |
| A removed user's SSH keys stay, and go to the next user of that name: an old key would still log in after a new paste | 7.13 | The script removes the `pnmt` user's keys first |
| The `address` restriction works: the lab's logins from Podman's gateway were refused until the source address matched | both | As planned (§4.2) |

**Decisions taken while building M1:**

- **One PNMT per router.** The join and removal scripts remove every object whose comment starts with `pnmt:`, not only this installation's tag, because the names (`pnmt`, `pnmt-read`, the interface) are the same for every installation. Otherwise a router moved to another installation, or a PNMT reinstalled with a new database, fails with "already exists".
- **No events before the first successful login.** A router waiting for its join script stays "no data"; the Setup tab shows the last error. After the first login, failures give 440 and 441 as in §5.3.
- **440's message stays the same while a router fails** ("Unreachable since 10:04: SSH: connection timed out"), so the dedupe holds; "for 12 min" would write a new event at every poll.
- **Direct SSH keeps the configured port.** Only through the tunnel does the first login take the router's own SSH port, because a Direct SSH router may sit behind a port forward.
- **Migrations per phase**, starting with `AddMikrotikRouters` (M1: `mikrotik_routers`, `mikrotik_settings`, and `mikrotik_ssh_keys`, a table of its own instead of a jsonb column, for the rotation).
- **Packages:** SSH.NET 2026.0.0, which brings BouncyCastle.Cryptography 2.7.0, used for the Ed25519 keys (and later X25519).

**M1 as built:** the sensor type with its table and validation, PNMTD's SSH key (Data Protection, purpose `PNMTD.Mikrotik.SshKey.v1`), the join and removal scripts for Direct SSH (the WireGuard parts are generated and lab-tested, but the modes stay refused until the gateway exists), host key pinning and acceptance, `RouterPollService` and `RouterPoller` (events 240, 241, 242, 244, 440, 441), `VersionCatalog`, the API of §10 for a router plus `/mikrotik/status` and `/mikrotik/routers`, `pnmtd mikrotik list|poll|script|accept-host-key|versions`, and in the UI the sensor form's router section, the status strip, the Setup tab and the settings dialog. Not yet: the admin page (M3). M1 and M2 go first because they need no gateway, so SSH, parsing and backups can be built and tested against a CHR on the same network. The order of M3 and M1/M2 can be swapped if the tunnel matters more early on.

**M2 as built (backups):**

- **The export runs in the poll's session** when it is due (the backup interval since the last successful export, or none yet) or asked for (*Back up now*, `pnmtd mikrotik backup`). A failed export is retried after `Backups:RetryMinutes` (30), not at every poll.
- **Stored normalised** (the time line dropped, LF), as gzip, with its SHA-256, line count, the RouterOS version from its first line, and the line counts against the version before; a new version only when the hash differs. Version 1 (trigger `FirstLogin`) gives no 243.
- **442's message** is "Backups failing: <the error>", stable while the error is the same; it replaces 240 from the second failure in a row until an export succeeds.
- **The diff is PNMT's own** (`LineDiff`, Myers with unified hunks, about 200 lines in `PNMTD.Lib`), so no package was added; a randomised test checks it against a longest-common-subsequence reference.
- **Retention** (`BackupRetentionTask`, hourly) as §6.3. Backups of a deleted router keep only the age rule. An Admin can delete a version (`DELETE …/backups/{id}`, audit-logged); deleting the newest makes the next export a new version.
- **The API** of §10 for backups, plus that delete; content, diffs and downloads of exports with secrets (and of binary backups, M5) need Admin and are audit-logged as `MikrotikBackupDownloaded`.
- **The UI:** the Backups tab (the versions with trigger, author, RouterOS version and +/− lines; view, the changes against the version before, two selected versions compared, download, *Back up now*, delete for Admins) and *Last backup* in the status strip.
- **Lab:** the lab test now also checks version 1 at the first login, nothing stored for an unchanged export, and a change on the router (`/system note`) as version 2 with 243 and a diff that shows it, on 7.13 and 7.25.1.

**M3 as built (WireGuard + SSH, WireGuard only):**

- **The gateway** (`pnmtd wireguard gateway`, `PNMTD/WireGuard/Gateway/`): the drop ruleset before wg0, `wg syncconf`, `nft -f` with one atomic table replace, `LISTEN wireguard_changed` plus a resync every 30 seconds, `wg show dump` into `wg_peer_status` with the endpoint changes, the heartbeat, and the SOCKS5 server (CONNECT, username and password, IPv4 router addresses and their SSH port only, nothing from the tunnel). It waits for the app's migrations.
- **Two findings of the first real run:**
  - *The key ring.* Root with every capability dropped cannot read files the app's non-root user owns with 0600. The gateway gets `DAC_READ_SEARCH` besides `NET_ADMIN`; the keys volume stays read-only and is its only mount. The alternatives were worse: a non-root gateway gets no `NET_ADMIN` under Docker, and plaintext keys in the database would let a dump impersonate the gateway.
  - *Restarts.* After the gateway was recreated, a router behind NAT needed a few minutes for its next handshake (WireGuard backs off). The gateway records when it started (`gateway_started_at`), and for one stale-handshake period the poller writes no event for a tunnel without a fresh handshake.
- **The routers:** a WireGuard mode gets a peer (the next free address after the server block, X25519 keys from BouncyCastle, checked against RFC 7748 and `wg pubkey`); the join script carries its private key, the gateway's key, the endpoint in effect, and `address=<server address>/32` for `pnmt`; *Regenerate keys*; "script outdated" compares the router's tunnel (its key, the gateway's key, endpoint, port, keepalive, MTU, the management rule) with the settings; in WireGuard only mode, the settings' and keys' changes against when the script was last shown. The endpoint warning appears from 6 changes in 24 hours.
- **The poll:** WireGuard + SSH through the gateway's SOCKS5 to the tunnel address (the router's own SSH port); WireGuard only from the handshake (240 "Tunnel up (WireGuard only)", 440 after the GracePeriod); nothing while the gateway is down or settling.
- **The network** on the admin page, with the environment winning (`WireGuard__ExternalHost`, `WireGuard__ExternalPort`; an empty value from compose counts as unset), a new prefix with typed confirmation that keeps every peer's offset, and the list of routers that need their new script.
- **Deployment:** the compose service behind the profile, `./init.sh --wireguard [--wg-external-host … --wg-external-port …]`, `info.sh`'s WireGuard line, `deploy/README.md`, `WIREGUARD=1 ./start.sh`, and `wireguard-tools`, `iproute2` and `nftables` in the image. The importer's reset deletes users instead of truncating them, because a custom tunnel's owner refers to them.
- **The admin page** *Admin → MikroTik & WireGuard* (`/admin/mikrotik?tab=…`, as the UDP page does it): Overview, Network and Routers. Custom tunnels (M4) and Jobs (M5) follow.
- **Lab, on real RouterOS:** CHR 7.25.1 behind QEMU's NAT joined as WireGuard + SSH with one paste and dialled the gateway by name; PNMTD logged in through SOCKS (240, host key pinned, a backup over the tunnel, no script differences). CHR 7.13 joined as WireGuard only (240 "Tunnel up"). With its route and allowed addresses widened to the whole prefix, the 7.13 router still reached neither the other router nor the gateway's SOCKS port.

**M4 as built (custom tunnels):**

- **Tunnels** (`wg_peers` of kind `Custom`, `wg_peer_access`; migration `AddWireGuardTunnels`) with a name, notes, an owner, all or selected routers, an expiry and *Enabled*; created, changed, deleted and regenerated by an Admin, each audit-logged, the gateway resyncing at once.
- **What a tunnel reaches** is computed in one place (`TunnelReach`) for the API and the gateway: routers in a WireGuard mode, enabled, with management access on, of those all or the selected ones. The gateway's ruleset allows exactly those addresses on the management ports, and rewrites the source to the access address.
- **The file** is wg-quick's, only the tunnel network routed. The API returns it as text; **the QR code is drawn in the browser** (`QrCode.vue`), as the 2FA setup does, rather than as an SVG from the server (§10's `format=qr`).
- **Downloads:** Admins under *Admin → MikroTik & WireGuard → Custom tunnels*; an owner under *Account → Tunnels* (the user menu), only while the tunnel is enabled and not expired. Every download is audit-logged.
- **`pnmtd wireguard tunnel add <name> [--owner …] [--routers all|…] [--expires …] [--out …]`.**
- **Lab, on real RouterOS:** a laptop container brought up the file the CLI wrote with `wg-quick` and reached WinBox (8291) and SSH (22) on the 7.25.1 router with management access, but not port 23, not the 7.13 router without management access, and not the gateway's SOCKS port. The router accepted it only through PNMT's rule for the access address, which shows the source rewriting; its login as `pnmt` was refused.

**M5 as built (updates):**

- **Jobs** (`mikrotik_jobs`, `mikrotik_job_log`; migration `AddMikrotikJobs`, which already has M6's command columns). `POST /api/v1/mikrotik/jobs` (Admin) takes routers (up to 500), a channel or each router's own, the admin's username and password, and the options *binary backup*, *firmware* and *stop at the first failure* (all on by default).
- **Checked before a job exists**, per router, with the reason in the answer: HTTPS (§7.2), an SSH mode, enabled, no job queued or running, the target known and newer, a pinned host key, and then **a login with the password that reads the user's group and its policies** (`ssh read write policy reboot`, with the binary backup also `test sensitive ftp`). The version comes first, so an up-to-date router is not logged in to. Accepted routers form a batch, refused ones are listed, and the password goes into the `CredentialVault` for the batch only.
- **`JobRunner`** is a hosted service outside `BackgroundTasks`, so the tests run real jobs. It runs one job at a time and holds the router's lock (the poller skips the router meanwhile). A failure cancels the batch's later jobs. The vault forgets the batch when its last job ends, or after `CredentialLifetimeMinutes`.
- **The steps of §7.3, as built:**
  - *Check* and *Export* as `pnmt`; an unchanged export stores no new version.
  - *Binary backup:* `/system backup save name=pnmt-<8 hex> password=<admin's>`, SFTP with the pinned host key, a size cap (`MaxBinaryBackupBytes`, 64 MiB), `/file remove`. It is stored as a backup of kind *Binary*, marked as encrypted with the admin's password.
  - *Download:* `set channel`, `check-for-updates`, `get latest-version` must be the target, the free storage against `MinimumFreeStorageBytes`, `download`, then the status every `PollSeconds` until "Downloaded" (`DownloadTimeoutMinutes`).
  - *Reboot:* `/system reboot` over exec; the session ending with it is expected.
  - *Wait:* as `pnmt` until the router answers with a boot time after the reboot (`RebootTimeoutMinutes`).
  - *Verify*, then *Firmware* on a RouterBOARD only, with its own reboot and wait.
  - *Done:* before the job counts as done, a poll with a forced export (trigger **AfterUpdate**, new), which writes 241 and 242.
- **Cancelling** (`DELETE …/jobs/{id}`, audit-logged) works until the reboot. **443** names the step, and after a download it says that the package installs at the next reboot and how to remove it. A restart of PNMTD fails running jobs (§7.5, as built).
- **The UI:** the router's **Updates** tab shows the installed version against the newest of its channel, the release's changelog (fetched from MikroTik and cached in memory), *Update…* for Admins, and the router's jobs, each with its log as a timeline. The dialog takes the channel, credentials and options, and shows each router's refusal. *Admin → MikroTik & WireGuard → Jobs* lists all jobs, with *Cancel*. **Not yet:** choosing several routers in the Routers list for one batch (the API takes them), and parallel jobs.
- **Tests** against a simulated router that answers like 7.25.1 in the lab: every step, 242, the binary backup (Admin only, removed from the router), the fresh export after the update; a wrong password and missing policies refused with no job; an unapplied package as 443; HTTP and non-Admins refused; an up-to-date router refused. **The marker password is searched for in every text column of the database.**
- **Lab, on real RouterOS:** r13, a CHR on 7.13 behind QEMU's NAT, joined as WireGuard + SSH, was updated to 7.25.1 from the API in 41 seconds. The steps were check, an unchanged export, a binary backup (27 KiB), the download (3 s), the reboot, and back after 35 s on 7.25.1, with "no RouterBOARD" for the firmware. Events 241 and 242 followed. **On 7.13, `/system reboot` over exec also reboots at once, without its question.** The policies measured on 7.25.1 are those of §4.2's table.

**M6 as built (remote commands):**

- **`POST /api/v1/mikrotik/commands`** and the rest of §10's command endpoints (`MikrotikCommandsController`, `MikrotikCommandService`). A run as `pnmt` needs CanWrite; a run as the router's admin needs Admin and HTTPS, and checks the password on every router first, writing the admin's group into each job's log ("its group is full (ssh, read, write, …)"). The only policy required is `ssh`: the router enforces the rest. A router is refused when it is in WireGuard only mode, disabled, not yet logged in to (no pinned host key), or has an update queued or running.
- **The text** is stored as typed (line ends normalised), at most `Commands:MaxCommandBytes` (16 KiB) of UTF-8, and always sent through `RouterOsScript.AsOneScript`. The timeout is 5 to 600 seconds (60 by default).
- **The runner** (`JobRunner`) now runs update and command jobs alike: up to `Commands:Parallelism` (4) at once, one per router, a batch in order when it runs one at a time (updates always), with a stop at its first failure. Cancelling a running command closes its session through the job's own cancellation token, and is possible until the command has run (not during its export after). A restart of PNMTD fails running commands with "the router may have run it, but its output is lost".
- **The steps:** Check (from what PNMTD knows; reaching the router is the next step's), the export before (option), Run in a session of its own, the export after (option, whatever the outcome), Done. An unreachable router or a refused login is *Skipped*: nothing ran. Exports for jobs are now taken as the poll takes them (`JobExportAsync`: with secrets only when the router opted in and its group allows), so their versions compare with the poll's, and a changed export writes 243 as the poll would; the update's export does the same now.
- **Outcomes:** *Succeeded*; *Failed* by a non-zero exit status or by RouterOS's error line (`CommandOutput.Error`, the last line: the known prefixes, or 7.25's "(… line N column M)"), with that line as the message; *TimedOut* (a new job state) with the output read so far; *Skipped*; *Cancelled*.
- **Output** is read up to `Commands:MaxOutputBytes` (1 MiB), cleaned of escape sequences and CRs, and kept up to `MaxStoredOutputBytes` (256 KiB), marked as cut. It is not copied into the job log. Lists load jobs without their output.
- **Rights:** admin runs are listed and shown to Admins only. A router's own job list (`GET /sensors/{id}/mikrotik/jobs`, CanRead) has its updates only. Cancelling: one's own runs, or any as an Admin. **Redaction** (Admin, audit-logged) replaces the text, the output, the error and the run's log messages; the backups stay. Audit events `MikrotikCommandStarted`, `MikrotikCommandCancelled`, `MikrotikCommandRedacted`, none with the text.
- **`pnmtd mikrotik run <name|id>… (--command … | --file …) [--admin <user> --password-stdin] [--timeout …] [--backup|--no-backup] [--one-at-a-time|--all-at-once]`** claims its jobs when it creates them and runs them in its own process, so the password never leaves it; the server's runner leaves running jobs alone. It prints the results grouped by output and exits with 0 only when every router succeeded. Ctrl-C cancels like the web's Cancel.
- **The UI:** the router's **Commands** tab (for Users and Admins, in the SSH modes): the editor with its hints (PNMT's objects, `reset-configuration`, ping and traceroute without `count=`, monitors without `once`, tools without `duration=`, a reboot without `:execute`), *Run as* for Admins, the timeout and the options; the result with its output (Copy, Download .txt), the change as a diff, and the log; the history with Run again, Cancel and Redact. On the admin page, **routers can be chosen in the Routers list** (across pages) for *Run command…* and *Update…* (the batch updates M5 left out), and the Jobs tab shows commands too, with a batch's results grouped by outcome and output (`?tab=jobs&batch=…`).
- **Also fixed:** jobs of a batch got creation times 100 ns apart, which PostgreSQL's microseconds collapse, so a batch could run out of order; they are 1 µs apart now.
- **Tests** against the simulated router, which now unwraps `:parse`: a print as `pnmt`, a change refused by the router, `:error`, a failed script with exit status 0; an admin run with the diff, the group in the log, 243 without the text, the audit entry and the marker password nowhere; a batch grouped by output and an admin batch stopping at its canary; a timeout with its partial output; the output limit; cancelling a running command; the rights of Users and Viewers; redaction (the secret then nowhere in the database); the refusal during an update. `CommandOutput` and the editor's hints have unit tests.
- **Lab, on real RouterOS** (7.13 over Direct SSH, two 7.25.1 over WireGuard + SSH): `/ip address print` as `pnmt` on all three; `/ip address add` as `pnmt` refused with "not enough permissions (9)"; as the admin it succeeded with a diff of +1 line and 243 naming who ran it; a wrong password refused before any job; an admin batch over the three stopped after the first router's error; `:put first; :delay 30s` with a 5 second timeout kept "first"; a cancelled `monitor-traffic`; `:execute {/system reboot}` ended at once with exit status 0, and the router rebooted after it (241); the CLI as `pnmt` and as the admin. The lab password was in neither a `pg_dump` nor the app's log. Error lines recorded: "bad command name nosuch (line 1 column 2)", "expected end of command (line 2 column 19)", "input does not match any value of interface (/ip/address/add (interface); line 1)"; `:error "boom"` gives "boom (:error; line 1)" on 7.25.1 and "boom" on 7.13.

**M7 as built (hardening and release):**

- **SSH key rotation** (§4.2): `GET /api/v1/mikrotik/ssh-keys`, `POST …/ssh-keys/rotate` and `POST …/ssh-keys/retire[?force=true]` (Admin, audit events `MikrotikSshKeyRotated` and `MikrotikSshKeyRetired`).
  - A rotation creates a new pair. **Join scripts now install the newest key only**, so a script pasted after a rotation removes the router's old key; until M7 they installed every key in use.
  - PNMTD offers every key not yet retired, so routers keep working until their new script is pasted.
  - The list of routers that may still need an older key comes from each poll's key owners. Retiring is refused while that list is not empty, unless forced.
  - The admin page's Overview shows the keys, *Rotate…*, the waiting routers and *Retire older keys…*.
- **Re-addressing after a prefix change** was built in M3 (§13.1, M3), so nothing was added.
- **`pnmtd dev seed`:** the five office gateways are MikroTik routers in Direct SSH mode, replacing their "config backup" heartbeats, so the seed keeps 300 sensors. Each router has:
  - a reading parsed by `RouterPoll.Parse`;
  - backup versions with real diffs, and events;
  - an update with its log (geneva, 7.24.2 → 7.25.1, with a binary backup) for one router, and an update available for another (lugano);
  - a `pnmt` batch over all five; a refused change; an admin change with its diff and 243.

  They are polled daily, so the demo stays as seeded. The WireGuard modes are not seeded: the seed has no key ring.
- **Playwright** (`e2e/mikrotik.spec.ts`, on the seed):
  - a router's status strip, Setup with the join script, a backup diff, a command with its output and change, and an update's log;
  - the admin page's rotation and retirement, the *Behind on updates* filter, and a batch grouped by output;
  - a Viewer without Commands or the script;
  - a new router through the form.
- **Load** (`MikrotikLoadTests`, opt-in `PNMTD_PERF=1`), against §14's targets:
  - 500 routers at a 5-minute interval, behind a connector with 150 ms to connect and 250 ms a command, through the real poller with 8 sessions;
  - the first round, with every first export, took 42 s, and a steady round 26 s; at most 8 sessions were open;
  - a steady round added no event rows and no backups.
  - The gateway's files for 1,000 peers are built in milliseconds. In the gateway container, `wg syncconf` took 111 ms for 1,000 new peers and 20 ms when nothing changed, and `nft -f` 17–21 ms for 50 tunnels that each reach 200 routers.
- **The password never persists:** the command tests now also capture every log line (PNMTD and ASP.NET Core at Trace) and find no marker password in them.
- **Fixed:** `start.sh` and `stop.sh` named the gateway and Vite containers `pnmtd-wg` and `pnmtd-web` whatever the stack's `NAME`. So the e2e stack removed the debugging stack's gateway. Both names now derive from `NAME`.
- **Job names** are "host/router" (as `office-bern-gw/router`): routers are often all named "router", one per host.
- **Docs:** the README (features, a section, the seed), `deploy/README.md` (updates and commands, the password, `pnmtd mikrotik run`), `deploy/UPGRADING-1.0.md` (new and off until used), and the release notes in [docs/release-notes-mikrotik.md](release-notes-mikrotik.md).
- **Not done:** §14's OpenSSH container test. The opt-in CHR lab tests (`PNMTD_CHR_LAB`) cover real SSH, key login, host key pinning and the SOCKS5 path against RouterOS itself.

---

## 14. Testing

- **Pure code (`PNMTD.Lib`, unit tests):**
  - versions: ordering, betas and release candidates, the floor;
  - the `:serialize` JSON, durations and sizes, on the M0 fixtures of 7.13, 7.20.2 and the current stable;
  - export normalisation and hashing;
  - command output: escape sequences removed, error lines found, grouping by output;
  - **the join script of each mode and the removal script**, against golden files:
    - the private key appears only in the WireGuard modes;
    - quoting is correct for names with quotes, `$`, `\` and non-ASCII characters;
  - **the address plan:** the server block, allocation, exhaustion, re-addressing;
  - **the `wg-quick` file** and **the nftables ruleset** for given peers and access rules, against golden files;
  - X25519 against the RFC 7748 vectors; key formats.
- **Settings:** the environment wins over the UI, and a locked setting cannot be changed through the API. The endpoint in effect is derived correctly: environment, UI, `PublicBaseUrl`, or none (a warning). Scripts and files carry it. After a change, "script outdated" is right for each mode.
- **Against a simulated router:** `IRouterSession` has a fake that replays the fixtures and can simulate a timeout, a refused login, a changed host key, a reboot, a version change and an export change. With `TestApp` and PostgreSQL:
  - the poller's states and events, including the 440 grace, 441 at once, 241, 242, 243, 244 once, and 442 until a backup succeeds;
  - WireGuard only mode from `wg_peer_status` alone;
  - the pauses (uplink, gateway, a running job);
  - backups: nothing stored when unchanged, line counts, retention, survival after the sensor is deleted;
  - jobs: every step, cancelling, the timeout, the restart case, and a batch that stops at the first failure;
  - commands: the outcomes (error lines, an exit status, a timeout with partial output, cancelling), the output limits, grouping by output, redaction, the refusal during an update, and the poller's pause.
- **Over real SSH:** an OpenSSH server in a container, to test key login, host key pinning, timeouts and the SOCKS5 path (the gateway's SOCKS server without WireGuard).
- **The password never persists:** after an update job and an admin command run, each with a known marker password, the test searches every table (a `pg_dump`), the captured logs and the audit log for the marker, and finds nothing.
- **Keys at rest:** no private key appears in plaintext in a `pg_dump`. Viewing a join script and downloading a tunnel file write audit entries.
- **API:** the role matrix for every new endpoint (`EndpointAuditTests`):
  - a Viewer sees no export content and no join script;
  - a User can add routers and see their scripts, but cannot change the network, tunnels, management access or secrets;
  - an owner downloads only their own, unexpired tunnels;
  - only Admins update routers or download binary backups;
  - a User runs commands as `pnmt` and sees those runs, but cannot run as an admin or see admin runs, and a Viewer can do neither;
  - also validation, and a password refused over HTTP.
- **Lab (opt-in, `CHR=1`):** against CHR 7.13 and the current stable:
  - joining in each mode by pasting the script into the console, behind a NAT, with a differing external port;
  - a poll, an export, a configuration change;
  - a command as `pnmt` (a print works, a change is refused) and as an admin, with the diff;
  - **isolation:** CHR to CHR, CHR to the laptop container, the laptop to a router without management access, and the laptop as `pnmt`, are all refused;
  - an update to the current stable (this needs internet), and removal.

  CHR has no RouterBOARD, so the firmware step is covered by the simulator only.
- **UI:** Vitest for the diff view, the setup steps, the network form with its locked fields, and the update dialog's summary. Playwright with the seeded, simulated routers and tunnels.
- **Load (M7):** 500 simulated routers at a 5-minute interval, and 1,000 peers in the gateway. Polls stay within their interval, there are never more than `MaxConcurrentSessions` sessions, `wg syncconf` and `nft -f` stay under a second, and the database writes are only the readings, the peer status and deduplicated events.

---

## 15. Risks

| Risk | Mitigation |
|---|---|
| RouterOS 7.13 behaves differently from the current release (syntax, prompts, output) | M0 runs on 7.13, 7.20.2 and the current stable. The fixtures are kept per version, and the reader knows the version (§4.1) |
| **On 7.13, `/export` fails for a user without `sniff`** (fixed in 7.14) | M0 tests it. If it fails, either add `sniff` to the group on 7.13 routers only, with a note in the Setup tab, or offer backups from 7.14 on. `sniff` allows the packet sniffer, which is more than a read-only user should have, so the second is the cleaner choice |
| **7.20.0–7.20.2 broke non-interactive commands** | M0 tests PNMTD's exec on 7.20.2. If it fails, use a shell channel for those three versions, or give 441 "Update to 7.20.3 or later by hand" |
| **A join script or tunnel file is copied** | §4.4 and §9.6: the audit log, host key pinning, the identity check, the flapping warning, regeneration, and expiry |
| The WireGuard module is missing on the host (Synology DSM, some NAS, old kernels), or WireGuard or nftables do not work in rootless Podman | Direct SSH always works. The host requirement is documented, and the Overview shows it. M0 tests rootless Podman. A userspace fallback is M8, without custom tunnels |
| The upstream NAT is set up wrong (wrong forward, wrong external port) | The Overview shows the endpoint in effect and its sources. A router that never handshakes shows *waiting for the router* with a hint to check the forward. The lab tests a differing external port |
| A changed endpoint or prefix strands routers | Prefer a DNS name. The UI shows the effect before saving and lists the outdated routers. The script repairs them. The prefix needs a typed confirmation |
| An export changes without a configuration change, giving false 243s | The normaliser, and M0's check. Per-router ignore patterns later |
| An update leaves a router unreachable | An export and a binary backup first, one router at a time with a stop at the first failure, a timeout with 443 and 440, and the job log for the diagnosis |
| A failed job leaves a downloaded package that installs at the next reboot | Said in the job log and in 443, with the remedy (§7.5) |
| A command run as an admin breaks a router or cuts off PNMT | Admin only, the canary with a stop at the first failure, an export before and after, the warning about PNMT's objects. The join script restores PNMT's access (§8.2) |
| RouterOS runs commands over exec differently between versions (several lines, questions, exit statuses) | M0 on each version, and one exec per line as the fallback (§8.2) |
| Secrets in a command's text or output | The dialog's notice, redaction, and an Admin-only history for admin runs (§8.3) |
| The admin password ends up in a log or a dump | Memory only, HTTPS only, and a test that searches for it (§7.2, §14) |
| The join script surprises the router's admin (the firewall, the SSH service) | A minimal, tagged change, a script that says what it adjusts, and a removal script |
| The tunnel prefix overlaps a router's network | Only the server block (/30) is routed on the router, and the prefix is configurable |
| Parsing breaks with a new RouterOS release | `:serialize to=json` instead of tables, and fixtures for each version. An unexpected output counts as a failed poll ("unexpected output"), keeps the last values, and `poll --raw` shows it |
| A PNMTD compromise exposes all routers | Read-only access, an address restriction, secrets off by default, management access off by default, and keys under Data Protection (§9.6) |
| MikroTik changes the version endpoint | It is optional, and used only for display and 244. Its failure shows on System health |
| SSH load on small routers (hEX, hAP lite) | One session at a time, a 5-minute interval, and exports once a day |

---

## 16. Decisions and open questions

### 16.1 Decisions from the reviews of revisions 1 and 2 (2026-11-12)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Who generates the router's WireGuard key? | **PNMTD.** The private key is in the join script, so one paste is all it takes | §3.2, §4.3, §4.4 |
| 2 | How does a router join? | **One script**, shown after the router is added in the UI, with everything: the tunnel and its keys, the firewall, the `pnmt` user and key. The second path, which used the admin password to create the user, is dropped | §4.3 |
| 3 | When is the router's admin password needed? | **For updates** (revision 2), **and for commands run as an admin** (decision 8). Binary backups are taken only inside an update | §6.5, §7.2 |
| 4 | Which connection modes? | **WireGuard + SSH, WireGuard only, and Direct SSH.** Nothing else | §2.3, §3.1 |
| 5 | The external address and port of the WireGuard endpoint | **Also in environment variables** (`PNMTD_WG_EXTERNAL_HOST`, `PNMTD_WG_EXTERNAL_PORT`), for a PNMTD behind NAT. The environment wins over the UI | §3.3, §9.5 |
| 6 | The tunnel network | **Configured in the web UI, on one admin page** that also monitors it | §3.3, §11.2 |
| 7 | Custom tunnels | **Extra WireGuard peers**, with their configuration downloadable | §3.5 |
| 8 | Remote commands *(review of revision 2)* | **Yes:** a command or a script, on one router or several. As `pnmt` without a password, or as the router's admin with the password typed for that run | §8 |

### 16.2 Open questions

Each has a recommendation, and the sections above are written to it.

| # | Question | Options | Recommendation |
|---|---|---|---|
| 1 | **What are custom tunnels for?** This plan reads "custom tunnels for the pnmt user (and automatic download)" as follows: tunnels that an Admin creates, optionally for a PNMT user, who then downloads the configuration, in order to reach the routers' management ports | **a)** People's devices reaching the routers (this plan). **b)** Other devices reaching PNMT through the tunnel (sensor reports over WireGuard). **c)** Both. **d)** Something else | **a)**. b) would only need forwarding to the app's ports, and can follow |
| 2 | Who may see a router's join script, which holds its private key? | CanWrite (those who add routers), or Admin only | **CanWrite**, audit-logged. Adding a router is a User's task, and the script is useless without the router to run it on |
| 3 | What may custom tunnels reach? | The routers' management ports only; any port on the routers; or also the networks behind them | **The management ports only.** The networks behind routers need routes and NAT on each router (M8) |
| 4 | Management access on a router: the default | Off, or on | **Off**, switched on per router by an Admin |
| 5 | Who may read export contents? | CanRead (Viewers too) or CanWrite | **CanWrite.** Exports reveal addressing, firewall rules and VPN peers. The list itself is CanRead. Secrets are Admin only |
| 6 | The gateway is down: what happens to tunnel routers? | No events, as with the uplink guard; or 440 for all of them | **No events**, plus a warning on the Overview and System health. Otherwise one gateway restart sends a notification for every router |
| 7 | Defaults | The prefix, the port, on or off | **`10.253.0.0/16`, 51820/udp, off until the compose profile is enabled** |
| 8 | Secrets in exports? | Off, or on by default | **Off by default, opt-in per router** (§6.6) |
| 9 | A router below 7.13 | Refuse; or monitor with a warning | **441 in the SSH modes**, with backups and updates off. WireGuard only works on any RouterOS 7 |
| 10 | Credentials for a batch of updates | One set for the batch; or one per router | **One set for the batch**, checked on every router first. One per router later, if fleets with different admin passwords need it |
| 11 | **Commands that change a router:** where do the rights come from? | **a)** The router's admin password, typed for each run (this plan). **b)** A standing user with write rights and PNMTD's key, opt-in per router | **a).** With b), a compromised PNMTD could change every router that opted in. b) can follow per router if typing the password is too much |
| 12 | Who may run commands as `pnmt`? | CanWrite (Users), or Admin only | **CanWrite.** The router lets `pnmt` only read, and Users may read export contents already |
| 13 | *Diagnostics* (`test`) for `pnmt`: the default | Off, or on | **Off**, switched on per router by an Admin (§8.4) |
| 14 | Keep the text and output of commands? | Permanently, for a time, or not at all | **Permanently**, as an audit trail, with redaction by an Admin (§8.3) |

---

## Appendix: configuration and file changes

| Where | Change |
|---|---|
| `PNMTD.Lib/Models/Enum/SensorType.cs` | `MIKROTIK = 80` |
| `PNMTD.Lib/Models/PNMTStatusCodes.cs` | `MIKROTIK_OK = 240` … `MIKROTIK_UPDATE_AVAILABLE = 244`, `MIKROTIK_UNREACHABLE = 440` … `MIKROTIK_UPDATE_FAILED = 443` |
| `PNMTD.Lib/WireGuard/`, `PNMTD.Lib/Mikrotik/` | Keys, the address plan, `wg-quick` files, the nftables ruleset; parsers, export text, command output, the join script (§9.3) |
| `PNMTD/WireGuard/` | Network settings, peers, the gateway with its SOCKS5 server (§9.3) |
| `PNMTD/Mikrotik/` | Sessions, poller, backups, version catalog, update and command jobs, credential vault (§9.3) |
| `PNMTD/Udp/SensorEventWriter.cs` | Moved to `PNMTD/Services/Events/`, unchanged |
| `PNMTD/Services/Domain/SensorService.cs`, `Api/V1/Contracts/Sensors.cs` | The `mikrotik` part of `SensorRequest` and `SensorDetail`, its validation, and creating `mikrotik_routers` and the peer |
| `PNMTD/Services/Domain/SystemService.cs` | The sensor type, status codes and task health |
| `PNMTD/Models/Enums/KeyValueKeyEnums.cs` | `LAST_MIKROTIK_POLL_RUN` |
| `PNMTD/Data/PnmtdDbContext.cs` | The tables of §9.2, their indexes, and the migration `AddMikrotikAndWireGuard` |
| `PNMTD/Auth/UserAccounts.cs` | The `AuthEvents.WireGuard*` and `Mikrotik*` constants |
| `PNMTD/Program.cs` | `AddWireGuard(…)`, `AddMikrotik(…)`, with the hosted services of §9.3 |
| `PNMTD/Cli/CommandLine.cs` | `wireguard …` and `mikrotik …` |
| `PNMTD/Api/V1/WireGuardControllers.cs`, `MikrotikControllers.cs`, `Contracts/` | §10 |
| `PNMTD.Tests/Hosting/EndpointAuditTests.cs` | The new endpoints in the matrix. The anonymous allowlist is unchanged |
| `Directory.Packages.props`, lock files | SSH.NET, BouncyCastle.Cryptography (if needed), DiffPlex (if chosen) |
| `Dockerfile` | `wireguard-tools`, `iproute2` and `nftables` in the runtime stage |
| `deploy/docker-compose.yml` | The `pnmtd-wg` service behind the `wireguard` profile, and `WireGuard__PublishedPort`, `WireGuard__ExternalHost` and `WireGuard__ExternalPort` for the app (§9.5) |
| `deploy/.env.example`, `init.sh`, `info.sh`, `README.md` | `COMPOSE_PROFILES`, `WireGuard__Enabled`, `PNMTD_WG_PORT`, `PNMTD_WG_BIND_ADDRESS`, `PNMTD_WG_EXTERNAL_HOST`, `PNMTD_WG_EXTERNAL_PORT`; `--wireguard`; the WireGuard and MikroTik lines; a section on both |
| `start.sh`, `stop.sh`, `tools/chr-lab.sh` | `WIREGUARD=1`, `CHR=1`, the lab routers and the laptop container |
| `PNMTD.WebUI/src/layouts/navigation.ts`, `router/index.ts` | *MikroTik & WireGuard* in the Admin group (`/admin/mikrotik/{overview,network,routers,tunnels,jobs}`), `/sensors/:id/{setup,backups,updates,commands}`, `/account/tunnels` |
| `PNMTD.WebUI/src/views/sensors/mikrotik/` | The status strip, the Setup, Backups, Updates and Commands tabs, the diff view, the update and command dialogs |
| `PNMTD.WebUI/src/views/admin/MikrotikView.vue` and `admin/mikrotik/` | The admin page with its five tabs (§11.2) |
| `PNMTD.WebUI/src/views/account/TunnelsView.vue` | The owner's tunnels and downloads |
| `README.md` | A feature line |
