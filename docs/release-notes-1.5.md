# PNMT 1.5: Cookie, the assistant, and the availability plan

Version 1.5.0.

## What's new

- **Cookie, PNMT's assistant** (docs/cookie-plan.md, packages nine and nine point five). It is off until `.env` has `Cookie__Enabled=true`, and stays stopped until an admin starts it on *Admin → Cookie*.
  - **The dashboard's Cookie card,** at the top of the left side: the overall state (*All fine*, *Info*, *Warning*, *Alert*) with one line saying why, and **your personal summary:** at most five lines, what needs you first. The same card heads Cookie's window.
  - **Cookie's window** (*Overview → Cookie*):
    - **the cases:** a host's bad sensors and Zabbix problems together, and each watched ticket, with its timeline, deadlines and people's comments. **Comment on a case, or stop it** (*Don't go on with this*, with why): Cookie leaves it alone and its reminders stop, until someone lets it go on or the problem grows. **Ask the big brother** for a fix;
    - **the trace:** each round's triage, Cookie's thinking, the tools it used, its notes and proposals;
    - **the decisions:** everything Cookie proposed and what came of it, with Seraph's verdict. **Rate each one *right* or *wrong*,** with a comment;
    - **the chat:** ask Cookie, or Seraph, about what you may see. With *Cookie: Instruct*, tell Cookie to mute a host or suppress a kind of message: it fills it in, and you confirm it.
  - **Cookie proposes, code decides.** Cookie can propose a Pushover or an e-mail to a person, a ticket, a private note or a public answer on a ticket, and a note on a host or sensor. Each proposal goes through code's checks (rights, limits, duplicates, mute and suppress), then **Seraph,** a second model that reviews everything that leaves PNMT or reaches people. **Every action starts at *Propose*:** it waits in the decisions inbox for a person, until an admin sets it to *Auto* on *Admin → Cookie → Actions*.
  - **Mail rules set to *AI every time*** are now decided by Cookie, with Seraph in place of the verification model. While Cookie is stopped, such mails wait.
  - **The big brother:** two strong models that propose a fix for a problem that matters and nobody has fixed for a working day, with a budget of their own. Its solution goes onto the ticket, or into the inbox as a proposed ticket. **Off until switched on.**
  - **Lessons:** once a day, people's ratings become short lessons Cookie and Seraph follow. A lesson like "no e-mail about toner" becomes a rule code enforces.
  - **Cookie reads, and only reads,** through tools that check rights in code: hosts and sensors, events, locations, the knowledge base (never a page marked sensitive), Redmine tickets, Zabbix problems and the availability plan.
  - **Privacy:** people's names, e-mail addresses and phone numbers become placeholders before anything leaves PNMT, and come back when it is shown. Requests go only to zero-data-retention endpoints with fp8 or better.
  - **Cookie has a budget of its own** (`Cookie__BudgetUsd`, 30 USD a month), and so does the big brother. A round in which nothing changed costs nothing.
- **The case book and the ticket deadlines, in code.** With `Cookie__Enabled=true`, PNMT keeps track of problems and of the tickets of the Redmine projects you watch, whether Cookie runs or not: a first answer within 4 working hours and an update at least every 14 days, with reminders and escalations, and a ticket for a problem that has shown no progress for 2 days. **Every duty is off until you switch it on.** Tickets and notes need a second Redmine key, `Redmine__WriteApiKey`. While Cookie runs, it can write the reminders' texts itself (*Actions*).
- **The availability plan** (*Admin → Availability plan*), like the team's Excel plan: a row per day with the responsible, the fallback and a comment, count columns such as *On site* and *Remote* (red below their minimum), and a column per person with their availability: *ANWE*, *HOME*, *VACA* and the other types you define, each with what it means (vacation, holiday, on site, work travel, home office, compensation).
  - **Everyone in the plan edits their own column;** *Availability: Edit* edits everything; *Fill* writes each person's usual week for the next 365 days, and a nightly fill keeps it filled.
  - **It routes Cookie's messages:** a reminder goes to the assignee, or to the project's escalation group when the assignee isn't working; a missed deadline also to whoever is responsible that day. **Outside working hours, Cookie reaches nobody.** The working hours and holidays live here now.
- **Your contact details are on your account:** your e-mail address and Pushover key on *Account*, with *Send a test*. Admins set the Redmine user, the short code, the colour and the usual week on *Admin → Users*.
- **New rights:** *Cookie* (*Use*: Cookie's window, comments, ratings; *Instruct*: stop a case, mute and suppress, the big brother; *Manage*: *Admin → Cookie*) and *Availability plan* (*View*, *Edit*, *Manage*). Admins have all of them; no group has them until you give them.

## Updating from 1.4

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.5.0`. It takes a backup first. The migrations run when the new image starts.

- **Cookie is off** until `Cookie__Enabled=true`. Then:
  1. give Cookie a group on *Admin → Cookie → Rights* (it reads with that group's rights, never with the admin flag), with *Availability plan: View* so it knows who works, and *Mail: Read* for the *AI every time* mails;
  2. fill in the availability plan's people, and let everyone set their contact details on *Account*;
  3. choose the watched projects on *Admin → Cookie → Redmine*;
  4. switch on the duties and actions you want on *Actions*;
  5. start Cookie.

  `deploy/.env.example` and [deploy/README.md](../deploy/README.md) describe the settings, including Seraph's model (`Cookie__SeraphModel`) and the big brother's (`Cookie__BigBrother__*`).
- **The situation analysis is gone,** with its three system sensors (*AI analysis: critical*, *warnings*, *info*), their findings and `Ai__Analysis__*`. Cookie's status replaces it. Notification rules on those sensors lose them.
- **Mail rules set to *AI every time*** go to Cookie while `Cookie__Enabled=true`; without Cookie, package one handles them as before. **The analysis of unhandled mails stays as it was.**
- **Package one's AI budget** (`Ai__Budget__Usd`) no longer counts Cookie's requests: Cookie has its own.
- **`./info.sh`** shows whether Cookie is available, and whether it may write to Redmine.

## Good to know

- **Nothing reaches people without a person, at first:** every action starts at *Propose*, Seraph reviews it, and the output limits apply whatever its level. Only an admin sets an action to *Auto*.
- **The full trace,** with Cookie's thinking and what its tools returned, is for *Cookie: Manage* and for whoever may see everything Cookie's group sees. Everyone else sees the notes and decisions of the cases they may see.
- **Messages from MCP** come with package eight.
