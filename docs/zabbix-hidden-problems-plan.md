# Plan: Zabbix's problems as Zabbix shows them

| | |
|---|---|
| Status | Built (§11). Released in 1.22.1. |
| Date | 2026-11-23 |
| Scope | The Zabbix poller (`PNMTD/Zabbix/ZabbixSnapshot.cs`), its settings, the counts that name the hidden problems, the fake Zabbix and its fixtures. |
| Goals | <ol><li>A problem Zabbix doesn't show isn't in PNMT either: not in the lists and counts, the ZABBIX sensors, Cookie's case book, the lounge or the MCP tool. These are problems whose trigger, host or item is disabled, or whose trigger depends on another trigger in problem.</li><li>`trigger.get` only for the triggers of open problems: **the shown ones every poll, the hidden ones every hour**.</li><li>Both rules and the interval set in `.env`.</li></ol> |
| Not in scope | Zabbix's cause and symptom problems (6.4 on, §10). Listing the hidden problems in PNMT (§10). A problem's detail by event id (§3.6). |

---

## 1. Today

### 1.1 PNMT

- **`ZabbixPoller.PollAsync`** (`ZabbixSnapshot.cs:178-219`) runs every `Zabbix__PollSeconds` (60) as a background service. It sends one `problem.get` (`recent: false`, the host groups), then `trigger.get` **only for triggers it hasn't seen yet, and only for their hosts**. The hosts are cached until the trigger has no open problem left.
- **Nothing checks the trigger's status.** The snapshot is replaced with every problem `problem.get` returned.
- **Every consumer reads that snapshot:** the problem list, the summary and its counts, the host list and host pages, the ZABBIX sensors (`ZabbixSensors.EvaluateAsync`), Cookie's case book (`CaseBook.ProblemsAsync`), the lounge (`LoungeGround`), and the MCP tool `zabbix` (`IntegrationReadTools.ZabbixAsync`).

### 1.2 Zabbix

**Disabling a trigger doesn't close its open problem.** The problem stays in Zabbix's `problem` table, so `problem.get` keeps returning it. The frontend hides it: `CScreenProblem::getData()` (6.0 and 7.0 alike) is behind both *Monitoring → Problems* and the *Problems* widget. It checks every problem's trigger and drops the problems whose trigger doesn't come back:

```php
API::Trigger()->get([
    'triggerids'    => array_keys($triggerids),   // the problems' objectids
    'monitored'     => true,
    'skipDependent' => ($filter['show'] == TRIGGERS_OPTION_ALL) ? null : true,
    ...
]);
foreach ($problems as $eventid => $problem)
    if (!array_key_exists($problem['objectid'], $data['triggers']))
        unset($problems[$eventid]);
```

- **`monitored`:** "Return only enabled triggers that belong to monitored hosts and contain only enabled items."
- **`skipDependent`:** "Skip triggers in a problem state that are dependent on other triggers." A trigger depended on is ignored if it is disabled, has disabled items or has disabled item hosts.

| The problem's trigger… | Zabbix | PNMT today |
|---|---|---|
| is disabled | hidden | shown |
| is on a disabled host | hidden | shown (a ZABBIX sensor on that host gives 491 anyway) |
| uses a disabled item | hidden | shown |
| depends on a trigger that is in problem | hidden | shown |

## 2. The rule

- **A problem is shown when Zabbix's frontend would show it:** its trigger comes back from `trigger.get` with `triggerids`, `monitored: true` and `skipDependent: true`. PNMT passes the flags and Zabbix decides, so PNMT doesn't copy Zabbix's rules.
- **Hidden is per trigger:** all of a hidden trigger's open problems are hidden.
- **A trigger the token can't read is hidden too.** Today its problem is shown without a host. Zabbix's frontend wouldn't show it to that user either.
- **Each flag can be switched off** (§4).

## 3. The design

### 3.1 What a poll asks

1. **`problem.get`**, as today.
2. **The triggers to check:**

   | The trigger | Checked |
   |---|---|
   | New: none of the last poll's problems had it | this poll |
   | Shown at its last check | **every poll** |
   | Hidden, with a problem the last poll didn't have | this poll |
   | Hidden otherwise | when `Zabbix__HiddenRecheckMinutes` (60) have passed since its last check |
   | No open problem any more | forgotten |

   A disabled trigger makes no new events, so a new problem on a hidden trigger means Zabbix evaluates it again, for instance because it was re-enabled. That problem is checked at once rather than an hour later.

