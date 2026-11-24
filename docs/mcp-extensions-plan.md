# Plan: MCP extensions

| | |
|---|---|
| Status | **Built** (§15). Released in 1.25.0. Revision 3 |
| Date | 2026-11-24 |
| Revision | 3: **the answers** (§14): *Alert* allowed; **no limit of its own** for board messages, only the existing ones; Redmine combinations under *Edit*.<br>2: **the items chosen** from revision 1's menu (A1, A2, A4–A8, B1–B3, C2, D1, E1, E3, G4–G6, H1, H2, H4), **plus creating and editing responsibilities** (R1); the rest of the menu dropped.<br>1: a menu of what MCP was missing, from the code at `4110024` (1.24.1) |
| Scope | **30 new tools and 4 extended ones,** 95 → 125:<ul><li>**sensors:** MikroTik routers, and each type's own settings for SSL, Zabbix and UDP sensors; what the types and codes mean; importance in bulk;</li><li>**routers:** update jobs, changelogs, join-script differences, one's commands;</li><li>**reading:** OS updates, notification coverage, the knowledge base's recent changes, Advanced reports and rule tests, certificate history;</li><li>**writing:** likes and uses of pages, Advanced rules and fields, **responsibilities**, **board messages**;</li><li>**reading boards and the lounge**</li></ul> |
| Not in scope | Everything else on revision 1's menu. What mcp-plan §7.7 keeps out stays out (§11) |
| Reverses | **Eight earlier decisions** (§10): mcp-plan §7.3/§7.7 (routers, a direct-SSH join script), linux-os-updates-plan (reading), knowledge-base-extensions-plan (likes and uses), advanced-sensor-plan (rule writing), boards-plan and board-messages-plan (boards in the ceiling, messages from outside), lounge-plan D15 (reading), responsibilities-plan decisions 10, 11 and R9 (editing over MCP, a new level) |
| References | **docs/mcp-plan.md:** §5.3 (the ceiling), §6.4 (secrets), §7 (the tools), §7.5 (deleting). **`Mcp/McpRights.cs`, `Tools/ToolCatalog.cs`, `Tools/Write/MonitoringWriteTools.cs`.** The feature plans named per item |
| Target | 1.25.0 |

---

## 1. Summary

| # | Tools | Kind | Right | Size |
|---|---|---|---|---|
| A1 | `sensor_create` *(ext)*: type **MIKROTIK**, with a `mikrotik` object | W | Monitoring Edit | M |
| A2 | `router_join_script` *(new)*: direct SSH only | R | Monitoring Edit | S |
| A4 | `sensor_create`, `sensor_update` *(ext)*: a `certificate` object | W | Monitoring Edit, Certificates Edit | S |
| A5 | `sensor_update` *(ext)*: a `zabbix` object | W | Monitoring Edit, Zabbix View | S |
| A6 | `sensor_create`, `sensor_update` *(ext)*: a `udp` object | W | Monitoring Edit | S |
| A7 | `sensor_types`, `status_codes` *(new)* | R | Monitoring View | S |
| A8 | `sensors_set_importance` *(new)* | W | Monitoring Edit | S |
| B1 | `router_jobs`, `router_changelog` *(new)* | R | Monitoring View | S |
| B2 | `router_get` *(ext)*: its differences; `router_difference_acknowledge` *(new)* | R, W | Monitoring View, Edit | S |
| B3 | `mikrotik_commands_list`, `mikrotik_command_cancel` *(new)* | R, A | Commands Run | S |
| C2 | `os_updates_get` *(new)* | R | Monitoring View | S |
| D1 | `notification_coverage` *(new)* | R | Monitoring View | S |
| E1 | `kb_changes` *(new)* | R | KB View | S |
| E3 | `kb_page_used`, `kb_page_like` *(new)* | W | KB View | S |
| G4 | `advanced_reports`, `advanced_test` *(new)* | R | Monitoring View (Details for data that names people) | S |
| G5 | `advanced_rules_change`, `advanced_fields_change` *(new)* | W | Monitoring Edit | M |
| G6 | `certificate_history`, `certificate_authorities` *(new)* | R | Certificates View | S |
| H1 | `boards_list`, `board_get` *(new)* | R | Boards View | S |
| H2 | `board_message_send`, `board_message_withdraw` *(new)* | W | Boards Message | S |
| H4 | `lounge_read` *(new)* | R | none | S |
| R1 | `responsibility_create`, `responsibility_update`, `responsibility_link`, `responsibility_redmine`, `responsibility_options` *(new)*; `responsibility_get` *(ext)* | W, R | **Responsibilities Edit (a new level)** | M |

