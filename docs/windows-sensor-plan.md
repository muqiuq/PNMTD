# Plan: Windows servers, the PNMT sensor service, and diagnostics (package two)

| | |
|---|---|
| Status | **Built:** W1–W6 (§16, *As built*). **W0 is still to do** on real Windows machines, with the checklist of §16. The reviews' answers are in §15.1; no question is open |
| Date | 2026-11-13 |
| Revision | 3: **tests only for PNMTD, and few**; the existing CI stays as it is (§13).<br><br>2: **the review of revision 1 applied** (§15.1):<ul><li>**no new port:** the WebSocket runs over PNMT's existing address and port;</li><li>**built on macOS, with testing kept to a minimum**;</li><li>**a log file per day, 10 kept**, and **a service that logs and reports every failure to PNMTD, and carries on**;</li><li>**Windows 10 Pro and Server 2019 supported**, with a version check that warns and runs anyway;</li><li>**the service runs as LocalSystem**, so scripts can run as admin;</li><li>**a server is added to an existing host**;</li><li>**logins and connections monitored**: every admin login, but not ordinary users on file shares;</li><li>run-as passwords **may be stored on the server**, encrypted;</li><li>**GitHub release assets, built on tags only**; a normal push only builds;</li><li>script parameters **parsed on the agent**;</li><li>**ARM64** too.</li></ul> |
| Scope | Package two of docs/ideas.md:<ul><li>**A Windows server, or a normal Windows PC, runs the PNMT sensor service:** one binary with .NET included.</li><li>**It joins** with a token generated in PNMT's web UI, and trusts PNMTD through a key pair from then on.</li><li>**It keeps a WebSocket open on PNMT's existing port**, sends a heartbeat every minute, sends what changed when it changes, and sends everything every 6 hours. That covers the system, hardware, network, services, remote access, **logins and connections**, the event log, and diagnostics results.</li><li>**Diagnostics** (ping and DNS for now) are defined centrally in sets, assigned to servers, and run by the service on a schedule or by hand. They are built so that other agents (Linux, later) can use them too.</li><li>**The service is read-only.** PowerShell scripts, run from PNMT under chosen accounts, come last, as the first feature that changes anything (W6).</li></ul> |
| Non-goals | A new port, or a listener of its own. Linux or macOS agents (the diagnostics are ready for them). A remote shell or remote desktop. Installing software or Windows updates. Performance history (graphs of CPU or memory): the heartbeat carries current values only. Active Directory inventory. Ordinary users' file-share logons. Commands of any kind before W6. AI on Windows data (package one's analysis sees a Windows server only as a sensor and its events). Changes to the sensor API. Code signing (there is no certificate) |
| References | docs/webui-migration-plan.md (architecture, conventions, roles). docs/mikrotik-plan.md, the pattern for a managed device: a sensor type with a 1:1 table, joining from the UI, a permanent log, a password typed per run. docs/udp-iot-plan.md (keys, protocol documentation). docs/ai-operations-plan.md (importance, the Notification Manager). Microsoft's documentation of .NET's supported OS versions, Windows services in .NET, WebSockets in ASP.NET Core, the event log, WMI/CIM, and the Security audit events |

---

## 1. Summary

The **PNMT sensor service** is one Windows executable, `pnmt-sensor.exe`, built from this repository and published with each release on GitHub. Double-clicked as an administrator, it shows a window that asks for PNMT's URL and a join token. It then creates its key pair, registers with PNMTD, installs itself as a Windows service, and connects **over the same address and port as the web UI**. From then on, PNMT shows everything about the server, records every relevant login and connection, and runs the diagnostics assigned to it. **Whatever fails is logged to a daily file and reported to PNMTD, and the service carries on.**