3. **One `trigger.get` per 500 of them:**

   ```json
   { "triggerids": ["…"], "output": ["triggerid"], "selectHosts": ["hostid", "host", "name"],
     "monitored": true, "skipDependent": true }
   ```

   The hosts come with every answer, so a renamed host is renamed in PNMT within a poll. Today it would only change after its problems closed.
4. **What comes back is shown,** with its hosts. **What was asked for and didn't come back is hidden.** Either way, the check time is now (`Clock.UtcNow`).
5. **The snapshot holds the problems of the shown triggers,** plus the number of hidden problems.

### 3.2 The state

- `triggerHosts` becomes `Dictionary<string, TriggerCheck>`, with `sealed record TriggerCheck(bool Shown, IReadOnlyList<ZxTriggerHost> Hosts, DateTime CheckedAt)`, plus a `HashSet<string>` of the last poll's event ids.
- **It lives in memory, as today.** After a restart every trigger is new, so the first poll checks them all.

### 3.3 When a call fails

- **A failed `trigger.get` fails the whole poll,** as a failed `problem.get` does now: the snapshot stays as it was, the error is recorded and `ZabbixHealth` counts the failure. A snapshot is never half checked.
- The poll builds the new trigger states in a new dictionary and swaps it in only after the poll succeeds.

### 3.4 The snapshot

- `ZabbixSnapshot.State` gets `int Hidden`, and `Replace(problems, hidden, asOf)` takes it.
- `Problems` holds only the shown problems, so no consumer has to filter anything.

### 3.5 What changes for whom

| Where | After |
|---|---|
| Problem list, summary, host list, host pages | Without the hidden problems. The summary gets `hidden`. |
| ZABBIX sensors | Evaluated without them. **A sensor that fails only because of hidden problems writes 290** at the first poll after the update, and that sends its recovery notification. |
| Cookie's case book | The hidden problems leave their cases. A case made only of hidden problems closes: *"Everything is fine again."* |
| Lounge | Its counts are without them. |
| MCP tool `zabbix` | Without them. Its answer gets `hidden`, so an agent knows there are more problems in Zabbix's table. |
| *Test* on *System health → Integrations* | *"120 hosts, 14 open problems, 3 hidden as in Zabbix"* (`ZabbixTestResult.Hidden`). |

### 3.6 What stays

- **`GET /zabbix/problems/{eventId}`** still asks Zabbix directly. It is only opened from a row or a link, and a link to a hidden problem still opens.
- **The role's allow list:** `trigger.get` is already on it (`deploy/README.md`, *Zabbix*).
- **The host sync**, and its 491 for a disabled host.

### 3.7 The UI

- **The overview's bar:** next to *Count suppressed (n)*, a muted *n hidden as in Zabbix*, only when n > 0. Its title: *"Their trigger, host or item is disabled in Zabbix, or the trigger depends on one in problem. Zabbix doesn't show them either."*
- **`IntegrationsPanel.vue`:** the Test result's line as in §3.5.
- **No switch to list them.** Zabbix has none either (§10).

## 4. Settings

| Setting | Default | Meaning |
|---|---|---|
| `Zabbix__HideUnmonitored` | `true` | Hide problems whose trigger, host or item is disabled (`monitored`). |
| `Zabbix__HideDependent` | `true` | Hide problems of triggers that depend on a trigger in problem (`skipDependent`). |
| `Zabbix__HiddenRecheckMinutes` | `60` | How often a hidden trigger is checked again. At least 1; a value below the poll interval means every poll. |

- **Shown triggers follow `Zabbix__PollSeconds`** (60, at least 30) and have no setting of their own.
- **With both rules `false`,** only triggers the token can't read are hidden. `trigger.get` still runs every poll, for the hosts.
- As with every `.env` setting, a change takes a restart.

## 5. Load

| Call | Today | After |
|---|---|---|
| `problem.get` | 1 per poll | 1 per poll |
| `trigger.get` | for new triggers only | 1 per 500 triggers that are shown, new or due, per poll |

- **Fewer than 500 open problems:** one more small call a minute, asking only for ids and hosts.
- **At `MaxProblems` (10,000):** at most 20 calls per poll.

## 6. Changes

