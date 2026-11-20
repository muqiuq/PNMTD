# PNMT 1.16: Redmine pages at once, and a log of what PNMT sends

## What's new

- **Redmine's pages open at once** (docs/redmine-queue-outbound-plan.md). The overview, the search and a ticket's page come from what PNMT read last, instead of waiting for a dozen calls to Redmine one after the other.
  - **The header says how old it is:** *Refreshed 2 min ago*, with the exact time in a tooltip. While a page is open, PNMT reads it again in the background, so it is at most a minute old (`Redmine__RefreshSeconds`).
  - **⟳ reads it now.** It spins until the new data is in, and its request goes before everything else PNMT asks of Redmine.
  - **When Redmine doesn't answer,** the page keeps what it had and says *refresh failed*, with Redmine's message in a tooltip.
  - **What PNMT knows itself is always current:** a ticket's links, and the tickets linked to a problem, show at once.
  - A page nobody opened for a while is read again when someone does. The overview of all projects is read every `Redmine__SyncMinutes` anyway, so it is never old by much.
- **One queue for Redmine.** Every read and every write goes through one background task.
  - **The order:**
    1. what a person started with a button (⟳, *Test connection*, *Link*, a note or ticket a person ordered or approved);
    2. Cookie's own writes;
    3. pages someone waits for;
    4. the rest (the scheduled refreshes, Cookie's case book, the copy of linked tickets).
  - **The same read asked twice** is made once.
  - **At most `Redmine__Parallelism` (4) calls run at once,** one of them always free for people. The overview's calls now run side by side, so a cold overview takes about a third of the time it did.
  - **A Redmine that answers 429 or 503** pauses the background reads, not the buttons.
- **A new tab, *System health → Outbound*,** shows everything PNMT sends, live:
  - ***Running now*:** what is going out this second, and the Redmine queue, lane by lane;
  - ***Notifications*:** every e-mail, Pushover message and webhook, simulated ones included, with what it was for (*rule Storage → Alice*, *Cookie: note on #603*, *Contact test*) and who asked. The newest 10 000 are kept (`Outbound__KeepNotifications`);
  - ***Redmine*:** every call to Redmine, with its job, lane, how long it waited in the queue and how long it took. The newest 1 000 are kept (`Outbound__KeepRedmine`).

  A record's details open in a window you can link to. They show:
  - the request and the answer, with their headers and bodies (JSON formatted, with *Copy* and *Download*);
  - an e-mail's whole message and its SMTP conversation;
  - for a Redmine call, *Show the job's calls*.

  PNMT's own secrets are masked: the Redmine keys (but for their last 4 characters, so the reading key and Cookie's write key can be told apart), Pushover's app token, a Pushover user key (but for its last 4), and the SMTP password. Webhook URLs are kept as they are. Redmine's private notes are kept only when PNMT shows them (`Redmine__ShowPrivateNotes`). Only admins see the tab (*System: Manage*).

## Updating from 1.15

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.16.0`. It takes a backup first.

- **One migration, `OutboundLogs`,** runs at the start: two new tables, `outbound_notifications` and `redmine_calls`. Nothing existing changes.
- **Nothing to configure.** The new `Redmine__*` settings of the queue and the `Outbound__*` settings of the logs are optional; see deploy/README.md, *Redmine* and *Everyday operation*.
- **After the update, the first Redmine page may still take a moment:** PNMT reads the lookups and the overview first after every start, and a ticket or search the first time someone opens it.

## Good to know

- **Old data is labelled, never hidden.** If a page says *Refreshed 20 min ago*, nobody had it open; it is being read again as you look at it.
- **The logs are in the backups,** with message bodies, addresses, webhook URLs and ticket contents. Each body is cut at `Outbound__MaxBodyKiB` (256) KiB and stored compressed: the notification log is tens of MB, the Redmine log a few.
- **The Redmine log reaches back hours, not days:** about 2 hours with a Redmine page open all day, about half a day otherwise. It is meant for the recent picture: why a page was slow, what the queue did. `Outbound__KeepRedmine` keeps more.
- **The plan:** docs/redmine-queue-outbound-plan.md, with the decisions (§10) and what was built (§15).
