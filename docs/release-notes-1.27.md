# PNMT 1.27: responsibilities in full over MCP, the escalation order, and arranging the Overview

## What's new

### The escalation order

- **Each responsibility says whom to ask, one after another** (docs/responsibilities-mcp-and-map-plan.md §3): the responsible, the
  deputy, the global responsible, the global deputy. It is one simple queue: whoever isn't set or is inactive is skipped and the next
  moves up, and a person counts once.
- **Shown on a responsibility's page** below its people, and **on the Overview's side panel** as a numbered list, naming who was skipped
  because inactive. A person's panel says their place in each responsibility's order.
- **Information only,** as responsibilities are: nothing is sent, and alerts and Cookie's escalations don't follow it.

### The Overview's lines

- ***Lines to* Responsible · Deputy · Both** above the map: the first in each escalation order (the default), the second, or the first
  two. So a missing responsible's line goes to whoever stands in, and the line's style says their role: solid for the responsible,
  dashed for the deputy, dotted for a global person.
- **Only people with a line are drawn,** and the legend names the view. The choice is in the page's address (`?lines=deputy`,
  `?lines=both`), as the layers are.

### Arrange

- **An *Arrange* menu among the map's controls:** *Circle*, *Around their responsible* and *Grid*. Every responsibility is placed at
  once, never two names on each other and none under a note:
  - *Circle*: one ring as small as it can be, by main responsible and then by name; above 24 responsibilities, rings around each other;
  - *Around their responsible*: a small ring for each main responsible, with that person in the middle, the rings packed together;
  - *Grid*: rows and columns by name, about as wide as a screen.
- **The result is moves, as a drag's:** *Save layout* keeps it for everyone, *Discard* takes it back. Moves made by hand are replaced
  only after asking. People and linked objects settle around the new places at once.
- **The bar on top says what was arranged,** clear of the map's controls in the bottom-right corner. Without *Edit* it says the
  arrangement is only yours, until you leave the page, and its *Discard* goes back to the automatic layout.

### Responsibilities in full over MCP

- **The colour as hex:** `responsibility_create` and `responsibility_update` take `color` as `#rrggbb` (the `#` may be left out, any
  case; an empty text clears it). `responsibility_options` lists the twelve swatches with how many use each, and suggests the first
  unused one.
- **More in the answers:** the colour and its swatch's name, *needs attention*, the escalation order, and who created and changed it
  when; `responsibilities_list` filters by `attention`. A description can be given on create, and `responsibility_update` takes
  `restoreFrom`, a history row, as *Restore* does on the page.
- **Six new tools,** 125 → 131:
  - `responsibility_history`: a responsibility's changes, and one row's description before and after;
  - `responsibility_links_list`: the Links tab, and the hosts and locations nobody links (with *Monitoring: View* or *Locations: View*);
  - `responsibility_links_change`: link, unlink, move or also link up to 500 at once;
  - `responsibility_notes`, `responsibility_note` and `responsibility_note_delete`: the Overview's notes, deleting only while deleting is
    allowed over MCP.
- **Still not over MCP:** deleting a responsibility and the global people (*Manage*, which no token has), and the Overview's layout.
- **Cookie** keeps its seventeen tools; its answers about responsibilities also give the escalation order.

### Changed

- **The Overview draws fewer lines by default:** only the first in each escalation order, where 1.26 drew every responsible and deputy.
  *Both* comes close to 1.26's picture. People who are only someone's deputy show with *Deputy* or *Both*.
- **An inactive person gets no line** any more: the escalation order skips them. The responsibility keeps its ⚠ and its panel names them.
- **People may sit elsewhere on the Overview:** a person now starts next to the responsibilities they are drawn to. Saved responsibilities
  and notes don't move.

## Updating from 1.26

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.27.0`. It takes a backup first.

- **No migration** and no new right: the escalation order is computed, the lines' choice is in the address, and *Arrange* saves through
  the places 1.26 added.
- **MCP clients** see six more tools after reconnecting. Every existing argument keeps its meaning: `responsibility_update` without
  `color` keeps the colour.

## Good to know

- **The audit log** has the new tools' changes under the events the page writes, with *MCP* as the actor; a note deleted over MCP is
  recorded as it was.
- **The plan:** docs/responsibilities-mcp-and-map-plan.md, with its decisions and, in *As built*, where the build differs from it.
