# PNMT 1.30: licences & services, what IT pays for, off until you turn it on

## What's new

### Licences & services (off by default)

- **A new page, *Licences & services*,** under *Knowledge*, for everything IT pays for: **licences** (software, subscriptions,
  support), **services** (cloud and SaaS, hosting, internet lines, phones, support contracts, domains, consulting) and
  **hardware** (purchases, leases, warranties). Each is an *item*, numbered `LIC-12`, `SVC-31` or `HW-40`.
- **It is off until you turn it on:** `Licenses__Enabled=true` in `.env`. Off, nothing of it is usable or visible: no menu entry,
  no page, no API (404), no rights area, no sensors, no panels, no e-mails, no MCP tools. See *Turning it on* below.
- **The tabs:** *Overview* (the cost and licence overview), *Licences*, *Services*, *Hardware* (sortable, searchable tables with
  their filters in the URL), *Upcoming* (expiries, renewals, cancellation deadlines, warranty ends, ends of life and reviews,
  month by month with the cost), *Costs* (by category, type, provider, manufacturer, cost centre, location, host, owner or item,
  spent and forecast, every charge, CSV) and *Templates* (templates, categories, and renaming manufacturers, vendors and cost
  centres).
- **An item holds** its product (name, product, edition or model, manufacturer, vendor or provider), its licence key and product
  key (encrypted, shown on request and audited), its seats, its price (one-time, recurring or paid by use; in total or per seat),
  its billing period and its term (expiry, renewal by hand or automatic, notice period), its end of life (from its template or its
  own), its review rhythm, its category, cost centre, location and owner, a note, support details and documents (invoices,
  contracts, licence and key files).
- **Its state** is its own: valid (245), renewed (246, once), about to expire or its cancellation deadline near (445), expired
  (446), its end of life near (447) or passed (448), more seats used than bought (449).
- **On a host, an item is a sensor** that shows its state, and the host's notification rules tell the host's people. An item on
  no host has no sensor and needs none.
- **The owner is told by e-mail** when an item goes bad, a few days before the date, when it renews by itself, and when its
  review falls due, whether it is on hosts or not. Each person chooses on *Account → Notifications*. Items nobody owns are told to
  whoever with *Edit* chooses *Licences and services nobody owns*.
- **PNMT records each billing period's charge itself,** so *spent* is right without typing in every invoice; an invoice corrects
  the amount. The forecast is today's prices ahead, and marks what depends on a renewal by hand. Totals are per currency, never
  converted.
- **Linked sensors:** a service linked to the sensors that watch it (an internet line's ping) shows the provider's support contact,
  customer number and SLA on those sensors' pages.
- **Everywhere else:** a panel on the host's and the location's page, *Assign a licence or service…* in the host's menu, items in
  the global search (by number, name, product, reference, customer number, or a key's last five characters), and three MCP tools
  for MCP and Cookie: `licenses_list`, `license_get`, `licenses_costs` (never keys or documents).

## 1.30.1

- **A test of the MCP tools no longer fails on a busy build machine.** It checked that one can't cancel someone else's RouterOS
  command on a command that was waiting to run, and PNMT's job runner sometimes took that command before the check. The test now
  uses a running command.
- **A Redmine test no longer fails since 2026-11-28.** Its recorded tickets were last changed on 2026-11-14, and 14 days later
  they counted as *no update for 14 days*. The test now gives every ticket it doesn't set itself a change a day ago.
- PNMT itself is unchanged: both fixes are in the tests.
- **Updating from 1.30.0:** `./update.sh --tag v1.30.1`. No migration, nothing to configure.

## Updating from 1.29

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.30.1`. It takes a backup first.

- **One migration:** eight tables for licences & services, and a column on the file uses. It runs whatever the switch says, and
  adds the eleven categories (*Software*, *Cloud & SaaS*, *Connectivity*, …). Nothing else changes until you turn the feature on.
- **With the feature off** (the default), the update adds nothing anyone sees.

### Turning it on

1. Set `Licenses__Enabled=true` in `deploy/.env` (the example's *Licences & services* section has the other settings).
2. Restart PNMTD: `docker compose up -d` in `deploy/`.
3. Grant *Licences & services* to the groups that need it (*Admin → Groups*): *View* sees everything but keys and documents, the
   costs included; *Details* shows keys and downloads documents; *Edit* changes items; *Manage* keeps the templates and categories,
   and deletes. Admins have it all.

## Good to know

- **A rule for *everything*** tells about the licences and services on hosts too, once the feature is on: their sensors' events are
  sensor events. A rule by host tells about the items on that host.
- **Turning it off again** keeps every item, charge, document, key, grant and choice. Only the item sensors on hosts are removed
  (with their events), and they are made again from the assignments when it is turned back on. A notification rule or board
  element made for one single item sensor goes with it; rules by host, importance or for everything aren't affected.
- **Turned on again after a while,** PNMT catches up: the automatic renewals and the billing periods of the months it was off, at
  today's prices, and one notice per item whose state changed meanwhile.
- **Documents are files in the files folder,** as the knowledge base's are, and in `files.tar` of the backups. Licence and key files
  may contain keys: keep the backups as safe as the database dump. The keys themselves need the key ring (`keys.tar.gz`).
- **The customer number on a linked sensor's page** lets a colleague call the provider in an outage; whoever sees the sensor sees it.
- **The audit log** has a category *Licences & services*: items, their hosts, links, renewals, charges and documents, keys shown,
  documents downloaded, and what turning the feature off and on did to the item sensors.
- **The plan:** docs/licences-plan.md, with, in *As built*, where the build differs from it.
