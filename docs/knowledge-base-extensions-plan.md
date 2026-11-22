# Plan: Knowledge base extensions: page links and the knowledge map, likes and *Used to solve a problem*, recent changes, *Written with AI*, and the sensitive mark made visible

| | |
|---|---|
| Status | **Built: P0–P7, on master** (2026-11-22, §20 *As built*). Not released: the version isn't bumped and nothing is tagged. Revision 2: **the review's answers applied** (§19): everything as recommended, except that **a link between two pages is mutual** (§7). Revision 1: the first plan |
| Date | 2026-11-21 |
| Scope | docs/knowledge-base-feature-text.md:<ol><li>**Pages linked to each other**, and **a map** of pages, locations, hosts and sensors and how they are connected, sharing the network map's code and libraries (§7, §8).</li><li>**Likes** and ***Used to solve a problem***, each with a counter. The page's authors can be e-mailed about them, and each person chooses that in their settings. The settings and the sending are **general**, so other features can notify people the same way, and **the existing notification task sends them** (§9, §10).</li><li>**Recent changes** at the top of the knowledge base: at most 10, from the last 14 days (§6).</li><li>**A checkbox *Written mostly with AI*** (§5).</li><li>**The sensitive mark**, which exists but can hardly be found (§2.2), **made visible** (§4).</li></ol> |
| Not in scope | Comments on pages, watching a page, e-mails about edits (they fit the catalog later, §10.7). Pushover for these notifications. Moving the lounge's switches into the new settings (§19, decision 10). Saved positions on the knowledge map, and dragging nodes there. A scale for "how much AI". Likes or uses over MCP. **Changing what *sensitive* means:** people, Viewers included, still read sensitive pages (docs/locations-knowledge-plan.md §14.1, decision 8). Permissions per page stay a non-goal |
| References | **docs/locations-knowledge-plan.md:** the knowledge base as planned (§6, §7, §8.2) and as built (§15). **docs/network-map-plan.md §8.2, §14** and **docs/network-map-positions-plan.md:** the map's drawing and its libraries. **docs/notification-recipients-plan.md §1, §4:** the alert path and whom a rule reaches. **docs/redmine-queue-outbound-plan.md §6:** the notification log. **docs/lounge-plan.md §8.2, §9.4:** the only per-person switches today. **docs/mcp-plan.md §7.3:** MCP's writes. **docs/audit-redmine-zabbix-plan.md §5.2:** areas and levels |
| Target | 1.20.0 |

**Words.** ***A page link*** is a mutual link between two pages, set in either page's details like a link to a host; ***a links-only revision*** is the revision a page gets when a page link to it is made or removed from the other page; ***a link in the text*** is a Markdown link to `/pnmt-admin/kb/12`, which exists today. ***A use*** is one person recording that a page just helped them solve a problem. ***The page's authors*** are the people who created it or made an edit to its title, summary or content (`kb_revisions.IsEdit`). ***A kind*** is something a person can be notified about (*someone liked a page I wrote*); ***a channel*** is how (e-mail); ***a preference*** is one person's choice for a kind and a channel. ***A user notification*** is one row in the new queue: one event for one person. ***A batch*** is the user notifications sent as one e-mail.

---

## 1. Summary

| Topic | Decision |
|---|---|
| **The sensitive mark** | **It is built:** the server, the history, the audit and `MachineAccess` work. In the UI it is only in the page's ⋯ menu, in the AI dialog (which never opens while AI is off), and in the credentials warning (§2.2). **Made visible:** a switch in the editing page's details, a *Sensitive* tag in the page's header, a row in *About this page*, and a filter in the list. Unmarking in the editor takes the two statements, recorded on the revision the save makes (§4) |
| **Written with AI** | A checkbox ***Written mostly with AI*** in the editing page's details. A tag on the page, an icon in lists, a filter. Part of every revision. **Pages created over MCP start checked** (§5) |
| **Recent changes** | A panel above the list: the pages changed in the last 14 days, **one row per page** (its newest change: who, when, what, +/− lines, the note), at most 10. Shown on the plain list only, not while searching or filtering (§6) |
| **Page links** | **A fourth link kind, *Page*, and mutual** (decided in the review): set on either page, shown on both as *Related*, removed from either. Stored as two mirrored rows, so each page's links stay its own. **Both pages get a revision:** the one where it was set in its save, the other a ***links-only revision***, which never causes an edit conflict for someone editing that page and never loses their link. Links in the text stay one-way, and are shown under *In the text* (§7) |
| **The knowledge map** | `/kb/map`: pages, locations, hosts and sensors as nodes; page links, links in the text, links to objects, and the structure (sensor → host → location → parent) as edges. Around one page or object (`?focus=page:42`, 2 steps by default), or the whole knowledge base with filters, at most 600 nodes. **The network map's layout, geometry, focus and pan/zoom are shared, and its libraries; its canvas isn't** (§8) |
| **Likes** | One per person and page, undone by clicking again. A counter on the page and in lists, *most liked* as an order. Who liked is shown (§9) |
| **Used to solve a problem** | **An event, not a toggle:** a click records a use; the same person can record another after 12 hours. An optional note (*printer on the 2nd floor*). A counter, *last used 3 days ago*, the recent uses on the page, *most used* as an order (§9) |
| **Notifications to people** | **General:** a catalog of kinds in code, each person's preferences per kind and channel on ***Account → Notifications***, and a durable queue, `user_notifications`, filled in the same transaction as the like. **Sent by the existing `NotificationTask`, on a second timer of its own**, through `NotificationService`: every e-mail is in the notification log and simulated in development, and **an alert never waits for a person's e-mail.** Batched (15 minutes, one e-mail per page), at most 20 a day per person, retried (§10) |
| **Who is told** | The page's authors, without the person who acted, if they still have *Knowledge base: View*, are active and have an e-mail address. A sensitive page is named by its number only (§9.4, §10.5) |
| **Rights** | Likes and uses: *Knowledge base: View*, like views. The map: *Knowledge base: View*; the states of hosts and sensors only with *Monitoring: View*. Page links, sensitive, *Written with AI*: *Knowledge base: Edit*. Notification preferences: one's own (§14) |
| **Not breaking anything** | New request fields mean *unchanged* when missing; a links-only revision is skipped by every revision check; the alert loop isn't touched; the network map's layout moves without changing a result; a page link to a sensitive page never reaches a machine. Each has a test (§15) |

---

## 2. Today

### 2.1 The knowledge base as built

- **Pages** are `kb_pages` (`KbPageEntity`, `[Audited]`): number, title, summary, content, status, `Sensitive`, `Revision`, who created and updated it, `ArchivedAt`, the `Views` counter and the generated search vector.
  - **Every change is a full snapshot** in `kb_revisions`, written by `KbRevisions.CommitAsync`. It also derives the plain text, the page refs and the file uses, and lists what changed (`KbChangeKinds`).
  - A save that changes nothing makes no revision (`KbPageService.Differs`).
- **A save replaces every writable field.** `PUT /kb/pages/{id}` takes the whole `KbPageRequest`:
  - the page view's quick saves (status, a link, confirming a tag) send the whole page through `kbRequest()` in `PNMTD.WebUI/src/api/kb.ts`;
  - MCP's `kb_page_update` reads the page and sends back what it didn't change.

  **So a new field must mean "unchanged" when it is missing**, or every older caller would reset it.
- **Links** are `kb_links`: one row per page and target, with exactly one of `LocationId`, `HostId` and `SensorId` (the check `CK_kb_links_one_target`), each cascading with its target.
  - `KbLinkKind` has three values. It is used 41 times in 6 server files, and in 5 web files.
  - `KbRevisions.RefOf` falls through to `SensorId!.Value`: a fourth kind must be handled there, or it throws.
  - `redmine_links` already allows a page as a target, with `num_nonnulls("PageId", "LocationId", "HostId", "SensorId") = 1`. That is the precedent for four.
- **Between pages there are only links in the text.**
  - `MarkdownText` finds `/pnmt-admin/kb/12`, and `kb_page_refs` keeps them by number.
  - The page shows *Linked from*. **`KbPageDetail.LinksTo` is computed, but no view shows it.**
  - The editor's *Link to a page…* inserts such a link.
- **Views are the pattern for counters:**
  - `POST …/views` is counted once per person per 30 minutes, remembered in memory (`KbViews`);
  - it adds 1 to `kb_pages.Views` with SQL and to the day's row in `kb_page_views`, with no revision and no change to `UpdatedAt`;
  - it is `[NotAudited]`, and **the only write a Viewer makes** (the role matrix lists it with *Knowledge base: View*).
- **The list** `/kb` (`KbListView.vue`, `KbPageList.ListAsync`):
  - orders by newest, updated, views, edits, title, and relevance while searching;
  - its filters are in the URL (`listState.ts`); the tag browser is beside it.
  - *Recently updated* exists, but shows neither who changed a page nor what.
- **Rights:** `Area.KnowledgeBase`, with *View*, *Edit* and *Manage* (`PNMTD/Auth/Rights/Rights.cs`). Each endpoint has `[RequireRight]`.
  - `EndpointAuditTests` lists every endpoint with its right, and fails for one it doesn't know.
  - Every write needs `[Audit(…)]` or `[NotAudited("…")]`.
  - `AuditModelTests` checks that every entity is `[Audited]` or `[NotAudited("…")]`.
