# Plan: Linux servers, the PNMT agent for Debian (package three)

| | |
|---|---|
| Status | **Plan, revision 2: the review of revision 1 applied** (§16.1). No question is open |
| Date | 2026-11-14 |
| Revision | 2: **the review of revision 1 applied** (§16.1):<ul><li>**PNMTD's agent infrastructure is generalised now**, before 1.3.0;</li><li>**built for Debian 12 and 13**, and **working on Proxmox VE and Ubuntu LTS** too;</li><li>**the binary is downloaded from PNMT**, with the GitHub link next to it;</li><li>**an SSH key manager**: every key named and assigned to a user, on an admin page of its own;</li><li>removals wait for offline servers; the agent's journal parsers get tests; sudo and cron lines only on request; 462 is in.</li></ul>1: the first plan |
| Scope | Package three of docs/ideas2.md:<ul><li>**A Linux server runs the PNMT agent for Linux**, `pnmt-agent`: one binary with .NET included, a systemd service, and a command line. **No GUI.** It is built for **Debian 12 and 13**, and works on **Proxmox VE and Ubuntu LTS** too.</li><li>**It is installed with one line** from PNMT: download the binary from PNMT, check its SHA-256, and join with a token from PNMT's web UI.</li><li>**It reports what the Windows agent reports, in Linux terms:** system, hardware, network, services, remote access, logins, the journal and diagnostics. The protocol, trust, cadence, logging and resilience are the same.</li><li>**It reads and monitors every `authorized_keys` file**, and removes keys from them where the server allows it.</li><li>**An SSH key manager in PNMT** names every key and assigns it to a user.</li><li>**What the agent may change is decided on the server itself**, in `/etc/pnmt-agent/config.yaml`: removing keys and running bash scripts, both off by default.</li><li>**As much as possible is shared with package two:**<ul><li>a shared agent core used by both agents;</li><li>PNMTD's agent infrastructure, generalised from *Windows servers* to *servers*.</li></ul></li></ul> |
| Non-goals | **Adding or deploying SSH keys, users or sudo rules from PNMT.** PNMT can take access away, never grant it (§2.2). The key manager is an inventory and a removal tool, never a deployment tool. **Users registering their own keys** (Admins register them; §6.4). Distributions without systemd or with musl (Alpine), RHEL and its relatives, 32-bit. A GUI. A `.deb` package or an apt repository. Updating the agent from PNMT: an update is started on the server. Installing packages or running `apt upgrade` (a bash script can, where allowed). Ending SSH sessions. Keys behind `AuthorizedKeysCommand` (LDAP, SSSD) beyond noting that they exist. File integrity monitoring beyond key files. Docker containers' inventory, and Proxmox's VMs. auditd. Performance history. Code signing. Changes to the sensor API |
| References | **docs/windows-sensor-plan.md**, package two, on which this plan builds. It is cited as **W§**: *W§3.4* is its §3.4. docs/webui-migration-plan.md (architecture, roles). docs/mikrotik-plan.md (a managed device, the CHR lab). docs/ai-operations-plan.md (importance, the Notification Manager). The man pages of `sshd`, `sshd_config` (`AuthorizedKeysFile`, `-T`), `authorized_keys` format, `journalctl`, `systemd.exec`, `systemd-run`, `os-release`. Microsoft's documentation of .NET on Linux (dependencies, single-file, globalization-invariant mode). Proxmox VE's documentation of the cluster file system (`/etc/pve`) |

---

## 1. Summary

The **PNMT agent for Linux** is one executable, `pnmt-agent`, built from this repository and published with each release on GitHub, next to the Windows executables.

1. **An admin copies one line from PNMT** and runs it as root on the server.
2. **The line downloads the binary from PNMT itself**, which keeps a copy of its release's files, and checks its SHA-256. GitHub's link is shown next to it.
3. **`pnmt-agent join` creates the agent's keys**, registers with PNMTD, and installs a systemd unit.
4. **The agent connects over the same address and port as the web UI**, exactly as the Windows agent does.

From then on:

- **PNMT shows everything about the server.** That includes every `authorized_keys` file and the keys in it, and **which key each SSH login used**.
- **PNMT records every relevant login and elevation** (sudo, su).
- **The agent runs the diagnostics** assigned to the server.
- **Where root allowed it in `config.yaml`, PNMT can remove a key or run a bash script.**

**PNMT's SSH key manager** gives every key a name and an owner, usually a PNMT user. So PNMT shows **whose key let whom in**, and **where a departing colleague's keys are still authorized**, with one step to remove them everywhere.

**Half of the agent is the Windows agent's code**, moved into a shared core. **PNMTD treats both as servers with an agent.**

| Topic | Decision |
|---|---|
| The binary | **`pnmt-agent-linux-x64` and `pnmt-agent-linux-arm64`**: self-contained single-file .NET 10, with globalization-invariant mode, so libicu isn't needed. **A command line only** (`join`, `status`, `keys`, `config check`, `log`, `update`, `uninstall`, …) and the service mode (§4.1) |
| Distributions | **Built and tested for Debian 12 (bookworm) and 13 (trixie)**, amd64 and arm64, with every release. **Supported too: Proxmox VE 8 and 9, and Ubuntu LTS 22.04, 24.04 and 26.04**, checked in L0 and whenever a release touches what differs there. **Anything else is flagged and runs anyway**, as on Windows (§4.8) |
| Installing | **One line** from *Add server → Linux*: download the binary from PNMT (`/agent/v1/download/…`), `sha256sum -c`, install to `/usr/local/sbin`, and `pnmt-agent join --url … --token …`. GitHub's link, with the same hash, is next to it. **No `curl \| bash`**: nothing runs before its hash is checked (§4.4) |
| The binary from PNMT | **PNMTD serves its own version's agent files.** It fetches them from the GitHub release when first asked for, checks each against its `.sha256`, and keeps them. A folder an admin fills works without internet. **The Docker image doesn't grow.** `pnmt-agent update` fetches from PNMT and checks a manifest that PNMTD signed with the key pinned at the join (§4.4) |
| The service | **`pnmt-agent.service`, as root**, `Restart=always`, with systemd's watchdog and the sandboxing that blocks no feature. Scripts run in transient units of their own (§4.3, §8.4) |
| Trust | **As on Windows:** an ECDSA P-256 key per agent, a challenge signed per connection, PNMTD's key pinned at the join, and every request signed. **The keys are files readable only by root**, because Linux has no machine key store (§4.5) |
| Rights on the server | **`/etc/pnmt-agent/config.yaml`**, written by root, never by PNMT:<ul><li>removing keys (with a per-user allowlist and protected keys);</li><li>running bash scripts (with allowlists of accounts and scripts);</li><li>what is collected beyond the defaults.</li></ul>**Everything that changes something is off by default. A broken or unsafe file grants nothing** (§4.6) |
| What it reports | **System, hardware, network, services (with timers and cron), remote access, logins and elevations, the journal, SSH keys, and diagnostics results.** Hardware and network reuse the Windows records (§5) |
| SSH keys on the servers | **Every `authorized_keys` file that sshd uses, for every account**, through sshd's own effective configuration, with each key's fingerprint, options and notes. **Watched with inotify**, so a new key reaches PNMT within seconds. **The last use of each key** comes from the SSH logins (§6.1–§6.3) |
| The SSH key manager | ***Admin → SSH keys*:** every key registered with **a name and an owner (a PNMT user, or a service or person outside PNMT)**, active or retired.<ul><li>Keys found on servers but not registered are the work list.</li><li>**465 for a key that isn't registered as active.**</li><li>Retiring a key, or deactivating a user, shows where their keys are still authorized, with one step to remove them all.</li><li>Every user sees their own keys on their account page (§6.4, §11.4)</li></ul> |
| Removing keys | **Where `config.yaml` allows it:** rewritten with the file owner's rights, with a backup, protected keys never. **Offline servers get the removal when they connect** (up to 7 days). Restoring is only possible locally (§6.5) |
| Logins | **Every SSH and console login, and every sudo and su**, from the journal, with the key (and its registered name) for key logins. File transfers and failures are aggregated. **Internet scanners' "invalid user" noise never pages** (§5.6) |
| Bash scripts | **The same library, run dialog and history as PowerShell scripts.** Parameters are declared in a YAML header that PNMTD parses itself. The arguments go in an environment file, never on a command line. **Run with `systemd-run`, as root or a named account, only where `config.yaml` allows it** (§8) |
| Shared code: the agent | **A new `PNMTD.Sensor.Core`** (net10.0) with the connection loop, scheduler, problems, log file, state, diagnostics, and the joining and script skeletons. **Moved out of `PNMTD.Sensor.Windows` without changing it** (§3.2) |
| Shared code: PNMTD | **The `windows_*` tables, services, API and admin page become `agent_*` / *Servers*, with a platform, before 1.3.0.** Windows servers aren't released yet, so this costs no compatibility. Hub, join, sections, logins, changes, agent log, diagnostics, scripts, downloads and retention then serve both (§3.3) |
| PNMT model | **A Linux server is a sensor of type `LINUX` (102)** in the shared `agents` table. Events 260, 261, 263, 264 and 461–465. Diagnostics (101, 252/452) as they are (§9) |
| Distribution | **GitHub release assets, built only on version tags**, next to the Windows executables, with their SHA-256, **and served by PNMTD** (§4.4, §12) |
| Tests | **Few, and only for PNMTD**, as for Windows (W§13). Two additions: **table-driven tests of the agent's journal-line parsers**, and a fake Linux agent in `pnmtd agent simulate`. **A Debian lab in Podman** (`tools/linux-lab.sh`) for checking by hand (§12, §14) |

---

## 2. Requirements, starting point and alternatives

### 2.1 Requirements

| # | Requirement (docs/ideas2.md, package three) | Where |
|---|---|---|
| R1 | **Basically the same as the Windows server sensor, for Debian Linux** | §5, §7 |
| R2 | **Bash scripts** can be defined instead of PowerShell scripts | §8 |
| R3 | **No GUI, only a CLI** | §4.1 |
| R4 | **Installed with a single line**: every argument, and the binary | §4.4 |
| R5 | **Runs with the usual systemd service** | §4.3 |
| R6 | **Shares as much code as possible with the Windows sensor**, in a project of its own | §3 |
| R7 | **Checks the `authorized_keys` files and monitors them** | §6.1–§6.3 |
| R8 | **May remove keys from these files** | §6.5 |
| R9 | **Whether it may is configured in the agent itself:** `/etc/pnmt-agent/config.yaml` or similar | §4.6 |
| R10 | **Other rights are configured there too** | §4.6, §8.5 |
| R11 *(review)* | **Built for Debian 12 and 13; works on Proxmox VE and Ubuntu LTS too** | §4.8 |
| R12 *(review)* | **The binary is downloaded from PNMTD**, and the GitHub link is offered too | §4.4 |
| R13 *(review)* | **An SSH key manager:** keys get a name and are assigned to users, on an admin page | §6.4, §11.4 |
| R-W | **Package two's requirements and review decisions hold** unless this plan says otherwise:<ul><li>one binary with .NET bundled (W R4, R9);</li><li>joining with a token `xxxx-xxxx` on an existing host (W R5, R7, R18);</li><li>trust through key pairs (W R6);</li><li>an open WebSocket on PNMT's existing port, a heartbeat every minute, changes when they happen, everything every 6 hours (W R10, R13);</li><li>read-only by default (W R11);</li><li>a log file per day, 10 kept; every failure logged, reported, and carried past (W R15);</li><li>a version check that runs anyway (W R16);</li><li>every relevant admin login monitored (W R19);</li><li>GitHub release assets built on tags (W R21);</li><li>ARM64 (W R23);</li><li>few tests, only for PNMTD (W§13)</li></ul> | throughout |

### 2.2 Design goals

- **PNMT can take access away, never grant it.**
  - The protocol has no message that adds a key, an account, a group membership or a sudo rule.
  - The key manager registers keys in PNMT, and never deploys them anywhere.
  - Restoring a removed key is a local command (§6.5).
  - The only way PNMT can change a server beyond removing keys is a script, and only where root turned scripts on and named the accounts (§8.5).
- **A compromised PNMT must not mean compromised servers** (W§2.2):
  - rights are granted on the server, by root, in a file PNMT can't write;
  - **a broken, unreadable or unsafe `config.yaml` grants nothing** (§4.6);
  - break-glass keys, and Proxmox's own cluster keys, can't be removed.
- **A root agent touches users' files with no more right than their owner.** Key files in a user's home are read and rewritten with that user's uid. A user's symlinks are never followed, and unparseable lines are never reported, so a symlink can't turn the agent into a reader or writer of `/etc/shadow` (§6.6).
- **The Windows agent's rules, unchanged:** the protocol, cadence, logging and the eight resilience rules (W§3.8). Differences exist only where Linux differs, and are listed as such.
- **Nothing to install first.**
  - The agent needs what Debian's and Ubuntu's base systems have: systemd, journald, iproute2, OpenSSH, dpkg and apt.
  - Optional tools (`smartctl`, `dmidecode`, `nft`, `shellcheck`) add detail when present. Without them, the part is *not available*.
  - **A server needs to reach only PNMT**, not GitHub.
- **One fix for both agents:** shared code is shared in one place, not copied (§3).

### 2.3 Alternatives

| Option | Why not |
|---|---|
| **Agentless over SSH from PNMTD**, as for MikroTik's Direct SSH | PNMT would hold a root-capable SSH key to every server, so a compromised PNMT would mean root everywhere. It also needs inbound SSH, servers behind NAT can't be reached, and there are no logins within seconds. The request is the Windows agent's model |
| Existing agents (osquery, Wazuh, Zabbix agent 2, node_exporter) | Their own servers and formats. osquery has an `authorized_keys` table, but nothing removes keys under local rights, names keys per person, or joins PNMT |
| **Parallel tables and services for Linux** (`linux_agents`, …) next to the `windows_*` ones | The hub, join, sections, logins, changes, agent log, retention and the simulator would be duplicated. Diagnostics' targets would become two (they read `windows_agents`). Every later feature would be built twice. Decided in the review: generalise (§3.3) |
| An agent in Go, Python or Bash | Nothing shared with the C# Windows agent and PNMTD.Lib, which is the whole point of R6. Python would also need an interpreter on the server |
| `curl … \| sudo bash` | Runs an unverified script as root, and nothing pins its content. The chosen line checks the binary's SHA-256 before running anything (§4.4) |
| **The binary from GitHub only** | Servers often reach PNMT (they must) but not GitHub. Decided in the review: PNMT serves it (R12) |
| **The binaries in the Docker image** | About 70 MB more in every image, for files most installations download a handful of times. Fetched once and kept costs nothing. The download folder covers a PNMT without internet (§4.4) |
| A `.deb` and an apt repository | A signed repository to host and keep. The request is a single line. A `.deb` around the same binary can come later |
| NativeAOT | Smaller and faster to start, but it can't be cross-compiled from macOS, and `AgentJson` (reflection) and YamlDotNet would need rework first. Possible later |
| Rights in PNMT's UI | The idea asks for rights on the agent itself, and PNMT must not be able to grant itself rights |
| **Users register their own keys** | A registered key no longer raises 465, so a user could vouch for any key, a stolen or planted one included. Admins register; users see theirs (§6.4) |
| **Deploying registered keys to servers** (a full key distribution system) | It would give PNMT the power to grant access, which §2.2 rules out. Configuration management (Ansible, cloud-init) deploys keys; PNMT watches, names and removes them |
| Script parameters by `getopts`, argbash or docopt | None of them can be read as typed parameters without running code. A declarative YAML header can be (§8.2) |
| Script arguments on the command line, or with `--setenv` | Visible in `ps`, and quoting is a source of injection. An environment file is read by systemd and never becomes a command line (§8.4) |
| `/var/log/auth.log` | Debian 12 no longer installs rsyslog by default. The journal has the same lines on Debian and Ubuntu, with cursors (§5.6) |
| A `FileSystemWatcher` per `.ssh` folder | Each takes an inotify instance, and the default limit is 128 per user. One instance with many watches instead (§6.2) |
| Keys in a TPM | Not on every server or VM, and much more code. Root-only files, as root controls the server anyway (§4.5) |

