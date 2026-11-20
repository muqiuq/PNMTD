# Plan: Redmine through one queue, and every outbound operation on record

| | |
|---|---|
| Status | **Built (2026-11-20): O1 to O4, with the deviations of §15. Released in 1.16.0.** **Reviewed once (2026-11-19):** a job started by a person pressing a button goes first in the queue (D3); **two logs**, the newest 10 000 notifications and the newest 1 000 Redmine calls (D12); snapshots don't survive a restart (D17); webhook URLs aren't masked (D18); other outbound traffic comes later (D19); the details open in a dialog with its own URL (D13); the agent can't refresh (D20); the age of PNMT's copy in the panels comes later (D21). Nothing is open (§11). |
| Date | 2026-11-19 |
| Scope | Two parts. **Part A, Redmine:** every read from and write to Redmine goes through **one background task**, the *Redmine worker*, which works **a queue**: **first what a person started with a button**, then writes, then what someone waits for, then the rest, with as many calls at once as make sense. What it reads is kept as **snapshots**. Pages are answered from them at once, even when they are older, and every Redmine page shows **when its data was refreshed**, with **a button to refresh it**. The button spins until the worker has the new data. **Part B, two outbound logs:** every e-mail, Pushover message and HTTP notification (webhook) is recorded in ***the notification log***, and every Redmine call in ***the Redmine log***, with its headers and contents. Both are shown live in a new tab ***Outbound*** in *System health*, and **the details open in a window** (a dialog), not a sidebar. **The newest 10 000 notifications and the newest 1 000 Redmine calls are kept.** |
| Goals | <ol><li>**No one waits for Redmine to open a Redmine page** that was opened before. The first visit to a page nobody opened yet still waits once (R1).</li><li>**One place talks to Redmine**, so its load, its order and its failures are in one hand (R2, R3).</li><li>**A button pressed by a person goes before everything else** (R14).</li><li>**Data that may be old says so:** when it was refreshed, whether a refresh is running, whether the last one failed (R5).</li><li>**A refresh on request is visible** until it is done (R6).</li><li>**Every message PNMT sends, and every Redmine call, can be looked at afterwards:** what went out, what came back, how long it took, for what and for whom (R8–R10).</li><li>**No secret of PNMT's own in the logs** (R11).</li></ol> |
| Not in scope | Other outbound traffic: the AI's requests (they have `ai_requests` already), Zabbix, MikroTik, Connectors, the uplink check, the advisory and version feeds. These come later (D19). Push to the browser (the web UI has none; it polls, §7.2). Retrying failed writes to Redmine. Keeping snapshots across restarts (D17). Showing the age of PNMT's copy of linked tickets in the panels, which comes later (D21). |
| Depends on | Redmine (docs/audit-redmine-zabbix-plan.md §6). Cookie's outbox (docs/cookie-plan.md §6.1, §19.1). The notifications (docs/ai-operations-plan.md §8.3; docs/notification-recipients-plan.md). The AI request log as the pattern for stored bodies (docs/ai-operations-plan.md §3.6; `AiRequestLog`). |
| Target | **1.16.0**, all of it (§12). |

**Words.**

- ***The worker*** (`RedmineWorker`) is the one background task that calls Redmine. It replaces `RedmineSync` and keeps its name, *Redmine sync*, in *System health* (§3.1).
- ***A job*** is one thing the worker does: refresh the overview, read a ticket, run a search, add a note. A job makes one or more ***calls***, each one HTTP request to Redmine (§3.2).
- ***A lane*** is a job's place in the queue (§3.3):
  - ***button***: a person pressed a button and waits;
  - ***write***: Cookie writes to Redmine;
  - ***interactive***: someone waits for it;
  - ***background***.
- ***A slot*** is the right to have one call in flight. There are `Redmine__Parallelism` slots (§3.4).
- ***A key*** names what a job reads, e.g. `overview:` or `issue:603`. Two jobs with the same key are one job (§3.5).
- ***A snapshot*** is what a job read, kept under its key, with the time it was read (§4).
- ***Watched*** describes a snapshot that someone asked for in the last `Redmine__WatchMinutes` (§4.3).
- ***The bar*** is the "Refreshed 2 min ago ⟳" in the header of a Redmine page (§5).
- ***An operation*** is one outbound action: an e-mail, a Pushover message, an HTTP notification, or one call to Redmine. There are two logs of them (§6):
  - ***the notification log*** keeps e-mails, Pushover messages and HTTP notifications, the newest 10 000;
  - ***the Redmine log*** keeps Redmine calls, the newest 1 000.

---

## 1. Today

*Before 1.16.*

### 1.1 How a Redmine page is read

- **The browser asks twice, one after the other.** `RedmineView.vue` asks `GET /api/v1/redmine/status` (`:35`). Only once that says *enabled* does it ask `GET /overview` (`:52-57`), and it asks again every 60 s.
- **`/status` reads the lookups** (`RedmineService.LookupsAsync`, `RedmineService.cs:58-132`): every page of `/projects.json`, then `/issue_statuses.json`, `/trackers.json`, `/enumerations/issue_priorities.json`, and `/projects/{id}/memberships.json` per configured project. That is five calls or more, one after the other.
- **`/overview` makes at least eight calls, one after the other** (`LoadOverviewAsync`, `:222-256`): *open* (`limit=1`), *urgent*, *unassigned*, *overdue*, *recent*, and the stale tickets.
  - **The stale tickets** (`LoadStaleAsync`, `:285-333`) need:
    - up to five pages of tickets without a change since the cutoff;
    - `/users/current.json` with the write key (`CookieUserIdAsync`, `:335-356`);
    - up to five pages of tickets whose last change is Cookie's;
    - then **one `/issues/{id}.json?include=journals` per such ticket, one at a time** (`LastChangeAsync`, `:359-365`).
  - Then come five database queries for the link counts (`LinkCountsAsync`, `:872`) and the problem tickets (`ProblemTicketsAsync`, `:425`).
- **The cache is `IMemoryCache`, in the process, shared by all users** (registered in `AuthSetup.cs:91`). It fills only when a request finds a key missing.

  | Key | Kept for |
  |---|---|
  | `redmine:lookups` | 1 h. `RedmineSync` also removes and reloads it every `SyncMinutes`. |
  | `redmine:overview:{project}` | 60 s |
  | `redmine:stale:{project}` | 60 s |
  | `redmine:writer` | 1 h |
  | `redmine:last-change:{id}:{updated_on}` | 1 day |

  - **It has no lock.** Two requests at once both load.
  - **Failures aren't kept,** so each request tries again and can wait up to `TimeoutSeconds` (15 s).
  - **The loader runs on the request's cancellation token.** A user who leaves cancels the load.
  - **The overview is cached whole, link counts included.** Linking a ticket shows in it only after the 60 s.
- **The search (`SearchAsync`, `:149`) and a ticket's page (`IssueAsync`, `:468`) aren't cached.** Each visit asks Redmine.
- **Measured from the code, not with a clock:** a cold overview is 13 or more calls in a row (5 for `/status`, 8 for `/overview`) plus one per ticket Cookie touched. At 150–400 ms a call, that is a few seconds. The cache lasts as long as the page's refetch interval, so most visits pay it.

### 1.2 Who calls Redmine

`RedmineClient` (`RedmineClient.cs:32` `GetAsync`, `:111` `WriteAsync`) is called directly from these places:

| Where | What | When |
|---|---|---|
| `RedmineService` (16 calls) | lookups, overview, stale, search, ticket, link checks | a request of the web UI, of the agent's tools (`IntegrationReadTools.cs:71, :122, :129`; `KnowledgeWriteTools.cs:214`), of the global search (`SearchService.cs:74`) |
| `RedmineSync.RunAsync` (`RedmineSetup.cs:51-90`) | the copy of linked tickets, 100 a call | every `SyncMinutes` (10) |
| `CaseBook` (`CaseBook.cs:305, :322, :388, :422`) | open tickets of the watched projects (up to 10 pages), their journals, a ticket that left the list, the staff (memberships, its own `cookie-staff:*` cache key) | every `Cookie__TicketsMinutes` (5) |
| `CookieService` (`CookieService.cs:832`) | `/roles.json` | Cookie's settings page |
| `RedmineController.Test` (`RedmineController.cs:73-81`) | `/users/current.json` and the lookups | *Test connection* |
| `Outbox` (`Outbox.cs:207, :222, :257`) | **the only writes:** a note (`PUT /issues/{id}.json`, after a read of the ticket to check its project), a new ticket (`POST /issues.json`) | Cookie |

### 1.3 How Redmine is written

- **The Outbox has no task of its own.** It is a singleton whose `SendAsync` (`Outbox.cs:122`) sends inline, in whoever calls it, one at a time (`SemaphoreSlim(1,1)`, `:43`).
- **The callers** are Cookie's tasks: `CaseBookTask` → `DutyRunner` → `Duties`, `CookieLoop` / `CookieRound` → `CookieExecutor`, `CookieMail`, `BigBrother`.
- **Some sends come from a person, inside a web request:**
  - a proposal approved in the inbox (`RunApprovedAsync`);
  - a held-back message sent with *Send* (`SendHeldBackAsync`, `:154`);
  - an order from the chat.
- **The `CookieOutbox` row records what went out.** It isn't a queue, and a failed send isn't retried.
- **Nothing that writes clears the Redmine cache.** The only `cache.Remove` is `ForgetLookups`.

### 1.4 How notifications go out

- **E-mail, Pushover and HTTP all go through `NotificationService.SendAsync`** (`NotificationService.cs:14`). It picks the provider by the recipient's form:
  - `EMailNotification` uses MailKit (`EMailNotification.cs:54-66`);
  - `PushoverNotification` posts JSON with the app token in the body (`PushoverNotification.cs:41, :59`);
  - `HttpNotification` posts the subject and the text as plain text to the URL (`HttpNotification.cs:20-21`).
  - The Pushover and HTTP providers share the named `HttpClient` `notifications`.
- **It has four callers:**
  - `NotificationTask`, the sensor alerts: a `Timer` every 10 s that sends one at a time and blocks a thread-pool thread (`NotificiationTask.cs:52, :197`);
  - the Outbox;
  - `McpAlerts`, the token-blocked alert (`McpStrikes.cs:99`);
  - the contact test (`People.cs:124`).
- **`Development:DoNotSendNotifications`** makes a send *simulated*: it is logged, not sent.
- **What is kept of a send:**
  - a line in the application log, deliberately without the recipient (`NotificationService.cs:3-6`);
  - for Cookie, the `CookieOutbox` row;
  - for a rule, `LastSentAt` and `LastSentTo`.
  - **Neither the headers nor the body, nor the provider's answer, is kept anywhere.**

### 1.5 Patterns to reuse

- **`AiRequestLog`** (`AiRequestLog.cs:11-58`) keeps every request to a model:
  - the bodies gzip-compressed, each capped at 1 MiB, never the `Authorization` header;
  - the newest `Ai__LogEntries` (10 000), trimmed by `Id` every 25 inserts.
  - Part B copies it.
- **Waking a worker:**
  - `ConnectorWorker.Nudge()` (`ConnectorWorker.cs:68`) and `JobRunner.Wake()` (`JobRunner.cs:41`) wake a loop at once. Calling them never waits for the work.
  - The Connectors' *Calls* tab polls every 5 s (`CallsTab.vue:65`), and a call's details open in a dialog (`ConnectorCallDialog.vue`). The new tab follows both.
  - The AI's *Requests* tab opens its details in a drawer (`views/admin/ai/RequestsTab.vue`). The new tab doesn't (D13).
- **`FakeRedmine`** (`PNMTD/Dev/FakeRedmine.cs:15`) is an `HttpMessageHandler` in place of Redmine for dev and the tests.

### 1.6 System health

- **The page has no tabs** (`SystemView.vue`, route `/admin/system`, `System: Manage`; `navigation.ts:183`, `router/index.ts:423-427`).
- **Its panels poll every 15–30 s:** the background tasks, the uplink, the notification coverage, UDP and the integrations.
- **`Redmine sync` is in the tasks list,** with an interval of 600 s (`SystemService.cs:36`).

---

## 2. Requirements

| | Requirement |
|---|---|
| R1 | A Redmine page that has been read before is answered from PNMT at once, whatever Redmine's speed. |
| R2 | Every read from Redmine and every write to it goes through the worker. Nothing else calls `RedmineClient`. |
| R3 | The worker works its jobs in order (R14): what a person started with a button first, then writes, then jobs someone waits for, then the rest. Calls run at once where that is safe, up to a limit. |
| R4 | The same read asked twice while it is queued or running is made once. |
| R5 | Every Redmine page shows when its data was refreshed, whether a refresh is running, and whether the last one failed and why. |
| R6 | A refresh button asks the worker for new data and shows that it is busy until the data is in. The page then shows it. Leaving the page doesn't stop the refresh. |
| R7 | What PNMT changes in Redmine, and links changed in PNMT, show up without waiting for the next scheduled refresh. |
| R8 | Every e-mail, Pushover message, HTTP notification, and every Redmine call is recorded, with its headers, its contents, the answer, the outcome, the duration, what it was for and who asked. Simulated sends are recorded too. |
| R9 | Both logs appear live in *System health → Outbound*, newest first, and can be filtered. A record's details open in a window, not a sidebar. |
| R10 | Two logs, each with no age limit: the notification log keeps the newest 10 000 e-mails, Pushover messages and HTTP notifications, and the Redmine log the newest 1 000 Redmine calls. |
| R11 | No key, token or password of PNMT's own is stored, not in a header and not in a body: Redmine's keys, Pushover's app token, the SMTP password. Webhook URLs are stored as they are (D18). Private notes are stored only when PNMT shows them (`Redmine__ShowPrivateNotes`). |
| R12 | Recording never makes a send slower or makes it fail. |
| R13 | Every limit and interval is a setting (§9). |
| R14 | **A job a person started by pressing a button goes before every other job in the queue.** If it joins a job that is already queued or running, that job moves up to the button lane. |

---

## Part A: Redmine

## 3. The worker

### 3.1 One task

- **`RedmineWorker` is a singleton `BackgroundService`. It replaces `RedmineSync`.**
  - It has the queue (§3.3), the slots (§3.4), the snapshots (§4) and a scheduler. The scheduler is a loop that wakes every 5 s, or at once when it is nudged.
  - `RedmineSync.RunAsync` becomes one of its jobs (*linked copy*, §3.2).
  - It keeps updating `LAST_REDMINE_SYNC` and its system sensor: two failed passes in a row fail it, as today. In *System health* the task's interval becomes `Redmine__RefreshSeconds`.
- **Only the worker calls `RedmineClient`.**
  - `GetAsync` and `WriteAsync` become `internal`.
  - A test fails if any type other than the worker's jobs references `RedmineClient` (§13).
- **Callers ask the worker, they don't call Redmine:**
  - `RedmineService` keeps its methods (`OverviewAsync`, `SearchAsync`, `IssueAsync`, …) and its callers. Inside, each one reads a snapshot or asks the worker for a job, **saying which lane** (§3.3).
  - `CaseBook`, `CookieService`, `Outbox` and `RedmineController.Test` ask the worker the same way.
