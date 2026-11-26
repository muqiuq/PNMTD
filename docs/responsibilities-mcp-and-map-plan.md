# Plan: Responsibilities in full over MCP, the escalation order, and the Overview's lines and arrangements

**Status:** **Built: P1–P6, on master** (2026-11-26, §15 *As built*), for 1.27.0. Revision 2's decisions are in §12. It extends [responsibilities-extensions-plan.md](responsibilities-extensions-plan.md) (released in 1.26.0) and [mcp-extensions-plan.md](mcp-extensions-plan.md) (released in 1.25.0). Their decisions stay unless §13 names them: responsibilities are information only (first plan, decision 5), and nothing is inherited (first plan, decision 6). Below, *decision N* means this plan's §12.

| | |
|---|---|
| **Goal** | <ol><li>**Everything a responsibility has is reachable over MCP** (§6), above all **its colour, as a hex value**. That covers what the web UI's *Edit* level can change, plus the history, the Links tab and the Overview's notes.</li><li>**An escalation order** for each responsibility: **responsible → deputy → global responsible → global deputy** (§3). It is shown on the page, on the map and in the tools.</li><li>**The map's lines limited to the responsible, the deputy, or both** (§4). **By default, only the main responsible's lines are shown.**</li><li>**An *Arrange* button on the map** that places the responsibilities in a compact circle, in clusters around their responsible, or in a grid, never overlapping (§5).</li></ol> |
| **Not in scope** | <ul><li>Sending anything to anyone: the escalation order is information, like everything else here (first plan, decision 5). Alerts and Cookie's escalations don't change.</li><li>Deleting responsibilities and setting the global people over MCP: they need *Manage*, which the MCP ceiling never holds (mcp-plan §5.3).</li><li>The map's layout over MCP: positions and *Arrange* (decision 7).</li><li>Uploading files to a description over MCP.</li><li>A choice of lines remembered per person: it is kept in the page's address, like the layers (decision 3).</li><li>Saved positions for people and linked objects (extensions plan, decision 7).</li></ul> |
| **Rights** | No new area or level. *Responsibilities: View* reads, *Edit* changes, as in 1.25. Arranging works like dragging: anyone can do it, and saving needs *Edit* (decision 6) |
| **Migrations** | **None.** The escalation order is computed, the choice of lines is kept in the page's address, and *Arrange* saves through the existing positions |

---

## 1. Summary

| Part | What it is |
|---|---|
| **Escalation order** | Each responsibility's people in the order to ask them: responsible, deputy, global responsible, global deputy. Anyone unset or inactive is skipped, and a person appears only once. It is computed on the server (`EscalationOrder.Of`) and returned on the list, the detail, the map and in the tools |
| **Lines** | A choice above the map: *Lines to* **Responsible** (the default) · Deputy · Both. *Responsible* draws a line to the first person in each escalation order, *Deputy* to the second, and *Both* to the first two. The line's style still says the person's role. It is kept in the page's address as `?lines=` |
| **Arrange** | A menu in the map's controls: **Circle**, **Around their responsible** and **Grid**. Each places every responsibility so that no two names overlap and none sits under a note. The result is an ordinary unsaved move, so the *Save layout · Discard* bar decides what happens to it |
| **MCP** | Colour on create, update, list and get, as `#rrggbb`. The palette is in `responsibility_options`. Also: the escalation order, *needs attention* and who changed it when; a description on create; restoring from the history. **6 new tools:** history, the Links tab (list and bulk change), and the Overview's notes (read, write, delete). 125 → 131 tools |
| **Cookie** | Its two tools also answer with the escalation order, with names masked as Cookie knows people. No colour, no new tools: Cookie keeps its 17 |

---

## 2. Today (checked in the code)

**MCP** ([ResponsibilityReadTools.cs](../PNMTD/Tools/Read/ResponsibilityReadTools.cs), [ResponsibilityWriteTools.cs](../PNMTD/Tools/Write/ResponsibilityWriteTools.cs)):

