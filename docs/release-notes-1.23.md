# PNMT 1.23: boards, monitoring dashboards for a wall

## What's new

### Boards

- **Boards** (docs/boards-plan.md): monitoring dashboards for a wall, each at a URL of its own,
  `https://pnmt.example.com/pnmt-board/<token>`. They are set up under ***Boards*** in the admin UI's *Overview*.
- **Their own path.** The board app and its data are all under `/pnmt-board/`, apart from `/pnmt-admin/` and `/api/`. A reverse proxy
  can pass the boards and keep the admin UI internal, or the other way round.
- **Who opens a board:**
  - anyone with its URL, from the board's **IPv4 and IPv6 networks** (none: from anywhere);
  - **a signed-in PNMT user from any address**, in the browser they signed in with. A board's requests never keep a session alive;
  - ***Renew URL*** stops the old one at once. The URL is shown again on the board's *Access* tab, each display in the audit log, with a
    QR code for a TV. The board never shows its own URL.
- **Set up on a full page with a live preview** at a chosen screen's size (1280 × 720 up to 4K, or portrait):
  - **1 to 4 columns**, each with a width and its elements in order, dragged or moved with the arrow buttons;
  - **fit to window** for a single screen: the text scales with the screen's height, and lists show what fits and how many more there
    are. Without it, a page that scrolls, its columns stacked on a phone;
  - **a status bar across the columns, green, yellow or red by its rules:** a measure (failing sensors from an importance or in a
    location, hosts with a failing sensor, the uplink, late tasks, DNS zones, Zabbix problems from a severity, Redmine's counts,
    expiring certificates), at least how many, and the colour. The worst matching rule wins. **It turns grey** when a source can't be
    read and nothing worse matched, and when the board has had no answer for three refreshes;
  - **fifteen themes**, picked from tiles with a preview: Default, Dark mode, High contrast, High contrast dark, Slay, Switzerland,
    Terminal, Brasil, Atomic age, WTF, SUSE Linux, Windows 95, Windows XP, Ubuntu and Amiga. A theme never changes what green, yellow
    and red mean;
  - people by name, by initials or not at all; the text size; how often the board refreshes; motion on or off.
- **Elements:**
  - **Redmine:** the overview (its counts and lists), the newest tickets (the recent ones marked *New*), tickets received today and
    tickets solved today (each with yesterday's number and thresholds for its colour);
  - **Zabbix:** the open problems by severity, and the latest problems with their recovery, history style;
  - **the lounge**, read-only and live;
  - **PNMT:** sensors, failing sensors, status tiles for chosen hosts and sensors, host bars, recent changes, the uplink;
  - **general:** a clock with other time zones, a note in Markdown, who is on duty today, expiring certificates.
- **What a board shows is shaped for it:** private Redmine tickets without their subject, sensor messages only where an element says
  so, the lounge without refused or removed lines, and no links.
- **On the wall:** the screen stays awake, the cursor hides, `F` toggles full screen, the tab's title and icon show the bar's colour,
  and the board reloads itself after an update of PNMT.

### Redmine: received and solved today

- **The Redmine tab shows *Received today* and *Solved today*** after *Open*, each with yesterday's number. A tile opens the search with
  the tickets it counts, by the new filter *Received* or *Solved*, today or yesterday.
- **Today is PNMT's** (`TZ`). Solved means a closed status: a ticket reopened since doesn't count.

### Fixed

- A project's Redmine overview listed every configured project's tickets linked to a problem, not only its own (and its subprojects').
- *Overdue* counted a ticket due yesterday only from UTC's midnight, not PNMT's.
- A shared enum of the API's types could take `null` from its first nullable use; the UI's generated types now keep nullability on the
  property only.

## Updating from 1.22

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.23.0`. It takes a backup first.

- **Two migrations run at the start:** `ZabbixProblemHistory` and `Boards` add new tables. Nothing existing changes.
- **Only admins see *Boards*** until an admin gives *Boards: View* or *Manage* to a group (*Admin → Groups*). No group gets it by itself.
- **A reverse proxy that passes only some paths** needs `/pnmt-board/` for boards (deploy/README.md, *Boards*).
- **Nothing to configure.** `Boards__Enabled=false` turns all of `/pnmt-board` off.

## Good to know

- **Zabbix's history of problems** comes from PNMT's poll, which now asks for the problems resolved within Zabbix's *Display OK
  triggers for* (5 minutes by default) as well. Keep that longer than `Zabbix__PollSeconds`, or recoveries are only estimated. The
  Zabbix role still needs only its five methods. `Zabbix__HistoryDays` (7) keeps resolved problems.
- **Adding an element needs its area's right**: a Redmine element *Redmine: View*, a Zabbix one *Zabbix: View*. What someone else added
  stays as it is.
- **The audit log** has a category *Boards*: created, changed (with a summary of the layout's changes), deleted, URL shown, URL renewed,
  and a screen refused by the networks (at most once an hour for an address).
- **`pnmtd dev seed`** adds two boards: *NOC* (from anywhere) and *Helpdesk TV* (private networks only).
- **The plan:** docs/boards-plan.md, with its decisions and, in *As built*, where the build differs from it.
