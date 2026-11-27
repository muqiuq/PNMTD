# Plan: Licences & services: what IT pays for, for which hosts and sites, until when, at what cost, and with its documents

| | |
|---|---|
| Status | **Built (§20 *As built*). Released in 1.30.0.** Plan revision 3, with all of §17's recommendations taken. Revision 2 widened revision 1's licences to everything IT pays for: **services** and **hardware** besides licences, **items on no host**, **costs** recorded and forecast, and **the cost and licence overview**. Revision 3 puts the whole feature behind **a switch, `Licenses__Enabled`, off by default**: while it is off, nothing of it is usable or visible (§13). And **an item on no host has no sensor:** the item keeps its state itself, and its owner is told by e-mail (§5.2, §5.5). §0 marks what each request and each revision brought. The open questions of §17 come first; each has a recommendation |
| Date | 2026-11-27 (plan); built 2026-11-27 |
| Scope | **A page *Licences & services*** in the menu, with tabs: the cost and licence overview; a sortable, searchable table each for licences, services and hardware; what comes up in the next months; the costs by category, provider, cost centre, location or owner; and the templates. **An item** is a licence, a service or hardware. It holds its product or service (name, product name, manufacturer, vendor or provider), its keys, its price (one-time, recurring or variable), its billing and its term, its review, its category, cost centre, location and owner, a note and its documents. **It is assigned to any number of hosts, or to none.** On each of its hosts it appears as a sensor. **An item on no host needs no sensor:** it keeps its state itself, and its owner is told by e-mail when it turns bad, before it expires and when a review is due. The state turns bad when the item expires, a set number of days before, and before the product's end of life. **Costs** are recorded per billing period, by PNMT or by hand, and forecast from the prices. **Templates** fill in a new item's product and defaults. **Documents** (PDF, text, Markdown, images, `.key`, `.lic`, …) are stored in the files folder, as the knowledge base stores its files. **All of it is off until `Licenses__Enabled=true`** (§13) |
| Not in scope | Counting installations from the hosts' software (the agents report no software list). Converting currencies. Accounting: VAT, bookkeeping, paying invoices, depreciation, budgets (§18). An inventory of devices: stock, lending, serial numbers beyond the reference field. Importing items from a vendor's portal or a CSV file. Previews of documents. Editing a document in PNMT. Encrypting the documents on disk (§12, decision 17). Users and the per-user licences of a directory (Entra ID, AD). Turning the feature on or off without a restart |
| Depends on | **The sensor pipeline:** `SensorService`, `SensorEventWriter`, `MonitoringState`. **The SSL certificate sensor** as the pattern of a sensor PNMTD evaluates itself (docs/ssl-certificate-plan.md §5, §15). **Diagnostics assignments** as the pattern of one managed sensor per pair (`DiagnosticAssignmentEntity`). **People's notifications** (`UserNotifications`, docs/knowledge-base-extensions-plan.md §10) for the owner's e-mails. **The integrations' switches** as the pattern of a feature the deployment turns on: `SystemInfo.Redmine` and `NavItem.feature` for the menu, `ToolSpec.Available` for the tools, a task's `Configured` for *System health*. **The files folder** (`FileStore`, `file_uses`, docs/locations-knowledge-plan.md §5.5, §8.5) and **board images** as the pattern of files only their owner serves. **The connector keys** as the pattern of a secret encrypted at rest and shown on request (`ConnectorKeyProtector`). **Locations** (`LocationEntity`, a tree by `ParentId`; `HostEntity.LocationId`) for where a service is used and for the costs by location. **Users** as the owner, as a responsibility's responsible (`ResponsibleId`, `PersonPicker`). **The responsibilities' list** as the pattern of a sortable list kept in the URL (docs/responsibilities-extensions-plan.md §3). **The availability plan's columns** (`MonthColumns.vue`) as the pattern of a hand-made SVG chart |
| Target | 1.30.0 (a migration, so a minor version; 1.29.0 is out) |

> **Off by default (§13).** The whole feature is behind **`Licenses__Enabled`**, false unless set in `.env`. While it is off, nothing of it is usable or visible: no menu entry, no page, no API (404), no rights area, no sensors, no panels on hosts and locations, no strips on sensors, no search results, no e-mails, no MCP tools, no background work. Its data stays as it was, and is all there again when it is turned on. An installation that updates to 1.30 gets empty tables and nothing anyone sees.

**Words.**

- ***An item*** is anything this page keeps: a licence, a service or a piece of hardware. The plan says *item* for short. The UI never does: it says the type's name, or *licences and services*.
- ***The type*** says which of the three it is:
  - ***a licence*** is one purchase of a right to use software: one key, one contract, one subscription. Ten keys bought one by one are ten licences. One volume key for 50 devices is one licence with 50 seats;
  - ***a service*** is what a provider delivers for a price: a cloud or SaaS subscription, hosting, an internet line, mobile phones, a support or maintenance contract, a domain, consulting or training;
  - ***hardware*** is a device bought or leased, with its price, its warranty and its end of support. It records what the device costs; it is not an inventory of what is in the rack.
