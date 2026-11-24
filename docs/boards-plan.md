# Plan: Boards: monitoring dashboards for a wall, under their own path, opened by a URL with a token

| | |
|---|---|
| Status | **Built: P0–P8, on master** (2026-11-23, §21 *As built*). Released in 1.23.0. Revision 2 applied **the review's answers** (§20): the path and URL as proposed, any signed-in user from anywhere, names by default, *solved* as a closed status, the Zabbix history from the poll, **the lounge read-only with no extra right**, **panag's fifteen themes as in its picker**, and **every element in 1.23**. Revision 1 was the first plan |
| Date | 2026-11-23 |
| Scope | **Boards:** monitoring dashboards that are configured in the admin UI and shown by an app of their own:<ol><li>**Their own path, `/pnmt-board/`,** with their own data endpoints under it. A reverse proxy can pass the boards and not the admin UI, or the admin UI and not the boards (§4).</li><li>**A URL with a token** opens a board without sign-in. **IPv4 and IPv6 networks** can limit it, and **a signed-in PNMT user opens it from anywhere** (§5).</li><li>**Configured on a full page in the admin UI** (*Overview → Boards*), with a live preview (§10). Each board has:<ul><li>**1 to 4 columns,** each with its elements chosen and ordered (§6);</li><li>**fit to window** for a single screen, or a page that scrolls (§6.3);</li><li>**a status bar across all columns:** green, yellow or red by rules the admin sets (§8);</li><li>**a theme,** chosen from tiles with previews (§9).</li></ul></li><li>**Elements** (§7):<ul><li>**Redmine:** the overview, the newest tickets, the tickets received today, the tickets solved today.</li><li>**Zabbix:** the number of problems, and the latest problems in a history style.</li><li>**The lounge,** read-only and live.</li><li>**PNMT's own monitoring** (summary, failing sensors, status tiles, host bars, recent changes, the uplink), and **general elements** (a clock, a note, who is on duty, expiring certificates).</li></ul>All of them come in 1.23 (decision 9).</li><li>**The Redmine tab gets *Received today* and *Solved today*** (§7.4).</li></ol> |
| Not in scope | <ul><li>**Anything a board changes.** Boards only read: no buttons, no acknowledging, no comments in the lounge.</li><li>**Links on a board** to PNMT, Redmine or Zabbix. A board is for a wall (§18).</li><li>**Themes for the admin UI.** It keeps light and dark (§9.5).</li><li>**Rotating between boards, embedding a board in another page, sounds** (§18).</li><li>**MCP and Cookie tools for boards.**</li><li>**Changing *the dashboard*,** the admin UI's start page.</li></ul> |
| References | <ul><li>**docs/webui-migration-plan.md:** §3.3 (the sensor API's answers), §3.5 (the UI under `/pnmt-admin`), §5.3 (design and dark mode).</li><li>**docs/connectors-plan.md:** §6.3 (a caller's networks), §6.4 (the `Proxy` setting).</li><li>**docs/cookie-access-plan.md §6.6:** *Was this you?*, the only token page without sign-in so far.</li><li>**docs/audit-redmine-zabbix-plan.md:** the Redmine overview and the Zabbix snapshot.</li><li>**docs/zabbix-hidden-problems-plan.md:** hidden problems (a plan, not built).</li><li>**docs/lounge-plan.md:** the lounge.</li><li>**docs/menu-proposal.md:** the drawer's rules.</li><li>**panag's themes** (`~/source/panag`: PLAN.md §4.9, `public/css/themes.css`, `src/themes.php`, `tests/e2e/themes.spec.js`): the themes and the picker this plan ports (§9).</li></ul> |
| Target | 1.23.0 |

**Words.**

- ***A board*** is a monitoring dashboard: a row of the new `boards`, configured under *Boards*, shown at its URL.
  - **It is called a board so that it isn't confused with *the dashboard*,** the admin UI's start page (`views/dashboard/DashboardView.vue`). *The dashboard* doesn't change.
- ***The board app*** is the second web app, at `/pnmt-board/`. It is built from `PNMTD.WebUI`, but as a bundle of its own that holds no admin UI code (§4.3).
- ***A board's URL*** is `https://pnmt.example.com/pnmt-board/<token>`. The token is the key (§5.1).
- ***A board's networks*** are the IPv4 and IPv6 networks from which the URL works without sign-in. With none, it works from anywhere (§5.2).
- ***A column*** is one of 1 to 4. Each holds its ***elements*** in order.
- ***An element*** is one block in a column. It has a type (§7), a title, its settings and a height.
- ***The status bar*** spans all columns. It is green, yellow or red by its ***rules*** (§8), and grey when it can't tell.
- ***Fit to window*** means the board takes exactly the window, without scrolling (§6.3).
- ***A theme*** is the look of one board (§9).

---

## 1. Summary

