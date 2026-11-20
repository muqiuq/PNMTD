# Network map: moving routers and saving their positions

**Status:** Built (2026-11-20, §10, §11). Released in 1.17.0. It replaces decision 2 of [network-map-plan.md](network-map-plan.md) ("positions are not saved").

| | |
|---|---|
| **Goal** | A person drags the routers on the map to where they want them, and saves that by hand. From then on, everyone sees the routers there, under every filter. |
| **In scope** | Router positions, one layout for everyone (decision 1). *Save* and *Discard*. *Back to automatic* for one router or for all. Moving with the keyboard. Moving a whole location by its name. The automatic layout keeping saved routers where they are. |
| **Not in scope** | Saved positions of hosts (decision 2). Several named layouts, or one per person (decision 1). Saving the zoom and pan. Positions over MCP or the CLI (§5.3). Changing a router's location by dragging it into another circle: the location stays the host's, set on its page. |

---

## 1. Today

- **`MapCanvas.vue`:** a drag writes the node's place into `moved`, in memory only.
  - `moved` is cleared whenever the set of nodes changes, so changing a filter drops every drag.
  - Leaving the page or reloading drops them too.
- **`networkLayout.ts`:** two stages (groups, then their members), deterministic, with no fixed nodes. `separate()` pushes overlapping circles apart, both by the same amount.
- **The graph API** has no positions. The client lays everything out (network-map-plan.md §10).

## 2. For the person

1. **Drag a router.** It stays where it is dropped. A router that is moved but not saved has a **dashed ring**, so it is clear what *Save* will write.
2. **A bar appears at the bottom of the map** (with NetworkMap *Edit*): `3 routers moved · [Discard] [Save positions]`.
   - Without *Edit* there is no bar. A drag lasts until the page is left, as today.
3. **Save positions** writes:
   - the moved routers;
   - the visible routers that have no saved position yet (§4.2).

   So after saving, nothing on the map moves. The toast reads "Saved the positions of 12 routers".
4. **From then on, for everyone,** the routers are where they were saved, under every filter. A router added later is placed automatically, near the routers it is connected to.
5. **Discard** puts the moved routers back where they were.
6. **The location circles follow.** A circle is always the smallest one around its visible nodes, also while dragging. Dragging a router out of its group makes its circle bigger, and the location stays the same.
7. **Dragging a location's name moves the whole location:** all its visible nodes. Its routers then count as moved.
8. **Keyboard:** a focused router moves with the arrow keys (8 units, or 40 with Shift). *Escape* during a drag puts the router back. Dragging is never the only way (WCAG 2.5.7).
9. **The side panel of a router** shows its position:
   - *Saved* · by anna, 2 d ago · [Back to automatic] (*Edit*);
   - or *Automatic*.
10. **A *Layout* menu next to *Fit*:**
    - *Save this layout* (*Edit*): saves every visible router where it is now, without moving anything. For when the automatic picture is already right.
    - *Back to automatic for all routers…* (*Edit*): asks first, and names how many routers it resets.
11. **Unsaved moves are kept:**
    - When the filters change, hidden routers keep their moves too. The bar counts them.
    - The refresh every minute keeps them as well. A position that someone else saved meanwhile arrives with the refresh, for the routers I haven't moved.
    - Switching the tab, leaving the page or reloading asks "Leave without saving?", as the editing page does.

## 3. Data

**`network_router_positions`** (`[Audited]`), one row per router:

| Column | Type | |
|---|---|---|
| `SensorId` | uuid, key | The router's `MIKROTIK` sensor. Deleted with it (cascade). |
| `X`, `Y` | double precision | In the map's own units: the layout's space, before zoom and pan. |
| `UpdatedAt` | timestamptz | |
| `UpdatedByName` | text | As the other network tables keep their names. |

- **Checks:** both values finite, and within ±100,000.
- **A host with two routers** has two rows, one per router node.
- **No row** means the router is placed automatically.

## 4. The layout with saved routers

### 4.1 The algorithm

