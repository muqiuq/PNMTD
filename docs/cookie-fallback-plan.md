# Plan: Cookie without Redmine or Zabbix, and a chat that knows what Cookie does

| | |
|---|---|
| Status | **Built** (§12), F1 to F4. **Released in 1.9.0.** |
| Date | 2026-11-16 |
| Scope | Two corrections to Cookie (docs/cookie-plan.md): **(1) the chat says what Cookie does from its cases** instead of denying it, and **(2) when Redmine or Zabbix isn't available, Cookie leaves them out:** no tickets, no notes on tickets, no looking for tickets or Zabbix problems; what matters goes by **e-mail**, and what is urgent by **Pushover** too. |
| Goals | <ol><li>**Cookie never denies an ability it has, and never claims one it lacks.** In the chat it says: not from here, but from a case, under these conditions (R1).</li><li>**Without a ticket system, nothing important is lost:** what would have been a ticket reaches whoever is responsible, by e-mail; what is urgent, by Pushover too (R2).</li><li>**Cookie doesn't propose, look for or wait on what isn't there:** no ticket proposals, no ticket tools, no Zabbix tools, no prompt text that sends it looking (R3).</li><li>**One reading of what is available,** used by the prompts, the tools, the executor, the duties, the chat and the UI, so they can't disagree (R4).</li><li>**A blip isn't an outage:** Cookie changes its behaviour only after a grace period, and changes back by itself (R5).</li></ol> |
| Not in scope | New channels (Teams, SMS). Cookie writing to Zabbix (acknowledgements): it never does (§6 of the Cookie plan). A different model for Cookie (§11, decided separately). Changing the urgency rules of §6: Pushover stays for what is urgent, e-mail for what is important or urgent. |
| Depends on | The actions and their levels (docs/cookie-plan.md §6, §21.1), the code's duties (§7.1, §7.2), who gets what (§8.3), the chat (§10.2), the read tools (docs/mcp-plan.md §7.2), Redmine's write key (docs/audit-redmine-zabbix-plan.md §8.7). |
| Target | 1.9.0 |

**Words.** ***Available*** means PNMT can use the integration now: it is configured, and the last read worked within the grace period (§3). ***Writable*** means Redmine is available **and** has the write key and a project for Cookie's tickets. ***The fallback*** is what Cookie does instead of a ticket: an e-mail to whoever is responsible, and Pushover when it is urgent. ***The grace period*** is how long a failing integration is still treated as available (§3.3).

---

## 1. Today

### 1.1 The chat denies what Cookie does

- **The chat's system prompt** (`CookieRound.RepliesAsync`) is the identity, goals and rules, the *request* instruction, and a fixed sentence: *You don't act from the chat: for anything the person wants done, say what would happen and who could do it.* It **doesn't contain the paragraph that lists what Cookie may propose** (`CookieBrain.ActingAsync`), which only the cases get.
- **The *request* instruction contradicts it** (docs/cookie-prompts.md §7.9): *If they ask you to do something, propose it only if their rights allow it.* Cookie can't propose from the chat at all; the only thing it fills in is an instruction (mute, suppress, a note on how to work) for someone with *Cookie: Instruct* (§10.3).
- **So Cookie improvises.** Asked for a test Pushover on 16 Nov 2026 it answered that it *has no way to send pushover notifications*, that it can only propose *additions inside the monitoring system*, and offered to *fill in a message to yourself for you to confirm*, which doesn't exist. Every one of those is wrong: Pushover is one of its action kinds (`CookieActionKind.Pushover`, *Propose* by default), the executor sends it through the outbox for what is urgent, and the fill-in flow is for instructions only.
- **What the honest answer is:** not from the chat; from a case, Pushover goes out only for what is urgent, to a PNMT user with a key who works today, in working hours, within the limits; a test wouldn't pass the urgency check. A person tests their own Pushover with PNMT's debug send (`DebugController`, `POST debug/pushover`).

### 1.2 Redmine and Zabbix are assumed