- **The worker starts at once,** after the database migration. Today `RedmineSync` waits 30 s.
  - Its first jobs are the lookups and the overview of all projects, so the first visitor after a deploy rarely waits.
  - The *linked copy* follows after 30 s, as today.

### 3.2 Jobs

| Job | Key | Lane | Calls | Asked by |
|---|---|---|---|---|
| **Lookups** | `lookups` | background; interactive when there is no snapshot yet | projects (pages), statuses, trackers, priorities, memberships per project | the scheduler (hourly), a cold start; within *Test* |
| **Writer** | `writer` | background | `/users/current.json` with the write key | the stale tickets |
| **Overview** | `overview:{project}` | background; interactive when a person looks at an old one (§4.2); **button with ⟳** | *open*, *urgent*, *unassigned*, *overdue* and *recent* **at once**, and the stale tickets | the scheduler, the overview page, the agent's `redmine_overview` |
| **Stale** | `stale:{project}` | as the overview | the two lists at once, then the journals, **up to `Redmine__Parallelism` at once** | the overview, the search's *stale* filter |
| **Ticket** | `issue:{id}` | interactive; **button with ⟳, and when a ticket is being linked** | `/issues/{id}.json?include=journals,attachments,relations,children`, and the copy updated if linked (`IssueAsync`, `:484-488`) | the ticket page, the agent, *Link* |
| **Search** | `search:{normalized query}` | interactive; **button with ⟳** | `issues.json` with the filters, one page; with *stale*, the stale snapshot and then `issue_id=…` | the search tab, the link dialog, the global search, the agent |
| **Linked copy** | `sync` | background | `issues.json?issue_id=…`, 100 at a time | the scheduler, every `SyncMinutes` |
| **Case book tickets** | `casebook` | background | the watched projects' open tickets (up to 10 pages), journals of changed ones, a check per ticket that left the list | `CaseBook`, every `Cookie__TicketsMinutes` |
| **Staff** | `staff:{project}:{roles}` | background | memberships, by pages | `CaseBook` |
| **Roles** | `roles` | interactive | `/roles.json` | Cookie's settings page |
| **Test** | `test` | **button** | `/users/current.json`, then the lookups | *Test connection* |
| **Note** | none | write; **button when a person ordered it** (§3.8) | a fresh read of the ticket (the project check, `Outbox.cs:257`), then `PUT /issues/{id}.json` | the Outbox |
| **New ticket** | none | write; **button when a person ordered it** | `POST /issues.json` | the Outbox |

- **A job with the *fresh* flag** always reads Redmine and doesn't take a snapshot. A *note*'s check and the case book's per-ticket check are fresh.
  - A fresh job still joins one with the same key that is **queued but not started**.
  - It never joins one that is already running, because that one may have started before the change it needs to see.

### 3.3 Lanes and order

- **Four lanes, worked in this order:**
  1. **button**: a person pressed a button and waits for the result (R14):
     - the refresh button ⟳ on a Redmine page (§5.3);
     - *Test connection* in System health;
     - *Link* (the read of the ticket being linked, `AddLinkAsync`);
     - a Redmine write a person ordered: *Send* on a held-back message, *Approve* on a proposal in the inbox, an order in Cookie's chat (§3.8).
  2. **write**: Cookie's own notes and tickets. They are rare and short, and their order matters.
  3. **interactive**: a page, the agent or the global search waits for data there is no snapshot of, or a person looks at an old snapshot (§4.2).
  4. **background**: the scheduler, the case book and the linked copy.
- **The caller names the lane;** the worker doesn't guess it.
  - Only the endpoints behind those buttons ask for the button lane: `POST /redmine/refresh`, `POST /redmine/test` and `POST /redmine/links`.
  - The Outbox asks for it when its request is a person's (§3.8).
- **Each lane is first in, first out.** A free slot goes to the first job of the highest lane that has one.
- **Joining moves a job up.** A button press that joins a job already queued or running in a lower lane (§3.5) raises that job to the button lane. Its calls that haven't started yet take the next free slots before any other lane's.
- **Writes run one at a time.** A write a person ordered goes before Cookie's queued writes. Otherwise writes keep the order they came in. Two writes never run at once.
- **A job's calls take slots one by one.** A long background job (the stale tickets with 40 journals) doesn't hold the queue. Between its calls, a button or interactive job takes the next free slot.

### 3.4 Calls at once

- **`Redmine__Parallelism` (4) calls are in flight at most,** over all jobs together.
- **One slot is kept for the button, write and interactive lanes.** Background calls never take the last free slot.
  - So a button job never waits for the queue. It waits at most for the first call in flight to end, typically well under a second and never longer than `TimeoutSeconds`.
- **Within a job, independent calls run at once:**
  - the overview's five lists;
  - the stale tickets' two lists;
  - the journals of the stale tickets;
  - the pages of a list, once the first page has given the total.
- **Each call keeps `Redmine__TimeoutSeconds` (15).** A call that runs out frees its slot.
- **A `429` or `503` from Redmine pauses the background lane** for `Redmine__BackoffSeconds` (30). The `Retry-After` header wins if Redmine sends one. The other lanes go on.

### 3.5 One read, asked twice

- **A job asked while one with the same key is queued or running joins it:** all who asked get the same result.
  - Two people opening ticket #603 make one call.
  - A reload pressed while the scheduler refreshes the overview waits for that refresh, and raises it to the button lane (§3.3). The refresh started after the page was loaded, so its data is newer than what the page shows.
- **Writes never join each other.**

### 3.6 Waiting for a job

- **The worker answers every job through a `Task`.** Whoever asked can await it or not.
- **Someone who stops waiting doesn't cancel the job.** For example, the request was cancelled or the user left. The job finishes, and its snapshot is kept for the next visitor.
- **How long a caller waits:**
  - a button or interactive caller waits at most `Redmine__WaitSeconds` (30), then gets `504 redmine_busy` ("Redmine is slow; the data will be here when it answers"); the refresh button gets a `202` instead (§5.3);
  - the global search waits at most 5 s for its *search* job, and otherwise answers without tickets, as it does today when Redmine fails;
  - the Outbox waits for its write as long as the write takes, as today.
- **At shutdown, queued jobs are dropped.** Their waiters get `503 redmine_stopping`. A write that has started is finished first, within its timeout.

### 3.7 Failures

- **A failed job keeps the snapshot it had.** It records the time and the message, which the bar shows (§5.2).
- **A failed background refresh of a key waits before the next attempt:** 1, 2, 5, then 10 minutes. A button press or a new interactive request doesn't wait for this.
- **The system sensor:**
  - two failed scheduler passes in a row fail it (`REDMINE_FAILING`, as today);
  - the next pass that succeeds clears it.
- **`RedmineException` keeps its kinds.** The controller's answers stay as they are: 409 not configured, 502 unreachable or refused, and now 504 busy.

### 3.8 Writes and what they change

- **The Outbox keeps its semaphore, its limits and its `CookieOutbox` row** (`Outbox.cs:122-151`).
  - For a Redmine channel, `DeliverAsync` asks the worker for a *note* or *new ticket* job and awaits it, instead of calling `RedmineClient`.
  - A write that times out is `Failed`, as today. It isn't retried, because Redmine may have applied it, and a second note would be a duplicate.
- **`OutboxRequest` gets `ByPerson`. It only chooses the lane:** button when true, write when false. The limits and the audit stay as they are. It is true for:
  - *Send* on a held-back message (`SendHeldBackAsync`, which already has `byPerson`);
  - a proposal a person approved (`CookieExecutor.RunApprovedAsync`);
  - an order from the chat (`OrderedBy` is set).
- **After a write succeeds, the worker:**
  - queues a fresh *ticket* job for that ticket, in the write's own lane;
  - marks every `overview:*`, `stale:*` and `search:*` snapshot as **due** (§4.3), so the scheduler refreshes the watched ones on its next tick (5 s).
  - Several writes within a tick cause one refresh.