| File | Change |
|---|---|
| `PNMTD/Zabbix/ZabbixOptions.cs` | `HideUnmonitored`, `HideDependent`, `HiddenRecheckMinutes`, and `HiddenRecheck` as a `TimeSpan`. |
| `PNMTD/Zabbix/ZabbixSnapshot.cs` | `TriggerCheck` and the poll of §3.1–3.3. `State.Hidden`. The class comments. |
| `PNMTD/Zabbix/ZabbixService.cs` | `SummaryAsync` passes `Hidden`; `TestAsync` returns it. |
| `PNMTD/Api/V1/Contracts/Zabbix.cs` | `ZabbixSummary.Hidden`, `ZabbixTestResult.Hidden`. |
| `PNMTD/Tools/Read/IntegrationReadTools.cs` | `hidden` in the `zabbix` tool's answer. |
| `PNMTD/Dev/FakeZabbix.cs` | `trigger.get` honours `monitored` and `skipDependent` (§7.1). |
| `PNMTD/Dev/Fixtures/zabbix-{60,70,80}/trigger_get.json`, `problem_get.json` | The hidden cases of §7.1, **written by hand**, not recorded as in Z0. |
| `PNMTD.WebUI/src/api/openapi.json`, `schema.d.ts` | Regenerated (build, then `tools/npm.sh run gen:api`). |
| `PNMTD.WebUI/src/views/zabbix/ZabbixView.vue` | The overview's count (§3.7). |
| `PNMTD.WebUI/src/views/admin/IntegrationsPanel.vue` | The Test result's line. |
| `deploy/.env.example` | The three settings, commented, after `Zabbix__HostSyncMinutes`. |
| `deploy/README.md`, *Zabbix* | A bullet *"As in Zabbix: …"* with §2 in short. The *Load* bullet as in §5. |
| `docs/audit-redmine-zabbix-plan.md` §7.3 | One line pointing here. |
| `docs/release-notes-<next>.md` | *Zabbix: problems Zabbix hides (disabled trigger, host or item, or a dependent trigger) are hidden in PNMT too, and ZABBIX sensors don't count them.* |

## 7. Tests

### 7.1 The fake and its fixtures

- **Fixtures:** each version gets four more triggers and their problems, at High so that the ZABBIX sensor on web01 would count them, with ids following the recorded ones:
  - a disabled trigger (`"status": "1"`) on web01;
  - a trigger on old01, the disabled host. It must be in *PNMT Servers*, or the `groupids` filter drops it before the check;
  - a trigger with a disabled item (`"items": [{ "status": "1" }]`) on web01;
  - a trigger on sw-core that depends on *Port 12 down* (`"dependencies": [{ "triggerid": "…" }]`).
- **None of the recorded problems is on a disabled host,** so every count the tests check today stays the same.
- **`monitored`:** the trigger's `status` is `"0"` (a missing status counts as `"0"`), every host it names has `status` `"0"` in the host fixture, and every item it lists has `status` `"0"`.
- **`skipDependent`:** hide a trigger when any trigger it depends on, followed recursively, has an open problem in `Problems` and is itself monitored. A disabled master hides nothing, as in Zabbix.

### 7.2 New tests in `ZabbixTests`

| Test | Checks |
|---|---|
| `ProblemsZabbixHidesStayOutOfPnmt` (6.0, 7.0, 8.0) | The four hidden problems are in none of these: the problem list, the summary's counts, the host page, the host list's counts, the MCP tool. `summary.Hidden` is 4. The `trigger.get` call carries `monitored` and `skipDependent`. |
| `ShownTriggersEveryPollHiddenOnesHourly` | Poll 1 asks for all 8 triggers; poll 2 only for the 4 shown ones. A shown trigger disabled in the fake is hidden at the next poll. A hidden trigger enabled in the fake stays hidden until the clock is 60 minutes on, then the next poll shows it. |
| `ANewProblemOfAHiddenTriggerIsCheckedAtOnce` | A new event id on a hidden trigger, now enabled in the fake, is shown at the next poll. |
| `TheRulesCanBeSwitchedOff` | With `HideUnmonitored` and `HideDependent` `false`, all 8 problems are shown and neither flag is sent. |
| `ASensorDoesntCountHiddenProblems` | The sensor on web01 counts neither the disabled trigger's problem nor the disabled item's. Disabling its last counted trigger in the fake turns it 290 with one event. |
| `AFailedTriggerCheckKeepsTheSnapshot` | `trigger.get` fails; the snapshot and its `AsOf` are unchanged, and the error is recorded. |

