# Plan: Responsibilities: who owns what, with a deputy, a global responsible, and the Redmine projects and trackers each person handles

| | |
|---|---|
| Status | **Built: P1–P6, on master** (2026-11-23, §19 *As built*). For 1.21.0; not tagged yet. Revision 2 applied **the review's answers** (§18). Revision 1 was the first plan |
| Date | 2026-11-23 |
| Scope | A new page, ***Admin → Responsibilities***:<ol><li>**Responsibilities**, each with:<ul><li>a name, a short description, and a long description in Markdown, **edited like a knowledge base page**, with images, files and page links (§4);</li><li>a responsible and a deputy, each a PNMT user;</li><li>links to knowledge base pages, locations and hosts.</li></ul></li><li>**The global responsible and the global deputy.** They own whatever no responsibility covers, and stand in for a responsibility that has nobody (§5).</li><li>**Redmine combinations, optional.** A responsibility can list Redmine projects with a tracker, or with every tracker. Each names the user who handles it (§6).</li><li>**Shown where linked.** A panel on a host, a location or a page that a responsibility links (§8).</li><li>**MCP and Cookie read them,** read-only (§7).</li></ol> |
| Not in scope | <ul><li>**Telling anyone anything.** Responsibilities are information only (decision 5). Notification rules, Cookie's duties, the away watch and the access watch keep choosing their recipients as today. §13 names where they could hook in later.</li><li>**Who works today.** Responsibilities don't read the availability plan (decision 4). The availability plan itself stays as it is.</li><li>**Inheritance.** A host in a linked location shows nothing (decision 6).</li><li>**Anything per ticket.** No ticket is matched, nothing is shown on a ticket, and no assignee is set in Redmine.</li><li>Groups as the responsible or the deputy (decision 9).</li><li>Sensors as link targets.</li><li>Writes over MCP.</li><li>Reading which trackers a Redmine project has enabled.</li><li>Permissions per responsibility.</li><li>Archiving (decision 12).</li></ul> |
| References | <ul><li>**docs/cookie-plan.md §17.2 and decision 2:** "Hosts and locations have no owner", and the decision then against owners in PNMT. This plan brings owners in, as information (§2.1).</li><li>**docs/notification-recipients-plan.md:** *the responsible of the day*.</li><li>**docs/cookie-redmine-tab-plan.md:** the Redmine user links and the lookups.</li><li>**docs/locations-knowledge-plan.md:** the editor (§5.2–5.4), the files folder and `file_uses` (§5.5), and the location history.</li><li>**docs/knowledge-base-extensions-plan.md §7:** the link pattern.</li><li>**docs/knowledge-base-media-plan.md:** videos and the upload buttons, which this editor gets with no extra work.</li><li>**docs/mcp-plan.md:** tools and the ceilings.</li><li>**docs/audit-redmine-zabbix-plan.md §5:** areas and levels.</li><li>**docs/menu-proposal.md:** the drawer's rules.</li></ul> |
| Target | 1.21.0 |

**Words.**

- ***A responsibility*** is a row of the new `responsibilities`: an area of work, such as *Network*, *Backup* or *Printers*.
- ***Its responsible*** and ***its deputy*** are PNMT users.
- ***The global responsible*** and ***the global deputy*** are two users, set once on the page (§5).
- ***A link*** ties a responsibility to a page, a location or a host. An object is ***linked*** when a link names it. Only linked objects show the responsibility (§8).
- ***A Redmine combination*** is a Redmine project with a tracker, or with *every tracker*. It is listed on a responsibility and names the user who handles it there.
  - It says who handles that kind of ticket.
  - **It doesn't touch, match or change any ticket.**
- **Not to be confused with:**
  - ***the responsible of the day*** and ***the fallback of the day*** in the availability plan (`availability_days.ResponsibleId`, `FallbackId`, `PlanSnapshot.ResponsibleOn`);
  - ***the plan's global fallback*** (`AvailabilitySettings.GlobalFallbackUserId` or `GlobalFallbackGroupId`).

  Those say who is on duty on a day, for everything. A responsibility says who owns one area, every day. **Every label of this plan says *of Network* or *global responsible*, never *the responsible* alone** (§2.2).

---

## 1. Summary

| Topic | Decision |
|---|---|
| **What is there today** | **Nothing owns anything.** Hosts, locations, pages and sensors have no owner or contact. The Cookie plan decided against owners and chose the availability plan for who is on duty. **"Responsible" already means *the responsible of the day*,** and the C# name `Responsibility` is taken by the availability plan's record (§2) |
| **A responsibility** | A name (unique), a short description (one line, optional), and a long description in Markdown. It has a responsible, a deputy and links. **The description is edited and stored like a knowledge base page's text:** the same editor, size limit, credentials check, images, files and page links (decision 7). **Its old texts are kept,** with *Restore* (decision 8) (§4) |
| **The people** | **One responsible and one deputy,** each an active PNMT user, both optional, never the same person. A user deactivated later stays set, marked *inactive*. A deleted user's place becomes empty (§4.2) |
| **The global responsible** | **Two users in a settings row,** both optional (decision 1). They own **whatever no responsibility covers.** They **stand in for a responsibility without an active responsible or deputy**: it shows *Global responsible stands in*. They are separate from the availability plan's global fallback (§5) |
| **Links** | **Pages, locations and hosts,** many to many: a host can belong to *Network* and *Backup*. **No inheritance:** a host in a linked location isn't linked (decision 6) |
| **Redmine combinations** | **On a responsibility, optional, each with a user** (decision 2). The tracker can be *every tracker*. Subprojects are projects of their own (decision 3). **A combination exists once across all responsibilities.** It is stored by Redmine id, with the names copied. The user needs no Redmine link. The part about Redmine appears only when Redmine is configured (§6) |
| **The page** | **Admin → Responsibilities,** with the tabs ***Responsibilities*** (the global panel and the list) and ***Redmine*** (every combination, when Redmine is configured). **Each responsibility has a page of its own:** details, people, the description, links, combinations, and the history (§9) |
| **Elsewhere** | **A read-only panel on a host, a location or a page, only when a responsibility links it** (decision 6, §8) |
| **MCP and Cookie** | **Two read tools, `responsibilities_list` and `responsibility_get`, for MCP and for Cookie** (decision 11). Linked pages marked sensitive are left out. Cookie sees them once its rights group holds *Responsibilities: View*. Nothing of Cookie's duties changes (§7) |
| **Nothing is sent** | **Alerts, Cookie's reminders and escalations, and the away watch don't change** (decision 5). The page says so |
| **Rights** | **A new area, *Responsibilities*, with *View* and *Manage*** (decision 10). Admins hold it at once. No migration grants it to Operators or Viewers. **MCP tokens and Cookie can reach *View*,** never *Manage* (§12) |
| **Audit and history** | **Every change is in the audit log,** under a new category *Responsibilities*. Links and unlinks name the object too, so its History shows them. `responsibility_history` keeps old descriptions (§10.5, §12) |
| **What breaks nothing** | The availability plan, notification rules and Cookie's duties are untouched. The `file_uses` check is widened by one owner. The upload and download rights gain a third right. Cookie's pinned tool list grows by two. Each has a test (§14) |

---

## 2. Today

### 2.1 Nobody owns anything

- **Hosts, locations, sensors and pages have no owner, contact or responsible field** (`HostEntity`, `LocationEntity`, `SensorEntity`, `KbPageEntity`).
  - A page has authors: its creator and editors (`KbReactionService`, docs/knowledge-base-extensions-plan.md §9.4). They get the like and use e-mails. They aren't owners.
  - A location can have a field of type *User*, such as *On-site contact* (`LocationFieldType.User`, `LocationFieldService.cs:35`). It is shown, and no code reads it.
- **The Cookie plan found this and decided against owners.** docs/cookie-plan.md §17.2: *"Hosts and locations have no owner."* Decision 2: *"no owners, acknowledgements, maintenance windows or rota in PNMT"*. Revision 7 then added a rota after all: the availability plan.
- **This plan brings owners in as information.** Who gets what stays where decision 2 put it: the notification rules, the availability plan and Cookie's settings (decision 5).

### 2.2 "Responsible" already means something