- **Links are PNMT's data.** A link added or removed shows up at once, because link counts and problem tickets are read from the database at every request (§4.4). It also marks the `search:*` snapshots that use the *linked* filter as due.

---

## 4. The snapshots

### 4.1 What is kept

- **`RedmineSnapshots` is a singleton dictionary in the worker. It replaces the `redmine:*` and `cookie-staff:*` keys in `IMemoryCache`.**
  - `IMemoryCache` expires an entry when its time is up. A snapshot must stay while it is used and be replaced only once its successor is ready (stale-while-revalidate).
- **Each snapshot has:**
  - its value: Redmine's data only (§4.4);
  - `RefreshedAt`;
  - `Refreshing`, true while its job is queued or running;
  - `FailedAt` and `Error`, from the last failed attempt;
  - `RequestedAt`, the last time anyone asked for it;
  - `Due`, set by a write (§3.8).

| Key | Refreshed | Removed |
|---|---|---|
| `lookups` | hourly, by *Test connection*, and by the linked copy as today | never |
| `writer` | with the lookups | never |
| `overview:` (all projects) | every `RefreshSeconds` while watched, otherwise every `SyncMinutes` | never |
| `overview:{project}`, `stale:{project}` | every `RefreshSeconds` while watched | `KeepMinutes` after the last request |
| `issue:{id}` | every `RefreshSeconds` while watched | `KeepMinutes` after the last request |
| `search:{query}` | every `RefreshSeconds` while watched | `KeepMinutes` after the last request |
| `last-change:{id}:{updated_on}` | never (the key changes with the ticket) | 1 day after it was read |
| `staff:{project}:{roles}` | with the case book, as `cookie-staff:*` today | as today |

- **At most `Redmine__MaxSnapshots` (500) are kept.** Beyond that, the snapshots asked for longest ago are removed first. `lookups` and `overview:` are never removed.

### 4.2 Reading

- **There is a snapshot:** it is returned at once with its freshness (§4.5), however old it is.
  - If it is older than `RefreshSeconds`, or due, a refresh is queued **interactive**, because someone is looking now.
  - The page polls while `Refreshing` is true and shows the new data when it is in (§5.4).
- **There is none:** the caller waits for the job (interactive, §3.6). This happens:
  - on the first visit to a ticket, a search or a project nobody looked at within `KeepMinutes`;
  - after a restart, before the warm-up is done.

### 4.3 Watched and due

- **A snapshot is *watched*** while it was asked for in the last `Redmine__WatchMinutes` (5).
  - An open Redmine page asks every 30–60 s (§5.4), so it keeps its snapshot watched.
  - A browser tab in the background stops polling, because vue-query doesn't refetch in the background, so it stops watching.
- **On each tick, the scheduler queues a background refresh of every snapshot that is:**
  - watched, and older than `RefreshSeconds` or due; or
  - `lookups` or `overview:`, older than its interval in §4.1.
- **Snapshots nobody watches aren't refreshed:** tickets, searches and per-project overviews. The next visitor sees the old one at once and starts its refresh (§4.2).

### 4.4 What isn't in a snapshot

Snapshots hold what Redmine said. **What PNMT knows is added on every read,** from the database, so it is never old. This costs a few indexed queries a request, as today.

- **`Links` on each ticket** (`LinkCountsAsync`).
- **The overview's *Linked to a problem in PNMT*:** `ProblemTicketsAsync`, from PNMT's copy and the dashboard.
- **The ticket page's links:** `LinksOfAsync`.
- **The panels on pages, locations, hosts and sensors (`/related`) and the dashboard's chips (`/problems`):** these are read from PNMT's copy already, and stay as they are.

### 4.5 Freshness in every answer

- **A new record, `RedmineFreshness(DateTime? RefreshedAt, bool Refreshing, DateTime? FailedAt, string? Error)`, is added to every answer that comes from a snapshot:**
  - `RedmineOverview` gets `Freshness` **in place of `AsOf`**;
  - `RedmineIssueDetail` gets `Freshness`;
  - the search answers `RedmineSearchPage(Items, Page, PageSize, TotalCount, Freshness)` instead of `Paged<RedmineIssueItem>`. It has the same fields as `Paged`, plus `Freshness`, so `useServerTable` keeps working;
  - `RedmineStatus` gets `Freshness` of the lookups, for *System health*.
- **The agent's tools** (`redmine_overview`, `redmine_ticket`, `redmine_search`) pass `refreshedAt` through, so the model knows how old the data is. **The agent can't trigger a refresh** (D20).

### 4.6 Restarts and several instances

- **Snapshots live in the process and don't survive a restart** (D17).
  - A restart starts empty and warms the lookups and the overview first (§3.1).
  - Tickets and searches are read again on their first visit.
- **PNMT runs as one instance** (`deploy/docker-compose.yml`). With several, each would have its own worker and snapshots. The plan doesn't change that.

---

## 5. The bar

### 5.1 Where

- **The bar goes in the `#actions` slot of `PageHeader`** (`PageHeader.vue:22`), to the left of the existing buttons. It is a new component, `RedmineFreshnessBar.vue`, on:
  - **the ticket page** (`RedmineIssueView.vue:133-162`), before *Open in Redmine* and *Copy link*;
  - **the overview tab** (`RedmineView.vue`), in place of the line "As of 12:03" (`:291`);
  - **the search tab**, for the page shown.
- **The link dialog's search** (`LinkTicketDialog.vue`) and the panels elsewhere don't get a bar. The dialog is short-lived, and the panels read PNMT's copy (D21).

### 5.2 What it shows

| State | Shown |
|---|---|
| fresh | `Refreshed 2 min ago` (the time in the tooltip), ⟳ |
| refreshing (the button, or the worker on its own) | ⟳ spinning, `Refreshing…`, the button disabled |
| the last refresh failed | ⚠ `Refreshed 12 min ago · refresh failed`, the message in the tooltip, ⟳ |
| never read, Redmine fails | the page's error state, as today (`ErrorState`), with *Try again* |

- **"Refreshed" is when the data was read from Redmine,** not when the page fetched it from PNMT.
- **The time updates by itself** (`RelativeTime`).

### 5.3 The button

1. **A click sends `POST /api/v1/redmine/refresh`** with what the page shows: `{ "view": "overview", "project": 5 }`, `{ "view": "issue", "id": 603 }`, or `{ "view": "search", …the search's query… }`.
2. **The endpoint queues a job in the button lane,** ahead of everything else (§3.3). If a job for the same key is already queued or running, it joins that job and raises it to the button lane (§3.5).
   - It awaits the job up to `WaitSeconds` and answers with the new `RedmineFreshness`.
   - If it ran out of time, it answers `202` with `Refreshing: true`, and the page goes on polling (§5.4).
3. **While the request is open, the button spins** and the bar says *Refreshing…*.
4. **When the answer comes,** the page invalidates its query and fetches the new snapshot from PNMT, which is fast.
   - On failure, it shows a toast with the message, and the bar shows the failed state.
5. **The job doesn't depend on the request** (§3.6). A user who leaves the page doesn't stop it.
6. **Two clicks, or two users,** join the same job (§3.5).

- **Right:** `Redmine: View`, as for reading. The refresh isn't audited, because reads aren't (docs/audit-redmine-zabbix-plan.md §3).

### 5.4 Refreshes the page didn't start

- **While `Freshness.Refreshing` is true, the page polls every 2 s.** This covers the scheduler's refresh and another user's reload. When it turns false, the page has the new data.
- **Otherwise the page polls at its normal pace:**
  - the overview every 30 s (today 60 s, but the request is now cheap);
  - a ticket and the search every 60 s.
