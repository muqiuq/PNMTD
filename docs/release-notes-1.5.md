# PNMT 1.5: Cookie, the assistant, watches

Version 1.5.0.

## What's new

- **Cookie, PNMT's assistant** (docs/cookie-plan.md, package nine). It is off until `.env` has `Cookie__Enabled=true`, and stays stopped until an admin starts it on *Admin → Cookie*. **In this release Cookie watches:** it explains, summarises and answers. It sends nothing, writes nothing outside PNMT and opens no tickets itself.
  - **The dashboard's Cookie card:** the overall state (*All fine*, *Info*, *Warning*, *Alert*) with one line saying why, as Cookie sees it, or as PNMT computes it when Cookie isn't running. Below it is **your personal summary:** at most five lines, what needs you first.
  - **Cookie's window** (*Overview → Cookie*):
    - **the cases:** a host's bad sensors and Zabbix problems together, and each watched ticket, with its timeline and deadlines;
    - **the trace,** like Copilot's: each round's triage, Cookie's thinking, the tools it used and what they returned, its notes and the status;
    - **the chat:** ask Cookie about what you may see. It answers with the lower of your rights and its own.
  - **Cookie reads, and only reads,** through tools that check rights in code: hosts and sensors, events, locations, the knowledge base (never a page marked sensitive), Redmine tickets and Zabbix problems.
  - **Privacy:** people's names, e-mail addresses and phone numbers become placeholders before anything leaves PNMT, and come back when it is shown. Host names and addresses stay. Requests go only to zero-data-retention endpoints with fp8 or better.
  - **Cookie has a budget of its own** (`Cookie__BudgetUsd`, 30 USD a month). A round in which nothing changed costs nothing.
- **The case book and the ticket deadlines, in code.** With `Cookie__Enabled=true`, PNMT keeps track of problems and of the tickets of the Redmine projects you watch, whether Cookie runs or not:
  - **a first answer within 4 working hours, and an update at least every 14 days,** with the hours per weekday and holidays with their own hours, the roles that count as staff, and the statuses that stop the clock, per project;
  - **reminders and escalations:** a private note on the ticket, and an e-mail to the assignee or the project's escalation group, Pushover too when a deadline is missed;
  - **a ticket for a problem that has shown no progress for 2 days,** linked to the host, and a closing note on it when the problem is resolved.

  **Every duty is off until you switch it on.** Tickets and notes need a second Redmine key, `Redmine__WriteApiKey`.
- ***Admin → Cookie*** (the new right *Cookie: Manage*) sets all of it:
  - Overview, with start, stop and run now, the outbox and the budget;
  - Identity;
  - every prompt, with its default and a preview;
  - an instruction per kind of case;
  - working hours;
  - who gets what, and the contacts;
  - the Redmine projects;
  - the duties and output limits;
  - the models;
  - Cookie's group.

  **Every change is versioned and can be rolled back.**
- **Output limits in code:** Pushover, e-mail, tickets and notes per person, per ticket and per day, up to ceilings only a release can raise. What is over a limit is held back and listed, and an admin can send it.
- **A new right, *Cookie*:** *Use* opens Cookie's window, *Instruct* is for package nine point five, *Manage* is *Admin → Cookie*. Admins have all three; no group has them until you give them.

## Updating from 1.4

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.5.0`. It takes a backup first. The migrations run when the new image starts.

- **Cookie is off** until `Cookie__Enabled=true`. Then:
  1. give Cookie a group on *Admin → Cookie → Rights* (it reads with that group's rights, never with the admin flag);
  2. set up the contacts and projects;
  3. switch on the duties you want;
  4. start Cookie.

  `deploy/.env.example` and [deploy/README.md](../deploy/README.md) describe the settings.
- **The situation analysis is gone,** with its three system sensors (*AI analysis: critical*, *warnings*, *info*), their findings and `Ai__Analysis__*`. Cookie's status replaces it. Notification rules on those sensors lose them. The mail analysis and the *AI every time* rules stay as they are until package nine point five.
- **Package one's AI budget** (`Ai__Budget__Usd`) no longer counts Cookie's requests: Cookie has its own.
- **`./info.sh`** shows whether Cookie is available, and whether it may write to Redmine.

## Good to know

- **Nothing Cookie writes leaves PNMT in this release:** its notes, summaries and replies stay in PNMT. Reminders, escalations and tickets are PNMT's own template texts, and they go out only when you switch them on.
- **The full trace,** with Cookie's thinking and what its tools returned, is for *Cookie: Manage* and for whoever may see everything Cookie's group sees. Everyone else sees the notes of the cases they may see.