- **The availability plan names *the responsible of the day*.** It also names a fallback per day, and a global fallback user or group (`AvailabilityDayEntity.ResponsibleId`, `FallbackId`; `AvailabilitySettings.cs:34-38`).
  - `PlanSnapshot.ResponsibleOn(day)` (`AvailabilityPlan.cs:115-143`) returns, in this order: the responsible, if they work; else the fallback, if they work; else the global fallback user; else the global group's members who work; else nobody.
  - It returns **`PNMTD.Availability.Responsibility(UserIds, As)`** (`AvailabilityPlan.cs:13`).
- **The same word is a value and a label in three more places:**
  - `NotificationRecipientKind.Responsible`, *"The responsible of the day"* (`NotificationRecipients.cs:33`, `ruleHelp.ts:17`);
  - `AwayRecipient.Responsible` (`CookieSettings.cs:165`);
  - Cookie's role strings *responsible today*, *fallback today* and *stands in today* (`CookieRound.cs:748-757`).
- **What follows for this plan:**
  - **No new type is named `Responsibility`.** `CookieRound`, `NotificationRecipients` and `Duties` import the availability namespace, and §13's later hooks would import both. The entity is `ResponsibilityEntity`; the namespace is `PNMTD.Responsibilities`; contracts are `ResponsibilityItem`, `ResponsibilityDetail`, and so on.
  - **Labels always qualify:** *Responsible of Network*, *Global responsible*. The MCP tools' descriptions say *of a responsibility, not of the day*. The documentation page explains both words side by side (§16, P6).

### 2.3 What PNMT knows about Redmine projects and trackers

- **The lookups live in memory** (`RedmineWorker.Loads.cs:112-179`): projects, statuses, trackers and priorities, refreshed hourly and pinned. **Nothing about projects or trackers is in the database.**
- **Projects:** `Redmine__Projects` names the chosen ones. With `IncludeSubprojects` (on by default), every descendant is *allowed* too (`:139-154`).
- **Trackers are Redmine's global list.** PNMT never reads which trackers a project has enabled (no `include=trackers`).
- **Settings refer to projects and trackers by numeric id:** `WatchedProject.ProjectId`, `TicketProjectId`, `TicketTrackerId` (`CookieSettings.cs:116-140`). Names are only display copies.
- **Subprojects stand alone in Cookie.** The case book keeps tickets whose project is exactly a watched one (`CaseBook.cs:322-346`).
- **The UI's lookups need rights this page's users may lack:**
  - `GET /redmine/lookups` needs *Redmine: View*.
  - `GET /cookie/admin/redmine` needs *Cookie: Manage* or *Redmine: Manage*.
- **A PNMT user's Redmine user** is `users.RedmineUserId` and `RedmineName`, linked on *Admin → Cookie → Redmine* (`RedmineLinks.cs`). Many users have none, and this plan doesn't need one (§6.1).

### 2.4 What can be reused

| Need | Exists | Where |
|---|---|---|
| **Links to pages, locations and hosts** | The *exactly one target* pattern: a check, cascade foreign keys, and filtered unique indexes with the target first | `kb_links`, `redmine_links` (`PnmtdDbContext.cs:416-429`, `:966-987`) |
| **Their names** | A location's path, a host's location, a page's number and whether it is archived, and *deleted …* | `KbLinkNames.ResolveAsync` (`KbPageList.cs:242`) |
| **Picking them** | `LinkPicker.vue` over the global search, with `kinds`. **The search needs *Monitoring: View*** (`SystemController.cs:56`) | `views/kb/LinkPicker.vue` |
| **The knowledge base's editor** | `MarkdownEditor` with `mode="page"` and `layout="page"`: the whole window, Ctrl/⌘S, the leave guard, drafts, uploads, paste and drop, `pick-page`, the conflict and credentials dialogs | `components/markdown/MarkdownEditor.vue`, `EditingPage.vue`, `KbEditView.vue:358-540` |
| **A page's text on the server** | `KnowledgeBase:MaxPageBytes` (1 MB), `MarkdownText.Read` (plain text, files, page numbers), `SecretScanner` on new lines (422 `looks_like_credential`), the revision with 409 `edit_conflict`, `FileUses.RecordAsync` | `KbPageService.cs:217-219`, `:412-440`; `KbRevisions.CommitAsync` |
| **Files** | `file_uses` with **exactly one owner, a page or a location** (`CK_file_uses_one_owner`). Without a use, an upload is removed after a day | `FileUses.cs:22-64`, `FileTasks.cs`, `FileAdmin.cs:57-62` |
| **Picking people** | `GET /cookie/admin/people` and `GET /notification-rules/people`, each under its own right. **`GET /users` is admin-only** | `usePickers.ts`, `useRecipientPickers.ts` |
| **A list → detail admin page** | `GroupsView` → `GroupView`: a list, a *New* dialog, a detail page with a form, `dirty`, Save and *Delete…* | `views/admin/groups/` |
| **A history with old and new values** | `location_history` with `Kind`, `OldValue`, `NewValue`, `Detail`; restoring writes a row too | `LocationEntities.cs:173-220`, `LocationHistory.vue` |
| **Read tools for MCP and Cookie** | `ToolSpec` with `ForCookie`; `ctx.Untrusted` for people's text; `MachineAccess.Pages` leaves out sensitive pages | `Tools/Read/KnowledgeReadTools.cs`, `Tools/ToolSpec.cs`, `Knowledge/MachineAccess.cs:28` |
| **Cookie's rights** | Cookie reads with one group's rights, `CookieRightsSettings.GroupId`; in a chat, with the lower of the group's and the asker's | `CookieSettings.cs:443-452`, `CookieTools.For`, `RightsTab.vue:45-46` |

---

## 3. Requirements

| # | Requirement |
|---|---|
| R1 | **A responsibility has a name, a short description and a long description in Markdown,** edited like a knowledge base page |
| R2 | **It is linked to knowledge base pages, locations and hosts:** any number of each, and an object may be in several responsibilities |
| R3 | **It has a responsible and a deputy,** each a PNMT user |
| R4 | **There is one global responsible and one global deputy** |
| R5 | **A responsibility can list Redmine combinations,** a project and a tracker, each with the user who handles it. They are optional |
| R6 | **All of it is on one new page, reached by an entry in the drawer's *Admin* group** |
| R7 | **A linked host, location or page shows its responsibilities** |
| R8 | **Every change is in the audit log,** and an overwritten description can be restored |
| R9 | **MCP and Cookie read responsibilities,** and never change them |
| R10 | **Nothing that exists changes behaviour:** alerts, Cookie's duties, the availability plan, files, rights (§14) |

---

## 4. A responsibility

### 4.1 Fields

| Field | Rules |
|---|---|
| **Name** | 1–100 characters, one line, trimmed. **Unique, ignoring case** (`NormalizedName`, as `users.NormalizedUsername`). A duplicate gives 409 `duplicate_name` |
| **Short description** | Optional, at most 300 characters, one line. Shown in lists, panels and the MCP tools |
| **Long description** | Markdown, optional, **as a knowledge base page's text** (decision 7):<ul><li>at most `KnowledgeBase:MaxPageBytes` (1 MB);</li><li>saved with its `revision`: a stale one gives 409 `edit_conflict`;</li><li>**the credentials check** on new lines (422 `looks_like_credential`, `acknowledgeCredentials`);</li><li>**images, files and links to pages** as on a page. Videos come with docs/knowledge-base-media-plan.md, through the same uploads.</li></ul> |
| **Responsible** | Optional. An active PNMT user |
| **Deputy** | Optional. An active PNMT user, **not the responsible** (400; a database check too) |
| **Links** | Pages, locations and hosts, **at most 200** (409 `too_many_links`). The same object at most once (409 `already_linked`) |
| **Redmine combinations** | Optional (§6) |

- **A deputy without a responsible is allowed.**
- **Archived pages stay linked** and show *archived*, as page links do.
- **Deleting a page, a location or a host removes its links** (cascade). The audit log keeps the object's deletion.
- **The order is by name** everywhere.

### 4.2 People who leave