**The read-only token** gets 14 of the new tools: `sensor_types`, `status_codes`, `router_jobs`, `router_changelog`, `os_updates_get`, `notification_coverage`, `kb_changes`, `advanced_reports`, `advanced_test`, `certificate_history`, `certificate_authorities`, `boards_list`, `board_get`, `lounge_read`, within its cap. **Cookie gets none** (`ForCookie` stays off), so its prompt doesn't grow.

**Every new tool** goes through the existing service of its endpoint, writes the endpoint's audit events, returns no secret, and is covered by the canary test (`McpToolsTests`). Admin → MCP's documentation shows it by itself.

---

## 2. Rights: what changes

| Area | Ceiling (read-write) | Read-only cap | Why |
|---|---|---|---|
| **Boards** | **Message** *(new)* | **View** *(new)* | H1, H2. The area was left out (boards-plan l.886) |
| **Responsibilities** | **Edit** (was View) | View | R1. **The area gets a level *Edit* between *View* and *Manage*** (§8.1) |
| Lounge | — | — | H4: reading the lounge needs no right in PNMT, so the tool has no area. Writing (*Lounge: Use*) stays out |

**Still no *Manage* anywhere in the ceiling** (mcp-plan §5.3; `McpToolsTests` checks it). That is why R1 needs a new level rather than *Manage*.

---

## 3. Sensors

**Today** `sensor_create` takes only the types in `MonitoringWriteTools.CreatableTypes`, and only the fields all types share. Each type's own settings (`SensorRequest.Mikrotik`, `.Certificate`, `.Zabbix`, `.Udp`) are left out. So a sensor gets PNMT's defaults, and `sensor_update` can never change them.

**The shape:** `sensor_create` and `sensor_update` take **one optional object per type**, named as in `SensorRequest`. An object for another type is `invalid_argument`. **On update, left-out fields keep their value:** the tool reads the current settings and merges, as `host_update` does.

### 3.1 A1: MikroTik routers

- **`MIKROTIK` joins `CreatableTypes`.** The object `mikrotik`:

| Field | What |
|---|---|
| `mode` | `WireGuardSsh` (default), `WireGuardOnly`, `DirectSsh`. The WireGuard modes need PNMT's WireGuard network to be set up; the service says so if it isn't |
| `address`, `sshPort` | Direct SSH: a name or address PNMTD can reach; 22 when left out |
| `sourceAddress` | Direct SSH: PNMTD's address as the router sees it, the only one the `pnmt` user may log in from. Empty: from anywhere |
| `backupIntervalHours` | 1 to 8760, or `null` for off. Left out: the installation's default, as the UI does (`MikrotikBackupSettingsService`) |

- **`intervalSeconds`** is at least `RouterPoller.MinimumIntervalSeconds` (60), as the service checks.
- **Never over MCP:** management access, secrets in exports, diagnostics, encrypted backups. They are an admin's: `MikrotikRouterService` refuses them below *MikroTik: Manage* (l.72-77), and the token never has it. A new router gets the installation's defaults for them.
- **The answer** has the sensor and its setup state, plus the next step:
  - direct SSH: *"Fetch the join script with `router_join_script` and paste it into the router's terminal"*;
  - WireGuard: *"The join script is on the sensor's page in PNMT: it holds the router's private key."*
- **`sensor_update` keeps a router's settings as they are.** Changing them over MCP wasn't chosen.

### 3.2 A2: `router_join_script`

- **Direct SSH only.** That script holds PNMT's public key, and the router makes its own random password (`JoinScript.cs:234`). Nothing in it is secret.
- **The WireGuard modes answer `not_available`:** *"This router's join script holds its WireGuard private key: it is on the sensor's page in PNMT."* That keeps mcp-plan §7.7's rule for every script that holds a key.
- **Parameters:** `firewall` and `removals`, as the join options (docs/mikrotik-join-options-plan.md §2). Left out, the router's own are used.
- **Recorded as the UI's view is.**
- **The canary test** covers it: a WireGuard router's script never comes back, and no answer contains `private-key=`.

### 3.3 A4: SSL certificate settings (`certificate`)

