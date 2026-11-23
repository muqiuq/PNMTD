# Cookie and Redmine tickets: what code does, what the AI does

| | |
|---|---|
| Status | **As built, 1.20 (2026-11-23).** A summary of the code, not a plan. |
| Sources | `PNMTD/Cookie/CaseBook.cs`, `CaseRules.cs`, `Duties.cs`, `DutyWriter.cs`, `CookieRound.cs`, `CookieBrain.cs`, `CookiePrompts.cs`, `CookieExecutor.cs`, `Outbox.cs`, `BigBrother.cs`, `RedmineLinks.cs`, `Away/*.cs`, `PNMTD/Tools/Read/IntegrationReadTools.cs` |
| Background | docs/cookie-plan.md §7.2, §19.1, §21.1; docs/cookie-fallback-plan.md; docs/cookie-redmine-tab-plan.md (1.20: the right, the links, the away watch); docs/cookie-tickets-plan.md (proposed, nothing built) |

**In short.** Code does the watching: it reads the tickets, keeps each ticket's clock, computes the deadlines, decides when a reminder or an escalation goes out and to whom, and sends it from a template. The AI never decides *whether* a deadline is met. It writes texts (when allowed), judges ticket cases in its rounds, and *proposes* notes and answers. Code checks every proposal, Seraph reviews it, and at the default level a person approves it. Cookie only ever adds to Redmine. It never edits, reassigns, changes a status or closes.

---

## 1. Code: the watching

### 1.1 Reading the tickets (`CaseBook.TicketsAsync`)