| Case | What happens |
|---|---|
| **A user is deactivated** (`IsActive = false`) | They stay set, marked ***inactive*** in the list, on the page, in the panels and in the tools. The list's *Needs attention* finds such responsibilities (§9.2). With neither person active, the global people stand in (§5) |
| **A user is deleted** | Their place becomes empty (`SET NULL`, as `availability_days.ResponsibleId` and `notificationrules.UserId` do). A combination naming them shows *Nobody (user removed)* (§6.5) |
| **Picking** | Only active users are offered; the current choice stays readable when it isn't (`withUnknown`, as in `cookieAdmin.ts:27-37`) |

### 4.3 Deleting a responsibility

- **A hard delete, with a confirmation** that says what goes with it: *"3 links and 2 Redmine combinations are removed with it."* (decision 12).
- **Its links, its combinations, its history and its file uses** go with it (cascade). Its files become unused and are cleaned up as a deleted page's are.
- **The audit log keeps** `ResponsibilityDeleted` with its name, people and counts.

---

## 5. The global responsible and the global deputy

- **Two users, both optional, not the same,** in a one-row table, `responsibility_settings`. The migration inserts the row, so a save is always an update.
- **What they are for** (decision 1):
  1. **They own whatever no responsibility covers.**
     - On the page: *"They own what no responsibility covers."*
     - In the tools: asked about a host, a location or a page that nothing links, `responsibilities_list` says so and names them (§7.1).
  2. **They stand in for a responsibility with no active responsible and no active deputy.** That responsibility shows ***Global responsible stands in: Anna Muster (deputy Ben Keller)***, in the list, on its page, in the panels and in the tools.
- **They aren't the availability plan's global fallback.**
  - That one says who is on duty when nobody of the day works, for everything (§2.2).
  - The global responsible says whose area everything uncovered is.
  - They can be the same person, and neither is copied from the other. The panel says so in one line: *"Who is on duty each day is the availability plan's."*
- **Shown at the top of the *Responsibilities* tab** (§9.2).
- **Unset:** a warning there: *"No global responsible is set. What no responsibility covers has nobody."*

---

## 6. Redmine combinations

### 6.1 A combination

| Field | Rules |
|---|---|
| **Responsibility** | The one it is listed on, always (decision 2) |
| **Project** | A Redmine project PNMT reads: `Allowed` in the lookups, subprojects included. Stored by id, with `ProjectName` copied |
| **Tracker** | A tracker from Redmine's list, or ***Every tracker*** (stored as null). Stored by id, with `TrackerName` copied |
| **User** | **Required: who handles this project and tracker for this responsibility.** An active PNMT user when chosen. **No Redmine link is needed.** Without one, a muted hint says *"Dan has no Redmine user linked (Admin → Cookie → Redmine)"*, as information |
| **Note** | Optional, at most 200 characters: *"Support desk only during the migration"* |

- **Optional, twice over:**
  - **A responsibility needs no combination.** Most will have none.
  - **Without Redmine configured, the Redmine parts aren't shown at all:** no tab, and no panel on a responsibility's page. Combinations made while it was configured stay in the database, and come back when it is configured again.
- **Each (project, tracker) exists once, across all responsibilities:** a unique index, with nulls not distinct. A second gives 409 `duplicate_combination`, naming the responsibility and user that have it. So *"who handles Helpdesk · Hardware?"* has one answer.
- ***Every tracker* and a single tracker of the same project can exist side by side** (decision 3). *Helpdesk · every tracker → Carla* with *Helpdesk · Hardware → Dan* reads as "Dan handles hardware, Carla the rest". On the tab, the single tracker is listed under its *every tracker* row.
- **Subprojects are projects of their own** (decision 3). *Helpdesk › Printers* isn't covered by *Helpdesk*'s rows. This is how Cookie's watched projects work (§2.3). The picker indents subprojects under their parent.
- **A combination doesn't need Cookie to watch the project.** Any project PNMT reads can be listed.

### 6.2 The lookups

- **`GET /responsibilities/redmine`** (*Responsibilities: Manage*) returns `configured`, the allowed projects (id, name, parent), the trackers and `error`. It reads the worker's cached `lookups` snapshot through `RedmineService`, so a manager needs no Redmine or Cookie right, and Redmine isn't asked again.
- **Names are refreshed** from the lookups when the page loads. A renamed project or tracker shows its new name and is saved with it on the next change, as `RedmineLinks` refreshes a renamed user (`RedmineLinks.cs:186-200`).

### 6.3 When Redmine isn't reachable

| Case | What is shown |
|---|---|
| **Redmine can't be reached** | The rows with their copied names; *Add* and *Change* wait (*"Redmine can't be reached: projects and trackers can't be listed right now"*) |
| **A project is no longer allowed, or gone** | The row stays, marked ***Not a project PNMT reads***, until it is changed or deleted |
| **A tracker is gone** | The row stays, marked ***Unknown tracker #7*** |

### 6.4 Where they are edited

- **On a responsibility's page,** in its *Redmine* panel: its own combinations (§9.3).
- **On the *Redmine* tab,** every combination of every responsibility, for the overview and for finding one (§9.5). Both edit the same rows.

### 6.5 A user who leaves

- **Deactivated:** the row stays, marked *inactive*, and *Needs attention* lists its responsibility.
- **Deleted:** the row stays with nobody (`SET NULL`; the database allows it, the API requires a user). It shows ***Nobody (user removed)***.

---

## 7. MCP and Cookie (decision 11)

### 7.1 Two read tools

| Tool | What it returns | Arguments |
|---|---|---|
| **`responsibilities_list`** | The responsibilities: id, name, short description, responsible, deputy (with *inactive*, or *the global responsible stands in*), counts of links and combinations. **Given an object or a project, the responsibilities that link it or list it.** When nothing links the object: *"No responsibility links this host. The global responsible is Anna Muster, the global deputy Ben Keller."* | `query`, `hostId`, `locationId`, `pageNumber`, `redmineProject`, and the paging arguments. At most one of the four filters |
| **`responsibility_get`** | One responsibility in full: the fields of `responsibilities_list`, **the long description** (`ctx.Untrusted`, at most 8,000 characters, as `CookieTools.MaxResultChars`), the linked pages (number and title), locations (path) and hosts (name and location), the Redmine combinations (project, tracker, user), the global people, and a link to its page (`ctx.Link("admin/responsibilities/{id}")`) | `id`, or `name` |

- **Both are `ToolKind.Read`, `Area.Responsibilities`, `Level.View`, `ForCookie = true`.**
- **Their descriptions say what the word means:** *"Responsibilities: who owns an area of work (not the availability plan's responsible of the day)."* This keeps an AI client from mixing the two.
- **Linked pages go through `MachineAccess.Pages`,** so a page marked sensitive is never named. The count says *and 1 page not shown*.
- **People are names only,** never e-mail addresses or Pushover keys. That matches `availability_get`.
- **The Redmine combinations are left out while Redmine isn't configured,** as the page leaves them out.
- **In `Tools/Read/ResponsibilityReadTools.cs`,** listed in `ToolCatalog.All`. They call `ResponsibilityQueries` (§10.6), the same service as the API.

### 7.2 Rights

- **`McpRights.Ceiling` and `ReadOnlyCap` gain `[Area.Responsibilities] = Level.View`.** A token gets the tools only when its user holds the right. *Manage* never reaches MCP, which `McpToolsTests` already checks ("no Manage level").
- **Cookie reads with its rights group** (`CookieRightsSettings.GroupId`). It sees the tools once that group holds *Responsibilities: View*. **No migration grants it.**
  - **In a chat, Cookie reads with the lower of the group's rights and the asker's** (`RightsTab.vue:45-46`). So the person asking needs *Responsibilities: View* too.
  - *Admin → Cookie → Rights* lists the group's rights as they are, and shows the new area once it is granted. It needs no change.
  - The release notes say to grant it.
- **`McpToolsTests`' pinned list,** "Cookie keeps its fifteen", becomes seventeen, with both tools.

### 7.3 Nothing else of Cookie changes

- **No prompt, duty, recipient or summary reads responsibilities** (decision 5).
- Cookie uses the tools **when it is asked**, in chat or the lounge: *"who is responsible for sw-core-01?"*, *"who handles Helpdesk hardware tickets?"*.
- **The lounge's denied tools** (`LoungeTools.Denied`) stay as they are, so the lounge can use the tools too.

---

## 8. Shown where linked (decision 6)