`layout(nodes, edges, saved)`, where `saved` maps node ids to points.

1. **Members per group**, as today.
2. **A group with saved routers is anchored:**
   - its saved routers are fixed at their points (`fx`/`fy`);
   - its other members (new routers, hosts) are laid out around them by the group's own simulation. They start at the saved routers' centre, with the seeded jitter;
   - its circle is the one that encloses all its members (`packEnclose`), plus the padding.
3. **A group without saved routers** is laid out around (0, 0) as today, and is sized by its members.
4. **The groups:**
   - anchored groups are fixed bodies;
   - the free groups are placed around them, pulled along the connections and kept off by collision.
5. **`separate(circles, fixed)`:**
   - an anchored circle never moves;
   - when one circle of an overlapping pair is anchored, the other moves the whole distance;
   - two anchored circles stay where the person put them, even when they overlap.
6. **When every visible router is saved, no simulation runs** for the routers: the saved picture comes back exactly.

### 4.2 Why *Save* also writes the visible routers that were never saved

Saving only the moved routers would change the layout of the others:

- their groups are now placed around anchored ones;
- so the routers that weren't touched would move after saving.

Saving those as well keeps the picture exactly as the person saw it. Routers that already had a saved position and weren't moved are not sent. A save therefore never overwrites a position that someone else saved meanwhile, unless the same router was moved here too (§8).

### 4.3 The same data gives the same picture

- Saved points are used as they are. The vitest checks that they come back exactly.
- Everything else stays deterministic, as today: sorted input and a seeded random source.

## 5. API, MCP, CLI

### 5.1 Endpoints (`/api/v1/network-map`)

| Method | Path | Right | Audit | |
|---|---|---|---|---|
| GET | `graph` | View | | A router node gets `x` and `y`. Both are null when it is placed automatically. |
| GET | `devices/Router/{id}` | View | | Adds `position`: `{ x, y, updatedAt, updatedByName }` or null. |
| PUT | `positions` | Edit | `NetworkPositionsSaved` | Body `{ positions: [{ sensorId, x, y }] }`, 1 to 5,000. Writes them all, adding or updating. Answers `{ saved, skipped }`. |
| DELETE | `positions/{sensorId}` | Edit | `NetworkPositionCleared` | One router back to automatic. |
| DELETE | `positions` | Edit | `NetworkPositionsReset` | All routers back to automatic. |

- **`skipped`:** a router deleted between the drag and the save is skipped, not refused, so it doesn't block the others. An id that is not a router at all gets 400.
- **Audit:** category *Network*, with a new target, `NetworkRouterPosition`. The summary reads "Saved the positions of 12 routers", or "office-gw back to automatic".

### 5.2 Rights

| | View | Edit |
|---|---|---|
| Drag and move with the keyboard, until the page is left | ✓ | ✓ |
| *Save positions*, *Discard*, *Save this layout*, *Back to automatic* for one router or for all | | ✓ |

All of it with *Edit* (decision 4): the positions are a picture, and every save is in the audit log.

### 5.3 MCP and CLI: unchanged

A position says how the picture looks, not how the network is connected. An agent learns nothing from it. `network_device_get`, `network_connections_list` and the other network tools stay as they are.

## 6. Files

| File | Change |
|---|---|
| `PNMTD/Models/Db/NetworkMapEntities.cs`, `PnmtdDbContext`, a migration | `NetworkRouterPositionEntity`, its table, the cascade |
| `PNMTD/Network/NetworkPositionService.cs` (new) | Save, clear and reset, with the checks |
| `NetworkMapService.cs`, `NetworkViews.cs` | `x`/`y` on router nodes, `position` on the router's details |
| `Api/V1/Contracts/NetworkMap.cs`, `NetworkMapControllers.cs` | The three endpoints and their contracts |
| `Audit/AuditEvents.cs` | The three events and the target |
| `PNMTD.WebUI/src/views/network/networkLayout.ts` | Saved points, anchored groups, `separate(…, fixed)` |
| `MapCanvas.vue` | Moves kept across filters, the dashed ring, the keyboard, dragging a location by its name, circles from the current positions |
| `MapTab.vue` | The bar, *Save* and *Discard*, the *Layout* menu, the leave guard |
| `DevicePanel.vue` | The position and *Back to automatic* |
| `views/docs/NetworkMapDocs.vue` | A section "Arranging the map" |
| `docs/network-map-plan.md` | Decision 2, and §1, §8.2, §10, §13 and §14 point here |

