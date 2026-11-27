# PNMT's features in detail

What each part of PNMT does, a section per feature. The [README](../README.md) has the short list. Each section ends with
where to read more: the setup in [deploy/README.md](../deploy/README.md), and the design with its decisions in a plan in
`docs/`. The release notes ([docs/release-notes-1.30.md](release-notes-1.30.md) and older) say what came when.

**Contents**

- [Monitoring](#monitoring): [sensors](#sensors), [advanced sensors](#advanced-sensors), [SSL certificates](#ssl-certificates),
  [DNS zones](#dns-zones), [e-mail input](#e-mail-input), [IoT nodes over UDP](#iot-nodes-over-udp),
  [PNMT's own condition](#pnmts-own-condition), [notifications](#notifications)
- [Servers and the network](#servers-and-the-network): [Windows servers](#windows-servers-and-pcs),
  [Linux servers](#linux-servers), [OS updates](#os-updates-on-linux-servers), [the SSH key manager](#the-ssh-key-manager),
  [the agents' versions](#the-agents-versions), [MikroTik routers](#mikrotik-routers), [the network map](#the-network-map)
- [Knowledge and the team](#knowledge-and-the-team): [locations](#locations), [the knowledge base](#the-knowledge-base),
  [responsibilities](#responsibilities), [the availability plan](#the-availability-plan),
  [licences & services](#licences--services), [boards](#boards)
- [AI, optional](#ai-optional): [AI proposals](#ai-proposals), [Cookie](#cookie-the-assistant),
  [the access watch](#the-access-watch), [the lounge](#the-lounge), [the MCP server](#the-mcp-server)
- [Integrations](#integrations): [Redmine](#redmine), [Zabbix](#zabbix), [3rd Party Connectors](#3rd-party-connectors)
- [Administration](#administration): [the admin UI](#the-admin-ui), [users and rights](#users-and-rights),
  [the audit log](#the-audit-log), [the outbound log](#the-outbound-log), [operations](#operations)

---

## Monitoring

### Sensors

Sensors are grouped in hosts. A host and each of its sensors have an importance, which notification rules can select by.

- **Scripts and devices report over plain HTTP:** `/event/<token>/<code>/<message>`, a code from 0 to 399 for success and from 400
  for a failure. The [README](../README.md#-reporting-to-pnmt) has examples.
- **Heartbeats** that must arrive in time, and **ping checks**.
- **Value checks** with regular expressions, and **several values in one JSON report**.
- **Timespan conditions** such as "one success daily at 8".
- Every sensor has a write token and a read token, and its page shows copyable examples. The sensor API answers exactly as it did
  in v0.12, which a recorded contract checks on every build. The UI's *Documentation* page lists every status code and sensor type.

### Advanced sensors

- **An app, a script or a service POSTs any JSON object** (up to 256 KiB) with the sensor's token, as often as it likes.
- **Shown readably with no setup:** lists of objects as tables, times in your zone, health words coloured. A history with a
  comparison, and charts of the values a person picks.
- **Rules decide whether all is well, without code:** `disk.free_gb` at least 5, none of `jobs[*].state` is `failed`,
  `backup.finished` newer than 26 h, the app's own `status` healthy.
- **Health formats work as they are:** the IETF health format, and Spring Boot's and ASP.NET's health endpoints.
- One evaluator judges every report in order and writes an event only when the verdict changes. An app that goes silent fails
  after its interval.

More: [docs/advanced-report.md](advanced-report.md) (the format) ·
[deploy/README.md](../deploy/README.md#advanced-sensors) · [docs/advanced-sensor-plan.md](advanced-sensor-plan.md)

### SSL certificates

- **A sensor reads the certificate of any `host:port`** and stores everything it says: names, issuer, key, fingerprints,
  extensions and the chain.
- **It fails days before expiry** and once expired. Where a sensor says so, it also fails when the certificate, its key or its
  issuer changes, until a person accepts the change.
- **Public CAs, private CAs added in PNMT, and self-signed certificates** pinned on first sight.
- PNMT reads only what the server sends: it never fetches a URL a certificate names.

More: [deploy/README.md](../deploy/README.md#ssl-certificates) · [docs/ssl-certificate-plan.md](ssl-certificate-plan.md)

### DNS zones

- A zone file is the reference, and every record is compared with what the resolvers answer.

### E-mail input

- **Reports from other tools become sensor events:** Synology HyperBackup, Veeam, Zabbix and the like. Mails are matched by
  sender, subject and body.
- **A rule can also ignore or delete mails.** With AI on, PNMT proposes rules for mails no rule handles, and a rule can be
  set to *AI every time*.
- The *Mail log* shows every mail and what became of it.

### IoT nodes over UDP

PNMT-UDP, on port 5028/udp, for devices with a radio budget.

- **A signed heartbeat in 9 bytes,** instead of about 1 KB of HTTP.
- **Signatures** from a static token to HMAC (SHA-256, SHA-1, MD5) and AES-CMAC, and **AES-CCM encryption**.
- **Commands** are delivered with the node's next packet, and **firmware updates** come from an uploaded `.bin`.
- The protocol is documented with live examples in the UI, and `pnmtd udp node` simulates a node.

More: [docs/udp-protocol.md](udp-protocol.md) (the protocol) · [deploy/README.md](../deploy/README.md#udp-nodes) ·
[docs/udp-iot-plan.md](udp-iot-plan.md)

### PNMT's own condition

These are sensors too, notified like any other:

- the uplink, with its history on its own page;
- the AI and its budget;
- the RouterOS and OS advisories;
- Redmine and Zabbix.

The uplink check needs a remote endpoint. [examples/digitalocean_function.py](../examples/digitalocean_function.py) is one, for a
[DigitalOcean Function](https://www.digitalocean.com/products/functions).

### Notifications

- **By e-mail, Pushover or webhook:** always, on a change, on failures, or on failures and the way back to OK.
- **To an address, a user, a group, or the responsible of the day:**
  - a user is reached at the e-mail address and Pushover key of their account, and a group at each member's;
  - the responsible comes from the [availability plan](#the-availability-plan) at the moment of the send, then the fallback, then a
    stand-in;
  - the message says why the person got it.
- **What a rule covers:** everything of at least one importance, or a host, with exclusions.
- **The Notification manager** shows what nothing covers, and the rules that reach nobody right now.
- **Pushover's priorities are a setting:** what a Low, Normal and High message is on the phone, up to Emergency, which repeats
  until it is acknowledged.

More: [deploy/README.md](../deploy/README.md#notifications-to-people) ·
[docs/notification-recipients-plan.md](notification-recipients-plan.md)

---

## Servers and the network

### Windows servers and PCs

For Windows Server 2016 to 2025, and Windows 10 Pro and 11.

- **One executable, joined with a token from the UI.** It reports over a WebSocket on PNMT's own port, so there is nothing to open.
- **What it reports:**
  - the system, hardware and network in detail;
  - services, grouped by who signed them;
  - every way in (RDP, WinRM, SMB, SSH, remote tools and so on), with the firewall;
  - every admin login and the failed ones;
  - the event log's errors;
  - changes, in readable form.
- **Diagnostics** (ping and DNS sets) run on the servers on a schedule or by hand. Each assignment is a sensor of its own.
- **PowerShell scripts from PNMT** run only where a local administrator allowed them. They run as accounts whose passwords never
  reach PNMT, or reach it only encrypted to the server.

More: [deploy/README.md](../deploy/README.md#windows) · [docs/windows-sensor-plan.md](windows-sensor-plan.md)

### Linux servers

For Debian 12 and 13, and also Proxmox VE and Ubuntu LTS.

- **One binary, installed with one line copied from PNMT.** It is downloaded from PNMT itself and checked by its SHA-256, and runs
  as a systemd service with a command line.
- **What it reports,** in Linux terms:
  - updates;
  - services, with timers and cron;
  - sshd and every other way in;
  - logins and sudo, from the journal;
  - the journal's errors.
- **Every `authorized_keys` file,** watched within seconds (see [the SSH key manager](#the-ssh-key-manager)).
- **Bash scripts** run as named accounts, only where root allowed them.

More: [deploy/README.md](../deploy/README.md#linux) · [docs/linux-sensor-plan.md](linux-sensor-plan.md)

### OS updates on Linux servers

- **A daily `apt-get update`** runs at a fixed time per server inside a window, also while PNMT is unreachable. Each server then
  reports every pending package, with its versions, its origin, and whether it is a security update.
- **Installing from PNMT:** `apt-get upgrade --with-new-pkgs` or `apt-get full-upgrade`, run non-interactively and keeping your
  configuration files.
  - The dialog lists what would be removed, and the server refuses if that changed since you looked.
  - PNMT sends only the kind and the mode, and the agent builds every command line itself.
- **A run can't be broken:** it runs in a systemd unit of its own and is never killed midway, also when the agent restarts or the
  connection is lost.
  - Every run has its full log, live and downloadable.
  - Upgrades someone ran on the server show up too.
- **The high-risk check** (optional) flags pending updates that fix vulnerabilities exploited in the wild, among the top 1 % of
  EPSS, or rated critical by the distribution.
  - It downloads the public sources whole: Debian's and Ubuntu's trackers, CISA's KEV and FIRST's EPSS. Nothing about the fleet
    leaves PNMT.
  - A finding turns the server's sensor bad until the update is installed, or someone acknowledges it for 1, 7 or 30 days.
- **The dashboard's *OS updates* card** sums it up: the servers with updates, the security updates, the high-risk ones, failed runs
  and stale lists.

More: [deploy/README.md](../deploy/README.md#os-updates-on-linux-servers) · [docs/linux-os-updates-plan.md](linux-os-updates-plan.md)

### The SSH key manager

- **Each key has a name and belongs to a user,** so PNMT shows whose key let whom in.
- **A key nobody registered** raises an event.
- **A departing colleague's keys** are removed from every server in one step, wherever the server's own
  `/etc/pnmt-agent/config.yaml` allows it.

### The agents' versions

- **PNMT chooses which version each platform's agents get:** its own by default, or another release, which can be fetched from
  GitHub or uploaded for a PNMT without internet.
- **Both agents update from PNMT** with `update`. The download is checked against PNMT's signed manifest.
- **They roll back automatically** when the new version doesn't run within 30 seconds, and by hand with `update --rollback`.
- **Only the server starts an update:** PNMT has no button for it. Choosing a version only changes what the next update installs.

More: [docs/agent-update-plan.md](agent-update-plan.md)

### MikroTik routers

For RouterOS 7.13 or later.

- **Status and events from a read-only user.** One pasted join script creates the `pnmt` user, which may read but not change
  anything.
- **Configuration backups with diffs:**
  - on a schedule of their own, or off;
  - optionally encrypted one way with an age key that never reaches PNMT.
- **Updates and remote commands:**
  - updates: download and reboot, or RouterOS's own install;
  - both run over SSH, or through PNMT's own WireGuard tunnel for routers behind NAT (`./init.sh --wireguard`);
  - the router admin's password is typed only for an update or a command run as the admin, and is never stored.
- **Security advisories:** MikroTik's release feed is read hourly. A router running a release older than one that fixes a critical
  security issue raises an alert.
- **Custom WireGuard tunnels** give technicians WinBox and SSH access to the routers.
- **Logins:** WinBox, WebFig, the API, SSH and the console, on the router's *Access* tab and watched by
  [the access watch](#the-access-watch).

More: [deploy/README.md](../deploy/README.md#mikrotik-routers-and-wireguard) · [docs/mikrotik-plan.md](mikrotik-plan.md) ·
[docs/mikrotik-backup-plan.md](mikrotik-backup-plan.md) · [docs/mikrotik-join-options-plan.md](mikrotik-join-options-plan.md)

### The network map

How the MikroTik routers, the hosts and the servers are connected, under *Monitoring → Network map*.

- **A scan reads what the routers see,** one router at a time, as the read-only `pnmt` user. It reads the interfaces with their
  links and SFP modules, the bridges and VLANs, the MAC table, the neighbors (MNDP, CDP, LLDP), ARP, the DHCP leases, the tunnels
  and the Wi-Fi clients. Nothing on a router changes.
- **Scheduling:** scans start by hand, for all routers, a location or one router, or on a schedule (every 14 days by default, once
  turned on).
- **PNMT suggests connections,** each with its evidence and a confidence:
  - routers that see each other;
  - WireGuard peers and tunnels between routers;
  - a server's MAC alone on a port;
  - Wi-Fi clients;
  - hosts behind a switch.
- **One cable is one suggestion,** however many VLANs run over it: VLAN interfaces and bridges count for their physical port.
- **Skip rules** leave rows out of the suggestions: by router or location, interface, type, VLAN, evidence or MAC prefix. Before
  saving, the form previews what a rule would change.
- **A person makes a suggestion a connection,** with a type (*BASE-T 1G*, *DAC 10G*, *Fiber SMF 10G*, *WireGuard* and so on). A
  connection the scans no longer see is marked, not deleted.
- **The map:**
  - the routers, and the hosts of chosen locations;
  - top-level locations as circles;
  - lines that grow with the speed;
  - routers stay where someone placed them.
- **Network interfaces:** the MACs the routers see, where each is attached, and the server adapter, router port or host each is
  matched to. PNMT discovers interfaces, not hosts.
- **Over MCP,** an AI client can ask for the path between two devices, its slowest hop and its single points of failure.

More: [deploy/README.md](../deploy/README.md#mikrotik-routers-and-wireguard) · [docs/network-map-plan.md](network-map-plan.md) ·
[docs/network-map-positions-plan.md](network-map-positions-plan.md) ·
[docs/network-suggestion-rules-plan.md](network-suggestion-rules-plan.md)

---

## Knowledge and the team

### Locations

- **Locations nest:** a site, its rooms, a rack. Each location gathers its hosts, on a page of its own.
- **Fields an Admin defines:** Markdown above all. A field can be inherited by sub-locations or marked sensitive.

More: [docs/locations-knowledge-plan.md](locations-knowledge-plan.md)

### The knowledge base

- **Pages** (`KB-42`) have tags, a status, and an edit history with diffs.
- **Linked to the rest of PNMT:** pages link to locations, hosts and sensors, and to each other, both ways. A map shows how pages,
  locations, hosts and sensors are connected.
- **Found** by the global search and a full-text search.
- **One editor for both pages and location fields:**
  - a visual editor, with VS Code's behind a switch;
  - images and files pasted in;
  - Mermaid diagrams and charts.
- **Social:**
  - the newest changes of two weeks are on top;
  - people like pages, and record that a page solved their problem;
  - a page's authors hear of it by e-mail if they want.
- **Marks:** a page says when it was written mostly with AI. A page marked sensitive never reaches any AI.
- **AI suggests tags,** only for pages a person declared free of credentials and personal data.
- **The files** lie in a folder on the Docker host, and are in the backups.

More: [deploy/README.md](../deploy/README.md#uploaded-files) · [docs/locations-knowledge-plan.md](locations-knowledge-plan.md) ·
[docs/knowledge-base-extensions-plan.md](knowledge-base-extensions-plan.md)

### Responsibilities

Who owns which area of work. They are information only: nothing in PNMT changes because of them.

- **Each responsibility has:**
  - a responsible and a deputy;
  - a long description, written as a knowledge base page is;
  - links to pages, locations and hosts, which then show it;
  - with Redmine, the projects and trackers each person handles.
- **A global responsible and deputy** cover what nothing else does.
- **The escalation order:** responsible, deputy, global responsible, global deputy. When someone is missing, the next one moves up.
- **The list:** a colour each, sortable by any column. A *Links* tab shows every link, the hosts and locations nobody owns, and
  changes many links at once.
- **The Overview** draws them on a map with their people and what they link. It is arranged by hand or with *Arrange* (a circle,
  clusters, a grid) for everyone, with notes.
- **Read over MCP and by Cookie.** Over MCP, everything *Responsibilities: Edit* can change on the page can be changed too.

More: [docs/responsibilities-plan.md](responsibilities-plan.md) ·
[docs/responsibilities-extensions-plan.md](responsibilities-extensions-plan.md) ·
[docs/responsibilities-mcp-and-map-plan.md](responsibilities-mcp-and-map-plan.md)

### The availability plan

It works like a team's Excel plan, under *Admin → Availability plan*.

- **A row per day,** with the responsible, the fallback, a comment and notes.
- **Count columns** such as *On site* and *Remote*, red below their minimum.
- **A column per person,** with the types you define: on site, home office, vacation, holiday, work travel, compensation.
- **Editing:** everyone in the plan edits their own column. *Fill* writes each person's usual week ahead, and a nightly fill keeps
  it filled.
- **The Status tab:**
  - days without a working responsible, and days below a minimum;
  - vacation days, and the share of days on site;
  - each person's days as responsible against their share;
  - a year calendar of the office, and who is away in the next 14 days;
  - CSV downloads for Excel.
- **It routes messages:** notification rules to *the responsible* and Cookie's reminders follow it. Outside working hours, Cookie
  reaches nobody. The working hours and holidays live here.
- The day's responsible can have a name of your own, such as *Hero*.

More: [docs/cookie-plan.md](cookie-plan.md) (where it began) · [docs/availability-extensions-plan.md](availability-extensions-plan.md)

### Licences & services

What IT pays for. It is off by default and turned on with `Licenses__Enabled=true`.

- **Licences, services and hardware.** Services include cloud, hosting, internet lines, phones, support contracts and domains.
- **What each item records:**
  - keys, encrypted and shown on request;
  - seats, prices, billing and terms;
  - ends of life and reviews;
  - owners, cost centres, locations and documents.
- **Sensors:**
  - an item on a host is a sensor there;
  - an item on no host needs none, and its owner is told by e-mail when it goes bad, before its date, when it renews by itself,
    and when a review is due.
- **Costs:**
  - each billing period's charge is recorded, and the coming months are forecast;
  - costs by category, provider, cost centre, location, host or owner;
  - what comes up, month by month.
- **Linked sensors** show a service's support details where its outage is handled.
- **Read over MCP and by Cookie,** but never the keys or the documents.

More: [deploy/README.md](../deploy/README.md#licences--services-optional-off-by-default) · [docs/licences-plan.md](licences-plan.md)

### Boards

Monitoring dashboards for a wall.

- **A URL of their own** under `/pnmt-board/`, which a reverse proxy can expose without the admin UI, or the other way round.
- **Who sees a board:** anyone, without signing in, from the IPv4 and IPv6 networks it names; signed-in users from anywhere.
- **1 to 4 columns of elements,** for example:
  - failing sensors and hosts;
  - Redmine's overview, the newest tickets, and the tickets received and solved today;
  - Zabbix's problems and their history;
  - the lounge, a clock, a note, who is on duty, expiring certificates;
  - a sensor's last message as large as fits;
  - an uploaded image or animated GIF.
- **A status bar** turns green, yellow or red by the board's own rules, and grey when it can't tell.
- **Fifteen themes,** picked from tiles. A board is set up on a full page with a live preview.
- **Messages:** a large message box (alert, warning, maintenance, information, all clear) goes to one or more boards, from
  *Admin → Debug* or from Cookie.

More: [deploy/README.md](../deploy/README.md#boards) · [docs/boards-plan.md](boards-plan.md) ·
[docs/board-messages-plan.md](board-messages-plan.md)

---

## AI, optional

All AI goes through [OpenRouter](https://openrouter.ai) with one key. Each part has a budget of its own, and every request is
logged with its cost. Outside text is screened for prompt injection first.

### AI proposals

- **Rules** for mails that no rule handles.
- **Ratings** of RouterOS releases.
- **Waiting for a person:** every proposal waits under *Decisions*.

More: [deploy/README.md](../deploy/README.md#ai-optional) · [docs/ai-operations-plan.md](ai-operations-plan.md)

### Cookie, the assistant

Cookie is off until `Cookie__Enabled=true`, and stays stopped until an admin starts it on *Admin → Cookie*.

- **The case book:**
  - a host's bad sensors and Zabbix problems are one case;
  - each watched Redmine ticket is a case too, with its timeline, deadlines and comments;
  - code keeps the ticket deadlines (a first answer within 4 working hours, an update every 14 days), with reminders and
    escalations. Every duty is off until it is switched on.
- **Cookie proposes, code decides:**
  - Cookie proposes Pushover messages and e-mails to people, tickets, notes on tickets, and notes on hosts and sensors;
  - code checks rights, limits, duplicates, mutes and suppressions;
  - **Seraph**, a second model, reviews everything that leaves PNMT or reaches people;
  - every action starts at *Propose* and waits for a person, until an admin sets it to *Auto*.
- **On the dashboard:** the overall state, and your personal summary of what needs you first.
- **The chat:**
  - ask Cookie or Seraph about what you may see;
  - with *Cookie: Instruct*, mute a host or suppress a kind of message;
  - an admin can give one message *powers*: Cookie then does what it asks, and Seraph can still stop it.
- **Learning from people:** people rate Cookie's decisions as *right* or *wrong*. Once a day, the ratings become lessons, and a
  lesson like "no e-mail about toner" becomes a rule that code enforces.
- **The big brother:** two strong models propose a fix for a problem nobody fixed for a working day. It is off until switched on.
- **The away watch:** a comment on a ticket whose assignee is away goes to the day's responsible or a chosen person, when Cookie
  judges that someone should act before the assignee is back.
- **Reads only,** through tools that check rights in code. It never reads a page or field marked sensitive.
- **Privacy:**
  - names, e-mail addresses and phone numbers become placeholders before anything leaves PNMT;
  - requests go only to zero-data-retention endpoints.
- **A budget of its own:** 30 USD a month by default. A round in which nothing changed costs nothing.

More: [deploy/README.md](../deploy/README.md#cookie-optional) · [docs/cookie-plan.md](cookie-plan.md) ·
[docs/cookie-powers-plan.md](cookie-powers-plan.md) · [docs/cookie-object-notes-plan.md](cookie-object-notes-plan.md)

### The access watch

It is off until an admin switches it on under *Admin → Cookie → Access*.

- **What is watched:**
  - the logins on servers with the agent, and sudo and su;
  - the logins on MikroTik routers;
  - sign-ins to PNMT, and MCP tokens.
- **Code compares each new login with the target's last 90 days.** What it has seen before stays quiet. What is new goes to
  Cookie, which judges it *normal*, *unusual*, *suspicious* or *alarming*, and code holds the verdict to the signals.
- **The contacts are told,** per verdict, by e-mail or Pushover, also outside working hours for what is alarming.
- ***Was this you?*** The account's owner gets an e-mail with a link. *No* makes the case alarming at once.
- **When Cookie can't judge,** code sends its own notice for a strong signal after 10 minutes.

More: [docs/cookie-access-plan.md](cookie-access-plan.md)

### The lounge

Cookie and Seraph, off duty, in one conversation that every signed-in user reads.

- **Two voices, each on its own model.** They talk about what happened lately, joke, disagree, and look things up when they argue.
- **Each has a conviction:** Cookie that a reboot is a fix, Seraph that nothing should change on a Friday.
- **Memory:** they remember and forget, with a half-life.
- **People join in** with *Lounge: Use*. Everyone can opt out of being talked about or e-mailed.
- **Running it:** it has Start and Stop of its own, a rhythm and a budget (10 USD a month by default).

More: [deploy/README.md](../deploy/README.md#cookie-optional) · [docs/lounge-plan.md](lounge-plan.md)

### The MCP server

It is off until `Mcp__Enabled=true`, at `/mcp`.

- **Your own AI client** (Claude Code, VS Code, Cursor) reads and works in PNMT with your rights, capped: never the admin flag, a
  *Manage* level, approvals or PNMT's settings.
- **Two tokens per person:**
  - read-only;
  - read & write, which lasts 12 hours and needs 2FA.
- **The tools** cover hosts, sensors, locations, the knowledge base, rules, mails, Redmine, Zabbix, routers, servers, the network
  map, responsibilities, licences and Cookie.
- **Deleting needs you each time:** a 10-minute window for at most 20 objects.
- **Nothing secret comes out:** no tokens, keys or passwords, and no page or field marked sensitive.
- **Everything is logged,** and 3 refused requests in 24 hours block the token and alert the admins.

More: [deploy/README.md](../deploy/README.md#mcp-optional) · [docs/mcp-plan.md](mcp-plan.md) ·
[docs/mcp-extensions-plan.md](mcp-extensions-plan.md)

---

## Integrations

### Redmine

- **The tickets of chosen projects in PNMT:**
  - an overview of the most important ones: urgent, unassigned, overdue, or linked to a failing sensor;
  - a search, and a page per ticket;
  - pages that open at once, from what PNMT read last, refreshed in the background.
- **Links:** tickets link to hosts, sensors, locations and knowledge base pages, whose panels list them. A failing sensor shows
  that someone is on it.
- **Read-only,** through one API key. Cookie's tickets and notes need a second key.

More: [deploy/README.md](../deploy/README.md#redmine-optional) · [docs/redmine-queue-outbound-plan.md](redmine-queue-outbound-plan.md)

### Zabbix

For Zabbix 6.0 or later.

- **What PNMT shows:**
  - the open problems, by severity and by host group or tag;
  - the hosts, and a page per host.
- **Zabbix hosts as sensors** of PNMT's hosts:
  - one fails from a chosen severity up, and notifies like any other sensor;
  - they are attached by hand, or in bulk from suggestions matched by name and address.
- **Read-only,** through an API token of a role that may call five methods.

More: [deploy/README.md](../deploy/README.md#zabbix-optional) · [docs/audit-redmine-zabbix-plan.md](audit-redmine-zabbix-plan.md) ·
[docs/zabbix-hidden-problems-plan.md](zabbix-hidden-problems-plan.md)

### 3rd Party Connectors

An outside system, such as a shop's back office, a ticketing system or a scheduler, runs one script (or one function in it) on one
server, with `POST /connector/v1/<slug>` and its key.

- **Arguments:** the admin exposes or fixes each argument, with patterns that must match the whole value.
- **Calling:**
  - the caller waits for the outcome, or takes a ticket;
  - each caller has one key, which can be shown again, and the networks it may call from.
- **Review:** the typesafe model can review every call against the connector's purpose and the admin's own questions.
- **Running:** calls are queued, one run per script and server at a time.
- ***Calls*** shows every call with its arguments, review, run and output.
- **The server's own policy stays final.**

More: [docs/connectors-api.md](connectors-api.md) (the API) ·
[deploy/README.md](../deploy/README.md#3rd-party-connectors) · [docs/connectors-plan.md](connectors-plan.md)

---

## Administration

### The admin UI

At `/pnmt-admin/`:

- **A dashboard** that answers "is anything wrong, and what exactly?" at a glance, with uptime bars for every host;
- **compact pages** for everything else, in light and dark, on a phone too;
- **a global search,** and the *Documentation* page: the sensor API, every status code and sensor type, and a guide per feature.

More: [docs/webui-migration-plan.md](webui-migration-plan.md)

### Users and rights

- **Groups and rights:** a level per area (such as *View*, *Edit* and *Manage*), or the admin flag.
- **Two-factor authentication** with any authenticator app.
- **Signing in:** the password at least every 30 days and the 2FA code once a day, both adjustable.
- **Lockout,** and recovery scripts on the server.

### The audit log

- **Every change, sign-in and refusal:** who, when, from where, and each field's old and new value.
- **Changes over MCP** are marked as such.
- **A *History* tab** on hosts and sensors, and a CSV export.

More: [docs/audit-redmine-zabbix-plan.md](audit-redmine-zabbix-plan.md)

### The outbound log

Everything PNMT sends, under *System health → Outbound*.

- **Notifications:** every e-mail, Pushover message and webhook, with what it was for.
- **Redmine calls,** and what is going out right now.
- **Details:** each record opens with the request and the answer. PNMT's own secrets are masked.

More: [docs/redmine-queue-outbound-plan.md](redmine-queue-outbound-plan.md)

### Operations

- **One image and PostgreSQL,** run with Docker compose from the `deploy/` folder.
- **Scripts** for backup, restore, update and users.
- **Behind a reverse proxy:** Caddy, nginx or Traefik.

More: [deploy/README.md](../deploy/README.md) · [deploy/UPGRADING-1.0.md](../deploy/UPGRADING-1.0.md) (from v0.12)
