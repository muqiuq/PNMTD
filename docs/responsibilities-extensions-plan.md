# Plan: Responsibilities: a Links tab, a sortable list, a colour each, and an Overview map with notes

**Status:** Revision 2 (2026-11-24): revision 1's questions are answered, in §14 *Decisions*. Nothing is built. It extends [responsibilities-plan.md](responsibilities-plan.md) (built, released in 1.21.0). Its decisions stay: responsibilities are information only (its decision 5), and nothing is inherited (its decision 6). Planned for 1.25.0. Below, *decision N* means this plan's §14, and *first plan, decision N* means responsibilities-plan.md §18.

| | |
|---|---|
| **Goal** | Four additions to *Admin → Responsibilities*:<ol><li>**A *Links* tab:** every link between a responsibility and a page, location or host, in one table. It has filters, views of what no responsibility covers, and changes to many links at once (§5).</li><li>**A sortable list:** the table sorts by its columns (§3).</li><li>**A colour for each responsibility,** shown wherever it is named (§4).</li><li>**An *Overview* tab:** the responsibilities on a map, drawn like the network map. They are arranged by hand and saved for everyone, and notes can be put on the map (§6, §7).</li></ol> |
| **Not in scope** | <ul><li>An order set by hand for everyone (decision 1): the list only sorts by its columns.</li><li>Inheritance: a host in a linked location still isn't linked itself (first plan, decision 6). §5.1 adds a filter that treats it as covered, and that's all.</li><li>A layout per person, or several named layouts.</li><li>Saved positions for people and linked objects (decision 7).</li><li>Notes over MCP and for Cookie (decision 9).</li><li>Images or HTML in notes.</li><li>Redmine combinations on the Links tab: they have their own tab.</li><li>Sending anything to anyone.</li></ul> |
| **Rights** | No new area or level. **Responsibilities: View** reads all of it. **Manage** sets colours, changes links, saves the layout and writes notes (§8) |

---

## 1. Summary

| Part | What it is |
|---|---|
| **Tabs** | `Responsibilities · Links · Overview · Redmine`. The list stays the default (decision 2) |
| **Sorting** | A click on a column's header sorts the list, in the browser. The sort is kept in the URL. Every other list stays sorted by name |
| **Colour** | `responsibilities.Color`, `#rrggbb` or none. 12 swatches or a custom colour, on the details form and the map's side panel. It shows as a dot next to the name, and as the fill on the map |
| **Links tab** | One row per link, paged on the server. It filters by words, responsibility, person, kind and location. It can also show the *hosts* or *locations without a responsibility*. Rows can be linked, unlinked, moved or copied to another responsibility, many at once |
| **Overview** | An SVG map on the network map's shared parts. It shows responsibilities and, as layers, their people, the objects they link, and notes. *Save layout* and *Discard* work as on the network map. Only responsibilities and notes keep a saved position |
| **Notes** | Short Markdown, as a board's note. Each has a colour, a width and a place on the map. They are checked for credentials and recorded in the audit log |

---

## 2. Today (checked in the code)

- **The page** ([ResponsibilitiesView.vue](../PNMTD.WebUI/src/views/admin/responsibilities/ResponsibilitiesView.vue)) has two tabs:
  - *Responsibilities*, the list;
  - *Redmine*, only while Redmine is configured.

  The route is `/admin/responsibilities/:tab?`.
- **Links** are made and removed one at a time, on a responsibility's page:
  - the page uses the `LinkPicker` with `targets`, and `POST`/`DELETE {id}/links`;
  - a responsibility links at most `MaxLinks = 200` objects;
  - no page lists the links of all responsibilities, and no page shows a host that nobody owns.
- **Order:** every list is sorted by name:
  - `ResponsibilityQueries.ListAsync` sorts by `NormalizedName`;
  - so do `LinkedAsync` (the panels on hosts, locations and pages) and `responsibilities_list` (which reads `ListAsync`);
  - the list's DataTable has no sorting of its own;
  - the list isn't paged: `GET /responsibilities` returns every responsibility at once, with `updatedAt`.
- **Colours elsewhere:**
  - the availability plan's types (`#rrggbb`, checked by a `[GeneratedRegex("^#[0-9a-f]{6}$")]` in `AvailabilityService`);
  - the users' plan colour (`UserEntity.Color`) and short code (`ShortCode`);
  - the field and the contrast helper live under the availability plan: [ColorField.vue](../PNMTD.WebUI/src/views/admin/availability/ColorField.vue) is the browser's picker plus the hex text, and `textOn()` is in `views/admin/availability/plan.ts`.
