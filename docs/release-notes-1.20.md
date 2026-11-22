# PNMT 1.20: a knowledge base that is linked, liked and mapped

## What's new

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

## Fixed

- **A link added by a save was counted twice** in its revision's history (*links +2 −0*) and stored twice in that revision. Old revisions
  are read with each link once.

## Updating from 1.19

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.20.0`. It takes a backup first.

- **Four migrations run at the start:** `KbSensitiveAndAi`, `KbPageLinks`, `UserNotifications` and `KbReactions`. They add columns with
  defaults (every existing page: not written with AI, no likes or uses) and new tables; nothing existing changes. `kb_links` takes a fourth
  kind of target.
- **E-mails about likes and uses are on by default** for everyone with an address on their account, if `Smtp__*` is set. Each person can
  turn them off; `UserNotifications__Enabled=false` turns them off for everyone.
- **Optional settings:** `UserNotifications__*`, `KnowledgeBase__RecentChangesDays`, `__RecentChangesMax`, `__UseRepeatHours` and
  `__MapMaxNodes` (see `.env.example` and deploy/README.md, *Notifications to people*).

## Good to know

- **The notification task has a second timer** for these e-mails, with a guard of its own, so a slow mail server never delays an alert.
  *System → Task health* shows it as *User notifications*.
- **A sensitive page is named by its number only** in these e-mails, without its title or the notes.
- **Linking a page changes the other page too:** it gets a *links-only* revision. Someone editing that page meanwhile gets no conflict and
  keeps the link.
- **Redacting a text** reaches the notes of a page's uses too.
- **`pnmtd dev seed`** adds page links, likes and uses with notes.
- **The plan:** docs/knowledge-base-extensions-plan.md, with its decisions and, in *As built*, where the build differs from it.