```
┌ Responsibilities ─────────────────────────────────────────┐
│ Network      Switches, routers, firewall, WAN              │
│              Anna Muster · deputy Ben Keller               │
│ Backup       Veeam jobs and the offsite copies             │
│              Global responsible stands in: Anna Muster     │
└────────────────────────────────────────────────────────────┘
```

- **`views/responsibilities/ResponsibilitiesPanel.vue`,** with props `hostId` / `locationId` / `pageId`, as `KbPanel`'s.
- **Shown only when a responsibility links this object, and only with *Responsibilities: View*.** Otherwise nothing is rendered: no empty panel, no *"No responsibility"*.
  - **The object's own response says whether it has any.** `HostDetail`, `LocationDetail` and `KbPageDetail` gain `responsibilities`: a short list that is empty without the right. The panel needs no request of its own, and the page doesn't flicker.
- **No inheritance:** a host in a linked location shows nothing, unless the host is linked itself.
- **Where:**
  - `HostView.vue`: after *Properties*;
  - `LocationView.vue`: the overview's right column, after *Hosts*;
  - `KbPageView.vue`: the aside, after *Links*.
- **Read-only.** A name opens the responsibility. Links are made on the responsibility's page, so there is one place to change them.
- **Tickets show nothing:** a Redmine combination isn't about a single ticket.

---

## 9. The page

### 9.1 The drawer and the routes

- **The drawer** (`layouts/navigation.ts`), in the *Admin* group, right after *Availability plan*:

  | Label | Icon | Route | Name | Right |
  |---|---|---|---|---|
  | *Responsibilities* | `pi pi-briefcase` (unused so far; `navigation.test.ts` keeps icons unique) | `/admin/responsibilities` | `responsibilities` | `['Responsibilities', 'View']` |

- **The routes** (`router/index.ts`), all lazy:

  | Path | Name | View | Right |
  |---|---|---|---|
  | `/admin/responsibilities/:tab(list\|redmine)?` | `responsibilities` | `ResponsibilitiesView.vue` | View |
  | `/admin/responsibilities/:id([0-9a-f-]{36})` | `responsibility` | `ResponsibilityView.vue` | View |
  | `/admin/responsibilities/:id([0-9a-f-]{36})/edit` | `responsibility-edit` | `ResponsibilityEditView.vue` | Manage |

- **Without Redmine configured,** `/admin/responsibilities/redmine` shows the list tab instead.

### 9.2 The *Responsibilities* tab

```
Admin › Responsibilities
 Responsibilities │ Redmine

┌ Global ─────────────────────────────────────────────────────────────────────────────┐
│ Global responsible  Anna Muster          Global deputy  Ben Keller        [Change…] │
│ They own what no responsibility covers, and stand in for one that has nobody.      │
│ Who is on duty each day is the availability plan's.                                │
└────────────────────────────────────────────────────────────────────────────────────┘
 Responsibilities say who owns what. Alerts and Cookie's reminders still follow the
 notification rules and the availability plan.

 [🔍 Name, description, people…]  ☐ Needs attention                [+ New responsibility]

 Name       Short description                   Responsible        Deputy        Links  Redmine
 Backup     Veeam jobs and the offsite copies   Global stands in: Anna Muster ⚠      3        0
 Network    Switches, routers, firewall, WAN    Anna Muster        Ben Keller       14        2
 Printers   The printers and their contracts    Carla Rossi ⚠      Ben Keller        6        1
                                                 inactive
```

- **The global panel:** both people and ***Change…*** (Manage), a small dialog with two pickers. With *View* only, the people are text.
- **The list:** a DataTable, ordered by name; a row click opens the responsibility.
  - **The search** goes to the server (`q`) and covers the name, the short description, the long description's plain text and the people's names.
  - ***Needs attention*** filters to responsibilities with nobody set, with an inactive person, or with a combination whose user is inactive or removed. A ⚠ marks them.
  - **Links** counts pages, locations and hosts. **Redmine** counts combinations, and is shown only when Redmine is configured.
- ***New responsibility*** (Manage): a dialog with the name, the short description, the responsible and the deputy. It then opens the new page, as *New group* does.
- **Empty:** *"No responsibilities yet. Create one for each area of work, such as Network or Backup, and link the hosts, locations and pages it covers."*

### 9.3 A responsibility's page

```
Admin › Responsibilities › Network                                              [⋯]
Network
Switches, routers, the firewall and the WAN links
 Overview │ History

┌ Description ──────────────────────────────────────── [Edit] ┐ ┌ Details ─────────────────────────┐
│ ## What it covers                                           │ │ Name         [Network          ] │
│ The core switches in every building, the firewall, …        │ │ Short desc.  [Switches, routers…] │
│ ![Core rack](/api/v1/files/…/core-rack.jpg)                 │ │ Responsible  [Anna Muster     ▾] │
│                                                             │ │ Deputy       [Ben Keller      ▾] │
│ ## Escalation                                               │ │                          [Save]  │
│ See KB-42 for the WAN provider's numbers.                   │ └──────────────────────────────────┘
└─────────────────────────────────────────────────────────────┘ ┌ Linked ──────────────────────────┐
                                                                │ [Link a page, location or host…] │
                                                                │ 📄 KB-42 WAN providers          ✕│
                                                                │ 📍 Zurich › Building A          ✕│
                                                                │ 🖥 sw-core-01 · Zurich › Bldg A ✕│
                                                                └──────────────────────────────────┘
                                                                ┌ Redmine ─────────────────────────┐
                                                                │ Infrastructure · every tracker   │
                                                                │   Anna Muster                  ✕ │
                                                                │ Helpdesk · Network               │
                                                                │   Ben Keller                   ✕ │
                                                                │ [+ Add…]                         │
                                                                └──────────────────────────────────┘
```

- **Layout:** `GroupView`'s, `.columns` 2fr/1fr with panels; one column on narrow screens.
- **Details:** one form with `dirty` and *Save*, as `GroupView`, sending all four fields. Errors are mapped with `e.field(...)`. With nobody active: *Global responsible stands in: Anna Muster (deputy Ben Keller)* below the form.
- **Description:** `MarkdownView` with the responsibility's `files`. ***Edit*** opens §9.4. Empty: *"No description yet."* with *Write one* (Manage).
- **Linked:** the rows as `KbPageView` shows a page's links: an icon (`kbLinkIcon`), a RouterLink (`kbLinkRoute`), a muted detail, and ✕ (Manage).
  - ***Link a page, location or host…*** is `LinkPicker` with `kinds: ['Page', 'Location', 'Host']` and a new optional `search` prop. Here it uses `GET /responsibilities/targets`, which needs no *Monitoring: View* (§2.4).
- **Redmine** (only when Redmine is configured): this responsibility's combinations, each with its user and note. ***Add…*** opens the dialog of §9.5 for this responsibility, and a row opens it for a change.
- **⋯:** ***Delete…*** (Manage), with `ask()` and the counts of §4.3.
- **History:** §10.5's rows, newest first. A description row has ***Show*** (the old and the new text, as the location history shows a value) and ***Restore*** (Manage). Restoring is a save of its own.
- **With *View* only:** the same page, without the pickers, ✕, *Edit*, *Save*, *Restore* and ⋯.

### 9.4 Editing the description: like a knowledge base page

- **`ResponsibilityEditView.vue`,** built as `KbEditView.vue` is (decision 7):
  - `MarkdownEditor mode="page" layout="page"`. The editor mode a person chose for pages is the one they get here.
  - The `title` slot shows the responsibility's name.
  - `:base-revision`, `:draft-key="responsibility:${id}"`, `:files`, `:back-label="Back to Network"`.
  - **`pick-page`,** so links to pages are inserted as in a page.
  - **Uploads, paste and drop** of images and files. With docs/knowledge-base-media-plan.md, videos and the *Image*, *Video* and *File* buttons come too.
  - A 409 `edit_conflict` sets `conflict`, and a 422 `looks_like_credential` sets `credentials`, as on a page.
  - After a save: `editor.saved()`, the query invalidated, and the page opened.
- **`layout="page"`** brings the whole window, Ctrl/⌘S and the leave guard (`EditingPage.vue`).
- **What a page has that this doesn't:** tags, a status, the sensitive mark, *Written with AI*, likes and uses, numbered revisions with diffs, and the knowledge map. A responsibility has its own fields instead, and §10.5's history.

