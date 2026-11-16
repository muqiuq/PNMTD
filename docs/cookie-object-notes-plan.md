# Plan: Cookie's own notes, *Monitoring: Details* for what names people, and Pushover priorities as a setting

| | |
|---|---|
| Status | **Built (2026-11-16), §11. Released in 1.10.0.** |
| Date | 2026-11-16 |
| Scope | Three changes. **(1) Cookie stops writing into the people's notes of a host or sensor and gets notes of its own,** shown on the host's and the sensor's page in a panel of their own, carried with the rights of their sources, removable by a person; a note needs *Monitoring: View* in Cookie's group. **(2) A level *Details* in *Monitoring*, between *View* and *Edit*,** for what names people: servers' and routers' logins, the access cases about them, a server's event log, journal and SSH keys. The logins borrowed *Edit* for want of such a level. With (1) and (2), **Cookie's group needs no *Monitoring: Edit* at all.** **(3) The mapping from importance to Pushover's priority becomes a setting on the Notification manager,** with Pushover's emergency level available, instead of a fixed mapping and an .env switch. |
| Goals | <ol><li>**The people's notes are the people's.** Cookie never writes into them (R1).</li><li>**Cookie's notes are Cookie's:** dated, linked to their case, visible to whoever may see their sources, and gone when a person removes them (R2).</li><li>**Cookie's group needs *Monitoring: View* for a note** (R3), ***Monitoring: Details* for the logins** (R7), and *Edit* for nothing (R8).</li><li>**Cookie reads its own notes back** on the host and the sensor, so it doesn't repeat itself (R4).</li><li>**The case book's idea of progress doesn't change:** a person's change to the notes is progress, Cookie's note isn't (R5).</li><li>**A person who looks after access without editing hosts gets *Details*,** and sees logins without being able to change a sensor (R7).</li><li>**Whoever manages alerting decides how loud each importance is on the phone,** in the GUI, for every Pushover PNMT sends (R9).</li></ol> |
| Not in scope | Moving the lines Cookie wrote into the people's notes until now: **the system isn't in production;** they stay as text, or the database is reset (§9, decision 4). A separate area for logins (decision 9). Notes on locations or tickets. Cookie editing or replacing a note of its own. The secrets' right: tokens, URLs, UDP and WireGuard keys, backups stay *Edit*. Pushover priorities per sender (Cookie, the access watch) instead of per importance (decision 12); Pushover sounds; acknowledgement receipts for emergency messages (decision 13); e-mail's `[High]` prefix. |
| Depends on | The actions and the executor (docs/cookie-plan.md §6, §21.8), the trace by rights (§9.2), the case book's progress rule (§7.1), the read tools (docs/mcp-plan.md §7.2), the access watch's rights (docs/cookie-access-plan.md §9, §10), the groups and rights (docs/audit-redmine-zabbix-plan.md §5), the notifications' importance and the debug page (docs/ai-operations-plan.md §8.2, §11.5). |
| Target | 1.10.0 |

**Words.** ***The people's notes*** are `Hosts.Notes` and `Sensors.Notes`: the field on the host's and the sensor's page that people write in. ***Cookie's notes*** are the new rows of `cookie_object_notes`: what Cookie noted on a host or a sensor, one row per note. ***An object*** is a host or a sensor. ***The areas*** of a note are the areas its sources came from (`Monitoring`, `Redmine`, `Mail`, …), as a case note carries them (cookie-plan §9.2). ***Details*** is the new level: *View*, plus what names people. ***Secrets*** are what *Edit* alone shows: sensor tokens and URLs, UDP keys, a router's WireGuard keys and scripts, backups. ***Importance*** is a sensor's Low, Normal or High, which every notification carries. ***Priority*** is Pushover's: −2 lowest, −1 low, 0 normal, 1 high, 2 emergency.

---

## 1. Today

### 1.1 Cookie writes into the people's notes

- **The action `object-note`** (`CookieActionKind.ObjectNote`, level *Auto* by default, not reviewed by Seraph because it stays inside PNMT) ends in `CookieExecutor.ObjectNoteAsync`: the text, with the date and Cookie's name in front, is **appended to `Hosts.Notes` or `Sensors.Notes`** as `[Cookie, 2026-11-15 10:30] …`, separated by an empty line. The audit gets `CookieNoteAdded` on the host or sensor; the decision log gets a row of kind `object-note` with target `object:{id}` and the case's areas; the day's limit is `Limits.ObjectNotesPerDay` (50).
- **The check before it** (`CookieExecutor`, the `ObjectNote` case of the checks) asks for ***Monitoring: Edit* in Cookie's group,** because that is the right of `PUT hosts/{id}` and `PUT sensors/{id}`, which change the same field. Without it: *Cookie's group needs Monitoring: Edit for a note on a host or sensor.* The seed's Cookie group has *Monitoring: View* only (`DevSeedCookie`), so on a seeded installation every note is refused.
- **The case book has to tell the lines apart.** A change to a host's or sensor's notes counts as a person's progress on the case (§7.1), so `CaseBook.PeoplesNotes` strips the lines that match `CookieExecutor.NoteLine` (`^[name, yyyy-MM-dd HH:mm] `) before hashing. A person who writes a line in that shape, or Cookie under another name, breaks the rule either way.
- **The web UI shows one field.** The host page's *Notes* and the sensor page's `SensorNotesPanel` edit the people's notes with *Monitoring: Edit*; Cookie's lines sit in the same textarea. A person tidying their notes deletes Cookie's lines with them, and Cookie's lines bury what the person wrote.
- **Cookie reads them back as outside text.** `host_get` and `sensor_get` return `Notes` wrapped as `<untrusted source="host notes">` (`MonitoringReadTools`), and the access brief does the same (`AccessJudge.BriefAsync`, *HostNotes*). Cookie's own earlier words come back to it as something to distrust, cut to 1500 characters together with the people's.
- **The rights of §9.2 aren't kept.** A case note carries the areas of its sources and is shown only to a reader who has them all (`CookieAccess.CanSee`). A note Cookie writes on a host from a case whose sources include a mail or a ticket **is shown to everyone with *Monitoring: View*,** because the people's notes are.