### 2.4 What package two built, and what Linux reuses

**The state of package two**:

- **Built:** W1–W6 are built (`9fec15e` … `50dd1c0`).
- **Not built:** W0, the checks on real Windows machines, is still to do.
- **Not released:** all of it came after `v1.2.0`.
- **Its size:**
  - the agent: about 6,100 lines in `PNMTD.Sensor.Windows`;
  - the protocol: 1,400 lines in `PNMTD.Lib/Agent`;
  - the server: 2,300 lines in `PNMTD/Agents`, `PNMTD/Windows` and `PNMTD/Diagnostics`, plus services, controllers, the simulator and the seed;
  - the web UI: 39 files;
  - the tests: `PNMTD.Tests/Windows` and `PNMTD.Tests/Diagnostics`, and `e2e/windows.spec.ts`.

**The inventory**, part by part:

| Part | Where | Platform | For Linux |
|---|---|---|---|
| **The protocol:** envelopes, frames and limits, signatures, join tokens, `AgentSession` (handshake, signed requests, replay window), `AgentJoinClient`, diagnostics records, `ScriptArguments`, `AgentSecrets` | `PNMTD.Lib/Agent/` | **Neutral already** | Used as it is. Additions in §3.4 |
| The section records `HardwareSection`, `NetworkSection`, `Heartbeat`, `LoginRecord`, `SessionInfo`, `AgentProblem` | `PNMTD.Lib/Agent/AgentSections.cs`, `AgentMessages.cs` | Neutral, with a few Windows names (`VolumeInfo.Letter`, `BitLocker`) | Reused. The Windows names become generic while nothing is released (§3.4) |
| `SystemSection`, `ServicesSection`, `RemoteAccessSection`, `AccessSection`, `EventLogSection` | same | Windows | Linux records of their own (§5) |
| **The service's loop:** connect, handshake, backoff for ever, revocation, watchdog, memory limit, status file, request dispatch, `IAgentFeature` | `Service/AgentService.cs` | Neutral, except `WindowsVersion`, the CNG key and the folder ACL | **→ Core**, behind `IAgentPlatform` (§3.2) |
| The collectors' scheduler, and the collector base with its volatile-free hash | `Service/SectionScheduler.cs`, `Collectors/Collector.cs` | Neutral (`NetworkChange` works on Linux) | **→ Core** |
| Problems, `Isolated`, the daily log file | `Resilience/Problems.cs`, `Logging/DailyFileLogger.cs` | Neutral | **→ Core** |
| `agent.json`, `status.json`, state files, quarantine | `State/AgentFiles.cs` | Neutral, except `Secure()` and the paths | **→ Core**, with paths and securing from the platform |
| **Diagnostics:** schedule, Run now, ping, DNS, buffer | `Diagnostics/DiagnosticsFeature.cs` | Neutral (`Ping`, `Dns`, DnsClient) | **→ Core unchanged** (§7) |
| Joining and uninstalling | `Join/Joiner.cs`, `Join/Uninstaller.cs` | The flow is neutral. Key, service and program copy are Windows | **→ Core** flow with platform steps |
| Scripts: policy, hash, argument check, secret decryption, 1 MiB output cap, result | `Scripts/ScriptsFeature.cs`, `ScriptPolicy.cs` | The checks are neutral. Parsing and running are PowerShell | **→ Core** skeleton with `IScriptHost`. PowerShell stays |
| Heartbeat values, `AgentInfo` (version, architecture, paths), the command line's commands | `Service/HeartbeatSampler.cs`, `AgentInfo.cs`, `Program.cs` | The record and the commands are neutral. Values and paths are Windows | Split |
| WMI, registry, firewall COM, `WinVerifyTrust`, the event log stream, `sc.exe`, the CNG key, WinForms, PowerShell, `WindowsVersion` | `Collectors/*`, `Access/LoginStream.cs`, `Native/*`, `Service/ServiceControl.cs`, `State/AgentKey.cs`, `Ui/`, `Scripts/PowerShell.cs` | Windows | **Stay.** The Linux counterparts are §4–§8 |
| **The hub:** challenge, hello, one connection per agent, routing, rate limits, origin check | `PNMTD/Agents/AgentHub.cs` | Neutral in code. Reads `db.WindowsAgents` | Generalised (L1) |
| The join endpoint, PNMTD's signing key, the anonymous paths | `PNMTD/Agents/AgentJoinEndpoint.cs`, `AgentKeyStore.cs`, `AgentPaths.cs` | Neutral, but creates `SensorType.WINDOWS` | Generalised. The platform comes from the token (§3.3) |
| Heartbeats → 250/251/451, problems, sections, bye | `PNMTD/Windows/WindowsReportService.cs`, `WindowsSections.cs` | Neutral, except the codes and the "Kerberos" message | Generalised, with codes per platform |
| Logins → 254/454 | `PNMTD/Windows/WindowsAccessService.cs` | Neutral, except the codes and the kinds that page | Generalised |
| The differs (changes, 253) | `PNMTD/Windows/WindowsChanges.cs` | Windows records | The store and dispatch are shared. Windows differs stay, Linux differs are new |
| Agent log, retention, options, configuration | `WindowsAgentLog`, `WindowsMaintenanceTask`, `WindowsOptions`, `WindowsConfig` | Neutral | Generalised |
| The release's links and hashes | `WindowsDownloads` (reads the `.sha256` assets, cached) | Neutral | Generalised, **and it serves the files themselves** (§4.4) |
| **Diagnostics on PNMTD** | `PNMTD/Diagnostics/`, `DiagnosticsSetService` | Neutral; the target is any row of `windows_agents` with the capabilities | **Works for Linux after L1 without a change** |
| Scripts on PNMTD: library, versions, runs, parse on connect, redaction | `WindowsScriptService`, `Windows/Scripts/ScriptResults.cs`, `ScriptsControllers` | Neutral, except parsing and run-as | Generalised with a language (§8.1) |
| Fleet, tokens, switches, revoke, re-join; the API | `WindowsServerService`, `WindowsControllers` | Neutral, except typed sections | Generalised. Typed sections per platform (§10) |
| The fake agent and the seed | `Cli/WindowsSimulator.cs`, `WindowsCommand.cs`, `DevSeedWindows.cs` | Neutral session code, Windows content | `pnmtd agent simulate --platform linux`, and Linux servers in the seed |
| Web UI | `views/admin/windows/` (14), `views/sensors/windows/` (17) | Mostly generic tables | Split into shared, Windows and Linux (§11) |
| Build and release | `build-sensor-windows.sh`, `tools/agent.sh`, `push.yml`'s `agent_release` | Windows targets | Extended (§12) |

**What package two leaves open that matters here:**
- **W0 is not done.** The Core extraction (L2) moves Windows code that has not run on a real Windows machine yet.
  - L2 therefore only moves code; it changes none.
  - Before 1.3.0, W0 and L0 are worth doing in the same session.
- **The Windows names in shared code are unreleased**, so they can still become generic without compatibility work:
  - `VolumeInfo.Letter` and `BitLocker`;
  - the `Windows__*` settings and the `/api/v1/windows/…` paths;
  - the `windows_*` tables.

---

## 3. Sharing the code (R6)

### 3.1 The projects

```
PNMTD.Lib/Agent/          the protocol, as today; + the platform, the Linux records, the authorized_keys
                          line parser and fingerprints, the bash script header parser (§3.4)
PNMTD.Sensor.Core/        new, net10.0: the agent's platform-neutral half (§3.2)
PNMTD.Sensor.Windows/     net10.0-windows: WinForms, WMI, the event log, PowerShell, sc.exe  → Core
PNMTD.Sensor.Linux/       new, net10.0 (linux-x64, linux-arm64): the command line, systemd,
                          /proc and /sys, journald, SSH keys, config.yaml, bash  → Core
PNMTD/                    the server: Agents/ generalised (§3.3); SshKeys/ new; Windows/ and Linux/
                          only for what differs
```

### 3.2 PNMTD.Sensor.Core

**What moves, from `PNMTD.Sensor.Windows` (about 1,900 of its 6,100 lines):**
- `AgentService` and `IAgentFeature`;
- `SectionScheduler` and the `Collector` base;
- `ProblemReporter` and `Isolated`;
- `DailyFileLoggerProvider`, with the component prefix as a parameter;
- `AgentFiles` and its records (`AgentConfigFile`, `AgentStatusFile`, `DiagnosticsStatus`);
- `DiagnosticsFeature`;
- the join and uninstall flows;
- the scripts skeleton: policy, hash, a fresh parse, `ScriptArguments`, secrets, result, output cap;
- the `AgentRights` model (scripts, and, on Linux, keys);
- the shared commands: `status`, `collect`, `version`, `update`, and `join`'s and `uninstall`'s steps.

**What a platform provides:**

```csharp
public interface IAgentPlatform
{
    AgentPlatform Platform { get; }                 // Windows, Linux
    AgentLayout Paths { get; }                      // data, state, logs, status, crash marker, collect-now flag, program
    AgentOs ReadOs();                               // the version check (W§3.9, §4.8): supported or not
    string? MachineId();                            // MachineGuid, /etc/machine-id
    IAgentKeyStore Keys { get; }                    // signing and encryption keys: create, open, delete
    void SecureFolders();                           // the ACL; chmod 0700 root
    void SystemLog(string message, bool warning);   // the Windows event log; the journal
    IServiceManager Service { get; }                // install, start, stop, restart, status, delete, installed version
    IHeartbeatValues Heartbeat { get; }             // CPU, memory, fullest volume, sessions, reboot pending, last shutdown
    AgentRights ReadRights();                       // scripts-policy.json (Windows' window); config.yaml (Linux)
    IScriptHost? Scripts { get; }                   // PowerShell; bash
}
```

- **Core has no OS-specific package.** It depends on `Microsoft.Extensions.Hosting`, `DnsClient` and `PNMTD.Lib`.
- **The move is one commit (L2) that changes no behaviour.**
  - The Windows agent keeps its namespaces where it keeps its files.
  - It still builds on macOS and on CI.
  - The simulator and the tests don't change, because they use `AgentSession` from PNMTD.Lib.

### 3.3 The server side, generalised (decided in the review)

**Before 1.3.0, the agent infrastructure loses its Windows names, and Windows-specific code keeps them.** This is one phase (L1) that changes no behaviour, with every existing test green.

| Today (Windows only) | After L1 |
|---|---|
| `windows_settings`, `windows_join_tokens`, `windows_agents`, `windows_sections`, `windows_logins`, `windows_changes`, `windows_agent_log` | `agent_settings`, `agent_join_tokens` (+ `Platform`), `agents` (+ `Platform`; `MachineGuid` → `MachineId`), `agent_sections`, `agent_logins`, `agent_changes`, `agent_log` |
| `windows_scripts`, `windows_script_versions`, `windows_script_runs` | `agent_scripts` (+ `Language`), `agent_script_versions`, `agent_script_runs` |
| `windows_run_as` | **Stays:** Windows' run-as accounts with passwords and gMSAs |
| `PNMTD/Windows/` (report, access, sections, agent log, maintenance, options, downloads, configuration) | `PNMTD/Agents/` |
| `WindowsChanges` | `PNMTD/Agents/AgentChanges.cs` (store, dispatch, 263/253), `PNMTD/Windows/WindowsDiffers.cs`, `PNMTD/Linux/LinuxDiffers.cs` |
| `WindowsServerService`, `WindowsScriptService`, `ScriptResults` | `AgentServerService`, `AgentScriptService` |
| `/api/v1/windows/servers…`, `join-tokens`, `agent-log`, `agent`, `scripts…`, `script-runs…` | `/api/v1/agents/…` |
| `/api/v1/windows/servers/{id}/sections/{s}`, `/api/v1/windows/run-as…` | **Stay**, and `/api/v1/linux/…` comes next to them (§10) |
| `Windows__*` settings | `Agents__*` (§9.5) |
| `AuthEvents.Windows*` | `Agent*` for what is shared (joins, tokens, revocations, scripts), `SshKey*` for keys |
| `pnmtd windows simulate`, `pnmtd windows tokens` | `pnmtd agent simulate [--platform linux]`, `pnmtd agent tokens` |
| *Admin → Windows servers* (`/admin/windows`) | ***Admin → Servers*** (`/admin/servers`) (§11) |
| `PNMTD.Tests/Windows/` | `PNMTD.Tests/Agents/` for what is shared; `Windows/` and `Linux/` for the rest |

- **The migration renames, it doesn't recreate** (`ALTER TABLE … RENAME`), so development databases keep their data.
- **Nothing released changes:**
  - the protocol stays version 1;
  - a Windows agent built before L1 connects to PNMTD after L1;
  - no deployed `.env` has `Windows__*` yet, apart from development.
- **docs/windows-sensor-plan.md gets a note** at its top: the names of its §8–§10 changed with L1, and where they went.

### 3.4 The protocol's additions (`PNMTD.Lib/Agent/`)

- **`AgentPlatform { Windows, Linux }`**, in `Hello.Platform` and `JoinRequest.Platform`. Both are optional and default to `Windows`, so earlier Windows agents need no change.
- **Section names:**
  - `journal` and `ssh-keys` are added;
  - `SectionNames.Windows` and `SectionNames.Linux` list what each platform sends;
  - `system`, `services`, `remote-access` and `access` are named the same on both, and **typed by the agent's platform**.
- **New records** in `AgentLinux.cs`:
  - `LinuxSystemSection`, `LinuxServicesSection`, `LinuxRemoteAccessSection`, `LinuxAccessSection`, `JournalSection`;
  - `SshKeysSection`, with `SshdKeySettings`, `AuthorizedKeysFile`, `AuthorizedKey`;
  - `AgentRightsSummary`.
- **Logins:**
  - `LoginRecord` gains `KeyFingerprint` and `TargetUser`, both optional;
  - `LogonKind` gains `Sudo`, `Su` and `Sftp`.
- **Requests and answers:**
  - `remove-ssh-key` (signed) with `RemoveSshKeyRequest(User, Path, Fingerprint)`;
  - `key-removal` with `SshKeyRemovalResult(RequestId, Outcome, LinesRemoved, Backup, NewFileHash, Message)`. Like `log` for `send-log`, it completes the waiting request.
- **The download manifest:** `DownloadManifest(Version, Assets: [Name, Size, Sha256])`, a `SignedEnvelope` signed with PNMTD's agent signing key, which agents check against the key they pinned at the join (§4.4).
- **`RunAsKind.User`:** a local account, which needs no password on Linux (§8.4).
- **Capabilities:**
  - `keys.v1`: the `ssh-keys` section;
  - `keys.remove.v1`: only while `config.yaml` allows removal;
  - `scripts.v1` and `parse.v1`, as on Windows. The platform says which language.
- **`Hello.Rights`** (optional): a summary of the effective rights, which PNMT shows and uses to grey out what a server refuses. For example: key removal for `deploy`, `www`; 2 protected keys; scripts as `root`, `deploy`.
- **Pure helpers, used by PNMTD and the agent alike:**
  - `SshKeys.ParseLine` (options, type, blob, comment), which also recognises a private key and refuses it;
  - `SshKeys.Fingerprint` (`SHA256:` and unpadded Base64, as `ssh-keygen -l` prints it);
  - `BashScriptHeader.Parse` → `ParsedScript` (§8.2).
- **Generic names in the shared records, while they are unreleased:**
  - `VolumeInfo.Letter` → `Mount`;
  - `VolumeInfo.BitLocker` → `Encryption`.

---

## 4. The agent on Linux

### 4.1 One binary, a command line (R3)

