# PNMT 1.19: OS updates on Linux servers, and alerts for high-risk fixes

## What's new

- **Linux servers keep their package lists fresh, and install updates from PNMT** (docs/linux-os-updates-plan.md):
  - **The daily `apt-get update`** runs at a fixed time per server inside a window (04:00–06:00 server time by default), also while PNMT is
    unreachable; a missed day catches up at the next start. Each server then reports every pending package with its versions, source package,
    origin and whether it comes from the security archive.
  - **A server's new *OS updates* tab** shows them, the run in progress with its log live, both plans (*upgrade* never removes, *full upgrade* may),
    what needrestart says still runs old libraries, and the log of every run.
  - ***Refresh lists*** and ***Install updates…***: `apt-get upgrade --with-new-pkgs` or `apt-get full-upgrade` (the default on Proxmox VE),
    non-interactively, keeping your configuration files. The dialog lists the removals, and the server refuses when they changed since you
    looked. PNMT sends only the kind and the mode; the agent builds every command line itself.
  - **A run can't be broken by PNMT or the agent:** it runs in a systemd unit of its own, writes its log to a file, and is never killed midway.
    An agent restart, a lost connection or an upgrade of the server PNMT itself runs on carries on, and reports when it can.
  - **Full logs** of every update and upgrade, downloadable. Upgrades someone ran on the server (unattended-upgrades, `apt`) appear too, from
    history.log.
  - **The dashboard's new *OS updates* card:** servers with updates, packages, security updates, high risk, failed runs and stale lists, by server
    or by package.
- **The high-risk check** (optional, off by default): which pending updates fix vulnerabilities that are exploited in the wild, among the top
  1 % of EPSS, or rated critical by the distribution.
  - **Public data, downloaded whole:** the Debian Security Tracker, Ubuntu's security notices, CISA's Known Exploited Vulnerabilities and FIRST's
    EPSS, plus each pending package's changelog from the server itself. **Nothing about the fleet leaves PNMT.**
  - **Sources only add:** one source failing or saying less can't hide what another one shows. *Admin → Servers → Advisories* lists them, their
    state and the coverage of each role; a blind check is an alert itself (434 on the new system sensor *OS advisories*).
  - **466 turns the server's sensor bad** while a high-risk update is pending: "web-01: 1 high-risk update pending: openssl 3.0.22-1~deb12u2
    (CVE-…: exploited in the wild (CISA KEV since …))".
  - **Findings can be acknowledged** for 1, 7 or 30 days, with a reason, in the audit log. An acknowledgement ends early when the evidence gets
    stronger (an EPSS-only finding enters KEV, for example), and 466 says so.
  - **AI for advisory texts** (optional): RSS sources such as Debian's DSA list, read for "exploited in the wild" before KEV lists it. The AI only
    sees the public text, can only raise a rating, and only when a second model of another vendor agrees and its quote is in the text word for
    word.
- **New events:** 265 *LINUX_UPDATES_INSTALLED*, 466 *LINUX_HIGH_RISK_UPDATE*, 467 *LINUX_UPDATE_FAILED* (a failed upgrade until one succeeds or
  someone marks it handled, interrupted dpkg, or lists not refreshed for 3 days), and 434 *OS_ADVISORIES_FAILING*.
- **A new level in the *Servers* area, *Run*:** refreshing lists, installing OS updates and acknowledging findings, without the rest of
  *Servers: Manage*.
- **`pnmt-agent os-updates`** on the server: what applies, the next daily update, the runs; `pnmt-agent os-updates log -f` follows a run;
  `pnmt-agent os-updates update` refreshes the lists now.

## Updating from 1.18

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.19.0`. It takes a backup first.

- **One migration runs at the start:** `AddOsUpdates` adds nine tables and twelve columns on `agents` (the three new switches on for every
  server). Nothing existing changes.
- **Update the agents** (`pnmt-agent update` on each Linux server): an agent before 1.19 reports no OS updates, and its tab says so.
- **OS updates are on by default, on both sides.** PNMT's switch is `OsUpdates__Enabled`; each server has its own on its *OS updates* tab. On the
  server, `/etc/pnmt-agent/config.yaml`'s new `os_updates:` block decides, and **a missing setting means on**. To keep PNMT from installing
  updates on a server, root adds:

  ```yaml
  os_updates:
    upgrade: false
    full_upgrade: false
  ```

  `update: false` stops the daily `apt-get update` too; `pnmt-agent config check` shows what applies. A broken or unsafe `config.yaml` grants
  nothing, as before.
- **The new level *Servers: Run*** is in no group after the update; admins and *Servers: Manage* have it. Give it in *Admin → Groups* to those
  who should install updates.
- **The high-risk check is off** until `OsUpdates__Advisories__Enabled=true`. It then creates its default sources and downloads them over https
  (`security-tracker.debian.org`, `ubuntu.com`, `www.cisa.gov`, `epss.empiricalsecurity.com`); the tracker is about 15 MB compressed, every 6
  hours, sent only when it changed.
- **Optional settings:** `OsUpdates__*` and `Ai__Tokens__OsAdvisoryRate` (see `.env.example`).

## Good to know

- **PNMT never reboots and restarts no service.** It shows the reboot a new kernel needs and what needrestart lists.
- **Proxmox VE without a subscription:** its enterprise repository answers 401, so apt exits with an error although the other lists were
  refreshed. PNMT records the update as *Warnings* and alerts only when the lists get stale. Disable the enterprise repository to silence it.
- **Retention:** the newest 50 update and 200 upgrade runs per server, each log up to 8 MiB (`OsUpdates__KeepUpdateLogs`,
  `OsUpdates__KeepUpgradeLogs`, `OsUpdates__MaxLogBytes`).
- **Debian's security uploads** have no changelog on metadata.ftp-master until the next point release; the agent then reads the changelog from
  the package itself, downloaded into its own folder and deleted again.
- **`pnmtd dev seed`** adds pending packages, two weeks of runs, a failed upgrade marked handled, the check's sources and findings, and an
  acknowledged one.
- **The plan:** docs/linux-os-updates-plan.md, with its decisions and, in §15, where the build differs from it.
