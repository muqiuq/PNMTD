# Plan: Advanced sensors: an app's JSON report, shown in PNMT and judged by rules

| | |
|---|---|
| Status | **Built** (§17): the open questions of §15 took their proposals. Released in 1.14.0. |
| Date | 2026-11-17 |
| Scope | A new sensor type, **Advanced** (`ADVANCED`). An external service or app POSTs a JSON object to PNMT with the sensor's write token, as often as it needs to. PNMT keeps the reports and shows the latest on a tab of its own (*Data*). The values a person chose as fields get a label, a unit, a place on the overview and a chart. Everything else shows as a readable tree with tables. **Rules** defined on the sensor (*Rules* tab) say what must hold for the report to be alright. A rule is a path into the JSON, an operator and a value: `disk.free_gb` at least 5, none of `jobs[*].state` is `failed`, `backup.finished` newer than 26 h. The sensor is OK when every rule holds. It warns or fails when one doesn't, fails when the report can't be read, and turns 460 when no report came within its interval. **The endpoint only stores a report. One task, the evaluator, judges every report and is the only thing that writes the sensor's codes.** The rest of PNMT (notifications, the dashboard, uptime, `/read`, Cookie) reacts to those codes as it does for any sensor. |
| Goals | <ol><li>**An app says more than a code and a message:** any JSON object up to 256 KiB, in one POST, with the token in a header (R1, R14).</li><li>**Shown properly, with no setup first:** the latest report as a tree with tables, with dates and states readable. Fields a person picks get a label, a unit, a place on the overview and a chart (R2, R3, R12).</li><li>**Rules decide what is alright,** per sensor, without code: comparisons, text, states, ages, counts and changes, over one value or every element of a list (R4, R5, R6).</li><li>**Quiet when nothing changes:** an event when the verdict changes, not with every report (R8).</li><li>**A broken or silent app shows:** an unreadable report fails the sensor, and so does a missing one (R9, R10).</li><li>**Nothing changes for the sensor API:** `/event` and `/read` stay frozen, and reports have a path of their own (R11).</li></ol> |
| Not in scope | PNMTD fetching JSON from a URL (pull, §16). Reports by GET with query parameters, form posts, or several sensors in one request (§16). An expression language or computed values (`free / total`). Rules combined with OR, or rules that depend on other rules. A warning *state* (§3). Rule templates shared between sensors (§16). Rules as child sensors with notifications of their own (§16). AI suggesting rules (§16). JSON Schema validation. Reading reports back with the read token. |
| Depends on | The sensor pipeline: `SensorService`, `SensorEventWriter`, `MonitoringState`. The Zabbix sensor's side table, pure `Evaluate` and events on change (docs/audit-redmine-zabbix-plan.md §7.7). The agents' paths as the pattern for anonymous endpoints outside `/api` that authenticate by token (docs/windows-sensor-plan.md §9.1). The rights, and *Monitoring: Details* for what names people (docs/audit-redmine-zabbix-plan.md §5; docs/cookie-object-notes-plan.md §4). The audit's side-table subjects (docs/audit-redmine-zabbix-plan.md §4.3). The tool catalog (docs/mcp-plan.md §7.3). docs/ssl-certificate-plan.md for the numbers it has taken (§1.3). |
| Target | 1.14.0, with the SSL certificate sensors (built on the same day with the numbers of §1.3; 1.13.0 wasn't released on its own) |

**Words.**

- ***A report*** is one JSON object that an app POSTs to `/report`.
- ***The app*** is whatever sends it: a service, a script, a cron job or a device.
- ***A path*** names values in a report (`disk.free_gb`, `jobs[*].state`, §6.1). A path with `*` can name several values, its ***matches***.
- ***A rule*** is a path, an operator and its operand. It also says what happens when nothing matches, and how bad a failure is (*fail* or *warn*).
- ***A field*** is a path that a person gave a label, a format and a unit, for the *Data* tab, the overview and charts.
- ***The verdict*** is what the rules make of the latest report, or of its absence: a code, a message and the rules that didn't hold.
- ***The verdict key*** identifies a verdict, so PNMT can tell whether an event is due (§3.3).
- ***The evaluator*** is the one task that judges reports and writes the sensor's codes (§7.3). ***An unevaluated report*** is stored, but not judged yet.
- ***A nudge*** wakes the evaluator at once, instead of at its next timer run (§7.3).
- ***Stale*** describes a sensor whose last report is older than its interval plus its grace period.
- ***A repeat*** is a report identical to the one before it, folded into that one's row (§7.2).
- ***A sample*** is a charted field's values in one minute.

---

## 1. Today

### 1.1 What an app can tell PNMT now

- **A code and a message per request** (`SensorApiController`, frozen at v0.12.1: docs/webui-migration-plan.md §3.3):
  - `GET /event/<token>/<code>/<message>`, or a POST of the message;
  - the code decides OK (up to 399) or failing;
  - whatever structure the app has must fit into one string.
- **VALUECHECK and HEARTBEAT_VALUECHECK** match the message against one regular expression, giving 275 or 475 (`SensorTypeActionHelper.AdjustCodeForValueCheckSensorTypeInEvent`). That is one rule, on text, and the pattern has no timeout.
- **ENCAPSULADED** takes a JSON array of `{ Name, Code, Message }` and makes each name a child sensor, at most 65. The app still decides each code itself. PNMT judges nothing and keeps nothing but the message.
- **Events are the only storage:** 2000 per sensor, each a code and a message, with the dedupe (when the last two are equal, only the time moves). There is no place for structured data, and no history of values.
- **A UDP node's DATA** becomes event 232, with the payload as text or `hex:`. Interpreting it was left for later (docs/udp-iot-plan.md §8, U7).

### 1.2 What the new type builds on

- **Sensors that PNMT judges itself keep a side table and a pure verdict** (the Zabbix sensor):
  - the side table is `ZabbixSensorEntity`: 1:1 with the sensor, `[Audited]`, deleted by cascade;
  - the verdict is pure: `ZabbixSensorService.Evaluate(rules, host, problems)` returns `ZabbixVerdict(Code, Message, ProblemIds)`;
  - an event is written only when the code or the problems change (`LastCode`, `LastProblemIds`);
  - `ApplyAsync` clears `LastCode`, so a settings change shows at once;
  - `SensorService` calls the type's `ValidateAsync`, `ApplyAsync`, `RemoveAsync` and `DetailAsync`, and `SensorRequest.Zabbix` and `SensorDetail.Zabbix` carry the type's part.
- **A task that judges and writes the code is the usual pattern:**
  - `PingCheckTask` (every 10 s) pings and writes 270 or 470;
  - `TimespanConditionsCheckTask` (every 15 s) judges the reports since its last check and writes 280/480 or 281/481;
  - the Zabbix poller judges Zabbix's problems and writes 290 to 492;
  - `RouterPollService.PollAsync(sensorId, wait, …)` is one poll that both the timer and *Poll now* call, under a per-router lock. A request that needs the result waits for it, and the poller stays the only writer.
- **The existing tasks react to codes, whoever wrote them:**
  - `NotificiationTask` (every 10 s) takes every event of the last 30 minutes that no rule has handled yet (`GetAllPendingNotificationsForLastMinutes`), whatever its source;
  - `MonitoringState`, uptime, `/read`, the sensor lists and Cookie's case book read the sensor's last event.
- **`SensorEventWriter.WriteAsync`** writes PNMT's own events under the per-sensor advisory lock (`pg_advisory_xact_lock` on the sensor id, the same key the sensor API uses), with the dedupe and the 2000-event cap.
- **Anonymous endpoints outside `/api` already exist:**
  - the agents' `/agent/v1/join` is a minimal-API endpoint (`AgentJoinEndpoint`) with JSON errors of its own, a 64 KiB body limit read by hand, and a per-address guard (`JoinGuard`);
  - `Program.cs` keeps cookie authentication off the sensor API, the agents' paths and `/mcp` (`UseWhen(… !SensorPaths.IsSensorPath … !AgentPaths.IsAgentPath …)`);
  - security headers and ProblemDetails apply to `/pnmt-admin` and `/api` only;
  - `UdpControllers` sets a per-request body limit through `IHttpMaxRequestBodySizeFeature`.
- **Staleness belongs to `HeartbeatCheckTask`:**
  - every 10 s it checks HEARTBEAT, HEARTBEAT_VALUECHECK, UDP_NODE, WINDOWS, LINUX and scheduled DIAGNOSTICS sensors, and writes 460 when the *last event* is older than `Interval + GracePeriod`;
  - it pauses while the uplink is down (`TaskRunDecisionMaker`) and for `Heartbeat:StartupGraceMinutes` after a start;
  - it reads events, so it only works for types that write an event, or move one, with every report.
- **Uptime comes from the intervals between events** (`MonitoringQueries.UptimeAsync`). A type that writes only on change keeps correct uptime bars, as Zabbix does.
- **JSON from outside is stored in several places already:** as `jsonb` in `agent_changes.Details` and in a diagnostics run's `Results`, and as bytes with a hash in the agents' sections (`AgentSectionEntity.Hash`), compared before a new row is kept.
- **The audit follows side tables to their sensor:** an `[Audited]` entity with a `SensorId` is listed under the sensor's *History*, named by its `Name` (`AuditChangesInterceptor.Subjects`, `SubjectCandidates`, `NameCandidates`).
- **The web UI has the building blocks:**
  - `TabNav` and the per-type tab sets in `SensorView.vue` (`zabbixTabs`, `mikrotikTabs`, the `tab` computed);
  - `InlineField`, `ListTable` and `CopyField`;
  - `DiffView` with `lineDiff.ts`;
  - the sensor's URL panel with curl examples (`SensorUrlPanel.vue`);
  - an SVG polyline chart with no chart library (the latency chart in `UplinkView.vue`).
- **Tools** mark outside text as untrusted (`ToolContext.UntrustedOrNull`) and strip credentials (`ToolSafety.WithoutCredentials`). `MonitoringWriteTools.CreatableTypes` lists the types `sensor_create` may make.

### 1.3 Free numbers

- **Sensor type:** the types go up to `ZABBIX = 110`, and docs/ssl-certificate-plan.md takes `SSL_CERTIFICATE = 120`. **`ADVANCED = 130`.**
- **Status codes:**
  - the SSL plan takes 285, 286 and 485 to 489;
  - 221, 423 and 424 are reserved for the AI analysis;
  - **295, 296, 495 and 496** are unused, and pair as `x95`/`x95+200`, as 290/490 do;
  - a missing report reuses **460** (`HEARTBEAT_MISSING`), as UDP nodes and servers do.
- **Key-value key:** the SSL plan takes `LAST_CERTIFICATE_CHECK = 310`. **`LAST_ADVANCED_EVALUATION = 320`.**
- Whichever plan is built first, the other keeps its numbers.

## 2. Requirements

From the request:

- **R1.** A sensor type, *Advanced*, to which an external service or app sends more information, as JSON.
- **R2.** PNMT displays that information properly.
- **R3.** The information has a tab of its own on the sensor.
- **R4.** Rules can be defined on the sensor, and decide from the data received whether everything is alright or not.

Added by this plan, because R1 to R4 need them (each can be dropped in the review):

- **R5.** Rules reach into nested objects and lists: one value, or every element of a list (all, any or none of them).
- **R6.** A rule can warn instead of fail.
- **R7.** Rules can be tried before they are saved: on the latest report, on a stored one, or on pasted JSON.
- **R8.** An event is written when the verdict changes, not with every report.
- **R9.** A report PNMT can't read (not JSON, too large) fails the sensor, and the app is told why.
- **R10.** A sensor with an interval fails (460) when no report came within the interval plus the grace period.
- **R11.** The sensor API (`/event`, `/read`) stays unchanged byte for byte, and the contract replay stays green.
- **R12.** Reports are kept for a while, with a history, a comparison with the previous report, and charts of chosen values.
- **R13.** Per sensor, data that names people is shown only to people with *Monitoring: Details*.
- **R14.** The token can travel in a header, so it stays out of proxies' access logs.

## 3. Codes and states

| Code | Name | Success | When |
|---|---|---|---|
| 295 | `ADVANCED_OK` | yes | The latest report holds every rule, or the sensor has no rules. |
| 296 | `ADVANCED_WARNING` | yes | Only rules marked *warn* don't hold. The state stays *OK*, because PNMT has no warning state (docs/ssl-certificate-plan.md decision 4). The *Data* and *Rules* tabs show the warning in amber, and a notification rule *on change* tells it. |
| 495 | `ADVANCED_FAILED` | no | At least one rule marked *fail* doesn't hold. |
| 496 | `ADVANCED_INVALID` | no | The latest report couldn't be read: not JSON, not an object, too large, too deep, or a key twice. |
| 460 | `HEARTBEAT_MISSING` | no | With an interval: no report, valid or not, for `Interval + GracePeriod`. |

**Precedence, worst first:** 460 → 496 → 495 → 296 → 295. A sensor that has never had a report has no event, so its state is *No data*, as for every type.

### 3.1 Messages

**A message stays the same while the verdict does.** An event is written once and stays the sensor's last message, so it names the values *as they were when the verdict changed*. The *Data* tab shows the live ones.

- **295:** `All rules hold`, or `Report received` for a sensor without rules.
- **296:** `Warning: Queue length (queue.length = 812, must be at most 500)`.
- **495:** `2 rules fail: Disk space (disk.free_gb = 3.2, must be at least 5); No failed job (jobs[1].state = "failed")`.
  - The rules come in their order.
  - At most 1000 characters, then `… and 3 more`.
- **496:**
  - `The report isn't valid JSON: line 3, position 14: ',' expected`;
  - `The report is 1.2 MB, more than 256 KiB`;
  - `The report must be a JSON object, not an array`;
  - `The report has the key "disk" twice`.
- **460:** `No report since 17 Nov 2026 10:02 (expected every 5 min, 1 min grace)`.

**A sensor whose data names people** (§13) gets messages without the values: `2 rules fail: Owner (owner: must be "nobody"); Disk space (disk.free_gb: must be at least 5)`. Events are visible with *Monitoring: View*, and they reach mails and Pushover.

**Values from the app are untrusted text** in a message that reaches mails and Pushover:

- each value is cut at 60 characters;
- control characters are removed;
- strings are shown in quotes;
- dates are absolute, in PNMT's time zone (`TZ`), never "3 hours ago".

### 3.2 Each rule's outcome

- **The outcome** is `Pass`, `Warn`, `Fail` or `Off` (disabled).
- **A reason**, when the outcome needs one:
  - `Missing`: nothing matched, and the rule's *when missing* decided (§6.2);
  - `WrongKind`: the value can't be compared with this operator (text for a number operator);
  - `Timeout`: the pattern took too long;
  - `TooManyMatches`.
  `WrongKind`, `Timeout` and `TooManyMatches` give the rule's severity (*fail* or *warn*).
- **`Warn` has two sources:** a *warn* rule that doesn't hold, or `healthy` meeting a warning word (§6.3).
- **Each outcome carries:**
  - the actual values, cut at 200 characters;
  - the concrete paths of up to 20 matches (`jobs[3].state`), which the *Data* tab marks.

### 3.3 When an event is written

Only the evaluator writes the type's events (§7.3). It writes one when the verdict key changes:

| Verdict | Key |
|---|---|
| 295 | `295` |
| 296 | `296\|<ids of the warning rules>` |
| 495 | `495\|<ids of the failing rules>`. Warnings don't count, so a warning coming and going while the sensor fails writes nothing. |
| 496 | `496\|<kind>`: `not-json`, `not-object`, `too-large`, `too-deep`, `duplicate-key`, or `internal` (PNMT couldn't evaluate it, §7.3) |
| 460 | `460` |

The rule ids are in the rules' order.

- **A value that changes while the same rules fail writes nothing.** Free space falling from 3.2 to 3.1 GB is the same problem.
- **A failure that lasts one report still leaves its events.** The evaluator judges every report in order, not only the newest (§7.3). A report that fails between two that pass writes the failure and the recovery, even when all three arrive between two runs.
- **An event posted through `/event`** is written as for any sensor, since the write token works there too and that API is frozen. The evaluator compares with the sensor's newest event as well as with the key, so the next report writes its verdict again.
- **A change of the rules, the interval or the grace period** has the evaluator re-judge the latest report at once (§7.5). It writes an event only if the key changes, so renaming a rule doesn't notify anyone.

## 4. The report

### 4.1 The endpoint

```http
POST /report
Authorization: Bearer <write token>
Content-Type: application/json

{ "status": "ok", "message": "Nightly backup done", "disk": { "free_gb": 412.5 }, "jobs": [ … ] }
```

- **The token:**
  - **`POST /report`** with `Authorization: Bearer <write token>`. The token is the sensor's existing write token.
  - **`POST /report/<write token>`** for apps that can't set a header. The sensor's page shows the header form first.
- **Why its own path:**
  - not under `/event`, because that path is frozen (R11);
  - not under `/api/v1`, because that is the UI's API, with cookies, CSRF and ProblemDetails.
- **Its place in the pipeline:**
  - `ReportPaths.IsReportPath` joins `SensorPaths` and `AgentPaths` in `Program.cs`'s `UseWhen`. Without cookie authentication there is no session, and so no session gates.
  - Security headers and ProblemDetails don't apply, because they cover only `/pnmt-admin` and `/api`.
  - The endpoint is a minimal-API endpoint (`ReportEndpoint`, like `AgentJoinEndpoint`), `.AllowAnonymous()`, mapped in `AdvancedSetup.MapReportEndpoints`.
- **Reading the body:**
  - it is read as JSON whatever the `Content-Type` says, because `curl --data` sends `application/x-www-form-urlencoded`;
  - it is read as UTF-8, and a BOM is skipped;
  - `Content-Encoding: gzip` is accepted, through `UseRequestDecompression` on this path only, so the frozen paths are untouched. The size limit counts the decompressed bytes.
- **The sensor** is the token's sensor. It must be enabled and of type ADVANCED.
  - An unknown token or a disabled sensor gets an empty 404, as on `/event`.
  - A sensor of another type gets 409 `wrong_type`: *"This sensor is a HEARTBEAT; reports go to Advanced sensors. Its URL is /event/<token>/<code>."*
- **The rate limit:** at most one report per sensor every `Advanced__MinSecondsBetweenReports` seconds (5). A report that comes sooner gets 429 `too_soon` with `Retry-After`, and is neither counted nor stored. This is the sensor API's 425 without its quirks.

### 4.2 The body

- **A JSON object.** Everything inside it belongs to the app: any keys, nesting and lists, in the app's order.
- **PNMT reads three keys when they are there** (all optional, at the top level):
  - **`status`** (string): the app's own verdict. It shows as a badge, and new sensors get a rule for it (§6.5).
  - **`message`** (string, up to 1000 characters used): a line for the report's headline and the history.
  - **`time`** (ISO 8601 with an offset, or Unix seconds): when the app measured. It shows next to *received*. A difference of more than 5 minutes shows a hint, such as *the app's clock is 7 min behind*. Rules on ages compare with PNMT's clock, not with this.

  Otherwise these keys are data like any other, and rules may use them.
- **Common health formats work as they are:**

  | Format | Status words | Parts |
  |---|---|---|
  | The IETF draft *Health Check Response Format for HTTP APIs* (`application/health+json`) | `pass`, `warn`, `fail` | `checks`, with `observedValue`, `observedUnit`, `status` and `time` |
  | Spring Boot Actuator's `/actuator/health` | `UP`, `DOWN` | `components` |
  | ASP.NET Core's HealthChecks UI writer | `Healthy`, `Degraded`, `Unhealthy` | `entries` |

  The starter rule reads all three formats' status words (§6.3), and their parts show as tables (§10.3).
- **Limits.** Breaking one gives 496 (§3):
  - **size:** `Advanced__MaxReportBytes`, 256 KiB (262 144 bytes), checked on `Content-Length` before reading and again while reading (`IHttpMaxRequestBodySizeFeature`, as in `UdpControllers`);
  - **depth:** 32 (`JsonDocumentOptions.MaxDepth`);
  - **no key twice**, because which one counts would be a guess. This uses `JsonDocumentOptions.AllowDuplicateProperties = false`, new in .NET 10; A2 confirms it, or else a walk checks each object's names;
  - **no comments and no trailing commas** (the defaults).

### 4.3 The answer

The answer is small JSON, camelCase, and never repeats the report. **The request doesn't decide the sensor's code; the evaluator does, a moment later (§7.3).** So the answer says the report was received, with a *preview*: the verdict that the same pure function (§6.4) gives this report now. The preview writes nothing.

| Status | Body | When |
|---|---|---|
| 202 | `{ "ok": true, "reportId": "…", "preview": { "code": 495, "state": "failing", "message": "2 rules fail: …", "failed": ["Disk space", "No failed job"], "warnings": [] } }` | Stored, and waiting for the evaluator. `ok` means *received*, and the preview lets a script log what the sensor will say. |
| 422 | `{ "ok": false, "reportId": "…", "error": "invalid_json", "message": "…" }` | Stored as an invalid report (§5.4), which the evaluator turns into 496. The app is told at once. |
| 413 | `{ "ok": false, "reportId": "…", "error": "too_large", "message": "…" }` | Too large. Recorded without its body, and the evaluator turns it into 496. |
| 429 | `{ "ok": false, "error": "too_soon", "message": "…" }`, with `Retry-After` | The rate limit. Nothing is stored. |
| 409 | `{ "ok": false, "error": "wrong_type", "message": "…" }` | Not an Advanced sensor. |
| 401 | `{ "ok": false, "error": "no_token", "message": "…" }`, with `WWW-Authenticate: Bearer` | `POST /report` without the header. |
| 404 | empty | An unknown token, or a disabled sensor. |

- **The preview can differ from the code the evaluator writes** in two cases: a report that arrived just before this one isn't judged yet, or the rules change in between. Both are rare, and the code is what counts.
- **`?dryRun=true`** answers 200 with the preview, but stores nothing and doesn't nudge the evaluator. It is for the app's developer, and counts against the rate limit.
- **The answer names rules.** The token's holder may know them: they are the app's rules.

### 4.4 Examples

These examples appear on the sensor's URL panel (`SensorUrlPanel.vue`, with a set for the type) and in docs/advanced-report.md:

```sh
# a file, the token in the header
curl -H "Authorization: Bearer $TOKEN" --data @status.json https://pnmt.example.com/report

# inline, the token in the path
curl --data '{"status":"ok","queue":{"length":12}}' https://pnmt.example.com/report/$TOKEN

# forward an app's own health endpoint from cron
curl -s http://localhost:8080/health | curl -H "Authorization: Bearer $TOKEN" --data-binary @- https://pnmt.example.com/report
```

```powershell
Invoke-RestMethod -Method Post -Uri https://pnmt.example.com/report -Headers @{ Authorization = "Bearer $token" } `
  -ContentType 'application/json' -Body ($status | ConvertTo-Json -Depth 10)
```

```python
requests.post("https://pnmt.example.com/report", json=status, headers={"Authorization": f"Bearer {token}"}, timeout=10)
```

## 5. The data model

The migration is `AdvancedSensors`.

- **No column on `sensors`:**
  - `Interval` and `GracePeriod` are the expected report interval and its grace (0 means no expectation);
  - `Parameters` stays null.
- **The SQLite import needs nothing,** because v0.12 had none of these tables.

### 5.1 `advanced_sensors` (`AdvancedSensorEntity`, `[Audited]`): 1:1 with the sensor, cascade

**The setting** (audited):

- `NamesPeople` (bool, default false): the data names people, so the reports need *Monitoring: Details* (R13, §13).

**The state** (`[AuditIgnore]`), in two parts, each with one writer:

- **The receipt, written by the endpoint** (§7.1):
  - `LastReportId` (FK → `advanced_reports`, SET NULL);
  - `LastReportAt`: any report, valid or not. Staleness reads this, so a report counts as a sign of life as soon as it is stored, before it is judged;
  - `LastValidReportAt`, and `LastSource` (up to 100 characters, the address).
- **The verdict, written by the evaluator** (§7.3):
  - `LastVerdictKey` (up to 2000), `LastCode`, `LastMessage` (up to 1000) and `LastEvaluatedAt`;
  - `RuleStates` (`jsonb`): each rule's latest outcome and since when, for *failing since* on the *Rules* tab.
- **`ReevaluateRequestedAt`**, set by a change of the rules or the settings (§7.5) and cleared by the evaluator once it has re-judged the latest report.

`Created`.

### 5.2 `advanced_rules` (`AdvancedRuleEntity`, `[Audited]`): cascade with the sensor

- **Identity:** `Id`, `SensorId` and `Order` (int).
- **Description:**
  - `Name` (1 to 200 characters);
  - `Enabled` (default true);
  - `Note` (up to 1000): why the rule exists, shown with a failure on the tabs.
- **The condition:**
  - `Path` (1 to 500 characters, §6.1);
  - `Operator` (`AdvancedOperator`, stored as a string);
  - `Operand` (up to 2000, null for operators without one);
  - `Operand2` (up to 200, for *between* and *outside*);
  - `IgnoreCase` (bool).
- **How it counts:**
  - `Quantifier` (`All`, `Any` or `None`; default `All`; read only for paths with `*`);
  - `WhenMissing` (`Fail`, `Warn` or `Pass`; default `Fail`);
  - `Severity` (`Fail` or `Warn`; default `Fail`).
- `Created`.

At most **50 rules** per sensor. The audit lists every change on the sensor's *History*, such as *Rule "Disk space": Operand 5 → 10*, through its `SensorId` and `Name` (§1.2).

### 5.3 `advanced_fields` (`AdvancedFieldEntity`, `[Audited]`): cascade with the sensor

- `Id`, `SensorId` and `Order`.
- `Name` (1 to 100 characters): the label. The column is called `Name` so that the audit uses it as the row's name.
- `Path` (1 to 500), without `*`: a field is one value.
- `Format`: `Auto`, `Number`, `Percent`, `Bytes`, `Duration` (seconds), `DateTime`, `Text`, `Boolean` or `Status`.
- `Unit` (up to 20 characters, shown after a number) and `Decimals` (0 to 6; null means as needed).
- `Pinned`: shown on the overview.
- `Chart`: samples are kept for it (§5.5).

At most **30 fields** per sensor, **10 of them charted**.

### 5.4 `advanced_reports` (`AdvancedReportEntity`, `[NotAudited("what an app reported")]`)

- **Receipt:**
  - `Id` and `SensorId` (cascade);
  - `ReceivedAt`;
  - `LastReceivedAt` and `Repeats`: the same report again (§7.2);
  - `Source` (up to 100 characters).
- **The bytes:** `Bytes` (int) and `Sha256` (hex, of the body as received).
- **`Body` (`text`):** the JSON exactly as received, or null for a report too large to keep.
  - Not `jsonb`: PostgreSQL reorders `jsonb` keys (shorter keys first) and drops duplicate keys, and the app's order is part of showing a report properly.
  - Values are read in .NET when a report arrives (for rules and samples), never by SQL.
- **Whether it could be read:** `Valid` (bool), `ErrorKind`, and `Error` (up to 500 characters).
- **The three keys of §4.2,** for the history's list: `Status` (up to 50 characters), `Message` (up to 1000) and `MeasuredAt`.
- **`EvaluatedAt`:** null until the evaluator has judged the report. This is the evaluator's queue: it lives in the database, so a restart loses nothing. A repeat sets it back to null (§7.2).
- **`EvaluationAttempts`** (smallint): the runs in which judging this report threw. At three, the report is marked judged (§7.3, *Errors*).
- **The verdict at this report,** written by the evaluator:
  - `Code`;
  - `Failed` and `Warnings` (counts);
  - **`Results` (`jsonb`):** each rule's outcome, with the rule's name and condition as they were then. An old report shows what was judged then, even after the rules changed.
- **Indexes:**
  - (`SensorId`, `ReceivedAt` desc);
  - a partial index on (`SensorId`, `ReceivedAt`) `WHERE "EvaluatedAt" IS NULL`, so the evaluator finds its work without reading the history.
- **Retention** (`AdvancedMaintenance`, hourly, §7.4). Per sensor, these limits apply:
  - the newest `Advanced__KeepReports` reports (500);
  - none older than `Advanced__KeepDays` (30);
  - at most `Advanced__MaxBytesPerSensor` (50 MiB).

  The newest report and the one before it are always kept, for the change operators (§6.3), and so is every unevaluated report.

### 5.5 `advanced_samples` (`AdvancedSampleEntity`, `[NotAudited("charted values")]`)

- **One row per field per minute.** The key is (`SensorId`, `FieldId`, `Minute`), upserted with `ON CONFLICT … DO UPDATE`. A row holds `Min`, `Max`, `Last` (double) and `Count`.
- **Written by the evaluator,** which parses each report anyway. The minute is the report's receipt (`ReceivedAt`, or `LastReceivedAt` for a repeat), not the evaluation's, so a backlog after a restart lands in the right minutes.
- **Only charted fields with a numeric value:** fields with `Chart`, whose value is a number or a string holding one.
- **Kept for `Advanced__SampleDays` (30).** That is 1 440 rows per field per day and 43 200 for 30 days. Ten charted fields on one sensor make at most 432 000 rows.
- **Deleted with the field** (cascade on `FieldId`) and **with the sensor**.

### 5.6 What doesn't change

- **`events`:** a verdict's event is a row like any other. The event list, the `/read` API (whose `lastEventSuccess` and `lastEventMessage` are then the verdict's) and the notifications read it as they do today.
- **`sensors`:** no new column. The new type's number goes into `PNMTD.Lib`, which the agents share and ignore.

## 6. Rules

### 6.1 Paths

Paths are a small subset of JSONPath, parsed once when a rule is saved (`AdvancedPath.Parse`, in `PNMTD/Advanced/`):

| Form | Means |
|---|---|
| `disk.free_gb` | A member of a member. A leading `$.` is accepted and dropped. |
| `["free space"]`, `disk["a.b"]` | A key that isn't a plain name, in double quotes with JSON escapes. A plain name is `[A-Za-z_][A-Za-z0-9_-]*`. |
| `jobs[0]`, `jobs[-1]` | An element of a list. Negative indexes count from the end. |
| `jobs[*].state` | Every element of a list (*matches*). |
| `checks.*` | Every member value of an object. |

- **Left out on purpose:** filters (`[?(@.x > 1)]`), recursive descent (`..`) and functions. The quantifiers (§6.2) and the count operators cover the common needs, and a path stays readable in a notification.
- **Limits:** at most 16 segments in a path, and at most 1 000 matches evaluated. More matches fail the rule with `TooManyMatches`.
- **Nobody has to type a path:** the *Data* tab writes one when a person picks a value (§10.3).

### 6.2 Quantifiers and missing values

- **One value** (a path without `*`): the operator applies to it.
- **Several matches:**
  - `All`, the default: every match must hold;
  - `Any`: at least one match must hold;
  - `None`: no match may hold, as in *none of the jobs is `failed`*.

  The outcome names the first five matches that decided it, such as `jobs[3].state = "failed"`.
- **Nothing matched:** a key that isn't there, an index past the end, or an empty list under `*`. *When missing* then decides:
  - `Fail`, the default, because a value that disappears is usually a problem;
  - `Warn`;
  - `Pass`, for optional parts.

  The `exists` and `absent` operators decide for themselves.
- **`null` is a value, not a missing one.** `is_null` and `not_null` test it. A comparison with `null` gives `WrongKind`.

### 6.3 Operators

| Group | Operator | Operand | Holds when |
|---|---|---|---|
| Compare | `eq`, `ne` | a value | The value is equal, or not equal, to the operand. A number compares as a number (`5` equals `5.0`), `true` and `false` as booleans, `null` as null, and anything else as text (case-sensitive unless *ignore case* is set). |
| | `lt`, `le`, `gt`, `ge` | a number | The value is less than, at most, greater than, or at least the operand. |
| | `between`, `outside` | two numbers | The value is in the range (ends included), or outside it. |
| Text | `contains`, `not_contains`, `starts_with`, `ends_with` | text | As named. |
| | `matches`, `not_matches` | a .NET regular expression | The pattern finds a match in the text, or doesn't. The timeout is 100 ms, and a timeout gives the reason `Timeout`. |
| | `one_of`, `not_one_of` | a list, one entry per line | The value is in the list, or isn't. |
| Kind | `is_true`, `is_false` | – | The value is the boolean `true`, or `false`. The text "true" is not. |
| | `exists`, `absent` | – | The path matches something, or nothing. |
| | `is_null`, `not_null` | – | As named. |
| | `empty`, `not_empty` | – | The value is `""`, `[]`, `{}` or `null`, or anything else. |
| State | `healthy` | – | The value is a healthy word: `ok`, `pass`, `passed`, `up`, `healthy`, `success`, `green` or `true`, or the boolean `true`. The words `warn`, `warning`, `degraded` and `yellow` give *Warn*, even on a *fail* rule. Anything else fails. Case-insensitive. |
| Age | `older_than`, `newer_than` | a duration: `90s`, `15m`, `26h`, `7d`, or ISO 8601 such as `PT26H` | The value is a time older, or newer, than the duration, **compared with PNMT's clock now** (see below). |
| Count | `count_ge`, `count_le`, `count_eq` | a whole number | The count is at least, at most, or exactly the operand. For a path with `*`, the count is the number of matches. For a list or an object, it is the number of items or members. For text, it is the length. |
| Change | `changed`, `unchanged`, `increased`, `decreased`, `not_decreased` | – | The value compared with the same path in the previous valid report (see below). |

- **Ages:**
  - a time is ISO 8601 with an offset, or Unix seconds; numbers above 10¹¹ are read as milliseconds;
  - a time without an offset is read as UTC, and the outcome says so;
  - age rules are re-evaluated every 30 s (§7.3), so *the backup is more than 26 h old* fails on time, between reports.
- **Changes:**
  - **the previous report** is the receipt before the one being judged, since the evaluator judges reports in order (§7.3). For a repeat (§7.2) it is the same body, so a counter that must grow fails when the app sends the same thing twice;
  - the first report passes.
- **Numbers in strings:** numeric operators read `"3.2"` as 3.2, in the invariant culture with `.` as the decimal point. Anything else gives `WrongKind`.
- **Numbers with text operators** are read as their JSON text, so `3.20` stays `3.20`.
- **Numbers compare as `decimal`** when both fit, and as `double` otherwise, so parsing doesn't cause rounding surprises.
- **Operands are validated on save,** with a message per operator: *"A number, such as 5 or 0.25"*, *"A duration: 90s, 15m, 26h or 7d"*, *"Not a valid pattern: …"*.

### 6.4 The verdict (`AdvancedVerdict.Evaluate`, pure)

`Evaluate(rules, report, previous, at, lastReportAt, interval, grace)` returns `AdvancedVerdict(Code, Key, Message, Results)`. `at` is the moment the verdict is for:

- **a report from the queue** is judged as of its receipt (`ReceivedAt`, or `LastReceivedAt` for a repeat), with `lastReportAt` set to that same receipt. A backlog after a restart is then judged as it would have been at the time, and is never stale;
- **the timer's pass** judges the newest report as of now, with the side row's `LastReportAt` (§7.3).

The steps:

1. No report yet: no verdict, so no event and the state is *No data*.
2. An interval is set and `at − lastReportAt > Interval + GracePeriod`: **460**.
3. The report is invalid: **496**, with its kind.
4. Each enabled rule, in order, gives its outcome (§3.2).
5. Any rule failed: **495**. Otherwise, any rule warned: **296**. Otherwise **295**.
6. The message (§3.1) and the key (§3.3).

The function is pure: no database, and no clock, because `at` is passed in. `AdvancedVerdictTests` covers it without `TestApp`, as with `ZabbixSensorService.Evaluate` and `CertificateVerdict`. Three callers share it: the evaluator (which writes), and the endpoint's preview and the *test* endpoint (which write nothing).

### 6.5 The starter rule

A new sensor gets the rule **"The app's status"**: `status` `healthy`, *when missing: pass*.

- An app that sends no `status` key is unaffected by it.
- For an app that sends one, `"status": "error"` fails the sensor from the first report.
- The form has a checkbox for it, *Add a rule for the app's own status*, on by default.
- It is a rule like any other, changed or deleted on the *Rules* tab.

### 6.6 Trying rules

`POST /api/v1/sensors/{id}/advanced/test` evaluates without storing anything, writing an event or notifying anyone (R7).

- **The report** is the latest, a stored one by its id, or JSON in the request, within §4.2's limits.
- **The rules** are the saved ones, or a draft list (the editor's).
- **The answer** is each rule's outcome and the verdict the sensor would have.
- **The rule editor calls it while one types** (debounced), with the latest report. Every rule shows, before it is saved, something like *with the last report: 3.2 → fails*.

### 6.7 An example

The report:

```json
{
  "status": "ok",
  "message": "Nightly backup done",
  "time": "2026-11-17T02:14:03+01:00",
  "backup": { "finished": "2026-11-17T02:14:03+01:00", "size_bytes": 48213307392, "duration_s": 3712 },
  "disk": { "free_gb": 3.2, "total_gb": 2000 },
  "jobs": [
    { "name": "db", "state": "done", "errors": 0 },
    { "name": "files", "state": "failed", "errors": 3 }
  ]
}
```

The rules, and what they make of it:

| Rule | Path | Condition | Severity | Outcome |
|---|---|---|---|---|
| The app's status | `status` | `healthy` | fail | holds (`"ok"`) |
| Last backup | `backup.finished` | `newer_than 26h` | fail | holds |
| Disk space | `disk.free_gb` | `ge 5` | fail | **fails** (3.2) |
| No failed job | `jobs[*].state` | none `eq failed` | fail | **fails** (`jobs[1].state = "failed"`) |
| No job errors | `jobs[*].errors` | all `le 0` | warn | warns (`jobs[1].errors = 3`) |

The verdict is **495**: `2 rules fail: Disk space (disk.free_gb = 3.2, must be at least 5); No failed job (jobs[1].state = "failed")`. The key is `495|<Disk space>,<No failed job>`. Tomorrow's report with 3.1 GB free and the same failed job writes no event. If the job succeeds tomorrow, the key becomes `495|<Disk space>` and an event is written.

## 7. Receiving and evaluating

**The split:** the endpoint stores reports and judges nothing. One task, the evaluator, judges every report and writes every code, as `PingCheckTask` and `TimespanConditionsCheckTask` do for their types. The existing tasks then react to the codes as they do for any sensor (§1.2):

```
app ──POST /report──▶ ReportEndpoint ──▶ advanced_reports (EvaluatedAt = null) ──nudge──▶ AdvancedEvaluator
                                                                                              │
                                     events (295/296/495/496/460) ◀── SensorEventWriter ◀─────┘
                                        │
     NotificiationTask · MonitoringState · uptime · /read · Cookie's case book (unchanged)
```

### 7.1 Receiving (`ReportEndpoint`, `AdvancedReports.StoreAsync`)

A report is stored in one transaction, under the per-sensor advisory lock. The lock has the key that `SensorEventWriter` and the sensor API use, and it is reentrant within the transaction. It keeps two reports that arrive at once from both folding or both trimming.

1. **Find the sensor** by its token (§4.1), and apply the rate limit.
2. **Read and parse the body** with `JsonDocument`, within the limits of §4.2, to tell valid from invalid. A report that is refused becomes an invalid report row: `Valid = false`, the kind and the error, and the body if it is within the size limit, so the tab can show what arrived.
3. **An identical report** has the same SHA-256 as the latest row, and that row is valid. It updates that row's `LastReceivedAt`, `Repeats + 1` and `Source`, sets `EvaluatedAt` back to null, and adds no row.
4. **Otherwise, a new row,** with `EvaluatedAt` null.
5. **Update the side row's receipt columns** (§5.1): `LastReportId`, `LastReportAt`, `LastValidReportAt` and `LastSource`.
6. **Trim the sensor's reports** beyond `KeepReports`, as `TrimEvents` does, never an unevaluated one.
7. **Commit, nudge the evaluator** for this sensor, and answer 202 with the preview (§4.3).

The endpoint writes no event, no sample and no verdict column. It takes a few milliseconds.

### 7.2 Repeats

Many apps send the same thing every minute.

- **One row with `Repeats` and `LastReceivedAt`** keeps 500 reports from being 500 copies. The history shows *×1 440 · until 10:02*.
- **A repeat is judged again,** because ages move and the change operators see the same body (§6.3). That's why it sets `EvaluatedAt` back to null.
- **A repeat folded in before its row was judged** is judged once, as a repeat. The sensor's verdict is still right; only the step in between is skipped.

### 7.3 The evaluator (`AdvancedEvaluator`, a hosted task)

**The only writer of the type's codes.** Everything that changes an Advanced sensor's state goes through it: new reports, staleness, ages, and changes of the rules.

**When it runs:**

- **On a nudge.** The endpoint and `AdvancedSensorService` call `Nudge(sensorId)`, which wakes the task at once. Nudges that arrive during a run are taken by the next one, straight after. A report is usually judged within a second, and its notification follows at `NotificiationTask`'s next 10 s tick.
- **On its timer,** every `Advanced__EvaluateSeconds` (30).
- **One run at a time,** under a `RunGuard`. A run takes all the work there is.
- **The nudge is only a speed-up.** The work is in the database (`EvaluatedAt` null, `ReevaluateRequestedAt`), so a lost nudge or a restart costs at most one timer interval.

**A run, in this order:**

1. **The queue:** for each sensor with unevaluated reports, every such report in arrival order (`ReceivedAt`, then `Id`). Each one is judged as of its receipt (§6.4), against the report before it (§6.3). For each report:
   - its `Results`, code and counts, and `EvaluatedAt`;
   - the samples of the charted fields (§5.5);
   - the event, if one is due (below), with the report's address as its source.
2. **Re-evaluations:** each sensor with `ReevaluateRequestedAt` gets its newest report re-judged with the current rules (§7.5). This updates that report's `Results` and counts, and clears the request.
3. **The timer's pass:** the enabled sensors that **have an interval** (for staleness) or **a rule with an age operator** (`older_than`, `newer_than`). Each one's newest report is judged as of now, with the event's source set to `PNMTD`.

The queue comes first, so a report that arrived just in time is judged before staleness is checked. Staleness reads `LastReportAt`, which the endpoint writes at receipt, so a stored report that isn't judged yet already counts as a sign of life.

**Each sensor's work** is one transaction under its advisory lock, which also updates the verdict columns of the side row: `LastVerdictKey`, `LastCode`, `LastMessage`, `LastEvaluatedAt` and `RuleStates`.

**An event is due** when the key differs from `LastVerdictKey`, or when the sensor's newest event doesn't have this code (an event posted through `/event`). It goes through `SensorEventWriter.WriteAsync`.

- The event's time is the evaluation's, a moment after the report. Events stay in order, and uptime moves by that moment.
- No pass ever moves an existing event's time: only a report is a sign of life.

**How it holds back 460, as `HeartbeatCheckTask` does:**

- not while the uplink is down (`TaskRunDecisionMaker`), because reports can't arrive;
- not within `Heartbeat:StartupGraceMinutes` of a start.

Only the 460 step waits. Reports that do arrive are judged as usual, and age rules aren't held back either: the backup is as old as it is.

**Not `HeartbeatCheckTask`:** that task reads the last event's time, and this type writes events only on a change (decision 5).

**Errors:**

- A sensor whose work throws is logged and rolled back, and its reports stay in the queue for the next run. The other sensors go on.
- A report that throws in three runs in a row is marked judged, with the internal error in its `Results`, and the sensor gets 496 with *PNMT couldn't evaluate this report*. One bad report doesn't block the ones after it.

**Bookkeeping:** each run records `LAST_ADVANCED_EVALUATION`. *System health*'s task table lists *Advanced sensors*, with 60 s as the expected time between runs.

**Bounded work:** the rate limit allows at most six reports per sensor between two timer runs. A run after a long stop works through the backlog in order, at a few milliseconds per report.

### 7.4 Maintenance

- **Hourly, in the same task:** the retention of reports (§5.4) and of samples (§5.5).
- **Deleting a sensor deletes** the side row, the rules, the fields, the reports and the samples (cascade).
- **Changing the type away deletes them too,** through `RemoveAsync`, as for Zabbix.

### 7.5 Settings changes

A change of the rules, the interval or the grace period doesn't judge anything in the request.

- **`AdvancedSensorService` sets `ReevaluateRequestedAt`** in the same transaction as the change. It then calls `AdvancedEvaluator.RequestAsync(sensorId, wait: true)`, which nudges the evaluator and waits up to 5 s for its pass of that sensor. This follows `RouterPollService.PollAsync(…, wait)`.
- **The answer** therefore carries the rules' new outcomes, so the *Rules* tab shows them at once. If the evaluator takes longer, the answer comes without them, and the page's next refresh has them.
- **The evaluator re-judges the newest report** (§7.3, step 2). It writes an event only if the key changes (§3.3): a new rule that fails shows at once, and renaming a rule notifies nobody.

## 8. The sensor through `/sensors`, and its API

### 8.1 `SensorService`

- **`ValidateAsync`, case `ADVANCED`:**
  - `Parameters` must be empty;
  - `Interval` is 0, or 30 s and more (up to the general limit);
  - `AdvancedSensorService.Validate(request.Advanced, v)`.
- **`CreateAsync`** calls `advanced.ApplyAsync(sensor, request.Advanced)`: the side row, and the starter rule when `AddStatusRule` is set (the default).
- **`UpdateAsync`:**
  - calls `ApplyAsync` for the type;
  - calls `RemoveAsync` when the type changes away. The rules, fields, reports and samples go, and the form says so first;
  - then requests a re-evaluation from the evaluator, and waits for it (§7.5).
- **`GetAsync`:** `SensorDetail.Advanced = await advanced.DetailAsync(id, canDetails, ct)`.
- **The integration** (`SensorIntegration`) gets `ReportUrl` (`PublicUrls.ReportUrl`, `{Base}/report`) for the type. The write URL stays, because `/event` works for every type and is frozen. The panel shows the report URL.

### 8.2 Contracts (`Api/V1/Contracts/Advanced.cs`)

- **`AdvancedSensorRequest`:** `NamesPeople`, and `AddStatusRule` (creation only). It goes in `SensorRequest.Advanced`.
- **`AdvancedSensorDetail`:**
  - the setting;
  - `LastReportAt`, `LastValidReportAt` and `LastSource`;
  - `Stale`, and `StaleAt` (when it will be);
  - `Verdict`: `Code`, `Message`, `Failed` and `Warnings`;
  - `Rules`: each an `AdvancedRuleItem` with its latest outcome;
  - `Fields`: each an `AdvancedFieldItem` with its latest value;
  - `Latest` (`AdvancedReportSummary`): its id, when it was received, the repeats and until when, the source, the size, whether it was valid and its error, and the status, message, measured time and clock difference;
  - `Pending`: the reports waiting for the evaluator, usually 0;
  - `CanSeeData`: false for a sensor that names people, when the caller lacks Details.

  It carries no body. The *Data* tab loads that (`reports/latest`), so the sensor's page stays light at its 30 s refresh.
- **`AdvancedRuleRequest` and `AdvancedRuleItem`:** the columns of §5.2. The item adds:
  - `Condition`: the rule in words, such as `disk.free_gb ≥ 5` or `none of jobs[*].state is "failed"`;
  - `Outcome` (§3.2), with `Actual` and `Matches`;
  - `FailingSince`.
- **`AdvancedFieldRequest` and `AdvancedFieldItem`.**
- **`AdvancedReportItem`** is a row of the history, with `Evaluated` false while the report waits for the evaluator. **`AdvancedReportDetail`** adds the `Body` (a raw JSON string) and the `Results`.
- **`AdvancedTestRequest`** (`ReportId?`, `Report?` as raw JSON, `Rules?` as drafts) and **`AdvancedTestResult`**.
- **`AdvancedPathItem`** (`Path`, `Kind`, `Preview`): the latest report's paths, for the editor's suggestions.
- **`AdvancedSamples`:** `From`, `To`, `Step`, the points (`At`, `Min`, `Max`, `Last`), and the thresholds of rules on the same path.

### 8.3 Endpoints (`AdvancedSensorController`, `api/v1/sensors/{id:guid}/advanced`)

*View* means *Monitoring: View*, and *Edit* means *Monitoring: Edit*. *(+ Details)* means that *Monitoring: Details* is also needed when the sensor's data names people.

| Method and path | Right | Audit | What |
|---|---|---|---|
| `GET reports` | View (+ Details) | – | The history, newest first, paged: received, repeats, code, failed and warnings, status, message, size. |
| `GET reports/latest`, `GET reports/{reportId}` | View (+ Details) | – | A report, with its body and its outcomes at the time. |
| `DELETE reports/{reportId}` | Edit | `AdvancedReportDeleted` (warn) | For a report that carried what it shouldn't, such as a password (§13). The latest report can be deleted too, and the verdict stays until the next report. |
| `DELETE reports` | Edit | `AdvancedReportsCleared` (warn) | All reports, and the samples. |
| `GET paths` | View (+ Details) | – | The latest report's paths. |
| `GET samples?fieldId=&range=24h\|7d\|30d` | View (+ Details) | – | A field's chart: per minute for 24 h, and per hour for 7 and 30 days (`date_trunc` over the minutes). |
| `POST test` | View (+ Details for a stored report) | – | §6.6. The body is within §4.2's limits, and each user may test 30 times a minute. |
| `POST rules`, `PUT rules/{ruleId}`, `DELETE rules/{ruleId}` | Edit | the entity's own, on *History* | Validated (§6.3). The evaluator then re-judges the newest report, and the answer waits up to 5 s for it (§7.5). |
| `PUT rules/order` | Edit | the entity's own | `{ ids }`, with every rule once. Re-judged as above, because the message lists rules in their order. |
| `POST rules/copy` | Edit | the entities' own | `{ fromSensorId, replace }`: the rules and fields of another Advanced sensor, appended or replacing these. For an app deployed many times. Re-judged as above. |
| `POST fields`, `PUT fields/{fieldId}`, `DELETE fields/{fieldId}`, `PUT fields/order` | Edit | the entity's own | |

- **The rights' texts** in `Rights.cs` grow:
  - View: *advanced sensors' reports*;
  - Details: *the reports of advanced sensors whose data names people*;
  - Edit: *advanced sensors' rules and fields, and deleting their reports*.
- **The audit catalog** gets `AdvancedReportDeleted` and `AdvancedReportsCleared` (category Sensors, target Sensor, tone Warn), and `AdvancedRulesChanged` and `AdvancedFieldsChanged` for the rules' and fields' endpoints, whose entries carry the entities' field changes. `EndpointAuditTests` covers every endpoint.
- **`openapi.json` and `schema.d.ts`** are regenerated (`tools/npm.sh run gen:api`).

### 8.4 The catalogs

- **`SystemService.StatusCodes()`** lists 295, 296, 495 and 496 with their descriptions. 460's description adds *an Advanced sensor without a report*.
- **`SystemService.SensorTypes()`** gets `ADVANCED`: *"An app POSTs a JSON object to /report with the write token (Authorization: Bearer); PNMT stores it, and its evaluator judges it by the sensor's rules within moments: 295 when every rule holds, 296 when only warning rules don't, 495 when a rule fails, 496 when the report can't be read; with an interval, 460 after Interval + GracePeriod without a report."* `Parameters` is null, `CanCreate` true and `UsesInterval` true.
- **The task table** gets *Advanced sensors*: `LAST_ADVANCED_EVALUATION`, 60 s, always shown.

## 9. Notifications

- **Nothing new in the notification task.** `NotificiationTask` takes the evaluator's events as it takes every event (§1.2). A failing report is judged within about a second of arriving (a nudge), or within 30 s if the nudge is lost. It is notified at the task's next 10 s tick.
- **Nothing new in the notification rules.** With an event per verdict change:
  - *always* tells each change;
  - *on change* also tells 295 ↔ 296 ↔ 495;
  - *failures and back to OK* tells the failure and the recovery;
  - a value falling within the same failure doesn't notify again (§3.3).
- **The message is the verdict's** (§3.1): the failing rules with their values, which is what a person needs at night.
- **Cookie's case book** takes a failing Advanced sensor into its host's case, like any failing sensor. `advanced_sensor_get` (§11) lets Cookie look at the report.

## 10. The web UI

### 10.1 Lookups and helpers

- **`useLookups.ts`:**
  - `typeLabel('ADVANCED')` is *Advanced (JSON)*;
  - `typeIcon` is `pi pi-sliders-h`;
  - `usesGracePeriod` is true for the type.
- **`resources.ts`:**
  - `sensorRequest` carries `advanced`;
  - new functions: `api.advanced.reports`, `report`, `latest`, `deleteReport`, `clearReports`, `paths`, `samples`, `test`, `copyRules`, `rules.*` and `fields.*`.
- **`views/sensors/advanced/advanced.ts`:**
  - paths from a position in the tree, with keys quoted where needed (`formatPath(['jobs', 3, 'state'])` gives `jobs[3].state`);
  - the field formats: bytes, durations, percent, and dates in `TZ`;
  - the operators' labels and their operands' hints;
  - parsing durations, for the hints.

  Tested in `advanced.test.ts`.

### 10.2 *Add sensor* (`SensorFormView.vue` + `advanced/AdvancedFields.vue`)

- **Expect a report every:** *no expectation*, 1 min, 5 min, 15 min, hourly, daily, or a custom interval. **Grace** is preset to a fifth of the interval, at least 1 min. These replace the generic *Interval* and *Grace period* fields for the type.
- **Add a rule for the app's own status:** on by default.
- **Its data names people:** off by default, with the hint *Only people with Monitoring: Details see the reports*.
- **After saving,** the page opens on **Data**. Until the first report arrives, it shows:
  - the URL and the examples, for people with Edit;
  - *Paste a sample*, to set up rules before the app sends anything (the test of §6.6).

### 10.3 The sensor's page (`SensorView.vue`)

**Tabs:** *Overview* · ***Data*** · ***Rules*** · *History*.

- The tab set is `advancedTabs`. The `tab` computed accepts `data` and `rules` for the type, as it does for Zabbix's tabs.
- The *Data* tab carries a `!` badge while the code is 496. The *Rules* tab carries the number of failing rules.

**Overview** is the plain sensor page (configuration, events, uptime, notification rules, notes, the knowledge base and Redmine panels), with these additions:

- **A status strip** (`AdvancedStatusStrip.vue`, like `MikrotikStatusStrip`):
  - *last report 12 s ago from 10.0.0.5 · every 5 min expected*, or *stale since …*;
  - the app's status as a badge, and its message;
  - *6 rules: 5 hold, 1 fails*;
  - *judging 2 reports…* while `Pending` is above 0, which is normally too short to see. If it stays, the evaluator is behind, and *System health* says so too.
- **The pinned fields as cards** (`FieldCards.vue`):
  - each with its label, and its value with the unit;
  - a 24 h sparkline for charted fields;
  - red or amber when a rule on its path doesn't hold.
- **The configuration panel's rows:**
  - the expected interval and grace, as `InlineField`s;
  - *names people*;
  - *Parameters* is hidden.
- **The integration panel** (`SensorUrlPanel.vue`):
  - the report URL, with the header form first;
  - the examples of §4.4: curl, forwarding a health endpoint, PowerShell and Python;
  - the `/event` examples are hidden for the type.

**Data** (`advanced/AdvancedDataTab.vue`) is the new tab (R3):

- **The headline:**
  - when it was received, with *×1 440 until 10:02* for repeats;
  - when it was measured, with the clock hint;
  - where it came from, and its size;
  - the app's `status` as a badge, and its `message`.

  An invalid latest report shows as an error `Message`, with the first 2 KB of what arrived as text.
- **Fields** (`FieldCards.vue`), all of them, not only the pinned ones:
  - each with its chart (`SampleChart.vue`): an SVG polyline with a min–max band, like the latency chart in `UplinkView.vue`;
  - ranges of 24 h, 7 d and 30 d;
  - a rule's threshold on the same path drawn as a line;
  - the value under the pointer.

  *Edit fields* (Edit) opens a list to add, reorder, change and remove fields.
- **The report** (`components/JsonTree.vue`, reusable) shows the whole report, readable with no setup (R2):
  - **objects** as nested sections in the app's order, with the first two levels open;
  - **a list of objects as a table** when its items share at most 12 scalar keys, sortable, with nested values expandable;
  - **an object whose members are objects of the same shape** as a table too, with a row per member and its name in the first column. This turns `jobs`, the IETF `checks`, Actuator's `components` and the HealthChecks writer's `entries` into tables. The IETF `observedUnit` shows next to its `observedValue`;
  - **a list of scalars** as chips, with *+n more* past 50, and lists past 100 items in pages;
  - **times** (ISO 8601) in PNMT's time zone, with *3 h ago* beside them and the original on hover;
  - **booleans** as ✓ and ✗, `null` muted, and health words coloured by the `healthy` mapping;
  - **long or multi-line text** as a block, collapsed past 10 lines;
  - **rule marks:** each value a rule looks at carries the rule's mark (✓, ✗ or !), with the rule's name and outcome on hover;
  - **a filter** over keys and values, and *Changed since the previous report*, which marks the values that differ;
  - **a menu per value** with *Copy path*, *Copy value*, and for Edit:
    - **Add rule…** opens the editor with the path, the value's natural operator and the current value: a number gets `≥` and its value, a status word `healthy`, a time `older_than 1d`;
    - **Add field…**;
  - ***Raw***: the JSON as received, pretty-printed and highlighted (highlight.js, as in the knowledge base's code blocks), with *Copy* and *Download*.
- **History** (`ReportHistory.vue`, a `ListTable`):
  - the columns: received, repeats, code (*judging…* until the evaluator has judged the report), failed and warnings, status, message, size;
  - a row opens that report in the tree, with its outcomes at the time;
  - *Compare with the previous* opens `DiffView` over both, pretty-printed;
  - *Delete* per row and *Delete all reports* in the menu (Edit), each confirmed.
- **When the data names people and the viewer lacks Details,** the tab shows the verdict and the rules' outcomes without the actual values, and says *The data of this sensor names people: Monitoring: Details*.

**Rules** (`advanced/AdvancedRulesTab.vue`):

- **The list** (`ListTable`):
  - the columns: order (drag, or up and down), name, the condition in words, severity, *with the last report* (✓ holds, ✗ fails or ! warns, with the actual value), failing since, and on;
  - per row (Edit): edit, duplicate and delete;
  - empty: *No rules yet. Pick a value on the Data tab, or add one here.*
  - in the menu (Edit): *Copy rules from another sensor…*
- **The editor** (`RuleEditor.vue`, a dialog):
  - the name;
  - the path, with suggestions from the latest report's paths (`GET paths`) and their current values;
  - the operator, grouped as in §6.3 and narrowed to the value's kind. Every operator stays reachable;
  - the operand fields the operator needs, with their hints;
  - *for several matches*, shown only for paths with `*`;
  - *when missing*, the severity, the note, and on.

  **Below, live:** *With the last report: `disk.free_gb` = 3.2 → fails (must be at least 5)*, from `POST test` while one types.
- **Try** (`RuleTester.vue`): the latest report, a stored one, or pasted JSON, against all the rules. It shows each outcome and the verdict the sensor would have. Nothing is stored.

### 10.4 Elsewhere

- **The host page, the sensor lists and the dashboard:** nothing to change. The type's label, icon and verdict message show as for any type.
- **The *Documentation* page:**
  - the codes and the type come from the catalogs;
  - `DocsView.vue`'s sensor API section gets `/report`, its answers, and the format in short;
  - docs/advanced-report.md is the full reference.

## 11. Tools, seed, docs

- **The MCP tool `advanced_sensor_get`** (Monitoring: View, read) returns:
  - the verdict;
  - the rules, with their conditions and outcomes;
  - the fields, with their values;
  - the latest report's headline.

  The body (pretty-printed, at most 8 000 characters) comes only when the sensor doesn't name people, or the caller has Details. All of the app's text goes through `ToolSafety.WithoutCredentials` and `ctx.UntrustedOrNull`, as sensor messages do.
- **`MonitoringWriteTools.CreatableTypes`** gets `ADVANCED`, because it needs nothing set up elsewhere. Rules are made in PNMT, as notification rules' recipients are (a rule-writing tool is §16).
- **The seed** (`SEED=1`, a new `DevSeedAdvanced`) adds five sensors on the demo hosts, with 7 days of reports and samples, so the tabs and charts have content:
  - ***Nightly backup*** (daily; fields for size, duration and last finished; rules for the status, the age and the errors) is **OK**;
  - ***Order queue*** (IETF health format, every minute; *queue length at most 500* as a warning) **warns (296)**;
  - ***Disk report*** (free space at least 5 GB) **fails (495)**;
  - ***Payroll export*** **names people**;
  - ***Label printer*** is **stale (460)**.
- **Docs:**
  - **docs/advanced-report.md**, for app developers: the endpoint, the token, the body and its limits, the answers, examples in curl, PowerShell, Python, Node and C#, the health formats, and what rules can do;
  - **README:** a bullet under the sensor features, and a curl line under *Reporting to PNMT*;
  - **deploy/README.md:** the `Advanced__*` settings, and the reports in the backups;
  - **docs/release-notes-1.14.md**;
  - **this plan's §17** once built.

**Options** (`AdvancedOptions`, `Advanced__*`):

| Setting | Default |
|---|---|
| `MaxReportBytes` | 262 144 |
| `MinSecondsBetweenReports` | 5 |
| `KeepReports` | 500 |
| `KeepDays` | 30 |
| `MaxBytesPerSensor` | 52 428 800 |
| `SampleDays` | 30 |
| `EvaluateSeconds` | 30 |

The structural limits are constants: depth 32, 50 rules, 30 fields with 10 of them charted, 16 path segments, 1 000 matches, and the 100 ms pattern timeout.

## 12. Tests

- **`AdvancedPathTests`:**
  - parsing and formatting round trips;
  - brackets, escapes, Unicode keys, and `$.`;
  - indexes, negative ones included;
  - `*` on lists and on objects;
  - errors, with their positions;
  - the limits of 16 segments and 1 000 matches.
- **`AdvancedVerdictTests`** (pure):
  - **operators:** each operator with each JSON kind (number, numeric string, text, boolean, null, list, object, missing);
  - **quantifiers** with 0, 1 and many matches; *when missing*; severity;
  - **`healthy`'s words:** IETF pass/warn/fail, Spring UP/DOWN, ASP.NET Healthy/Degraded/Unhealthy;
  - **ages:** at the boundary with a fake `now`, with offsets, and with Unix seconds and milliseconds;
  - **change operators,** including repeats and the first report;
  - **precedence;**
  - **messages:** values cut, control characters removed, the 1000-character limit with *… and n more*;
  - **keys:** a changed value keeps the key, and another failing rule changes it.
- **`ReportEndpointTests`** (`TestApp`):
  - **the token:** in the header and in the path;
  - **the sensor:** another type gets 409; an unknown token or a disabled sensor gets an empty 404;
  - **the answer:** 202 with the preview. The endpoint itself writes no event, no sample and no verdict column, and the report is queued (`EvaluatedAt` null);
  - **invalid reports:** invalid JSON gets 422 and an invalid row, which the evaluator turns into 496;
  - **size:** too large gets 413, by `Content-Length` and by a chunked body;
  - **the body's form:** gzip; a key twice; too deep; an array at the root;
  - **the rate limit:** 429 with `Retry-After`;
  - **repeats** fold into one row, and send it back to the queue;
  - **the nudge:** a report is judged within a second, without waiting for the timer;
  - **`dryRun`** stores nothing and nudges nothing;
  - **no session:** no `Set-Cookie`, and no session even when a cookie is sent;
  - **the frozen API:** `/event` with the same token still works, and the contract replay is unchanged.
- **`AdvancedEvaluatorTests`** (`TestApp`, the task's run called directly, with a fake clock):
  - **the queue in order:** pass, fail, pass arriving between two runs give two events, the failure and the recovery, with each report's own `Results`;
  - **quiet:** three identical failing reports write one event and send one notification (an *only failures* rule, with simulated sending);
  - **as of receipt:** a backlog judged after a "restart" uses each report's receipt time for its age rules, and its samples land in the receipt's minutes;
  - **durable:** reports stored without a nudge, which stands in for a lost one or a restart, are judged at the next timer run;
  - **staleness:** 460 at exactly interval plus grace; none for a report that is stored but not judged yet; none during the startup grace or while the uplink is down, while reports that arrive in that time are still judged;
  - **ages:** an age rule fails between reports, at the right time; back to OK with the next report;
  - **re-evaluations:** a rule change, with the answer waiting for the pass; an event only when the key changes;
  - **`/event`:** an event posted there is followed by the verdict again at the next report;
  - **errors:** a sensor whose work throws doesn't stop the others; a report that throws three times becomes 496 `internal`, and the reports after it are judged;
  - **the work set:** a sensor without an interval, age rules, a queue or a request isn't read.
- **`AdvancedSensorTests`** (API):
  - creating, with and without the starter rule;
  - validation: each operator's operand, paths, and the limits (50 rules, 30 fields, 10 charted);
  - reordering, and copying rules;
  - the type changed away deletes everything, and deleting the sensor cascades;
  - a rule change answers with the new outcomes;
  - *History* lists the rule changes;
  - **names people:** a Viewer gets 403 on reports, paths and samples, and sees the outcomes without actual values;
  - deleting reports is audited;
  - retention keeps the newest two;
  - samples per minute (min, max, last, count), and the hourly aggregation for 7 days.
- **`EndpointAuditTests`** cover the new endpoints. **`MonitoringApiTests.ReferenceDataForTheDocs`** checks that the four codes are listed, with 295 and 296 as successes.
- **Web UI:**
  - `advanced.test.ts`;
  - `JsonTree.test.ts`: tables, chips, times, paging, rule marks and the filter. A value of `<img src=x onerror=…>` renders as text;
  - `RuleEditor`: operators narrowed to the kind, operand hints, and the live outcome (mocked).
- **E2E (`e2e/advanced.spec.ts`):**
  1. create the sensor;
  2. POST a report through Playwright's `request`, and see it on *Data*;
  3. *Add rule…* from a value;
  4. POST a failing report, and see 495 and the strip;
  5. see the failing rule on *Rules*;
  6. fix the report, and see 295;
  7. compare in the history.

  `pages.spec.ts` takes screenshots of *Data* and *Rules*.

## 13. Security and privacy

- **The write token is the only key,** as for `/event`. Rotating it (`POST /sensors/{id}/tokens/rotate`) stops the app at once.
- **The header form keeps the token out of access logs** (R14). The path form exists for tools that can't set a header, as on `/event`.
- **No CORS:** a browser page can't post a report, because a token doesn't belong in a browser. docs/advanced-report.md says so.
- **Every cost is bounded:**
  - the size (256 KiB), the depth (32) and the rate (one report per 5 s per sensor);
  - 50 rules, 1 000 matches per rule, and 100 ms per pattern;
  - storage per sensor: 500 reports, 30 days and 50 MiB.

  An app can fill only its own sensor's share.
- **Nothing the app sends is rendered as HTML.** Values go through Vue's text bindings, never `v-html`. The raw view's highlighting escapes as the knowledge base's code blocks do. Links in values aren't made clickable.
- **The app's text is untrusted elsewhere too:** in messages it is cut and stripped of control characters (§3.1), and in the tools it is marked and stripped of credentials (§11).
- **Data that names people** is shown only with Details (R13), and the tools withhold it in the same way.
- **A report that carried a secret can be deleted,** one or all, and each deletion is audited. The reports are in PNMT's backups (`pg_dump`), and deploy/README.md says so.

## 14. Performance

- **The request** costs one short transaction: an insert, or an update for a repeat, plus an update of the side row and a trim. Rules, samples and events are no longer on the request's path. At the rate limit, 500 sensors send at most 100 reports a second, which PostgreSQL handles easily.
- **The evaluator** costs one transaction per sensor with work:
  - it parses each queued report once and walks it once per rule;
  - it writes a few upserts of samples, the report's verdict, and the side row's verdict;
  - it writes an event only on a change.

  It finds the queue through the partial index. In the timer's pass it parses a body only when an age rule needs it, since staleness needs only `LastReportAt`.
- **The sensor's page** reads the side row, the rules, the fields and the latest report's `Results` (`jsonb`), but never a body. The body loads only on the *Data* tab.

## 15. Decisions and open questions

1. **The type is `ADVANCED`, labelled *Advanced (JSON)*,** as the request names it. The code's classes are `Advanced*`.
2. **Reports have a path of their own, `/report`,** not under `/event` (frozen) and not under `/api/v1` (the UI's). They use the existing write token, in a header or in the path (§4.1).
3. **The body is free JSON,** with three optional keys PNMT reads (§4.2). Display hints live in PNMT, as fields, not in the payload: any existing JSON then works without changing the app. The IETF format's `observedUnit` is the one hint read from a payload.
4. **An event per verdict change,** with the values at the change in its message (§3.1, §3.3). The *Data* tab and the history show every report.
5. **One task, the evaluator, judges every report and writes every code** (§7). The endpoint only stores reports, and the existing tasks react to the codes as they do for any sensor. This follows `PingCheckTask`, `TimespanConditionsCheckTask` and the Zabbix poller. It is also why staleness is the evaluator's, from the last report's time, reusing 460: `HeartbeatCheckTask` reads events, which this type writes only on a change.
   - **Every report is judged, in order,** not only the newest, so a failure that lasts one report still leaves its events.
   - **A nudge starts the work at once,** and the queue in the database (`EvaluatedAt`) makes it durable.
   - **The cost** is that the HTTP answer can only *preview* the verdict (202, §4.3), and the code follows about a second later.
6. **A warning is a success code (296), not a state** (as the SSL plan's decision 4).
7. **An invalid report is stored and fails the sensor (496),** and the app gets 422. An app that breaks its own JSON shows in PNMT, not only in its own logs.
8. **All rules must hold,** and there is no expression language. *When missing: pass* covers optional parts.
9. **A value that disappears fails by default** (`WhenMissing = Fail`).
10. **Reports are stored as text, not `jsonb`,** to keep the app's order (§5.4).
11. **Identical reports fold into one row** with a count (§7.2).
12. **"Names people" is per sensor,** and gates the data behind *Monitoring: Details*.
13. **Copying rules from another sensor is in scope** (§8.3). Shared templates are later.
14. **Open:** the retention defaults: 500 reports, 30 days and 50 MiB per sensor, and 30 days of samples. Proposed: as listed. Busy sensors reach 500 reports in a few hours unless they repeat.
15. **Open:** is the starter rule on by default? Proposed: yes. It does nothing without a `status` key.
16. **Open:** is a rate limit of one report per 5 s enough? Some apps send bursts when a job ends. Proposed: 5 s as on `/event`, configurable.
17. **Open:** `?dryRun` on the public endpoint. Proposed: yes, counted in the rate limit. It saves app developers a sensor for tests.
18. **Open:** rules with OR, or conditions ("only when `mode` is `production`"). Proposed: later, if a real case needs them.
19. **Open:** should the event message carry the app's own `message` too? Proposed: no, because it changes with every report, and a stable message is what keeps the sensor quiet (§3.1).

## 16. Later

- **Pull:** PNMTD fetches a JSON URL at the interval (an app's `/health`, with headers) and judges it with the same rules. This would add a source column (*push* or *pull*) and a URL. The cron example of §4.4 covers the need until then.
- **GET with query parameters** (`/report/<token>?temp=21.5&door=closed` as a flat object), for devices that can only call a URL (Shelly actions, simple IoT).
- **Several sensors in one request,** as ENCAPSULADED does for codes.
- **UDP DATA as JSON:** a UDP node's DATA payload that is JSON, judged by the same rules (docs/udp-iot-plan.md U7).
- **An agent's script output as a report:** a Bash or PowerShell script on a server whose JSON output goes to an Advanced sensor, with no token on the server.
- **Mail input:** a JSON body or attachment as a report.
- **Rule templates** shared by many sensors, changed once.
- **Rules as child sensors,** for notifications and uptime per rule.
- **AI-suggested rules** from the latest report, as a decision, screened, and never for data that names people.
- **A rule-writing tool** for MCP and Cookie, once rules proposed by an AI have a review step.
  - *Built for MCP without a review step (docs/mcp-extensions-plan.md §7, 2026-11-24): `advanced_rules_change` and `advanced_fields_change`, whose answer is the latest report judged again. Cookie still doesn't write rules.*
- **Computed values** (`disk.free_gb / disk.total_gb`), and OR groups.
- **JSON Schema as a rule:** *the report matches this schema*.
- **The global search** over the latest reports' values.
- **Reading the latest report with the read token** (`GET /report/<read token>`).

## 17. Build order, and what was built

1. **A1. Model:**
   - `SensorType.ADVANCED`, the codes and `LAST_ADVANCED_EVALUATION`;
   - the entities and the migration `AdvancedSensors`, with `dotnet ef migrations has-pending-model-changes` clean;
   - `AdvancedOptions`;
   - the catalogs in `SystemService`.
2. **A2. Paths and verdict** (pure):
   - `AdvancedPath`, the operators, and `AdvancedVerdict.Evaluate`;
   - `AdvancedPathTests` and `AdvancedVerdictTests`;
   - settle `AllowDuplicateProperties` here (§4.2).
3. **A3. Receiving and the evaluator:**
   - `ReportPaths`, `ReportEndpoint`, and `AdvancedReports.StoreAsync` with repeats, trimming and the preview;
   - `AdvancedEvaluator`: the nudge, the queue in order, re-evaluations, the timer's pass, samples, events, errors, and the maintenance;
   - `ReportEndpointTests` and `AdvancedEvaluatorTests`;
   - the contract replay green.
4. **A4. Service and API:**
   - the `SensorService` hooks, and `AdvancedSensorService`;
   - the controller, testing and copying;
   - the audit events and the rights' texts;
   - `openapi.json` and `schema.d.ts`;
   - `AdvancedSensorTests` and `EndpointAuditTests`.
5. **A5. Web UI:**
   - the lookups and the form's fields;
   - the strip and the field cards;
   - `JsonTree` and the *Data* tab, with the history and the comparison;
   - the *Rules* tab, with the editor and the tester;
   - the sample chart;
   - unit tests.
6. **A6. Tools, seed, docs, e2e:**
   - `advanced_sensor_get` and `CreatableTypes`;
   - the seed;
   - docs/advanced-report.md, `DocsView`, README and deploy/README.md;
   - `advanced.spec.ts` and the screenshots;
   - the release notes for 1.14.0. A migration means a minor version, per the release recipe.

### 17.1 What was built, and where it differs

All six steps, in `PNMTD/Advanced/` (the core, the endpoint, the evaluator and the service), `PNMTD/Api/V1/AdvancedSensorController.cs`, `PNMTD/Models/Db/AdvancedEntities.cs`, `PNMTD/Cli/DevSeedAdvanced.cs`, `PNMTD.Tests/Advanced/` and `PNMTD.WebUI/src/views/sensors/advanced/` with `components/JsonTree.vue`. The SSL certificate sensors (1.13) were built first, with exactly the numbers of §1.3, so this type keeps 130, 295/296/495/496 and 320.

- **`AllowDuplicateProperties = false`** works in .NET 10's `JsonDocument` (§4.2): no walk of its own.
- **The evaluator** is a singleton (`AdvancedEvaluator`) with a pump that a nudge starts, and a hosted timer (`AdvancedEvaluatorTask`) that only `BackgroundTasks` starts. Nudges work without the timer, so the integration tests see a report judged within a second. `Advanced__Nudge=false` leaves reports to the timer, for the tests of the queue itself.
- **A rule change waits for the evaluator** through `RequestAsync(sensorId, wait: true)` even with nudges off. Rule and field changes take the sensor's advisory lock, so the evaluator never judges with half a change.
- **The side row's `RuleStates`** keeps each rule's latest outcome with its values, not only the outcome and since when: the *Rules* tab and the field cards read it without a report's `Results`. The timer's pass keeps it current for age rules.
- **Field changes** request a re-evaluation too, so a new field shows its value at once. The verdict can't change by it, so no event comes.
- **The 460 hold-back** (§7.3) skips a stale sensor entirely while held, rather than judging it without staleness, so an existing 460 isn't undone by the hold. A rule change during the hold stores the outcomes and writes no event.
- **Events written in one instant** (a backlog judged in one run) are a microsecond apart, so their order is the reports' order.
- **Messages of sensors whose data names people** name the rules without the values (§3.1). The preview in the HTTP answer still has them: the token's holder sent them.
- **`POST /report` without a token** answers 401 `no_token` rather than an empty 404 (§4.3): nothing about a token is revealed, and it tells a developer what is missing.
- **Times on the *Data* tab are in the browser's zone,** as everywhere in the UI (`format.ts`); the messages, which reach mails and Pushover, keep PNMT's `TZ` (§3.1).
- **The tree keeps the app's order** even for keys that look like numbers, which JavaScript's objects would reorder: `components/jsonTree/jsonTree.ts` parses with an order of its own, and *Raw* re-indents token by token, so `3.20` stays `3.20`. Highlighting renders Vue nodes, never an HTML string.
- **The open questions of §15** took their proposals: the retention defaults, the starter rule on by default, 5 s between reports (configurable; the e2e stack sets 0), `?dryRun` counted in the rate limit, no OR, no app message in the event.
- **Tests:** 95 in `PNMTD.Tests/Advanced/` (paths, the verdict, the endpoint, the evaluator with a fake clock, the API, the tool), `EndpointAuditTests`, `MonitoringApiTests` and `DevSeedTests` extended; the web UI's unit tests in `views/sensors/advanced/` and `components/JsonTree.test.ts`; `e2e/advanced.spec.ts`.