### 1.2 The logins borrow *Edit*

**Logins name people and addresses, so they were put behind *Monitoring: Edit*** (docs/cookie-access-plan.md §2: *Cookie's group has it already where Cookie writes notes on hosts*; the agents' plans before that: *need the User role*). *Edit* was the only level above *View*. Everything that sits on *Edit* for that reason, and not because it changes anything:

| | Where | Right today |
|---|---|---|
| A server's logins | `GET agents/servers/{id}/logins` (`AgentControllers`) | *Monitoring: Edit* |
| A server's SSH keys (their comments name people) | `GET agents/servers/{id}/ssh-keys` | *Monitoring: Edit* |
| A server's *access*, *event log*, *journal* sections | `GET windows/servers/{id}/sections/{section}`, `GET linux/servers/{id}/sections/{section}`: *View* on the endpoint, the sensitive sections gated in code by `CanSeeSecrets` | *Monitoring: Edit* in code |
| A router's logins | `GET sensors/{id}/mikrotik/logins` (`SensorMikrotikAccessController`) | *Monitoring: Edit* |
| *This wasn't expected* on a login | `POST cookie/access/logins/{id}/not-expected` (`CookieAccessController`) | *Monitoring: Edit* |
| Who may see an access case about a server or router | `CookieAccess.RightOf(LoginsArea)` | *Monitoring: Edit* |
| Who may be a contact for such a notice | `AccessNotices` (the contacts' check) | *Monitoring: Edit* |
| Whether Cookie reads logins at all | `AccessService` (`AccessAdminState.cookieReadsLogins`), `AccessJudge`'s trace line | *Monitoring: Edit* in Cookie's group |
| The read tools | `logins_list`, `access_profile`, `account_logins` (`AccessReadTools`); `server_get`'s sensitive sections (`OperationsReadTools.ServerAsync`, `canEdit`) | *Monitoring: Edit* |
| The web UI | The sensor page's *Access*, *Journal*, *SSH keys*, *Event log* tabs and a router's *Access* tab (`SensorView`, `canWrite`); `NotExpectedDialog`; the Access tab's and the Actions tab's texts | `can('Monitoring', 'Edit')` |

**What *Edit* rightly guards** and stays where it is: creating, changing and deleting hosts, sensors, rules, zones and mail rules; `CanSeeSecrets` for tokens and URLs; UDP keys and counters; a router's WireGuard keys, join and removal scripts, backups' contents, diffs and downloads, the backup settings; an agent's settings; attaching Zabbix hosts; the token lookup in the search.

### 1.3 What *Monitoring: Edit* in Cookie's group does and doesn't do

- **Cookie can't disable a sensor, whatever its group has.** Cookie's tools are the read tools and nothing else, by construction: `ToolCatalog.ForCookie` and `CookieTools.All` take only `ToolKind.Read` with `ForCookie`, and `CookieTools.RunAsync` refuses a name outside that list. The write tools (`sensor_update`, `host_update`, the deletes) are MCP's. Whatever Cookie changes, it changes through the executor's code paths: a note, a ticket, a mail, a mail rule's event, an access notice.
- **What *Edit* does give Cookie:** the logins' tools and sections of §1.2, and a router's backup diff. In a chat Cookie has the lower of its group's rights and the asker's, so none of that reaches a person who lacks it.
- **What it costs:** a standing exception to least privilege; a person put into Cookie's group by mistake has *Edit*; and two borrowed reasons, the note and the logins, neither of which is about editing.

### 1.4 Who writes what, after this plan

| | The people's notes | Cookie's notes |
|---|---|---|
| Written by | People, with *Monitoring: Edit*; MCP's `host_update` and `sensor_update` | Cookie's executor only |
| Read by | *Monitoring: View* | *Monitoring: View* **and** the note's areas |
| Removed by | Whoever edits them | *Monitoring: Edit*, one note at a time, audited |
| Counts as progress | Yes | No |
| Shown to Cookie as | Outside text, wrapped | Its own words, masked |

### 1.5 Why a place of its own for the notes, and a level for the logins

- **For the notes, a level wouldn't do.** A level that allows writing the people's notes would spare Cookie's group the rest of *Edit*, but it would leave everything else of §1.1 as it is: Cookie's lines in the people's field, the case book guessing which lines are whose, the areas of §9.2 ignored. **The notes belong to people. Cookie's text belongs to Cookie, in a place of its own.** Once it has one, writing there needs no right on the people's field at all.
- **For the logins, a level is exactly right.** Reading logins is reading. The question is only who may see names and addresses, and that is what a level between *View* and *Edit* says. *Zabbix* has the same step already: *View*, then ***Details*** for *inventory and acknowledgement messages, which name people*. *Monitoring* gets the same level with the same name and the same meaning (§4).

### 1.6 Pushover's priority is fixed in code

- **One place maps importance to priority:** `PushoverNotification.PriorityOf`: Low → −1 (quiet), Normal → 0, High → 1 (bypasses the user's quiet hours). Off with `Notifications__PushoverPriorityByImportance=false`, after which everything goes out at 0 (docs/ai-operations-plan.md §11.5). **Emergency (2) is never sent;** the debug page's request documents that it is left out because it needs acknowledgements. No `sound`, `retry` or `expire` is sent.
- **Every sender decides only the importance.** Notification rules carry the sensor's effective importance (`NotificationTask`). Cookie's outbox maps urgency to importance: urgent → High, everything else → Normal (`Outbox`), and Cookie's own Pushover action is allowed only for what is urgent, so a Pushover from Cookie is priority 1. The access watch's notices are urgent for *suspicious* and *alarming*, important for *unusual* (`AccessNotices`): 1 and 0. MCP's strike notice is High: 1. A person's contact test is Normal: 0. Nothing but a Low sensor ever produces −1.
- **So an admin who wants a different loudness changes code or .env,** and can't make High alerts repeat until acknowledged at all. The web UI's *Notification manager* (`/notifications`, *Matrix* and *Coverage*) manages who is told about what, but not how loud.

## 2. Requirements

| | |
|---|---|
| R1 | Cookie never writes into `Hosts.Notes` or `Sensors.Notes`. `PeoplesNotes` and `NoteLine` are deleted. No data is moved: the lines written until now stay as text in the people's notes on a development database, or the database is reset. |
| R2 | A note of Cookie's is a row: when, on what, the text, its case, its decision, its areas. The host's and the sensor's page show the notes a reader may see. A person with *Monitoring: Edit* removes one; the audit says so. |
| R3 | The executor asks for *Monitoring: View* in Cookie's group for a note. Nothing else about the action changes: *Auto* by default, not reviewed by Seraph, the day's limit, the decision log. |
| R4 | Cookie sees its latest notes on the object in the case brief and from `host_get` and `sensor_get`, as its own words, masked; MCP sees them within the token's rights and the notes' areas. |
| R5 | The case book hashes the people's notes as they are. |
| R6 | No more than a fixed number of Cookie's notes live on one object; above it, a proposal is held back and says why. |
| R7 | *Monitoring* has three levels: *View*, ***Details***, *Edit*. *Details* includes *View*; *Edit* includes *Details*. Everything of §1.2 moves to *Details*; everything *Edit* rightly guards stays. A group with *Edit* loses nothing; a group with *View* gains nothing. |
| R8 | Cookie's group does everything Cookie does with *Monitoring: Details* (and *Audit log: View* for PNMT's sign-ins): notes, the access watch, its tools. No text in PNMT tells an admin to give Cookie's group *Edit*. |
| R9 | The priority Pushover gets for Low, Normal and High is a setting, one row, read for every Pushover PNMT sends: rules, Cookie, the access watch, MCP's strikes. Each may be any of Pushover's five; emergency carries its retry and expiry. Readers of the Notification manager see it; *Monitoring: Edit* changes it; the audit records the change. The .env switch goes; the defaults are today's mapping. |

## 3. Cookie's notes

### 3.1 The table

**`cookie_object_notes`:** one row per note. An EF migration adds the table; there is no data step.

| Column | | |
|---|---|---|
| `Id` | `bigint`, identity | |
| `HostId` | `uuid?` → `hosts`, **on delete cascade** | Exactly one of `HostId` and `SensorId` is set (a check constraint) |
| `SensorId` | `uuid?` → `sensors`, **on delete cascade** | A sensor's note stays with the sensor when the sensor moves to another host |
| `At` | `timestamptz` | When Cookie wrote it |
| `Text` | `varchar(1500)` | As written: `ProposedAction.Text`'s limit, unmasked (names restored, as every text the executor writes). **Without** the `[Cookie, date]` prefix: the row has the date and the author |
| `CaseId` | `uuid?` → `cookie_cases`, on delete set null | The case it came from |
| `DecisionId` | `bigint?` → `cookie_decisions`, on delete set null | Its row in the decision log; the log is pruned after a year (`CookieLoop`), the note stays |
| `Areas` | `text[]` | The case's areas at the time (`CookieCaseEntity.Areas`), as a case note carries them (`CookieNoteAreas`) |

Indexes on `(HostId, At)` and `(SensorId, At)`. The cascade does what `HostService.DeleteAsync`'s and `SensorService.DeleteAsync`'s bulk deletes need; nothing to add there.

**Why rows and not a text column.** A column `Hosts.CookieNotes` would be the smallest change and would meet R1 and R3. It wouldn't meet R2: a column has no areas, so a note drawn from a mail would again be shown to everyone with *Monitoring: View* (§1.1); a person can't remove one note without editing Cookie's text; and there is no link to the case that explains the note. It wouldn't help R4 much either: Cookie would read back a growing blob. The rows cost one table and three endpoints.

### 3.2 The executor

- **The check** (`CookieExecutor`, the `ObjectNote` case): `Monitoring: View` instead of `Edit`; the message becomes *Cookie's group needs Monitoring: View for a note on a host or sensor.* The object must exist, as now. **New:** when the object has `MaxNotesPerObject` live notes already (R6, §3.5), the check refuses with *{name} has {n} notes of yours already; a person removes old ones. Don't propose another.*
- **The action** (`ObjectNoteAsync`): a row instead of the appended line: `HostId` or `SensorId`, `At = now`, `Text = body`, `CaseId = c?.Id`, `DecisionId = log.Id`, `Areas = c?.Areas ?? ["Monitoring"]`. The day's limit stays. The audit event `CookieNoteAdded` stays, on the host or sensor, with the text. `log.Detail` becomes *Noted on {name}.*
- **The decision log** keeps kind `object-note` and target `object:{id}`, unchanged, so the trace, the ratings and the lessons see nothing new.
- **`Reviewed()`** stays false for a note: it stays inside PNMT.
- **`NoteLine`** is deleted with `PeoplesNotes` (R1).

### 3.3 What Cookie sees

- **The case brief** (`CookieRound`'s brief of a problem case, and `AccessJudge.BriefAsync`): a new field **`YourNotes`**: the latest five of Cookie's notes on the case's host and on its sensors, each as `{ At, On, Text }`, masked with the case's pseudonyms, **not wrapped as untrusted:** they are Cookie's own words, as `YourLastNote` is. `HostNotes` stays what it is: the people's, wrapped.
- **`host_get` and `sensor_get`** get **`CookieNotes`**: the latest ten, `{ at, text, caseId }`. For Cookie, masked (`ctx.Privacy.Mask`) and not wrapped. For MCP, as they are, **filtered by the token's rights and each note's areas** (`CookieAccess.CanSee(ctx.Rights, note.Areas)`), as the case list filters cases. `hosts_search` and the sensors list don't carry them.
- **The acting paragraph** (`CookieBrain.ActingAsync`, docs/cookie-prompts.md): the condition of `object-note` becomes *on a host or sensor, shown on its page to the people who look after it; read your notes there first (host_get, sensor_get) and don't repeat one.* The `Condition` switch in `CookieBrain` carries it.

### 3.4 The case book

- `ProblemsAsync` hashes `host.Notes` and each sensor's `Notes` **as they are.** `PeoplesNotes` goes. On a development database whose notes still hold Cookie's old lines, the hashes change once; that registers as progress once on those cases and matters to nobody.
- Nothing is read from `cookie_object_notes` for progress: Cookie's notes are never progress (R5).
- The *As built* note of cookie-plan §21.8 (*Cookie's lines in a host's or sensor's notes aren't progress*) becomes *Cookie's notes are its own rows, never progress.*

### 3.5 Limits

- **`Limits.ObjectNotesPerDay`** stays (50 a day, Admin → Cookie → Actions).
- **New `Limits.MaxNotesPerObject`,** default **20**: live notes on one host or one sensor. Above it, the check refuses (§3.2). Nothing is removed by itself; a person removes.

### 3.6 The rights of a note

- **Reading:** the panel on the host's and the sensor's page needs *Monitoring: View*, the page's right. **Each note** is shown only to a reader who has its areas (`CookieAccess.CanSee(rights, note.Areas)`), as the case list and the case note are. A reader who may see none sees no panel. The areas of a note are the case's areas when it was written: for a problem case `Monitoring`, plus `Zabbix`, `Redmine`, `Mail`, `KnowledgeBase` as its sources add them (`CaseBook`). A note from a case Cookie worked with a ticket is for readers with *Redmine: View* too.
- **Writing:** Cookie, with *Monitoring: View* in its group: it must be able to see the object; its notes are its own, not the object's configuration.
- **Removing:** a person with ***Monitoring: Edit***: whoever looks after the host and tidies its page. Not *Cookie: Instruct*: removing a note tells Cookie nothing; it tidies the page. The audit gets **`CookieNoteRemoved`** (category Cookie, target the host or sensor, detail the note's text, who).

## 4. The level *Monitoring: Details*

### 4.1 The catalog

`RightsCatalog`'s *Monitoring* row becomes three levels. `Level.Details` exists already (Zabbix); only `Area.Monitoring`'s list changes. Levels are stored by name (`group_rights.Level`, a string), so no migration touches them.

| Level | Allows |
|---|---|
| **View** | As today: the dashboard, hosts, sensors, events, notification rules and the manager, DNS zones, mail input rules, the uplink, the search, and the hosts' and sensors' history. |
| **Details** | *View, plus what names people: servers' and routers' logins and the access cases and notices about them, a server's event log, journal and SSH keys.* |
| **Edit** | *Details, plus* create, change and delete them; sensor tokens and URLs; importance; notification assignments and the Pushover priorities; UDP keys and counters; a router's WireGuard keys and backups; an agent's settings; attaching Zabbix hosts. |

### 4.2 What moves to *Details*

| | Change |
|---|---|
| `GET agents/servers/{id}/logins`, `GET agents/servers/{id}/ssh-keys` | `[RequireRight(Area.Monitoring, Level.Details)]` |
| `GET sensors/{id}/mikrotik/logins` | The same |
| `POST cookie/access/logins/{id}/not-expected` | The same: whoever may see a login may say it wasn't expected (decision 7) |
| The Windows and Linux section endpoints | A new **`V1ControllerBase.CanSeeDetails`** (*Monitoring: Details*) is passed to `SectionAsync` and `LinuxSectionAsync` instead of `CanSeeSecrets`; the parameter is renamed `withDetails`. `CanSeeSecrets` stays *Edit* for the sensors' DTOs |
| `CookieAccess.RightOf(LoginsArea)` | `(Area.Monitoring, Level.Details)`; its summary says *Details* |
| `AccessNotices`'s contacts check | *Details* for a server or router target; `AuditLog: View` for PNMT as now |
| `AccessService`'s `AccessAdminState.cookieReadsLogins`, `AccessJudge`'s trace line | *Details* |
| `logins_list`, `access_profile`, `account_logins` | `Area.Monitoring, Level.Details` |
| `server_get` (`OperationsReadTools.ServerAsync`) | `canEdit` becomes `withDetails = ctx.Rights.Has(Area.Monitoring, Level.Details)` |
| `rights.ts` | `levelOrder.Monitoring = ['View', 'Details', 'Edit']`, so `session.can` ranks them right; the group page's table and *Your access* take the levels from the server's catalog already |
| `SensorView.vue` | A `canDetails = session.can('Monitoring', 'Details')` for the *Access*, *Journal*, *SSH keys*, *Event log* and a router's *Access* tabs; `canWrite` stays for *Commands* and the settings |
| `NotExpectedDialog.vue`, `RouterAccessTab.vue` | *Details* in their gates and comments |
| Texts | `accessAdmin.ts` (*Cookie's group lacks Monitoring: Details, so Cookie can't read servers' and routers' logins…*), `AccessTab.vue` (the contacts' rights), `AccessJudge`'s trace, `AccessReadTools`' summary |

### 4.3 What stays at *Edit*

Everything of §1.2's last paragraph: the writes, `CanSeeSecrets`, UDP keys and counters, a router's WireGuard keys, join and removal scripts, backups' contents, diffs (`router_backup_diff` too) and downloads, the backup settings, an agent's settings, attaching Zabbix hosts, the token lookup in `GlobalSearch.vue`.

### 4.4 MCP

- **The ceiling** (`McpRights.Ceiling`) stays *Monitoring: Edit*, which includes *Details*: a read-write token of a user with *Edit* reads logins as today; one of a user with *Details* reads them and can't write.
- **The read-only cap** (`McpRights.ReadOnlyCap`) goes from *View* to ***Details***: the cap's rule is *the reading levels only*, and *Details* is one, as Zabbix's *Details* is in the cap already. A read-only token of a user with *Details* or more reads logins; one of a user with *View* doesn't (decision 8). docs/cookie-access-plan.md §9 says so instead of *the read-only token is capped at View*.

### 4.5 Groups

- ***Operators*** have *Edit* and keep everything. ***Viewers*** have *View* and gain nothing. No group changes by itself.
- **Cookie's group:** *Monitoring: Details* is all Cookie needs in *Monitoring* (R8). The seed (`DevSeedCookie`) gives *Details* instead of *View*, so that the seeded Cookie writes notes and judges logins.
- **A new kind of group becomes possible:** people who look after access without editing hosts, with *Monitoring: Details*, *Cookie: Use* and, for PNMT's sign-ins, *Audit log: View*.
- **The Rights tab** (Admin → Cookie → Rights) gets a line under the chosen group's rights: *For notes Cookie needs Monitoring: View; for servers' and routers' logins Monitoring: Details; for PNMT's sign-ins Audit log: View. It never needs Monitoring: Edit.*

## 5. Pushover priorities as a setting

### 5.1 The setting

**`notification_settings`:** one row (`SingletonId = 1`, as `mikrotik_settings`), `[Audited]`, created by the migration with today's mapping.

| Column | Default | |
|---|---|---|
| `PushoverLow`, `PushoverNormal`, `PushoverHigh` | −1, 0, 1 | Each −2 to 2: the priority a message of that importance gets |
| `EmergencyRetrySeconds` | 60 | Pushover's `retry`, 30 to 3600: how often the phone repeats an emergency until it is acknowledged |
| `EmergencyExpireSeconds` | 3600 | Pushover's `expire`, at least the retry, at most 10800: when it stops repeating |
| `UpdatedAt`, `UpdatedBy` | | |

**The .env switch `Notifications__PushoverPriorityByImportance` goes.** *Off* is the three priorities set to 0. The system isn't in production, so no installation has to carry the switch's value over; docs/ai-operations-plan.md §11.5 and its .env table are amended.

### 5.2 Where it is read

- **A singleton `PushoverPriorities`** (in `PNMTD/Notifications/`) holds the row, read once from the database (`IDbContextFactory<PnmtdDbContext>`) and dropped when the settings are saved, as `dashboardCache.Invalidate()` does for the dashboard. Every provider is a singleton too, so this is the shape they already have.
- **`PushoverNotification` resolves the priority** when it sends: `message.Priority ?? settings.Of(message.Importance)`. The provider is the one place every Pushover goes through, the debug page's test included, so one place decides for rules, Cookie's outbox, the access watch and MCP's strikes alike. `PriorityOf` is gone.
- **Emergency:** when the priority is 2, the provider adds `retry` and `expire` from the settings. Pushover answers with a receipt; PNMT doesn't read it (decision 13): the phone repeats until the person acknowledges or the expiry passes.
- **The debug page's test** (`DebugPushoverRequest.Priority`) takes −2 to 2; for 2 the settings' retry and expiry go with it, so an admin can hear what an emergency does before mapping High to it. The field's remark about emergency being left out goes.
- **Who sends at which importance doesn't change** (§1.6): rules the sensor's, Cookie urgent → High and else Normal, the access watch suspicious and alarming → High and unusual → Normal, MCP's strikes High, tests Normal.

### 5.3 The API

| Endpoint | Right | |
|---|---|---|
| `GET /api/v1/notifications/settings` | *Monitoring: View* | `NotificationSettings { pushoverLow, pushoverNormal, pushoverHigh, emergencyRetrySeconds, emergencyExpireSeconds, changedAt, changedBy }` |
| `PUT /api/v1/notifications/settings` | *Monitoring: Edit* | `NotificationSettingsRequest` with the five values; `[Audit(NotificationSettingsChanged)]`; 400 with the field for a priority outside −2 to 2, a retry outside 30 to 3600, an expiry below the retry or above 10800 |

On `NotificationsController`, next to *matrix*, *coverage* and *assign*, with their rights: the manager's readers see it, those who assign rules change it. The audit line names what changed: *Pushover: High 1 → 2, retry 60 s, expire 3600 s.*

### 5.4 The web UI

**A third tab on the Notification manager,** `/notifications?tab=pushover`, ***Pushover***, next to *Matrix* and *Coverage*:

- **A table with three rows, Low, Normal and High,** each with a select of Pushover's five priorities, labelled with what they do: *Lowest (−2): no notification, only the badge* · *Low (−1): quiet, no sound* · *Normal (0)* · *High (1): bypasses the person's quiet hours* · *Emergency (2): repeats until acknowledged*. Read-only without *Monitoring: Edit*.
- **The emergency fields,** *Repeat every* (seconds) and *Give up after* (seconds), shown as soon as any row is *Emergency*, with Pushover's limits as their hints.
- **Who sends what,** under the table, as a short list from §1.6, so that the reader knows what a row reaches: *Notification rules: the sensor's importance. Cookie: urgent is High, the rest Normal. The access watch: suspicious and alarming are High, unusual is Normal. MCP's strike notices: High.*
- **Save** with the audit; *Changed {when} by {who}* beneath. A link *Send a test Pushover* to Admin → Debug for those with *System: Manage*.
- **`Admin → System health`'s *Notifications* panel** gets a second link, *Pushover ›*, next to *Coverage ›*.

### 5.5 What this doesn't change

E-mail's `[High]` subject prefix (ai-operations-plan §11.5), the webhooks, Cookie's hop from urgency to importance (`Outbox`), the access watch's rows that choose e-mail, Pushover or both per verdict (`AccessSettings`), the people's quiet hours in the availability plan. A row per sender instead of per importance is decision 12.

## 6. The API for Cookie's notes and the moved endpoints

**New:**

| Endpoint | Right | |
|---|---|---|
| `GET /api/v1/hosts/{id:guid}/cookie-notes` | *Monitoring: View* | The host's notes of Cookie's the reader may see, newest first: `CookieObjectNoteItem[]` |
| `GET /api/v1/sensors/{id:guid}/cookie-notes` | *Monitoring: View* | The sensor's, the same |
| `DELETE /api/v1/cookie/object-notes/{id:long}` | *Monitoring: Edit* | Removes one; `[Audit(CookieNoteRemoved)]`; 404 for one the reader may not see |

**`CookieObjectNoteItem`:** `Id`, `At`, `Text`, `CaseId`, `CaseTitle` (null when the case is gone or the reader lacks *Cookie: Use*), `Areas`. The host's and the sensor's detail DTOs don't change; the panel loads its list.

**`HostsController` and `SensorsController`** get the two reads, delegating to a `CookieObjectNotes` service in `PNMTD/Cookie/` that also serves the executor, the brief and the tools; **`CookieController`** gets the delete.

**Moved** (§4.2): the four endpoints to *Details*. **`EndpointAuditTests`' matrix** gets the three new rows here, the two of §5.3, and the four changed ones (`"Monitoring:Details", Roles.User`). `openapi.json` is regenerated and `gen:api` run (`tools/npm.sh`, node in the container).

## 7. The web UI for Cookie's notes and the level

- **`CookieNotesPanel.vue`** (in `views/cookie/`, used by both pages): a panel titled *Cookie's notes*; one row per note, newest first: the date, the text, *from a case* as a link to the case for readers with *Cookie: Use*, and *Remove* for *Monitoring: Edit*, with a confirm (*Remove Cookie's note of 15 Nov?*). **Hidden when the list is empty.** Loaded by `useQuery` on `keys.cookieNotes('host', id)`; removing invalidates it.
- **The host page:** below *Notes* in the properties panel's column. **The sensor page:** below `SensorNotesPanel`, in every place that panel appears (the overview and the agents' `#notes` slots).
- **The sensor page's tabs** by `canDetails` (§4.2).
- **Admin → Cookie → Actions:** the `objectNote` text becomes *Shown on the host's or sensor's page as Cookie's own note, with the date; stays inside PNMT, so Seraph doesn't review it. Cookie's group needs Monitoring: View.* The limits form gets *Notes on one host or sensor* (`maxNotesPerObject`).
- **Admin → Cookie → Rights and Access:** the texts of §4.2 and §4.5.
- **`resources.ts`:** `api.hosts.cookieNotes(id)`, `api.sensors.cookieNotes(id)`, `api.cookie.removeObjectNote(id)`, `api.notifications.settings()`, `api.notifications.updateSettings(request)`.

## 8. Tests

| | |
|---|---|
| `GroupsTests` | *Details* includes *View* and not *Edit*; *Edit* includes *Details*; `Combine` ranks the three; a group with `Monitoring = "Details"` is created and shown; the audit line says *View → Details*. |
| `EndpointAuditTests` | The matrix: five new rows, four changed to `Monitoring:Details`. The generic cases do the rest: a user with every right but *Monitoring* gets 403 on them, one with exactly *Details* gets through. |
| Agents (`Windows`, `Linux`) | The *access*, *event-log*, *journal* and *ssh-keys* sections come back for *Details* and are refused for *View*; the other sections for *View*. |
| `McpToolsTests` | The read-only cap gives *Details* to a user with *Edit* and *View* to one with *View*; `logins_list` is offered to a read-only token of the first and not of the second; `server_get`'s sensitive sections likewise. |
| `AccessWatchTests` | A reader with *Details* sees an access case about a server; one with *View* doesn't; a contact with *Details* gets the notice. Cookie's group with *Details* judges a case; with *View* the trace says it can't, naming *Details*. |
| `CookieActingTests` | A note becomes a row on the host (and on a sensor), with the case's areas and decision id; `Hosts.Notes` is untouched. With *Monitoring: View* only, the note goes through; without *Monitoring*, it is refused with the new message. The day's limit holds back the 51st; the object's cap refuses the 21st with its message. `Reviewed(ObjectNote)` is still false. |
| `CaseBookTests` | A person's change to the notes is progress; a note of Cookie's on the host or a sensor isn't. |
| Rights of a note | A note with `["Monitoring", "Mail"]` isn't in the list for a reader with *Monitoring: View* alone, and is for one with *Mail: Read* too; its delete is 404 for the first. |
| Tools (`McpToolsTests`, `CookieLoopTests`) | `host_get` carries `cookieNotes` for Cookie masked and unwrapped; for an MCP token without *Mail: Read*, the mail-sourced note is left out. |
| Notifications | With the fake Pushover endpoint (`Pushover:Endpoint`): a High event on a rule arrives with priority 1 by default; after `PUT notifications/settings` sets High to 2, the next one arrives with 2, `retry` and `expire`, without a restart; a Low one with −1; Cookie's urgent outbox row with the High row's priority. The PUT refuses a priority of 3, a retry of 10, an expiry below the retry; the audit line names the change. The debug test sends 2 with the settings' retry and expiry. |
| Web UI (vitest) | `holds()` with the three levels (`guard.test.ts`); the panel hides on an empty list, shows *Remove* only with *Monitoring: Edit*, and links the case only with *Cookie: Use*; the sensor page's tabs for *Details* without *Edit*; the Pushover tab shows the emergency fields only when a row is *Emergency*, and is read-only without *Monitoring: Edit*. |

## 9. Decisions and open questions

| # | Question | Decision |
|---|---|---|
| 1 | A column on hosts and sensors, or rows? | **Rows** (§3.1): the areas, the removal, the case link. A column is the fallback if the table is judged too much |
| 2 | Who removes a note? | ***Monitoring: Edit*** (§3.6). *Cookie: Instruct* was considered: removing tidies the page, it doesn't instruct |
| 3 | What right does Cookie need for a note? | ***Monitoring: View*** (§3.6): it must see the object. No right on the people's field |
| 4 | Move the lines Cookie wrote until now? | **No.** The system isn't in production. `PeoplesNotes` and `NoteLine` are deleted; a development database keeps the old lines as plain text or is reset |
| 5 | Should Cookie edit or replace its own note? | **Not now.** It reads its notes first and doesn't repeat (§3.3); a person removes stale ones. Replacing would need an id in the proposal and a check that it is Cookie's own |
| 6 | Cap per object | **20**, a setting (§3.5); above it the proposal is refused, nothing is deleted by itself |
| 7 | *This wasn't expected* at *Details* or *Edit*? | ***Details***: it is the judgement of someone who may see the login; it changes no host or sensor. It teaches Cookie, which is the point |
| 8 | The read-only MCP token: *View* or *Details*? | ***Details*** (§4.4): the cap is the reading levels, and Zabbix's *Details* is in it already. A token never has more than its user |
| 9 | A level in *Monitoring*, or an area *Logins*? | **A level.** An area would make *Edit* stop implying the logins, so every group with *Edit* would need the new area added, and *Operators* would have to be changed by an update; a level keeps *Edit* whole. The one thing an area could do that a level can't, giving someone logins without the dashboard, isn't wanted |
| 10 | The level's name | ***Details***, reused from Zabbix, where it means the same: *which name people*. A new name (*Logins*, *People*) would say less about the event log and the SSH keys |
| 11 | Where the Pushover priorities live, and whose right | **A tab on the Notification manager** (§5.4), *Monitoring: View* to see, ***Monitoring: Edit*** to change: the people who assign rules decide how loud they are, and the manager is where alerting is managed. Considered: a page under *Admin* with *System: Manage*, next to *Debug*; rejected because it would put the loudness of alerts with the admins and the rest of alerting with the operators |
| 12 | Priorities per importance, or per sender? | **Per importance** (§5.1): one mapping, three rows, which every sender already feeds. Per sender (Cookie, the access watch, MCP, rules) would let *alarming login* be an emergency while a High sensor isn't; it needs a kind on `NotificationMessage` and a row per kind. Open: it can come later without undoing this; the three rows stay as the default |
| 13 | Emergency receipts | **Not read.** Pushover repeats on the phone until acknowledged or expired, which is the point of the level; PNMT learning who acknowledged would need the receipts API and a place to show it. Open |
| 14 | Keep the .env switch? | **No** (§5.1). One source of truth; *off* is three zeros |

## 10. Build order

| | | |
|---|---|---|
| **N1** | **The level:** the catalog row, `levelOrder`, `CanSeeDetails`, the four endpoints, the section gates, the tools, `CookieAccess`, `AccessNotices`, `AccessService`, `AccessJudge`'s line, the MCP read-only cap, the seed, the UI's gates and texts, `GroupsTests`, the matrix, the agents', MCP's and the access watch's tests. docs/audit-redmine-zabbix-plan.md §5.2, docs/cookie-access-plan.md §2, §5.2, §6.4, §9, §10, docs/mcp-plan.md §7.2 amended | server, web UI, docs |
| **N2** | **The Pushover priorities:** the row and its migration, `PushoverPriorities`, `NotificationService` resolving the priority, the provider's `retry` and `expire`, the two endpoints and their matrix rows, the audit event, the debug test's range, the tab, the System health link, the notification tests, docs/ai-operations-plan.md §8.2, §11.5 and the .env table | server, web UI, docs |
| **N3** | **The table and the executor:** entity, migration, `CookieObjectNotes` service, the check with *View* and the cap, the row instead of the line, `PeoplesNotes` and `NoteLine` deleted, the settings' `MaxNotesPerObject`, `CookieActingTests`, `CaseBookTests` | server |
| **N4** | **What Cookie sees:** `YourNotes` in the briefs, `cookieNotes` in `host_get` and `sensor_get` with the areas filter for MCP, the acting paragraph, docs/cookie-prompts.md | server |
| **N5** | **The API and the UI for the notes:** the three endpoints, the matrix rows, `openapi.json` and `gen:api`, `CookieNotesPanel`, the two pages, the Actions tab's text and limit, the Rights tab's line | server, web UI |
| **N6** | **The docs:** cookie-plan §21.8, the release notes | docs |

N1 and N2 stand alone and can ship in either order. Each step leaves master working: after N3 Cookie writes rows that the pages don't show yet.

## 11. As built (2026-11-16)

Built as planned, with these differences:

- **The Pushover priority is resolved in the provider** (`PushoverNotification`), not in `NotificationService` (§5.2): the provider is the one path every Pushover takes, the debug page's test included. `PushoverPriorities` is the singleton that holds the row and is dropped on a save.
- **The settings' DTO is flat** (§5.3): five values, `changedAt`, `changedBy`.
- **The tab's logic is in `pushoverPriorities.ts`** (the five priorities, the limits, the draft and its checks), tested with Vitest; `PushoverTab.vue` is the form.
- **The notes' page side is `CookieObjectNoteService`,** next to the static `CookieObjectNotes` helpers the executor, the briefs and the tools use (§3). The panel is `CookieNotesPanel.vue` with the composable `useCookieNotes` in `views/cookie/`.
- **The executor saves the decision log's row before the note,** because the note names its decision (§3.2).
- **The seed's Cookie group has *Monitoring: Details*** (§4.5).
- **Migrations:** `NotificationSettings` (the one row, created on the first save; the defaults until then) and `CookieObjectNotes`.
- **The release notes:** docs/release-notes-1.10.md.