- **Configuration.** Redmine is on when `Redmine__Url`, `Redmine__ApiKey` and `Redmine__Projects` are set (`RedmineOptions.Enabled`); it is writable with `Redmine__WriteApiKey` (`CanWrite`) and a project under *Admin → Cookie → Redmine → Cookie's tickets* (`CookieRedmineSettings.TicketProjectId`). Zabbix is on with `Zabbix__Url` and `Zabbix__ApiToken` (`ZabbixOptions.Enabled`).
- **Reachability.** The case book reads the watched tickets each run; a failure is kept as `CookieRuntime.CaseBookError` (*Redmine: …*) and the ticket cases stay as they were (`CaseBook`: *an unreachable Redmine closes nothing*). Zabbix's poller keeps the last snapshot with `Error` and `ErrorAt` (`ZabbixSnapshot.State`). **Nothing in Cookie reads either signal.**
- **What Cookie does with them today:**
  - **the prompts** tell it to look for *an open ticket*, *a Zabbix acknowledgement*, to propose *a ticket in {ticket_project}*, that *one recurring problem is worth one ticket* (docs/cookie-prompts.md §7.1, §7.3, §7.5), and the goals say *problems that nobody takes care of get a ticket*;
  - **the actions list** (`ActingAsync`) names ticket, private-note and public-answer whenever their level isn't *Off*, whatever Redmine's state;
  - **the tools** `ticket_get` and `zabbix_problems` are offered whenever Cookie's group has the right (`ToolCatalog.ForCookie`); `ToolSpec.Available` exists, but only MCP applies it (`McpCalls`), Cookie doesn't;
  - **the executor** refuses a ticket or a note without the write key (*A ticket needs Redmine__WriteApiKey and a project…*), after Cookie proposed it; the proposal lands in the decision log as refused;
  - **the code's duties:** the problem ticket of §7.1 ends as *Not possible* without the write key (`Duties.TicketAsync`), and **nobody is told about the problem**. The ticket reminders already fall back: no note on the ticket, e-mail and Pushover by urgency to the assignee, the escalation group and whoever is responsible (`Duties`, §8.3).
- **So without Redmine,** an unresolved problem produces refused proposals and a *Not possible* duty, and reaches nobody. Without Zabbix, Cookie looks for acknowledgements that can't exist and gets tool errors.

## 2. Requirements

| | |
|---|---|
| R1 | **The chat tells the truth about Cookie's abilities:** which actions it may propose from a case, under which conditions, and that nothing asked in the chat becomes a proposal. The sentence is built by code from the same levels and availability the cases use. |
| R2 | **Without a writable Redmine, what would have been a ticket goes out as the fallback:** e-mail to whoever is responsible today, the fallback and whoever stands in (§8.2, §8.3), as *important*; Pushover too when it is *urgent*. The urgency rules of §6 don't change. |
| R3 | **Cookie proposes, looks for and waits on nothing that isn't available:** without a writable Redmine no ticket, private-note or public-answer in its list; without Redmine no `ticket_get`; without Zabbix no `zabbix_problems`; a fixed paragraph in the prompt says what is missing and what to do instead. |
| R4 | **One reading per round** (`CookieIntegrations`, §3), used by the prompts, the tools, the executor, the duties, the chat and the UI. |
| R5 | **A grace period** before an unreachable integration counts as unavailable, and **no hysteresis the other way:** the first successful read makes it available again. |
| R6 | **An unreachable integration closes nothing:** ticket cases and Zabbix members stay as they are (as today for Redmine; to confirm for Zabbix in F1). |
| R7 | **Admins see it:** which integrations Cookie counts as available, since when, and why not; and the ticket actions shown as unavailable on *Admin → Cookie → Actions*. |
| R8 | **No new setting.** Availability follows the configuration and the reads; the grace period is a constant. |

## 3. What is available

### 3.1 One reading: `CookieIntegrations`

A small service in `PNMTD/Cookie/`, read **once at the start of a round** (and once per chat reply), returning:

```
IntegrationState Redmine   // None | ReadOnly | Writable, with Reason and Since
IntegrationState Zabbix    // None | Available, with Reason and Since
```