| Command | Does |
|---|---|
| `pnmt-agent join --url <PNMT URL> --token <xxxx-xxxx> [--name <name>] [--quiet]` | Joins, installs and starts the service. **Exits 0 when connected**, 3 when installed but not connected within 60 s. Missing arguments are asked on the terminal, the token without echo |
| `pnmt-agent status` | The service, the connection, the last heartbeat, the recent problems, the diagnostics loaded, the logging gaps, and **the effective rights** (what `status.json` and `config.yaml` say) |
| `pnmt-agent keys` | The `authorized_keys` files and keys as the agent sees them, and which of them PNMT may remove |
| `pnmt-agent keys restore <backup>` | Puts a removed key's file back from its backup, after a confirmation (§6.5) |
| `pnmt-agent config check` | Validates `/etc/pnmt-agent/config.yaml` and prints the effective rights, or why everything is off |
| `pnmt-agent collect [<section>]` | Collects once and prints the sections as JSON (for support) |
| `pnmt-agent collect-now` | Asks the running service to collect everything now (a flag file, as on Windows) |
| `pnmt-agent log [-n <lines>] [-f]` | The log file's last lines, or follows it |
| `pnmt-agent update [<file>]` | **Without a file:** downloads PNMT's version from PNMT, and checks it against the manifest PNMTD signed (§4.4). **With a file** (a newer binary): uses it. Then stop, replace `/usr/local/sbin/pnmt-agent`, start. The identity, keys, state and `config.yaml` stay |
| `pnmt-agent uninstall [--purge]` | Stops, disables and deletes the unit, says `bye`, and deletes the keys and `/var/lib/pnmt-agent`. **`config.yaml` and the logs stay unless `--purge`**, as `apt remove` and `apt purge` do |
| `pnmt-agent version` | Version and architecture |
| `pnmt-agent service` | The service mode, started by systemd |

- **Every command except `version` and `help` needs root**, and says so otherwise.
- **The build:**
  - `net10.0`, `linux-x64` and `linux-arm64`;
  - `SelfContained`, `PublishSingleFile`, `EnableCompressionInSingleFile`;
  - **`InvariantGlobalization=true`** (no libicu, which minimal Debian lacks);
  - **not trimmed:** `AgentJson` uses reflection, and so does YamlDotNet;
  - the single-file settings are in the project file behind `-p:AgentRuntime=…`, the pattern W5 found for the lock files.
  - L0 measures the size (expect 30–40 MB) and checks that nothing is extracted at start.
- **Needed on the server:** glibc (any supported distribution), `libssl3` and `ca-certificates`, for TLS and for the download itself. Nothing else.
- **The tools it calls**, each with a timeout and isolated (W§3.8, rules 1 and 2), and *not available* when missing:
  - from the base system: `systemctl`, `journalctl`, `loginctl`, `timedatectl`, `ip`, `ss`, `lsblk`, `lscpu`, `sshd -T`, `dpkg-query`, `apt-get -s`, `systemd-run`, `bash -n`, `setpriv`;
  - optional: `nft`, `ufw`, `smartctl`, `dmidecode`, `shellcheck`, `fail2ban-client`, `aa-status`, `mokutil`, `snap`, `pveversion`.
- **Packages:** `Microsoft.Extensions.Hosting.Systemd` (notify, watchdog), `YamlDotNet` (config.yaml, script headers), and `DnsClient` (already pinned).

### 4.2 Where it lives

| What | Where |
|---|---|
| The program | **`/usr/local/sbin/pnmt-agent`**, root, 0755. `join` and `update` copy themselves there |
| The unit | `/etc/systemd/system/pnmt-agent.service` (§4.3) |
| **The rights** | **`/etc/pnmt-agent/config.yaml`**, root, 0600 (§4.6). Written by root, never by PNMT. `join` creates a commented template once, with everything off, if there is none |
| The identity | `/var/lib/pnmt-agent/agent.json`, 0600: **the same record as Windows' `agent.json`** (URL, agent id, PNMTD's pinned key, the last configuration) |
| The keys | `/var/lib/pnmt-agent/keys/`, 0700: the signing and encryption keys as PKCS#8 PEM, 0600 |
| The state | `/var/lib/pnmt-agent/state/`: journal cursors, buffers, crash marker, and key backups in `state/key-backups/` |
| The status | `/var/lib/pnmt-agent/status.json`, every 10 s, for `pnmt-agent status` |
| Logs | **`/var/log/pnmt-agent/pnmt-agent-YYYY-MM-DD.log`, one file per day, 10 kept, 50 MB each**: the shared `DailyFileLoggerProvider`. The journal (identifier `pnmt-agent`) gets the starts, stops, joins, crashes, key removals and script runs, as the Windows event log does |
| Script runs | `/var/lib/pnmt-agent/runs/<id>/`, deleted after each run (§8.4) |

### 4.3 The systemd unit (R5)

```ini
[Unit]
Description=PNMT agent: reports this server to PNMT
Documentation=https://github.com/muqiuq/PNMTD/blob/master/docs/linux-sensor-plan.md
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=0

[Service]
Type=notify
ExecStart=/usr/local/sbin/pnmt-agent service
Restart=always
RestartSec=60
WatchdogSec=5min
TimeoutStopSec=30
# The server comes first (W§3.8 rule 8): .NET ignores thread priorities on Linux, so the whole agent is nice.
Nice=10
CPUWeight=20
IOSchedulingClass=best-effort
IOSchedulingPriority=7
# Sandboxing that blocks no feature; L0 checks each line against every collector.
NoNewPrivileges=yes
ProtectSystem=full
PrivateTmp=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictSUIDSGID=yes
LockPersonality=yes
# Written by join and update from config.yaml's ssh_keys.writable_paths (§4.6), when it names any.
# ReadWritePaths=/etc/pve/priv

[Install]
WantedBy=multi-user.target
```

- **Root** is needed:
  - to read every account's key files, the whole journal, `sshd -T`, `/etc/shadow`'s lock state, SMART and DMI;
  - to rewrite key files with their owners' rights (§6.5);
  - to start scripts as any account (§8.4).
  - A dedicated user with capabilities would need `CAP_DAC_OVERRIDE`, `CAP_SETUID`, `CAP_SETGID` and `CAP_CHOWN`, which is root in all but name.
- **Restarts:**
  - `Restart=always` with `RestartSec=60` and no start limit stands in for Windows' recovery settings (W§3.8, rule 5);
  - **the watchdog is stronger than on Windows:** the service loop notifies systemd with each heartbeat tick, so a hung process (not only a hung loop) is killed and started again.