- **Seven tools:** `responsibilities_list` and `responsibility_get` (View, also Cookie's), and `responsibility_options`, `_create`, `_update`, `_link`, `_redmine` (Edit).
- **No colour anywhere.** `responsibility_update` sends `Color = current.Color` (l.126), so a change over MCP keeps the colour but can't set it. Neither reading tool returns it. The extensions plan's §8.4 and its decision 9 kept colours, the map and notes out of the tools.
- **Missing from the answers:** *needs attention* (`ResponsibilityItem.Attention`), who created and last changed it (`CreatedAt`, `CreatedByName`, `UpdatedByName`), and the description's files (`ResponsibilityDetail.Files`).
- **Not reachable at all:**
  - the history (`GET {id}/history`, `…/{rowId}`) and *Restore* (`POST …/restore`, Edit);
  - the Links tab (`GET links`, `unlinked/hosts`, `unlinked/locations`, and the bulk `POST links`, `links/unlink`, `links/move`);
  - the Overview's notes (`/responsibilities/map/notes`).
- **Out by rule:** deleting and `PUT global` need *Manage*. The MCP ceiling has *Responsibilities: Edit* and never *Manage* (`McpRights.cs`, checked by `McpToolsTests`).
- **Colours are checked** by `Colours.Normalize` and `Colours.IsHex` ([Colours.cs](../PNMTD/Services/Domain/Colours.cs)): `#rrggbb`, stored in lower case. The 12 swatches exist only in the web UI ([colours.ts](../PNMTD.WebUI/src/components/colours.ts) `palette`, `firstUnused`).

**The map** ([overviewLayout.ts](../PNMTD.WebUI/src/views/admin/responsibilities/overview/overviewLayout.ts), [OverviewCanvas.vue](../PNMTD.WebUI/src/views/admin/responsibilities/overview/OverviewCanvas.vue), [OverviewTab.vue](../PNMTD.WebUI/src/views/admin/responsibilities/overview/OverviewTab.vue)):

- **`buildGraph` draws every named person** (l.117-124):
  - a solid line to the responsible and a dashed one to the deputy, **even when inactive** (greyed);
  - dotted lines from both global people **only when `globalStandsIn`**, that is, when neither the responsible nor the deputy is set and active (`ResponsibilityQueries.StandsIn`, l.222).
- **No choice of lines.** The *People* layer shows everyone who has a line.
- **Arranging:**
  - only by hand: drag, the arrow keys, *Save layout · Discard*;
  - the *Layout* menu (`OverviewTab.vue` l.238) has *Save this layout* and *Back to automatic for all…*.
- **People and objects don't follow a move until it is saved.** `layoutOverview` anchors only saved places (`n.saved`), never `moved`.
- **Free nodes start on a sunflower spiral** around the anchored ones (`forceLayout.ts` `spiral`, l.80, l.196), not near their neighbours.

---

## 3. The escalation order

### 3.1 The rule

**Each responsibility's people, in the order to ask them:**

1. the responsible;
2. the deputy;
3. the global responsible;
4. the global deputy.

**It is one simple queue, one person after another** (decision 2). Whoever is missing is skipped, and everyone behind them moves up one place. There are no branches and no second list: the page, the map, the side panels and the tools all read this one queue.

**How the order is built:**

- **A step is skipped** when nobody is set there or the person is inactive (decision 1).
- **A person appears once,** at their first place. A global responsible who is also this responsibility's deputy is listed as its deputy.
- **The order has 0 to 4 people.** Each comes with their role: `Responsible`, `Deputy`, `GlobalResponsible`, `GlobalDeputy`.
- **The *main responsible* is the first one in the order.**
- **`GlobalStandsIn` keeps its meaning,** and is now the same thing as "the first in the order is a global person". The *G* mark and every *Global responsible stands in* text stay as they are.

| Responsible | Deputy | Global responsible | Global deputy | Escalation order |
|---|---|---|---|---|
| Anna | Ben | Carla | Dan | 1 Anna (responsible) · 2 Ben (deputy) · 3 Carla (global responsible) · 4 Dan (global deputy) |
| – | Ben | Carla | Dan | 1 Ben (deputy) · 2 Carla · 3 Dan |
| Anna, inactive | Ben | Carla | – | 1 Ben (deputy) · 2 Carla |
| – | – | Carla | Dan | 1 Carla · 2 Dan, so *G* |
| Carla | Ben | Carla | Dan | 1 Carla (responsible) · 2 Ben (deputy) · 3 Dan. Carla is listed once |
| – | – | – | – | Nobody. ⚠, as today |

- **It is information only.** Nobody is told anything, and Cookie's escalations (`Duties.cs`), alerts and the away watch don't change. The page and the documentation say: *"Whom to ask, in this order. Nothing is sent."*
- **The name:**
  - **in English, *escalation order*.** It is the order you asked for as "Escalation row".
  - **In the code, `EscalationOrder`,** so it isn't mistaken for Cookie's `Escalation…` types.

### 3.2 Where it shows

| Where | How |
|---|---|
| **A responsibility's page** | One line below the people: *Escalation order: Anna Muster → Ben Keller → Carla Rossi (global) → Dan Roth (global deputy)*, with *Nobody* when it is empty |
| **The map's side panel** | A numbered list (mockup in §4.1). Below it, a skipped person is named: *Carl Weber, the responsible, is inactive: skipped* |
| **A person on the map** | Their responsibilities, each with their role and their step: *Network: responsible, 1st · Backup: global responsible, 2nd* |
| **The map's lines** | §4 |
| **MCP and Cookie** | `escalation: [{ step, role, name }]` in `responsibilities_list` and `responsibility_get` (§6.2) |

The panels on hosts, locations and pages keep their wording (`ResponsibilityRef`). They name the responsible, the deputy and who stands in, which is enough there.

### 3.3 Data

- **Computed once, on the server:**
  - a new `EscalationOrder.Of(responsible, deputy, global)` in `PNMTD/Responsibilities/`, a pure function, next to `StandsIn` and `NeedsAttention`;
  - it returns `IReadOnlyList<ResponsibilityStep>`, with `record ResponsibilityStep(int Step, ResponsibilityRole Role, ResponsibilityPerson Person)` and `enum ResponsibilityRole { Responsible, Deputy, GlobalResponsible, GlobalDeputy }`.
- **Added to `ResponsibilityItem`, `ResponsibilityDetail` and `ResponsibilityMapNode`** as `Escalation`. `ResponsibilityQueries` already has every person with their `Active` flag, and the map builds on `ListAsync` (`ResponsibilityMap.cs` l.41), so nothing more is read.
- **The contracts only grow.** `openapi.json` and `schema.d.ts` are regenerated.

---

## 4. The map's lines: responsible, deputy, or both

### 4.1 For the person

```
┌ Responsibilities · Links · Overview · Redmine ─────────────────────────────────────────────────────────────┐
│ Show ☑ People ☐ Hosts ☐ Locations ☐ Pages ☑ Notes   Lines to [Responsible│Deputy│Both]   🔍 Find…  [+ Note] │
├───────────────────────────────────────────────────────────────────────────────┬────────────────────────────┤
│                                                     [Fit] [Arrange ▾] [Layout ▾]│ ● Backup                   │
│      (●Network)──(AM)                                                          │ Veeam jobs, offsite copies │
│                    │                                                           │ Escalation order           │
│      (●Printers)───┘        (●Backup)┄┄┄(BK)                                   │  1  Ben Keller    deputy   │
│                                                                                │  2  Anna Muster   global   │
│   ── responsible  ╌╌ deputy  ┄┄ global   Lines: the first in each order        │  3  Dan Roth  global deputy│
│                                                                                │ Carl Weber, the responsible│
│                                                                                │ is inactive: skipped.      │
└────────────────────────────────────────────────────────────────────────────────┴────────────────────────────┘
```

- ***Lines to*** is a SelectButton next to the layers. It is disabled, with the tooltip *Turn on People*, while the *People* layer is off.

**The three choices read the queue** (§3.1, decision 2): *Responsible* is whoever is first in it, *Deputy* whoever comes next.

| Choice | Draws, for each responsibility | Legend line |
|---|---|---|
| **Responsible** (default, decision 3) | A line to the **first** in its escalation order | *Lines: the first in each escalation order* |
| **Deputy** | A line to the **second** in it | *Lines: the second in each escalation order* |
| **Both** | Lines to the **first two** | *Lines: the first two in each escalation order* |

For example, Backup's responsible Carl is inactive, its deputy is Ben, and Anna is the global responsible. *Responsible* draws Ben (dashed), *Deputy* draws Anna (dotted), and *Both* draws both of them.

- **The style says the role,** not the step:
  - solid for the responsible, dashed for the deputy, dotted (in the warning colour) for a global person, as today.
  - So in the default view, a dashed line means *the deputy is asked first, because the responsible is missing or inactive*. A dotted one means *nobody of its own*, the same as the *G* mark.
- **The *People* layer shows only the people who have a line in the chosen view.** By default, someone who is only a deputy isn't on the map. *Find* lists only the people shown. The global card in the corner always shows both global people.
- **Inactive people get no line,** because the order skips them. The responsibility keeps its ⚠, and its side panel names the skipped person (§3.2).
- **The page's address keeps the choice** (decision 3), as it already keeps the layers and the selection:
  - *Deputy* makes the address `…/admin/responsibilities/overview?lines=deputy`, and *Both* makes it `?lines=both`;
  - *Responsible*, the default, adds nothing, as the default layers add nothing (`overviewState.ts`);
  - so a reload, the browser's *Back*, a bookmark and a copied link all show the same lines;
  - nothing is stored for the person, so whoever opens *Overview* from the menu starts with *Responsible*.
- **The linked objects' grey lines** are unaffected: the layers control them.

### 4.2 Code

- **`buildGraph(map, layers, lines)`:**
  - takes `lines: 'responsible' | 'deputy' | 'both'`;
  - reads `r.escalation` instead of `responsibleId`, `deputyId` and `globalStandsIn`;
  - draws step 1, step 2, or both, with the kind from the role (`GlobalResponsible` and `GlobalDeputy` → `'global'`).
- **`overviewState.ts`** gets `linesFromQuery` and `linesToQuery`.
- **The legend** in `OverviewCanvas.vue` (l.327) adds the line naming the view.
- **The person panel** (`OverviewPanel.vue`) lists their steps from `escalation`.
- **Switching the view moves only the free nodes** (people), as switching a layer does. Saved and arranged responsibilities stay where they are.

---

## 5. *Arrange*

### 5.1 For the person

- **An *Arrange ▾* menu among the map's controls,** next to *Fit* and *Layout*:
  - ***Circle:*** the responsibilities on a ring, as small as it can be without two names overlapping.
  - ***Around their responsible:*** a small ring for each main responsible (§3.1), with that person in the middle. The rings are packed tightly together.
  - ***Grid:*** rows and columns, by name, about as wide as a screen (16:10).
- **What arranging moves:**
  - it moves **every responsibility**, since all of them are always on the map;
  - **notes stay where they are,** and no responsibility is placed under one (§5.3).
- **The result is an unsaved move,** as a drag is:
  - each responsibility that moved has the dashed ring;
  - the bar says *12 responsibilities moved · [Discard] [Save layout]*;
  - **nothing is saved until *Save layout*,** and *Discard* goes back to the saved layout.
- **The view fits the result,** and a toast says *Arranged 12 responsibilities as a circle. Not saved yet.*
- **Unsaved hand moves of responsibilities ask first:** *Replace your unsaved moves?* Moved notes stay moved.
- **People and linked objects are placed around the new places right away** (§5.4). They don't wait for *Save*.
- **Everyone can arrange,** as everyone can drag. Without *Edit*, the arrangement lasts until the page is left, and there is no bar (decision 6).

### 5.2 The forms

**Every form uses one cell size, so no two responsibilities overlap,** names included:

- **Width:** the longest name shown, cut at 28 characters as the map cuts it, at about 6.6 units a character at 12 px. At least the circle's 52 units, at most 200, plus a gap of 24.
- **Height:** 110, which is the circle (2 × 26), the name below it (24), and a gap.
- **The same data gives the same form.** The order is fixed and nothing is random.

| Form | Order | Geometry |
|---|---|---|
| **Circle** | **By main responsible, then by name;** responsibilities with nobody come last. A person's responsibilities sit together, so their lines stay short | One ring, starting at 12 o'clock and going clockwise. Its radius is the smallest at which neighbours are a cell's width apart: `R = w / (2·sin(π/n))`, but at least 120, so that people fit inside. One responsibility sits in the middle. **Above 24 responsibilities, concentric rings** (a compact disc), 1.5 cell heights apart, filled from the inside, each holding as many as fit |
| **Around their responsible** | **Clusters by main responsible,** the largest first, then by the person's name. *Nobody* is its own cluster, without a person in the middle, and comes last | Each cluster's ring has radius `max(110, w / (2·sin(π/k)))`; a single responsibility sits 110 to the right of the middle. The clusters are packed with d3's `packSiblings` (d3-hierarchy is already a dependency, `forceLayout.ts` uses `packEnclose`), with 40 between them. So they never overlap, and the result is as compact as circle packing gets |
| **Grid** | By name | `columns = ceil(√(n · 1.6 · h / w))`, filled row by row |

### 5.3 Where the form goes

- **Centred where the responsibilities are now:** the mean of their places as drawn. So the view doesn't jump far.
- **Clear of notes:**
  - if a cell would overlap a note (with a margin of 24), **the whole form moves**, along a fixed spiral in steps of 60, to the nearest place where it doesn't;
  - the form keeps its shape and the notes keep their places;
  - after 200 tries it stays centred, and the toast adds *Some responsibilities are under notes.*

### 5.4 People and objects after arranging

- **Arranged places count as fixed for the automatic layout.**
  - `OverviewTab` keeps them as `anchors` and passes them to the canvas;
  - `layoutOverview(nodes, edges, obstacles, anchors)` treats them as it treats saved places;
  - so the people settle around the new form at once.
  - *Discard*, *Save* (after the refresh, the saved places are the same) and *Back to automatic* clear the anchors.
  - A drag after arranging doesn't lay the people out again, as today.
- **A free node starts at the mean of its fixed neighbours,** not on the spiral:
  - `layoutGraph` gets an optional `starts` map; the network map passes none and stays the same;
  - a cluster's person therefore starts in its middle and stays there, also after a reload;
  - on the circle, a person starts inside the ring, next to their arc;
  - free nodes with the same start are spread by the seeded random source, as d3's collision force already does.
- **The same picture after a reload:** the responsibilities come from their saved places, and the people from the rule above.

### 5.5 Code

- **`overview/arrange.ts`, pure functions:**
  - `cellSize(names)`;
  - `circle(nodes, order)`, `clusters(nodes, escalation)`, `grid(nodes)`, each returning `Map<id, Point>` around (0, 0);
  - `place(form, centre, notes)`, which moves the form clear of the notes (§5.3).
- **`OverviewTab.vue`:** the menu sets `moved` and `anchors`, asks first when there are unsaved moves, and then calls `fit`.
- **`forceLayout.ts`:** the optional `starts` parameter.
- **Performance:** 100 responsibilities are arranged in under 20 ms. The layout's budget is unchanged (extensions plan §6.3).

---

## 6. MCP: everything a responsibility has

### 6.1 What the web UI has, and what MCP gets

| In the web UI | Right | MCP today | After this plan |
|---|---|---|---|
| Name, short description | Edit | `_create`, `_update` | As today |
| **Colour** | Edit | – (kept as it is) | **`_create`, `_update`** with `color`; read in `_list` and `_get`; the palette in `_options` (§6.3) |
| Long description (Markdown) | Edit | `_update` with `baseRevision` | Also on `_create` |
| Responsible, deputy | Edit | `_create`, `_update` | As today |
| Escalation order | (computed) | – | `_list`, `_get`, also for Cookie |
| *Needs attention* | (computed) | – | `_list` (also as a filter) and `_get` |
| Created and changed, by whom | – | `_get`: `updatedAt` | `_get`: `createdAt`, `createdByName`, `updatedAt`, `updatedByName`; `_list`: `updatedAt` |
| The description's files | Edit (upload) | – | `_get` lists them: name, kind, size. Uploading stays in the web UI |
| A link, one at a time | Edit | `responsibility_link` | As today |
| **Links tab:** rows, filters, what nobody links | View (+ Monitoring or Locations: View) | – | **`responsibility_links_list`** (new) |
| **Links tab:** link, unlink, move or copy many | Edit | – | **`responsibility_links_change`** (new) |
| Redmine combinations | Edit | `responsibility_redmine` | As today |
| **History** | View | – | **`responsibility_history`** (new) |
| *Restore* a description | Edit | – | `_update` with `restoreFrom` |
| **Overview notes** | View, Edit | – (extensions plan, decision 9) | **`responsibility_notes`, `responsibility_note`, `responsibility_note_delete`** (new; decision 8) |
| Overview layout: positions, *Arrange* | Edit | – | **Stays in the web UI** (decision 7) |
| Delete a responsibility | Manage | – | **Stays out:** no *Manage* in the ceiling |
| The global responsible and deputy | View reads, Manage sets | Read in `_list`, `_get` | Read as today; **setting stays out** |

### 6.2 The extended tools

| Tool | New arguments | New in the answer |
|---|---|---|
| **`responsibility_options`** | – | `palette`: the 12 swatches as `{ name, hex, usedBy }`. `suggestedColor`: the first unused one, as the page's *New responsibility* dialog suggests it (`firstUnused`) |
| **`responsibility_create`** | `color` (§6.3; left out: none), `description` (Markdown; credentials are refused) | `color`, `colorName` |
| **`responsibility_update`** | `color` (an empty text clears it); `restoreFrom`, a history row's id from `responsibility_history`, which puts that description back as *Restore* does. It can't be combined with `description` | `color`, `colorName` |
| **`responsibilities_list`** | `attention: true`, only those that need attention, as the page's filter | Per item: `color`, `colorName`, `attention`, `escalation`, `updatedAt` |
| **`responsibility_get`** | – | `color`, `colorName`, `attention`, `escalation`, `createdAt`, `createdByName`, `updatedByName`, `files` (name, kind, size; no URL, since a token can't fetch them) |

- **A colour alone** goes through `SetColorAsync`, as the map's side panel does, so only one history row is written (*none → #2f8fd8*). With other fields, it goes into `UpdateAsync`'s `Color`, replacing `Color = current.Color` (l.126).
- **The history says *via MCP*** for the colour as for everything else (mcp-extensions-plan §8.2). The service already takes it from the scope.
- **`escalation`** is `[{ step: 1, role: "deputy", name: "Ben Keller" }, …]`. Names are masked as everywhere else for Cookie (`ctx.Privacy.Mask`), and the list is never contact details.

### 6.3 Colours as hex

- **Accepted:**
  - `#2f8fd8`, `#2F8FD8` or `2f8fd8`;
  - the tool adds the `#`, and the service lower-cases it (`Colours.Normalize`) and checks it (`Colours.IsHex`).
- **Refused:**
  - anything else, including names such as *blue* and the short `#28d`;
  - the answer is `invalid_argument`: *color: a colour as #rrggbb, such as #2f8fd8. responsibility_options lists the palette.*
- **Always answered as `#rrggbb`,** in lower case. `colorName` gives the swatch's name (*Blue*), or null for a custom colour, so an AI can talk about it.
- **Left out on create, it is none.** The tool takes no initiative; the description points to `suggestedColor`.
- **The palette moves to the server too:**
  - `Colours.Palette` in `Colours.cs` gets the 12 names and values;
  - a test reads `PNMTD.WebUI/src/components/colours.ts` and checks that both lists agree, as `BoardsTests` reads `registry.ts` (l.418);
  - the web UI keeps its own copy, because it needs it without a request.

### 6.4 The new tools

| Tool | Kind, right | What it does | Events |
|---|---|---|---|
| **`responsibility_history`** | Read, View | A responsibility's history, newest first, paged: when, by whom, what (`Kind` as its label, e.g. *Colour changed*), `detail`, and *via MCP*. With `rowId`, one row with the description before and after (untrusted, cut at 20,000 characters) | – |
| **`responsibility_links_list`** | Read, View | The Links tab. **`view`:** `links` (the default), `unlinkedHosts` or `unlinkedLocations`. **Filters:** `responsibilityIds`, `personId`, `kinds`, `locationId` with `below` (default true), `query`, and for the unlinked views `covered`. **Rows:** link id, responsibility (id, name, colour), kind, object id, name, *where*. Paged as `LinkRowsAsync` and `UnlinkedAsync` page | – |
| **`responsibility_links_change`** | Write, Edit | **`action`:** `link` (a responsibility, with pages by number, locations and hosts), `unlink` (link ids), `move` (link ids to another responsibility; with `keep: true` they are copied, as *Also link to…*). **At most 500**, as `MaxBulk`. The answer: linked, unlinked, and the skipped ones with their reason | `ResponsibilityLinked`, `ResponsibilityUnlinked` |
| **`responsibility_notes`** | Read, View | The Overview's notes: id, text (untrusted, 2,000 characters), colour, width, place, who wrote and changed them | – |
| **`responsibility_note`** | Write, Edit | `create` (text, colour, width; `x` and `y` optional) or `update` (by `noteId`; what is left out stays). At most 500 notes (`MaxNotes`) | `ResponsibilityNoteCreated`, `ResponsibilityNoteChanged` |
| **`responsibility_note_delete`** | Delete, Edit | A note, inside the deleting window (mcp-plan §7.5). The audit keeps its text | `ResponsibilityNoteDeleted` |

**The rules each new tool follows:**

- **The unlinked views need more rights,** as the endpoints do:
  - *Monitoring: View* for `unlinkedHosts`, *Locations: View* for `unlinkedLocations`;
  - they are checked inside the tool with `ctx.Rights.Has`, as `sensor_create` checks *Zabbix: View* (`MonitoringWriteTools.cs` l.334);
  - without the right, the answer is `missing_right`, naming the right, as there.
- **Pages only as machines may read them** (`MachineAccess.Pages`):
  - in the Links list, a sensitive page isn't named; it is counted (*2 linked pages are not shown*);
  - in `links_change`, a page is given by number and resolved the same way, as `responsibility_link` does it.
- **A note's place, when left out:**
  - to the right of everything saved on the map (saved responsibilities and notes), at the top, 80 units clear;
  - the server doesn't know the automatic places, but the automatic layout keeps clear of notes (extensions plan §6.3).
- **Credentials:** a note's text is checked as on the page (422 `looks_like_credential`), and **is never acknowledged over MCP**, as a description isn't.
- **Untrusted text:** names, notes and the history's texts go through `ctx.Untrusted`, as `responsibility_get` already treats them.
- **The read-only token** gets the three reading tools, `responsibility_history`, `responsibility_links_list` and `responsibility_notes`, within its *View* cap.
- **Cookie gets none** of them (`ForCookie` stays off). Its prompt doesn't grow, and it keeps its 17 tools.
- **The tools call the same services as the endpoints:** `ResponsibilityService` (`.Links.cs`), `ResponsibilityQueries` (`.Links.cs`) and `ResponsibilityMapService`. So the limits, the history and the audit are the same.

**The count:** 125 → **131 tools**, 5 of them extended.

---

## 7. Rights and audit

- **No new endpoint and no new right.** `EndpointAuditTests` is unchanged.
- **The tools' rights** are the endpoints' (§6.4), and the catalog's rules apply: a right, a kind, the events, and no *Manage* (`McpToolsTests`).
- **The audit:**
  - every tool's entry is its endpoint's event, with `ctx.Target` naming the responsibility or the note;
  - the bulk tool writes one entry per responsibility, as `RecordLinks` does for the page.
- **The responsibility's history:**
  - the colour, links, description and restore write their rows as on the page, with `Via = mcp`;
  - notes, as before, are in the audit log only.
- ***Arrange*** saves through `PUT map/positions` (`ResponsibilityMapSaved`), as a drag does.

---

## 8. Data model and migrations

**No migration.** The changes are:

- `ResponsibilityStep` and `ResponsibilityRole`;
- `Escalation` on three contracts;
- `Colours.Palette`.

So `dotnet ef migrations has-pending-model-changes` stays green, and the upgrade check has nothing new.

---

## 9. Compatibility: what must not break

| What | Why it holds | Checked by |
|---|---|---|
| **The network map** | `layoutGraph`'s new `starts` parameter is optional, and the network map doesn't pass it | Its unit tests, the browser tests in `network-map.spec.ts` |
| **The Overview as in 1.26:** layers, *Find*, focus, the saved layout, notes, the moves bar, the question before leaving | Only additions. `buildGraph` keeps its output for the people's nodes | `overviewLayout.test.ts`, `responsibilities.spec.ts` |
| **The picture changes on purpose:** fewer lines by default, deputies off the map | Decided (§4). `?lines=both` comes close to 1.26 | Release notes |
| **People may sit elsewhere after the update** | Free nodes start near their neighbours (§5.4). Responsibilities and notes don't move | Release notes |
| **`GlobalStandsIn`, *G*, *Global responsible stands in*** | Their meaning is unchanged (§3.1) | `ResponsibilitiesApiTests` |
| **MCP clients of 1.26** | Every existing argument keeps its meaning. `responsibility_update` without `color` keeps the colour, as today | `McpExtensionsTests` |
| **Cookie** | 17 tools. Its two answers get one more field (`escalation`) and nothing else | `McpToolsTests` ("Cookie keeps its seventeen"), `ResponsibilityToolsTests` |
| **The API** | Contracts only grow | `openapi.json`, the contract group of `run-tests.sh` |

---

## 10. Testing

| Area | Tests |
|---|---|
| **The escalation order** (`EscalationOrderTests`, pure) | Every row of §3.1's table. A person counted once. Inactive and unset people skipped. `GlobalStandsIn` equals *the first is global* for all 81 combinations of set, unset and inactive across the four places |
| **The contracts** (`ResponsibilitiesApiTests`, `ResponsibilityMapTests`) | `escalation` on the list, the detail and the map nodes. Deactivating a user changes it on the next read |
| **MCP colour** (`McpExtensionsTests`, or a new `McpResponsibilitiesTests`) | `#2F8FD8`, `2f8fd8` and `#2f8fd8` are stored as `#2f8fd8`. `blue` and `#28d` give `invalid_argument`. An empty text clears it. `_update` without `color` keeps it. A colour alone writes one history row *via MCP*. `_create` with a description. `restoreFrom`, alone and together with `description` (refused). `_options` lists the palette and `suggestedColor` |
| **The palette** | `Colours.Palette` equals `colours.ts`'s `palette` (the file is read, as `BoardsTests` does) |
| **The new tools** | The history, paged, and one row's texts. The Links list with each filter and view. `unlinkedHosts` without *Monitoring: View* gives `missing_right`. A sensitive page isn't named. The bulk change: the limit, the skipped ones, `move` with and without `keep`. Notes: create, update, delete (only inside the window), credentials refused, the limit. The audit events and targets |
| **The catalog** (`McpToolsTests`) | 131 tools. The read-only token gets the 3 new reading tools. Cookie keeps 17. Every new tool has events, a right and no *Manage* |
| **Web** (Vitest) | `buildGraph` for each choice of lines over §3.1's table: the right people, the right styles, nobody without a line. `linesFromQuery` and `linesToQuery`. `arrange.ts`: for n = 1 to 120 and each form, no two cells overlap and the same input gives the same output; the circle's order; clusters don't overlap and *nobody* comes last; the form moves off notes. `layoutGraph` with `starts`: a node starts there; without it, the result is identical to 1.26's |
| **Browser** (`responsibilities.spec.ts`) | By default only responsible lines, and *Both* adds the deputies. The side panel shows the escalation order. *Arrange → Circle* shows the bar; *Save layout*, a reload, and the places are the same. *Discard* puts them back |

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| ***Escalation* sounds like something is sent** (Cookie has escalations, alerts escalate) | The page, the panel and the docs say *Nothing is sent*. The code says `EscalationOrder`. Cookie's `Duties` stay untouched |
| **By default, someone who is only a deputy is off the map** | The legend names the view. *Both* is one click away. The side panel lists the whole order |
| ***Arrange* undoes a hand-made layout** | Nothing is saved until *Save layout*. *Discard* restores it. It asks before replacing unsaved moves |
| **One long name makes the grid and the circle wide** | Names are cut at 28 characters, as the map draws them, and the cell is at most 200 wide |
| **Six more tools cost every MCP client context** | Short descriptions, as in mcp-extensions-plan. The read-only token gets only three |
| **An AI picks clashing colours** | `palette` and `suggestedColor` in `responsibility_options` |
| **A bulk change over MCP touches many links** | At most 500, as on the page. Every change is in the audit and in each responsibility's history, *via MCP* |

---

## 12. Decisions (review of revision 1, 2026-11-26)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Who is in the escalation order? | **As recommended:** only people who are set and active, each once. An inactive person is named on the side panel as *skipped*, and not drawn | §3.1 |
| 2 | What do *Deputy* and *Both* draw? | **"One simple queue, one after another":** responsible, deputy, global responsible, global deputy. Whoever is missing is skipped and the next moves up. *Responsible* draws the first in the queue, *Deputy* the second, *Both* the first two. The alternative, only the people actually named, is gone | §3.1, §4.1 |
| 3 | The default, and where the choice is kept | **Responsible** by default, as asked. **Kept in the page's address,** e.g. `…/overview?lines=both`, as the layers already are (asked "what URL?"; explained in §4.1). So a reload, *Back* and a shared link keep it. Nothing is stored for the person | §4.1 |
| 4 | Which forms? | **As recommended:** *Circle* (with rings above 24), *Around their responsible*, *Grid*. Possible later: *Grid by colour*, *A row per person* | §5.2 |
| 5 | What does *Arrange* move? | **As recommended:** every responsibility; notes stay | §5.1 |
| 6 | Who may arrange? | **As recommended:** everyone, as everyone may drag; saving needs *Edit* | §5.1 |
| 7 | The map's layout over MCP? | **As recommended: no.** Where something sits is for a person looking at it | §6.1 |
| 8 | Notes over MCP? | **Yes:** read, write, and delete inside the deleting window. This reverses the extensions plan's decision 9 for MCP; Cookie still doesn't read them | §6.4 |
| 9 | Delete and the global people over MCP? | **As recommended: no.** Both are *Manage*, and the ceiling never holds *Manage* (mcp-plan §5.3) | §6.1 |
| 10 | Colour: hex only? | **As recommended: yes.** `#rrggbb`, with or without `#` and in any case. Left out on create, none. Names are only in the answer (`colorName`) | §6.3 |
| 11 | Cookie and the escalation order | **As recommended:** Cookie gets it, since "whom do I ask?" is its question. No colour, no new tools | §6.2 |
| 12 | The version | **As recommended: 1.27.0** (features, no migration) | – |

No questions are open.

---

## 13. What this reverses

| Where | Said | Now |
|---|---|---|
| responsibilities-extensions-plan §8.4 and decision 9 | *"No colour, map or notes reach the tools"* | Colour and notes reach MCP (§6). The map's layout still doesn't. Cookie still reads neither |
| responsibilities-extensions-plan §6.1 | Lines to every named person, and from the global people only when they stand in | Lines follow the escalation order and the chosen view (§4). By default only the first person in each order |
| responsibilities-extensions-plan §6.3 | Free nodes start on a spiral | They start near their fixed neighbours (§5.4) |

---

## 14. Phases

| Phase | What | Done when |
|---|---|---|
| **P1** | **The escalation order:** `EscalationOrder.Of`, the contracts, the page's line, the side panels | API and Vitest tests are green; the page shows it |
| **P2** | **The lines:** `buildGraph(…, lines)`, the SelectButton, `?lines=`, the legend, the person panel | `overviewLayout.test.ts`; the browser test for the default and *Both* |
| **P3** | ***Arrange*:** `arrange.ts`, the menu, the anchors, `layoutGraph`'s `starts` | Vitest for the forms; the network map's tests unchanged; the browser test arranges, saves and reloads |
| **P4** | **MCP fields:** colour, `Colours.Palette` and its test, `_options`, `_create` (description), `_update` (`restoreFrom`), `_list` (`attention`), `_get`; the escalation order for Cookie | `McpExtensionsTests`, `McpToolsTests` |
| **P5** | **MCP tools:** history, the Links list and bulk change, notes | 131 tools; the tests in §10 |
| **P6** | **Documentation:** `ResponsibilitiesDocs.vue` (the escalation order, the lines, *Arrange*, the MCP part rewritten), README, `docs/release-notes-1.27.md`, this plan's *As built* | `run-tests.sh` green in both time zones; screenshots of the map in each view and form |

---

## 15. As built

**Built: P1–P6, on master (2026-11-26).** For 1.27.0; `docs/release-notes-1.27.md` is written, the version isn't bumped.

### Where the build differs from the plan

1. **Rings are fitted by checking the cells, not by the chord formula alone** (§5.2). `w / (2·sin(π/n))` keeps neighbours a cell's width
   apart, but a cell is wider than it is tall, so diagonal neighbours of short names still overlapped. `fitRing` starts at the formula and
   grows the radius in steps of 2 until no two cells overlap (`cellsOverlap`), at the latest where every pair is a cell's diagonal apart.
   Rings around each other aren't 1.5 cell heights apart either: each is the smallest that clears the ones inside it, holding as many as
   fit a diagonal apart.
2. **A cluster's ring is at least 150, not 110** (§5.2). The layout keeps a free person 137 from a responsibility (39 + 50 + the gap of
   48), so a smaller ring pushed the person out of its middle. A lone responsibility sits 150 to the right of its middle.
3. **A started node is pulled towards its start, not to the middle of everything** (§5.4). With the pull to the overall centre, a person
   in a cluster far from the middle ended up hundreds of units off it. `layoutGraph`'s bodies with a start (`hx`, `hy`) are pulled
   there; those without one, as every node on the network map, as before.
4. **A cell is never 200 wide:** names are cut at 28 characters, about 185 units, so the cap in §5.2 never binds.
5. **A note deleted over MCP is recorded as it was,** field by field (`AsItWas`), as every delete over MCP is. The entity's removal
   alone records only *deleted*.
6. **Links to sensitive pages are no links for `_links_change` either:** given by id to `unlink` or `move`, they are left out, and with
   nothing left the answer is `not_found`.
7. **No test pins 131 tools** (§10). `McpResponsibilitiesTests` checks the six new ones by name, kind and token, so later tools don't
   touch it.
8. ***Arrange* is a labelled button with its own menu,** not in the ⋯ *Layout* menu, since everyone may arrange (decision 6). Its toast
   is *Arranged 3 responsibilities as a circle* with *Not saved yet: Save layout keeps it.*, or without *Edit* *Only for you, until you
   leave the page.*
9. **The page's escalation line also names who was skipped,** as the side panel does.
10. **The history tool's rows say `restorable`** (a description change) and `viaMcp`.
11. **The notes browser test selects its note from the keyboard.** With fewer lines by default, the automatic layout changed and the
    note's fixed drag landed it under the Network circle, which a click then hit.
12. **The MCP history test advances its frozen clock a second** between the two description changes: with one timestamp for both,
    "newest first" tied and New York's run read the other row.

### P1 and P2: the escalation order and the lines (d6d5a3b)

- `PNMTD/Responsibilities/EscalationOrder.cs`; `ResponsibilityRole`, `ResponsibilityStep` and `Escalation` on `ResponsibilityItem`,
  `ResponsibilityDetail` and `ResponsibilityMapNode`.
- The page's line (`ResponsibilityView.vue`), the side panel's list and a person's places (`OverviewPanel.vue`), `escalationLabel`,
  `skippedPeople`, `ordinal`, `roleLabel` (`api/responsibilities.ts`).
- `buildGraph(map, layers, lines)` with `linedSteps`, `?lines=` (`overviewState.ts`), the SelectButton, the legend's line.
- `EscalationOrderTests` (the table, a person once, the 81 combinations against `StandsIn`, the list, page and map).

### P3: *Arrange* (8a0124f)

- `overview/arrange.ts` (`cellSize`, `fitRing`, `circle`, `clusters`, `grid`, `place`, `cellsOverlap`) with its tests; the menu and the
  anchors in `OverviewTab.vue`, `anchors` in `OverviewCanvas.vue`; `layoutOverview`'s anchors and starts; `layoutGraph`'s `starts`.
- `e2e/responsibilities.spec.ts`: the escalation order on the page and panel, the three views of lines, *Arrange → Circle* and
  *Discard*, *Around their responsible* saved and the same after a reload.

### P4 and P5: MCP (f72a889)

- `Colours.Palette`, `NameOf`, `FirstUnused`; `ResponsibilityWriteTools` (`color`, `description` on create, `restoreFrom`,
  `responsibility_links_change`, `responsibility_note`), `ResponsibilityReadTools` (`attention`, the new fields, `responsibility_history`,
  `responsibility_links_list`, `responsibility_notes`), `DeleteTools.responsibility_note_delete`.
- `McpResponsibilitiesTests` (over MCP with real tokens), `ResponsibilityToolsTests` (sensitive pages in the Links list and in
  `_links_change`, the unlinked views' rights, the palette against `colours.ts`).

### P6: the finish

`ResponsibilitiesDocs.vue`, the README, `docs/release-notes-1.27.md`, this section.

