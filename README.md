![PNMT](docs/logos/3x/Artboard%201@3x.png)

# PNMTD - Personal Network Monitoring Tool

**Self-hosted monitoring for a small IT team:** hosts and sensors, servers and MikroTik routers, e-mails and DNS zones. Also a
knowledge base, the team's plan, wall boards and an optional AI assistant. Useful on its own, or as oversight of the main
monitoring.

![Build](https://github.com/muqiuq/PNMTD/actions/workflows/dotnet.yml/badge.svg)
[![Docker](https://img.shields.io/docker/pulls/uisach/pnmtd?label=Docker&style=flat)](https://hub.docker.com/r/uisach/pnmtd)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue?style=flat)](LICENSE)

**[Features](#-features)** · **[Features in detail](docs/features.md)** · **[Self-hosting](#-self-hosting)** ·
**[Reporting to PNMT](#-reporting-to-pnmt)** · **[Development](#-development)**

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/dashboard-dark.png">
  <img src="docs/screenshots/dashboard.png" alt="The dashboard">
</picture>

## ⭐ Features

Each feature in detail: **[docs/features.md](docs/features.md)**.

### 📈 Monitoring

- **[Sensors](docs/features.md#sensors)** grouped in hosts: HTTP events from any script, heartbeats, pings, value checks, and
  timespans such as "one success daily at 8".
- **[Advanced sensors](docs/features.md#advanced-sensors):** an app POSTs any JSON, and rules decide whether it's OK, without
  code.
- **[SSL certificates](docs/features.md#ssl-certificates):** expiry and changes of any `host:port`, with private CAs and pinning.
- **[DNS zones](docs/features.md#dns-zones):** every record of a zone file is compared with what the resolvers answer.
- **[E-mail input](docs/features.md#e-mail-input):** reports from Veeam, HyperBackup, Zabbix and others become sensor events.
- **[IoT nodes over UDP](docs/features.md#iot-nodes-over-udp):** a signed heartbeat in 9 bytes, with encryption, commands and
  firmware updates.
- **[Notifications](docs/features.md#notifications)** by e-mail, Pushover or webhook, to an address, a user, a group, or the
  day's responsible.

### 🖥️ Servers and the network

- **[Windows](docs/features.md#windows-servers-and-pcs) and [Linux servers](docs/features.md#linux-servers):** one agent each
  reports the system, its services, every way in and every login. Scripts run only where the server allows them.
- **[OS updates on Linux](docs/features.md#os-updates-on-linux-servers):** pending packages, installs from PNMT, and alerts for
  fixes of exploited vulnerabilities.
- **[The SSH key manager](docs/features.md#the-ssh-key-manager):** whose key let whom in, and a departing colleague's keys
  removed everywhere in one step.
- **[MikroTik routers](docs/features.md#mikrotik-routers):** status, backups with diffs, updates and commands over SSH or
  WireGuard, and security advisories.
- **[The network map](docs/features.md#the-network-map):** scans of the routers suggest how routers, hosts and servers are
  connected.

### 📚 Knowledge and the team

- **[Locations](docs/features.md#locations)** that nest (a site, a room, a rack), with fields of your own.
- **[A knowledge base](docs/features.md#the-knowledge-base)** with history, links, a map and a full-text search.
- **[Responsibilities](docs/features.md#responsibilities):** who owns which area, with a deputy, an escalation order and a map.
- **[The availability plan](docs/features.md#the-availability-plan):** who works where on each day, and who is responsible.
- **[Licences & services](docs/features.md#licences--services)** (off by default): what IT pays for, with keys, renewals and
  costs.
- **[Boards](docs/features.md#boards):** dashboards for a wall in fifteen themes, shown without sign-in on networks you choose.

### 🤖 AI, optional

- **[Cookie](docs/features.md#cookie-the-assistant), the assistant,** watches problems and tickets. It proposes messages and
  tickets, which a second model and a person review.
- **[The access watch](docs/features.md#the-access-watch)** judges every new login on servers, routers and PNMT.
- **[The lounge](docs/features.md#the-lounge):** Cookie and Seraph, off duty, talk about the day.
- **[The MCP server](docs/features.md#the-mcp-server):** your own AI client works in PNMT, with your rights.
- **[AI proposals](docs/features.md#ai-proposals)** for mail rules and ratings of RouterOS releases, through OpenRouter, with a
  monthly budget.

### 🔌 Integrations

- **[Redmine](docs/features.md#redmine):** tickets linked to hosts, sensors and pages.
- **[Zabbix](docs/features.md#zabbix):** its problems, and Zabbix hosts as PNMT's sensors.
- **[3rd Party Connectors](docs/features.md#3rd-party-connectors):** an outside system runs one approved script on one server.

### 🔐 Administration

- **[The admin UI](docs/features.md#the-admin-ui):** a dashboard that answers "is anything wrong?", in light and dark, on a phone
  too.
- **[Users and rights](docs/features.md#users-and-rights):** groups, a level per area, and two-factor authentication.
- **[The audit log](docs/features.md#the-audit-log)** of every change, field by field, and
  **[the outbound log](docs/features.md#the-outbound-log)** of everything PNMT sends.
- **[Operations](docs/features.md#operations):** one image, PostgreSQL, and scripts for backup, restore, update and users.

<p>
  <img src="docs/screenshots/sensor.png" alt="A sensor with its URLs and events" width="68%">
  <img src="docs/screenshots/phone.png" alt="The dashboard on a phone" width="22%">
</p>

## 🔧 Self-hosting

PNMT runs as two containers, the app and PostgreSQL, from the `deploy/` folder. Docker with compose is all it needs.

```sh
git clone https://github.com/muqiuq/PNMTD.git /srv/pnmtd
cd /srv/pnmtd/deploy
./init.sh --url https://pnmt.example.com   # writes .env, shows the first admin's password once
docker compose up -d
./info.sh
```

Then sign in at `https://pnmt.example.com/pnmt-admin/` (or `http://<host>:5001/pnmt-admin/`).

- **Operation, backups, users, reverse proxies (Caddy, nginx, Traefik) and every optional feature:**
  [deploy/README.md](deploy/README.md).
- **Upgrading from v0.12** (two containers, SQLite): [deploy/UPGRADING-1.0.md](deploy/UPGRADING-1.0.md). Sensor URLs, data and
  passwords carry over.
- **What's new in each version:** the release notes, [docs/release-notes-1.30.md](docs/release-notes-1.30.md) and older.

## 📡 Reporting to PNMT

Every sensor has a write token and a read token, which its page in the UI shows with copyable examples. No sign-in is needed: the
token is the secret.

```sh
# report: a code (0–399 is success, from 400 a failure) and a message
curl "https://pnmt.example.com/event/<write token>/200/backup%20done"
curl --data "backup done, 12.4 GB" "https://pnmt.example.com/event/<write token>/200"

# read the last state: json, raw ("True;message") or property
curl "https://pnmt.example.com/read/<read token>/raw"

# an Advanced sensor: any JSON object, judged by the sensor's rules
curl -H "Authorization: Bearer <write token>" --data '{"status":"ok","disk":{"free_gb":412.5}}' https://pnmt.example.com/report
```

The sensor API answers exactly as it did in v0.12, which a recorded contract checks on every build. The UI's *Documentation* page
lists every status code and sensor type.

- **JSON reports:** [docs/advanced-report.md](docs/advanced-report.md).
- **UDP nodes:** [docs/udp-protocol.md](docs/udp-protocol.md). `pnmtd udp node` simulates one.
- **MikroTik routers** join with a script from their *Setup* tab:
  [deploy/README.md](deploy/README.md#mikrotik-routers-and-wireguard).
- **Windows and Linux servers** join with a token from *Add server*:
  [deploy/README.md](deploy/README.md#servers-with-an-agent).

## 🛠 Development

- **Stack:** .NET 10, PostgreSQL, and Vue 3 with PrimeVue.
  - The projects are `PNMTD`, `PNMTD.Lib` and `PNMTD.WebUI`.
  - The agents, `PNMTD.Sensor.Windows` and `PNMTD.Sensor.Linux`, share `PNMTD.Sensor.Core` and build on macOS and Linux too.
  - `./build-sensor-windows.sh` and `./build-sensor-linux.sh` build the agents' executables into `dist/` (`x64` or `arm64` for one).
- **Node runs only in containers:** `tools/npm.sh` and `start.sh` use Podman by default (`ENGINE=docker` works too). Never on the
  host.
- **Labs:** `tools/linux-lab.sh` runs Debian and Ubuntu servers in Podman for the Linux agent, and `tools/chr-lab.sh` runs MikroTik
  CHR routers.

```sh
./start.sh                  # build and run on http://localhost:7327/pnmt-admin/ (dev.env holds the admin password)
SEED=1 ./start.sh           # …with demo data: 50 hosts, 300 sensors, routers, servers, locations and pages
WIREGUARD=1 ./start.sh      # …with the WireGuard gateway, for the CHR routers to join
WEB=dev ./start.sh          # …plus Vite with live reload on http://localhost:5173/pnmt-admin/ (boards: :5174/pnmt-board/<token>)
./run-tests.sh              # everything: .NET, web, the sensor contract, deploy/, the v0.12 upgrade, browser tests
./stop.sh                   # stop it all (--data also offers to delete the volumes)
```

`start.sh` and `run-tests.sh` list their other options at the top. Each feature's design and decisions are in its plan in `docs/`,
linked from [docs/features.md](docs/features.md).

## 🧸 Motivation

- I was using uptimerobot as oversight monitoring, but needed more features, especially the DNS zone monitoring.
- I needed to automatically process e-mails from secondary monitoring tools (like Synology NAS HyperBackup, Zabbix, …).
- Improve my C# skills.

If you like what we're building and want to contribute, you're warmly welcome to **join the effort**!

## License

[GNU Affero General Public License v3.0](LICENSE)