| Topic | Decision |
|---|---|
| The binary | **`pnmt-sensor-x64.exe` and `pnmt-sensor-arm64.exe`:** self-contained single-file .NET 10, with a WinForms window. **Three modes:**<ul><li>the window (double-click, elevated);</li><li>the service (started by Windows);</li><li>the command line (`join`, `status`, `uninstall`) for unattended installs.</li></ul>No other dependency (§3.1) |
| Windows versions | **Supported and tested:** Windows Server 2016, **2019**, 2022 and 2025; Windows 11; **Windows 10 Pro**, Enterprise and LTSC. **A version check at start:** another version is logged, reported and flagged in PNMT, **and the service runs anyway** (§3.9) |
| No new port | **The WebSocket is an upgraded HTTP request on PNMT's existing address and port**, served by the same listener as the web UI and the sensor API. Nothing to open, no compose change; only the reverse proxy must pass the upgrade (§3.5, §8.6) |
| Adding a server | **On an existing host:** *Add server* (from the host's page, or *Admin → Windows servers*) creates a **join token**, `xxxx-xxxx` (lower-case letters and digits, single use, valid 24 hours). A name is optional. The server's details arrive when it joins (§3.3) |
| Trust | **The service creates an ECDSA P-256 key pair in Windows' machine key store, non-exportable.** At the join, PNMTD records the public key and hands over its own signing key. **Every connection is authenticated by signing PNMTD's challenge.** Configuration and requests from PNMTD are signed by PNMTD. Normal TLS validation on top (§3.4) |
| Connection | A heartbeat every 60 s, a change when it happens, everything every 6 hours. Reconnects with backoff, for ever. A WebSocket ping every 30 s keeps proxies from closing it (§3.5) |
| Logging and resilience | **A log file per day, the last 10 kept**, in `%ProgramData%\PNMT Sensor\logs\`. **Every collector, check, message and loop is isolated:** a failure is logged, reported to PNMTD as a *problem*, and the rest carries on. A crash restarts the service through Windows' recovery settings (§3.8) |
| What it reports | **System, hardware, network** (in detail), **services** (classified as Windows, Microsoft, third-party or unsigned), **remote access** (RDP, WinRM, SMB, WMI, DCOM, RPC, Remote Registry, OpenSSH, Windows Admin Center, Remote Assistance, Quick Assist, Server Manager, third-party remote tools), **logins and connections**, and **the last 100 event log entries** that aren't Information (§4) |
| Logins and connections | **Every relevant login and connection, kept 180 days in PNMT:** console, RDP, SSH and WinRM for everyone; network logons (SMB, RPC, WMI) **only for admins**, so ordinary users on file shares stay out; failed logins of every kind, aggregated. **The audit policy is checked**, and what is missing is shown with the command to fix it. Optional events for admin logins (254) and bursts of failures (454) (§4.7) |
| Changes | **Each section has a hash.** A changed section is sent at once, compared on PNMTD, and kept as a readable change ("RDP enabled", "Service AnyDesk added"). A notable change writes event **253** (§4.9) |
| Diagnostics | **Admin → Diagnostics:** sets of checks (ping, DNS), assigned to servers, scheduled with an interval or run by hand. **Each assignment is a sensor of its own** (DIAGNOSTICS, 101), with its own state, importance and notifications. Run by the service; results are buffered when it is offline (§5) |
| Read-only | **PNMTD can ask the service only to:** collect now, run assigned diagnostics, and send its log. The service knows no other request before W6 (§6) |
| Service account | **LocalSystem:** it reads everything listed here, and in W6 can run scripts as admin (§3.2) |
| PowerShell (W6) | **Scripts in PNMT; their parameters parsed on an agent** (never executed for that) and sent to PNMT. **Run as:** the service itself (admin), an account whose password is **stored on that server**, encrypted and entered there, a password typed per run, or a gMSA. **Only on servers where an administrator turned scripts on locally** (§7) |
| PNMT model | **A server is a sensor of type `WINDOWS` (100)**, with a 1:1 table, `windows_agents`. Diagnostics assignments are sensors of type `DIAGNOSTICS` (101). Events: 250, 251, 253, 254, 451, 453, 454 and 460; diagnostics 252, 452 and 460 (§8.2) |
| Distribution | **GitHub release assets, built only when a sensor tag (`sensor-1.2.0`) is pushed:** both executables and their SHA-256. A normal push only compiles. **Not in the Docker image.** PNMT's *Download* tab links to the sensor release of its own version (§11) |
| Tests | **Few, and only for PNMTD:** join, authentication, limits, reports and the endpoint matrix, in `PNMTD.Tests`. **No tests of the agent's code**; it is checked by hand on one Server 2019 and one Windows 10 Pro or Windows 11 machine. The existing CI is unchanged (§13) |

---

## 2. Requirements, starting point and alternatives

### 2.1 Requirements

| # | Requirement (docs/ideas.md, package two) | Where |
|---|---|---|
| R1 | Everything about the Windows server:<ul><li>all hardware, **especially the network**;</li><li>diagnostics results;</li><li>the last 100 event log entries, everything except Information;</li><li>uptime and host name;</li><li>active services, telling built-in Windows ones from special ones;</li><li>which remote-control features are active: RDP, WinRM, SMB, WMI, DCOM, RPC, Remote Registry, OpenSSH Server, Windows Admin Center, Remote Assistance, Quick Assist, Server Manager, and so on;</li><li>the latest interactive logins: by whom, how, and from which IP address</li></ul> | §4 |
| R2 | Diagnostics, used by others later too (Linux): an admin page defining **diagnostic sets**, each a list of checks with a type (ping and DNS for now), **assigned to a server**, run **regularly (with an interval) or by hand** | §5 |
| R3 | Diagnostics run in the service | §5.3 |
| R4 | The PNMT sensor service runs on the monitored server, and installs on normal Windows too. **One binary** | §3.1 |
| R5 | Double-clicked as an admin, it opens a window to enter the URL and the **token: 4 lower-case alphanumerics, a dash, 4 more**, generated first in PNMT's web UI | §3.3 |
| R6 | **Trust through a private and public key** with PNMTD | §3.4 |
| R7 | Adding the sensor needs nothing, not even a name (a name may be given). Everything is filled in when it joins | §3.3 |
| R8 | It connects, downloads what it needs, and installs a service. **The window shows the service's status, can uninstall it, and shows the diagnostic sets loaded**, and more | §3.3, §10.4 |
| R9 | **.NET bundled**, no dependencies | §3.1 |
| R10 | **An open WebSocket at all times**, a heartbeat every minute, a message when something changed, and a full update every 6 hours | §3.5 |
| R11 | **Read-only for now:** no commands on the server. Commands come later | §6 |
| R12 *(later)* | **PowerShell scripts** added in the web UI, written to best practice, run from PNMT under different user names (defined in the UI), with arguments, which are recognised automatically | §7 |
| R13 *(review)* | **No new port:** the WebSocket runs over the same port | §3.5, §8.6 |
| R14 *(review)* | **Built on macOS, with testing kept to a minimum** | §11, §13 |
| R15 *(review)* | **A rotating log file, one per day, 10 kept.** **Resilient:** when something fails, the service logs it, reports it to PNMTD, and carries on | §3.8 |
| R16 *(review)* | The versions of revision 1, **plus Windows 10 Pro**; **Server 2019 must work**. A version check, and the service runs anyway | §3.9 |
| R17 *(review)* | The service account has **enough privilege to run scripts as admin** | §3.2, §7.4 |
| R18 *(review)* | **The server is added to an existing host** | §3.3 |
| R19 *(review)* | **Everything needed to monitor all logins and connections** to the machine: **all relevant admin logins, but not the users of a file server** | §4.7 |
| R20 *(review)* | Run-as passwords **stored on the server if necessary, securely** | §7.4 |
| R21 *(review)* | **A GitHub release asset, built only on tags.** No extensive testing on GitHub, only building on a normal push. **The full test suite runs locally** | §11 |
| R22 *(review)* | Script parameters **parsed on the agent**, and the relevant information sent to the server | §7.1 |
| R23 *(review)* | **ARM64** | §3.1 |

### 2.2 Design goals

- **A compromised PNMT must not mean compromised servers.**
  - Before W6 the protocol has no way to run anything.
  - In W6, scripts run only where an administrator allowed them on the server itself.
- **A compromised server must not compromise PNMT.**
  - Each agent has its own key and connection.
  - Messages are size-limited, typed and validated.
  - Everything an agent reports is shown escaped.
- **Never silent, never stuck.** Every failure is written to the log file and reported to PNMTD. No single failure stops the service (§3.8).
- **Nothing to install first, nothing to open.** One executable, using only what Windows has: WMI/CIM, the event log, the registry, the service manager, the IP helper API, and the firewall's COM API. It talks to PNMT on PNMT's existing port.
- **Quiet on the network:** about 1 KB a minute, sections only when they change, compressed.
- **PNMT's structure:**
  - a sensor type with a 1:1 table;
  - an admin page with tabs;
  - a permanent per-device log;
  - joining from the UI, as for MikroTik routers;
  - PNMTD's private key protected with Data Protection;
  - every endpoint in `EndpointAuditTests`.

### 2.3 Alternatives

| Option | Why not |
|---|---|
| **Agentless:** WMI or WinRM from PNMTD | Needs inbound ports on every server, Windows credentials stored in PNMT, and NTLM or Kerberos from a Linux container. Servers behind NAT can't be reached |
| SNMP | Little information, and v2c has no security |
| Existing agents (Zabbix agent, windows_exporter, Wazuh) | Their own servers, ports and formats, and no view of logins or remote access as asked. The request is a PNMT-native single binary |
| A port of its own for the agents | Rejected in the review (R13). The WebSocket upgrade works on the existing port |
| The agent polls over HTTP every minute | Simpler for proxies, but the request is an open WebSocket, which also lets *Collect now* and *Run diagnostics* act at once |
| mTLS client certificates | The reverse proxy (Caddy, nginx, Traefik) ends TLS, so a client certificate doesn't reach PNMTD without proxy configuration. Signatures in the application work through any proxy |
| An MSI installer | Extra tooling (WiX), and two files instead of one. The executable installs itself. An MSI wrapper for Group Policy or Intune can come later |
| A logging library (Serilog, NLog) | A daily file with 10 kept is about 100 lines on top of `Microsoft.Extensions.Logging`. One dependency less |
| The executable in the Docker image | 60–80 MB more per architecture in every image. A release asset instead (R21) |

### 2.4 What the code base already provides

| Building block | Where | Used for |
|---|---|---|
| A sensor type with a 1:1 table, created and validated by `SensorService` | `SensorType.MIKROTIK`, `mikrotik_routers`, `SensorService.ValidateAsync` | `WINDOWS = 100` with `windows_agents`, and `DIAGNOSTICS = 101` (§8.1) |
| Events with the sensor API's rules: a lock, the dedupe (the last two equal events only move the time), at most 2000 per sensor | `PNMTD/Services/Events/SensorEventWriter.cs` | A heartbeat every minute writes the same 250, so it collapses (§8.2) |
| Heartbeat semantics: 460 after Interval + GracePeriod | `HeartbeatCheckTask` (for `HEARTBEAT`, `HEARTBEAT_VALUECHECK`, `UDP_NODE`) | Add `WINDOWS`, and `DIAGNOSTICS` with a schedule |
| Joining from the UI, audited | MikroTik's join script, `AuthEvents.MikrotikJoinScriptViewed` | Join tokens (§3.3) |
| Keys protected with Data Protection, in a singleton settings row | `MikrotikKeyStore`, `mikrotik_settings`, `BackupDigestKey` | PNMTD's agent signing key (§3.4) |
| A permanent per-device log | `mikrotik_job_log`, `udp_command_log` | `windows_agent_log` (§8.3) |
| A password typed per run, held in memory only | `CredentialVault` (docs/mikrotik-plan.md §7.2) | Run-as passwords typed per run in W6 (§7.4) |
| Anonymous paths outside cookie authentication | `SensorPaths`, `UseWhen(… UseAuthentication)` in `Program.cs` | `AgentPaths` for `/agent/v1/…` (§9.1) |
| The anonymous endpoints' allowlist, rate limiting | `EndpointAuditTests.AnonymousAllowlist`, `UseRateLimiter` (the login only) | The join and the WebSocket (§8.7) |
| A device simulator in the CLI | `pnmtd udp node` (`PNMTD/Cli/UdpCommand.cs`) | `pnmtd windows simulate`, a fake agent for development and tests (§11) |
| Shared contracts | `PNMTD.Lib` | The protocol's records, shared by the agent and PNMTD (§3.6) |
| Sensor pages with tabs, admin pages with tabs, copy fields | `SensorView.vue` (`:tab(…)`), `MikrotikView.vue`, `CopyField.vue` | §10 |
| Importance, the Notification Manager | docs/ai-operations-plan.md §11 | Windows and diagnostics sensors fit without change |
| A release workflow on version tags | `.github/workflows/push.yml` (the Docker image on `v*`) | A workflow of its own on sensor tags, `sensor.yml`, that publishes the executables to the GitHub release (§11) |

---

## 3. The sensor service

### 3.1 One binary, three modes

| Mode | Started by | Does |
|---|---|---|
| **Window** | A double-click. The manifest asks for elevation (`requireAdministrator`), so Windows shows its UAC prompt | Joins and installs, or shows the status (§10.4) |
| **Service** | Windows' service manager: `pnmt-sensor.exe service` | Collects, connects and reports. Hosted with `Microsoft.Extensions.Hosting.WindowsServices` |
| **Command line** | `pnmt-sensor.exe join --url https://pnmt.example.com --token abcd-efgh [--name …] [--quiet]`, `status`, `uninstall`, `collect` (prints the sections as JSON, for support) | For unattended installs: GPO, Intune, RMM |

- **The build:** `net10.0-windows`, WinForms, `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile`, and **`EnableWindowsTargeting=true`**, so it builds on macOS and on GitHub's Linux runners (W0 checks it).
- **Two executables** (R23): `win-x64` and `win-arm64`. The x64 one also runs emulated on Windows 11 ARM64, but the native one is preferred.
- **The size** is measured in W0. Expect 60–80 MB each, compressed; WinForms cannot be trimmed.
- **Windows-only APIs:**
  - `System.Management` (WMI) and `System.Diagnostics.EventLog` (with `EventLogReader`, `EventLogWatcher` and bookmarks);
  - `System.ServiceProcess.ServiceController`;
  - `Microsoft.Win32.Registry`;
  - P/Invoke for `iphlpapi` (TCP/UDP listeners, routes), `wtsapi32` (sessions), `wintrust` (signatures), `netapi32` (local groups) and `advapi32` (`AuditQuerySystemPolicy`);
  - the firewall's COM API (`HNetCfg.FwPolicy2`).

### 3.2 Where it lives

| What | Where |
|---|---|
| The program | `%ProgramFiles%\PNMT Sensor\pnmt-sensor.exe`. The window copies itself there when started from somewhere else |
| The service | Name `PNMTSensor`, display name *PNMT Sensor*, automatic (delayed) start. **Recovery:** restart after 1 min, 1 min, then 5 min, with the failure count reset after a day. **Account: LocalSystem** (R17): it can read the Security log, WMI, BitLocker and the TPM, and in W6 run scripts as admin |
| The configuration | `%ProgramData%\PNMT Sensor\agent.json`: the URL, the agent id, PNMTD's public key, the key's name, the diagnostics last received, the log level. **The folder's ACL: SYSTEM and Administrators only** |
| The state | `%ProgramData%\PNMT Sensor\state\`: the event log bookmarks, buffered results and problems, and *(W6)* stored credentials. The same ACL |
| The private key | A CNG key in the machine key store (`Microsoft Software Key Storage Provider`), non-exportable, `PNMT Sensor <agent id>` |
| The status for the window | `%ProgramData%\PNMT Sensor\status.json`, written by the service every 10 s: connected or not, since, the last heartbeat, the last problems, the diagnostics with their last results. **A file, not a pipe:** no second interface to secure |
| Logs | **`%ProgramData%\PNMT Sensor\logs\`, one file per day** (§3.8). Windows' event log (*Application*, source *PNMT Sensor*) only for starts, stops, joins and crashes |

### 3.3 Joining (R5, R6, R7, R8, R18)

1. **In PNMT, an Admin chooses an existing host** and opens *Add server*: from the host's page (*Add sensor → Windows server*, with the host preselected), or from *Admin → Windows servers*, where the host must be chosen.
   - Optional: a name (default "Windows") and how long the token is valid (24 hours by default, at most 7 days).
   - PNMT shows:
     - **the token**, for example `k7mq-3xtp`;
     - PNMT's URL;
     - the download links with their SHA-256;
     - the command line for unattended installs.
   - **The token is shown once**, because only its hash is stored. Another one is a click away.
2. **On the server**, the administrator double-clicks `pnmt-sensor-x64.exe` and confirms UAC. The window asks for the URL and the token, and optionally a name.
3. **The service checks the URL:**
   - `https`, or plain `http` after a confirmation: the window shows PNMT's key as the URL answers, to compare with the *Add server* dialog, and needs a tick; the command line needs `--allow-http`. The join must then answer the same key;
   - the certificate is validated against Windows' trust store;
   - `GET /agent/v1/join` answers PNMTD's version and its signing key's fingerprint.
4. **It creates the key pair** in the machine key store.
5. **`POST /agent/v1/join`** sends:
   - the token and the public key (SPKI, base64);
   - the host name, the FQDN, the domain or workgroup;
   - the OS caption, edition and build;
   - the agent's version and architecture;
   - `MachineGuid` (from the registry).
6. **PNMTD checks the token:** that it exists, hasn't expired or been used, its host still exists, and the rate limit isn't exceeded. Then it:
   - creates the `WINDOWS` sensor **on the token's host**, with its `windows_agents` row and the public key;
   - marks the token used.
   - **It answers** the agent id, the sensor id, **PNMTD's signing public key**, and the first configuration (the diagnostics), all signed.
7. **The window shows** both fingerprints, the server's key and PNMTD's, as `AB12 CD34 …`. **PNMT's agent log records the same fingerprints**, to compare if in doubt.
8. **It installs and starts the service**, which connects (§3.5).
9. **The window waits** until `status.json` says *connected*, at most 60 s, then shows the status page.
10. **The host's page and PNMT's server list** now show the server. The first full report arrives within a minute.

- **Joining again:**
  - *Re-join* on a server's page creates a token **bound to that sensor**. Joining with it replaces the key and keeps the sensor's history.
  - An unbound token always creates a new sensor. If its `MachineGuid` matches a known server, PNMT notes that in the agent log and on both servers' pages ("possibly the same machine").
- **The command line** does the same, without the window, and exits 0 when connected.

### 3.4 Trust: keys and signatures

| Key | Where | Used for |
|---|---|---|
| **The agent's key** (ECDSA P-256) | The server's machine key store, non-exportable | Signing the connection challenge |
| **PNMTD's signing key** (ECDSA P-256) | `windows_settings.SigningKeyProtected`, Data Protection with the purpose `PNMTD.Windows.AgentSigning.v1`, the key ring on `pnmtd-keys`. **`backup.sh` already covers it** | Signing the join answer, the configuration, and every request (`collect`, `run-diagnostics`, and the scripts of W6). The agent checks against the key it pinned at the join |
| *W6:* **the agent's encryption key** (ECDH P-256) | As the agent's key; registered by the first W6 version over the authenticated connection | Passwords typed per run, encrypted to the agent (ECDH + HKDF + AES-GCM), so that a TLS-inspecting proxy cannot read them |

- **The challenge:**
  - after the WebSocket upgrade, PNMTD sends `challenge { nonce (32 bytes), serverTime }`;
  - the agent answers `hello { agentId, version, os, protocol: 1, capabilities, agentTime, signature }`;
  - the signature covers `"pnmt-agent-v1\n" + origin + "\n" + agentId + "\n" + nonce + "\n" + agentTime`.
  - **No clock dependency.** The nonce is used once.
- **A socket that hasn't authenticated within 10 s is closed** (4408). A wrong signature gets 4401, and a revoked agent 4403 with the reason. The agent logs these and shows them in its window, and stops retrying after 4403.
- **Rotating PNMTD's key** is a later admin action (not in W1–W5). It would be sent signed by the old key.
- **Revoking** (*Admin → Windows servers → … → Revoke*) deletes the public key, closes the connection, and writes the agent log. The sensor and its history stay.

### 3.5 The connection (R10, R13)

- **No new port.** The WebSocket is an HTTP request that is upgraded **on PNMT's existing address and port**: the same Kestrel listener and the same published port as the web UI and the sensor API (`PNMTD_PORT`, 5001, or 443 at the reverse proxy).
  - **The agent's URL is the one people use for PNMT**, for example `wss://pnmt.example.com/agent/v1/ws`.
  - `docker-compose.yml`, the host's firewall and the network need no change. **Only a reverse proxy in front must pass the upgrade** (§8.6).
  - PNMTD adds `app.UseWebSockets()` for the `/agent/v1/ws` path only.
- **The subprotocol:** `pnmt-agent.v1`.
- **Frames:**
  - text frames carry JSON `{ "t": type, "id": message id, "at": time, "b": body }`;
  - binary frames carry the same JSON, gzip-compressed, which is used for sections.
  - **Limits:** 4 MiB per message, 16 MiB decompressed.
- **Keepalive:** a WebSocket ping every 30 s. nginx closes an idle proxied connection after 60 s by default.
- **Reconnects, for ever:** after 1 s, then doubling to 5 minutes, with ±20 % jitter. **PNMTD restarting is normal**, and the agent just comes back.
- **The cadence:**

  | What | When |
  |---|---|
  | **Heartbeat** | Every 60 s (`Windows:HeartbeatSeconds`) |
  | **A changed section** | When a collector finds a new hash (§4.9) |
  | **Logins and connections** | Every 15 s, read on from the logs' bookmarks (§4.7) |
  | **Every section** | Every 6 hours (`Windows:FullUpdateHours`), and after each connect |
  | **Diagnostics results** | After each run |
  | **Problems** | When they happen (§3.8) |

- **The heartbeat's body:**
  - the agent's time and the boot time;
  - CPU %, memory used %, the volume with the least free space and its %;
  - the interactive sessions now;
  - whether a reboot is pending;
  - each section's hash;
  - the configuration's version, the agent's version, and the number of problems since the last heartbeat.
  - About 500 bytes.
- **One connection per agent.** A second one replaces the first, which is closed with 4409.

### 3.6 Messages

All of them are records in `PNMTD.Lib/Agent/`, shared by both sides. The same records type the API v1 answers (§9.2), so the UI's schema is typed too.

| Direction | Type | Body |
|---|---|---|
| PNMTD → agent | `challenge` | nonce, server time |
| agent → PNMTD | `hello` | agent id, version, architecture, the OS (caption, edition, build, supported or not), protocol, capabilities (`collect.v1`, `access.v1`, `diag.ping`, `diag.dns`; W6: `scripts.v1` only when allowed locally, and `parse.v1`), agent time, signature |
| PNMTD → agent | `welcome` (signed) | the configuration: heartbeat and full-update intervals, the access settings, diagnostics assignments with their checks, the configuration version |
| PNMTD → agent | `config` (signed) | The same, when it changes |
| agent → PNMTD | `heartbeat` | §3.5 |
| agent → PNMTD | `section` | name, schema version, hash, collected at, the data (§4) |
| agent → PNMTD | `access` | a batch of logins and connections (§4.7) |
| agent → PNMTD | `diag-result` | assignment, run id, trigger, start, end, results per check (§5.4) |
| agent → PNMTD | `problem` | §3.8 |
| PNMTD → agent | `collect` (signed) | the sections to collect now |
| PNMTD → agent | `run-diagnostics` (signed) | assignment, run id |
| PNMTD → agent | `send-log` (signed) | the number of lines (at most 500) |
| agent → PNMTD | `log` | the lines |
| agent → PNMTD | `ack` | the id of the request, and ok or an error |
| agent → PNMTD | `bye` | uninstalling |

- **Unknown types are ignored and logged** on both sides.
- **A signed request** carries the request id, the agent id, an expiry (5 minutes), and PNMTD's signature over all of them. The agent keeps the ids of the last hour, so a request runs once.

### 3.7 Offline, and time

- **While disconnected**, the agent keeps collecting and running scheduled diagnostics. It buffers:
  - diagnostics results, up to 1,000;
  - logins and connections, up to 5,000 (the event log bookmarks also let it read missed ones from the logs later);
  - problems, up to 200;
  - the latest section of each kind.
  - **It sends all of them after reconnecting.** When a buffer is full, the oldest entries go, and that is reported as a problem.
- **Clock skew:** PNMTD compares the heartbeat's agent time with its own.
  - More than `Windows:MaxClockSkewSeconds` (120) writes **451** "the clock is off by 7 min 12 s".
  - Kerberos fails beyond 5 minutes, so this is worth knowing.

### 3.8 Logging and resilience (R15)

**The log file:**
- **Where:** `%ProgramData%\PNMT Sensor\logs\pnmt-sensor-YYYY-MM-DD.log`, **one file per day** (the server's local date). **The last 10 files are kept**; older ones are deleted at each new day and at start.
- **A size cap of 50 MB per file**, so that a loop can't fill the disk. Past it, lines are dropped, with one note saying how many.
- **The window** writes its own `pnmt-sensor-ui-YYYY-MM-DD.log` next to it, with the same rules.
- **A small logger provider** of our own on top of `Microsoft.Extensions.Logging`, with no third-party library. Each line holds the time with its offset, the level, the component, the message, and on errors the exception with its stack.
- **The level:** Information by default. Debug is switched on in the window, or with `logLevel` in `agent.json`, without a restart.
- **The window's *Open log folder*** opens it. PNMT's *Send log* (§3.6) fetches the last 500 lines.

**Resilience: whatever fails is logged, reported, and the rest carries on.**

| # | Rule | How |
|---|---|---|
| 1 | **Every unit of work is isolated** | Each collector, each diagnostics check, each received message, each timer tick and the connection loop runs in its own `try`/`catch`. A failure is logged, reported (rule 3), and the next unit runs |
| 2 | **Everything has a timeout** | Collectors 30 s, each WMI query 15 s, checks as configured, a send 30 s. A hung WMI provider costs one section, not the service |
| 3 | **Problems are reported to PNMTD** | A `problem` message: the component, the operation, the message, the exception's type, the first 20 lines of its stack, and how often and since when. Deduplicated per component and message per hour, and buffered while offline. PNMTD writes it to the agent log, and shows it on the server's *Agent* tab. **Problems in 3 heartbeats in a row write 451** |
| 4 | **The connection loop never ends** | Any failure → backoff → reconnect. A watchdog restarts the loop if no heartbeat went out for 3 intervals |
| 5 | **A crash restarts the service** | An unhandled exception is logged as fatal, the log is flushed, and the process exits. Windows' recovery settings start it again. **At the next start, the agent reports "restarted after a crash"** with the fatal entry |
| 6 | **Resources are bounded** | The buffers have their caps (§3.7). If the working set exceeds 300 MB, the agent logs it, reports it, and restarts itself cleanly |
| 7 | **Damaged state files are not fatal** | A damaged bookmark or buffer file is renamed `.broken`, logged, reported, and started anew. Without `agent.json`, the service logs it and waits, and the window offers *Re-join* |
| 8 | **The server comes first** | The collectors run at below-normal priority, one at a time. Diagnostics are rate-limited (§5.1) |

### 3.9 Windows versions (R16)

| | Versions |
|---|---|
| **Supported and tested** (manually, §13) | Windows Server 2016, **2019**, 2022 and 2025; Windows 11; **Windows 10 Pro**, Enterprise and LTSC |
| **Anything else** | Logged, reported, and **run anyway** |

- **The check:** at every start, the agent reads the product name, the edition and the build with its UBR, and compares them with its list.
- **Another version** (Windows Server 2012 R2, Windows 8.1, or a Windows 10 build older than 1809) gets:
  - a warning in the log;
  - `supported: false` in `hello`;
  - an amber *not a supported version* badge on the server's page, without an event.
  - **The service runs anyway.** Collectors that find a WMI class missing report the section as *not available* (§3.8, rule 1).
- **Windows 10 Pro** is outside .NET 10's own support list (which names only Windows 10's Enterprise, LTSC and IoT editions), but .NET 10 programs run on it. W0 checks it on a real machine.

---

## 4. What it reports (R1)

### 4.1 Overview

| Section | Contents | Source | Collected |
|---|---|---|---|
| `system` | Host name, OS, uptime, domain, updates, time, Defender, local administrators | WMI, registry, `netapi32`, `w32time` | 15 min, at boot, every 6 h |
| `hardware` | Machine, firmware, CPU, memory, disks, volumes, TPM, GPU | WMI (`Win32_*`, `MSFT_PhysicalDisk`) | At boot, every 6 h |
| `network` | Adapters, addresses, routes, DNS, listeners, firewall, profiles, proxy | IP helper API, WMI (`MSFT_NetAdapter`, `MSFT_NetConnectionProfile`), firewall COM | On a network change event, 5 min, every 6 h |
| `services` | Every service, its state, start type, account, binary, and its class | Service manager, registry, `wintrust` | 1 min (cheap), every 6 h |
| `remote-access` | The features of §4.6 and their exposure | Registry, services, listeners, firewall | 5 min, every 6 h |
| `access` | Sessions now, the audit policy's state, the Security log's span, counts of the last 24 hours. **The logins and connections themselves are a stream** (§4.7) | `wtsapi32`, `AuditQuerySystemPolicy`, the event logs | 5 min, every 6 h; the stream live |
| `event-log` | The last 100 entries of System and Application with level Critical, Error or Warning | `EventLogReader`, `EventLogWatcher` | Live (debounced 10 s), every 6 h |
| `diagnostics` | Not a section: the results go to their own sensors (§5) | – | – |

- **Each section is gzip-compressed.** Expect 10–60 KB per server for all of them together (W0 measures).
- **A section whose collector failed** is sent with `errors` filled. The others are unaffected (§3.8).

### 4.2 System

- **Identity:** host name, FQDN, domain or workgroup, domain role (workstation, member server, domain controller), `MachineGuid`.
- **OS:** caption, edition, version, build and UBR, install date, architecture, licence activated or not, **supported or not** (§3.9).
- **Uptime:** the boot time, and the last shutdown's reason (event 1074 or 6008).
- **Updates:** the last hotfix installed and its date, and **whether a reboot is pending** (CBS, Windows Update, pending file renames).
- **Time:** time zone, the time source and the last sync (`w32tm`), and the skew against PNMTD (§3.7).
- **Security:** Defender (real-time on, signature age, last scan), the firewall per profile (on or off), BitLocker per volume, Secure Boot.
- **Members of the local *Administrators* group**, with nested domain groups resolved where the domain answers.
- **Sessions now:** who is logged on, and whether through the console or RDP.
- **Virtualisation:** a hypervisor present, and the vendor (Hyper-V, VMware, KVM, …).

### 4.3 Hardware

- **The machine:** manufacturer, model, serial number, BIOS or UEFI version and date, and TPM (version, ready).
- **CPU:** the processors, cores and threads, and the clock.
- **Memory:** the total, and the modules (slot, size, speed, manufacturer).
- **Disks:**
  - physical disks: model, size, media (HDD, SSD, NVMe), bus, health and operational status, and SMART *predict failure*;
  - volumes: letter, label, file system, size, free space, BitLocker.
- **Graphics adapters and batteries**, when present.

### 4.4 Network (in detail)

- **Adapters:**
  - name and description, driver and its version, MAC, type (Ethernet, Wi-Fi, virtual, team);
  - status, link speed, MTU;
  - DHCP or static, and the DHCP server and lease;
  - **IPv4 and IPv6 addresses with prefixes**, gateways, **DNS servers**, DNS suffix and registration;
  - VLAN id where the driver exposes it;
  - NIC teams (`MSFT_NetLbfoTeam`) and Hyper-V switches.
- **The network profile** per connection (Domain, Private, Public).
- **Routes:** the default route, and the persistent and non-local routes.
- **Listening ports:** TCP and UDP, the local address, and the process and service that own each.
- **Connections:** the number established, by state. No list: it changes too often. **Logins** over the network are in §4.7.
- **The firewall:** each profile's state and default inbound action, and **the enabled inbound rules for the ports that listen**.
- **The proxy** (WinHTTP) and the hosts file (its non-default entries).

### 4.5 Services, and which ones are Windows' own

- **Each service:**
  - name, display name, state, start type (automatic, delayed, manual, trigger, disabled), account;
  - the binary's path and, for services that run in `svchost`, their `ServiceDll`;
  - the version and the company.
- **The class**, decided by the binary (the `ServiceDll` for `svchost` services):

  | Class | Means |
  |---|---|
  | **Windows** | Under `%SystemRoot%`, and signed (embedded or by catalog, checked with `WinVerifyTrust`) by *Microsoft Windows* |
  | **Microsoft** | Signed by Microsoft, but not part of Windows: SQL Server, Exchange, Defender's platform under `ProgramData` |
  | **Third-party** | Signed by anyone else. **"Special"** |
  | **Unsigned** | No valid signature. **Special, and highlighted.** The PNMT sensor service itself is unsigned (§14) and is recognised as itself |

- **"Active"** means running. Also flagged: **automatic but not running** (except the trigger-start services, which Windows starts on demand).
- **The list shows the special ones first.** A filter shows everything.

### 4.6 Remote access

| Feature | Detected by | Active when |
|---|---|---|
| **RDP** | `fDenyTSConnections = 0`, *TermService* running, the port (`RDP-Tcp\PortNumber`), NLA required (`UserAuthentication`), the *Remote Desktop* firewall group | Allowed and listening |
| **WinRM / PowerShell remoting** | *WinRM* running, its listeners (HTTP 5985, HTTPS 5986), the firewall | A listener exists and the service runs |
| **SMB server** | *LanmanServer* running, 445 listening, the shares (including admin shares, `AutoShareServer`), SMB1 on or off, signing required | The service runs and 445 listens |
| **WMI (remote)** | *Winmgmt*, and the *Windows Management Instrumentation (WMI)* firewall group | The firewall allows it inbound |
| **DCOM** | `HKLM\SOFTWARE\Microsoft\Ole\EnableDCOM` | `Y` |
| **RPC** (endpoint mapper) | 135 listening, and the firewall | Allowed inbound |
| **Remote Registry** | *RemoteRegistry*: state and start type | Running, or set to start |
| **OpenSSH Server** | *sshd*, and its port from `%ProgramData%\ssh\sshd_config` | Running |
| **Windows Admin Center** | Its service (`ServerManagementGateway`, or `WindowsAdminCenter` in newer versions) and its port | Installed and running |
| **Remote Assistance** | `fAllowToGetHelp`, `fAllowFullControl` | Allowed |
| **Quick Assist** | The app (`MicrosoftCorporationII.QuickAssist`) or the older `quickassist.exe` | Installed. It only works outbound, started by a logged-on user |
| **Server Manager remote management** | Remote management enabled: WinRM listening and its firewall rules (W0 finds the setting itself) | Enabled |
| **Others** | Telnet server, the IIS management service (WMSVC), FTP, SNMP, and **third-party remote tools** by their services: TeamViewer, AnyDesk, ScreenConnect, Splashtop, RustDesk, VNC servers, … (a list in the agent, extended with each release) | Installed and running |

- **Per feature:** active or not, the ports, whether they listen, **whether the firewall lets them in** (and on which profiles), and notes (for example *NLA off*, or *SMB1 on*).
- **The overview** sums it up as "RDP · WinRM · SMB open".
- **Who actually connected** through each of them is in §4.7.

### 4.7 Logins and connections (R1, R19)

**What is recorded:**

| Who | Kinds | Recorded |
|---|---|---|
| **Everyone** | Console (logon type 2), **RDP** (10), cached credentials (11), unlock (7), **RDP connections**, **SSH**, **WinRM / PowerShell remoting** | Every success, with its logoff where known |
| **Admins only** | **Network logons** (3: SMB, including `C$` and `ADMIN$`, RPC, WMI), explicit credentials (4648: `runas`, scheduled tasks), batch (4) | Every success |
| **Ordinary users, network** | File-share access and other network logons | **Not recorded** (R19: no users of a file server) |
| **Everyone, failed** | Every kind, including network | **Aggregated:** one entry per user, source and kind per 10 minutes, with a count |
| **Never** | Machine accounts (`…$`), anonymous logons, and built-in service logons (types 4 and 5 by `SYSTEM`, `LOCAL SERVICE`, `NETWORK SERVICE`, virtual service accounts) | – |

- **An admin** is a logon that received special privileges (event 4672), or an account in the local *Administrators* group, or in *Domain Admins* or *Enterprise Admins*.
- **Repeated identical successes** (the same user, source and kind within 10 minutes, as backup or monitoring software causes) become one entry with a count and the first and last time.

**The sources** (the event ids are confirmed in W0):

| Log | Events | For |
|---|---|---|
| Security | 4624 logon, 4625 failed logon, 4634/4647 logoff, 4648 explicit credentials, 4672 special privileges, 4740 account locked out, 4778/4779 session reconnected/disconnected | Every kind |
| `Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational` | 1149: RDP user authenticated, with the source address | RDP connections, even those that never reach a logon |
| `Microsoft-Windows-TerminalServices-LocalSessionManager/Operational` | 21–25: session logon, shell start, logoff, disconnect, reconnect | RDP sessions and their duration |
| `OpenSSH/Operational` | sshd's accepted and failed logins, with the key or password and the source | SSH |
| `Microsoft-Windows-WinRM/Operational` | The shell being created, with the user | PowerShell remoting |
| `Microsoft-Windows-SMBServer/Security` | Failed SMB authentications | SMB failures |

**Per entry:**
- the time, the outcome, and for failures the reason (bad password, unknown user, locked out, expired, disabled: from 4625's status codes);
- **the user** (domain\name), and whether it is an admin;
- **how:** console, RDP, SSH, WinRM, SMB/network, cached, unlock, explicit credentials;
- the authentication: Kerberos, NTLM (and its version), key or password for SSH;
- **from where:** the source address and port, and the workstation's name;
- whether the token was elevated;
- the logon id, which pairs the logoff and so gives the session's duration;
- the count and the last time, for aggregated entries.

**How nothing is missed:**
- **Within 15 seconds:** each log is read on from its bookmark every 15 s, and new entries are sent (§3.5).
- **Bookmarks:** each log's last read record is saved in `state\bookmarks.json`. After a restart, or a time offline, the agent reads on from there, **as long as the log still holds the records**.
- **PNMTD stores them** in `windows_logins`, deduplicated by the log and the record id, for **180 days**.

**The audit policy check** (in the `access` section):
- The agent reads the effective audit policy (`AuditQuerySystemPolicy`) for *Logon*, *Logoff*, *Special Logon*, *Account Lockout*, *Other Logon/Logoff Events* and *Credential Validation*, success and failure.
- It checks that the operational logs above are enabled.
- **It reads the Security log's maximum size and its oldest record**, for example "it holds 9 hours".
- **Whatever is missing is shown on the *Access* tab**, with the command that fixes it (for example `auditpol /set /subcategory:"Logon" /failure:enable`), because the agent itself changes nothing (§6). It is also flagged in the server list.

**Optional events** (per server, §10.2):

| Code | When | Default |
|---|---|---|
| **254** WINDOWS_ADMIN_LOGIN | An admin logs in by console, RDP, SSH or WinRM. Message: "CONTOSO\jdoe: RDP from 10.0.4.17 (NLA, Kerberos)" | **On** |
| **454** WINDOWS_LOGIN_FAILURES | `FailedLoginThreshold` (10) failed logins within 10 minutes from one source, or for one user, or a lockout (4740) | **On** |

- **Without a notification rule nothing is sent.** Which of them reach whom is set in the notifications, as for every sensor.

### 4.8 The event log

- **System and Application**, with the levels **Critical, Error and Warning**: everything except Information and Verbose. The logins and connections of §4.7 are kept separately.
- **The newest 100 of both together:** the time, the log, the level, the source, the event id, the category, the message (rendered, capped at 2 KB), and the user.
- **Within about 15 s:** the login stream's poll notices a new entry and the section is collected again.
- **Optional, per server:** *Event on new Critical entries* writes **453** with the entry's source and message. It is off by default, because some servers log Critical often.

### 4.9 Changes

- **On PNMTD, a section that arrives with a new hash is compared with the stored one** by a per-section `Differ`. The comparison writes readable lines to `windows_changes`, for example:
  - "Service *AnyDesk* added (third-party, automatic, running)"
  - "RDP enabled (port 3389, NLA on)"
  - "10.0.1.20/24 → 10.0.1.21/24 on Ethernet0"
  - "New local administrator CONTOSO\jdoe"
  - "Windows updated: build 17763.6532 → 17763.6659"
  - "Logon failure auditing turned off"
- **Notable changes write 253** with a summary:
  - services added, removed, or with a new start type or account;
  - any change to remote access;
  - the local administrators;
  - addresses, gateways or DNS servers;
  - the OS build;
  - disks or memory;
  - the audit policy.
  - **Logins and event log entries are logs, not changes.**
- **Kept:** 180 days.

---

## 5. Diagnostics (R2, R3)

### 5.1 Sets and checks

- **A set** has a name, a description, and an ordered list of up to 50 checks. It is defined on *Admin → Diagnostics*.
- **A check** has a name, a type, and typed parameters:

  | Type | Parameters | Passes when |
  |---|---|---|
  | `ping` | Target (a host name or an address), count (4), timeout (1000 ms), IPv4 or IPv6 or either | Loss ≤ `maxLossPercent` (0), and, if set, the average round trip ≤ `maxAvgMs` |
  | `dns` | Name, record type (A, AAAA, CNAME, MX, TXT, SRV, PTR, NS, SOA), server (empty: the system's resolvers; or an address) | It resolves. If set, an answer equals or contains `expect`, and the time ≤ `maxMs` |

- **Validated on PNMTD:**
  - names and addresses are syntactically valid;
  - counts and timeouts are capped (at most 10 pings, at most 5 s);
  - the interval is at least 1 minute;
  - **so a set can't make servers flood a target.**

### 5.2 Assignments

- **An assignment** links a set to a server's `WINDOWS` sensor, with:
  - **Scheduled**, and its interval in minutes (default 15), or **Manual**;
  - enabled or not.
- **Each assignment creates a sensor** of type `DIAGNOSTICS` (101) on the server's host, named "{set}", for example *Diagnostics: branch connectivity* (decided in the review).
  - It has its own state, importance and notification rules.
  - **Deleting the assignment deletes the sensor**, and its history goes with it, after a confirmation.
- **The agent receives** its assignments and their checks in its signed configuration.

### 5.3 Running

- **Scheduled:** by the agent's own timer, so they keep running while PNMTD is unreachable. The results are buffered (§3.7).
- **Manual:** *Run now* (CanWrite) sends `run-diagnostics`. The result shows in the UI within seconds.
- **The checks:**
  - ping uses `System.Net.NetworkInformation.Ping`;
  - DNS uses the system resolver (`DnsQuery_W`), or for a given server the `DnsClient` library, which PNMTD already uses for its DNS check.
  - The checks of a run go in order, each with its timeout.
  - **A check that throws is recorded as *error*** with the message, and the next check runs (§3.8).

### 5.4 Results and events

- **`diagnostic_runs`**: the assignment, the diagnostics sensor, the trigger (schedule or manual, and who), the start and the end, the outcome, and the results.
  - **Each result:** pass, fail or error, the values (loss, min/avg/max ms; answers, the resolver, the time), and a message.
  - **Kept:** 30 days, and at least the last 100 per assignment.
- **The events** on the diagnostics sensor:
  - **252** "All 5 checks passed";
  - **452** "2 of 5 failed: ping gw-zurich (100 % loss), dns intranet.example (NXDOMAIN)".
  - The same outcome and message collapse by the dedupe.
- **Scheduled assignments have heartbeat semantics:** no result within the interval plus a grace period (2 min) writes **460**.
- **Manual ones don't:** their state is the last run's.

### 5.5 Ready for other agents

- **The model is not Windows-specific.** Sets, checks, assignments and runs live in `PNMTD/Diagnostics/`.
- **An assignment's target** is any sensor whose agent announced the check types in `hello` (`diag.ping`, `diag.dns`).
- **Later:**
  - a Linux agent announces the same;
  - PNMTD itself could become a target (the same set from PNMT's side, to compare);
  - new check types (TCP port, HTTP, traceroute) are a record and an executor each.

---

## 6. Read-only (R11)

- **Before W6, the agent knows exactly these requests:** `collect`, `run-diagnostics` (only an assignment it already has, with its checks as configured), and `send-log`. Each is signed (§3.6). **There is no way to run a program, a script or a command line.**
- **Diagnostics can't be turned into commands:** a check's type is an enum, and its parameters are typed and validated on both sides.
- **The agent changes nothing on the server**, not even the audit policy it reports as missing (§4.7).
- **Nothing the agent collects is a secret:** no passwords, keys, file contents or memory. Logins and event log messages contain user names and addresses. That is why the *Access* and *Event log* tabs need the User role (§10.2).

---

## 7. PowerShell scripts (W6, R12)

### 7.1 The library, and the parameters parsed on the agent (R22)

- ***Admin → Windows servers → Scripts:*** a script has a name, a description and its text, with a version for each change.
- **The parameters are recognised on an agent** (decided in the review), and the agent sends PNMT what matters:
  - **When a version is saved,** PNMT sends its text to a connected agent in a signed `parse-script` request.
  - **The agent parses it with a fixed parser script** that is built into the agent, in Windows PowerShell 5.1, which every supported Windows has: `[System.Management.Automation.Language.Parser]::ParseFile`.
  - **The script itself is never executed for this**, so parsing needs no local permission (§7.2), and the `parse.v1` capability is always announced from W6 on.
  - **It returns:**
    - the parameters: name, type, mandatory, default, position, `ValidateSet`, `ValidateRange`, `HelpMessage`;
    - the comment-based help (`.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER`);
    - syntax errors;
    - the best-practice findings (§7.1, below).
  - **PNMT stores the result** with the version.
  - **With no agent connected,** the version is saved as *parameters pending*, and parsed when the next agent connects.
  - **Before every run,** the executing agent parses the script again and refuses arguments that don't match it.
- **The run dialog builds its form from the parameters:** a switch is a checkbox, a set is a select, an integer is a number field, a `[securestring]` is a password field, and so on.
- **"Best practice" is checked on the agent, from the parsed script, and shown as warnings:**
  - `[CmdletBinding()]`;
  - typed parameters;
  - comment-based help;
  - no `Invoke-Expression`;
  - `Set-StrictMode`, and `$ErrorActionPreference = 'Stop'`;
  - no plain-text passwords in parameters (`[securestring]` or a credential instead).
  - **A new script starts from a template** that follows them.

### 7.2 Allowed only locally

- **Off by default on every server.** *Allow PNMT to run scripts on this server* is a switch in the service's window, which needs a local administrator. Then either:
  - **only the scripts registered on the server** (the default): an administrator adds each `.ps1` in the window, which copies it, records its SHA-256 and the account it runs as, and optionally schedules it (see *Local scripts* in §16); PNMT may run exactly these texts, each as its own account;
  - or **any script PNMT sends**, optionally only as named run-as accounts.
- **Only then** does the agent announce `scripts.v1`. **PNMT cannot turn it on from the outside.**

### 7.3 Running

- **Admin only, audited**, from a server's *Scripts* tab or for several servers at once, with the script, its arguments and a run-as account.
- **The request** (`run-script`, signed) carries:
  - the script's text and hash;
  - **the arguments as typed JSON values**;
  - the account;
  - for a password typed per run, the encrypted password (§7.4);
  - a timeout (default 10 minutes).
- **The agent:**
  - checks the signature, the expiry, the local allowlists, the hash, and the arguments against its own parse;
  - writes the script to a folder only SYSTEM can read;
  - starts `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <wrapper.ps1>`, or `pwsh.exe` if chosen and installed;
  - the wrapper reads the arguments from a JSON file and **splats them**: **arguments are never pasted into a command line**;
  - it runs **as the account** (§7.4);
  - it collects stdout, the error stream, the exit code and the duration (capped at 1 MiB);
  - it deletes the files, and logs the run in its log file and in the Windows event log.
- **PNMT keeps** the run (script version, arguments, account, who, when, output, exit code) as a history, as for MikroTik commands (docs/mikrotik-plan.md §8.3). An Admin can redact arguments.

### 7.4 Run-as accounts and passwords (R17, R20)

| Kind | Password | Where it is kept |
|---|---|---|
| **The service itself** (LocalSystem): **runs as admin** (R17) | None | – . Allowed only if the server's local allowlist names it |
| **An account with a password stored on the server** (R20) | Entered **locally, in the service's window** on that server: *Stored accounts → Add* | **On that server only:** encrypted with DPAPI (machine scope, with an extra random secret), in `state\credentials`, readable only by SYSTEM. **It never reaches PNMT and never crosses the network.** PNMT knows the account's name only |
| **A password typed per run** | In PNMT's run dialog | Held in memory as `CredentialVault` holds MikroTik admin passwords, **encrypted to the agent's encryption key** (§3.4), never stored or logged |
| **A gMSA** | None | Active Directory |

- **Stored accounts are for when they are needed** (R20): a script that must run as a specific domain account without anyone typing its password, as scheduled scripts will later.
- **The limits of "secure":** DPAPI protects against other users, copies of the disk to another machine, and backups. **A local administrator of that server can still recover the password**, as they can recover any secret on their machine.
- **Running as another account** uses `LogonUser` and `CreateProcessAsUser`, which LocalSystem may do.

---

## 8. Integration into PNMTD

### 8.1 A server is a sensor

- **`SensorType.WINDOWS = 100`.**
  - Interval 60 (the heartbeat), GracePeriod 120.
  - **Created only by a join, never through the sensor form.** For this type, the form opens *Add server* with the host preselected (§3.3).
- **`SensorType.DIAGNOSTICS = 101`,** created by an assignment.
- **`HeartbeatCheckTask`** includes both (DIAGNOSTICS only when scheduled).
- **`SystemService.SensorTypes()`** and the docs page describe both.

### 8.2 Events and status codes

| Code | Name | Sensor | When |
|---|---|---|---|
| **250** | WINDOWS_OK | Windows | A heartbeat. The message is stable: "SRV01: Windows Server 2019 Standard 17763, agent 1.3.0", so it collapses by the dedupe |
| **251** | WINDOWS_RESTARTED | Windows | The boot time moved. The message includes the last shutdown's reason |
| **253** | WINDOWS_CHANGED | Windows | A notable change (§4.9) |
| **254** | WINDOWS_ADMIN_LOGIN | Windows | Optional, on by default (§4.7) |
| **451** | WINDOWS_AGENT_PROBLEM | Windows | Clock skew; problems in 3 heartbeats in a row (§3.8); a key or protocol problem |
| **453** | WINDOWS_CRITICAL_EVENT | Windows | Optional, off by default (§4.8) |
| **454** | WINDOWS_LOGIN_FAILURES | Windows | Optional, on by default (§4.7) |
| **460** | HEARTBEAT_MISSING | both | No heartbeat or result within Interval + GracePeriod (the existing code) |
| **252** | DIAGNOSTICS_OK | Diagnostics | Every check passed |
| **452** | DIAGNOSTICS_FAILED | Diagnostics | At least one failed |

- **250–254 and 450–454 are free.** Package one uses 220–221 and 420–427.

### 8.3 Data model

One EF migration per phase: `AddWindowsAgents` (W1), `AddWindowsSectionsAndAccess` (W2–W3), `AddDiagnostics` (W4), `AddWindowsScripts` (W6).

| Table | Columns |
|---|---|
| `windows_settings` (one row) | Id, SigningKeyProtected, SigningPublicKey, SigningKeyFingerprint, CreatedAt |
| `windows_join_tokens` | Id, TokenHash (unique), Prefix (the first 2 characters, for the list), **HostId (required) → cascade**, SensorId → cascade (for a re-join), Name, CreatedAt, CreatedBy, CreatedByName, ExpiresAt, UsedAt, UsedFromAddress, ResultSensorId, RevokedAt |
| `windows_agents` (1:1 with the sensor) | SensorId (primary key → cascade), AgentId (unique), PublicKey, KeyFingerprint, EncryptionKey (W6), JoinedAt, MachineGuid, Hostname, Fqdn, Domain, OsCaption, OsEdition, OsBuild, OsSupported, Architecture, AgentVersion, ConfigVersion, Connected, ConnectedAt, LastSeenAt, RemoteAddress, BootTime, ClockSkewSeconds, Heartbeat (jsonb: the last values), **AdminLoginEvents (default true), FailedLoginEvents (default true), FailedLoginThreshold (default 10), CriticalEvents (default false)**, RevokedAt |
| `windows_sections` | SensorId → cascade, Section, primary key on both. SchemaVersion, Hash, CollectedAt, ReceivedAt, Content (bytea, gzip JSON), Errors |
| **`windows_logins`** | Id, SensorId → cascade, At, LastAt, Count, Outcome, FailureReason, User, Domain, IsAdmin, Kind, AuthPackage, SourceAddress, SourcePort, Workstation, Elevated, LogonId, LoggedOffAt, Channel, RecordId. **Unique on (SensorId, Channel, RecordId).** Indexed on (SensorId, At). **Kept 180 days** |
| `windows_changes` | Id, SensorId → cascade, Section, At, Summary, Details (jsonb, small), Notable. Kept 180 days |
| `windows_agent_log` | Id, SensorId → set null, AgentName, At, Kind (joined, connected, disconnected, auth-failed, revoked, uninstalled, config-sent, request, **problem**, crashed, error), Message, Details (jsonb: the problem's component, exception, stack, count), Address. **Permanent, and it outlives the server**, like `mikrotik_job_log`. Kept 1 year |
| `diagnostic_sets`, `diagnostic_checks` | Set: Id, Name (unique), Description, CreatedAt, UpdatedAt. Check: Id, SetId → cascade, Order, Name, Type, Parameters (jsonb, typed per type) |
| `diagnostic_assignments` | Id, SetId → restrict, TargetSensorId → cascade, SensorId (the DIAGNOSTICS sensor) → cascade, Mode, IntervalMinutes, Enabled |
| `diagnostic_runs` | Id, AssignmentId → cascade, SensorId, Trigger, RequestedBy, StartedAt, FinishedAt, Outcome, Results (jsonb) |
| *W6:* `windows_scripts`, `windows_script_versions` (with the parsed parameters, help and findings), `windows_run_as`, `windows_script_runs` | As in §7 |

### 8.4 Server components

| Component | Where | Does |
|---|---|---|
| `AgentPaths` | `PNMTD/Agents/` | `/agent/v1/…`: no cookie authentication, no CSRF, no ProblemDetails, as `SensorPaths` |
| `AgentJoinEndpoint` | `PNMTD/Agents/` | `GET` and `POST /agent/v1/join`, rate-limited |
| `AgentHub` | `PNMTD/Agents/` (singleton) | Accepts WebSockets on the existing listener, authenticates, keeps the connections, routes messages, sends signed requests |
| `AgentKeyStore` | `PNMTD/Agents/` | PNMTD's signing key (Data Protection), signatures, verification |
| `WindowsReportService` | `PNMTD/Windows/` | Heartbeats (250, 251, 451, batched `LastSeenAt` writes every 30 s), sections, the differs, changes and 253, problems into the agent log |
| `WindowsAccessService` | `PNMTD/Windows/` | Logins and connections into `windows_logins`, 254 and 454 |
| `DiagnosticsService` | `PNMTD/Diagnostics/` | Sets, checks, assignments (with their sensors), runs, 252/452 |
| `WindowsMaintenanceTask` | `PNMTD/Windows/` | Expired tokens, and the retention of logins, changes, logs and runs |
| Domain services and controllers | `Services/Domain/WindowsServerService.cs`, `DiagnosticsSetService.cs`; `Api/V1/WindowsControllers.cs`, `DiagnosticsControllers.cs` | API v1 (§9.2) |
| `pnmtd windows …` | `PNMTD/Cli/WindowsCommand.cs` | `simulate` (a fake agent: joins with a token, sends canned sections and logins from fixtures, answers requests), `tokens` (list, revoke) |
| The agent | `PNMTD.Sensor.Windows/` (new project) | §3 and §4. Its collectors in `Collectors/`, the access stream in `Access/`, diagnostics in `Diagnostics/`, the log file in `Logging/`, the window in `Ui/` |
| The protocol | `PNMTD.Lib/Agent/` | The records of §3.6 and §4, and the signing and verification of requests, shared by both sides |

### 8.5 Configuration

| Setting (`.env` name) | Default | Notes |
|---|---|---|
| `Windows__Enabled` | `true` | The agent endpoints answer 404 when off |
| *(no setting)* | | Plain HTTP is the agent's choice, confirmed at the join (§3.3); PNMTD accepts both |
| `Windows__HeartbeatSeconds`, `Windows__FullUpdateHours` | 60, 6 | Sent to the agents in their configuration |
| `Windows__TokenHours` | 24 | The default validity; at most 168 |
| `Windows__MaxClockSkewSeconds` | 120 | |
| `Windows__MaxMessageBytes` | 4194304 | Decompressed: 4 times as much |
| `Windows__JoinRateLimitPerMinute` | 10 | Per address. 50 failures in an hour close the join for 15 minutes |
| `Windows__AgentDownloadBaseUrl` | `https://github.com/muqiuq/PNMTD/releases/download` | For the *Download* tab: `{base}/sensor-{version}/pnmt-sensor-x64.exe` |

### 8.6 Deployment: the existing port (R13)

- **Nothing new to open.** The agents use PNMT's existing HTTPS address and port, as people and sensors do. There is **no new entry in `docker-compose.yml`** and no new firewall rule.
- **WebSockets must pass a reverse proxy in front:**
  - **Caddy** and **Traefik** pass them by default.
  - **nginx** needs `proxy_http_version 1.1; proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection $connection_upgrade;` (with the usual `map`), and a `proxy_read_timeout` above the 30 s ping.
  - `deploy/README.md` adds these lines to its nginx example. W0 tests the proxy in use.
- **Without a reverse proxy** (PNMTD's published port directly), nothing is needed. The agents use HTTPS where PNMT has it; plain HTTP works after a confirmation at the join (§3.3), and is then not encrypted.
- **The image doesn't grow:** the executables are release assets (§11).
- **`backup.sh` needs no change:** the database and the key ring hold everything.

### 8.7 Security

| Threat | Mitigation |
|---|---|
| **Guessing a token** | 8 characters from 31 (no `0 o 1 i l`): about 40 bits. Single use, 24 hours, bound to a host, 10 attempts a minute per address, and the join closes for 15 minutes after 50 failures in an hour. Failures are audited (`AuthEvents.WindowsJoinFailed`) |
| **A token used by someone else** before the server | The admin sees the join in the agent log (host name, address, fingerprints) and the server in the list. *Revoke* ends it |
| **Impersonating an agent** | The non-exportable key, a fresh challenge per connection, one connection per agent, revocation |
| **Impersonating PNMTD** (DNS, a mis-issued certificate) | TLS validation, and **PNMTD's signature on everything it sends, with the key pinned at the join** |
| **A malicious agent attacks PNMTD** | Size limits (4 MiB, 16 MiB decompressed, which also stops gzip bombs), typed parsing with caps on string and list lengths, a message rate per agent (60 a minute, plus sections and access batches), no HTML anywhere, and the authentication deadline |
| **A compromised PNMT attacks servers** | No command in the protocol before W6. Diagnostics typed and capped. Scripts only where a local administrator allowed them (§7.2). Stored passwords never leave their server (§7.4) |
| **A TLS-inspecting company proxy** | It can read reports, as for any HTTPS. It can't forge PNMTD's requests (signatures), and it can't read passwords typed per run (§3.4) |
| **Sensitive data in reports** (users, addresses) | *Access* and *Event log* need the User role. Everything else is CanRead, as for other sensors |
| **New anonymous endpoints** | Exactly two paths, `/agent/v1/join` (`GET` and `POST`) and `GET /agent/v1/ws`, in `EndpointAuditTests.AnonymousAllowlist` |
| **An unsigned executable** (no code-signing certificate) | Downloaded from this repository's GitHub release, with the SHA-256 shown in PNMT and attached to the release. `Get-FileHash` checks it. SmartScreen warns: *More info → Run anyway*, or `Unblock-File` for unattended installs (§14) |

---

## 9. API

### 9.1 The agent's endpoints (anonymous, authenticated by token or key, on the existing port)

| Endpoint | Purpose |
|---|---|
| `GET /agent/v1/join` | The URL check: PNMTD's version and its key's fingerprint |
| `POST /agent/v1/join` | The join (§3.3) |
| `GET /agent/v1/ws` | The WebSocket (§3.5) |

### 9.2 API v1

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/v1/windows/servers` | CanRead | The fleet: state, host, host name, OS (and supported or not), uptime, agent version (*outdated* when older than PNMTD's), connected, last heartbeat, remote access summary, audit gaps, diagnostics state |
| `GET /api/v1/windows/servers/{sensorId}` | CanRead | The agent's details, the heartbeat, the recent problems |
| `GET /api/v1/windows/servers/{sensorId}/sections/{section}` | CanRead; `access` and `event-log`: CanWrite | The typed section |
| `GET …/logins` | CanWrite | Paged. Filters: outcome, admin, kind, user, source, period |
| `GET …/changes` | CanRead | Paged |
| `GET …/log` | Admin | The agent log, with the problems |
| `POST …/collect` | CanWrite | `{ sections }` |
| `PUT …/settings` | CanWrite | `{ adminLoginEvents, failedLoginEvents, failedLoginThreshold, criticalEvents }` |
| `POST …/revoke`, `POST …/rejoin-token` | Admin | Audited |
| `GET /api/v1/windows/join-tokens`, `POST`, `DELETE /{id}` | Admin | `POST { hostId, name?, validHours? }` answers the token once. Audited (`WindowsJoinTokenCreated`, `…Revoked`) |
| `GET /api/v1/windows/agent` | Admin | PNMTD's version, the release links for both architectures, and their SHA-256 (fetched from the release's `.sha256` assets and cached; links only if GitHub can't be reached) |
| `GET /api/v1/diagnostics/sets`, `GET /{id}` | CanRead | |
| `POST`, `PUT /{id}`, `DELETE /{id}` | Admin | A set with its checks. `DELETE` refuses while assigned |
| `GET /api/v1/diagnostics/assignments` | CanRead | Filtered by server |
| `POST`, `PUT /{id}`, `DELETE /{id}` | Admin | Creates or deletes the DIAGNOSTICS sensor |
| `POST /api/v1/diagnostics/assignments/{id}/run` | CanWrite | 409 when the agent is offline |
| `GET /api/v1/diagnostics/runs`, `GET /{id}` | CanRead | |
| *W6:* `/api/v1/windows/scripts…` (with `POST /{id}/versions` → parse on an agent), `/api/v1/windows/run-as…`, `POST /api/v1/windows/servers/{id}/scripts/run`, the history | Admin | As for MikroTik commands |

- **`AuthEvents`:** `WindowsJoinTokenCreated`, `WindowsJoinTokenRevoked`, `WindowsJoined`, `WindowsJoinFailed`, `WindowsAgentRevoked`, `DiagnosticsSetChanged`, `DiagnosticsAssignmentChanged`; W6 adds `WindowsScriptChanged` and `WindowsScriptRun`.

---

## 10. Web UI

### 10.1 Admin → Windows servers

| Tab | Contents |
|---|---|
| **Servers** | The fleet list, with filters (state, OS, unsupported OS, outdated agent, remote access open, audit gaps, diagnostics failing). *Add server* |
| **Join tokens** | Pending, used and expired tokens (the prefix, the host, the name, who created it, until when, used by which server), and *Revoke* |
| **Download** | PNMTD's version, **the links to that version's GitHub release** for x64 and ARM64 with their SHA-256, the `Get-FileHash` line to check them, and the command line for unattended installs |
| **Agent log** | All agents: joins, connections, problems, crashes, failures, revocations |
| *W6:* **Scripts**, **Run-as accounts** | §7 |

- ***Add server*** is a dialog: **the host (required; preselected when opened from a host's page)**, a name and the validity (optional) → the token in large type with a copy button, PNMT's URL, the download links, the command line, and the instructions.

### 10.2 A server's sensor page

- **Its tabs:** `overview`, `hardware`, `network`, `services`, `remote-access`, `access` (User), `event-log` (User), `diagnostics`, `changes`, `agent`. They are added to the route's `:tab(…)` list in `router/index.ts`.
- **Overview:**
  - host name, OS (with the *not a supported version* badge where it applies), **uptime**;
  - pending reboot, Defender, the time's skew;
  - the remote access summary;
  - **the last admin logins, and failed logins in the last 24 hours**;
  - the diagnostics' states;
  - the last 5 changes;
  - the agent's state and its recent problems.
- **Hardware, network, services, remote access and event log** show their section as tables, with filters where useful: the services by class and state, the event log by level and log.
  - Each shows when it was collected, with *Collect now*.
- **Access** (logins and connections):
  - the sessions now;
  - the list from `windows_logins`, filtered by outcome, admins only, kind, user, source and period;
  - the failed-login aggregates;
  - **the audit policy's state**, with the commands for what is missing, and the Security log's span.
- **Diagnostics:** the assignments, with their last results per check, *Run now*, and the history.
- **Changes:** the readable change lines, newest first.
- **Agent:**
  - the connection (since when, from which address);
  - the version and architecture;
  - the key's fingerprint;
  - **the problems and crashes reported**, and the agent log;
  - *Re-join*, *Revoke*, *Send log*;
  - **the switches:** *Event on admin logins* (on), *Event on failed-login bursts* (on) with its threshold, *Event on new Critical entries* (off).

### 10.3 Admin → Diagnostics

- **Sets:** the list, and an editor. Checks are added as typed rows (ping: target, count, timeout, limits; DNS: name, type, server, expectation), with validation as you type.
- **Assignments:** set × server, the schedule, the last outcome, *Run now*, and *Assign…* to several servers at once.
- **Runs:** the most recent, filtered by set, server and outcome.

### 10.4 The service's window (on the server)

- **Not joined:**
  - the URL, the token (an input formatted `____-____`), a name (optional), and *Join*;
  - a step list with ticks: *checking PNMT*, *creating the key*, *registering*, *installing the service*, *connected*;
  - both fingerprints.
  - **On an unsupported Windows version**, a line says so, and joining goes ahead.
- **Joined:**
  - **the service:** running or stopped, with *Start*, *Stop* and *Restart*;
  - **the connection:** connected to the URL since …, the last heartbeat, **the last problems**;
  - **the agent:** the id, the version, the key's fingerprint, PNMTD's fingerprint;
  - **the diagnostics loaded:** the sets, their schedules, the last run, and the outcome;
  - **the audit policy's gaps**, with the commands to fix them;
  - **buttons:** *Collect now*, *Open log folder*, *Debug logging* (on or off), *Re-join…*, *Uninstall…*.
  - **W6 adds:** *Allow scripts from PNMT*, with the allowlists, and ***Stored accounts*** (add, replace or remove an account with its password, which stays on this server, §7.4).
- ***Uninstall*:**
  - stops and deletes the service;
  - sends `bye`, when PNMTD can be reached;
  - deletes the key, the stored credentials, and `%ProgramData%\PNMT Sensor` (the logs are kept, unless *Also delete the logs* is ticked);
  - deletes the program at the next reboot (`MoveFileEx`).
  - **PNMT keeps the sensor and its history**, marked *uninstalled*.
- ***Update*:** an older installed version, when the new executable is run, is offered *Update*: stop, replace, start. The identity, the key and the stored accounts stay.

---

## 11. Build, release and tooling (R14, R21)

- **A new project, `PNMTD.Sensor.Windows`,** in `PNMTD.slnx`, using `PNMTD.Lib` (the protocol). **`EnableWindowsTargeting=true` in its project file**, so `dotnet build` works on macOS and on GitHub's Linux runners.
- **Locally, on macOS:**
  - `dotnet build` builds everything, the agent included;
  - **`tools/agent.sh`** publishes both executables (`win-x64` and `win-arm64`, single-file, self-contained, compressed) into `artifacts/agent/`, with their SHA-256, for a manual test on a Windows machine;
  - **the full test suite runs locally:** `./run-tests.sh`, as today.
- **On GitHub, a normal push only compiles the agent:** the existing *Build* step of `dotnet.yml` builds `PNMTD.slnx`, which now includes it. **No Windows runner, no agent tests and no publishing on GitHub.**
- **On a sensor tag (`sensor-1.2.0`; version tags, `v*`, publish only the Docker image):**
  - `.github/workflows/sensor.yml`, on `ubuntu-latest`, publishes both executables;
  - it computes their SHA-256;
  - it **creates the tag's GitHub release** (or adds to it) with four assets: `pnmt-sensor-x64.exe`, `pnmt-sensor-x64.exe.sha256`, `pnmt-sensor-arm64.exe`, `pnmt-sensor-arm64.exe.sha256`;
  - it uses `gh release` with the workflow's token (`contents: write`).
  - **No tests run there.**
- **The agent's version is `Directory.Build.props`' version**, and the sensor tag must name it (the workflow fails otherwise; `sensor-1.2.0-rc1` is a pre-release of 1.2.0). PNMT's *Download* tab links to the release `sensor-{its own version}` (§10.1).
- **`pnmtd windows simulate --url … --token …`**:
  - a fake agent that joins and connects;
  - it sends sections and logins from fixtures (`PNMTD.Tests/Fixtures/Windows/`, captured on the W0 machines) and heartbeats;
  - it answers `collect` and `run-diagnostics` with canned results.
  - **For development without Windows.**
- **`pnmtd dev seed`** adds two Windows servers with sections, logins, changes, a diagnostics set and runs.

---

## 12. Phases

| Phase | Content | Size |
|---|---|---|
| **W0** | **Verify**, with no product code, and kept short:<ul><li>building the WinForms single-file executables on macOS (size, start time);</li><li>on **one Windows Server 2019** and **one Windows 10 Pro or Windows 11** machine: every collector as LocalSystem, the Security log and the operational logs (event ids of §4.7), `AuditQuerySystemPolicy`, `WinVerifyTrust` with catalogs, the firewall COM API, the Server Manager remote management setting;</li><li>the WebSocket through the reverse proxy in use;</li><li>what SmartScreen and Defender do with the unsigned executable.</li></ul>The results go into this document | small |
| **W1** | **The server side:** the protocol records, `WINDOWS`, `windows_agents`, join tokens (bound to a host) and the join endpoint, PNMTD's signing key, `AgentHub` on the existing listener with authentication, heartbeats → 250, 251, 451 and 460, problems into the agent log, `pnmtd windows simulate`, *Admin → Windows servers* (servers, *Add server*, tokens), the host page's *Add sensor → Windows server*, and the overview tab. **Testable without Windows** | medium-large |
| **W2** | **The agent, first version:**<ul><li>the project;</li><li>**the daily log file and the resilience rules** (§3.8);</li><li>the version check;</li><li>the window (join, status, uninstall) and the command line;</li><li>the service, the CNG key, the WebSocket client with reconnects, heartbeats, problems;</li><li>the sections `system`, `hardware` and `network`;</li><li>`tools/agent.sh`</li></ul> | large |
| **W3** | **The rest of R1, and R19:** services and their classes, remote access, **logins and connections** (the stream, the bookmarks, the filters, `windows_logins`, the audit policy check, 254 and 454), the event log; the differs, `windows_changes` and 253; the remaining tabs; the optional 453 | large |
| **W4** | **Diagnostics:** sets, checks and assignments (the admin page), the `DIAGNOSTICS` sensors, the agent's scheduler and executors, buffering, runs and their events, the *Diagnostics* tab, *Run now* | medium-large |
| **W5** | **Release and polish:**<ul><li>the tag job that publishes both executables to the GitHub release;</li><li>the *Download* tab;</li><li>updating by running the new executable;</li><li>the outdated badge;</li><li>`deploy/README.md` (the proxies' WebSocket lines, the agent);</li><li>the limits tuned with W0's numbers;</li><li>this plan's *As built* section</li></ul> | medium |
| **W6** | **PowerShell scripts** (R12): the library, parsing on the agent, the best-practice warnings, run-as accounts (the service itself, stored on the server, typed per run, gMSA), the local allowlist, signed and encrypted requests, the history | large |

- **Each phase:**
  - is one or more commits on master;
  - has its tests green locally (`./run-tests.sh`) before the next phase starts;
  - is noted in an *As built* section, as docs/mikrotik-backup-plan.md §7 was.

---

## 13. Testing: few, and only for PNMTD (R14)

**Only PNMTD's side gets automated tests,** in `PNMTD.Tests`, alongside the existing ones (decided in the review). The agent gets none.

| What | How |
|---|---|
| **Join** | The token's expiry, single use, its host, and the rate limit; re-join with a bound token |
| **Authentication** | A real WebSocket in the TestApp (`WebApplicationFactory`'s `CreateWebSocketClient`): a good signature, a wrong one (4401), a revoked agent (4403), no `hello` within 10 s (4408) |
| **Limits** | An oversized message, a gzip bomb |
| **Reports** | A heartbeat → 250, a new boot time → 251; logins stored once (the dedupe by record id); a burst of failures → 454 |
| **Endpoints** | `EndpointAuditTests`: the agent paths in the anonymous allowlist, and every API v1 endpoint in the matrix (the existing test, extended) |

- **The agent is checked by hand, per release:** join, the tabs filled, a login and a failed login seen, a diagnostics run, uninstall, update. On one Windows Server 2019 and one Windows 10 Pro or Windows 11 machine, with a checklist in this document (W5).
- **Not planned:** tests of the agent's code (collectors, parsers, log file), a Windows CI runner, e2e tests, a VM matrix, load tests.
- **The existing CI stays as it is** (decided in the review). It runs these tests with the others, and builds the agent on every push.

---

## 14. Risks

| Risk | Mitigation |
|---|---|
| **An unsigned executable** is blocked or warned about (SmartScreen, Defender, application control) | The SHA-256 in PNMT and on the release, *Run anyway* or `Unblock-File`, a Defender exclusion where policies require it. Code signing can come later without a change to the design |
| **WMI hangs** on a broken provider | Per-query and per-collector timeouts. A failed section doesn't stop the others (§3.8) |
| **The audit policy is off, or the Security log is small** | The *Access* tab says so, with the fix. The bookmarks read missed records while the log still holds them |
| **Admin network logons are frequent** (backup and monitoring software) | Aggregation per user, source and kind per 10 minutes. 254 is for console, RDP, SSH and WinRM logins only |
| **Proxies close or break WebSockets** | Pings every 30 s, nginx's lines in the README, W0 with the proxy in use |
| **Many servers** | One connection each is cheap for Kestrel. `LastSeenAt` is written in batches; heartbeat events collapse; sections are written only when they change |
| **Untested Windows versions** | The version check, with a warning, and the service still runs. Collectors report missing classes as *not available* |
| **Few automated tests, none for the agent** (R14) | PNMTD's security-relevant paths are tested. The checklist per release covers the agent. Problems from the field arrive in PNMT by themselves (§3.8) |
| **Scripts (W6) widen the attack surface** | Off by default, enabled only locally, allowlists, signatures, Admin only, audited. Passwords stored only on their own server, or encrypted end to end |

---

## 15. Decisions and open questions

### 15.1 Decisions

**From the plan:**

1. **One executable** with a window, a service mode and a command line; WinForms (§3.1).
2. **Trust through a CNG key and challenge signatures, and PNMTD's signed messages.** No client certificates (§3.4).
3. **The agent paths live under `/agent/v1/`,** outside `/api`, like the sensor API's paths (§9.1).
4. **Before W6, no commands exist in the protocol** (§6).
5. **Diagnostics are generic** (`PNMTD/Diagnostics/`), targeting any sensor whose agent announces the check types (§5.5).
6. **The agent's version is PNMTD's version** (§11).

**From the review of revision 1 (2026-11-13):**

| # | Question | Decision | Where |
|---|---|---|---|
| – | Ports | **No new port:** the WebSocket runs over PNMT's existing address and port | §3.5, §8.6 |
| – | Build and tests | **Built on macOS; testing kept to a minimum** | §11, §13 |
| – | Logs and failures | **A log file per day, 10 kept. Every failure logged and reported to PNMTD; the service carries on** | §3.8 |
| 1 | The token | **`xxxx-xxxx`** | §3.3 |
| 2 | Windows versions | **Server 2016–2025 (2019 required), Windows 11, Windows 10 Pro, Enterprise and LTSC. A version check; other versions run anyway** | §3.9 |
| 3 | Code signing | **None for now:** unsigned, with SHA-256 | §8.7, §14 |
| 4 | The service account | **LocalSystem**, with enough privilege to run scripts as admin | §3.2, §7.4 |
| 5 | The host | **An existing host**, chosen when the token is created | §3.3 |
| 6 | The event log | **Everything needed to monitor all logins and connections**: the Security log and the RDP, SSH, WinRM and SMB operational logs, plus System and Application for errors | §4.7, §4.8 |
| 7 | Which logins | **Every relevant admin login, of every kind; no ordinary users on file shares**; failures aggregated | §4.7 |
| 8 | Diagnostics | **A sensor of their own per assignment** | §5.2 |
| 9 | Run-as passwords | **Stored on the server when needed**, encrypted with DPAPI and entered there; or typed per run | §7.4 |
| 10 | Distribution | **GitHub release assets, built on tags only**; a normal push only builds; the full suite runs locally | §11 |
| 11 | Script parameters | **Parsed on an agent**, and the result sent to PNMT | §7.1 |
| 12 | ARM64 | **Yes:** `win-arm64` next to `win-x64` | §3.1 |

**From the review of revision 2 (2026-11-13):**

| Question | Decision | Where |
|---|---|---|
| The existing CI | **Unchanged.** It keeps running the whole suite on every push | §11, §13 |
| Tests | **Few, and only for PNMTD**, in `PNMTD.Tests`. No tests of the agent's code | §13 |

### 15.2 Open questions

None.

---

## 16. As built

The phases as they were built, with what differs from the plan above.

### W0: not done here

W0 needs real machines (one Windows Server 2019, one Windows 10 Pro or 11) and the reverse proxy in use: the executable's size and start, every collector as LocalSystem, the event ids of §4.7, `AuditQuerySystemPolicy`, `WinVerifyTrust` with catalogs, the firewall COM API, SmartScreen. **It is still to do**, with the checklist of §13, before the first release is relied on. Everything that could be checked on macOS was: the agent builds there (W2).

### W1: the server side

- **The protocol** is in `PNMTD.Lib/Agent/`: the records of §3.6 (`AgentMessages.cs`, `AgentDiagnostics.cs`), the frames with their limits (`AgentFrames.cs`), the signatures and join tokens (`AgentSigning.cs`), and **the agent's side of a connection, `AgentSession`**, with the HTTP join (`AgentJoinClient`). The Windows agent and `pnmtd windows simulate` share that code, and the tests run it against the in-memory server, so the agent's protocol code is tested after all, without testing the agent.
- **Signed messages** carry the signed JSON as text (`{ "p": text, "s": signature }`): the text is verified as it is, then parsed, so neither side needs canonical JSON. What is signed names its type, its agent, a request id and, for requests, an expiry; the agent checks the expiry against PNMTD's clock (the challenge's time gives the offset), so a server whose clock is off still accepts requests. The hello signs `pnmt-agent-v1`, the origin, the agent id, the nonce and its time.
- **The origin check:** the origin an agent signs must name PNMT's host: `PublicBaseUrl`'s, the request's `Host`, or `X-Forwarded-Host`. `Windows:CheckOrigin=false` turns it off for proxies that rewrite the host.
- **Plain HTTP is the agent's choice, confirmed by a person** (revised after W6; first it was a server setting for development only): the window shows PNMT's key as the URL answers and needs a tick that it matches the one in PNMT's *Add server* dialog, which warns about plain HTTP and shows the key; the command line needs `--allow-http` and prints the key, and PNMT's command line includes the flag for an http:// PNMT. The join must answer the same key as the check, or the agent refuses it. `agent.json` notes `plainHttp`, and the window shows it.
- **A re-join gives the agent a new id** as well as a new key, so each key has a name of its own on the server; the old id is refused (4401).
- **Every new close code:** 4400 for a protocol error, 4404 when the agents are turned off, 4413 for a message above the limits, besides 4401, 4403, 4408 and 4409.
- **`LastSeenAt` is written with each heartbeat**, in the transaction that writes its event, not in batches every 30 s: a heartbeat writes its event anyway.
- **Revoked and uninstalled servers get no 460** from `HeartbeatCheckTask`: both are on purpose.
- **`Windows:HelloTimeoutSeconds`** (10) exists for the tests, and `Windows:MessagesPerMinute` (60) and `Windows:ReportsPerMinute` (300) are the message rates of §8.7.
- **API:** besides §9.2, `POST …/servers/{id}/send-log` (Admin) answers the last 500 lines, and `GET /api/v1/windows/agent-log` (Admin) is the log of every agent.
- **Web UI:** *Admin → Windows servers* with *Servers*, *Join tokens* and *Agent log*; *Add Windows server…* in a host's menu (Admin) and on the admin page; a server's *Overview*, *Events* and *Agent* tabs. The sensor form shows no WINDOWS type: a hint points to the host's menu.
- **Tests** (`PNMTD.Tests/Windows/WindowsAgentTests.cs`): the join (single use, expiry, revoked and malformed tokens, the host), the re-join, the rate limit and the lockout, a good hello, 4401, 4403, 4408, 4413 for a large message and for a gzip bomb, heartbeats (250, the dedupe, 251, 451), and collect and send-log answered by the simulator; the endpoint audit has the three agent paths and the new v1 endpoints.

### W2: the agent, first version

- **`PNMTD.Sensor.Windows`** (`net10.0-windows`, WinForms, `EnableWindowsTargeting`) is in `PNMTD.slnx`: `dotnet build` builds it on macOS, and so does CI's build on Linux. **`tools/agent.sh`** publishes `pnmt-sensor-x64.exe` (**50 MB**) and `pnmt-sensor-arm64.exe` (**48 MB**) with their `.sha256` into `artifacts/agent/`, in about 10 s on a Mac: single-file, self-contained, compressed. The size is below §3.1's 60–80 MB.
- **Its parts:** `Logging/DailyFileLogger.cs` (one file per local day, 10 kept, 50 MB each with a note of the lines dropped, the window's own `pnmt-sensor-ui-…`); `Resilience/Problems.cs` (problems deduplicated per component and message per hour, 200 buffered, the oldest dropped and counted; `Isolated` for rule 1 and 2); `State/` (`agent.json`, `status.json` every 10 s, the folders' ACL for SYSTEM and Administrators, damaged files renamed `.broken`); `State/AgentKey.cs` (the CNG key); `WindowsVersion.cs` (§3.9); `Service/` (the service's loop, the collectors' scheduler, the heartbeat's values, `sc.exe` for the service and its recovery); `Join/` (joining, uninstalling); `Ui/MainForm.cs` (the window, built in code); `Program.cs` (the three modes; the command line attaches to its console).
- **The key's name** is `PNMT Sensor <random id>`, not the agent id: the key is created before the join answers the id. `agent.json` keeps the name.
- **The service's loop** reconnects for ever (1 s doubling to 5 min, ±20 %), stops after 4403 until `agent.json` shows a new join, restarts the connection when no heartbeat went out for three intervals, and exits above 300 MB (the recovery settings, with `failureflag`, start it again; the next start reports the reason). An unhandled exception is logged as fatal, leaves a marker, and the next hello says `restartedAfterCrash`.
- **Sections are hashed without their volatile values** (sessions, free space, signature ages, connection counts), so they are sent when something real changed, besides the full update every 6 hours and after each connect.
- **Collect now in the window** writes a flag file the service picks up within a second: no second interface into the service.
- **Not built:** nested domain groups in the local administrators (they are listed as groups); the last time sync (`w32tm`'s output is localised: only the source is read).
- **PNMTD:** `windows_sections` (migration `AddWindowsSections`), one row per server and section, stored gzip-compressed, replaced only when the hash changes (the comparison of §4.9 hooks in there, W3). `GET /api/v1/windows/servers/{id}/sections/{section}` answers the typed section (`access` and `event-log` need CanWrite). The simulator sends canned `system`, `hardware` and `network` sections. The server page gains *Hardware* and *Network*, and the *Overview* shows the `system` section.
- **Tests:** a section is stored once per hash and served typed; the viewer gets 403 for `access`.

### W3: the rest of R1, and R19

- **The agent:** `ServicesCollector` (classes by `WinVerifyTrust`, embedded and by catalog, without network; the result cached per path, size and time, so the minute's collection checks only new binaries), `RemoteAccessCollector` (the 13 features of §4.6, Telnet, WMSVC, FTP, SNMP, and 23 third-party remote tools by their services), `AccessCollector` (the six subcategories by GUID, the operational logs, the Security log's span, the last 24 hours), `EventLogCollector`, and `Access/LoginStream` (`Native/WinTrust.cs` has the signature and audit policy APIs).
- **The login stream reads each log from its bookmark every 15 seconds** with `EventLogReader`, rather than an `EventLogWatcher`: a login reaches PNMT within 15 s, the server is spared a poll every second, and a missed event is impossible because the bookmark moves only after a record is handled. The bookmarks (`state/bookmarks.json`) are `EventBookmark.BookmarkXml`. The first time, it reads the last hour, not the whole log. At most 20,000 records per log and poll; a larger backlog is read on at the next one. The event log's new entries are noticed in the same poll.
- **What it records** follows §4.7, with these choices: a logon is an admin's when a 4672 of the same logon id came with it (or within 10 minutes before), or when the user is a direct member of the local Administrators; nested and domain groups count through 4672 alone. The RDP sessions come from 4624 type 10 and the 1149 connections; LocalSessionManager is only checked as a log (§4.7's table), not parsed, because its 21–25 repeat 4624. A 4648 is kept when its subject or its target is an admin.
- **Aggregation on the agent:** identical successes and failures (outcome, user, source, kind) within the window are one record whose count and last time grow; it is sent again with the same log and record id, and PNMTD updates it. A logoff updates its logon the same way.
- **The codes for the audit policy are GUIDs**, quoted (`auditpol /set /subcategory:"{0CCE9215-…}" /failure:enable`): the subcategories' names are localised, and unquoted braces are a script block in PowerShell (found in the first test on Windows).
- **PNMTD:** `windows_logins` and `windows_changes` (migration `AddWindowsAccess`), and two columns on `windows_agents` for the list (`RemoteAccessSummary`, `AuditGaps`). `WindowsAccessService` stores logins (unique per server, log and record id), writes 254 for a new admin login by console, RDP, SSH or WinRM (within the last hour, so a backlog after an outage doesn't page), and 454 for a burst from one source, for a user from several sources, and for a lockout; the 454 messages are stable while a burst lasts, so they collapse. `WindowsChanges` has a differ per section; 453 for a new Critical entry when the server's switch is on. `GET …/logins` (CanWrite) and `GET …/changes` (CanRead).
- **The enums are `LogonOutcome` and `LogonKind`**, not `LoginOutcome`: PNMT's sign-in already has a `LoginOutcome`.
- **Web UI:** a server's *Services*, *Remote access*, *Access* and *Event log* (both User), and *Changes* tabs; the overview shows remote access, the last admin logins and failed logins (User), and the last changes.
- **Tests:** logins stored once (a resend updates, a network logon writes no 254), 254's message, a burst and a lockout writing 454, a service added and RDP turned off writing changes and 253, a new Critical entry writing 453, and the differs' lines for addresses, DNS, the build, a new administrator and the audit policy.

### W4: diagnostics

- **PNMTD:** `diagnostic_sets`, `diagnostic_checks`, `diagnostic_assignments` and `diagnostic_runs` (migration `AddDiagnostics`), in `PNMTD/Diagnostics/` (the agent's configuration, the rules of §5.1, the results) and `Services/Domain/DiagnosticsSetService.cs`. An assignment creates its DIAGNOSTICS sensor "Diagnostics: {set}" on the server's host, with the schedule's interval and a grace of 2 minutes (manual ones have no interval, so no 460); deleting it deletes the sensor and its history; a set refuses to go while assigned; deleting a server deletes its assignments' sensors. Every change raises the installation's configuration version and pushes `config` to the agents concerned. The rules are checked again on the agent's side by the type of each check (§6).
- **§5.5 in practice:** an assignment is refused for an agent that announced capabilities without the set's check types (an agent that never connected is accepted).
- **Run now** creates the run's row first (manual, by whom), asks the agent (signed `run-diagnostics`), which answers at once and sends the result as soon as it has it; the row is completed then. A refusal completes it as an error.
- **The agent:** `Diagnostics/DiagnosticsFeature.cs`: its own timer per scheduled assignment (with up to 15 s of spread), one run at a time, the checks in order, a thrown check an error; ping with `System.Net.NetworkInformation.Ping`, DNS through the system's resolver for A and AAAA without a server (hosts file and suffixes included) and through DnsClient otherwise; results buffered (1,000) and kept across restarts in `state/`.
- **Events:** 252 "All 2 checks passed", 452 "1 of 2 failed: ping gw-zurich (100 % loss)". A run's outcome is Fail when a check failed, Error when one only errored.
- **Retention:** runs older than 30 days go, except the last 100 of each assignment.
- **Web UI:** *Admin → Diagnostics* (*Sets* with an editor of typed rows, *Assignments* with *Assign…*, the interval, *Pause*, *Run now* and delete, *Runs* with a drawer per run); a server's *Diagnostics* tab (its assignments and history) and the states on its overview; the fleet's *diagnostics failing* filter and badge.
- **Tests** (`PNMTD.Tests/Diagnostics/DiagnosticsTests.cs`): the caps, an assignment with its sensor and the agent's new configuration, Run now through the simulator with 252 and 452, the fleet's count, and deleting.

### W5: release and polish

- **Building by hand:** `./build-sensor-windows.sh` (`x64` or `arm64` for one) builds both executables with their `.sha256` into `dist/`, which ignores itself; it calls `tools/agent.sh`. The single-file settings are in the project file, behind `-p:AgentRuntime=win-x64`: given as `-r` and `-p` on the command line they reached `PNMTD.Lib`'s restore too and broke the locked restore, so `tools/agent.sh` failed from W2 until this fix (unnoticed: its output went nowhere). The single-file analyzer is off, because it pulls in a package the normal build doesn't restore. The builds are locked and reproducible: the same commit gives the same SHA-256.
- **The release:** `.github/workflows/sensor.yml` runs on sensor tags (`sensor-1.2.0`; first a job in `push.yml` on version tags, moved to a tag of its own so the agent can be released without a new image), on `ubuntu-latest`: it checks that the tag names `Directory.Build.props`' version, runs `tools/agent.sh`, then `gh release create` (or nothing, when the release exists) and `gh release upload --clobber` of both executables and their `.sha256`, with the workflow's token (`contents: write`). A tag with a dash is a pre-release. No tests run there; `dotnet.yml` is unchanged and builds the agent with the solution on every push. The *Download* tab links to `sensor-{version}`.
- **The Download tab** (*Admin → Windows servers*): PNMTD's version, both links, the SHA-256 read from the release's `.sha256` assets (cached 6 hours, 10 minutes after a failure; *Add server* never waits for GitHub), the `Get-FileHash` line, the unattended command line, and how to update.
- **Updating:** the window offers *Update* when the installed agent is older than the executable that runs; `pnmt-sensor.exe update` does the same on the command line. Stop, replace, start: `agent.json`, the key and the buffers stay.
- **The outdated badge** shows in the fleet, the status strip and the Agent tab when the agent is older than PNMTD.
- **`deploy/README.md`:** the nginx example passes the upgrade (`map $http_upgrade`, `proxy_http_version 1.1`, `proxy_read_timeout 120s`), and a *Windows servers* section; **`deploy/.env.example`:** the `Windows__*` settings.
- **`pnmtd dev seed`** adds two servers (`backup-srv`, `app-04`) with every section, a week of logins, changes, the agent log, and *branch connectivity* assigned to both with runs (app-04's Basel gateway failed for half an hour and recovered, so the demo's failing hosts stay three). The seed's servers have no agent: their heartbeat interval is a day.
- **A browser test after all** (`e2e/windows.spec.ts`, two tests): the admin page and *Add server*, every tab of a seeded server, and *Admin → Diagnostics* with a run. Not the agent: only the pages, which nothing else could open (§13 asked for no e2e tests; these are the cheapest way to see the pages render).
- **Not done: the limits tuned with W0's numbers** (W0 is still to do).

### After W6: the window

- **Redone** (`Ui/`): a header with the PNMT icon, the server, the agent's version and its state as a coloured pill; white cards on a light page as in the web UI; brand-coloured buttons; Segoe UI; sizes scaled to the screen's DPI. Joined, three tabs: *Status* (the connection and the service with Start, Stop and Restart, Update when the running executable is newer, problems, this server in PNMT with both keys to copy, diagnostics, the audit policy's gaps with *Copy* per command, the actions), *Scripts* (whether PNMT may run scripts, the scripts registered here and the stored accounts; see *Local scripts* below) and *Log* (the service's log file, following).
- **Resizing:** fields, keys and text stretch with the window and wrap; buttons and the token field keep their size; the join's log and the *Log* tab fill the height that is left. The window keeps a minimum size.
- **Nothing is rebuilt while you type:** the views are built once and a timer every 2 s updates what changed (the first window rebuilt every control every 2 s, which also cleared half-typed allowlists). Slow service actions run off the UI thread; dialogs are task dialogs.
- **The icon** (`Ui/pnmt-icon.svg`): the web UI's mark, white on the brand blue, rendered to `pnmt.ico` (16–256 px) and `logo.png`; the executable, the window and the taskbar use it.
- **Debug logging** switched in the window takes effect in the service within 10 s, without a restart.
- Not seen yet: the window was built and compiled on macOS; how it looks on Windows is part of the checklist below.

### After W6: local scripts

- **The *Scripts* tab** (`Ui/ScriptsTab.cs`): the policy on top (*Allow PNMT to run scripts on this server*; *only the scripts registered below* or *any script PNMT sends, as these accounts*), below it a list of the registered scripts (name, account, schedule, state) and the selected one's details beside it: the file and its SHA-256 (copyable), whether the copy still matches it, the account, the schedule with the task's state, last run, result and next run, who added it when, and *View*, *Edit…*, *Replace…*, *Run the task now* and *Remove*. The list and the details share the width and resize with the window.
- **Add script…** opens the open-file dialog (`.ps1`). A name that is registered asks *Replace it*, *Add under another name…* or *Cancel*; the same text registered under another name says so. Then the script's dialog (`Ui/ScriptDialog.cs`): the name, the account (*this service (LocalSystem)*, *an account, its password stored here*, or *a gMSA*), the password when it isn't stored yet (checked with `LogonUser` before anything is saved), and an optional schedule (daily or weekly at a time, every N minutes, at startup).
- **Saved** (`Scripts/LocalScripts.cs`): a copy in `%ProgramData%\PNMT Sensor\scripts\{name}.ps1` that only SYSTEM, Administrators and the script's account can read; the entry in `state\local-scripts.json` (name, file, SHA-256, source, account, schedule, added by and when); the password as a stored account; with a schedule, a task `\PNMT Sensor\{name}` with that principal (highest privileges, at most 2 h, no second instance). An edit of the copy afterwards shows red in the list: PNMT refuses the changed text, the task runs it.
- **The hash** (`ScriptHashes.Of`) is the SHA-256 of the text without a BOM and with LF line endings, in PNMT and on the agent, so a file saved by a Windows editor matches the same script pasted into PNMT.
- **Which account:** a run of a registered script uses the registered account (its stored password, or the gMSA), whatever PNMT names; PNMT naming another account is refused. A password typed per run still wins.
- **The example** (*Example script*): `Documents\PNMT Sensor\Get-DiskReport.ps1`, written once and opened in the editor Windows registered for editing `.ps1` (Notepad otherwise, never *Open*, which would run it): a read-only disk report with help, `[CmdletBinding()]`, typed and validated parameters, strict mode and an exit code.
- **Stored accounts…** lists the stored accounts; removing one warns about the registered scripts that use it.
- **Uninstall** removes the tasks in `\PNMT Sensor\` too; the copies go with the data folder.
- **PowerShell's console** is the OEM code page in Windows PowerShell 5.1: the parser's output and the task runner's input travel Base64-encoded UTF-8, so names and passwords with umlauts arrive intact.

### After the first test on a real machine

- **Not available is no error:** a WMI class or namespace this Windows doesn't have (SMART's `MSStorageDriver_FailurePredictStatus` in a VM, a missing role) leaves its part empty without an error in the section; the rest is collected as always. SMART is then *unknown* per disk, not *fine*.
- **Activation:** `OsInfo.Activation` (`ActivationInfo`): the licensing status (Licensed, the grace periods, Notification), product and channel (Retail, OEM, Volume:MAK, Volume:GVLK), the installed key's last five characters, the grace time left, an evaluation's end, the KMS host, the firmware's OEM key's last five (the key itself never leaves the server) and the licensing error while not activated. The overview shows it under *Activation*.
- **Services:** each collector has its own time limit (`Collector.Timeout`, 30 s; *services* 4 minutes, as the first pass checks every binary's signature, mostly catalog lookups; *system* 2 minutes for the licensing service). A pass that runs out keeps the signatures it checked. One service that fails (a registry key, an odd path) is reported in the section's errors with what is known, instead of losing the section. The tab shows the counts per filter and says why it is empty.
- **The auditpol commands** quote the GUID (`/subcategory:"{…}"`): unquoted, PowerShell reads the braces as a script block and auditpol answers 0x57.
- **Web UI:** who is logged on (the overview and *Access*) as rows with the domain muted, how, since when, from where and the state; the last 24 hours as four figures. Tags next to each other or to text keep their distance (Vue drops the whitespace between elements on separate lines).

### The checklist per release (§13)

On one Windows Server 2019 and one Windows 10 Pro or Windows 11 machine, with the release's executable:

1. `Get-FileHash` matches the release's `.sha256`; SmartScreen's *Run anyway* works.
2. Join from the window with a token from *Add Windows server…*: both fingerprints match the agent log; the server shows *connected*. The window looks right at 100 % and 150 % scaling, and resizes (fields stretch, the log fills). Over plain http://, the confirmation shows PNMT's key.
3. Within a minute: 250; the overview, *Hardware*, *Network*, *Services*, *Remote access*, *Access*, *Event log* filled, without errors in the sections.
4. A login over RDP as an admin: in *Access* within 15 seconds, 254. A wrong password three times: an aggregate with its count; ten from one address: 454.
5. Stop a service set to automatic: *Services* flags it; add a local administrator: *Changes* has the line, 253.
6. Assign a diagnostics set: *Run now* shows its result within seconds; a check against an address that doesn't answer: 452.
7. Unplug the network for 5 minutes: the agent's log shows the reconnects; the logins of that time arrive after.
8. Kill `pnmt-sensor.exe` in the task manager: Windows starts it again within a minute; the agent log has *crashed*.
9. Run the next version's executable: *Update*; the server stays the same sensor.
10. *Scripts → Add script…* with the example script, as this service, daily: the copy and the task `\PNMT Sensor\` exist; *Run the task now* shows a result; *Add script…* with the same file name asks to replace. With *Allow PNMT to run scripts*, the same text pasted into PNMT runs from there; one changed character is refused.
11. *Uninstall…*: the service, the key and the scripts' tasks are gone; PNMT shows *uninstalled* and no 460.

### W6: PowerShell scripts

- **The library:** `windows_scripts` and `windows_script_versions` (a version per change of the text, with its SHA-256), and `windows_run_as` and `windows_script_runs` (migration `AddWindowsScripts`). *Admin → Windows servers → Scripts* edits them (a new script starts from the template of §7.1) and *Run-as accounts* names the accounts; a server's *Scripts* tab (Admin) runs on it and shows its history. Every endpoint is Admin and audited (`WindowsScriptChanged`, `WindowsScriptRun`).
- **Parsing on an agent:** saving a version sends a signed `parse-script` to a connected agent that announced `parse.v1`; with none, the version waits as *parameters pending* and the next agent that connects parses it. The agent runs a fixed parser script in Windows PowerShell 5.1 (`[Parser]::ParseFile`) over the script in a file: the parameters with their types, mandatory, defaults, positions, `ValidateSet`, `ValidateRange` and `HelpMessage`, the comment-based help, the syntax errors and the best-practice findings. Nothing in the script runs.
- **The arguments are checked twice with the same code** (`ScriptArguments` in `PNMTD.Lib`): by PNMTD before it sends a run, and by the agent against its own fresh parse before it runs.
- **Allowed only locally:** the window's *Scripts from PNMT* (a local administrator) writes `state\scripts-policy.json`: on or off, optionally allowed hashes and allowed accounts; the agent itself as `service` must always be named. (After W6 the allowed hashes became the registered scripts: see *Local scripts*.) Only then does the agent announce `scripts.v1`; saving restarts the service, so the hello says so.
- **Run-as:** the service runs the wrapper itself (LocalSystem). Every other account runs **through a scheduled task** with that principal (a password for stored and typed accounts, none for a gMSA), registered, started, awaited and removed by a small runner, rather than `LogonUser` and `CreateProcessAsUser`: the same rights (log on as a batch job), no token handling of our own, and gMSAs work the same way. The run folder (`state\runs\{id}`, SYSTEM and Administrators, opened to the account for the run) is deleted afterwards.
- **Secrets end to end:** the agent has an ECDH P-256 key in the machine store, non-exportable; its public half is in the hello and covered by the hello's signature. A password typed per run and every `[securestring]` argument are encrypted to it (ephemeral ECDH, HKDF-SHA256 over the run's id, AES-256-GCM), never stored in PNMT, and handed to the wrapper in a file it deletes before the script starts. Stored accounts' passwords are DPAPI (machine scope with an extra random secret) in `state\credentials.json`.
- **The wrapper splats** the arguments from `args.json`; nothing is pasted into a command line. stdout and the error stream go to the history, at most 1 MiB, with the exit code and the duration; the run is logged in the agent's log file and in the Windows event log, and in PNMT's agent log.
- **The history** keeps the plain arguments (an Admin can redact them), never the secure ones or a password; it outlives its script and its server.
- **Tests** (`PNMTD.Tests/Windows/ScriptTests.cs`): the secret opens only with the agent's key and its run; a version parsed by the simulator; arguments refused (mandatory, unknown, outside the set, a missing per-run password); a refusal where scripts aren't allowed; a run as an account whose password is typed per run, with a secure argument, both arriving decrypted at the simulator and neither stored; redacting.

---

## Appendix: configuration and file changes

| Area | New | Changed |
|---|---|---|
| Protocol | `PNMTD.Lib/Agent/` (messages, sections, signatures) | – |
| Agent logic | Inside `PNMTD.Sensor.Windows/`: the login parsers and filters, the service and remote access rules | – |
| Agent | `PNMTD.Sensor.Windows/` (`Program.cs`, `Ui/`, `Service/`, `Collectors/`, `Access/`, `Diagnostics/`, `Logging/`, `app.manifest`) | `PNMTD.slnx`, `Directory.Packages.props` (`System.Management`, `System.Diagnostics.EventLog`, `System.ServiceProcess.ServiceController`, `Microsoft.Extensions.Hosting.WindowsServices`) |
| Server | `PNMTD/Agents/` (`AgentPaths`, `AgentJoinEndpoint`, `AgentHub`, `AgentKeyStore`), `PNMTD/Windows/` (`WindowsReportService`, `WindowsAccessService`, the differs, `WindowsMaintenanceTask`, `WindowsOptions`), `PNMTD/Diagnostics/`, `Services/Domain/WindowsServerService.cs`, `DiagnosticsSetService.cs`, `Api/V1/WindowsControllers.cs`, `DiagnosticsControllers.cs`, `Models/Db/WindowsEntities.cs`, `DiagnosticsEntities.cs`, `Cli/WindowsCommand.cs` | `Program.cs` (`UseWebSockets` for the agent path, `AddWindowsAgents`, the `AgentPaths` exclusion), `SensorType` (100, 101), `PNMTStatusCodes`, `SystemService`, `SensorService`, `HeartbeatCheckTask`, `AuthEvents`, `DevSeedCommand` |
| Web UI | `views/admin/windows/`, `views/admin/diagnostics/`, `views/sensors/windows/` | `router/index.ts`, `layouts/navigation.ts`, `SensorView.vue`, `SensorFormView.vue`, the host page (*Add sensor → Windows server*) |
| Build and release | `tools/agent.sh`, `.github/workflows/sensor.yml` (the release on sensor tags) | `deploy/README.md` (the proxies' WebSocket lines, the agent), `deploy/.env.example` (`Windows__*`). **The `Dockerfile` is unchanged** |
| Tests | `PNMTD.Tests/Windows/`, `PNMTD.Tests/Diagnostics/`, `PNMTD.Tests/Fixtures/Windows/` | `EndpointAuditTests` |