- **Machines** read through `MachineAccess.Pages` (pages not sensitive): the AI tag task, MCP and Cookie.
  - MCP reads with `kb_search` and `kb_page` (text only, no links).
  - It writes with `kb_page_create`, `kb_page_update` and `kb_page_archive`, through `KbPageService`. Such a revision says `Via = "mcp"`.

### 2.2 The sensitive mark: built, but hard to find

**On the server, it is complete:**

- `KbPageEntity.Sensitive`, and `PUT /kb/pages/{id}/sensitive` (`KbPageService.SetSensitiveAsync`):
  - marking takes effect at once;
  - unmarking needs both statements, for the current revision;
  - both are revisions, go to the audit log as `KbPageSensitivityChanged`, and cancel a running AI scan.
- `MarkSensitive` in the save request, for the credentials warning's *Mark as sensitive and save*.
- `MachineAccess` keeps the page from AI, MCP and Cookie, and the AI tag task refuses it twice.

**In the web UI, it is in three places, each hard to reach:**

| Place | Shown when |
|---|---|
| ***Mark as sensitive*** / ***Unmark as sensitive…*** in the page's ⋯ menu (*More actions for the page*, `KbPageView.vue:178–183`) | With *Knowledge base: Edit*, **behind the ⋯ button** of a saved page |
| ***Mark as sensitive*** in the AI dialog (`AiTagsDialog.vue`) | Only when the dialog opens: after a new page's first save, after about one edit in ten, or from *Find tags with AI*. **And only while AI is usable and `KnowledgeBase:AiTags` is on** (`canAi`, `KbPageView.vue:98`). **With AI off, never** |
| ***Mark as sensitive and save*** in the credentials warning (`MarkdownEditor.vue:974–985`) | Only when a save has new lines that look like credentials |

Once a page is marked, a lock icon shows in its header, the list rows and the panels, and the page says *Sensitive: never shown to AI or MCP.*

**Missing:** the editing page's details (summary, tags, links and the note, nothing else), a new page, the list's filters, and *About this page*.

**So on an installation without AI, a person writing pages never sees the mark** unless they open the ⋯ menu of a saved page. Nothing is broken; it is placed where nobody looks. §4 moves it into view, with a small change on the server.

### 2.3 How notifications are sent today

#### The alert path: `NotificationTask`

1. **`PNMTD/Tasks/NotificiationTask.cs`** (sic) is an `IHostedService` with a `Timer` every 10 seconds and a `RunGuard`, so runs never overlap.
   - It is registered in `Program.cs` with the other monitoring tasks, under `BackgroundTasks:Enabled`. That is off in the integration tests, which call `RunOnce()` directly (`NotificationRecipientsTests`).
   - It writes `LAST_NOTIFICATION_TASK_RUN` on every run, and *System → Task health* shows it (`SystemService.Tasks`).
   - Unlike the checks that use `TaskRunDecisionMaker`, it doesn't pause while the uplink is down.
2. **Its queue is implicit.** `GetAllPendingNotificationsForLastMinutes` (`PNMTD/Data/DbContextNotificationExtensions.cs:73`) takes:
   - the events of the last 30 minutes that have no `notificationruleevent` row;
   - each paired with the rules that cover its sensor (links, then scopes minus exclusions), and the sensor's effective importance.
3. **For each pair:**
   - `NotificationRuleTriggerLogic.Eval(type, old code, new code)` decides, and the rule, sensor and host must be enabled;
   - if the rule fires, `NotificationRecipients.ResolveAsync` (once per rule and run) turns it into deliveries. An address is taken as it is; a user, a group's active members or the responsible of the day are reached at `users.Email` and `users.PushoverKey`, by the rule's channels.
4. **Each delivery** goes out through `NotificationService.SendAsync(new NotificationMessage(recipient, subject, short, long, importance), context, simulate)`. The rule's `LastSentAt` and `LastSentTo` are set when one went out.
5. **Then the `notificationruleevent` row is written, whatever the send's result:**
   - a failed send is not retried;
   - an event older than 30 minutes is never sent.

   That fits alerts: the next change of state sends again. **It doesn't fit a message that exists only once**, such as *Anna liked your page*.
6. Every fifth run cleans up old rows.

#### The sender: `NotificationService`

- **It picks the provider by the recipient's form:** `pushover://…`, `http(s)://…`, or an e-mail address.
  - `EMailNotification`: MailKit, the `Smtp:*` settings, a plain-text body, **a 30-second timeout**.
  - `PushoverNotification` and `HttpNotification`.
- **Providers return a `DeliveryResult`** and never throw for a delivery problem.
- **Every send is in the notification log** (`OutboundLog`, *Admin → Notification log*), sent, failed or simulated, with its purpose and who asked.
- `Development:DoNotSendNotifications` simulates every send.

#### Other senders to people

| Sender | How it works | A queue? |
|---|---|---|
| **Cookie's `Outbox`** (`PNMTD/Cookie/Outbox.cs`) | Cookie's messages, access notices, *Was this you?* and the lounge's e-mails. Counted against daily and hourly limits from Cookie's settings, sent at once through `NotificationService`, and kept as a row of `cookie_outbox` (Sent, Failed, Simulated, HeldBack). A person can send what a limit held back | **No:** synchronous, called from Cookie's loops, no retry. Its limits, states and view are Cookie's |
| **`McpAlerts`** (`PNMTD/Mcp/McpStrikes.cs`) | A blocked MCP token: every active admin, by Pushover and e-mail, directly | No |
| **`People.TestAsync`** | *Account → Contact → Test* | No |

#### Queues in the code base

- **`AiWork` and `AiWorker`:** a table, states, retries (5 times a minute apart, then 1 hour, 3 hours and every 24 hours), and `MaxAttempts`. **It runs only while AI is usable.**
- **`RedmineWorker`**, with its lanes; **`NetworkScanQueue`**; **`ConnectorWorker`**.

**None of them carries messages to people.** The AI queue can't, since it stops when AI is off.

#### Per-person choices

- **Only the lounge has them:** `users.LoungeMentions` and `users.LoungeEmails`, both on by default.
  - They are set on *Account → Lounge* (`PUT /account/lounge`, audited as `LoungeSwitchesChanged`) and on the admin's user page.
- **They are one column per switch on `users`.** A third and a fourth feature would add more columns and more panels.

#### What follows for this plan

- **A message about a like needs what alerts don't have:**
  - a durable row, since it exists only once;
  - retries;
  - the person's choice;
  - batching: ten likes in an hour are one e-mail.
- **What to reuse is there:** the contact details, `NotificationService` with its providers, the notification log, the simulation, and the running `NotificationTask` with its place on task health.
- **The alert loop must not change, and must never wait for a person's e-mail.** A hanging SMTP server costs up to 30 seconds per message.

### 2.4 The network map's drawing

- **`PNMTD.WebUI/src/views/network/networkLayout.ts`** (pure, with a vitest) lays out in two stages with d3-force, location circles from d3-hierarchy's `packEnclose`, a seeded random source, and saved positions.
  - `LayoutNode.kind` (`Router` or `Host`) only chooses the radius.
  - **With every node's group null, it is a plain force layout:** each node is a group of its own, without a circle.
- **`src/composables/usePanZoom.ts`** (with a vitest) is already shared: wheel, pinch, dragging the background, fit, centre.
- **`src/views/network/MapCanvas.vue`** (541 lines) draws the SVG.
  - It holds the edge paths that bend apart for parallel edges, the bounds, the focus, and **moving and saving routers' positions**.
  - It is bound to `NetworkGraph`, with the network's glyphs and edge styles (`networkFormat.ts`).
- **The patterns around it:** the side panel as a non-modal `Drawer` (`DevicePanel.vue`), `?focus=router:<id>` (`parseFocus`), filters in the URL, and *the same data gives the same picture*.
- **`d3-force` 3.0.1 and `d3-hierarchy` 3.1.3 are direct dependencies.** Cytoscape and vis-network were rejected (docs/network-map-plan.md §14). **No new library is needed.**

---

## 3. Requirements

| # | Requirement |
|---|---|
| R1 | **Pages can be linked to each other,** as they are linked to locations, hosts and sensors. **A link is mutual:** both pages show it, and either can remove it (review of revision 1) |
| R2 | **A map shows pages, locations, hosts and sensors and how they are connected.** It shares code with the network map, or at least its libraries |
| R3 | **A person can like a page,** and the page shows how many did |
| R4 | **A person can record that they just used a page to solve a problem,** and the page shows how often |
| R5 | **The page's authors can be e-mailed about likes and uses, and each person decides in their settings.** The settings are general, so other features can offer their own notifications in the same place |
| R6 | **These notifications go through the background task that already sends notifications to people** |
| R7 | **The knowledge base's page shows the newest changes at the top:** at most 10, from the last 14 days |
| R8 | **A page can be marked as written mostly with AI** |
| R9 | **The sensitive mark can be seen and set where pages are written and read** |
| R10 | **Nothing that exists breaks:** saves by older callers and MCP, the alerts, the network map, `MachineAccess`, the history, the lounge, upgrades (§15) |

---

## 4. The sensitive mark, made visible (R9)

**What it means doesn't change:** a sensitive page is never sent to an AI and never shown to MCP or Cookie; people read it, Viewers included.