### 9.5 The *Redmine* tab (only when Redmine is configured)

```
Admin › Responsibilities
 Responsibilities │ Redmine

 Who handles which Redmine project and tracker. A single tracker is listed under its
 project's "every tracker". Subprojects are projects of their own.

 Project                  Tracker          Responsibility   User              Note
 Helpdesk                 every tracker    Workplaces       Carla Rossi ⚠     Desk only
                          Hardware         Workplaces       Dan Weber         (no Redmine user linked)
                          Network          Network          Ben Keller
 Helpdesk › Printers      every tracker    Printers         Carla Rossi ⚠
 Infrastructure           every tracker    Network          Anna Muster
 Project #31              Bug              Backup           Nobody (user removed) ⚠  ⚠ Not a project PNMT reads
```

- **Ordered by project path, then *every tracker* first, then tracker name.**
- **Filters:** by project, by responsibility and by user.
- **A row opens the dialog** (Manage), and ⋯ has *Delete*. ***Add a combination*** is here too, with *Responsibility* as the dialog's first field.
- **The dialog:**
  - *Responsibility*: set and fixed when opened from a responsibility's page;
  - *Project*: a Select with a filter, subprojects indented by `parentId`;
  - *Tracker*: a Select with *Every tracker* first;
  - *User*: a Select over the active users, with the hint of §6.1;
  - *Note*: optional.

  A 409 `duplicate_combination` says *"Helpdesk · Network is already listed on Network, with Ben Keller"*, with a link to that responsibility.
- **§6.3's warnings** sit above the table.

---

## 10. Data model and migrations

### 10.1 `responsibilities` (`ResponsibilityEntity`, `[Audited]`)

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | |
| `Name` | text (100) | |
| `NormalizedName` | text (100) | Upper case, trimmed; unique index. `[AuditIgnore]` |
| `Summary` | text (300), null | The short description |
| `Description` | text, default `''` | Markdown. `[AuditText]`: the audit log records line counts, the history keeps the text |
| `PlainText` | text, default `''` | `MarkdownText.Read(...).PlainText`, for the search. `[AuditIgnore]` |
| `Revision` | int, default 0 | Raised by each description save |
| `ResponsibleId` | uuid, null | FK `users`, `SET NULL` |
| `DeputyId` | uuid, null | FK `users`, `SET NULL` |
| `CreatedAt`, `CreatedBy`, `CreatedByName` | | As pages have them |
| `UpdatedAt`, `UpdatedBy`, `UpdatedByName` | | |

- **Check** `CK_responsibilities_two_people`: `"ResponsibleId" IS NULL OR "DeputyId" IS NULL OR "ResponsibleId" <> "DeputyId"`.

### 10.2 `responsibility_links` (`ResponsibilityLinkEntity`, `[Audited]`)

- **Columns:** `Id`; `ResponsibilityId` (FK, cascade); `PageId`, `LocationId` and `HostId`, each null with an FK and cascade; `CreatedAt`, `CreatedBy`, `CreatedByName`.
- **Check** `CK_responsibility_links_one_target`: `num_nonnulls("PageId", "LocationId", "HostId") = 1`.
- **Filtered unique indexes, the target first:** `(PageId, ResponsibilityId) WHERE PageId IS NOT NULL`, and the same for locations and hosts. They serve the panels of §8 and the cascades, as `kb_links`' do. Also an index on `ResponsibilityId`.

### 10.3 `responsibility_settings` and `responsibility_redmine`

- **`responsibility_settings`** (`ResponsibilitySettingsEntity`, `[Audited]`):
  - `Id` int, always 1, with a check;
  - `GlobalResponsibleId` and `GlobalDeputyId`, each an FK to `users` with `SET NULL`, with the check of §10.1;
  - `UpdatedAt`, `UpdatedBy`, `UpdatedByName`.

  The migration inserts the row.
- **`responsibility_redmine`** (`ResponsibilityRedmineEntity`, `[Audited]`):
  - `Id` uuid;
  - `ResponsibilityId` uuid, **not null** (FK, cascade);
  - `ProjectId` int, `ProjectName` text (255);
  - `TrackerId` int null (null means every tracker), `TrackerName` text (255) null;
  - `UserId` uuid null (FK `users`, `SET NULL`; the API requires it, §6.5);
  - `Note` text (200) null;
  - created and updated, as above.

  Indexes:
  - **unique** `(ProjectId, TrackerId) NULLS NOT DISTINCT`, as `locations` uses it;
  - indexes on `ResponsibilityId` and `UserId`.

### 10.4 `file_uses`: a third owner, as pages have it

- **`file_uses.ResponsibilityId`** uuid null, FK with cascade. **The check `CK_file_uses_one_owner`** becomes `num_nonnulls("PageId", "LocationId", "ResponsibilityId") = 1`.
- **`FileUses.RecordAsync`** takes an owner, `FileOwner(FileUserKind Kind, Guid Id)`, instead of two nullable ids. Its two callers, `KbRevisions.CommitAsync` and `LocationValues.SetAsync`, pass theirs, and behave as before.
- **A description save records its files as a page's save does.** Uses are only added while the owner exists, never removed.
- **`FileUserKind.Responsibility`.** `FileAdmin` lists the use, and `FilesTab.vue`'s `useTarget` links to the responsibility.
- **The upload and download rights** get a third right (§12).

### 10.5 `responsibility_history` (`ResponsibilityHistoryEntity`)

- **Marked** `[NotAudited("a history of its own; each change is also in the audit log")]`.
- **Columns:** `Id`; `ResponsibilityId` (FK, cascade); `At`; `ByUserId`; `ByName`; `Kind`; `OldValue`; `NewValue`; `Detail`. Indexed on `(ResponsibilityId, At)`. This is `location_history`'s shape.
- **`ResponsibilityHistoryKind`**, stored as its number (append, never reorder): `Created`, `Renamed`, `SummaryChanged`, `PeopleChanged`, `DescriptionChanged` (the old and the new text), `LinkAdded`, `LinkRemoved`, `RedmineAdded`, `RedmineChanged`, `RedmineRemoved`.
- **Restoring** an old description writes a `DescriptionChanged` row with *Restored from 2026-11-20 14:03* in `Detail`.
- **Links removed because their object was deleted** leave no row: the cascade happens in the database. The object's deletion is in the audit log.

### 10.6 Code

- **Namespace `PNMTD.Responsibilities`:**
  - `ResponsibilityService`: the CRUD, the links, the people, the description, the history, and the combinations;
  - `ResponsibilityQueries`: the reads that the list, a responsibility's page, the panels and the two tools share. It covers *linked to this object*, *listing this project*, and *the global people stand in*. It doesn't depend on HTTP;
  - `ResponsibilitiesSetup.AddResponsibilities`, called from `Program.cs`.
- **Contracts in `Api/V1/Contracts/Responsibilities.cs`:**
  - `ResponsibilityItem`, `ResponsibilityDetail`, `ResponsibilityList`, `ResponsibilityRef` (for the panels);
  - `ResponsibilityRequest`, `ResponsibilityDescriptionRequest`, `ResponsibilityPerson(Id, Name, Active)`;
  - `RedmineCombinationItem`, `RedmineCombinationRequest`;
  - `ResponsibilityErrors`.
- **Links reuse `KbLinkKind`'s values `Page`, `Location` and `Host`,** so `KbLinkNames.ResolveAsync`, `kbLinkIcon` and `kbLinkRoute` serve them as they are. A request with `Sensor` gives 400.

### 10.7 Migrations

| Migration | Phase | Changes |
|---|---|---|
| `Responsibilities` | P1 | `responsibilities`, `responsibility_links`, `responsibility_settings` with its row, and `responsibility_history` |
| `ResponsibilityFiles` | P2 | `file_uses.ResponsibilityId` and the widened check |
| `ResponsibilityRedmine` | P3 | `responsibility_redmine` |

- **The tables are new,** and the one new column is null, so no row needs a backfill and `tools/upgrade-test.sh` passes.
- **Each migration carries the summary naming this plan,** as `20261122013518_KbPageLinks.cs` does.
- **`has-pending-model-changes`** stays clean (`run-tests.sh:127`).

---

## 11. API

