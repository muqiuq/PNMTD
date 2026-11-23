# PNMT 1.20: a knowledge base that is linked, liked and mapped; the Redmine tab with its own right, Redmine users linked by id, and the away watch

## What's new

### The knowledge base

- **The sensitive mark, where pages are written** (docs/knowledge-base-extensions-plan.md §4). It was in the page's ⋯ menu and the AI
  dialog only, which never opens while AI is off.
  - **A switch in the editing page's details**, seen before a new page's first save. Unmarking takes the two statements, recorded for the
    text being saved.
  - **A *Sensitive* tag** in the page's header, a row in *About this page*, and a filter in the list.
- ***Written mostly with AI*** (§5): a checkbox in the details; a tag on the page, an icon in lists, a filter. Pages created over MCP start
  checked. It is part of every revision.
- **Recent changes on top of the knowledge base** (§6): the pages changed in the last 14 days, each once with its newest change (who,
  what, the lines and the note), at most 10. Hidden while searching or filtering, and it folds.
- **Pages linked to each other, mutually** (§7). Link a page in its details or under the new *Related pages* panel: both pages show the
  link, either removes it, and both histories say who linked them. Links in the text are listed there too, with their direction.
- **Likes and *Used to solve a problem*** (§9):
  - a like is once per person, undone by clicking again;
  - a use is recorded with a click, again after 12 hours, with an optional note (*printer on the 2nd floor*), and *Undo* for 10 minutes;
  - both counted on the page and in lists, with *Most liked* and *Most used* as orders; *About this page* shows who and the last uses;
  - Viewers take part.
- **E-mails to a page's authors** about likes and uses (§10): batched (one e-mail per page for a quarter of an hour), at most 20 a day per
  person, and each person decides under the new ***Account → Notifications***. The mechanism is general: later features add their own
  kinds there.
- **The knowledge map** (§8): pages, locations, hosts and sensors, and how they are connected (page links, links in the text, links to
  objects, and the structure from sensor to site). Around a page or object, 1 to 4 steps, or the whole knowledge base with filters. A
  click opens a side panel. Reached from *Map* on the knowledge base, *Show on the knowledge map* on a page, and the knowledge base
  panels of locations, hosts and sensors. It shares the network map's layout and pan/zoom.
- **MCP:** `kb_page` returns its related pages (never a sensitive one), *Written with AI*, likes and uses; `kb_search` the counts;
  `kb_page_create` and `kb_page_update` take `writtenWithAi`.

### Redmine and Cookie

- **A right for Admin → Cookie → Redmine: *Redmine: Manage*** (docs/cookie-redmine-tab-plan.md §3). It is a new level after *Link*.
  - **The tab needs it;** *Cookie: Manage* alone no longer shows the tab.
  - **Someone with only *Redmine: Manage*** opens *Admin → Cookie* and sees the Redmine tab alone.
  - **The duties' switches stay on *Actions*,** under *Cookie: Manage*. The Redmine tab shows whether each is on.
- **People and their Redmine users,** a new panel on the Redmine tab (§4).
  - **Each active user's Redmine user and how it stands:** *linked*, *matches by name*, *several match*, *no match*, *not set*, or
    *linked, unknown to Redmine*.
  - **Two people with one Redmine user** are marked on both rows.
  - **A link is picked, never typed,** from Redmine's users: the members of every project PNMT shows, and the people on watched tickets.
  - ***Link all exact matches*** links every user whose name is exactly one Redmine user's.
  - **A second list shows the Redmine users with work in PNMT but no PNMT user:** open watched tickets, or a staff role. Their reminders
    go to the escalation group.
  - **Who uses the link:** reminders, escalations, *Needs you* and Cookie find a ticket's assignee by the linked id first. A rename in
    Redmine keeps the link. *Account* and the user's profile show whether someone is linked.
- **The away watch** (§6): someone comments on a ticket whose assignee is away in the availability plan. Away means a day of vacation,
  holiday or compensation.
  - **Cookie judges from the comment** whether someone should act before the assignee is back.
  - **If so, code tells the responsible of the day or a chosen person,** as set on the Redmine tab. Each stands in for the other when it is
    nobody. It is never the away assignee, nor whoever wrote the comment.
  - **When it goes out:** in working hours only, at most once per ticket in 4 hours, and never once the assignee is back.
  - **Without Cookie's verdict,** the comment is told marked *not judged*, or only recorded, as set. That happens when Cookie is stopped or
    paused, doesn't judge within 30 minutes, or the comment looks like it tries to instruct an AI.
  - **Switched on under *Actions → PNMT's duties*,** like the other ticket duties. It is off until an admin switches it on.
  - **A new action, *Away notice*** (*Auto*, 0.7), with Seraph's review; a suppress of the new channel `away` stops it.
  - **New limits:** away notices a day (20), and per person a day (10).
  - **The tab lists the comments,** with the verdicts and whom they told.