| Topic | Decision |
|---|---|
| **Where** | **`/pnmt-board/` serves the app, and `/pnmt-board/api/…` its data.** Nothing of a board is under `/api/v1` or `/pnmt-admin`, and nothing of the admin UI is under `/pnmt-board`. A reverse proxy passes either one without the other (§4.1) |
| **Two apps, one package** | **The board app is a second Vite build in `PNMTD.WebUI`,** with its own entry, base and output (`dist-board` → `wwwroot/pnmt-board`). The elements live in `src/boards/` and are shared with the admin preview. A lint rule keeps the admin client, stores, router and views out of the board app's code, and a test checks its bundle (§4.3) |
| **The URL** | **A token of 32 random bytes in the path,** found by its SHA-256. A copy is kept under Data Protection so that it can be shown again, as connector keys are. It can be renewed, and the old URL then stops at once. **The polls send the token in a header,** so it isn't in a proxy's log every 30 s (§5.1) |
| **Who may open it** | **In this order:** an unknown token gets 404, a board switched off gets 403. **A signed-in PNMT user is let in from any address.** Anyone else is let in only from the board's networks (none means anywhere), or gets 403 with the address PNMT sees. Failed tokens are limited per address (§5.2) |
| **The layout** | **1 to 4 columns,** each with a width (1–4 shares) and its elements in order. **Each element has a height,** *auto* or a share of the rest. Stored as one JSON document per board, validated on save (§6) |
| **Fit to window** | **On:** exactly the window, with text scaled to the window's height; lists show what fits and end with *+ 4 more*. **Off:** a page that scrolls, with the columns stacked on narrow screens (§6.3) |
| **On the wall** | **Polling every 10 to 300 s** (30 by default), paused while hidden. **After 3 missed polls, a banner** says the data is old, and the bar turns grey. The page reloads itself after an update of PNMT. A changed layout applies without a reload. It keeps the screen awake and hides the cursor (§6.4) |
| **Elements** | **Requested:** the Redmine overview, the newest tickets, received today, solved today, Zabbix problems by severity, the latest Zabbix problems (history), the status bar, the lounge. **Added by this plan:** sensor summary, failing sensors, status tiles, host bars, recent changes, the uplink, a clock, a note, who is on duty, expiring certificates. **All in 1.23** (decision 9, §7.1) |
| **What an element shows** | **Shaped for the board on the server,** never an admin contract passed on. Sensor messages are off by default. **People are shown by name** (by default, decision 4), **by initials or not at all,** per board. **Private Redmine tickets** show without subject. **The lounge is read-only:** it shows the chat and nothing to write with, and leaves out refused and removed lines (decision 7) (§7, §14) |
| **The status bar** | **Rules:** a measure (failing sensors from an importance, Zabbix problems from a severity, Redmine counts, the uplink …), *at least n*, and a colour (yellow or red). **The worst matching rule wins.** Green has a text of its own. **Grey when a source can't be read** and nothing worse matched, so the bar never stays green while blind (§8) |
| **Themes** | **Per board.** **panag's fifteen themes, as in its picker** (decision 8): Default, Dark mode, High contrast, High contrast dark, Slay, Switzerland, Terminal, Brasil, Atomic age, WTF, SUSE Linux, Windows 95, Windows XP, Ubuntu, Amiga. **Their colours, fonts and decorations are panag's,** except that Default and Dark mode use PNMT's blue where panag uses PANAG red. **Picked from panag's tiles,** each drawn in its own theme. **A new board starts with *Default*.** CSS only, on the board's own variables. A theme never changes what green, yellow and red mean. AA contrast throughout, and AAA for the two high-contrast themes (§9) |
| **The admin UI** | ***Overview → Boards*:** a list, and **a full-page editor** with the tabs *Layout*, *Status bar*, *Look* and *Access*, beside **a live preview** at a chosen screen size (§10) |
| **New data** | **Redmine:** received today, solved today and the newest tickets in the overview's loader, plus `CreatedOn` on a ticket in a list (§7.4). **Zabbix:** the poll keeps the problems it sees, open and resolved, in `zabbix_problem_history`, using no new API method (§7.5) |
| **Rights** | **A new area, *Boards*: *View* and *Manage*.** Only *Manage* sees and renews URLs. **To add an element, the editor needs that element's right,** e.g. *Redmine: View*. The lounge, the clock and the note need none beyond *Boards: Manage*. **The board itself shows what its elements show, to whoever holds the URL:** the token is the right (§13) |
| **Audit** | Created, changed (with a summary of the layout's changes), deleted, URL shown, URL renewed, and refusals by network (at most one per board and address an hour) (§13.2) |

---

## 2. Today

### 2.1 How the admin UI is served

- **One app at a fixed path.** `WebUi.BasePath = "/pnmt-admin"` (`Hosting/HostingSetup.cs:12`) and `base: '/pnmt-admin/'` (`vite.config.ts:12`) must agree.
  - The shell's files are anonymous. Assets are immutable and the rest `no-cache` (`HostingSetup.cs:16-42`).
  - Deep links get `index.html` (`:49-52`).
  - `/api/**` without an endpoint gets a ProblemDetails 404.
- **Security headers only for `/pnmt-admin` and `/api`** (`HostingSetup.cs:62-90`): `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, the UI's CSP (`default-src 'self'`, `frame-ancestors 'none'`), and `no-store` on the API. Everything else keeps v0.12's bare answers (§3.3 of the migration plan).
- **One Vite entry.** `index.html` hard-codes the favicon under `/pnmt-admin/` (`:8`). There is no second input, workspace or monorepo.
  - The Dockerfile builds `dist` (`:14-20`) and copies it to `PNMTD/wwwroot/pnmt-admin/` (`:35`).
  - `package.json`'s `build` is `vue-tsc --noEmit && vite build`.
- **The app's wiring assumes a signed-in user.** `main.ts:18-30` installs Pinia, the router, VueQuery, PrimeVue and `installSessionHandling`, which sends a 401 to the login (`composables/sessionHandling.ts:12-38`).
  - `api/client.ts` is openapi-fetch with the XSRF middleware.
  - `api/resources.ts` is one 1,700-line module of every resource.
- **Reverse proxies** (deploy/README.md, *Behind a reverse proxy*): *"The app needs `/event/`, `/read/`, `/api/` and `/pnmt-admin/` at the root of its host"*. Restricting paths at the proxy is allowed, as long as the sensor paths stay open.
  - **So a board can't live under `/api/v1` or `/pnmt-admin`:** a proxy that hides the admin UI hides those too. This is the user's requirement *"the admin UI is not accessible through the reverse proxy, and might be the other way around"*.

### 2.2 Sign-in

- **Cookie authentication only, deny by default** (`Auth/AuthSetup.cs:53-90`).
  - The cookie is `HttpOnly`, `SameSite=Strict` and `Path=/`, with sliding expiration over `Auth:SessionDays` (7).
  - Each request is checked by `SessionCache.ValidateAsync`, and `UseSessionGates` holds back a session whose temporary password must be changed (`Program.cs:257`).
- **Authentication is skipped** for the sensor, agent, report, MCP and connector paths (`Program.cs:250-255`).
- **The cookie belongs to the host name.** A board opened at another name than the admin UI's never sees a session (§5.3).
- **Precedent for a token page without sign-in:** `WasThisYouController`, `[AllowAnonymous]` and rate-limited (`Api/V1/AccessControllers.cs:104-122`). `EndpointAuditTests.AnonymousAllowlist` lists every anonymous endpoint (`:28-58`).

### 2.3 Networks, the `Proxy` setting, and keys shown again

- **`ConnectorNetworks`** (`Connectors/ConnectorNetworks.cs`):
  - `Normalize` turns one address into /32 or /128 and refuses host bits;
  - `Contains` maps IPv4-in-IPv6 to IPv4;
  - `AnyAddress` flags `0.0.0.0/0` and `::/0`, and `Inside` flags duplicates;
  - at most 50 entries, each with a label.

  **IPv4 and IPv6 are both handled already.** The connectors' page shows *your address as PNMT sees it*, with a hint when it is a Docker bridge's (`ConnectorService.cs:893-905`).
- **`Proxy`** (`Hosting/ProxySetting.cs`): `UseForwardedHeaders` runs first (`Program.cs:236`), so `RemoteIpAddress` is the client's when the proxy is trusted. Without it, every call seems to come from the proxy.
- **Keys:** `ConnectorKeys` finds a caller by the key's SHA-256 and keeps the key under Data Protection (purpose `PNMTD.Connectors.Key.v1`) to show it again.
  - `ConnectorGuard` blocks an address after failed keys (`Connectors/ConnectorRuntime.cs:10-40`).

### 2.4 The data a board can show, and where it is

| Source | What exists | Where |
|---|---|---|
| **Monitoring** | `DashboardService` builds the summary, failing sensors by host (with 24 h bars), recent changes and hosts (with 90-day bars). **The data is the same for every user,** and `DashboardCache` holds it, stale-while-revalidate, at most 10 s old. **States:** Disabled, Ignored, NoData, Failing, Ok. **Importance:** Low, Normal, High; `EffectiveImportance` is the sensor's own, else its host's. **No contract carries importance;** `NotificationManagerService.cs:133-135` counts by it | `Services/Domain/DashboardService.cs:66-216`, `MonitoringState.cs:26, 55-157`, `Contracts/Dashboard.cs` |
| **Redmine** | **Snapshots in memory,** read by one worker. **The overview** (`overview:{project}`) holds the open count and lists (10 each): urgent, unassigned, overdue, stale, recently updated. Tickets *linked to a problem* are computed per request from PNMT's copy. The overview is pinned: refreshed every `SyncMinutes` (10), or every `RefreshSeconds` (60) for `WatchMinutes` (5) after a request. **`RmIssue` has `created_on`, `closed_on` and `is_private`. The list contract has no `CreatedOn`. Nothing counts tickets of today** | `Redmine/RedmineWorker.Loads.cs:247-339`, `RedmineWorker.cs:631-676`, `RedmineModels.cs:30-53`, `Contracts/Redmine.cs:30, 59-64` |
| **Zabbix** | **Only the open problems, in memory** (`ZabbixSnapshot`), from `problem.get` with `recent: false` every 60 s. Stale after 3 failed polls. Suppressed problems are left out of counts by default. **No history:** a problem leaves the snapshot when it closes, with no record of when. The token's role may call five methods: `hostgroup.get`, `host.get`, `problem.get`, `trigger.get`, `item.get` | `Zabbix/ZabbixSnapshot.cs:21-235`, `ZabbixService.cs:75, 132, 215`, deploy/README.md:374 |
| **Severity colours** | `ZabbixSeverity` 0–5. `.sev-*` from Disaster `#e45959` to Not classified `#97aab3`, with text `#111` | `views/zabbix/zabbix.ts`, `ZabbixSeverityTag.vue:26-47` |
| **The lounge** | **One room, read by every signed-in user** (no right needed, `LoungeControllers.cs:15-22`). Plain text with chips (`[host:…]`, `#1234`), no files. **Refused lines are shown dimmed; removed ones are blanked.** The client polls every 10 s, or 2 s while a voice types | `Cookie/Lounge/LoungeService.cs:35-106`, `Contracts/Lounge.cs:13`, `views/lounge/loungeFormat.ts`, `LoungeLine.vue:56-60` |
| **Time zone** | **`TZ`** (an IANA name, default UTC) is `Clock.TimeZone`. It defines *today* for uptime days and is sent as `Dashboard.timeZone`. **The UI formats in the browser's zone** | `PNMTD.Lib/Helper/Clock.cs:5-70`, `format.ts:1-17` |

### 2.5 What the board app can reuse, and what it can't

| Reusable as it is | Tied to the admin app |
|---|---|
| `format.ts`, `useNow`, `UptimeBar.vue` with `uptime.ts`, `RelativeTime`, `SinceText`, `LogoMark`, `QrCode.vue` (admin side) | The dashboard panels: RouterLink, `useSession`, `usePreferences`, `useActions` |
| `zabbix.ts` (severities, labels; imports only a type) | `LoungeLine.vue`: `useSession`, `RouterLink`, PrimeVue `Button` |
| `loungeFormat.ts` (pure; imports only a type) | `TicketRow.vue`, `ZabbixProblemList.vue`: links into the admin UI |
| `schema.d.ts`, types only | `api/client.ts`, `api/resources.ts`, the stores, the router, `sessionHandling` |
| | `renderMarkdown.ts`: brings highlight.js and its twelve languages |

### 2.6 Found along the way (checked in the code)

1. **`ProblemTicketsAsync` ignores the project** (`Redmine/RedmineService.cs:183`). With `?project=` set, `t.ProjectId == project || lookups.Allowed.Contains(t.ProjectId)` lets through every allowed project's tickets.
   - **P4 fixes it:** a board's Redmine overview with a project filter depends on it.
2. **`RedmineQuery`'s comment says *"with '-' for ascending"*** (`Contracts/Redmine.cs:37`). In fact `-created` is `created_on:desc` (`RedmineWorker.Loads.cs:564`). A one-line fix of the comment in P4.
3. ***Overdue* uses UTC's yesterday** (`RedmineWorker.Loads.cs:572`, `Clock.UtcNow`). Between local midnight and 01:00 or 02:00 in Zurich, a ticket due yesterday isn't overdue yet. **P4 uses `Clock.LocalNow`,** as *received today* does.

---

## 3. Requirements

| # | Requirement |
|---|---|
| R1 | **A board lives under its own path,** apart from `/pnmt-admin` and `/api`. A reverse proxy can pass one without the other |
| R2 | **A board opens by a URL with a token,** without sign-in |
| R3 | **IPv4 and IPv6 networks can limit a board** to where it may be opened from |
| R4 | **A signed-in PNMT user opens any board from any address** |
| R5 | **Boards are added and configured in the admin UI, on a full page, with a preview** |
| R6 | **1 to 4 columns;** for each column, the elements are chosen and ordered |
| R7 | **Fit to window,** for a board on one screen |
| R8 | **Elements:** the Redmine overview, the newest tickets, tickets received today, tickets solved today, the number of Zabbix problems, the latest Zabbix problems (history style), the lounge, and common ones (§7.1) |
| R9 | **A status bar across all columns,** green, yellow or red, with configurable rules for each colour |
| R10 | **Tickets received and solved today on the Redmine tab** too |
| R11 | **Themes, chosen per board** |
| R12 | **Code may be shared with the admin UI, but the board app is separate:** it works when only `/pnmt-board/` is reachable |
| R13 | **Nothing that exists changes behaviour:** the sensor API, the admin UI's paths and headers, Redmine's and Zabbix's existing views, the Zabbix role's five methods (§15) |

---

## 4. Paths, hosting, and the two apps

### 4.1 The path

| Path | What | Sign-in |
|---|---|---|
| `/pnmt-board` | 301 to `/pnmt-board/`, as `/pnmt-admin` | – |
| `/pnmt-board/`, `/pnmt-board/<token>` | **The board app's `index.html`:** no data and no token in it | Anonymous |
| `/pnmt-board/assets/…` | The bundle: hashed and immutable | Anonymous |
| `/pnmt-board/api/view`, `/pnmt-board/api/lounge` | **A board's data** (§12.2) | The token, then §5.2 |
| `/pnmt-board/api/**` (anything else) | ProblemDetails 404 | – |

- **Why `/pnmt-board/`** (decision 1): it mirrors `/pnmt-admin/`, is plainly PNMT's, and is unlikely to collide with anything else on a proxy's host.
- **The three set-ups a proxy can have, each with no change in PNMT:**

  | Set-up | The proxy passes |
  |---|---|
  | **Boards public, admin internal** | The sensor paths as today, and `/pnmt-board/`. Not `/pnmt-admin/` or `/api/` |
  | **Admin public, boards internal** | `/pnmt-admin/`, `/api/`, and not `/pnmt-board/` |
  | **Both** | Everything, as today |

- **`Boards__Enabled`** (`true` by default): with `false`, all of `/pnmt-board` answers a bare 404, as an unknown path does, and *Boards* says the setting is off.

### 4.2 On the server

- **`Hosting/BoardHosting.cs`, built as `WebUi` is:** `BasePath = "/pnmt-board"`, the 301, static files with the same cache headers, the fallback `/pnmt-board/{**path:nonfile}` → `pnmt-board/index.html`, and the ProblemDetails 404 for `/pnmt-board/api/{**rest}`.
- **`UseSecurityHeaders` covers `/pnmt-board`:**
  - the UI's CSP for the shell, and the API's (with `no-store`) for `/pnmt-board/api`;
  - also `X-Robots-Tag: noindex, nofollow`, so a public board isn't indexed.
- **`UseApiErrors` covers `/pnmt-board/api`,** so errors are ProblemDetails, as the board app expects.
- **Authentication runs for `/pnmt-board/api`,** because §5.2 reads the session. The endpoints are `[AllowAnonymous]` and call `BoardAccess` themselves.
  - **`UseSessionGates` skips `/pnmt-board/api`.** A session that must change its password simply isn't *signed in* there (§5.2).
  - **No sliding renewal on board requests:** `CookieAuthenticationEvents.OnCheckSlidingExpiration` sets `ShouldRenew = false` for `/pnmt-board/api`. Otherwise a board polling every 30 s in a signed-in browser would keep that session alive forever.
- **`BoardPaths.IsBoardPath`,** as `ConnectorPaths`, for the pipeline's checks.

### 4.3 The board app: a second build of `PNMTD.WebUI`

```
PNMTD.WebUI/
  index.html                 the admin UI (as today)
  board.html                 the board app's entry: its own favicon and title, <meta name="robots" content="noindex">
  vite.config.ts             the admin UI (as today)
  vite.board.config.ts       base '/pnmt-board/', input board.html, outDir dist-board
  src/
    boards/                  SHARED by both apps: what draws a board
      BoardCanvas.vue        columns, heights, fit to window, the bar, the theme's root
      StatusBar.vue
      elements/*.vue         one component per element type (§7)
      themes/                registry.ts, themes.css (§9)
      layout.ts              the layout's types (from schema.d.ts) and pure helpers
      boardMarkdown.ts       the note's Markdown: remark, GFM, sanitize; no HTML, images or highlighting
    board-app/               ONLY the board app
      main.ts, BoardApp.vue  no router, no Pinia, no VueQuery, no PrimeVue components
      client.ts              fetch to /pnmt-board/api with the token header; nothing of api/client.ts
      useBoardPolling.ts     the polling, the backoff, the stale banner (§6.4)
      kiosk.ts               wake lock, cursor, full screen, reload on a new version
    shared/                  moved here from views/, imported by both apps
      zabbix.ts              (from views/zabbix/zabbix.ts; the old path re-exports it)
      loungeFormat.ts        (from views/lounge/loungeFormat.ts; likewise)
```

- **One package, two builds** (decision): both share `node_modules`, tsconfig, lint, Vitest, the generated `schema.d.ts` and the version. A second package would duplicate all of them for no gain.
  - `build` becomes `vue-tsc --noEmit && vite build && vite build -c vite.board.config.ts`.
  - The Dockerfile adds `COPY --from=web /web/dist-board PNMTD/wwwroot/pnmt-board/`.
- **The boundary is enforced, not hoped for.** ESLint's `no-restricted-imports`:
  - **`src/boards/**` and `src/board-app/**` may not import** `@/api/client`, `@/api/resources` (types from `@/api/schema` only), `@/stores/*`, `@/router`, `@/layouts/*`, `@/views/**`, `@/composables/sessionHandling`, or `primevue/*`.
  - **The admin UI may import `src/boards/**`** for the preview, **never `src/board-app/**`**.
- **The board app uses no PrimeVue components:** plain HTML and CSS on the board's own variables. Themes then control everything (§9), and the bundle stays small.
  - PrimeIcons' font is used (`'self'`).
  - The fonts are the admin's bundled Inter and JetBrains Mono.
- **A test reads the board build's Vite manifest** (`run-tests.sh`, after the build). It fails when:
  - a chunk comes from `src/views/`, PrimeVue, Monaco, Mermaid, Crepe or highlight.js;
  - or the JavaScript exceeds **250 kB gzipped**.
- **Development:** `WEB=dev ./start.sh` starts a second Vite on port 5174 for `/pnmt-board/`, proxying `/pnmt-board/api` to the app, and prints both addresses.

---

## 5. Access: the token, the networks, a signed-in user

### 5.1 The token and the URL

- **The token:** `pnmb_` and 32 random bytes in base64url (48 characters).
  - **Stored as** `TokenHash` (SHA-256, unique index, for the lookup), `TokenProtected` (Data Protection, purpose `PNMTD.Boards.Token.v1`, to show it again) and `TokenHint` (the last four characters). This is `ConnectorKeys`' pattern.
- **The URL:** `<base>/pnmt-board/<token>`. `<base>` is, in this order:
  1. `Boards__BaseUrl`, for boards reached at another name than the admin UI;
  2. `PublicBaseUrl`;
  3. the admin UI's own origin.
- **Shown on *Access*** (§10.3), with *Manage*: masked (`…/pnmt-board/pnmb_••••3fA9`), with *Show*, *Copy* and a **QR code** (`QrCode.vue`), to set up a TV without typing it. Showing it is audited.
- ***Renew URL…*** makes a new token. **The old URL stops at once.** The confirmation says so, and screens with the old one show *"This board's URL has changed"* (§6.4).
- **The page URL carries the token; the polls don't.** The board app reads the token from its path and sends `Authorization: Board <token>` with each data request. A proxy's access log has the token once per page load, not every 30 s (decision 2).
- **The board never shows its own URL,** so a photo of the wall doesn't leak it.
- **`Referrer-Policy: no-referrer`** (already in the header set) keeps the URL from being passed on.

### 5.2 The check, for each data request

`BoardAccess.CheckAsync(http, token)`, used by every `/pnmt-board/api` endpoint, in this order:

| Step | Check | Answer when it fails |
|---|---|---|
| 1 | **The address isn't blocked.** After 10 unknown tokens in 10 minutes, an address is blocked for 10 minutes (`ConnectorGuard`'s pattern). Separately, at most `Boards__RequestsPerMinute` (120) requests per address | 429 |
| 2 | **The token names a board** (looked up by hash, cached 30 s; renew and delete clear the cache) | 404 `board_not_found` |
| 3 | **The board is switched on** | 403 `board_disabled`, *"This board is switched off."* |
| 4 | **A signed-in user is let in from any address.** *Signed in* means a session that `/api/v1/auth/me` accepts: an active user, past two-factor, with no password change pending. **No right is needed** (decision 3): they have the URL, and they are at least as trusted as anyone on the board's networks | – |
| 5 | **The networks:** with none, any address is let in. Otherwise the address (after `Proxy`, IPv4-in-IPv6 as IPv4) must be in one, via `ConnectorNetworks.Contains` | 403 `board_network`, with the address: *"This board can't be opened from 203.0.113.7. An admin can add the network, or sign in to PNMT in this browser."* |