- **Maps:**
  - The **network map** ([MapCanvas.vue](../PNMTD.WebUI/src/views/network/MapCanvas.vue)) is Vue-rendered SVG. Routers are dragged and saved for everyone with *Save positions* and *Discard* ([network-map-positions-plan.md](network-map-positions-plan.md)).
  - The **knowledge map** (`KnowledgeMapCanvas.vue`) can't be dragged.
  - **Shared by both:**
    - `components/graph/forceLayout.ts`: a force layout in which saved points stay fixed, and which, with every group null, is a plain force layout;
    - `geometry.ts`, `focus.ts` and `composables/usePanZoom.ts`;
    - the libraries `d3-force` and `d3-hierarchy`, and no others.
  - **Not shared:** the moving code (drag, arrow keys, Escape, the `moved` map, dragging a group by its name) sits inside `MapCanvas.vue`.
- **Filtered tables:** the network map's *Connections* tab already has the parts the Links tab needs: `FilterBar`, `LocationPicker` and `useServerTable` (paged on the server, with the page, sort and search in the URL).
- **Notes elsewhere:**
  - a board's *note* element is rendered by `src/boards/boardMarkdown.ts`: GFM, sanitised, without images or HTML;
  - the name `CookieObjectNoteEntity` is taken (Cookie's notes about objects), so the new entity is `ResponsibilityMapNoteEntity`.
- **Audit:** `[Audit(...)]` takes several events, so one endpoint can write both `ResponsibilityLinked` and `ResponsibilityUnlinked`.

---

## 3. Sorting the list (decision 1)

- **A click on a column's header sorts the list.**
  - A second click sorts the other way, and a third goes back to the default: by name, as today (PrimeVue's `removable-sort`).
  - The arrow in the header shows the sort, and the header carries `aria-sort`.
- **The columns that sort:**

  | Column | Sorts by |
  |---|---|
  | *Name* | The name |
  | *Responsible* | The name shown in the cell. Rows where the global responsible stands in sort by the global responsible's name. Rows with nobody come last, in both directions |
  | *Deputy* | The deputy's name; rows with nobody come last |
  | *Links* | How many links |
  | *Redmine* | How many combinations (only while Redmine is configured) |
  | *Changed* (new) | When it was last changed, shown as "2 d ago", with the date and the person on hover |

  - *Short description* doesn't sort: it is free text.
  - Names compare as people read them: case-insensitive, with numbers as numbers, so *Backup 2* comes before *Backup 10* (`Intl.Collator`, `numeric`, `sensitivity: 'base'`).
  - On a tie, rows sort by name.
- **In the browser:** the list isn't paged, so the server, the API and the database stay as they are. The search and *Needs attention* keep the sort.
- **Kept in the URL** (`?sort=responsible`, `?sort=-changed`), as `useServerTable` keeps it for paged tables. Coming back from a responsibility's page, reloading or sharing a link shows the same sort. Without `sort`, the list is by name.
- **Only this table:**
  - the panels on hosts, locations and pages, the Links tab's filter, the Overview's side list and `responsibilities_list` stay sorted by name;
  - the Links tab sorts by its own columns, on the server, since it is paged (§5.1).
- **No order is set by hand,** for anyone. There is no migration, endpoint or audit event for sorting.

---

## 4. A colour

### 4.1 For the person

- **On a responsibility's page,** *Details* gets a *Colour* field, saved with the form. It offers:
  - 12 swatches (§4.3);
  - *Custom*: the availability plan's `ColorField`, which is the browser's picker and the `#rrggbb` text;
  - *None*.
- **The *New responsibility* dialog** suggests the first swatch that no responsibility uses yet, and it can be changed or cleared (decision 3).
- **On the Overview's side panel,** the same field saves at once (§6.4).
- **Where it shows:**
  - a dot before the name in the list, on the Links tab, on the *Redmine* tab, in the panels on hosts, locations and pages, and in the page's title;
  - on the Overview, the node's fill.

  Without a colour, the dot is an empty ring and the node has a neutral fill.
- **Never colour alone** (WCAG 1.4.1): the name always stands next to it.
- **Readable in both themes:**
  - every dot and node has an outline in the border colour, so a light colour on white, or a dark one on the dark theme, still has an edge;
  - text on a colour is black or white, whichever reads better (`textOn`).
- **Colours needn't be unique:** two responsibilities may share one.

### 4.2 Data and API

- **`responsibilities.Color`**: varchar(7), null meaning none. Stored lower-case.
  - Audited field by field, as the other fields are.
  - A new history kind, `ColorChanged = 10` ("Colour: none → #2f8fd8"), appended to `ResponsibilityHistoryKind`.
- **`ResponsibilityRequest`** (create and update, the whole form as today) gets `color`. A wrong value gets 400 "A colour as #rrggbb.".
  - The regex moves from `AvailabilityService` to a shared `Colours` helper, which both use.
  - Only the web UI sends this request, and it always sends the colour.
- **`PUT /api/v1/responsibilities/{id}/color { color }`** is for the Overview's side panel: it changes the colour alone, without resending the form.
  - Needs *Manage*; audited as `ResponsibilityChanged`.
  - It writes the same history row.
- **`color` in the contracts:**
  - `ResponsibilityItem`, `ResponsibilityDetail` and `ResponsibilityRef` (the panels);
  - the Links tab's rows and the map's nodes.
- **MCP and Cookie don't get it:** a colour tells an agent nothing.

### 4.3 The palette

There are twelve swatches, chosen to read on both themes: red `#d94f4f`, orange `#e8833a`, amber `#d9a400`, lime `#7cb342`, green `#2e9d5b`, teal `#1fa3a3`, blue `#2f8fd8`, indigo `#4f5bd5`, violet `#8e5cd9`, pink `#d0559b`, brown `#8d6e63` and slate `#78909c`.

- **They live in `components/colours.ts`.** The availability plan's `ColorField` and `textOn` move to `components/`, and the plan's settings import them from there.
- **A vitest checks** that `textOn` gives each swatch a contrast of at least 4.5:1.

---

## 5. The *Links* tab

### 5.1 For the person

```
┌ Responsibilities · Links · Overview · Redmine ─────────────────────────────────────────────────┐
│ Show [Links ▾]  🔍 Object, location, responsibility…  [Responsibility ▾] [Person ▾]            │
│ [Location ▾] ☑ and below   Kind  ☑ Pages ☑ Locations ☑ Hosts                      [+ Link…]    │
├──┬────────────────┬────┬──────────────────────────┬───────────────────────────┬─────────────┬──┤
│☐ │ Responsibility │    │ Object                   │ Where                     │ Linked      │  │
│☐ │ ● Network      │ 🖥 │ sw-core-01               │ Zurich › Server room      │ anna · 3 d  │✕ │
│☐ │ ● Network      │ 📍 │ Server room              │ Zurich                    │ anna · 3 d  │✕ │
│☐ │ ● Printers     │ 📄 │ KB-42 Printer setup      │ archived                  │ ben · 1 w   │✕ │
└──┴────────────────┴────┴──────────────────────────┴───────────────────────────┴─────────────┴──┘
 2 selected   [Move to… ▾]  [Also link to… ▾]  [Unlink]                          1–50 of 212  ‹ ›
```

**Three views,** chosen with *Show*:

1. **Links** (the default): every link of every responsibility, one row each.
2. **Hosts without a responsibility:** the hosts that no responsibility links itself.
3. **Locations without a responsibility:** the same, for locations.

In 2 and 3, a column *Through its location* names the responsibilities that link the host's location or one of its parents, since nothing is inherited (first plan, decision 6). The checkbox *Count a linked location as covering what is in it* hides those rows. It only filters this view; the panels and the tools stay as they are.

**Filters:**

| Filter | In the view | Keeps |
|---|---|---|
| **Search** | all | Words in the object's name and its *Where* (location path, page number). In *Links*, also the responsibility's name |
| **Responsibility** | Links | One or more, by name, with their colours |
| **Person** | Links | The links of the responsibilities where this user is the responsible or the deputy ("what does Anna own?") |
| **Location** | all | The `LocationPicker`; *and below* includes its sub-locations. It keeps the hosts in those locations and the locations themselves. Pages have no location, so a location filter hides them |
| **Kind** | Links | Pages, locations, hosts |

- **Sorting:** a click on the header of *Responsibility*, *Object*, *Kind* or *Linked*, as in the list (§3), but on the server, since the tab is paged. By default it sorts by responsibility, then kind, then object, each by name.
- **The filters and the page are in the URL,** as on the *Connections* tab, so a filtered view can be shared or bookmarked.
- **A responsibility's page** gets *Show in the Links tab* under *Linked*. It opens the Links tab filtered to that responsibility.
- **Read-only with *View*:** the same tables, without the checkboxes and buttons.

**Changes, with *Manage*:**

- **Link…** opens a dialog:
  - a responsibility (by name, with colours);
  - one or more objects, picked with the `LinkPicker` as chips.

  It answers "Linked 5 to Network", and says which were skipped because they were linked already.
- **In *Hosts / Locations without a responsibility*:** select rows, then *Link to… [responsibility]*. This is the quick way to cover what nobody owns.
- **Unlink:**
  - ✕ on a row unlinks at once, as on the responsibility's page;
  - *Unlink* for the selected rows asks first: "Unlink 12 links from 3 responsibilities?".
- **Move to… [responsibility]:**
  - the selected links leave their responsibilities and are linked to the one chosen;
  - an object that the target links already is only removed from its source;
  - all of it in one transaction.
- **Also link to… [responsibility]:** the same objects are linked to another responsibility as well, and their links stay.
- **The limit stays 200 per responsibility,** and it counts before anything is written. If the selection doesn't fit, nothing is linked, and the message reads "Network links 195; 5 more fit".
- **The selection:**
  - it holds rows across pages until a filter changes;
  - an action takes at most 500 rows.

### 5.2 API

| Method | Path | Right | Audit | |
|---|---|---|---|---|
| GET | `links?q&responsibility*&person&kind*&location&below&sort&page&size` | View | | `Paged<ResponsibilityLinkRow>`: `linkId`, `responsibility {id, name, color}`, `kind`, `id`, `name`, `detail`, `number`, `archived`, `createdAt`, `createdByName` |
| GET | `unlinked?kind=Host\|Location&q&location&below&covered&page&size` | View, and Monitoring: View for hosts or Locations: View for locations; or Manage | | `Paged<ResponsibilityUnlinkedRow>`: `kind`, `id`, `name`, `detail`, `throughLocation [{id, name, color}]` |
| POST | `links` | Manage | `ResponsibilityLinked` | `{ responsibilityId, targets: [{kind, id}] }`, 1–500. Answers `{ linked, skipped: [{kind, id, reason}] }`. 409 `too_many_links` |
| POST | `links/unlink` | Manage | `ResponsibilityUnlinked` | `{ linkIds }`, 1–500. Answers `{ unlinked }` |
| POST | `links/move` | Manage | `ResponsibilityLinked`, `ResponsibilityUnlinked` | `{ linkIds, responsibilityId, keep }`. With `keep`, this is *Also link to…* |

- **The single `POST`/`DELETE {id}/links` stay** for the responsibility's page.
- **Audit:**
  - one entry per responsibility a request touches, with every object as a subject, so each object's own *History* shows it;
  - the summary reads "Network: linked sw-core-01, sw-core-02 and 10 more".
- **History:** one row per link, as now (`LinkAdded`, `LinkRemoved`). A move writes a `LinkRemoved` on the source and a `LinkAdded` on the target.
- **The rights of `unlinked`:** it lists objects that no responsibility names, so it needs the right to list those objects, or *Manage*. *Manage* already names them in the link picker.
- **In SQL:**
  - a union of the three kinds with their names, filtered, counted and paged in the database;
  - the *Where* text (location paths from `LocationTree`, page numbers) is computed only for the page shown;
  - the location filter becomes a set of ids from `LocationTree`.

---

## 6. The *Overview* tab: the map

### 6.1 For the person

```
┌ Responsibilities · Links · Overview · Redmine ──────────────────────────────────────────────────┐
│ Show ☑ People ☐ Hosts ☐ Locations ☐ Pages ☑ Notes   🔍 Find…        [Fit] [Layout ▾] [+ Note]  │
├──────────────────────────────────────────────────────────────────────────┬──────────────────────┤
│ ┌ Global ─────────────┐                                                  │ ● Network            │
│ │ Anna Muster (AM)    │      ┌─────────────────┐                         │ Switches, routers,   │
│ │ deputy Ben Keller   │      │ Infra: ask Anna │      (AM)               │ the firewall         │
│ └─────────────────────┘      │ before changes  │     ╱    ╲              │ Responsible Anna M.  │
│                              └─────────────────┘ (●Network) (●Backup)    │ Deputy      Ben K.   │
│                                                     ╎        ╱           │ 14 links · 2 Redmine │
│         (●Printers)──(CR)                          (BK)─────╯            │ Colour [● ▾]         │
│                                                                          │ [Open]  [Automatic]  │
├──────────────────────────────────────────────────────────────────────────┴──────────────────────┤
│ 3 moved · [Discard] [Save layout]                                                               │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**What is drawn:**

| Item | Shape | Lines |
|---|---|---|
| **Responsibility** | A circle in its colour, with its name below. ⚠ inside when it needs attention; a small *G* when the global responsible stands in. Its summary shows on hover | |
| **Person** (layer *People*, on by default) | A smaller circle with their short code from the availability plan (else their initials), in their plan colour if they have one, with their name below. Greyed and marked *inactive* when deactivated | Solid to each responsibility they are responsible for, dashed to each where they are deputy |
| **The global responsible and deputy** | A card in the corner, always, outside the pan and zoom. With *People*, they are person nodes as well | Dotted to each responsibility they stand in for |
| **Linked object** (layers *Hosts*, *Locations*, *Pages*, off by default) | The knowledge map's shapes, small: pages rounded squares, locations and hosts circles, in a neutral colour (no monitoring state, so no other right is needed) | Thin and grey to each responsibility that links it. An object that two responsibilities link sits between them, which shows the overlaps |
| **Note** (layer *Notes*, on by default) | A rectangle in its note colour, with its text (§7) | |

- **At most 600 linked objects are drawn.** Above that, a notice reads "600 of 2,140 linked objects shown. Choose fewer layers, or filter with Find.", as the knowledge map does.
- **Pan, zoom and *Fit*** work as on the network map. Hovering a node fades what isn't connected to it.
- **A click** selects a node and opens the side panel. A double-click or *Enter* opens a responsibility's page.
- ***Find*** highlights matching responsibilities, people and objects, and centres on the first match.
- **The URL keeps the layers and the selection**, e.g. `?layers=people,hosts&focus=responsibility:<id>`, as the knowledge map does. A responsibility's page gets *Show on the overview*.

### 6.2 Arranging, as on the network map

This follows [network-map-positions-plan.md](network-map-positions-plan.md) §2, so both maps behave the same.

- **Moving:**
  - drag a responsibility or a note;
  - a focused one moves with the arrow keys (8 units, 40 with Shift);
  - *Escape* during a drag puts it back.
- **Until it is saved,** a moved item has a dashed outline, and a bar shows `3 moved · [Discard] [Save layout]` (*Manage*).
- ***Save layout* writes:**
  - the moved responsibilities and notes;
  - the visible responsibilities that were never saved.

  So after saving nothing moves (§4.2 of that plan). The toast reads "Saved the places of 9 responsibilities and 2 notes".
- **The *Layout* menu:**
  - *Save this layout*;
  - *Back to automatic for all responsibilities…*, which asks first. Notes always keep their place.
- **On the side panel:**
  - *Saved · by anna, 2 d ago · [Back to automatic]*;
  - or *Automatic*.
- **Without *Manage*,** a drag lasts until the page is left, and there is no bar.
- **Unsaved moves survive** a change of the layers and the refresh every minute.
- **Leaving with unsaved moves asks first:** "Leave without saving?", when switching the tab, leaving the page or reloading.
- **People and linked objects are never saved** (decision 7). They are placed around their responsibilities each time, as hosts are on the network map. Once every responsibility has a saved place, switching layers moves nothing that was arranged.

### 6.3 The automatic layout

It uses the shared `layoutGraph` (`components/graph/forceLayout.ts`), with every group null:

- **The nodes** are responsibilities, people and objects, each with its body radius.
- **The edges** run from responsibility to person and from responsibility to object.
- **Saved responsibilities are fixed:**
  - when every one is saved, they come back exactly where they were;
  - otherwise the unsaved ones are placed around them.
- **Notes are fixed bodies,** each with half its diagonal as radius and no edges, so nothing is placed under a note.
- **The same data gives the same picture,** as on the other maps: the input is sorted by name, and the random source is seeded.
- **The budget is the network map's:** 100 responsibilities, 100 people, 600 objects and 200 notes are laid out in under 300 ms. If that fails, the layout moves to a Web Worker.

### 6.4 The side panel

| Selected | Shows | With *Manage* |
|---|---|---|
| **A responsibility** | Colour, name, summary, responsible and deputy (or *Global responsible stands in*), how many links and combinations, *Needs attention*. *Open* goes to its page, *Links* to the Links tab filtered to it | *Colour* (saved at once, §4.2), *Back to automatic* |
| **A person** | Their name, and the responsibilities they are responsible or deputy for, with colours. No contact details | |
| **An object** | Its name and kind, the responsibilities that link it, and *Open* | *Unlink* from one of them |
| **A note** | Its text, who wrote it and when | *Edit*, *Delete* |

### 6.5 Shared code

- **The moving code moves out of `MapCanvas.vue`** into `components/graph/useMoving.ts`: drag, arrow keys, Escape, the `moved` map and dragging a set of nodes.
  - It is the first commit of P3, alone.
  - It is checked by the network map's vitests and `e2e/network-map.spec.ts`, with no change to either. The knowledge base did the same when the force layout moved (knowledge-base-extensions-plan.md §8.5).
- **The canvases stay separate.** The overview gets `views/admin/responsibilities/overview/OverviewCanvas.vue`.
  - Its bar and *Layout* menu are small enough to have their own copy.
  - A shared canvas can come later, once three maps show what they have in common.

---

## 7. Notes on the map

### 7.1 For the person

- ***+ Note*** (*Manage*) opens a dialog with:
  - **the text:** short Markdown, as a board's note (bold, italics, lists, links), without images or HTML, at most 2,000 characters;
  - **the colour:** yellow (the default), green, blue, pink, purple or grey;
  - **the width:** *Small*, *Medium* or *Large* (160, 240 or 360 units).

  *Save* creates the note in the middle of the current view, with its place saved at once. It can be dragged from there.
- **The height follows the text,** up to 3× the width. A longer text is cut with "…", and the side panel shows all of it.
- **Edit** (from the side panel, or by double-clicking the note) uses the same dialog. **Delete** asks first.
- **The text is saved by its dialog,** and the place by *Save layout*, as for the responsibilities.
- **Notes are free on the map** (decision 9). Their purpose is labels and reminders: "Infra team", "Ask Anna before changing the firewall".
- **Credentials:** a text that looks like a password gets the description's warning (422 `looks_like_credential`). It is saved only once the warning is acknowledged.
- **Note colours are names, not hex values,** so that each theme draws them in its own shades: pastel in the light theme, muted in the dark one. The text on them always reads.
- **Rendered by** `src/boards/boardMarkdown.ts`, the board note's renderer. Links open in the same tab when they lead inside PNMT.

### 7.2 Data

**`responsibility_map_positions`** (`[NotAudited]`: each save is `ResponsibilityMapSaved`):

| Column | Type | |
|---|---|---|
| `ResponsibilityId` | uuid, key | Deleted with the responsibility (cascade) |
| `X`, `Y` | double precision | In the map's units, before zoom and pan. Finite, within ±100,000 |
| `UpdatedAt`, `UpdatedByName` | | |

There is no row for a responsibility placed automatically.

**`responsibility_map_notes`** (`ResponsibilityMapNoteEntity`, `[Audited]`):

| Column | Type | |
|---|---|---|
| `Id` | uuid | |
| `Text` | text, `[AuditText]` | 1–2,000 characters |
| `Colour` | varchar(10) | `Yellow`, `Green`, `Blue`, `Pink`, `Purple`, `Grey`, stored as names (as board messages store their kinds) |
| `Width` | varchar(10) | `Small`, `Medium`, `Large` |
| `X`, `Y` | double precision, `[AuditIgnore]` | The same checks. A move is part of `ResponsibilityMapSaved` |
| `CreatedAt`, `CreatedBy`, `CreatedByName`, `UpdatedAt`, `UpdatedBy`, `UpdatedByName` | | |

There are at most 500 notes (409 `too_many_notes`).

### 7.3 API (`/api/v1/responsibilities/map`)

| Method | Path | Right | Audit | |
|---|---|---|---|---|
| GET | `map?people&hosts&locations&pages` | View | | `ResponsibilityMap { responsibilities, people, objects, notes, global, objectsTotal, truncated }`. A responsibility node: `id`, `name`, `summary`, `color`, `x`, `y` (null when automatic), `position {updatedAt, updatedByName}`, `responsibleId`, `deputyId`, `globalStandsIn`, `attention`, `links`, `combinations`. A person: `id`, `name`, `code`, `color`, `active`. An object: `kind`, `id`, `name`, `responsibilityIds` |
| PUT | `map/positions` | Manage | `ResponsibilityMapSaved` | `{ responsibilities: [{id, x, y}], notes: [{id, x, y}] }`, 1–2,000. Answers `{ saved, skipped }`. One deleted meanwhile is skipped, not refused |
| DELETE | `map/positions` | Manage | `ResponsibilityMapReset` | Every responsibility back to automatic |
| DELETE | `map/positions/{id}` | Manage | `ResponsibilityMapReset` | One back to automatic |
| POST | `map/notes` | Manage | `ResponsibilityNoteCreated` | `{ text, colour, width, x, y, acknowledgeCredentials }` |
| PUT | `map/notes/{id}` | Manage | `ResponsibilityNoteChanged` | `{ text, colour, width, acknowledgeCredentials }` |
| DELETE | `map/notes/{id}` | Manage | `ResponsibilityNoteDeleted` | |

- **People's names, codes and plan colours come from the users.** They aren't contact details, and the availability plan shows them too.
- **Objects' names come from `KbLinkNames`,** as the responsibility's page shows them.

---

## 8. Rights, audit, history, MCP

### 8.1 Rights

| | View | Manage |
|---|---|---|
| The list, sorted by its columns | ✓ | ✓ |
| Colours | | ✓ |
| The Links tab, the Overview, dragging until the page is left | ✓ | ✓ |
| *Hosts / Locations without a responsibility* | With Monitoring: View or Locations: View | ✓ |
| Linking, unlinking, moving many at once | | ✓ |
| *Save layout*, *Back to automatic*, notes | | ✓ |

### 8.2 Audit

The new events are in the category *Responsibilities*:

| Event | Summary |
|---|---|
| `ResponsibilityMapSaved` | "Overview: saved the places of 9 responsibilities and 2 notes" |
| `ResponsibilityMapReset` | "Overview: Network back to automatic", or "… all responsibilities back to automatic" |
| `ResponsibilityNoteCreated`, `…Changed`, `…Deleted` | "Overview note: Infra: ask Anna…" (the first 60 characters) |

- **New target:** `ResponsibilityNote`.
- **Reused events:**
  - `ResponsibilityLinked` and `ResponsibilityUnlinked` for the bulk changes and moves (§5.2);
  - `ResponsibilityChanged` for a colour.
- **`EndpointAuditTests`** gets the new endpoints.

### 8.3 History

- **`ColorChanged = 10`** is appended to `ResponsibilityHistoryKind`.
- **Not in a responsibility's history:** the map positions and the notes. They say how things are shown, not what a responsibility is, and the audit log has them.

### 8.4 MCP and Cookie

- **Nothing changes:**
  - `responsibilities_list` stays sorted by name;
  - no colour, map or notes reach the tools (decision 9);
  - `McpToolsTests` keeps its count ("Cookie keeps its seventeen").

---

## 9. Data model and migrations

| Phase | Migration | |
|---|---|---|
| P1 | `ResponsibilityColour` | `Color` |
| P3 | `ResponsibilityMap` | `responsibility_map_positions` |
| P4 | `ResponsibilityMapNotes` | `responsibility_map_notes` |

- **Each migration has a summary that names this plan.**
- **The dev seed** gets colours for its three responsibilities, and two notes. `DevSeedTests` changes with it.
- **No position is seeded,** so the automatic layout is what a new installation sees.

---

## 10. Compatibility: what must not break

- **Every list keeps its order:** the list sorts by name until a header is clicked, and the other lists don't change.
- **`ResponsibilityRequest` gains `color`.**
  - Only the web UI sends it, and it is regenerated from openapi.json (`gen:api`) in the same commit.
  - MCP and Cookie only read.
- **The network map behaves exactly as before** after its moving code moves out (§6.5).
- **The availability plan's colour field** moves to `components/` and works unchanged; its tests move with it.
- **The responsibility's page keeps its single link and unlink.** The panels, the tools and Redmine stay as they are, apart from the colour dot.
- **The first plan's decision 6 stands:** *Count a linked location as covering what is in it* filters one view and nothing else.

---

## 11. Testing

**.NET**

| Test class | What it covers |
|---|---|
| `ResponsibilitiesApiTests` | <ul><li>`color`: valid, invalid, cleared.</li><li>`PUT {id}/color`.</li><li>`ColorChanged` in the history.</li></ul> |
| `ResponsibilityLinksTabTests` | <ul><li>Each filter alone and together.</li><li>Paging and sorting.</li><li>*Where* only for the page shown.</li><li>`unlinked` with and without `covered`.</li><li>`unlinked`'s rights: View only gets 403; Monitoring: View, Locations: View or Manage get through.</li><li>Bulk link: skipped rows, the limit checked before writing.</li><li>Unlink.</li><li>Move with and without `keep`, including an object the target links already.</li><li>One audit entry per responsibility, with the objects as subjects.</li><li>History rows per link.</li></ul> |
| `ResponsibilityMapTests` | <ul><li>The layers.</li><li>The cut at 600 objects.</li><li>People with code and colour, inactive people.</li><li>The global people, `globalStandsIn`.</li><li>Positions: save, skip, reset one or all, the cascade, NaN and ±1e9.</li><li>Notes: create, change, delete, at most 2,000 characters, at most 500 notes, `looks_like_credential`, the audit text.</li></ul> |
| `EndpointAuditTests` | The matrix, extended |
| `DevSeedTests` | The new counts |

**Vitest**

- `api/responsibilities` gets the new calls.
- **The list's sorting:**
  - each column in both directions;
  - nobody last in both directions, and the global responsible's name where they stand in;
  - *Backup 2* before *Backup 10*;
  - ties by name;
  - `?sort` to and from the URL, and an unknown value falling back to by name.
- `components/colours`: the palette and `textOn`'s contrast.
- **`overviewLayout`:**
  - saved points come back exactly;
  - nothing is placed under a note;
  - the same data gives the same picture.
- **`useMoving`:** its own tests. The network map's tests run unchanged.
- **The Links tab's state** to and from the URL.

**Playwright**, in a new `e2e/responsibilities.spec.ts`:

- sort the list by *Responsible*, open a responsibility, go back, and the sort is kept;
- a colour in the list and in a host's panel;
- on the Links tab, *Hosts without a responsibility*, then *Link to…*, then the host's panel;
- on the Overview, drag, *Save layout*, reload, and the place is kept;
- a note created, edited and deleted;
- *Discard*.

**The suites:** `./run-tests.sh --only web,dotnet` after each commit, both time zones, and `e2e` after P3 and P5.

---

## 12. Risks

| Risk | Answer |
|---|---|
| **The map is cluttered** when objects are shown | Objects are off by default and cut at 600. *Find* and the hover fade help |
| **Moving the moving code breaks the network map** | It is one commit, alone, checked by the network map's unchanged tests (§6.5) |
| **Two people arrange the map at once** | The last save wins, per responsibility or note. Positions are only sent for what was moved or never saved, so a save doesn't overwrite what the other person placed elsewhere |
| **Bulk changes flood the audit log** | One entry per responsibility and request, with the objects as subjects |
| ***Without a responsibility* read as inheritance** | The column *Through its location*, and a checkbox that says what it does |
| **Custom colours unreadable in one theme** | The outline in the border colour, and black or white text by contrast |
| **Notes holding passwords** | The description's credentials check, and the audit log keeps who wrote them |

---

## 13. Phases

| Phase | Content |
|---|---|
| **P1** | **Sorting and colour:** <ul><li>the list's column sorting, the *Changed* column, `?sort` in the URL;</li><li>the migration for `Color`;</li><li>`PUT {id}/color`, and `color` in the request and the contracts;</li><li>the dots in the list, the panels, the page and the Redmine tab;</li><li>the palette, with `ColorField` and `textOn` moved to `components/`.</li></ul> |
| **P2** | **The Links tab:** `links`, `unlinked`, the bulk endpoints, the tab with its filters and selection, *Show in the Links tab* |
| **P3** | **The Overview:** <ul><li>first, alone: `useMoving` out of `MapCanvas.vue`;</li><li>then the positions table, `GET map` and the positions endpoints;</li><li>the canvas with its layers, side panel, *Find* and the URL state;</li><li>*Save layout*, *Discard*, *Layout*, the leave guard.</li></ul> |
| **P4** | **Notes:** the table, the endpoints, the dialog, drawing and moving them |
| **P5** | **The finish:** <ul><li>`ResponsibilitiesDocs.vue` (sorting, colours, the Links tab, the Overview, and its search words);</li><li>the README bullet;</li><li>`docs/release-notes-1.25.md`;</li><li>§15 *As built* here.</li></ul> |

Each phase is one commit or more on master, with the suites green.

---

## 14. Decisions (review of revision 1, 2026-11-24)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | What does "sortable" mean? | **Just make the table sortable:** the list sorts by its columns. No order is set by hand, so revision 1's `SortOrder`, `PUT order`, drag handle, ↑ ↓ and `ResponsibilitiesReordered` are gone, and every other list stays by name | §3 |
| 2 | Which tab opens first, and in what order? | **As proposed:** `Responsibilities · Links · Overview · Redmine`, with the list as the default | §1 |
| 3 | Colours: free or from a palette? Pre-filled? | **As proposed:** 12 swatches plus a custom `#rrggbb`; a new responsibility gets the first unused swatch, which can be cleared | §4 |
| 4 | Which "without a responsibility" views? | **As proposed:** hosts and locations | §5.1 |
| 5 | Bulk *Move to…* and *Also link to…*? | **Yes, both** | §5.1 |
| 6 | What does the Overview show besides responsibilities? | **As proposed:** people on by default; hosts, locations and pages as layers, off by default; notes on | §6.1 |
| 7 | Which positions are saved? | **As proposed:** responsibilities and notes only | §6.2 |
| 8 | Saving: by hand, or at every drop? | **By hand,** with *Save layout* and *Discard* as on the network map | §6.2 |
| 9 | Notes: free, or attached to a responsibility? | **Free on the map.** The tools don't read notes, as proposed (not answered on its own) | §7.1, §8.4 |
| 10 | Who arranges the map and writes notes? | **As proposed:** *Manage*, with no new level | §8.1 |

**Decided along with these, for the next review:**

- **The sort is kept in the URL,** so it survives a visit to a responsibility's page, a reload and a shared link.
- **A *Changed* column** is added, since the list already has the date. Rows with nobody sort last in both directions.
- **The Links tab sorts by its columns too,** on the server because it is paged.

No questions are open.