- **The overview no longer waits for `/status`** (§1.1). Both are asked at once. `/overview` answers 409 when Redmine isn't configured, and the page shows that as today.

---

## Part B: The two outbound logs

## 6. What is recorded

### 6.1 Two logs

| Log | Channel | Kind | One record per | Kept | Captured in |
|---|---|---|---|---|---|
| **Notifications** | **E-mail** | send | mail, per recipient as today | **the newest 10 000** over all three channels | `EMailNotification` (§6.3) |
| | **Pushover** | send | message | | a handler on the `notifications` client (§6.3) |
| | **HTTP** (webhook) | send | request | | the same handler |
| **Redmine** | **Redmine** | read (`GET`) or write (`PUT`, `POST`) | call | **the newest 1 000** | `RedmineClient` (§6.3) |

- **Each log has its own table and its own limit,** so the frequent Redmine reads never push a notification out.
- **A Redmine job of several calls makes several records, grouped by the job's id.** The details can show all of a job's calls (§7.3).
- **A simulated send** (`Development:DoNotSendNotifications`) is recorded in the notification log with the outcome *Simulated*. It shows the message as it would have gone out, but has no answer.
- **A send that was never tried isn't an operation.** For example, Cookie's limits held it back, or no provider takes the recipient. Its trace stays where it is today: the `CookieOutbox` row and the application log.

### 6.2 The records

**Two tables:**
- `outbound_notifications` (`OutboundNotificationEntity`);
- `redmine_calls` (`RedmineCallEntity`).

Both are `[NotAudited]`, as `ai_requests` is. They share most columns:

| Column | In | |
|---|---|---|
| `Id` | both | `bigint` identity. It gives the order and drives the trimming. |
| `StartedAt`, `DurationMs` | both | |
| `Channel` | notifications | `Email`, `Pushover`, `Http` |
| `Kind` | Redmine | `Read`, `Write` |
| `Method` | both | `GET`, `POST`, `PUT`, `SMTP` |
| `Target` | both | the URL as it is (D18); for Redmine, the path and query; for e-mail, `smtp://host:port` |
| `Recipient` | notifications | the mail address, the Pushover user key (masked, §6.5), or the webhook URL |
| `Summary` | both | for a send, the subject; for Redmine, what the call was for within its job, e.g. `overview: urgent` |
| `Purpose` | both | what it was for: `rule Storage → Alice`, `Cookie: note on #603`, `overview refresh (all projects)`, `ticket #603`, `contact test`, `MCP token blocked` |
| `AskedBy` | both | who: a user's name, `Cookie`, `the agent (token …)`, or `PNMT` for a task |
| `JobId`, `Lane` | Redmine | the job (`uuid`), and its lane when the call started: `Button`, `Write`, `Interactive`, `Background` |
| `QueuedMs` | Redmine | how long the call waited for a slot. It shows the queue at work. |
| `Outcome` | both | `Ok`, `Failed`, `Simulated` (notifications), `Cancelled` |
| `StatusCode` | both | the HTTP status, or the SMTP reply code |
| `Error` | both | at most 1000 characters |
| `RequestHeaders`, `ResponseHeaders` | both | `jsonb`: `[[name, value], …]`, masked |
| `RequestBody`, `ResponseBody` | both | `bytea`, gzip, each capped at `Outbound__MaxBodyKiB` (256) before compression |
| `RequestBytes`, `ResponseBytes` | both | the full sizes |
| `Truncated` | both | true if either body was cut |
| `Transcript` | notifications | `bytea` gzip, e-mail only: the SMTP conversation, with `AUTH` redacted |

**Indexes:**
- notifications: `(Channel, Id)`, `(Outcome, Id)`;
- Redmine: `(JobId)`, `(Outcome, Id)`.

Text search covers `Target`, `Recipient`, `Summary` and `Purpose`. With `ILIKE` over 10 000 rows at most, it needs no index.

### 6.3 Where it is captured

- **Redmine, in `RedmineClient`, into the Redmine log.**
  - `GetAsync` already reads the body into memory, up to 5 MiB (`RedmineClient.cs:78-94`). `WriteAsync` has the body it sends and the text it gets back.
  - Both record the request's and the answer's headers, both bodies, the status and the duration. This includes failures: a timeout, a refused connection, a `401`.
  - The worker passes the job's id, lane and the time the call waited for its slot.
  - `FakeRedmine` sits below the client, so dev and the tests record too.
- **Pushover and HTTP, in `OutboundHttpHandler`, into the notification log.** This is a `DelegatingHandler` added to the `notifications` client (`NotificationService.cs:43`).
  - It copies the request's content, which is always small: JSON or text that PNMT made.
  - It reads the answer up to the cap and puts that back as the answer's content. Neither provider needs more than a short answer.
  - It records an exception as *Failed* and passes it on unchanged.
- **E-mail, in `EMailNotification`, into the notification log.**
  - The request headers are the message's (`From`, `To`, `Subject`, `Date`, `Message-Id`, `Content-Type`, …).
  - The request body is the whole MIME message (`MimeMessage.WriteTo`).
  - The response body is the server's answer to `DATA` (the `answer` of `:66`).
  - The `Transcript` comes from MailKit's `ProtocolLogger` with `RedactSecrets` (MailKit 4.18), so `AUTH` lines don't hold the password.
- **Simulated sends are recorded in `NotificationService.SendAsync`** (`:19-23`). The providers get a `Describe(message)` that gives the request as it would be: headers, body, target. They don't send.
- **What it was for, and who asked: an `OutboundScope`** (an `AsyncLocal`).
  - `NotificationService.SendAsync` opens it with its existing `context` string, e.g. `rule Storage → Alice` or `Cookie: …`.
  - The worker opens it per job, with the job's purpose, its first asker and its id.
  - The handler and the client read it, so no signature in between changes.

### 6.4 Writing without slowing anything

- **`OutboundLog.Record(…)` doesn't wait.** It puts the record into a bounded `Channel` (5 000 entries) and returns.
  - If the channel is full, the record is dropped. A counter per log goes up, and *System health* shows it (§7.2). The send is never held up (R12).
- **A writer in `OutboundLog`** inserts what is waiting every second, in one batch per table, through its own `DbContext`.
  - It compresses outside the request path.
  - **Every 100 inserts into a table, it trims that table** to its limit, with `AiRequestLog`'s query by `Id` (`AiRequestLog.cs:52-57`):
    - `outbound_notifications` to `Outbound__KeepNotifications` (10 000);
    - `redmine_calls` to `Outbound__KeepRedmine` (1 000).

### 6.5 Secrets and private notes

All masking is done by one class, `OutboundRedactor`. It runs before a record leaves the process's memory, and each rule has a test (§13). **Only PNMT's own secrets are masked.**

- **Headers:**
  - `X-Redmine-API-Key`, `Authorization`, `Proxy-Authorization`, `Cookie` and `Set-Cookie` become `•••` followed by the last 4 characters.
  - Keeping the last 4 shows *which* key was used: the read key or Cookie's write key.
- **Pushover:** the app token (`token`) is masked in the body. The user key (`user`) is masked except its last 4 characters, in the body and in `Recipient`.
- **SMTP:** `AUTH` lines are redacted (`ProtocolLogger.RedactSecrets`). The password is never in the message.
- **URLs aren't masked** (D18). Webhook URLs are stored as configured, even when their path or query holds a token (Teams, Slack, Discord). Both logs are for `System: Manage` only (D15).
- **Private notes:**
  - while `Redmine__ShowPrivateNotes` is off, PNMT doesn't show private notes, so the Redmine log doesn't keep them either;
  - in a stored Redmine answer, the `notes` of every journal with `private_notes: true` become `"(private note, not stored: Redmine__ShowPrivateNotes)"`.