- **The output limits' maximums are set in `.env`** (§13): `Cookie__Ceilings__*`, one per limit. Without a line, the maximum is the one
  that was in code. A limit saved above its maximum counts as the maximum, and the Actions tab says so.

## Fixed

- **A link added by a save was counted twice** in its revision's history (*links +2 −0*) and stored twice in that revision. Old revisions
  are read with each link once.
- **Cookie's ticket prompts named the first watched project's deadlines for every ticket.** Each ticket now gets its own project's
  deadlines and reminders. With projects that differ, the goals say *"within the deadlines of their project"*.
- **Every ticket "had no update for 7 days"** in Cookie's prompt. It now gets the real number of days since staff last moved it.
- **Two users with the same Redmine name:** either one got the ticket's reminders, by chance. Now neither does until one is linked; the
  reminders go to the escalation group meanwhile.

## Updating from 1.19

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.20.0`. It takes a backup first.

- **Six migrations run at the start:** `KbSensitiveAndAi`, `KbPageLinks`, `UserNotifications` and `KbReactions` add columns with defaults
  (every existing page: not written with AI, no likes or uses) and new tables; `kb_links` takes a fourth kind of target. `RedmineLinks` adds
  the Redmine user's id to users, and `AwayWatch` the newest journal seen per ticket case and the away watch's table. Nothing existing
  changes.
- **E-mails about likes and uses are on by default** for everyone with an address on their account, if `Smtp__*` is set. Each person can
  turn them off; `UserNotifications__Enabled=false` turns them off for everyone.
- **Optional settings:** `UserNotifications__*`, `KnowledgeBase__RecentChangesDays`, `__RecentChangesMax`, `__UseRepeatHours` and
  `__MapMaxNodes` (see `.env.example` and deploy/README.md, *Notifications to people*).
- **Only admins see the Redmine tab** until an admin gives *Redmine: Manage* to a group (*Admin → Groups*). No group gets it by itself.
- **Nobody is linked yet.** Until someone links a person, their tickets are found by the name, as before. *Link all exact matches* links
  everyone whose name is exactly one Redmine user's.
- **The away watch is off.** Set who is told on the Redmine tab, then switch it on under *Actions → PNMT's duties*.
- **New optional settings for Cookie:** `Cookie__Ceilings__*` and `Cookie__Tokens__AwayComment` (see `.env.example` and deploy/README.md).

## Good to know

### The knowledge base

- **The notification task has a second timer** for these e-mails, with a guard of its own, so a slow mail server never delays an alert.
  *System → Task health* shows it as *User notifications*.
- **A sensitive page is named by its number only** in these e-mails, without its title or the notes.
- **Linking a page changes the other page too:** it gets a *links-only* revision. Someone editing that page meanwhile gets no conflict and
  keeps the link.
- **Redacting a text** reaches the notes of a page's uses too.
- **`pnmtd dev seed`** adds page links, likes and uses with notes.
- **The knowledge base's plan:** docs/knowledge-base-extensions-plan.md, with its decisions and, in *As built*, where the build differs from it.

### Redmine and Cookie

- **The away watch writes nothing to the ticket,** and nothing reaches the requester.
- **The notice quotes the comment** only to a recipient with *Redmine: View*. Anyone else gets the links.
- **The model never names whom to tell:** the Redmine tab's settings do.
- **The comment's text is removed after 30 days;** the verdict stays.
- **The away watch only looks at comments newer than the newest journal the case book saw.** So switching it on, or watching a new
  project, judges nothing old.
- **Changing a user's Redmine name on their profile removes the link.**
- **No MCP token gets *Redmine: Manage*.** MCP's ceiling for Redmine stays *Link*.
- **The plans:** docs/cookie-redmine-tab-plan.md for the Redmine tab, the links and the away watch, with its decisions and, in *As built*,
  where the build differs from it; docs/cookie-ticket-watch.md summarises what Cookie and code do with tickets.
