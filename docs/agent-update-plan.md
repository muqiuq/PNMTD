# Plan: the agents' versions managed in PNMT, and updates that only the machine starts

| | |
|---|---|
| Status | **U1 and U2 built** (§15.1, §15.2); U3 and U4 to build. No open questions |
| Date | 2026-11-15 |
| Revision | 3: **checked against 1.7.0** (the access watch, the sensor's notes on the overviews): nothing this plan builds on changed. `AgentDownloads`, both updaters, `AgentsController`, `sensor.yml` and `agent_settings` are as they were; the access watch only adds logins (`LogonKind`) and reviews. The target moves to 1.8.0, and the versions in the examples with it.<br>2: **the review's answers** (§14):<ul><li>one chosen version per platform;</li><li>**uploads for admins**, no setting;</li><li>**versions newer than PNMT allowed, with a warning**;</li><li>the automatic rollback on both platforms;</li><li>**no guard against scripts:** a script PNMT runs may update the agent (§7.3)</li></ul>1: the first draft |
| Scope | **Which agent version PNMT hands out, and how the agents update.** The download folder keeps several versions; a new tab, *Admin → Servers → Versions*, lists, fetches, uploads, downloads and deletes them and **chooses the version each platform's agents get**. The signed manifest is per platform. **`pnmt-sensor.exe update` downloads from PNMT**, as `pnmt-agent update` does. **PNMT has no way to start an update**, apart from a script the machine lets it run (§7.3) |
| Goals | <ol><li>**PNMT's admins manage the agents' binaries in the web UI:** GitHub's releases, fetched into PNMT, downloaded, deleted, and uploaded where PNMT has no internet (R1).</li><li>**They choose the version each platform's agents get**, PNMT's own by default (R2).</li><li>**The Windows agent updates itself from PNMT** as the Linux agent does: the signed manifest, the hash, the version check, the hand-over (R3).</li><li>**An update starts only on the machine:** a person there, or a schedule the machine's admin set up there. PNMT has no button, message or setting that starts one (R4).</li></ol> |
| Not in scope | **Automatic updates built into the agents.** A version per server, group or ring (§12). Signing the releases with a key of the project's (§12). The agents' own version number, apart from PNMTD's (docs/agent-feature-level-plan.md §8) |
| Depends on | The download folder and the manifest (docs/linux-sensor-plan.md §4.4, §9.4, §9.5, §9.7). `pnmt-agent update` as built (`PNMTD.Sensor.Linux/Update/Updater.cs`). Windows' update as built (docs/windows-sensor-plan.md §10.4). The scripts (docs/linux-sensor-plan.md §8, docs/windows-sensor-plan.md §7) |
| Target | 1.8.0. 1.7.0 (the access watch) is released, and changed nothing this plan builds on (revision 3) |

**Words.** ***The machine*** is the server or PC an agent runs on, a *server* in PNMT's UI. ***PNMT*** is PNMTD and its web UI. ***The chosen version*** is the version a platform's agents get (§4).

---

## 1. Today

- **One version, PNMT's own.** `AgentDownloads` serves only the files of `sensor-{ServerInfo.Version}`:
  - on the first request it fetches them from GitHub into `{Agents__DownloadFolder}/sensor-{version}/`, checked against the release's `.sha256`;
  - **after each fetch it deletes every other `sensor-*` folder** (`DeleteOlderReleases`);
  - for a PNMT without internet, an admin mounts a folder holding the files.
- **The web UI** (*Admin → Servers → Download*) shows that one version: the PNMT and GitHub links, the hashes, each file's state, *Fetch again*. No other version can be seen, fetched or chosen.
- **Linux: `pnmt-agent update [<file>] [--force]`**, run by root on the machine:
  1. the signed manifest `GET /agent/v1/download`, checked against the key pinned at the join;
  2. this architecture's file, only when it is newer (or with `--force`);
  3. its size and SHA-256 against the manifest, and `version` must answer as a PNMT agent;
  4. the new binary runs `update-install`: stop, replace `/usr/local/sbin/pnmt-agent` atomically, write the unit, start.

  The Linux overview and the *Agent* tab show the line when the agent is outdated.
- **Windows: `pnmt-sensor.exe update`, or *Update* in the window.**
  - **It installs the executable that runs**, over `C:\Program Files\PNMT Sensor\pnmt-sensor.exe`: stop, copy, `sc config`, start.
  - **It downloads nothing:** someone gets the new exe from the *Download* tab first.
  - The window offers *Update* only when the exe that runs is newer than the installed one and is a different file.
  - PNMT shows *outdated* for Windows agents, but no line to run (`AgentTab.vue` shows it for Linux only, `WindowsOverviewTab.vue` not at all).
- ***Outdated*** is `AgentServerService.IsOutdated`: the agent's version is lower than PNMTD's.
- **Can PNMT start an update today?** Not directly. But **a script PNMT runs on a machine could.** It runs with what config.yaml (Linux) or the script policy (Windows) allows, root or SYSTEM included, and on Linux in a transient unit of its own outside the agent's service (`systemd-run --unit=pnmt-script-<id>`), so `pnmt-agent update` would work there. **That stays as it is** (§7.3).

## 2. Requirements

| # | Requirement | Where |
|---|---|---|
| R1 | The agents' binaries are managed in PNMT's web UI: listed, fetched, downloaded, deleted, uploaded | §3, §8.1 |
| R2 | PNMT sets the version the agents get | §4, §5 |
| R3 | The Windows agent updates from PNMT, as the Linux agent does | §6 |
| R4 | **Only the machine ever starts an update, never PNMT.** A script PNMT runs there with the machine's permission is accepted | §7 |
| R5 | Agents in the field keep working: Linux 1.3 to 1.7 get the chosen version from their `update`; Windows 1.7 and older update once the old way | §5.3, §10 |

## 3. The versions in PNMT

### 3.1 The download folder keeps several versions

- **One folder per version, as now:** `{DownloadFolder}/sensor-{version}/`, each file next to its `.sha256`, **served only while it matches** (unchanged).
- **A fetch no longer deletes the other versions.** §3.4 decides what is kept.
- **Who added a version, and how,** is in a new table `agent_versions` (§10): from GitHub, uploaded, or found in the folder. A `sensor-*` folder without a row, for example one an admin copied in, is listed as *in the folder*.
- **Files in the folder itself** (not in a `sensor-*` folder; an admin's, or `dist/` in development) still count as PNMT's own version when `sensor-{PNMT}` lacks them, as now. The tab calls them *in the folder itself*, and deploy/README recommends `sensor-{version}/` folders, which are now listed as versions of their own.

### 3.2 GitHub's releases

- **`AgentReleaseCatalog`** lists the releases whose tag starts with `sensor-`, from GitHub's API:
  - `Agents__ReleasesApiUrl`, derived from `AgentDownloadBaseUrl` when that is `https://github.com/{owner}/{repo}/releases/download`: `https://api.github.com/repos/{owner}/{repo}/releases?per_page=100`. Empty for a mirror: no list;
  - per release: the version, the date, whether it is a pre-release, and which of the four files and their `.sha256` it has;
  - **cached for an hour, 10 minutes after a failure.** The API allows 60 requests an hour per address without a token;
  - fetched with the named HTTP client `AgentDownloads`, whose handler the tests replace.
- **Without the list** (no internet, a mirror, the API failing), *Fetch a version…* takes a version number and fetches `{AgentDownloadBaseUrl}/sensor-{version}/{file}`, as now.

### 3.3 Fetch, download, delete

- **Fetch** all four files of a version, or one platform's.
  - As `FetchAsync` does now, with the version as a parameter: the `.sha256` first, the file into a `.part` file hashed while it is written, renamed into place only when it matches, 300 MB at most, one fetch per file at a time.
  - Audited: `AgentVersionFetched`.
- **Download** any file of any version here to the browser, with its `.sha256`. Behind the login, `Servers: Manage`. The anonymous `/agent/v1/download/{asset}` serves only the chosen versions (§5.2).
- **Delete** a version's folder and row.
  - **Refused for a chosen version**, and for PNMT's own version while a platform follows it.
  - Audited: `AgentVersionDeleted`.

### 3.4 What is kept

- **Kept:**
  - the chosen versions;
  - PNMT's own version;
  - the two newest other versions from GitHub (`Agents__KeepVersions`);
  - **uploaded versions, until someone deletes them**, as they can't be fetched again.
- **Older versions from GitHub are deleted** after a fetch, and once a day by `AgentMaintenanceTask`. They can be fetched again.
- A version with all four files is about 300 MB, so the volume holds about five. The tab shows the folder's size.

### 3.5 Upload

- **For a PNMT without internet**, instead of mounting a folder.
- **For admins only** (`[RequireAdmin]`), not for everyone with `Servers: Manage`. An upload is a binary that didn't come from the project's release, and the machines install it at their next update.
- **What is checked:**
  - the file name is one of the four assets, at most 300 MB, and a version is typed in;
  - PNMT computes the SHA-256, and compares it with a `.sha256` when one is uploaded too;
  - **the file's header:**
    - `.exe`: `MZ`, a PE header, and the machine type (x64 `0x8664`, arm64 `0xAA64`);
    - Linux: a 64-bit ELF with `e_machine` x86-64 (62) or AArch64 (183).
- **PNMT can't read the version from the binary.** The agent checks it before it installs anything, and refuses a mismatch (§6.2, step 4).
- **Audited** with the hash: `AgentVersionUploaded`. **The tab says *uploaded by alice*, and so does the manifest** (§5.1), so the machine's `update` prints where the binary came from.

## 4. The version the agents get

- **One version per platform**, in `agent_settings`: `WindowsAgentVersion` and `LinuxAgentVersion`. **Empty means PNMT's own version:** what happens today, and the default.
- **Any version here or on GitHub can be chosen.** Pre-releases are marked.
- **A version newer than PNMT is allowed, with a warning.**
  - The release flow builds the agents at PNMTD's version (`sensor.yml` checks `Directory.Build.props`), so a newer agent belongs to a PNMT that isn't installed yet. It may send what this PNMT doesn't understand: unknown message types are ignored and logged, and unknown fields are dropped.
  - Choosing one asks first: *1.9.0 is newer than this PNMT (1.8.0). Its agents may report what this PNMT doesn't show yet. Update PNMT first if you can.*
  - Afterwards, the tag *newer than PNMT* stays next to the chosen version (§8.1).
  - PNMT can't tell what a future release changed. If it raised `AgentProtocol.Version`, its agents can't connect at all (4400) until PNMT is updated. The warning says this too.
- **Choosing a version starts nothing (§7).** It changes:
  - what `update` finds on a machine;
  - what the install lines and *Add server* download;
  - which agents are *outdated*.

  A version that is only on GitHub is fetched in the background when it is chosen.
- **An agent newer than the chosen version** (holding a release back, or after a bad one) is shown as *newer than the version agents get*. Its `update` says so, and installs the older one only with `--force`, on the machine. **PNMT can't downgrade an agent either.**
- **Changed with `PUT /api/v1/agents/versions/chosen`**, `Servers: Manage`. Audited as `AgentVersionChosen`, and as a change of `agent_settings` (`[Audited]`).
- ***Outdated* now means older than its platform's chosen version**: `IsOutdated(agentVersion, chosen)`. The API field keeps its name.
  - If docs/agent-feature-level-plan.md is built, *lacks features* stays a signal of its own.
  - Choosing a version whose feature level is below what PNMT expects then shows a warning listing what that version lacks.

## 5. What the machines download

### 5.1 The manifest, per platform

- **`GET /agent/v1/download?platform=Windows|Linux`** answers with that platform's chosen version and its files that are here and verified, signed as now.
- **Without `platform`, it answers for Linux.** That is what agents 1.3 to 1.7 ask for (only Linux had an update), so they get the chosen Linux version unchanged.
- **The signed body gains fields that old agents ignore:**

  ```csharp
  public sealed record DownloadManifest(string Version, IReadOnlyList<DownloadAsset> Assets,
      AgentPlatform? Platform = null,
      string? Source = null,      // "GitHub's release sensor-1.8.0", "uploaded to PNMT by alice on 2026-11-20"
      bool PreRelease = false);
  ```

- **It expires 10 minutes after it is signed** (`SignedContent.ExpiresAt`, which signed requests already use). `AgentSigning.Verify` leaves the expiry to the caller, so old agents ignore it. New ones refuse an expired manifest, so an old manifest can't be replayed (with `--force`) to downgrade a machine. A machine whose clock is more than 10 minutes off is told so.
- **Files of the chosen version that aren't here yet** are fetched first, waiting up to 30 seconds, as now.

### 5.2 The files

- **`GET /agent/v1/download/{asset}`** serves the chosen version of that asset's platform. The path, the 20 downloads per hour and address, and the check against the `.sha256` are unchanged. **Other versions aren't served anonymously.**
- **The install lines** (*Download*, *Add server*) and Windows' links use the chosen version: PNMT's link, and GitHub's `sensor-{chosen}`.

### 5.3 Agents in the field

- **Linux 1.3 to 1.7:** `pnmt-agent update` gets the chosen Linux version, with no change. Only its message for an older version still says "PNMT's".
- **Windows 1.7 and older** have no download. They update once the old way (§10).

## 6. The update on the machine

### 6.1 One updater, two installers

- **The download part of `Updater.cs` moves to `PNMTD.Sensor.Core/Update/`:**
  - the manifest, checked against `agent.json`'s `PnmtdKey`;
  - the file for the platform and architecture;
  - its size and hash;
  - `IsNewer`.

  The Windows agent's `agent.json` has the same `Url`, `PnmtdKey` and `PlainHttp` (Core's `AgentFiles`).
- **Each platform installs itself:** Linux with systemd as now (`InstallSelf`), Windows with the service manager (§6.3).

### 6.2 The steps, on both platforms

1. **The manifest for this platform:** the signature against the pinned key, the type, the empty agent id, the expiry.
2. **This architecture's file:** only when it is newer, else *Already up to date*, exit 0. An older one only with `--force`.
3. **Download it** into the state folder (root only, or SYSTEM and Administrators only), and check its size and hash.
4. **Run its `version`:** the product, the architecture, and **the manifest's version must match**. Today the version isn't compared; an upload makes that necessary (§3.5).
5. **Print where it came from:** *from GitHub's release sensor-1.8.0*, or *uploaded to PNMT by alice on 2026-11-20*.
6. **Hand over:** the new binary runs `update-install --from <old>`. It stops the service, **keeps the old binary as `.previous`**, puts itself in place, writes the service definition, and starts the service.
7. **Check that it runs:** within 30 seconds the service must be running and the new version must have written `status.json`. **If not, it puts `.previous` back**, starts that, and exits 1 with *1.8.0 did not start: back on 1.7.0*. A connection to PNMT that fails is reported, but doesn't roll back: it can fail for other reasons.
8. **The journal or event log:** *updated from 1.7.0 to 1.8.0*.

**The command line, on both platforms:** `update [<file>] [--force]`, as now on Linux. New:
- **`update --check`** says whether a newer version is offered: exit 0 when up to date, 10 when there is a newer one. For the machine's own monitoring and scripts.
- **`update --rollback`** puts `.previous` back the same way, on the machine.

### 6.3 Windows

- **`pnmt-sensor.exe update`, run from the installed exe** (`C:\Program Files\PNMT Sensor\pnmt-sensor.exe`), asks PNMT (§6.2).
- **Run from another exe** (downloaded by hand), it installs that exe, as now, so the current instructions and RMM scripts keep working. `update <file>` installs that file.
- **How the running file is replaced.** The command line or window runs the installed file. Windows won't overwrite a running exe, but it can rename it. So `update-install`:
  1. stops the service;
  2. renames `pnmt-sensor.exe` to `pnmt-sensor.exe.previous`, replacing an older one;
  3. copies the new exe into place, runs `sc config`, and starts the service.

  `.previous` stays for a rollback until the next update replaces it.
- **The download goes into `C:\ProgramData\PNMT Sensor\state\update\`,** which only SYSTEM and Administrators can write (`WindowsPlatform`'s secure folder). Nothing unprivileged can swap the file between the hash check and the start.
- **`version` must answer over a pipe.** The exe is a `WinExe` that attaches to its parent's console (`AttachConsole(-1)`). U3 checks that the answer reaches a redirected stdout, and adds another way if it doesn't.
- **The window:**
  - the status view's *Update* card asks PNMT's manifest when the window opens, and on *Check for updates*;
  - it shows *PNMT offers 1.8.0 (GitHub's release); this agent is 1.7.0* with *Update*, the steps as progress, and *Roll back to 1.6.3* while a `.previous` exists;
  - the card for a newer exe that runs stays as it is.
- **The event log** says *PNMT Sensor updated from X to Y*, as now.
- **Unattended:** `update` exits 0 when up to date, so the machine admin's own scheduled task, GPO, Intune or RMM can run it (§7.1).

### 6.4 Linux

As built, plus:
- `?platform=Linux` in the manifest request;
- the version check (§6.2, step 4) and the source;
- `--check`;
- `/usr/local/sbin/pnmt-agent.previous`, with the automatic and the manual rollback.

## 7. Only the machine starts an update

### 7.1 The rule

- **An update starts only when someone or something on the machine runs it:**
  - an admin at the command line, or in the Windows window;
  - a schedule the machine's admin set up there: cron, a systemd timer, a scheduled task, GPO, Intune, an RMM.
- **The agents ship no schedule and no automatic update, and nothing in PNMT switches one on.**
- **PNMT decides *what* an update installs, never *when*.**

### 7.2 What PNMT doesn't have, on purpose

- **No message in the agent protocol that updates**, and no field in `config` that makes an agent update or check for one.
- **The service never runs the updater.** The update code is called only from the command line (`Program`) and the window (`Ui`). It is never called from `AgentService`, the connection to PNMT, or the handlers of PNMT's requests. A test guards this (§11).
- **No *Update* button, bulk action or API in PNMT that reaches an agent.** Where PNMT shows the line, it says *PNMT can't start an update: run this on the server.*
- **Not for Cookie, not for MCP.** Choosing, fetching, uploading and deleting versions are not tools. MCP's tools only read, and Cookie's executor gets no action for agent versions.

### 7.3 Scripts: accepted

- **A script PNMT runs on a machine may run the update.** It has the rights config.yaml (Linux) or the script policy (Windows) gives it, root or SYSTEM included, and on Linux it runs outside the agent's service (§1).
- **That is accepted, with no guard.** The machine's admin chose to allow scripts with those rights (docs/linux-sensor-plan.md §8.5), and such a script can do anything root can anyway. Neither the agent nor PNMT checks scripts for the update command.
- **This is the one way PNMT can start an update,** and only through the scripts feature the machine allows. deploy/README says so where it describes the scripts' rights.

## 8. The web UI

### 8.1 *Admin → Servers → Versions* (new)

- **What the agents get:** a row each for Windows and Linux.
  - A select: *PNMT's version (1.8.0)*, or a version. Then *Save*.
  - Tags: *pre-release*, *older than PNMT*, ***newer than PNMT*** (a warning, §4), and *lacks features* once the feature level is built.
  - How many run it: *12 of 15 Linux agents run 1.8.0; 3 are older*, linked to the fleet filtered.
  - Below: *The agents update when their server's admin runs the update. PNMT never starts one.*
- **The versions:** a table, newest first:

  | Column | Content |
  |---|---|
  | Version | the number, *pre-release* |
  | From | *GitHub* with the release date, *uploaded by alice*, or *in the folder* |
  | Files | the four files, each *here*, *fetching*, *failed* (with the reason), *not here* or *not in the release* |
  | Agents | Windows n, Linux n, linked to the fleet |
  | Chosen | *Windows*, *Linux* |
  | | *Fetch*, *Fetch again*, *Download* (a menu per file and its `.sha256`), *Delete* (asks first; disabled with the reason for a chosen version or PNMT's own) |

- ***Fetch a version…*** with a version number, for a mirror or when GitHub's list failed. ***Upload…*** only for admins.
- **At the bottom:** the download folder, its size, and what is kept (§3.4).
- **The *download folder* table moves here from *Download*.**

### 8.2 *Download*

- **The chosen versions' files and lines:** *Windows: PNMT Sensor 1.7.0*, *Linux: the PNMT agent 1.8.0*.
- **Windows' *Updating*:** *On the server, in an elevated prompt: `& "C:\Program Files\PNMT Sensor\pnmt-sensor.exe" update`, or Update in the agent's window. It downloads the version PNMT offers, checks it against PNMT's signature, and restarts the service. The identity and the key stay.*
- **Both platforms:** *A schedule of your own on the server can run it. PNMT doesn't.*

### 8.3 The server's pages and the fleet

- **The *Agent* tab, and the Linux and Windows overviews:**
  - *Update available: 1.8.0* replaces *PNMT is x.y.z*;
  - the platform's line to copy, with *Run it on the server*;
  - *Newer than the version agents get* is a neutral tag.
- **The fleet (*Servers*):** the filter *Outdated agent* means older than the version its platform's agents get.

## 9. API

| Endpoint | What it does |
|---|---|
| `GET /api/v1/agents/versions` | The versions here and on GitHub, their files and states, the agents per version and platform, the chosen versions, PNMT's version, the folder's size |
| `PUT /api/v1/agents/versions/chosen` | `{ windows, linux }`, null for PNMT's version. A version newer than PNMT needs `confirmNewer: true`. Audited |
| `POST /api/v1/agents/versions/{version}/fetch` | `{ files? }`. Answers at once. Audited |
| `POST /api/v1/agents/versions/{version}/files` | Uploads a file (multipart). **Admins only** (`[RequireAdmin]`). Audited |
| `GET /api/v1/agents/versions/{version}/files/{file}` | Downloads a file, behind the login |
| `DELETE /api/v1/agents/versions/{version}` | Deletes a version. Audited |
| `GET /agent/v1/download?platform=` | The manifest per platform (§5.1). Anonymous |
| `GET /api/v1/agents/downloads`, `POST …/downloads/fetch` | As now, for the chosen versions |

- **All of them `Servers: Manage`, apart from the upload**, in `AgentsController`. That right's text already covers *the agents' files*.
- **The new audit events:** `AgentVersionFetched`, `AgentVersionUploaded`, `AgentVersionDeleted`, `AgentVersionChosen`, in the *Agents* category.
- **The OpenAPI and `schema.d.ts` are regenerated.**

## 10. Data, settings, upgrading

- **Migration `AgentVersions`:**
  - `agent_settings.WindowsAgentVersion` and `LinuxAgentVersion`, nullable;
  - **`agent_versions`** (`[Audited]`): `Version` (key), `Source` (GitHub, Upload or Folder), `AddedAt`, `AddedBy`, `PreRelease`, `ReleasedAt`.
- **Settings**, added to deploy/README and the settings list:
  - `Agents__ReleasesApiUrl`: derived from the base URL by default, empty for none;
  - `Agents__KeepVersions`: `2`.
- **Upgrading PNMT:** nothing is chosen, so the agents get PNMT's version, as before. From then on the folder keeps the older `sensor-*` folders (§3.4).
- **Upgrading the agents:**
  - Linux 1.3 and later update as before, and get the chosen version;
  - **Windows 1.7 and older get the new update once the old way:** download the 1.8.0 exe from *Download* and run it (*Update* in its window, or `pnmt-sensor-x64.exe update`). After that, `update` from the installed exe asks PNMT. The release notes say so.

## 11. Testing

- **PNMTD.Tests:**
  - versions in the folder: fetching a given version, the check against `.sha256`, what is kept (§3.4), files in the folder itself;
  - the catalog from a fake GitHub (the named client's handler): `sensor-*` tags only, pre-releases, missing assets, the cache and the failure cache;
  - the chosen versions: newer than PNMT only with `confirmNewer`, then tagged; a chosen version can't be deleted; a GitHub-only version is fetched when chosen;
  - the manifest per platform, Linux without `platform`, its expiry, its source;
  - uploads: refused for a non-admin with `Servers: Manage`, the name, the size, the PE and ELF headers and machine types, the `.sha256` comparison;
  - `IsOutdated` against the chosen version;
  - the audit events.
- **Agent tests (Core):**
  - the manifest's checks (signature, type, agent id, expiry);
  - `IsNewer` with `--force`;
  - **a binary whose `version` doesn't match the manifest is refused**.
- **The guard on R4:** a test over the agents' sources asserts that the update code is referenced only from `Program.cs` and `Ui/`, never from `Service/`, the session or the handlers. Breaking R4 then breaks the build.
- **The Linux lab** (`tools/linux-lab.sh`):
  - an update to the chosen version;
  - up to date, exit 0;
  - an older chosen version: refused, then `--force`;
  - **the automatic rollback** with a binary built to fail in `service`;
  - `--rollback`.
- **Windows, by hand, on a Windows Server VM** (as for W5):
  - an update from the installed exe, on the command line and in the window, x64 and arm64 if one is at hand;
  - the rename hand-over while the window runs from the installed file;
  - the automatic rollback, and `--rollback`;
  - `version` over a pipe;
  - a 1.7 agent updated the old way to 1.8.0, then from PNMT to a test build.
- **e2e (Playwright):** the *Versions* tab with a stubbed catalog: choosing a version, the warning for one newer than PNMT, fetching, *Delete* disabled for the chosen one, *Upload* hidden for a non-admin. *Update available* and the line on a Windows server's page.

## 12. Later, not in this plan

- **A version per group or ring** (*test servers first*). Since only the machines start updates, their admins already stage a rollout by when they run it.
- **Releases signed with a project key** compiled into the agents, so an agent would trust only binaries the project built, whatever PNMT hands out. That needs a signing key in the release workflow and a change to `sensor.yml`.
- **Telling someone when agents fall behind**, as a notification or an event.

## 13. Phases

| Phase | What | Size |
|---|---|---|
| **U1** | PNMT: versions in the folder, the GitHub catalog, the chosen versions, the manifest per platform, *outdated* against the chosen version, the API, the *Versions* tab, *Download* for the chosen versions. **Useful on its own:** Linux agents already update to the chosen version | large |
| **U2** | The shared updater in Core. Linux on it: the version check, the source, `--check`, `.previous` with both rollbacks. The lab | medium |
| **U3** | Windows: the update from PNMT, the rename hand-over, the window's card, the rollbacks, the lines on the server's pages. The manual test on a VM | medium |
| **U4** | deploy/README (the settings, and that a script may update the agent), the release notes, this plan's *As built* | small |

## 14. The review's answers (2026-11-15)

| # | Question | Answer | Where |
|---|---|---|---|
| 1 | One chosen version per platform, or one for both? | **Per platform** | §4 |
| 2 | Uploads: behind a setting, for `Servers: Manage`, or not at all? | **For admins** (`[RequireAdmin]`), no setting | §3.5, §9 |
| 3 | Versions newer than PNMT? | **Allowed, with a warning** | §4, §8.1 |
| 4 | The automatic rollback when the new service doesn't start? | **Yes**, on both platforms | §6.2 |
| 5 | Guard against scripts that run the update? | **No.** A script PNMT runs may update the agent; no check in the agent or in PNMT | §7.3 |

## 15. As built

### 15.1 U1: PNMT (2026-11-15)

**Where it is:**
- **`PNMTD/Agents/AgentDownloads.cs`:** every operation takes a version. `sensor-{version}/` per version; `Check(version, file)`, with the folder itself only for PNMTD's own version; fetching, `StoreUploadAsync` with `HeaderProblem` (PE and ELF, the architecture's machine), `DeleteVersion`, `Retain` (§3.4), the manifest of a platform's version, and the links of the chosen versions. The fetches, failures and the releases' hashes are kept per version.
- **`PNMTD/Agents/AgentReleaseCatalog.cs`:** GitHub's list (§3.2), with a `User-Agent`, cached an hour (10 minutes after a failure), through the named client `AgentDownloads`.
- **`PNMTD/Agents/AgentVersionService.cs`:** the chosen versions (`Resolve`: null is PNMTD's own), `IsOutdated` and `IsAhead`, the list, choosing (409 `newer_than_pnmt` without `confirmNewer`), fetching, uploading, a file for the browser, deleting.
- **`PNMTD.Lib/Agent/AgentVersionOrder.cs`:** the versions' order (a release above its pre-releases) and what a version may look like (`1.8.0`, `1.8.0-rc1`; no slashes, no `..`). The agents will use it in U2.
- **The manifest:** `GET /agent/v1/download?platform=`, signed with `AgentProtocol.ManifestLifetime` (10 minutes); `DownloadManifest` gained `Platform` and `PreRelease`, `DownloadAsset` gained `Source`. A file is served of its platform's chosen version.
- **The API** in `AgentsController` (§9), the upload with `[RequireAdmin]` and `AgentUploadLimit` (300 MB and the form). Audit events `AgentVersionChosen`, `AgentVersionFetched`, `AgentVersionUploaded`, `AgentVersionDeleted`.
- **Migration `AgentVersions`:** `agent_settings.WindowsAgentVersion` and `LinuxAgentVersion`.
- **`AgentMaintenanceTask`** applies the retention every hour, and a fetch after it is done.
- **`AgentServerService`:** *outdated* against the platform's chosen version; `AgentServerItem.ChosenVersion` and `Ahead`.
- **Web UI:** `views/admin/servers/VersionsTab.vue` and `versions.ts` (with its test); *Download* shows the chosen versions and links to *Versions*; *Update available: x.y.z* on the *Agent* tab, the Linux overview and the status strip; the fleet takes `?platform=` and `?filter=` (the Versions tab links to the older agents).
- **Settings:** `Agents__ReleasesApiUrl`, `Agents__KeepVersions` (deploy/.env.example, deploy/README.md, with the proxy's body limit for uploads).

**Where it differs from the plan:**
- **No table `agent_versions`** (§10). Where each file came from (GitHub, an upload and by whom, when) is kept **per file** in `agent-version.json` in the version's folder, which `AgentDownloads.OriginsFile` names and nothing serves. It stays with the files, survives a database restore without them and the other way round, and the singleton needs no database. A folder from before 1.8 has none: its files count as fetched from GitHub. The audit log has who fetched, uploaded and deleted.
- **The source is per file** in the manifest (`DownloadAsset.Source`), not the manifest's: a version can have one platform's files from GitHub and the other's uploaded, and the update prints its own file's.
- **The manifest without `platform` lists the Linux files only** (it listed all four): only Linux agents ask without it.
- **The download folder's table moved** from *Download* to *Versions* (§8.1), as planned; *Download* names the files not here yet.
- **A version can be chosen** when a file of its platform is here or GitHub's list has it; a version chosen but not here is fetched in the background.
- **PNMTD's own version can be deleted** while no platform gets it; files in the folder itself (outside `sensor-*`) stay.

**Tests:** `PNMTD.Tests/Agents/AgentVersionTests.cs` (the list with a stub GitHub, choosing and what follows, newer than PNMT, deleting, fetching, uploads and their checks, the versions' order, the headers) and `AgentDownloadTests` (the manifest per platform and its expiry, the retention). The matrix of `EndpointAuditTests` has the six endpoints. The e2e `linux.spec.ts` opens *Versions*.

**Known:** `McpServerTests.TheReadWriteTokenNeedsTwoFactorAndLastsTwelveHours` fails in the evening in Europe/Zurich, before and after U1: it moves the clock 12 hours, past midnight, and the day's 2FA code is then due.

### 15.2 U2: the shared updater, the Linux agent (2026-11-15)

**Where it is:**
- **`PNMTD.Sensor.Core/Update/AgentUpdate.cs`:** the offer (`?platform=`, the manifest checked against the pinned key, its type, agent id and expiry; a PNMT before 1.8 signs no expiry and is still trusted), the download (size and SHA-256 against the manifest, into a folder the platform secures first), `VersionProblem` (product, architecture, the manifest's version), and `WaitRunningAsync` (the new service wrote `status.json` as its version).
- **`PNMTD.Sensor.Linux/Update/Updater.cs`:** `update`, `update --check` (exit 10 when PNMT offers a newer version), `update --rollback`, and the hidden `update-install`, which keeps the program as `/usr/local/sbin/pnmt-agent.previous`.

**Where it differs from the plan:**
- **`--rollback` swaps:** the previous program installs itself (with its own `update-install`, which every Linux release since 1.3.0 has), and the program it replaces becomes `.previous`, so a second `--rollback` switches back.
- **The automatic rollback:** `update-install` exits 3 when the new version doesn't run within 30 seconds, or when `systemctl start` fails. The previous program goes back, writes its own unit (`install-unit`, since 1.3.0), and must run again. The caller says *1.8.0 did not start: back on 1.7.0*.
- **A connection to PNMT** is not part of the check: `status.json` written by the new version is (§6.2, step 7).

**The lab** (2026-11-15; Debian 12, arm64, `tools/linux-lab.sh`; a PNMT stack of its own with builds `1.8.0-lab1` to `-lab4` uploaded on *Versions*):
1. `update --check` while Linux agents get an older version: *Up to date … newer than 1.7.0*, exit 0. With `1.8.0-lab2` chosen (409 without `confirmNewer`): exit 10, with *uploaded to PNMT by lab-admin*.
2. `update` from lab1 to lab2: downloaded, checked, `.previous` kept, the service running as lab2, the journal written.
3. `update --rollback` twice: lab1, then lab2 again; no files left behind.
4. A build whose service exits at once (lab3): **the first run found a bug.** `systemctl start` failed, `update-install` stopped before its check, and lab3 stayed in a restart loop. Fixed (0d058e1): with lab4, lab2 came back, ran, and the update exited 1 with the reason.
5. A released 1.7.0 agent, installed from PNMT and joined, updated with its own `update` (no `platform` in the request) to lab2; lab2 kept 1.7.0 as `.previous`.
6. `update --rollback` from lab2 to the released 1.7.0: its own `update-install` installed it, the service ran.