- **The shell is anonymous** (static files, no data), as `/pnmt-admin`'s is. Only the data is checked.
- **The board's API has only GETs,** so no CSRF token is needed.
- **The networks are edited as a connector caller's are:** CIDR and label, at most 50, a single address becomes /32 or /128, host bits are refused, duplicates are flagged. The component is shared (extracted from the connectors' caller page if it isn't separate yet).
- **Networks are opt-in.** A board without networks is open to anyone with the URL, and the list shows it with ⚠ *Anywhere*. `0.0.0.0/0` and `::/0` count as *Anywhere* too (`AnyAddress`).
- **Without `Proxy` and with networks set,** *Access* warns: *"Proxy isn't set: behind a reverse proxy, every screen seems to come from the proxy's address."* This uses the same words as deploy/README.

### 5.3 Signed in, and host names

- **The session cookie belongs to the host name.** If the screens open `board.example.com` and the admins sign in at `pnmt.internal`, no session reaches the board, and only the networks count. *Access* says so in one line.
- **`SameSite=Strict`:** the first navigation from another site's link carries no cookie. The board's own data requests are same-origin and carry it. Nothing to do.
- ***Access* advises against signing in to PNMT in a wall's browser:** that browser would then open the admin UI for anyone standing at it. Use the networks instead.

### 5.4 Seen on screens

- **`LastSeenAt`, `LastSeenAddress` and `LastSeenAgent`** (the user agent, 200 characters) are written at most once a minute per board.
- **Screens now:** distinct (address, user agent) pairs seen within twice the refresh interval, in memory (`BoardViewers`). The list shows *2 screens now*, *3 days ago* or *Never*. This lets an admin see which walls are alive.
- **Views aren't audited.** Refusals by network are (§13.2).

---

## 6. A board

### 6.1 Fields

| Field | Rules |
|---|---|
| **Name** | 1–100 characters, one line, trimmed. **Unique, ignoring case.** 409 `duplicate_name` |
| **Description** | Optional, at most 300 characters. For the list only, never shown on the board |
| **Enabled** | On by default. Off: the URL answers 403 `board_disabled`, and the token stays |
| **Token** | §5.1 |
| **Networks** | §5.2 |
| **Layout** | §6.2 |
| **Revision** | Raised by each save. A stale one gives 409 `edit_conflict`, as a page's save does |

### 6.2 The layout (`BoardLayout`, one JSON document)

```jsonc
{
  "schemaVersion": 1,
  "refreshSeconds": 30,                                  // 10–300
  "look": { "theme": "default", "fit": true, "textScale": 100, "motion": true, "people": "names" },
  "header": { "title": "NOC Zurich", "clock": true, "date": false, "seconds": true },
  "bar": {
    "show": true, "position": "top", "size": "M",
    "texts": { "green": "All clear" },
    "rules": [ { "metric": "sensorsFailing", "filter": { "importance": "High" }, "atLeast": 1, "colour": "red", "label": "important sensors failing" } ]
  },
  "columns": [
    { "width": 1, "elements": [
      { "id": "k3f9a2c1", "type": "sensorSummary", "title": null, "showTitle": true, "height": "auto", "settings": { "importance": "Low" } },
      { "id": "p0d7e4b8", "type": "failingSensors", "height": 3, "settings": { "rows": 12, "messages": false } }
    ] }
  ]
}
```

- **Columns:** 1–4, each with a **width** of 1–4 shares (a CSS grid of `fr`).
- **Elements:** at most 12 per column and 30 per board. Each has:
  - **`id`:** 8 characters, stable. It keys the element's data and its place in the editor;
  - **`title`:** null for the type's own, at most 60 characters. `showTitle` hides the title;
  - **`height`:** `auto` (its own height) or 1–6 (a share of what is left, §6.3);
  - **`settings`:** the type's own (§7).
- **`look.people`:** `names` (the default, decision 4), `initials` or `hidden`, for every element that names a person: assignees, authors, the lounge, who is on duty.
- **`look.textScale`:** 75–250 %.
- **`look.motion`:** off stops every animation. `prefers-reduced-motion` stops them too.
- **Validated on save** by `BoardLayout.Validate`. An unknown type or a setting out of range gives 400 with the field's path (`columns[1].elements[2].settings.rows`), which the editor marks.
- **Why one JSON document:** the editor saves the whole page at once, and the preview sends the same document. Nothing queries columns or elements on their own. Tables per column and element would add joins and no use.
- **`schemaVersion`** lets a later change migrate the document when it is read.

### 6.3 Fit to window, or a page that scrolls

| | **Fit to window** (on) | **Scroll** (off) |
|---|---|---|
| **Size** | Exactly the window (`100dvw × 100dvh`), no scrollbar | A page that grows with its content |
| **Heights** | `auto` elements take their own height. The rest share what is left by their numbers (`flex-grow`) | Every element takes its own height |
| **Lists** | **As many rows as fit,** measured with a `ResizeObserver`, ending with *+ 4 more* | The element's own number of rows |
| **Text** | **Scaled to the window's height:** 1080 px high is 16 px at 100 %, so a board looks the same on an HD and a 4K screen; then `textScale` | 16 px, then `textScale` |
| **Narrow windows** | As they are: the board is meant for its screen | **Below 800 px wide, columns stack** in order, for a phone or a laptop |
| **Long text** | Cut with an ellipsis, one line per row | Wraps |

- **Fit is on for a new board.**
- **In fit mode, an element whose content can't shrink** (a clock, a number) keeps its size, and the shares go to the others. If nothing is left, the last elements of the column are cut off at the bottom, and the editor's preview marks them: *"Column 2 doesn't fit: Lounge is hidden at 1920 × 1080"*.

### 6.4 On the wall: the board app's behaviour

- **One request per poll:** `GET /pnmt-board/api/view` every `refreshSeconds`. It returns the layout, the bar and every element's data (§12.2).
  - **Polling pauses while the tab is hidden,** and runs at once when it is shown again.
  - The lounge has its own, faster poll (§7.6).
- **Errors keep the last data.** Retries back off over 5, 10, 20, 40 and 60 s.
- **Stale:** after three intervals without a good answer, a banner covers the top of the board: *"No connection to PNMT since 14:03. What is shown may be out of date."*
  - The board dims, and **the status bar turns grey**.
  - **The board never stays green on old data.**
- **A refused board** (404, 403: renewed, deleted, switched off, wrong network) shows a full-screen message with the reason and the address PNMT sees. **It tries again every 60 s,** so a board switched on again, or a network added, comes back by itself.
- **A changed layout applies on the next poll,** without a reload, because the view carries the layout.
- **A new version of PNMT:** the view carries `appVersion`. When it differs from the bundle's, the page reloads after a random 0–60 s, so ten screens don't reload at once.
- **Kiosk:**
  - **the Screen Wake Lock API** keeps the screen on (HTTPS or localhost only; without it, nothing happens);
  - **the cursor hides** after 3 s without movement;
  - **`F` or a double click** toggles full screen.
- **The header line:** the title and the clock (with the date and seconds, as set), in the status bar when it is shown, else in a slim line of their own. Also *Updated 14:03:12*, small and muted.
- **Times are in PNMT's time zone** (the view's `timeZone`), not the browser's. A wall's browser often runs in UTC.
- **The browser tab's title and favicon show the bar's colour,** e.g. *🔴 NOC Zurich*. This helps when a board is one tab of many.

---

## 7. Elements

### 7.1 The catalog

**Requested** (R8) and **added by this plan** (marked *+*). **All of them are built for 1.23** (decision 9):

| Type | Shows | Settings | To add it | Source |
|---|---|---|---|---|
| **Redmine** | | | ***Redmine: View*, and Redmine configured** | |
| `redmineOverview` | **The overview:** tiles (Open, Urgent, Unassigned, Overdue, No update n days, Received today, Solved today) and lists (Urgent, Unassigned, Overdue, Linked to a problem) | Which tiles, which lists, rows per list (1–10), a project (or all), subjects on/off | | The overview snapshot (§7.4) |
| `redmineNewest` | **The newest tickets,** newest first: number, tracker, subject, project, *4 min ago*. Those younger than *n* minutes are marked ***New*** | Rows (1–20), *New* for (0–240 min, 30), a project, subjects on/off | | The overview snapshot, new list (§7.4) |
| `redmineReceived` | **Tickets received today:** a large number | A project; *yesterday: 17* below it; yellow from *n*, red from *m* | | The overview snapshot, new count |
| `redmineSolved` | **Tickets solved today:** a large number | As above | | The overview snapshot, new count |
| **Zabbix** | | | ***Zabbix: View*, and Zabbix configured** | |
| `zabbixCount` | **The open problems:** the total and one tile per severity from a minimum, coloured by the worst | Minimum severity, host groups, suppressed on/off (off) | | `ZabbixSnapshot` |
| `zabbixHistory` | **The latest problems, history style:** newest first, the open ones and those resolved in the window. Severity, host, problem, *since 13:51 (12 min)*, or *13:02–13:09 (7 min)* muted with ✓ | Window (1–72 h, 24), minimum severity, host groups, rows | | Snapshot and `zabbix_problem_history` (§7.5) |
| **The lounge** | | | **No right beyond *Boards: Manage*** (decision 7) | |
| `lounge` | **The lounge's chat, live and read-only:** the newest lines, with *Cookie is typing…*. Nothing to write, comment or click | Lines (5–50, 15), comments on/off | | `LoungeService` (§7.6) |
| **PNMT monitoring** | | | ***Monitoring: View*** | |
| `sensorSummary` + | **Counts:** OK, failing, no data (and ignored and off, if set), as the dashboard's strip | Minimum importance, a location (with its sub-locations), ignored and off on/off | | `DashboardCache` + importance (§7.7) |
| `failingSensors` + | **Failing sensors:** host, sensor, importance, *failing since 13:40*, the message if set. The most important first, then the longest failing, as the dashboard's | Minimum importance, a location, messages on/off (**off**), rows | | As above |
| `statusTiles` + | **Large tiles for chosen hosts or sensors** (*Internet*, *Mail*, *ERP*), each coloured by its state with *since* | The hosts and sensors (1–24), the tiles per row | | As above |
| `hostBars` + | **Hosts with their 30-day % and 90-day bar** (`UptimeBar`), as rows or tiles | A location, only hosts with a problem, rows or tiles | | `DashboardService.HostsAsync` |
| `recentChanges` + | **State changes of the last 24 h,** newest first, as the dashboard's | Rows, a location | | `DashboardCache` |
| `uplink` + | **The uplink:** up or down, since, the latest latency | – | | The uplink's state |
| **General** | | | **No right beyond *Boards: Manage*** | |
| `clock` + | **The time,** large, with the date; up to three more time zones with labels | Seconds, date, extra zones | | The browser, in `timeZone` |
| `note` + | **A text written on the board:** on-call numbers, a message for the room. Markdown (GFM) without images or HTML, at most 4,000 characters | The text, its alignment, large or normal | | The layout |
| `onDuty` + | **Who is on duty today:** the availability plan's responsible and fallback of the day | – | ***Availability: View*** | `PlanSnapshot.ResponsibleOn` |
| `certificates` + | **Certificates expiring within n days,** soonest first | Days (1–90, 30), rows | ***Certificates: View*** | The certificate sensors |