### 6.6 How much

- **Caps:** each body is cut at 256 KiB, then gzipped. JSON gzips to roughly an eighth, so a full record is about 64 KiB at most.
  - **The notification log:** a mail or message is a few KiB, so 10 000 records are tens of MB.
  - **The Redmine log:** 1 000 calls are at most about 65 MB, and typically a few MB.
- **How far back each log reaches.** These are estimates, from the intervals in §4 and today's case book:

  | Situation | Redmine calls an hour | The Redmine log (1 000) reaches back |
  |---|---|---|
  | **nobody on a Redmine page** | ≈ 50 for the overview every 10 min, ≈ 6 for the linked copy, ≈ 15–50 for the case book every 5 min, ≈ 5 for the lookups; **about 80–110 in all** | **9–12 hours** |
  | **a Redmine page open all day** | **about 480** (the overview every 60 s) on top | **about 2 hours** |

  - **The notification log's reach depends only on how many notifications go out.** Redmine reads don't count against it. At a few hundred notifications a day, it reaches back for weeks.
  - The Redmine log is meant for the recent picture: what the queue did, and why a page was slow. To reach back further, raise `Outbound__KeepRedmine`.

### 6.7 Running now

- **The worker and `OutboundLog` keep in memory what is in flight:**
  - the operations, with their start, channel, target and purpose;
  - the Redmine queue: per lane, the jobs queued, and the jobs running with their keys, lanes and ages.
- **`GET /api/v1/system/outbound/live`** returns that, plus the newest `Id` of each log and the count of dropped records per log.

---

## 7. System health → Outbound

### 7.1 Tabs

- **System health gets tabs** (`TabNav`), as the Redmine page has:
  - ***Overview***: today's page, unchanged;
  - ***Outbound***.
- **The route becomes `/admin/system/:tab?`.** `/admin/system` stays the overview.
- **The right stays `System: Manage`** for both tabs. It is the page's right today, and the records hold message bodies, addresses, webhook URLs and ticket contents.

### 7.2 The tab

- **At the top, *Running now*.** These come from `/live`, every 2 s:
  - the operations in flight, each with its channel icon, target, purpose and a running timer;
  - the Redmine queue, e.g. `Redmine: 1 button · 0 writes · 1 interactive · 3 background queued · 2 running`;
  - a warning if records were dropped (§6.4).
- **Below, a switch picks the log: *Notifications* | *Redmine*.**
  - It is in the URL (`?log=redmine`). *Notifications* is the default.
  - Each log has its own table, newest first, 50 to a page.

| Log | Columns | Filters |
|---|---|---|
| **Notifications** | time; channel; recipient; subject; purpose; asked by; outcome (badge, with the status code); duration; size | channel, outcome, text |
| **Redmine** | time; kind; method and path; what for (`Summary`); purpose; lane; asked by; outcome; waited (`QueuedMs`); duration; size | kind, lane, outcome, the job, text |

- **The time** is a `RelativeTime`, with the exact time in the tooltip.
- **The filters** are in a `FilterBar` and kept in the URL.
- **Live:**
  - on page 1, the shown log polls every 3 s, as the Connectors' *Calls* do every 5 s (`CallsTab.vue:65`);
  - new rows are marked for a moment;
  - a ***Pause*** switch stops the polling, so a row doesn't move away while it is being read;
  - on other pages the log doesn't poll.
- **Mobile:** the `#mobile` template of `ListTable`, as elsewhere. It shows time, channel or kind, summary and outcome.

### 7.3 The details window

- **A click on a row, or its *Details* button, opens `OutboundDialog.vue`.** It is a modal PrimeVue `Dialog`, as `ConnectorCallDialog.vue`. It isn't a `Drawer`. Both logs use it.
  - It is `min(1100px, 95vw)` wide and can be maximized.
  - **Its URL is `?log=…&op=<id>`,** so a record can be linked to, reopened, or opened in a new browser tab (D13).
- **Its sections:**
  1. **Summary:** time, duration, channel or kind, outcome and status, target, recipient, purpose, asked by.
     - For Redmine, it also has the lane, how long the call waited, and the job, with ***Show the job's calls***, which filters the Redmine log by `JobId`.
  2. **Request:** method and URL; the headers as a table; the body.
     - JSON is shown formatted, text and MIME as they are.
     - ***Copy*** and ***Download*** are next to the body.
     - A cut body says so, e.g. "256 KiB of 1.2 MiB stored".
  3. **Response:** the status, the headers and the body, as above.
  4. **Transcript** (e-mail only).
  5. **Error**, if there is one.
- **Masked values show as `•••abcd`,** and a note says that they are masked on purpose.
- **`←` / `→` step to the previous and next record of the log** without closing the window.

---

## 8. API

| Method | Path | Right | |
|---|---|---|---|
| `POST` | `/api/v1/redmine/refresh` | `Redmine: View` | §5.3, button lane. `?view=overview\|search\|issue` with `project`, `id` or the search's filters; answers `RedmineFreshness`, or `202` while still refreshing. |
| `POST` | `/api/v1/redmine/test`, `/api/v1/redmine/links` | as today | Now button lane (§3.3). |
| `GET` | `/api/v1/redmine/overview`, `/issues`, `/issues/{id}` | as today | They answer from snapshots, with `Freshness` (§4.5). `/issues` answers `RedmineSearchPage`. A new error: `504 redmine_busy`. |
| `GET` | `/api/v1/system/outbound/notifications` | `System: Manage` | `?channel=&outcome=&search=&page=&pageSize=`; answers `Paged<OutboundNotificationItem>`, without headers or bodies. |
| `GET` | `/api/v1/system/outbound/notifications/{id}` | `System: Manage` | `OutboundDetail`: everything, the bodies decompressed as text. |
| `GET` | `/api/v1/system/outbound/redmine` | `System: Manage` | `?kind=&lane=&outcome=&job=&search=&page=&pageSize=`; answers `Paged<RedmineCallItem>`. |
| `GET` | `/api/v1/system/outbound/redmine/{id}` | `System: Manage` | `OutboundDetail`, as above. |
| `GET` | `/api/v1/system/outbound/live` | `System: Manage` | §6.7 |

The OpenAPI types are generated as for the other endpoints, and the web UI uses them through `api.system.outbound` and `api.redmine.refresh` (`resources.ts`).

---

## 9. Settings

| Setting | Default | |
|---|---|---|
| `Redmine__Parallelism` | `4` | calls in flight at most; one of them is kept for the button, write and interactive lanes (§3.4) |
| `Redmine__RefreshSeconds` | `60` | how old a watched snapshot may get (§4.3) |
| `Redmine__WatchMinutes` | `5` | how long a request keeps a snapshot watched |
| `Redmine__KeepMinutes` | `60` | when a snapshot nobody asked for is removed |
| `Redmine__MaxSnapshots` | `500` | §4.1 |
| `Redmine__WaitSeconds` | `30` | how long a button or interactive caller waits (§3.6, §5.3) |
| `Redmine__BackoffSeconds` | `30` | the background lane's pause after `429` or `503` (§3.4) |
| `Redmine__SyncMinutes` | `10` | as today: the linked copy, and the overview of all projects while unwatched |
| `Redmine__TimeoutSeconds` | `15` | as today, per call |
| `Outbound__KeepNotifications` | `10000` | the notification log: the newest e-mails, Pushover messages and HTTP notifications kept (§6.4) |
| `Outbound__KeepRedmine` | `1000` | the Redmine log: the newest Redmine calls kept (§6.4) |
| `Outbound__MaxBodyKiB` | `256` | per body, before compression (§6.2) |

They are documented in `deploy/.env.example`, `deploy/README.md` and `dev.env`, as the other settings are.