- ***A template*** is a product or service as PNMT knows it: type, manufacturer, product name, edition, vendor, category, and the defaults a new item starts with. It also carries the product's **end of life**.
- ***The manufacturer*** makes the product (Microsoft, Veeam, Dell). ***The vendor*** sold it (a reseller, a distributor, or the manufacturer itself). For a service the vendor is ***the provider*** (Swisscom, Hetzner), and the UI says so.
- ***The category*** is what the money is for (*Software*, *Cloud & SaaS*, *Connectivity*, …): a short list an Admin keeps, and the first axis of the costs (§3.1). ***The cost centre*** is whose budget pays for it: a word, as the manufacturer is.
- ***The owner*** is the PNMT user who looks after an item: renews it, reviews it, is asked about it.
- ***An assignment*** is an item on a host; ***a host sensor*** (or ***item sensor***) is the sensor each assignment has on its host. An item on no host has no sensor (§5.2). ***A linked sensor*** is an existing sensor that watches a service (the internet line's ping), linked to it (§5.4).
- ***The owner's notices*** are the e-mails PNMT sends an item's owner about it: a warning, a reminder, an automatic renewal, a review due. They are people's notifications, as the knowledge base's *liked* and *used* are, and need neither a sensor nor a rule (§5.5).
- ***The switch*** is `Licenses__Enabled`. ***On*** and ***off*** in this plan mean the switch, not a sensor's *Enabled* (§13).
- ***The term*** is how long the item runs: until ***the expiry*** (`ExpiresOn`), or without end. ***A renewal*** moves the expiry forward. ***The notice period*** is how long before the expiry an automatic renewal must be cancelled; its last day is ***the cancellation deadline***.
- ***The billing period*** is how often the item is paid for (monthly, yearly, …). It needn't be the term: a 36-month contract paid monthly has a term of 36 months and a billing period of one.
- ***A charge*** is one recorded cost: a billing period's price, a purchase, an extra, a credit (§6.2). ***Spent*** is the sum of the charges. ***The forecast*** is what today's prices will cost in the coming months (§6.4). ***Per month*** and ***per year*** are the running cost of what is active today (§6.1).
- ***End of life (EOL)*** is the day the manufacturer stops supporting the product (no more updates), or the provider discontinues the service (*end of service*). It belongs to the product, so it is on the template, and an item follows it unless it sets its own.
- ***The verdict*** is the item's state: OK, or the worst of its findings (§4). The item keeps it itself (§3.3); its host sensors, where it has any, show the same verdict.
- ***A review*** is a person confirming that an item is still right: still needed, on the right hosts, at the right price.
- ***Seats*** are how many units an item covers (devices, users, cores, lines, …). ***Used seats*** are what its assignments count, for items whose seats are counted on hosts.
- **Spelling and code.** Everything a person reads says ***licence*** (the noun) and ***licensed*** (the verb), as the docs say *colour*. Code, tables, JSON, API paths, tool names and the rights area say `license`, as the code says `Color`. **In code, every item is a `license`,** told apart by `Type`. That matches the request (*a licence can also be a service*) and keeps one word for one area, not three (decision 2).

---

## 0. Features at a glance

Everything this plan builds. **From** says where each feature comes from:

- **A:** the first request (§2, R1–R18);
- **A2:** the second request: services, items on no host, all IT costs, the cost and licence overview (§2, R19–R22);
- **A3:** the third request: the switch, off by default; no sensor for an item on no host (§2, R45–R47);
- **+:** worked out in revision 1;
- **+2:** worked out in revision 2;
- **+3:** worked out in revision 3.

| # | Feature | From | § |
|---|---|---|---|
| | ***On, or not at all*** | | |
| 1 | **The switch `Licenses__Enabled`, off by default.** Off, nothing of the feature is usable or visible: no menu entry, page, API, rights area, sensors, panels, strips, search results, e-mails, tools or background work | A3 | §13.1, §13.2 |
| 2 | **Off loses nothing:** the items, charges, documents, keys, grants and choices stay; turned on again, it is as it was, the host sensors made again from the assignments | +3 | §13.3 |
| | ***The page*** | | |
| 3 | **A page *Licences & services* in the menu, with tabs:** *Overview*, *Licences*, *Services*, *Hardware*, *Upcoming*, *Costs*, *Templates* | A, A2, +2 | §8.1 |
| 4 | **The *Overview* tab, the cost and licence overview:** the running cost per month and per year, the forecast, what was spent, what needs attention, the cost per month, the costs by category and by provider, seats, ends of life, and *Worth a look* | A2; +2 (the content) | §8.2 |
| 5 | **The *Licences* tab:** a table of all licences, **sortable** by every column and **searchable**, with the sort and the search in the URL | A | §8.3 |
| 6 | **The *Services* and *Hardware* tabs:** the same table, with the columns each type needs (provider, cancellation deadline and location; host and warranty) | A2 (services); +2 (hardware) | §8.3 |
| 7 | **The *Upcoming* tab:** the coming months' expiries, renewals, cancellation deadlines, warranty ends, ends of life and reviews, with the cost per month; what is overdue on top | + | §8.4 |
| 8 | **The *Costs* tab:** costs by category, type, provider, manufacturer, cost centre, location, owner or item, per month or per year, spent and forecast; every charge listed; CSV | A2; +2 (the detail) | §8.5 |
| 9 | **The *Templates* tab:** templates for product name, vendor and manufacturer, and now type, category and a service's defaults; the categories; renaming manufacturers, vendors and cost centres | A; +, +2 | §8.6 |
| | ***The items*** | | |
| 10 | **Three types: licence, service, hardware** | A2 (service); +2 (hardware) | §3.3 |
| 11 | **The fields the request names:** name, product name, manufacturer, vendor, licence key, product key, price, note | A | §3.3 |
| 12 | **More fields:** a number (`LIC-12`, `SVC-31`, `HW-40`), edition or model, licensed to, a reference (order, contract or serial number), the customer number, the portal's URL, the start | +, +2 | §3.3 |
| 13 | **A service's support:** the provider, a support contact, a support URL, the SLA | +2 | §3.3 |
| 14 | **Kinds per type** (perpetual, subscription, contract, support, usage, one-off, purchase, lease, trial, free, other), each with its defaults and its words | +, +2 | §3.3 |
| 15 | **Category, cost centre, location and owner** for every item | +2 | §3.1, §3.3 |
| 16 | **The note,** scanned for credentials, which suggests the key fields instead | A (note); + (scan) | §3.3 |
| 17 | **Keys encrypted at rest,** masked, shown on request (audited) with a copy button; their **last five characters** stay findable | A (keys); + | §3.4 |
| | ***Hosts and sensors*** | | |
| 18 | **Assigned to hosts, one item to many hosts** | A | §5.1 |
| 19 | **On each of its hosts, an item appears as a sensor** | A | §5.3 |
| 20 | **An item needn't be on a host, and then has no sensor:** everything works without one | A2; A3 | §5.2 |
| 21 | **The item keeps its state itself;** its host sensors show it, and nothing about an item needs a sensor | +3 | §3.3, §5.5 |
| 22 | **Linked sensors:** the sensors that watch a service (the line's ping, the SaaS's HTTP check) on its page; the provider, support contact and customer number on theirs | +2 | §5.4, §8.9 |
| 23 | **Seats and quantities,** counted on hosts or not, and **over-assignment** as a finding | +, +2 | §3.3, §4 |
| 24 | **The item sensor's page:** a status strip linking to the item; its name and host follow the item; *Unassign…* instead of *Delete* | + | §5.3, §8.9 |
| 25 | **The host's page and the location's page** list their items | +, +2 | §8.8 |
| | ***Dates and states*** | | |
| 26 | **An item can expire, and it goes bad when it does,** and so do its sensors | A | §4 |
| 27 | **It goes bad before it expires, and before its product's end of life** | A | §4 |
| 28 | **The item's state (the verdict),** worst finding first, wherever the item is named | + | §4 |
| 29 | **End of life on the template,** followed by every item of the product unless it sets its own, with a warning period of its own (90 days by default) | + | §3.2, §4 |
| 30 | **A recurring item's term:** when it expires, how often it is renewed, how many days before it warns | A | §3.3, §5.6 |
| 31 | **When it was last reviewed;** a rhythm, ***Mark reviewed…***, and the due reviews listed and told to the owner, but failing no sensor | A; +, +3 | §5.7 |
| 32 | **Automatic renewal** with a **notice period** and its cancellation deadline, warned about on request (on by default for contracts) | +, +2 | §5.6 |
| 33 | ***Renew…*** and the history of the renewals | + | §5.6 |
| 34 | **Its life:** *active*, *cancelled* (runs to its end, not renewed), *retired* (kept for the record, on no host); deleting is for *Manage* | + | §5.8 |
| | ***Notifying*** | | |
| 35 | **The host sensors notify through the existing rules,** with nothing new to set up; sensor messages never carry prices | + | §5.5 |
| 36 | **The owner is told by e-mail,** on hosts or not: when the item goes bad, a reminder before the date, an automatic renewal, a review due. Each person's choice is on *Account → Notifications* | +3 | §5.5 |
| 37 | **Items nobody owns:** whoever with *Edit* chooses so hears about them; an item nobody hears about is *worth a look* | +3 | §5.2, §5.5 |
| | ***Money*** | | |
| 38 | **The price is one-time or recurring,** and also **variable** (paid by use) | A; +2 | §3.3, §6.1 |
| 39 | **The billing period apart from the term** (a 36-month contract paid monthly) | +2 | §3.3, §6.1 |
| 40 | **A price in total or per seat** (25 users at EUR 12.50 a month) | +2 | §6.1 |
| 41 | **Charges:** each billing period recorded by PNMT; extras and credits by hand; estimates for usage until the invoice is in; the invoice attached to its charge | +2 | §6.2, §6.3 |
| 42 | **The forecast** of the coming months, with what depends on a manual renewal marked | +2 | §6.4 |
| 43 | **Costs by location and by host,** shared by seats when an item is on several hosts | +2 | §6.5 |
| 44 | **Totals per currency,** never converted; one currency per chart | +, +2 | §6.6 |
| 45 | ***Worth a look:*** unused seats and what they cost, items paid past their end of life, items nobody is notified about, estimates without an invoice, items without a price, overdue reviews | +2 | §6.7 |
| | ***Documents*** | | |
| 46 | **Documents attached** (txt, PDF, images, md, key, lic, …), stored in the files folder as the knowledge base's files | A | §7 |
| 47 | **Documents only the item serves:** their own upload and download, kinds (invoice, contract, quote, licence file, key file, warranty, other), an invoice linked to its charge, never reachable through the knowledge base's file URL, each download audited | +, +2 | §7 |
| | ***Everywhere else*** | | |
| 48 | **A rights area, *Licences & services*:** *View* (including the costs), *Details* (keys and documents), *Edit*, *Manage* | + | §9 |
| 49 | **The audit and a *History* tab** for each item; the generic history no longer shows these changes to people without the area | + | §10 |
| 50 | **The global search finds items** by number, name, product, manufacturer, provider, reference, customer number and a key's last five characters | + | §8.10 |
| 51 | **MCP and Cookie:** `licenses_list`, `license_get`, `licenses_costs`, never with keys or documents | +, +2 | §11 |
| 52 | ***Duplicate***, to make the next item of the same kind (without its keys) | + | §8.7 |
| 53 | **Windows activation, matched:** a host whose Windows agent reports a product key's last five characters is matched to the licence with that key (optional phase L8) | + | §8.11 |
| 54 | **The seed, the in-app documentation, the README, the settings table and release notes 1.30,** which say how to turn it on | +, +3 | §13, §16 |

---

## 1. Today (checked in the code)

### 1.1 What there is to build on

- **No licence or cost feature exists.** "Licence" appears in docs/ only about npm packages, and in the Windows agent's activation (below).
- **A sensor's state is the code of its last event:** up to 399 is *OK*, above is *Failing* (`EventEntityPoco.END_OF_SUCCESS_CODES`). There is no warning state, so *about to expire* is a failure, as the SSL sensor's 485 is.
- **Sensors PNMTD evaluates itself** follow the certificate sensor (`PNMTD/Certificates/`):
  - a pure verdict (`CertificateVerdict.Evaluate`) returns a code, a message and a key;
  - a hosted service (`CertificateChecker`) evaluates every minute behind a `RunGuard`, and writes `LAST_CERTIFICATE_CHECK` for *System health → Background tasks*;
  - an event is written only when the key changes, through `SensorEventWriter.WriteAsync(db, sensorId, code, message, source, ct)`, in a transaction (it takes `pg_advisory_xact_lock`);
  - the side table keeps `LastCode` and `LastVerdictKey`.
- **One managed sensor per pair exists:** a diagnostics set assigned to a server has an assignment row (`diagnostic_assignments`: `SetId`, `TargetSensorId`, `SensorId` unique, both cascading). Its sensor is named *Diagnostics: {set}*; deleting it directly is 409; removing the assignment removes the sensor and its events.
- **`SensorEntity` has no "managed" flag.** Ownership is the type, checked in `SensorService`:
  - `ValidateAsync` refuses DNS and SYSTEM as a requested type, and WINDOWS, LINUX and DIAGNOSTICS unless the sensor already has it;
  - `UpdateAsync` lets a DNS sensor change only *Enabled*, *Ignore*, *Importance* and *Notes*: the strictest precedent;
  - `DeleteAsync` refuses SYSTEM (`system_sensor`) and DIAGNOSTICS (`diagnostics_sensor`).
- **A host's deletion cascades** to its sensors, and from there to their side tables.
- **Hosts have a location** (`HostEntity.LocationId` → `locations`). Locations are a tree (`LocationEntity.ParentId`). A location's page has the tabs *overview, information, pages, tickets, history* (`/locations/:id/:tab`).
- **People are users:** a responsibility's responsible and deputy are `ResponsibleId` and `DeputyId` → `users`, SET NULL when the user is deleted, picked with `PersonPicker`.
- **Notifications need nothing new:** rules find events by the sensor, its host, its importance or *everything* (`NotificationScopeKind`: `Importance`, `Host`, `Everything`, plus single sensors), whatever the type. There is no scope by sensor type.
- **People's notifications** tell a person about their own things without a sensor or a rule (`PNMTD/Notifications/UserNotifications.cs`, docs/knowledge-base-extensions-plan.md §10):
  - the kinds are in code (`UserNotificationKinds`): a key, a label and help for *Account → Notifications*, the area and level the recipient must hold when it is sent, and whether e-mail is on by default. Today there are the knowledge base's two, `kb.liked` and `kb.used`;
  - a feature enqueues rows in its own transaction (`EnqueueAsync`), never to the actor;
  - rows are batched per person and key (`BatchMinutes`, 15), at most `MaxPerUserDay` (20) e-mails a day;
  - a renderer per kind (`IUserNotificationRenderer`) writes the e-mail when it is sent, from the subject as it is then, and returns null when nothing is left to say;
  - they go out through `NotificationService`, into the notification log, and need the user's e-mail address and `UserNotifications__Enabled` (on by default).
- **Features a deployment turns on** exist, and each hides in its own way:
  - `Cookie__Enabled` and `Ai__Enabled` are off by default; `Connectors__Enabled=false` makes `/connector` answer a plain 404;
  - `GET /system/info` tells the web client whether Redmine and Zabbix are configured (`SystemInfo.Redmine`, `SystemInfo.Zabbix`), and the menu leaves out an entry whose `NavItem.feature` isn't (`visibleNavigation`);
  - an MCP tool has `ToolSpec.Available`: Redmine's and Zabbix's tools are listed only when configured (`IntegrationReadTools`);
  - *System health → Background tasks* shows a task that isn't configured as *off* (`SystemService.Tasks`, its `Configured`);
  - the router's guard (`router/guard.ts`, `decide`) checks rights only, not features. Unknown paths go to `not-found`.
- **Start-up work after the migrations** runs in `DatabaseMigrationService.StartAsync`, registered before every other hosted service, whatever `BackgroundTasks:Enabled` says.
- **A test class can start a host with settings of its own:** `new TestApp(settings: …)`.
- **The files folder** (`PNMTD/Files/`):
  - `FileStore.StoreAsync` streams an upload to `.tmp/`, refuses blocked extensions (`exe msi dll scr com bat cmd ps1 vbs js jar hta lnk`, every dot part), cuts off at the kind's limit (25 MB for an attachment), hashes it, deduplicates by SHA-256 and moves it to `yyyy/MM/{id}-{slug}`;
  - `files` says what a file is; `file_uses` says who uses it, with one owner column each (`PageId`, `LocationId`, `ResponsibilityId`, `BoardId`) and `CK_file_uses_one_owner`;
  - the daily cleanup marks a file unused for 24 hours as deleted, and removes it 7 days later;
  - the *File storage* system sensor (428) checks the folder daily and its hashes weekly;
  - `./backup.sh` writes the folder to `files.tar`.
- **The generic download checks no owner:** `GET /api/v1/files/{id}/{name}` serves any file to anyone with *Knowledge base*, *Locations* or *Responsibilities: View* who knows its id. **Board images are the exception:** `GET /api/v1/boards/{id}/images/{fileId}` serves only a file the board uses (`BoardService.ImageAsync`).
- **Secrets at rest:** there is no shared protector. Each feature makes its own Data Protection protector with a versioned purpose (`PNMTD.Connectors.Key.v1`, …). A connector caller's key is stored as `KeyProtected` (`[AuditSecret]`) with `KeyHint` (`[AuditPlain]`), shown by `GET …/key`, which audits `ConnectorCallerKeyViewed` (warn), and `Unprotect` returning null gives 409 `key_lost`.
- **The audit guard** (`AuditModelTests`) requires `[AuditSecret]`, `[AuditPlain]` or `[AuditIgnore]` on every property of an `[Audited]` entity whose name matches `token|secret|password|key|hash|protected|private|credential`.
- **The credentials scanner** (`SecretScanner`) warns with 422 `looks_like_credential` and *Save anyway* on pages, location fields, responsibility descriptions and map notes. A line like `key: ABCDE-…` triggers it.
- **The generic history** (`GET /api/v1/audit-log/subjects/{id}`, `AuditHistory.vue`) needs only *Monitoring: View*, for any id.
- **Tabbed pages** use `TabNav` with the tab in the route, guarded by a regex (`/admin/responsibilities/:tab(list|links|overview|redmine)?`).
- **Sorting in the browser, kept in the URL,** is `views/admin/responsibilities/listSort.ts` (`parseSort`, `formatSort`, `sortItems`), wired into a `lazy removable-sort` DataTable.
- **The only CSV export** is the audit log's (`AuditLogController`, formula-safe `Csv()`).
- **Money exists only as AI costs in USD** (`numeric(12,6)`, `usd()`). No business object has a price or a currency.
- **There is no chart library.** Charts are hand-made SVG: `SampleChart.vue` (advanced sensors), `MonthColumns.vue` and `WeekdayColumns.vue` (the availability plan's *Status*), the uplink's view.
- **Days are `DateOnly`,** serialised as `yyyy-MM-dd`, shown with `formatDay` and picked with PrimeVue's `DatePicker` as in `FillAgainDialog.vue`.
- **The Windows agent reports activation:** `ActivationInfo.PartialKey` is the installed product key's last five characters, with the channel (Retail, OEM, Volume:MAK, Volume:GVLK) (`PNMTD.Lib/Agent/AgentSections.cs`).
- **The menu** (`layouts/navigation.ts`) has the groups *Overview, Monitoring, Alerts, Mail, Knowledge, Integrations, Devices, Admin*, which `navigation.test.ts` asserts, with unique icons. *Knowledge* holds only *Knowledge base*.

### 1.2 Free numbers

- **Sensor type:** the types go up to `ADVANCED = 130`. **`LICENSE = 140`**, for all three types of item.
- **Status codes:** 245–249 and 445–449 are free, a pair of half-decades as 285/485 are for certificates. **245, 246, 445–449** (§4).
- **Key-value key:** the keys go up to `LAST_USER_NOTIFICATION_RUN = 350`. **`LAST_LICENSE_EVALUATION = 360`.**
- **Rights area:** `Area.Licenses`, appended (stored by name in `group_rights`).
- **Audit:** `AuditCategory.Licenses`, the targets `License`, `LicenseTemplate` and `LicenseCategory`.
- **People's notification kinds:** `license.warning`, `license.review` and `license.unowned`, strings in code; no migration (§5.5).
- **The problem code** `licenses_disabled`, for every endpoint while the feature is off (§13.2).

## 2. Requirements

From the first request:

- **R1.** Licence management.
- **R2.** A licence can be assigned to a host.
- **R3.** A licence can expire.
- **R4.** A licence assigned to a host appears there like a sensor.
- **R5.** The sensor goes bad when the licence expires.
- **R6.** The licence also goes bad when it is about to reach its end of life.
- **R7.** There are licence templates.
- **R8.** A licence holds a licence key, a product key, a manufacturer, a name, a vendor, a price and a product name.
- **R9.** The licences and their templates are managed on a page of the menu.
- **R10.** The page has tabs, as other pages do.
- **R11.** One tab is an overview of the licences: a sortable and searchable table.
- **R12.** One tab manages the templates (product name, vendor, manufacturer).
- **R13.** One licence can be assigned to several hosts.
- **R14.** A licence has a note.
- **R15.** The price is recurring or a one-time purchase.
- **R16.** A recurring licence is configured: when it expires, how often it must be renewed, and how many days before it warns; and when it was last reviewed.
- **R17.** More that makes sense (R23 onwards).
- **R18.** Documents can be attached (txt, PDF, images, md, key, lic, …), stored as the knowledge base stores its files.

From the second request:

- **R19.** A licence can also be a service.
- **R20.** An item doesn't have to be assigned to a host.
- **R21.** All IT costs and services can be tracked.
- **R22.** A cost and licence overview.

From the third request:

- **R45.** The whole feature can be turned off in the environment, and is then neither usable nor visible.
- **R46.** It is off by default.
- **R47.** An item that isn't on a host needs no sensor.

Added by this plan:

- **R23.** An item has a state of its own, the worst of its findings, shown wherever it is named.
- **R24.** The end of life is the product's: set once on the template, followed by its items.
- **R25.** Seats: what an item covers, what its hosts use, and over-assignment as a finding.
- **R26.** Renewing is an action, and every renewal is kept.
- **R27.** Automatic renewals move the expiry by themselves; a notice period says by when to cancel.
- **R28.** Reviews have a rhythm, and the due ones are listed.
- **R29.** An item can be cancelled and retired, not only deleted.
- **R30.** Keys are secrets: encrypted at rest, shown on request, audited, never to MCP or Cookie.
- **R31.** Documents are served only to people who may read the item's documents, never through the generic file URL.
- **R32.** The coming months' dates and costs are on one tab.
- **R33.** The items are found by the global search, and every list can be exported.
- **R34.** A host's items show on its page.
- **R35.** A rights area of its own.
- **R36.** Hardware is a third type, so that devices' purchases, leases and warranties are IT costs too.
- **R37.** An item on no host can still notify, without a sensor.
- **R38.** The billing period is apart from the term; a price can be per seat; a price can be variable.
- **R39.** What was spent is known without typing in every invoice.
- **R40.** The coming months' costs are forecast.
- **R41.** The costs add up by category, type, provider, manufacturer, cost centre, location, host and owner.
- **R42.** A service's support details are where its outage is handled.
- **R43.** The overview points at money and risk worth a look.
- **R44.** Currencies are never mixed in one total or one chart.
- **R48.** Turning the feature off loses nothing; turned on again, it is as it was.
- **R49.** An item's owner hears about it without a sensor or a rule, and chooses whether to.

## 3. The data model

### 3.1 `license_categories` (`LicenseCategoryEntity`, `[Audited]`)

- `Id`.
- **`Name`** (1–60, unique, case ignored).
- `Description` (≤ 200).
- `SortOrder` (int): the order of the lists and of the overview's legend.
- **`DefaultFor`** (string?, `License` | `Service` | `Hardware`, unique where set): the category a new item of that type starts in without a template.
- `CreatedAt`, `UpdatedAt`.

**The migration adds eleven,** in this order:

1. *Software* (the default for licences);
2. *Cloud & SaaS*;
3. *Hosting & infrastructure*;
4. *Connectivity* (internet, WAN, VPN);
5. *Telephony & mobile*;
6. *Security*;
7. *Support & maintenance*;
8. *Hardware* (the default for hardware);
9. *Domains & certificates*;
10. *Consulting & training*;
11. *Other* (the default for services).

**Every item and template has one category** (`CategoryId`, required, RESTRICT). Categories are changed on the *Templates* tab (*Manage*, §8.6). Deleting one in use asks which category its items and templates move to (`DELETE …?moveTo=`), in one transaction. The last category can't be deleted.

**A list, not a word** as the manufacturer is (decision 8): the costs add up by category, so a typo would split a total, and the overview needs a short, ordered set.

### 3.2 `license_templates` (`LicenseTemplateEntity`, `[Audited]`)

- `Id`.
- **`Name`** (1–200, unique, case ignored): how the template is listed, e.g. *Windows Server 2016 Standard (16 cores)*, *Business Internet 1 Gbit/s*.
- **`Type`** and **`CategoryId`** (§3.1).
- **The product or service:**
  - `Manufacturer` (≤ 100; required for licences and hardware);
  - `ProductName` (1–200), required;
  - `Edition` (≤ 100): the edition, version or model, e.g. *2016 Standard*, *Business Premium*, *v12*, *PowerEdge R650*;
  - `Vendor` (≤ 100): the vendor or provider;
  - `ProductUrl` (≤ 500, `http(s)` only): the manufacturer's page, for its lifecycle dates.
- **The defaults a new item starts with** (each null is "no default"):
  - `Kind` (§3.3);
  - `Seats`, `SeatUnit`, `SeatsFromHosts`;
  - `PriceAmount`, `Currency`, `PriceBasis`, `Billing`, `BillingMonths`, `TermMonths`;
  - `Renewal`, `WarnDays`, `NoticeDays`, `WarnBeforeRenewal`;
  - `ReviewMonths`;
  - `SupportContact`, `SupportUrl`, `Sla`.
- **The end of life** (followed by the items, §3.3):
  - `EndOfLifeOn` (`DateOnly?`);
  - `EndOfLifeNote` (≤ 200): what it is, e.g. *end of extended support*.
- `Note` (≤ 2,000, plain text).
- `CreatedAt`, `CreatedBy`, `UpdatedAt`.

Manufacturers, vendors and cost centres are **not entities of their own:** they are text, with suggestions from every value in use, renamed or merged together (§8.6). A list of each would be three more pages for what is one word.

### 3.3 `licenses` (`LicenseEntity`, `[Audited]`)

**Identity:**

- `Id`, and **`Number`** (int, unique, from the sequence `license_numbers`). It is shown with the type's prefix: `LIC-12`, `SVC-31`, `HW-40`. One sequence serves all three types, so a number is unique whatever its prefix, and changing the type changes the prefix, not the number. The search finds `12`, `LIC-12` and `SVC-12` alike. Numbers are never reused.
- **`Type`:** `License` | `Service` | `Hardware`.
- **`Name`** (1–200, required): by default the template's product and edition (*Microsoft 365 Business Standard*). Names needn't be unique: ten keys of one product are ten licences of one name, told apart by number.
- `TemplateId` (FK → `license_templates`, SET NULL); **`CategoryId`** (FK → `license_categories`, required, RESTRICT).

**The product or service** (copied from the template, then the item's own):

- `Manufacturer` (≤ 100), `ProductName` (≤ 200), `Edition` (≤ 100; for hardware, the model), `Vendor` (≤ 100; labelled *Provider* for services).
- **`Kind`** (string; the form offers the type's kinds):

  | Kind | Types | What it is | The defaults it brings |
  |---|---|---|---|
  | `Perpetual` | licence | Bought once, used without end | One-time price, no expiry |
  | `Subscription` | licence, service | Paid per period, ends unless renewed | Recurring. A licence: billed yearly, a 12-month term, manual renewal. A service: billed monthly, a 1-month term, automatic renewal |
  | `Contract` | service | A fixed term with a notice period: an internet line, a phone contract, hosting | Billed monthly, a 12-month term, automatic renewal, 90 days' notice, a warning before the deadline |
  | `Support` | all | Maintenance or support: updates, an extended warranty, vendor support | Billed yearly, a 12-month term, manual renewal |
  | `Usage` | service | Paid by use: cloud resources, calls, traffic | A variable price, billed monthly, no term |
  | `OneOff` | service | A job done once: an installation, a project, a training | One-time price, no expiry |
  | `Purchase` | hardware | A device bought | One-time price. The expiry is the warranty's end; no renewal |
  | `Lease` | hardware | A device leased | Billed monthly. The expiry is the lease's end; no renewal |
  | `Trial` | licence, service | An evaluation | No price; an expiry is required |
  | `Free` | licence, service | Open source, freeware, a free tier | No price, no expiry |
  | `Other` | all | Anything else | None |

  **The kind words the labels and the messages:** the expiry is *Expires on*, *Ends on*, *Warranty until* or *Lease ends on*. A message says *Subscription ends on …*, *Contract renews on …*, *Warranty ended on …* or *Trial ends on …*.
- `LicensedTo` (≤ 200): the organisation or person it is registered to.
- `Reference` (≤ 200): the order or contract number, or a device's serial number.
- `CustomerNumber` (≤ 100): the number the vendor or provider asks for on the phone.
- `PortalUrl` (≤ 500, `http(s)` only): where it is managed (the vendor's portal, the admin centre).

**The support** (for services above all; shown for every type when filled in):

- `SupportContact` (≤ 500, plain text, several lines): phone, e-mail, hours. It is checked by `SecretScanner`, as the note is.
- `SupportUrl` (≤ 500, `http(s)` only).
- `Sla` (≤ 200): e.g. *24/7, 4 h response*.

These are shown on linked sensors' pages to anyone who sees the sensor (§5.4, decision 9).

**Where it belongs and who looks after it:**

- `CostCenter` (≤ 100): with suggestions, and renamed together with the manufacturers (§8.6).
- `LocationId` (FK → `locations`, SET NULL): where it is used, e.g. the site of an internet line. It feeds the costs by location (§6.5).
- `OwnerId` (FK → `users`, SET NULL), as a responsibility's responsible.

**The keys** (§3.4):

- `LicenseKeyProtected` (`[AuditSecret]`) and `LicenseKeyHint` (≤ 5, `[AuditPlain("the last five characters, to find a key")]`).
- `ProductKeyProtected` (`[AuditSecret]`) and `ProductKeyHint` (≤ 5, `[AuditPlain(…)]`).
- The *Keys* panel is shown for licences, and for services and hardware only when a key is set.

**Seats:**

- `Seats` (int?, 1–1,000,000): null is *not counted* (a site licence, an unlimited one, a single line).
- `SeatUnit` (≤ 30, default *devices*). It is suggested from *devices, users, cores, instances, sites, lines, mailboxes* and every unit in use.
- **`SeatsFromHosts`** (bool): the seats are counted on the hosts, by each assignment's seats. It defaults to true for licences counted in devices, cores or instances, and false otherwise. Only then are used seats counted, over-assignment a finding (449), and unused seats *worth a look* (§6.7). A Microsoft 365 subscription for 25 users isn't assigned to 25 hosts.

**The price** (§6):

- `PriceAmount` (`numeric(12,2)`, ≥ 0; null is *unknown* or *free*).
- `Currency` (ISO 4217, three upper-case letters, default `Licenses__Currency`). PNMT doesn't convert; totals are per currency (decision 16).
- **`PriceBasis`:** `Total` | `PerSeat` (the amount is per seat; needs `Seats`).
- **`Billing`:** `OneTime` | `Recurring` | `Variable` (the amount is an estimate per billing period; the invoices tell the rest).
- **`BillingMonths`** (int?, 1–120): the billing period, how often it is paid for; the amount is per billing period. Required for `Recurring` and `Variable`; it defaults to the term.
- **`RecordCharges`** (bool, default `Licenses__RecordCharges`, true): PNMT records each billing period's charge (§6.2).
- `ChargesFrom` (`DateOnly?`): the first billing day PNMT records, set when `RecordCharges` is turned on (§6.2).

**The term:**

- **`StartsOn`** (`DateOnly?`, default the day the item is created): when it was bought or the contract began. It replaces revision 1's `PurchasedOn`.
- **`ExpiresOn`** (`DateOnly?`): null is *doesn't expire*. Required for manual and automatic renewals and for trials. **An item is valid through the day it expires,** and expired from the next day, 00:00 in PNMT's time zone (`TZ`).
- **`TermMonths`** (int?, 1–120): how often it is renewed. Required for automatic renewal; with a manual one it presets *Renew…*.
- **`Renewal`:** `None` (it ends, unless someone buys anew), `Manual` (a person renews it, *Renew…*), `Automatic` (the vendor renews it unless cancelled, §5.6).
- **`WarnDays`** (int, 0–730, default `Licenses__WarnDays`, 30): how many days before the expiry the item goes bad. 0 is *only when it has expired*.
- `NoticeDays` (int?, 0–730): with an automatic renewal, how many days before the expiry it must be cancelled.
- `WarnBeforeRenewal` (bool): with an automatic renewal, warn `WarnDays` before the cancellation deadline (§4). It defaults to true for contracts and false otherwise (decision 11).

**The end of life:**

- `EndOfLifeFromTemplate` (bool, default true when there is a template).
- `EndOfLifeOn` (`DateOnly?`) and `EndOfLifeNote` (≤ 200): the item's own, used when it doesn't follow the template, or has none.
- `EolWarnDays` (int, 0–1,095, default `Licenses__EolWarnDays`, 90).

**The review:**

- `ReviewedOn` (`DateOnly?`), `ReviewedBy` (≤ 200), `ReviewNote` (≤ 500).
- `ReviewMonths` (int?, 1–60, default `Licenses__ReviewMonths`, 12): null is *no rhythm*.

**Its life:**

- **`Status`:** `Active` | `Cancelled` (it won't be renewed and runs to its expiry) | `Retired` (no longer in use, kept for the record).
- `StatusChangedAt`, `StatusChangedBy`.

**The note:** `Note` (≤ 10,000, Markdown without images, `[AuditText]`), shown with `MarkdownView`. It is checked by `SecretScanner` on save. A finding answers 422 `looks_like_credential`, and the dialog says *This looks like a key. Keys belong in the licence key and product key fields, which are encrypted and shown only on request.* with *Save anyway*.

**The record:** `CreatedAt`, `CreatedBy`, `UpdatedAt`.

**Its state, which the evaluation keeps** (`[AuditIgnore]`, §5.5). **The item holds its own state,** so nothing about it needs a sensor: the lists, the overview, the item's page and the owner's notices read these columns, and the host sensors show the same.

- `VerdictCode` (int?, null until first evaluated and when retired), `VerdictKey` (≤ 200), `VerdictAt`: the verdict of §4, and since when.
- `AnnouncedExpiresOn` (`DateOnly?`): the expiry the last OK verdict announced, so a renewal shows 246 once (§4).
- `NoticeKey` (≤ 200) and `ReviewNoticeOn` (`DateOnly?`): what the owner was last told, so each warning, reminder and due review is told once (§5.5).
- `AutoRenewedThrough` (`DateOnly?`): the last expiry PNMT moved by itself, so a restart doesn't renew twice.

**Validation** (400 `invalid` with the field):

- `Recurring` and `Variable` need `BillingMonths`; `OneTime` ignores it.
- `Automatic` needs `TermMonths` and `ExpiresOn`, and `NoticeDays < TermMonths × 28`. `Manual` needs `ExpiresOn`.
- A `Trial` needs `ExpiresOn`.
- `PerSeat` needs `Seats`. `SeatsFromHosts` needs `Seats`.
- A currency without an amount is kept (the default for the next price); an amount needs a currency.
- `EndOfLifeFromTemplate` needs a template.
- The kind must be one of the type's (the table above).

### 3.4 The keys

- **Encrypted with Data Protection,** purpose `PNMTD.Licenses.Key.v1`, by `LicenseKeyProtector` (as `ConnectorKeyProtector`). The column holds the protected text, never the key.
- **The hint is the last five letters and digits,** upper case, separators ignored: `NJ4MX-VQQ7Q-FKK44-W8FV9-CC9DR` → `CC9DR`. That is what Windows reports as `PartialKey`, so a host can be matched (§8.11).
- **Up to 4,000 characters,** several lines allowed: some licence keys are blocks of text. A licence file belongs in the documents (§7).
- **Shown on request:** `GET /api/v1/licenses/{id}/keys` (*Details*) returns both, audited `LicenseKeysShown` (warn). The page shows `•••••-CC9DR` with ***Show*** and then a `CopyField`, as a connector caller's key.
- **When the key ring is lost** (`Unprotect` returns null): 409 `key_lost`, and the page says *This key can't be read: PNMT's key ring was replaced. Enter the key again.* The backups hold the key ring (`keys.tar.gz`); deploy/README.md says the keys need it.
- **Never in:** the audit's values, the history, exports, the search, MCP, Cookie, sensor messages, the logs.
- **Passwords and API keys of a service** are not keys in this sense: they belong in a password manager, and the scanner warns about them in the note and the support contact.

### 3.5 `license_assignments` (`LicenseAssignmentEntity`, `[Audited]`)

- `Id`, `LicenseId` (FK, cascade), `HostId` (FK → `hosts`, cascade).
- **`SensorId`** (FK → `sensors`, SET NULL, unique where set): the host sensor. It is null only while the feature is off, when the item sensors are removed and the assignments stay (§13.3).
- Unique (`LicenseId`, `HostId`): an item is on a host once.
- `Seats` (int, 1–1,000,000, default 1): what this host uses, e.g. 16 cores.
- `Note` (≤ 500).
- `AssignedAt`, `AssignedBy`.
- **State** (`[AuditIgnore]`): `SensorKey`, the verdict key its sensor last showed. An event is written when it differs from the item's `VerdictKey` (§4), so a sensor made again later starts with the item's state.

**An assignment is a host, never anything else:** there is no sensor without a host (§5.2).

### 3.6 `license_sensor_links` (`LicenseSensorLinkEntity`, `[Audited]`)

- `Id`, `LicenseId` (FK, cascade), `SensorId` (FK → `sensors`, cascade); unique (`LicenseId`, `SensorId`).
- `Note` (≤ 200), e.g. *the primary line*.
- `CreatedAt`, `CreatedBy`.
- An item sensor can't be linked (400 `invalid`): it is the item already.

### 3.7 `license_renewals` (`LicenseRenewalEntity`, `[Audited]`)

One row per renewal: the term's history. The money is in the charges (§3.8).

- `Id`, `LicenseId` (FK, cascade).
- **`Kind`:** `Renewal` (a person, *Renew…*) | `AutomaticRenewal` (PNMT, §5.6).
- `On` (`DateOnly`): when it was renewed.
- `PreviousExpiresOn`, `NewExpiresOn`.
- `PriceAmount`, `Currency` (null when the price stayed the same): the price from this renewal on.
- `Reference` (≤ 200), `Note` (≤ 500).
- `CreatedAt`, `CreatedBy` (*automatically* for PNMT's own).

### 3.8 `license_charges` (`LicenseChargeEntity`, `[Audited]`)

One row per recorded cost: what *spent* adds up.

- `Id`, `LicenseId` (FK, cascade).
- **`Kind`:** `Purchase` (a one-time price) | `Period` (a billing period) | `Extra` (anything else: a setup fee, an overage, an invoice of its own) | `Credit` (money back).
- **`Source`:** `Automatic` (PNMT, §6.2) | `Person` (§6.3).
- **`On`** (`DateOnly`): the billing or invoice day. The reports use it.
- `PeriodFrom`, `PeriodTo` (`DateOnly?`): what it covers.
- **`Amount`** (`numeric(12,2)`, signed: a credit is negative; its dialog takes a positive amount), **`Currency`**: the currency it was recorded in, kept when the item's currency changes.
- **`Estimated`** (bool): an automatic charge of a variable price, until a person enters the invoice's amount.
- `Reference` (≤ 200): the invoice number. `Note` (≤ 500).
- `CreatedAt`, `CreatedBy` (null for PNMT's own), `UpdatedAt`, `UpdatedBy`.
- **Unique (`LicenseId`, `PeriodFrom`) where `Source = Automatic` and `Kind = Period`:** a billing period can't be recorded twice by PNMT, whatever restarts.
- Index on (`On`), for the costs of a period.

### 3.9 `license_documents` (`LicenseDocumentEntity`, `[Audited]`)

- `Id`, `LicenseId` (FK, cascade), `FileId` (FK → `files`, cascade); unique (`LicenseId`, `FileId`).
- **`Kind`:** `Invoice`, `Contract`, `Quote`, `LicenseFile` (a `.lic` and the like), `KeyFile`, `Warranty`, `Other`. It is suggested from the name (`.lic` → licence file, `.key` → key file, *invoice*/*rechnung* → invoice, *warranty*/*garantie* → warranty) and can be changed.
- `Description` (≤ 200).
- `ChargeId` (FK → `license_charges`, SET NULL): the invoice of a charge. `RenewalId` (FK → `license_renewals`, SET NULL): the paper of a renewal.
- `AddedAt`, `AddedBy`.

**And `file_uses` gets `LicenseId`** (FK → `licenses`, cascade; unique (`LicenseId`, `FileId`)), with `CK_file_uses_one_owner` counting it. That is what keeps the cleanup, *File storage* and *Admin → Knowledge base → Files* right (§7.3).

### 3.10 The migration `Licenses`

- The eight tables (categories, templates, items, assignments, sensor links, renewals, charges, documents), the sequence, `file_uses.LicenseId` and the constraint.
- The eleven categories (§3.1), into their new, empty table.
- No other data moves; no group is granted the area (decision 20).
- **It runs whatever the switch says:** every installation has the tables, empty, and the categories, so turning the feature on later needs no migration (§13.2).
- `dotnet ef migrations has-pending-model-changes` clean.

## 4. The verdict, the codes and the messages

`LicenseVerdict.Evaluate(facts, today)` is pure. The facts are the item, its template's end of life, the sum of its used seats, and its newest renewal. It returns the code, the message and the key. Nothing else decides an item's state; §14 tests it without a database.

**The verdict is the item's,** stored on it (§3.3), with or without hosts. The codes below are status codes so that the host sensors can show them; an item on no host has the same codes, on its page, in the lists and in its owner's notices.

| Code | Name | Success | When |
|---|---|---|---|
| 245 | `LICENSE_OK` | yes | Valid, no finding |
| 246 | `LICENSE_RENEWED` | yes | The expiry moved forward since the last announced one. Written once; the state stays 246 until the verdict changes |
| 445 | `LICENSE_ABOUT_TO_EXPIRE` | no | `ExpiresOn − today ≤ WarnDays`, with `Renewal` *None* or *Manual*, or a cancelled item. With *Automatic* and `WarnBeforeRenewal`: the cancellation deadline is within `WarnDays` |
| 446 | `LICENSE_EXPIRED` | no | `today > ExpiresOn`, and it wasn't renewed automatically |
| 447 | `LICENSE_EOL_SOON` | no | The end of life is within `EolWarnDays` |
| 448 | `LICENSE_EOL` | no | The end of life has passed |
| 449 | `LICENSE_OVER_ASSIGNED` | no | `SeatsFromHosts`, and the used seats are more than `Seats` |

**Precedence, worst first:** 446 → 448 → 449 → 445 → 447 → 246/245. The code is the worst finding; the message names the others too (*"; also …"*, as the certificates' do).

**Messages** use absolute days, never *in 12 days*: an event is written once and must not go stale. The UI counts the days live. **No message carries a price, a key or the note:** anyone with *Monitoring: View* reads sensor messages. The kind words them (§3.3):

- 245: `Valid until 31 Dec 2026 · Microsoft 365 Business Standard (LIC-12)`; `Doesn't expire · Veeam Backup & Replication (LIC-7)`; `Contract renews automatically on 1 Jul 2027 · Business Internet 1 Gbit/s (SVC-31)`; `Warranty until 30 Apr 2028 · Dell PowerEdge R650 (HW-40)`
- 246: `Renewed until 31 Dec 2027 (was 31 Dec 2026) · … (LIC-12)`; `Renewed automatically until 1 Jul 2028 · … (SVC-31)`
- 445: `Expires on 31 Dec 2026, within the 30 days' warning · … (LIC-12)`; `Trial ends on …`; cancelled: `Ends on 31 Dec 2026 (cancelled): remove it from this host · …`; automatic: `Renews automatically on 1 Jul 2027 unless cancelled by 2 Apr 2027 · … (SVC-31)`; `Warranty ends on 30 Apr 2028, within the 30 days' warning · … (HW-40)`
- 446: `Expired on 31 Dec 2026 · …`; `Support contract ended on …`; `Lease ended on …`
- 447: `Windows Server 2016 reaches its end of life (end of extended support) on 12 Jan 2027, within the 90 days' warning · LIC-4`; a service: `… reaches its end of service on …`
- 448: `Windows Server 2016 reached its end of life on 12 Jan 2027 · LIC-4`
- 449: `Over-assigned: 7 of 5 devices · … (LIC-9)`

**The key** is what the message is about, so an event is written when, and only when, that changes:

| Verdict | Key |
|---|---|
| 245, 246 | `ok\|{ExpiresOn}\|{EOL}` |
| 445, 446 | `{code}\|{ExpiresOn}\|{Status}` |
| 447, 448 | `{code}\|{EOL}` |
| 449 | `449\|{used}\|{Seats}` |

plus the secondary findings' codes, so a finding that joins the worst writes a new event too. A change of the item's settings clears its `VerdictKey`, so the next evaluation stores the state the new settings give and writes it to its sensors (as `CertificateSensorService.ApplyAsync` does).

**246 or 245:** OK with the item's `AnnouncedExpiresOn` set and different from `ExpiresOn` is 246; the evaluation then sets `AnnouncedExpiresOn`. The next OK evaluation with the same key changes nothing, so the state stays 246 until the verdict changes, as a certificate's 286 does.

**Without findings, an item that never expires** (`ExpiresOn` null, no end of life, seats not over) is 245 for ever.

**Reviews don't fail sensors** (decision 10): a review due is the item's business, not the host's. It shows on the list, on *Upcoming*, on the overview and on the item's page, and its owner is told (§5.7).

**Money makes no finding.** A verdict tells about dates and seats; what costs too much, or isn't recorded, is *Worth a look* (§6.7).

**A retired item has no verdict** (`VerdictCode` null), no sensors and no notices. Its page says *Retired on …*.

## 5. Hosts, sensors, notices, renewals and reviews

### 5.1 Assigning to hosts

- **An item is assigned to many hosts at once** (`POST …/assignments` with `hostIds`, at most 500 per call), and a host is assigned items one by one from its page (§8.8).
- **At most `Licenses__MaxAssignments` (1,000) hosts** per item: each one is a sensor.
- **Hardware** is usually one machine: its dialog picks one host by default, and allows several (ten laptops on one invoice).
- **Seats,** with `SeatsFromHosts`: the dialog shows *3 of 5 devices used* and, when an assignment would go over, says so in warn colours: *This makes 6 of 5 devices: the licence will be over-assigned (449) on all its hosts.* It isn't refused: the licence may be about to be increased, and the finding says what is wrong.
- **A retired item can't be assigned** (409 `license_retired`).

### 5.2 Items on no host need no sensor

- **An item needn't be on a host:** a cloud subscription, a domain, a phone contract, consulting. **It then has no sensor, and needs none.** Everything works without one:
  - its state, which the item keeps itself (§3.3), evaluated as every item's (§5.5);
  - its owner's notices by e-mail (§5.5);
  - the costs, the dates, the documents, *Upcoming*, the overview and *Worth a look*.
- **A sensor stands for an item on a machine:** it is on the host's page, among the host's problems, and the host's rules tell the host's people. A contract that belongs to no machine has no host whose page it should be on, so it gets no sensor.
- **No stand-in host** (decision 6). Revision 2 gave such items a sensor of their own, on a host PNMT made for them, *Licences & services*. That host was no machine: it was in every host list, map and count, kept alive by a 409, for sensors whose only purpose was to reach the notification rules. The owner's notices reach the person directly, and need neither the host nor the sensors.
- **Who hears about it:** its owner. Without an owner, whoever with *Edit* chose *Licences and services nobody owns* on *Account → Notifications* (§5.5).
- **Nobody notified:** an active item with an expiry, an end of life or a review rhythm, on no host, without an owner, while nobody has chosen *Licences and services nobody owns*. Its page says *On no host and without an owner, so nobody is told when it expires.* with ***Set an owner***, and it counts in *Worth a look* (§6.7). An owner without an e-mail address counts as none, and the page says so: *Its owner has no e-mail address in PNMT.* An owner who turned these e-mails off still counts as told: that choice is theirs, and isn't shown to others.
- **Assigning it to a host later** makes the host sensor (§5.3); unassigning the last host leaves the item without a sensor, as it began.

### 5.3 The item's sensors

- **One sensor per assignment,** of type `LICENSE`, created with the assignment and deleted with it, events included, as a diagnostics assignment's sensor. **Only while the feature is on:** off, the item sensors are removed and made again when it is turned on (§13.3).
  - **Name:** *{Type}: {name} ({number})*: *Licence: Microsoft 365 Business Standard (LIC-12)*, *Service: Business Internet 1 Gbit/s (SVC-31)*, *Hardware: Dell PowerEdge R650 (HW-40)*. It is kept in step when the item is renamed or its type changes.
  - `Parameters`: null. `Interval` and `GracePeriod`: 0. `Importance`: null (the host's).
  - **The first event is written at once:** the assignment evaluates its item before it answers.
- **What a person may change on it** (`SensorService.UpdateAsync`, the DNS branch): *Enabled*, *Ignore*, *Importance*, *Notes*. Its name and host follow the item. The type can't change, to or from `LICENSE` (400 `invalid`, *"These sensors are made by assigning a licence or service"*).
- **Deleting it through `/sensors`** is 409 `license_sensor`: *Unassign it on the licence instead.* The sensor's page offers *Unassign…* (*Edit*) in its place.
- **The sensor API refuses events for it** (`SensorApiController`, 409 `license_sensor`): an event posted with its write token would stand for weeks, since the verdict writes only when it changes. The *Integration* panel is hidden, as for system sensors.
- **A disabled or ignored item sensor** is evaluated like any other and shown as disabled or ignored; that is how a person silences one host.
- **A host's deletion** removes its assignments and their sensors (cascade). An item's deletion removes its sensors first, then itself.
- **`SensorDetail.License`** (`LicenseSensorSummary`, for *Monitoring: View*) has:
  - the item's id, number, type, name, kind, status, manufacturer, product, `ExpiresOn` and the end of life;
  - the seats used and covered, and the assignment's seats.

  **No price, no keys, no note, no documents:** the sensor page works for *Monitoring: View* without *Licences & services*.
- **The catalogs:** `SystemService.SensorTypes()` gets `LICENSE`, while the feature is on (§13.2):
  - description: *"A licence, service or piece of hardware (Licences & services), on a host it is assigned to. PNMTD evaluates it every minute: 245 while valid, 246 once after a renewal, 445 within the item's warning days before it expires or before its cancellation deadline, 446 once expired, 447 within the warning days before its end of life, 448 after it, 449 when more seats are used than bought."*;
  - `CanCreate` false, `UsesInterval` false.

  `StatusCodes()` gets the seven codes, while the feature is on.

### 5.4 Linked sensors

- **On the item's *Hosts & sensors* tab, *Link sensors…*** (*Edit*) picks existing sensors (`GET …/targets?kind=sensor&q=`), several at once: the internet line's ping, the SaaS's HTTP check, the backup job's sensor. Item sensors are refused (§3.6).
- **The tab lists them** with their host, state and last message. **Their states don't change the item's verdict** (decision 9): monitoring stays monitoring, and the item's state is about dates and seats.
- **On the linked sensor's page,** for everyone who sees it, a strip (`LinkedItemsStrip.vue`): *Service SVC-31 · Business Internet 1 Gbit/s · Swisscom · support 0800 800 800 (24/7) · customer no. 1234567 · SLA 4 h*, with *Open* for *Licences & services: View*. When the line is down at 3 a.m., the number to call and the customer number are where the alarm is.
- **`SensorDetail.LinkedItems`** (`LinkedItemSummary`): id, number, type, name, provider, support contact, support URL, customer number, SLA, verdict. No price, keys, note, reference or documents.
- **A link goes with either side.** Linking and unlinking are audited (`LicenseSensorLinked`, `LicenseSensorUnlinked`).

### 5.5 The evaluation, the sensors and the owner's notices

- **`LicenseEvaluator`,** a hosted service registered with the other tasks, runs **every minute** behind a `RunGuard`, **only while the feature is on** (§13.2), and not with `BackgroundTasks:Enabled=false`:
  1. it moves the automatic renewals that are due (§5.6);
  2. it records the charges that are due (§6.2), after the renewals, so a contract renewed today is charged today;
  3. **it evaluates every item that is not retired, with hosts or without,** in one transaction per item that changed:
     - it stores the verdict on the item (§3.3);
     - it writes an event to each host sensor whose `SensorKey` differs from the item's key (§4), through `SensorEventWriter`, source `Licence evaluation`;
     - it enqueues the owner's notices that are due (below);
  4. it writes `LAST_LICENSE_EVALUATION`. *System health → Background tasks* shows *Licence checks*, every 60 seconds; its `Configured` is the switch, so it shows *off* while the feature is.
- **It needs no network,** so the uplink doesn't stop it. Items are hundreds: evaluating all of them each minute is a query and a loop.
- **`LicenseService.EvaluateAsync(licenseId)`** runs steps 1–3 for one item after every change (save, assign, renew, review, retire, a template's end of life), under the same lock, so a page shows the new state at once. The person who made the change is the notices' actor, so an owner isn't told what they just did themselves.
- **"Today" is PNMT's,** in `TZ` (`Clock.LocalNow`). An item expiring on 31 Dec turns 446 at 00:00 on 1 Jan in that zone. A shared helper `Clock.FormatDay(DateOnly)` writes the days, `d MMM yyyy`, invariant, which `CertificateVerdict.Format` and `AdvancedValues` can use too.
- **The host sensors notify through the rules,** with nothing new: rules by host, by importance or for everything pick up their events. **The release notes say so:** once the feature is turned on, a rule for *everything* tells about licences on hosts too.
- **Cookie's case book** takes a failing item sensor into its host's case, as any failing sensor. Nothing to code; §16 checks that it reads sensibly. An item on no host isn't in a case: it has no sensor.

**The owner's notices** are people's notifications (§1.1), three kinds in `UserNotificationKinds`:

| Kind | On *Account → Notifications* | To whom | E-mail by default | The recipient needs |
|---|---|---|---|---|
| `license.warning` | *Licences and services I own: warnings* | the owner | on | *Licences & services: View* |
| `license.review` | *Licences and services I own: reviews due* | the owner | on | *Licences & services: View* |
| `license.unowned` | *Licences and services nobody owns: warnings* | everyone who chose it | off | *Licences & services: Edit* |

- **When an owner is told** (`license.warning`), once each, kept in the item's `NoticeKey`:
  - **the item goes bad,** or another finding becomes the worst (445 → 446, a finding joins): the key of §4 changed, and the verdict isn't OK;
  - **a reminder** `Licenses__RemindDays` (7) before the date of a 445 or 447: the expiry, the cancellation deadline, the end of life. The notice key gains *remind*, so it goes once;
  - **an automatic renewal** (246 *Renewed automatically …*), with the new term's price: a contract that renewed itself is news its owner wants. A renewal a person made isn't told to them, since they are its actor.

  Going back to OK sends nothing otherwise.
- **When a review falls due** (`license.review`): once per due day (`ReviewNoticeOn`), for an item with an owner. It changes no verdict and fails no sensor (decision 10).
- **Items nobody owns** (`license.unowned`): the same warnings and reminders, for active items without an owner, to everyone with *Edit* who chose the kind. Off by default, so nobody gets e-mails they didn't ask for. It is how whoever looks after contracts gets all the unowned ones in one place.
- **On hosts or not:** the owner is told about every item they own. The hosts' rules tell the hosts' people about the sensors; the notice tells the owner about the item. Whoever gets both can turn either off.
- **What an e-mail says:** one e-mail per person and kind, batched as the knowledge base's (`BatchKey` = the kind), with every item in it, worst first:
  - *LIC-12 Microsoft 365 Business Standard: expires on 31 Dec 2026, renewed by hand · EUR 3,960.00 a year · Renew: {link}*;
  - *SVC-31 Business Internet 1 Gbit/s: renews automatically on 1 Jul 2027 unless cancelled by 2 Apr 2027 · CHF 11,880.00 for the next 12 months · {link}*;
  - *Because:* *you are the owner of LIC-12 and SVC-31*, or *you chose licences and services nobody owns*.

  It carries the price, since its recipient holds *View*, which sees prices (§9). **Never the keys, the note, the support contact or the documents.**
- **The renderer reads the items when it sends:** an item renewed, reviewed or retired in the meantime is left out, and an e-mail with nothing left isn't sent (the renderer returns null). The right is checked then too, as for every kind.
- **The limits are people's notifications':** an e-mail address on the user, `UserNotifications__Enabled`, `BatchMinutes` and `MaxPerUserDay`. Every e-mail is in the notification log.
- **While the feature is off,** the kinds aren't offered, nothing is enqueued, and a notice still pending isn't sent (§13.2).

### 5.6 Renewing

- ***Renew…*** (*Edit*), on the item's page and in the lists' row menus, opens a dialog:
  - **New expiry:** preset to the old expiry + `TermMonths`; to today + `TermMonths` when the item expired more than a term ago; editable.
  - **Price from now on:** preset to the current price; a changed amount becomes the item's price and is kept on the renewal.
  - **Reference** (the order or invoice number), **note**, and **the paper** (an invoice, a quote) as a document (§7), dropped or picked there.
  - **Record a charge for it,** offered only when `RecordCharges` is off (otherwise the billing days record it, §6.2): amount, day.
  - ***Reactivate it***, when it was cancelled.
- **Saving** writes a `Renewal` row, moves `ExpiresOn`, audits `LicenseRenewed`, and evaluates: the sensors show 246.
- **Automatic renewal** (`Renewal = Automatic`, `Status = Active`): when `today > ExpiresOn`, the evaluator moves `ExpiresOn` by `TermMonths` (several times if PNMT was off that long, up to today). It writes an `AutomaticRenewal` row per term, with `CreatedBy` *automatically*, and audits `LicenseRenewedAutomatically` (`AuditWho.System`). The sensors show 246 *Renewed automatically until …*. `AutoRenewedThrough` makes it happen once per expiry.
- **The notice period:** with `NoticeDays`, *the cancellation deadline* is `ExpiresOn − NoticeDays`. It is on *Upcoming*, the overview and the item's page. It makes the item 445 (and so its sensors and its owner's warning) only with `WarnBeforeRenewal` (decision 11).
- **Cancelling** (*Cancel…*, *Edit*, audited `LicenseCancelled`) sets `Status = Cancelled`. An item without an expiry is asked for its last day, which becomes `ExpiresOn`. No automatic renewal happens any more, no charge is recorded after the expiry, and the item warns and expires as `None` does, with *cancelled* in the messages. *Reactivate* takes it back (`LicenseReactivated`).
- **A renewal can be corrected** (reference, note; *Edit*) and deleted (*Manage*). Neither moves the expiry back: the expiry is the item's own field.

### 5.7 Reviewing

- ***Mark reviewed…*** (*Edit*), on the page and for many rows of a list at once: the day (today by default), an optional note. It sets `ReviewedOn`, `ReviewedBy` and `ReviewNote`, and audits `LicenseReviewed` with the note. The reviews' history is the audit's.
- `ReviewedOn` can also be set by hand, for a review made before PNMT knew the item.
- **A review is due** when `ReviewMonths` is set and `ReviewedOn + ReviewMonths ≤ today`, or the item was never reviewed and was created `ReviewMonths` ago. Due reviews have a chip in the lists (*review due*), a filter, their section on *Upcoming*, and a line in *Worth a look*.
- **The owner is told** when a review falls due, once per due day (`license.review`, §5.5). Marking it reviewed sets the next due day.

### 5.8 Retiring and deleting

- ***Retire…*** (*Edit*) is for an item no longer in use. The dialog lists the hosts it is on. Retiring removes its assignments (their sensors and events go), with a checkbox to confirm when there are any, and stops its charges and its notices. The item, its renewals, charges, documents and history stay. It leaves the default lists (the filter *Retired* shows it). Audited `LicenseRetired`.
- **A retired item** can be read, renewed and reactivated, but not assigned.
- ***Delete…*** (*Manage*) removes the item, its sensors, assignments, links, renewals, charges and document rows. Its files become unused and the cleanup removes them (§7.3). The dialog names the hosts, the charges' total and the documents, and asks to type the number (`LIC-12`). Audited `LicenseDeleted`. **Its charges leave the costs:** the dialog says so, and suggests *Retire* to keep them.

## 6. Costs

### 6.1 The price and the running cost

`CostMath` is pure, as the verdict is, and is the only place costs are computed (§14 tests it without a database).

- **The unit price** is `PriceAmount`, times `Seats` with `PriceBasis = PerSeat`.
- **Per month:** for `Recurring` and `Variable`, the unit price ÷ `BillingMonths`; for `OneTime`, nothing (it shows as *one-time*). **Per year** is per month × 12.
- **What counts:** an item that is not retired, and is not past its expiry unless it renews automatically. An expired licence that waits for a manual renewal counts nothing: it isn't being paid, and it is in *Needs attention*.
- **The billing days** are when a billing period starts. They step by `BillingMonths` from one anchor:
  - with `ExpiresOn` and `TermMonths`: the current term's first day, `ExpiresOn + 1 day − TermMonths`. A 36-month contract ending on 30 Jun 2029 and billed monthly is billed on the 1st;
  - otherwise `StartsOn`, else the day the item was created.

  **Steps are counted from the anchor, never from the last step,** so a period that starts on 31 Jan falls on 28 Feb and on 31 Mar, not on the 28th for ever after. An irregular renewal moves the anchor with the term, and the days follow it.
- **A billing day is in the term** when it is on or after `StartsOn`, on or before `ExpiresOn` (if any), and before the item was retired.

### 6.2 Charges PNMT records

- **Billing periods:** for an item with `RecordCharges` and a `Recurring` or `Variable` price, the evaluator records each billing day in the term that has come (≤ today):
  - from `ChargesFrom`, or after the newest automatic period charge;
  - as a charge of kind `Period`, source `Automatic`;
  - dated on the billing day, covering up to the day before the next one;
  - for the unit price at the time, in the item's currency;
  - `Estimated` when the price is `Variable`.
- **A one-time price** is recorded once, as `Purchase` on `StartsOn`, when the item is created with a price or first given one.
- **Never twice:** the unique index (§3.8) holds whatever restarts. At most 120 charges per item per run: a long stop catches up over a few minutes.
- **From when.** Creating an item sets `ChargesFrom` to the later of `StartsOn` and today, so a contract from 2023 entered today is charged from today on. The form offers ***Also record the charges since 1 Jul 2023 at this price (41 charges)***, off by default, since the price may have been different then (decision 13). Turning `RecordCharges` on later sets `ChargesFrom` to today.
- **Correcting:** an automatic charge's amount, currency, day, period, reference, note and invoice can be changed (*Edit*, audited `LicenseChargeChanged`). Entering the invoice's amount clears `Estimated`. It stays automatic. **It can't be deleted** (409 `automatic_charge`, *It would be recorded again: set it to 0 with a note instead.*).
- **Not audited one by one:** they would be most of the audit, and the row is the record. Corrections are audited.
- **Stopping:** no charge is recorded after the expiry of an item that doesn't renew, after a cancelled item's last day, or after retirement.

### 6.3 Charges by hand

- ***Record a cost…*** (*Edit*), on the item's *Costs* tab and on the *Costs* tab (which picks the item first):
  - **kind:** *Extra* (a setup fee, an overage, an invoice of its own), *Billing period* (for items without `RecordCharges`), *Credit* (money back), *Purchase*;
  - **day**, **period** (optional), **amount** and **currency** (the item's by default), **reference**, **note**;
  - **the invoice**, dropped or picked there, which becomes a document of kind *Invoice* linked to the charge.
- **Audited** `LicenseChargeRecorded`. Deleting a person's charge is *Manage* (`LicenseChargeDeleted`), as money records are.

### 6.4 The forecast

`CostMath.Forecast(items, from, to, today)` gives what today's prices will cost from tomorrow to the window's end:

- **every billing day** after today:
  - **within the term:** due;
  - **beyond the expiry,** for an active item that renews automatically: due, since the renewal is assumed;
  - **beyond the expiry,** for an active item renewed by hand: ***if renewed***, counted in and marked;
  - **beyond the expiry,** for an item that doesn't renew or is cancelled: nothing;
- **a one-time purchase** whose `StartsOn` is still to come, on that day;
- **nothing for retired items.**

**Rules of the forecast:**

- **The amount** is the unit price today; a variable price is its estimate.
- **The current month** is what was spent so far plus the forecast for its rest.
- **The *if renewed* part** is shown in tooltips and as a line under every total: *of it if renewed: EUR 8,900.00*.
- **No price rises, no inflation** (decision 14): the forecast is today's prices, and it says so.

### 6.5 Groups, and costs shared by hosts

- **By category, type, provider (the vendor), manufacturer, cost centre, owner or item:** each item is in one group; *none* collects the empty ones.
- **By location:**
  - **an item with a location** belongs to it;
  - **an item on hosts** is shared over its hosts' locations, by each host's seats (1 when not counted);
  - **anything else** is under *no location*.

  The table groups by the top-level location and opens down the tree; a location's total includes its children's.
- **By host:** an item is shared over its hosts by their seats; the rest is under *no host*.
- **Shares are rounded to cents by the largest remainder,** so the parts add up to the whole.
- **Past charges are shared by today's hosts:** PNMT doesn't keep where an item was in March. The table says *shared by the hosts it is on today*.

### 6.6 Currencies

- **Every price and every charge has its own currency;** nothing is converted (decision 16).
- **Totals are per currency.** The tiles show the main currency (`Licenses__Currency`) large, and the others in a line under it (*+ CHF 1,190.00*).
- **A chart shows one currency,** picked in the filter row above it, and a table shows one at a time with *Also in CHF: 14,280.00 [Show]*. Two currencies never share an axis or a sum.
- **The CSV** has a currency column and no totals across currencies.

### 6.7 *Worth a look*

Computed with the overview (§8.2), each a count with a link to the list it counts:

1. **Unused seats:** items with `SeatsFromHosts` using fewer than their seats, with what the unused part costs a year. That is `(Seats − used) ÷ Seats` of the item's yearly cost: *11 unused seats · EUR 1,320.00 a year*.
2. **Paid past their end of life:** active items with a recurring price whose end of life has passed.
3. **Nobody notified:** on no host, without an owner (or one without an e-mail address), and nobody chose the items nobody owns (§5.2).
4. **Estimates without an invoice:** estimated charges older than `Licenses__EstimateDays` (45).
5. **Without a price:** active items with no amount (other than *Free* and *Trial*): the costs miss them.
6. **Reviews overdue:** §5.7.

## 7. Documents

### 7.1 For the person

- **The *Documents* tab** of an item lists its documents: name, kind, description, size, added by and when, and the charge or renewal it belongs to.
- **Upload** by button, by drop on the tab, several at once, with progress, as the editors do (`useUpload.ts`'s sending, reused). Kinds are suggested from the names (§3.9).
- **Download** (*Details*): each click is audited, as a WireGuard tunnel's configuration is.
- **Change** a document's kind, description, charge and renewal; **remove** it (*Edit*).
- **Anything an attachment may be:** PDF, text, Markdown, images, `.key`, `.lic`, `.xml`, `.zip`, …, up to 25 MB (`KnowledgeBase:MaxAttachmentBytes`); executables are refused with the existing message. Everything is a download; nothing is shown inline (previews are §18).

### 7.2 The server

- **`POST /api/v1/licenses/{id}/documents?name=…&kind=…`** (*Edit*, `[FileUploadLimit]`, audited `LicenseDocumentAdded`): the body streams to `FileStore.StoreAsync(…, FileKind.Attachment, …)`, as a board's image does; then the `license_documents` row and the `file_uses` row are written together. A file is never left without its use, so it is never served by the generic URL as an unused upload.
- **`GET /api/v1/licenses/{id}/documents/{fileId}/{name}`** (*Details*, audited `LicenseDocumentDownloaded`) serves only a file that item has (a join on `license_documents`). It is always sent as `application/octet-stream` with `attachment`, `nosniff` and `Cache-Control: private, no-store`: a key file shouldn't stay in a browser's cache for a year.
- **The generic `GET /api/v1/files/{id}/{name}` refuses (404) a file that only items use.** A file that a page also uses (the same bytes uploaded twice, deduplicated) stays served there: its bytes are on that page already.
- **`UploadsPerHour`** counts these documents like any upload.

### 7.3 The rest of the files machinery

- **`FileOwner.License` and `FileUserKind.License`,** and the `RecordAsync` switch. Its default branch maps to *Responsibility* today; it becomes an explicit case for each owner, and the default throws.
- **Removing a document** deletes its two rows; the file is then unused and the daily cleanup removes it after `UnusedFileHours` and `DeletedFileDays`, as any file. `FileUses.RemoveLicenseAsync` is the second place uses are removed, after `SyncBoardAsync`.
- ***Admin → Knowledge base → Files*** (`FileAdmin.OverviewAsync`, `FilesTab.vue`) counts these documents in the sizes. Its *used by* says *LIC-12* with a link for a caller with *Licences & services: View*, and *a licence or service* otherwise. **Deleting such a document there is 409 `license_document`:** *Remove it on the licence or service.* `useTarget()`'s default (the location route) becomes an explicit case per kind.
- **`MachineAccess.Files`** doesn't change: it reads only pages and locations.
- ***File storage*** (428) and its hash check cover the documents as every file.

## 8. The web UI

### 8.1 The page and the menu

- **`/licences/:tab(overview|licences|services|hardware|upcoming|costs|templates)?`**, `LicencesView.vue` with `TabNav`, as `ResponsibilitiesView`; the tab defaults to *Overview*:

  ```
  ┌───────────────────────────────────────────────────────────────────────────────────────────┐
  │ Licences & services                                                          [+ Add ▾]    │
  │ Overview │ Licences 3 │ Services 1 │ Hardware │ Upcoming 5 │ Costs │ Templates            │
  ├───────────────────────────────────────────────────────────────────────────────────────────┤
  ```

  - The badges: *Licences*, *Services* and *Hardware* count what of their type needs attention (red while something fails); *Upcoming* counts the next 30 days.
  - ***+ Add*** (*Edit*) is a split button: ***Licence…***, ***Service…***, ***Hardware…***, and ***From a template…*** (a picker with search; a template's type and defaults are filled in). On a type's tab, its own type comes first.
  - *Templates* is shown to everyone with *View*; changing them is *Manage*.
- **The menu:** ***Licences & services*** in the group *Knowledge*, after *Knowledge base*, with `pi pi-id-card`, right *Licences & services: View* (decision 4), and **`feature: 'licenses'`**, so it is there only while the feature is on (§13.2). The group's list grows; `navigation.test.ts` asserts the groups, not *Knowledge*'s entries, so it needs only the new icon's uniqueness, and that the entry goes without the feature.
- **The routes** carry `meta.feature: 'licenses'`; while the feature is off, the guard sends them to *not found*, as an unknown path (§13.2).
- **The item's page:** `/licences/:number(\\d+)/:tab(overview|hosts|costs|renewals|documents|history)?`, by number, as `/kb/:number`, for all three types.
- **`api/licenses.ts`** beside `resources.ts`, as `responsibilities.ts`, with `licenseKeys` under one prefix so a change refreshes the lists, the overview, the costs, the page, the panels and the sensor.

### 8.2 The *Overview* tab: the cost and licence overview

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Currency EUR ▾   Type ▾   Category ▾   Cost centre ▾   Location ▾   Owner ▾                       │
│                                                                                                   │
│ Per month        Per year          Next 12 months       Spent in 2026      Needs attention  Cancel by │
│ EUR 6,420.00     EUR 77,040.00     EUR 79,310.00        EUR 71,460.00      7                3        │
│ + CHF 1,190.00   + CHF 14,280.00   of it if renewed     2025: 64,102.00    2 failing        within   │
│                                    EUR 8,900.00                                             90 days  │
│                                                                                                   │
│ Cost per month · EUR                                                    ■ Spent   ▨ Forecast       │
│  ▆ ▆ ▇ ▆ ▆ ▆ █ ▆ ▆ ▇ ▆ ▆ │ ▨ ▨ ▨ ▨ ▨ ▨ ▨ ▨ ▨ ▨ ▨ ▨                                               │
│  Dec 25           Nov 26 │ Dec 26           Nov 27                                               │
│                                                                                                   │
│ Per year by category · EUR                   Per year by provider · EUR                           │
│ Cloud & SaaS        ██████████  28,920       Contoso                 ████████  21,300             │
│ Connectivity        ██████      16,200       Telekom                 ██████    16,200             │
│ Software            █████       13,900       Hetzner                 ███        8,400             │
│ Support & maint.    ███          8,100       …                                                    │
│ …                                            Other providers (14)    ███        9,800             │
│                                                                                                   │
│ Needs attention                              Seats counted on hosts                               │
│ ● LIC-21 Adobe Acrobat Pro   expired 14 Nov  LIC-13 Windows Server CALs  ████████░░  40/50 devices │
│ ● SVC-33 Phones Zürich       cancel by 2 Jan LIC-9  Veeam B&R            ██████████ ⚠ 7/5, over 2  │
│ …                                                                                                 │
│ Worth a look                                 Ends of life within a year                           │
│ 11 unused seats · EUR 1,320.00 a year        LIC-4  Windows Server 2016    12 Jan 2027   3 hosts   │
│ 2 paid past their end of life                HW-41  Dell PowerEdge R640    31 May 2027   1 host    │
│ 3 nobody is notified about                                                                        │
│ 4 estimates without an invoice                                                                    │
│ 5 without a price                                                                                 │
│ 2 reviews overdue                                                                                 │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **One call,** `GET /api/v1/licenses/overview?currency=&type=&category=&costCenter=&location=&owner=`, computed on the server with `CostMath` (decision 19). The filters in one row above it all, kept in the URL, apply to every tile, chart and list.
- **The tiles:**
  - ***Per month*** and ***Per year:*** the running cost (§6.1) in the chosen currency, the others under it;
  - ***Next 12 months:*** the forecast (§6.4), with its *if renewed* part;
  - ***Spent in {year}:*** this year's charges, with last year's total for comparison;
  - ***Needs attention:*** items whose verdict isn't OK, with how many fail (446, 448);
  - ***Cancel by:*** cancellation deadlines within 90 days, with the next one.

  Each tile links to the list it sums.
- **Cost per month:** 24 columns, the last 12 months spent and the next 12 forecast (§8.12). A rule marks today. Hovering a month shows its total, spent and forecast, the *if renewed* part and its three largest categories. A click opens *Costs* for that month.
- **Per year by category** and **by provider:** ranked horizontal bars, largest first, the running cost per year printed at each bar's end. The providers show the top 10, then *Other providers (n)*. A click opens *Costs* grouped and filtered so.
- **Needs attention:** the items with a finding, worst first, with their dot, number, name and the finding's date. At most 10, then *All n*.
- **Seats counted on hosts:** a list of meters, one per item with `SeatsFromHosts`: name, a bar of used against covered, and the figures. Over-assignment shows in the warn colour with ⚠ and *over by 2*, never by colour alone.
- **Worth a look** (§6.7) and **Ends of life within a year:** links to their lists.
- **Empty:** *Nothing here yet: add the first licence or service, or make templates for the ones you buy most.*

### 8.3 The *Licences*, *Services* and *Hardware* tabs

**One table, `LicenseTable.vue`, with the columns each type needs.** The licences:

```
┌────────┬──────────────────────────┬───────────────────────┬──────────┬───────┬────────────────────┬──────────┬──────────────┬───────────┐
│ No.    │ Licence                  │ Product               │ Vendor   │ Hosts │ Expires            │ EOL      │ Per year     │ Reviewed  │
├────────┼──────────────────────────┼───────────────────────┼──────────┼───────┼────────────────────┼──────────┼──────────────┼───────────┤
│ LIC-12 │ ● M365 Business Standard │ Microsoft · M365 B.S. │ Contoso  │ –     │ 31 Dec 2026 · 35 d │ –        │ EUR 3,960.00 │ 2 Mar 26  │
│ LIC-4  │ ● Windows Server 2016    │ Microsoft · WS 2016   │ –        │ 3/3   │ doesn't expire     │ 12 Jan 27│ one-time     │ due       │
│ LIC-9  │ ● Veeam B&R              │ Veeam · B&R v12       │ Comp. AG │ 7/5 ⚠ │ 1 Mar 2027 · auto  │ –        │ EUR 1,450.00 │ 1 Oct 26  │
├────────┴──────────────────────────┴───────────────────────┴──────────┴───────┴────────────────────┴──────────┴──────────────┴───────────┤
│ 42 licences · per year: EUR 18,420.00 · CHF 2,300.00 · one-time this year: EUR 890.00                                                 │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The services:

```
┌────────┬────────────────────────────┬───────────┬──────────────┬──────────┬────────────────────────────┬────────────────┬──────────┐
│ No.    │ Service                    │ Provider  │ Category     │ Location │ Term                       │ Per month      │ Owner    │
├────────┼────────────────────────────┼───────────┼──────────────┼──────────┼────────────────────────────┼────────────────┼──────────┤
│ SVC-31 │ ● Business Internet 1 Gb/s │ Swisscom  │ Connectivity │ Zürich   │ renews 1 Jul 2027 ·        │ CHF 990.00     │ A. Meier │
│        │                            │           │              │          │ cancel by 2 Apr 2027       │                │          │
│ SVC-20 │ ● Dedicated server AX102   │ Hetzner   │ Hosting      │ –        │ renews monthly             │ EUR 145.00     │ B. Keller│
│ SVC-35 │ ● Azure                    │ Microsoft │ Cloud & SaaS │ –        │ usage                      │ ~EUR 1,100.00  │ B. Keller│
└────────┴────────────────────────────┴───────────┴──────────────┴──────────┴────────────────────────────┴────────────────┴──────────┘
```

The hardware: number, name, manufacturer · model, host(s), kind (*purchase*, *lease*), started on, *warranty until* or *lease ends*, end of life, price (one-time or per month).

- **One row per item** of the tab's type, from `GET /api/v1/licenses?type=` (all but the retired ones unless asked). Items are hundreds, not hundred-thousands, so **sorting, searching and filtering happen in the browser**, and the URL keeps them all (`?q&sort&state&kind&…`) (decision 19).
- **The columns, all sortable,** besides those shown: kind, category, cost centre, location, owner, renewal, per month and per year, seats, updated; the narrow screens hide the less needed ones. A column chooser keeps the choice per type in the browser.
- **The sort** uses `listSort.ts`'s rules, moved to `components/listSort.ts` and given the fields as accessors: nulls last both ways, ties by name, numbers and days as such. *Expires* sorts by the day; *doesn't expire* comes last.
- **The search** looks in the number (`LIC-12`, `12`), name, manufacturer, product, edition, vendor, licensed to, reference, customer number, cost centre, the keys' hints, and the names of the assigned hosts. Not in the note (decision 18).
- **The filters** (`FilterBar`):
  - **State:** *needs attention* (445, 447, 449), *failing* (446, 448), *OK*;
  - **Status:** active and cancelled by default; *retired* added on request;
  - **Kind**, **category**, **manufacturer**, **vendor**, **template**, **cost centre**, **location**, **owner** (*mine* first);
  - **Host** (items on this host), *on no host*, and *nobody notified*;
  - **Expires within** 30, 90 or 365 days; **review due**; **without a price**.
- **The totals** under the table, for the rows shown: the count, the running cost per year per currency, and the one-time purchases of this year per currency. No conversion between currencies.
- **Selection** (*Edit*): *Mark reviewed…* and *Export*. **Export CSV** (`GET /api/v1/licenses/export.csv` with the same filters, or the selection's ids) writes the visible columns plus licensed to, reference, customer number, renewal, notice, seats and unit, the hosts' names, and the costs per month and per year. It is formula-safe with the audit log's `Csv()`. **Never the keys, the note or the documents.** Not audited: it is what the page shows.
- **Empty states:** *No licences yet: add the first, or make a template for the products you buy most.*, the same for services and hardware, and *Nothing matches the filters.*
- **Phones:** `ListTable`'s mobile list: number, name, dot, expiry and days left, cost.

### 8.4 The *Upcoming* tab

```
Next  3 | [6] | 12 | 24 months   Type ▾   Owner ▾            Cost in this time: EUR 39,840.00 · CHF 7,140.00
                                                                         of it if renewed: EUR 4,960.00

Overdue
  ● LIC-21 Adobe Acrobat Pro          expired on 14 Nov 2026                 2 hosts   [Renew…]
  ◐ LIC-4  Windows Server 2016 Std    review due since 3 Oct 2026            3 hosts   [Mark reviewed…]

December 2026                                                                  EUR 6,420.00
  31 Dec  LIC-12  M365 Business Standard   expires · manual       no host · nobody notified   EUR 3,960.00 if renewed
January 2027                                                                   EUR 6,420.00
  12 Jan  LIC-4   Windows Server 2016 Std  end of life (end of extended support)   3 hosts
  30 Jan  LIC-9   Veeam B&R                cancel by (renews on 1 Mar)           7 hosts
April 2027                                                                     EUR 6,420.00
   2 Apr  SVC-31  Business Internet 1 Gb/s cancel by (renews on 1 Jul)         no host · A. Meier
  30 Apr  HW-41   Dell PowerEdge R640      warranty ends                         1 host
```

- **`GET /api/v1/licenses/upcoming?months=12`** gives the dates of every item that is not retired:
  - expiries, with the renewal's kind and, for recurring items, the price of the next term;
  - automatic renewals and cancellation deadlines;
  - warranty and lease ends (hardware's expiries);
  - ends of life;
  - reviews due;
  - **and, for recurring items, every renewal within the window,** so 24 months show a monthly-renewed subscription 24 times.
- **Billing days are not listed:** a monthly service would fill the tab. **Each month's subtotal** is the forecast for it (§6.4), per currency, and the header has the window's total and its *if renewed* part.
- **Overdue** on top: expired, past their end of life, reviews due.
- **Who hears about it:** the hosts' count, or for an item on no host its owner's name, and *nobody notified* for the items of §5.2.
- **Actions in place** for *Edit*: *Renew…*, *Mark reviewed…*, *Cancel…*.

### 8.5 The *Costs* tab

```
Period [2026 ▾]  Group by [Category ▾]  Show [Spent and forecast ▾]  Currency [EUR ▾]   [ Costs | Charges ]   ⤓ CSV   [Record a cost…]

Cost per month · EUR     (the columns of §8.2, for the period)

┌───────────────────────────┬───────┬───────┬─────┬───────┬────────┬───────────┬───────────────┐
│ Category                  │ Jan   │ Feb   │  …  │ Nov   │ Dec ▨  │ 2026      │ Per year now  │
├───────────────────────────┼───────┼───────┼─────┼───────┼────────┼───────────┼───────────────┤
│ ▸ Cloud & SaaS            │ 2,310 │ 2,310 │     │ 2,410 │ 2,410  │ 28,400.00 │ 28,920.00     │
│ ▾ Connectivity            │ 1,350 │ 1,350 │     │ 1,350 │ 1,350  │ 16,200.00 │ 16,200.00     │
│     SVC-36 Fibre Berlin   │   990 │   990 │     │   990 │   990  │ 11,880.00 │ 11,880.00     │
│     SVC-32 LTE backup line│   360 │   360 │     │   360 │   360  │  4,320.00 │  4,320.00     │
│ ▸ Software                │ …                                                               │
├───────────────────────────┼───────┼───────┼─────┼───────┼────────┼───────────┼───────────────┤
│ Total                     │ 6,120 │ 6,140 │     │ 6,420 │ 6,420  │ 77,880.00 │ 77,040.00     │
└───────────────────────────┴───────┴───────┴─────┴───────┴────────┴───────────┴───────────────┘
▨ forecast · of it if renewed: EUR 3,960.00 · Also in CHF: 14,280.00 in 2026 [Show]
```

- **`GET /api/v1/licenses/costs?from=&to=&group=&show=&currency=`** plus the overview's filters, computed on the server.
- **Period:** a year, the last 12 months, the next 12 months, or a range of months. ***Years*** shows the last five years as columns instead of months.
- **Group by:** category, type, provider, manufacturer, cost centre, location, host, owner or item (§6.5). Each group opens to its items; every column sorts.
- **Show:** *spent* (the charges), *forecast* (§6.4), or *spent and forecast*: past months spent, coming months forecast and shaded, the current month both.
- ***Per year now*** is the running cost (§6.1), next to what the period cost: a group that grew shows it.
- **The *Charges* view** (`?show=charges`) lists every charge: day, item, kind, period, amount, estimated, source, reference, invoice. It filters by period, type, category, item, kind, source and *estimated*. *Edit* corrects them inline (§6.2, §6.3). It is where invoices are checked, and the estimates confirmed.
- **CSV** for both views (`GET costs/export.csv`, `GET charges/export.csv`), one currency column, formula-safe.
- **The chart** above is the overview's columns for the period and the filters (§8.12).

### 8.6 The *Templates* tab

- **Three views** (`?show=templates|categories|names`):
  - ***Templates*:** a table (name, type, category, manufacturer, product, edition, vendor, kind, default term, billing and price, end of life, items made from it), sortable and searchable as the lists. A row opens `TemplateDialog.vue` (the fields of §3.2). ***New template*** and ***Make an item from it***.
  - ***Categories*:** the categories in their order, each with its description, its count of items and templates, and its running cost per year. They can be renamed, reordered, added and deleted (§3.1); *Default for* is set here.
  - ***Names*:** every manufacturer, vendor and cost centre in use (and the seat units), with the number of items and templates using it. ***Rename…*** changes it everywhere; renaming to a name in use **merges** the two (*Microsoft Corp.* → *Microsoft*), with the count in the dialog. Audited `LicenseNamesRenamed`, with the old and new name and the number of rows.
- **Changing a template** changes no item's own fields: they were copied. **Its end of life is the exception,** followed by every item with `EndOfLifeFromTemplate`. The dialog says how many: *14 items follow this end of life; they will be evaluated again.*
- **Deleting a template** (*Manage*) keeps its items as they are (`TemplateId` null; those that followed its end of life keep it as their own). The dialog says so.
- **Suggestions:** the manufacturer, vendor, cost centre and unit fields everywhere offer the names in use (an `AutoComplete` over `GET /api/v1/licenses/names`).
- **Rights:** reading is *View*; templates, categories and renames are *Manage* (decision 7).

### 8.7 The item's page

**Header:** `SVC-31 · Business Internet 1 Gbit/s`, the type, the verdict's tag, and the status (*cancelled*, *retired*). For *Edit* it has ***Renew…***, ***Mark reviewed…***, ***Assign to hosts…***, and a menu with *Record a cost…*, *Duplicate*, *Cancel…* or *Reactivate*, *Retire…*, and *Delete…* (*Manage*).

**Tabs:** *Overview* · *Hosts & sensors* (count) · *Costs* · *Renewals* (count) · *Documents* (count) · *History*.

**Overview:**

- **The headline:** *Valid · 35 days left*, *Expires in 5 days*, *Expired 2 days ago*, *Doesn't expire*, *Renews automatically in 95 days · cancel by 2 Apr 2027*, or *Warranty until 30 Apr 2028*. Under it, a bar runs from the term's start to the expiry, marking today and the warning's start, and the end of life when it falls within it. The days are `certificate.ts`'s `daysLeftText`, moved to `format.ts`.
- **The findings,** each a line in its colour, and *On no host and without an owner, so nobody is told when it expires.* (§5.2).
- **The costs in one line:** *EUR 330.00 per month · EUR 3,960.00 per year · spent this year EUR 3,630.00*, linking to the *Costs* tab.
- **Panels of `InlineField` rows,** each saving the item (`PUT …` with the current values and the one changed, as `sensorRequest` does), read-only without *Edit*:
  - **Product** (or ***Service***, ***Hardware***): type, template (a link), category, manufacturer, product, edition or model, vendor or provider, kind, licensed to, reference, customer number, portal (a link);
  - **Support:** support contact, support URL, SLA;
  - **Where and whose:** cost centre, location (`LocationPicker`), owner (`PersonPicker`);
  - **Keys:** licence key and product key, masked with their hint, ***Show*** for *Details* (audited, §3.4), and a password-type editor for *Edit* with *Clear*;
  - **Seats:** seats and unit, *counted on hosts*, used (with ⚠);
  - **Price and billing:** amount and currency, total or per seat, one-time, *every n months* or *variable*, per month and per year, *PNMT records the charges*;
  - **Term:** started on, expires on (`DatePicker`, `parseDay` for typed days), term, renewal, warn days, notice days and the cancellation deadline, *warn before it renews*;
  - **End of life:** *from the template (12 Jan 2027, end of extended support)* or the item's own day and note; warn days;
  - **Review:** last reviewed on and by with the note, every n months, *due on …*;
  - **Notifying:** who hears about it: *the owner, by e-mail* (or *the owner has no e-mail address in PNMT*), *the 3 hosts' sensors, through their rules*, *2 people who chose items nobody owns*, or *nobody* (§5.2). Whether the owner turned these e-mails off isn't shown: that choice is theirs. For the owner reading it, the line links to their *Account → Notifications*;
  - **Note:** Markdown, as a responsibility's description.
- **The form for a new item** (`LicenseFormView.vue`, `/licences/new?type=…&template=…`) has the same groups, shown for the type. It fills in the template's defaults, and the kind's defaults (§3.3) when the kind changes. It offers to record the past charges (§6.2). *Save* creates the item, then offers *Assign to hosts…*.
- ***Duplicate*** opens that form with everything but the keys, the documents, the charges, the renewals and the review, named *… (copy)*.

**Hosts & sensors:**

- **The hosts:** the assignments (host with its state, location, the sensor's state and last message, seats, note, assigned by and when), sortable. ***Assign to hosts…*** opens a dialog:
  - a host picker (`LinkPicker` with `kinds: ['Host']`, searching through `GET /api/v1/licenses/targets?kind=host&q`, *Edit*, so assigning doesn't need *Monitoring: View*);
  - several hosts at once, the seats per host, and the seat count live.

  Rows can be unassigned, one or many (*Unassign…*), and their seats and note changed inline. On no host, the tab says *On no host: this item has no sensor. Its owner is told by e-mail.*
- **Linked sensors** (§5.4): host, sensor, state, last message, note; ***Link sensors…*** and *Unlink*.

**Costs:** the running cost per month and per year, the spending per year (*2026: EUR 3,630.00 · 2025: EUR 3,600.00*), the forecast of the next 12 months, and the charges, newest first (day, kind, period, amount, estimated, reference, the invoice as a document link, by). *Record a cost…*; corrections as §6.2 and §6.3.

**Renewals:** the renewals, newest first: day, kind, old and new expiry, the price from then on, reference, the paper (a document link), note, by.

**Documents:** §7.1.

**History:** the audit entries of the item, its assignments, links, renewals, charges and documents, from `GET /api/v1/licenses/{id}/history` (*View*), shown with `AuditHistory.vue`, which gets a `load` prop instead of its fixed URL.

### 8.8 The host's page and the location's page

- **The host: a *Licences & services* panel** (`LicencesPanel.vue`, *View*) after *Responsibilities*: the host's items with their dot, number, name, expiry and days left, each a link. It shows nothing while the host has none, as the responsibilities' panel does.
- ***Assign a licence or service…*** in the host's menu (*Edit*): a picker of the items (number, name, seats left), the seats, a note.
- **The *Sensors* panel** lists the item sensors like the others, with `pi pi-id-card` and *Licence & service*.
- **The location: the same panel on its *Overview* tab,** for *View*, with the items whose location it is and those on its hosts, and their running cost per month, from `GET linked?location=`. An office's internet line, phones and printers' leases are on the office's page.

### 8.9 The sensors' pages

- **An item sensor's page** has a status strip (`LicenseStatusStrip.vue`, as `CertificateStatusStrip`), from `SensorDetail.License`, for everyone: *Licence LIC-12 · Microsoft 365 Business Standard · valid until 31 Dec 2026 · 35 days left*, and *Open* for *Licences & services: View*.
  - **The configuration rows are hidden,** except *Enabled*, *Ignore*, *Importance* and *Notes*. The name is read-only, with *Follows the licence*.
  - **The menu** has *Unassign…* (*Edit*) instead of *Delete sensor…*, and no *Type*.
  - `useLookups.ts`: `typeLabel('LICENSE')` is *Licence & service*, `typeIcon` is `pi pi-id-card`. *Add sensor* doesn't offer the type (`CanCreate` false).
- **A linked sensor's page** has the strip of §5.4, one line per linked item.

### 8.10 The global search

- `SearchResult.Licenses` (`SearchLicense`: id, number, type, name, product, the worst finding), filled only for *Licences & services: View* and while the feature is on, as tickets are only for *Redmine: View* and while Redmine is configured.
- **It finds** items by number (`LIC-12`, `svc 31`, `12`), name, product, manufacturer, vendor, reference and customer number. For a query of exactly five letters and digits, it also matches the keys' hints.
- `GlobalSearch.vue` gets the kind, its icon and its route. The MCP `search` tool doesn't list items (§11).

### 8.11 Windows activation (phase L8, optional)

- **On a licence's *Hosts & sensors* tab,** for a licence with a product key hint: ***Hosts that use this key*** lists the Windows hosts whose agent reports `ActivationInfo.PartialKey` equal to the hint. Those not assigned are offered: *2 hosts use this key but aren't assigned: Assign them.*
- **On the host's panel:** *Windows is activated with …CC9DR: LIC-4*, or *with …8XK2P, a key that is in no licence*, for a host with a Windows agent.
- **Only the five characters** are compared; nothing more of a key ever leaves the server, as today. Volume keys (GVLK for KMS) are the same on every machine, so the match says *KMS client key* and offers nothing.

### 8.12 The charts

- **Hand-made SVG,** as `MonthColumns.vue`: `CostColumns.vue`, `RankedBars.vue` and `MeterList.vue` in `components/charts/`, plus a plain stat tile. No chart library.
- **The form follows the job:**
  - the headline numbers are tiles, not charts;
  - the months are columns;
  - categories and providers are ranked horizontal bars, largest first;
  - seats are a list of meters.

  No pies, no donuts, no second axis.
- **One hue for the money.** The columns and bars use one hue: spent in its full step, forecast in a lighter step of the same hue, with a legend *Spent · Forecast*. Categories get no colours of their own: there are eleven, more than colours can tell apart, and the bars are ranked by length anyway.
- **The status colours are for the verdict only** (the dots, *over by 2*), always with an icon and a word.
- **One currency per chart** (§6.6).
- **Hover:** a tooltip on every column, bar and meter, with a hit area larger than the mark. **Keyboard:** the marks can be reached with Tab, and show the tooltip on focus.
- **Each chart has *Show as table*,** with the same numbers as the CSV, so nothing is readable only as a shape.
- **Light and dark:** the steps come from the theme's tokens and are checked for contrast on both surfaces; dark mode has its own steps, not inverted ones.
- **The figures** are formatted as the totals are (`EUR 28,400.00`; the axis in thousands, `28k`).

## 9. Rights

**`Area.Licenses`**, label *Licences & services*, in `RightsCatalog`:

| Level | Allows |
|---|---|
| **View** | The *Licences & services* page and every item: products, dates, seats, hosts, **prices and costs** (the overview, the costs, the charges), renewals, reviews, notes, support details, the documents' names, the history; the templates and categories; the host's and location's panels; items in the search; the CSV exports |
| **Details** | View, plus showing the keys and downloading the documents (both audited) |
| **Edit** | Details, plus adding, changing, duplicating, assigning and unassigning, linking sensors, renewing, reviewing, cancelling and retiring items; recording and correcting charges; adding and removing documents; choosing the notices about items nobody owns |
| **Manage** | Edit, plus the templates, the categories, renaming manufacturers, vendors and cost centres, deleting items, renewals and people's charges |

- **Why *Details* for the keys:** whoever reinstalls a machine needs the key, not the right to change licences. *Details* exists for Monitoring and the network map with the same sense: more than *View*, nothing changed.
- **Costs are *View*** (decision 21, open question 5): whoever sees what is bought sees what it costs. A separate area for the money is the alternative in §17.
- **What stays *Monitoring*:**
  - the item sensors in the lists, the dashboard and the host page;
  - their state, message, events and uptime;
  - their notifications, importance and *History*;
  - the linked items' strip on a sensor.

  A *Monitoring: View* user without the area sees *Licence: M365 Business Standard (LIC-12) · Expires on 31 Dec 2026*, and no price, key or link.
- **The sensor's writes** (*Enabled*, *Ignore*, *Importance*, *Notes*) are *Monitoring: Edit*, as any sensor's. Unassigning is *Licences & services: Edit*.
- **MCP:** `McpRights.Ceiling[Licenses] = View`, `ReadOnlyCap[Licenses] = View`. The tools read; keys and documents are no tool's at any level (§11).
- **The web client:** `api/rights.ts` gets `Licenses: ['View', 'Details', 'Edit', 'Manage']`. The group page, *Your access* and *Documentation → Access* read the catalog.
- **The groups:** the migration grants the area to nobody (decision 20), as no area added after the groups' migration (`GroupsAndRights`) was granted. Admins have every level; the release notes ask them, once they turn the feature on, to grant *Licences & services*.
- **While the feature is off** (§13.2), the area isn't listed on the group page, on *Your access* or in *Documentation → Access* (`RightsCatalog.Visible`, which every list of areas reads). A grant of it can't be made, and **a group's save keeps its stored rows** of the area, so turning the feature on again gives every group back what it had. An admin's every level means nothing while off: the API answers 404 before any right is checked.
- **Counts that change:** the catalog has 26 areas (`GroupsTests`), 25 of them listed while the feature is off; `Rights.cs`'s comment saying 17 is corrected.

## 10. API and audit

### 10.1 Endpoints

**While the feature is off, every endpoint below answers 404 `licenses_disabled`** (*Licences & services is turned off on this installation.*), for everyone, admins too, before the right is checked: a filter on the three controllers, `[RequiresLicenses]` (§13.2). Nothing is audited for these 404s.

**Items** (`LicensesController`, `api/v1/licenses`, class-level *View*):

| Method and path | Right | Audit | What |
|---|---|---|---|
| `GET` | View | – | The items as rows (`LicenseRow`), `?type=&status=active,cancelled,retired` |
| `GET export.csv` | View | – | §8.3 |
| `GET overview` | View | – | §8.2, with its filters |
| `GET costs` | View | – | §8.5: `from`, `to`, `group`, `show`, `currency` and the filters |
| `GET costs/export.csv` | View | – | §8.5 |
| `GET charges`, `GET charges/export.csv` | View | – | The *Charges* view, with its filters |
| `GET upcoming?months=` | View | – | §8.4 (1–36 months) |
| `GET names` | View | – | Manufacturers, vendors, cost centres and units in use, with counts |
| `GET linked?host=`, `?location=`, `?sensor=` | View | – | The panels and strips |
| `GET targets?kind=host\|sensor&q=` | Edit | – | Hosts to assign and sensors to link, without *Monitoring: View* |
| `GET {id}`, `GET number/{number}` | View | – | `LicenseDetail`: everything but the keys, with the hints, the verdict, the findings and the costs in one line |
| `GET {id}/keys` | Details | `LicenseKeysShown` (warn) | Both keys; 409 `key_lost` |
| `GET {id}/history` | View | – | The audit entries, paged |
| `POST` | Edit | `LicenseCreated` | `LicenseRequest`, with `recordPastCharges` |
| `POST {id}/duplicate` | Edit | `LicenseCreated` | §8.7 |
| `PUT {id}` | Edit | `LicenseChanged` | `LicenseRequest`; keys: null keeps, `""` clears |
| `POST {id}/renew` | Edit | `LicenseRenewed` | `LicenseRenewRequest` |
| `POST {id}/review` | Edit | `LicenseReviewed` | `{ on?, note? }`; `POST review` with `ids` for many |
| `POST {id}/cancel`, `{id}/reactivate` | Edit | `LicenseCancelled`, `LicenseReactivated` | `{ endsOn? }`, required without an expiry |
| `POST {id}/retire` | Edit | `LicenseRetired` | `{ confirmed }`, required when it has sensors |
| `DELETE {id}?number=` | Manage | `LicenseDeleted` | The number must match, with or without its prefix |
| `GET {id}/assignments` | View | – | |
| `POST {id}/assignments` | Edit | `LicenseAssigned` | `{ hostIds, seats?, note? }`, ≤ 500 |
| `PUT {id}/assignments/{hostId}` | Edit | `LicenseAssignmentChanged` | Seats, note |
| `POST {id}/assignments/remove` | Edit | `LicenseUnassigned` | `{ hostIds }` |
| `GET {id}/sensor-links` | View | – | |
| `POST {id}/sensor-links` | Edit | `LicenseSensorLinked` | `{ sensorIds, note? }` |
| `DELETE {id}/sensor-links/{sensorId}` | Edit | `LicenseSensorUnlinked` | |
| `GET {id}/renewals` | View | – | |
| `PUT {id}/renewals/{renewalId}` | Edit | `LicenseRenewalChanged` | |
| `DELETE {id}/renewals/{renewalId}` | Manage | `LicenseRenewalDeleted` | |
| `GET {id}/charges` | View | – | |
| `POST {id}/charges` | Edit | `LicenseChargeRecorded` | §6.3 |
| `PUT {id}/charges/{chargeId}` | Edit | `LicenseChargeChanged` | |
| `DELETE {id}/charges/{chargeId}` | Manage | `LicenseChargeDeleted` | 409 `automatic_charge` for PNMT's own |
| `GET {id}/documents` | View | – | |
| `POST {id}/documents?name&kind&chargeId&renewalId` | Edit | `LicenseDocumentAdded` | §7.2 |
| `PUT {id}/documents/{fileId}` | Edit | `LicenseDocumentChanged` | Kind, description, charge, renewal |
| `GET {id}/documents/{fileId}/{name}` | Details | `LicenseDocumentDownloaded` | §7.2 |
| `DELETE {id}/documents/{fileId}` | Edit | `LicenseDocumentRemoved` | |
| `POST names/rename` | Manage | `LicenseNamesRenamed` | `{ field: manufacturer\|vendor\|costCenter\|seatUnit, from, to }` |

**Templates** (`LicenseTemplatesController`, `api/v1/license-templates`):

| Method and path | Right | Audit |
|---|---|---|
| `GET`, `GET {id}` | View | – |
| `POST` | Manage | `LicenseTemplateCreated` |
| `PUT {id}` | Manage | `LicenseTemplateChanged` |
| `DELETE {id}` | Manage | `LicenseTemplateDeleted` |

**Categories** (`LicenseCategoriesController`, `api/v1/license-categories`):

| Method and path | Right | Audit |
|---|---|---|
| `GET` | View | – |
| `POST` | Manage | `LicenseCategoryCreated` |
| `PUT {id}`, `PUT order` | Manage | `LicenseCategoryChanged` |
| `DELETE {id}?moveTo=` | Manage | `LicenseCategoryDeleted` (with the count moved) |

**Elsewhere:**

- `SensorDetail.License` and `SensorDetail.LinkedItems` (§5.3, §5.4), empty while the feature is off. `SensorService` refuses the type's create, type change and delete (§5.3); `SensorApiController` refuses its events.
- `GET /system/info` gets `licenses` (bool, signed in only), as `redmine` and `zabbix` (§13.2).
- *Account → Notifications* (`UserNotifications.SettingsAsync`, `SaveSettingsAsync`) offers and accepts the three kinds of §5.5 only while the feature is on: `UserNotificationKind` gets `Available`, as `ToolSpec` has.
- `GET /api/v1/files/{id}/{name}` refuses files only items use (§7.2); `DELETE /api/v1/files/{id}` refuses their documents (§7.3), whether the feature is on or off.
- `GET /api/v1/audit-log/subjects/{id}` leaves out entries of the category *Licences & services* for a caller without its *View*: the host's and the sensor's *History* use it with *Monitoring: View* only.
- `openapi.json` and `schema.d.ts` regenerated (`tools/npm.sh run gen:api`).

### 10.2 The audit

- **The category** `AuditCategory.Licenses` (*Licences & services*), and the targets `License`, `LicenseTemplate` and `LicenseCategory`. The entries of an assignment, link, renewal, charge and document target the item. An assignment's entry also belongs to its host, a link's to its sensor, and a document's names the file.
- **The tones:** `LicenseKeysShown` and `LicenseDocumentDownloaded` are warn, explicitly. The rest follow the existing rules: *Created* is success; *Deleted*, *Retired*, *Cancelled* and *Removed* are warn.
- **The summaries:**
  - `LicenseRenewed`: *LIC-12 M365 Business Standard renewed until 31 Dec 2027 (was 31 Dec 2026), price from now on EUR 4,200.00, order R-2026-1187*.
  - `LicenseChargeRecorded`: *SVC-35 Azure: EUR 1,184.20 for Nov 2026, invoice 4471-118*.
  - `LicenseKeysShown`: *LIC-12: the keys were shown*.
  - `LicenseDocumentDownloaded`: *LIC-12: office-2024.lic (licence file) downloaded*.
- **The changes** come from `[Audited]` (`AuditChangesInterceptor`): prices and days as values, keys as *changed (secrets are never logged)*, the note as *+n −m lines*.
- **PNMT's automatic charges are not audited** (§6.2); its automatic renewals are.
- **Turning the feature off and on** is audited by what it does to sensors (§13.3), `AuditWho.System`, warn: `LicenseSensorsRemoved` (*Licences & services is turned off: 14 item sensors removed with their events; their 14 assignments stay.*) and `LicenseSensorsMade` (*Licences & services is turned on: 14 item sensors made from the assignments.*). Each entry also belongs to the hosts.
- **The audit log keeps the category's entries while the feature is off:** it is the record of what happened, and only its readers (*Audit log*) see it.
- **The owner's notices** are in the notification log, as every e-mail; they aren't audited.
- **`EndpointAuditTests`** lists every endpoint above with its right; `EveryAuditedEndpointWritesItsDeniedEntry` covers them.
- **The seed** writes one entry of the category (`DevSeedTests` counts one per category).

## 11. MCP and Cookie

- **`licenses_list`** (*View*, Read, `ForCookie`): the items with filters (`type`, `state`, `status`, `category`, `cost_center`, `location`, `owner`, `expires_within_days`, `host`, `manufacturer`, `vendor`, `search`). Each comes with number, type, name, product, kind, status, verdict, expiry, cancellation deadline, end of life, seats, the hosts' count, price, period, and the cost per month and per year. Paged.
- **`license_get`** (*View*, Read, `ForCookie`): one item by id or number, with everything of `LicenseDetail` but the keys (the hints neither), and:
  - its hosts, its linked sensors and their states;
  - its renewals, its charges of the last 12 months, and its documents' names;
  - its support details.

  **The note and the support contact go through `ToolSafety.WithoutCredentials`.**
- **`licenses_costs`** (*View*, Read, `ForCookie`): `{ group, from, to, show: spent|forecast|both, currency? }` and the filters. It returns the totals per group and currency, by month when asked, with the *if renewed* part.
- **Never:** keys, documents' contents, the exports.
- **So Cookie can answer** *what expires this month?*, *which licences are on srv-db-02?*, *what does connectivity cost us a year?*, *what will we pay in the first quarter?*, *who do I call about the line in Zürich?*
- **Only while the feature is on:** the three have `Available` = the switch, as Redmine's and Zabbix's tools have their configuration. Off, `tools/list` doesn't list them, a call is refused as any tool that isn't available, and Cookie doesn't know them.
- **The catalog tests:** Cookie's tools go from seventeen to twenty with the feature on, and stay seventeen while it is off (`McpToolsTests`, both); `McpExtensionsTests`' `Reads` gets the three.
- **Writes over MCP are §18:** renewing, assigning and recording costs are decisions a person makes on the page.

## 12. Security and privacy

| Threat | Answer |
|---|---|
| **A key read from the database or a dump** | Encrypted with Data Protection (`PNMTD.Licenses.Key.v1`); the key ring is a separate volume and a separate file of the backup (§3.4) |
| **A key read over a shoulder or in a screenshot** | Masked until *Show*; shown keys go when the page is left |
| **Who read a key** | `LicenseKeysShown` and `LicenseDocumentDownloaded` in the audit, warn |
| **A key or password in the note or the support contact** | The credentials scan warns and points to the key fields (§3.3) |
| **A key file reached by its file id** | The generic file URL refuses files only items use; the item's URL checks *Details* and that the item has the file (§7.2) |
| **A key file cached in a browser** | `no-store` on these documents |
| **A key or price in a sensor message** | Messages carry names, numbers, days and seats only (§4) |
| **Support details on sensor pages** | Provider, support contact, support URL, customer number and SLA only, for people who see the sensor; never prices, keys, the note or the reference (§5.4, decision 9) |
| **What the organisation pays, seen by the wrong people** | The area's *View*, which no group has until an Admin grants it (§9) |
| **These changes in the generic history** | Left out without the area's *View* (§10.1) |
| **Keys to an AI** | No tool returns keys or documents; the note and the support contact are scrubbed; Cookie's and MCP's level is *View* |
| **A file that is really an executable** | The store's blocked extensions, every dot part (unchanged) |
| **CSV formula injection** | The audit log's `Csv()` |
| **Prices in e-mails** | The owner's notices go only to people who hold the area's *View* when the e-mail is sent; never keys, the note, the support contact or documents (§5.5) |
| **A feature nobody asked for, after an update** | Off by default: an update to 1.30 adds empty tables and nothing anyone can see or reach (§13) |
| **Contract values and keys reached while the feature is off** | Every endpoint answers 404, the documents stay refused by the generic file URL, and no tool lists them (§13.2) |

- **The documents are files in the folder,** as the knowledge base's are, readable by whoever reads the folder or `files.tar` (decision 17). deploy/README.md says so next to the backups: *licence documents may contain keys; keep `files.tar` as safe as the database dump.*
- **The customer number on a sensor's page** lets a colleague call the provider in an outage. Whoever can see it can also ask the provider about the contract; that is the trade the strip makes, and the release notes name it.

## 13. The switch and the settings (`Licenses__*`, `LicenseOptions`)

### 13.1 Off by default

- **`Licenses__Enabled`, default `false`.** Unless `.env` says `Licenses__Enabled=true`, the feature is off (decision 22).
- **One place reads it:** `LicenseSetup.Enabled(configuration)`, as `RedmineSetup.Configured` is for Redmine. Everything in §13.2 asks it, on the server and, through `GET /system/info`, in the browser.
- **Read at start-up.** Changing it needs a restart, as every `.env` setting does (`./start.sh`, or `docker compose up -d` in deploy/).
- **In the files:** deploy/'s `.env` example gets `#Licenses__Enabled=true` with a line on what it turns on. `dev.env` turns it on, so the development seed has it (§16).
- **Turning it on:** set `Licenses__Enabled=true`, restart PNMTD, and grant *Licences & services* to the groups that need it (§9). The release notes and deploy/README.md say so in these three steps.
- **With the feature on and `BackgroundTasks:Enabled=false`,** only the evaluator stops, as every task does: the page and the API work, and nothing is evaluated.

### 13.2 What off means: neither usable nor visible

| Where | While the feature is off |
|---|---|
| **The menu** | No entry: `SystemInfo.Licenses` is false, and the entry has `feature: 'licenses'` (§8.1) |
| **`/licences`, `/licences/…`** | The *not found* page: `decide` gets the route's `meta.feature` and checks it as it checks the right (`router/guard.ts`) |
| **The API** | Every endpoint of `LicensesController`, `LicenseTemplatesController` and `LicenseCategoriesController`: 404 `licenses_disabled`, for admins too, before the right (§10.1) |
| **Rights** | The area isn't listed on the group page, on *Your access* or in *Documentation → Access*; stored grants stay (§9) |
| **Hosts and locations** | No *Licences & services* panel, no *Assign a licence or service…* in the host's menu |
| **Sensors** | No item sensors (§13.3). The type and its seven codes leave `SensorTypes()` and `StatusCodes()`, so neither the documentation nor the code labels list them |
| **A linked sensor's page** | No strip: `SensorDetail.LinkedItems` is empty |
| **The global search** | No items |
| ***Account → Notifications*** | Not the three kinds (§5.5); people's stored choices stay. A notice still pending isn't sent: the renderer returns null, as when there is nothing left to say |
| **MCP and Cookie** | Not the three tools (§11) |
| ***Documentation*** | No *Licences & services* page (`LicencesDocs.vue`) |
| ***System health → Background tasks*** | *Licence checks* shows *off*, as every task that isn't configured |
| ***Admin → Knowledge base → Files*** | An item's documents count in the sizes and say *used by: Licences & services (turned off)*. They can't be deleted there (409 `license_document`), so turning the feature off can't lose a document |
| **The audit log** | Keeps the category's entries: it is the record of what happened (§10.2) |
| **The release notes 1.30** | The one place it is named: what it is, that it is off, and how to turn it on |

**What stays while it is off:** the tables and everything in them (items, templates, categories, assignments, links, renewals, charges, document rows, keys), the documents' files (their `file_uses` rows keep them from the cleanup, and the generic file URL keeps refusing them), the groups' grants, people's choices of the three kinds, and the items' stored states. **The migration runs whatever the switch says** (§3.10).

**What doesn't run:** the evaluator (no automatic renewals, no automatic charges, no verdicts, no notices) and the item sensors.

### 13.3 Turning it off and on again

- **Turned off,** a start-up step, `LicenseSensorSync`, removes every item sensor with its events and clears the assignments' `SensorId`. It runs in `DatabaseMigrationService.StartAsync` after the migrations, whatever `BackgroundTasks:Enabled` says, so a test host sees the same. **The assignments stay.** It is audited once, with the count (`LicenseSensorsRemoved`, §10.2), and does nothing when there are no item sensors, so a restart that changes nothing writes nothing.
  - **Why remove them, not keep them:** nobody would evaluate them, so each would show its last state for ever, a failing one among the dashboard's problems. Hiding them instead would touch every query that shows sensors: the dashboard, hosts, events, boards, the search, Cookie's case book, the tools. They are the items' states on hosts, and the states can be made again (decision 23).
  - **What goes with them:** their events, and anything that names one of them by its id: a notification rule for that single sensor, a board element of it. Rules by host, by importance or for everything aren't affected. The release notes and the in-app documentation say so.
- **Turned on again,** the same step makes a sensor for every assignment without one, evaluates its item and writes its first event at once (§5.3). It is audited once (`LicenseSensorsMade`). A sensor made failing notifies through the rules, as any new failing sensor does.
- **Then the evaluator catches up,** as after a long stop:
  - the automatic renewals that came due while it was off, one per term;
  - the charges of the billing days since the newest automatic charge, at most 120 per item per run (§6.2). The items were paid meanwhile, so *spent* is right again (§17, question 13);
  - one notice per item whose state changed while off: its state now, not every step it missed.
- **An installation that never turns it on** has the empty tables and the eleven categories, and nothing else: no host, sensor, area, menu entry, task run or e-mail.

### 13.4 The settings

| Setting | Default | What it does |
|---|---|---|
| **`Enabled`** | **false** | **The switch (§13.1, §13.2).** Off: nothing of the feature is usable or visible, nothing is evaluated, and its data stays |
| `WarnDays` | 30 | The default for new items |
| `EolWarnDays` | 90 | The default for new items |
| `RemindDays` | 7 | The owner's reminder before the date of a warning (§5.5); 0 is none |
| `ReviewMonths` | 12 | The default rhythm for new items; 0 is none |
| `Currency` | `EUR` | The default currency for new prices, and the overview's main currency |
| `RecordCharges` | true | The default for new items (§6.2) |
| `EstimateDays` | 45 | After how many days an estimated charge without an invoice is *worth a look* |
| `MaxAssignments` | 1000 | Hosts per item |

They go into deploy/README.md's settings table, `Enabled` first.

## 14. Tests

**Server** (`PNMTD.Tests/Licenses/`):

- **The test hosts:** the licence test classes start theirs with the feature on (`new TestApp(settings: new() { ["Licenses:Enabled"] = "true" })`). Every other class runs with the default, off, so the tests that exist today see 1.30 as an installation that never turns it on.
- **`LicenseFeatureTests`**, the switch (§13), on a host with the default and on one with the feature on:
  - off, every endpoint of the three controllers answers 404 `licenses_disabled`, for an admin too, and writes no denied entry. `EndpointAuditTests` checks their rights and denied entries on a host with the feature on;
  - off, `GET /system/info` says `licenses: false`; the area isn't listed, and a group's save keeps its stored rows of it; the three kinds aren't offered, saving one is refused, and a pending notice isn't sent; `tools/list` lacks the three tools and Cookie has seventeen; the catalogs lack the type and the codes; the search finds no item; *Licence checks* is *off*; `SensorDetail.LinkedItems` is empty;
  - on, each of these the other way round;
  - `LicenseSensorSync`: off removes the item sensors and their events, keeps the assignments, clears `SensorId` and audits once; a second start writes nothing; on again makes the sensors with their first event and audits once;
  - off and on again loses no item, charge, renewal, document, key, grant or choice; the documents' files survive the cleanup while off;
  - on again catches up the automatic renewals and charges, and sends one notice per changed item, not one per missed step.
- **`LicenseVerdictTests`** (pure):
  - every code and its message, per type and kind;
  - the precedence, and the *"; also …"* findings;
  - the days: valid through `ExpiresOn`, expired at 00:00 the next day in `TZ` (a fake clock in Pacific/Auckland and in UTC), `WarnDays` 0, a leap day;
  - the end of life from the template and the item's own;
  - over-assignment at exactly the seats and one over, and none without `SeatsFromHosts`;
  - automatic renewals with and without `WarnBeforeRenewal`, and the notice deadline;
  - cancelled items;
  - the keys, and that 246 follows a renewal once.
- **`CostMathTests`** (pure):
  - per month and per year for every billing kind, in total and per seat;
  - what counts (expired by hand, automatic, retired);
  - the billing days: the anchor from the term and from `StartsOn`, steps from the anchor across month ends (31 Jan, 29 Feb), an irregular renewal;
  - the forecast: due, *if renewed*, none, a purchase to come, a variable estimate, the current month;
  - sharing by hosts and by locations up the tree, with the largest remainder adding up to the cent;
  - currencies never summed together.
- **`LicenseEvaluatorTests`:**
  - every item evaluated and its state stored, with hosts and without;
  - an event only when the key changes, and the first event at assignment;
  - a renamed item renames its sensors;
  - an automatic renewal once per term after a restart, and several terms after a long stop;
  - automatic charges once per billing day, after a restart and after a long stop (at most 120 per run);
  - `ChargesFrom` and the past recorded on request;
  - estimates for variable prices; no charges after the expiry, the cancellation or the retirement;
  - a change of a template's end of life re-evaluates its items;
  - with the feature off, nothing runs (`LicenseFeatureTests` has the rest).

  The fake clocks start on whole seconds, from one read (memory: Linux's 100-ns ticks against PostgreSQL's microseconds fail only on CI).
- **`LicenseServiceTests`:**
  - validation (§3.3), and the defaults of types, kinds, templates and categories;
  - the number sequence and the prefixes;
  - a `Purchase` charge;
  - keys stored protected (the column isn't the key), the hint, *Show* audited, `key_lost` with a lost ring;
  - the scan of the note and the support contact;
  - duplicate without keys or charges;
  - cancel (with a last day when there is no expiry), reactivate, retire (removes all sensors, needs `confirmed`), delete (needs the number, removes the sensors and the charges).
- **`LicenseAssignmentTests`:**
  - a sensor per host with its name; bulk assign;
  - over-assignment allowed and 449;
  - a retired item refused;
  - unassigning the last host leaves the item without a sensor, its state still kept;
  - `/sensors` refuses the type's create, type change, rename, host move and delete;
  - the sensor API refuses its events;
  - a host's deletion removes the assignment;
  - `MaxAssignments`.
- **`LicenseNoticeTests`**, the owner's notices (§5.5):
  - an item on no host with an owner: warned when it goes bad, reminded `RemindDays` before, once each, and told of an automatic renewal with the new price;
  - an item on hosts: its owner told too;
  - a person's own change, renewal or review isn't told to them (the actor);
  - a review due told once per due day, and not again after *Mark reviewed…*;
  - items nobody owns: only to people with *Edit* who chose the kind; off by default;
  - the renderer leaves out an item renewed or retired meanwhile, and sends nothing when none is left; an owner who lost *View* gets nothing;
  - the e-mail carries no key, note, support contact or document;
  - *nobody notified*: on no host, no owner (or one without an e-mail address), nobody chose the kind; an owner who turned the e-mails off still counts as told.
- **`SensorLinkTests`:** linking, refusing an item sensor, the summary on the sensor without price or note, removal with either side.
- **`LicenseChargeTests`:** recording by hand, credits stored negative, an automatic charge corrected but not deleted, the estimate cleared by the invoice's amount, an invoice linked, *Manage* to delete.
- **`LicenseRenewalTests`:** the presets, the price taken over, a cancelled item reactivated, corrections that don't move the expiry.
- **`LicenseDocumentTests`:**
  - upload, kinds suggested, `.exe` refused;
  - download only through the item, and only with *Details*;
  - the generic URL's 404 for a file only an item uses, and 200 for a file a page uses too;
  - removal leaves the file to the cleanup;
  - Admin's delete refused;
  - `file_uses`' constraint.
- **`LicenseTemplateTests`** and **`LicenseCategoryTests`:** defaults, the end of life followed, deletion keeping the items, renaming and merging names, a category deleted with its items moved, the last one kept.
- **`LicenseRightsTests`:** each level against each endpoint; the subjects endpoint without the area; the sensor's summaries without price or note.
- **`OverviewTests`, `CostsTests` and `UpcomingTests`:** the tiles, the groups, spent and forecast, totals per currency, overdue first, recurring renewals within 3 and 24 months.

  They filter to a category the class makes, so other classes' items and charges don't count (memory: the shared test database holds other classes' future and pending rows). They clean up after the class.
- **The counts:**
  - 26 areas in the catalog, 25 listed while off; 22 tasks, *Licence checks* among them, on or off;
  - the seed's hosts as today (no host is added); its sensors as today while off, and plus the item sensors while on (`DevSeedTests`, both);
  - Cookie's seventeen tools while off, twenty while on;
  - `ReferenceDataForTheDocs` with the feature on (the type and the seven codes);
  - `EndpointAuditTests`, and `AuditModelTests` (every key column marked).
- **The shared test database:** the item tests' hosts are disabled and cleaned up after the class, as the certificate tests' are, so the dashboard's counts elsewhere hold.

**Web** (Vitest):

- the lists: the sort and search, the URL state, the per-type columns;
- the costs shown: per month and per year, the totals per currency;
- the formats: `daysLeftText`, the hint's masking;
- the kinds' defaults;
- the upcoming months' grouping;
- the charts' scales, and their tables matching their bars;
- the switch: `visibleNavigation` without the entry when `licenses` is false (`navigation.test.ts`); `decide` sending a route with `meta.feature` to *not found* (`guard.test.ts`); the host's and location's panels and the sensor's strip not rendered.

**Browser** (`licences.spec.ts`, Playwright, on an instance with the feature on):

- make a template, a licence from it, assign two hosts, and see the sensor fail at *about to expire* (a licence expiring in 10 days);
- renew it and see 246;
- add a service on no host with an owner and an expiry within its warning days: no sensor anywhere, its state on the list and its page, its notice in the notification log;
- link a sensor, and see the support strip on it;
- record a cost and see it on *Costs* and the overview;
- upload and download a document, show a key, retire.

## 15. Compatibility: what must not break

- **An update changes nothing anyone sees:** the feature is off by default (§13). An installation updated to 1.30 gets the empty tables and the eleven categories; no menu entry, area, sensor, panel, tool, task run or e-mail appears until an Admin turns it on.
- **Existing sensors, notifications and the dashboard:** the type only adds, and only while on; failing item sensors count as problems, as any. The seed's item sensors that fail are ignored, so the dashboard's six problems in three hosts hold.
- **Hosts:** none is added, on or off: an item on no host has no sensor (§5.2).
- **People's notifications:** three kinds more on *Account → Notifications*, only while on; the knowledge base's two are as they were.
- **Files:** the generic URL serves every file it serves today; only new files used only by items are refused. `file_uses`' constraint gains a column; existing rows have one owner, as before.
- **Rights:** no group gains or loses anything.
- **The menu:** one entry in *Knowledge*, while on; the groups stay.
- **The audit:** a new category, stored as a string; no data moves.
- **`AuditHistory.vue`:** its URL stays the default; only the item passes its own.
- **The sensor page:** a sensor linked to no item looks as it does today.

## 16. Phases

1. **L1. Model, rights and the switch:**
   - `SensorType.LICENSE`, the seven codes, `LAST_LICENSE_EVALUATION`;
   - `Area.Licenses` with its four levels, `McpRights`, `api/rights.ts`;
   - the entities and the migration `Licenses` with its categories, `LicenseKeyProtector`;
   - **the switch first:** `LicenseOptions` with `Enabled` false, `LicenseSetup.Enabled`, `[RequiresLicenses]`, `SystemInfo.Licenses`, `RightsCatalog.Visible` and the group save that keeps hidden rows, so every later phase is built behind it;
   - the catalogs in `SystemService` (with the switch), `Clock.FormatDay`.
2. **L2. The verdict, the cost maths and the evaluator:**
   - `LicenseVerdict` and `CostMath`, pure, with their tests;
   - `LicenseEvaluator`: automatic renewals, automatic charges, the items' stored states, events;
   - the owner's notices: the three kinds with `Available`, their renderer;
   - `LicenseSensorSync` in `DatabaseMigrationService`;
   - `LicenseEvaluatorTests`, `LicenseNoticeTests` and `LicenseFeatureTests`.
3. **L3. Service and API:**
   - `LicenseService` and `LicenseQueries` (partial classes, as responsibilities'), with assignments, links, renewals, charges, reviews, the life, templates, categories and names, keys;
   - the `SensorService` and sensor API refusals;
   - the overview, costs, charges and upcoming queries, the CSVs, the history and the subjects filter, the search;
   - the audit catalog; `openapi.json` and `schema.d.ts`;
   - the tests of §14 for these.
4. **L4. Documents:** `file_uses.LicenseId` and the owner, the two endpoints, the generic URL's refusal, Admin's overview, the cleanup's removal; `LicenseDocumentTests`.
5. **L5. The web UI: lists and the item's page:**
   - the menu with `feature: 'licenses'`, the guard's `meta.feature`, `LicencesView` with *Licences*, *Services*, *Hardware*, *Upcoming* and *Templates*;
   - `LicenseTable`, with `listSort` shared;
   - the item's page and form;
   - the dialogs: *Renew*, *Mark reviewed*, *Assign*, *Link sensors*, *Record a cost*, *Retire*, *Delete*, template, category, rename;
   - their unit tests.
6. **L6. The web UI: *Overview* and *Costs*:** the chart components (§8.12), the overview, the costs table and the *Charges* view, with unit tests and a look in light and dark.
7. **L7. Elsewhere and the finish:**
   - the host's and the location's panels, the host's menu;
   - the sensors' strips and menus, the lookups, the global search;
   - `licenses_list`, `license_get`, `licenses_costs`;
   - the seed (`DevSeedLicenses`), only while the feature is on (`dev.env` turns it on):
     - the categories, and eight templates, among them a product past its end of life and one within 90 days;
     - about twenty-five items in every type, verdict, kind and status, on the seed's hosts and on none, with owners and without, one of them *nobody notified*;
     - a year of charges, and a variable service with estimates;
     - an internet line linked to a seed ping sensor;
     - renewals and a document;
   - `licences.spec.ts` and the screenshots;
   - a look at Cookie's case for a failing item sensor, and at a linked sensor's (§5.5);
   - `LicencesDocs.vue` in *Documentation* (shown while on), the README's features, deploy/README.md's settings, how to turn it on (§13.1), what turning it off removes (§13.3), and the backup note;
   - deploy/'s `.env` example and `dev.env` (§13.1);
   - release notes 1.30: the feature, **off by default**, the three steps to turn it on, the owner's e-mails, and that a rule for *everything* then tells about licences on hosts;
   - this plan's *As built*.
8. **L8. Windows activation (optional, §8.11):** matching the agents' `PartialKey` to the hints. It can ship later without touching L1–L7.

## 17. Open questions

Each has a recommendation; the plan is written as recommended. Question 3 was decided by the third request, and stays here with its answer so the numbers hold.

1. **The page's name and place.** **Recommended: *Licences & services*, in *Knowledge*, after *Knowledge base*,** as `/licences`. It names what people look for, and items are records people look things up in (keys, contracts, support numbers, dates). The alternatives:
   - *IT costs*, which names the purpose but hides the licences;
   - a new group *Assets* or *Costs*, which changes `navigation.test.ts` and the menu's agreed groups;
   - *Licence*, as the first request had it, which no longer covers the services.
2. **Hardware as a third type?** **Recommended: yes.** Devices' purchases, leases and warranties are a large part of IT costs, and a warranty's end fits the sensor as an expiry does. It stays a cost record, not an inventory. Alternative: licences and services only, with a device as a *service* of the category *Hardware*.
3. **Where do the sensors of items on no host live?** **Decided (revision 3): nowhere.** An item on no host has no sensor; it keeps its state itself, and its owner is told by e-mail (§5.2, §5.5, decision 6). Revision 2's own sensor on a host *Licences & services* is dropped.
4. **Should PNMT record the charges of each billing period by itself?** **Recommended: yes,** on by default, from the day an item is created (decision 13). *Spent* is then right without typing in every invoice, and the invoices correct it. Alternative: charges only by hand, which leaves *spent* empty for most items.
5. **Who sees the costs?** **Recommended: the area's *View*** (decision 21): one area, one grant. Alternative: a second area, *Costs*, without which prices, charges, the overview's money and the *Costs* tab are hidden. That fits an organisation where helpdesk staff need keys but not contract values; it costs a check in every query that returns money.
6. **Should an over-assigned licence fail its sensors (449)?** **Recommended: yes,** on all its hosts, but only for seats counted on hosts: it is a compliance problem now, and the finding names it. Alternative: a finding on the item's page only.
7. **Should a due review fail the sensors?** **Recommended: no** (decision 10). It is listed, filtered, on *Upcoming* and in *Worth a look*, and its owner is told by e-mail (§5.7).
8. **Automatic renewals: should PNMT move the expiry by itself?** **Recommended: yes,** recorded as an automatic renewal, so the sensor doesn't fail for something that renews and the forecast stays right. A cancelled contract is marked *cancelled* by the person who cancels it.
9. **The default currency:** **recommended: a setting, `Licenses__Currency`, default `EUR`;** every price and charge has its own currency, and totals are per currency.
10. **Turned off: remove the item sensors, or keep them?** **Recommended: remove them at start-up, and make them again when it is turned on** (§13.3, decision 23). The assignments stay, so nothing of the items is lost; what goes is the sensors' event history and rules or board elements made for a single item sensor. The alternatives:
    - keep them, disabled: they stay on the hosts' pages, so the feature is still visible;
    - keep them and hide them in every query that shows sensors: many places to get right, and one forgotten shows a sensor nobody evaluates.
11. **Should the owner be told about items that are on hosts too?** **Recommended: yes,** about every item they own (decision 25). The hosts' rules tell the hosts' people about the machines; the owner wants to hear about the contract, which a rule by host doesn't route to them. Each person can turn the kind off. Alternative: only items on no host, which leaves the owner of a licence on 40 hosts depending on someone's rule.
12. **Who hears about items nobody owns?** **Recommended: whoever with *Edit* chooses so, off by default** (decision 26), plus *Worth a look* and the item's page saying *nobody notified*. Alternative: everyone with *Manage*, by default: no item goes unheard, at the price of e-mails to people who didn't ask.
13. **Turned on again: should PNMT record the charges of the months it was off?** **Recommended: yes,** as after any long stop (§13.3, decision 27): the items were paid meanwhile, and *spent* is right again at today's prices, which a person corrects from the invoices. Alternative: record from the day it is turned on, which leaves a gap in *spent* for those months.

## 18. Later

- **Budgets** per category (or cost centre) and year, against spent and forecast.
- **A notification scope by sensor type** (*every licence sensor → this person*) for the host sensors.
- **The owner's notices by Pushover,** once people's notifications have a channel besides e-mail.
- **Notices to a group,** not only to the owner (*Finance* hears about every contract's cancellation deadline).
- **Turning the feature on or off without a restart.**
- **Cookie's case book** naming a failing linked sensor's provider and support contact in the case.
- **A board element *Expiring licences & services*,** as *Expiring certificates* (`BoardElementType.Certificates`), with a days filter; and a dashboard card of the running cost.
- **Responsibilities for items:** an item as a fourth kind of link (`responsibility_links.LicenseId`), so *who renews this* is a responsibility.
- **Writes over MCP:** `license_review`, `license_assign`, `license_charge_record`.
- **Invoices by mail:** a mail input that files an invoice's PDF as a charge's document.
- **Import** from a CSV file, and from vendors' portals (Microsoft 365's subscriptions through Graph, a cloud's billing API for usage).
- **Previews** of PDFs and images, under a CSP of their own.
- **An iCal feed** of the *Upcoming* dates.
- **Currencies converted** at a rate set by an Admin, for one total.
- **Price history and rises** in the forecast.
- **Software inventory from the agents,** to count installations against seats.
- **Depreciation** of hardware.

## 19. Decisions

1. **"Licence" for people, `license` in code** (§Words), as *colour* and `Color`.
2. **In code, every item is a `license`,** told apart by `Type`: one table, one service, one evaluation and one rights area for the three types, as the request has it (*a licence can also be a service*).
3. **Three types, and hardware is a cost, not an inventory** (§17, question 2).
4. **The page is *Licences & services*, in *Knowledge*** (§17, question 1).
5. **One sensor per assignment,** made and removed by PNMT, as a diagnostics assignment's. A licence on ten hosts is ten sensors: each host shows its problem, and notifications by host work.
6. **An item on no host has no sensor** (revision 3, §17 question 3). The item keeps its state itself, and its owner is told by e-mail. Revision 2's own sensor on a host *Licences & services* is dropped: a host that is no machine, kept alive by a 409, to reach the notification rules. A sensor without a host would have been a new kind of sensor everywhere.
7. **Templates are copied, except the end of life,** which is the product's and is followed. Templates, categories and renames are *Manage*: one change touches many items.
8. **Categories are a list; manufacturers, vendors and cost centres are words** with suggestions and renaming (§3.1).
9. **Linked sensors don't change the verdict,** and the support details are shown on them to whoever sees the sensor: the outage is handled there.
10. **Reviews don't fail sensors;** the owner is told instead.
11. **Automatic renewals warn only on request** (`WarnBeforeRenewal`); **contracts ask by default,** since missing their notice period costs a term.
12. **The billing period is apart from the term,** and the billing days step from the current term's first day, so a renewal realigns them.
13. **PNMT records the charges by default, from the day an item is created;** the past is recorded only on request, since its prices may have been different (§17, question 4).
14. **Spent is the charges; the forecast is today's prices.** Nothing is shown as spent that wasn't recorded, and the forecast marks what depends on a manual renewal.
15. **Expired means the day after `ExpiresOn`,** in PNMT's time zone.
16. **No currency conversion;** totals per currency, and one currency per chart.
17. **Keys are encrypted; documents are not,** as the request asks the documents to be stored as the knowledge base's. Encrypting them would need a store of its own, a key-ring dependency for every download, and a backup that is useless without the ring. The owner-checked URL, `no-store`, the audit and the backup advice answer the risks that matter (§12).
18. **The search doesn't read the note,** so a key someone saved anyway isn't found by a part of it.
19. **Lists sort and search in the browser,** since items are few, as the responsibilities' list. **Costs are computed on the server,** by `CostMath`, so the overview, the costs, the CSV and MCP give the same numbers.
20. **No group is granted the area by the migration,** as no area added after `GroupsAndRights` was (Cookie, Availability, MCP, Certificates, Responsibilities, …).
21. **Prices and costs are the area's *View*** (§17, question 5).
22. **Off by default, and off means out of sight** (§13): `Licenses__Enabled` is false unless set. While off, no part of the feature is usable or visible, the migration has still run, and none of its data is lost. Admins included: the API answers 404 before any right.
23. **Turning it off removes the item sensors and keeps the assignments;** turning it on makes them again (§13.3, §17 question 10). The sensors are the items' states on hosts and can be made again; hiding them in every sensor query couldn't be got right in one place.
24. **The owner's notices are people's notifications,** not sensors and not rules: one machinery for e-mails to a person about their own things, with their choice on *Account → Notifications*, batching, limits and the notification log (§5.5).
25. **The owner is told about every item they own, on hosts or not** (§17, question 11).
26. **Whoever with *Edit* chooses so hears about the items nobody owns,** off by default (§17, question 12).
27. **Turned on again, PNMT catches up** the renewals and charges of the time it was off, as after a long stop (§17, question 13).

## 20. As built

Built on 2026-11-27 for 1.30.0, with all of §17's recommendations taken. Where the build differs from the plan, or settles what it
left open:

- **The number** is an identity column, as KB-42's, not a sequence `license_numbers` (§3.3). The seed sets it on after its own.
- **The state is the item's** (`VerdictCode`, `VerdictKey`, `VerdictMessage`, `VerdictAt`, `AnnouncedExpiresOn` on `licenses`); an
  assignment keeps only `SensorKey`, the key its sensor last showed (§3.5). `AnnouncedExpiresOn` is also set at an item's first
  evaluation whatever its state, so a renewal after a warning shows 246; an automatic renewal at the first evaluation announces the
  expiry it had.
- **The switch's 404** is a middleware after routing (`LicenseSetup.UseLicensesSwitch`), for endpoints marked `[RequiresLicenses]`:
  authorization runs in middleware, before any MVC filter could answer (§10.1).
- **A hidden area in a group's request** is ignored, not refused: the group page never sends it, and the stored row stays (§9).
- **`UserNotificationKind` got `Available`,** as `ToolSpec` has; `SystemService.SensorTypes()` and `StatusCodes()` take the switch.
- **The owner's e-mail** names each item with its finding and the price for the decision at hand: a year of a recurring price, the
  next term for an automatic renewal, a one-time price (§5.5).
- **`POST …/renew` returns the renewal,** so the dialog can attach the paper to it.
- **The costs by location** are grouped by the top-level location, with the items in each; the table doesn't open down the tree
  (§6.5).
- **The charges' list** isn't paged: at most 10,000 rows, newest first.
- **The seed** adds no document (it writes no files) and one key; it seeds only with the switch on.
- **Phase L8 is built:** `GET /api/v1/licenses/activations?license=|host=` matches the Windows agents' `PartialKey` to the keys'
  hints, on the item's *Hosts & sensors* tab (*Hosts that use this key*, *Assign them*) and on the host's panel (§8.11).
- **The web UI:**
  - the lists' `state` and `kind` take comma lists (`state=attention,failing`); `host` filters through the panels' query, since a row
    carries host names, not ids; the *Hosts* column shows *used/seats* whenever seats are counted on hosts (§8.3);
  - the *Costs* tab's spent/forecast switch is `?costs=spent|forecast`, since `?show=charges` is the *Charges* view; its chart draws
    the current month as one column, *spent so far and forecast* (the costs answer one total per column); the overview splits it;
  - a provider's bar on the overview opens *Costs* grouped by provider with it open: there is no provider filter;
  - the *Charges* view lists every currency, with sums per currency, and has no currency filter; the seat meters have no table view,
    their figures being text already (§8.12);
  - the item's page has *Edit in the form…* besides the inline fields, and a *Name* row; the *Keys* panel shows for services and
    hardware only once they have a key (set in the form);
  - assigning several hosts with different seats sends one call per seats value; a link's note can't be changed after linking;
  - an item's next 12 months come from `GET costs` grouped by item; the form starts a new item's review rhythm at 12 months, the
    other defaults (warning days, currency) are the server's settings;
  - the Windows activation list (*Hosts that use this key*) is on the *Hosts & sensors* tab for items with a key's hint.
- **The browser test** is `e2e/licences.spec.ts`; `tools/e2e.sh` turns the feature on for its stack.
