# Plan: Board messages, and more for the boards' elements: sensor messages, images, clocks, dates, on duty

| | |
|---|---|
| Status | **Built** (2026-11-24). Released in 1.24.0. Where the build differs from the plan: §20, *As built*.<ul><li>**Revision 3:** the answers to revision 2's questions (§19, decisions 5 and 6): the deputies are the availability plan's fallbacks, and every board is 24-hour by default.</li><li>**Revision 2:**<ul><li>applied the answers to revision 1's questions (§19): the Debug page for *System: Manage* or *Boards: Message*, the five kinds, 10 s to 60 min, and Cookie in the lounge;</li><li>added **part B** (§9): a sensor's last message as an element, 24- or 12-hour clocks, date formats, images and GIFs as an element, and more options for *On duty today*.</li></ul></li><li>Revision 1: the board messages.</li></ul> |
| Date | 2026-11-24 |
| Scope | **A. Board messages:** a message box sent to one or more boards, drawn over the board, large enough to read across a room:<ol><li>**A kind** (*Alert*, *Warning*, *Maintenance*, *Information*, *Success*), **a title and a message**, shown for **60 s by default**, or for 10 s to 60 min (§4).</li><li>**Sent by a person** from ***Admin → Debug*** (`/pnmt-admin/admin/debug`), with a preview, the open messages and *Withdraw* (§7).</li><li>**Sent by Cookie,** from its case work and when asked in the lounge. Only **to the boards chosen for it** in *Admin → Cookie → Actions*, and only while **Cookie's group holds *Boards: Message*** (§8).</li><li>**A new right level, *Boards: Message*,** between *View* and *Manage* (§12).</li><li>**A board shows a message within about 5 seconds,** through a light poll of its own (§6).</li></ol>**B. More for the elements** (§9):<ol><li>**A *Sensor message* element:** a sensor's last message, in five sizes up to *as large as fits*.</li><li>**24- or 12-hour time** for the whole board, and for each clock on its own.</li><li>**Date formats,** the week number, and the language of day and month names.</li><li>**An *Image* element:** PNG, JPEG, WebP and animated GIFs, uploaded in the editor.</li><li>***On duty today*:** how many deputies, labels and texts, where people work, the day's comment, tomorrow.</li></ol> |
| Not in scope | <ul><li>**Sounds.** Browsers block audio a page plays without a click, and a wall has nobody to click (§17).</li><li>**Messages from outside PNMT** (MCP, a connector's caller, a webhook) (§17).</li><li>**Scheduled or recurring messages,** and messages that stay until withdrawn (§17).</li><li>**Answering or acknowledging a message on a board.** Boards only read (docs/boards-plan.md, *Not in scope*).</li><li>**A message changing the status bar.** The bar shows the monitoring state; a message sits over the board and leaves the bar as it is (§5.1).</li><li>**Images in a message box, video, and images from a URL** (§17).</li><li>**Translating the board's own words.** The date language names days and months only (§9.2).</li></ul> |
| References | <ul><li>**docs/boards-plan.md:** §5 (access), §6.4 (the board app's polls, the kiosk), §7 (elements), §9 (themes), §12.2 (the board's API), §13 (rights and audit), §21 (as built).</li><li>**docs/ai-operations-plan.md §8:** the Debug page and its rate limit.</li><li>**docs/cookie-plan.md:** §6 (actions and their levels), §8.2 (the availability plan's responsible and fallbacks), §9.3 and §10 (Cookie's group and rights), §21.1 (proposals).</li><li>**docs/lounge-plan.md §8.2:** `send_email`, the lounge's way to an action through the executor.</li><li>**docs/locations-knowledge-plan.md §5.5:** the files folder, the image check, uses and cleanup.</li><li>**docs/advanced-sensor-plan.md §3, §13:** a sensor whose data names people gets messages without the values.</li></ul> |
| Target | 1.24.0 (migrations: a minor version) |

**Words.**
- **A message:** a kind, a title, a text and a duration, sent to one or more boards at once.
- **Open:** sent, not withdrawn, and its time not up.
- **The box:** how a board shows an open message, over its columns.
- **Withdraw:** ending an open message early, on every board it went to.
- **The stamp:** a number per board that changes whenever its open messages change. The board's poll sends it back and gets *304 Not Modified* while nothing changed.
- **The chain:** who is on duty on a day, in order (§9.5):
  1. the day's responsible;
  2. the day's fallback;
  3. the global fallback;
  4. the global fallback group's members.
  
  Only those who work that day count.

---

## 1. Summary

| Topic | Decision |
|---|---|
| **What a message is** | **A kind, a title (up to 100 characters), a text (up to 1000, Markdown as in a note element), and how long it shows:** 60 s by default, 10 s to 60 min. **Sent to one or more boards** at once, or to all boards that show messages (§4) |
| **The kinds** | ***Alert*** (red), ***Warning*** (amber), ***Maintenance*** (violet), ***Information*** (blue), ***Success*** (green). Alert, warning and success use the status bar's red, yellow and green, which no theme changes (§4.1) |
| **On the board** | **A message box over the columns,** 80 % of the width and 70 % of the height by default, or the whole board. **The status bar stays visible.** A large icon and title, the text scaled to fit, who sent it and when, and a countdown. **Several open: the most important first, rotating every 15 s**, with "1 / 3" (§5) |
| **Per board** | ***Look → Messages***: on by default; the box *Large* or *Full*. A board with messages off is not offered as a target (§5.5) |
| **Delivery** | **A poll of its own every 5 s,** `GET /pnmt-board/api/messages`. **The stamp makes it cheap:** while nothing changed, the answer is *304* with no database read. **A message shows within about 5 s.** The board counts down from the *remaining* seconds the server sends, so a wall whose clock is wrong still closes it on time (§6) |
| **The rate limit** | **Fixed in the same release:** a board's requests count per address *and board*, with the default raised from 120 to 600 a minute. Today all screens behind one address share 120 requests a minute across all boards, and a second poll would make that tighter (§6.4) |
| **Sending in the admin UI** | ***Admin → Debug* gets a panel *Send a board message*:** the boards, the kind, the title and text, how long, and **a live preview in the first board's theme**. Below it, **the open messages with *Withdraw*,** and the last 20 sent (§7) |
| **Who sends** | **The new level *Boards: Message*** ("View, plus sending messages to boards and withdrawing them"). *Boards: Manage* includes it. **The Debug page opens for *System: Manage* or *Boards: Message*,** and shows each panel by its own right (decision 1, §19) |
| **Cookie** | **A new action, *Board message*,** with a level like Cookie's other actions (*Off*, *Propose*, *Auto*). **It needs both gates:** the boards Cookie may send to (*Admin → Cookie → Actions*; none by default) and *Boards: Message* for Cookie's group (*Admin → Cookie → Rights*). **Seraph reviews it before it goes out,** as for Pushover. Its own limits per day and per board an hour. **A proposal waits at most 30 min** in the decisions inbox (§8) |
| **Cookie in the lounge** | **A lounge tool `send_board_message`,** only for a person who holds *Boards: Message* and asks for it (decision 4, §8.6) |
| **Kept** | **In `board_messages`, 30 days** (`Boards__MessageHistoryDays`), cleaned when a message is sent. **No new background service** (§10) |
| **Audit** | ***Board message sent*** and ***Board message withdrawn*** in the category *Boards*, by a person or by Cookie (§12.2) |
| **B. Sensor message** | **A new element: one sensor's last message,** plain text. **Size:** *Small*, *Medium*, *Large*, *Huge*, or ***Fit***, as large as the element allows. Monospace, at most so many lines, the state's colour, and how old it is (§9.3) |
| **B. Time** | ***Look → Time*: 24-hour (default) or 12-hour,** for every time on the board. **Each clock** (the header's and each *Clock* element, with its other zones) **can override it** (§9.1) |
| **B. Dates** | ***Look → Date*:** eight formats, from "Monday, 24 November" to "24.11.2026" and "2026-11-24". **The week number** on or off, and **the language of day and month names:** English, German, French, Italian, or the screen's browser. A *Clock* element can override the format (§9.2) |
| **B. Image** | **A new element: an uploaded PNG, JPEG, WebP or GIF, animated ones included,** up to 10 MB (`Boards__MaxImageBytes`). Stored in PNMT's files folder and checked as knowledge base images are. ***Contain*, *Cover* or *Original size*,** a background, a caption and an alternative text. **With *Motion* off, an animation shows its first frame** (§9.4) |
| **B. On duty today** | **The responsible and up to 5 deputies** from the chain (1 by default). **Labels:** *Responsible*, *Deputy*, and the text for nobody. **Options:** the role, where each works today (on site, home office, travelling), the day's comment, tomorrow, the name size, stacked or in one line (§9.5) |

---

## 2. Today

| Area | What there is | Where |
|---|---|---|
| **Boards (1.23)** | **A board polls its view** every 10 to 300 s (30 by default), paused while hidden, and the lounge every 10 s (2 s while a voice types). **Access:** the token in the `Authorization` header, then *enabled*, a signed-in user, the networks. **Every request counts against one budget per address:** `Boards__RequestsPerMinute` (120), shared by every board and screen behind that address | `PNMTD/Boards/BoardAccess.cs:40-60`, `BoardTokens.cs:105-111`, `src/board-app/polling.ts` |
| **The layout** | One JSON document per board. `BoardLook(Theme, Fit, TextScale, Motion, People)`; *People* is *Names*, *Initials* or *Hidden*. **The elements' settings are one flat record,** normalised per type on save | `Contracts/Boards.cs:63-125`, `Boards/BoardLayouts.cs` |
| **The board app** | **No PrimeVue and no icon font:** the bundle guard fails the build above 250 kB gzipped (76 kB today). Themes are CSS on `.board[data-board-theme=…]` with the board's variables. The status colours are fixed. **The board UI's CSP allows `img-src 'self' data:`** | `vite.board.config.ts`, `src/boards/themes/`, `Hosting/HostingSetup.cs:63` |
| **Times and dates** | **`Intl.DateTimeFormat` with the browser's locale** (`undefined`) and PNMT's zone. **So the hour cycle is the browser's:** 1.23's screenshots show "10:35:46 PM" from an English browser, and a German one shows "22:35". Dates are "Monday, 24 November" in the element and "24 November" in the header. **There are no settings for either** | `src/boards/time.ts`, `BoardClock.vue`, `elements/ClockElement.vue` |
| **Sensor messages on a board** | **Only in *Failing sensors*,** with its *Messages* switch, after the host and sensor. No element shows one chosen sensor's message | `Boards/BoardData.cs:195-209` |
| **On duty today** | **One step of the chain:** `ResponsibleOn(day)` gives the first who works (the day's responsible, else the day's fallback, else the global fallback person, else the global fallback group's working members), and how ("responsible", "fallback", "global fallback"). **No deputies, no options.** The availability plan also has a comment per day, and types with *On site*, *Home office* and *Work travel* | `Availability/AvailabilityPlan.cs:111-143`, `Boards/BoardData.cs:487-495`, `Models/Db/AvailabilityEntities.cs` |
| **Files** | **The files folder** (`KnowledgeBase__FilesPath`) stores each content once. **Images (PNG, JPEG, GIF, WebP) are checked by their bytes, at most 10,000 px a side and 40 megapixels, and stripped of metadata.** Only GIF comments and WebP EXIF/XMP go, so **animations and their looping are kept**. Images are at most 2 MB (`KnowledgeBase__MaxImageBytes`). **A file is kept while a `file_uses` row points at it** (a page, a location or a responsibility, exactly one of them); unused uploads are marked after 24 h and removed 7 days later. The files are in the backups | `Files/FileStore.cs`, `Files/ImageCheck.cs:259-312, 413`, `Models/Db/FileEntities.cs`, `Files/FileTasks.cs:55-113` |
| **Admin → Debug** | **System: Manage.** Panels for a test Pushover, a test mail and the AI test. **At most 10 sends a minute per user** (`AuthSetup.DebugRateLimit`), each in the audit log (`DebugNotificationSent`) | `views/admin/DebugView.vue`, `Api/V1/DebugController.cs:13-26`, `Auth/AuthSetup.cs:142-144` |
| **Rights** | Ordered levels per area. ***Boards*: View, Manage.** The `Level` enum is stored by name (`"Boards:Manage"`) | `Auth/Rights/Rights.cs:20, 104-106` |
| **Cookie's actions** | `CookieActionKind` with a level each (`ActionLevels`: *Off*, *Propose*, *Auto*, and a minimum confidence). **Output limits** per day with ceilings in `.env` (`OutputLimits`, `Cookie__Ceilings__*`). **The executor** checks a proposal (its targets, Cookie's rights, mute and suppress, limits), **Seraph reviews it**, then it goes to the decisions inbox (*Propose*) or runs (*Auto*). **The rights it needs come from Cookie's group:** a ticket needs *Redmine: Link*, a note on a host *Monitoring: View* | `Cookie/CookieActions.cs`, `CookieSettings.cs:216-300`, `CookieExecutor.cs:89, 426-560` |
| **Cookie in the lounge** | Tools of the lounge's own (`remember`, `open_case`, `send_email`). `send_email` goes through the executor with the lounge's level, checks, and a review by the other voice's model | `Cookie/Lounge/LoungeTools.cs:48-70, 266-290`, `CookieExecutor.cs:227-320` |
| **Suppressing a channel** | Instructions can suppress a channel: `pushover`, `email`, `ticket`, `redmine-note`, `access`, `away` | `Cookie/Instructions.cs:26-27` |
| **The decisions inbox** | Cookie's proposals whose areas are all in *Monitoring*, *Mail* or *Cookie* go to anyone with *Decisions: Decide*; others are for admins only | `CookieExecutor.cs:46, 212` |

---

## 3. Requirements

**A. Messages**
1. **Message-box style, but large,** taking up a big part of the screen. **Kinds** such as alert, information and warning, **a title and a message.**
2. **Sent to a board.** One message can go to several boards (§4.3).
3. **Shown for 60 s by default;** the sender sets another duration within a range.
4. **Sent by people from `/pnmt-admin/admin/debug`.**
5. **Sent by Cookie,** through the same channel. **A board must be chosen for Cookie, and the right given,** as for Cookie's other channels.

**B. Elements**

6. **A sensor's last message as an element,** with different sizes.
7. **Every clock can be set to 24 or 12 hours.**
8. **Different ways to show the date.**
9. **Images and GIFs as an element,** uploaded.
10. ***On duty today* with more options:** how many deputies are shown, and options for its texts.

**Both**

11. **They fit the boards as built:** their own path, the token, the networks, the themes, and a bundle with no admin UI code in it (docs/boards-plan.md §4).

---

## 4. A message

### 4.1 Kinds

| Kind | Colour | Icon | For |
|---|---|---|---|
| **Alert** | The status red | An octagon with "!" | An outage or an incident that affects many people now |
| **Warning** | The status yellow | A triangle with "!" | Something degraded, or about to be |
| **Maintenance** | Violet | A wrench | Planned work: starting, running, done soon |
| **Information** | Blue | An "i" in a circle | Anything the room should know |
| **Success** | The status green | A check mark | All clear, resolved |

- **The order of importance:** Alert, Warning, Maintenance, Information, Success. It decides which open message a board shows first (§5.3).
- **The colours of Alert, Warning and Success are the status bar's,** which no theme changes (docs/boards-plan.md §9.4). Violet and blue are two new board variables, also fixed. A theme changes the box's frame, not what a colour means (§5.4).
- **The icons are five small inline SVGs** in `src/boards/`. The board app has no icon font, and gets none.

### 4.2 Content

- **Title:** 1 to 100 characters, plain text, one line.
- **Text:** up to 1000 characters, optional. **Markdown as in a note element:** bold, italics, lists, line breaks and inline code; no images and no raw HTML; links shown as text. The same sanitiser (`src/boards/boardMarkdown.ts`).
- **Duration:** **60 s by default; from 10 s to 60 min.** The admin UI offers 30 s, 1 min, 2 min, 5 min, 10 min, 30 min and 1 h, or any number of seconds in the range. **The upper bound is a setting,** `Boards__MessageMaxMinutes` (60, at most 1440).
- **Who sent it,** kept with it: the user (id and display name) or Cookie (with its decision and case).

### 4.3 Targets

- **One or more boards, or *All boards that show messages*.** "All" is resolved when sending; a board added later doesn't get it.
- **A target must exist, be enabled, and show messages** (§5.5). One that is switched off later stops showing its messages at its next poll.
- **At most 10 open messages per board.** The 11th is refused with the board's name; withdraw one first.

### 4.4 Open, ended, withdrawn

- **A message is open from when it is sent until its time is up,** on every board it went to. A board that opens while it is open shows it for the time that is left.
- **Withdraw** ends it everywhere at the board's next poll. Anyone with *Boards: Message* may withdraw any open message; withdrawing is in the audit log.
- **A sent message isn't changed.** Withdraw it and send another.
- **Deleting a board** ends its messages there; messages sent to other boards as well stay open on those.

---

## 5. On the board

### 5.1 The box

```
┌──────────────────────────── status bar (unchanged, still visible) ─────────────────────────────┐
│                                                                                                │
│      ┌────────────────────────────────────────────────────────────────────────────────┐        │
│      │ ▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌ ALERT ▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌▌ │        │
│      │                                                                                │        │
│      │    (!)   Core switch down in building B                                        │        │
│      │                                                                                │        │
│      │          Phones and Wi-Fi in building B are out. We are on it;                │        │
│      │          next update at 14:45.                                                 │        │
│      │                                                                                │        │
│      │   Anna Muster · 14:32                              1 / 2 · closes in 0:42      │        │
│      │   ████████████████████████████████████████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │        │
│      └────────────────────────────────────────────────────────────────────────────────┘        │
│                                (the board, dimmed, behind it)                                  │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Size:** *Large* is 80 % of the width and 70 % of the height of the board below (or above) the status bar, centred. *Full* is the whole of that area with a small margin. **Below 700 px of width** (a phone), the full width and as tall as the text needs.
- **The status bar stays visible,** above or below the box as the board has it. A message never changes the bar's colour.
- **The board behind is dimmed to about 40 %,** not blurred: a blur costs a TV's weak GPU more than it is worth.
- **The kind's band across the top,** with its name ("ALERT"), then **the icon and the title** in large type. **Then the text.** At the bottom **who sent it and when** (in the board's time and date format, §9.1), **"1 / 2"** when several are open, **"closes in 0:42"**, and **a bar that drains** over the message's time.
- **Who sent it follows the board's *People* setting:** a name, initials, or nothing. **Cookie always by its name** (*Admin → Cookie → Identity*), with "(Cookie)" when renamed.

### 5.2 Text size

- **The title** starts at 7 % of the box's height. **The text** starts at 4.5 % and steps down to 2 % until it fits. Below that it is cut with "…". 1000 characters fit at 1080p (a test, §15).
- **The sizes come from the box's height,** not the board's *Text size*, so a message is as readable on a board with small text as on one with large text.
- **Web fonts:** measured again when they are loaded, as `FitList` does. **The same fitting as *Fit* in a *Sensor message* element** (§9.3): one helper, `src/boards/fitText.ts`.

### 5.3 Several open messages

- **The most important kind first, then the newest** (§4.1).
- **A message that arrives is shown at once,** for one turn, and then takes its place in that order.
- **With two or more open, the box rotates every 15 s,** with "1 / 3" and a dot per message. Each counts down on its own: one that ends leaves the rotation.
- **The stamp changes when a message is sent or withdrawn** (§6.2). A message that ends by its time is removed by the board's own countdown.

### 5.4 Themes and motion

- **The box uses the board's variables** for its font, background, text, border and radius, plus `--board-msg-violet` and `--board-msg-blue`. **Themes with a look of their own frame it as their era's dialog:**
  - *Windows 95* and *Windows XP*: a title bar with the kind's name and a close button that does nothing;
  - *Terminal*: a box of line-drawing characters in the terminal's colour;
  - *Amiga*: an Alert whose border blinks red, as the Amiga's alerts did.
  - The others: a card with the kind's band.
- **With *Motion* on:** the box scales in over 200 ms, and an Alert's frame pulses slowly (every 2 s). **With it off, or with `prefers-reduced-motion`:** none of that.
- **While an Alert or a Warning is open,** the tab's title starts with "⚠" and the message's title, as the bar's colour does with the icon. This helps when a board is a tab among others.

### 5.5 Per board: *Look → Messages*

- **Show messages sent to this board:** on by default, also for boards made before 1.24. A board with messages off isn't offered as a target, to people or to Cookie.
- **Size:** *Large* (default) or *Full*.
- **Preview a message:** shows a sample in the editor's preview, of a kind you pick, with your text or a sample text. It stays in the editor.
- **The layout gains `look.messages` and `look.messageSize`;** a layout without them reads as on and *Large*. No data migration.

### 5.6 Closing the box on a screen

- **Esc, or a click on the ✕, hides it on this screen** until a new message arrives. A wall has no keyboard, so there it stays until its time is up; a person looking at a board on a laptop can close it. Nothing is sent to the server.

---

## 6. Delivery

### 6.1 The poll

- **`GET /pnmt-board/api/messages` every 5 s** while the page is visible, at once when it becomes visible, and at once after the view's first answer. **Paused** while the view's poll is refused (wrong network, renewed, deleted, switched off), until the view comes back.
- **The same access check as the view** (docs/boards-plan.md §5): the token in the header, enabled, a signed-in user or the networks. A board with messages off answers an empty list.
- **The answer:** the board's open messages, each with its id, kind, title, text, who sent it (as the board's *People* setting allows), when, its length in seconds, and **`remainingSeconds`**. The board counts down from the moment the answer arrived, not from the time on the wall's clock.
- **Within about 5 s,** plus the network. Push (a WebSocket or a long poll) would win a few seconds and lose the plain GET that every proxy passes (docs/boards-plan.md §6.4). A long poll is in §17, should 5 s turn out too slow.

### 6.2 The stamp

- **`BoardRuntime` keeps a stamp per board,** a number that goes up when a message to that board is sent or withdrawn, and when the board's *Messages* setting changes. **The ETag is the process's start id and the stamp,** so that after a restart no board gets a *304* for what it had before.
- **With `If-None-Match` and an unchanged stamp, the answer is *304* with no body and no database read.** A board that is open on ten screens costs ten tiny answers every 5 s, and one query per change.
- **A message whose time is up** doesn't change the stamp: the board's countdown removes it, and the next full answer no longer has it.

### 6.3 What a board asks per minute

| Poll | Requests a minute, per screen |
|---|---|
| The view (30 s) | 2 |
| The messages (5 s) | 12 |
| The lounge, if shown (10 s; 2 s while a voice types) | 6, up to 30 |
| **Together** | **14 to 44** |

Images (§9.4) are fetched once per screen, and again only when the board's layout names another file.

### 6.4 The rate limit (fixed on the way)

- **Today every board request counts against one budget per address, 120 a minute.** Screens behind one NAT address, or behind a reverse proxy without `Proxy` set (which makes them all the proxy's address), share it. Four screens with the lounge while a voice types already come close; with the messages poll, three screens would be over it.
- **The fix:**
  - **a known token counts per address and board;**
  - **the default goes to 600 a minute** (`Boards__RequestsPerMinute`): about 13 screens of one board behind one address;
  - **an unknown token** keeps its own block after ten tries (docs/boards-plan.md §5.4). That guard is what stops guessing; this limit is only against a flood.
- **A *429* on the messages poll** waits for the `Retry-After` and doesn't touch the view's state.

---

## 7. Sending in the admin UI: *Admin → Debug*

### 7.1 The panel *Send a board message*

```
┌─ Send a board message ─────────────────────────────────────────────────────────────────────────┐
│ Boards     [NOC ✕] [Helpdesk TV ✕]  ▾          ○ All boards that show messages (3)              │
│            NOC: from anywhere ⚠ · Helpdesk TV: 10.0.0.0/8, 192.168.0.0/16                       │
│ Kind       [▲ Warning               ▾]                                                          │
│ Title      [Mail delays                                                    ] 11 / 100           │
│ Message    [Outbound mail is delayed by about 20 minutes. Nothing is lost. ]                    │
│            [                                                               ] 62 / 1000          │
│            Markdown: **bold**, *italics*, lists.                                                │
│ Show for   (30 s) (●1 min) (2 min) (5 min) (10 min) (30 min) (1 h) [   60 ] s                   │
│                                                                                                 │
│ ┌ Preview, in NOC's theme ───────────────────────────┐                                         │
│ │  (the box, as the board will show it, scaled down) │                                         │
│ └────────────────────────────────────────────────────┘                                         │
│                                                                     [ Send to 2 boards ]        │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
┌─ Open board messages ──────────────────────────────────────────────────────────────────────────┐
│ ▲ Warning  Mail delays       NOC, Helpdesk TV   Anna Muster · 14:32   0:42 left   [Withdraw]    │
│ ■ Alert    Core switch down  NOC                Cookie · 14:20        8:15 left   [Withdraw]    │
├─ Sent in the last 30 days (the last 20) ───────────────────────────────────────────────────────┤
│ ...                                                                                             │
└─────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **The boards** come from *Boards* with *Messages* on. **Each one's reach is shown under the picker:** its networks, or "from anywhere" with a warning sign. A message on a board that is open from anywhere can be read by anyone with its URL.
- **The preview is the board app's own component,** `MessageBox.vue` from `src/boards/`, as the editor's preview is. It is drawn in the first chosen board's theme and box size.
- **Send:** "Shown on NOC and Helpdesk TV until 14:33:10." A refusal names its reason: a board switched off, 10 open on a board, the rate limit.
- **The open messages** refresh every 10 s, each with its time left and ***Withdraw*** (after a confirmation). **The last 20 sent** in the 30 days kept, with who withdrew one, if anyone did.
- **The Debug page's limit applies:** at most 10 sends a minute per user, shared with the test Pushover and the test mail.

### 7.2 Who sees the panel (decision 1)

- **The Debug page opens for *System: Manage* or *Boards: Message*.**
  - The Pushover, mail and AI panels need *System: Manage*, as now.
  - The board message panels need *Boards: Message*; with *Boards: View* only, the open messages are listed without *Withdraw*.
  - *Admin → Debug* is in the menu for either right.
- **The page's text** says what it now is: "Tools that send through PNMT's channels, to check them or to tell a room something."
- **A *Send a message* button on *Boards*** stays in §17.

---

## 8. Cookie

### 8.1 The two gates, and the level

- **Boards Cookie may send to:** a new field under *Admin → Cookie → Actions → Board message*. **None by default:** until an admin picks one, Cookie can't send any.
- **The right:** **Cookie's group needs *Boards: Message*,** as a ticket needs *Redmine: Link* (*Admin → Cookie → Rights*). Without it, the executor refuses the proposal, and the Actions tab says why ("Cookie's group needs Boards: Message").
- **The level:** a new entry in `ActionLevels`, *Board message*, **Propose by default** like Cookie's other actions that reach people, with a minimum confidence of 0.7. With no board chosen or without the right, the Actions tab shows it as unavailable, as it does a ticket without a project.
- **How long Cookie's messages show:** a setting next to it, **60 s by default, up to 10 min** (and never above `Boards__MessageMaxMinutes`).

### 8.2 When Cookie sends one

- **The brief lists the boards when the action is available:** `BoardsYouMayMessage`, each with its id, name and description. Otherwise Cookie doesn't hear of boards.
- **The instructions (Cookie's prompt):**
  - a board message is for **what a room of people should know now**: an outage or incident that affects many, a planned maintenance as it starts, and an all-clear afterwards;
  - **never for one sensor's routine failure**, and **at most one per incident and board**;
  - **no people's names, no customers, no addresses, no credentials:** a board may hang in a corridor;
  - the kind: *urgent* → Alert, *important* → Warning, *info* → Information; planned work → Maintenance; resolved → Success.
- **`ProposedAction` gains `BoardKind`** ("For board-message: alert, warning, maintenance, information or success; empty otherwise"). **The board's id goes in `ObjectId`.** Subject → title (up to 100), text → text (up to 1000).

### 8.3 The executor's checks

- **The board:**
  - in Cookie's list, existing, enabled, and showing messages;
  - Cookie's group holds *Boards: Message*;
  - the level isn't *Off*;
  - the channel isn't muted or suppressed (`board-message` joins `Instructions.Channels`).
- **Limits** (`OutputLimits`, with ceilings in `.env`):
  - `BoardMessagesPerDay`: 10 (ceiling 50);
  - `BoardMessagesPerBoardHour`: 2 (ceiling 10).
- **No duplicate:** one of Cookie's messages from the same case and to the same board that is still open, or waiting in the inbox, refuses the next one. **A *Success* is the exception:** it ends Cookie's open message from that case on that board and takes its place.
- **Working hours don't apply.** A wall isn't a person: an outage at 3 a.m. is shown to the night shift.

### 8.4 Seraph's review

- **In *Auto*, Seraph reviews every board message before it goes out,** as for Pushover. **The evidence tells Seraph the board's reach:** its networks or "open from anywhere: anyone with its URL reads it", and its *People* setting.
- **Seraph rejects** a message that names a person, a customer or an address, or that isn't something the room should know now.
- **Revise or Ask** sends it to a person (the inbox), as for the other actions.

### 8.5 *Propose*: the decisions inbox

- **A board message waits in the inbox at most 30 min.** After that, the decision expires ("A board message is about now; this one is 30 minutes old"), and it is logged as *expired*.
- **Board messages are for admins in the inbox,** as every proposal outside *Monitoring*, *Mail* and *Cookie* is (§2). *Boards* doesn't join the inbox areas, because a decider would also need *Boards: Message*, which the inbox doesn't check.
- **On approval** it is sent with the duration Cookie proposed; the person who approved is in the audit entry.

### 8.6 In the lounge (decision 4)

- **The tool `send_board_message`** joins the lounge's own tools (`remember`, `open_case`, `send_email`). **It is offered to a voice only when:**
  - the person the voice answers holds *Boards: Message*;
  - the action's level isn't *Off*;
  - Cookie has a board.
- **Through the executor, like `send_email`:** the same checks and limits; **the review by the other voice's model**, never its own; *Propose* or *Auto*.
- **The voice says what came of it:** "Shown on NOC for 1 min", "A person decides first", or the refusal.

### 8.7 What is written

- **The message:** `Source = Cookie`, the sender's name is Cookie's name, linked to the decision and the case.
- **The decision log:** "Board message by Cookie to NOC: Core switch down", with its outcome.
- **The audit log:** *Board message sent*, with Cookie as the actor; for an approved one, with the person who approved.

---

## 9. B. More for the elements

### 9.1 Time: 24 or 12 hours

- ***Look → Time*: *24-hour* (default), *12-hour*, or *As the screen's browser*.** It applies to every time on the board:
  - the header's clock and the status bar's "since";
  - *Clock* elements and their other zones;
  - the times in lists (tickets, Zabbix, recent changes, the lounge);
  - the message box.
- **Each clock can override it.** A *Clock* element gets *Time: As the board / 24-hour / 12-hour*. The header's clock gets the same choice under *Header*. A board can show a 24-hour clock for the room and a 12-hour one for a US office next to it.
- **12-hour:** "2:35 PM", with "PM" at 60 % of the size in a *Clock* element; "2:35 PM" in lists.
- **How:** `boardTime(zone, { hourCycle })` gets `h23` or `h12` (or none, for the browser's). `Intl.DateTimeFormat` does the rest.
- **The default changes how boards from 1.23 look on an English browser:** "22:35" instead of "10:35 PM" (decision 6, §19).
- **The layout gains `look.timeFormat`, `header.timeFormat` and the element setting `timeFormat`;** missing means 24-hour, *As the board* and *As the board*.

### 9.2 Dates

- ***Look → Date*: the format**, for the header's date, a *Clock* element's date, and dates in lists where something is older than this week:

| Format | Example (English) |
|---|---|
| **Weekday, day month** (default) | Monday, 24 November |
| Short | Mon 24 Nov |
| Day month year | 24 November 2026 |
| Weekday, day month year | Monday, 24 November 2026 |
| Dotted | 24.11.2026 |
| Weekday, dotted | Mon 24.11.2026 |
| ISO | 2026-11-24 |
| US | 11/24/2026 |

- **The week number:** on or off ("Week 48", ISO 8601, which `Intl` doesn't give: computed in `time.ts`).
- **The language of day and month names:** *English* (default), *Deutsch*, *Français*, *Italiano*, or *As the screen's browser*. "Montag, 24. November", "lundi 24 novembre". **Only these names change.** The board's own words ("since", "closes in", "Nobody is on duty") stay English (§17).
- **A *Clock* element can override the format** (*As the board* or one of the eight), and **turn the date or the week number off**, as now.
- **The layout gains `look.dateFormat`, `look.weekNumber`, `look.dateLanguage` and the element setting `dateFormat`;** missing means today's formats, so 1.23's boards look as before.

### 9.3 *Sensor message*

```
┌ web-01 / queue ──────────────────────┐   ┌ fw-01 / temperature ─────┐
│ Queue: 12 mails waiting,             │   │                          │
│ oldest 3 min                         │   │        41.5 °C           │   (Fit, centred)
│                       12 min ago     │   │                          │
└──────────────────────────────────────┘   └──────────────────────────┘
```

- **A new element in *Monitoring*: one sensor's last message,** as its last event left it (`LastMessage`). **Its title defaults to "host / sensor".**
- **Size:**
  - *Small*, *Medium*, *Large* and *Huge* are 1×, 1.5×, 2.5× and 4× the board's text size;
  - ***Fit*** is as large as the element's box allows, found by a binary search between 0.8× and 20×. It is measured again on a resize, a new message, and once web fonts are in (`fitText.ts`, shared with the message box, §5.2).
  - **In a page that scrolls,** *Fit* has no height to fill; it means as large as the column's width allows, at most 6×.
- **Options:**
  - **At most so many lines** (1 to 20, 6 by default; not with *Fit*), cut with "…";
  - **Monospace,** for a script's output;
  - **Alignment:** left or centred;
  - **The state's colour:** *None*, *Bar* (a band on the left in the state's colour; the default) or *Background* (the whole element);
  - **How old it is** ("12 min ago"), on by default.
- **Plain text:** a sensor's message comes from outside PNMT. Line breaks are kept; no Markdown and no links. It is cut at 2000 characters.
- **No message:** "No message". **No data:** "No data yet". **A deleted sensor:** "This sensor no longer exists" (`BoardTileState.Missing`).
- **Rights:** adding it needs *Monitoring: View*, as the other monitoring elements.
  - **No extra right for an advanced sensor whose data names people** (`NamesPeople`): its messages already come without the values (docs/advanced-sensor-plan.md §3).
  - Its events, with those messages, are *Monitoring: View* everywhere else.
- **The editor warns** under the sensor picker: "The message is shown as the sensor sent it, to everyone who opens this board."
- **Settings:** the sensor in `targets` (one sensor), `textSize`, `rows` (the lines), `monospace`, `center`, `stateColour`, `age`. **The data:** `BoardSensorMessage(SensorId, Host, Name, State, Message, At)`.

### 9.4 *Image*

```
┌ (no title) ─────────────────────────────┐
│                                         │
│        [ the uploaded image or GIF ]    │   Contain · black background
│                                         │
│   Our new office in Zurich              │   (caption)
└─────────────────────────────────────────┘
```

- **A new element in *General*: an uploaded image.** PNG, JPEG, WebP or GIF, **animated GIFs and WebPs included.** They keep their frames and looping (§2).
- **Upload in the element's settings:** a button, drag and drop, or paste. Each image is checked by its bytes, stripped of metadata and stored once in PNMT's files folder, as a knowledge base image is (`FileStore`, `ImageCheck`).
  - **At most 10 MB** (`Boards__MaxImageBytes`). This is larger than a page's 2 MB, because an animated GIF often is.
  - The same pixel limits as the knowledge base.
  - The same uploads-per-hour limit (`UploadGuard`).
- **Or pick one of the board's images:** the settings list the images this board already uses, with their size and dimensions.
- **Options:**
  - ***Contain*** (all of it, the default), ***Cover*** (fills the element, cropped) or ***Original size*** (centred, scaled down only when larger);
  - **a background:** *None*, *Black* or *White*;
  - **a caption**, optional, under the image;
  - **an alternative text**, for screen readers; by default the caption or "Image".
- **In a page that scrolls,** the image is as wide as its column, and as tall as its aspect ratio makes it, at most 80 % of the window's height. **With *Fit*,** it fills its share of the column.
- ***Motion* off, or `prefers-reduced-motion`:** an animated image shows its first frame. It is drawn once to a canvas with `createImageBitmap`; a GIF in an `<img>` can't be paused.
- **How the board gets it:**
  - `GET /pnmt-board/api/images/{fileId}`, with the token in the header like every board request; only a file the board's layout uses.
  - **The board app fetches it and shows it from a `blob:` URL.** An `<img src>` can't send the header, and the token stays out of URLs (docs/boards-plan.md §5.1). **The board UI's CSP gains `img-src blob:`.**
  - **Files never change**, so the answer may be cached (`private, max-age=31536000, immutable`). A screen fetches each image once.
- **The admin's preview** loads it from `GET /api/v1/boards/{id}/images/{fileId}` (*Boards: View*; only a file that board uses).
- **Uses:** `file_uses` gains `board_id`. **Uploading for a board records a use at once,** so the preview works before saving. **Saving a board's layout makes its uses match the images it names.** A file no board, page, location or responsibility uses then goes the usual way: marked after `KnowledgeBase__UnusedFileHours` (24), removed 7 days later. **Duplicating a board copies its uses; deleting a board drops them.** The check "exactly one owner" on `file_uses` gains `board_id`.
- **The files folder must be set up,** as for the knowledge base (deploy/README). Without it, the upload answers *503* with the knowledge base's text.
- **Rights:** *Boards: Manage*, as every element; no other area's right.
- **Settings:** `fileId`, `imageFit`, `background`, `caption`, `alt`.

### 9.5 *On duty today*

```
┌ On duty today ─────────────────────────┐      ┌ Pikett ───────────────────────────────────────────┐
│ Anna Muster                            │      │ Anna Muster · Ben Keller · Carla Rossi            │  (one line)
│ Responsible · on site                  │      └───────────────────────────────────────────────────┘
│                                        │
│ Ben Keller          Deputy · home      │
│ Carla Rossi         Deputy · on site   │
│                                        │
│ "Server room closed from 16:00"        │   (the day's comment)
│ Tomorrow: Ben Keller                   │
└────────────────────────────────────────┘
```

- **The chain** (*Words*): the day's responsible, the day's fallback, the global fallback person, then the global fallback group's members, only those who work that day, each person once. **The first is on duty; the next ones are the deputies.** A new `PlanSnapshot.ChainOn(day)` gives them in order; `ResponsibleOn(day)` stays as it is for Cookie and the case book.
- **Options:**

| Option | Values | Default |
|---|---|---|
| **Deputies shown** | 0 to 5 | 1 |
| **Label for the one on duty** | text, up to 40 characters (e.g. "Pikett") | how they are on duty: "Responsible", "Fallback" or "Global fallback" |
| **Label for a deputy** | text, up to 40 (e.g. "Stellvertretung") | "Deputy" |
| **Text when nobody is on duty** | text, up to 100 | "Nobody is on duty today." |
| **Text on a closed day** | text, up to 100 | "Closed today." |
| **Show the role** | on, off | on |
| **Where each works today** | on, off: "on site", "home office", "travelling", from their day's type | off |
| **The day's comment** from the availability plan | on, off | off |
| **Tomorrow** | on, off: "Tomorrow: Ben Keller" (the first of tomorrow's chain) | off |
| **Name size** | *Medium*, *Large*, *Huge*; deputies one step smaller | *Large* |
| **Layout** | *Stacked* or *One line* | *Stacked* |

- **Names follow the board's *People* setting.** With *Hidden*, the element shows the labels without names, and the editor says so.
- **The day's comment is opt-in:** whoever fills in the plan writes it for the team, not for a wall.
- **Rights:** *Availability: View*, as now.
- **Settings:** `deputies`, `dutyLabel`, `deputyLabel`, `nobodyText`, `closedText`, `role`, `where`, `comment`, `tomorrow`, `nameSize`, `oneLine`. **The data:** `BoardOnDuty` gains the people as `(Name, IsDeputy, As, Where)`, the comment and tomorrow's first person. **Old layouts read with 0 deputies,** so a 1.23 board looks as before until someone changes it. *New* elements get 1.

---

## 10. Data model and migrations

**`board_messages`** (migration `BoardMessages`):

| Column | Type | |
|---|---|---|
| `id` | uuid | |
| `board_ids` | uuid[] | The targets, as sent; GIN index |
| `kind` | text | `Alert`, `Warning`, `Maintenance`, `Information`, `Success` |
| `title` | varchar(100) | |
| `text` | varchar(1000) | |
| `seconds` | int | 10 to `MessageMaxMinutes × 60` |
| `sent_at` / `ends_at` | timestamptz | `ends_at` indexed |
| `source` | text | `Person`, `Cookie` |
| `sent_by_id` / `sent_by_name` | uuid null / text | The user, or null for Cookie. The name as it was |
| `approved_by_id` / `approved_by_name` | uuid null / text null | For a Cookie proposal a person approved |
| `cookie_decision_id` / `cookie_case_id` | uuid null | |
| `withdrawn_at` / `withdrawn_by_id` / `withdrawn_by_name` | null | Also set when Cookie's *Success* ends one (by Cookie) |

- **Open:** `withdrawn_at is null and ends_at > now()`, and the board in `board_ids`.
- **Kept `Boards__MessageHistoryDays` (30) after `ends_at`.** The cleanup runs when a message is sent, at most once every 6 hours, as the Zabbix history's does. **No new background service.**

**`file_uses.board_id`** (migration `BoardImages`): uuid null, a foreign key to `boards` with *cascade on delete*, an index. The check that a use has exactly one owner now counts four columns.

**The layout** needs no migration. Every new field has a default that keeps a 1.23 board as it was, except the time format (§9.1, decision 6):
- `look`: `messages`, `messageSize`, `timeFormat`, `dateFormat`, `weekNumber`, `dateLanguage`;
- `header`: `timeFormat`;
- elements: the settings of §9.1 to §9.5.

**The element types** gain `SensorMessage` and `Image`. The flat `BoardElementSettings` gains their settings and *On duty*'s. **Saving normalises them per type,** as it does today: a setting that doesn't belong to the type is dropped.

**Cookie:**
- `ActionLevels.BoardMessage`;
- `BoardMessageSettings(BoardIds, Seconds)` in the *actions* section;
- two `OutputLimits`.

These are all in Cookie's JSON settings, with defaults for what isn't there yet.

---

## 11. API

| Method and path | Right | |
|---|---|---|
| `GET /api/v1/boards/messages?open=true` | Boards: View | The open messages, with their boards' names and the time left |
| `GET /api/v1/boards/messages?limit=20` | Boards: View | The last sent (30 days) |
| `POST /api/v1/boards/messages` | Boards: Message | `{ boardIds, allBoards, kind, title, text, seconds }` → *201* with the message. Rate-limited with the Debug page's policy. Audited |
| `POST /api/v1/boards/messages/{id}/withdraw` | Boards: Message | *204*. Audited |
| `GET /api/v1/boards` | Boards: View | Each board gains `showsMessages` and its reach (*anywhere* or its networks' count), for the picker |
| `POST /api/v1/boards/{id}/images` | Boards: Manage | The upload (the body is the image). *201* with the file's id, type, size and dimensions; records the use. *413*, *415*, *429*, *503* as the knowledge base's uploads |
| `GET /api/v1/boards/{id}/images` | Boards: View | The images the board uses |
| `GET /api/v1/boards/{id}/images/{fileId}` | Boards: View | The image, for the preview; only a file the board uses |
| `GET /pnmt-board/api/messages` | The board's token (docs/boards-plan.md §5) | `ETag` and *304* (§6.2). In the OpenAPI document, for the board app's types |
| `GET /pnmt-board/api/images/{fileId}` | The board's token | The image; only a file the board uses. Cached as immutable (§9.4) |

**Errors** (`ApiErrors`):
- *400*: a field with what's allowed;
- *404*: a board or file that doesn't exist, or isn't the board's;
- *409*: a board switched off, with messages off, or with 10 open;
- *429*: the rate limit.

---

## 12. Rights and audit

### 12.1 *Boards: Message*

- **The levels become View, Message, Manage.** *Message*: "View, plus sending messages to boards (Admin → Debug) and withdrawing open ones."
- **`Level.Message` is added to the end of the enum.** Rights are stored by name, so no stored right changes. A group with *Boards: Manage* gets *Message* through the order; a group with *View* doesn't.
- **The endpoint audit test's matrix** gains the new endpoints. Groups' tests count the new level.
- **The new elements' rights** (§9.3 to §9.5) go in the element catalog, as for the others; §7.2 of the boards plan (a removed right doesn't remove an element someone else added) applies.

### 12.2 Audit events

| Event | Tone | What it says |
|---|---|---|
| ***Board message sent*** | Notable | The boards, the kind, the title, the duration and the text; the actor (a person, or Cookie with the person who approved) |
| ***Board message withdrawn*** | Normal | The message's title and boards, who withdrew it, and how long it had left |

- **An image upload** is in the auth log as every file upload is. Adding it to a board is in *Board changed*'s summary of the layout ("Image added to column 2").

---

## 13. Privacy and security

- **A board may hang in a corridor, or be open from anywhere.**
  - The message panel shows each board's reach (§7.1).
  - Cookie's instructions and Seraph's review keep names, customers and addresses out (§8.2, §8.4).
  - The *Sensor message* element warns that the message is shown as sent (§9.3). A sensor whose data names people sends its messages without the values.
  - *On duty* shows the day's comment only when someone turns it on (§9.5).
- **A message's text is sanitised Markdown** on the board, as a note element's is; the title is plain text. **A sensor's message is plain text.** No HTML, no images in text, no links to follow.
- **Images:**
  - checked by their bytes and stripped of metadata;
  - served only to a board that uses them, by its token, never by a URL that carries a secret;
  - the CSP allows `blob:` for images only.
- **The board's polls carry the token in the header** (no content and no token in a URL). Messages are answered `no-store`, as the view is.
- **Who sent a message follows the board's *People* setting** (§5.1).
- **Abuse by someone with the right:** the audit log, the rate limit, 10 open per board, and 60 min at most.

---

## 14. Compatibility: what must not break

- **Boards from 1.23:**
  - their layouts read with messages on and *Large*, today's date formats, *On duty* with no deputies;
  - **the time is 24-hour,** where an English browser showed 12-hour before (decision 6);
  - a screen with the 1.23 app reloads itself after the update (the version check, docs/boards-plan.md §6.4), and then polls messages.
- **The rate limit's new default** only raises the budget; an admin's own `Boards__RequestsPerMinute` stays as set and now counts per address and board.
- **Rights:** existing groups keep what they have. *Boards: Manage* now also means *Message*, which it could do in effect before by editing a note element.
- **Cookie:**
  - `ProposedAction` gains a nullable `BoardKind`; decisions in the log without it read as before;
  - the level defaults to *Propose*, but with no board chosen nothing changes until an admin picks one.
- **The Debug page's route** opens for one more right; what it showed before is unchanged for *System: Manage*.
- **Files:**
  - the knowledge base's 2 MB limit stays;
  - `file_uses`' new column is null for every existing row;
  - the cleanup's rule "used by anything" now includes boards.

---

## 15. Testing

- **.NET:**
  - **Messages:**
    - sending, validating and withdrawing, with the rights matrix;
    - "all boards" resolved at sending;
    - 10 open per board; a board switched off or with messages off;
    - the board's poll: access as the view's, *304* on an unchanged stamp with no query (counted), the stamp after a send, a withdraw and a settings change, a new ETag after a restart, `remainingSeconds` from a fake clock;
    - cleanup after 30 days;
    - the rate limit per address and board, an unknown token's block unchanged;
    - audit entries.
  - **Images:**
    - upload: the type by its bytes, 10 MB, a WebP and an animated GIF that keep their frames;
    - the use recorded at once, matched on save, copied on duplicate, dropped on delete, then the cleanup;
    - the board's and the admin's image endpoints only for a file the board uses;
    - the CSP header.
  - **Elements:**
    - `ChainOn` (responsible, fallback, global person, group, nobody working, a closed day, nobody twice);
    - *On duty*'s data with deputies, where, comment and tomorrow;
    - *Sensor message* for a sensor with a message, with none, with no data, deleted;
    - a `NamesPeople` sensor's message, without its values;
    - layout defaults for a 1.23 layout.
- **Cookie (with the fake model):**
  - off; no board; the board not in the list; no right;
  - mute and suppress; both limits; a duplicate;
  - a *Success* replacing its case's message;
  - Seraph's reject, revise and approve;
  - *Propose* expiring after 30 min, and an approval in time;
  - the lounge tool offered and refused by the asker's right.
- **Vitest:**
  - **Messages:** the order (kind, then newest), the rotation, and a new message shown first; the countdown from `remainingSeconds`; closing on a screen; the panel's form rules.
  - **Times and dates:** 24-hour, 12-hour and the browser's; the eight date formats in four languages; the ISO week (years with 53 weeks, 1 January in week 52 or 53).
  - **`fitText`:** a short value grows to fill; a long text shrinks to its minimum, then is cut.
  - **Images:** the first frame with *Motion* off.
  - **The layout:** the defaults.
- **Playwright:**
  - **Messages:**
    - from the Debug page to two boards: the box on both within 6 s; withdrawn, gone within 6 s;
    - a person with *Boards: View* only sees the list without *Withdraw*.
  - **The elements:**
    - a board with a *Sensor message* (*Fit*), an uploaded animated GIF, a 12-hour clock with a German date, and *On duty* with two deputies, built in the editor and shown at its URL.
  - **Screenshots:**
    - the five kinds in *Default*, *Dark*, *Windows 95*, *Terminal* and *Amiga* at 1920 × 1080;
    - the longest text at 1280 × 720 and 3840 × 2160;
    - the new elements in *Default* and *Dark*.

---

## 16. Risks

| Risk | Mitigation |
|---|---|
| **A 60-minute Alert left by mistake covers a wall's board** | *Withdraw* on the Debug page; 60 min at most by default; the bar stays visible; Esc on a screen with a keyboard |
| **Cookie sends too many** | Two gates, a level, Seraph's review, 10 a day and 2 a board an hour, one per incident and board |
| **A message on a public board says too much** | The reach in the panel; Cookie's instructions and the review; the *People* setting for who sent it |
| **Unreadable from across the room** | The sizes from the box's height; screenshots at three resolutions; at most 1000 characters |
| **Polling load** | *304* without a query; 12 tiny requests a minute per screen |
| **The rate limit cuts off screens behind one address** | Per address and board, 600 a minute (§6.4) |
| **A large animated GIF stutters on a TV's weak browser** | 10 MB at most; the editor shows the size; *Motion* off shows the first frame |
| **A sensor's message shows something it shouldn't** | An element of its own, added on purpose, with the warning in the editor; a sensor whose data names people sends its messages without the values |
| **Boards from 1.23 switch from 12- to 24-hour** | Only where an English browser chose 12-hour; one setting puts it back (decision 6) |
| **Images fill the files folder** | Each content once; unused images go after 24 h + 7 days; the uploads-per-hour limit |

---

## 17. Later (not in this plan)

- **A sound** per kind, for kiosk browsers started with autoplay allowed.
- **From outside PNMT:** an MCP tool, a 3rd party connector's caller, or a token of its own for a webhook (Zabbix actions, CI).
  - *The MCP tool is built (docs/mcp-extensions-plan.md §9.2, 2026-11-24): `board_message_send` and `board_message_withdraw`, sent as the token's person with the source `Mcp`, with this plan's limits.*
- **Scheduled and recurring messages; "until withdrawn".**
- **An element *Recent messages*** listing a board's last messages after they close.
- **A long poll,** if 5 s turns out too slow.
- **A *Send a message* button on *Boards*** and on a board's page, with the same panel in a dialog.
- **An image in a message box;** a slideshow of several images in one element; video; an image from a URL.
- **The board's own words in German, French and Italian,** beyond day and month names.

---

## 18. Phases

| Phase | What | Size |
|---|---|---|
| **E1** | **Times and dates** (§9.1, §9.2): the look's and each clock's settings, `boardTime` with the hour cycle, the formats, the week and the language; the editor. Vitest | small |
| **E2** | ***On duty today*** (§9.5): `ChainOn`, the options, the data, the element and its settings. .NET tests, Vitest | small |
| **E3** | ***Sensor message*** (§9.3) with `fitText.ts`: the element, its settings and rights. .NET tests, Vitest | small |
| **E4** | ***Image*** (§9.4):<ul><li>`file_uses.board_id` and its migration;</li><li>the upload, the uses, the two image endpoints, the CSP;</li><li>the element with its first-frame stop; the editor's upload and picker.</li></ul>.NET tests, Vitest | medium |
| **M1** | **Messages, the server:**<ul><li>`board_messages` and its migration; `Level.Message`;</li><li>the service, the admin API and the board's poll with the stamp;</li><li>the audit events; the rate limit per address and board;</li><li>`Boards__MessageMaxMinutes` and `Boards__MessageHistoryDays`.</li></ul>.NET tests | medium |
| **M2** | **Messages, the board:**<ul><li>`MessageBox.vue` and the icons; the poll; the order and rotation; the countdown;</li><li>the frames for *Windows 95*, *Windows XP*, *Terminal* and *Amiga*;</li><li>*Look → Messages* and *Preview a message* in the editor.</li></ul>Vitest, screenshots | medium |
| **M3** | **The Debug page:** the panel with the preview; the open and the recent messages; *Withdraw*; the route for either right. Playwright | small |
| **M4** | **Cookie:**<ul><li>the action, its settings and limits;</li><li>the brief and the instructions;</li><li>the executor's checks; Seraph's evidence; the inbox's 30 minutes;</li><li>the suppressible channel; the Actions tab.</li></ul>Tests with the fake model | medium |
| **M5** | **The lounge tool** `send_board_message` (decision 4) | small |
| **R** | **Release:**<ul><li>the documentation page (*Boards → Messages* and the new elements);</li><li>deploy/README (the new settings, the rate limit, the files folder for board images);</li><li>the release notes for 1.24; this plan's *As built*; the Playwright run and its screenshots.</li></ul> | small |

The elements come first: they are small, independent of each other and of the messages, and each is a commit that runs on its own.

---

## 19. Decisions (2026-11-24)

1. **The Debug page opens for *System: Manage* or *Boards: Message*,** each panel by its own right (§7.2). The *Send a message* button on *Boards* wasn't part of the recommendation and stays in §17.
2. **The five kinds:** *Alert*, *Warning*, *Maintenance*, *Information*, *Success* (§4.1).
3. **10 s to 60 min, 60 s by default** (`Boards__MessageMaxMinutes`), **and Cookie's up to 10 min** (§4.2, §8.1).
4. **Cookie in the lounge:** yes, for a person with *Boards: Message*, as M5 (§8.6).
5. **The deputies of *On duty today* are the availability plan's fallbacks** (the chain: the day's fallback, the global fallback person, the global fallback group), not the deputies of *Responsibilities* (§9.5).
6. **24-hour by default for every board, those from 1.23 included** (§9.1).

---

---

## 20. As built (2026-11-24)

All phases E1 to E4, M1 to M5 and R are built. Where the build differs from the plan above:

**Times and dates (§9.1, §9.2)**
- **The default date format leaves the weekday out in the header line** ("24 November"), as 1.23 did; every other format shows there as chosen.
- **A layout without a language reads as *As the screen's browser*,** so 1.23's boards keep their names; a new board gets *English*.
- **Lists:** today the time, within six days the weekday and time, older a short date in the format's style ("24 Nov", "24.11.",
  "2026-11-24", "11/24").
- **The header's clock** offers *As the board*, *24-hour* and *12-hour*; the browser's hours only for the whole board.
- **The plan's examples say "Monday, 24 November"**: 24 November 2026 is a Tuesday. The tests use the real day.

**On duty today (§9.5)**
- **"Tomorrow" is the next open day within a week:** "Tomorrow: Ben Keller" or "Monday: Ben Keller". With names hidden, it isn't shown.
- **Where each works** comes from the day's cell, else the plan's default type.
- **When the global fallback group is the first step,** all its working members are on duty, as in 1.23; later in the chain each member is
  one deputy.
- **The settings are named** `deputies`, `dutyLabel`, `deputyLabel`, `nobodyText`, `closedText`, `role`, `where`, `dayComment` (the lounge
  has `comments` already), `tomorrow`, `textSize` (shared with *Sensor message*, not `nameSize`) and `oneLine`.
- **Labels without names** show when the board hides people, whatever *Show the role* says.

**Sensor message (§9.3)**
- **The default size is *Medium*.** No sensor chosen: *not configured*. A deleted one has no name in the data.
- ***Fit* grows a message only:** "No message", "No data yet" and "This sensor no longer exists" stay at the medium size.
- **The text is cut with a fade** where *Fit* can't fit it at its smallest, and by `line-clamp` (with "…") otherwise.
- **No separate test of a `NamesPeople` sensor on a board:** the element shows the message as stored, which the advanced sensor's tests
  check is without values.

**Image (§9.4)**
- **The CSP's `img-src blob:` is the UI's, for the admin UI too:** the editor's preview shows images from memory as the board does.
- **A save refuses an image that isn't the board's** (uploaded for it, or a use it has): "Not one of this board's images: upload it for this
  board." An id from a page's image would otherwise show a file the editor may not see.
- **Both apps fetch images through a loader the board's host provides** (`src/boards/inject.ts`): the board app with its token, the editor's
  preview with the session. The editor's thumbnails use `GET /api/v1/boards/{id}/images/{fileId}` directly in an `<img>`.
- **The first frame** is drawn once to a canvas and kept as a PNG `blob:` URL; each file is fetched once per screen.
- **The catalog gains** `maxImageBytes` and `messageMaxSeconds`, for the editor's and the Debug page's texts.

**Messages (§4 to §7)**
- **The kinds' red, yellow and green are the theme's status colours,** as the bar's are: Windows 95's red is darker. What they mean doesn't
  change.
- **Text sizes:** 1 % of the box's height is the unit; the title is 7 units, the text fitted between 2 and 4.5 (`fitText.ts`). Cut with a
  fade.
- **The ✕ in Windows 95's and XP's title bar closes the box on that screen,** as Esc does (§5.6). The Debug page's preview never closes.
- **The tab's title** is "⚠ <title> · <board>" while an alert or a warning is open; its icon keeps the bar's colour.
- **The stamp moves on every save of a board**, not only when its *Messages* setting changes: a save drops the board's cached rows anyway.
- **No GIN index on `board_ids`:** open messages are few, and the index on `ends_at` serves the poll. `cookie_decision_id` is a `bigint`
  (the decision log's ids are numbers). A second index on `sent_at`, for the lists.
- **The audit's target type is *BoardMessage*.**
- **The rate limit counts after the token is known:** an unknown token doesn't use a budget, and its block after ten stays.
- **The board list carries** `showsMessages` and `messageSize`, for the picker and the preview.

**Cookie (§8)**
- **Duplicates:** the target is `board:<id>` (`board:<id>:success` for an all-clear), and the executor's rule is the usual one: the same case
  and target done within a day, or waiting. Stricter than "still open".
- **An admin's empowered order never sends a board message:** the chat's powers list doesn't offer it.
- **The decisions inbox:** `DecisionDraft` gains `ExpiresIn`; a board message's expires after 30 minutes, and an approval later than that
  ends as *expired* (the maintenance task marks the decision within the hour).
- **The Actions tab** names why the action is unavailable: no board chosen, or Cookie's group without *Boards: Message* (`acting.boardsRight`
  in Cookie's overview).
- **Who sent it on the board:** Cookie's name, with "(Cookie)" when renamed; the approver in the admin's list and the audit.

**The lounge (§8.6)**
- **Offered to the voice that answers a person's message,** when that person is active and holds *Boards: Message*, the level isn't *Off*,
  and both of Cookie's gates are open. The checks are Cookie's group's rights, as for a case's.
- **One waiting per board** is its duplicate rule; Seraph's paragraph for it says it is a board message, not a light e-mail.

**Tests**
- **Playwright:** the elements' board is set up through the API, its image uploaded in the editor; the five kinds in *Default*, the alert in
  *Dark*, *Windows 95*, *Windows XP*, *Terminal* and *Amiga*, and the longest text at 1280 × 720 and 3840 × 2160
  (`e2e/board-messages.spec.ts`).