- **The status bar** (R9) isn't an element. It belongs to the board (§8).
- **Every element has the common fields** of §6.2: a title, show title, and a height.

### 7.2 The rights to add an element, and the right to see it

- **To add an element, or change its settings, the editor needs the right in the table above.** A save that adds one without it gives 403 `element_right`, naming the element and the area.
- **An element someone else added stays** when the editor lacks its right: they can move or remove it, not change its settings. The editor shows it locked: *"Needs Redmine: View to change"*.
- **The board shows its elements with no user's rights: the URL is the right.** *Access* says so above the networks: ***"Anyone who opens this URL from these networks sees everything this board shows."***
- **The admin preview** withholds the data of elements whose right the editor lacks (*"Needs Redmine: View to preview"*). The board itself shows them.

### 7.3 States, the same for every element

| State | Shown |
|---|---|
| **First load** | A muted *Loading…*, never a spinner that stays |
| **Empty** | A positive line in the theme's *ok* colour: *✓ Nothing is failing*, *✓ No Zabbix problems* |
| **Its source can't be read** (Redmine or Zabbix unreachable or stale) | The last data, dimmed, with *"Redmine can't be reached since 14:01"*. The bar sees it as unknown (§8.1) |
| **Its source isn't configured** (Redmine or Zabbix switched off since) | *"Zabbix isn't set up in PNMT."* The element stays in the layout and comes back with it |
| **Data as of** | Elements fed by a snapshot show its age when it is older than twice its poll: *as of 13:58* |

### 7.4 Redmine: the four elements, and the Redmine tab

**New data, in the overview's loader** (`LoadOverviewAsync`, `RedmineWorker.Loads.cs:247-271`), for every overview snapshot:

