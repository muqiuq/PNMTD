# PNMT 1.8: the agents' versions, chosen in PNMT

Version 1.8.0.

## What's new

- **The agents' versions on *Admin → Servers → Versions*** (docs/agent-update-plan.md).
  - **The version each platform's agents get,** Windows and Linux separately: PNMT's own by default, or another version. **One newer than PNMT** can be chosen after a warning: its agents may report what this PNMT doesn't show yet.
  - **The versions:** every release on GitHub (`sensor-*`) and every version PNMT has, with its four files, where each came from, and the agents running it. *Fetch* a version from its release, *Download* a file, *Delete* a version no platform gets.
  - **Admins can upload** an agent's file, for a PNMT without internet. PNMT checks that it is an executable of its platform and architecture, and the update prints who uploaded it.
  - ***Outdated*** now means older than the version its platform's agents get. A server's pages say *Update available: 1.8.0* and how to update it; an agent newer than that is marked too.
  - **The download folder keeps several versions:** the chosen ones, PNMT's own, uploaded ones until deleted, and the two newest others. Older ones are deleted and can be fetched again.
- **The Windows agent updates from PNMT,** as the Linux agent does: `& "C:\Program Files\PNMT Sensor\pnmt-sensor.exe" update` in PowerShell as an administrator, or *Update* in the agent's window. It downloads the version Windows agents get, checks it against PNMT's signed manifest, and restarts the service.
- **Both agents roll back:**
  - **automatically,** when the new version doesn't run within 30 seconds: the previous program goes back and the update says so;
  - **by hand,** with `update --rollback`, which also switches back again.
- **`update --check`** on both says whether PNMT offers a newer version: exit 10 if so, 0 if not.
- **Only the server starts an update.** PNMT has no button, message or setting that does; choosing a version only changes what the next update installs. A schedule of your own on the server (cron, a systemd timer, a scheduled task) can run `update`, which exits 0 when there is nothing newer.

## Updating from 1.7

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.8.0`. It takes a backup first.

- **The migration `AgentVersions`** adds two columns to `agent_settings`: the versions Windows and Linux agents get. Empty, they get PNMT's own version, as before.
- **New settings,** both optional (deploy/.env.example):
  - `Agents__ReleasesApiUrl`: GitHub's list of releases, derived from `Agents__AgentDownloadBaseUrl`; set it empty for a mirror;
  - `Agents__KeepVersions`: how many other versions the download folder keeps (2).
- **Windows agents of 1.7 and older can't download from PNMT yet:** run the 1.8 executable on each server once (*Update* in its window). From then on, the agent updates from PNMT. Linux agents update as before, and get the chosen version.
- **Uploading an agent's file** through a reverse proxy needs a body limit of about 310 MB for `/api/v1/agents/versions/` (deploy/README.md). Without uploads, nothing changes.

## Good to know

- **A script PNMT runs on a server can run the update,** if that server lets scripts run as root or SYSTEM. That is the only way PNMT can start one.
- **The update checks the server's clock:** PNMT's manifest is valid for ten minutes, so an old one can't be replayed. A clock off by more than that makes the update refuse, and say why.
- **The Windows agent's update has not been tried on Windows** at the time of writing (docs/agent-update-plan.md §15.3).