---

## 10. Decisions

| | Decision | Why |
|---|---|---|
| D1 | **One worker owns `RedmineClient`;** it replaces `RedmineSync` and keeps its name. | R2. One place for order, limits, failures and the record. |
| D2 | **The queue is in memory, not a table.** | Its jobs are reads that refill snapshots, which can be asked again, and writes, whose record is the `CookieOutbox` row and whose caller waits, as today. A table as with the Connectors (connectors-plan D7) would hold nothing worth keeping across a restart. |
| D3 | **Four lanes: button, write, interactive, background (review 1).** First in, first out within a lane. Joining from a higher lane raises a job. Background never takes the last slot. | R3, R14. A person who pressed a button never waits behind anything; nobody waits behind the scheduler or the case book. |
| D4 | **Four calls at once, one write at a time.** A write a person ordered goes before Cookie's queued writes. | Four parallel reads take a cold overview from 8+ round trips down to about 3, without loading Redmine much. Writes one at a time keep Cookie's notes in order. |
| D5 | **Same key, same job.** | R4. Reloads and simultaneous visitors cost one call. |
| D6 | **Snapshots replace the `IMemoryCache` keys. They are kept while in use and refreshed behind the page.** | R1. An expiry would make the next visitor wait again. |
| D7 | **What PNMT knows (links, problems) is added at every read, not kept in the snapshot.** | R7. Links show at once, at the cost of a few database queries. |
| D8 | **Watched snapshots every 60 s. Unwatched ones only when someone looks again, except the overview of all projects, every 10 min.** | Fresh where people look, little load and few records where they don't (§6.6). |
| D9 | **The refresh button is a `POST` that queues in the button lane and waits. The job outlives the request.** | R6, R14, with one request and no push channel. |
| D10 | **One record per operation. For Redmine, one per call, grouped by the job, with its lane and its wait.** | R8. A slow overview shows which call was slow, and whether it waited in the queue. |
| D11 | **Captured in `RedmineClient`, in a handler on the `notifications` client, and in `EMailNotification`.** | The places that have the bytes. No provider's signature changes, because the purpose comes through `OutboundScope`. |
| D12 | **Two logs in two tables (review 1): the newest 10 000 notifications (e-mail, Pushover, HTTP) and the newest 1 000 Redmine calls.** Bodies are gzipped and capped at 256 KiB. | R10. The frequent Redmine reads don't push notifications out, and the Redmine log stays small. The bodies follow `ai_requests`. |
| D13 | **The details are a modal dialog with its own URL (`?log=…&op=`), not a drawer (review 1).** | As requested ("a window, no sidebar"). The URL makes it linkable, and it opens in a new browser tab too. |
| D14 | **Live means polling: 2 s for *Running now*, 3 s for the log shown.** | The web UI has no push to the browser. The Connectors' *Calls* poll as well. |
| D15 | **The tab's right is `System: Manage`.** | It is the page's right, and the records hold message bodies, webhook URLs and ticket contents. |
| D16 | **PNMT's own secrets are masked before storing, with a test per rule. Private notes are kept only if PNMT shows them.** | R11. |
| D17 | **Snapshots don't survive a restart (review 1).** | The warm-up covers the overview. Tickets and searches are read again on their first visit. |
| D18 | **URLs aren't masked (review 1).** Webhook URLs are stored as configured, even with a token in the path or query. | The log is for admins, who manage those URLs on the rule's page anyway, and the full URL is what shows which endpoint was called. |
| D19 | **Other outbound traffic comes later (review 1):** Zabbix, MikroTik, Connectors, the uplink check, the feeds. | The handler (§6.3) can be added to any named client then. The AI already has `ai_requests`. |
| D20 | **The agent can't refresh (review 1).** Its tools pass `refreshedAt` on (§4.5). | Refreshing is a person's decision. A person can press the button. |
| D21 | **The age of PNMT's copy in the panels comes later (review 1)**, with `FetchedAt` per ticket. | The panels aren't Redmine pages, and the copy follows `SyncMinutes`. |

---

## 11. Open questions

None. The first review (2026-11-19) answered the seven open questions of the draft:

| | Question | Answer |
|---|---|---|
| Q1 | How should the 10 000 be split? | **Two logs: 10 000 notifications, 1 000 Redmine calls** (D12). |
| Q2 | Should snapshots survive a restart? | **No** (D17). |
| Q3 | Should webhook URLs with a secret in the path be masked? | **No** (D18). |
| Q4 | Should other outbound traffic be recorded? | **Later** (D19). |
| Q5 | Is "a window" a dialog in the page, or a browser tab? | **The dialog, with its own URL** (D13). |
| Q6 | May the agent's tools refresh? | **No** (D20). |
| Q7 | Should the panels show the age of PNMT's copy? | **Later** (D21). |

The review also added **the button lane** (R14, D3): what a person starts by pressing a button goes before everything else in the queue.

---

## 12. Phases

All four phases shipped together in 1.16.0.

| Phase | What |
|---|---|
| **O1: the two logs** | The two tables and their migration (`OutboundLogs`); `OutboundLog` (channel, writer, trimming per table); `OutboundRedactor`; capture in `RedmineClient`, `OutboundHttpHandler` and `EMailNotification`; simulated sends; `OutboundScope` in `NotificationService`; the API; System health's tabs, the *Outbound* tab with its two logs, and `OutboundDialog`. |
| **O2: the worker** | `RedmineWorker` with the four lanes, slots, joining and raising, and backoff; the jobs of §3.2; every call site moved (§1.2), with its lane; `ByPerson` on `OutboxRequest`; `RedmineSync` folded in; the overview's and stale tickets' calls in parallel; `RedmineClient` made internal, with its test; lane and wait in the Redmine log. |
| **O3: snapshots and the bar** | The snapshots; watched, due, scheduler and warm-up; `RedmineFreshness` in the answers; `POST /refresh`; the after-effects of writes; `RedmineFreshnessBar.vue` on the three views; the overview no longer waits for `/status`; polling while refreshing. |
| **O4: documentation** | The settings in `deploy/.env.example`, `deploy/README.md` and `dev.env`; `docs/release-notes-1.16.md`; the in-app docs on Redmine; the version. |

---

## 13. Testing

- **The worker** (`PNMTD.Tests/Redmine/RedmineWorkerTests.cs`):
  - **order:** with the one slot busy and jobs waiting in all four lanes, the freed slots go to button, then write, then interactive, then background;
  - **slots and raising:** background calls never take the last slot; a background job raised to the button lane goes before a queued interactive one;
  - **joining:** the same key is one job, raised by a button press that joins it; a fresh job doesn't join a running one;
  - **waiting:** a waiter that cancels doesn't cancel the job;
  - **snapshots:** an old snapshot answers at once; a due one is refreshed behind the page;
  - **the button:** `/refresh` waits for the new data; with Redmine down it answers 502 and the page keeps the old data with the failure; an unknown ticket is 404, an unknown view 400;
  - **a person's write:** an Outbox note with `ByPerson` is in the button lane, Cookie's own in the write lane, each with its fresh project check in the same job;
  - **the architecture:** no file but the worker, the client, the setup, the fake and Zabbix's handler names `RedmineClient`.
- **The existing Redmine tests** (`RedmineTests`, `RedmineStaleTests`) run through the worker; the sync test calls `RedmineWorker.SyncAsync`, and Redmine down shows a cached ticket and 502 for one never read.
- **The logs** (`PNMTD.Tests/Outbound/OutboundLogTests.cs`):
  - Pushover through the debug tests' fake, the token and the user key masked, through the API too, and forbidden for non-admins;
  - a webhook's URL kept as it is;
  - a simulated send, and an e-mail that never went out because `Smtp:Host` is empty;
  - an e-mail against a small SMTP fake in the test process: the MIME, the server's answer, the conversation without the password;
  - a Redmine call with its job, the key masked, no private note, through the API's job filter and `/live`;
  - each log trimmed to its own newest records, a flood of Redmine calls never removing a notification;
  - a full queue drops and counts instead of waiting;
  - the redactor's rules.