| Count or list | Redmine query | Note |
|---|---|---|
| **Received today** | `status_id=*`, `created_on=>=<local midnight as UTC, ISO 8601>`, `limit=1` → `total_count` | Every ticket created today, whatever its status now |
| **Solved today** | `status_id=c`, `closed_on=>=<local midnight>`, `limit=1` | **Solved means a closed status** (Redmine's `is_closed`), decision 5. In a standard Redmine, *Resolved* isn't closed. Redmine keeps `closed_on` when a ticket is reopened, and `status_id=c` drops those |
| **Received yesterday**, **solved yesterday** | The same with `><` from yesterday's to today's local midnight | Only for *yesterday: n* |
| **Newest** | `status_id=*`, `sort=created_on:desc`, `limit=20` | A new list |

- **Local midnight is PNMT's `TZ`** (`Clock.ToUtc(Clock.LocalNow.Date)`), sent as a UTC timestamp. Redmine would otherwise read a date in its API user's time zone. **P0 checks** that Redmine accepts the timestamp on `created_on` and `closed_on`. If it doesn't, the fallback is a date with a filter afterwards on the times returned.
- **Four `limit=1` calls and one list per refresh.** They are cheap, and the overview refreshes at most once a minute.
- **`RedmineOverviewTiles` gains `ReceivedToday`, `SolvedToday`, `ReceivedYesterday` and `SolvedYesterday`, and `RedmineOverview` gains `Newest`.** `RedmineIssueItem` gains `CreatedOn`.
- **A board counts as a watcher.** While a board shows a Redmine element, the overview refreshes every `RefreshSeconds` (60), as while someone has the Redmine page open. However many boards show it, that is one refresh a minute per project.
- **`FakeRedmine` learns** the `created_on` and `closed_on` filters and the sort by `created_on` (`Dev/FakeRedmine.cs:228-275`).

**On a board:**

- **Private tickets** (`is_private`) are listed as *#4711 · Private ticket*, without subject or assignee. They are counted. The admin UI doesn't change.
- **Subjects can be switched off** per element: *#4711 · Hardware · Helpdesk*.
- **Assignees and authors** follow `look.people`.
- ***Linked to a problem*** uses `ProblemsAsync`, with the project filter of §2.6 (1) fixed.

**On the Redmine tab** (R10, `views/redmine/RedmineView.vue:71-104`):

- **Two tiles after *Open*: *Received today* and *Solved today*,** each with *yesterday: n* below.
- **They open the search,** which gains `received=today` and `solved=today` in `RedmineQuery` (the same filters), so a tile shows the tickets it counts.

### 7.5 Zabbix: the count, and a history of problems

**The count** reads `ZabbixSnapshot` as the Zabbix page does:

- suppressed problems are left out unless the element says otherwise;
- hidden problems are left out once docs/zabbix-hidden-problems-plan.md is built, because they leave the snapshot.

**The history needs data PNMT doesn't keep** (§2.4). The poll keeps it, with no new API method (decision 6):

- **The poll asks `problem.get` with `recent: true`,** and `r_eventid` and `r_clock` in the output. Zabbix then returns the open problems plus **those resolved within its *Display OK triggers for*** (`ok_period`, 5 minutes by default), with their recovery time. That window is longer than PNMT's poll (60 s), so every recovery is seen with its exact time.
  - **Rows with `r_eventid ≠ 0` are left out of the snapshot,** so everything that reads it now sees exactly what it sees today.
- **A new table, `zabbix_problem_history`** (§11.2), takes every problem the poll sees:
  - inserted when first seen;
  - marked resolved with `r_clock` when the poll sees its recovery;
  - updated with `LastSeenAt` on each poll.
- **A problem that leaves without a recovery seen** is marked resolved with an estimate, *between* its last sighting and the poll that missed it. This happens when PNMT was down, when `ok_period` is shorter than the poll, or when the problem became hidden or left the configured host groups. The element shows *resolved 13:05–13:20*.
- **Kept `Zabbix__HistoryDays` days** (7), cleaned up daily.
- **Gaps can't be filled:** what opened and closed while PNMT was down isn't seen. The release notes say so.
- **The element** merges the open problems (from the snapshot) and the resolved ones in the window (from the table), newest start first. The table is filtered by the same host groups, minimum severity and suppression.
- **Severity colours** are Zabbix's (`zabbix.ts`), through the theme's `--board-sev-0…5` (§9.3).

### 7.6 The lounge (decision 7)

- **Read-only: the board shows the chat, and that is all.** It has no composer, no *Comment*, no links, and nothing else of the lounge page. Nobody writes to the lounge from a board.
- **Adding it needs no right beyond *Boards: Manage*.** Every signed-in user reads the lounge without a right (§2.4).
- **Read through `LoungeService.RoomAsync`, cut for a board:** the newest *n* lines (5–50).
  - **Refused lines** (Jev) **and removed lines are left out,** never dimmed.
  - Comments are nested under their line, or left out (a setting).
  - ***Cookie is typing…*** comes from `lounge_runtime.Speaking`.
- **Its own poll:** `GET /pnmt-board/api/lounge?after=<id>` every 10 s, or 2 s while a voice types, as the lounge page does. It runs only when the board has a lounge element, and answers 404 otherwise.
- **Chips** (`[host:…]`, `[sensor:…]`, `[case:…]`) show as the lounge shows them, *host* or *sensor*, without names and without links. `#1234` stays text. Lines that open a case or e-mail someone keep their words, and the case's title is left out.
- **Authors:** Cookie and Seraph by name, people per `look.people`.
- **The catalog's line for it says who reads it:** *"The lounge's chat, read-only. Anyone who opens the board reads it."*
- **Rendered by `boards/elements/LoungeElement.vue`** on `shared/loungeFormat.ts`, not by `LoungeLine.vue` (which needs the session, the router and PrimeVue).

### 7.7 PNMT monitoring

- **From `DashboardCache`** (10 s): the same data as the dashboard, and the same for every board. Nothing is computed per screen.
- **Importance is added,** for the filters and for the bar: `BoardData` takes each failing sensor's `EffectiveImportance` from the monitoring snapshot, as `NotificationManagerService.cs:133-135` counts it. The admin contracts don't change.
- **Ignored and disabled sensors never count as failing,** as today.
- **Sensor messages are off by default:** a message can say more than a wall should (*"login failed for user backup-admin"*).
- **A location** includes its sub-locations, as the location pages' host lists do.

---

## 8. The status bar

### 8.1 Colours

```
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ ●  ALL CLEAR                                    NOC Zurich                      14:03:12  │  green
└───────────────────────────────────────────────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ ▲  2 important sensors failing · 1 Zabbix problem High or worse   since 13:51   14:03:12  │  red
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

| Colour | When |
|---|---|
| **Red** | A red rule matches |
| **Yellow** | No red rule matches, and a yellow rule does |
| **Grey** | No rule matches, and **a rule's source can't be read:** Redmine or Zabbix unreachable or stale, or the board's own data is old (§6.4). Text: *"Status unknown: Zabbix can't be read"* |
| **Green** | No rule matches, and every source could be read. Text: `bar.texts.green`, *All clear* by default |

- **The worst wins.** The text lists what matched, each rule's label with its number: *3 failing sensors · 1 Zabbix problem High or worse*. A source that can't be read is added in yellow or red as well: *· Zabbix can't be read*.
- ***since 13:51*** is when the colour last changed. The server keeps it per board, in memory; after a restart it starts again.
- **Each colour has a word or a symbol** (● ▲ ■ ?), never colour alone (§9.4).
- **On turning red, the bar can flash for 10 s** (with `look.motion`).
- **Position:** top or bottom. **Size:** S, M or L (48, 72 or 112 px at 100 % and 1080 px).

### 8.2 Rules

**A rule:** a measure, its filter, *at least n* (1–9,999), a colour (yellow or red), and an optional label (60 characters; else one is made from the measure). At most 20 rules.

| Measure | Filter | Counts |
|---|---|---|
| `sensorsFailing` | Minimum importance, a location | Failing sensors |
| `hostsFailing` | Minimum importance, a location | Hosts with a failing sensor |
| `uplinkDown` | – | 1 while the uplink is down |
| `lateTasks`, `dnsMismatches` | – | As the dashboard's strip counts them |
| `zabbixProblems` | Minimum severity, host groups, suppressed | Open problems |
| `redmineOpen`, `redmineUrgent`, `redmineUnassigned`, `redmineOverdue`, `redmineLinkedToProblem`, `redmineReceivedToday` | A project | The overview's counts |
| `certificatesExpiring` | Days | Certificates expiring within them |

- **The rights are those of §7.2:** a Redmine rule needs *Redmine: View* to add.
- **A rule needs no element.** The bar can be red for Zabbix on a board that shows no Zabbix element.
- **Elements with a number** (received today, solved today, the Zabbix count) take the same thresholds for their own colour (§7.1).
- **Rules for a new board:**

  | Colour | Rule |
  |---|---|
  | Red | `sensorsFailing`, importance High, at least 1: *important sensors failing* |
  | Red | `zabbixProblems`, High or worse, at least 1 (only with Zabbix configured) |
  | Yellow | `sensorsFailing`, at least 1: *sensors failing* |
  | Yellow | `uplinkDown` |
  | Yellow | `zabbixProblems`, Average or worse, at least 1 (only with Zabbix configured) |

### 8.3 Computed on the server

- **`BoardStatus.Evaluate(layout, data)`** runs with the same data as the elements, in the same request. The bar and the elements always agree, and the preview shows the same bar.
- **The measures a board's rules need are computed,** even without an element for them.

---

## 9. Themes (decision 8)

**panag's fifteen themes, as its picker shows them,** ported from `~/source/panag`:

- `src/themes.php`: the registry;
- `public/css/themes.css`: the styles;
- `public/css/main.css:287-327` and `public/profile.php:207-215`: the picker;
- `tests/e2e/themes.spec.js`: the tests.

### 9.1 The picker

```
 Theme
 ┌────────────────┐ ┌────────────────┐ ┌────────────────┐ ┌────────────────┐ ┌────────────────┐
 │████████████████│ │████████████████│ │████████████████│ │████████████████│ │██▀▀▀▀▀▀▀▀▀▀▀▀██│
 │ ┌────────────┐ │ │ ┌────────────┐ │ │ ┏━━━━━━━━━━━━┓ │ │ ┏━━━━━━━━━━━━┓ │ │ ┌────────────┐ │
 │ │ ▬▬▬▬▬▬▬▬▬▬ │ │ │ │ ▬▬▬▬▬▬▬▬▬▬ │ │ │ ┃ ▬▬▬▬▬▬▬▬▬▬ ┃ │ │ ┃ ▬▬▬▬▬▬▬▬▬▬ ┃ │ │ │ ▬▬▬▬▬▬▬▬▬▬ │ │
 │ │ ▬▬▬▬▬▬     │ │ │ │ ▬▬▬▬▬▬     │ │ │ ┃ ▬▬▬▬▬▬     ┃ │ │ ┃ ▬▬▬▬▬▬     ┃ │ │ │ ▬▬▬▬▬▬     │ │
 │ │ ████       │ │ │ │ ████       │ │ │ ┃ ████       ┃ │ │ ┃ ████       ┃ │ │ │ (████)     │ │
 │ └────────────┘ │ │ └────────────┘ │ │ ┗━━━━━━━━━━━━┛ │ │ ┗━━━━━━━━━━━━┛ │ │ └────────────┘ │
 └────────────────┘ └────────────────┘ └────────────────┘ └────────────────┘ └────────────────┘
  Default            Dark mode          High contrast      High contrast dark  Slay
  PNMT blue on       Dark surfaces,     Black on white,    White and yellow    Violet glam with
  light grey         easy on the eyes   strong borders and on black (WCAG      a pride stripe ✨
                     at night           focus outlines     AAA)
                                        (WCAG AAA)
  Switzerland        Terminal           Brasil             Atomic age          WTF
  SUSE Linux         Windows 95         Windows XP         Ubuntu              Amiga
                                                                                    [Use theme]
```

- **As panag's picker:**
  - **A tile per theme, in rows of five,** in the order of §9.2: three rows, as in its screenshot.
  - **Each tile's preview is drawn by its own theme, with no text:** a strip at the top, then a panel with two lines (text and highlight) and an accent block. The preview element carries `data-board-theme`, so each tile shows exactly its theme, whatever theme surrounds it. With no text, the picker stays readable (AAA) in the high-contrast themes.
  - **Below the preview:** the name in bold, and the line, small and muted.
  - **A radio per tile:** the chosen tile has a 3 px outline, and keyboard focus a dashed one. Arrow keys move between tiles.
- **Where:** *Look* shows the board's theme as its tile, with *Change…*.
  - That opens the picker as a wide dialog, because the side panel is too narrow for five in a row.
  - **A click on a tile shows the theme on the board's preview at once,** behind the dialog. *Use theme* keeps it in the draft, and *Save* saves the page.
- **A new board starts with *Default*.** Duplicating a board keeps its theme.

### 9.2 The fifteen themes

They are listed in the picker's order. **The colours are panag's,** from each `[data-theme]` block of `themes.css`, named here by the board's variables (§9.3). Where panag sets no value, Bootstrap 5.3's light or dark default applies, as in panag. **Bold** marks the only changes, in Default and Dark mode.

| # | Id | Name | Line | Page | Top strip, its text | Panels | Text · muted | Border · radius | Accent, its text | Highlight | Dark |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `default` | Default | **PNMT blue on light grey** | `#f5f7fb` | **`#3b59a5`** (panag: PANAG red `#dc3545`), white | `#fff` | `#212529` · Bootstrap's | `#dee2e6` · 0.375rem | **`#3b59a5`** (panag: `#0d6efd`), white | `#0a58ca` | |
| 2 | `dark` | Dark mode | Dark surfaces, easy on the eyes at night | `#16191d` | **PNMT blue, darker** (`{primary.700}`; panag: `#9b1c2c`), white | `#212529` | `#dee2e6` · Bootstrap's dark | `#495057` · 0.375rem | **PNMT blue, lighter** (`{primary.400}`; panag: `#0d6efd`) | Bootstrap's dark | ● |
| 3 | `high-contrast` | High contrast | Black on white, strong borders and focus outlines (WCAG AAA) | `#fff` | `#000`, white | `#fff` | `#000` · `#333` | `#000` | `#000`, white | `#0000c8` | |
| 4 | `high-contrast-dark` | High contrast dark | White and yellow on black (WCAG AAA) | `#000` | `#000`, white | `#000` | `#fff` · `#e6e6e6` | `#fff` | `#ff0`, black | `#ff0` | ● |
| 5 | `slay` | Slay | Violet glam with a pride stripe ✨ | `#f7f0ff` | gradient `#3c096c` → `#5a189a` → `#7b2cbf`, white | `#fff` | `#2a1240` · `#5e4a73` | `#dcc6f2` · 0.6rem | gradient `#8a3fd1` → `#5a189a`, white | `#6a1b9a` | |
| 6 | `switzerland` | Switzerland | Swiss red and white, Helvetica, flat and strict | `#fff` | `#e60000`, white | `#fff` | `#111` · `#555` | `#cfcfcf` · 0 | `#000`, white | `#c80000` | |
| 7 | `terminal` | Terminal | Green on black, monospace, $ prompts | `#000` | `#000`, `#33ff33` | `#000` | `#33ff33` · `#1fbf1f` | `#1a7a1a` · 0 | `#33ff33`, black | `#a6ff8f` | ● |
| 8 | `brasil` | Brasil | Verde, amarelo e azul – green, yellow and blue | `#f4fbf6` | gradient `#006b2b` → `#00843d`, white | `#fff` | `#1e2b38` · `#4a5a52` | `#c7dfcf` · 0.375rem | `#002776`, white | `#0a3d91` | |
| 9 | `atomic-age` | Atomic age | 1950s retro-futurism: teal, mustard and starbursts | `#f7efdc` with starbursts | `#0e5e66`, `#fff7e6` | `#fffaf0` | `#2b2118` · `#5e4a3a` | `#d9c49a` · 0.75rem | `#b8401f`, `#fffaf0` | `#0b5c5e` | |
| 10 | `wtf` | WTF | Comic Sans, clashing neon, wonky cards. Why? Why not. | diagonal stripes `#fff0f8`, `#f0fff4`, `#fffbe0`, `#eef4ff` | rainbow `#ff00aa` → `#ffe600` → `#39ff14` → `#00e5ff` → `#c77dff`, black | `#fff` | `#1a1a1a` · `#4a4a4a` | `#000` · wobbly | `#39ff14`, black | `#6a00f4` | |
| 11 | `suse` | SUSE Linux | Pine and jungle green on fog grey | `#efefef` | `#0c322c`, white | `#fff` | `#0c322c` · `#4d5c57` | `#cfd8d5` · 0.5rem | `#30ba78`, `#0c322c` | `#0a6640` | |
| 12 | `win95` | Windows 95 | Teal desktop, grey bevelled windows, navy title bars | `#008585` | `#c0c0c0`, black | `#c0c0c0`, bevelled | `#000` · `#000` | `#808080` · 0 | `#000080`, white | `#000080` | |
| 13 | `winxp` | Windows XP | Blue title bars, green start button, beige windows | gradient `#d6e6fb` → `#9ec5f2` | blue gradient `#1f4fc4` … `#1941a5`, white | `#ece9d8` | `#000` · `#444` | `#aca899` · 0.375rem | `#1e7b1e`, white | `#0b3d91` | |
| 14 | `ubuntu` | Ubuntu | Aubergine and orange | `#f7f7f7` | gradient `#2c001e` → `#4c0f34` → `#77216f`, white | `#fff` | `#2b2b2b` · `#5e5e5e` | `#d9d9d9` · 0.5rem | `#c34113`, white | `#772953` | |
| 15 | `amiga` | Amiga | Workbench blue, white and orange – failures as Guru Meditation | `#0055aa` | `#fff`, `#0055aa` | `#0055aa` | `#fff` · `#cfe0ff` | `#fff` · 0 | `#ff8800`, black | `#ffd9a8` | ● |

**Fonts, decoration and the status colours.** *Bootstrap's* is `#198754`, `#ffc107` and `#dc3545`, as in panag:

| Theme | Font | Decoration on a board | OK · warning · failure |
|---|---|---|---|
| Default | Inter (PNMT's own) | – | Bootstrap's |
| Dark mode | Inter | – | Bootstrap's |
| High contrast | Inter | 2 px borders on panels and the bar; strong outlines | `#0b5a1c` · Bootstrap's · `#a30000` |
| High contrast dark | Inter | 2 px white borders | `#7dff8a` · `#ffd60a` · `#ff8a8a` |
| Slay | Inter | **The pride stripe** (six colours) under the top strip and the status bar. **✨ after the board's title, twinkling.** Pill-shaped accent blocks and badges | Bootstrap's |
| Switzerland | Helvetica Neue, Helvetica, Arial, Nimbus Sans, Liberation Sans | **The Swiss flag** before the title (the only pure red). Flat, no shadows | Bootstrap's · Bootstrap's · `#c80000` |
| Terminal | ui-monospace, SF Mono, Menlo, DejaVu Sans Mono, Consolas | **Scanlines over the whole board** (no pointer events). **`$ ` before every element's title, `root@pnmt:~$ ` before the board's, and a blinking █ after it.** A green glow on titles | `#33ff33` · `#ffcc00` · `#ff5555` |
| Brasil | Inter | **The flag** after the title | `#00702b` · `#ffdf00` · `#c8102e` |
| Atomic age | Futura, Futura PT, Century Gothic, Avenir Next, URW Gothic, Trebuchet MS (titles) | **Starbursts** on the page; **an atom turning** (14 s) by the title | `#4f7a28` · `#e8a33d` · `#a8361a` |
| WTF | Comic Sans MS, Comic Sans, Comic Neue, Chalkboard SE, cursive | **Wobbly panel borders,** panels and titles **tilted** a little (−1.5° to 0.5°), **"?!"** after the title | `#007a33` · `#ffe600` · `#d00000` |
| SUSE Linux | Inter | – | `#0a6640` · Bootstrap's · Bootstrap's |
| Windows 95 | MS Sans Serif, Microsoft Sans Serif, Tahoma, Geneva, Verdana | **Bevelled panels,** and **navy title bars** on the elements, with the three window buttons drawn on the right | `#006400` · Bootstrap's · `#800000` |
| Windows XP | Tahoma, Trebuchet MS, Verdana, DejaVu Sans | **Blue gradient title bars** on the elements, rounded at the top | Bootstrap's · Bootstrap's · `#a00000` |
| Ubuntu | Ubuntu, Ubuntu Sans, Segoe UI, system-ui | – | Bootstrap's |
| Amiga | Topaz (where installed), ui-monospace, Menlo, DejaVu Sans Mono | **White title strips** on the elements. **Failures as a Guru Meditation:** a red status bar, and a failing status tile, become a black box with a 4 px `#ff3030` frame that blinks once a second, its text `#ff3030` | `#a6f5b0` · `#ffd27a` · Guru Meditation |

**What changes from panag, and why:**

- **Default and Dark mode use PNMT's blue** (`#3b59a5`, `design/preset.ts:6`, and its shades from the preset's palette) where panag has PANAG red, in the top strip and the accent. Default's line says *PNMT blue on light grey*.
- **Inter** replaces Bootstrap's system font where panag sets none, as in the admin UI.
- **Terminal's prompt names PNMT:** `root@pnmt:~$`. panag's ASCII logo on its login page has no place on a board.
- **Decoration moves to a board's parts:**
  - Amiga's Guru Meditation dresses a red status bar and failing tiles, where panag dresses error alerts.
  - Windows 95's and XP's title bars, and Amiga's strips, dress the elements' titles, where panag dresses card headers. An element with its title hidden has no title bar.
- **Kept as panag has them:**
  - its contrast fixes: Switzerland's `#e60000` instead of `#ff0000`, Windows 95's `#008585` instead of `#008080`, Ubuntu's `#c34113` instead of Ubuntu orange;
  - the SVG decorations as data URIs (flags, atom, starbursts, window buttons), which the board's CSP allows (`img-src 'self' data:`);
  - **no logos of the models,** only colours and shapes.

### 9.3 How a theme is built

- **CSS only, scoped on `[data-board-theme="<id>"]`** on the board's root (`BoardCanvas`) and on each picker tile's preview.
- **One file, `src/boards/themes/themes.css`, ported from panag's `themes.css`.** panag found that the live preview needs every theme anyway. panag's variables become the board's:

  | panag | Board | For |
  |---|---|---|
  | `--panag-page-bg` | `--board-bg` | Behind the columns |
  | `--panag-nav-bg`, `--panag-nav-color` | `--board-top`, `--board-top-text` | The header line (title, clock) when it isn't in the status bar; the tile's strip |
  | `--bs-body-bg` | `--board-panel` | Elements |
  | `--bs-body-color`, `--bs-secondary-color` | `--board-text`, `--board-muted` | |
  | `--bs-border-color`, `--bs-border-radius` | `--board-border`, `--board-radius` | |
  | `--panag-accent`, `--panag-accent-color` | `--board-accent`, `--board-accent-text` | *New* badges, the clock's figures, numbers without a status colour |
  | `--bs-link-color` | `--board-highlight` | Ticket numbers, host names (a board has no links) |
  | `--bs-success`, `--bs-warning`, `--bs-danger` | `--board-ok`, `--board-warn`, `--board-fail`, each with `-on` (text on it) and `-soft` (a tint) | The status bar, tiles, dots |
  | `--bs-body-font-family`, a theme's own fonts | `--board-font`, `--board-heading-font` | |
  | – | `--board-unknown` | Grey, in every theme |
  | – | `--board-sev-0` … `--board-sev-5` | Zabbix's colours (`zabbix.ts`), the same in every theme except the two high-contrast ones, which get AAA shades |

- **Decoration rules move from panag's selectors to the board's:**
  - `.app-navbar` → `.board-header`;
  - `.card-header` → `.board-element-title`;
  - `.alert-danger` → `.board-bar[data-colour="red"]` and `.board-tile[data-state="Failing"]`;
  - `h1`–`h5` → `.board-title`.
- **Scoped to the board's root,** so in the admin preview a theme dresses only the preview, and the admin UI keeps its own light and dark (`--pnmt-*`).
- **`src/boards/themes/registry.ts`** holds the id, name, line and dark, in the picker's order, as panag's `THEMES`. **The server keeps the ids** (`BoardThemes.Ids`) to validate a save, and a test compares the two lists.
- **A new theme is an entry in `registry.ts`, its id in `BoardThemes.Ids`, and a block in `themes.css`,** as in panag.

### 9.4 Rules for every theme

- **A theme never changes what a status means.**
  - OK stays green, warning amber or yellow, failure red, unknown grey. The table of §9.2 keeps every theme in those hue families.
  - Every status has a word or symbol too. Terminal's text is green, so its *OK* is marked by its ● and word.
  - Amiga's Guru Meditation is still red.
  - **A unit test checks each theme's status colours:** hue bands (ok 90–160°, warn 35–65°, fail 340–20°), and each `-on` colour's contrast with its fill. A shade that fails is adjusted in P3, and §9.2's table with it.
- **Contrast, as panag tests it** (`themes.spec.js`):
  - `@axe-core/playwright` on a seeded board, once per theme, with every colour of the bar;
  - **AA for every theme, AAA for the two high-contrast ones;**
  - text in exactly its background's colour (axe's *incomplete*, `equalRatio`) counts as a failure, which is how panag found a black badge on black;
  - a screenshot per theme, attached to the report.