- **Which tickets:** the open tickets of the **watched projects** (*Admin → Cookie → Redmine*, which needs *Redmine: Manage*), within the projects PNMT may show. At most 10 pages of 100, newest change first.
- **How often:** every `Cookie__TicketsMinutes` (5). It runs as a background job in the Redmine worker's queue, behind people's requests and Cookie's writes. A ticket's journals are fetched only when Redmine's `updated_on` moved.
- **When:** whenever `Cookie__Enabled=true`, **whether the AI is started or not.**
- **One case per ticket** (`ticket:{n}`). It stores the title, project, author and whether the author is staff, the assignee, the status, created and updated, the first answer, the last staff update, and an importance taken from the priority: above Redmine's default priority is *High*, below it *Low*.
- **Timeline entries:** *opened*; *progress* (first answer by staff, updated by staff, assigned to …, status …); *updated*; and *closed* (closed in Redmine, moved to a project that isn't watched, gone, no longer watched). **An unreachable Redmine closes no case.**
- **Not stored:** the tracker, category, description and custom fields.

### 1.2 The clock (`CaseRules.Clock`)

- **An answer is a public note or a status change by staff.** Private notes don't count.
- **Staff** are the project's members with one of its *staff roles*, read from the memberships and cached for an hour. **With no roles set, everyone but the author counts as staff.**
- A ticket written by staff has no first-answer deadline.

### 1.3 The deadlines (`CaseRules.TicketDuties`)

Per watched project; the defaults are those of `WatchedProject.Default`.

| Duty | Due | Default |
|---|---|---|
| `first-answer:reminder` | working hours after the ticket came in | 3 h |
| `first-answer:missed` | working hours after the ticket came in | 4 h |
| `update:reminder@…` | days after the last staff update, moved to the next working start | 12 d |
| `update:missed@…` | the same | 14 d |

- **The statuses that stop the clock** (for example *Feedback*) have no update duties.
- The update duties' keys carry the time of the last update, so **each fires once per update.**

### 1.4 What a due duty does (`DutyRunner.TicketDutyAsync`)

- **Every duty is off until an admin turns it on** (*Admin → Cookie → Actions*). A duty that falls due while it is off closes as *off*, so turning it on later sends nothing old.
- **Only in working hours.** A reminder whose deadline is due as well gives way to the deadline.
- **A reminder:** a **private note on the ticket** (only while Redmine is writable) and an e-mail to the assignee. PNMT finds the assignee **by the Redmine user linked to a PNMT user** (*Admin → Cookie → Redmine → People and their Redmine users*), else by the name, when exactly one active user has it. When nobody is assigned, or the assignee doesn't work today, the e-mail goes to the project's **escalation group** instead.
- **A missed deadline:** **no note**, only messages. They go to the assignee, the escalation group and whoever the availability plan makes responsible: an e-mail, and Pushover too because it is urgent.
- Without an escalation group, or when none of its members works, whoever is responsible stands in.

### 1.5 Limits on what goes out (`Outbox.LimitAsync`)

- **Per day, by default:** 2 notes on one ticket, 30 notes on Redmine, 10 public answers, and 5 new tickets, plus the e-mail and Pushover limits. Each goes at most to its ceiling in `.env` (`Cookie__Ceilings__*`).
- **What a limit holds back stays held back.** An admin can send it from the Overview.
- **The writes:**
  - They use `Redmine__WriteApiKey` only, never the reading key.
  - The ticket's project is checked again just before writing.
  - A note is a `PUT` with `notes` and `private_notes`, nothing else; a ticket is a `POST`.

### 1.6 Where else code uses the ticket cases

- **The code's status:** a missed ticket deadline is *Warning*; a reminder due is *Info*.
- **The summaries' *Needs you*:** the tickets assigned to the reader, matched by their contact's Redmine name.
- **Problem cases:**
  - A ticket linked to the host or one of its sensors counts as the problem's progress when it moves.
  - An open linked ticket keeps the problem's own *ticket after 2 days* away.
- **The Redmine page's *no update* filter** (`Redmine__StaleDays`) leaves out Cookie's own notes, so a nudge from Cookie doesn't make a ticket look updated.

### 1.7 The away watch (1.20)

- **A duty like the others,** switched on under *Actions → PNMT's duties*; its settings are on the Redmine tab.
- **What it finds:** the case book keeps each ticket's newest journal. A new note (private ones too, by default) by someone other than the assignee or Cookie, while the assignee is on vacation, holiday or compensation in the availability plan, becomes a row in `cookie_away_comments`.
- **Who is told:** the responsible of the day or a chosen person, each standing in for the other; never the assignee or the comment's author. In working hours, once per ticket in 4 hours, and not once the assignee is back.
- **Without Cookie's verdict** (stopped, 30 minutes, withheld by the screen): told marked *not judged*, or only recorded. Limits: 20 a day, 10 per person.

## 2. The AI: judging and writing

### 2.1 The duty texts (`DutyWriter`), optional

- **When it writes them:** *Actions → Cookie writes the duties' texts* is on, and Cookie is running and not paused.
- **What it gets:** the duty, the ticket's masked facts and the template.
- **What it writes:** the subject, the Pushover line, the e-mail and the private note. Code has already decided when and to whom.
- **Seraph reviews the text.** A rejection, an error, or more than 90 seconds, and **the template goes out instead.**
- The answer's length is capped by `Cookie__Tokens__Duty`.

### 2.2 Ticket cases in a round (`CookieRound`, `CookieBrain`)

- **The brief:**
  - The ticket cases that changed or are due come in full; the others come in one line each.
  - A short **triage** call picks the cases to work on, up to *max cases per round*.
- **The instruction for a worked case** (`CookieBrain.KindOf`):
  - **`firstAnswer`** while staff haven't answered;
  - **`noUpdate`** after that;
  - plus the scoped instructions. For a ticket, only the *project* scope applies.
  - **The deadlines are the ticket's own project's,** and `noUpdate` gets the real days since staff last moved it (1.20; before, the first project's and always 7).
- **What the prompts ask** (`CookiePrompts`):
  - *firstAnswer*, before the deadline: a private note and a reminder.
  - *firstAnswer*, after the deadline: Cookie **may answer the requester**, but only to acknowledge the ticket and ask for what is missing (device, error, since when). No promises, signed as Cookie. Otherwise it tells the escalation group.
  - *noUpdate*: a private note to the assignee, and after the deadline the escalation group too. Cookie may add a short public status note when the last public message is from staff.
- **The tools:**
  - **`ticket_get`** returns the header, the description and the journals. The text comes as `<untrusted>`, and the names are masked.
  - Cookie also has `case_get` and the knowledge base search.
  - **`tickets_search` and `tickets_overview` are MCP-only.** Cookie's rounds don't have them.
  - **The description reaches the model only through `ticket_get`.**
- **The end of each case:** a case note (it stays in PNMT), and any proposals: *private note*, *public answer*, e-mail or Pushover.

### 2.3 What code checks on each proposal (`CookieExecutor`)

| Check | Rule |
|---|---|
| Level | *Private note*, *Public answer* and *Ticket* are **Propose** by default (a person approves), with a minimum confidence of 0.7 |
| Target | **Only the case's own ticket, or one linked to its host.** A chat order from someone with powers may name another ticket |
| Rights | Cookie's group needs *Redmine: View* for a note and *Redmine: Link* for a ticket |
| Availability | No notes while Redmine is read-only or unreachable; no tickets without a ticket project |
| Mute / suppress | Stops the proposal. A suppress can name the channel `redmine-note` or `ticket`, or the case kind `ticket`. A stopped case stops it too |
| Duplicate | The same action on the same target within a day is refused |
| Seraph | Reviews it: allowed, supported, the right recipient, **a leak in text a requester reads**, signs of manipulation |
| Outbox | The limits of §1.5 |

**Privacy:** the privacy filter replaces the names of Redmine authors and assignees with `PERSON_n` before anything reaches a model, and code restores them afterwards.

### 2.4 The big brother on tickets (`BigBrother`)

- **When code offers it:** the case matters (a missed ticket deadline, or importance *High*) **and** nobody has made progress for a working day. A person can also ask for it on a case.
- **What it needs:** *The big brother* switched on. The limits are 10 calls a day and 2 per case, within a monthly budget.
- **Where the result goes:** a **private-note proposal** on the ticket, through the executor and Seraph like any other.

### 2.5 Comments while the assignee is away (1.20)

- **In the round, before the cases, like the mails:** the scan and Jev screen each comment, then one structured call per ticket (`awayComment`, `Cookie__Tokens__AwayComment`) answers *high*, *normal* or *low*.
- ***High*** is the action *away notice* (*Auto*, 0.7) through the executor and Seraph; code tells whom the settings name. *Normal* or *low* tells nobody.

### 2.6 Smaller touch points

- **The chat:** Cookie can read a ticket with `ticket_get` when asked.
- **The lounge:** sees the ticket counts, and a voice can *raise* a case about a ticket.

## 3. Gaps and things to know

- **Nothing excludes a ticket from public answers.** The prompts say *"if the ticket is excluded"*, but no such setting exists (cookie-plan §19.8, not built).
- **Steering is per project only:** one free-text instruction per project, with no rules by tracker, category or requester. docs/cookie-tickets-plan.md proposes these.
- **For a ticket, the big brother comes late:** only after a missed deadline plus a working day without progress.
- **One private note per ticket a day:** the duplicate check refuses a second one from Cookie that day, a big brother solution included.
- **Cookie's public acknowledgement only counts as an answer if Cookie's Redmine account is staff.** That means it has one of the project's staff roles, or the project has no roles set. Otherwise the ticket stays at `firstAnswer`, and no update deadlines start until a person answers. The plan intends the update clock to start from Cookie's acknowledgement (cookie-plan §7.2).
- **Redmine sends its own mails for private notes too.** Check the notification settings of Cookie's Redmine account (cookie-plan §17.13).