- **The web UI:** `outboundFormat.test.ts` for the dialog's helpers; `e2e/redmine.spec.ts` checks the bar and ⟳; `e2e/outbound.spec.ts` opens the Redmine log after a Redmine page, a call's window with `X-Redmine-API-Key` masked, steps with →, and reopens it from its URL.

---

## 14. Risks

| Risk | Answer |
|---|---|
| People act on old data. | The bar always says how old it is. A visit to an old page refreshes it behind the page. The button refreshes it before anything else. Writes mark the views due. Links are never old (D7). |
| The Redmine log reaches back only about 2 hours when a Redmine page is open all day. | It is meant for the recent picture (§6.6). `Outbound__KeepRedmine` raises it. The notifications have their own log and aren't affected. |
| A secret ends up in the logs. | PNMT's own secrets are masked by one redactor, with a test per rule. Webhook URLs are stored as they are, by decision (D18), and both logs are admin-only. |
| A hanging Redmine blocks the queue. | Each call has its timeout; a slot is kept for the button, write and interactive lanes; background jobs back off; a waiter gets `504` after `WaitSeconds`. |
| Many button presses starve everything else. | They join per key (§3.5), so a person can't queue more than one job per view. Interactive and background jobs go on in the slots the button lane doesn't use. |
| Snapshots use too much memory. | `MaxSnapshots`, `KeepMinutes`, and only Redmine's fields in a snapshot. |
| Moving the case book and the Outbox onto the worker changes Cookie's timing. | Cookie's writes come right after button presses. The case book's reads are background, which is what they are. Its interval doesn't change. |
| The logs' writer falls behind. | Batches every second; drops when the channel is full, and shows that it dropped (§6.4). |

---

## 15. As built

What differs from the sections above, and what came on top.

- **Cookie's staff lookup** (§4.1, `staff:*`) keeps its own one-hour `IMemoryCache` entry (`cookie-staff:*`, as before). Its calls go through the worker, in the case book's background job.
- **System health's *Redmine sync*** keeps its interval of 600 s (§3.1). `LAST_REDMINE_SYNC` is still written by the linked copy every `SyncMinutes`, which is what the interval checks.
- **The agent's ticket search** (`redmine_search`) has no `refreshedAt`, because its answer is the tools' common page shape (§4.5). `redmine_overview` carries the whole `freshness`, and `redmine_ticket` its `refreshedAt`.
- **A send that never reached the wire** (no Pushover key, no `Smtp:Host`, not a mail address) is recorded too, by `NotificationService`, as *Failed* with the provider's reason (§6.1 left it out). `OutboundContext.Recorded` tells it whether a provider recorded the send already.
- **The Outbox treats the queue's `504`** (its lookups not read within `WaitSeconds`) as a failed send, as it does Redmine's own failures (§3.6).
- **A failed ⟳** answers 502; the bar shows a toast and then the failed state (§5.3).
- **The web UI:**
  - System health's old content is its own `OverviewTab.vue`, so its 15–30 s polls stop on the Outbound tab;
  - the route lists its tabs: `/admin/system/:tab(overview|outbound)?`;
  - the Redmine search only fetches while its tab is shown, so it doesn't keep a search snapshot watched from the overview;
  - the notification log has *Channel* and no *Kind* (always a send); the Redmine log has *Kind*, method and lane, and a *Lane* filter;
  - the log stops polling while the details window is open, so `←` / `→` don't shift under it;
  - `RedmineStatus.freshness` isn't shown in the Integrations panel yet.
- **The API's paths** are in their own `OutboundController` (`api/v1/system/outbound`) and `OutboundQueries`, not in `SystemController`.

---

## Appendix: files

| File | Change |
|---|---|
| `PNMTD/Redmine/RedmineWorker.cs` (new) | the jobs, the gate with its lanes and slots, joining and raising, the snapshots, the scheduler, the linked copy |
| `PNMTD/Redmine/RedmineWorker.Loads.cs` (new) | the reads of §3.2, Redmine's query syntax, *Test connection* |
| `PNMTD/Redmine/RedmineService.cs` | reads from snapshots and asks the worker, with the lane; DB parts at read time; no `IMemoryCache` |
| `PNMTD/Redmine/RedmineClient.cs` | `internal`; records each call into the Redmine log; `RedmineException` with the HTTP status and `Retry-After` |
| `PNMTD/Redmine/RedmineSetup.cs` | `RedmineSync` removed; the worker registered |
| `PNMTD/Redmine/RedmineOptions.cs` | the Redmine settings of §9 |
| `PNMTD/Api/V1/RedmineController.cs` | `POST refresh`; `Test` and `Link` in the button lane |
| `PNMTD/Api/V1/Contracts/Redmine.cs` | `RedmineFreshness`, `RedmineSearchPage`; `RedmineOverview.AsOf` replaced |
| `PNMTD/Cookie/Outbox.cs`, `PNMTD/Cookie/CookieExecutor.cs` | Redmine sends through the worker; `ByPerson` on `OutboxRequest` |
| `PNMTD/Cookie/CaseBook.cs`, `PNMTD/Cookie/CookieService.cs` | through the worker |
| `PNMTD/Services/Domain/SearchService.cs` | 5 s for a ticket number nobody looked up yet |
| `PNMTD/Tools/Read/IntegrationReadTools.cs` | `refreshedAt` in the ticket |
| `PNMTD/Outbound/OutboundLog.cs`, `OutboundRedactor.cs`, `OutboundScope.cs`, `OutboundHttpHandler.cs`, `OutboundOptions.cs`, `OutboundQueries.cs`, `OutboundSetup.cs` (new) | §6, §8 |
| `PNMTD/Models/Db/OutboundEntities.cs` (new), `PnmtdDbContext.cs`, migration `OutboundLogs` | `outbound_notifications`, `redmine_calls` and their indexes |
| `PNMTD/Notifications/NotificationService.cs`, `EMailNotification.cs`, `PushoverNotification.cs`, `HttpNotification.cs`, `INotificationProvider.cs` | the scope; capture; `DescribeAsync` for simulated and unsent sends; the handler on the client |
| `PNMTD/Api/V1/OutboundController.cs`, `Contracts/Outbound.cs` (new) | the five `outbound` endpoints |
| `PNMTD.WebUI/src/views/redmine/RedmineFreshnessBar.vue` (new), `RedmineView.vue`, `RedmineIssueView.vue` | the bar; polling; no waiting for `/status` |
| `PNMTD.WebUI/src/views/admin/SystemView.vue`, `views/admin/system/OverviewTab.vue`, `OutboundTab.vue`, `OutboundDialog.vue`, `outboundFormat.ts` (new) | the tabs, the two logs, the window |
| `PNMTD.WebUI/src/router/index.ts`, `src/api/resources.ts`, `src/composables/useServerTable.ts` | `/admin/system/:tab?`; the new calls; the search page's freshness |
| `PNMTD.WebUI/src/views/docs/IntegrationsDocs.vue` | the bar, ⟳ and the Redmine log |
| `PNMTD.Tests/Redmine/*`, `PNMTD.Tests/Outbound/*`, `PNMTD.Tests/Hosting/EndpointAuditTests.cs`, `PNMTD.WebUI/e2e/*` | §13 |
| `deploy/.env.example`, `deploy/README.md`, `dev.env`, `docs/release-notes-1.16.md` | §9, O4 |