- **No fonts from elsewhere** (the CSP's `font-src 'self'`): the system fonts of §9.2 with their fallbacks, or the bundled Inter and JetBrains Mono. Topaz, Futura and Comic Sans appear only where installed.
- **Motion runs only with `look.motion` on and without `prefers-reduced-motion`:** Slay's twinkle, Terminal's cursor, Atomic age's atom, Amiga's blinking frame and the red flash.
- **Flavour stays in CSS, the words stay PNMT's.** No theme changes a text.

### 9.5 Not in scope

- **Themes for the admin UI.** The variables are separate (`--board-*`, `--pnmt-*`), so a later plan can give the admin UI the same themes, per user, as panag has them.
- **More themes:** panag's unbuilt *Crazy Colors* and *Rainbow*, or a *Control room* theme for walls read from afar. Each is one entry and one CSS block (§9.3).
- **A theme by time of day,** and **a theme chosen in the URL** (`?theme=`) for one of several screens (§18).

---

## 10. The admin UI: *Boards*

### 10.1 The drawer and the routes

- **The drawer** (`layouts/navigation.ts`), in *Overview*, after *Dashboard*:

  | Label | Icon | Route | Name | Right |
  |---|---|---|---|---|
  | *Boards* | `pi pi-objects-column` (P0: in PrimeIcons 7.0.0, unused; `navigation.test.ts` keeps icons unique) | `/boards` | `boards` | `['Boards', 'View']` |

- **The routes** (`router/index.ts`), lazy:

  | Path | Name | View | Right |
  |---|---|---|---|
  | `/boards` | `boards` | `BoardsView.vue` | View |
  | `/boards/:id([0-9a-f-]{36})/:tab(layout\|bar\|look\|access)?` | `board` | `BoardEditView.vue`, **the whole window** | View (read-only), Manage (editing) |

### 10.2 The list

```
Overview › Boards                                                       [+ New board]

 Name           Columns  Elements  Theme        Networks          Seen               On
 NOC Zurich     3        9         Dark mode    2 networks        2 screens now      ●
 Helpdesk TV    2        5         SUSE Linux   1 network         4 min ago          ●
 Lobby          1        3         Switzerland  ⚠ Anywhere        Never              ○
```

- **A row opens the board's page.** Its ⋯ has *Open board ↗*, *Duplicate* and *Delete…* (Manage).
- ***New board*** (Manage): a dialog with the name, the number of columns and *Start from*:
  - ***Empty***;
  - ***A starting layout***: the sensor summary, failing sensors and a clock, with the rules of §8.2;
  - **a copy of a board.**

  It opens the new board's page.
- ***Duplicate*** copies everything but the token, the networks and *seen*.
- ***Delete…*** asks, and says that the URL stops at once.
- **Empty:** *"No boards yet. A board shows chosen parts of PNMT, Redmine and Zabbix on a wall, at a URL of its own."*

### 10.3 A board's page: the full-page editor

```
← Boards   NOC Zurich   ● saved                Preview at [1920 × 1080 ▾]  [Open board ↗]  [Save]
┌ Layout │ Status bar │ Look │ Access ┐ ┌──────────────────────── preview ─────────────────────────┐
│ Columns   ( 1 │ 2 │ 3 │ 4 )         │ │ ●  ALL CLEAR                 NOC Zurich        14:03:12   │
│                                     │ │┌─────────────────┬─────────────────┬──────────────────┐ │
│ Column 1 · width [1 ▾]              │ ││ Sensors         │ Zabbix problems │ Newest tickets   │ │
│  ⠿ Sensor summary        ⚙  ✕       │ ││ 412 OK  0  3    │ 0  0  2  1      │ #4711 Printer…   │ │
│  ⠿ Failing sensors       ⚙  ✕       │ ││                 │                 │  Helpdesk · NEW  │ │
│  ⠿ Clock                 ⚙  ✕       │ ││ Failing sensors │ Latest problems │ #4710 VPN for…   │ │
│  [+ Add element]                    │ ││ ✓ Nothing is    │ ▲ Avg sw-core-01│                  │ │
│ Column 2 · width [1 ▾]              │ ││   failing       │   since 13:51   │ Received  Solved │ │
│  ⠿ Zabbix problems       ⚙  ✕       │ ││                 │ ✓ High fw-01    │    14        9   │ │
│  ⠿ Latest problems       ⚙  ✕       │ ││ 14:03           │   13:02–13:09   │                  │ │
│  [+ Add element]                    │ ││ Mon 23 Nov      │                 │ Lounge           │ │
│ Column 3 · width [1 ▾]              │ ││                 │                 │ Cookie: The …    │ │
│  …                                  │ │└─────────────────┴─────────────────┴──────────────────┘ │
└─────────────────────────────────────┘ └───────────────────────────────────────────────────────────┘
```

- **The whole window,** as `EditingPage.vue`: no drawer, Ctrl/⌘S saves, the leave guard asks before unsaved changes are lost, and *← Boards* goes back.
- **Left, the settings in four tabs. Right, the preview** (§10.4).
- ***Layout:***
  - **the number of columns** (1–4). Fewer columns move the elements of the removed ones to the end of the last one, with a toast; nothing is lost;
  - **per column:** the width, and its elements with a handle to drag (within and between columns), ↑ ↓ for the keyboard, ⚙ and ✕;
  - **an element's ⚙** opens its form in the panel: title, show title, height, and the type's settings. **A click on an element in the preview** selects it too;
  - ***+ Add element*** opens the catalog, grouped as §7.1, each type with an icon, its name and one line. Types the editor can't add are greyed with the reason (*"Needs Redmine: View"*, *"Zabbix isn't set up"*).
- ***Status bar:*** show it, top or bottom, size, the green text, the title and the clock in the bar, and **the rules**: a list to add to, remove from and reorder. Each rule is one line: *measure · filter · at least n · colour · label*. Above them: *"The worst matching rule colours the bar. Grey: a source can't be read."*
- ***Look:*** the theme (§9.1), fit to window, text size (75–250 %), refresh (10–300 s), people (names, initials, hidden), motion.
- ***Access:***
  - ***Enabled***;
  - **the URL** (§5.1): masked, with *Show*, *Copy*, the QR code and *Renew URL…* (Manage only);
  - **the networks**, with *"Your address as PNMT sees it: 10.0.0.12"*, and the warnings of §5.2 and §5.3;
  - ***Seen:*** the screens now, and the last one's time, address and browser;
  - the line of §7.2: *"Anyone who opens this URL from these networks sees everything this board shows."*
- **Errors** from a save mark their field: the layout's paths (§6.2) select the element and open its form.
- **With *View* only:** the same page read-only, with no URL, and *Save* and the ⋯ actions hidden.

### 10.4 The preview

- **`BoardCanvas` with the draft,** fed by `POST /api/v1/boards/preview`. It gets the same `BoardView` the board gets, computed for the draft, with the bar.
  - It refreshes after each change (debounced 500 ms), and every `refreshSeconds` while the page is open.
- **The size:** *This window*, 1280 × 720, 1920 × 1080, 2560 × 1440, 3840 × 2160, or portrait 1080 × 1920. The canvas is drawn at that size and scaled down with a CSS transform, so fit to window and the text scale look as on the wall.
  - Without fit, the preview is a page of that width that scrolls.
- **What doesn't fit** is marked (§6.3).
- ***Open board ↗*** (Manage) opens the real URL in a new tab.

---

## 11. Data model and migrations

### 11.1 `boards` (`BoardEntity`, `[Audited]`)

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | |
| `Name` | text (100) | |
| `NormalizedName` | text (100) | Upper case, trimmed; unique. `[AuditIgnore]` |
| `Description` | text (300), null | |
| `Enabled` | bool, default true | |
| `TokenHash` | text (64) | SHA-256, hex; unique. `[AuditIgnore]` |
| `TokenProtected` | text | Data Protection. `[AuditIgnore]` |
| `TokenHint` | text (8) | |
| `Networks` | text, default `'[]'` | JSON, as `connector_callers.Networks` |
| `Layout` | jsonb | §6.2. `[AuditIgnore]`: the audit entry carries a summary instead (§13.2) |
| `Revision` | int, default 0 | |
| `CreatedAt`, `CreatedBy`, `CreatedByName`, `UpdatedAt`, `UpdatedBy`, `UpdatedByName` | | |
| `LastSeenAt`, `LastSeenAddress`, `LastSeenAgent` | null | §5.4. `[AuditIgnore]` |

### 11.2 `zabbix_problem_history` (`ZabbixProblemHistoryEntity`)

- **Marked** `[NotAudited("a copy of Zabbix's problems, written by the poll")]`.
- **Columns:**
  - `EventId` bigint (key), `TriggerId` bigint, `Name` text, `Severity` int, `Suppressed` bool;
  - `Hosts` (JSON: id and name), `Groups` (int[]), `Tags` (jsonb);
  - `StartedAt`; `ResolvedAt` null; `ResolvedFrom` null, set only for an estimate (§7.5); `FirstSeenAt`; `LastSeenAt`.
- **Indexes:** `StartedAt`, and `ResolvedAt`.
- **Cleanup:** daily, rows resolved more than `Zabbix__HistoryDays` (7) ago.

### 11.3 Migrations

| Migration | Phase | Changes |
|---|---|---|
| `Boards` | P1 | `boards` |
| `ZabbixProblemHistory` | P5 | `zabbix_problem_history` |

- **New tables only:** no backfill, and `tools/upgrade-test.sh` passes.
- **Each migration carries the summary naming this plan.**
- **`has-pending-model-changes`** stays clean.

### 11.4 Code

- **Namespace `PNMTD.Boards`:**
  - `BoardService`: create, change, duplicate, delete, the token, the networks;
  - `BoardLayout`: the document, `Validate`, and `Diff` (for the audit's summary);
  - `BoardTokens` and `BoardTokenProtector`;
  - `BoardAccess` (§5.2) and `BoardGuard` (the blocks per address);
  - `BoardData`: builds a `BoardView` for a layout, with one source per area: `MonitoringSource`, `RedmineSource`, `ZabbixSource`, `LoungeSource`, `GeneralSource`;
  - `BoardStatus` (§8);
  - `BoardViewers` (§5.4) and `BoardHosting` (§4.2);
  - `BoardEndpoints` (the minimal API under `/pnmt-board/api`);
  - `BoardsSetup.AddBoards`, called from `Program.cs`.
- **Contracts** in `Api/V1/Contracts/Boards.cs`:
  - `BoardItem`, `BoardDetail`, `BoardRequest`, `BoardNetworkItem`, `BoardCatalog`;
  - `BoardLayout`, with the element settings as a union by `type` (System.Text.Json polymorphism, so `schema.d.ts` types each);
  - `BoardView`, `BoardElementData` (a union by `type`), `BoardBarView`.
- **The element data are the board's own shapes.** A failing sensor is host, sensor, importance, since, and the message only when switched on. No admin contract is passed on as it is (§14).
- **Caching:** a `BoardView` per board for `min(refreshSeconds / 2, 10 s)`, so ten screens of one board cost one build.
  - Redmine and Zabbix come from their workers' snapshots: no call per board.
  - The lounge comes from the database (a small room).
- **Settings:**

  | Setting | Default | |
  |---|---|---|
  | `Boards__Enabled` | `true` | §4.1 |
  | `Boards__BaseUrl` | empty | §5.1 |
  | `Boards__RequestsPerMinute` | `120` | §5.2 |
  | `Zabbix__HistoryDays` | `7` | §7.5 |

---

## 12. API

### 12.1 The admin's: `/api/v1/boards`

In the existing style. The OpenAPI file and `schema.d.ts` are regenerated.

| Endpoint | Right | Audit | |
|---|---|---|---|
| `GET /boards` | View | – | The list: name, description, columns, elements, theme, networks (count, `anywhere`), seen, screens now, enabled |
| `GET /boards/{id}` | View | – | The detail, without the token. With *Manage*, the hint |
| `POST /boards` | Manage | `BoardCreated` | `{ name, description?, columns, from: 'empty' \| 'start' \| <boardId> }` → 201. 409 `duplicate_name` |
| `PUT /boards/{id}` | Manage | `BoardChanged` | `{ name, description, enabled, networks, layout, revision }`. 409 `edit_conflict`, 400 with paths, 403 `element_right` |
| `POST /boards/{id}/duplicate` | Manage | `BoardCreated` | A new token, no networks |
| `DELETE /boards/{id}` | Manage | `BoardDeleted` | |
| `GET /boards/{id}/url` | Manage | `BoardUrlShown` | `{ url }` |
| `POST /boards/{id}/url/renew` | Manage | `BoardUrlRenewed` | `{ url }` |
| `POST /boards/preview` | View | – | `{ layout }` → `BoardView`. Elements beyond the caller's rights come back as `withheld` |
| `GET /boards/catalog` | View | – | The element types, each with `available` and the reason; the themes; the pickers' lists: importance, locations, Redmine projects, Zabbix host groups, the time zones |
| `GET /boards/catalog/targets?q` | Manage | – | Hosts and sensors for status tiles, at most 20, names only, from the global search (`SearchService`), with *Monitoring: View* |
| `GET /boards/address` | View | – | `{ address, dockerBridge, proxySet }` for *Access* |

### 12.2 The board's: `/pnmt-board/api`

| Endpoint | Access | |
|---|---|---|
| `GET /pnmt-board/api/view` | `Authorization: Board <token>`, then §5.2 | `BoardView`: name, layout (without what only the editor needs), look, header, the bar (colour, text, since), each element's data by id, `generatedAt`, `timeZone`, `appVersion`, `refreshSeconds` |
| `GET /pnmt-board/api/lounge?after=` | The same | The lounge's new lines (§7.6). 404 without a lounge element |

- **The answers:** 404 `board_not_found`, 403 `board_disabled`, 403 `board_network` with `address`, 429. Each is a ProblemDetails with `code`, which the board app shows full screen.
- **`EndpointAuditTests.AnonymousAllowlist` gains both,** with a comment naming this plan. A test checks that every `/pnmt-board/api` endpoint calls `BoardAccess`.
- **Both are in `openapi.json`.** The board app uses its types only, through its own small client.

---

## 13. Rights and audit

### 13.1 Rights

- **`Area.Boards`,** appended to the enum, with `View` and `Manage` in `RightsCatalog`:
  - *View:* "The boards, their layout, status bar, look and preview. Not their URLs."
  - *Manage:* "Create, change and delete boards, with their elements, status bar, look and networks; show and renew their URLs. Adding an element also needs its area's right."
- **No migration grants it.** Admins hold it at once.
- **The web UI:** `levelOrder` in `api/rights.ts` gains the area. `GroupsTests`' count of areas goes from 24 to 25.
- **MCP:** nothing. The ceiling stays without *Boards*.
  - *Since docs/mcp-extensions-plan.md §9 (2026-11-24): `boards_list` and `board_get` (never a board's URL), and messages; the ceiling has *Boards: Message*.*
- **Elements:** §7.2.
- **The board app:** §5. The URL is the right.

### 13.2 Audit

- **A new `AuditCategory.Boards`,** and the target type `Board`.
- **Events:**
  - **`BoardCreated`** and **`BoardDeleted`**.
  - **`BoardChanged`,** with the field changes from the interceptor and **the layout's summary** from `BoardLayout.Diff`: *"Column 2: added Newest tickets; status bar: rule 'important sensors failing' changed; theme: Dark mode → Terminal"*.
  - **`BoardUrlShown`** and **`BoardUrlRenewed`**.
  - **`BoardRefused`:** a request refused by the networks, with the address. **At most one per board and address an hour.** Unknown tokens aren't audited; `BoardGuard` counts them.

  Each is added to `AuditCatalog`.
- **`AuditModelTests`** finds the new entities marked.

---

## 14. Privacy and security

**A board is PNMT's first page that shows monitoring data without sign-in.** This section collects what keeps that safe.

| Threat | Answer |
|---|---|
| **The URL leaks** (a chat, a screenshot, a proxy's log) | *Renew URL* stops the old one at once. The networks, where set. The polls carry the token in a header (§5.1). `no-referrer`. `noindex`. The board never shows its own URL |
| **Someone guesses a token** | 256 random bits. `BoardGuard` blocks an address after 10 unknown tokens. A rate limit per address |
| **A forged `X-Forwarded-For`** | Only trusted proxies' headers count (`Proxy`), as for connectors. *Access* warns when networks are set and `Proxy` isn't (§5.2). deploy/README's paragraph on trusted ranges applies as it is |
| **A board shows more than intended** | **Each element is shaped for the board on the server** (§11.4). Sensor messages are off by default. `look.people` hides or shortens names. Private tickets show without subject. Subjects can be switched off. The lounge leaves out refused and removed lines. **Never on a board:** knowledge base pages, mail, the audit log, Cookie's chats, logins, SSH keys, server details |
| **An editor publishes what they can't see** | Adding an element needs its area's right (§7.2) |
| **A signed-in browser on a wall** | No sliding renewal from board requests (§4.2). *Access* advises against it (§5.3) |
| **Clickjacking, framing** | `frame-ancestors 'none'` and `X-Frame-Options: DENY`, as the admin UI's |
| **Script in a note or a ticket subject** | Text is rendered as text. The note's Markdown goes through `rehype-sanitize`, with HTML and images off. Links get `rel="noopener noreferrer"` |
| **Load from many screens** | A cached view per board; Redmine and Zabbix from snapshots; polling paused in hidden tabs; the rate limit |
| **The admin UI is reachable through the board's proxy path** | It isn't: `/pnmt-board` serves only the board app and its two endpoints. A test requests `/pnmt-board/../api/v1/…` and encoded variants |

---

## 15. Compatibility: what must not break

| What | How it stays | Checked by |
|---|---|---|
| **The sensor API, `/report`, `/connector`, `/agent`** | Untouched. The headers and bare 404s outside `/pnmt-admin`, `/api` and `/pnmt-board` stay | The golden contract; `HostingTests` |
| **The admin UI's paths, headers and build** | `/pnmt-admin` unchanged. The second build adds files under `dist-board` only | `e2e/pages.spec.ts`; the Dockerfile's build in CI |
| **Sign-in** | A board request never renews a session. The gates skip `/pnmt-board/api` only | A test: a signed-in poll doesn't extend the cookie |
| **Redmine's overview and the Redmine tab** | New fields and two tiles. The existing lists unchanged. The project filter of *Linked to a problem* fixed | `RedmineTests`, `FakeRedmine` with the new filters |
| **Zabbix's snapshot** | Resolved rows from `recent: true` are left out before `Replace`, so every reader sees what it sees today. **The role still needs only its five methods** | `ZabbixTests`: the snapshot with and without resolved rows; `FakeZabbix` refuses any sixth method as today |
| **The lounge** | Read only. `LoungeService` gains a cut for boards; its page is unchanged | `LoungeTests` |
| **The rights catalog** | One area appended | `GroupsTests` (25), `rights.test.ts` |
| **Anonymous endpoints** | Two, listed | `EndpointAuditTests` |
| **The drawer** | One entry, an unused icon | `navigation.test.ts` |
| **Upgrades** | Two new tables | `tools/upgrade-test.sh` |
| **The seed** | Two boards (*NOC*, *Helpdesk TV*), on an empty database only, with networks for the lab's range | `DevSeedTests` |

---

## 16. Testing

As before: **few tests, where they protect something.**

| What | How |
|---|---|
| **Access** (`BoardAccessTests`) | Unknown token → 404; switched off → 403; IPv4, IPv6 and IPv4-in-IPv6 inside and outside the networks; no networks → anywhere; a signed-in user from outside → in; a session with a pending password change → only the networks count; behind a trusted proxy and an untrusted one; 10 unknown tokens → 429 for the address; renewing stops the old token at once, despite the cache. `BoardRefused` once an hour |
| **Hosting** | `/pnmt-board` → 301; the shell anonymous with the UI's CSP and `noindex`; assets immutable; `/pnmt-board/api/x` → ProblemDetails 404; no admin endpoint answers under `/pnmt-board`, encoded paths included; `Boards__Enabled=false` → bare 404. A signed-in poll doesn't renew the cookie |
| **Boards** (`BoardsApiTests`) | Rights per level. A duplicate name → 409. Stale revision → 409. Invalid layouts → 400 with paths. An element without its area's right → 403, and one already there, kept, without it → 200. The URL only with *Manage*, audited. Duplicate gets a new token. The layout's audit summary |
| **Data** (`BoardDataTests`) | Each element's data for a seeded state. Messages off by default. `people` names, initials, hidden. Private tickets without subject. A location includes its sub-locations. Importance filters. The cache shared between two requests |
| **The status bar** (`BoardStatusTests`) | Red over yellow over grey over green; a source that can't be read is grey only when nothing else matched; labels and numbers in the text; *since* kept across polls |
| **Redmine** | The four counts and *Newest* in the loader, against `FakeRedmine` with `created_on` and `closed_on` (reopened tickets drop out); local midnight in `TZ` (a fake clock on whole seconds, from one read: test-clock precision); the tiles and the search filters; `ProblemTicketsAsync` with a project |
| **Zabbix** (`ZabbixHistoryTests`) | `recent: true` with resolved rows: the snapshot as before; the table gets the recovery's time; a problem gone without a recovery gets an estimate; the cleanup; filters by group and severity |
| **The lounge** | Refused and removed lines left out; chips without names; 404 without a lounge element |
| **Web** (Vitest) | The layout's helpers (moving elements, fewer columns); fit's row counts; the stale banner after three intervals; the reload on a new `appVersion`; the theme registry against the server's ids; the status colours' hues and contrasts per theme; `boardMarkdown` drops HTML and images |
| **The bundle** | The manifest test of §4.3: nothing from `views/`, no PrimeVue, under 250 kB gzipped |
| **Themes** (`e2e/board-themes.spec.ts`) | Per theme, a seeded board with every colour of bar: axe AA, AAA for the high-contrast ones; a screenshot attached |
| **End to end** (`e2e/boards.spec.ts`) | An admin creates a board with three columns, adds and orders elements by drag and by keyboard, sets two rules, picks a theme, sees the preview change, adds a network, saves, and opens the URL. From an address outside the network, signed out → the refusal page with the address; signed in → the board. A sensor fails: the bar turns red within a poll. Renew: the open board shows the URL has changed. A user with *View* sees the page read-only without the URL |

---

## 17. Risks

| Risk | Answer |
|---|---|
| **A board's URL ends up with the wrong people** | Renew; networks; a board shows only what its elements show; the warning on *Access* |
| **Boards open to anywhere by default** | Networks are a choice, not forced: a board for a phone has no fixed network. The list marks *Anywhere* with ⚠, and *Access* says it above the URL |
| **A wall shows green while PNMT can't see** | Grey for what can't be read; the stale banner; *as of* on old snapshots (§6.4, §7.3, §8.1) |
| **Redmine doesn't accept a timestamp on `created_on`** | P0 checks it; the fallback is a date in the API user's zone with a filter afterwards |
| **`ok_period` in Zabbix shorter than PNMT's poll** | Recoveries become estimates (*13:05–13:20*). The documentation names the setting (*Administration → General → Trigger displaying options*) |
| **Themes make a status unreadable** | §9.4: hue bands, contrast, axe per theme, a word or symbol per status |
| **The board bundle grows into the admin UI** | The lint rule and the manifest test (§4.3) |
| **The lounge shows something unexpected on a public wall** | Read-only, with refused and removed lines left out and names per `look.people`. The catalog says who reads it. An admin's delete removes a line from the board on its next poll |
| **Ten screens reload at once after an update** | A random delay of 0–60 s (§6.4) |
| **A kiosk browser without the Wake Lock API** | Nothing breaks; the documentation names the kiosk flags of Chromium and the screen's own settings |
| **Layout documents of an older `schemaVersion`** | Migrated when read; a test per version step |

---

## 18. Later (not built)

| What | Note |
|---|---|
| **Rotation:** a playlist of boards, each shown for *n* seconds | A board type of its own, listing boards |
| **Embedding** a board in an intranet page or a home automation panel | Per board, the origins allowed in `frame-ancestors` |
| **A theme by time of day,** and a theme or text size in the URL for one of several screens | `look.night`, `?theme=`, `?scale=` |
| **Sounds** when the bar turns red | Kiosk flags for autoplay; off by default |
| **Links** on a board to Redmine, Zabbix and PNMT | For boards on laptops, not walls |
| **More elements:** advanced sensors' charts, Cookie's open cases, OS updates waiting, MikroTik updates, the knowledge base's recent changes, the certificate and connector alarms | Each a source and a component |
| **Themes for the admin UI,** per user | §9.5 |
| **MCP tools** to list boards | Not needed now; built in docs/mcp-extensions-plan.md §9 |
| **Export and import** of a board's layout as JSON | |
| **Burn-in protection** for OLED screens: a slow shift of a few pixels | |

---

## 19. Phases

| Phase | Content | Size |
|---|---|---|
| **P0** | **Checks, with no product code:** `pi-objects-column` in PrimeIcons 7.0.0 and unused; Redmine (5.1 and 6.0) accepts an ISO timestamp on `created_on` and `closed_on`; Zabbix's `problem.get` with `recent: true` returns `r_eventid` and `r_clock` under the existing five-method role; Vite builds two configs from one package without conflicts in `dist`; the Wake Lock API in Chromium's kiosk mode | small |
| **P1** | **The foundation:** the area, the migration `Boards`, `BoardService`, `BoardAccess`, `BoardGuard`, `BoardHosting` (headers, gates, no renewal), the board app (polling, stale, refusals, fit and scroll, columns, kiosk, reload), the second build and the Dockerfile, the lint rule and the bundle test, *Boards* (list, editor with *Layout* and *Access*, preview), the elements `clock`, `note`, `sensorSummary` and `failingSensors`, the audit, the seed | large |
| **P2** | **The status bar:** rules, `BoardStatus`, the monitoring measures, the *Status bar* tab, the tab title and favicon. Redmine's and Zabbix's measures come with P4 and P5 | medium |
| **P3** | **Themes:** the variables, panag's fifteen themes ported (§9.2, §9.3), the picker (§9.1), the hue and contrast tests, axe and a screenshot per theme | medium |
| **P4** | **Redmine:** the loader's counts and *Newest*, `CreatedOn`, `FakeRedmine`, **the Redmine tab's two tiles with their search filters**, the four elements, private tickets, the measures for the bar, and §2.6's three fixes | medium |
| **P5** | **Zabbix:** `recent: true`, the migration `ZabbixProblemHistory`, the estimates and the cleanup, the two elements, the measure for the bar | medium |
| **P6** | **The lounge element** | small |
| **P7** | **The remaining elements** (decision 9): `statusTiles`, `hostBars`, `recentChanges`, `uplink`, `onDuty` and `certificates`, with the bar's `certificatesExpiring` | medium |
| **P8** | **Release:** the documentation page (`views/docs/BoardsDocs.vue`), deploy/README (*Behind a reverse proxy*: pass `/pnmt-board/`; the three set-ups of §4.1; Zabbix's `ok_period`; kiosk browsers), docs/release-notes-1.23.md, the README's features, this plan's *As built* | small |

- **Each phase** is one or more commits on master, has `./run-tests.sh` green before the next starts, and goes into an *As built* section.
- **P2 to P7 need P1, not each other.** The Redmine tab's tiles (part of P4) need nothing of P1 and can come first.
- **P1 alone is already useful:** a board of PNMT's own state on a wall, at its URL, limited to networks.

---

## 20. Decisions (review of revision 1, 2026-11-23)

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | The path | **`/pnmt-board/`** | §4.1 |
| 2 | The token in the path, or in the fragment? | **In the path: `/pnmt-board/<token>`.** The polls send it in a header | §5.1 |
| 3 | Which signed-in users may open a board from anywhere? | **As proposed:** any signed-in user, with no right | §5.2 |
| 4 | Names of people on a board by default | **Names.** Initials or hidden per board | §6.2 |
| 5 | What *solved* means | **As proposed:** a closed status (Redmine's `is_closed`) | §7.4 |
| 6 | The Zabbix history: from the poll, or from `event.get`? | **As proposed:** from the poll, with `recent: true`, in `zabbix_problem_history`. The Zabbix role keeps its five methods | §7.5 |
| 7 | The right to put the lounge on a board | **The lounge on a board is read-only: it only shows the chat.** So adding it needs no right beyond *Boards: Manage*. The proposed *Cookie: Manage* is dropped | §7.1, §7.6 |
| 8 | Themes | **panag's fifteen themes, as its picker shows them,** with panag's colours, fonts and decorations, and PNMT's blue in Default and Dark mode. They are picked from panag's tiles. The proposed *Control room* is dropped (§9.5) | §9 |
| 9 | Which elements come in 1.23? | **All of them** | §7.1, §19 |

**Decided along with these, for the next review:**

- **A new board starts with the theme *Default*,** as panag's users do. The review named no theme for new boards, and *Dark mode* was revision 1's proposal.
- **Default's top strip and accent are PNMT's blue,** not PANAG red. *"PANAG red on light grey"* becomes *"PNMT blue on light grey"*.

No questions are open.

---

## 21. As built

**P0–P8 are on master** (2026-11-23), in five commits: the Redmine counts first, then a fix the first full run turned up, the Zabbix
history, the boards themselves, and the documentation. Where the code differs from the sections above, this section says so; the rest
is as planned.

### The order of the commits

- **The Redmine counts and the Zabbix history came first,** each a commit of its own: neither needs a board, and each changes what an
  existing page or worker does.
- **P1, P2, P3, P6 and P7 are one commit:** the element types, the status bar's measures and the themes share the layout's contract,
  the board app and the editor, so they were tested together rather than split into states that never ran.
- **`UDP listener: the port before Listening`:** the first full run lost 31 UDP tests to a race in the listener's state (the port set
  after the flag the tests wait for). Fixed on the way, as its own commit.

### P0: the checks

- **`pi-objects-column`** is in PrimeIcons 7.0.0 and was unused.
- **Redmine 5.1, 6.0 and 7.0** accept an ISO timestamp on `created_on` and `closed_on` (`>=` and `><`): a ticket created earlier the
  same day is left out, and `status_id=c` drops a reopened ticket whose `closed_on` stays. Checked against the three images in
  Podman, with tickets dated in the database.
- **Zabbix's `r_eventid` and `r_clock`** are in the `problem.get` answers recorded from 6.0, 7.0 and 8.0 (the fixtures). `recent:
  true` follows Zabbix's documentation; the fake honours it.

### Where the build differs

- **The bundle check is part of the board's build,** not a separate test: a plugin in `vite.board.config.ts` fails the build when a
  module of the admin UI or of its heavy libraries gets in, or when the JavaScript is over 250 kB gzipped (it is 76 kB). The lint rule
  of §4.3 keeps the imports out.
- **The board app's page is `PNMTD.WebUI/board/index.html`, with `board/main.ts`,** Vite's root for the board: neither the build nor
  the dev server meets the admin UI's `index.html`, and a relative script path outside the root broke the dev server.
- **The Zabbix history keeps the open problems too** (§11.2): every shown problem the poll sees, with `LastSeenAt`. The element reads
  open and resolved ones from the table; host groups are read at the time, from the hosts' mirror, so the table has no groups column.
- **The lounge's own poll returns the element's newest lines each time,** not the lines after an id: a board shows at most 50.
- ***Recent changes* shows at most 15 rows,** the dashboard's newest changes of the last 24 hours.
- **A board that fits the window is a page that scrolls below 700 px of width:** a phone opening a wall's URL gets stacked columns
  rather than a squeezed screen (§6.3 said "as they are").
- **"As of" shows on an element older than five minutes,** not twice its poll: Redmine's overview refreshes every minute while a board
  reads it, and two minutes put the note on boards that were fine.
- **The status colours a theme declares are listed in `registry.ts`** (`statusColours`): a unit test checks their hues and the
  contrast of their text (AA; AAA in the high-contrast themes). **axe isn't run:** PNMT has no `@axe-core/playwright`, and a new
  dependency needs its own vetting. The e2e test takes a screenshot of the seed's NOC board in every theme instead.
- **Settings shown in a `Select` use `''` or `0` for "none"** (any importance, everywhere, every project, its own height): PrimeVue's
  `Select` shows nothing for a null value. They become null again on the way out.
- **`GET /boards/catalog/names`** names the hosts and sensors of status tiles for the editor.
- **The note's Markdown has a renderer of its own** (`boardMarkdown.ts`): remark, GFM and `rehype-sanitize` without images, raw HTML or
  highlighting, turned into Vue nodes.
- **The API's document keeps `null` out of enum schemas** (a schema transformer in `Program.cs`): an enum's shared schema took `null`
  from its first nullable use, so 13 enums carried it already, and the boards' nullable `Importance` and `ZabbixSeverity` would have
  made every `Importance` in the UI's types nullable. A property's own `oneOf` still says whether it may be null.
- **`WEB=dev ./start.sh` runs the board app's Vite on port 5174** in the same container (`BOARD_WEB_PORT`).
- **The seed mounts the key ring** (`start.sh`), so the seed's board URLs and connector keys can be shown again.
- **The release:** docs/release-notes-1.23.md is written; the version isn't bumped and nothing is tagged.