- **Redmine `None`:** `RedmineOptions.Enabled` is false (*Redmine isn't configured*), or the last successful ticket read (`CookieRuntime.TicketsAt`) is older than the grace period while `CaseBookError` names Redmine (*Redmine is unreachable since 10:12: {error}*).
- **Redmine `ReadOnly`:** available, but `CanWrite` is false (*no Redmine__WriteApiKey*) or `TicketProjectId` is null (*no project for Cookie's tickets under Admin → Cookie → Redmine*).
- **Redmine `Writable`:** available, the write key, the project.
- **Zabbix `None`:** `ZabbixOptions.Enabled` is false, or `ZabbixSnapshot.Current.Error` is set and `AsOf` is null or older than the grace period.
- **`Since`** is `ErrorAt`, or the last successful read, or null when not configured.

### 3.2 Why both reads, not a ping

A ping would say *up* while the credentials, the projects or the host groups are wrong. The reads the case book and the poller do anyway are the truth Cookie works with; using them costs nothing and can't disagree with the cases.

### 3.3 The grace period

**30 minutes,** a constant (`CookieIntegrations.Grace`). The case book runs every round and the Zabbix poller every `Zabbix__PollSeconds` (60 s by default), so one failed read never flips Cookie; a Redmine restart or a short network cut doesn't either. After 30 minutes Cookie treats the integration as gone, and **the first successful read brings it back** (R5): the next round lists tickets again, the next problem that falls due gets a ticket again.

**Why 30 and not longer:** the problem ticket of §7.1 falls due after two days; an hour more or less doesn't matter to it. What matters is a reminder of §7.2 that is due now: within the grace period it still tries the note on the ticket (and fails, as today, with the e-mail going out regardless); after it, the note isn't attempted.

## 4. What changes when something is missing

### 4.1 The actions list

`CookieBrain.ActingAsync` takes the reading. **Without a writable Redmine,** ticket, private-note and public-answer are left out of *You may propose actions now: …* as if their level were *Off*, and one sentence follows:

> There is no ticket system you can write to now ({reason}): don't propose tickets or notes on tickets. What would have been a ticket is an e-mail to whoever is responsible (PeopleYouMayAddress), important; when it is urgent, Pushover too.

With every remaining action *Off*, the *watch only* text stays as it is.

### 4.2 A fixed paragraph for the cases: *what isn't there*

After the case's instructions, before the actions, code adds (not editable, like `ActingAsync`):

- **Redmine `None`:** *Redmine isn't available now ({reason}): don't look for tickets, and don't count on one being open.* Plus the sentence of §4.1.
- **Redmine `ReadOnly`:** the sentence of §4.1 only (tickets can be read, so the ticket cases and `ticket_get` stay).
- **Zabbix `None`:** *Zabbix isn't available now ({reason}): don't look for Zabbix problems or acknowledgements; PNMT's own sensors are what you have.*

The editable defaults (docs/cookie-prompts.md §7) keep their ticket sentences: the fixed paragraph overrides them, and an install that changed its texts keeps them. **One default changes,** §7.5 *A problem unresolved for 2 days*, so the default itself reads right without Redmine:

> - Propose a ticket in {ticket_project}. … **Without a ticket system (code tells you when), propose an e-mail to whoever is responsible instead, with the same content.**

### 4.3 The tools

- `ticket_get` gets `Available = Redmine is None ? false : true`; `zabbix_problems` gets `Available = Zabbix available`. `tickets_overview`, `tickets_search`, `zabbix_summary`, `zabbix_hosts_search` and `zabbix_host_get` aren't Cookie's (`ForCookie` false) and keep MCP's behaviour, where `Available` already applies.
- **`CookieTools.For` applies `Available`**, as `McpCalls` does (R3, R4). The `Available` predicate reads `CookieIntegrations`, so Cookie, MCP and the UI agree.
- An unavailable tool isn't in the definitions the model sees, so Cookie can't call it; it isn't told a tool exists and then refused.

### 4.4 The executor

`CookieExecutor.CheckAsync` refuses ticket, private-note and public-answer **with the reading's reason** when Redmine isn't writable (*Redmine is unreachable since 10:12: no ticket; what matters goes by e-mail.*), before the rights. The refusal is the safety net for a Cookie that proposes one anyway; with §4.1 it should be rare, and the decision log shows it when it isn't.

The e-mail and Pushover checks don't change: a person by user id, an address or a key, the urgency, working today, working hours, Seraph, the limits (§6, §6.1).

### 4.5 The code's duties

- **The problem ticket (§7.1, `Duties.TicketAsync`).** Without a writable Redmine, instead of *Not possible*: the fallback.
  - **Who:** the people of §8.3 for a case without a person of its own: whoever is responsible today, the fallback, whoever stands in; members who don't work today are skipped; **the escalation group is a ticket project's, so without a project nobody comes from there.**
  - **What:** the ticket's text (`DutyTexts.ProblemTicket`: the timeline, what was noticed, what to check first) as an e-mail, subject *{title}: unresolved for {hours} hours, nobody took care*, signed as today's e-mails are; Cookie writes its own text when *Admin → Cookie → Actions* says so (§21.1), the template otherwise.
  - **Urgency:** *Important*, as the ticket is today, so e-mail only. **Pushover too when the case is urgent:** importance *High*, or several systems affected (the definition the prompts use, docs/cookie-prompts.md §7.1), through `CaseRules`.
  - **Outcome:** *Done* with *No ticket system: told {names} by e-mail* (and *and Pushover*), in the case's timeline; **nobody to tell** stays a *Not possible* with the reason (nobody works, nobody has an address), as the reminders do today.
  - **When Redmine comes back,** no ticket is opened retroactively by the duty (it is done). Cookie may still propose one from the case, as the executor allows it while the case has no ticket of its own.
- **The closing note (`Duties.ClosingNoteAsync`).** A case told by e-mail has no ticket, so the closing note is *Off* today. **It becomes a short closing e-mail to the same people** (*{title}: resolved*, two lines), so the thread code opened is closed by code; *Info*, allowed as the one exception because it closes what code sent. (Decision D2, §10.)
- **The ticket reminders (§7.2)** already fall back; the only change is that **within Redmine `None` or `ReadOnly` the note on the ticket isn't attempted** and the outcome says why (*no note on the ticket: Redmine is unreachable since 10:12*).

### 4.6 The outbox limits

E-mails replace notes where Redmine is missing, so the per-day limits of §6.1 (`MessagesPerDay`, per person per day) bind sooner. No change in code; the release notes say that an install without Redmine may want to raise them on *Admin → Cookie → Actions*.

### 4.7 The case book

Unchanged for Redmine (R6 holds today). **For Zabbix, confirm in F1** that an unreachable poller keeps the last problems in the snapshot so that existing cases' Zabbix members neither vanish nor resolve; if the snapshot empties on error, the case book keeps the members of open cases until a successful poll says otherwise.

## 5. The chat

### 5.1 The sentence, built by code

`CookieBrain.ChatActingAsync` (the same levels and reading as §4.1) replaces the fixed *You don't act from the chat* sentence:

> From the cases you work on your own, you may propose: pushover (only for what is urgent, to a PNMT user with a Pushover key, in working hours), email (what is important or urgent), ticket, private-note, public-answer, object-note. Each goes through code, Seraph and, at Propose, a person in the decisions inbox; the levels are on Admin → Cookie → Actions. **Nothing asked in this chat becomes a proposal:** say what you would do from a case, under which conditions, and who can do it now.

With every action *Off*: *You only watch: you propose nothing, from the chat or from a case.* The paragraphs of §4.2 follow when something is missing, so the chat says *there is no ticket system now* rather than *I'd open a ticket*.

The instruction part (*This person may give instructions…* / *can't give instructions*) and *Who is away is in the availability plan* stay as they are.

### 5.2 The *request* instruction

The default of docs/cookie-prompts.md §7.9 changes its second bullet:

> - If they ask you to do something, don't propose it from here: say what you would do from a case (which channel, under which conditions), whether their rights would allow it, and who can do it now.

An install that overrode the text keeps its own; the release notes name the change.

### 5.3 Testing one's own channels

The chat may point to where a person tests Pushover or e-mail themselves (PNMT's debug send, `POST debug/pushover`, `POST debug/mail`; admins). One clause in §5.1's sentence; decision D4 (§10).

## 6. API and web UI

### 6.1 API

- **`GET /api/v1/cookie/acting`** (`CookieActingItem`) gains `integrations`:

```json
"integrations": {
  "redmine": { "state": "none", "reason": "Redmine is unreachable: 502 Bad Gateway", "since": "2026-11-16T09:12:00Z" },
  "zabbix":  { "state": "available", "reason": null, "since": null }
}
```

- No new endpoint, no new setting, no migration.

### 6.2 Web UI

- ***Admin → Cookie → Actions*** (`ActionsTab.vue`): the rows *Ticket*, *Private note* and *Public answer* show **Unavailable now: {reason}** under their level when Redmine isn't writable; the level stays editable (it is what Cookie does when Redmine is back).
- ***Admin → Cookie → Overview*** (`OverviewTab.vue`): an *Integrations* line, *Redmine: writable · Zabbix: unreachable since 09:12 (502 Bad Gateway)*, next to the case book's error it shows already.
- **Cookie's docs in the app** (`CookieDocs.vue`): a paragraph *Without Redmine or Zabbix*.

## 7. Data, settings, upgrading

- **No migration.** The reading is computed from `CookieRuntime.TicketsAt`, `CaseBookError`, the Zabbix snapshot and the options.
- **No new `.env` key.** `Redmine__*`, `Zabbix__*` and *Admin → Cookie → Redmine* decide, as today.
- **Upgrading:** an install without Redmine sees the ticket actions as unavailable at once, and its next unresolved problem goes by e-mail. An install with Redmine sees no change.

## 8. Testing

All with the fakes in `PNMTD.Tests/Cookie` (`FakeRedmine`, `FakeOpenRouter`, the fake clock):

| Test | Where | What it guards |
|---|---|---|
| Redmine not configured → `None`; configured without write key → `ReadOnly`; with key and project → `Writable` | new `IntegrationsTests` | §3.1 |
| A failing ticket read keeps `Writable` for 29 minutes and gives `None` at 31; the next successful read gives `Writable` at once | `IntegrationsTests`, `CaseBookTests` | §3.3, R5 |
| Zabbix: options off → `None`; snapshot error older than the grace → `None`; a fresh snapshot → available | `IntegrationsTests` | §3.1 |
| `ActingAsync` without a writable Redmine lists neither ticket nor the notes, and contains the fallback sentence; with every other action *Off* it is the watch-only text | `CookieActingTests` | §4.1 |
| The fixed paragraph names what is missing, and only that | `CookieRulesTests` | §4.2 |
| `CookieTools.For` omits `ticket_get` with Redmine `None` and `zabbix_problems` with Zabbix `None`; MCP's list agrees | `CookieActingTests`, `McpTests` | §4.3 |
| A ticket proposal without a writable Redmine is refused with the reading's reason, before the rights | `CookieActingTests` | §4.4 |
| The problem ticket's duty without a writable Redmine e-mails whoever is responsible today, skips who doesn't work, adds Pushover for a case of importance High, and records *told … by e-mail* | `DutiesTests` | §4.5 |
| The closing e-mail goes to the same people once the problem resolves; none when nobody was told | `DutiesTests` | §4.5 |
| A reminder within Redmine `None` doesn't attempt the note and says why; the e-mail goes out as today | `DutiesTests` | §4.5 |
| The chat's system prompt contains the sentence of §5.1 with exactly the actions above *Off*, and the paragraphs of §4.2 when something is missing | `CookieActingTests` (the chat path) | §5.1 |
| The default *request* instruction contains the new bullet; an overridden text is kept | `CookieRulesTests` | §5.2 |
| `GET cookie/acting` carries `integrations` | `CookieActingTests` | §6.1 |
| An unreachable Zabbix closes no case and drops no member | `CaseBookTests` | R6, §4.7 |

The planted-injection set of docs/cookie-plan.md §14.3 is run once more after F3, since the chat's prompt changes.

## 9. Phases

| Phase | What | Where |
|---|---|---|
| **F1** | `CookieIntegrations`; `ActingAsync` and the fixed paragraph; `Available` on the two tools and applied in `CookieTools.For`; the executor's refusals; the §7.5 default; the Zabbix check of §4.7 | `PNMTD/Cookie`, `PNMTD/Tools/Read/IntegrationReadTools.cs`, tests |
| **F2** | The duties: the problem e-mail, the closing e-mail, the reminders' note skipped with a reason | `PNMTD/Cookie/Duties.cs`, `DutyTexts`, tests |
| **F3** | The chat: `ChatActingAsync`, the *request* default, docs/cookie-prompts.md §7.9 | `PNMTD/Cookie/CookieRound.cs`, `CookiePrompts.cs`, tests |
| **F4** | API field, the two admin tabs, the in-app docs, docs/cookie-plan.md (§6, §7.1, §10.2 pointers), release notes 1.9 | `PNMTD/Api`, `PNMTD.WebUI`, `docs/` |

F1 to F3 are independent of the UI and can ship with the API field alone; F4 completes it.

## 10. Decisions to confirm

| | Question | Default in this plan |
|---|---|---|
| D1 | The grace period | 30 minutes, a constant (§3.3) |
| D2 | A closing e-mail when a problem told by e-mail resolves | **Yes,** two lines, to the same people (§4.5) |
| D3 | May an *Info*-level message go by e-mail without a ticket system? | **No.** Info stays a note on the host or sensor and in the case; the fallback is for what would have been a ticket, which is important by definition |
| D4 | The chat names where a person tests their own Pushover or e-mail | **Yes,** one clause, for admins (§5.3) |
| D5 | Pushover for the problem e-mail: importance *High* or several systems | As the prompts define *urgent* today (§4.5) |

## 11. Related, decided separately: Cookie's model

Not part of this plan's phases; recorded here so the research of 16 Nov 2026 isn't lost. The chat failure of §1.1 is a prompt gap, not the model's; a change of model doesn't replace §5.

- **Candidate: `anthropic/claude-haiku-5.5`** on `google-vertex/europe`: $0.10 / $0.50 per million tokens in / out, cached reads $0.01, against DeepSeek V4.1 Flash's $0.30 / $1.20 / $0.006. Vals Index #19 (58.7) against #13 (61.3); Terminal-Bench 4.0 39 % against 32 %; Anthropic's system card reports 4 % attack success for injection through tool results, where the study of docs/cookie-plan.md §14.2 found DeepSeek and GLM resist about 1 %. A different vendor from Seraph's GLM, as §5 asks.
- **What a switch needs:** `Cookie__Model`, `Cookie__Providers=google-vertex/europe`; **`Cookie__MinQuantization` cleared** (vendor endpoints report their quantisation as *unknown*; the fp8 filter would likely leave no endpoint); **a top-level `cache_control` in the request** for Anthropic models (DeepSeek caches automatically, Anthropic needs the field; without it Haiku still costs less, but without the cached-prefix discount); the rollout tests of §14.3 again. Watch the trace for over-refusal, which the audits flag.
- **Alternatives looked at:** GPT-6 Luna (same price, Azure EU, Vals #31: weaker), Mistral Small 4 (Mistral's EU endpoint, $0.15 / $0.60, unranked, no caching: the EU-vendor option to trial), Gemini 3.5 Flash Lite (costs more than DeepSeek), GLM-5.3 Flash (Seraph's vendor, Vals #32).

## 12. As built (2026-11-16)

Built as planned, in one go, with these notes:

- **`CookieIntegrations`** (`PNMTD/Cookie/CookieIntegrations.cs`): `ReadAsync` once per round, per duty run, per executor check of a ticket or note action, and for the admin API; `Current` for the tools' `Available`, from the configuration alone before the first reading. The case book refreshes it at the end of every run, so the tools' lists follow the reads at once. `Read(...)` is pure and tested on its own (`IntegrationsTests`).
- **Writable is Redmine with the write key;** a ticket needs the project too (`CanTicket`). So without a project for Cookie's tickets, notes on tickets stay possible and only the ticket leaves the list, with a sentence of its own (§4.1).
- **The texts** are in `CookieBrain`: `ActingAsync(reading)`, `ChatActingAsync(reading)`, `Missing(reading)` and `WithMissing(...)`, `NoTicketSystem(reading)`. The chat's fixed sentence of §1.1 is gone; the instruction part and *who is away* stay.
- **The tools:** `ticket_get` and `zabbix_problems` carry `Available`; `CookieTools.For(rights, services)` applies it for the round, the chat, the access judge and the big brother. MCP applied it already, so both lists agree.
- **The duties:** `DutyRunner.ProblemByEmailAsync` (the ticket's content as an e-mail, `DutyTexts.ProblemEmail`, Pushover for importance High) and `ClosingEmailAsync` (`DutyTexts.ClosingEmail`, to the people the problem's duty reached, found in the outbox by the duty). Both count as *to people*, so they wait for working hours. **The case book now revisits a closed problem whose problem duty is done,** not only one with a ticket of Cookie's, so the closing e-mail falls due. **Narrowed:** the problem e-mail uses the template; Cookie's own writing of duty texts (§21.1) stays with the ticket reminders.
- **The executor** refuses with the reading's reason (`NoTicketReason`, `NoNoteReason`); the texts for a configured Redmine without a write key are the ones installs know.
- **The prompt preview** on Admin → Cookie → Prompts shows a case prompt as it is now, with what is missing.
- **API and UI:** `CookieActingItem.Integrations`; *Overview* shows Redmine and Zabbix next to Seraph and the big brother; *Actions* says *Unavailable now: …* under the ticket actions; the in-app Cookie docs have a paragraph.
- **Decisions D1 to D5** stand as proposed: 30 minutes, the closing e-mail, no Info by e-mail, the chat names *Admin → Debug*, Pushover for importance High.
- **Tests:** `IntegrationsTests` (the reading and the texts), `CookieActingTests` (the prompts with the real settings, the executor's refusals and the tools when Redmine has been unreachable for longer than the grace period, the API field, the first read bringing it back), `DutiesTests` (the problem e-mail and the closing e-mail, nothing written to Redmine), `CookieLoopTests` (the chat's system prompt).