```
┌──┬─────────────────────────────────────────────────────────────────────────────┐
│≡ │ ‹ KB-42 [Printer setup, 2nd floor           ] Published ▾  Visual│Markdown   │
│  │                                                           [Cancel] [Save]   │
│  │ ▾ Details: summary · 2 tags · 1 link · sensitive · written with AI           │
│  │   Summary [………………………………………………………………]   Tags  #printer #zrh  [Add a tag…]       │
│  │   Links   ⌂ Zurich DC › Room 2.13 ×   📖 KB-12 Drivers ×  [Link to…]          │
│  │   [■] Sensitive: never sent to AI, never shown to MCP                       │
│  │   [■] Written mostly with AI                                                │
│  │   Note for the history [……………………………………]                                   │
```

- **The editing page's details** get a switch ***Sensitive: never sent to AI, never shown to MCP***.
  - **A new page opens its details**, so the switch is seen before the first save. Folded, the details' line names it: *summary · 2 tags · 1 link · sensitive*.
  - **Turning it on** marks the page with the save, in the same revision: audited as `KbPageSensitivityChanged`, and a running AI scan is cancelled.
  - **Turning it off** on a sensitive page opens the two statements under the switch: *no credentials*, and *no personal data, and no confidential or IP-protected information*.
    - Both must be checked before *Save* is allowed.
    - **They are recorded as the declaration of the revision this save makes**, so they cover the text being saved, not the text before.
    - Turning the switch on again closes them.
- **Reading the page:**
  - **a *Sensitive* tag with the lock** beside the status in the header, instead of the lone icon. The note under the header stays;
  - ***About this page*** gets a row *Sensitive: yes* or *no*, with *Mark…* or *Unmark…* for *Knowledge base: Edit*;
  - the ⋯ menu keeps its two entries.
- **The list** gets a filter ***Sensitive:*** *any*, *only*, *none* (`?sensitive=true|false`). The lock stays in the rows.
- **The server:**
  - `KbPageRequest.Sensitive` (`bool?`; null: unchanged) and `KbPageRequest.Statements` (`KbStatements?`);
  - `MarkSensitive` stays, and means `Sensitive = true`;
  - unmarking without both statements: 400 on `statements`;
  - the rest as `SetSensitiveAsync` does it: the audit, and `scans.Cancel`;
  - `CheckCredentials` is skipped when the save marks the page, as it is for `MarkSensitive` now.
- **The documentation page** says where the mark is and what it does.

---

## 5. *Written mostly with AI* (R8)

- **`kb_pages.WrittenWithAi`** (default false), kept in every revision (`kb_revisions.WrittenWithAi`, `KbSnapshot`).
  - The history lists it as a change, `writtenWithAi`: *marked* or *unmarked*.
  - It doesn't count as an edit for *most edited* (`IsEdit` stays title, summary and content).
- **The editing page's details:** a checkbox ***Written mostly with AI***, with the help *Check it when most of the text came from an AI. Readers see it; it changes nothing else.* The details' line names it.
- **Reading:**
  - a tag ***Written with AI*** in the page's header, with `pi pi-microchip-ai`. P0 checks that PrimeIcons 7.0.0 has it; `pi pi-sparkles` is taken by unconfirmed AI tags;
  - an icon in list rows, in recent changes and on the map;
  - a filter in the list (`?ai=true|false`).
- **It is independent of AI tags and of the sensitive mark.** Tagging with AI doesn't set it, and a page written with AI can still be sensitive.
- **It is a statement by people.** Anyone with *Knowledge base: Edit* checks or unchecks it, and the history says who. Whoever rewrites a page by hand unchecks it.
- **The server:** `KbPageRequest.WrittenWithAi` (`bool?`; null: unchanged, and false for a new page).
- **MCP:**
  - **`kb_page_create` checks it unless the call says `writtenWithAi: false`**, since an AI client wrote the text;
  - `kb_page_update` leaves it as it is unless the call gives it;
  - `kb_page` and `kb_search` return it.

---

## 6. Recent changes (R7)

```
┌ Knowledge base  128                                               [Map] [New page] ┐
│ Recent changes · last 14 days                                                   ▾ │
│ KB-42  Printer setup, 2nd floor  Needs review   edited by Anna · 2 h ago   +12 −3 │
│        “Driver path moved to print02”                 and 3 more changes by 2 people │
│ KB-57  VPN for contractors  ✦                         created by Ben · yesterday     │
│ KB-12  Replacing a UPS  🔒                             status → Published · 3 days ago │
│                                                                      All changes › │
├────────────────────────────────────────────────────────────────────────────────────┤
│ [Search the pages' content…]  Newest ▾  Any status ▾  Linked to…  ☐ Show archived   │
```

- **On `/kb`, between the header and the filters, a panel *Recent changes · last 14 days*.**
  - **It shows on the plain list only:** no search, no filter, the first page of results. While searching it would push the results down.
  - It folds, and the browser remembers that (the preferences store).
  - It refreshes with the list, every 60 seconds.
- **What counts as a change:** a revision that created the page, changed its title, summary or content (`IsEdit`), changed its status, or restored an old revision.
  - **Not:** tags, links, the sensitive mark, *Written with AI*, archiving, AI tags, redacting. Those are housekeeping; the history has them.
- **One row per page, with its newest change:**
  - at most 10 rows (`KnowledgeBase:RecentChangesMax`), from the last 14 days (`KnowledgeBase:RecentChangesDays`), newest first;
  - only pages that aren't archived. Sensitive pages are included: people see them.
- **A row:**
  - the number, the title, the status if it isn't *Published*, the lock, the AI icon;
  - **what and who:** *edited by Anna · 2 h ago*, *created by Ben*, *status → Needs review*, *restored from 4*;
  - *+12 −3*, which opens that revision's diff (`/kb/42/history?to=N`);
  - the revision's note, muted;
  - ***and 3 more changes by 2 people***, when the page changed more often in the 14 days.
- **Below:** *All changes* opens `/kb?sort=updated`. With no change in 14 days, the panel says so in one line.
- **The server:** `GET /kb/changes` (*Knowledge base: View*) returns `KbRecentChange[]`.
  - The revisions since 14 days ago that are `IsEdit`, or whose changes hold `status` or `restored`. That is a jsonb containment test, through `EF.Functions.JsonContains`.
  - Then the newest per page, the 10 newest of those, and per page the number of changes and of authors in the period.
  - The index on `kb_revisions.At` serves it.

---

## 7. Links between pages (R1)

### 7.1 For the person

- **The link picker offers pages too:** *📖 KB-12 Printer drivers*, from the global search's pages.
  - It works where locations, hosts and sensors are linked: in the editing page's details, and in the page's *Links* panel.
  - **The list's *Linked to…* filter keeps the three kinds** (a `kinds` property on `LinkPicker`).
- **A page link is mutual** (decided in the review). Linking KB-42 to KB-12 shows each under the other's *Related pages*, and removing it on either page removes it from both. There is no *from* and no *to*.
- **Both histories record it:**
  - **the page where it was set**, in the revision of its save (*links +1*), as any link;
  - **the other page, in a links-only revision of its own:** *Linked with KB-42* or *Unlinked from KB-42*, by the person who did it.

  So each page's snapshots list the link exactly while it exists, and the history of either page says who linked them and when.
- **One panel, *Related pages*, takes the place of *Linked from*:**
  - ***Related:*** the page links, each page once, with × to remove one (*Knowledge base: Edit*);
  - ***In the text:*** the pages this page's text links to (`LinksTo`, unused until now), and the pages whose text links here (today's *Linked from*), each marked with its direction. A page that is already related isn't repeated.
  - A page that is gone shows as *deleted page* in old revisions.
- **Links in the text stay one-way:** they are part of a page's text, and only that page's own saves change them.
- **A page can't link to itself.** The 50 links per page count on both pages: a link fails with *KB-12 has 50 links already* when the other page is full.
- **An archived page is read-only, so its page links can't change.**
  - A link to it shows muted, *archived*, without ×, and stays until the page is restored.
  - A save of the other page keeps it, whatever the save sends.
- **Deleting a page removes its page links on both sides** (cascade), without a revision of the other pages, as when a host is deleted. Their old revisions show *deleted page*.

### 7.2 Two mirrored rows

- **Each page link is two rows of `kb_links`:** KB-42 → KB-12 and KB-12 → KB-42. They are written and deleted together, in one transaction, and only by `KbPageService`.
- **Why two rows and not one per pair:** each page's own `Links` then holds its page links. The snapshot (`KbSnapshot.Of`), the comparison, restoring a revision, the panels and the cascades keep working per page, as they do now. With one row per pair, each of them would have to look at both columns.
- **The pages involved are locked in the order of their ids** (`SELECT … FOR UPDATE`) before anything changes. Two people linking the same two pages from both sides at once can't deadlock; the second finds the link made.
- **A test checks the mirror after every operation:** linking and unlinking from either side, a save with a stale list of links, restoring a revision, archiving and restoring, deleting, redacting.

### 7.3 Links-only revisions and edit conflicts

**Without a rule, a mutual link breaks the other page's editing.** A link made on KB-42 gives KB-12 a new revision. Someone who has KB-12 open in the editor started from the revision before:

- their save would be refused with 409 `edit_conflict`, though nobody touched the text;
- and *Save mine anyway* would send KB-12's links as they were loaded, removing the new link again, since a save replaces every field (§2.1).