Everything is under `/api/v1/responsibilities`, in the existing style. `{id}` is `{id:guid}`, so `global`, `people`, `targets` and `redmine` don't collide with it. The OpenAPI file and `schema.d.ts` are regenerated. **The sensor API and its golden contract don't change.**

| Endpoint | Right | Audit | |
|---|---|---|---|
| `GET /responsibilities?q&attention` | View | – | `{ global, items }`. Items: id, name, summary, responsible, deputy, `globalStandsIn`, link and combination counts, `attention` |
| `GET /responsibilities/{id}` | View | – | Plus description, revision, files, links (`KbLinkItem` through `KbLinkNames.ResolveAsync`), combinations (empty without Redmine configured) |
| `POST /responsibilities` | Manage | `ResponsibilityCreated` | `{ name, summary?, responsibleId?, deputyId? }` → 201 with the detail. 409 `duplicate_name` |
| `PUT /responsibilities/{id}` | Manage | `ResponsibilityChanged` | `{ name, summary, responsibleId, deputyId }`: the whole form, null meaning *nobody*. The description isn't in it |
| `PUT /responsibilities/{id}/description` | Manage | `ResponsibilityChanged` | `{ content, revision, acknowledgeCredentials? }`. 409 `edit_conflict`, 422 `looks_like_credential`, 400 over `MaxPageBytes`, as a page's save |
| `POST /responsibilities/{id}/links` | Manage | `ResponsibilityLinked` | `{ kind, id }`. 404 for an unknown target, 409 `already_linked`, 409 `too_many_links` |
| `DELETE /responsibilities/{id}/links/{linkId}` | Manage | `ResponsibilityUnlinked` | |
| `DELETE /responsibilities/{id}` | Manage | `ResponsibilityDeleted` | |
| `GET /responsibilities/{id}/history?page` | View | – | |
| `POST /responsibilities/{id}/history/{rowId}/restore` | Manage | `ResponsibilityChanged` | A `DescriptionChanged` row only; a save with the current revision |
| `PUT /responsibilities/global` | Manage | `GlobalResponsibleChanged` | `{ responsibleId, deputyId }` |
| `GET /responsibilities/people` | Manage | – | Active users: id, name, short code, whether a Redmine user is linked. **Never contact details** |
| `GET /responsibilities/targets?q` | Manage | – | Pages (number, title, archived), locations (path) and hosts (name, location), at most 20. Names only, as the links show them |
| `GET /responsibilities/redmine` | Manage | – | The lookups of §6.2 |
| `GET /responsibilities/redmine/combinations?project&responsibility&user` | View | – | Every combination, with its warnings; 404 `redmine_not_configured` without Redmine |
| `POST /responsibilities/{id}/redmine` · `PUT …/redmine/{combinationId}` | Manage | `ResponsibilityRedmineSaved` | `{ projectId, trackerId?, userId, note? }`. A `PUT` may move the combination to another responsibility (`responsibilityId`). 409 `duplicate_combination`; 400 for a project PNMT doesn't read (`RedmineService.CheckProject`) or an inactive user |
| `DELETE /responsibilities/{id}/redmine/{combinationId}` | Manage | `ResponsibilityRedmineDeleted` | |
| `GET /hosts/{id}` · `GET /locations/{id}` · `GET /kb/pages/{number}` | As today | – | **Plus `responsibilities`:** id, name, summary, responsible, deputy, `globalStandsIn`. Empty without *Responsibilities: View* (§8) |
| `POST /files` · `GET /files/{id}/{name}` | + Responsibilities: Manage / View | As today | §12 |

- **Validation** goes through `ApiValidator`, with the camelCase field names; text is trimmed with `Text.Trimmed`.
- **`EndpointAuditTests` lists each endpoint** with its right and `Roles.Admin`: no former role holds the new area.

---

## 12. Rights and audit

### 12.1 Rights

| Capability | Right |
|---|---|
| The page, the list, a responsibility, its history, the *Redmine* tab | *Responsibilities: View* |
| The panels on linked hosts, locations and pages | *Responsibilities: View*, on a page whose own right the reader already has |
| Create, change and delete; links; the description and its restore; the global people; combinations | *Responsibilities: Manage* |
| Upload and read files in the description | *Manage* to upload, *View* to read |
| `responsibilities_list`, `responsibility_get` | *Responsibilities: View*: a token's user, or Cookie's rights group |

- **`Area.Responsibilities`** is appended to the enum, with `View` and `Manage` in `RightsCatalog` (decision 10).
  - *View:* "The responsibilities, their people, links and Redmine combinations, and the panels on linked hosts, locations and pages. Also over MCP and for Cookie."
  - *Manage:* "Create, change and delete responsibilities, their links and Redmine combinations, and set the global responsible and deputy."
- **No migration grants it.** `group_rights` stores areas as text, so nothing else changes. Admins meet every right; others need it on a group. For Cookie, it is the group of *Admin → Cookie → Rights* (§7.2).
- **The web UI:** `levelOrder` in `api/rights.ts` gains the area; it is a `Record<Area, …>`, so vue-tsc demands it. `GroupsTests`' count of areas goes from 23 to 24.
- **MCP:** `Ceiling` and `ReadOnlyCap` gain *View* (§7.2).
- **The files:** `RequireAnyRight` takes two rights today (`RequireRight.cs:20-22`). It gets a constructor for three, and the upload and the download add *Responsibilities: Manage* and *View*. Former roles keep their access, which `EndpointAuditTests` checks.
- **What a manager sees through the pickers:**
  - every active user's name;
  - the titles of pages, sensitive ones included. A title isn't the content, and people with *Knowledge base: View* see sensitive pages anyway;
  - locations' paths and hosts' names.

  This is what the links show anyway. **Machines never see a sensitive page's title** (§7.1).

### 12.2 Audit

- **A new `AuditCategory.Responsibilities`,** and the target type `Responsibility`. `ResponsibilityEntity` maps to it by its label (`AuditChangesInterceptor.cs:113`).
- **Events:**
  - `ResponsibilityCreated`, `ResponsibilityChanged` and `ResponsibilityDeleted`;
  - `ResponsibilityLinked` and `ResponsibilityUnlinked`. **These add the object's id to `Subjects`, so its own History shows *Linked to Network*;**
  - `GlobalResponsibleChanged`;
  - `ResponsibilityRedmineSaved` and `ResponsibilityRedmineDeleted`.

  Each is added to `AuditCatalog`, with its tone derived from its name.
- **The field changes** come from the interceptor: the people as ids, with the names in the summary (`AuditLog.Summary`), as notification rules name their recipient.
- **The tools write no audit,** as every read tool. `McpLog` records their calls as for any tool.
- **`AuditModelTests`** finds every new entity marked.

---

## 13. Later: where responsibilities could decide who is told (not built)

**This plan sends nothing** (decision 5). Each of these would be a plan of its own, calling `ResponsibilityQueries`:

| Use | Where it would hook | What it would need |
|---|---|---|
| **Cookie's ticket reminders and escalations** | `Recipients.ForTicket` (`Duties.cs:32-52`): the user of the ticket's project and tracker, before the project's escalation group | A `TrackerId` on `cookie_cases`, which today has only the project |
| **Cookie's own tickets** | `Outbox.DeliverAsync` (`Outbox.cs:260-278`): `assigned_to_id` from a combination's user, by `users.RedmineUserId` | That user's Redmine link |
| **Notification rules** | A new `NotificationRecipientKind`, *the people of the host's responsibilities* | `NotificationRecipients.ResolveAsync` would need the sensor's host, and its per-run cache key would become (rule, host) (`NotificiationTask.cs:126-128`, `:200-209`) |
| **The away watch** | A recipient per project: docs/cookie-redmine-tab-plan.md decision 5's "natural next step" | The combinations of §6 |
| **Who works today** | The deputy when the responsible is away, from `PlanSnapshot.Works` | Decision 4 found it not needed now |
| **The global search** | `SearchService`, a kind of its own | |

- **No type is named `Responsibility`,** so those files can import both namespaces (§2.2).

---

## 14. Compatibility: what must not break