- **The sandbox doesn't reach scripts:** they run in transient units of their own, started by PID 1 (§8.4), so `ProtectSystem` and `NoNewPrivileges` don't restrict them.
- **`ProtectSystem=full` makes `/etc` read-only for the agent itself.**
  - A key file under `/etc` (for example `AuthorizedKeysFile /etc/ssh/authorized_keys/%u`, or Proxmox's `/etc/pve/priv/authorized_keys`) can be read but not rewritten there.
  - Removing a key from it is refused with that reason. `config.yaml` can list such paths in `ssh_keys.writable_paths`, which become `ReadWritePaths=` at the next `join` or `update`.

### 4.4 Installing with one line (R4, R12)

1. **In PNMT, an Admin chooses an existing host** and opens *Add server* from the host's menu or *Admin → Servers*. They choose **Linux**, and optionally a name (default "Linux") and the token's validity (24 h, at most 7 days).
2. **PNMT shows the token once, and the line**, filled in with:
   - PNMT's URL, its version and the architecture (x64 or arm64, a toggle);
   - the binary's SHA-256 and the token.
   - Toggles choose where the binary comes from (**PNMT**, the default, or **GitHub**, with the same hash), `wget` or `curl` (Debian's standard utilities bring `wget`; minimal images may have neither, see the dialog's hint), and running as root or through `sudo sh -c '…'`.

   ```sh
   wget -qO /tmp/pnmt-agent https://pnmt.example.com/agent/v1/download/pnmt-agent-linux-x64 && echo "3f2a…c91e  /tmp/pnmt-agent" | sha256sum -c --quiet && install -m 0755 /tmp/pnmt-agent /usr/local/sbin/pnmt-agent && rm /tmp/pnmt-agent && pnmt-agent join --url https://pnmt.example.com --token k7mq-3xtp
   ```

   With GitHub chosen, only the URL changes: `https://github.com/muqiuq/PNMTD/releases/download/v1.3.0/pnmt-agent-linux-x64`.
3. **`join` checks the machine:** root, systemd running (`/run/systemd/system`), a supported distribution or not (§4.8), and a writable `/var/lib`.
4. **It checks the URL** as on Windows (W§3.3, steps 3–6):
   - `https` only, unless PNMTD answers `allowHttp`;
   - the certificate is validated against the system's store;
   - `GET /agent/v1/join` answers PNMTD's version and key fingerprint.
5. **It creates the keys** in `/var/lib/pnmt-agent/keys/`.
6. **`POST /agent/v1/join`** sends:
   - the token, the public key and `Platform: Linux`;
   - the host name, FQDN and domain (the realm when joined to one);
   - the OS from `os-release` and the kernel;
   - the agent's version and architecture;
   - `/etc/machine-id`.
7. **PNMTD checks the token and that it was made for Linux.**
   - A Windows token used by a Linux agent is refused with that reason, and the other way round.
   - Then the join proceeds as on Windows. **The `LINUX` sensor is created on the token's host.**
8. **It writes `agent.json`**, and **`config.yaml`'s template if there is none**. A file deployed beforehand (Ansible, Puppet) is never touched.
9. **It writes the unit**, then runs `systemctl daemon-reload` and `systemctl enable --now pnmt-agent`. It waits until `status.json` says *connected*, at most 60 s.
10. **It prints both fingerprints**, `AB12 CD34 …`. PNMT's agent log records the same.

- **The token in the shell's history** is single use and expires in 24 hours, so it is harmless after the join. `join` without `--token` asks for it without echo.
- **Unattended installs:** the same line. Exit 0 means connected.
- **Re-join:** as on Windows, with *Re-join* on a server's page: a new key and a new agent id, and the sensor keeps its history.
- **Updating:** `pnmt-agent update` on the server. The *Agent* tab shows that line when the agent is outdated.

**The binary from PNMT (R12):**

- **Two anonymous paths** under the existing `/agent/v1/` (no session: the server isn't joined yet, and the files are public on GitHub anyway):

  | Path | Answers |
  |---|---|
  | `GET /agent/v1/download` | **The manifest:** PNMTD's version and each asset's name, size and SHA-256, signed with PNMTD's agent signing key |
  | `GET /agent/v1/download/{asset}` | **The file**, for a fixed list of names only: `pnmt-agent-linux-x64`, `pnmt-agent-linux-arm64`, `pnmt-sensor-x64.exe`, `pnmt-sensor-arm64.exe`, and their `.sha256`. Anything else is 404 |

- **Where PNMTD gets the files: its download folder** (`Agents__DownloadFolder`, by default `/var/lib/pnmtd/downloads` inside the container).
  - **An asset that isn't there is fetched from the GitHub release of PNMTD's own version** when it is first asked for (the *Add server* dialog asks ahead). It is checked against the release's `.sha256` and kept. A download that comes in meanwhile waits for the fetch.
  - **Kept in the container, not in a volume:** a recreated container (every update) starts empty and fetches its own version again. `docker-compose.yml` doesn't change.
  - **A PNMT without internet:** mount a folder there holding the release's files. **PNMTD serves only files that match their `.sha256`.** The same works for development, with `dist/`.
- **The hash in the line** is the one PNMTD verified, so the line catches any other file, whether it comes from PNMT or GitHub.
- **`pnmt-agent update` trusts PNMT's signature, not only TLS:** it checks the manifest against the key it pinned at the join. A TLS-inspecting proxy can't hand over another binary.
- **Limits:**
  - 20 downloads per hour per address;
  - `Agents__ServeDownloads=false` turns the paths off, and the dialog then offers GitHub only;
  - the paths are in `EndpointAuditTests`' anonymous allowlist (§9.7).
- **The Windows executables are served the same way**, so the Windows *Download* tab offers PNMT's link next to GitHub's too.
- ***Admin → Servers → Download*** shows each file's state (here, being fetched, or why it couldn't be fetched), with *Fetch again*.

### 4.5 Trust and keys

- **The scheme is W§3.4's unchanged:**
  - ECDSA P-256 for the agent's signatures;
  - ECDH P-256 for secrets encrypted to the agent;
  - PNMTD's signing key pinned at the join;
  - a fresh challenge per connection;
  - every request signed, unexpired, for this agent, and run once.
- **The difference: Linux has no machine key store** like CNG's non-exportable keys. The keys are **PEM files, 0600, in a 0700 folder owned by root**.
  - Root can copy them and impersonate the agent elsewhere. Root controls the server and everything it reports anyway.
  - *Revoke* in PNMT ends a copied key as it ends the original.
  - A TPM-backed key is a possible later step (§2.3).

### 4.6 `config.yaml`: the rights on the server (R8, R9, R10)

```yaml
# /etc/pnmt-agent/config.yaml: what PNMT may do on this server.
# Only root can change this file; PNMT can't. The agent reads it at start and when it changes.
# Check it with: pnmt-agent config check

log_level: info                 # info or debug; read without a restart

ssh_keys:
  remove: false                 # PNMT may remove keys from authorized_keys files
  users: []                     # only these accounts' keys; empty: every account
  protect: []                   # fingerprints PNMT may never remove (break-glass keys), e.g.
                                #   - SHA256:Kq1f9hJ0x0mRk8xPZ3oWJ6gW5p0rPj5n8l2pHq0rS2A
  writable_paths: []            # key files under /etc the agent may rewrite, e.g. /etc/pve/priv on Proxmox VE

scripts:
  enabled: false                # PNMT may run bash scripts
  run_as: []                    # the accounts they may run as; root must be named to be allowed, e.g. [root, deploy]
  allowed_hashes: []            # only these scripts (SHA-256 of their text); empty: any script of PNMT's library
  max_timeout_minutes: 60

collect:
  sudo_commands: false          # sudo's command lines in the logins (they can contain passwords)
  cron_commands: false          # crontab lines in full (by default only the program each line runs)
```

- **Only root grants rights.**
  - No protocol message writes the file.
  - The agent never writes it, except the template at the first `join` when none exists.
- **The rules when the file is read:**

  | Condition | Effect |
  |---|---|
  | Owned by root, not writable by group or others, valid YAML, known keys | Applied |
  | Missing | The defaults: **everything that changes something off** |
  | Not owned by root, writable by others, unreadable, or not valid YAML | **Everything off**, a problem reported (W§3.8 rule 3), and the log says why |
  | An unknown key (`remvoe: true`) or a value of the wrong type | That setting is off, with a warning, so a typo never silently grants or withholds |

- **Changes apply without a restart.** An inotify watch on `/etc/pnmt-agent/` re-reads the file.
  - **When the rights changed, the agent reconnects**, so that its hello announces the new capabilities and rights.
  - PNMT's agent log records it, for example "Rights changed on the server: key removal allowed for deploy, www; scripts off."
- **PNMT shows the rights** on the server's *Agent* tab and in the fleet's filters, and greys out what a server refuses, with the line of `config.yaml` that would allow it.
- **The same model on Windows:** Core's `AgentRights` is read there from the window's `scripts-policy.json` (W§7.2), and on Linux from this file.

### 4.7 Logging and resilience

- **W§3.8's eight rules apply unchanged**, from the shared Core:
  - each collector, check, message and loop is isolated;
  - everything has a timeout;
  - problems are reported to PNMTD, deduplicated per hour, buffered while offline;
  - the connection loop never ends;
  - a crash restarts the agent and is reported at the next start;
  - resources are bounded (300 MB);
  - damaged state is renamed `.broken`;
  - the server comes first.
- **W§3.7's offline buffers are the same.** Logins are read on from the journal cursor after a time offline, as long as the journal still holds them.

| Where Linux differs | How |
|---|---|
| A crash | systemd restarts the agent (`Restart=always`). The crash marker in `state/` makes the next hello say `restartedAfterCrash` |
| A hung process | systemd's watchdog (`WatchdogSec=5min`) kills it and starts it again |
| The system log | The journal instead of the Windows event log: starts, stops, joins, crashes, rights changes, key removals, script runs |
| Priority | `Nice=10`, a low `CPUWeight` and best-effort I/O at the lowest level, for the whole agent |
| Clock skew | 461 says it as on Windows, without the Kerberos remark. On Linux it matters for TLS, TOTP and logs |

### 4.8 Distributions (R11)

| | Versions |
|---|---|
| **Built and tested**, with every release (§14) | **Debian 12 (bookworm) and 13 (trixie)**, amd64 and arm64 |
| **Supported** (no badge): checked in L0, and again when a release changes what differs there | **Proxmox VE 8 and 9** (Debian 12 and 13 underneath). **Ubuntu LTS 22.04, 24.04 and 26.04**, amd64 and arm64 |
| **Anything else with systemd and glibc** | Other Ubuntu releases, Raspberry Pi OS, Debian 11, and other derivatives: **logged, reported, flagged *not a supported version*, and run anyway**, as on Windows (W§3.9). A collector that finds a tool or file missing reports that part as *not available* |
| **Not at all** | musl (Alpine), no systemd (Devuan, containers without an init), 32-bit. `join` refuses with the reason |

- **The check** runs at every start:
  - `/etc/os-release` (`ID`, `ID_LIKE`, `VERSION_ID`, `VERSION_CODENAME`);
  - `/etc/debian_version`;
  - for Proxmox VE, `pveversion` and `/etc/pve`.
- **Where they differ**, handled by the collectors rather than by separate code paths:

  | | Differences |
  |---|---|
  | **Ubuntu** | <ul><li>**rsyslog still writes `/var/log/auth.log`.** The agent reads the journal, which has the same lines.</li><li>**From 22.10 on, `ssh.socket` starts sshd on demand.** The listening ports come from the socket unit, and §5.5 reads both.</li><li>**Updates:** the security archive is `<codename>-security`, and Ubuntu Pro's ESM is noted when enabled.</li><li>**Snaps:** services from snaps run from `/snap` and are classed by the snap's publisher (§5.4).</li><li>AppArmor is on, and `/var/run/reboot-required` is written, by default.</li><li>**systemd 249 on 22.04 is the oldest supported.** L0 checks every call there.</li></ul> |
  | **Proxmox VE** | <ul><li>**Its archive** (`download.proxmox.com`) counts as the distribution's.</li><li>**Root's `authorized_keys` is a symlink to `/etc/pve/priv/authorized_keys`**, the cluster's shared file (pmxcfs). It is read and shown as *cluster-wide*. A removal there takes the key from every node of the cluster (§6.5).</li><li>**The cluster nodes' own root keys are protected** automatically (§6.6).</li><li>The web UI (8006) and the SPICE proxy (3128) are remote access (§5.5).</li></ul> |
  | **Debian 11** | Outside .NET 10's own support list. L0 checks whether the binary starts. If it does, it is *unsupported, runs anyway* |

---

## 5. What it reports (R1)

### 5.1 Overview

| Section | Contents | Source | Collected |
|---|---|---|---|
| `system` | Host, OS, kernel, uptime, updates, time, security, local admins, accounts | `os-release`, `/proc`, `hostnamectl`, `timedatectl`, `apt-get -s`, `/etc/passwd`, `group`, `shadow` (lock state), `sudoers`, `loginctl` | 15 min, at boot, every 6 h |
| `hardware` | Machine, firmware, CPU, memory, disks, volumes, TPM, GPU, batteries. **The Windows record** | `/sys/class/dmi`, `lscpu -J`, `/proc/meminfo`, `lsblk -J`, `dmidecode`\*, `smartctl -j`\* | At boot, every 6 h |
| `network` | Adapters, addresses, routes, DNS, bonds, bridges, listeners, proxy, hosts. **The Windows record** | `ip -j`, `/sys/class/net`, `ss -H -tulpn`, `/etc/resolv.conf`, lease files | On a netlink change, 5 min, every 6 h |
| `services` | systemd services and their class, timers, cron | `systemctl`, `dpkg-query`, `apt-cache policy`, `snap`\*, cron files | 1 min (the units), 15 min (packages), every 6 h |
| `remote-access` | sshd's effective settings, other ways in, the firewall's view | `sshd -T`, units and sockets, listeners, `nft -j`\*, `ufw`\* | 5 min, every 6 h |
| `access` | Sessions now, the logging check, the last 24 hours. **Logins and elevations are a stream** (§5.6) | `loginctl`, `journalctl` | 5 min, every 6 h; the stream every 15 s |
| `journal` | The newest 100 entries at warning or above | `journalctl -p warning -o json` | Noticed by the 15 s poll, every 6 h |
| `ssh-keys` | sshd's key settings, every key file, every key (§6) | Key files, `sshd -T` | **On change (inotify, within seconds)**, 5 min, every 6 h |
| *diagnostics* | Not a section: the results go to their own sensors (§7) | – | – |

\* when installed.

- **Sections are hashed without their volatile values** (free space, counters, leases, PIDs, ages), as on Windows, so a section is sent when something real changed.
- **A collector that fails** sends its section with `errors` filled (W§3.8). The other sections are unaffected.

### 5.2 System

- **Identity:**
  - host name and FQDN;
  - `/etc/machine-id`;
  - the realm when joined to a domain (realmd, SSSD or winbind);
  - virtualisation (`systemd-detect-virt`);
  - whether container runtimes (Docker, Podman) are installed and running;
  - on Proxmox VE, its version and cluster name.
- **OS:**
  - `PRETTY_NAME`, version and codename, the Debian point release;
  - the kernel's release and version, the architecture;
  - **supported or not** (§4.8).
- **Uptime:** the boot time, and the last shutdown. Clean or not is read from the previous boot's journal: did it reach the shutdown target?
- **Updates**, from the package lists as they are. **The agent runs only `apt-get -s dist-upgrade`, a simulation; never `apt update`:**
  - the packages that can be upgraded;
  - **those from the security archive, by name** (at most 200);
  - when the lists were last refreshed;
  - unattended-upgrades installed, enabled, and its last run;
  - **a reboot pending:** `/run/reboot-required`, a newer kernel installed than the one running, or `needrestart`'s verdict when installed.
- **Time:** the time zone, NTP synchronised or not, the service (systemd-timesyncd, chrony, ntpsec), and the skew against PNMTD.
- **Security:**
  - the firewall in use (nftables, iptables, ufw, firewalld), whether it is active, and the input chain's default policy;
  - AppArmor: on or off, and the profiles enforcing and complaining;
  - Secure Boot;
  - encrypted volumes (LUKS);
  - fail2ban: running, its jails, and the addresses banned now.
- **Local administrators:**
  - accounts with uid 0;
  - members of `sudo` (and `admin` and `wheel` where they exist);
  - **the sudoers rules that give `ALL`**, from `/etc/sudoers` and `/etc/sudoers.d/`, by user and group name only.
- **Accounts:**
  - local accounts with a login shell: uid, groups, home;
  - **whether a password is set and not locked**, from `/etc/shadow`'s fields, **never the hash**;
  - this list also offers the run-as accounts in the script dialog (§8.4).
- **Sessions now**, from `loginctl`: who, how (SSH, console, graphical), from where, since when.

### 5.3 Hardware and network: the Windows records

**Both sections reuse `HardwareSection` and `NetworkSection`**, so PNMT's tabs, their differs for disks, memory, addresses, gateways and DNS, and the overview's figures are shared.

| Part | On Linux |
|---|---|
| Machine, firmware | `/sys/class/dmi/id`: vendor, product, serial, BIOS version and date. UEFI or BIOS from `/sys/firmware/efi` |
| TPM | `/sys/class/tpm/tpm0` (version, present) |
| Processors | `lscpu -J`: model, sockets, cores, threads, maximum clock |
| Memory | `/proc/meminfo`. The modules (slot, size, speed, maker) from `dmidecode -t 17` when installed |
| Disks | `lsblk -J -b`: model, serial, size, media (rotational → HDD, `nvme` → NVMe, else SSD), transport. **Health and SMART's verdict from `smartctl -j -H`** when installed |
| Volumes | Mounted real file systems (not `tmpfs`, `overlay`, `squashfs`, `proc`): **`Mount`** (the mount point), label, type, size, free space, **`Encryption`** (LUKS underneath). ZFS datasets are shown by their pool |
| GPUs, batteries | PCI display devices from `/sys/bus/pci`, and `/sys/class/power_supply` |
| Adapters | `ip -j -d link` and `addr`, plus `/sys/class/net`:<ul><li>name and kind (ethernet, Wi-Fi, bridge, bond, VLAN, WireGuard, veth, tun);</li><li>MAC, state, MTU, speed;</li><li>driver and its version;</li><li>addresses with prefixes, the VLAN id, virtual or not;</li><li>**DHCP and its lease**, best effort, from ifupdown/dhclient, systemd-networkd, netplan's backend, or NetworkManager</li></ul>`Profile` and `RegisterInDns` are null |
| DNS | `/etc/resolv.conf`, and systemd-resolved's servers per link when it runs (as on Ubuntu) |
| Teams, virtual switches | Bonds (`/proc/net/bonding`) with their mode, and bridges with their ports (Proxmox's `vmbr*` included) |
| Routes | `ip -j route` for IPv4 and IPv6 (the main table; policy routing is noted) |
| Listeners | `ss -H -tulpn`: protocol, address, port, process and PID. **The systemd unit from `/proc/<pid>/cgroup`** fills `Services`, as Windows fills it with the services in a process |
| Connections | Counts by state |
| Firewall | `null`. Linux's firewall is in §5.2 and §5.5 (Windows' profiles don't exist) |
| Proxy, hosts | `/etc/environment` and apt's proxy; the non-default entries of `/etc/hosts` |

**The heartbeat is the shared record:**
- CPU from `/proc/stat`'s delta;
- memory from `MemAvailable`;
- the fullest real file system;
- the sessions from logind;
- a pending reboot;
- the boot time and the last shutdown.

### 5.4 Services, timers and cron

- **Each systemd service:**
  - name and description;
  - load state, active and sub state (running, exited, failed);
  - unit file state (enabled, disabled, static, masked, generated);
  - the account it runs as, its main PID, its program (`ExecStart`'s binary);
  - its unit file: under `/usr/lib/systemd/system` from a package, `/etc/systemd/system` the admin's, `/run` generated.
- **The class**, decided by the program as W§4.5 decides it by the signature, and cached per path, size and time:

  | Class | Means |
  |---|---|
  | **Distribution** | The program belongs to a package (`dpkg -S`) from the distribution's archive: Debian's, Ubuntu's, or Proxmox's on Proxmox VE, by `apt-cache policy`'s origin. Or to a snap published by Canonical |
  | **Third-party** | It belongs to a package from another archive (Docker, Zabbix, a vendor), to a `.deb` installed by hand, or to another publisher's snap. **"Special"** |
  | **Unpackaged** | No package or snap owns it: `/usr/local`, `/opt`, a home folder. **Special, and highlighted** |
  | **Pnmt** | The agent itself |

- **Flagged:**
  - **enabled but failed or inactive**, except one-shot services that exited, and socket- or timer-activated ones;
  - units in the failed state.
- **Timers:** each timer, its service, its last and next run.
- **Cron:**
  - the files: `/etc/crontab`, `/etc/cron.d/`, the users' crontabs (`/var/spool/cron/crontabs/`), and the scripts in `/etc/cron.{hourly,daily,weekly,monthly}`;
  - per entry: its schedule, its user, and **the program it runs**, with the line's hash for the changes. **Command lines can hold passwords**, so the full line is sent only with `collect.cron_commands: true` (decided in the review).
- **The list shows the special ones first**, as on Windows. Timers and cron are persistence too, so their additions are notable changes (§5.8).

### 5.5 Remote access

| Feature | Detected by | Notes reported |
|---|---|---|
| **OpenSSH server** | `ssh.service` or `ssh.socket` (Ubuntu since 22.10), **`sshd -T`** (the effective settings), the listeners | The ports and addresses. *Root login allowed* (with password, or key only). *Password authentication on*. *Keyboard-interactive on*. `AllowUsers`/`AllowGroups`. *Agent, TCP or X11 forwarding on*. `MaxAuthTries`. *`AuthorizedKeysCommand` set* and *`TrustedUserCAKeys` set* (keys and certificates outside the files, §6.1). *Match blocks* |
| **Proxmox VE** | `pveproxy` (8006), `spiceproxy` (3128) | The web UI's realms (PAM, PVE, LDAP, AD) and whether two-factor is required, where readable |
| **Cockpit** | `cockpit.socket`, 9090 | |
| **Webmin**, Usermin | Their services, 10000 and 20000 | |
| **xrdp** | `xrdp.service`, 3389 | |
| **VNC servers** | x11vnc, TigerVNC (`vncserver@`), TightVNC, wayvnc, ports 5900 and up | *No password*, where visible |
| **Telnet, rsh, rlogin, rexec** | inetd/xinetd configuration, their daemons, ports 23 and 512–514 | Always highlighted |
| **SNMP** | `snmpd`, 161/udp | v1/v2c communities present (names never sent) |
| **Samba, NFS** | `smbd` (445, 139) and its shares; `nfs-server` (2049) and `/etc/exports` | SMB1 on; exports to `*` |
| **Docker's API over TCP** | `dockerd`'s `-H tcp://` or `daemon.json`'s `hosts`, ports 2375 and 2376 | **Root-equivalent access.** *Without TLS* |
| **VPNs and overlays** | WireGuard interfaces, OpenVPN, Tailscale, ZeroTier, NetBird | Ways in that bypass the perimeter |
| **Third-party remote tools** | Their services: TeamViewer, AnyDesk, RustDesk, ConnectWise ScreenConnect, NoMachine, Splashtop, MeshCentral | A list in the agent, extended with each release |
| **fail2ban** | `fail2ban.service` and `fail2ban-client status` | A mitigation: its jails, and how many are banned |

- **Per feature:** installed, active, the ports, whether they listen, **whether the firewall lets them in**, and the notes.
  - The firewall's verdict is **open**, **filtered** or **unknown**.
  - It is evaluated for ufw and for simple nftables and iptables input chains: the default policy plus accept rules on the port. **Anything more complex (Proxmox's own firewall included) says *unknown* rather than guess.**
- **The overview** sums it up, for example "SSH (root: key only) · Proxmox UI · WireGuard".
- **Who actually connected** through each of them is in §5.6.

### 5.6 Logins and elevations (W R19, in Linux terms)

**What is recorded:**

| Who | Kinds | Recorded |
|---|---|---|
| **Everyone** | **SSH sessions** (shell or command) and **console logins** | Every success, with its end where the journal shows it |
| **Everyone** | **sudo and su** (who, as whom, from which terminal) | Every success. **The command only with `collect.sudo_commands: true`** (decided in the review) |
| **File transfers** | SFTP or SCP-only sessions (`subsystem request for sftp`, `internal-sftp`), and backup tools' forced commands | **Aggregated:** one entry per user and source per 10 minutes, with a count (W R19: the "file server users" of Linux) |
| **Failures, existing accounts** | SSH (password, key, keyboard-interactive), console, sudo (incorrect password, **not in sudoers**), su | **Aggregated** per user, source and kind per 10 minutes, with a count. They count towards 464 |
| **Failures, no such account** | "Invalid user …": the internet's scanners | **Aggregated per source** per 10 minutes, with a count and the first 10 names tried. **Never 464** |
| **Never** | cron's and systemd's own PAM sessions, `systemd --user`, Proxmox's own SSH between cluster nodes (aggregated as file transfers when it is visible), the agent's own script runs (they are in the script history) | – |

**The sources**, all from the journal. Debian 12 and Ubuntu keep it persistently by default; L0 confirms the identifiers and lines on every supported system:

| Identifier | Lines | For |
|---|---|---|
| `sshd`, and with newer OpenSSH (Debian 13's 10.0) `sshd-session` and `sshd-auth` | `Accepted publickey for U from A port P ssh2: ED25519 SHA256:…`; `Accepted password`, `keyboard-interactive`; `Failed password`/`publickey for [invalid user] U from A`; `Invalid user U from A`; `Disconnected from user U A port P`; `subsystem request for sftp by user U`; `maximum authentication attempts exceeded` | SSH, **with the key's fingerprint** (and a certificate's id and CA) |
| `login` | `pam_unix(login:session): session opened/closed`; `FAILED LOGIN … FOR 'U'` | Console |
| `sudo` | `U : TTY=… ; PWD=… ; USER=root ; COMMAND=…`; `authentication failure`; `incorrect password attempts`; `user NOT in sudoers` | sudo |
| `su` | `pam_unix(su:session)` / `(su-l:session) session opened for user root by U`; `FAILED SU` | su |
| `systemd-logind` | `New session N of user U`; `Session N logged out` | Pairing a session with its end, and its duration |

- **Lines from OpenSSH and sudo are not localised**, unlike Windows' event messages, so a fixed set of patterns reads them.
  - **The patterns are pure functions**, tested against lines captured in L0 from each supported system (§14).
  - A line that matches no pattern is counted, and the count is reported (one problem per hour). It is never dropped silently.
- **An admin** is:
  - an account with uid 0;
  - a member of `sudo`, `admin` or `wheel`;
  - or an account named, directly or through a group, in a sudoers rule that gives `ALL`.
- **Per entry**, the shared `LoginRecord`:
  - time, outcome and reason;
  - user, and admin or not;
  - kind (Ssh, Sftp, Console, Sudo, Su);
  - the method (publickey, password, keyboard-interactive, gssapi) in `AuthPackage`;
  - **`KeyFingerprint`**, which PNMT shows with the key's registered name and owner (§6.4);
  - `TargetUser` for sudo and su;
  - source address and port;
  - the session id for pairing;
  - the count and last time for aggregates.
- **How nothing is missed:**
  - every 15 s, `journalctl -o json --after-cursor=<cursor>`, filtered to the identifiers above;
  - **the cursor is saved** in `state/bookmarks.json` after the entries are handled, as Windows' event log bookmarks are;
  - the first time, the last hour is read;
  - at most 20,000 entries per poll; a backlog is read on at the next one.
  - The record id PNMT deduplicates on is a 64-bit hash of the cursor.
- **PNMTD stores them** in `agent_logins` for 180 days, as for Windows.
  - **An SSH login with a key also sets that key's *last used*** (§6.3).

**The logging check** (in the `access` section), with what is missing shown on the *Access* tab with the command that fixes it. The agent changes nothing (W§6).
- **Is the journal persistent?** It needs `/var/log/journal`. The fix: `mkdir -p /var/log/journal && systemd-tmpfiles --create --prefix /var/log/journal && systemctl restart systemd-journald`.
- Its span (the oldest entry, for example "it holds 41 days") and its size limit.
- **sshd's `LogLevel`.**
  - `INFO` logs accepted keys' fingerprints.
  - `VERBOSE` also logs the keys of failed attempts, which is recommended. The fix: a drop-in in `/etc/ssh/sshd_config.d/`.
- **That sudo's logging is not turned off.**

**Optional events** (per server, §11.3):

| Code | When | Default |
|---|---|---|
| **264** LINUX_ADMIN_LOGIN | An admin (root included) logs in over SSH or at the console, within the last hour (so a backlog after an outage doesn't page). Message: "root: SSH from 10.0.4.17 with key *Philipp, laptop* (philipp)" | **On** |
| **464** LINUX_LOGIN_FAILURES | `FailedLoginThreshold` (10) failures against **existing accounts** within 10 minutes from one source or for one user, a `not in sudoers`, or a lockout (pam_faillock). **Invalid users never** | **On** |

### 5.7 The journal

- **The newest 100 entries at priority warning or above** (emerg, alert, crit, err, warning):
  - from every unit except the login sources of §5.6;
  - from this boot and the one before.
- **Per entry:** the time, priority, unit, identifier, PID, the boot, and the message capped at 2 KB.
- **Within about 15 s:** the login stream's poll notices a new entry, and the section is collected again, as on Windows.
- **Optional:** *Event on new critical entries* writes **463** with the unit and message for emerg, alert and crit. It is off by default.

### 5.8 Changes

- **The shared store and dispatch** (§3.3) compares a section that arrives with a new hash, **with Linux differs**, and writes readable lines to `agent_changes`, for example:
  - "Service *zabbix-agent2* added (third-party, enabled, running)"
  - "sshd: password authentication turned on"
  - "Cron entry added for root: */5 * * * * /opt/sync/run.sh"
  - "deploy added to sudo"
  - "Kernel updated: 6.1.0-27-amd64 → 6.1.0-28-amd64"
  - "AppArmor turned off"
  - "The journal is no longer persistent"
- **Notable changes write 263**, as 253 does on Windows (W§4.9):
  - services, timers and cron entries added or removed, or with a new start state or class;
  - any change to remote access or sshd's settings;
  - local administrators and sudoers rules;
  - accounts that can log in with a password;
  - addresses, gateways or DNS servers;
  - the distribution's point release and the kernel;
  - disks or memory;
  - the firewall's state or policy, and AppArmor;
  - the logging check;
  - **key files' permissions, and registered keys added or removed** (§6.3).
- **Keys that aren't registered as active write 465** instead (§6.3). Logins and journal entries are logs, not changes.
- **Pending security updates write 462** when the server's switch is on (§9.2).
- **Kept:** 180 days.

---

## 6. SSH keys (R7, R8, R13)

### 6.1 What is read

- **sshd's own view.** `sshd -T` gives the effective `AuthorizedKeysFile` patterns (with `%h`, `%u`, `%%`), `AuthorizedKeysCommand`, `TrustedUserCAKeys`, `AuthorizedPrincipalsFile`, `PubkeyAuthentication`, `PermitRootLogin` and `StrictModes`.
  - **With `Match` blocks, the patterns are evaluated per account** (`sshd -T -C user=…,host=,addr=`), so a key file that only applies to some users isn't missed.
  - **Keys only `AuthorizedKeysCommand` knows** (LDAP, SSSD, a vault) can't be listed. The section says that they exist and which command provides them.
  - **The CA keys of `TrustedUserCAKeys`** are listed with their fingerprints, because a CA can let in anyone it signs for.
- **Every account in `/etc/passwd`**, not only those with a login shell: a key of an account with `nologin` still allows port forwarding.
- **Symlinks:**
  - **a symlink is followed only when the link and its target both belong to root** and no folder on the way is writable by others. That covers Proxmox VE's `/root/.ssh/authorized_keys` → `/etc/pve/priv/authorized_keys`, and an admin's own layouts;
  - **a symlink that belongs to a user is never followed:** it is reported as a problem, unread (§6.6).
- **Per file:**
  - account, path (and the target, when it is a followed symlink), exists or not;
  - owner, group, mode, size, modification time, SHA-256;
  - **cluster-wide** on Proxmox VE (`/etc/pve`);
  - **problems:** a user's symlink; owned by someone else; writable by group or others, or in such a folder (sshd's `StrictModes` refuses it, or worse, anyone can add keys); too large.
- **Per key:**
  - account, path, line, type (`ssh-ed25519`, `ecdsa-sha2-*`, `sk-*`, `ssh-rsa`, `ssh-dss`), bits;
  - **the fingerprint** (`SHA256:…`, as `ssh-keygen -l` prints it) and the comment;
  - **the options:** `from=`, `command=`, `restrict`, `no-*`, `permitopen=`, `expiry-time=`, `principals=`, `environment=`, `cert-authority`;
  - **notes:**
    - DSA: refused by OpenSSH;
    - RSA under 2048 bits: refused; under 3072: weak;
    - an `expiry-time` passed;
    - root's key without `from=`;
    - the same key twice in one file;
    - a CA key (`cert-authority`);
    - on Proxmox VE, a cluster node's key.
- **Lines that aren't keys are reported by their number only, never their text** (§6.6).
- **Caps:** 1 MiB and 1,000 keys per file, 20,000 keys per server. Beyond them, the section says so and stops.

### 6.2 How it is watched (R7)

- **One inotify instance** (P/Invoke, not a `FileSystemWatcher` per folder: each of those takes an instance, and the default limit is 128 per user) watches:
  - every folder that holds a key file, and every home's `.ssh`;
  - the home itself, while it has no `.ssh` yet;
  - `/etc/ssh` and `/etc/ssh/sshd_config.d/`;
  - `/etc` for `passwd` and `group`.
- **A change collects `ssh-keys` after 3 s of quiet**, and sends it when its hash changed. **A key added by hand reaches PNMT within about 10 seconds.**
- **A full rescan every 5 minutes anyway** catches what inotify can't: new homes, homes on NFS and Proxmox's cluster file (where another node's change raises no event here), a dropped event queue, a server above `max_user_watches`. The full update every 6 hours does the same.
- **A watch limit reached** is reported as a problem once, and the agent relies on the rescan.

### 6.3 Where each key is authorized, and when it was used

- **`ssh_key_authorizations`** holds every key found on every server: account, file, line, options, notes, and first seen. It is rebuilt from each `ssh-keys` section, keyed by server, account, file and fingerprint, so the first-seen and last-use times survive.
- **It is joined to the key manager by fingerprint, not by a reference.** A key found today and registered tomorrow is named everywhere, past logins included.
- **Last used:** an SSH login with a key (§5.6) sets `LastUsedAt` and `LastUsedFrom` on that key's row for that server and account. **Keys unused for 180 days** are one filter away.
- **Events:**

  | Code | When | Default |
  |---|---|---|
  | **465** LINUX_KEY_ADDED | **A key that isn't registered as active appears** in a key file. An unregistered key: "root: unregistered key ssh-ed25519 SHA256:Kq1f… 'jdoe@laptop' added to /root/.ssh/authorized_keys". A **retired** key: "root: retired key *Jan, old laptop* (jdoe) added again to …" | **On** |
  | **263** LINUX_CHANGED | A **registered, active** key added: "*Philipp, laptop* (philipp) added to deploy's authorized_keys". A key removed (outside PNMT, or by PNMT, which is said). A key file's permissions or owner changed. A new CA key | – |

- **A server's *SSH keys* tab** shows:
  - sshd's key settings and the CA keys;
  - per account and file: the keys with their **registered name and owner** (or *unregistered*), notes and last use;
  - the files' problems;
  - **whether PNMT may remove each key here**, from the agent's rights, with the reason when not;
  - for Admins: *Register…* and *Remove…*.

### 6.4 The SSH key manager (R13)

**PNMT keeps a register of the SSH keys it knows**, on ***Admin → SSH keys*** (§11.4). **It names and assigns keys; it never deploys them** (§2.2).

- **A registered key has:**
  - **the public key**: type, bits, fingerprint, and the comment it came with;
  - **a name**, which is required: "Philipp, laptop", "Backup server bkp-01";
  - **an owner:**
    - **a PNMT user**;
    - or, for keys of nobody in PNMT, **a free text**: a service, a contractor, a vendor's support;
  - **a kind**: *person* or *service*;
  - a note;
  - **a state**: *active* or *retired*, with who retired it and when.
- **Registering** is for Admins only. A registered key no longer raises 465, so nobody may vouch for a key by registering it themselves.
  - **Paste** one or more public keys, as in `id_ed25519.pub`, and give them a name, an owner and a kind.
    - The fingerprint is computed in `PNMTD.Lib` as you type.
    - **A key registered before is refused.**
    - **A pasted private key is refused with a warning** and never stored or logged.
  - **Or register keys found on servers:** from the *Unregistered* list or a server's tab, prefilled with the key's comment. Several keys can go to one owner at once.
- **What registering changes:**
  - **names everywhere:** the server tabs, the logins, 264's message and the change lines say "*Philipp, laptop* (philipp)" instead of a fingerprint;
  - **465 only for keys that aren't registered as active** (§6.3);
  - **nothing on any server.**
- **Retiring a key** (a lost laptop, a key replaced, a colleague leaving):
  - it is marked *retired*;
  - **every server and account where it is still authorized is flagged**, with *Remove from every server…* (§6.5);
  - if it ever appears again anywhere, 465 says so.
- **Keys and PNMT users:**
  - ***Admin → Users*** shows each user's number of keys, and on a user, their keys and where each is authorized.
  - **When a user is deactivated** while their keys are still authorized somewhere, the key manager shows a warning with ***Remove all from every server…***: offboarding in one step. Their keys are retired with it, after a confirmation.
  - **A deleted user's keys keep their names.** The owner becomes the text "former user jdoe".
  - **Every user sees their own keys** on their account page: the names, where each is authorized (server and account), and when it was last used. The page is read-only.
- **Unregistered keys** are listed by fingerprint, with every server and account where they are authorized, first seen and last used. That is the inventory's work list: register them, or remove them.
- **Deleting a registration** (Admin) makes the key unregistered again. It stays authorized wherever it is, and new appearances raise 465 again.
- **Audited:** `SshKeyRegistered`, `SshKeyChanged`, `SshKeyRetired`, `SshKeyDeleted`.

### 6.5 Removing a key (R8)

1. **An Admin chooses the keys and *Remove…*** from:
   - a server's *SSH keys* tab;
   - a key in the key manager ("from every server");
   - a retired key, or a deactivated user's keys (offboarding);
   - the *Unregistered* list.
2. **The dialog lists each server, account and file affected:**
   - where the server allows it, and where not (greyed, with the reason from its rights);
   - **warnings:**
     - the account would have no key left;
     - root would have no key left and password login is off ("this may lock everyone out of root");
     - the key was used in the last 24 hours;
     - **on Proxmox VE: the file is cluster-wide**, so the key goes from every node of the cluster, named.
   - **The Admin confirms by typing the number of keys.**
3. **PNMTD sends a signed `remove-ssh-key`** with the account, the path and the fingerprint.
   - **Servers that are offline get the removal when they connect** (decided in the review), as pending script versions are parsed then (W§7.1). It waits at most `Agents__KeyRemovalPendingDays` (7) days. The history shows when it was applied, or that it expired.
4. **The agent checks, in order, and refuses with the reason at the first failure:**
   1. `ssh_keys.remove` is true in `config.yaml`;
   2. the account is in `ssh_keys.users`, or that list is empty;
   3. the fingerprint is not in `ssh_keys.protect`, and **on Proxmox VE is not a cluster node's own key**;
   4. **the path is one the agent itself found for that account** through sshd's `AuthorizedKeysFile` (and a root symlink's target, §6.1). PNMT can't name any other path;
   5. the file is a regular file, **not a user's symlink**, owned by the account (or by root), in folders not writable by others;
   6. under `/etc`, the path is in `writable_paths`.
5. **It keeps a backup:** the file as it was goes to `state/key-backups/<account>/<time>-<file>` (root, 0600). Backups are kept 30 days, and at least the last 10 per file.
6. **It rewrites the file with its owner's rights.**
   - The agent starts itself as the file's owner (`setpriv --reuid --regid --init-groups pnmt-agent internal rewrite-keys …`).
   - That process removes **every line with that key** (the same key with other options too), writes a temporary file in the same folder, syncs it, keeps the mode, and renames it over the original.
   - For a root-owned file (Proxmox's cluster file included), the agent does this itself. L0 checks the rename on pmxcfs.
7. **It answers** `key-removal`: removed (the lines, the backup, the file's new hash), *not found* (the key is gone already), *refused* (the reason), or *failed*. Then it collects `ssh-keys` at once.
8. **PNMT records it:**
   - `ssh_key_removals` (permanent, it outlives the server);
   - the agent log;
   - `AuthEvents.SshKeyRemoved`;
   - the change line "*Jan, old laptop* (jdoe) removed from root's authorized_keys by PNMT (philipp)".

- **Restoring is local only:** `pnmt-agent keys restore <backup>` as root, after a confirmation. PNMT can't restore, because restoring is adding a key (§2.2).
- **Sessions that already use the key stay open.** Ending them is a non-goal; the dialog says so.

### 6.6 Safety

| Risk | Mitigation |
|---|---|
| **A compromised PNMT locks admins out** | Off by default. Allowed per account, and only by root. **Protected break-glass keys** can never be removed. Backups and a local restore. The warnings of §6.5 step 2. Admin only, audited |
| **A Proxmox cluster loses its own SSH** | The cluster nodes' root keys are never removed. L0 confirms how to tell them reliably (their `root@<node>` comment for the nodes in `/etc/pve/nodes/`, and each node's own public key). The cluster-wide warning |
| **A compromised PNMT lets itself in** | No message can add a key, an account or a sudo rule. The key manager never deploys (§2.2) |
| **A planted key passes as known** | Only Admins register keys, audited. A retired key raises 465 wherever it reappears |
| **A user makes root write elsewhere** (a symlinked `.ssh` or key file, a swapped folder) | Files are rewritten **with their owner's uid**, so a trick can only reach what that user could write anyway. Users' symlinks are refused. Paths come from sshd's patterns, never from PNMT |
| **A user makes root read elsewhere** (`authorized_keys` → `/etc/shadow`) | Users' symlinks are not followed. Files are read only when regular and owned by their account. **Lines that aren't keys are reported by number, never by text** |
| **A private key pasted into the key manager** | Recognised and refused before anything is stored or logged |
| **A huge or hostile file** | 1 MiB and 1,000 keys per file. The parser is a fixed grammar with length limits, in `PNMTD.Lib` |
| **Keys outside files** (`AuthorizedKeysCommand`, CA certificates) | Shown as such, with the CA keys' fingerprints. A CA is not removable through this feature: it is sshd's configuration, which a script can change where allowed |

### 6.7 Later: Windows servers' OpenSSH keys

The section, its records, the key manager and PNMT's keys tables are platform-neutral. Windows' OpenSSH reads `%ProgramData%\ssh\administrators_authorized_keys` and the users' `.ssh\authorized_keys`, so a Windows collector for the same section is a later, small step. The key manager would then cover both platforms.

---

## 7. Diagnostics (shared)

- **Unchanged.** `DiagnosticsFeature` moves to Core in L2. The Linux agent announces `diag.ping` and `diag.dns` like the Windows one.
- **After L1, assignments target Linux servers without a change** (W§5.5): their target is any row of `agents` that announced the check types.
- **On Linux:**
  - `Ping` uses raw sockets as root;
  - DNS without a server goes through the system's resolver (glibc's NSS: `/etc/hosts`, the search domains, systemd-resolved where it runs);
  - with a server, it goes through DnsClient, as on Windows.
- **Admin → Diagnostics** offers both kinds of servers. Its description reads "assigned to a server's agent".

---

## 8. Bash scripts (R2)

### 8.1 One library for both

- **The script library of W§7 gains a language:** `PowerShell` or `Bash`.
  - A script runs only on servers of its platform.
  - Versions, the history, redaction, the run dialog built from the parameters, and the Admin-only, audited runs are shared as they are.
- **A new bash script starts from a template** that follows §8.3's practices.

### 8.2 Parameters: a header PNMTD can read

```bash
#!/usr/bin/env bash
# pnmt:
#   synopsis: Rotate a site's logs and reload nginx
#   description: Compresses logs older than a day and deletes those older than keep_days.
#   parameters:
#     site:      { type: string, mandatory: true, set: [www, api, shop], help: "The site" }
#     keep_days: { type: int, default: 14, range: [1, 365] }
#     dry_run:   { type: bool }
#     api_token: { type: secret, help: "The CDN's token, to purge its cache" }
set -euo pipefail

echo "Rotating ${PNMT_SITE}, keeping ${PNMT_KEEP_DAYS} days"
[[ "${PNMT_DRY_RUN}" == true ]] && exit 0
```

- **The block:**
  - it starts with a line `# pnmt:` within the first 50 lines, and runs while lines start with `#`;
  - without the `# `, it is YAML (YamlDotNet; anchors and aliases refused; 64 KB at most);
  - **`synopsis`, `description`, and `parameters`**: a map from name to type, `mandatory`, `default`, `set`, `range` and `help`.
- **Names:** `[a-z][a-z0-9_]{0,39}`. **Types:**
  - `string`, `int` and `number`;
  - `bool`: a checkbox, like a PowerShell switch;
  - `secret`: a password field, like `[securestring]`.
- **It becomes the shared `ParsedScript`.** `ScriptArguments.Check` and the run dialog need no change.
- **In the script, each parameter is an environment variable `PNMT_<NAME>`**:
  - `bool` is `true` or `false`;
  - an optional parameter without a default and not given is unset, so scripts use `${PNMT_X:-}`.
- **A script without a header has no parameters.**
- **The header is read by `PNMTD.Lib`**, on PNMTD when a version is saved, and on the agent before a run.
  - **Bash scripts' parameters never wait for an agent**, unlike PowerShell's (W§7.1), which need Windows' parser.

### 8.3 Checks

- **On PNMTD, at save** (`PNMTD.Lib`, nothing run). These are warnings, except that **header errors block runs**:
  - the shebang is bash;
  - `set -euo pipefail` (or at least `-e` and `-u`);
  - no `eval`;
  - a `PNMT_*` used but not declared, or declared but not used;
  - `sudo` in the script (it already runs as the chosen account);
  - a secret parameter that is echoed.
- **On an agent** (`parse.v1`, the flow of W§7.1):
  - saving sends the text to a connected Linux agent, which runs **`bash -n`** (a syntax check that runs nothing) and **`shellcheck -f json`** when installed (the first 50 findings);
  - with none connected, the syntax check waits as *pending*, and the parameters are already there.
- **Before every run, the executing agent checks again:**
  - the header, with the same code;
  - the arguments (`ScriptArguments`);
  - `bash -n`.

### 8.4 Running

- **The agent prepares the run:**
  - **the script** goes into `/var/lib/pnmt-agent/runs/<id>/script.sh`. The folder is owned by the run-as account and readable only by it, for the run;
  - **the environment** goes into a separate root-only file: `PNMT_<NAME>=…` lines in systemd's `EnvironmentFile` syntax, quoted, with **secret arguments decrypted** with the agent's ECDH key (W§3.4, `AgentSecrets`).
- **It starts the script in a transient unit:**
  ```sh
  systemd-run --unit=pnmt-script-<id> --description="PNMT script <name>" --wait --pipe --collect --quiet \
    --uid=<account> --gid=<its group> -p EnvironmentFile=<env file> -p WorkingDirectory=<home or /> \
    -p RuntimeMaxSec=<timeout> -p TimeoutStopSec=10 -p PrivateTmp=yes /bin/bash <folder>/script.sh
  ```
  - **Arguments never reach a command line:** PID 1 reads the environment file. Only file names are on `systemd-run`'s command line.
  - **Its own unit:** it runs outside the agent's sandbox (§4.3), gets the account's supplementary groups, and at the timeout **its whole process tree** is stopped.
  - L0 checks that `--wait --pipe` passes the exit code and output through on every supported system, from Ubuntu 22.04's systemd 249 on.
- **Afterwards:**
  - stdout and stderr go to the history (1 MiB at most), with the exit code and duration, as on Windows;
  - the run folder and environment file are deleted;
  - the run is logged in the log file, in the journal ("PNMT ran the script *rotate-logs* (3f2a…) as deploy: exit 0"), and in PNMT's agent log.
- **Run as:**
  - **`root`**: the service itself, `RunAsKind.Service`;
  - **a local account by name**: `RunAsKind.User`. The dialog offers the server's accounts (§5.2).
  - **No passwords on Linux:** root starts anything as anyone. Windows' stored accounts, passwords typed per run and gMSAs (W§7.4) don't exist here. Secret arguments are still encrypted end to end.

### 8.5 Allowed only on the server (R10)

- **`scripts.enabled`, `scripts.run_as`, `scripts.allowed_hashes` and `scripts.max_timeout_minutes`** in `config.yaml` (§4.6) are Linux's counterpart of the Windows window's *Scripts from PNMT* (W§7.2).
  - Off by default.
  - **`root` must be named to be allowed.**
- **Only while enabled** does the agent announce `scripts.v1`. `parse.v1` is always announced: parsing runs nothing.
- **PNMT can't turn it on from the outside.**

---

## 9. Integration into PNMTD

### 9.1 A Linux server is a sensor

- **`SensorType.LINUX = 102`:**
  - interval 60 (the heartbeat), grace period 120;
  - **created only by a join**, never through the sensor form, which points to *Add server* as it does for `WINDOWS`.
- **`HeartbeatCheckTask`** includes it as it includes `WINDOWS`: revoked and uninstalled agents get no 460.
- **`SystemService.SensorTypes()`** and the docs page describe it. `DIAGNOSTICS`' description becomes platform-neutral.
- **Importance and the Notification Manager** (docs/ai-operations-plan.md §11) fit without change. Package one's analysis sees a Linux server as a sensor and its events, as it sees a Windows one.

### 9.2 Events and status codes

| Code | Name | When |
|---|---|---|
| **260** | LINUX_OK | A heartbeat. A stable message, so it collapses by the dedupe: "web-01: Debian GNU/Linux 12 (bookworm), kernel 6.1.0-28-amd64, agent 1.3.0" |
| **261** | LINUX_RESTARTED | The boot time moved. The message says whether the last shutdown was clean |
| **263** | LINUX_CHANGED | A notable change (§5.8), and registered keys added or removed (§6.3) |
| **264** | LINUX_ADMIN_LOGIN | Optional, on by default (§5.6) |
| **461** | LINUX_AGENT_PROBLEM | Clock skew; problems in 3 heartbeats in a row; an unsafe `config.yaml`; a key or protocol problem |
| **462** | LINUX_SECURITY_UPDATES | Optional, **off** by default (decided in the review): security updates pending for more than `SecurityUpdateDays` (7). "12 security updates pending since 9 days: openssl, …" |
| **463** | LINUX_CRITICAL_ENTRY | Optional, off by default (§5.7) |
| **464** | LINUX_LOGIN_FAILURES | Optional, on by default (§5.6) |
| **465** | LINUX_KEY_ADDED | Optional, on by default: a key that isn't registered as active (§6.3) |
| **460** | HEARTBEAT_MISSING | No heartbeat within Interval + GracePeriod (the existing code) |
| 252, 452 | DIAGNOSTICS_OK, _FAILED | Unchanged |

- **260–269 and 461–469 are free.** 270 and 470 are the ping sensor's. **Linux gets its own codes** rather than sharing Windows' 25x and 45x, as each device type has its block (UDP 23x, MikroTik 24x/44x, Windows 25x/45x). A notification rule or a report can then tell them apart.

### 9.3 Data model

**L1** (`GeneraliseAgents`): the renames of §3.3. It also adds:
- `agents.Platform`, `agent_join_tokens.Platform`, `agent_scripts.Language` (existing rows: Windows, PowerShell);
- `agents.MachineGuid` → `MachineId`.

**The Linux phases add:**

| Table or column | Content | Phase |
|---|---|---|
| `agents`: `Rights` (jsonb), `KeyAddedEvents` (default true), `SecurityUpdateEvents` (default false), `SecurityUpdateDays` (default 7), `KernelRelease`, `Distribution` | The effective rights from the hello; the Linux switches; the kernel and distribution for the fleet list | L3–L5 |
| `agent_logins`: `KeyFingerprint` (indexed), `TargetUser` | §5.6 | L4 |
| **`ssh_keys`** (the key manager) | Id, **Fingerprint** (unique), Type, Bits, PublicKey (the key's Base64), Comment, **Name**, **UserId** → users (set null), **Owner** (text), Kind (Person, Service), Note, **State** (Active, Retired), RetiredAt, RetiredByName, CreatedAt, CreatedByName, UpdatedAt, UpdatedByName | L5 |
| **`ssh_key_authorizations`** | Id, SensorId → cascade, Account, Path, Line, **Fingerprint** (indexed; joined to `ssh_keys` by value, so unregistered keys exist), Type, Bits, Comment, Options (jsonb), Notes, ClusterWide, FirstSeenAt, **LastUsedAt**, **LastUsedFrom**. Unique on (SensorId, Account, Path, Fingerprint) | L5 |
| **`ssh_key_removals`** | Id, SensorId → set null, ServerName, Account, Path, Fingerprint, KeyName, RequestedAt, RequestedBy, RequestedByName, Reason (from a server, a key, a retirement, an offboarding), State (Pending, Sent, Removed, NotFound, Refused, Failed, Expired), LinesRemoved, Backup, Message, AppliedAt. **Permanent; outlives its server**; kept 1 year | L6 |

Migrations: `GeneraliseAgents` (L1), `AddLinuxAgents` (L3–L4), `AddSshKeys` (L5), `AddSshKeyRemovals` (L6).

### 9.4 Server components

| Component | Where | Does |
|---|---|---|
| The agent infrastructure | `PNMTD/Agents/` (after L1) | Hub, join, keys, report, access, sections, changes (dispatch), agent log, retention, options, configuration, for both platforms. **Codes and messages per platform** |
| `AgentDownloads` | `PNMTD/Agents/` | The release's hashes (as `WindowsDownloads` today), **the download folder, fetching and checking the files, serving them, and the signed manifest** (§4.4) |
| `LinuxDiffers` | `PNMTD/Linux/` | The differs of §5.8 |
| `WindowsDiffers` | `PNMTD/Windows/` | Moved out of `WindowsChanges`, unchanged |
| **The SSH keys** | `PNMTD/SshKeys/` | `SshKeyAuthorizations` (from each section, last use from logins, 465 and 263 by registration), `SshKeyRegistry` (register, edit, retire, delete, users' keys), `SshKeyRemovals` (pending, sent when an agent connects, results, expiry) |
| Domain services | `Services/Domain/AgentServerService.cs`, `AgentScriptService.cs`, `SshKeyService.cs` | API v1 (§10). `UserService` gains the keys' side of deactivating and deleting a user (§6.4) |
| Controllers | `Api/V1/AgentControllers.cs`, `WindowsControllers.cs` (sections, run-as), `LinuxControllers.cs` (sections), `SshKeysController.cs` | §10 |
| `pnmtd agent …` | `Cli/AgentCommand.cs`, `Cli/AgentSimulator.cs` | `simulate --platform windows\|linux` (Linux: canned sections, logins with key fingerprints, an `ssh-keys` section whose keys can be removed, rights from `--allow-key-removal` and `--allow-scripts`), `tokens` |
| The seed | `Cli/DevSeedAgents.cs` | §12 |

### 9.5 Configuration

**The settings of W§8.5 become `Agents__*`, unchanged in meaning:**
- `Enabled`, `AllowHttp`;
- `HeartbeatSeconds`, `FullUpdateHours`, `TokenHours`;
- `MaxClockSkewSeconds`, `MaxMessageBytes`;
- `JoinRateLimitPerMinute`, `MessagesPerMinute`, `ReportsPerMinute`;
- `CheckOrigin`, `HelloTimeoutSeconds`;
- `AgentDownloadBaseUrl` (GitHub's releases, as today);
- `FailedLoginWindowMinutes`;
- `KeepLoginsDays`, `KeepChangesDays`, `KeepAgentLogDays`.

New:

| Setting | Default | Notes |
|---|---|---|
| `Agents__ServeDownloads` | `true` | The two download paths; off: 404, and the dialogs offer GitHub only |
| `Agents__DownloadFolder` | `/var/lib/pnmtd/downloads` | Filled from GitHub when empty; a mounted folder for a PNMT without internet, or for development |
| `Agents__DownloadsPerHour` | 20 | Per address |
| `Agents__KeyRemovalPendingDays` | 7 | How long a removal waits for an offline server |
| `Agents__KeepKeyRemovalsDays` | 365 | |

### 9.6 Deployment

- **Nothing new to open.** Linux agents use the same address, port, `/agent/v1/` paths and WebSocket as Windows agents (W§8.6). The downloads are under `/agent/` too, which the proxy notes already pass.
- **`docker-compose.yml` doesn't change.**
  - The download folder lives in the container and refills itself.
  - **A PNMT without internet** mounts a folder there with the release's files: `deploy/README.md` shows the lines.
- **`deploy/README.md`:**
  - its *Windows servers* section becomes *Servers with an agent*, with Windows and Linux, the downloads and the key manager;
  - the proxy notes say "agents".
- **The image doesn't grow.** `backup.sh` needs no change: the key manager is in the database.

### 9.7 Security

**W§8.7's threats and mitigations hold for both platforms.** Linux adds:

| Threat | Mitigation |
|---|---|
| **A compromised PNMT removes keys to lock admins out** | §6.6: off by default, per account, protected keys, Proxmox's cluster keys, backups, a local restore, the dialog's warnings |
| **A compromised PNMT gains access** | No message adds access, and the key manager never deploys (§2.2). Scripts only where root enabled them and named the accounts (§8.5) |
| **A planted key passes as registered** | Registering is Admin only and audited; users can't vouch for keys (§6.4) |
| **A non-root user grants rights** by editing `config.yaml` | A file not owned by root, or writable by others, grants nothing (§4.6) |
| **The root agent as a confused deputy** (symlinks, swapped folders in homes) | Reads and writes with the owner's rights. No users' symlinks. Paths only from sshd's configuration (§6.6) |
| **Secrets in what is collected** (sudo commands, crontab lines, shadow) | Never sent by default (`collect.*`). From shadow, only whether a password is set and locked. Unparseable key lines by number only |
| **A tampered binary** (in transit, or a wrong file in the download folder) | PNMTD serves only files that match the release's `.sha256`. The install line checks the hash before anything runs. `update` checks a manifest signed with the pinned key (§4.4) |
| **The download paths abused** (bandwidth) | Fixed names only, 20 per hour per address, and a switch to turn them off |
| **The agent's key copied** by root | Root owns the server anyway. *Revoke* ends the copy too (§4.5) |
| **Sensitive data in reports** (users, addresses, key comments) | *Access*, *Journal* and *SSH keys* need the User role, as *Access* and *Event log* do on Windows. The key manager is Admin only; a user sees only their own keys |
| **New anonymous endpoints** | **Two:** `GET /agent/v1/download` and `GET /agent/v1/download/{asset}`, in `EndpointAuditTests.AnonymousAllowlist`, next to the three agent paths |

---

## 10. API

**After L1**, the shared endpoints are W§9.2's under `/api/v1/agents/`. A `platform` filter is added where it is a list.

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/v1/agents/servers?platform=` | CanRead | The fleet of both platforms. For Linux: distribution, kernel, pending security updates, key removal allowed, unregistered keys |
| `GET /api/v1/agents/servers/{id}`, `…/changes` | CanRead | |
| `GET …/logins` | CanWrite | Adds the filters *kind* (Sudo, Su, Sftp), *key fingerprint* and *key owner* |
| `GET …/ssh-keys` | CanWrite | The server's keys with their registration, last use and removability |
| `GET …/log`, `POST …/send-log`, `POST …/revoke`, `POST …/rejoin-token` | Admin | |
| `POST …/collect` | CanWrite | |
| `PUT …/settings` | CanWrite | Adds `keyAddedEvents`, `securityUpdateEvents`, `securityUpdateDays` |
| `GET`, `POST`, `DELETE /api/v1/agents/join-tokens` | Admin | `POST { platform, hostId, name?, validHours? }` |
| `GET /api/v1/agents/agent-log` | Admin | |
| `GET /api/v1/agents/downloads`, `POST /api/v1/agents/downloads/fetch` | Admin | Both platforms: each file's PNMT and GitHub links, SHA-256 and state (here, being fetched, failed with the reason); **the Linux install and update lines**. `fetch` tries again |
| `/api/v1/agents/scripts…`, `/api/v1/agents/script-runs…` | Admin | With `language`. A run is refused on a server of the other platform |
| `GET /api/v1/windows/servers/{id}/sections/{s}`, `/api/v1/windows/run-as…` | As today | Unchanged |
| `GET /api/v1/linux/servers/{id}/sections/{s}` | CanRead; `access`, `journal`, `ssh-keys`: CanWrite | Typed by Linux's records |
| `GET /api/v1/ssh-keys?search=&owner=&kind=&state=&filter=` | Admin | **The registered keys**, each with where it is authorized (counts), last use and notes. `search` takes a name, fingerprint, comment, or a pasted public key |
| `GET /api/v1/ssh-keys/{id}` | Admin | One key: every authorization, recent logins with it, its history |
| `POST /api/v1/ssh-keys` | Admin | `{ publicKeys, name, userId?, owner?, kind, note? }`: one or more keys. Refuses duplicates and private keys. Audited |
| `PUT /api/v1/ssh-keys/{id}`, `POST …/retire`, `POST …/reactivate`, `DELETE …` | Admin | Audited |
| `GET /api/v1/ssh-keys/unregistered` | Admin | Keys found on servers and not registered, by fingerprint, with where they are authorized |
| `GET /api/v1/ssh-keys/users/{userId}` | Admin | A user's keys and where each is authorized: for *Admin → Users* and offboarding |
| `GET /api/v1/ssh-keys/mine` | Signed in | The caller's own keys, where each is authorized, last used. Read-only |
| `POST /api/v1/ssh-keys/remove` | Admin | `{ items: [{ sensorId, account, path, fingerprint }], reason, confirm }`. Answers per item: sent, pending (offline), refused (not allowed there). Audited (`SshKeyRemoved`) |
| `GET /api/v1/ssh-key-removals` | Admin | The history, filtered by server, key, owner and state |

- **`EndpointAuditTests`** gets every new and renamed endpoint in its matrix, and the two download paths in the anonymous allowlist.

---

## 11. Web UI

### 11.1 Admin → Servers

*Admin → Windows servers* becomes ***Admin → Servers*** (`/admin/servers/:tab(servers|tokens|download|log|scripts|run-as)`):

| Tab | Contents |
|---|---|
| **Servers** | The fleet of both platforms, with a platform column and filter, and the filters of W§10.1. Linux adds: *security updates pending*, *unregistered keys*, *retired keys still authorized*, *key removal allowed*. *Add server* |
| **Join tokens** | As today, with the platform |
| **Download** | PNMTD's version. **Windows:** the two executables from PNMT and from GitHub, and `Get-FileHash`. **Linux:** the install and update lines for x64 and arm64, from PNMT or GitHub, with `wget` or `curl`. **Each file's state** in PNMT's download folder, with *Fetch again* |
| **Agent log** | All agents |
| **Scripts** | PowerShell and bash, with a language filter and a template per language |
| **Run-as accounts** | Windows only, as today |

- ***Add server*** asks **the platform first**, then the host (preselected from a host's page), a name and the validity. It shows:
  - the token, in large type;
  - for Windows, today's content, with PNMT's download link added;
  - for Linux, **the one line** with its toggles, GitHub's link, and what `join` will do.

### 11.2 A Linux server's sensor page

- **Its tabs:** `overview`, `hardware`, `network`, `services`, `remote-access`, `access` (User), `journal` (User), `ssh-keys` (User), `diagnostics`, `changes`, `scripts` (Admin), `agent`. `journal` and `ssh-keys` are added to the route's `:tab(…)` list.
- **Shared components** move from `views/sensors/windows/` to `views/sensors/agent/`, and Windows keeps its own:

  | Shared (`agent/`) | Windows (`windows/`) | Linux (`linux/`) |
  |---|---|---|
  | `SectionBar`, the status strip, *Hardware*, *Network*, the logins list and its filters, *Diagnostics*, *Changes*, *Scripts*, *Agent* | The system panel, *Services* (signers), *Remote access*, *Event log*, the audit policy | The system panel (updates, kernel, AppArmor, firewall, admins, accounts), *Services* (with timers and cron), *Remote access*, *Journal*, the logging check, ***SSH keys*** |

- **Overview:**
  - host, distribution (with the *not a supported version* badge where it applies), kernel, **uptime**;
  - **updates pending**, with the security ones, and *reboot pending*;
  - NTP and the skew;
  - the remote access summary;
  - **the last admin logins and elevations, with the keys' names, and the failed logins of 24 hours** (scanners counted apart);
  - **the SSH keys at a glance:** how many, unregistered ones, retired ones still here, weak ones, unused for 180 days;
  - the diagnostics, the last 5 changes, the agent's state and its problems.

### 11.3 The Agent tab

As on Windows (W§10.2):
- the connection, version and fingerprints;
- the problems and the agent log;
- *Re-join*, *Revoke*, *Send log*;
- **the switches:** *Event on admin logins* (on), *Event on failed-login bursts* (on) with its threshold, *Event on new critical entries* (off). Linux adds *Event on unregistered keys* (on) and *Event on pending security updates* (off) with its days.
- **The rights the server gives PNMT**, from `config.yaml`, with what each would need.
- **When the agent is older than PNMTD:** the line `pnmt-agent update`.

### 11.4 Admin → SSH keys: the key manager (R13)

**A page of its own** in the admin navigation (*SSH keys*, a key icon, Admin only), at `/admin/ssh-keys/:tab(keys|unregistered|removals)`.

- **A strip on top** with figures that open the matching filter:
  - registered keys;
  - **unregistered keys** found on servers;
  - **retired keys still authorized**;
  - **keys of deactivated users still authorized**, with *Remove all…*;
  - weak keys.

| Tab | Contents |
|---|---|
| **Keys** | The register. **Columns:** name, owner (a user, or the text, and *former user* where it applies), kind, type and bits, fingerprint (short, with copy), **authorized** (servers and accounts, counted), **last used** (when, on which server, from where), state, notes (weak, unused for 180 days, owner deactivated). **Filters:** owner, kind, state, notes. **Search:** name, fingerprint, comment, or a pasted public key. ***Add keys…***. **Per row:** *Edit*, *Retire* or *Reactivate*, *Remove from servers…*, *Delete* |
| **Unregistered** | Keys found on servers that aren't registered, grouped by fingerprint. **Columns:** comments seen, type and bits, **where** (servers and accounts), first seen, last used, notes. ***Register…*** (one, or several to one owner), ***Remove…*** |
| **Removals** | Every removal: when, by whom, why (a server, a key, a retirement, an offboarding), the server, account and file, the key, the state (pending, removed, not found, refused with the reason, failed, expired), the backup's name |

- **A key's drawer:**
  - the public key (with copy), fingerprint, type, bits, the comments seen on servers;
  - the owner, kind and note;
  - **where it is authorized:** server, account, file, its options (`from=`, `command=`, …), last use there, and whether PNMT may remove it there (or why not);
  - **the recent logins with it** (server, account, from where, when);
  - **its history:** registered, edited, retired, added or removed on servers.
- ***Add keys…*:**
  - a text field for one or more public key lines;
  - for each line, a row with its fingerprint (computed as you type), type, bits, a weak note, and **a name, prefilled with the comment**;
  - for all of them: **the owner** (a PNMT user from a list, or a text), the kind and a note;
  - **duplicates and private keys are marked and refused.**
- **Elsewhere:**
  - ***Admin → Users*:** an *SSH keys* column; on a user, their keys with where each is authorized, and *Remove all from every server…*. **Deactivating a user who has keys** asks whether to retire their keys and remove them now (§6.4).
  - **The account page** (every user): *My SSH keys*, read-only (§6.4).
  - **A server's *SSH keys* tab** (§6.3), with *Register…* and *Remove…* for Admins.

---

## 12. Build, release and tooling

- **New projects in `PNMTD.slnx`:** `PNMTD.Sensor.Core` and `PNMTD.Sensor.Linux`.
  - Both are `net10.0`, so `dotnet build` builds them anywhere.
  - CI's existing *Build* step builds them with the solution, and nothing else changes in `dotnet.yml`.
- **`tools/agent.sh`** gets `PLATFORM=windows|linux|all` (default all).
  - Linux publishes with `-p:AgentRuntime=linux-x64` and `linux-arm64`, single-file, self-contained, compressed, locked.
  - The outputs are `pnmt-agent-linux-x64` and `pnmt-agent-linux-arm64`, with their `.sha256` (`sha256sum`'s format, so the install line's `-c` reads it).
- **`build-sensor-linux.sh`** builds both into `dist/`, as `build-sensor-windows.sh` does. `Agents__DownloadFolder=dist` lets a development PNMTD serve them.
- **`push.yml`'s `agent_release`** publishes all eight assets on a version tag. **The `Dockerfile` is unchanged.**
- **`tools/linux-lab.sh`**, like `tools/chr-lab.sh`, with Podman by default:

  | Command | Does |
  |---|---|
  | `start <name> [debian-12\|debian-13\|ubuntu-22.04\|ubuntu-24.04\|ubuntu-26.04] [--arch amd64\|arm64]` | A container with systemd, OpenSSH, sudo and a test user, as `pnmtd-linux-<name>` |
  | `install <name>` | Copies the binary from `dist/` into it, or downloads it from the development PNMTD as the line does |
  | `join <name> <token>` | Joins the development PNMTD (`host.containers.internal`, `Agents__AllowHttp`) |
  | `ssh <name> [user]`, `shell <name>` | Logs in over SSH, which the logins tab then shows; or a root shell |
  | `stop <name>\|--all`, `list` | |

  - **arm64 runs natively on Apple Silicon;** amd64 is emulated.
  - Containers have no real DMI, SMART or Secure Boot, so **one real machine or VM** covers the hardware collectors in L0 and before a release. **Proxmox VE is checked on a real (or nested) installation**, with a two-node cluster for its shared key file.
- **`pnmtd dev seed`** adds two Linux servers:
  - *web-01*, Debian 12, and *pve-01*, Proxmox VE 9;
  - every section, a week of SSH logins with keys, sudo and scanner noise;
  - **the key manager:**
    - registered keys of the seeded users ("Philipp, laptop", "Philipp, workstation");
    - a service key ("Backup server bkp-01");
    - a retired key still authorized on *web-01*;
    - an unregistered weak key;
    - a key unused for 200 days;
  - a removal done, one refused, and one pending;
  - changes, and *branch connectivity* assigned to *web-01*.

---

## 13. Phases

| Phase | Content | Size |
|---|---|---|
| **L0** | **Verify**, with no product code, and kept short. The results go into this document. Ideally done in one session with Windows' W0, before 1.3.0.<ul><li>**`tools/linux-lab.sh`**, one real Debian machine or VM, and a Proxmox VE cluster of two nodes;</li><li>**the binary** on Debian 12 and 13 and Ubuntu 22.04, 24.04 and 26.04, amd64 and arm64: size, start time, nothing extracted, no libicu;</li><li>**the unit** with each sandboxing line against every collector, and the watchdog;</li><li>**the journal:** persistent by default; **the identifiers and lines** of sshd, sshd-session and sshd-auth for each system's OpenSSH, sudo, su, login and logind; **fixtures captured for the parser tests**;</li><li>`sshd -T` with `Match`, and Ubuntu's `ssh.socket`; `ip -j`; `ss -H -tulpn`; `systemctl` with `--output=json` from systemd 249 on; `nft -j`; `apt-get -s`'s security origin on Debian and Ubuntu; snaps' publishers;</li><li>**Proxmox VE:** the root key symlink, renaming on pmxcfs, and how to tell the cluster nodes' keys;</li><li>**inotify** on homes and `.ssh` folders, and its limits;</li><li>**`systemd-run --wait --pipe`**: exit codes and output;</li><li>**the one line** with `wget` and `curl`, from PNMT and from GitHub, as root and with `sudo`, on a minimal Debian</li></ul> | small |
| **L1** | **PNMTD's agent infrastructure generalised**, with no change in behaviour (§3.3):<ul><li>the renames and the migration;</li><li>the platform on agents, tokens and scripts;</li><li>`SensorType.LINUX` and its codes;</li><li>the protocol's platform fields and generic names;</li><li>*Admin → Servers* and *Add server* with a platform;</li><li>`views/sensors/agent/`;</li><li>`pnmtd agent simulate`;</li><li>`Agents__*`;</li><li>the note in docs/windows-sensor-plan.md.</li></ul>**All existing tests and `e2e/windows.spec.ts` green.** Before the 1.3.0 tag | medium-large |
| **L2** | **`PNMTD.Sensor.Core`** extracted from the Windows agent behind `IAgentPlatform`, by moving code and changing none. The Windows agent builds on macOS and CI, and `tools/agent.sh` gives executables of the same size | medium |
| **L3** | **The Linux agent, first version:**<ul><li>the project and the command line;</li><li>`join` with the unit, the template `config.yaml`, `status`, `uninstall`;</li><li>the file keys;</li><li>**`config.yaml`** (read, validated, reloaded, the rights in the hello);</li><li>the version check for the supported distributions;</li><li>heartbeats;</li><li>the sections `system`, `hardware`, `network`;</li><li>`tools/agent.sh` and `build-sensor-linux.sh`.</li></ul>**PNMTD:**<ul><li>`LINUX` heartbeats → 260, 261, 461, 460;</li><li>**the downloads:** the folder, fetching and checking, the two paths, the signed manifest, and `pnmt-agent update`;</li><li>the Linux tab of *Add server* with the line;</li><li>the overview, *Hardware*, *Network* and *Agent* tabs;</li><li>the simulator's Linux sections</li></ul> | large |
| **L4** | **The rest of R1:**<ul><li>services, timers, cron and snaps;</li><li>remote access, Proxmox's included;</li><li>**logins and elevations** (the journal stream, cursors, aggregation, key fingerprints, the logging check, 264, 464) **with the parsers' tests**;</li><li>the journal and 463;</li><li>the Linux differs and 263; 462;</li><li>the remaining tabs;</li><li>**diagnostics checked on Linux**</li></ul> | large |
| **L5** | **SSH keys and the key manager (R7, R13):**<ul><li>the section, Proxmox's cluster file and the inotify watch;</li><li>`ssh_key_authorizations` and last use;</li><li>**`ssh_keys`**: registering (pasting, from servers), names, owners, retiring, deleting;</li><li>465 and 263 by registration;</li><li>**_Admin → SSH keys_** (keys, unregistered, the drawer, *Add keys…*);</li><li>the server's *SSH keys* tab;</li><li>users' keys on *Admin → Users* and *My SSH keys* on the account page</li></ul> | large |
| **L6** | **Removing keys (R8):**<ul><li>the rights and checks on the agent, protected keys and Proxmox's cluster keys;</li><li>the owner-rights rewrite, backups, `keys restore`;</li><li>the *Remove…* dialog from every entry point;</li><li>**pending removals for offline servers**;</li><li>offboarding a deactivated user;</li><li>the *Removals* history and the audit</li></ul> | medium |
| **L7** | **Bash scripts (R2):**<ul><li>the header and its parser in `PNMTD.Lib`;</li><li>the language in the library, the template, PNMTD's checks;</li><li>the agent's `bash -n` and shellcheck;</li><li>running with `systemd-run` as root or an account, with the environment file and secrets;</li><li>`config.yaml`'s scripts block</li></ul> | medium |
| **L8** | **Release and polish:**<ul><li>the tag job with the Linux assets;</li><li>the *Download* tab's lines and states;</li><li>`deploy/README.md` (the downloads, an offline PNMT, the key manager);</li><li>the seed;</li><li>`e2e/linux.spec.ts` (a Linux server's pages and *Admin → SSH keys*, as for Windows);</li><li>the limits tuned with L0's numbers;</li><li>the checklist (§14);</li><li>this plan's *As built*</li></ul> | medium |

- **Each phase:**
  - is one or more commits on master;
  - has `./run-tests.sh` green before the next starts;
  - is noted in an *As built* section, as in docs/windows-sensor-plan.md §16.
- **L1 and L2 can come before 1.3.0 on their own.** The Linux agent (L3–L8) can follow in the same release or the next.

---

## 14. Testing: few, and only for PNMTD

**As decided for Windows (W§13): automated tests only for PNMTD's side**, in `PNMTD.Tests`, alongside the existing ones. **One exception, decided in the review: the agent's journal-line parsers.**

| What | How |
|---|---|
| **L1 changes nothing** | The existing `Windows/`, `Diagnostics/` and endpoint tests, renamed where their names change, and `e2e/windows.spec.ts`, all green |
| **Join** | A Linux token makes a `LINUX` sensor; a token of the other platform is refused; re-join |
| **Downloads** | A file in the folder is served only when it matches its `.sha256`; unknown names are 404; the rate limit; turned off is 404; the manifest verifies with PNMTD's agent key |
| **Reports** | A Linux heartbeat → 260, a new boot time → 261, problems → 461; the Linux sections stored once per hash and served typed; the viewer gets 403 for `access`, `journal` and `ssh-keys` |
| **Logins** | Stored once (the dedupe by cursor hash); a key login sets the key's last use; an admin's SSH login → 264 with the key's registered name; failures against an account → 464; **invalid users never 464** |
| **The key manager** | Registering by paste (the fingerprint equals `ssh-keygen -l`'s for fixtures); duplicates and private keys refused; an unregistered key → 465, a registered one → 263, a retired one → 465; registering later names past logins; a deleted user's keys become "former user"; `mine` answers only the caller's keys; non-Admins get 403 for the register |
| **Removals** | Through the simulator: refused without `keys.remove.v1`; sent, applied, recorded and audited; **pending for an offline server, sent when it connects**, expired after its days; a user's offboarding sends one removal per server and account |
| **Scripts** | A bash header → its parameters, and arguments refused against them; a run refused on a Windows server; a run with a secret arriving decrypted at the simulator and stored nowhere |
| **Shared parsers** (`PNMTD.Lib`) | `authorized_keys` lines with options and odd quoting; fingerprints; private keys recognised; the bash header's errors |
| **The agent's journal-line parsers** | **Table-driven tests** over the lines captured in L0 from Debian 12 and 13 and Ubuntu 22.04, 24.04 and 26.04 (`PNMTD.Tests/Fixtures/Linux/journal/`): sshd's accepted, failed and invalid-user lines with and without keys and certificates, SFTP, sudo, su, login, logind. `PNMTD.Tests` references `PNMTD.Sensor.Linux` (net10.0) for these pure functions only |
| **Endpoints** | `EndpointAuditTests`: every new and renamed endpoint in the matrix, and the two download paths in the anonymous allowlist |

- **The agent is checked by hand, per release**, in the lab and on one real machine, on Debian 12 and 13:
  1. The line from PNMT on Debian 12 and 13 (x64 and arm64): the hash checks; `join` exits 0; both fingerprints match the agent log. The GitHub variant works too.
  2. Within a minute: 260, and every tab filled without errors in the sections.
  3. An SSH login as root with a registered key: in *Access* within 15 seconds, with the key's name, and 264. That key's *last used* is set.
  4. Ten wrong passwords for an existing account: 464. A hundred tries for invalid users: one aggregate, no event.
  5. `sudo -i` as the test user: an elevation in *Access*.
  6. An unregistered key appended to `~deploy/.ssh/authorized_keys` by hand: 465 within about 10 seconds, and the key in *Unregistered*. Registering it: the name shows everywhere.
  7. **Removing it with `remove: false`:** refused, with the reason. With `remove: true`: removed, a backup, the change line. `keys restore` puts it back. Retiring it: flagged where it is, and 465 if it comes back.
  8. A protected key: refused. A user's symlinked `authorized_keys`: refused, and not read through.
  9. With the agent stopped, a removal: *pending*. After starting it: removed.
  10. A bash script as root and as `deploy` with a secret argument: output and exit code in the history; a timeout ends its children.
  11. `kill -9` the agent: systemd starts it again; the agent log says *crashed*. A reboot: 261.
  12. Network off for 5 minutes: the logins of that time arrive after.
  13. `pnmt-agent update` after PNMTD's update; `uninstall` keeps `config.yaml`, `--purge` doesn't; PNMT shows *uninstalled* and no 460.
- **Proxmox VE and Ubuntu LTS** are checked in L0, and again when a release changes a collector, parser or call that differs there (§4.8).
- **Not planned:** other tests of the agent's code, a Linux CI runner for the agent, a VM matrix, load tests.

---

## 15. Risks

| Risk | Mitigation |
|---|---|
| **L1 and L2 break the Windows side**, which hasn't run on real Windows yet (W0) | Both change no behaviour, and each is a commit of its own. L1 is covered by PNMTD's existing tests and the Windows e2e test. L2 only moves code, and the executable's size and the simulator are checked. W0 and L0 in one session before 1.3.0 |
| **Log lines differ between OpenSSH versions and distributions** (sshd, sshd-session, sshd-auth; Debian and Ubuntu) | Fixtures from every supported system in L0, and tests over them (§14). Unmatched lines are counted and reported, never silently lost. The patterns live in one place |
| **Ubuntu and Proxmox VE drift** between the checks | They differ in few places, all listed (§4.8), and each falls back to *not available* rather than failing. Problems from the field arrive in PNMT by themselves (W§3.8) |
| **A Proxmox cluster loses its SSH** through a removal | The nodes' own keys are protected. The cluster-wide warning names the nodes. Removal is off by default (§6.6) |
| **PNMTD can't reach GitHub** | The download folder, filled by hand. GitHub's link for admins who can reach it. `scp` as the last resort (§4.4) |
| **Internet-facing SSH** produces thousands of failures a day | Aggregated per source per 10 minutes. Invalid users never page. Capped batches. The overview counts the scanners apart |
| **A key removal locks someone out** | §6.6: off by default, protected keys, warnings, backups, a local restore |
| **The key register goes stale** (keys replaced, people leave) | The *Unregistered* list, *unused for 180 days*, retired keys still authorized, and deactivated users' keys are all on the page's strip. 465 for every key nobody registered |
| **A root agent in users' homes** | Owner-rights reads and writes, no users' symlinks, paths from sshd only (§6.6) |
| **inotify's limits, NFS homes, pmxcfs** | One instance; the 5-minute rescan; a problem when a limit is reached |
| **The firewall's verdict per port is wrong** on complex rule sets | *Unknown* rather than a guess (§5.5) |
| **Other configurations** (no persistent journal, Dropbear instead of OpenSSH, rsyslog only) | The logging check, with fixes. Dropbear uses the same `authorized_keys` format, but its logs aren't parsed (noted as *not available*) |
| **`ProtectSystem=full` blocks a feature** | L0 checks each line. Key files under `/etc` are refused unless named in `writable_paths` (§4.3) |
| **Few automated tests** | PNMTD's security-relevant paths and the agent's parsers are tested; the lab and the checklist cover the rest; problems from the field arrive in PNMT by themselves (W§3.8) |

---

## 16. Decisions and open questions

### 16.1 Decisions

**From the plan:**

1. **A project of its own, `PNMTD.Sensor.Linux`, and a shared `PNMTD.Sensor.Core`** moved out of the Windows agent unchanged (§3.2).
2. **`config.yaml` is root's only.** A broken or unsafe file grants nothing; PNMT can't write it (§4.6).
3. **PNMT can remove keys but never add or deploy them.** Restoring is local (§6.5).
4. **Files in homes are read and rewritten with their owner's rights**; only root's symlinks are followed (§6.1, §6.6).
5. **Bash parameters come from a YAML header**, read by `PNMTD.Lib`. Arguments are passed as environment variables from a file (§8.2, §8.4).
6. **Scripts run in transient systemd units**, outside the agent's sandbox (§8.4).
7. **Linux has event codes of its own**, 26x and 46x (§9.2).
8. **The binary is named `pnmt-agent`**, as the idea names its folder (`/etc/pnmt-agent`). Windows keeps `pnmt-sensor.exe`.
9. **PNMTD fetches the release's files from GitHub and keeps them**, rather than carrying them in the image. A folder filled by hand covers a PNMT without internet (§4.4).
10. **Keys are registered by Admins only**, owned by a PNMT user or a free-text owner, and joined to what servers report by fingerprint (§6.4).

**From the review of revision 1 (2026-11-14):**

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Generalise PNMTD's side now | **Yes:** `agent_*`, `/api/v1/agents/…`, `Agents__*`, *Admin → Servers*, before 1.3.0 | §3.3 |
| 2 | Distributions | **Built for Debian 12 and 13; working on Proxmox VE and Ubuntu LTS too** | §4.8 |
| 3 | Where the binary comes from | **Downloaded from PNMTD, with the GitHub link too** | §4.4 |
| 4 | Labels for keys | **An SSH key manager:** every key named and assigned to a user, on an admin page | §6.4, §11.4 |
| 5 | Removals for offline servers | **Yes:** pending until the server connects, at most 7 days | §6.5 |
| 6 | Tests of the agent's journal-line parsers | **Yes:** table-driven, over L0's fixtures | §14 |
| 7 | sudo commands and crontab lines | **Only the program by default; the full line with `collect.*`** | §5.4, §5.6 |
| 8 | 462, pending security updates | **Yes**, off by default per server | §9.2 |

### 16.2 Open questions

None.

---

## Appendix: configuration and file changes

| Area | New | Changed |
|---|---|---|
| Protocol | `PNMTD.Lib/Agent/AgentLinux.cs` (records), `SshKeys.cs` (line parser, fingerprints, private keys), `BashScriptHeader.cs`, `DownloadManifest` | `AgentMessages.cs` (platform, rights, removal), `AgentSections.cs` (generic names, `LoginRecord`, `LogonKind`, section lists), `AgentProtocol.cs` (types, capabilities, download paths), `AgentScripts.cs` (`RunAsKind.User`, language) |
| Agent core | `PNMTD.Sensor.Core/` (moved from the Windows agent; `IAgentPlatform`) | – |
| Windows agent | `Platform/WindowsPlatform.cs` | `PNMTD.Sensor.Windows/` (what moved to Core is removed; it uses Core) |
| Linux agent | `PNMTD.Sensor.Linux/`: `Program.cs` (commands), `Platform/` (paths, keys, systemd, heartbeat, os-release, Proxmox and Ubuntu specifics), `Collectors/`, `Access/JournalStream.cs`, `Access/JournalLines.cs` (the tested parsers), `Keys/` (collector, inotify, rewrite, backups), `Rights/ConfigYaml.cs`, `Scripts/Bash.cs`, `Update/` (manifest, download), `pnmt-agent.service` (template) | `PNMTD.slnx`, `Directory.Packages.props` (`Microsoft.Extensions.Hosting.Systemd`, `YamlDotNet`) |
| Server (L1) | `PNMTD/Agents/*` (moved from `PNMTD/Windows/`), `Windows/WindowsDiffers.cs`, migration `GeneraliseAgents` | Everything that named the Windows agent infrastructure (§3.3), `SensorType` (102), `PNMTStatusCodes` (26x, 46x), `HeartbeatCheckTask`, `SensorService`, `SystemService`, `AuthEvents`, `Program.cs` (`Agents` options, the download paths) |
| Server (Linux) | `PNMTD/Agents/AgentDownloads.cs` (serving), `PNMTD/Linux/LinuxDiffers.cs`, `PNMTD/SshKeys/` (authorizations, registry, removals), `Services/Domain/SshKeyService.cs`, `Api/V1/LinuxControllers.cs`, `Api/V1/SshKeysController.cs`, `Models/Db/SshKeyEntities.cs`, migrations `AddLinuxAgents`, `AddSshKeys`, `AddSshKeyRemovals` | `AgentHub` (pending removals on connect), `AgentAccessService` (key use), `AgentScriptService` (bash), `UserService` (keys on deactivating and deleting), `DiagnosticsSetService` (wording), `EndpointAuditTests` |
| Web UI | `views/admin/servers/` (from `windows/`), **`views/admin/sshkeys/`** (`SshKeysView.vue`, `KeysTab.vue`, `UnregisteredTab.vue`, `RemovalsTab.vue`, `KeyDrawer.vue`, `AddKeysDialog.vue`, `RemoveKeysDialog.vue`), `views/sensors/agent/`, `views/sensors/linux/`, *My SSH keys* in `views/account/` | `router/index.ts`, `layouts/navigation.ts` (*Servers*, *SSH keys*), `SensorView.vue`, `SensorFormView.vue`, `UsersView.vue` (keys, offboarding), the host page (*Add server*) |
| Build and release | `PNMTD.Sensor.Linux`'s publishing in `tools/agent.sh`, `build-sensor-linux.sh`, `tools/linux-lab.sh` and `tools/linux-lab/` | `.github/workflows/push.yml` (eight assets), `deploy/README.md`, `deploy/.env.example` (`Agents__*`). **The `Dockerfile` and `docker-compose.yml` are unchanged** |
| Tests | `PNMTD.Tests/Agents/`, `PNMTD.Tests/Linux/` (incl. the journal-line tests), `PNMTD.Tests/SshKeys/`, `PNMTD.Tests/Fixtures/Linux/`, `e2e/linux.spec.ts` | `PNMTD.Tests/Windows/` (renamed parts), `EndpointAuditTests` |
| Docs | this plan | `docs/windows-sensor-plan.md` (a note on L1's renames) |
