# PNMT 1.3: servers with an agent, locations and the knowledge base

Version 1.3.0.

## What's new

- **Windows and Linux servers report through PNMT's agent,** on PNMT's own address and port: nothing to open. *Admin → Servers* lists them, issues join tokens and offers the downloads.
  - **Windows** servers and PCs (Server 2016–2025, Windows 10 Pro and 11) run one executable, `pnmt-sensor-x64.exe` or `-arm64.exe`, joined with a token.
  - **Linux** servers (Debian 12 and 13; Proxmox VE and Ubuntu LTS too) run `pnmt-agent`, installed with one line copied from PNMT, as a systemd service with a command line.
  - **Both report** the system, hardware, network, services, every way in with the firewall, admin logins and failed ones, the event log's or journal's errors, and readable changes. **Diagnostics** (ping and DNS sets) run on them, each a sensor of its own.
  - **Scripts** from PNMT, PowerShell or bash, run only where the server itself allows them.
  - **The SSH key manager** (*Admin → SSH keys*): every `authorized_keys` file on Linux servers is read and watched, each key gets a name and an owner, an unregistered key raises an event, and a departing colleague's keys can be removed from every server in one step, where the server's `/etc/pnmt-agent/config.yaml` allows it.
- **Locations.** A location is an object of its own, and locations nest: a site, its buildings, their rooms, a rack.
  - A host can be assigned to one location and keeps its free text, now *Location text*, for the rack or the unit.
  - *Locations → From the hosts' texts…* turns the texts hosts use into locations, when you choose to; nothing is converted by itself.
  - **A page per location:** its sub-locations and hosts on the left, with their state, and on the right the fields marked for the overview, then its knowledge base pages.
- **Location fields.** Admins define what is kept per location under *Admin → Location fields*: Markdown above all, and text, numbers, choices, links, users, hosts and more. A field can be inherited by sub-locations (an address written once for a site) and marked sensitive. *Add typical fields…* offers the usual ones.
- **The knowledge base.** Pages with a number (`KB-42`), a title, tags, a status and an archive.
  - Every save is a revision: who, when, a note, and the changes line by line. Any two revisions can be compared and an old one restored. An Admin can redact a leaked text from every revision, and delete an archived page.
  - Pages link to locations, hosts and sensors, and show up on their pages.
  - The global search (Ctrl K) finds pages by title, summary and `#tag`; the knowledge base searches their content, with the matches marked.
  - Lists by newest, recently updated, most viewed and most edited, and a tag browser.
- **One Markdown editor for pages and fields,** on a page of its own: a visual editor and VS Code's editor (Monaco) behind a switch, with a plain text field where they can't run (on phones, for one).
  - **Pasted or dropped files are uploaded at once:** images up to 2 MB are shown in the page (larger ones are recompressed in the browser), any other file up to 25 MB becomes an attachment to download. Executables are refused, and images lose their metadata (GPS included).
  - **Diagrams and charts** from Mermaid blocks: flowcharts, sequences, Gantt charts, pie, bar and line charts and more, drawn in the browser.
- **AI tags, optional.** With AI on, *Find tags with AI* suggests up to 10 tags. It asks after a page's first save and after about one in ten edits, and sends nothing until the person confirms that the page holds no credentials and no personal or confidential data. **A page marked sensitive never reaches an AI**, and later never MCP.
- **Not a password manager:** a save that looks like it holds a password warns first, and offers to mark the page sensitive.

## Updating from 1.2

In the `deploy/` folder: `git pull` for the new `docker-compose.yml`, which mounts the files folder, then `./update.sh --tag v1.3.0`. It takes a backup first. The migrations run when the new image starts.

- **The files folder.** Images and attachments are files in a folder on this host, `deploy/files/` (`PNMTD_FILES_DIR` in `.env` moves it, for example to a disk of its own). It must belong to the app's user, uid 1654, mode `0750`.
  - `./update.sh` creates it before it starts the new release; a new installation gets it from `./init.sh`.
  - **Updating another way,** create it before the containers start, or Docker creates it as root and uploads fail:
    `sudo install -d -o 1654 -g 1654 -m 0750 files` (rootless Podman: `mkdir -m 0750 files && podman unshare chown 1654:1654 files`), then `docker compose up -d`.
  - `./info.sh` checks that the folder exists, is mounted and writable, and says what to do. The new system sensor *File storage* on the host *PNMT* turns red (428) for the same problems, for missing or changed files, and below 1 GB free.
  - SELinux hosts label it with `:z` ([deploy/README.md](../deploy/README.md#uploaded-files)).
- **Backups now hold `files.tar`,** the folder's files, next to `database.pgdump` and `keys.tar.gz`. `./backup.sh --no-files` leaves it out, for a host that backs the folder up with its own tools. `./restore.sh` puts the files back as the app's user; a backup from 1.2 restores as before.
- **A reverse proxy needs a body limit of 32 MB** for uploads: `client_max_body_size 32m;` (nginx) or `request_body { max_size 32MB }` (Caddy); Traefik has no limit. It replaces the 20 MB for firmware images. The UI names a proxy's refusal (413) as such.
- **The agents' files** come from PNMT itself: PNMTD fetches its version's files from the GitHub release `sensor-1.3.0` when first asked for, checks their SHA-256 and keeps them in the new volume `pnmtd-downloads`. Without internet, mount a folder with the release's files instead ([deploy/README.md](../deploy/README.md#servers-with-an-agent)).
- **Behind nginx, the agents need the WebSocket lines** for `/agent/v1/ws` and a `proxy_read_timeout` above 30 s ([deploy/README.md](../deploy/README.md#behind-a-reverse-proxy)); Caddy and Traefik pass WebSockets as they are.
- **Hosts keep their location texts,** and a new location field, *Overview*, is there to fill. Nothing else changes until someone creates a location or a page.

From v0.12, [deploy/UPGRADING-1.0.md](../deploy/UPGRADING-1.0.md) applies as it is: `./init.sh` creates the files folder.

## Good to know

- **The files on the host** may be read, copied, measured and backed up with any tool, but never changed, renamed or deleted there: the daily check notices, and pages show *File removed*. *Admin → Knowledge base → Files* shows what takes the space, and deletes a file at once.
- **What reaches the AI:** the page's text, checked for credentials first and scrubbed of IP addresses, host names, e-mail addresses, URLs and phone numbers. Never its files, never a sensitive page, never an old revision. Every request is in *Admin → AI* as before.
- **Roles:** Viewers read everything, sensitive pages included; Users write pages and locations; only Admins define fields, delete locations and archived pages, redact, and manage tags and files.
- **Demo data:** `SEED=1 ./start.sh` now has two sites with sub-locations, filled fields, and about twenty pages with an image, an attachment and diagrams.