| What | How it stays | Checked by |
|---|---|---|
| **The availability plan, *the responsible of the day* in rules, Cookie's duties and the away watch** | Untouched: no file of theirs changes | Their tests as they are: `NotificationRecipientsTests`, the Cookie duty tests, the away watch tests |
| **Cookie's tools** | Two read tools are added; none changes | `McpToolsTests`: the pinned list (17), "Cookie only reads", every area within the ceiling |
| **MCP tokens** | *View* is added to the ceiling and the cap; nothing above it | `TheReadOnlyCapAndTheCeilingHoldForEveryArea` |
| **Files of pages and location fields** | `RecordAsync` takes an owner and behaves the same for its two callers. The check is widened, not changed | `FileUses` tests, `KbPageTests`, the location value tests; a test that a responsibility's use keeps a file past the cleanup |
| **Uploading and reading files** | The two existing rights stay; a third is added | `EndpointAuditTests` (former roles keep their access) |
| **The details of hosts, locations and pages** | A new list that is empty without the right | `check:api`; the detail tests with and without the right |
| **Deleting users, pages, locations and hosts** | `SET NULL` for people and cascade for links, so nothing refuses a delete | Tests: deleting a user empties their places; deleting a host, a location or a page removes its links |
| **The editor** | Used as pages use it; the editor mode preference is shared with pages, by design | `KbEditView`'s tests unchanged; the e2e of §15 |
| **The drawer** | One entry, an unused icon | `navigation.test.ts` |
| **The rights catalog** | One area appended | `GroupsTests` (24), `rights.test.ts` |
| **Upgrades** | New tables, one null column, the settings row inserted | `tools/upgrade-test.sh` |
| **The seed** | Three responsibilities, the global people and two combinations, on an empty database only | `DevSeedTests` |
| **Pages and the CSP** | The new routes join the list | `e2e/pages.spec.ts` for Admin; a Viewer is sent to the dashboard |

---

## 15. Testing

As before: **few tests, where they protect something.**

| What | How |
|---|---|
| **Responsibilities** (`ResponsibilitiesApiTests`) | Rights per level, with `ClientWithRights`. A duplicate name ignoring case gives 409. The responsible equal to the deputy gives 400. An inactive user can't be chosen but stays set. With neither person active, `globalStandsIn`. Delete removes the links, combinations and history. The audit events and their `Subjects` |
| **The description** | 409 `edit_conflict` from a stale revision; 422 `looks_like_credential` and the acknowledgement; over `MaxPageBytes` gives 400; the plain text in the search; its files survive `FileCleanupTask`; deleting the responsibility leaves them unused; `FileAdmin` lists the use |
| **Links** | Each kind. A sensor gives 400. The same object twice gives 409; the 201st gives 409. An archived page stays linked. Deleting a host, a location or a page removes its link. The details of a linked host carry it, of an unlinked one (in a linked location) don't, and without the right the list is empty |
| **History** | Each kind writes its row. Restore writes a new `DescriptionChanged` with the old text. Restore of another kind gives 400 |
| **Redmine combinations** | A combination with *every tracker* and one with a single tracker of the same project side by side. The same pair on another responsibility gives 409, nulls included. A user is required and active. A project PNMT doesn't read gives 400. A deleted user leaves *nobody*. Without Redmine configured, the combinations are left out of the detail and the list gives 404. Redmine's client is faked, as the Redmine tests do |
| **The tools** (`ResponsibilityToolsTests`) | `responsibilities_list` by words, by host, location, page and project. For an unlinked object, the global people. `responsibility_get`: the description as untrusted text, cut at the limit; **a linked sensitive page never named**; no contact details. A read-only token reaches both; a token without the right doesn't; Cookie with and without the right on its group |
| **Web** (Vitest) | The *global stands in* and *inactive* wording (`responsibilityPeople.ts`). The *Redmine* tab's order and warnings. `navigation.test.ts` and `rights.test.ts` as they are |
| **End to end** (`e2e/responsibilities.spec.ts`) | An admin creates *Network*, sets a responsible and a deputy, writes a description with an image and a page link, links a host and a location, and adds a combination. The panel shows on the host, and not on another host in that location. Then it is deleted. A user with *View* sees all of this without edit actions |

---

## 16. Risks

| Risk | Answer |
|---|---|
| **People confuse *the responsible of Network* with *the responsible of the day*** | Labels always qualify (§2.2). The page says that alerts and Cookie follow the rules and the plan. The tools say it in their descriptions. The documentation page puts both side by side |
| **People expect alerts or tickets to go to the responsible** | The same line on the page; §13 for later; the release notes say it plainly |
| **An AI client mixes up the two** | The tools' descriptions, and `availability_get` keeps its own words |
| **Responsibilities go stale:** people leave or change teams | *Needs attention* and the ⚠ mark. *Global stands in* when nobody is active. A deleted user empties their place, and the history and the audit say when |
| **A host is expected to show its location's responsibility** | Decision 6: only linked objects show one. Linking a host takes one picker on the responsibility's page. Inheritance can come later, in the same panel |
| **A combination names a tracker its project doesn't use** | It is information and does no harm. The dialog says the trackers are Redmine's global list |
| **A Redmine project is renamed, moved or removed** | Stored by id, names refreshed. *Not a project PNMT reads* marks it |
| **Redmine is switched off** | The Redmine parts disappear; the rows stay and come back with it |
| **No global responsible is set** | The warning on the tab; the tools say *nobody* |
| **The description holds a password** | The credentials check of pages. Machines read it as untrusted text, as they read pages, which have the same check and nothing more |
| **A sensitive page is linked to a responsibility** | Machines don't see it: `MachineAccess.Pages` |
| **The widened `file_uses` check breaks the existing owners** | `RecordAsync`'s callers unchanged in behaviour, and their tests (§14) |

---

## 17. Phases

| Phase | Content | Size |
|---|---|---|
| **P0** | **Check, with no product code:** `pi-briefcase` in PrimeIcons 7.0.0 | small |
| **P1** | **Responsibilities and the global people:** the area, the migration `Responsibilities`, `ResponsibilityService` and `ResponsibilityQueries`, the endpoints without the description and the combinations, the drawer entry and routes, the *Responsibilities* tab, a responsibility's page (details, people, links, history, delete), `LinkPicker`'s `search` prop, the audit, the seed | medium–large |
| **P2** | **The description, like a page:** its endpoint, the revision, the credentials check, `ResponsibilityEditView`, the history's *Show* and *Restore*, the migration `ResponsibilityFiles`, `FileOwner`, the file rights, `FileAdmin` and `FilesTab` | medium |
| **P3** | **Redmine combinations:** the migration `ResponsibilityRedmine`, the lookups endpoint, the responsibility's *Redmine* panel and the *Redmine* tab, hidden without Redmine | medium |
| **P4** | **Shown where linked:** `responsibilities` on the details of hosts, locations and pages, and `ResponsibilitiesPanel` | small |
| **P5** | **MCP and Cookie:** `ResponsibilityReadTools`, the ceiling and the cap, the pinned list | small |
| **P6** | **Release:** the documentation page (`views/docs/ResponsibilitiesDocs.vue`), with *responsible of the day* and *responsible of a responsibility* side by side. docs/release-notes-1.21.md (with "grant *Responsibilities: View* to Cookie's group"), the README, this plan's *As built* | small |

- **Each phase** is one or more commits on master, has `./run-tests.sh` green before the next starts, and goes into an *As built* section.
- **P2, P3, P4 and P5 need P1, not each other.** P5 names combinations once P3 is in.
- **P1 alone is already useful:** who owns what, with a deputy and the global people, on one page.

---

