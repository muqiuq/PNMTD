# PNMT 1.8: the agents' versions, chosen in PNMT

Version 1.8.2.

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

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.8.2`. It takes a backup first.

- **The migration `AgentVersions`** adds two columns to `agent_settings`: the versions Windows and Linux agents get. Empty, they get PNMT's own version, as before.
- **New settings,** both optional (deploy/.env.example):
  - `Agents__ReleasesApiUrl`: GitHub's list of releases, derived from `Agents__AgentDownloadBaseUrl`; set it empty for a mirror;
  - `Agents__KeepVersions`: how many other versions the download folder keeps (2).
- **The token limits are settings now:** `Ai__Tokens__*` and `Cookie__Tokens__*` (deploy/.env.example), the most tokens an answer may have, per kind of request. **The mail analysis gets three times as many** (4500, and 1200 for a mail of an *AI every time* rule); **Cookie, Seraph and the big brother twice as many.** On a reasoning model the reasoning counts against the limit, and the old 1500 for the mail analysis could end a request before its answer, as *invalid output: no message content* in *Admin → AI*.
- **Windows agents of 1.7 and older can't download from PNMT yet:** run the 1.8 executable on each server once (*Update* in its window). From then on, the agent updates from PNMT. Linux agents update as before, and get the chosen version.
- **Uploading an agent's file** through a reverse proxy needs a body limit of about 310 MB for `/api/v1/agents/versions/` (deploy/README.md). Without uploads, nothing changes.

## Security

Three findings of a review of 2026-11-16, fixed in 1.8.2:

- **UDP nodes: a forged NONE or TOKEN32 packet could lock a node's signed packets out.** The server kept one frame counter window per node, whatever the suite, so anyone who knew a device id could send an unkeyed heartbeat with a high counter and make the device's HMAC or AES packets look like replays until an admin reset the counter. NONE and TOKEN32 uplinks now have a window of their own (docs/udp-protocol.md §3.7): **the migration `UdpPlainCounter`** adds `PlainFcntHigh` and `PlainFcntWindow` to `udp_nodes`. On a node that also allows keyed uplinks, an AES-CCM answer to an unkeyed uplink is no longer sent (the nonce could repeat across the two windows); such a node's acks come as the uplink did, and its commands wait for a keyed uplink. Nodes that only send NONE or TOKEN32, and nodes that only send keyed packets, work as before.
- **Agents: a signed request could be replayed on the next connection.** The agent remembered request ids per connection, so a proxy that reads the TLS could capture a `run-script` or `remove-ssh-key` frame, drop the connection, and replay it within its five minutes. Every signed message (the welcome, each config, each request) now names the connection's challenge nonce, and the agent refuses one for another connection (docs/windows-sensor-plan.md §3.4). Older agents ignore the new field; a newer agent accepts messages from an older PNMT, which has no nonce to name.
- **The access watch sent source addresses and account names to the AI provider.** The brief, the tools' results and Seraph's evidence now carry them as `ADDRESS_n` and `PERSON_n`, restored by code before anything is shown or looked up (docs/cookie-access-plan.md §9). What still leaves, for Cookie and for the access watch, is stated in deploy/README.md under *AI*.

## Good to know

- **A script PNMT runs on a server can run the update,** if that server lets scripts run as root or SYSTEM. That is the only way PNMT can start one.
- **The update checks the server's clock:** PNMT's manifest is valid for ten minutes, so an old one can't be replayed. A clock off by more than that makes the update refuse, and say why.
- **The Windows agent's update has not been tried on Windows** at the time of writing (docs/agent-update-plan.md §15.3).
