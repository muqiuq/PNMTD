# PNMT 1.24: board messages, and more for the boards' elements

## What's new

### Board messages

- **A large message box over a board** (docs/board-messages-plan.md): an ***Alert***, ***Warning***, ***Maintenance***,
  ***Information*** or ***All clear***, with a title and a text in Markdown, shown for 10 seconds up to an hour (60 seconds by
  default), on one or more boards at once, or on all boards that show messages.
- **Sent from *Admin → Debug*** by anyone with the new level ***Boards: Message***, with a live preview in the first board's theme. Below
  it, the open messages with ***Withdraw***, and the last 20 sent. The Debug page opens for *System: Manage* or *Boards: Message*, each
  panel by its own right.
- **On the board:** 80 % × 70 % of the board, or all of it (*Look → Messages*); the status bar stays visible, and the board is dimmed
  behind it. The kind's band, its icon and the title in large type, the text as large as fits, who sent it and when, *closes in 0:42*,
  and a bar that drains. **Several open:** the most important first, a new one at once, then turn by turn every 15 seconds. Windows 95,
  Windows XP, Terminal and Amiga frame it as their era's dialog. `Esc` hides it on a screen with a keyboard. An alert or a warning puts
  "⚠" and its title before the tab's name.
- **Within about 5 seconds:** a board fetches its messages every 5 seconds, and while nothing changed the answer is a tiny *304* without
  a database read. Each counts down from the seconds the server says are left, so a wall whose clock is wrong still closes it on time.
- **Cookie sends them too,** as a new action *Board message* (*Propose* by default): only to the boards chosen for it under *Admin →
  Cookie → Actions*, and only while its group holds *Boards: Message*. For what a room should know now: an outage, maintenance as it
  starts, the all-clear afterwards, which ends Cookie's message of the same case. Seraph reviews each one, also outside working hours; a
  proposal waits at most 30 minutes; 10 a day and 2 an hour on one board, with ceilings in `.env`. The channel *board-message* can be
  suppressed. **In the lounge,** a voice puts one up when the person it answers holds *Boards: Message* and asks for it, through the
  same checks, reviewed by the other voice's model.

### More for the elements

- ***Sensor message*:** one sensor's last message, in four sizes or ***Fit***, as large as the element allows (a value such as
  "41.5 °C" across a whole tile). Monospace, at most so many lines, centred, the state's colour as a band or the background, and how old
  it is.
- ***Image*:** an uploaded PNG, JPEG, WebP or GIF, animations included, up to 10 MB, checked by its bytes and stripped of metadata as a
  page's images are. All of it, cropped to fill, or its own size; a background, a caption and an alternative text. With *Motion* off, an
  animation shows its first frame. The board fetches it with its token and shows it from memory: no URL carries a secret.
- **24- or 12-hour time** under *Look → Time*, for every time on the board, and for the header's clock and each *Clock* element on their
  own.
- **Date formats** under *Look → Date*: eight of them, from "Tuesday, 24 November" to "24.11.2026", "2026-11-24" and US style; the ISO
  week number; and the language of day and month names (English, Deutsch, Français, Italiano, or the screen's browser). A *Clock* element
  can have its own format and week number.
- ***On duty today*:** the responsible and up to five deputies from the availability plan's fallbacks (the day's fallback, the global
  fallback person, the global fallback group), with labels and texts of your own ("Pikett", "Stellvertretung", the text for nobody and
  for a closed day), where each works today, the day's comment, who is on duty on the next open day, three name sizes, stacked or in one
  line.

### Changed

- **A board's rate limit counts per address and board,** with a default of 600 a minute (120 per address before). Screens behind one NAT
  or reverse proxy address shared one budget across all boards, and the messages' poll would have made it tighter.
- **Board clocks are 24-hour by default,** also on boards made with 1.23: an English browser showed "10:35 PM" before. *Look → Time*
  puts 12-hour back.

## 1.24.1

- ***Fill again* keeps the days typed.** The dialog opens with the cursor in *From*, and a calendar opens from its button (or ↓),
  no longer when its field gets the focus. An open calendar, or the dialog when it finished opening, could take the focus away while
  a day was typed: the text was lost, and the field kept its old day.
- **The build's check of the npm registry's signatures** tries again with an empty npm cache when the registry fails to answer. The
  registry had answered one package's attestations with "OK" instead of JSON, and npm had kept that answer for its retries.
- **A failed browser test's error** is in the build's annotation on GitHub, not only its name.
- **Updating from 1.24.0:** `./update.sh --tag v1.24.1`. No migration, nothing to configure.

## Updating from 1.23

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.24.1`. It takes a backup first.

- **Two migrations run at the start:** `BoardImages` lets a board use files as pages do (a column on `file_uses`), and `BoardMessages`
  adds a table. Nothing existing changes.
- **Nobody gets *Boards: Message* by itself,** apart from those with *Boards: Manage*, which includes it.
- **The screens reload themselves** within a minute after the update, as after every update, and then show messages.
- **A `Boards__RequestsPerMinute` set in `.env`** stays as it is, and now counts per address and board.
- **Board images lie in the files folder** with the knowledge base's; a proxy's body limit of 32 MB (the example's) covers them.

## Good to know

- **A message on a board open from anywhere** is read by anyone with its URL: the Debug page's panel names each board's reach, and
  Cookie's instructions and Seraph keep names, customers and addresses out.
- **Boards from 1.23 look as before** but for the hours: their dates keep the browser's language and format, and *On duty today* shows no
  deputies until someone sets some. New elements show one deputy.
- **The audit log** has *Board message sent* and *Board message withdrawn* in the category *Boards*, by a person or by Cookie (with the
  person who approved a proposal).
- **The plan:** docs/board-messages-plan.md, with its decisions and, in *As built*, where the build differs from it.