**The rule:**

1. **`kb_revisions.LinksOnly`** (default false) marks the revisions made on the other page. They change nothing but its page links.
2. **Every revision check skips them.** `KbRevisions.SameTextSinceAsync(page, revision)` says whether every revision after the given one is links-only. The five checks use it instead of comparing the numbers:

   | Check | Where |
   |---|---|
   | A save's edit conflict: the editor, the page view's quick saves, MCP's `kb_page_update` | `KbPageService.UpdateAsync` |
   | Unmarking with the statements | `KbPageService.SetSensitiveAsync` |
   | Starting an AI tag scan | `KbAiTags.cs:167` |
   | The scan's checks before sending, and before adding its tags | `KbAiTags.cs:306`, `:355` |

   The statements and the AI scan are about the text, which a links-only revision doesn't change. The scan keeps sending the revision it was started for.
3. **A save's page links are a change against the revision it started from:** those it removed are removed, those it added are added, and a link that the other page made meanwhile stays.
   - Links to locations, hosts and sensors keep today's rule (the request's set), since the revision check protects them.
   - When the save started from the current revision, the two rules give the same result.
4. **A draft restored from an older revision carries that revision** (docs/locations-knowledge-plan.md §5.7), so the same rule applies. The web UI doesn't change for it.

**A links-only revision is no edit:** it doesn't count for *most edited*, isn't a recent change, and never brings the AI question.

### 7.4 The changes

| Where | What |
|---|---|
| `kb_links` | **`TargetPageId`** (null, FK to `kb_pages`, cascade). The check becomes `num_nonnulls("LocationId", "HostId", "SensorId", "TargetPageId") = 1`, as for `redmine_links`, plus `"TargetPageId" <> "PageId"`. A unique index (`TargetPageId`, `PageId`) where it is set |
| `kb_revisions` | **`LinksOnly`** (§7.3) |
| `KbLinkKind` | **`Page`** |
| `KbRevisions` | `RefOf` handles each kind explicitly, with no fall-through to `SensorId`. `SameTextSinceAsync`. The links-only revision and its change (*Linked with KB-42*) |
| `KbPageService` | `ApplyAsync`: the mirror, page links as a change against the starting revision, links to archived pages kept. `MissingTargetsAsync`: pages as targets, not itself, the other page's 50. The locking in the order of ids. The two revision checks |
| `KbAiTags` | Its three revision checks |
| `KbLinkNames.ResolveAsync` | A page's title, `KB-12` as the detail, archived or deleted |
| `KbLinkItem` | **`Number`** (null except for pages), so the UI can route to `/kb/12` |
| `KbPageDetail` | **`RelatedPages { Related, LinksTo, LinkedFrom }`:** the page links, then the links in the text (`kb_page_refs`) without the pages already related |
| `KbPageList.RelatedAsync` | **Unchanged:** the panels of locations, hosts and sensors are about object links |
| `DevSeedKnowledge` | Page links between a few seeded pages |
| Web | `kbLinkIcon` and `kbLinkRoute` (`Record<KbLinkKind, …>`, so the compiler finds each one), `LinkPicker.vue`, `KbPageView.vue` (the panel), `KbEditView.vue`, the history (*Linked with KB-42*) |

### 7.5 Machines

- **`MachineAccess.PageLinks(db)`:** page links whose two pages are both readable. A sensitive page never appears through a link from a readable one.
- **MCP's `kb_page` gains `related`** (number and title of the related pages, and of those linked in the text either way), through `MachineAccess` only.

---

## 8. The knowledge map (R2)

### 8.1 What it shows

```
┌ Knowledge map ───────────────────────────────────────────────────────────────┐
│ Around KB-42 ▾  2 steps ▾  #tags…  Linked to…  ☐ Circles for sites  [−][+][Fit] │
│                                                                              │
│        ⌂ Zurich DC ─ ─ ─ ⌂ Room 2.13            ┌──────────────────────────┐ │
│                           │                     │ KB-42 Printer setup, 2nd  │ │
│     ▣ KB-12 ────────── ▣ KB-42 ───── ● pr-3og    │ Needs review · ♥ 12 · ✓ 7 │ │
│        ▲  ╎               ╎            │         │ #printer #zrh             │ │
│        │  ╎               ▼            · Toner   │ [Open] [Focus here]       │ │
│     ▣ KB-31          ▣ KB-57 ✦                   └──────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Nodes:**

| Node | Drawn as | Colour |
|---|---|---|
| **A page** | A rounded square with a book glyph, `KB-42` and the title (cut to 32 characters) below; the lock and the AI mark | By status: *Published* plain, *Draft* muted, *Needs review* in the warning colour |
| **A location** | A circle with a marker glyph, its name | Plain |
| **A host** | A circle with a server glyph, its name | **Its state, with *Monitoring: View*;** plain without |
| **A sensor** | A small circle, its name | As a host |

**Edges:**

| Edge | From → to | Drawn |
|---|---|---|
| **Page link** | page — page, mutual | Solid, without an arrow |
| **Link in the text** | page → page | Dashed, with an arrow: it is one-way. With a page link between the same two pages, they are one solid edge, *also in the text* |
| **Object link** | page → location, host or sensor | Solid |
| **Structure** | sensor → host, host → location, location → parent | Thin and grey, without an arrow |

Archived pages are left out; *Show archived* brings them in, muted.

### 8.2 Which part of it

- **Around one thing:** `/kb/map?focus=page:42`, and `location:`, `host:` or `sensor:` with an id.
  - **Everything within 2 steps** (1 to 4, in a select) over the knowledge edges, in both directions.
  - **Structure is followed upwards always,** so a linked sensor brings its host, the host's location and its parents. Nothing floats without its place.
  - **Downwards only to reach pages:** from a location, its sub-locations, hosts and sensors are drawn only on the way to a page linked to them. A location's 200 PCs without pages never appear, and the map shows nothing of monitoring that the pages' links don't already name.
- **The whole knowledge base:** `/kb/map` without a focus.
  - The pages with at least one link of either kind, what they link to, and the structure above those.
  - **Filters:** tags, *Linked to* a location (with its sub-locations), status, *Written with AI*, *Show unlinked pages* (off).
- **At most `KnowledgeBase:MapMaxNodes` (600) nodes.**
  - Nearest to the focus first; without a focus, the most linked pages first.
  - Then the map says *Showing 600 of 1,240; narrow it with the filters.*
- **The focus and the filters are in the URL,** as on the network map, so a view can be shared.
- ***Circles for sites*** (off by default) draws each top-level location as a dashed circle around its locations, hosts and sensors, as the network map does. Pages stay outside the circles.

### 8.3 Where it is reached

- ***Map*** in the knowledge base's header.
- ***Show on the knowledge map*** in a page's ⋯ menu (`?focus=page:42`), and in the *Knowledge base* panels of locations, hosts and sensors (`KbPanel.vue`).
- No menu entry of its own: it is part of the knowledge base.

### 8.4 Using it

- **Pan, zoom and fit** as on the network map (`usePanZoom`). Hovering over a node highlights its neighbours.
- **A click opens a side panel**, a non-modal `Drawer`, as the network map's:
  - **a page:** number, title, status, summary, tags, likes and uses, *Open*, *Focus here*;
  - **a location, host or sensor:** its name, its path or host, its state (with *Monitoring: View*), the pages linked to it, *Open*, *Focus here*;
  - **an edge:** its kind and its two ends.
- **No dragging, no saved positions** (not in scope). The same data gives the same picture.
- **Empty:** *No links yet. Link pages to each other, or to locations, hosts and sensors, in the editing page's details.*

### 8.5 What is shared with the network map

| Part | Today | Then |
|---|---|---|
| **The layout** | `views/network/networkLayout.ts` | **Moved to `components/graph/forceLayout.ts`.** It is the same code, except that a node brings its radius (`r`) instead of `kind`. `networkLayout.ts` keeps `nodeRadius(kind)` and its exports, and calls it. **Its vitest runs unchanged:** the same input gives the same picture |
| **Pan and zoom** | `composables/usePanZoom.ts` | As it is |
| **Edge paths with bends, bounds** | Inside `MapCanvas.vue` | Moved to `components/graph/geometry.ts`, pure, with a vitest. `MapCanvas.vue` imports them |
| **The focus in the URL** | `parseFocus` in `networkFormat.ts` | Moved to `components/graph/focus.ts`, with the kinds as a parameter |
| **The canvas** | `MapCanvas.vue`: moving and saving routers, the network's styles | **Not shared.** `views/kb/map/KnowledgeMapCanvas.vue` draws the knowledge map on the shared parts. Making `MapCanvas` generic now would put the network map's moving and saving at risk for no gain; a shared canvas can follow once both are settled |
| **Libraries** | `d3-force`, `d3-hierarchy` | The same. Nothing new |

- **The move is the first commit of P6, alone,** checked by the network map's vitests and `e2e/network-map.spec.ts` with no change to either.
- **The same budget as the network map:** if 600 nodes take more than 300 ms to lay out, the layout moves to a Web Worker.

### 8.6 The server

- **`PNMTD/Knowledge/KbMap.cs`** reads, in a few queries:
  - the pages that aren't archived (id, number, title, status, sensitive, *Written with AI*, likes, uses);
  - `kb_links`, a page link's two rows as one edge, and `kb_page_refs` by number;
  - the hosts' `LocationId` and the sensors' `ParentId` for what is linked;
  - the location tree from `LocationTree`.

  It builds the graph in memory, walks it and cuts it. 5,000 pages and some 20,000 links are a few megabytes.
- **`GET /api/v1/kb/map?focus&depth&tags&location&status&ai&archived&unlinked`** (*Knowledge base: View*) returns `KbGraph { nodes, edges, groups, total, truncated }`. Node ids are `page:<id>`, `location:<id>`, `host:<id>` and `sensor:<id>`.
- **States** come from the dashboard's computation, as for locations, and only with *Monitoring: View*; otherwise null.
- **Names** of hosts and sensors are shown as the page's links show them today (`KbLinkNames`), which needs no right beyond the knowledge base. The structure above a linked object is what the links already name as their detail.

---

## 9. Likes and *Used to solve a problem* (R3, R4)

### 9.1 For the person

```
┌ Printer setup, 2nd floor   Needs review  🔒 Sensitive  ✦ Written with AI ─────────────┐
│ Knowledge base › KB-42                  [♥ 12]  [✓ Used to solve a problem · 7]  [Edit] [⋯] │
├──────────────────────────────────────────────────────┬──────────────────────────────────┤
│ …                                                    │ About this page                  │
│                                                      │ Liked by        12  (you, Anna…) │
│                                                      │ Used to solve   7 times, 4 people│
│                                                      │                 last 3 days ago  │
│                                                      │ Recent uses                      │
│                                                      │  Ben · 3 days · printer on 2nd   │
│                                                      │  Anna · 9 days                   │
```

- **Like** (`pi pi-heart`, filled once liked): a toggle with the count.
  - Its tooltip names the first people: *Anna, Ben and 10 others*. A click on the count lists everyone, with when.
- ***Used to solve a problem*** (`pi pi-check-circle`): **a click records a use at once.**
  - The toast says *Recorded*, with ***Add a note…*** (at most 200 characters, such as *printer on the 2nd floor*) and ***Undo*** for 10 minutes.
  - **The same person can record another use after 12 hours** (`KnowledgeBase:UseRepeatHours`). Until then the button says *You used it 2 h ago*.
- ***About this page*** shows *Liked by 12*, and *Used to solve a problem 7 times by 4 people, last 3 days ago*, with the last 5 uses: who, when, the note.
- **On an archived page,** the counts stay and the buttons are disabled.
- **The list:** each row's meta shows *♥ 12 · ✓ 7*. Two new orders, ***most liked*** and ***most used***, over the last 30 days or all time, as *most viewed*.
- **Who sees the names:** everyone with *Knowledge base: View*, as revisions name their authors (§19, decision 6).
- **Viewers like pages and record uses,** with *Knowledge base: View*. They are often the people who read a page to solve a problem.
- **Liking one's own page is allowed,** and tells nobody.

### 9.2 Data

| Table or column | |
|---|---|
| `kb_page_likes` | `PageId`, `UserId`, `UserName`, `At`. The key is (`PageId`, `UserId`). It goes with the page; an index on `UserId` |
| `kb_page_uses` | `Id`, `PageId`, `UserId`, `UserName`, `At`, `Note` (at most 200). It goes with the page; an index on (`PageId`, `At`) |
| `kb_pages.Likes`, `kb_pages.Uses`, `kb_pages.LastUsedAt` | Counters, `[AuditIgnore]`, with database defaults, indexed for the orders |

- **The counters move with the rows, in the same transaction,** as `KbViews` does for views:
  - `INSERT … ON CONFLICT DO NOTHING`, and `+1` only when a row was inserted;
  - `−1` only when a row was deleted.

  So two people liking at once count two. There is no revision and no change to `UpdatedAt`.
- **People are a user id and their name at the time, without a foreign key,** as authors are (docs/locations-knowledge-plan.md §8.2). A former colleague's like still counts.

### 9.3 The server

- **`PNMTD/Knowledge/KbReactions.cs`:** `LikeAsync(page, liked)`, `UseAsync(page, note)`, `NoteAsync(page, use, note)`, `UndoUseAsync(page, use)`, `LikesAsync(page)` and `UsesAsync(page, paging)`.
- **Each like and each use enqueues its notifications** (§10.4) in the same transaction.
  - An unlike cancels the notifications of its like that haven't gone out; undoing a use cancels the use's.
- **Refused:** an archived page, 409 `archived`; a use within 12 hours of the same person's last, 409 `used_recently` with when; undoing someone else's use, or one older than 10 minutes, 403.
- **The endpoints are in §12.** Likes and uses are `[NotAudited]`, as views are: they are reactions, not operations.

### 9.4 The e-mails

- **The subject:**
  - *PNMT: Anna liked KB-42 “Printer setup, 2nd floor”*;
  - *PNMT: 3 people liked KB-42 “Printer setup, 2nd floor”*;
  - *PNMT: Ben used KB-42 “Printer setup, 2nd floor” to solve a problem*.
- **The body:**
  - who and when, and the uses' notes;
  - the link, `{PublicBaseUrl}/pnmt-admin/kb/42`;
  - ***You get this because you wrote or edited KB-42. Turn it off:*** `{PublicBaseUrl}/pnmt-admin/account#notifications`.

  Without `PublicBaseUrl`, there are no links, as in the lounge's e-mails.