- `trust`: **`Public` or `Private`** (with `authorityIds` from `certificate_authorities`, G6). **`Pinned` stays in PNMT:** it trusts the certificate a person accepted, and accepting is no tool (ssl-certificate-plan l.530).
- `checkName`, `checkExpiry`, `incompleteChainFails`, `serverName` (SNI), `connectTo`, `warnDays` (1 to 365).
- `alertOnCertificateChange`, `alertOnKeyChange`, `alertOnIssuerChange`, `issuerMatch` (`Organization`, `Exact`).
- **Needs *Certificates: Edit*** besides *Monitoring: Edit*, as creating one does today.

### 3.4 A5: Zabbix sensor settings (`zabbix`, update only)

- `minSeverity`, `countAcknowledged`, `countSuppressed`, `unavailableFails`.
- **The Zabbix host stays.** Another host means another `zabbix_attach`.

### 3.5 A6: UDP node settings (`udp`)

- `deviceId`, `allowedSuites`, `sessionMode`, `ackMode`, `replayProtection`, `responseSuite`.
- **`keyFamily` at creation only.** A new family makes a new key (`UdpNodeService` l.123-126), which cuts the device off as rotating does. On update it is `invalid_argument`: *"A new key family means a new key: change it on the sensor's page in PNMT."*
- **Never `key` or `token`.** A new node's key is made by PNMT and is on its page.

### 3.6 A7: `sensor_types`, `status_codes`

- **`sensor_types`:** for each type, what it does, what its `parameters` mean (a ping target, a regular expression, `"daily 8"`, `host:port`), whether it uses the interval, and **whether MCP may create it**. Source: `SystemService.SensorTypes()`, with `CanCreate` from `CreatableTypes`.
- **`status_codes`:** every code with its name, success or failure, and its meaning (`StatusCodes()`). This is what mail input rules' codes and value checks need.
- **`sensor_create`'s description points to `sensor_types`** instead of *"as in PNMT's form"*.

### 3.7 A8: `sensors_set_importance`

- Up to 100 sensors to an importance, or back to the host's (`hostImportance: true`). Same as `hosts_set_importance`.
- Through `PUT sensors/importance`'s service, audit event `SensorsImportanceChanged`.

---

## 4. MikroTik routers