- **The fake clock** starts on a whole second, read once. Linux's 100-ns ticks against PostgreSQL's microseconds otherwise fail only on CI.
- **The other suites using the fake** (`McpToolsTests`, Cookie's `IntegrationsTests` and `CookieActingTests`, `DevSeedTests`, the e2e `zabbix.spec.ts`) need no changes. If one fails, it saw a hidden problem, which is the bug.

## 8. Steps

1. The fake and the fixtures (§7.1), and the tests of §7.2, failing.
2. The settings and the poll (§3.1–3.4); the tests pass.
3. The contracts, the MCP tool, the regenerated API types, the two views.
4. `.env.example`, `deploy/README.md`, §7.3 of the old plan, the release note.
5. **A lab run** with `dotnet run` on the test PG against the fake Zabbix: the overview shows *4 hidden as in Zabbix*, and the problems tab lists 4 problems.
6. **Against the real Zabbix:** the problem of the disabled trigger is gone from PNMT within a minute of the update, and the overview's count matches *Monitoring → Problems*.

## 9. Risks

- **A hidden problem comes back up to an hour late.** This happens when its trigger is re-enabled and the problem is still open, or when the trigger a dependent problem waits on resolves. Zabbix shows these at once. This was the choice made for this plan: `Zabbix__HiddenRecheckMinutes` shortens the wait, and a new event on a hidden trigger is checked at once (§3.1). §10 has a cheaper fix for the dependent case.
- **The first poll after the update** turns ZABBIX sensors that fail only on hidden problems to 290, with their notifications. It also closes Cookie cases made only of hidden problems, and any duty waiting on such a case follows the closing. This is the fix working, but it can be a burst: say so in the release note.
- **The fixtures' hidden cases aren't recordings.** The fake only stands in for Zabbix's filter; Zabbix itself applies `monitored` and `skipDependent`. Step 6 checks against a real 7.0. Both flags exist in `trigger.get` from 6.0 through 8.0.

## 10. Later, if wanted

- **Bringing a dependent problem back at once:** when a shown trigger's problems resolve in a poll, also check the hidden triggers that poll. It costs one call in polls where something resolved.
- **Listing the hidden problems with the reason:** a second `trigger.get` without the flags, with `status`, the hosts' and items' status and `selectDependencies`, behind a switch like *Suppressed*.
- **Cause and symptom problems** (Zabbix 6.4 on): the frontend asks `problem.get` for `symptom: false` and folds the symptoms under their cause, while PNMT lists them as rows of their own. This needs a version gate like `HostGroupsFrom`, and a decision on whether symptoms count.

## 11. As built

Built as planned (§3–§7), with these differences and additions:

- **`ZabbixPoller.Forget()`**, for the tests: the poller is a singleton, so the trigger states it remembers would outlive a test.
  `UseVersion` calls it together with `ZabbixSnapshot.Clear()`, as after a restart.
- **`FakeZabbix.Broken`** names a method that fails with Zabbix's application error, for `AFailedTriggerCheckKeepsTheSnapshot`.
- **The Test's audit entry** names the hidden problems too: *"Zabbix 7.0.32; 4 hosts, 3 problems, 4 hidden as in Zabbix; …"*.
- **The fixtures' hidden cases** (each version, ids after the recorded ones): *Free inodes on / below 10 %* (disabled trigger, web01),
  *Backup older than 2 days* (old01, the disabled host), *Fan speed too low* (disabled item, web01), and *HTTP service is down*
  (web01, depending on the core switch's *Port 12 down*, which is outside PNMT's host groups, as a master in another group can be).
  All four are High, except the dependent one, which is Disaster.
- **The MCP tool** has no test of its own: it reads the same snapshot that the tests check, and adds `hidden` to its answer.
- **The release note** is the section *1.22.1* in docs/release-notes-1.22.md.
- **Checked:** the Zabbix tests (17 before, 25 after, on 6.0, 7.0 and 8.0); a lab run with `dotnet run` on the test PostgreSQL
  against `pnmtd dev fake-integrations` (summary `hidden: 4`, three problems listed, Test `"problems":3,"hidden":4`); the browser
  test of the Zabbix pages. Not yet checked: a real Zabbix (§8, step 6).