## 7. Tests

- **.NET** (`NetworkPositionsApiTests`):
  - saving adds and updates;
  - View gets 403 on all three;
  - NaN, ±1e9, or a sensor that isn't a router give 400;
  - a deleted router is skipped;
  - deleting the sensor deletes the position;
  - the graph carries `x`/`y`, and the details carry `position` with the name;
  - each event is written once.
  - `EndpointAuditTests` and `has-pending-model-changes` stay clean.
- **Vitest** (`networkLayout.test.ts`):
  - saved points come back exactly;
  - a free group never overlaps an anchored one;
  - an anchored group's circle encloses its saved and free members;
  - all saved: the same result twice, with no simulation;
  - `separate` with fixed circles.
- **E2e** (`network-map.spec.ts`):
  - drag a router, and the bar shows "1 router moved";
  - *Save*, reload, and the router is in the same place (within 1 px of the screen);
  - *Discard* puts it back;
  - *Back to automatic* from the panel;
  - the viewer sees no bar.

## 8. Risks

| Risk | Answer |
|---|---|
| A saved map gets messy over time | *Back to automatic* for one router or for all. New routers are still placed automatically, near their connections. |
| Two people arrange the map at once | The last save wins, router by router. Only moved routers and those without a position are sent (§4.2), so one person's save rarely overwrites the other's. |
| Circles overlap when a person places locations into each other | Allowed for anchored circles: that is the person's choice. Free circles are still pushed away. |
| A router placed far from the rest of its location makes a huge circle | It shows at once, and dragging it back or *Back to automatic* fixes it. The docs page says so. |
| Many routers, many saved points | Anchored groups need fewer simulation steps than free ones. 5,000 points in one request is far above the number of routers. |

## 9. Phases

1. **P1 Server:** entity, migration, service, endpoints, audit, .NET tests.
2. **P2 Layout:** saved points and anchored groups in `networkLayout.ts`, with vitests.
3. **P3 Map:**
   - drag state kept across filters, the dashed ring, the bar, *Save* and *Discard*;
   - the keyboard, dragging a location by its name, the *Layout* menu, the leave guard;
   - the panel;
   - e2e.
4. **P4 Docs:** the Documentation page, and network-map-plan.md (decision 2 replaced).

## 10. Decisions (review of 2026-11-20)

1. **One layout for everyone**, for now. One per person would need a user key on each row, and a choice of whose layout a link shows.
2. **Only routers.** Hosts appear only with the *Hosts of…* filter, and there can be hundreds per location. They are placed automatically around the saved routers.
3. **Save writes the moved routers and the visible routers that were never saved** (§4.2), so nothing moves after saving.
4. **Everything with *Edit***, *Back to automatic for all routers* too (§5.2).

No questions are open.

## 11. As built

- **As planned**, with these details:
  - the bar sits on top of the map, in the middle, so that it never covers the legend or the zoom buttons;
  - the *Layout* menu is the ⋯ button next to *Fit* (the app's `MoreMenu`), shown with *Edit*;
  - the graph carries `savedPositions`, the number of routers with a saved position, for the reset's question;
  - the coordinates are saved to two decimals;
  - deleting all is `DELETE /positions` with *Edit* (decision 4), its summary naming how many were cleared.
- **The e2e test** covers dragging, *Discard*, the keyboard, the question before leaving, saving and reloading, *Back to automatic* and the reset. A user with View alone is tested in `NetworkMapTests`: the built-in *Viewers* group has no Network map right, as for every area added after the groups were made.