| Tool | What |
|---|---|
| **`router_jobs`** (B1) | A router's update jobs, newest first, by state, paged; with `jobId`, one job with its log. Only updates, as the router's page shows (commands are B3's) |
| **`router_changelog`** (B1) | A RouterOS release's changelog from MikroTik's update server, by default the newest of the router's channel. **Untrusted text** (`ctx.Untrusted`), `openWorld` |
| **`router_get`** *(ext)* (B2) | Gets `differences`: each difference between the router and its join script, with its id, its text, whether it is an admin's, and its acknowledgement (who, why, when) |
| **`router_difference_acknowledge`** (B2) | `acknowledge` (ids and a **required reason**), or `end` (one id). **The admin's differences** (policies, the management rule, retired keys) **are refused by the service** (`MikrotikRouterService` l.585, 614). Events `MikrotikDifferenceAcknowledged`, `MikrotikDifferenceAcknowledgementEnded` |
| **`mikrotik_commands_list`** (B3) | Commands and batches, newest first, by router and state, paged, as *Commands: Run* lists them. The admin's runs stay hidden, as in the UI |
| **`mikrotik_command_cancel`** (B3) | A queued or running command. **Only the token user's own:** MCP is never an admin. Event `MikrotikCommandCancelled`. No delete window: cancelling isn't deleting (mcp-plan §7.5) |

---

## 5. Reading

| Tool | What | Notes |
|---|---|---|
| **`os_updates_get`** (C2) | **Without `sensorId`:** the summary across Linux servers (`OsUpdatesService.SummaryAsync`). **With it:** pending packages, high-risk findings and their acknowledgements, the runs. **With `runId`:** a run and its log, in parts (`logFrom`). **With `package`:** its changelog | `details` follows *Monitoring: Details*, as the endpoint does. **apt's output and changelogs are untrusted** (`ToolSafety.Scanned`). **Starting a run, dismissing and acknowledging stay in PNMT** |
| **`notification_coverage`** (D1) | The figures (covered, uncovered by importance), the sensors no enabled rule reaches, and with `hostId`, the manager's matrix for that host | **Whom a rule reaches is masked**, as `notification_rules_list` does it (*a user*, *a group (4 members)*, *the responsible*; notification-recipients-plan R9) |
| **`kb_changes`** (E1) | The knowledge base's recent changes: page, who, when, what kind | Through `MachineAccess`: **sensitive pages left out** |
| **`advanced_reports`** (G4) | An Advanced sensor's reports, newest first, paged; with `reportId`, one report (at most 8,000 characters, as `advanced_sensor_get`) | A report whose data names people only with *Monitoring: Details*. The app's text is untrusted |
| **`advanced_test`** (G4) | Runs the sensor's rules, or **draft rules**, against a stored report or a JSON sample, and gives each rule's outcome. **Stores and notifies nothing** (`[NotAudited]` on the endpoint) | Draft rules take the same fields as G5 |
| **`certificate_history`** (G6) | The certificates a sensor has seen, newest first; with `versionId`, one with its facts; with `pem: true`, its PEM | Certificates are public. Texts from the server are untrusted, as `certificate_get` treats them |
| **`certificate_authorities`** (G6) | The private CAs: name, subject, validity, fingerprint; with `pem: true`, the PEM | Their ids are what `authorityIds` takes (A4) |

---

## 6. E3: likes and uses of pages

- **`kb_page_used`:** *"I used this page to solve a problem"*, with what it solved (at most 200 characters). **`kb_page_like`:** `liked` true or false.
- **Only pages `MachineAccess` lets the token read.**
- **Recorded as the token's user**, as in the UI. The MCP log has the call.
- **The endpoints are `[NotAudited]`** (*"a reaction, not an operation"*). But `McpToolsTests` requires every write tool to declare audit events (l.79). So **`ToolSpec` gets `NotAudited`, a reason taken from the endpoint,** and the test accepts a write with either events or that reason.

---

## 7. G5: Advanced rules and fields

- **`advanced_rules_change`:** `create`, `update` (by id), `remove` (by id), `order` (all ids), `copy` (from another sensor, `replace` or append). Rule fields as `AdvancedRuleRequest`: name, enabled, note, path, operator, operand(s), ignore case, quantifier, when missing, severity.
- **`advanced_fields_change`:** `create`, `update`, `remove`, `order`. Field fields as `AdvancedFieldRequest`: name, path, format, unit, decimals, pinned, chart.
- **No review step.** The advanced-sensor-plan wanted one for rules proposed by an AI (l.980). Over MCP **the token acts for its user, who holds *Monitoring: Edit***, as for every other write. In its place:
  - **every change first runs the rules against the latest report** (`TestAsync`), and the answer carries each rule's outcome. So the AI and the person see at once what the sensor will say;
  - the audit entry `AdvancedRulesChanged` / `AdvancedFieldsChanged` holds the rules before and after, with `Actor = Mcp`.
- **`remove` is no delete in mcp-plan §7.5's sense:** it is configuration, kept in the audit, and made again with `create`. No window.

---

## 8. R1: creating and editing responsibilities

### 8.1 A new level, *Edit*

**Today the area has *View* and *Manage*, and every change needs *Manage*,** which never reaches MCP. So the area becomes **View, Edit, Manage**:

| Level | Allows |
|---|---|
| View | As today |
| ***Edit*** *(new)* | **View, plus creating and changing responsibilities:** name, short and long description, responsible and deputy, links, Redmine combinations; restoring a description from the history |
| Manage | **Edit, plus deleting responsibilities and setting the global responsible and deputy** |

- **Nobody loses anything.** Rights are stored by name (`GroupRightEntity.Level`, a string), and *Manage* includes *Edit*. **No migration.** Admins grant *Edit* to whoever should edit without deleting.
- **The endpoints** for create, change, description, links, Redmine combinations, restore, `people`, `targets` and `redmine` (the lookups) move from *Manage* to *Edit*. Delete and `global` stay *Manage*.
- **The UI:** `api/rights.ts` gets the level. The page shows the buttons by *Edit*, and *Delete* and the global people by *Manage*.
- **`McpRights.Ceiling[Responsibilities] = Edit`**; the read-only cap stays *View*.
- **Cookie stays read-only** for responsibilities (decision 11 holds for Cookie).

### 8.2 The tools

| Tool | What | Event |
|---|---|---|
| **`responsibility_create`** | Name, short description, responsible, deputy | `ResponsibilityCreated` |
| **`responsibility_update`** | A patch of the same fields (`clearResponsible`, `clearDeputy`). **With `description` and `baseRevision`, the long description** in Markdown: `edit_conflict` with the current revision if someone was faster, as `kb_page_update` | `ResponsibilityChanged` |
| **`responsibility_link`** | `link` or `unlink` a page, a location or a host. **A page the token can't read (`MachineAccess`) is `not_found`**, so a sensitive page is never linked or revealed. `already_linked`, `too_many_links` as the endpoint | `ResponsibilityLinked`, `ResponsibilityUnlinked` |
| **`responsibility_redmine`** | `add`, `update` or `remove` a Redmine combination: project, tracker (or every tracker), the user who handles it, a note. `redmine_not_configured`, `duplicate_combination` as the endpoint | `ResponsibilityRedmineSaved`, `ResponsibilityRedmineDeleted` |
| **`responsibility_options`** | **The active users a responsibility can name** (name, username, whether a Redmine user is linked; **never contact details**), and Redmine's projects and trackers PNMT reads | — (read, *Edit*) |
| **`responsibility_get`** *(ext)* | Gets the description's revision, for `baseRevision` | — |

- **Credentials in a description are refused** (`looks_like_credential`). **Over MCP they are never acknowledged**, as for pages.
- **A change made over MCP says *via MCP* in the responsibility's history**, as a page's revision does (`kb_revisions.Via`). `responsibility_history` gets the same nullable `Via` column.
- **Not over MCP:** deleting a responsibility, and the global responsible and deputy (*Manage*).

---

## 9. Boards and the lounge

### 9.1 H1: `boards_list`, `board_get`

- **The boards, their elements** (kind, title, what each points to: a location, hosts, sensors, a responsibility), **the status bar and whether it takes messages.**
- **Never a board's URL.** The URL is the board's access (boards-plan §13.1). Never its networks either.
- **Not the data the elements show.** A board shows to whoever holds its URL what its elements show, without the viewer's rights. Over MCP the AI reads that data with its own tools and rights.

### 9.2 H2: `board_message_send`, `board_message_withdraw`

- **`board_message_send`:** boards (ids, or `allBoards`), kind (`Alert`, `Warning`, `Maintenance`, `Information`, `Success`), title, text, seconds. The same limits as Admin → Debug's panel (`BoardMessageService`: at most 10 open on a board, its duration limits).
- **Sent as the token's user, via MCP:**
  - `BoardMessageSource` gets `Mcp`. A board names the sender as it names people; Admin → Debug's list says *via MCP*;
  - the audit entry `BoardMessageSent` has `Actor = Mcp`.
- **No approval.** Cookie's messages need a person's approval because Cookie decides on its own. **A token acts for its person,** who holds *Boards: Message*.
- **No limit of its own** (decided, §14): the existing ones hold. MCP's own call limits per token (`McpLimits`), at most 10 open messages on a board, and the longest duration (`Boards__MessageMaxMinutes`).
- **`board_message_withdraw`:** an open message. **Only one the token's user sent,** not a colleague's or Cookie's. Event `BoardMessageWithdrawn`.

### 9.3 H4: `lounge_read`

- **The lounge's conversation,** newest first, paged (`afterId`). Reading it needs no right in PNMT, so neither does the tool.
- **People's and the AIs' text is untrusted** (`ctx.Untrusted`). Cookie's and Seraph's lines are model output that people may have steered.
- **Writing in the lounge stays out** (lounge-plan D15).

---

## 10. Decisions this plan reverses

**Each feature plan gets a line under its decision** pointing here, when the item is built.

| Plan | What it said | Now |
|---|---|---|
| docs/mcp-plan.md §7.3, §7.7 | `sensor_create` only for types that need nothing set up elsewhere; join scripts are secrets | MikroTik routers can be created; **the direct-SSH join script**, which holds no secret, comes over MCP. The WireGuard ones still don't |
| docs/linux-os-updates-plan.md l.601 | *"Cookie and MCP: no new tool"* | **A read tool.** Starting runs stays in the UI, as the plan wanted |
| docs/knowledge-base-extensions-plan.md l.8 | *"Likes or uses over MCP"* not in scope | `kb_page_used`, `kb_page_like` |
| docs/advanced-sensor-plan.md l.980 | Rule writing *"once rules proposed by an AI have a review step"* | Writing, with a test against the latest report in every answer instead of a review step (§7) |
| docs/boards-plan.md l.886, l.991 | *"MCP: nothing. The ceiling stays without Boards"* | *Boards: Message* in the ceiling, *View* in the read-only cap |
| docs/board-messages-plan.md l.8, §17 | Messages from outside PNMT not in scope; an MCP tool *later* | `board_message_send`, with the existing limits |
| docs/lounge-plan.md D15 | No MCP tools; reading *can come later* | `lounge_read`. Writing still not |
| docs/responsibilities-plan.md decisions 10, 11, R9 | *View* and *Manage*; MCP and Cookie read only | **A level *Edit*;** MCP edits. Cookie still reads only |

---

## 11. What stays out

**Unchanged from mcp-plan §7.7:**

- **Secrets:** sensor tokens, UDP keys and tokens, WireGuard keys and their join scripts, backup downloads, backups with secrets.
- **Approvals:** decisions, Cookie's proposals, mute and suppress, stopping a case, **accepting a changed certificate** (and so the pinned trust, §3.3).
- **A person's own statements:** *Was this you?*, access cases, one's availability column.
- **Creating and changing notification rules and DNS zones.**
- **Servers joining** (a join token is a secret).
- **Everything at *Manage* or behind the admin flag:** deleting responsibilities and the global people among them now.
- **Writing in the lounge.**

---

## 12. Testing

| What | Where |
|---|---|
| **The catalog's rules** for every new tool: a right, a kind, events or a `NotAudited` reason, no *Manage*; the read-only token sees only reading tools | `McpToolsTests` (l.79 extended for `NotAudited`) |
| **The canary test** over the new results: no sensor token, UDP key, WireGuard private key (`router_join_script` for a WireGuard router), contact detail of a rule's recipient, URL of a board, sensitive page (`kb_changes`, `responsibility_link`) | `McpToolsTests` |
| **Each type's object:** create with it, update one field and the others stay; MIKROTIK below its poll minimum, admin-only fields refused; UDP `keyFamily` on update refused; SSL `Pinned` refused; an object for the wrong type | `McpToolsTests` |
| **Responsibilities' *Edit*:** *Edit* creates, changes, links and edits combinations, and can't delete or set the global people; *Manage* still does everything; the endpoints' rights | `ResponsibilityToolsTests`, `EndpointAuditTests`, the rights tests |
| **Board messages:** sent via MCP with the actor, withdrawing only one's own | `McpExtensionsTests` |
| **Writes in the audit** as MCP, with their changes, for every new write | `McpToolsTests` |
| **The web UI:** the level *Edit* on the group page and the responsibilities page | Vitest; `e2e/responsibilities.spec.ts` (new: there is none yet) |

---

## 13. Phases

| Phase | Items | Notes |
|---|---|---|
| **X1** | A1, A2, A4, A5, A6, A7, A8 | The sensors. One change to `sensor_create` / `sensor_update`, then the types one by one |
| **X2** | B1, B2, B3 | Routers |
| **X3** | C2, D1, E1, G4, G6 | Read tools only, no rights change |
| **X4** | E3, G5 | Writes that reverse a decision; `ToolSpec.NotAudited` |
| **X5** | R1 | The level *Edit* (rights, endpoints, UI), then the tools |
| **X6** | H1, H2, H4 | Boards in the ceiling, `BoardMessageSource.Mcp` |

**Each phase is releasable on its own.** At the end: the release notes, and mcp-plan §7 with the new tools. No new setting.

---

## 14. Decided (2026-11-24)

1. **Board messages over MCP: every kind, *Alert* included.** **No limit of its own**, only the existing ones (§9.2).
2. **Advanced rules without a review step**, with each rule's outcome in every answer (§7).
3. **Redmine combinations under *Edit*** (§8.1).

---

## 15. As built

**State on 2026-11-24:** every item of §1 built, not committed. **125 tools** (95 before). `dotnet test` passes (§15.3). The web UI: `typecheck`, `lint`, `format:check`, `check:api`, Vitest (92 files, 742 tests).

### 15.1 Where

| What | Where |
|---|---|
| A type's own settings, MIKROTIK creatable, `sensors_set_importance` | `Tools/Write/MonitoringWriteTools.cs` |
| `router_join_script`, `router_jobs`, `router_changelog`, `mikrotik_commands_list` | `Tools/Read/RouterReadTools.cs` |
| `router_get`'s differences, `mikrotik_command_cancel` | `Tools/Actions/CommandTools.cs` |
| `router_difference_acknowledge` | `Tools/Write/RouterWriteTools.cs` |
| `sensor_types`, `status_codes`, `notification_coverage`, `os_updates_get`, `advanced_reports`, `advanced_test`, `certificate_history`, `certificate_authorities` | `Tools/Read/SensorReadTools.cs` |
| `kb_changes`; `kb_page_used`, `kb_page_like` | `Tools/Read/KnowledgeReadTools.cs`; `Tools/Write/KnowledgeWriteTools.cs` |
| `advanced_rules_change`, `advanced_fields_change` | `Tools/Write/AdvancedWriteTools.cs`; the view shared with `advanced_sensor_get` (`MonitoringReadTools.AdvancedView`) |
| The responsibility tools; `responsibility_get`'s `editing` | `Tools/Write/ResponsibilityWriteTools.cs`; `Tools/Read/ResponsibilityReadTools.cs` |
| `boards_list`, `board_get`, `lounge_read`; the messages | `Tools/Read/BoardReadTools.cs`; `Tools/Write/BoardWriteTools.cs` |
| `ToolSpec.NotAudited`; `Nested`, `Texts`, `Choices` and their readers; paging bridged to a service's `PageQuery` | `Tools/ToolSpec.cs`; `Tools/ToolContext.cs` (`Paging.Query`, `Paging.Of`) |
| No audit scope for a `NotAudited` write | `Mcp/McpCalls.cs` |
| The ceiling: *Responsibilities: Edit*, *Boards: Message* (read-only: *Boards: View*) | `Mcp/McpRights.cs` |
| *Responsibilities: Edit*; the endpoints and the upload's right | `Auth/Rights/Rights.cs`; `Api/V1/ResponsibilitiesController.cs`, `Api/V1/FilesController.cs` |
| `responsibility_history.Via` | `Models/Db/ResponsibilityEntities.cs`, `Responsibilities/ResponsibilityService.cs`, migration `ResponsibilityHistoryVia` |
| `BoardMessageSource.Mcp` | `Api/V1/Contracts/Boards.cs` |
| The UI: the level, the pages by *Edit* and *Manage*, *via MCP* in the history and on Admin → Debug's messages | `api/rights.ts`, `router/index.ts`, `views/admin/responsibilities/*`, `views/admin/BoardMessagePanel.vue` |
| Tests | `Mcp/McpExtensionsTests.cs` (new, against the fake Zabbix); `McpToolsTests` (the rule for `NotAudited`; a LINUX sensor is the refused type now); `ResponsibilitiesApiTests` (*Edit* changes, *Manage* deletes); `ResponsibilityToolsTests`, `GroupsTests`, `EndpointAuditTests` (the level) |

### 15.2 Where the code differs from the plan

- **G5 runs no separate test.** Every rule and field change already re-judges the latest report (`AdvancedSensorService`, `reevaluate: true`), so the answer is the sensor as `advanced_sensor_get` says it, with each rule's outcome. Rules and fields now carry their `id` there.
- **`router_join_script` is a read** that records a view, as the router's page does: `MikrotikJoinScriptViewed` with `Actor = Mcp`. Its `firewall` and `removals` are remembered, as in the UI.
- **`notification_coverage` names the rules that reach nobody, not why:** some reasons name a person (*Anna Muster is deactivated*). The rule's page in PNMT says it.
- **`certificate_history` and `certificate_authorities` give no PEM for a certificate that names an e-mail address**, as `certificate_get` leaves the address out of the subject.
- **`lounge_read` masks the address of a lounge e-mail**, as `notification_rules_list` masks recipients.
- **A Zabbix sensor's rules need *Zabbix: View*, checked in the tool**, as SSL settings need *Certificates: Edit*.
- **`responsibility_get`'s `editing`** (the revision, the Markdown, the ids) **is MCP's only**: Cookie's answer stays as it was.
- **`responsibility_options`** is a read at *Edit*, so only the read & write token has it.
- **`board_message_withdraw` refuses another's message with `not_yours`**, before the service is asked.

### 15.3 The checks

- `dotnet test`: 1374 tests, 1369 passed, 5 skipped (as before), 0 failed. The first full run had two failures in other classes (`BoardMessagesTests`, `CookieBoardMessageTests`): this class had dated its rows ahead of theirs and left a board message open. It now freezes its clock at the current second and removes its board and messages.
- Not done: trying the tools with a real client (mcp-plan §19.3 is still open), and the release notes.
