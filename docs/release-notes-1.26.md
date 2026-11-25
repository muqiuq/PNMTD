# PNMT 1.26: responsibilities on a map, a Links tab, colours and a sortable list

## What's new

### The Overview: responsibilities on a map

- **A new tab, *Admin → Responsibilities → Overview*** (docs/responsibilities-extensions-plan.md §6), drawn as the network map is:
  each responsibility a circle in its colour, with ⚠ when it needs attention and *G* where the global responsible stands in.
- **The people they name:** solid lines to the responsible, dashed to the deputy, dotted from the global responsible and deputy where
  they stand in. People show their short code from the availability plan, or their initials, in their plan colour. The global
  people are in a card in the corner.
- **Layers:** *People* and *Notes* by default; *Hosts*, *Locations* and *Pages* show what each responsibility links, at most 600, the
  most shared first. An object linked by two responsibilities sits between them.
- **Arranged by hand, for everyone,** as routers on the network map: drag a responsibility or a note, or move a focused one with the
  arrow keys; *Save layout* and *Discard*; *Layout → Save this layout* and *Back to automatic for all responsibilities…*; *Back to
  automatic* for one on its side panel. Leaving with unsaved moves asks first. People and linked objects are always placed
  automatically, never under a note.
- **The side panel:** a responsibility's people, counts, place and colour (changed right there), with *Open* and *Links*; a person's
  responsibilities; a linked object's responsibilities, each unlinked from here.
- **Find**, the layers and the selection are in the address; a responsibility's page has *Show on the overview*.

### Notes on the Overview

- **Short Markdown,** as a board's note (bold, italics, lists, links; no images or HTML), up to 2,000 characters, in six colours with a
  shade per theme and three widths. Free on the map: labels and reminders such as "Infra team: ask Anna before changing the firewall".
- **The text is saved by its dialog, the place by *Save layout*.** A text that looks like a password asks first. At most 500 notes.

### The Links tab

- **Every link of every responsibility,** one row each, filtered by responsibility, person (responsible or deputy), kind and location
  (with its sub-locations), searched in names, places and page numbers, sorted by its columns. The filters are in the address.
- ***Hosts without a responsibility*** and ***Locations without a responsibility*,** with the responsibilities that link their location
  as a hint. *Count a linked location as covering what is in it* leaves those out of this view; nothing is inherited elsewhere.
- **Many at once, with *Responsibilities: Edit*:** *Link…*, *Link to…* for the rows selected, *Move to…*, *Also link to…* and *Unlink*,
  up to 500 rows. What is linked already is skipped and named; the limit of 200 objects per responsibility counts before anything
  changes.

### Colours and sorting

- **A colour per responsibility:** twelve swatches or any `#rrggbb`, on its page; a new one suggests the first swatch nobody uses. A dot
  next to its name on the list, the Links and Redmine tabs, its page and the panels of what it links.
- **The list sorts by a click on a column's header:** name, responsible, deputy, links, Redmine and a new *Changed* column. Nobody
  comes last both ways. The sort is kept in the address.

### Changed

- **The documentation of responsibilities** names the *Edit* level and the MCP tools that change them, which 1.25 added.
- **`responsibility_update` over MCP keeps a responsibility's colour.** Colours, the Overview and notes aren't over MCP or for Cookie.

## Updating from 1.25

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.26.0`. It takes a backup first.

- **Three migrations run at the start:** `ResponsibilityColour` adds a column, `ResponsibilityMap` and `ResponsibilityMapNotes` add a
  table each. Nothing existing changes: every responsibility starts without a colour, placed automatically.
- **No new right:** the Links tab and the Overview need *Responsibilities: View*; changing links, colours, the layout and notes needs
  *Edit*, as changing a responsibility does. *Hosts without a responsibility* also needs *Monitoring: View*, and *Locations without a
  responsibility* *Locations: View*.

## Good to know

- **The audit log** has *Responsibility map saved*, *Responsibility map reset* and the notes' *created*, *changed* and *deleted* in the
  category *Responsibilities*; a change of many links writes one entry per responsibility, each object's own *History* showing it.
- **The plan:** docs/responsibilities-extensions-plan.md, with its decisions and, in *As built*, where the build differs from it.