## 18. Decisions (review of revision 1, 2026-11-23)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | What is the global responsible for? | **As recommended:** they own whatever no responsibility covers, and stand in for a responsibility that has nobody. Since decision 4 drops *today*, "nobody" means no active responsible and no active deputy | §5 |
| 2 | To whom is a Redmine combination assigned? | **To a user, within a responsibility:** every combination is listed on one responsibility and names the user who handles that project and tracker there. **The combination is assigned, not a ticket:** no ticket is matched or shown. **Redmine is optional:** a responsibility needs no combination, and the Redmine parts appear only when Redmine is configured | §6 |
| 3 | *Every tracker*, and subprojects? | **As proposed:** *every tracker* allowed, beside single trackers of the same project; subprojects are projects of their own | §6.1 |
| 4 | Who it is today, from the availability plan? | **Not relevant:** responsibilities show no *today*, and don't read the availability plan. **The availability plan and everything that uses it stay as they are** | §13, §14 |
| 5 | Does this plan change who is told? | **No:** information only, for now | §13 |
| 6 | Shown on other pages, and inherited? | **Only where linked:** a panel on a host, location or page that a responsibility links. No inheritance, no empty panel, nothing on tickets | §8 |
| 7 | Images and files in the description? | **Like the knowledge base:** the page editor with uploads and page links, the page's size limit and credentials check, and the files recorded as a page's are | §4.1, §9.4, §10.4 |
| 8 | A history of the description? | **Yes:** old and new text per save, with *Restore* | §10.5 |
| 9 | Groups as the responsible or the deputy? | **No, users** | §4.1 |
| 10 | Rights | **A new area, *Responsibilities*, with *View* and *Manage*,** granted by no migration | §12.1 |
| 11 | MCP and Cookie? | **Both, read-only:** `responsibilities_list` and `responsibility_get` | §7 |
| 12 | Deleting | **Delete,** at once, with a confirmation | §4.3 |

**Decided along with these, for the next review:**

- **A combination exists once across all responsibilities,** so *"who handles Helpdesk · Hardware?"* has one answer. The alternative is once per responsibility, so two responsibilities could list the same project and tracker, each with its own user.
- **A combination's user is required.** A combination without a user would say nothing that the responsibility's own people don't.
- **The user needs no Redmine link.** A hint says when they have none.
- **Cookie needs *Responsibilities: View* on its rights group** before it sees the tools, and in a chat the asker needs it too. Nothing grants it automatically.
- **A linked object's panel comes with the object's own response,** not a request of its own, so it only appears when there is something to show.

No questions are open.

---

## 19. As built

**P1–P6 are on master** (2026-11-23), in five commits. Where the code differs from the sections above, this section says so; the rest
is as planned.

### P1 and P2: the page, the global people, links, and the description

- **One commit for both** (`d885800`), not one each. P2's model change was made while P1's full run was building, so that run didn't
  count; P1 and P2 share the service, the contracts, the page and the model snapshot, so they were tested and committed together rather
  than split by hand into a state that was never run.
- **The description is restored from *before* a change** (§10.5), as a location's history restores a value: the history dialog shows the
  text before and after, and *Restore the text from before* saves the earlier one as a change of its own.
- **`SearchService.ObjectsAsync`** finds the link picker's pages, locations and hosts as the global search does, without sensors, tickets
  or Zabbix, for `GET /responsibilities/targets`.
- **`RequireAnyRight` has a constructor for three rights**, used by the upload and the download of files.
- **`FileUses.RecordAsync` takes a `FileOwner`** (page, location or responsibility) instead of two nullable ids.
- **The editing page's *Link to a page…*** searches through `GET /responsibilities/targets`, so it needs no *Monitoring: View* either.

### P3: Redmine combinations

- **Names come from the worker's cached lookups** (`RedmineWorker.CachedLookups`, new), which never waits or asks Redmine; the dialog
  and saves read the lookups through the queue as the Cookie tab does.
- **A save that needs Redmine's lookups while Redmine can't be reached** answers 503 `redmine_unreachable`. A change that keeps the
  project and tracker doesn't need them.
- **A move to another responsibility** writes a history row on both, and its audit entry names both, so both histories show it.

### P4: the panels

- **The panels have an endpoint of their own,** `GET /responsibilities/linked?host|location|page`, instead of a new field on the details
  of hosts, locations and pages (§8, §11). Those contracts are used by MCP, Cookie and many pages; the panel asks only for those with
  *Responsibilities: View*, and still renders nothing without a link.

### P5: MCP and Cookie

- **People's names go through the tool's masker** (`ctx.Privacy.Mask`), as `availability_get` does: Cookie knows them as it knows people
  elsewhere, MCP sees them as they are.
- **The description is given as plain text** (`MarkdownText.Read(…).PlainText`, at most 6,000 characters), as `kb_page` gives a page.

### P6: the finish

- The documentation page (*Responsibilities*, next to the availability plan's), docs/release-notes-1.21.md, the README's features.

### The checks

- **`./run-tests.sh --only web,dotnet` green after each commit:** typecheck, lint, Prettier, Vitest and the build; the .NET tests in
  Europe/Zurich and America/New_York; no pending model changes.
- **New tests:** `ResponsibilitiesApiTests`, `ResponsibilityRedmineTests` (against the fake Redmine), `ResponsibilityToolsTests`, and
  `responsibilities.test.ts`; `FileUsesTests`, `EndpointAuditTests`, `GroupsTests`, `McpToolsTests` and `DevSeedTests` extended.
- **The e2e group** (`./run-tests.sh --only e2e`) green after P6, with the new page in `e2e/pages.spec.ts`.
- **Not run here:** the contract, deploy and upgrade groups. The sensor API isn't touched; the three migrations only add tables and one
  null column.

---

## Appendix: files

**New, server:**

- `PNMTD/Responsibilities/ResponsibilityService.cs`, `ResponsibilityQueries.cs`, `ResponsibilitiesSetup.cs`
- `PNMTD/Models/Db/ResponsibilityEntities.cs`
- `PNMTD/Api/V1/ResponsibilitiesController.cs`, `PNMTD/Api/V1/Contracts/Responsibilities.cs`
- `PNMTD/Tools/Read/ResponsibilityReadTools.cs`
- `PNMTD/Data/Migrations/…_Responsibilities.cs`, `…_ResponsibilityFiles.cs`, `…_ResponsibilityRedmine.cs`
- `PNMTD/Cli/DevSeedResponsibilities.cs`

**New, tests:** `PNMTD.Tests/Responsibilities/ResponsibilitiesApiTests.cs`, `ResponsibilityRedmineTests.cs`, `ResponsibilityToolsTests.cs`

**New, web:**

- `PNMTD.WebUI/src/api/responsibilities.ts`, as `api/kb.ts` sits beside `resources.ts`
- `src/views/admin/responsibilities/`: `ResponsibilitiesView.vue`, `ResponsibilitiesTab.vue`, `RedmineTab.vue`, `GlobalPanel.vue`, `NewResponsibilityDialog.vue`, `CombinationDialog.vue`, `ResponsibilityView.vue`, `ResponsibilityEditView.vue`, `ResponsibilityHistory.vue`, `responsibilityPeople.ts` with its test
- `src/views/responsibilities/ResponsibilitiesPanel.vue`
- `src/views/docs/ResponsibilitiesDocs.vue`
- `e2e/responsibilities.spec.ts`

**Changed, server:**

- `PnmtdDbContext.cs` and the snapshot
- `Auth/Rights/Rights.cs` (the area), `RequireRight.cs` (three rights)
- `Mcp/McpRights.cs` (ceiling and cap), `Tools/ToolCatalog.cs`
- `Audit/AuditEvents.cs` (category, events, targets, catalog)
- `Files/FileUses.cs`, `FileAdmin.cs`, `FileEntities.cs`, `Api/V1/FilesController.cs`, `Contracts/Files.cs`
- `Knowledge/KbRevisions.cs`, `Locations/LocationValues.cs` (the owner)
- The details of hosts, locations and pages: `HostService`, `LocationService`, `KbPageService` and their contracts (`responsibilities`)
- `Program.cs`; `Cli/DevSeedCommand.cs` calls the new `Cli/DevSeedResponsibilities.cs`, as it calls the other features' seeds

**Changed, tests:** `Hosting/EndpointAuditTests.cs`, `Auth/GroupsTests.cs`, `Mcp/McpToolsTests.cs`, `Cli/DevSeedTests.cs`

**Changed, web:**

- `layouts/navigation.ts`, `router/index.ts`, `api/rights.ts`, `api/openapi.json`, `api/schema.d.ts`
- `views/kb/LinkPicker.vue` (`search`)
- `views/hosts/HostView.vue`, `views/locations/LocationView.vue`, `views/kb/KbPageView.vue`
- `views/admin/kb/FilesTab.vue`
- `e2e/pages.spec.ts`
- `views/docs/DocsView.vue`, for the new `ResponsibilitiesDocs.vue`

**Changed, docs:** docs/release-notes-1.21.md, README.md