- **A sensitive page is named by its number only, without its title or the notes:** an e-mail leaves PNMT.

---

## 10. Notifications to people (R5, R6)

**One mechanism for every feature that tells a person something about their own work.** Likes and uses are its first two kinds.

### 10.1 The catalog

`PNMTD/Notifications/UserNotificationKinds.cs`, in code:

| Key | Label on *Account → Notifications* | The recipient needs | E-mail by default |
|---|---|---|---|
| `kb.liked` | *Someone liked a page I wrote* | *Knowledge base: View* | On (§19, decision 2) |
| `kb.used` | *Someone used a page I wrote to solve a problem* | *Knowledge base: View* | On |

- **Each kind has:** its key, label and help; the area and level the recipient must hold when it is sent; its channels (e-mail for now) with a default each; its batch window; and **a renderer** that writes the e-mail from a batch.
- **Adding a kind is code only:** no migration, and no new column on `users`. Later candidates are in §10.7.

### 10.2 Preferences

```
┌ Notifications ──────────────────────────────────────────────────────────────┐
│ E-mail me when                                                    E-mail    │
│ Someone liked a page I wrote                                       [■]      │
│ Someone used a page I wrote to solve a problem                     [■]      │
│ At most one e-mail per page every 15 minutes, and 20 a day.                 │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **`user_notification_preferences`:** `UserId` (FK to `users`, cascade), `Kind`, `Channel`, `Enabled`, `ChangedAt`. The key is (`UserId`, `Kind`, `Channel`).
  - **Only a choice that differs from the default is stored.** A missing row is the kind's default, so a new kind needs no migration, and a new person gets the defaults.
- ***Account → Notifications*** (`views/account/NotificationSwitches.vue`) sits beside *Lounge*, with one switch per kind and channel.
  - **Without an e-mail address on the account,** the switches are disabled, with *Add an e-mail address under Contact*.
  - Kinds whose right the person lacks are hidden.
- **The API:** `GET /account/notifications` and `PUT /account/notifications`, one's own only. A save is audited as `NotificationPreferencesChanged`.
- **The lounge's two switches stay where they are** (§19, decision 10).

### 10.3 Where each step runs

| Step | Where | When |
|---|---|---|
| **Enqueue** | The feature's service (`KbReactions`), in its own transaction | At the like or use |
| **Cancel** | The same | At an unlike, or a use undone |
| **Send** | **`NotificationTask`'s second timer** | Every 30 seconds, for the batches that are due |
| **Record** | `NotificationService` and the notification log, as for alerts | At each send |
| **Clean up** | The same timer | Once a day |

### 10.4 The queue

- **`user_notifications`:**

  | Column | |
  |---|---|
  | `Id` | bigint, identity |
  | `UserId` | The recipient (FK to `users`, cascade) |
  | `Kind`, `BatchKey` | `kb.liked`, and `kb.liked:<page id>`: what is sent as one e-mail |
  | `SubjectId` | The page |
  | `SourceId` | The like or use, so it can be cancelled |
  | `ActorUserId`, `ActorName` | Who acted |
  | `Detail` | A use's note, at most 300 characters |
  | `CreatedAt`, `DueAt`, `NextAttemptAt` | |
  | `State` | *Pending*, *Sending*, *Sent*, *Simulated*, *Skipped*, *Failed*, *Cancelled* |
  | `Attempts`, `SentAt`, `Outcome` | `Outcome`: why it was skipped or failed, or the provider's answer (at most 500) |

  Indexes on (`State`, `DueAt`), (`UserId`, `SentAt`) and `SourceId`.
- **Enqueuing** (`UserNotifications.EnqueueAsync(db, …)`) runs in the caller's transaction, so a like and its notifications are saved together or not at all.
  - **One row per recipient.** The actor is never one.
  - A recipient whose preference is off gets no row. It is checked again when sending.
  - **`DueAt` joins the open batch:** if a *Pending* row with the same person and `BatchKey` exists, its `DueAt`; otherwise now plus `BatchMinutes` (15). Ten likes within a quarter of an hour are one e-mail.
- **Cancelling** (`CancelAsync(db, sourceId)`) turns the *Pending* rows of an unliked like, or of an undone use, into *Cancelled*. A like undone within 15 minutes sends nothing.

### 10.5 Sending: the existing `NotificationTask`, on a second timer

- **`NotificationTask` gets a second `Timer`,** every `UserNotifications:IntervalSeconds` (30), with **its own `RunGuard`**.
  - **The alert timer, `RunOnce()` and its 10 seconds stay exactly as they are.**
  - **The two never wait for each other,** so a slow mail server for a person's e-mail never delays an alert.
  - **Not inside `RunOnce`, after the alerts:** a slow mail server for a like would delay the next alert check by up to 30 seconds per message.
  - **Not through Cookie's `Outbox`:** it is Cookie's, with Cookie's limits, states and view (§2.3).
- **`RunUserNotificationsOnceAsync()`** (internal; the tests call it, as they call `RunOnce`):
  1. **It writes `LAST_USER_NOTIFICATION_RUN`.** *System → Task health* gets a row, *User notifications*, every 30 seconds.
  2. **Rows still *Sending* after 10 minutes** (PNMTD stopped during a send) go back to *Pending*. At worst one e-mail goes out twice.
  3. **It takes the due batches:** *Pending* rows with `DueAt` and `NextAttemptAt` passed, grouped by person and `BatchKey`, oldest first, at most `PerRun` (10). It marks them *Sending*.
  4. **For each batch:**
     - **The person:** active, with an e-mail address, the kind's preference on now, the kind's right from their groups (as `LoungeService` builds rights; admins have every right), and the page still there. **Otherwise *Skipped*, with why.**
     - **The daily limit:** at most `MaxPerUserDay` (20) e-mails today to the person. **Otherwise *Skipped*** (*daily limit*).
     - **The renderer** writes the subject and the body from the batch and the page as it is now. Null, when there is nothing left to say, means *Skipped*.
     - **`NotificationService.SendAsync`** with the person's address, the purpose *Knowledge base: likes on KB-42 → Anna Muster*, and `Development:DoNotSendNotifications`. **The notification log has it,** as every alert.
     - ***Sent***, or ***Simulated*** in development, with `SentAt`. **A failure:** one more attempt, the next after 1, 5 and 30 minutes, then 2 hours; after 5 attempts ***Failed***, with the provider's detail.
     - **After the first failure, the run stops.** The mail server is likely down, and the rest waits for the next run.
  5. **Once a day,** rows finished more than `KeepDays` (30) ago are deleted.
- **It depends on nothing else:** not on AI, Cookie or the uplink.
- **With `UserNotifications:Enabled=false`,** nothing is enqueued or sent, and the switches stay.

**Configuration**, the `UserNotifications` section (`UserNotifications__*` in `.env`, listed in `deploy/.env.example` and deploy/README.md):

| Setting | Default | |
|---|---|---|
| `Enabled` | true | Enqueuing and sending |
| `IntervalSeconds` | 30 | The second timer |
| `BatchMinutes` | 15 | How long a batch collects |
| `MaxPerUserDay` | 20 | E-mails per person and day |
| `PerRun` | 10 | Batches per run |
| `KeepDays` | 30 | Before finished rows are deleted |

### 10.6 What admins see

- ***Admin → Notification log:*** every e-mail, with its purpose and outcome, as now.
- ***System → Task health:*** the new row.
- No view of the queue itself in this plan. What failed is in the log.

### 10.7 Later kinds (not built)

- A page I wrote was set to *Needs review*.
- Someone else edited a page I wrote.
- A page I watch changed (watching is a non-goal of the first plan).
- The lounge's e-mails, moving `users.LoungeEmails` into the catalog.
- Pushover as a second channel.

---

## 11. Data model and migrations

**One migration per phase:**

| Migration | Phase | Changes |
|---|---|---|
| `KbSensitiveAndAi` | P1 | `kb_pages.WrittenWithAi` and `kb_revisions.WrittenWithAi` (bool, default false) |
| `KbPageLinks` | P3 | `kb_links.TargetPageId` (uuid, null, FK to `kb_pages`, cascade); the check of four; the check against linking to itself; the unique index (`TargetPageId`, `PageId`) where it is set. `kb_revisions.LinksOnly` (bool, default false) |
| `UserNotifications` | P4 | `user_notification_preferences`, `user_notifications` |
| `KbReactions` | P5 | `kb_page_likes`, `kb_page_uses`; `kb_pages.Likes` and `Uses` (int, default 0), `LastUsedAt` (null), with indexes for the orders |

- **Every new column has a database default,** so existing rows need no backfill and `tools/upgrade-test.sh` passes.
- **The audit attributes:**
  - `kb_page_likes`, `kb_page_uses` and `user_notifications`: `[NotAudited("…")]` (reactions; and what went out is in the notification log);
  - `user_notification_preferences`: `[NotAudited("each save is in the audit log as NotificationPreferencesChanged")]`, as `notification_settings` is;
  - `kb_pages.WrittenWithAi` is audited with the page; the counters are `[AuditIgnore]`.
- **Recent changes and the map need no tables.** The index on `kb_revisions.At` serves recent changes.

---

## 12. API

Everything under `/api/v1`, in the existing style. The OpenAPI file and `schema.d.ts` are regenerated. **The sensor API and its golden contract don't change.**

| Endpoint | Right | Audit | |
|---|---|---|---|
| `POST /kb/pages` · `PUT /kb/pages/{id}` | KB: Edit | As today | **+ `sensitive`, `statements`, `writtenWithAi`**, null for unchanged. Links can be of kind `Page`: mutual, applied as a change against the revision the save started from; links-only revisions since then are no conflict (§7.3) |
| `GET /kb/pages` | KB: View | – | **+ `sensitive`, `ai`**, the sorts **`likes`** and **`uses`**. Rows + `likes`, `uses`, `writtenWithAi` |
| `GET /kb/pages/{number}` | KB: View | – | **+ `writtenWithAi`**, **`reactions`** { likes, likedByMe, uses, people, lastUsedAt, myLastUseAt, recentUses }, **`relatedPages`** { related, linksTo, linkedFrom }. `KbLinkItem` + `number` |
| `GET /kb/changes` | KB: View | – | §6 |
| `PUT /kb/pages/{id}/like` | KB: View | `[NotAudited]` | `{ liked }` → `reactions`; 409 `archived` |
| `GET /kb/pages/{id}/likes` | KB: View | – | Who, and when |
| `POST /kb/pages/{id}/uses` | KB: View | `[NotAudited]` | `{ note? }` → 201 with `reactions`; 409 `used_recently`, 409 `archived` |
| `PUT /kb/pages/{id}/uses/{useId}` | KB: View, own use | `[NotAudited]` | The note, within 24 hours |
| `DELETE /kb/pages/{id}/uses/{useId}` | KB: View, own use | `[NotAudited]` | *Undo*, within 10 minutes |
| `GET /kb/pages/{id}/uses?page` | KB: View | – | |
| `GET /kb/map?focus&depth&tags&location&status&ai&archived&unlinked` | KB: View | – | §8.6 |
| `GET /account/notifications` · `PUT /account/notifications` | Signed in, one's own | `NotificationPreferencesChanged` | §10.2 |

**`EndpointAuditTests` lists each one with its right.** Likes and uses join `POST …/views` as the writes made with *Knowledge base: View*.

---

## 13. MCP and Cookie

- **`kb_page` and `kb_search`** return `writtenWithAi`, `likes`, `uses` and `lastUsedAt`, so an AI client can choose the page that helped people.
- **`kb_page`** returns `related` through `MachineAccess.PageLinks` (§7.3).
- **`kb_page_create`** takes `writtenWithAi` (true when left out); **`kb_page_update`** takes it, optionally.
- **No tools** for likes, uses, the map or the preferences: these are people's.
- **Cookie** reads through the same tools; nothing else of Cookie changes.

---

## 14. Rights and audit

| Capability | Right |
|---|---|
| Read pages, recent changes, the map, who liked, the uses | *Knowledge base: View* |
| Like; record a use; add a note to one's own use; undo it | *Knowledge base: View* |
| Page links, the sensitive mark (unmarking with the statements), *Written with AI* | *Knowledge base: Edit* |
| The states of hosts and sensors on the map | Also *Monitoring: View* |
| One's own notification preferences | Signed in |

- **The rights catalog's texts** (`RightsCatalog`, shown on the group page and in *Your access*):
  - *View:* "Pages, their history and files; likes and *Used to solve a problem*; the knowledge map."
  - *Edit:* "… links between pages, the sensitive mark, *Written with AI* …".
- **Audit:**
  - a save that marks or unmarks a page also writes `KbPageSensitivityChanged`, as `SetSensitiveAsync` does;
  - `WrittenWithAi` shows in `KbPageSaved`'s changes, through the audited entity;
  - `NotificationPreferencesChanged` is new;
  - likes and uses aren't audited.

---

## 15. Compatibility: what must not break

| What | How it stays | Checked by |
|---|---|---|
| **Older callers of `PUT /kb/pages/{id}`:** the page view's quick saves, `kbRequest()`, MCP's `kb_page_update` | The new fields are nullable; null leaves them | A test: a `PUT` without them keeps `WrittenWithAi` and `Sensitive` true |
| **Someone editing a page that another page links to meanwhile** | The links-only revision is no conflict, and the save keeps the new link (§7.3) | A test: KB-12 opened at revision 3, KB-42 links to it, KB-12's save from 3 passes and the link stays on both |
| **The five revision checks** (save, unmarking, the AI scan's three) | Each skips links-only revisions through one helper, and nothing else changes in them | Tests of the helper, and of the AI scan running on across a link |
| **The mirror of page links** | Both rows in one transaction, by one service; the pages locked in the order of ids | The mirror test after every operation (§7.2); two people linking the same pages at once |
| **`MarkSensitive`** and the credentials warning | `MarkSensitive` still means *mark*; marking with the switch also skips the warning | `KbPageTests` as they are |
| **The AI question and its dialog** | Unchanged | `e2e/knowledge.spec.ts` as it is |
| **Revisions and the history** | Old revisions read `WrittenWithAi` as false; a change is listed only on a difference; restoring keeps the links | `KbPageTests` |
| **The link kinds** | Each switch handles `Page`; `RefOf` has no fall-through; old revisions without page links read as before | vue-tsc; tests of `RefOf`, `ResolveAsync`, `MissingTargetsAsync` |
| **The panels of locations, hosts and sensors** (`GET /kb/related`) | Unchanged | Their tests |
| **`MachineAccess`** | Page links are filtered by both ends | `MachineAccessTests`: a link to a sensitive page is invisible |
| **The alerts** | The alert timer and `RunOnce` are untouched; the user notifications run on their own timer and guard | `NotificationRecipientsTests` as they are; a test that `RunOnce` doesn't touch `user_notifications`; a test that a hanging provider in the second loop doesn't hold up `RunOnce` |
| **The network map** | The layout moves without a change to its results | `networkLayout.test.ts` unchanged and green; `e2e/network-map.spec.ts` |
| **The lounge** | Its switches and its e-mails are untouched | `e2e/lounge.spec.ts`, its tests |
| **Views, lists, orders** | New orders and filters are added; the defaults stay | `KbSearchTests` and the list's tests |
| **Upgrades** | Database defaults on every new column | `tools/upgrade-test.sh` |
| **Rights** | Each new endpoint in the matrix; each entity with its audit attribute | `EndpointAuditTests`, `AuditModelTests` |
| **The seed** | Extended, on an empty database only | `DevSeedTests` |

---

## 16. Testing

As before: **few tests, where they protect something.**

| What | How |
|---|---|
| **Saves** (`KbPageTests`) | §15's compatibility. Marking with `sensitive: true`; unmarking without both statements gives 400, with them the declaration is on the new revision. `writtenWithAi` in the revision and its change |
| **Page links** | Linking from either page shows it on both, and removing it from either removes both rows; both pages get their revision, the other a links-only one; the mirror after every operation (§7.2); a link to itself refused; 50 links on the other page refused; a link to an archived page kept by the other page's save; deleting a page removes both rows; two people linking the same two pages at once make one link and no deadlock; *Related pages* lists page links, then links in the text, each page once; `GET /kb/related` unchanged |
| **Links-only revisions** | A save, unmarking and an AI scan started from a revision before a links-only one pass; one before a real edit still gets 409; the save applies its page links against its starting revision and keeps the other page's; a links-only revision isn't an edit, a recent change or a reason for the AI question |
| **Recent changes** (`KbChangesTests`) | What counts (edits, created, status, restored) and what doesn't (tags, links, sensitive); one row per page with *and N more*; the 14-day boundary with the fake clock; at most 10; archived pages left out |
| **Reactions** (`KbReactionsTests`) | A like toggles; two people liking at once count two; a second like by the same person changes nothing; a use again before 12 hours gives 409; undo after 10 minutes or of another's use gives 403; archived gives 409; each enqueues, and unliking cancels |
| **User notifications** (`UserNotificationsTests`) | Likes within the window are one batch; a preference turned off after enqueuing is skipped; a recipient who lost the right is skipped; a sensitive page is named by number only; the daily limit; the retry times, then *Failed*; the run stops after the first failure; *Sending* rows go back after 10 minutes; the cleanup; simulated in development; the actor never gets one |
| **`NotificationTask`** | `RunOnce` doesn't touch `user_notifications`; with a provider that hangs in the second loop, `RunOnce` completes |
| **The map** (`KbMapTests`) | The steps around a focus; structure upwards always, downwards only to reach pages; the cap and *truncated*; states null without *Monitoring: View* |
| **`MachineAccess`** | A page link to a sensitive page is invisible; MCP's `related` never names one |
| **Web** (Vitest) | `forceLayout` (the network's layout tests, moved as they are, and one with radii); `geometry`; `focus`; the list's new filters and orders in `listState`; the recent changes' lines |
| **End to end** (Playwright, `e2e/knowledge.spec.ts`) | Mark a new page sensitive in its details, then unmark it with the statements; check *Written mostly with AI* and see the tag; link a page to a page, see it under *Related pages* on both, and remove it from the other one; like and record a use, with a note, and see the counts; see the page in *Recent changes*; open the map focused on it. A Viewer likes and records a use, and sees no edit actions |
| **Upgrade** | `tools/upgrade-test.sh`: existing pages read `WrittenWithAi` false and their counters 0 |

---

## 17. Risks

| Risk | Answer |
|---|---|
| **A popular page floods its authors** | Batches of 15 minutes, one e-mail per page; 20 a day per person; one switch per kind; each e-mail says how to turn it off |
| **A hanging mail server delays alerts** | The second timer has its own guard and never blocks the alert timer; a run stops after its first failure |
| **A sensitive page's title leaves by e-mail** | Named by its number only, without the title or notes (§9.4) |
| **Notifications reach someone who lost access** | The kind's right is checked at sending, from the person's groups |
| **The counters drift** | They change with the rows, in the same transaction, by what the insert or delete did |
| **The map is unreadable on a large knowledge base** | 2 steps around a focus by default; 600 nodes at most, with a note; filters; circles for sites |
| **The map shows monitoring that a reader shouldn't see** | States only with *Monitoring: View*; structure downwards only to reach pages; names only as the links already show them |
| **Moving the layout breaks the network map** | The move is a commit of its own, checked by the network map's unchanged tests and e2e |
| **A switch over `KbLinkKind` misses `Page`** | The compiler for every TypeScript `Record`; §7.4 lists the server's places; tests of each |
| **A mutual link disturbs whoever edits the other page** | Links-only revisions are skipped by every revision check, and page links are saved as a change, so neither a conflict nor a lost link follows (§7.3) |
| **The two rows of a link drift apart** | Written only by `KbPageService`, in one transaction; the mirror test after every operation |
| **Two people link the same pages from both sides at once** | The pages are locked in the order of their ids; the unique index refuses a second pair |
| **Page links and links in the text confuse people** | One *Related pages* panel, with links in the text marked as such; on the map, solid and dashed |
| **Likes turn into a popularity contest** | They are a thank-you to the authors; no ranking of people, nothing on the dashboard |
| ***Written with AI* stays checked after a rewrite by hand** | It is a statement by people, and the history says who set it |

---

## 18. Phases

| Phase | Content | Size |
|---|---|---|
| **P0** | **Check,** with no product code: the icons in PrimeIcons 7.0.0 (`pi-microchip-ai`, `pi-heart-fill`, `pi-check-circle`). The questions are decided (§19) | small |
| **P1** | **The sensitive mark, visible, and *Written with AI*:** the request fields, the migration, the editing page's details, the header's tags, *About this page*, the list's filters and icons, the history's labels, MCP's `writtenWithAi`, the documentation page | small–medium |
| **P2** | **Recent changes:** `GET /kb/changes`, the panel, its settings | small |
| **P3** | **Page links, mutual:** the migration, `KbLinkKind.Page` and every place of §7.4, the mirrored rows and their locking, links-only revisions and the five revision checks, the picker, *Related pages*, `MachineAccess.PageLinks`, MCP's `related`, the seed | medium–large |
| **P4** | **Notifications to people:** the catalog, the preferences and *Account → Notifications*, the queue, `NotificationTask`'s second timer, task health, the cleanup, the settings | medium |
| **P5** | **Likes and uses:** the migration, `KbReactions`, the endpoints, the buttons, *About this page*, the orders, the two kinds and their e-mails, the seed | medium |
| **P6** | **The knowledge map:** (a) the shared graph parts moved, alone; (b) `KbMap` and its endpoint; (c) the view, its panel and the entry points; e2e | medium–large |
| **P7** | **Release:** the documentation page, docs/release-notes-1.20.md, the README, deploy/README.md and `.env.example`, this plan's *As built* | small |

- **Each phase** is one or more commits on master, has `./run-tests.sh` green before the next starts, and goes into an *As built* section.
- **P1, P2 and P3 don't depend on each other.** P5 needs P4. P6 needs P3, and shows likes once P5 is in.
- **P1 is first:** it is small, and it answers "I cannot see that anywhere".

---

## 19. Decisions (review of revision 1, 2026-11-21)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Who is told about a like or a use? | **The creator and everyone who made an edit** (`IsEdit`), without the person who acted | §9.4, §10.5 |
| 2 | Is the e-mail on by default? | **On,** with the batches and the daily limit; each e-mail says how to turn it off | §10.1 |
| 3 | Is *Used to solve a problem* once per person, or again? | **An event,** again by the same person after 12 hours, with an optional note | §9.1 |
| 4 | Recent changes: a row per page, or per revision? | **Per page,** its newest change, with *and 3 more* | §6 |
| 5 | Do Viewers like pages and record uses? | **Yes,** with *Knowledge base: View* | §9.1, §14 |
| 6 | Are the names of who liked shown? | **Names,** to everyone with *Knowledge base: View* | §9.1 |
| 7 | Are pages created over MCP *Written with AI* by default? | **Yes,** unless the call says otherwise | §5, §13 |
| 8 | Where are user notifications sent from? | **A second timer in `NotificationTask`,** with its own guard, through `NotificationService` | §10.5 |
| 9 | Page links: one-way or mutual? | **Mutual** (changed from the recommendation): shown on both pages, removed from either. Two mirrored rows; a links-only revision on the other page, which no revision check counts | §7 |
| 10 | The lounge's two switches | **They stay** where they are; they can become kinds later | §10.2, §10.7 |

No questions are open.

---

## 20. As built

**P0–P7 are on master** (2026-11-22), one or more commits per phase, P6 in two (the shared graph parts alone first). Where the code differs
from the sections above, this section says so; the rest is as planned.

### P0: the check

- **PrimeIcons 7.0.0 has `pi-microchip-ai`, `pi-heart-fill` and `pi-check-circle`.** *Written with AI* uses `pi-microchip-ai`.

### P1: the sensitive mark and *Written with AI*

- As planned. *About this page* says *Sensitive: Yes* or *No*, with *Mark…* or *Unmark…*; the header's tag carries the explanation.
- The list's two new filters read *Sensitive or not* and *AI or not*.
- The editing page sends the switch and the checkbox only when they changed, so a save never undoes someone else's mark.

### P2: recent changes

- **`GET /kb/changes` returns `{ days, items }`**, so the panel's title follows `KnowledgeBase:RecentChangesDays`.
- The panel and the list are named lists (*Recent changes*, *Pages*); the browser tests find a page's link in the list, since the panel may
  name it too.
- Its tests run on a fake clock a day after the newest revision in the shared database: other test classes' clocks run ahead.

### P3: mutual page links

- **No `KbPageDetail.RelatedPages`:** the UI takes the related pages from the page's links of kind `Page`, and the links in the text from
  `LinksTo` and `LinkedFrom`, which existed. `KbLinkItem` gained `Number` and `Archived`.
- **The picker for pages is in the *Related pages* panel;** the *Links* panel keeps locations, hosts and sensors. The editing page's
  details offer all four.
- **`KbRevisions.SameTextSinceAsync(db, page, current, revision)`** is static, for the AI handler's own context; `KbRevisionItem` gained
  `LinksOnly`.
- **A links-only revision doesn't move the page's `UpdatedAt`:** nobody edited it.
- **The seed links ten pairs of pages**, on the same timeline as its edits, so its stale saves pass over the links-only revisions as §7.3
  intends.
- **Found and fixed:** a link added by a save was counted twice in its revision (*links +2 −0*) and stored twice in its snapshot: EF's
  fix-up had already put the new row into the page's links before the explicit `Add`. Old revisions are read with each link once.
- A save that still names a page deleted meanwhile is refused with 400, as for any deleted target.

### P4: notifications to people

- As planned. **`user_notifications.MessageId`** names the e-mail a row went out in; the daily limit counts those.
- `UserNotificationSettings` also says whether PNMT sends them at all (`Enabled`). Saving one's contact details refreshes the panel.
- A renderer registered later for a kind wins (the tests replace the knowledge base's with a fake).
- The tests start each case from a clean queue, since other classes' likes in the shared database fall due on the same fake clock.

### P5: likes and uses

- **The service is `KbReactionService`;** `KbReactions` is the contract. Reading a page's reactions is a static query, so
  `KbPageService` needs no notifications (the seed's own service container has none).
- **`POST …/uses` answers 201; `PUT …/uses/{id}` sets one's own note within a day** (a pending e-mail takes it too); **`DELETE`** undoes
  one's own use within 10 minutes. All `[NotAudited]`, as views are.
- **List rows carry `Likes`, `RecentLikes`, `Uses` and `RecentUses`;** MCP calls the uses `usedToSolveAProblem`.
- **Redacting reaches the uses' notes** and the details of e-mails not yet sent.
- **The seed's likes and uses are written directly**, so seeding enqueues no e-mails.

### P6: the knowledge map

- **P6a, alone:** `components/graph/forceLayout.ts` (`layoutGraph`, a radius per node), `geometry.ts` (`edgePath`, `bendOf`, `boundsOf`)
  and `focus.ts` (`parseFocus`, with numbered kinds for `page:42`). The network map uses them; `networkLayout.test.ts` and
  `e2e/network-map.spec.ts` pass unchanged.
- **The server (`KbMap`)** builds the graph in memory. *Downwards only to pages* follows from what is in it: only objects pages link to, and
  what is above them. An object in focus without pages still brings its chain up to its site.
- **The circles** take each node's top-level location only with *Circles for sites*; the groups come with every answer.
- **Glyphs** are letters (KB, L, H, S), as the network map's are.

### P7: the finish

- The in-app documentation (*Markdown in PNMT*: *Sensitive*, and *Links, likes and the map*), docs/release-notes-1.20.md, README.md,
  deploy/README.md (*Notifications to people*) and `deploy/.env.example`.
- **Not done:** the version bump and the tag, which come with the release.

### The checks

**`./run-tests.sh` on the finished work:** every group green, *web* (Vitest 651 tests in 82 files, the build), *contract*, *deploy* (backup
and restore), *upgrade* (v0.12.1 → 1.0, then every migration up to `KbReactions`), *e2e* (the whole browser suite, `knowledge-extensions.spec.ts`
among it) and *shell*. In *dotnet*, one count needed its new row: *Task health* lists 21 background tasks now
(`TasksAreOffWhenBackgroundTasksAreDisabled`); after that, 1,279 tests (5 skipped) in Europe/Zurich and America/New_York.

**Known, outside this plan:** `SessionRulesTests.LastOnlineIsWhatTheGatesLetThrough` fails when it runs in the last 7 minutes before
04:00 in PNMT's time zone. Its fake clock starts at the real time and jumps 7 minutes, across `Auth:ResetHour`, where the session needs the
day's code and the request is refused. Starting its clock on a fixed time of day would end it.

---

## Appendix: files

**New:**

- **Server:**
  - `PNMTD/Knowledge/KbReactions.cs`, `KbMap.cs`, `KbChanges.cs`;
  - `PNMTD/Notifications/UserNotifications.cs` (enqueue, cancel, send, clean up), `UserNotificationKinds.cs`, and the two kinds' renderers;
  - `PNMTD/Api/V1/AccountNotificationsController.cs`, and the contracts;
  - four migrations;
  - `PNMTD.Tests/Knowledge/{KbReactionsTests,KbChangesTests,KbMapTests}.cs`, `PNMTD.Tests/Notifications/UserNotificationsTests.cs`.
- **Web UI:**
  - `src/components/graph/{forceLayout,geometry,focus}.ts`, with their tests;
  - `src/views/kb/map/{KnowledgeMapView,KnowledgeMapCanvas,MapPanel}.vue`;
  - `src/views/kb/{RecentChanges,Reactions,RelatedPages}.vue`;
  - `src/views/account/NotificationSwitches.vue`.

**Changed:**

- **Server:**
  - `KnowledgeBaseEntities.cs`, `PnmtdDbContext.cs`;
  - `KbPageService` (the mirrored page links, the locking, the revision checks), `KbRevisions` (`KbSnapshot`, `RefOf`, the change kinds, links-only revisions, `SameTextSinceAsync`), `KbAiTags` (its three revision checks), `KbPageList` (filters, orders, rows), `KbLinkNames`, `MachineAccess`, `KnowledgeBaseOptions`;
  - `KnowledgeBaseController`, `Contracts/KnowledgeBase.cs`;
  - `Tasks/NotificiationTask.cs` (the second timer only), `SystemService` (the task's row), `KeyValueKeyEnums` (`LAST_USER_NOTIFICATION_RUN`);
  - `AuditEvents`, `RightsCatalog`'s texts;
  - `Tools/Read/KnowledgeReadTools.cs`, `Tools/Write/KnowledgeWriteTools.cs`;
  - `DevSeedKnowledge`;
  - `EndpointAuditTests`, `MachineAccessTests`, `KbPageTests`.
- **Web UI:**
  - `KbListView.vue`, `KbPageView.vue`, `KbEditView.vue`, `KbHistoryView.vue`, `LinkPicker.vue`, `KbPanel.vue`, `listState.ts`;
  - `api/kb.ts`, `api/resources.ts`, `schema.d.ts`, `openapi.json`;
  - `views/network/networkLayout.ts`, `MapCanvas.vue` and `networkFormat.ts` (importing the moved parts);
  - `AccountView.vue`, the router, the preferences store, `DocsView.vue`;
  - `e2e/knowledge.spec.ts`.
- **Deployment and docs:** `deploy/.env.example`, deploy/README.md (`UserNotifications__*`, `KnowledgeBase__RecentChanges*`, `MapMaxNodes`, `UseRepeatHours`), README.md, docs/release-notes-1.20.md.

**Not changed:** the sensor API and its golden contract, the agents, the alert loop of `NotificationTask`, Cookie's `Outbox`, the lounge, the network map's behaviour.
