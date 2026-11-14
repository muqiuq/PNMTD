# Plan: Audit, groups and rights, Redmine and Zabbix (packages five point five, six and seven)

| | |
|---|---|
| Status | **Built: A1–A3, G1 (with G2), R0–R2, Z0–Z3 and F, on master** (§15 *As built*). Not done: A0's checks at scale; Zabbix 7.2 and 7.4 untested |
| Date | 2026-11-14 |
| Revision | 3: **the review of revision 2 applied** (§14.1):<ul><li>**No roles any more:** the admin flag, and groups for everything else (§5.3).</li><li>**Redmine:** an overview of the most important tickets; every ticket has two buttons, its detail page (a full page) and Redmine; on the detail page, tickets are linked to knowledge base pages too (§6.3–6.6).</li><li>**2FA stays as it is;** the open questions are decided: no reads logged, retention in ENV, the rest as proposed.</li></ul>2: **groups and rights added** (§5): rights are assigned to groups, a user can be in several, and **an admin flag on the user grants every right**. The three roles go; nobody's access changes with the update. Kept simple: 16 fixed areas with one to three levels each, no deny, no rights per object.<br>1: the first plan |
| Scope | Packages 5.5, 6 and 7 of docs/ideas2.md:<ul><li>**Audit (5.5):**<ul><li>every operation a person performs is logged: who, when, from where, what, on what, with what result, and what changed;</li><li>the existing auth log is extended for it, not replaced;</li><li>**the bug:** on *Admin → Auth log* the detail text is cut off long before the column ends, so on a desktop most of the column stays empty.</li></ul></li><li>**Groups and rights (added in review, with 5.5):**<ul><li>a more detailed rights system than the three roles;</li><li>rights are assigned to groups, and users to groups;</li><li>an admin flag on the user grants every right automatically;</li><li>not too complicated.</li></ul></li><li>**Redmine (6):**<ul><li>a Redmine page with **an overview of the most important tickets** and a search;</li><li>**every ticket has two buttons:** its detail page in PNMT (a full page, not a window) and the ticket in Redmine;</li><li>Redmine's address, its API token and the project ids come from the ENV file;</li><li>on the detail page, tickets are linked to **knowledge base pages**, locations, hosts and sensors;</li><li>links to Redmine and to each ticket's page in Redmine, and information about tickets, wherever they help;</li><li>a ticket's detail view is a page of its own.</li></ul></li><li>**Zabbix (7):**<ul><li>a Zabbix page: a summary of the open problems by category, and a list of them;</li><li>Zabbix hosts are attached to PNMT hosts: **each one becomes a *Zabbix* sensor**, and a host can have several;</li><li>all Zabbix hosts can be browsed and searched, each with a detail page that links on into Zabbix.</li></ul></li></ul> |
| Non-goals | **Rights per object** (a group that sees only one location's hosts), **deny rules, exceptions for one user, rights an admin defines:** §5.1, §14.1. **Logging every read:** views are not operations; only the few reads that already count as sensitive are logged (§3.1). **Tamper-proofing the log against a database administrator**, and forwarding it to a SIEM or syslog (§14.1). **Writing to Redmine:** no tickets created, commented or changed through the API in package six (that comes with Cookie, package nine). There is a prefilled *New ticket in Redmine* link instead (§6.6). **Redmine's time tracking, wiki, files, news and forums.** **Rendering Textile** (§6.8). **Writing to Zabbix:** no acknowledging, closing or editing of problems, hosts or maintenance from PNMT. **Zabbix graphs, maps, dashboards and history:** PNMT links to them. **Importing PNMT hosts into Zabbix, or the other way round in bulk** beyond what §7.8 matches. **Retiring the e-mail input for Zabbix's mails:** both keep working (§13). **The MCP server (eight) and Cookie (nine):** this plan only defines what they will get (§8.7) |
| References | **docs/webui-migration-plan.md:** the auth log (§4.3), the roles that §5 replaces (§4.10), the endpoint audit (§3.6), the UI conventions (§5). **docs/locations-knowledge-plan.md:** the links of pages to locations, hosts and sensors (§6.5), the audit so far (§8.7), `MarkdownView` (§5.6), `MachineAccess` (§7.4). **docs/ai-operations-plan.md:** system sensors (§4), `Untrusted` and Jev (§3). **docs/mikrotik-plan.md:** a polled sensor type with a side table (§5, §9). **The APIs:** Redmine's REST API (5.0 and later); Zabbix's JSON-RPC API (6.0 LTS and later) |

---

## 1. Summary

**The auth log becomes the audit log.** Same table, same page, more columns:

- **Every write endpoint makes one entry**, for everyone, whether it succeeds, fails or is refused. Of today's 151 write endpoints, about 70 log nothing yet (hosts, sensors, notification rules, DNS zones, mail rules, locations, pages, …).
- **An entry says what changed**, field by field, before and after. Secrets show as *changed*, never as values.
- **A test makes it impossible to add a write endpoint that isn't logged**, as `EndpointAuditTests` already does for access rules.
- **The page gets wider, not cut off:** the detail fills its column, and a row opens to show everything.
- **Host and sensor pages get a *History* tab** from the same log.

**Groups with rights replace the three roles:**

- **A group gives a level in each of 16 areas:** Monitoring *View* or *Edit*, Commands *Run*, Mail *Read*, the knowledge base *View*, *Edit* or *Manage*, and so on. A user's rights are the highest level any of their groups gives.
- **The admin flag on a user grants every right.** Only admins manage users and groups.
- **Nothing more:** no deny, no exceptions per user, no rights per object, and the areas are fixed in code.
- **The update changes nobody's access:** *User* becomes the group *Operators*, *Viewer* the group *Viewers*, *Admin* the flag.

**Redmine comes into PNMT read-only:**

- **A *Redmine* page** opens on **an overview of the most important tickets** (urgent, unassigned, overdue, linked to a problem in PNMT, waiting for 14 days), with a search beside it.
- **Every ticket has two buttons:** ***Details***, PNMT's ticket page (a full page), and ***Redmine***.
- **On the ticket page, a ticket is linked to knowledge base pages, locations, hosts and sensors**, and shows up on their pages and next to their problems.
- **PNMT keeps a small copy of linked tickets** (number, subject, status), so pages stay fast and still work when Redmine doesn't answer.

**Zabbix comes into PNMT read-only, plus a sensor:**

- **A *Zabbix* page:** the open problems by severity and by category (host group or tag), the list of problems, and every Zabbix host, searchable, each with a page.
- **A Zabbix host attached to a PNMT host becomes a *Zabbix* sensor.** It fails while the Zabbix host has problems at or above a chosen severity, so PNMT's dashboard, notifications and AI see it like any other sensor.
- **PNMT suggests which Zabbix host belongs to which PNMT host**, by name and address.

| Topic | Decision |
|---|---|
| The log | **`auth_log` is extended and renamed `audit_log`;** the page becomes *Audit log*, and `/admin/auth-log` redirects. Existing entries stay (§3.2) |
| What is an operation | **Every call to a write endpoint** (POST, PUT, DELETE), by anyone, **including failed and refused ones.** CLI commands and agent actions too. **Reads are not**, except those already logged as sensitive (join scripts, backups and tunnels downloaded) and the new export (§3.1) |
| What an entry says | **Who** (user, or CLI, agent, system), **when, from where** (address, browser), **what** (an event type of a catalog), **on what** (target type, id and name), **the outcome** (succeeded, failed, refused, with the error), **the changes** (field: before → after), and the request (§3.2) |
| How entries are made | **Three layers:** every write action declares its event; a filter writes one entry per request, failed ones included; an EF Core interceptor fills in the changes of tracked entities. Services add the target and a summary (§3.3) |
| Secrets | **Never in the log.** A property is marked `[AuditSecret]` and shows as *changed*. A test refuses audited entities with secret-looking properties that aren't marked (§3.4) |
| Nothing unlogged | **A test fails for every write endpoint without `[Audit]` or `[NotAudited("why")]`**, and for every entity without `[Audited]` or `[NotAudited]` (§3.7) |
| Retention | **400 days, at most 500,000 entries**, both in ENV. Trimmed once a day, not on every save (§3.8) |
| The bug | **The Detail cell's `max-width: 24rem` cuts the text**, whatever the column's width. The table gets a fixed layout; Detail takes the remaining width, its full text is in the row's details. The Mail log gets the same fix (§4.1) |
| Redmine's config | **`Redmine__Url`, `Redmine__ApiKey`, `Redmine__Projects`** (ids or identifiers) in `.env`, and a few optional ones. The key never leaves the server (§6.1) |
| Redmine's access | **The API key of a dedicated Redmine account with read access to the projects.** PNMT also only shows tickets of the configured projects, whatever the key could see (§6.2) |
| The Redmine page | **An *Overview* of the most important tickets:** urgent (above Redmine's default priority), new and unassigned, overdue, linked to a problem in PNMT, no update for 14 days, recently updated. **A *Search*** by text and number, with filters. **Two buttons on every ticket:** *Details* and *Redmine* (§6.3) |
| The ticket page | **A full page, not a window:** description, history (comments and changes), details, attachments as links, related tickets, and where it is linked in PNMT. ***Open in Redmine*** and ***Link to…*** **at the top** (§6.4) |
| Links | **`redmine_links`, shaped like `kb_links`:** a ticket to a knowledge base page, a location, a host or a sensor, with a note. A sensor's page shows its own, its host's and its locations' tickets (§6.5) |
| Where tickets show | **Panels on host, sensor, location and knowledge base pages**, a chip on a failing sensor and on the dashboard's problems when an open ticket is linked, the host's header, the global search (`#1234`) (§6.6) |
| Ticket text | **CommonMark through PNMT's `MarkdownView`**, sanitised; **Textile is shown as plain text** (§6.8) |
| Zabbix's config | **`Zabbix__Url`, `Zabbix__ApiToken`** in `.env`, optionally host groups to limit PNMT to (§7.1) |
| Zabbix versions | **6.0 LTS and later.** The client asks `apiinfo.version` first and adapts: the token as a Bearer header from 6.4, in the request before that (§7.2) |
| What PNMT keeps | **A mirror of Zabbix's hosts** (every 10 minutes), for search and matching. **The open problems in memory** (every 60 seconds), for the page and the sensors. Nothing else is stored (§7.3) |
| Categories | **Severity** (Zabbix's six), and **host group or a tag** (`class`, `component`, `scope`, …) as a matrix against severity (§7.4) |
| The Zabbix sensor | **Type `ZABBIX` (110), one per Zabbix host, a host can have many.** Fails (490) while the Zabbix host has a problem at or above its severity (default *Average*); 491 when the Zabbix host is gone or disabled. **If Zabbix itself doesn't answer, the sensors keep their state** and the *Zabbix* system sensor fails instead (§7.7) |
| Attaching | **From the PNMT host** (*Add sensor → Zabbix*), **from the Zabbix host's page**, or **in bulk from suggestions** matched by name and address. A Zabbix host is attached at most once (§7.8) |
| Rights | **Groups hold rights; users are in groups; the admin flag grants every right** and alone manages users and groups (§5.1) |
| Areas and levels | **16 fixed areas, each with one to three levels**, each level including those below: Monitoring, Commands, Mail, Decisions, Locations, Knowledge base, Redmine, Zabbix, UDP nodes, MikroTik & WireGuard, Servers, SSH keys, Diagnostics, AI, System, Audit log (§5.2) |
| The update | **Roles become groups:** *Operators* (was User), *Viewers* (was Viewer), the admin flag (was Admin). Everyone keeps what they had; `users.Role` goes (§5.3) |
| Checking | **`[RequireRight(area, level)]` on every endpoint**, read per request from the session cache, so a group change applies at once. 403 names the missing right (§5.4) |
| The new features' rights | **Audit log *View*** (admins). History tabs: Monitoring *View*. **Redmine *View* and *Link*** (Operators). **Zabbix *View*** (Operators, Viewers) and ***Details*** for inventory (Operators). Attaching: Monitoring *Edit*. Tests of the connections: System *Manage* (§8.1) |
| Health | **System sensors *Redmine* (429) and *Zabbix* (430)**, rows in task health, and *Test* on the debug page (§8.5) |

---

## 2. Requirements and starting point

### 2.1 Requirements

From docs/ideas2.md, packages 5.5, 6 and 7:

| # | Requirement |
|---|---|
| A1 | **Every operation a user performs is logged** |
| A2 | **That is an audit log, extending the existing auth log** |
| A3 | **On *Admin → Auth log*, the detail isn't cut off early;** on a desktop the column is used |
| G1 | **A more detailed rights system, with groups** (added in review) |
| G2 | **Rights are assigned to groups** |
| G3 | **Users have an admin flag that grants every right automatically** |
| G4 | **Not too complicated** |
| R1 | **A Redmine page where tickets are searched**, opened, and opened in Redmine from there |
| R2 | **The project ids, the API token and Redmine's address are in the ENV file** |
| R3 | **Tickets are linked to a location, a host or a sensor** |
| R4 | **Links and ticket information in the right places**, to Redmine itself and to each ticket's page there |
| R5 | **A ticket's detail view is a full page** |
| Z1 | **A Zabbix page with a summary of the open problems**, by category, and a list of them |
| Z2 | **Zabbix hosts are assigned to PNMT hosts;** several Zabbix hosts can be added to one PNMT host, **as Zabbix sensors** |
| Z3 | **All Zabbix hosts can be browsed and searched**, each has a detail page, and from there one gets to Zabbix |

What follows from them:

| # | Requirement |
|---|---|
| A4 | **"Exactly what happened, and what changed":** old and new values, without ever storing a secret (the bar package eight sets for its own log) |
| A5 | **Failed and refused operations are logged too:** an attempt says as much as a success |
| A6 | **Nothing new slips in unlogged:** adding an endpoint without its audit fails a test |
| A7 | **The log keeps long enough** for the higher volume, configurable |
| A8 | **Ready for packages eight to ten:** an entry can name an MCP token, Cookie, or a session on a server as the actor, without another change to the table |
| R6 | **Redmine's API key never leaves the server**, and PNMT only shows the configured projects |
| R7 | **Pages with ticket panels stay fast**, and work when Redmine doesn't answer |
| R8 | **A Redmine overview showing the most important tickets** (from the review) |
| R9 | **Two buttons per ticket:** one to its detail page, a full page and not a window, one to Redmine itself (from the review) |
| R10 | **On the detail page, a ticket is linked to knowledge base pages, locations, hosts and sensors** (from the review) |
| Z4 | **A Zabbix sensor behaves like PNMT's other sensors:** events, state, notifications, the dashboard, the AI's analysis |
| Z5 | **An outage of Zabbix doesn't turn every Zabbix sensor red** |
| Z6 | **Every Zabbix version still supported by Zabbix works**, from 6.0 LTS on |
| G5 | **The update changes nobody's access:** today's roles become groups and the flag |
| G6 | **No one raises their own rights:** only admins manage users and groups |
| G7 | **A change of rights applies at once**, also to sessions already open |
| G8 | **Every endpoint names the right it needs**, checked by a test as roles are now |

### 2.2 What the code base already provides

| Need | Existing | Where |
|---|---|---|
| The log | **`auth_log`** (`AuthLogEntity`): `Id, CreatedAt, UserId?, Username, EventType, IpAddress, UserAgent, Detail`. **About 80 event types** in `AuthEvents`, all security-relevant: sign-ins, accounts, UDP keys, MikroTik, WireGuard, agents, SSH keys, deletes of locations, pages and files | `PNMTD/Auth/UserAccounts.cs`, `Models/Db/UserEntity.cs` |
| Writing entries | **Four writers that build the entity by hand:** `UserAccounts.Log` (with `AuditContext`), `UdpViews.Audit` (from the HTTP context), `AgentLog.Audit` (agents), and direct `db.AuthLog.Add` in `FileStore`, `SshKeyRemovals` and `Cli/UdpCommand` | |
| Retention | **10,000 entries**, a constant, trimmed by a `DELETE … OFFSET` only when an account change is saved, so other entries pile up between | `UserAccounts.SaveAsync` |
| The page | **`AuthLogView.vue`:** server-paged, search over user and detail, a filter by user and by type. **Its type list is typed into the view and has 23 of the ~80 types**, so most can't be filtered | `views/admin/AuthLogView.vue` |
| The endpoints | **`EndpointAuditTests`' role matrix lists every v1 endpoint:** 135 GET, 89 POST, 34 PUT, 28 DELETE. A new endpoint fails it until it is added | `PNMTD.Tests/Hosting/EndpointAuditTests.cs` |
| Histories per object | **Locations** (`location_history`, with old and new values) and **pages** (`kb_revisions`) have their own. Hosts and sensors have none | `Models/Db/LocationEntities.cs`, `KnowledgeBaseEntities.cs` |
| Bulk writes | **68 `ExecuteUpdate`/`ExecuteDelete` calls**, deletes of hosts, sensors and rules among them. **EF Core's change tracker never sees them** | `Services/Domain/*` |
| Links to objects | **`kb_links`:** a page to exactly one location, host or sensor (check constraint), `ON DELETE CASCADE`, unique per target. `KbPanel.vue` on hosts, sensors and locations, with the *via* groups (sensor → host → location → ancestors). `LinkPicker.vue` | `Knowledge/`, `views/kb/KbPanel.vue` |
| A polled sensor type | **MikroTik:** `SensorType.MIKROTIK`, a side table keyed by `SensorId` (cascade), a poller, events through `SensorEventWriter`, its own tabs on the sensor page | `Mikrotik/`, `views/sensors/mikrotik/` |
| Sensor state | **A code ≤ 399 is OK, above is failing.** State comes from the last event. `SensorEventWriter` dedupes repeats and caps at 2,000 per sensor | `Services/Events/SensorEventWriter.cs`, `MonitoringState.cs` |
| Health | **System sensors** on host *PNMT* (220 and 420–428 in use), **task health** (`GET /system/tasks`), the **debug page**'s tests | `Services/Events/SystemSensors.cs`, `SystemService.cs` |
| Outbound HTTP | **Named `HttpClient`s** (`ai`, `notifications`, `uplink`, …), faked in tests by replacing the primary handler (`FakeOpenRouter`). `System.Net` logs at *Warning*, so URLs aren't logged | `Program.cs`, `PNMTD.Tests/Ai/FakeOpenRouter.cs` |
| Config | **`Section__Key` in `.env`**, typed options (`IOptionsMonitor`), a commented block per feature in `deploy/.env.example`, a section in deploy/README.md, a line in `info.sh` | `deploy/` |
| Roles | **Three roles, one per user** (`users.Role`: `Admin`, `User`, `Viewer`), **three policies** (`CanRead` on 27 controllers or actions, `CanWrite` on 70, `Admin` on 52). `CanSeeSecrets` (User and Admin) for tokens and URLs; `IsInRole(Admin)` in the MikroTik services, the decisions and the uplink; `TotpPolicy.RequiredForAdmins`; a decision's `RequiredRole` | `Auth/AuthSetup.cs`, `Api/ApiSetup.cs`, `Services/Domain/` |
| Sessions | **`SessionCache` looks the user up on every request** (cached one minute) and signs a session out when its stamp or role no longer match. The role is a claim in the cookie | `Auth/AuthService.cs` |
| Roles in the UI | **`session.can('User')`** with a rank (Viewer < User < Admin); `meta.role` on routes, `role` on drawer entries; **194 `canWrite` and 63 `isAdmin` checks** in the views | `stores/session.ts`, `router/guard.ts`, `layouts/navigation.ts` |
| Markdown | **`MarkdownView`**, sanitised: no raw HTML, external images as links | `components/markdown/` |
| Untrusted text | **`Untrusted`/`InputSanitizer`, Jev, the scrubber**, for anything that will reach an AI later | `Ai/` |

### 2.3 Alternatives

| Alternative | Why not |
|---|---|
| **A new `audit_log` table next to `auth_log`** | Two logs to search, two pages, two retentions; the package says to extend the auth log. Sign-ins are operations too |
| **Log requests in a middleware only** (method, path, status) | Says *that* something happened, not *what changed*, and nothing about the target's name once it's deleted (A4) |
| **Only an EF Core interceptor** | Misses the 68 bulk writes, failed operations, and anything that changes nothing in the database (a command sent, a test mail) |
| **PostgreSQL triggers** | No user, no address, no meaning; secrets would land in the log unless every trigger knew them |
| **Embedding Redmine or Zabbix in an iframe** | Their own login, cookies and CSP; no search across them from PNMT; no links to PNMT's objects. R5 asks for a page of PNMT's |
| **Redmine's or Zabbix's own PNMT plugin** | Two more code bases in Ruby and PHP to maintain, installed on systems PNMT doesn't own |
| **Copying all tickets or all problems into PNMT** | Large, stale, and a second copy of personal data. Only what PNMT links or shows is kept (§6.7, §7.3) |
| **More roles** (an *Operator*, an *Auditor*, …) instead of groups | Every new combination would be another role in code; G2 asks for rights assigned to groups |
| **Free permissions per endpoint** (as Zabbix's or Redmine's role editors) | Some 290 checkboxes: complicated (G4), and easy to grant an action without what it needs to work |
| **Rights per object** (per location, host or page) | Every list, search, count and dashboard would need filtering, and the AI's and MCP's readers too. Not now; it can come later as a group's optional location scope (§14.1) |
| **Zabbix problems as PNMT events directly**, without a sensor | No state, no place on a host, no notification rules. Z2 asks for a sensor |

---

# Part A: Audit (package five point five)

## 3. The audit log

### 3.1 What counts as an operation

**An operation is anything a person asks PNMT to do:**

- **Every call to a write endpoint** (POST, PUT, DELETE under `/api/v1`), by anyone. Today that is 151 endpoints.
- **Sign-in steps**, as now: logins, 2FA, logouts, lockouts.
- **CLI commands** that change something: `pnmtd users …`, `pnmtd agent …`, `pnmtd udp …`, `pnmtd mikrotik …`.
- **What agents do on a person's behalf**: joins, key removals done (as now).

**Not operations:**

- **Reading.** Opening a host, a page, a ticket or a mail is not logged. The exceptions stay as they are: a MikroTik join script viewed, a backup or tunnel downloaded. The audit log's export is added (§4.2).
- **POSTs that only read:** previews, parsing, lookups, the knowledge base's view counter and its *who is editing* heartbeat. Nine endpoints, each marked `[NotAudited("…")]` with its reason (Appendix A).
- **PNMT's own monitoring:** events, polls, checks, AI work. They have their own logs. Where the system changes configuration itself (a DNS sensor created by the DNS task, a system sensor created at start), that is logged with the actor *System* (§3.6).

### 3.2 The entry

**`auth_log` is renamed `audit_log`, and keeps its rows.** The page and the code follow: `AuditLogEntity`, `AuditEvents`, *Audit log*. The rename is one mechanical commit; `Import/SqliteImporter.cs`'s SQL changes with it.

| Column | Type | Notes |
|---|---|---|
| `Id`, `CreatedAt`, `UserId?`, `Username` | as now | `UserId` keeps no foreign key: entries outlive users |
| `EventType` | text | From the catalog (§3.9). The existing names stay |
| `IpAddress`, `UserAgent` | as now | |
| `Detail` | text, ≤ 2,000 | **A sentence a person reads:** "web01: interval 60 → 30 s, importance Normal → High". Was ≤ 1,000 |
| **`Category`** | text | `Auth`, `Users`, `Hosts`, `Sensors`, `Notifications`, `Locations`, `Knowledge`, `Files`, `Udp`, `Mikrotik`, `WireGuard`, `Agents`, `SshKeys`, `Diagnostics`, `Dns`, `Mail`, `Ai`, `Decisions`, `System`, `Redmine`, `Zabbix`. From the catalog |
| **`Actor`** | text | **`User`, `Cli`, `Agent`, `System`.** Packages eight to ten add `Mcp`, `Cookie`, `Seraph` without a migration (A8). Until now this was a prefix in `Detail` ("cli: …", "agent: …") |
| **`ActorRef`** | text? | Who exactly, when not a user: the agent's sensor, later the MCP token's id |
| **`Outcome`** | text | **`Succeeded`, `Failed`, `Denied`** (§3.5) |
| **`Error`** | text? | The status and PNMT's error code: `409 last_admin`, `422 validation`, `500` |
| **`TargetType`, `TargetId`, `TargetName`** | text? | **What the operation was on:** `Sensor`, its id, "web01 / Ping" **as it was named then**. For a bulk operation, the first target and "and 11 more" |
| **`Subjects`** | uuid[] | **The target and what it belongs to:** a sensor's id, its host's, its location's. The *History* tabs read it (§4.3). GIN index |
| **`Changes`** | jsonb? | **The changes, field by field** (§3.4) |
| **`Request`** | text? | The endpoint's template: `PUT /api/v1/sensors/{id:guid}` |
| **`CorrelationId`** | text? | `HttpContext.TraceIdentifier`. Joins the entries of one request (a bulk operation, or an operation and an agent's later result) |

**The existing rows are migrated:** `Category` and `Outcome` from their event type (the `…Failed` and `LockedOut` ones are *Failed*), `Actor` from the "cli: " or "agent: " prefix of `Detail`, which is then removed.

**New indexes:** `(EventType, CreatedAt)`, `(TargetId)`, GIN on `Subjects`. The existing `(CreatedAt)` and `(UserId, CreatedAt)` stay. A0 measures the search (`ILIKE` over user, detail and target name) on 500,000 rows and adds a `pg_trgm` index if it is slow.

### 3.3 How entries are made

**Three layers, so that no layer has to be perfect:**

**1. Every write action declares its event.**

```csharp
[HttpPut("{id:guid}")]
[Authorize(Policy = Policies.CanWrite)]
[Audit(AuditEvents.SensorUpdated)]
public async Task<ActionResult<SensorDetail>> Update(Guid id, SensorRequest request, CancellationToken ct) => …
```

An action that only reads carries `[NotAudited("preview: changes nothing")]` instead.

**2. `AuditFilter` writes one entry per request.** It is a global MVC filter for `/api/v1`:

- **Before the action** it opens the request's `AuditScope` (scoped): the event, the actor, the address, the browser, the endpoint, the route's `{id}` as a first guess at the target.
- **The service fills in the rest** through `IAuditLog`, which replaces the four writers of §2.2:

  ```csharp
  audit.Target(AuditTarget.Sensor(sensor));          // type, id, name, subjects (host, location)
  audit.Summary($"{name}: interval {old} → {new} s"); // optional: otherwise the changes make the detail
  audit.Record(AuditEvents.SshKeyRemoved, …);         // a further entry, for side effects and bulk targets
  ```

- **After the action** it sets the outcome. **A failure (an `ApiException`, a 4xx, an unexpected 500) is written through a new `DbContext` from the factory**, since the request's own context may hold what failed.

**3. `AuditChangesInterceptor` fills in the changes.** A `SaveChangesInterceptor` on `PnmtdDbContext`:

- inside an audit scope, it reads the tracked entities that are added, modified or deleted, **of the entity types marked `[Audited]`**, and adds their changes to the scope (§3.4);
- **the scope's entry is added to the same `SaveChanges`**, the first one of the request, and updated by any later one. **So a change is never committed without its entry**, and a change rolled back takes its entry with it.

**What the interceptor can't see:** `ExecuteUpdate` and `ExecuteDelete` bypass the change tracker. For those 68 calls, the service names the target before it deletes (most already load it for their 404), and the filter writes the entry after the action. **A2 goes through every one of them**, and the test of §3.7 checks a sample of deletes (host, sensor, rule, zone, mail rule, key value) end to end.

### 3.4 Changes: what is recorded, and what never is

**`Changes` is a list:**

```json
[
  { "field": "Interval", "old": "60", "new": "30" },
  { "field": "Importance", "old": "Normal", "new": "High" },
  { "field": "SecretToken", "kind": "secret" },
  { "field": "Notes", "kind": "text", "summary": "+3 −1 lines" },
  { "field": "Links", "kind": "set", "added": ["Host web01"], "removed": [] }
]
```

**Every property of an `[Audited]` entity is recorded, except:**

| Attribute | Meaning | Examples |
|---|---|---|
| `[AuditSecret]` | **Only *changed* is recorded, never a value** | Sensor tokens, UDP keys, MikroTik and run-as credentials, password hashes, TOTP secrets, WireGuard private keys, backup keys, the API keys of package six and seven (which live in ENV anyway) |
| `[AuditIgnore]` | **Noise**: not recorded | `UpdatedAt`, `LastSeenAt`, counters, `SearchVector`, `PlainText`, cached names |
| `[AuditText]` | **Long text**: the line counts, not the text | Markdown of pages and fields (the revision and the location history have the full diff), notes, scripts. The entry links the page's revision |

**Values are cut at 500 characters, and an entry at 50 changes** ("and 12 more"). Ids of other objects are shown by name where the scope knows them (a host's location: "Building A → Building B").

**`[Audited]` goes on configuration**, not on operational data. Every entity type of `PnmtdDbContext` carries one of the two: `[Audited]` (hosts, sensors and their side tables, rules, zones, mail rules, users, key values, locations, fields, values, pages, tags, links, files, tunnels, scripts, diagnostics, SSH keys, run-as accounts, settings rows) or `[NotAudited("operational")]` (events, mail log, AI requests, agent log, uplink checks, UDP packets, page views, the audit log itself).

### 3.5 Failed and refused operations

| Case | Entry |
|---|---|
| **The service refuses** (409 conflict, 422 validation, 404) | `Outcome = Failed`, `Error = "409 last_admin"`, the target if known, no changes |
| **An unexpected error** (500) | `Outcome = Failed`, `Error = "500"`. The exception goes to the server's log, not the audit log |
| **A signed-in user lacks the right** (403) | `Outcome = Denied`, through an `IAuthorizationMiddlewareResultHandler`: authorization runs before MVC, so the filter never sees it. Only for write endpoints |
| **No session** (401) | **Not logged:** anyone on the network could fill the log. Failed logins stay logged as now |
| **The request is malformed** (400 before the action runs) | `Outcome = Failed`, `Error = "400"`, no target |

**A burst of refused operations from one user** (a script in a loop) is capped: at most 60 `Failed`/`Denied` entries per user and minute, then one "and 240 more" entry per minute.

### 3.6 Outside a request: CLI, agents, the system

- **CLI:** `IAuditLog` with `Actor = Cli`, the OS user if known as `ActorRef`. Already done for users, agent tokens and UDP counters; A2 adds the others.
- **Agents:** `Actor = Agent`, `ActorRef` the sensor, the agent's address. Joins and key removals as now.
- **The system:** `Actor = System` for the few changes PNMT makes to configuration on its own: sensors created (DNS, system sensors), uploads and deleted files cleaned up (one entry per run, with the count). **Not** for events, polls or decisions expiring.
- **Work started by a person and done later** (a MikroTik command, an OTA update, a script run): the start is the person's entry; the result is in the feature's own log, which shows the same `CorrelationId`.

### 3.7 Nothing slips in unlogged

**`EndpointAuditTests` gets two more tests:**

1. **Every write endpoint of the access matrix has `[Audit]` or `[NotAudited]`.** A new endpoint fails until it has one (A6).
2. **Every write endpoint makes an entry:** each endpoint of the matrix is called once by a user without its right (403) and checked for a `Denied` entry with its event and the missing right. That needs no test data per endpoint, and proves for each that its event is found and written. **The few that every signed-in user may call** (logout, one's own password, 2FA and sessions) are covered by the account's tests, which check their entries already.

**And `AuditModelTests`:**

- every entity type is `[Audited]` or `[NotAudited]`;
- **on `[Audited]` entities, every property whose name contains *token, secret, password, key, hash, protected* or *private* is `[AuditSecret]`, or `[AuditPlain("why")]`** (a `PublicKey`, a `KeyName`);
- the catalog has a category and a label for every event used in code.

**Plus focused tests** for the paths of §3.3: a host update with its changes, a sensor delete through `ExecuteDelete` with its name, a rolled-back save without an entry, a 409 with its error, a secret shown as *changed*.

### 3.8 Retention

**The volume grows:** a team of five doing a few hundred changes a day, plus sign-ins, makes around 1,000 entries a day, so 10,000 lasts about ten days.

| Setting | Default | |
|---|---|---|
| `Audit__KeepDays` | **400** | A year and a bit, for a yearly review |
| `Audit__MaxEntries` | **500000** | A ceiling for busy installations. About 0.5–1 GB at most |

**`AuditRetentionTask` trims once a day** (oldest first, by age, then by count), listed in task health. The `DELETE … OFFSET` on every account save goes. **No one can delete entries through PNMT**, admins included.

### 3.9 The event catalog

**`AuditEvents` becomes a catalog:** each event has its name, category, label ("Sensor updated"), tone (success, warn, danger, neutral) and target type. **`GET /api/v1/audit-log/types` serves it**, so the page's filter can't fall behind again (§4.2).

**About 70 events are added** for what logs nothing today. Appendix A lists every write endpoint with its event. The groups:

| Category | New events |
|---|---|
| Hosts | `HostCreated`, `HostUpdated`, `HostDeleted`, `HostsImportanceChanged`, `HostsLocationChanged` |
| Sensors | `SensorCreated`, `SensorUpdated`, `SensorDeleted`, `SensorTokensRotated`, `SensorsImportanceChanged` |
| Notifications | `NotificationRuleCreated`, `…Updated`, `…Deleted`, `…SensorLinked`, `…SensorUnlinked`, `…ScopesChanged`, `…ExclusionAdded`, `…ExclusionRemoved`, `NotificationsAssigned` |
| DNS, mail | `DnsZoneCreated`, `…Updated`, `…Deleted`, `…UpdateForced`, `DnsRecordUpdated`; `MailInputRuleCreated`, `…Updated`, `…Deleted` |
| Groups (§5.6) | `GroupCreated`, `GroupUpdated`, `GroupDeleted`, `GroupRightsChanged`, `GroupMembersChanged`, `UserGroupsChanged`, `UserAdminChanged` |
| Users, system | `TotpSetupStarted`; `KeyValueCreated`, `…Updated`, `…Deleted`; `UplinkCheckRun`; `AuditLogExported` |
| UDP, MikroTik | `UdpCommandQueued`, `UdpCommandCancelled`, `UdpFirmwareChanged`; `MikrotikPollRequested`, `MikrotikBackupRequested`, `MikrotikBackupSettingsChanged` |
| Agents | `AgentCollectRequested`, `AgentLogRequested`, `AgentScriptRunRedacted`, `WindowsRunAsChanged`, `DiagnosticsRun` |
| Decisions | `DecisionDecided` (with the choice; what the decision applied comes in its changes) |
| Locations | `LocationCreated`, `LocationUpdated`, `LocationMoved`, `LocationValueChanged`, `LocationsCreatedFromText`; `LocationFieldCreated`, `…Changed`, `…Archived`, `…Restored`, `LocationFieldsReordered` |
| Knowledge, files | `KbPageCreated`, `KbPageSaved`, `KbPageArchived`, `KbPageRestored`, `KbRevisionRestored`, `KbAiTagsRequested`, `KbTagRenamed`; `FileUploaded` |
| Redmine (§6) | `RedmineLinkAdded`, `RedmineLinkRemoved`, `RedmineTested` |
| Zabbix (§7) | `ZabbixHostsAttached` (bulk; a single attach is `SensorCreated`), `ZabbixTested` |

---

## 4. The audit log page

### 4.1 The cut-off detail (the bug)

**The cause:** in `AuthLogView.vue`, the Detail column has no width, so it takes what the others leave, but its text sits in `<span class="ellipsis cell">`, and `.cell` has **`max-width: 24rem`**. On a 1,920 px screen the column is about 70 rem wide; the text stops at 24. The Browser column (`max-width: 14rem`) has the same rule. **The Mail log does the same** (`.cell.wide { max-width: 36rem }`).

**The fix:**

- **The table gets `table-layout: fixed`**, with widths for every column but Detail. Detail takes the rest; its span is `display: block` with the ellipsis and no `max-width`, so it is cut at the column's edge, wherever that is.
- **The full detail is in the row's details** (§4.2), wrapped, and in the cell's `title`.
- **The Browser column goes into the row's details.** A user agent is rarely what one scans for, and its 14 rem go to Detail.
- **On a phone** the detail gets two lines (`line-clamp: 2`) instead of one.
- **The Mail log gets the same layout.** A0 looks for the pattern in the other list views (the UDP tabs, the agent logins) and fixes those where a column has room to spare.

This is **the first commit of A1**, on its own, so it ships even if the rest waits.

### 4.2 The page

**`/admin/audit-log`, *Audit log* in the drawer.** `/admin/auth-log` redirects with its query, so bookmarks and the link on the users page keep working.

**Filters**, all in the URL:

- **search:** user, detail, target name;
- **category** (several), and **event**, grouped by category, from `GET /audit-log/types`;
- **outcome:** succeeded, failed, denied;
- **actor:** user, CLI, agent, system;
- **user** (a chip, as now), and **subject** (a chip, when coming from a host's or sensor's *History*: "web01, and its sensors");
- **time:** from and to.

**Columns on a desktop:** Time (9 rem), User (10 rem), Event (12 rem, a tag in the catalog's tone), Target (14 rem, a link to the object while it exists, its name struck through once it's gone), **Detail (the rest)**, Address (9 rem). A *Failed* or *Denied* row shows the outcome as a red tag after the event.

**A row opens** (the `#expansion` slot of `ListTable`) to show:

- the full detail, wrapped;
- **the changes as a table:** field, before, after; *changed* for a secret; the line counts and *Show revision* for a text;
- the outcome and the error, the request (`PUT /api/v1/sensors/{id}`), the browser;
- *Other entries of this request (3)*, by `CorrelationId`.

**Export:** *Download CSV* exports what the filters show, at most 100,000 rows, streamed. **The export is itself an entry** (`AuditLogExported`, with the filters).

### 4.3 History on host and sensor pages

**Hosts and sensors get a *History* tab** (Monitoring *View*), as locations already have:

- **the audit entries whose `Subjects` contain the object:** for a host, its own and its sensors'; for a sensor, its own;
- **who, when, what, the changes:** without address or browser, which stay on the audit log page;
- **with Audit log *View*, *All in the audit log ›*** opens the audit log with the subject filter.

**Locations keep their `location_history`**, and pages their revisions: they hold the full old and new values, which the audit log only summarises. Their audit entries are on the audit log page.

**The users page's link** becomes *Audit log ›*, filtered by the user.

---

# Part G: Groups and rights (added to package five point five)

## 5. Groups and rights

**Today PNMT has three roles**, `Viewer`, `User` and `Admin`, one per user, stored in `users.Role` and checked through three policies (`CanRead`, `CanWrite`, `Admin`). They are coarse: whoever may edit a host may also read every mail and send commands to every router, and whoever may set up a WireGuard tunnel may also manage users. **This part replaces them with groups that hold rights, plus an admin flag**, while keeping the model small enough to fit on one screen.

### 5.1 The model

- **The admin flag** (`users.IsAdmin`): **an admin has every right, always**, whatever their groups. The flag replaces the `Admin` role.
- **A group** has a name, a description, members, and **a level for each area** (§5.2). A user can be in several groups.
- **A user's level in an area is the highest level any of their groups gives.** Rights only add up: there is no *deny*, no exception for one user, no right on a single object.
- **Each level includes the ones below it.** *Edit* includes *View*, so there is never "may edit, but may not see".
- **The areas and levels are fixed in code** (§5.2). Admins compose groups from them; they don't define new rights.
- **Only admins manage users, groups and the flag itself.** No group can grant that, so no member of a group can raise their own rights.
- **At least one active admin always exists** (the existing `last_admin` check, now on the flag).
- **A user who is neither admin nor in any group** can sign in and manage their own account (password, 2FA, sessions, SSH keys, tunnels), and sees *You don't have access to anything yet. Ask an admin.*

### 5.2 Areas and levels

**16 areas.** Most have one or two levels; a level in *italics* holds what only admins could do until now.

| Area | Levels | What each level allows | Until now |
|---|---|---|---|
| **Monitoring** | View · Edit | **View:** the dashboard, hosts, sensors, events, notification rules and the manager, DNS zones, mail input rules (without mail contents), the uplink, the global search, the hosts' and sensors' *History* (§4.3). **Edit:** create, change and delete all of these, see sensor tokens and URLs, importance, notification assignments, UDP keys and counters, a router's WireGuard keys, an agent's settings, attaching Zabbix hosts (with Zabbix *View*) | Viewer: View · User: Edit |
| **Commands** | Run | **Run:** UDP commands, a MikroTik router's poll, backup now and commands, an agent's *Collect now*, a diagnostics run | User |
| **Mail** | Read | **Read:** the mail log and mail contents | User |
| **Decisions** | Decide | **Decide:** the decisions inbox. Decisions marked for admins stay with admins | User |
| **Locations** | View · Edit · *Manage* | **View:** locations, their fields' values and history. **Edit:** create, rename, move, fill in fields, set hosts' locations. **Manage:** *Admin → Location fields*, deleting locations | Viewer · User · Admin |
| **Knowledge base** | View · Edit · *Manage* | **View:** pages, history, files. **Edit:** write, archive, restore, AI tags, the sensitive mark, uploads. **Manage:** deleting pages and files, redacting, tags, *Admin → Knowledge base* | Viewer · User · Admin |
| **Redmine** | View · Link | **View:** the Redmine page, tickets, the panels and chips (§6). **Link:** linking and unlinking tickets | new |
| **Zabbix** | View · Details | **View:** the overview, problems, hosts, host pages (§7). **Details:** inventory and acknowledgement messages, which name people | new |
| **UDP nodes** | *Manage* | *Admin → UDP nodes:* firmware, updates, unknown senders | Admin |
| **MikroTik & WireGuard** | *Manage* | *Admin → MikroTik & WireGuard:* update jobs, PNMT's SSH keys, backup settings and keys, accepting host keys, deleting backups, tunnels, redacting commands | Admin |
| **Servers** | *Manage* | *Admin → Servers:* join tokens, revoking, scripts and running them, run-as accounts, WMI, the agents' files and logs | Admin |
| **SSH keys** | *Manage* | *Admin → SSH keys:* the key manager and removals | Admin |
| **Diagnostics** | *Manage* | *Admin → Diagnostics:* sets and assignments | Admin |
| **AI** | *Manage* | *Admin → AI:* the work queue, backfills, analysis runs, the request log | Admin |
| **System** | *Manage* | Key values, *System health* and task health, the debug page and its tests (Redmine's and Zabbix's too), *Check uplink now* | Admin |
| **Audit log** | View | *Admin → Audit log* and its export | Admin |
| *Users and groups* | (none) | **Only the admin flag** | Admin |

**Two rules where an endpoint serves two areas:**

- **Uploading a file** needs *Edit* in the knowledge base **or** in locations; saving the page or field that uses it checks the rest.
- **Attaching a Zabbix host** creates a sensor, so it needs Monitoring *Edit* and Zabbix *View*.

**What every signed-in user can do, without any right:** their own account, their own SSH keys and tunnels, the documentation, the system info.

**2FA stays as it is:** `Auth__TotpPolicy=RequiredForAdmins` means users with the admin flag; groups don't change it.

### 5.3 No roles any more

**After this package, PNMT has no roles at all:** not in the database, the API, the CLI, the UI or the tests. **A user is an admin or not; everything else comes from their groups.**

**The update converts the existing users once**, in its migration, so that nobody loses or gains access on the day of the update:

| Before | After |
|---|---|
| `Admin` | **The admin flag**, no groups |
| `User` | **A member of the group *Operators***: Monitoring *Edit*, Commands *Run*, Mail *Read*, Decisions *Decide*, Locations *Edit*, Knowledge base *Edit*, Redmine *Link*, Zabbix *Details* |
| `Viewer` | **A member of the group *Viewers***: Monitoring *View*, Locations *View*, Knowledge base *View*, Zabbix *View* |

- ***Operators* and *Viewers* are ordinary groups, created once by the migration.** Nothing in the code knows their names: they can be renamed, changed or deleted like any group, and a fresh installation starts with them too, as a starting point.
- **The upgrade test checks the conversion endpoint by endpoint** (§12): a 1.3 user gets the same answers afterwards, apart from the new Redmine and Zabbix areas.
- **`users.Role` is dropped** in the same migration, and with it `Roles`, `Policies`, `CanSeeSecrets`, the role claim, and `decisions.RequiredRole` (which becomes `AdminOnly`, §8.2).
- **New users** are created as admins or with groups chosen in the form. Without either, they can only manage their own account (§5.1).

### 5.4 How rights are checked

**On the server:**

- **`[RequireRight(Area.Monitoring, Level.Edit)]` replaces `[Authorize(Policy = …)]`.** As now: a default on the controller (usually *View*), and the stricter one on write actions. A policy provider turns each area and level into a policy (`right:Monitoring:Edit`); the admin flag satisfies all of them.
- **Rights are read on every request, not stored in the cookie.** `SessionCache` already looks the user up on every request (cached for a minute). Its entry gains the admin flag and the levels, computed from the user's groups. **Changing a group drops its members' entries**, so a change applies from the next request, also in sessions that are already open. A cookie can never carry old rights.
- **The security stamp** is still rotated when the admin flag changes or a user is deactivated (other sessions end), not on group changes, which need no sign-out.
- **Checks inside services** go through `ICurrentRights`: the `IsInRole(Roles.Admin)` calls in the MikroTik services and the uplink overview become their area's level, `CanSeeSecrets` becomes Monitoring *Edit*, and a decision marked `AdminOnly` needs the admin flag.
- **A refusal says which right is missing:** 403 with `code: "missing_right"` and `{ area, level }`, so the UI can say *You need Monitoring: Edit*. The audit's *Denied* entry names it too (§3.5).
- **The CLI:** `pnmtd users create <name> [--admin] [--group <name>]…`, `pnmtd users admin <name> on|off`, `pnmtd users groups <name> <group>…`, `pnmtd groups list`. **`--role` is gone.** The initial admin (`Auth__InitialAdmin__*`) gets the flag. `Import/UsersJsonImporter.cs` (users.json from before 1.0) converts as in §5.3.

**In the web UI:**

- **`/auth/me` returns `isAdmin` and the levels instead of `role`**, and the session store's `can('User')` becomes `can('Monitoring', 'Edit')`. Routes and drawer entries name a right instead of a role (`meta.right`).
- **The checks in the views** (194 `canWrite` and 63 `isAdmin` today) each get their view's area, view by view. **The server stays the authority:** a check the UI misses shows a button that answers *You need …*, never an action that succeeds.

### 5.5 The pages

**Admin → Groups** (`/admin/groups`, admins only):

- **The list:** name, description, members, and a short summary ("Monitoring Edit · Commands · Mail · +5").
- **A group's page** (`/admin/groups/:id`):
  - name and description;
  - **its rights as one table, a row per area and a segmented button per level** (*None · View · Edit · Manage*), with a line saying what the chosen level allows (from the API's catalog, the text of §5.2);
  - **its members**, with *Add…* and *Remove*;
  - *Delete group*: its members lose what it gave them, which the dialog lists.
- *Copy group* starts a new group from an existing one.

**Admin → Users:**

- **The role select becomes an *Admin* switch and a *Groups* multi-select.**
- **A user's page shows their effective rights:** the same table, read-only, with the group each level comes from. For an admin: *Every right (admin)*.
- The list gets a filter by group, and shows *Admin* or the groups in place of the role.

**Account → Security** gets ***Your access***: the user's groups and effective rights, read-only.

### 5.6 What the audit records

| Event | When |
|---|---|
| `GroupCreated`, `GroupUpdated`, `GroupDeleted` | Name, description; deletion with the members it had |
| **`GroupRightsChanged`** | Each area's level, before and after: "Mail: none → Read" |
| `GroupMembersChanged` | Who was added or removed |
| `UserGroupsChanged` | A user's groups, from the user's page |
| **`UserAdminChanged`** | The admin flag. Replaces `RoleChanged`; the old entries keep their type |

All in the category `Users`.

### 5.7 What packages eight to ten get

- **MCP tokens act with their user's rights** (package eight). **The read-only token is capped at the *View* and *Read* levels**: no *Edit*, *Run*, *Link* or *Manage*, whatever the user may do.
- **Cookie gets the rights of a group an admin chooses** (package nine), and never the admin flag.
- **Access to servers' consoles** (package ten) becomes an area of its own (*Server console*), not part of *Servers*.

---

# Part R: Redmine (package six)

## 6. Redmine

### 6.1 Configuration

In `.env`, as R2 asks:

```bash
# --- Redmine (optional: docs/audit-redmine-zabbix-plan.md §6, deploy/README.md "Redmine") ------------
#Redmine__Url=https://redmine.example.com
#Redmine__ApiKey=
#Redmine__Projects=it-support network 17
#Redmine__IncludeSubprojects=true
#Redmine__PublicUrl=
#Redmine__TextFormatting=common_mark
#Redmine__ShowPrivateNotes=false
#Redmine__SyncMinutes=10
#Redmine__StaleDays=14
#Redmine__CertificateSha256=
```

| Setting | Meaning |
|---|---|
| `Url` | **Where PNMT reaches Redmine.** Required |
| `ApiKey` | **The API key of the account PNMT uses** (§6.2). Required. Never shown, logged or sent to the browser |
| `Projects` | **The projects PNMT shows:** numeric ids or identifiers, separated by spaces or commas. Required. PNMT resolves them at start and on *Test*, and names those it can't find |
| `IncludeSubprojects` | Their subprojects too. Default `true`, as in Redmine |
| `PublicUrl` | **Where browsers reach Redmine**, if not at `Url` (PNMT may use an internal address). Every link to Redmine is built from it |
| `TextFormatting` | **Redmine's text formatting** (`common_mark`, `markdown`, `textile`, or `plain`): Redmine's REST API doesn't say which is set (§6.8). Default `common_mark`, Redmine's default since 5.0 |
| `ShowPrivateNotes` | Private notes, if the account can see them. Default `false` |
| `SyncMinutes` | How often linked tickets are refreshed (§6.7). Default 10 |
| `StaleDays` | When an open ticket counts as waiting, for the overview's *No update* list (§6.3). Default 14 |
| `CertificateSha256` | **For a Redmine with its own certificate:** the SHA-256 of the certificate PNMT accepts, instead of turning validation off |

**Redmine is on when `Url`, `ApiKey` and `Projects` are set.** Otherwise the drawer has no *Redmine*, and the panels aren't shown.

### 6.2 The client

**`RedmineClient`**, a named `HttpClient` (`redmine`), 15 s timeout, responses capped at 5 MiB:

- **The key goes in the `X-Redmine-API-Key` header**, never in a URL, so no proxy or log sees it.
- **Calls used:**

  | Call | For |
  |---|---|
  | `GET /users/current.json` | *Test*: the key works, and whose it is |
  | `GET /projects/{id}.json` | Resolving the configured projects, their names and identifiers |
  | `GET /issue_statuses.json`, `/trackers.json`, `/enumerations/issue_priorities.json`, `/projects/{id}/memberships.json` | **Lookups** for filters and for the names in a ticket's history. Cached an hour |
  | `GET /issues.json?…` | **The search** (§6.3), and **the refresh of linked tickets** by `issue_id=1,2,3` with `status_id=*` (§6.7) |
  | `GET /issues/{id}.json?include=journals,attachments,relations,children` | **The ticket page** (§6.4) |

- **Only the configured projects:** every ticket the client returns is checked against them (and their subprojects). **A ticket of another project is a 404 in PNMT**, even if the key could read it.
- **Least privilege, in deploy/README.md:** a Redmine account of its own (*pnmt*), a role with *View issues* only, member of the configured projects. PNMT never writes, so the role needs no more.
- **Errors:** 401 or 403 from Redmine is *the key is wrong or lacks rights*; a timeout or 5xx is *Redmine doesn't answer*. Both reach the *Redmine* system sensor (§8.5); the page shows them in an `ErrorState`.

**R0 checks against Redmine 5.1 and 6.0** (containers): the filter syntax for several projects at once (`f[]=project_id&op[project_id]==&v[project_id][]=…`), `issue_id` with a list, the text search (§6.3), what `include=` returns, private notes and private tickets, and the prefilled new-ticket URL (§6.6). Their real responses become the tests' fixtures.

### 6.3 The Redmine page: overview and search

**`/redmine`, *Redmine* in a new drawer group, *Integrations*, with two tabs: *Overview*, where it opens, and *Search*.**

**Every ticket PNMT lists has two buttons** (R9):

- ***Details*** opens PNMT's ticket page (§6.4), **a full page**, not a window;
- ***Redmine*** opens the ticket in Redmine, in a new tab.

They are on every row of the overview, the search, the panels of §6.6 and the ticket page's related tickets. The global search's results and the chips open the details.

**The overview shows the most important tickets** (R8) of the configured projects, with a project switch at the top when there are several:

- **Tiles:** open, urgent, new and unassigned, overdue, without an update for 14 days. Each opens *Search* with that filter.
- **Lists of at most 10 tickets each**, with *All ›* to the search:

  | List | Which tickets | Order |
  |---|---|---|
  | **Urgent** | Open, with a priority above Redmine's default one. Redmine's list of priorities says which is the default, so nothing is configured (in a standard Redmine: *High*, *Urgent*, *Immediate*) | Highest priority, then oldest |
  | **New, no one assigned** | Open, without an assignee | Oldest first |
  | **Overdue** | Open, due date passed | Most overdue first |
  | **Linked to a problem in PNMT** | Open, linked to a failing sensor, or to a host or location with one | Most important sensor first, then newest |
  | **No update for 14 days** | Open, not updated for `StaleDays` (default 14) | Longest waiting first |
  | **Recently updated** | Any status | Newest change first |

- **A row:** number, priority, status, subject, assignee, updated, and the two buttons. A ticket can be in several lists.
- **The overview's calls to Redmine** (one per list) **are cached for a minute**, shared by everyone who opens it, with "as of 10:42". The PNMT list comes from PNMT's copy (§6.7) and the sensors' state, with no call to Redmine.
- **The header:** *Open Redmine* (the project, or Redmine's issue list for several), in a new tab.

**Search:**

- **By text:** subject, description and notes. R0 decides between Redmine's `any_searchable` filter (from 5.0) and its search API; the first keeps one call and the filters together.
- **A number** (`1234` or `#1234`) **opens the ticket's details.**
- **Filters:** project (the configured ones), status (*Open* by default, *Closed*, *All*), tracker, priority, assignee (from the projects' members, or *none*), overdue, *Linked in PNMT* (only tickets linked to something).
- **Order:** last updated first; also newest, priority, number.
- **Columns:** number, tracker, status, priority, subject, assignee, updated, **a PNMT mark with the count of its links**, and the two buttons.
- **Paging:** 25 per page, through Redmine's `offset` and `limit`. The total is Redmine's `total_count`.
- **On a phone:** two-line rows (number, status; subject), as `ListTable` does, with the two buttons on the right.

### 6.4 The ticket page

**`/redmine/issues/1234`, a page of its own** (R5): never a dialog or a side panel.

**The top:** `#1234` and the subject, tags for tracker, status and priority, and the actions:

- ***Open in Redmine*** (primary, a new tab: `{PublicUrl}/issues/1234`);
- ***Link to…*** **a knowledge base page, a location, a host or a sensor** (R10), with the knowledge base's `LinkPicker`, which gains pages (found by number, `KB-42`, or title);
- *Copy link* (PNMT's).

**Two columns on a desktop, one on a phone:**

| Left | Right |
|---|---|
| **The description**, rendered (§6.8) | **Details:** project, author, assignee, created, updated, start and due date, done %, estimated time, custom fields that have a value |
| **The history**, oldest first, as Redmine shows it: **comments** rendered, **changes** as lines ("Status: New → In progress", "Assignee: – → Anna"), with who and when. Private notes only with `ShowPrivateNotes` | **In PNMT:** the pages, locations, hosts and sensors it is linked to, each with its note and *Unlink* (Redmine *Link*) |
| | **Related tickets:** parent, children, relations ("blocks #1201"), each a link to its PNMT page, closed ones struck through |
| | **Attachments:** name, size, author, **as links to Redmine** (PNMT doesn't fetch them: they would need the key in the browser, or a proxy) |

**A ticket that isn't in the configured projects, or that Redmine doesn't know,** shows *Not in PNMT's Redmine projects* or *Not found in Redmine*, and still lists its links in PNMT, so they can be removed.

### 6.5 Links to pages, locations, hosts and sensors

**`redmine_links`, shaped like `kb_links`:**

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | |
| `IssueId` | int | Redmine's number. **No foreign key:** the ticket lives in Redmine |
| `PageId?`, `LocationId?`, `HostId?`, `SensorId?` | uuid | **Exactly one** (check constraint), `ON DELETE CASCADE`. `PageId` is a knowledge base page |
| `Note` | text? ≤ 500 | Why it's linked: "the UPS replacement" |
| `CreatedAt`, `CreatedById`, `CreatedByName` | | |

- **Unique per target and ticket.** Linking twice is a 409.
- **Only tickets of the configured projects** can be linked (checked with Redmine at the time).
- **A ticket can be linked to several objects**, and an object to many tickets.
- **Deleting a page, host, sensor or location deletes its links** (cascade, through `ExecuteDeleteAsync` too). The audit has their names (§3.3). An archived page keeps its links.
- **A page's ticket links aren't part of its revisions:** they are made from the ticket's page or the page's panel, and recorded in the audit log, not in the page's history.
- **A sensitive page** shows its tickets to people as any page does; to machines (packages eight and nine), `MachineAccess` hides the link with the page (§8.7).
- **Linking and unlinking:** Redmine *Link*, `RedmineLinkAdded` / `RedmineLinkRemoved` with the ticket's number and subject.

### 6.6 Where tickets show up

R4 asks for links and ticket information in the right places:

| Place | What |
|---|---|
| **Host page** | **A *Tickets* panel** beside the knowledge base's: the host's own tickets, then *via* its location and that location's parents, as `KbPanel` groups them. **Open tickets first; closed ones folded** ("3 closed"). Each: number, subject, status, assignee, updated, **the two buttons** (§6.3), *Unlink*. Header actions: *Link ticket…* (search by number or text) and ***New in Redmine*** (below). **The header** gets a chip "2 open tickets" that scrolls to the panel |
| **Sensor page** | **The same panel**, with the sensor's own, its host's and its locations'. **While the sensor fails and an open ticket is linked**, the status strip says so: "Ticket #1234 open, In progress, Anna" |
| **Location page** | **The panel on *Overview*** (own tickets only, as pages are), and **a *Tickets* tab** with all, including those of its hosts and sub-locations |
| **Knowledge base page** | **A *Tickets* panel at the side**, next to the page's links: its own tickets, with *Link ticket…* (Redmine *Link*) |
| **Dashboard** | **The problems panel shows a ticket chip** on a failing sensor with a linked open ticket: someone is on it |
| **Zabbix's problems** (§7.5) | The same chip, when the Zabbix host is attached to a PNMT host with an open ticket |
| **Global search** | `#1234` finds the ticket (one call to Redmine); text finds linked tickets by subject (from PNMT's copy, §6.7) |
| **The Redmine page and the ticket page** | *Open Redmine*, the two buttons on every ticket, *Open in Redmine*, as above |

***New in Redmine*** opens Redmine's form for a new ticket in a new tab, **prefilled** with the subject ("web01: ") and a description holding PNMT's link to the host or sensor (`/projects/{identifier}/issues/new?issue[subject]=…&issue[description]=…`). With several projects, a menu picks one. **PNMT doesn't learn the new ticket's number:** the person links it afterwards, or the panel's *Link ticket…* finds it at the top, sorted by newest. Creating tickets through the API is package nine's.

### 6.7 PNMT's copy of linked tickets

**Panels show tickets on many pages; asking Redmine each time would be slow, and fail when it is down** (R7). So:

- **`redmine_issues` holds a copy of every linked ticket:** number, project, tracker, status, closed or not, priority, subject, assignee, updated, fetched at, and *missing* (Redmine no longer returns it).
- **`RedmineSync` refreshes them every `SyncMinutes`**, a hundred per call by `issue_id`. A ticket page refreshes its own when opened, and a link adds its ticket at once.
- **Panels, chips and the search read only the copy**, and say "as of 10:42" when the last sync is older than twice the interval.
- **No description, comments or attachments are copied:** the ticket page always asks Redmine. Personal data stays where it is.
- **A copy without links is deleted** by the next sync.

### 6.8 Rendering ticket text

| `TextFormatting` | Rendering |
|---|---|
| `common_mark`, `markdown` | **PNMT's `MarkdownView`**: sanitised, no raw HTML, external images as links. **Redmine's own syntax** is turned into links before: `#1234` to PNMT's ticket page, `attachment:"x.png"` and `![](x.png)` to the attachment in Redmine, `@user` stays text |
| `textile` | **Plain text** with its line breaks, and URLs and `#1234` as links. A Textile renderer isn't added (non-goal) |
| `plain` | Plain text, as above |

**Ticket text is untrusted**, as mail is. Nothing of it is executed or sent anywhere in this package; packages eight and nine pass it through `Untrusted` and Jev (§8.7).

---

# Part Z: Zabbix (package seven)

## 7. Zabbix

### 7.1 Configuration

```bash
# --- Zabbix (optional: docs/audit-redmine-zabbix-plan.md §7, deploy/README.md "Zabbix") --------------
#Zabbix__Url=https://zabbix.example.com
#Zabbix__ApiToken=
#Zabbix__HostGroups=
#Zabbix__PublicUrl=
#Zabbix__MinSeverity=Average
#Zabbix__PollSeconds=60
#Zabbix__HostSyncMinutes=10
#Zabbix__CertificateSha256=
```

| Setting | Meaning |
|---|---|
| `Url` | **Zabbix's frontend.** The API is `{Url}/api_jsonrpc.php`. Required |
| `ApiToken` | **An API token** (Zabbix 5.4 and later) of the account PNMT uses (§7.2). Required. Never shown, logged or sent to the browser |
| `HostGroups` | **Optional: only hosts of these groups** (names), separated by commas. Empty: all the token can see |
| `PublicUrl` | Where browsers reach Zabbix, if not at `Url` |
| `MinSeverity` | **The default for new Zabbix sensors** (§7.7): `Information`, `Warning`, `Average`, `High` or `Disaster`. Default `Average` |
| `PollSeconds` | How often the open problems are fetched. Default 60, at least 30 |
| `HostSyncMinutes` | How often the hosts are mirrored. Default 10 |
| `CertificateSha256` | As for Redmine |

**Zabbix is on when `Url` and `ApiToken` are set.**

### 7.2 The client and Zabbix's versions

**`ZabbixClient`**, a named `HttpClient` (`zabbix`), JSON-RPC 2.0, 15 s timeout, responses capped at 20 MiB:

- **First `apiinfo.version`** (it needs no token), at start and after every error. The version decides:
  - **the token:** as `Authorization: Bearer …` from 6.4; in the request's `auth` field before (6.0, 6.2). Never in a URL;
  - **names that changed:** `selectHostGroups` (6.2 and later) or `selectGroups`; host availability per interface; the frontend's URLs (§7.9).
- **Calls used**, all reads:

  | Call | For |
  |---|---|
  | `apiinfo.version` | The version |
  | `hostgroup.get` | Resolving `HostGroups`, the filters |
  | `host.get` | **The mirror** (§7.3): ids, technical and visible names, status, maintenance, groups, interfaces with their addresses and availability, tags. **The host page:** also templates, inventory, description |
  | `problem.get` | **The open problems:** severity, name, operational data, since when, acknowledged, suppressed, tags, acknowledgements |
  | `trigger.get` | **Which host each problem is on** (`problem.get` doesn't say): by the problems' trigger ids, with their hosts. Cached per trigger |
  | `item.get` | **The host page's latest data:** name, key, last value and time, units |

- **Least privilege, in deploy/README.md:** a Zabbix user of its own (*pnmt*), a user role with API access restricted to these six methods (*Allow list*), and a user group with **read** permission on the host groups PNMT should see. **Zabbix enforces what the token can see; `HostGroups` narrows it further.**
- **Errors:** an invalid token, missing permissions or a timeout go to the *Zabbix* system sensor (§8.5).

**Supported: 6.0 LTS and later.** Z0 tests 6.0 LTS, 7.0 LTS and the newest release, in containers, and records their responses as fixtures: the token in both places, the renamed parameters, availability fields, the frontend URLs, and the role's allow list.

### 7.3 What PNMT keeps

| What | Where | Why |
|---|---|---|
| **Hosts** | **`zabbix_hosts`, a mirror**, refreshed every `HostSyncMinutes`: id, technical name, visible name, status, maintenance, groups, addresses (interface IPs and DNS names), worst availability, synced at, gone | **Searching and browsing thousands of hosts fast**, the global search, matching to PNMT hosts (§7.8), and names when Zabbix doesn't answer |
| **Open problems** | **In memory, `ZabbixSnapshot`**, refreshed every `PollSeconds` by `ZabbixPoller` | **The summary, the problems list and the Zabbix sensors** all read the same snapshot: one `problem.get` a minute, whatever the page views |
| **Attached hosts** | **`zabbix_sensors`**, the Zabbix sensor's side table (§7.7) | |
| **Everything else** (items, inventory, templates, acknowledgement messages) | **Not kept:** the host page asks Zabbix | |

A host that disappears from Zabbix (or from the token's view) is marked *gone* in the mirror, and removed a week later unless attached.

### 7.4 The Zabbix page: overview

**`/zabbix`, *Zabbix* in the drawer's *Integrations*, with tabs *Overview*, *Problems*, *Hosts*.**

**Overview** (Z1's "summary of open issues by categories"):

- **A tile per severity**, Disaster to Not classified, in Zabbix's order and in PNMT's tones: the count of open problems, and how many are unacknowledged. A tile opens *Problems* filtered by it.
- **By category, a matrix:** rows are the categories, columns the severities, cells the counts.
  - **The category is chosen above the matrix:** *Host group* (default), or **a tag name** among those the open problems carry. Zabbix's templates tag problems with `class` (os, network, hardware, …), `component` (cpu, storage, …) and `scope` (availability, performance, …), so these appear when used.
  - A cell opens *Problems* filtered by the category and severity.
- **Hosts at a glance:** monitored, unavailable, in maintenance; **"attached to PNMT: 34 of 120"**, which opens the suggestions (§7.8).
- **Suppressed problems** (in maintenance) **are left out**, with a switch to count them.
- **"As of 10:42:13"** from the snapshot; red if older than three polls.
- **The header:** *Open Zabbix* (its dashboard), in a new tab.

### 7.5 Problems

**The list of open problems**, from the snapshot:

- **Columns:** severity (a tag), since (time and duration), **host** (Zabbix's name, a link to its PNMT page in §7.6; **and the PNMT host's name and link, if attached**), problem (name, and operational data), tags (chips), acknowledged (icon and count), **an icon that opens the problem in Zabbix** (its event page).
- **Filters:** minimum severity, host group, tag (`component: storage`), host (text), acknowledged (all, yes, no), suppressed (hidden by default), older than.
- **Order:** severity, then newest. 50 per page.
- **A row opens** to show the acknowledgements (who, when, message: Zabbix *Details* only) and the trigger's description. These come from Zabbix when the row is opened.
- **A ticket chip** when the attached PNMT host has an open linked ticket (§6.6).

### 7.6 Hosts and the host page

**The *Hosts* tab** lists the mirror (Z3), searchable as one types:

- **Search:** visible name, technical name, addresses.
- **Filters:** host group, status (monitored, disabled, in maintenance), availability (available, unavailable, unknown), attached to PNMT (yes, no).
- **Columns:** name (and the technical name beneath, when different), groups, addresses, availability (an icon per interface type: agent, SNMP, IPMI, JMX), **problems** (the count, in the colour of the worst severity, from the snapshot), **PNMT** (the attached host, a link; or *Attach…* with Monitoring *Edit*).

**The host page, `/zabbix/hosts/10084`, a page of its own:**

- **The top:** the visible name, the technical name, status and maintenance as tags, and **the links into Zabbix** (§7.9): ***Open in Zabbix*** (its dashboards, or *Monitoring → Hosts* filtered to it), *Problems*, *Latest data*, *Configuration*.
- **Overview:** interfaces with addresses, ports and availability (with Zabbix's error text when unavailable), groups, templates, tags, description.
- **Problems:** the host's open ones, as in §7.5.
- **Latest data:** its items' last values with units and time, searchable, 200 at most, sorted by name. Each links to its graph in Zabbix.
- **Inventory** (Zabbix *Details*): only the fields that have a value. It often names people and places.
- **PNMT:** the attached PNMT host and its Zabbix sensor, with their state; or, with Monitoring *Edit*, ***Attach to PNMT host…*** with the suggested host preselected (§7.8).

The page asks Zabbix when opened (`host.get`, `problem.get`, `item.get` for this host). If Zabbix doesn't answer, the mirror's data is shown with a notice.

### 7.7 The Zabbix sensor

**A new sensor type, `ZABBIX = 110`**, so a Zabbix host is part of a PNMT host like a ping or an agent (Z2, Z4):

- **One sensor per Zabbix host; a PNMT host can have several** (a server's OS and its iLO, a cluster's nodes).
- **A Zabbix host is attached at most once** (a unique index), so a problem always has one place in PNMT.
- **The side table `zabbix_sensors`**, keyed by `SensorId` (cascade, as MikroTik's):

  | Column | Default | |
  |---|---|---|
  | `ZabbixHostId` | | Unique |
  | `MinSeverity` | `Zabbix__MinSeverity` | **The sensor fails from this severity up** |
  | `CountAcknowledged` | yes | An acknowledged problem is still a problem |
  | `CountSuppressed` | no | In maintenance: no alarm |
  | `UnavailableFails` | no | **An unavailable interface fails the sensor** even without a problem (Zabbix's templates usually raise one anyway) |
  | `LastProblemIds` | | What the last event was about |

- **The state, after every poll:**

  | Code | When | Message |
  |---|---|---|
  | **290** `ZABBIX_OK` | No counted problem | "No problems at Average or above" (and "1 Warning", when there are lesser ones) |
  | **490** `ZABBIX_PROBLEM` | **A counted problem is open** | "2 problems: High: /var 95 % full; Average: High CPU load", cut at 500 characters |
  | **491** `ZABBIX_HOST_MISSING` | The Zabbix host is gone, disabled, or no longer visible to the token | "Not monitored in Zabbix any more: deleted, disabled or out of the token's groups" |
  | **492** `ZABBIX_HOST_UNAVAILABLE` | With `UnavailableFails`: an interface is unavailable | "Agent interface 10.0.4.17:10050 unavailable: …" |

- **An event is written when the code or the set of counted problems changes**, through `SensorEventWriter`. A new problem while already failing is a new 490 event. Notification rules apply as for any sensor.
- **If Zabbix doesn't answer, no events are written** (Z5): the sensors keep their state, their page says "Zabbix not reached since 10:42", and the *Zabbix* system sensor fails (430) and notifies once. The same as monitoring pausing while the uplink is down.
- **Not a heartbeat sensor:** `HeartbeatCheckTask` picks its types explicitly, so it ignores `ZABBIX`. No interval, no grace period (`UsesInterval: false`).
- **Its name** is the Zabbix host's visible name, and can be changed.
- **`GET /system/sensor-types` and `/system/status-codes`** get the type and the four codes, so the documentation shows them.

**The sensor's page** gets tabs, as the agents' have:

- ***Overview*:** the Zabbix host's problems (from the snapshot), its availability, *Open the Zabbix host ›* (PNMT's page) and *Open in Zabbix* (the links of §7.9), the ticket and knowledge base panels;
- ***Events*:** PNMT's events of the sensor;
- ***Setup*:** the settings above, and *Detach* (deletes the sensor; its events go with it).

### 7.8 Attaching Zabbix hosts

**Three ways in:**

1. **From the PNMT host:** *Add sensor → Zabbix*. The form's Zabbix host picker searches the mirror, **suggests matches first**, and greys out hosts already attached.
2. **From the Zabbix host's page:** *Attach to PNMT host…*, with the suggested PNMT host preselected, or **a new PNMT host** named as in Zabbix.
3. **In bulk, from the suggestions** (*Hosts → Suggestions*, and the overview's "34 of 120"): unattached Zabbix hosts, each with its suggested PNMT host and why. Checkboxes, *Attach selected*. One audit entry (`ZabbixHostsAttached`) lists them all.

**A suggestion matches**, in this order:

- **the name:** the Zabbix host's technical or visible name equals a PNMT host's name, ignoring case, or their first labels match (`web01.example.com` and `web01`);
- **an address:** an interface's IP or DNS name equals one PNMT knows for a host: a ping sensor's target, an agent's reported hostname or addresses, a MikroTik router's address.

**Only unambiguous matches are preselected.** Two candidates show both, and none is picked.

### 7.9 Links into Zabbix

**`ZabbixLinks` builds every URL in one place**, from `PublicUrl` and the version, because Zabbix's frontend URLs differ between versions. Z0 checks each one per version. For 7.0, for instance:

| Link | URL |
|---|---|
| Zabbix's dashboard | `zabbix.php?action=dashboard.view` |
| A host in *Monitoring → Hosts* | `zabbix.php?action=host.view&filter_set=1&filter_host={name}` |
| A host's dashboards | `zabbix.php?action=host.dashboard.view&hostid={id}` |
| A host's problems | `zabbix.php?action=problem.view&filter_set=1&hostids[]={id}` |
| A host's latest data | `zabbix.php?action=latest.view&filter_set=1&hostids[]={id}` |
| A host's configuration | `zabbix.php?action=host.edit&hostid={id}` |
| A problem (event) | `tr_events.php?triggerid={trigger}&eventid={event}` |
| An item's graph | `history.php?action=showgraph&itemids[]={id}` |

All open in a new tab. Ids come from Zabbix's responses and are checked to be numbers.

---

## 8. Integration into PNMTD

### 8.1 Rights

The rights of §5.2, for what this plan adds:

| What | Right | In the groups of §5.3 |
|---|---|---|
| **Audit log**, its export | Audit log *View* | admins only |
| **History tabs** on hosts and sensors | Monitoring *View* | Operators, Viewers |
| **Redmine:** the overview, search, tickets, panels, chips | Redmine *View* | Operators |
| Linking and unlinking tickets (to pages, locations, hosts, sensors) | Redmine *Link*, and *View* of what it is linked to | Operators |
| **Zabbix:** overview, problems, hosts, host pages | Zabbix *View* | Operators, Viewers |
| Zabbix inventory, acknowledgement messages | Zabbix *Details* | Operators |
| **Zabbix sensors:** reading them | Monitoring *View* | Operators, Viewers |
| Attaching Zabbix hosts, the sensor's settings | Monitoring *Edit* and Zabbix *View* | Operators |
| *Test* of Redmine and Zabbix, their status on *System health* | System *Manage* | admins only |
| **Groups, users, the admin flag** | the admin flag | admins only |

**Why *Viewers* get no Redmine:** tickets hold customers' and colleagues' text, as mails do, and *Viewers* don't read mails either. An admin can give a group Redmine *View* without anything else. Zabbix's monitoring data is what PNMT's own is, so *Viewers* see it; its inventory and acknowledgements name people, hence *Details*.

### 8.2 Data model

| Table | Change |
|---|---|
| `auth_log` → **`audit_log`** | Renamed; columns `Category`, `Actor`, `ActorRef`, `Outcome`, `Error`, `TargetType`, `TargetId`, `TargetName`, `Subjects` (uuid[]), `Changes` (jsonb), `Request`, `CorrelationId`; `Detail` to 2,000; indexes of §3.2; existing rows migrated |
| `users` | **`IsAdmin`** (bool) added; **`Role` dropped** after the conversion (§5.3) |
| **`groups`** | New: `Id`, `Name` (unique, ignoring case), `Description`, `CreatedAt`, `UpdatedAt` |
| **`group_rights`** | New: `GroupId`, `Area`, `Level` (text); key `(GroupId, Area)`; an area without a row is *None* |
| **`user_groups`** | New: `UserId`, `GroupId`; key on both, cascade from both sides |
| `decisions` | **`RequiredRole` becomes `AdminOnly`** (bool; `Admin` → true). Every decision needs Decisions *Decide*; an `AdminOnly` one the admin flag |
| **`redmine_links`** | New: a ticket to a page, location, host or sensor (§6.5) |
| **`redmine_issues`** | New, the copy of linked tickets (§6.7) |
| **`zabbix_hosts`** | New, the mirror (§7.3) |
| **`zabbix_sensors`** | New, keyed by `SensorId`, `ZabbixHostId` unique (§7.7) |
| `sensors` | `SensorType.ZABBIX = 110` (an enum value, no column) |

**Four migrations:** the audit log, groups and rights, Redmine, Zabbix, so each can ship alone (groups after the audit log, §11).

### 8.3 Server components

| Component | Folder | Does |
|---|---|---|
| `AuditEvents`, `AuditCatalog` | `Audit/` | The events with category, label, tone, target type |
| `IAuditLog`, `AuditScope`, `AuditTarget` | `Audit/` | Recording, per request or outside one (§3.3, §3.6) |
| `AuditFilter`, `AuditDeniedHandler` | `Audit/` | One entry per write request; refused ones (§3.3, §3.5) |
| `AuditChangesInterceptor`, the attributes | `Audit/` | The changes, the secret rules (§3.4) |
| `AuditRetentionTask` | `Audit/` | The daily trim (§3.8) |
| `AuditLogController` | `Api/V1/` | List, types, subject history, export |
| `Areas`, `Levels`, `RightsCatalog` | `Auth/Rights/` | The fixed areas and levels of §5.2, with their texts |
| `RequireRightAttribute`, `RightPolicyProvider`, `RightHandler` | `Auth/Rights/` | `[RequireRight]` as policies; the admin flag satisfies every one (§5.4) |
| `ICurrentRights`, `SessionCache` | `Auth/` | The levels per request, dropped when a group changes (§5.4) |
| `GroupService`, `GroupsController` | `Auth/`, `Api/V1/` | Groups, their rights and members; effective rights (§5.5) |
| `RedmineOptions`, `RedmineClient`, `RedmineLookups` | `Redmine/` | §6.1, §6.2 |
| `RedmineService`, `RedmineLinks`, `RedmineSync`, `RedmineText` | `Redmine/` | Search, tickets, links, the copy, Redmine's syntax for `MarkdownView` |
| `RedmineController` | `Api/V1/` | §9 |
| `ZabbixOptions`, `ZabbixClient`, `ZabbixLinks` | `Zabbix/` | §7.1, §7.2, §7.9 |
| `ZabbixHostSync`, `ZabbixPoller`, `ZabbixSnapshot` | `Zabbix/` | The mirror, the problems, the sensors' state (§7.3, §7.7) |
| `ZabbixService`, `ZabbixMatcher` | `Zabbix/` | Summary, problems, hosts, suggestions (§7.4–7.8) |
| `ZabbixController` | `Api/V1/` | §9 |
| `SensorService` | `Services/Domain/` | The `ZABBIX` type's create, update, delete, detail (as MikroTik's) |

### 8.4 Configuration

**ENV only**, as R2 asks, in `deploy/.env.example` (the blocks of §6.1 and §7.1, and below), `dev.env`, deploy/README.md (a section each, with the least-privilege accounts), and `info.sh` (Redmine and Zabbix: on or off, the URL; never the key).

```bash
# --- audit log (docs/audit-redmine-zabbix-plan.md §3.8) -----------------------------------------------
#Audit__KeepDays=400
#Audit__MaxEntries=500000
```

Typed options (`RedmineOptions`, `ZabbixOptions`, `AuditOptions`) through `IOptionsMonitor`, as AI's. **The keys' values are never in an API response:** status shows *set* or *not set*.

### 8.5 Health

- **System sensors** on host *PNMT*:
  - ***Redmine*:** **429 `REDMINE_FAILING`** when the sync or a call fails twice in a row ("Redmine doesn't answer", "the API key was refused", "project *network* not found"), 220 when it works again;
  - ***Zabbix*:** **430 `ZABBIX_FAILING`** likewise, for the poller and the host sync.

  Both exist only while their integration is configured, and notify once per outage, as the AI's does.
- **Task health** (`GET /system/tasks`): *Redmine sync*, *Zabbix problems*, *Zabbix hosts*, *Audit retention*.
- ***Admin → System health*** gets a section *Integrations*: for each, configured or not, the URL, the account (from `users/current.json`, Zabbix's version), the projects or groups as resolved, the last success and the last error. **The debug page** gets *Test Redmine* and *Test Zabbix* (System *Manage*, audited).

### 8.6 Security

| Risk | Measure |
|---|---|
| **The keys leak** | ENV only, `.env` at 0600; **headers, never URLs**; never in responses, logs, the audit (`[AuditSecret]` isn't even needed: they're not in the database); `System.Net` logs at *Warning* already |
| **Too much access** | **Dedicated accounts with read-only roles** (§6.2, §7.2); PNMT never writes; **PNMT's own allowlists** (projects, host groups) on top |
| **SSRF** | **The addresses come from ENV only.** Users never give PNMT a URL to fetch; ids in paths are checked to be numbers |
| **XSS through tickets, problems, inventory** | **Ticket text through `MarkdownView`**, which renders no HTML; everything else is text in Vue templates. Links to Redmine and Zabbix are built by PNMT from ids |
| **Self-signed certificates** | **A pinned SHA-256** (`CertificateSha256`), never turning validation off |
| **Large or slow answers** | Timeouts (15 s), size caps (5 and 20 MiB), Redmine's pages of 100 at most, Zabbix's `limit` on every call |
| **Personal data** | **Tickets need User; inventory and acknowledgements too.** PNMT copies only ticket headers of linked tickets (§6.7), and no Zabbix content beyond hosts and the problems in memory |
| **The audit log holds addresses and names** | Audit log *View* only, admins alone at the start; History tabs leave addresses and browsers out (§4.3); retention (§3.8) |
| **The audit log is changed** | No endpoint deletes or edits it; only the retention task trims. A database administrator can still change it (§14.1) |

### 8.7 What packages eight to ten get

- **The audit log is ready for them:** `Actor` (`Mcp`, `Cookie`, `Seraph`), `ActorRef` (the token's id, the decision's id), the same catalog and filter. Package eight's MCP log can be the audit log with `Actor = Mcp` and its reads, rather than a table of its own; that is for package eight to decide.
- **Rights:** MCP tokens act with their user's rights, the read-only token capped at *View* and *Read*; Cookie gets an admin-chosen group's rights; console access becomes an area of its own (§5.7).
- **Redmine and Zabbix are reached through `RedmineService` and `ZabbixService`**, never through the clients directly, so the projects' and groups' allowlists apply to MCP and Cookie too.
- **`MachineAccess` covers ticket links:** a link to a sensitive page is invisible to machines, as the page is.
- **Ticket text and problem names are untrusted input** for any AI: through `Untrusted` and Jev, as mail is. Writing to Redmine (Cookie's tickets and answers) will be a separate, explicit switch and a second key, so that the read-only key of this package stays read-only.

---

## 9. API

All under `/api/v1`, all added to `EndpointAuditTests`' access matrix with their right, every write one with its `[Audit]`. *Admin* means the admin flag; *signed in* means no right is needed.

| Endpoint | Right | |
|---|---|---|
| `GET /rights` | signed in | **The catalog of §5.2:** areas, levels, what each allows. For the group page and *Your access* |
| `GET /groups`, `GET /groups/{id:guid}` | Admin | Groups with their levels and members |
| `POST /groups`, `PUT /groups/{id:guid}` | Admin | `{ name, description, rights: { Monitoring: "Edit", … } }` |
| `DELETE /groups/{id:guid}` | Admin | |
| `PUT /groups/{id:guid}/members` | Admin | `{ userIds }` |
| `POST /users`, `PUT /users/{id:guid}` | Admin | **`isAdmin` and `groupIds` replace `role`** |
| `GET /users/{id:guid}/access` | Admin | Effective rights, with the group each comes from (§5.5) |
| `GET /account/access` | signed in | One's own (§5.5) |
| `GET /auth/me` | signed in | **Gains `isAdmin` and `rights`**, loses `role` |
| `GET /audit-log` | Audit log *View* | The list (§4.2); query: search, categories, types, outcome, actor, userId, subject, from, to, page |
| `GET /audit-log/types` | Audit log *View* | The catalog (§3.9) |
| `GET /audit-log/export` | Audit log *View* | CSV, same query (§4.2). Audited |
| `GET /audit-log/subjects/{id:guid}` | Monitoring *View* | A host's or sensor's history, without addresses (§4.3) |
| `GET /redmine/status` | signed in | On or off, `PublicUrl`, the projects' names: the UI decides what to show |
| `GET /redmine/lookups` | Redmine *View* | Projects, trackers, statuses, priorities, members |
| `GET /redmine/overview` | Redmine *View* | `?project=`: the tiles and lists (§6.3), cached a minute |
| `GET /redmine/issues` | Redmine *View* | The search (§6.3) |
| `GET /redmine/issues/{id:int}` | Redmine *View* | A ticket (§6.4) |
| `GET /redmine/related` | Redmine *View* | `?pageId\|locationId\|hostId\|sensorId`: linked tickets, with *via* groups for sensors, hosts and locations (§6.6) |
| `POST /redmine/links` | Redmine *Link* | `{ issueId, pageId\|locationId\|hostId\|sensorId, note }` |
| `PUT /redmine/links/{id:guid}` | Redmine *Link* | The note |
| `DELETE /redmine/links/{id:guid}` | Redmine *Link* | |
| `POST /redmine/test` | System *Manage* | |
| `GET /zabbix/status` | signed in | On or off, version, `PublicUrl`, as of |
| `GET /zabbix/summary` | Zabbix *View* | `?category=group\|tag:<name>&suppressed=`: tiles, matrix, hosts (§7.4) |
| `GET /zabbix/problems` | Zabbix *View* | The list (§7.5) |
| `GET /zabbix/problems/{eventId}` | Zabbix *View* | The trigger's description; acknowledgements with Zabbix *Details* |
| `GET /zabbix/hosts` | Zabbix *View* | The mirror's list (§7.6) |
| `GET /zabbix/hosts/{hostId}` | Zabbix *View* | The host page; inventory with Zabbix *Details* |
| `GET /zabbix/hosts/{hostId}/items` | Zabbix *View* | Latest data, `?search=` |
| `GET /zabbix/suggestions` | Zabbix *View* and Monitoring *Edit* | §7.8 |
| `POST /zabbix/attach` | Zabbix *View* and Monitoring *Edit* | `[{ zabbixHostId, hostId \| newHostName, minSeverity? }]`: one or many |
| `POST /zabbix/test` | System *Manage* | |

**Every existing endpoint** gets its right in the same pass: the controller's area at *View* for reads, and the level of §5.2 for writes (Appendix A lists the writes by area, and so by right).

**The Zabbix sensor itself** goes through the existing `POST`/`PUT /sensors` with a `zabbix` sub-request (`zabbixHostId`, the settings), as MikroTik's `mikrotik` does. `GET /search` gains `tickets` and `zabbixHosts` in `SearchResult`.

---

## 10. Web UI

### 10.1 Navigation

```
Monitoring   Dashboard · Decisions · Hosts · Locations · Events · …
Knowledge    Knowledge base
Integrations Redmine · Zabbix                         ← new; each only when configured
Admin        Users · Groups (new) · Audit log (was Auth log) · …
Help         Documentation
```

**Every entry shows only with its right** (`right` instead of `role` in `navigation.ts`): *Redmine* with Redmine *View*, *Audit log* with Audit log *View*, *Users* and *Groups* with the admin flag, *MikroTik & WireGuard* with its *Manage*, and so on. **The *Admin* group of the drawer shows whatever admin page the user may open**, so someone with only Servers *Manage* sees *Admin → Servers* and nothing else there.

### 10.2 Routes and views

| Route | View | |
|---|---|---|
| `/admin/groups`, `/admin/groups/:id` | `views/admin/groups/GroupsView.vue`, `GroupView.vue` | §5.5 |
| `/admin/users` | `views/admin/UsersView.vue` | The admin switch, groups, effective rights (§5.5) |
| `/account/security` | `views/account/SecurityView.vue` | Gains *Your access* (§5.5) |
| `/admin/audit-log` | `views/admin/AuditLogView.vue` | Renamed from `AuthLogView.vue` (§4) |
| `/admin/auth-log` | redirect | With its query |
| `/hosts/:id/:tab(overview\|history)?` | `HostView.vue` | Gains tabs: *Overview* (today's page), *History* |
| `/sensors/:id/:tab(…\|history)?` | `SensorView.vue` | *History* for every type; the Zabbix type's *Overview*, *Events*, *Setup* (router regex too) |
| `/redmine/:tab(overview\|search)?` | `views/redmine/RedmineView.vue` | *Overview* and *Search* (§6.3) |
| `/redmine/issues/:id(\\d+)` | `views/redmine/RedmineIssueView.vue` | The ticket's full page (§6.4) |
| `/kb/:number(\\d+)` | `views/kb/KbPageView.vue` | Gains the *Tickets* panel (§6.6) |
| `/locations/:id/:tab(…\|tickets)?` | `LocationView.vue` | Gains *Tickets* |
| `/zabbix/:tab(overview\|problems\|hosts\|suggestions)?` | `views/zabbix/ZabbixView.vue` | §7.4–7.6, §7.8 |
| `/zabbix/hosts/:hostId(\\d+)` | `views/zabbix/ZabbixHostView.vue` | §7.6 |

**New components:** `RightsTable.vue` (a group's levels, editable or read-only with their sources), `AuditChanges.vue` (the changes table, shared by the log and the History tabs), `AuditHistory.vue`, `RedminePanel.vue` (like `KbPanel`), `TicketButtons.vue` (*Details* and *Redmine*), `TicketChip.vue`, `RedmineIssuePicker.vue`, `ZabbixSeverityTag.vue`, `ZabbixHostPicker.vue`, `ZabbixFields.vue` (the sensor form), `views/sensors/zabbix/*Tab.vue`. `GlobalSearch.vue` gets the kinds `ticket` and `zabbix-host`.

**The session store** gets `isAdmin`, `rights` and `can(area, level)`; `useRights()` gives a view its area's checks. `router/guard.ts` reads `meta.right`; a missing right shows *You need …* rather than a blank page.

**The work on the sensor page that isn't committed yet** (`SensorNotesPanel.vue` and the overview tabs) comes first; this package builds on it.

### 10.3 The documentation

`DocsView.vue` gets a section each: ***Groups and rights*** (the areas and levels, generated from `GET /rights`, and the admin flag), ***The audit log*** (what is logged and what isn't, the outcome, retention), ***Redmine*** (what PNMT shows, links, the copy), ***Zabbix*** (the sensor's rules, the categories, attaching). The new sensor type and codes appear on their own, from the API.

---

## 11. Phases

| Phase | Content | Size |
|---|---|---|
| **A0** | **Verify**, with no product code; the results go into this document:<ul><li>the audit entries a full e2e run and a week of the dev seed would make, by category, to confirm the retention defaults;</li><li>**every `ExecuteUpdate`/`ExecuteDelete` per endpoint**, and how each will name its target;</li><li>the search on 500,000 generated entries, with and without `pg_trgm`;</li><li>the list views with the `.cell` `max-width` pattern</li></ul> | small |
| **A1** | **The fix and the base:**<ul><li>**first, on its own: the cut-off detail** (§4.1), in the auth log and the mail log;</li><li>the migration: rename, columns, the old rows, indexes;</li><li>`AuditCatalog` with today's events, `IAuditLog`, `AuditScope`, `AuditFilter`, the denied handler, `AuditChangesInterceptor` and its attributes;</li><li>**the four old writers replaced**;</li><li>the retention task; `Audit__*`;</li><li>**the tests of §3.7** (they fail until A2 is done, so they start as a list of the endpoints still missing, which A2 empties)</li></ul> | medium-large |
| **G1** | **Rights on the server:**<ul><li>`groups`, `group_rights`, `user_groups`, `users.IsAdmin`; **the conversion of the roles** (§5.3);</li><li>the catalog, `[RequireRight]`, the policy provider and handler;</li><li>**every endpoint's attribute converted**, driven by the access matrix of `EndpointAuditTests` (roles → rights);</li><li>`SessionCache` with the levels, dropped on group changes; `ICurrentRights` for the checks inside services; `missing_right`;</li><li>the admin flag: last admin, the 2FA policy on the flag, decisions' `AdminOnly`;</li><li>groups' and users' API, effective rights;</li><li>the CLI and the importer.</li></ul>**The web UI's session store, guard and drawer switch in the same commits**, so master works after every one: until G2, the store's existing helpers answer `canWrite` with Monitoring *Edit* and `isAdmin` with the admin flag. No role is kept on the way | large |
| **A2** | **Every endpoint's audit:**<ul><li>`[Audit]` or `[NotAudited]` on all write endpoints (Appendix A), the groups' included, the new events;</li><li>**targets and summaries in the services**, the bulk writes' names;</li><li>`[Audited]`/`[NotAudited]` on every entity, `[AuditSecret]`/`[AuditIgnore]`/`[AuditText]` on their properties;</li><li>the CLI commands; the system's own changes</li></ul> | large |
| **G2** | **Rights in the web UI:**<ul><li>the session store, `useRights()`, the guard and the drawer by right;</li><li>**every view's checks** (the 194 `canWrite` and 63 `isAdmin`), view by view, each to its area;</li><li>*Admin → Groups*, the users page (switch, groups, effective rights), *Your access*;</li><li>*You need …* for refusals;</li><li>the store's interim `canWrite` and `isAdmin` helpers removed;</li><li>the documentation's section</li></ul> | medium-large |
| **A3** | **The web UI:**<ul><li>*Audit log*: the redirect, filters from the catalog, the columns, the details with the changes, the export;</li><li>*History* on hosts and sensors;</li><li>the users page's link;</li><li>the documentation's section</li></ul> | medium |
| **R0** | **Verify against Redmine 5.1 and 6.0** (§6.2): filters for several projects, `issue_id` lists, the text search, `include=`, private notes and tickets, the prefilled form; **the overview's filters** (priorities above the default, no assignee, `due_date`, `updated_on`); **fixtures recorded** | small |
| **R1** | **The server:** options, client, lookups, the project allowlist, status and test, the system sensor, **the overview** and its one-minute cache, search, the ticket, links (pages too), the copy and its sync, the audit events, `RedmineText` | medium |
| **R2** | **The web UI:** the Redmine page's *Overview* and *Search*, **the two buttons**, the ticket's full page with *Link to…* (pages too), the panels on hosts, sensors, locations and knowledge base pages, the *Tickets* tab, the chips (sensor, dashboard), the host's header, *New in Redmine*, the global search, the documentation | medium |
| **Z0** | **Verify against Zabbix 6.0 LTS, 7.0 LTS and the newest release** (§7.2): the token in both places, the renamed parameters, availability, `problem.get` with `trigger.get`, the frontend URLs, the role's allow list; **fixtures recorded** | small |
| **Z1** | **The server's reading side:** options, client with the version, `ZabbixLinks`, the host mirror, the poller and snapshot, summary, problems, hosts, the host page's calls, the system sensor, status and test | medium |
| **Z2** | **The Zabbix sensor:** the type, its side table, the state and codes, `SensorService`, the matcher, attaching one and many, the audit events | medium |
| **Z3** | **The web UI:** overview, problems, hosts, the host page, suggestions, the sensor's form and tabs, the ticket chip on problems, the global search, the documentation | medium-large |
| **F** | **The finish:**<ul><li>**`pnmtd dev fake-integrations`**: a CLI command serving the recorded fixtures as a Redmine and a Zabbix, for `dev.env` and the e2e stack (as `AgentSimulator` does for agents);</li><li>the seed: links, Zabbix sensors, audit entries of every category;</li><li>`e2e/audit.spec.ts`, `e2e/redmine.spec.ts`, `e2e/zabbix.spec.ts`;</li><li>deploy/README.md, `.env.example`, `info.sh`, the README;</li><li>release notes; this plan's *As built*</li></ul> | medium |

- **Order:** A1, G1, A2, G2, A3 first: R and Z log through the new audit and are guarded by the new rights. **G comes after A1** so that group and rights changes are audited from their first day, **and before A2**, so that A2's pass through the endpoints sees their final attributes. **R and Z don't depend on each other** and can be built in either order, or one shipped without the other.
- **Each phase:** one or more commits on master; `./run-tests.sh` green before the next; an *As built* section.

---

## 12. Testing

As in the earlier packages: **few tests, where they protect something.**

| What | How |
|---|---|
| **Coverage of the audit** (`EndpointAuditTests`, `AuditModelTests`) | **Every write endpoint has its attribute; every one writes a `Denied` entry with its event when called without its right;** every entity is classified; secret-looking properties are marked; every event has its catalog entry (§3.7) |
| **The entries** | A host update records its changes and summary; **a secret shows as *changed***; a long text as line counts; a sensor deleted through `ExecuteDelete` keeps its name and subjects; **a rolled-back save leaves no entry**; a 409 and a 422 are *Failed* with their codes; a refused burst is capped; a CLI command is `Actor = Cli` |
| **The log's migration** (`tools/upgrade-test.sh`) | The old rows keep their meaning: categories, outcomes, the "cli: " prefix becoming `Actor` |
| **Retention** | By age, then by count, and nothing else deleted |
| **Rights** (`EndpointAuditTests`' access matrix) | **Every endpoint names its area and level.** For each: anonymous gets 401; **a user with every level except the one it needs gets 403**, naming the missing right; a user with exactly that level, and an admin, get through. `TestApp`'s `X-Test-Role` becomes `X-Test-Rights: Monitoring:Edit` and `X-Test-Admin` |
| **Groups** | Levels add up across groups, the highest wins; *Edit* includes *View*; **a group change applies on the very next request** of an open session; removing the admin flag from the last admin is a 409; **no right lets a non-admin change groups, users or the flag**; `RequiredForAdmins` asks the admin flag only |
| **The conversion** (`tools/upgrade-test.sh`) | **A Viewer, a User and an Admin of 1.3 get exactly the answers they got before**, endpoint by endpoint, after the update; **no role is left** in the database, the API's document or the UI's types |
| **The page** (Playwright) | **On 1,920 px, a 300-character detail fills its column** (the cut's position is measured, not eyeballed); the row opens with the changes; the filter lists every type; the old URL redirects |
| **Redmine** (`FakeRedmine`, from R0's fixtures) | **The overview's lists** pick the right tickets (priorities above the default, unassigned, overdue, `StaleDays`), and one minute of cache serves many users with one set of calls; **the key is a header and never in a URL or log line;** a ticket of another project is a 404, also by number and through links; the copy is refreshed and marks missing tickets; links are unique, cascade with their objects (pages included), and refuse other projects; errors reach the system sensor once |
| **Ticket text** (Vitest) | `#1234`, `attachment:` and image references become the right links; **the XSS corpus of `MarkdownView` through a ticket**; Textile stays text |
| **Zabbix** (`FakeZabbix`, from Z0's fixtures, one set per version) | **The token where the version wants it**, never in a URL; renamed parameters per version; `HostGroups` narrows the hosts; the mirror marks gone hosts |
| **The Zabbix sensor** | Each rule of §7.7: severity threshold, acknowledged, suppressed, unavailable, gone; **an event only on a change**; **no events while Zabbix doesn't answer**, and the system sensor fails once; a Zabbix host attached twice is a 409 |
| **Matching** | Names (case, first label), addresses (ping target, agent, router), two candidates not preselected |
| **Endpoints** | Every endpoint of §9 in the access matrix |
| **End to end** (Playwright, with `fake-integrations`) | **Audit:** change a host, see it on its *History* and in the audit log with the change; a *Viewers* member's refused delete appears as *Denied*, naming Monitoring *Edit*. **Redmine:** the overview lists an urgent and an overdue ticket; *Details* opens the full page, *Redmine* has the right URL; link the ticket to a host and to a knowledge base page; the host's panel and header and the page's panel show it; the failing sensor shows the chip, and the ticket appears under *Linked to a problem in PNMT*; search by number. **Zabbix:** the overview's counts, filter problems by a tag, attach a suggested host in bulk, its sensor fails with the problem; open a Zabbix host's page and follow *Open in Zabbix* |

---

## 13. Risks

| Risk | Mitigation |
|---|---|
| **An endpoint's changes aren't recorded** (a bulk write, a side table) | The coverage tests make every endpoint write *an* entry; A0 lists the bulk writes; A2 names their targets; the seed and e2e look at the result |
| **A secret ends up in the log** | `[AuditSecret]`, and the test that refuses unmarked secret-looking properties. Values are only recorded from `[Audited]` entities; request bodies are never logged |
| **The log grows too fast** | A0 measures; retention by age and count; the operational writes are `[NotAudited]` |
| **Writing entries slows writes down** | One row per request, in the same `SaveChanges`. Failures cost a second context only when they happen |
| **The rename breaks something that reads `auth_log`** | One commit, the importer's SQL, the upgrade test |
| **Redmine's or Zabbix's APIs differ between versions** | R0 and Z0 against real versions, fixtures per version, and Zabbix's version read first. Zabbix's URLs in one class |
| **Zabbix and PNMT both alert for the same thing** (Zabbix's mails through the mail input, and the sensor) | The documentation says so, and suggests retiring the mail rule once the host's Zabbix sensor is in. Both keep working |
| **An outage of Zabbix floods PNMT with alerts** | No events while it doesn't answer; one system sensor notifies (§7.7) |
| **Many Zabbix problems** (thousands in a large installation) | One `problem.get` a minute; `trigger.get` only for new triggers; the snapshot in memory; the list paged on the server |
| **Ticket text carries an attack** | No HTML is rendered; untrusted for every later AI (§8.7) |
| **The Redmine account sees more than PNMT should show** | PNMT's own project allowlist, also for links and the search (§6.2) |
| **The conversion changes someone's access** | The mapping of §5.3 copies the roles exactly; the upgrade test compares every endpoint's answer before and after |
| **Admins lock themselves out** (the last admin's flag removed, a group emptied) | The last-admin check on the flag; `pnmtd users admin <name> on` from the deploy host; groups never govern users and groups |
| **Someone raises their own rights** | Only the admin flag manages users, groups and the flag (§5.1); a test tries it with every level |
| **A view's check is missed in G2** | The server decides: the button answers *You need …*. G2 goes through the views by area, and the e2e run signs in as each seeded group |
| **Groups grow into something no one understands** | Fixed areas, one to three levels, no deny, no per-user exceptions; *Your access* and the users page show where every level comes from |
| **People expect to write to Redmine from PNMT** | *New in Redmine* opens Redmine's prefilled form; writing is package nine's |

---

## 14. Decisions and open questions

### 14.1 Decisions

1. **The auth log is extended and renamed, not joined by a second log** (§2.3, §3.2).
2. **An operation is a call to a write endpoint, failed and refused ones included; reads aren't** (§3.1).
3. **Three layers make the entries:** the endpoint's attribute, a filter per request, an interceptor for the changes (§3.3).
4. **Tests, not discipline, keep it complete:** every write endpoint and every entity is classified (§3.7).
5. **Secrets are recorded as *changed*, never as values** (§3.4).
6. **Redmine and Zabbix are read-only in these packages** (non-goals).
7. **Redmine's links are shaped like the knowledge base's**, and only linked tickets' headers are copied (§6.5, §6.7).
8. **A Zabbix host is a sensor of type `ZABBIX`**, attached at most once; its state follows a severity threshold (§7.7).
9. **Zabbix's open problems are held in memory, its hosts mirrored**, nothing else stored (§7.3).
10. **An outage of Zabbix or Redmine is one system sensor's alarm**, not every sensor's (§7.7, §8.5).

**From the review of revision 1 (2026-11-14):**

| # | Question | Decision | Where |
|---|---|---|---|
| 11 | A more detailed rights system | **Groups with rights replace the three roles;** a user can be in several groups | §5.1 |
| 12 | Admins | **An admin flag on the user grants every right automatically** | §5.1 |
| 13 | How detailed | **Kept simple:** 16 fixed areas with one to three levels, levels include those below, rights only add up; no deny, no exceptions per user, no rights per object | §5.2 |
| 14 | The update | **Nobody's access changes:** User → *Operators*, Viewer → *Viewers*, Admin → the flag | §5.3 |
| 15 | Who manages users and groups | **Only admins**, so no one raises their own rights | §5.1 |
| 16 | The *History* tabs (was open question 2) | **Monitoring *View***, like a location's history | §4.3 |
| 17 | Who reads tickets (was open question 4) | **Redmine *View***, given to *Operators*, not to *Viewers*; an admin can give it to any group | §8.1 |

**From the review of revision 2 (2026-11-14):**

| # | Question | Decision | Where |
|---|---|---|---|
| 18 | Roles | **None any more:** the admin flag, and groups for everything else. The existing users are converted once; no role is kept for compatibility, in the API, the CLI or the decisions | §5.3 |
| 19 | Logging reads (opening a sensitive page, a ticket, a mail) | **No:** only today's sensitive reads and the export | §3.1 |
| 20 | Retention | **400 days and 500,000 entries, both in ENV** | §3.8 |
| 21 | A Zabbix host on more than one PNMT host | **No**, at most once | §7.7 |
| 22 | A tamper-evident log, forwarding to syslog | **Not now** | Non-goals |
| 23 | Redmine's pages | **An overview of the most important tickets; two buttons on every ticket**, its detail page (a full page, not a window) and Redmine; **on the detail page, links to knowledge base pages**, locations, hosts and sensors | §6.3–6.6 |
| 24 | 16 areas | **Kept apart** as planned: the five admin areas aren't merged | §5.2 |
| 25 | Commands in Monitoring *Edit* | **No:** an area of its own | §5.2 |
| 26 | Groups resetting passwords | **No:** admins only | §5.1 |
| 27 | Rights per location | **Not in this package;** later, if needed, as a group's optional location scope | §2.3 |
| 28 | 2FA for the powerful levels | **No: kept as it is.** `RequiredForAdmins` means the admin flag | §5.2 |

### 14.2 Open questions

None.
---

## 15. As built

**On master** (2026-11-14): 11 commits from 3865fee (A1) to the finish. Where the code differs from the sections above, this section says so; the rest is as planned. Nothing is pushed or tagged, and the version isn't bumped: [docs/release-notes-1.4.md](release-notes-1.4.md) is written for 1.4.0.

| Commit | Phase |
|---|---|
| 3865fee | A1: the audit log, its migration, scope, filter, denied handler, interceptor, retention |
| 2ad6466 | G1 **with G2**: rights on the server and in the web UI together, so master worked after every commit |
| 09a3b70 | A2: every write endpoint's event, every entity classified |
| 6c68b3c | A3: *Admin → Audit log*, *History* on hosts and sensors |
| 073154d | R0 and R1: Redmine verified, and the server |
| d4b354a | R2: the Redmine web UI |
| 265cfc0 | Z0 and Z1: Zabbix verified, and the server's reading side |
| 62a3024 | Z2: the Zabbix sensor |
| 9bbad54 | Z3: the Zabbix web UI |
| 883e833, and the next | F: the fakes, the seed; then the documentation and this section |

### What was checked against real servers

**Redmine (R0), 5.1 and 6.0 in containers, answering alike;** their 6.0 answers are the fixtures (`PNMTD/Dev/Fixtures/redmine`):

- **Several projects and several priorities only through the filter syntax** (`f[]`, `op[]`, `v[]`). It leaves subprojects out, so PNMT adds their ids itself.
- **`any_searchable`** searches subjects, descriptions and notes in one call with the other filters: the search API isn't used.
- **`issue_id` lists** return tickets of any project the key can read: PNMT filters them to its projects.
- The overview's filters work as planned (priorities above the default, no assignee, `due_date`, `updated_on`); `is_closed` on statuses and `is_default` on priorities are in the answers; the prefilled new-ticket form works.

**Zabbix (Z0), 6.0.49, 7.0.32 and 8.0.0 in containers,** set up as deploy/README.md describes (a role with an allow list of five methods, a user group with read access to two of three host groups, a token), with triggers, problems, a maintenance, an acknowledgement and an unavailable interface. The answers and what was checked are the fixtures (`PNMTD/Dev/Fixtures/zabbix-60`, `-70`, `-80`, each with `checks.json`):

| | 6.0 | 7.0 | 8.0 |
|---|---|---|---|
| **The token** in the request's `auth` | yes | yes | **refused** ("unexpected parameter auth") |
| **The token** as `Authorization: Bearer` | **"Not authorized"** | yes | yes |
| `apiinfo.version` with a token | refused | refused | accepted |
| Host groups under `selectGroups` | yes | yes (still) | **ignored** |
| Host groups under `selectHostGroups` | **ignored** | yes | yes |
| A method outside the allow list | -32602 "No permissions to call" | -32500, the same text | -32500 |

- **The rule of §7.2 holds:** Bearer from 6.4, the `auth` field before; never a token for `apiinfo.version`; `selectHostGroups` from 6.2.
- **Availability is per interface** in all three (`available` 0, 1, 2 and `error`). `problem.get` names no host; `trigger.get` with `selectHosts` does. Problems carry their host's tags; `opdata` comes resolved.
- **The token sees only its groups' problems,** with *Frontend access: Disabled* on the user group.
- **Acknowledgements name only a user id,** and a role of type *User* can't read other users: PNMT shows "Zabbix user 1".
- **The frontend's URLs of §7.9 work in all three,** except the host's configuration: `zabbix.php?action=host.edit` is a page up to 7.0 and answers JSON in 8.0, where `action=popup&popup=host.edit` is the page. PNMT uses the popup from 7.2 on; **7.2 and 7.4 weren't tested.**

### Where the code differs from the plan

**Audit and rights:**

- **A0 wasn't done as a step of its own:** the search on 500,000 entries and the entry counts of an e2e run and a week of the seed weren't measured. The retention defaults stand as planned.
- **No `IAuditLog` interface:** a static `AuditLog` (`Target`, `Summary`, `Change`, `Record`, `Entry`) writes into the request's `AuditScope`. A service that records the endpoint's own event keeps its detail as given.
- **The attributes** are `[Audit(events…)]` and `[NotAudited(reason)]` on actions and entities, `[Audited]`, `[AuditSecret]`, `[AuditIgnore]`, `[AuditText]`, `[AuditPlain(reason)]` on entities and properties.
- **No `useRights()` composable:** the session store's `can(area, level)` and `allows(right)` serve every view.
- **An endpoint needing rights of two areas** (attaching Zabbix hosts: Monitoring *Edit* and Zabbix *View*) has two `[RequireRight]`; the access matrix writes it `Monitoring:Edit&Zabbix:View` and checks each alone is refused.
- **Validation errors are 400,** as elsewhere in v1, not 422.

**Redmine:**

- **Not configured is 409** `redmine_not_configured`, as AI's; Redmine's failures are 502 (`redmine_refused`, `redmine_unreachable`).
- **A ticket outside PNMT's projects, or unknown to Redmine,** shows *Not in PNMT's Redmine projects* or *Not found in Redmine*, but **not its links** (§6.4 wanted them listed for removal). The objects' panels still list such a ticket from PNMT's copy, and unlink it there.
- **The pickers:** *Link ticket…* is `LinkTicketDialog.vue` (no `RedmineIssuePicker.vue`); the ticket page's *Link to…* is `TargetPicker.vue` over the global search, which finds pages, locations, hosts and sensors. The knowledge base's `LinkPicker` is unchanged.
- **The header chip** reads "2 open tickets" and scrolls to the panel; a failing sensor's chip is `SensorTicket.vue`, fed by `GET /redmine/problems`.
- **The tests** of *Test Redmine* and *Test Zabbix* are on *System health → Integrations*, not on the debug page.

**Zabbix:**

- **Five migrations, not four:** `AddZabbix` (the mirror, Z1) and `AddZabbixSensors` (the side table, Z2).
- **The server gained what the UI needed:** an exact `severity` filter on problems (a tile or cell opens just its severity), the mirror's host groups in the summary (for the filters), and a problem's attached `sensorId` (for the ticket chip).
- **The sensors are evaluated after the host sync too,** so a host that disappears is 491 at once. New rules reset the last state, so their result is an event at once. A ZABBIX sensor's interval and grace period are 0.
- **The mirror's interfaces** are stored with their availability by name (`"Unavailable"`).
- **The sensor's page** has *Overview*, *Events* (the page every sensor has), *Setup* and *History*; Overview carries the knowledge base and ticket panels, and *Zabbix not reached since …* when the snapshot failed.

**The finish:**

- **The fakes are the tests' ones,** moved into `PNMTD/Dev` with their fixtures embedded, so tests, `pnmtd dev fake-integrations` and the browser tests use the same answers. `tools/e2e.sh` starts the fakes beside the stack, so `redmine.spec.ts` and `zabbix.spec.ts` always run; `E2E_ENV` names real servers instead (that is how R2 and Z3 were checked against Redmine 6 and Zabbix 7.0).
- **`dev.env` isn't touched** (it is gitignored and holds a person's settings): `pnmtd dev fake-integrations` prints the lines to add.
- **The seed** adds the Redmine links and copies only with Redmine configured, and nas-01's Zabbix sensor only with the fake Zabbix, whose host ids it knows.

**Found and fixed on the way:** `deploy/info.sh` still asked for `users."Role"`, which G1 removes, for its warning about admins without 2FA; the global search's ticket subjects escaped wildcards with nothing instead of `\`; the Redmine client's comment claimed a pinned certificate excludes valid ones (it accepts both).

**The uncommitted work on the sensor page** (`SensorNotesPanel.vue` and the overview tabs) was left as it was: this package's changes to `SensorView.vue` were committed on top of the committed file, without it. Once, early in R2, an editing script emptied that uncommitted file; it was restored from the recorded diff and compared, and later edits read the file whole before writing.

## Appendix A: write endpoints, their rights and audit events

**Bold** events are new; the rest exist. *NA* is `[NotAudited]`, with its reason. The right is the level of §5.2 a write needs; reads need the area's lowest level.

| Area | Right | Endpoint → event |
|---|---|---|
| **Hosts** | Monitoring *Edit* | `POST /hosts` → **HostCreated** · `PUT /hosts/{id}` → **HostUpdated** · `DELETE /hosts/{id}` → **HostDeleted** · `PUT /hosts/importance` → **HostsImportanceChanged** · `PUT /hosts/location` → **HostsLocationChanged** |
| **Sensors** | Monitoring *Edit* | `POST /sensors` → **SensorCreated** · `PUT /sensors/{id}` → **SensorUpdated** (MikroTik's secrets, access and diagnostics additionally as today) · `DELETE /sensors/{id}` → **SensorDeleted** · `POST /sensors/{id}/tokens/rotate` → **SensorTokensRotated** · `PUT /sensors/importance` → **SensorsImportanceChanged** · `POST /sensors/lookup` → *NA: a lookup by token* · `PUT /sensors/{id}/notification-rules/{ruleId}` → **NotificationRuleSensorLinked** · `DELETE …` → **NotificationRuleSensorUnlinked** |
| **Notification rules** | Monitoring *Edit* | `POST /notification-rules` → **NotificationRuleCreated** · `PUT /{id}` → **NotificationRuleUpdated** · `DELETE /{id}` → **NotificationRuleDeleted** · `PUT`/`DELETE /{id}/sensors/{sensorId}` → **NotificationRuleSensorLinked/Unlinked** · `PUT /{id}/scopes` → **NotificationRuleScopesChanged** · `POST /{id}/scopes/preview` → *NA: a preview* · `PUT`/`DELETE /{id}/exclusions/{sensorId}` → **NotificationRuleExclusionAdded/Removed** · `POST /notifications/assign` → **NotificationsAssigned** |
| **DNS zones** | Monitoring *Edit* | `POST /dns-zones` → **DnsZoneCreated** · `PUT /{id}` → **DnsZoneUpdated** · `DELETE /{id}` → **DnsZoneDeleted** · `POST /{id}/force-update` → **DnsZoneUpdateForced** · `PUT /{id}/records/{recordId}` → **DnsRecordUpdated** |
| **Mail input rules** | Monitoring *Edit* | `POST /mail-input-rules` → **MailInputRuleCreated** · `PUT /{id}` → **MailInputRuleUpdated** · `DELETE /{id}` → **MailInputRuleDeleted** |
| **Account** | signed in | `POST /auth/logout` → Logout · `POST /account/password` → PasswordChanged · `POST /account/sessions/revoke` → SessionsRevoked · `POST /account/totp/setup` → **TotpSetupStarted** · `POST /account/totp/confirm` → TotpEnabled · `POST /account/totp/disable` → TotpDisabled |
| **Users** | admin flag | `POST /users` → UserCreated · `PUT /users/{id}` → UserUpdated, **UserAdminChanged**, **UserGroupsChanged** (was RoleChanged) · `DELETE /users/{id}` → UserDeleted · `POST /{id}/reset-password` → PasswordReset · `POST /{id}/reset-totp` → TotpReset · `POST /{id}/unlock` → UserUnlocked |
| **Key values** | System *Manage* | `POST /key-values` → **KeyValueCreated** · `PUT /{id}` → **KeyValueUpdated** · `DELETE /{id}` → **KeyValueDeleted** |
| **UDP** | Monitoring *Edit* (key, counter, session) · Commands *Run* (commands) · UDP nodes *Manage* (firmware, updates) | `PUT /sensors/{id}/udp/key`, `POST …/regenerate-key` → UdpKeyChanged · `POST …/reset-counter` → UdpCounterReset · `POST …/reset-session` → UdpSessionReset · `POST …/commands` → **UdpCommandQueued** · `DELETE …/commands/{commandId}` → **UdpCommandCancelled** · `POST /udp/firmware` → UdpFirmwareUploaded · `PUT /udp/firmware/{id}` → **UdpFirmwareChanged** · `DELETE /udp/firmware/{id}` → UdpFirmwareDeleted · `POST /udp/ota-jobs` → UdpUpdateStarted · `DELETE /udp/ota-jobs/{id}` → UdpUpdateCancelled |
| **MikroTik** | Commands *Run* (poll, backup now, commands) · Monitoring *Edit* (regenerate keys) · MikroTik & WireGuard *Manage* (the rest) | `POST /sensors/{id}/mikrotik/poll` → **MikrotikPollRequested** · `POST …/host-key/accept` → MikrotikHostKeyAccepted · `POST …/backups` → **MikrotikBackupRequested** · `DELETE …/backups/{backupId}` → MikrotikBackupDeleted · `POST …/regenerate-keys` → WireGuardKeysRegenerated · `POST /mikrotik/jobs` → MikrotikUpdateStarted · `DELETE /mikrotik/jobs/{id}` → MikrotikUpdateCancelled · `POST /mikrotik/ssh-keys/rotate` → MikrotikSshKeyRotated · `POST /mikrotik/ssh-keys/retire` → MikrotikSshKeyRetired · `PUT /mikrotik/backup-settings`, `POST /mikrotik/routers/backup-settings` → **MikrotikBackupSettingsChanged** (MikrotikBackupEncryptionChanged additionally as today) · `POST /mikrotik/backup-keys` → MikrotikBackupKeySet · `POST /mikrotik/backup-keys/retire` → MikrotikBackupKeyRetired · `POST /mikrotik/commands` → MikrotikCommandStarted · `DELETE /mikrotik/commands/{id}` → MikrotikCommandCancelled · `POST /mikrotik/commands/{id}/redact` → MikrotikCommandRedacted |
| **WireGuard** | MikroTik & WireGuard *Manage* | `PUT /wireguard/network` → WireGuardNetworkChanged · `POST /wireguard/tunnels` → WireGuardTunnelCreated · `PUT /{id}` → WireGuardTunnelChanged · `DELETE /{id}` → WireGuardTunnelDeleted · `POST /{id}/regenerate-keys` → WireGuardKeysRegenerated |
| **Debug, uplink, AI** | System *Manage* (debug, uplink) · AI *Manage* (AI) | `POST /debug/pushover`, `POST /debug/mail` → DebugNotificationSent · `POST /debug/ai` → DebugAiTested · `POST /uplink/check` → **UplinkCheckRun** · `POST /ai/work/{id}/retry` → AiWorkRetried · `POST /ai/work/{id}/cancel` → AiWorkCancelled · `POST /ai/mail/backfill` → AiBackfillStarted · `POST /ai/analysis/run` → AiAnalysisStarted |
| **Decisions** | Decisions *Decide* (admins' decisions: admin flag) | `POST /decisions/{id}/decide` → **DecisionDecided** |
| **Agents** | Commands *Run* (collect) · Monitoring *Edit* (settings) · Servers *Manage* (the rest) | `POST /agents/servers/{id}/collect` → **AgentCollectRequested** · `POST …/send-log` → **AgentLogRequested** · `PUT …/settings` → AgentSettingsChanged · `POST …/revoke` → AgentRevoked · `POST …/rejoin-token`, `POST /agents/join-tokens` → AgentJoinTokenCreated · `DELETE /agents/join-tokens/{id}` → AgentJoinTokenRevoked · `POST /agents/downloads/fetch` → AgentDownloadsFetched · `POST`/`PUT`/`DELETE /agents/scripts…` → AgentScriptChanged · `POST /agents/scripts/{id}/parse` → *NA: parses, saves nothing* · `POST /agents/scripts/{id}/run` → AgentScriptRun · `POST /agents/script-runs/{id}/redact` → **AgentScriptRunRedacted** |
| **Windows** | Servers *Manage* | `PUT /windows/servers/{id}/wmi` → WindowsSettingsChanged · `POST …/local-scripts/run` → AgentScriptRun · `POST`/`PUT`/`DELETE /windows/run-as…` → **WindowsRunAsChanged** (the password `[AuditSecret]`) |
| **SSH keys** | SSH keys *Manage* | `POST /ssh-keys/parse` → *NA: parses* · `POST /ssh-keys` → SshKeyRegistered · `PUT /{id}`, `POST /{id}/reactivate` → SshKeyChanged · `POST /{id}/retire` → SshKeyRetired · `DELETE /{id}` → SshKeyDeleted · `POST /{id}/removal-items`, `POST /users/{userId}/removal-items`, `POST /remove/preview` → *NA: previews* · `POST /users/{userId}/offboard` → SshKeyRetired · `POST /remove` → SshKeyRemoved |
| **Diagnostics** | Diagnostics *Manage* · Commands *Run* (run) | `POST`/`PUT`/`DELETE /diagnostics/sets…` → DiagnosticsSetChanged · `POST`/`PUT`/`DELETE /diagnostics/assignments…` → DiagnosticsAssignmentChanged · `POST /diagnostics/assignments/{id}/run` → **DiagnosticsRun** |
| **Locations** | Locations *Edit* · *Manage* (delete) | `POST /locations` → **LocationCreated** · `PUT /{id}` → **LocationUpdated** · `POST /{id}/move` → **LocationMoved** · `DELETE /{id}` → LocationDeleted · `PUT /{id}/values/{fieldId}` → **LocationValueChanged** · `POST /locations/from-text` → **LocationsCreatedFromText** |
| **Location fields** | Locations *Manage* | `POST /location-fields` → **LocationFieldCreated** · `PUT /{id}` → **LocationFieldChanged** · `PUT /order` → **LocationFieldsReordered** · `POST /{id}/archive` → **LocationFieldArchived** · `POST /{id}/restore` → **LocationFieldRestored** · `DELETE /{id}` → LocationFieldDeleted |
| **Knowledge base** | Knowledge base *Edit* · *View* (views, editing) · *Manage* (delete, redact, tags) | `POST /kb/pages` → **KbPageCreated** · `PUT /{id}` → **KbPageSaved** · `POST /{id}/archive` → **KbPageArchived** · `POST /{id}/restore` → **KbPageRestored** · `DELETE /{id}` → KbPageDeleted · `POST /{id}/views` → *NA: the view counter* · `POST /{id}/editing` → *NA: who is editing* · `POST /{id}/revisions/{number}/restore` → **KbRevisionRestored** · `POST /{id}/redact` → KbRevisionRedacted · `PUT /{id}/sensitive` → KbPageSensitivityChanged · `POST /{id}/ai-tags` → **KbAiTagsRequested** · `PUT /kb/tags/{id}` → **KbTagRenamed** · `DELETE /kb/tags/{id}` → KbTagDeleted |
| **Files** | Knowledge base or Locations *Edit* · Knowledge base *Manage* (delete) | `POST /files` → **FileUploaded** · `DELETE /files/{id}` → FileDeleted |
| **New in this plan** | Redmine *Link* · System *Manage* (tests) · Monitoring *Edit* and Zabbix *View* (attach) · Audit log *View* (export) · admin flag (groups) | `POST /redmine/links` → **RedmineLinkAdded** · `PUT /redmine/links/{id}` → **RedmineLinkChanged** · `DELETE /redmine/links/{id}` → **RedmineLinkRemoved** · `POST /redmine/test` → **RedmineTested** · `POST /zabbix/attach` → **SensorCreated** (one) or **ZabbixHostsAttached** (several) · `POST /zabbix/test` → **ZabbixTested** · `GET /audit-log/export` → **AuditLogExported** · `POST /groups` → **GroupCreated** · `PUT /groups/{id}` → **GroupUpdated**, **GroupRightsChanged** · `DELETE /groups/{id}` → **GroupDeleted** · `PUT /groups/{id}/members` → **GroupMembersChanged** |

**Reads that stay logged:** `MikrotikJoinScriptViewed`, `MikrotikBackupDownloaded`, `WireGuardTunnelDownloaded`, and the new `AuditLogExported`.

---

## Appendix B: configuration and file changes

**Configuration:** `Audit__KeepDays`, `Audit__MaxEntries` (§3.8); `Redmine__*` (§6.1); `Zabbix__*` (§7.1). In `deploy/.env.example`, `dev.env` (pointing at `pnmtd dev fake-integrations`), deploy/README.md and `info.sh`.

**Server:**

- `PNMTD/Audit/` (new): the catalog, `IAuditLog`, scope, filter, denied handler, interceptor, attributes, retention task.
- `PNMTD/Auth/UserAccounts.cs`: `AuthEvents` moves to `Audit/AuditEvents.cs`; `Log` and `SaveAsync`'s trim go; roles become the admin flag and groups.
- `PNMTD/Auth/Rights/` (new): areas, levels, catalog, `[RequireRight]`, policy provider, handler. `Auth/AuthSetup.cs`: `Roles` and `Policies` go. `Auth/AuthService.cs`: `SessionCache` with the levels, the 2FA policy. `Api/ApiSetup.cs`: `CanSeeSecrets` goes.
- `PNMTD/Auth/GroupService.cs`, `Api/V1/GroupsController.cs` (new); `Api/V1/AuthController.cs` (users, `/auth/me`, `/account/access`).
- `PNMTD/Services/Domain/Mikrotik*Service.cs`, `DecisionService.cs`, `Api/V1/UplinkController.cs`: `ICurrentRights` instead of `IsInRole`.
- `PNMTD/Cli/UsersCommand.cs`, `Cli/GroupsCommand.cs` (new), `Import/UsersJsonImporter.cs`.
- `PNMTD/Services/Domain/UdpViews.cs`, `Agents/AgentLog.cs`, `Files/FileStore.cs`, `SshKeys/SshKeyRemovals.cs`, `Cli/*Command.cs`: through `IAuditLog`.
- `PNMTD/Api/V1/*.cs`: `[RequireRight]` instead of `[Authorize(Policy = …)]` everywhere, `[Audit]`/`[NotAudited]` on every write action; `AuthLogController` → `AuditLogController`.
- `PNMTD/Models/Db/*.cs`: `[Audited]`/`[NotAudited]`, `[AuditSecret]`, `[AuditIgnore]`, `[AuditText]`.
- `PNMTD/Services/Domain/*.cs`, `Locations/`, `Knowledge/`: targets and summaries.
- `PNMTD/Redmine/`, `PNMTD/Zabbix/` (new); `Api/V1/RedmineController.cs`, `ZabbixController.cs`.
- `PNMTD.Lib/Models/Enum/SensorType.cs` (`ZABBIX = 110`), `PNMTStatusCodes.cs` (290, 429, 430, 490–492).
- `Services/Domain/SensorService.cs`, `SystemService.cs` (types, codes, tasks), `SearchService.cs`; `Services/Events/SystemSensors.cs` (two keys).
- `Data/PnmtdDbContext.cs`, four migrations; `Import/SqliteImporter.cs`.
- `Program.cs`: the filter, the handler, the interceptor, the named clients, the hosted services.

**Web UI:**

- `stores/session.ts`, `router/guard.ts`, `layouts/navigation.ts`, `api/auth.ts`: rights instead of roles; `composables/useRights.ts` (new); every view's `canWrite`/`isAdmin`.
- `views/admin/groups/` (new), `views/admin/UsersView.vue`, `views/account/SecurityView.vue`, `components/RightsTable.vue` (new).
- `views/admin/AuthLogView.vue` → `AuditLogView.vue`; `views/mail/MailLogView.vue` (the layout fix).
- `views/hosts/HostView.vue`, `views/sensors/SensorView.vue`, `views/locations/LocationView.vue`: tabs and panels.
- `views/redmine/`, `views/zabbix/`, `views/sensors/zabbix/` (new); the components of §10.2; `views/kb/KbPageView.vue` and `LinkPicker.vue` (tickets and pages).
- `layouts/navigation.ts`, `router/index.ts`, `components/GlobalSearch.vue`, `views/dashboard/` (the ticket chip), `views/docs/DocsView.vue`.
- `api/resources.ts` and the generated `openapi.json`/`schema.d.ts`.

**Tests:** `PNMTD.Tests/Hosting/EndpointAuditTests.cs` (the access matrix by right), `TestApp.cs` (`X-Test-Rights`, `X-Test-Admin`), `Auth/GroupsTests.cs`, `Audit/`, `Redmine/` (`FakeRedmine`), `Zabbix/` (`FakeZabbix`), fixtures per version; `PNMTD.WebUI/e2e/audit.spec.ts`, `redmine.spec.ts`, `zabbix.spec.ts`.
