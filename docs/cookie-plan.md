# Plan: Cookie, the AI assistant (package nine)

| | |
|---|---|
| Status | **Revision 8: package nine point five (C and D) is built** (§21, As built §21.8), with **revision 7** (§20). **Package nine (A and B) is built** (§19, §19.8). Package nine point five is planned only. **Two packages: nine (Cookie watches) and nine point five (Cookie acts), §3.4.** The models are in §14, the big brother in §15, the review's answers in §16, the critical review in §17, and its answers in §18. The default prompts are in docs/cookie-prompts.md |
| Date | 2026-11-14 |
| Revision | 8: **package nine point five is built** (§21, As built §21.8): proposals through an executor and Seraph, the inbox for *Propose*, Cookie's texts for the duties, the *AI every time* mails, mute and suppress, the big brother, ratings and lessons. **Narrowed:** the unhandled-mail analysis stays with package one, and messages from MCP wait for package eight (§21.1).<br>7: **changes to package nine** (§20):<ul><li>**a person can stop a case, with a comment,** and anyone may comment on a case (§4.3);</li><li>**the dashboard's card in Cookie's window** too (§9.2);</li><li>**on the user:** e-mail, Pushover key, Redmine user, and for the plan a short code, a colour and a default week (§8.1);</li><li>**the availability plan,** a page of its own, after the team's Excel plan: a row per day with the responsible, the fallback and a comment, count columns, and each person's availability by type, always filled for 365 days ahead. **It is the base for Cookie's decisions about people,** and replaces *who gets what*, Cookie's working hours and people's quiet hours (§8.2 to §8.4, §9.4)</li></ul>6: **package nine is built** (§19, As built §19.8), and **Cookie and MCP** (§3.5): one set of read tools, built here, called directly by Cookie and offered over MCP by package eight.<br>5: **the critical review's answers applied** (§18):<ul><li>**two packages** (§3.4);</li><li>**Admin → Cookie** sets almost everything: working hours, who gets notified, output limits, case instructions, and prompts that ship with defaults (§9.3, docs/cookie-prompts.md);</li><li>**output limits in code** (§6.1);</li><li>**Seraph only on what leaves PNMT or reaches people** (§5);</li><li>**mute, away and suppress as objects** (§10.3);</li><li>**the trace by rights** (§9.2);</li><li>**privacy: `Code` by default, people only** (§13.2);</li><li>**the ticket clock** (§7.2).</li></ul>4: **a critical review** (§17), from two independent reads, with 18 questions on what to change (§18). The big brother cost in §15.5 is corrected.<br>3: **the big brother** (§15): **two models**, Kimi K3.1 Pro as the primary and a proposed backup, compared with the Claude, Gemini and OpenAI models. Each call picks one at random. Cookie asks for a solution when something important needs fixing and nobody is taking care of it. And a correction in §14.2 (Terminal-Bench, not SRE Bench).<br>2: **the review applied** (§16):<ul><li>working hours per weekday, plus holidays with their own hours (§7.3);</li><li>after a restart, Cookie resumes;</li><li>*AI every time* mail rules go to Cookie (§12);</li><li>temporary instructions, for whoever has the right *Cookie: Instruct* (§10);</li><li>package eight's tool for talking to the AI sends a message to Cookie;</li><li>escalations go to a PNMT group.</li></ul>1: the first draft, with the model research (§14) |
| Scope | Package nine of docs/ideas2.md: Cookie (the assistant loop), Seraph (the reviewer), the big brother, the privacy filter, the decision log with feedback, and Cookie's three pages. **Built as two packages** (§3.4): **nine**, the bookkeeping in code and Cookie watching, and **nine point five**, Cookie acting |
| Non-goals | **Deleting or changing anything** (Cookie only adds things). **Cookie changing its own configuration.** **Access to server consoles** (package ten). **Replacing notification rules:** they keep running whether Cookie is there or not. Training or fine-tuning |
| Depends on | Package one's AI foundation (`PNMTD/Ai/`: provider, Jev, request log, budget, decisions inbox). Five point five (groups and rights, audit with `Actor = Cookie / Seraph`). Six and seven (Redmine, Zabbix). **Not on eight:** Cookie comes first and builds the read tools that package eight's MCP server will offer (§3.5) |

---

## 1. Cookie in short

**Cookie is PNMT's assistant, created by Philipp.** It keeps an eye on everything PNMT knows (sensors, Zabbix problems, Redmine tickets, mails) and makes sure nothing falls through the cracks.

**Cookie works in rounds.** It wakes up, looks at what has changed, and decides whether anything needs doing. Usually nothing does, so it goes back to sleep for 5 minutes. When a sensor goes bad, it wakes up right away.

**What Cookie does for you:**

- It shows **the overall state** on the dashboard: *all fine*, *warning* or *alert*, with one line saying why.
- It gives **each person a short personal summary**: what matters to *you*, and what needs *you*.
- It **sends urgent things to the right person** by Pushover or e-mail.
- It **opens a ticket** when a problem is still unresolved after two days, or right away when the problem is serious.
- It **watches IT support tickets**. Every ticket must get a first answer within 4 hours and an update at least every 2 weeks. Cookie reminds people, and answers the ticket itself when it is sure.

**What Cookie never does:** delete or change anything. It only *adds*: a message, a note, a ticket, a comment.

**Seraph watches Cookie.** Every action Cookie wants to take goes to Seraph first. Seraph is a second AI, ideally from another vendor, and it approves the action, sends it back, or stops it. **Everything they decide goes into the decision log.** You can mark each decision as right or wrong and say why, and Cookie learns from that.

**When something important needs fixing and nobody takes care of it, Cookie asks its big brother.** That is one of two strong, more expensive models, chosen at random. It works out the cause and the fix, and Cookie puts the solution into the decisions inbox, or into the ticket for whoever works on it. The big brother only gives advice, and it has a budget of its own.

**You can talk to Cookie, and to Seraph,** in Cookie's window. That window also shows what Cookie is thinking and which tools it uses. **What Cookie does for you is limited by your own rights.**

**Admins set Cookie up:** its goals, personality, rules and limits. Cookie can't see or change any of this. **Cookie stays stopped until an admin starts it.**

**Privacy:** names of people are swapped for placeholders before anything leaves the house, and swapped back when the answer arrives. A local model can find more of them, if you turn it on.

**Cookie comes in two steps.** First it **watches**: PNMT keeps track of deadlines and sends reminders itself, and Cookie explains, summarises and answers questions. Then it **acts**: messages, tickets, answers, and the big brother.

---

## 2. Requirements

| # | Requirement (docs/ideas2.md, package nine) | Where |
|---|---|---|
| R1 | Cookie replaces the existing AI assistant (the mail analysis and similar) | §12 |
| R2 | Cookie runs in a loop, has goals, and can read everything through MCP, Redmine and Zabbix included. **Through the same tools as MCP, called inside PNMT** (§3.5) | §3, §4 |
| R3 | A dashboard section with the status (all fine, warning, alert …) | §9.1 |
| R4 | A window showing Cookie's thinking and tool calls, like Copilot in VS Code, where people can write to Cookie and to Seraph | §9.2, §10 |
| R5 | A configuration page Cookie can't reach: prompts, goals, personality | §9.3 |
| R6 | Cookie watches tickets, Zabbix and sensors, decides what is relevant to whom, and writes short personal summaries | §8 |
| R7 | Seraph checks every action and every decision. **Narrowed in review** (§18, answer 7): every action that leaves PNMT or reaches people, plus a sample of the rest | §5 |
| R8 | Pushover and e-mail to specific people when something is very urgent | §6 |
| R9 | A ticket for a problem nobody has resolved after 2 days, or at once when it is important | §7.1 |
| R10 | IT support tickets: a first answer within 4 h, an update at least every 2 weeks. Cookie may answer itself when it is very sure | §7.2 |
| R11 | Every decision is logged and visible. People rate it and comment, and the ratings are summarised and fed back to Cookie | §11 |
| R12 | Protection against prompt injection. No personal or IP-protected information goes to the AI (best effort) | §13 |
| R13 | A local model returns the personal data it finds as JSON; code replaces it and restores it afterwards. On or off in `.env` | §13.2 |
| R14 | What Cookie does for someone depends on that person's rights | §10 |
| R15 | Cookie sleeps 5 minutes when there is nothing to do, and a bad sensor wakes it | §4.1 |
| R16 | Cookie's creator is Philipp | §9.3 (identity) |
| R17 | Cookie is stopped by default, even when enabled in `.env`. An admin starts it | §9.3 |
| R18 | Cookie only adds information. It never deletes or edits | §6 |
| R19 | The "big brother": two stronger models (a primary and a backup, picked at random). When something important needs fixing and nobody is taking care of it, Cookie asks one of them for a solution and puts it into the decisions inbox or the ticket (added in review) | §15 |
| R20 | Per case, a person tells Cookie not to go on with it, and leaves a comment (revision 7) | §4.3 |
| R21 | The summary on the dashboard is in Cookie's window too (revision 7) | §9.2 |
| R22 | The Pushover key, the Redmine user name and the e-mail address are set on the user, not under Cookie (revision 7) | §8.1 |
| R23 | An availability plan, its own admin page, like the team's Excel plan: one row per day with the responsible, the fallback and a comment, count columns, and each person's availability, always filled for the next 365 days, with a button that fills it. Types with checkboxes (vacation, holiday, on site, work travel, home office, compensation) and a default. On the user: a short code of 3 letters, a colour, and the days they usually work. Everyone edits their own availability; some may edit everything. A table to scroll into the past and the future, with a button for today. **The base for Cookie's decisions about people** (revision 7) | §8.1, §8.2, §8.4, §9.4 |

---

## 3. Architecture

### 3.1 Overview

```
 Triggers ─────────► Scheduler ─────► Cookie: one round (§4)
 timer · bad sensor ·                  │  reads through the tool layer
 new ticket/problem ·                  │  (the read tools of §3.5, Cookie's group rights)
 deadline · message                    │  proposes actions, never runs them
                                       ▼
                           Privacy filter  ◄──►  remote models
                                       ▼
                                    Seraph ──► approve · revise · reject · ask a person
                                       ▼
                           Executor (code): closed list of add-only actions, limits, rights
                                       ▼
            Dashboard · chat · notes · Pushover · e-mail · Redmine
 Everything ──► trace, decision log, audit log (Actor = Cookie / Seraph)
```

### 3.2 Components

| Component | Job | AI? |
|---|---|---|
| **Scheduler** | Wakes Cookie on a timer or a trigger, groups triggers that arrive close together, and runs one round at a time | No |
| **Case book** | Turns what changed into **cases** (§4.3), keeps their timelines, deadlines and links, and closes them when they are resolved | No |
| **Cookie** | Judges: what matters, to whom, how urgent it is, what to write, whether to act | Yes (main model, with tools) |
| **Tool layer** | Read tools (hosts, sensors, events, locations, the knowledge base, Redmine, Zabbix, mail), checked against **Cookie's group's rights**. Built in package nine, and offered over MCP by package eight (§3.5) | No |
| **Privacy filter** | Swaps personal data for placeholders before a remote call, and back afterwards (§13.2) | Local model + code |
| **Seraph** | Reviews every proposal against the evidence, the rules and the lessons (§5) | Yes (second model) |
| **Big brother** | Two strong models, a primary and a backup. They write solutions for important problems nobody is fixing. Advice only (§15) | Yes (a strong model, used rarely) |
| **Executor** | Runs approved actions. It checks rights, limits and targets **again, in code** (§6) | No |
| **Memory** | Case notes, the lessons learned from feedback (§11), people's preferences | Written by AI, kept by code |
| **Trace and decision log** | Every round, step, tool call, proposal, verdict and result (§9.2, §11) | No |

### 3.3 The core rule: code keeps the books, the AI judges

**Code handles deadlines, timers, deduplication, "what changed", rights and limits.** The AI handles relevance, urgency, wording and the decision to act. This split makes Cookie reliable, because nothing depends on a model remembering a deadline. It also makes Cookie cheap: a quiet round costs one small call, or none.

### 3.4 Two packages

**Package nine: Cookie watches.** It is useful even before Cookie writes anything itself.

| Step | What it brings |
|---|---|
| **A: the bookkeeping, in code, with no AI** | <ul><li>**the case book** (§4.3);</li><li>**the deadlines,** with working hours and holidays (§7);</li><li>**reminders and escalations sent by code,** from template texts;</li><li>**the ticket after 2 days,** from a template;</li><li>**the overall status,** computed from sensors, cases and deadlines;</li><li>**the output limits** (§6.1);</li><li>**who gets what** (§8): since revision 7, the availability plan and the contact details on the user;</li><li>**Admin → Cookie**, with everything these need (§9.3)</li></ul> |
| **B: Cookie watches** | <ul><li>Cookie's loop, **reading only;**</li><li>the status, as Cookie sees it, with its explanation;</li><li>**the personal summaries;**</li><li>**the chat** with Cookie;</li><li>**the trace** (§9.2);</li><li>the privacy filter in `Code` mode (§13.2);</li><li>**the prompts and case instructions** on the admin page, with their defaults (docs/cookie-prompts.md);</li><li>package one's situation analysis is removed (§12)</li></ul> |

**Package nine point five: Cookie acts.** It builds on nine.

| Step | What it brings |
|---|---|
| **C: actions** | <ul><li>the actions of §6, **with Seraph** (§5);</li><li>the levels *Off · Propose · Auto*;</li><li>the rollout from *Propose* to *Auto* (§13.4);</li><li>**Cookie writes the reminders and escalations itself.** Code's templates stay as the fallback whenever Cookie is stopped or paused;</li><li>package one's mail analysis and the *AI every time* rules go to Cookie (§12)</li></ul> |
| **D: the rest** | <ul><li>**the big brother** (§15);</li><li>**ratings and lessons** (§11);</li><li>**mute and suppress** (§10.3);</li><li>the chat with Seraph;</li><li>messages from MCP (§10.2)</li></ul> |

### 3.5 Cookie and MCP (package eight)

**One set of tools, two ways in.** PNMT's read tools live in the server: each has a name, its inputs, the right it needs (area and level), and whether it reads or writes.

- **The MCP server (package eight) offers them to tokens,** with the token's user's rights.
- **Cookie calls the same tools directly, inside PNMT,** with its group's rights. **It doesn't go through the MCP server:** no token, no 12-hour expiry, no TOTP, and no Jev on Cookie's own calls.
- **Whichever package comes first builds the tools.** Cookie comes first, so package nine builds the read tools (§19), and package eight adds the MCP server and the write tools on top.

**What the tools do once, for both:** the rights check; `MachineAccess`, so a page marked sensitive reaches neither; outside text marked as untrusted; results that give ids, are short, and come in pages; the audit log (`Actor = Mcp` or `Cookie`). **Swapping people's names for placeholders is Cookie's own step** (§13.2); an MCP user sees what their rights allow.

**The boundaries:**

- **Cookie only gets the read tools.** MCP's write tools (UDP commands, adding hosts) are never in Cookie's list; code filters on the read/write mark. Cookie acts only through the closed list of §6.
- **Cookie's tools leave out MCP-only tools,** above all "talk to Cookie", so Cookie can't message itself.
- **Over MCP, a client can read** Cookie's status, the person's summary, cases and the trace (by the rights of §9.2), and **send messages** (§10.2). **Approving a proposal and confirming a mute or a suppress happen only in PNMT's UI,** so an AI behind an MCP client can never approve another AI's proposal.
- **Messages to Cookie are capped per token** (in code), because each one starts a round and costs budget.
- **Cookie doesn't use MCP servers outside PNMT.** That would widen what Cookie can reach, and what can reach it.

---

## 4. The loop

### 4.1 States and wake-ups

| State | Meaning |
|---|---|
| **Stopped** | The default. Nothing runs. Only an admin can start Cookie |
| **Sleeping** | Waiting for the timer or a trigger |
| **Working** | A round is running |
| **Paused** | Blocked: the budget is used up, the privacy filter can't be reached, or there were too many errors. The reason is shown, and Cookie resumes by itself once the cause is gone. **If only Seraph can't be reached, Cookie keeps working,** and its outbound actions wait at *Propose* (§5). **Code's deadlines, reminders and escalations keep running in every state** (§3.4 A) |

**What wakes Cookie:**

- **The timer:** 5 minutes after a round that found nothing to do (configurable).
- **A sensor goes bad**, or a new Zabbix problem appears at the configured severity or above. **Triggers are grouped for 30 seconds**, so a storm causes one round, not fifty.
- **A new or changed ticket** in a watched Redmine project.
- **A deadline falls due** (§7). Code sets these timers.
- **A message** from a person (at once), or an admin's *Run now*.

**When a round leaves work undone** (for example, more cases than one round may handle), **the next round starts right away**, up to a limit. Otherwise Cookie goes back to sleep.

### 4.2 One round, step by step

1. **Collect (code).** Gather what changed since the last round: events, Zabbix problems, tickets and their updates, mails, messages. Update the case book and find the deadlines that are due.
2. **Screen (code + Jev).** Mark all outside text as untrusted, and check it for injection (§13.1).
3. **Pseudonymise** (privacy filter, if it is on).
4. **Triage (Cookie, a short call).** Cookie gets a compact **brief**: the overall state, the open cases that changed or are due, and a one-line summary of the rest. It answers with the cases that need attention and an updated status. **If none need attention, the round ends here.**
5. **Work (Cookie, with tools), case by case.** Cookie reads more where it needs to (history, related tickets, the knowledge base, similar past cases), then decides: *nothing*, *a case note*, or *a proposal*. Every proposal is structured: the action, the targets (as ids), the text, the reason, the evidence (as ids), a confidence and an urgency.
6. **Review (Seraph).** Seraph reviews each proposal that leaves PNMT or reaches people (§5).
7. **Execute (code).** Restore the placeholders, check rights, the output limits (§6.1) and duplicates again, then run the action and record the result.
8. **Status.** Update the status and its explanation. **Personal summaries are written when someone opens the dashboard,** and cached for 15 minutes.
9. **Notes for next time.** Cookie writes short case notes, for example "waiting for PERSON_2's reply, look again tomorrow". Then the scheduler picks the next wake-up.

**Each round has limits:** tool calls (about 30), tokens, cases, wall time. Cookie also has a monthly budget, either its own or a share of the AI budget. **When the budget is used up, Cookie is *Paused*,** and the regular notification rules keep working.

### 4.3 Cases: what Cookie remembers between rounds

**A case is one thing Cookie keeps track of:** a bad sensor, a Zabbix problem, a ticket, or a person's request.

- **Code opens and closes cases.** A case closes when the sensor is OK again, the problem is resolved, the ticket is closed, or the request has been answered.
- **A case holds:** its links (host, sensor, location, ticket, Zabbix problem), a timeline, Cookie's notes, its deadlines, and the actions taken so far.
- **Related things become one case.** A bad sensor, a Zabbix problem on the same host and a ticket about it are one case. Code proposes the grouping (by host and location), and Cookie can merge or split cases.
- **Cookie never reads the raw streams.** It sees cases, which keeps its context small, current and cheap.
- **People comment on a case** (revision 7). Whoever may see the case and has *Cookie: Use* adds a comment. It goes into the case's timeline with its author, and **Cookie reads it in the next round,** screened and pseudonymised like a message (§13).
- **A person can stop a case:** *Don't go on with this*, with a comment that says why. It needs *Cookie: Instruct*. Then:
  - **Cookie leaves the case alone:** no triage, no tool work, no notes. The brief lists it in one line, as stopped, with the comment;
  - **its duties stop:** what is due is closed as *stopped*, and nothing new falls due;
  - **it raises the overall state to *Info* at most,** and no summary says it needs anyone;
  - **it stays open and visible,** marked *stopped*, with who, when and why.
- **A stop ends** when the case closes, when someone with *Instruct* lets Cookie go on (*Go on*, with a comment if they like), or, for a problem case, **when a new member joins** (another sensor of the host goes bad, or a new Zabbix problem). The problem has grown, so the case goes on by itself, and its timeline says why.
- **Comments, stops and *Go on* are in the audit log.** PNMT's notification rules don't change: they still fire for a stopped case's sensors.

### 4.4 What makes Cookie smart

- **A fresh, small context** every round (the brief plus the cases), and **tools to look deeper** when it needs to.
- **It looks before it writes.** Before answering a ticket, Cookie searches the knowledge base and similar closed tickets, and uses them as evidence.
- **It knows the people.** Who gets what comes from the availability plan (§8.2, §8.3). Redmine assignees and watchers, rights, and each person's own interests add to it.
- **Lessons from feedback** are part of every prompt (§11).
- **A confidence threshold per action.** Below the threshold, the action goes to a person instead (§6).
- **A second opinion.** Seraph catches mistakes before anyone sees them.

---

## 5. Seraph

- **A separate model, ideally from another vendor,** with its own prompt, set by admins.
- **What Seraph reviews:** **every action that leaves PNMT or reaches people.** That means Pushover, e-mail, Redmine tickets and notes, and events on a mail rule's sensor.
  - Internal things (case notes, the status, summaries, chat replies) are not reviewed one by one. **Seraph takes a sample** (1 in 10 by default) for its daily drift report.
- **Whether Seraph needs a stronger model** than Cookie's (for example GPT-6.1 Sol, §15.6) is decided after the *watch only* phase.
- **What Seraph gets:**
  - the proposal;
  - **the evidence, read again by code from the source**, not Cookie's retelling of it;
  - the rules, goals and lessons;
  - Cookie's recent similar decisions.
- **What Seraph checks:**
  - Is the action allowed?
  - Does the evidence support it?
  - Is it the right recipient?
  - Is it a duplicate?
  - Is anything leaking (personal data, secrets, internal details in a ticket the requester can read)?
  - Is the tone right?
  - Are there signs of injection?
- **The verdicts:**
  - **Approve.**
  - **Revise**, with a reason. Cookie gets one retry.
  - **Reject**, with a reason.
  - **Ask a person:** the proposal goes to the decisions inbox.
- **Seraph can't act.** Its tools only read evidence.
- **Seraph's own loop:** besides reviewing proposals as they come in, it looks at Cookie's decisions once a day for drift (too many alerts, the same mistake again and again) and writes a short report into the decision log.
- **It fails closed only for what it reviews.** If Seraph can't be reached:
  - **outbound actions wait at *Propose***, so a person decides;
  - internal work goes on;
  - code's reminders and escalations go out as usual (§3.4 A).

---

## 6. What Cookie may do

**The executor has a closed list of actions, and every one of them only adds.**

| Action | Limits | Default |
|---|---|---|
| Status and personal summaries on the dashboard | Only what the reader may see (§8) | Auto |
| Reply in Cookie's window | Within the asker's rights (§10) | Auto |
| A note on a host, sensor or location | Add only | Auto |
| **Pushover** to a person | Only when **very urgent**. Only PNMT users with a Pushover key. At most 3 per person per day. Only in working hours (§8.3) | Auto |
| **E-mail** to a person | Urgent or important. Only PNMT users' own addresses | Auto |
| **Create a Redmine ticket** | §7.1. **Never while an open ticket is linked to the case.** At most N per day. Needs the separate write key (audit plan §8.7) | Propose |
| **Add a note to a Redmine ticket** | A private note to staff (reminders) or **a public answer** (§7.2) | Private: Auto · Public: Propose |
| An event on a mail rule's sensor | Only for a mail that matched an *AI every time* rule, only on that rule's sensor, and only with that rule's OK or fail code (§12) | Auto |
| Hand it to a person | Anything else, or anything below the confidence threshold: an entry in the decisions inbox | Always |

- **The level per action is set by admins:**
  - **Off**: the action is never used;
  - **Propose**: a person approves the action in the decisions inbox, after Seraph;
  - **Auto**: Seraph's approval is enough.
- Cookie starts careful; admins move actions to *Auto* once they trust it.
- **Cookie never:**
  - deletes;
  - changes fields, status or assignees;
  - closes tickets;
  - changes PNMT settings;
  - sends UDP or router commands;
  - opens a server console (package ten);
  - writes to anyone who isn't a PNMT user;
  - reads pages marked as sensitive.
- **Every target is an id from PNMT's data** (a user id, a ticket id, a host id), never an address or a name taken from free text. An injected "send this to x@example.com" therefore has nowhere to go.

### 6.1 Output limits

**Code limits how much goes out, whatever Cookie decides.** The executor counts every message, ticket and note before sending it. The limits apply to Cookie and to code's own reminders alike.

| Limit | Default |
|---|---|
| Pushover per person | 3 a day, 1 an hour |
| E-mail per person | 10 a day |
| Pushover and e-mail in total | 30 a day |
| New Redmine tickets | 5 a day |
| Notes on one ticket | 2 a day |
| Notes on Redmine in total | 30 a day |
| Public answers on tickets | 10 a day |
| Notes on hosts, sensors and locations | 50 a day |
| Big brother calls | 10 a day, 2 a day per case (§15.5) |

- **The values are set on the admin page** (§9.3), **up to a ceiling in code** that only a new release can raise. Cookie can't change them.
- **What is over a limit isn't sent.** It is logged as *held back: limit*, and the admin page and Cookie's status show that a limit was hit. What was held back can be sent by a person from the log.

---

## 7. The rules Cookie enforces

### 7.1 Problems nobody resolves

- **A bad sensor or an open Zabbix problem opens a case.**
- **If the case shows no progress for 2 days, a ticket is created** (by code from a template in package nine, by Cookie in nine point five). Progress means a Zabbix acknowledgement, a note on the host or sensor, or a linked ticket that is moving. PNMT itself has no acknowledgement. The ticket goes into the configured project, is linked to the host, sensor and location, and carries the case's timeline. The 2 days can be changed.
- **When it's serious, Cookie acts at once.** Examples: a host with importance High is down, or many hosts at one location fail together. Cookie judges this, and Seraph checks the judgment.
- **Once the problem is resolved, Cookie adds a closing note to its ticket** ("resolved at …"). It does not close the ticket itself.

### 7.2 IT support tickets

| Rule | Reminder | When the deadline is missed |
|---|---|---|
| **First answer within 4 working hours** (§7.3): staff acknowledge the ticket or ask for more | At 3 working hours: a private note and a push to the assignee or the team | **Cookie answers itself**, if it is confident: it acknowledges the ticket and asks for what is missing. Otherwise it tells the project's escalation group |
| **An update at least every 14 days** | At 12 days: a reminder to the assignee | Cookie nudges the assignee and tells the escalation group. It can post a status note for the requester |

- **When Cookie acknowledges a ticket, the update clock starts.** The 14 days, or whatever the project sets, then run from Cookie's acknowledgement. **The reports still count it as a missed first answer by staff.**
- **Each watched project has, on the admin page:**
  - its deadlines;
  - **which Redmine roles count as staff.** Only their answers count as the first answer;
  - **which statuses stop the clock** (for example *Feedback*, *On hold*);
  - **its escalation group**, an ordinary PNMT group (audit plan §5) whose members are told when a deadline is missed. **Tickets nobody is assigned to** get their reminders sent to this group.
- **Cookie only answers a ticket itself when:**
  - its confidence is above the threshold;
  - its answer rests on the knowledge base or on similar tickets;
  - **the answer is signed as Cookie's;**
  - Seraph approves.
- **Categories and requesters can be excluded** from Cookie's own answers.

### 7.3 Working hours and holidays

- **The working hours and holidays are in the availability plan's settings** (§8.2, revision 7). Each weekday has one or more from–to periods, or is *closed*.
- **Holidays are a list of dates, each with its own hours:** *closed*, or special hours (for example 24 Dec, 08:00–12:00). A holiday's hours replace that weekday's hours. Closed days are greyed in the plan.
- **The 4 hours of §7.2 count only working hours.** A ticket that arrives on Friday at 16:00, with hours of 08:00–17:00, is due on Monday at 11:00.
- **The 14 days count calendar days.** A reminder or escalation that falls outside working hours goes out when the next working period begins.
- **Everything is in PNMT's time zone.** **There are no personal quiet hours any more:** outside working hours, Cookie reaches nobody (§8.3).

---

## 8. People, relevance and summaries

### 8.1 The user: contact details, short code and colour

**Revision 7: what belongs to a person is on their PNMT user, not under Cookie.** Cookie, its duties, the availability plan, and anything else in PNMT that names or reaches a person use it.

- **Contact details:**
  - **an e-mail address;**
  - **a Pushover user key;**
  - **a Redmine user:** the name Redmine shows on tickets, which is how an assignee is found.
- **For the availability plan** (§8.2):
  - **takes part:** the user has a column in the plan;
  - **a short code** of 3 letters, unique (*NME*), and **a colour.** The plan shows the person by them everywhere: in the column's header, and as responsible or fallback;
  - **the default week:** for each weekday, whether the person usually works, and with which type when it isn't the default (for example *HOME* on Fridays). The fill uses it.
- **Who sets what:**
  - each user sets their own e-mail address and Pushover key on *Account*, with *Send a test*;
  - admins set everything on *Admin → Users*, and *Availability: Manage* sets the plan's fields on the plan's *People* tab;
  - **only admins set the Redmine user,** because it decides whose tickets' reminders a person gets.
- **The notification rules keep their own recipients** (`pushover://…`, addresses), and don't change.

### 8.2 The availability plan

**The availability plan is the base for Cookie's decisions about people** (revision 7): who is responsible, who works, and where. It is *Admin → Availability plan*, a page of its own (§9.4). It follows the Excel plan the team uses today, and replaces Cookie's recipient rules, its *Working hours* tab and people's quiet hours.

- **A table with one row per day.** It scrolls into the past and the future, and **Today** jumps back:

  | Day | Date | Responsible | Fallback | Comment | Infra on site | Remote | … | OAR | RSO | … | NME | LMA |
  |---|---|---|---|---|---|---|---|---|---|---|---|---|
  | Tu | 17.11.2026 | NME | LMA | | 3 | 0 | | ANWE | ANWE | | ANWE | ANWE |
  | We | 18.11.2026 | LMA | PAL | | **1** | 3 | | EXTN | HOME | | UNAV | ANWE |
  | Sa | 21.11.2026 | | | | | | | | | | | |

  - **Responsible and fallback:** one person each, by their code and colour;
  - **a comment** for the day;
  - **the count columns** (below);
  - **one column per person taking part,** with the day's availability type, by its code and colour.

  **Weekends and closed holidays are greyed,** and need nothing.
- **The availability types are set in the plan's settings.** Each type has a name, a short code (*ANWE*, *HOME*, *VACA*, *UNAV*, *EXTN*, *KOMP*), a colour, and **what it means, as checkboxes:** *vacation*, *holiday*, *on site*, *work travel*, *home office*, *compensation*.
  - **Several can be checked, and nothing checks them against each other:** *vacation* with *holiday* is fine. A type with none checked, such as *UNAV*, means not working.
  - **One type is the default** (*ANWE*), and **one is for the days a person doesn't work** (*UNAV*).
- **Who works:** a person works on a day when its type is checked *on site*, *work travel* or *home office*, and none of *vacation*, *holiday* or *compensation*. Code reads the checkboxes, never the names.
- **The count columns** count the people of a day by the checkboxes. For each one the settings have:
  - a name, such as *Infra on site*, *In office* or *Remote*;
  - which checkboxes count, any of them: *home office* or *work travel* for *Remote*, for example;
  - which people count: everyone, or a PNMT group;
  - **a minimum,** if wanted. Below it the cell turns red, as the 1 on 18.11. does in the Excel plan;
  - whether it is shown.
- **The plan is always filled for the next 365 days:**
  - **Fill** writes every empty working day from each person's default week (§8.1). It writes the weekday's type on the days they work and the type for days off on the others;
  - **a nightly task fills the day that newly comes into reach,** so the plan never runs short;
  - **the fill never overwrites what a person set.** *Fill again*, after a default week changed, replaces only what the fill wrote.
    Since 1.6.1 (docs/adjustments1.md) it asks for **the days** (from today, at most 365 ahead; never the past), **the people** (everyone by default), **the types it may replace** (every type by default, e.g. only on-site days so holidays stay), and whether it may **also replace what people set themselves**; a cell it replaces counts as the fill's from then on (`AvailabilityFillRequest`).
- **Who edits:**
  - **everyone taking part edits their own column,** from today on;
  - ***Availability: Edit*** edits everything: every column, the past, the responsible, the fallback and the comments;
  - ***Availability: Manage*** sets the settings and the people.

  Every save is in the audit log.
- **The working hours and holidays are in the settings too** (§7.3). The ticket clocks count them.
- **Who is responsible**, in working hours:
  1. the day's responsible, if they work that day;
  2. otherwise the day's fallback, if they work;
  3. otherwise **the global fallback,** a user or a PNMT group from the settings;
  4. if nobody is left, the message is held back in the outbox as *nobody to reach*.
- **The plan warns in red:**
  - a responsible or fallback who doesn't work that day;
  - a working day without a responsible;
  - a count below its minimum;
  - an empty cell within the next 365 days.

### 8.3 Who gets what

- **What concerns a case without a person of its own goes to whoever is responsible** (§8.2). In package nine, that is a missed deadline. In nine point five, it is also Cookie's own messages about problems.
- **A ticket's messages:**
  - **a reminder** goes to the assignee, found by their Redmine user (§8.1). It goes to the project's escalation group when nobody is assigned, or when the assignee doesn't work that day or isn't a PNMT user;
  - **a missed deadline** goes to the assignee, the escalation group and whoever is responsible;
  - **a project without an escalation group** has whoever is responsible stand in for it.
- **Members of a group who don't work that day are skipped.**
- **The channel follows the urgency:** *important* by e-mail, *urgent* by e-mail and Pushover, each only when the person has an address or a key.
- **Only in working hours.** Outside them, Cookie reaches nobody, and its messages wait for the next working period. **PNMT's notification rules still alert at any hour.** The plan has no off-hours column. If Cookie is to wake someone at night, one comes back.

### 8.4 What Cookie does with the plan

- **Code routes** (§8.3). **Cookie reads the plan to judge and to explain.**
- **Its brief has today's staffing:**
  - who is responsible, and the fallback;
  - who works, and where: on site, travelling, at home;
  - the counts below their minimum;
  - the day's comment.

  **People are placeholders** (`PERSON_n`), never their codes, and the codes in the comment are replaced as well.
- **A read tool, `availability_get`,** gives Cookie a range of days in the same form, for questions such as "who is on site tomorrow?". It needs *Availability: View* in Cookie's group.
- **What Cookie does with it:**
  - **in its status and the summaries:** "you are responsible today", "the responsible is on vacation, the fallback stands in", "only 1 on site on Wednesday". **The code's status says *Info* when a count is below its minimum today, or nobody responsible works;**
  - **for a problem that needs hands on site** (a host that is off, a broken disk): it names who is on site, in its note and in the chat;
  - **in nine point five:** it proposes a message to someone on site, through Seraph and the limits.
- **Cookie never changes the plan.**

### 8.5 Summaries and the status

- **"Needs you" comes from code:** the tickets assigned to the reader, and the open problems while the reader is responsible (§8.2).
- **For the summaries, Cookie also uses** Redmine assignees and watchers, each person's rights, the interests people set themselves (locations, hosts, topics), and what they have already seen.
- **The personal summary is short:** at most 5 lines, **"needs you" first**, then what changed since the person's last visit. **It is written when the person opens the dashboard,** and cached for 15 minutes.
- **Rights are filtered in code before Cookie writes anything.** A summary only ever contains what that person may see, and it carries the areas of its sources (§9.2).
- **The status:**
  - the overall state: **All fine · Info · Warning · Alert**, with a one-line reason and the cases behind it. **Code computes a state of its own from sensors, cases and deadlines** (§3.4 A). Cookie's state replaces it while Cookie is running, and code's is shown whenever Cookie isn't;
  - Cookie's own state, from §4.1.

---

## 9. Pages

### 9.1 Dashboard section

- **Over coffee** (since 1.6.1, docs/adjustments1.md): first, one sentence each from Cookie and Seraph on how things are, as told to a colleague over coffee. Cookie's comes from the triage or the status step of a round whose state changed (`TriageAnswer.Coffee`, `StatusAnswer.Coffee`); Seraph's from a small call of its own after such a round, at most every 30 minutes (`Seraph.CoffeeAsync`, purpose `seraph.coffee`), shown while it is less than 6 hours old. Both live on `cookie_runtime`, and are shown to whoever sees all cases, and to everyone while all is fine (a sentence may mention a case the reader can't see); without a model for Seraph, only Cookie's.
- **The status:** the overall state and its reason, plus Cookie's own state.
- **Your summary:** the personal summary, with links to the cases.
- **Notes:** a hint when Cookie is waiting for you (a proposal to approve, a question).

### 9.2 Cookie's window

- **The dashboard's card heads the window too** (revision 7), above its tabs: the overall state and your summary, the same as on the dashboard and from the same cache, so it costs no second call.
- **The cases:** the ones the reader may see, each with its timeline, deadlines and comments. ***Comment*** for *Cookie: Use*, ***Stop*** and ***Go on*** for *Cookie: Instruct* (§4.3).
- **A live trace, like Copilot in VS Code:**
  - rounds, with their steps;
  - Cookie's reasoning;
  - tool calls, with their arguments and results (folded);
  - proposals, with Seraph's verdicts and the results.
- **Who sees what:**
  - **the full trace, with reasoning and tool results,** only for people whose rights cover everything Cookie's group can read, and for *Cookie: Manage*. A round's reasoning mixes several cases, and free text can't be filtered by rights;
  - **everyone else sees the trace per case:** the actions, proposals and verdicts of the cases they may see;
  - **every note, summary and lesson Cookie writes is tagged with the areas its sources came from** (Mail, Redmine, Zabbix …). It is shown only to readers who have all of those areas;
  - placeholders are restored for display.
- **The chat:** write to **Cookie** or to **Seraph**, and they reply in the same thread (§10).
- **Filters** by round, by case and by kind, and **links** from every entry to its decision and case.

### 9.3 Admin → Cookie

**This is where Cookie is adjusted.** It is set up once, and tuned again and again. **Everything here ships with defaults,** including every prompt (docs/cookie-prompts.md), so Cookie works from the first start. **Every change is versioned** (who, when, what), can be compared with an earlier version, and can be rolled back. **Cookie can't reach any of this:** no tool reads or writes it, and the page needs *Cookie: Manage* (§10.1).

| Tab | What is set there |
|---|---|
| **Overview** | **Start · Stop · Run now.** Cookie's state, the last rounds, the budget used, the limits hit (§6.1), and what is waiting at *Propose*. `Cookie__Enabled=true` only makes Cookie *available*: it is **Stopped** until an admin starts it. **After a restart it resumes** if it was running. Only a fresh installation, or the first time `Cookie__Enabled` is set, starts *Stopped*. Every start and stop is in the audit log |
| **Identity** | Name, **personality, tone, language**, "created by Philipp". **The prompts:**<ul><li>Cookie's identity and goals;</li><li>Cookie's rules;</li><li>Seraph's;</li><li>the big brother's;</li><li>the summary;</li><li>the status explanation;</li><li>the lessons summariser</li></ul>Each prompt has *Reset to default* and a preview of the full prompt, as a model receives it |
| **Cases** | **An instruction for each kind of case.** What Cookie should look at, what counts as urgent, what to do, and what never to do. The kinds:<ul><li>a bad sensor;</li><li>a Zabbix problem;</li><li>a problem that keeps coming back;</li><li>many things failing at once;</li><li>a problem unresolved for 2 days;</li><li>a support ticket waiting for its first answer;</li><li>a ticket without an update;</li><li>a mail from an *AI every time* rule;</li><li>a person's request</li></ul>**Admins can add instructions for a narrower scope**, such as a location, a host, a Redmine project or a sensor type. The narrower instruction is added after the general one |
| **Redmine** | The watched projects. For each one:<ul><li>its deadlines;</li><li>its escalation group;</li><li>**the roles that count as staff;**</li><li>**the statuses that stop the clock;**</li><li>the project for Cookie's own tickets;</li><li>categories and requesters excluded from Cookie's answers (§7.2)</li></ul> |
| **Actions** | For each action of §6:<ul><li>its level (*Off · Propose · Auto*);</li><li>its confidence threshold;</li><li>**its output limits** (§6.1)</li></ul> |
| **Mute, suppress** | The active objects of §10.3, who created them, until when, and *End now*. New ones can be created here too |
| **Lessons** | What Cookie learned (§11): see, edit, disable |
| **Models** | Cookie's, Seraph's and the big brother's models and providers, which override the `.env` defaults. Also reasoning effort per step, the budgets, the limits per round, and Seraph's sample rate |
| **Rights** | **Cookie's group:** the rights Cookie reads with. Never the admin flag (audit plan §5.7) |

**Revision 7 removed two tabs.** *Working hours* is in the availability plan now (§9.4). *Notifications* is gone: who gets what is the availability plan (§8.2, §8.3), and the contact details are on the user (§8.1).

### 9.4 Admin → Availability plan

**A page of its own in the admin menu** (revision 7), for the plan of §8.2. It works whether Cookie is enabled or not; Cookie and its duties are what use it so far.

| Tab | What is there |
|---|---|
| **Plan** | **The table of §8.2, like a spreadsheet:**<ul><li>the header and the first columns stay in place;</li><li>it scrolls into the past and the future, loading as it goes;</li><li>**Today** jumps back, and today's row is marked;</li><li>click a cell to pick a type, or type its code;</li><li>select several cells to set them at once;</li><li>**Fill** and *Fill again*;</li><li>the warnings of §8.2 in red</li></ul> |
| **People** | Who takes part and in which order, with their short code, colour and default week (§8.1) |
| **Settings** | **The availability types** with their checkboxes, the default and the type for days off; **the count columns**; **the working hours and holidays**; **the global fallback** |

- **The menu entry shows for everyone taking part,** who opens the plan without a right and edits their own column there, and for *Availability: View*.
- **A new area *Availability*:**

  | Level | What it allows |
  |---|---|
  | **View** | The plan |
  | **Edit** | View, plus every column, the past, the responsible, the fallback, the comments, and *Fill* for everyone |
  | ***Manage*** | Edit, plus *People* and *Settings* |

  Admins have every level. No group has them until an admin gives them. **Cookie's group needs *View*** for Cookie to read the plan (§8.4).

## 10. Talking to Cookie and Seraph

### 10.1 Rights: a new area *Cookie*

Cookie gets its own area in the groups-and-rights model of five point five (audit plan §5.2), which is being built now. Admins have every level, as always.

| Level | What it allows |
|---|---|
| **Use** | Cookie's window: the trace (within the person's other rights), the chat with Cookie and Seraph, rating decisions, **comments on cases** (§4.3) |
| **Instruct** | Use, plus **stopping a case and letting Cookie go on with it** (§4.3), **temporary instructions** (§10.3) and *Ask the big brother* (§15.2) |
| ***Manage*** | Instruct, plus *Admin → Cookie*: the configuration, *Start*, *Stop* (§9.3) |

**Everyone sees their own dashboard section** (§9.1). It needs no level.

### 10.2 Messages

- **A message wakes Cookie at once** and opens a *request* case.
- **When Cookie acts for someone, it has at most that person's rights,** and never more than its own group's.
  - Anyone with *Use* can ask about what they themselves can see.
  - **Asking Cookie to act needs the right for that action.** For example, a ticket needs Redmine *Link*, and messages to other people need *Instruct*.
- **Seraph explains itself:** ask it why it approved or rejected something. Seraph answers, and still can't act.
- **Messages are screened** like any other text (§13.1).
- **From MCP (package eight):** the tool for talking to the internal AI **sends a message to Cookie.**
  - It comes from the token's user, with that user's rights. **A read-only token can only ask questions**: no instructions, and no requests to act.
  - It is screened by Jev, as package eight plans, and shows in Cookie's window like any other message.
  - The tool returns Cookie's reply. If the round takes too long, it returns a conversation id so the client can ask again later.
  - **At most a few messages per token and hour** (in code, §3.5). **Instructions given over MCP are only drafts:** the person confirms them in PNMT's UI.

### 10.3 Temporary instructions: mute, away, suppress

**Most instructions are really maintenance windows, absences or suppressions.** As free text in a prompt, a model will sometimes ignore them. **So they become three kinds of object, and code enforces them:**

| Object | What it does | Example |
|---|---|---|
| **Mute** | No messages, tickets or big brother calls for a host, a sensor, a location or a case. Cases still open and are still shown | "Ignore host X this weekend" |
| ~~**Away**~~ | **Replaced in revision 7 by people's availability** (§8.2): set by the person or by *Availability: Edit*, and code sends what would go to them to whoever stands in | "Anna is away until Friday": her *VACA* days in the plan |
| **Suppress** | No messages of one kind (a category, a channel, a sensor type) | "No Pushover for toner warnings" |

- **Whoever has *Cookie: Instruct* can create them in the chat.** Cookie fills in the object, the person checks it and confirms. They can also be created on the admin page (§9.3).
- **Every object has an end date,** at most 30 days away (configurable). They are listed with who created them, and with *End now*. Creating and ending them goes into the audit log.
- **What doesn't fit the three kinds** stays as a free-text instruction with an end date. It goes into Cookie's and Seraph's prompts with the name of the person who gave it.
- **An instruction can narrow or steer, never widen.** It can't change goals, rules, action levels or limits, and it can't give Cookie rights. Those stay on the admin page.
- **A message from someone without *Instruct* is a request,** never an instruction.

---

## 11. The decision log, and learning from it

- **Every decision is logged:**
  - what was decided and why;
  - the evidence and the confidence;
  - **Seraph's verdict and its reason;**
  - the result.
- **Important decisions not to act are logged too** ("not alerting: known maintenance").
- **People see the decisions their rights allow,** and mark each one **Right** or **Wrong**, with a comment.
- **Learning:**
  - once a day, or after 10 new ratings, a summariser turns the ratings into **lessons**: short, concrete rules, for example "no Pushover for toner warnings";
  - at most about 30 lessons are active, and similar ones are merged;
  - lessons are versioned, and admins can see, edit and disable them (§9.3);
  - **a lesson that is a rule becomes a rule:** "no Pushover for toner warnings" becomes a *suppress* object (§10.3), which code enforces, and the admin is shown it;
  - **the other lessons go into every Cookie and Seraph prompt.**
- **Comments are untrusted text too:** they pass the privacy filter and the injection screening.

---

## 12. What changes in the existing AI (package one)

| | |
|---|---|
| **Removed** | **The situation analysis**, with its *AI analysis* system sensors, **in package nine:** the status replaces it. **The mail analysis** (unhandled mails turned into rule proposals, suspicious mails) **in package nine point five,** when Cookie can propose: unhandled mails then become an input to its cases. Until then, package one keeps both the mail analysis and the *AI every time* rules |
| **Handed to Cookie** | **Mail rules set to *AI every time*** (ai-operations plan §6.4).<ul><li>A matching mail becomes an input for Cookie. Cookie decides *ok*, *fail* or *unknown* and writes the event on the rule's sensor (§6), with Seraph in place of the verification model.</li><li>*Unknown*, a Jev hit or a rejection from Seraph means no event, and a decision in the inbox, as before.</li><li>**While Cookie is stopped or paused,** such mails stay unprocessed and visible in the mail log, as they do today without AI.</li><li>The rules themselves and their settings stay as they are. The rule form's *Verify with the second model* goes, because Seraph always reviews.</li></ul> |
| **Kept** | **The AI foundation:** provider, request log, budget, retries, Jev, `Untrusted`, scrubber. **The decisions inbox** (Cookie's *Propose* level uses it). **The knowledge base's AI tags. The RouterOS advisory rating.** The other mail rules |

---

## 13. Safety

### 13.1 Prompt injection

- **All outside text is data, never instructions:** tickets, mails, Zabbix names, knowledge base pages, comments, and chat messages too. Such text is wrapped and labelled, then **screened by Jev**.
- **Flagged text** is shown to Cookie and Seraph as suspicious. **Any action based on it needs a person.**
- **The power lives in code, not in prompts.** The executor enforces rights, the closed list of actions, id-only targets and the limits, whatever a model says.
- **Seraph reads the original evidence**, so a manipulated Cookie can't argue its way past it.
- **The configuration is out of reach** of every tool.

### 13.2 The privacy filter

**Three modes, set in `.env`:**

| Mode | What happens |
|---|---|
| `Off` | Text goes to the AI as it is (still without secrets and without pages marked as sensitive) |
| **`Code`** (default) | **Code finds people:** the names of PNMT's and Redmine's users, e-mail addresses, phone numbers |
| `LocalModel` | `Code`, **plus a local model** (any OpenAI-compatible URL on the LAN) that finds the names code doesn't know |

- **Only people are pseudonymised.** Host names and IP addresses stay as they are. Without them, Cookie and the big brother can't reason about subnets, gateways and which host is which, and that is what they are for. **The access watch is the exception** (cookie-access-plan §9): there code has already judged what matters about an address, so the logins' source addresses go as `ADDRESS_n`, and every account in a case as `PERSON_n`.
- **Each text is pseudonymised once, when it comes in** (a ticket, a mail, a note), and the result is kept. Rounds don't wait for the filter.
- **Before adding the local model, check whether EU endpoints with zero data retention (§14) and a data processing agreement already meet the requirement.** The local model is for when they don't, or when `Code` misses too much.

- **The local model only returns JSON:** a list of what it found, as `{ text, kind }`. **It never rewrites the text itself.**
- **Code then replaces each finding with a stable placeholder** (`PERSON_1`, `PERSON_2`). The same value gets the same placeholder throughout a case, so Cookie can still reason ("PERSON_1 hasn't replied").
- **The mapping stays in PNMT's database.** Answers are restored in code before anything is shown or run.
- **It fails closed:** in `LocalModel` mode, if the local model can't be reached, no remote call is made and Cookie is *Paused*.
- **The known limit:** a name the local model misses still goes out. Seraph's leak check is a second, imperfect net.

### 13.3 Configuration in `.env`

```
Cookie__Enabled=false            # available, but still stopped until an admin starts it
Cookie__Privacy=Code             # Off | Code | LocalModel (LocalModel needs the URL below)
Cookie__LocalModel__Url=
Cookie__LocalModel__Model=
Cookie__Model=                   # Cookie: needs good tool use (§14)
Cookie__SeraphModel=             # ideally another vendor than Cookie's (§14)
Cookie__BudgetUsd=
Redmine__WriteApiKey=            # separate from the read-only key (audit plan §8.7)
```

Everything else is set on the admin page (§9.3).

**The token limits** (`Cookie__Tokens__*`, deploy/.env.example): `max_tokens` per call, one setting each for Cookie's triage, work turn, case note, status, chat turn and instruction, the summary, the mail, the duty text, the lessons, the access watch's work turn and verdict, Seraph's review, chat, coffee and drift report, and the big brother's work turn and solution. The reasoning counts against the limit. 1.8.2 doubled them (work and chat 12000, Seraph 4000).

### 13.4 Rollout (package nine point five)

1. **Watch only, which is package nine:** status, summaries, chat, the trace. Cookie takes no actions, and code sends the reminders.
2. **Propose, the start of package nine point five:** every action goes through the decisions inbox. This builds up ratings and lessons.
3. **Auto, action by action,** as admins come to trust each one.

---

## 14. Models and providers (research, 2026-11-14)

**Short answer: yes. DeepSeek works well for Cookie. GLM works for Seraph, but it should be GLM-5.3, not GLM-5.3-FlashX.** The figures below are from OpenRouter's model and endpoint lists on 14 Nov 2026.

### 14.1 What the models offer

| | **Cookie:** `deepseek/deepseek-v4.1-flash-1017` | **Seraph today:** `z-ai/glm-5.3-flashx` | **Seraph proposed:** `z-ai/glm-5.3` |
|---|---|---|---|
| Tools, strict structured outputs, reasoning | Yes · yes · yes | Yes · yes · yes | Yes · yes · yes |
| `tool_choice` | `auto`, `required` or a named function at 45 of 49 endpoints. DeepSeek's own endpoint: `auto` only | **`auto` only** | All three at 29 of 48 endpoints |
| Providers, and how many offer zero data retention (ZDR) | 38 providers, 43 ZDR endpoints | **1 provider: Z.AI (Singapore)**, which offers ZDR | 37 providers, 42 ZDR endpoints |
| ZDR providers based in the EU | Mistral (FR), Nebius (NL) | None | Mistral (FR, including an EU endpoint), Nebius (NL) |
| EU in-region routing (OpenRouter) | No | No | Not checked |
| Price per 1M tokens, in / out, at an fp8 ZDR endpoint | $0.30 / $1.20, **cached input $0.006** | $0.37 / $1.25 | $1.40 / $4.40, cached input $0.14 to $0.26 |
| General capability (Vals Index) | **61.3 (#12)** | Not listed. FlashX is the fast variant of GLM-5.3-Flash: 50.9 | **59.9 (#15)** |
| Tool use | τ-bench ≈ 76–78 % (OpenRouter, per provider), Automation-Bench 60.5 | GLM-5.3-Flash: Toolathlon 78.4, AutomationBench 48.8 | Not checked |

### 14.2 Findings

1. **DeepSeek V4.1 Flash 1017 fits Cookie's job.** Cookie's job is reading through tools, judging, writing short texts and proposing in a fixed schema. The model handles tool use well, it is the strongest of the three, it has a 1M context (Cookie needs far less), and it is cheap. **Pin the dated snapshot** (`-1017`) rather than `~deepseek/deepseek-flash-latest`, so that Cookie's behaviour doesn't change overnight.
2. **GLM-5.3-FlashX is a poor choice for Seraph.**
   - **It is weaker than Cookie.** A reviewer weaker than the one it reviews catches little.
   - **It has only one provider.** Seraph fails closed (§5), so every Z.AI outage would also pause Cookie.
   - **It only accepts `tool_choice: auto`.**
   - **GLM-5.3** is about as strong as Cookie's model, comes from another vendor (which is what §5 asks for) and has many providers. Seraph reads little and writes short verdicts, so the higher price matters less. **If the budget is tight,** GLM-5.3-Flash at $0.15 / $0.50 is the fallback; it is better than FlashX because it has many providers.
3. **Neither model resists prompt injection.** In a November 2026 study with an adaptive, automated attacker, DeepSeek-V4.1-Flash and GLM-5.3 resisted only **about 1 %** of attacks ([arXiv 2611.11861](https://arxiv.org/html/2611.11861), Table 1). The study calls their resistance "remarkably low". **So §13.1 stays as it is, and is not optional:** rights, the closed list of actions, id-only targets and the limits are enforced in code. Jev screens, and **Seraph is a check on quality, not a security boundary.** The same injected text can mislead both models.
4. **Don't expect them to fix things by themselves.** On Terminal-Bench 4.0 (hard work in a shell, Vals AI, one harness for every model), DeepSeek V4.1 Flash 1017 solves 31.8 % and GLM-5.3 38.9 %. The best models solve 64–65 % ([Vals AI](https://www.vals.ai/benchmarks/terminal-bench-4), 11 Nov 2026). That fits package nine: Cookie triages, reminds, opens tickets and asks questions. **For Cookie's own answers on tickets, keep to acknowledging and asking for what is missing,** plus answers that rest on the knowledge base (§7.2). Hard cases go to the big brother (§15). *(Revision 1 cited Vals' SRE Bench here. That benchmark is reverse engineering of binaries, not operations work, so it doesn't apply.)*
5. **Privacy:** both have ZDR endpoints at EU providers (Mistral, Nebius), which is the best available while EU in-region routing isn't offered. **DeepSeek's own endpoint (China) is left out:** it isn't ZDR, has no structured outputs and no `required`.
6. **Quantisation varies by provider.** Many endpoints run fp4. For tool use, **allow only fp8 or better,** and pin a short list of providers.

### 14.3 What this means for the design

- **The tool loop only uses `tool_choice: auto`,** and **the proposal is a separate, final call with a strict schema and no tools.** This works on every endpoint, and the schema is enforced reliably.
- **Reasoning on, and passed back unchanged.** During a tool loop, the model's `reasoning_details` go back with every tool result, exactly as received (OpenRouter's rule). The same reasoning feeds the thinking window (§9.2), with placeholders restored.
- **Reasoning effort per step:** *low* for triage, *medium* for work, *medium* for Seraph.
- **One provider per round** (sticky routing), so behaviour doesn't change between turns and **the prompt cache works.** A stable prefix of identity, rules and lessons, then the changing brief. Cached input costs about 1/50 of the full price.
- **The `ChatClient` from package one only does single structured calls** (no tools, no reasoning, temperature 0). Cookie needs it extended: tool calls, reasoning passed back, provider settings per model, and streaming for the live trace.
- **Rough cost, for planning only.** The assumptions: 30 working rounds a day with 8 calls each, 100 triage calls, 60 Seraph reviews, about 70 % cached input. With that:
  - Cookie costs about **$30 a month**;
  - Seraph on GLM-5.3 costs about **$20 a month** (on GLM-5.3-Flash, about $3).
  - **Package one's 30 USD budget is too small for this.** Cookie gets a budget of its own, set after measuring during the *watch only* phase (§13.4).

**The models' settings in `.env`** (they replace the model lines of §13.3):

```
Cookie__Model=deepseek/deepseek-v4.1-flash-1017
Cookie__SeraphModel=z-ai/glm-5.3
Cookie__Providers=nebius,mistral,together      # in order, sticky per round
Cookie__SeraphProviders=mistral,together,fireworks
Cookie__Zdr=true                               # only zero-data-retention endpoints
Cookie__MinQuantization=fp8
Cookie__BigBrother__Primary__Model=moonshotai/kimi-k3.1-pro          # §15
Cookie__BigBrother__Primary__Providers=nebius,deepinfra,together    # natively 4-bit: no minimum quantisation
Cookie__BigBrother__Backup__Model=google/gemini-4-carbon            # proposed
Cookie__BigBrother__Backup__Providers=google-vertex/eu
Cookie__BigBrother__BudgetUsd=30                                    # shared by both
```

**To test before rollout:**

- a replay of a few weeks of real events and tickets, in *watch only* mode;
- **the language of the ticket answers** (in whatever language the tickets are in);
- the rate of schema failures and tool errors per provider;
- a set of planted injections in tickets and mails.

**Sources:**

- [OpenRouter model list and endpoints](https://openrouter.ai/api/v1/models), [OpenRouter ZDR endpoints](https://openrouter.ai/api/v1/endpoints/zdr), [reasoning tokens](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens);
- [Vals Index](https://www.vals.ai/benchmarks/vals_index), [Terminal-Bench 4.0 (Vals)](https://www.vals.ai/benchmarks/terminal-bench-4);
- [DeepSeek V4.1 Flash 1017](https://www.mindstudio.ai/blog/deepseek-v4-1-flash-1017-release), [GLM-5.3-Flash](https://www.datacamp.com/blog/glm-5-3-flash);
- [prompt injection with agent swarms](https://arxiv.org/html/2611.11861).

---

## 15. The big brother: two strong models for problems nobody fixes

### 15.1 Why

**Cookie's model is good at watching, sorting and writing, and weak at hard technical thinking.** On hard shell work it solves about half as much as the best models (§14.2). Such cases are rare, so **strong, expensive models are affordable, as long as they are only called when they are needed.**

**The big brother is two models: a *primary* and a *backup*.** Both are strong, from two different vendors (§15.6). **They advise. They never approve and never act.** Whatever they propose goes through Seraph and the executor (§5, §6), exactly as Cookie's proposals do. **They read with the same tools and Cookie's group's rights,** and get the same privacy filter.

### 15.2 When Cookie asks for a solution

**Cookie asks only when all three are true:**

| Condition | Who decides | What counts |
|---|---|---|
| **1. Something needs fixing** | **Cookie** judges, from the case | A fault, not just information. For example:<ul><li>a failing sensor or Zabbix problem;</li><li>a problem that keeps coming back (3 times in 7 days);</li><li>many things failing at once (3 or more hosts at a location, or 5 or more sensors within 10 minutes);</li><li>a ticket that describes a technical fault</li></ul> |
| **2. It matters** | **Code** | At least one of:<ul><li>status *Warning* or *Alert*;</li><li>importance *High*;</li><li>several hosts or people affected;</li><li>a ticket deadline of §7.2 already missed</li></ul> |
| **3. Nobody is taking care of it** | **Code** | **For 1 working day (§7.3):**<ul><li>no acknowledgement;</li><li>no note;</li><li>no linked ticket that is moving (a status change, a comment from staff);</li><li>no activity by the assignee</li></ul> |

- **Code checks conditions 2 and 3, and offers the case to Cookie.** Cookie confirms condition 1. Each call counts against the limits of §15.5.
- **A person can also ask:** an *Ask the big brother* button on a case in Cookie's window. It needs *Cookie: Instruct* (§10.1), because it costs money. The three conditions don't apply.
- **When Cookie and Seraph disagree, the big brother doesn't settle it.** Two rejections from Seraph still send the case to a person.

### 15.3 Which model answers

- **For each call, code picks one of the two at random**, half and half.
- **A second call on the same case goes to the other model.** So the case gets a second opinion, not the same answer twice.
- **If the chosen model fails** (error, timeout, provider down, a schema failure after one retry), **the other one answers.** If both fail, the case goes to a person with the note *the big brother couldn't be reached*.
- **Every solution names the model that wrote it.** The ratings in the decision log (§11) therefore also compare the two models over time, and the admin page shows the comparison.
- **Because either may answer, both must be strong.** The backup is not a cheaper fallback.

### 15.4 What it gets, and what it returns

- **What it gets:**
  - the case file: timeline, evidence, Cookie's notes, linked tickets;
  - what Cookie thinks needs fixing;
  - read-only tools;
  - **more room than Cookie:** about 60 tool calls, high reasoning effort.
- **What it returns, in a fixed schema: a solution.**
  - **The most likely cause,** with a confidence and the evidence for it, plus any alternatives.
  - **The fix: steps for a person,** with commands as text, which are never run.
  - **How to check that it worked,** and what to watch afterwards.
- **Where the solution goes,** always after Seraph:

| Situation | Where |
|---|---|
| **A ticket exists for the case** | **A private note on the ticket**, signed with Cookie's name and the model's, so the person working on it has the solution |
| **No ticket yet** | **A proposal in the decisions inbox:** *Create a ticket with this solution* · *Someone is handling it* · *Reject*, with a comment. The case's escalation group (§7.2) is told |

- **In both cases,** the solution also goes into the case, Cookie's window, and the summaries of the people concerned.

### 15.5 Limits and cost

- **Both models share one budget,** separate from Cookie's (30 USD a month by default), **and these limits:**
  - at most 10 calls a day;
  - at most 2 calls a day per case;
  - a cap on tokens and time per call.
- **When a limit is reached,** the case goes to a person with the note *would need the big brother*. **Nothing waits silently.**
- **A call costs about $0.30 to $2.50**, depending on the model and the size of the case; a call that uses all 60 tool calls is at the top of that range. **With the default budget that is about 15 to 40 calls a month.**
- **Everything is logged** (trace, decision log, AI request log) with the model that answered, and the dashboard shows the cost for each model.

### 15.6 Which models

- **Primary: `moonshotai/kimi-k3.1-pro`**, decided.
- **Backup: `google/gemini-4-carbon`**, proposed:
  - it comes from a fourth vendor;
  - it does almost as well on shell work (63.1 %);
  - **in general capability it is among the best (70.5)**, which makes up for Kimi's weak point;
  - it is cheaper per task than Claude ($7.45 against $10.75), and has an EU endpoint with zero data retention.
  - **Claude Sonnet 5.5 is an equally good choice.**

**The candidates.** Hard shell work is Terminal-Bench 4.0. General capability is the Vals Index. Both come from Vals, 11 Nov 2026, and both list the cost of one task. Prices are per 1M tokens at an endpoint with zero data retention (OpenRouter, 14 Nov 2026):

| Model | Shell work | Cost per task | General | Price in / out | EU endpoint with ZDR |
|---|---|---|---|---|---|
| **`moonshotai/kimi-k3.1-pro`** (primary) | **63.6 %** | **$4.57** | 62.5 | $2 / $10 | Nebius (NL) |
| `anthropic/claude-sonnet-5.5` | 64.1 % | $10.75 | 70.9 | $2.20 / $11 | Bedrock eu-west-1, Vertex europe |
| `anthropic/claude-opus-5.5` | 65.2 % | $12.04 | 70.1 | $4.40 / $22 | Bedrock eu-west-1, Vertex europe |
| **`google/gemini-4-carbon`** (backup, proposed) | 63.1 % | $7.45 | **70.5** | $2.20 / $11 | Vertex eu |
| `openai/gpt-6-astra` | 59.6 % | $9.58 | 65.0 | $10 / $50 | **None** (Azure global and US only) |
| `openai/gpt-6.1-sol` | 55.1 % | $1.72 | 63.9 | $2.20 / $11 | Azure eu |
| `openai/gpt-6.1-terra` | 49.5 % | $2.16 | 60.5 | $1.20 / $6 | Azure eu |
| *For comparison:* GLM-5.3 · DeepSeek V4.1 Flash 1017 | 38.9 % · 31.8 % | $9.37 · $0.54 | 59.9 · 61.3 | (§14.1) | |

**Why Kimi K3.1 Pro as the primary:**

- **It does about as well on shell work as the best models, at less than half their cost per task.** For the cases of §15.2 and for package ten, this is the score that counts.
- **The weights are open** (on Hugging Face since 2 Nov 2026), and 17 providers serve it with zero data retention.
- **Its reasoning comes back as plain text,** so the thinking window (§9.2) shows all of it. Claude and GPT only return a summary.
- **It comes from a third vendor,** separate from Cookie's (DeepSeek) and Seraph's (Z.AI).

**What to know:**

- **In general capability it is only slightly ahead of Cookie** (62.5 against 61.3). Its edge is in hard technical work, which is what it is called for. Its prompts should ask for that kind of work (diagnosis, root cause, steps), not for judgement calls that Cookie already makes.
- **Its native format is 4-bit.** It was trained in MXFP4, and there is no full-precision checkpoint. **So `Cookie__MinQuantization=fp8` does not apply to it:** fp4 and mxfp4 endpoints serve the original weights.
  - **The EU endpoint at Nebius (NL) can be used.** It is labelled *fp4*, so confirm with Nebius that it serves the original MXFP4 checkpoint and not a re-quantisation.
  - Community quantisations below 4 bits lose quality. Leave them out.
- **Its predecessor Kimi K3 did poorly against prompt injection** (arXiv 2611.11861, §14.2), and K3.1 Pro has not been tested. **Nothing changes:** the big brother advises only, and the boundary stays in code.
- **The OpenAI models:** they fit the GPT models' usual profile, very economical with tokens. GPT-6.1 Sol solves 55 % of the shell tasks at $1.72 a task. GPT-6 Astra is weaker than Kimi on shell work, costs five times as much per token, and has no EU endpoint, so it is out for the big brother. **GPT-6.1 Sol is still worth considering as Seraph:** it is stronger in general than Cookie's model (63.9 against 61.3) and much more economical than GLM-5.3 ($3.61 against $8.52 per task in the Vals Index), with an EU endpoint. That is not decided; Seraph stays on GLM-5.3 (§14).

### 15.7 Fixing things: package ten

**In package nine, no AI fixes anything.** Cookie only adds, and the big brother only advises. **Fixing happens in package ten**, when a person grants temporary access to a server. **The flash models aren't good enough for that work.** So:

- **the big brother does the console work,** on the primary. **The backup takes over if the primary fails.** A session doesn't switch models halfway through;
- **Cookie** keeps the case and the conversation;
- **every command is reviewed first:** rules in code, then Seraph, and the person who granted access can watch.

**A console session costs more:** about $2 to $8, judging by Vals' cost per task ($4.57 for Kimi, $7.45 for Gemini). Package ten's plan decides the details.

---

## 16. The review's answers (2026-11-14)

| # | Question | Answer | Where |
|---|---|---|---|
| 1 | The 4 hours: working hours or around the clock? | **Working hours, set for each day of the week, plus a list of holidays (dates) with their own hours** | §7.2, §7.3, §9.3 |
| 2 | After a restart: resume, or always *Stopped*? | **Resume.** *Stopped* only on a fresh installation, or the first time `Cookie__Enabled` is set | §9.3 |
| 3 | Mail rules set to *AI every time*: keep them, or hand them to Cookie? | **Hand them to Cookie** | §6, §12 |
| 4 | Temporary instructions in the chat? | **Allowed for whoever has the right to instruct Cookie** (*Cookie: Instruct*, in the new groups and rights; admins always) | §10.1, §10.3 |
| 5 | Package eight's "talk to the internal AI" tool: a message to Cookie? | **Yes** | §10.2 |
| 6 | Team leads for escalations? | **A PNMT group, chosen for each watched project** | §7.2, §9.3 |
| 7 | Which model is the big brother, for hard cases and for package ten's console work? | **Kimi K3.1 Pro** (primary) | §15.6, §15.7 |
| 8 | One big brother model, or two? | **Two, a primary and a backup.** Each call picks one at random, and the other takes over if it fails. Cookie asks when something needs fixing, it matters, and nobody is taking care of it. The solution goes into the decisions inbox, or into the ticket. The backup is proposed as Gemini 4 Carbon | §15.1–§15.4, §15.6 |

---

## 17. Critical review (2026-11-14)

**The answers are in §18.** **Two reads:** my own, and an independent second read that hadn't seen my conclusions. Both agree on the main points. §17.16 and §17.17 come only from the second read, and it added to several others.

**The core is sound:**

- code keeps the books and the AI judges (§3.3);
- Cookie only adds, from a closed list of actions, with targets as ids;
- the power lives in code, not in prompts;
- the rollout goes from watching to proposing to acting.

**What will not work is mostly around it, and the plan doesn't keep to its own principle.**

- **Fixed duties still run through a chain of AI services that all fail closed:** deadlines, routing, escalations and the status.
- **It relies on ownership data PNMT doesn't have.**
- **It will flood people on the first day,** and **it duplicates alerts.**
- **Public ticket answers are a path for leaking data.**
- **Nothing shows whether Cookie helps.**

**The biggest risk is trust.** Cookie could become a second, noisier alerting system that alerts the wrong people twice and opens tickets for known problems. A small team would mute it within weeks, and then it never gets the ratings it needs to improve. **Most of the findings shrink once these three things are done:**

- add owners, acknowledgements and maintenance windows to PNMT (§17.2);
- move the fixed duties into code (§17.1, §17.6);
- cut a first version, with measured baselines (§17.15).

### 17.1 Too much at once, and no smallest useful version

- **What won't work:** package nine bundles a great deal:
  - 19 requirements;
  - an agent loop, a reviewer, two advisor models, a local privacy model and Jev;
  - a case book, a live trace, and a chat with two AIs;
  - learning, instructions, MCP;
  - writing to Redmine, a working-hours calendar, and the mail-rule handover.

  That's six models from six vendors, for a handful of users on one installation. It would take months before any of it helps, and every part has to be kept working (snapshots retired, providers changing).
- **Adjustment:** build in steps that each help on their own:
  - **A: no AI at all.** The case book, deadlines, reminders, escalations, the ticket after 2 days (from a template) and the status, all in code. **Most of R9 and R10 is deterministic and needs no model.**
  - **B: Cookie watches.** Summaries, the explanation of the status, the chat, the trace, and the shadow mode of §17.17.
  - **C: actions,** with Seraph.
  - **D: the rest.** The big brother, learning, instructions.

  **Each step has to improve the measures of §17.15.**

### 17.2 PNMT doesn't know who is responsible, who is on duty, or whether anyone is working on a problem

- **What won't work:** §4.4, §8 and §15.2 rely on the people responsible for hosts and locations, on acknowledgements, and on known maintenance (§11). **PNMT has none of these.**
  - Hosts and locations have no owner.
  - A bad sensor can't be acknowledged or snoozed, and there are no maintenance windows.
  - **Pushover keys are stored on notification rules** (`pushover://USERKEY`), not on users.
  - Nobody is marked as on duty.
  - The interests people set themselves will mostly stay empty.

  So **routing becomes the model's guess**, and **"nobody is taking care" is true of every red sensor.**
- **Adjustment:** these become prerequisites, in this package:
  - **an owner** (a user or a group) per location and per host. A host takes its location's;
  - **I'm on it** (an acknowledgement) with an end time on a case: on the dashboard, in Cookie's window, and as a link in Pushover and e-mail;
  - **maintenance windows**, or a snooze, for a host, a sensor or a case;
  - **contact settings on the user,** with the Pushover key, and **an on-duty rota** (a group plus a weekly plan is enough).

  **Code does the routing. The AI only ranks what matters most.**

### 17.3 On the first start, and every day after, Cookie floods Redmine

- **What won't work:**
  - **Every installation has sensors that have been red for weeks,** old Zabbix problems, and tickets past 14 days. **Two days after the start, every one of them is "unresolved, and nobody is taking care":** a ticket, a big brother call and a reminder for each.
  - **A flapping sensor** closes its case and opens a new one (§4.3). That restarts the 2-day clock and gets around "never while an open ticket is linked" (§6).
  - **The tickets arrive unassigned,** and Cookie can't close them.
- **Adjustment:**
  - **a baseline at the first start:** whatever is already open counts as *known*. It gets no tickets, and is listed once for a person to sort out;
  - **a *known issue* state** with an end date;
  - **a case stays open until the sensor has been OK for X hours;**
  - **one ticket per sensor or host:** Cookie reuses its own ticket if it is still open or was closed recently, across cases;
  - **the ticket is assigned from the owner** (§17.2);
  - **small daily caps on the total** (tickets, reminders, calls). Whatever is over the cap goes into one digest, not into single messages.

### 17.4 Cookie duplicates the notification rules, and holds back the night-time alert

- **What won't work:**
  - Notification rules already push a bad sensor to Pushover. **Cookie's push for the same sensor comes again, minutes later, in different words.** More alerts and less trust: the quickest way to get Cookie muted.
  - **Quiet hours also apply to "very urgent" Pushover** (§6, §8). The outage at night is exactly the one that gets delayed.
- **Adjustment:** **Cookie is an escalation layer, not a second alerting system.** It alerts in two cases:
  - **no rule covers** the sensor;
  - **a rule fired and nobody reacted** (no *I'm on it*) within X minutes.

  **Code suppresses Cookie's message** if a rule has recently reached that person about the same case. **"Very urgent" goes to whoever is on duty, through quiet hours.**

### 17.5 The overall status shouldn't come from the model

- **What won't work:** *All fine · Warning · Alert* written by a model will flicker, and can say "All fine" while a High host is down on the same screen.
- **Adjustment:** **code computes the state** from the sensors, the cases and the deadlines. Cookie writes only the one line that explains it, and the summaries. This is §3.3 applied consistently.

### 17.6 When Cookie stops, its duties stop, and nobody notices

- **What won't work:**
  - Reminders, the 2-day ticket, escalations, the *AI every time* mails and the status all go through triage, proposal, Seraph and the executor. **If OpenRouter, Seraph's providers, Jev, the local model or the budget fails, Cookie is *Paused*,** and all of them stop silently (§4.1, §5, §13.2).
  - **After the update, Cookie starts *Stopped*.** So §12 switches off every *AI every time* rule on the day of the update, without anyone noticing.
- **Adjustment:**
  - **the fixed duties run in code** (§17.1 A), with template texts, whether Cookie is running or not. Cookie only adds wording, context and correlation;
  - **a system sensor *Cookie*** (as in package one) that goes bad when Cookie has been paused or stopped longer than N minutes, so the normal notification rules tell the admin;
  - **package one's mail path stays** until Cookie has proven itself, and after that it is the fallback while Cookie is paused.

### 17.7 Seraph on everything costs a lot where nothing is at risk, and stops Cookie when it fails

- **What won't work:**
  - Seraph is about as strong as Cookie, and by §14.2 it is no security boundary. A reviewer of the same strength tends to approve.
  - **Reviewing every summary, every internal note and every chat reply** doubles the calls and the delay for little gain. Summaries written on every change, for every person, multiply that again.
  - **Because Seraph fails closed,** a provider outage becomes a Cookie outage.
  - **And "add only" doesn't protect people:** a Pushover at 03:00 or a wrong public answer can't be taken back.
- **Adjustment:**
  - **Seraph reviews what leaves PNMT or reaches people:** Pushover, e-mail, Redmine, events. It takes **a sample of the rest** (say 1 summary in 10) for its daily drift report.
  - **If Seraph is down,** internal actions continue, and outbound ones wait at *Propose*.
  - **Summaries are written when someone opens the dashboard,** and cached for 15 minutes.
  - **If Seraph is really meant to catch mistakes, it should be stronger than Cookie,** for example GPT-6.1 Sol (§15.6).

### 17.8 Instructions and lessons in free text are only suggestions to a model

- **What won't work:** "ignore host X this weekend" and "Anna is away" are really maintenance windows and absences, and "no Pushover for toner warnings" is a suppression rule. In the prompt (§10.3, §11), a flash model will sometimes not follow them. And **"narrow, never widen" can't be enforced in a prompt:** narrowing is suppressing.
- **Adjustment:** they become **objects that code enforces:**
  - **mute:** a host, sensor or case, until a date;
  - **away:** a person, delegated to someone or a group, until a date;
  - **suppress:** a category or a channel.

  **The chat only fills in the form,** and the person confirms it. Only what doesn't fit stays as prompt text. **A lesson that is a rule becomes a rule.**

### 17.9 The learning loop will starve, and can be poisoned

- **What won't work:**
  - a handful of people will seldom rate decisions, and mostly the bad outcomes. So "after 10 ratings" means lessons drawn from anecdotes;
  - a *Wrong* without a comment teaches nothing;
  - **lessons go into both Cookie's and Seraph's prompts,** so the two share the same blind spot;
  - **a rating comment becomes a rule in every prompt.** That gives anyone with *Use* a lasting path for injection.
- **Adjustment:**
  - **one-click ratings where the result lands** (*useful / not useful*): in the Pushover message or e-mail, the ticket note, the dashboard line;
  - **signals that need no rating:** was the alert acknowledged, was Cookie's ticket rejected or closed as a duplicate, was the solution used;
  - **a lesson is a proposal, and goes live only after someone with *Manage* approves it.** Lessons that suppress alerts get an end date;
  - **Seraph gets no lessons,** or only its own, so the two keep separate views.

### 17.10 Rights leak through the text Cookie writes

- **What won't work:** the trace's reasoning and tool results (§9.2), case notes (§4.3), lessons (§11) and summaries (§8) are free text, drawn from everything Cookie's group can read. **Code can filter rows by area, but not a sentence that paraphrases a mail.** Restoring the placeholders puts real names into it on top of that.
- **Adjustment:**
  - **the full trace,** with its reasoning, **only for people whose rights cover Cookie's group** (or *Cookie: Manage*);
  - **every note, summary and lesson is tagged with the areas its sources came from.** It is shown only to readers who have all of those areas;
  - **the trace per case,** so that everyone else sees the actions, proposals and verdicts of the cases they may see.

### 17.11 The privacy filter will pause Cookie or blind it

- **What won't work:**
  - **The example in §13.3 sets `Cookie__Privacy=LocalModel` with no URL.** Cookie fails closed and stays *Paused* (§13.2).
  - Few small teams run a language model on their LAN, and running it on every tool result slows every round.
  - **Replacing IP addresses and host names removes the reasoning about subnets and gateways** that Cookie, and above all the big brother, exist for.
- **Adjustment:**
  - **`Code` by default, and pseudonymise people only.** Host names and IP addresses stay, or private addresses are kept, or are replaced in a way that keeps the structure;
  - **pseudonymise each text once, when it comes in,** and keep the result. Not on every call;
  - **first check whether the real requirement is already met** by EU endpoints with zero data retention and a data processing agreement. The local model comes later, if the misses justify it.

### 17.12 Redmine doesn't enforce "add only"

- **What won't work:** with an ordinary API key, Redmine lets Cookie do whatever its user may do. "Add only" exists only in PNMT's code.
- **Adjustment:** **a Redmine user *Cookie***, whose role in the watched projects only allows creating issues and adding notes (private notes included), nothing else. **Redmine then enforces "add only" as well.**

### 17.13 The ticket rules don't match how Redmine is used

- **What won't work:**
  - **Redmine doesn't know who is staff and who is the requester,** and has no notion of a first answer.
  - **Statuses like *Feedback* or *On hold* are waiting states.** In them, the 14 days shouldn't run.
  - **New tickets are usually unassigned,** so a reminder to "the assignee" (§7.2) reaches nobody.
  - **Cookie's acknowledgement at 4 hours makes the rule look met** while staff still haven't answered. And at *Propose*, it waits in the inbox of the very people who missed the deadline.
  - **Private notes trigger Redmine's own mails too.**
- **Adjustment:**
  - **for each project, set which Redmine roles count as staff,** and **which statuses stop the clock**;
  - **unassigned tickets escalate to the project's group;**
  - **Cookie's acknowledgement still counts as a missed deadline,** and the reports count the two separately;
  - **check Redmine's mail settings** for Cookie's user, and its notes.

### 17.14 The two big brothers give no comparison, and the cost is set too low

- **What won't work:**
  - **The random choice won't tell the models apart.** At one or two calls a day, few of them rated, it takes months before each model has enough rated solutions (about 30 to 50). Meanwhile similar cases get solutions of different quality, and the integration and testing are done twice.
  - **The cost in §15.5 was set too low** (corrected now). A call with up to 60 tool calls and high reasoning costs **$0.30 to $2.50** (Kimi), so 30 USD buys about 15 to 40 calls a month, not one or two a day.
  - **"Nobody is taking care" is an attention problem.** More text in an inbox nobody reads doesn't solve it. §17.2 and §17.4 do.
- **Adjustment:**
  - **one model, the primary, with the backup only for failover.** If a comparison is wanted, **both answer side by side** for *Alert* cases or when a person asks;
  - **start with the button only** (a person asks), and add the automatic trigger once the real cost per call has been measured.

### 17.15 Nothing shows whether Cookie helps

- **What won't work:** the plan doesn't say what success is. Nobody will be able to tell whether Cookie is worth its cost and noise, or whether a new model or prompt made it worse.
- **Adjustment:**
  - **measure from the *watch only* phase on:**
    - how often the first-answer rule is met;
    - the time until a problem is acknowledged;
    - problems unattended for more than a day;
    - alerts per person per week, and the share rated useful;
    - **the cost per useful action.**
  - **compare before and after each step;**
  - **keep a replay set of real past cases,** and run it on every change of model or prompt, as a regression test.

### 17.16 Public ticket answers are a path for leaking data

- **What won't work:**
  - **The requester writes the ticket text,** and Cookie reads it with rights that also cover mail, Zabbix, the knowledge base and other tickets. **Its reply goes back to that same requester** (§7.2).
  - **Id-only targets (§6) don't help here:** the reply itself is the channel. Both models resist only about 1 % of attacks, and Seraph is no security boundary (§14.2).
  - **Restoring the placeholders (§4.2, step 7)** puts real names and addresses back into the reply.
- **Adjustment:**
  - **public answers are written in a narrow context:** only the ticket itself, plus knowledge base pages marked as *safe to quote*. No other tools, and nothing from the case book;
  - **public answers stay at *Propose* permanently,** or are limited to templates ("received, we need the following: …").

### 17.17 The *Propose* phase floods the inbox, and *Propose* makes no sense for urgent actions

- **What won't work:** in §13.4 every action goes through the decisions inbox, and a small team won't work through that in real time. **A Pushover waiting for approval is no longer urgent,** and "acts at once" (§7.1) becomes "waits".
- **Adjustment:**
  - **a shadow mode instead:** Cookie logs what it *would* have done, and people rate a daily digest of it;
  - **urgent channels go from shadow mode straight to *Auto*,** with tight caps;
  - *Propose* stays for what isn't urgent and goes outside PNMT (tickets, public answers).

---

## 18. The review's answers (2026-11-14)

**Only what is marked as adopted went into the plan.** Everything else stays as it was. **§17 is kept as the record** of what was found.

| # | Finding | Answer | Where |
|---|---|---|---|
| 1 | §17.1: build in steps | **Adopted, as two packages:** nine (A and B) and nine point five (C and D). There are no measures (see 15) | §3.4 |
| 2 | §17.2: owners, *I'm on it*, maintenance windows, contact settings, on duty | **Changed:** no owners, acknowledgements, maintenance windows or rota in PNMT. **Who gets what is set on the admin page,** next to the working hours: recipients by scope, urgency and time, with their contact details.<br>**Revision 7: a rota after all,** the availability plan (§8.2), and the contact details on the user (§8.1) | §8, §9.3, §9.4 |
| 3 | §17.3: flooding | **Partly adopted: output limits in code** (§6.1). No baseline, *known issue* state or ticket reuse | §6.1 |
| 4 | §17.4: escalation layer only | Not adopted | |
| 5 | §17.5: status from code | Not adopted as such. But step A computes a status in code, so Cookie's status replaces it while Cookie runs, and code's is shown when it doesn't | §3.4, §8 |
| 6 | §17.6: duties in code, a system sensor, package one's mail path kept | Not adopted. Step A puts the fixed duties into code anyway, and package one keeps its mail path until nine point five | §3.4, §12 |
| 7 | §17.7: Seraph only on what leaves PNMT or reaches people | **Adopted:**<ul><li>R7 narrowed, with a sample of the rest;</li><li>internal work continues while Seraph is down;</li><li>summaries written when someone opens the dashboard;</li><li>whether Seraph needs a stronger model is decided after *watch only*</li></ul> | §2, §4, §5, §8 |
| 8 | §17.8: mute, away, suppress | **Adopted:** objects that code enforces, which the chat fills in. A lesson that is a rule becomes a rule | §10.3, §11 |
| 9 | §17.9: the learning loop | Not adopted | |
| 10 | §17.10: rights on the text Cookie writes | **Adopted:** the full trace only for those who may see everything Cookie's group sees, notes and summaries tagged with their source areas, the trace per case | §9.2 |
| 11 | §17.11: privacy | **Adopted:**<ul><li>`Code` by default;</li><li>only people are pseudonymised, host names and IP addresses stay;</li><li>each text is pseudonymised once, when it comes in;</li><li>check zero data retention plus a data processing agreement before adding the local model</li></ul> | §13.2, §13.3 |
| 12 | §17.12: a Redmine user with an add-only role | **No** | |
| 13 | §17.13: the ticket rules and Redmine | **Adopted, with a change:**<ul><li>staff roles and the statuses that stop the clock, per project;</li><li>unassigned tickets go to the escalation group;</li><li>Cookie's acknowledgement counts as a miss in the reports, **and from it the update clock starts** (14 days, or the project's time)</li></ul> | §7.2, §9.3 |
| 14 | §17.14: one big brother, not random | Not adopted: the random choice stays. The cost in §15.5 is corrected | §15.5 |
| 15 | §17.15: measures and a replay set | Not adopted | |
| 16 | §17.16: public answers in a narrow context | Not adopted | |
| 17 | §17.17: shadow mode | Not adopted | |
| 18 | §17.7: may Cookie correct its own notes? | Not adopted: Cookie only adds | |

**Added with these answers: the admin page is where Cookie gets adjusted.** It holds most of what the questions asked, plus an instruction for each kind of case, and all of it ships with default prompts (§9.3, docs/cookie-prompts.md).

---

## 19. Building package nine (A and B)

**Decided on 2026-11-14: build A and B now.** This section says how. Package nine point five gets a section of its own when it is built.

### 19.1 What A and B mean in the code

- **Code duties are actions too, and start switched off.** A fresh installation sends nothing: on *Admin → Cookie → Actions*, each duty (reminders, escalations, the ticket after 2 days, the closing note) is *Off* or *On*, and they are *Off* until an admin turns them on. **The case book and the code's status run whenever `Cookie__Enabled=true`,** whether Cookie (the AI) is started or not.
- **The AI in package nine only writes inside PNMT:** the status line, case notes, summaries, chat replies and the trace. **No AI text leaves PNMT,** so Seraph isn't needed yet (§5: Seraph reviews what leaves PNMT).
- **A problem case is per host.** The bad sensors of a host and the Zabbix problems of its Zabbix host are its members. It closes when no member is bad. **A ticket case is per Redmine ticket** in a watched project. A ticket linked to a host (§6.5 of the audit plan) shows in that host's problem case and counts as its progress; the ticket's own clock stays on the ticket case.
- **A first answer** is a public note or a status change by staff. **An update** is the same, later. Staff are the members of the project with one of the roles set on the admin page; **with no roles set, everyone but the ticket's author counts as staff.** A ticket written by staff has no first-answer deadline.
- **Who is told** (as built; **revision 7 changes it,** §8.3, §20):
  - **a ticket reminder** goes to the assignee, found by the *Redmine name* in the assignee's contact (§9.3, *Notifications*), and **to the project's escalation group when nobody is assigned** or no contact matches;
  - **a missed deadline** goes to the assignee and the escalation group;
  - **the recipient rules** of §8 add whoever they name for that scope, urgency and time, by their channels;
  - **channels for the people a duty names:** e-mail when the contact has an address; Pushover only for *urgent* (a missed deadline), when the contact has a key and isn't in quiet hours.
- **Every message goes through the outbox:** it counts the limits (§6.1), records what was sent or held back, and lets a person send what was held back.
- **The ticket after 2 days** goes into the project set under *Redmine → Cookie's tickets*, is linked to the host, and carries the case's timeline. It needs `Redmine__WriteApiKey`; **without it, the duty is shown as not possible**, and nothing else changes.
- **Privacy in `Code` mode:** PNMT's users' names and usernames, the Redmine names Cookie has seen, e-mail addresses and phone numbers become `PERSON_n`, `EMAIL_n` and `PHONE_n`. **The mapping is one table, stable across cases,** and code restores it before anything is shown.
- **The tools are the read tools of §3.5,** in `PNMTD/Cookie/Tools/`, each with its area and level. Cookie calls them with its group's rights; in a chat, with the asker's rights as well (the lower of the two).
- **Cookie's budget is its own** (`Cookie__BudgetUsd`, 30 USD a month by default), counted from the AI request log's `cookie.*` purposes. Package one's budget no longer counts them.
- **The trace is polled, not streamed:** the window asks every 2 seconds while a round runs. Streaming can come later without changing the trace's rows.

### 19.2 Data model

| Table | What it holds |
|---|---|
| `cookie_settings` | **Every version of every section of *Admin → Cookie*:** section, version, JSON, who, when. The newest version is in force; a rollback saves an old version as a new one |
| `cookie_runtime` | One row: started or stopped (and by whom), Cookie's state and why, the last and next round, the code's status and Cookie's status, each with its reason and cases |
| `cookie_cases` | The case book (§4.3): kind (*problem*, *ticket*, *request*), open or closed, title, host, location, ticket, importance, opened, last change, last progress, the ticket's clock (first answer, last update, assignee, status), Cookie's latest note and urgency, the source areas |
| `cookie_case_entries` | A case's timeline: what happened, when, by whom (code, Cookie, a person) |
| `cookie_duties` | **One row per deadline and step** (*first answer: reminder*, *update: missed since …*, *problem: ticket*), with when it is due and what came of it. Its key makes each duty happen once |
| `cookie_outbox` | Every message, ticket and note that went out or was held back: channel, recipient, text, state, by code or Cookie |
| `cookie_pseudonyms` | The privacy filter's mapping (§13.2) |
| `cookie_rounds`, `cookie_trace` | Rounds, and each round's steps: triage, tool calls, reasoning, notes, the status, errors, with the case and the source areas |
| `cookie_messages` | The chat: a person's messages and Cookie's replies, one thread per person |

### 19.3 Server

| Piece | Job |
|---|---|
| `CookieSettings` | The sections, their defaults (the prompts of docs/cookie-prompts.md included), versions, rollback, the ceilings of §6.1 |
| `WorkingCalendar` | Working hours and holidays (§7.3): adds working time, finds the next working period |
| `CaseBook` | Every minute: sensors, Zabbix problems, watched tickets (every 5 minutes, with their journals when they changed); opens, updates and closes cases; progress; the duties that are due; the code's status |
| `Duties` and `Outbox` | Turn due duties into messages, notes and tickets from templates; limits; recipients; send by e-mail, Pushover and Redmine |
| `RedmineWriter` | Creating a ticket and adding a note, with `Redmine__WriteApiKey`; audited with `Actor = Cookie` |
| `CookieLoop` | The states of §4.1, the wake-ups, grouping triggers for 30 seconds, one round at a time, resume after a restart |
| `CookieRound` | Brief, triage, work with tools, notes, the status (§4.2, steps 1 to 5, 8 and 9; package nine has no steps 6 and 7) |
| `CookieModel` | The chat client with tools, reasoning passed back, provider order, zero data retention and the minimum quantisation (§14.3) |
| `CookieTools` | The read tools (§3.5), with rights, `MachineAccess`, untrusted text wrapped, pseudonymised |
| `PrivacyFilter` | `Off` or `Code` (§13.2). `LocalModel` comes with nine point five, if it is needed |
| `Summaries` and `Chat` | The personal summary on request, cached for 15 minutes; messages and replies |

### 19.4 API

All under `/api/v1/cookie`. **The dashboard section needs no right; the window needs *Cookie: Use*; everything under `admin` needs *Cookie: Manage*.**

| Endpoint | What |
|---|---|
| `GET status` | The overall state with its reason and cases (Cookie's, or the code's), and Cookie's own state |
| `GET summary` | The reader's personal summary |
| `GET cases`, `GET cases/{id}` | The cases the reader may see, with timeline and duties |
| `GET trace` | The trace, by the rights of §9.2 |
| `GET messages`, `POST messages` | The reader's chat |
| `GET admin/overview` | State, last rounds, budget, limits hit, the outbox |
| `POST admin/start`, `admin/stop`, `admin/run` | Start, stop, run now |
| `GET admin/settings/{section}`, `PUT …`, `GET …/versions`, `POST …/rollback/{version}` | The sections of §9.3 |
| `GET admin/prompts/preview` | A prompt as the model receives it |
| `POST admin/outbox/{id}/send` | Send what was held back |

### 19.5 Web UI

- **The dashboard:** the Cookie card, with the overall state and its reason, Cookie's own state, your summary, and a hint when something waits for you.
- **Cookie's window** (`/cookie`): the cases, the trace, and the chat.
- ***Admin → Cookie*** (`/admin/cookie`): the tabs of §9.3 that package nine needs. *Mute, away, suppress* and *Lessons* come with nine point five.

### 19.6 Phases

| Phase | Content |
|---|---|
| **A1** | **The case book and the clock:** the right *Cookie*, the settings with versions, the calendar, the case book, the code's status, its API, tests |
| **A2** | **The duties:** outbox, limits, recipients and contacts, e-mail and Pushover, `RedmineWriter`, the reminders, escalations, the ticket after 2 days and the closing note, tests |
| **A3** | **The web UI for A:** the dashboard card, the cases, *Admin → Cookie* (Overview, Working hours, Notifications, Redmine, Actions, Rights) |
| **B1** | **The loop:** the model client with tools, the read tools, the privacy filter, the prompts, the loop and its rounds, the trace, Cookie's budget, tests with a fake model |
| **B2** | **Summaries and chat,** and the trace's rights |
| **B3** | **The web UI for B:** Cookie's window (trace and chat), the summary, and the tabs Identity, Cases and Models |
| **B4** | **The finish:** package one's situation analysis removed (§12), the seed, the documentation, release notes, this plan's *As built* |

- **Each phase:** one or more commits on master, the tests of its part green before the next, and an *As built* entry.

### 19.7 Testing

**Few tests, where they protect something:**

- **the calendar:** Friday 16:00 plus 4 working hours is Monday 11:00; a holiday's hours replace the weekday's; a reminder outside hours waits for the next period;
- **the case book:** a host's sensors are one case; it closes when all are OK; a Zabbix acknowledgement, a sensor note and a moving linked ticket are progress;
- **the ticket clock:** a staff note answers, the author's doesn't; a status that stops the clock stops it; a new update resets it;
- **the duties:** each happens once; nothing is sent while a duty is *Off*; the limits hold back the rest, and a person can send it;
- **the privacy filter:** names, addresses and phone numbers out, and back;
- **the tools:** a tool outside Cookie's group's rights refuses; a sensitive page never comes back;
- **the loop,** with a fake model: a quiet round makes no call; a bad sensor wakes it; it resumes after a restart; the budget pauses it;
- **the endpoints** in the access matrix.

### 19.8 As built (2026-11-14)

**Package nine is built, on master:**

| Commit | Phase |
|---|---|
| c7b6da5 | A1: the case book and the clock |
| 6920418 | A2: the duties and the outbox |
| 9a9a28e | A3: the web UI for the bookkeeping |
| 4ef9f4e | B1: the loop, the model client, the read tools, the privacy filter, the trace |
| 3dc05e2 | B2: the personal summary and the chat |
| 254fd5d | B3 and B4: the trace and the chat in the web UI, and the situation analysis removed |
| d5c2c96, 4559e89, 9e6b154, a1a74bc | the seed and the operators' documentation, and fixes from review |

**Revision 7 changes some of this** (§20): the contacts, the recipient rules, quiet hours, and the *Working hours* and *Notifications* tabs.

**Release notes:** [docs/release-notes-1.5.md](release-notes-1.5.md), written for 1.5.0. Nothing is pushed or tagged, and the version isn't bumped. Where the code differs from the sections above, this section says so; the rest is as planned.

**The bookkeeping (A):**

- **A problem case is per host, a ticket case per ticket** (§19.1). A ticket linked to the host, or to one of its bad sensors, counts as the problem's progress when PNMT's copy shows it moving (refreshed every `Redmine__SyncMinutes`), and an open one keeps the ticket duty away.
- **Progress** is also a Zabbix acknowledgement and **a change of the host's or a member sensor's notes**. PNMT's notes are one text per object, without a time, so the case book keeps a fingerprint of each and counts a change.
- **Zabbix problems** join their attached PNMT host's case. A problem on a Zabbix host attached to no PNMT host gets a case of its own (`zabbix:{hostid}`). The severity from which they count is on *Cases* (`Average`).
- **A ticket's clock:** the first answer and an update are a public note or a status change by staff. Watched projects' open tickets are read every `Cookie__TicketsMinutes` (5), and a ticket's journals only when Redmine's `updated_on` moved. **An unreachable Redmine closes no ticket case.**
- **The duties:**
  - **Each is off until switched on,** and a duty that falls due while off is closed as *off*, so switching it on later sends nothing old.
  - **A reminder whose deadline is due as well gives way to it** (after downtime, or when both were switched on together).
  - **Reminders and escalations wait for working hours;** tickets and closing notes don't.
  - **A missed first answer sends messages only;** the reminder before it is the private note.
  - **Who:** the assignee by the contact's *Redmine name*, the project's escalation group when nobody is assigned or no contact matches, both for a missed deadline, plus whoever the recipient rules name.
  - **Pushover** only for what is urgent, and never in a person's quiet hours (e-mail still goes).
- **The recipient rules' location scope is the location itself,** not its sub-locations.
- **The outbox** counts §6.1's limits per local day. What is held back stays held back; an admin sends it from the Overview, without limits, and the audit names them. **Redmine is written only with `Redmine__WriteApiKey`, and only in the projects PNMT shows;** a note's ticket is checked before writing.
- **The code's status:** *Alert* for a problem of importance High, or 3 hosts with problems at one location; *Warning* for any other problem of normal importance or a missed ticket deadline; *Info* for minor problems or a reminder due. **A reader who can't see every case behind it gets the state, not the reason.**

**Cookie (B):**

- **The read tools are `PNMTD/Cookie/CookieTools.cs`** (§3.5): `cases_list`, `case_get`, `hosts_search`, `host_get`, `sensor_events`, `location_get`, `kb_search`, `kb_page`, `ticket_get` and `zabbix_problems`, each with its area and level, none that writes. Package eight's MCP server is to offer these.
- **Screening:** Jev reads the outside text the brief carries (the members' messages) once per round, and every chat message, with a question meant for chat. **Tool results get the local scanner only** (cleaned, scanned, an obvious injection withheld, the rest wrapped and flagged). Nothing in package nine acts, so a misled Cookie can only write a wrong note. Jev on tool results comes with nine point five's actions.
- **The privacy filter's placeholders are one mapping for the server** (`PERSON_n`, `EMAIL_n`, `PHONE_n`), handed out under a lock and kept. **Account names that are ordinary words** (admin, backup, support …) aren't masked. Tool results are masked when they are read, not at ingest; in `Code` mode that is a regular expression, so no round waits for it.
- **A round:**
  - the brief has the cases that changed since the last round, have no note yet, or have a deadline within the hour, in full (at most 25), and the others in one line (at most 60);
  - **the same brief as last time is a quiet round: no call,** and Cookie's status stays valid;
  - each case's work ends in a separate call with a strict schema and no tools, on what the model found (§14.3).
- **Cookie's status counts while Cookie runs and is under 30 minutes old;** otherwise the code's is shown.
- **The chat:**
  - each message opens a request case and is answered in the next round, at most 5 a round, with the lower of the asker's rights and Cookie's, the asker's rights read from the database;
  - **a message Jev flags never reaches the model;**
  - at most 20 messages an hour per person; a message to a stopped Cookie is refused.
- **The summary** is kept for 15 minutes per person in memory, so a restart writes it again.
- **The trace** is polled (`afterId`), not streamed. It is kept 30 days; closed cases and the outbox a year.
- **Package one's AI budget no longer counts `cookie.*`** (§19.1).
- **Not in package nine:** the *Mute, away, suppress* and *Lessons* tabs, and *Cookie: Instruct* grants nothing more than *Use* yet.

**The web UI (A3, B3):**

- **The dashboard's card heads the left side,** above the problems. At 1920×1080 the recent changes then begin above the fold rather than fitting whole, and so do the problems at 1440×900 once the reader's summary lists open cases; docs/webui-migration-plan.md §5.5's targets say so. **It is hidden where `Cookie__Enabled` isn't set.** Cookie's own state is in its header, and the summary marks the lines that need the reader.
- **The prompts are on the *Identity* tab,** as §9.3 has them, each with *Reset to default* and *Preview*.
- **Cookie's window** (*Overview → Cookie*) has three tabs: *Cases*, *Trace* and *Chat*. The trace is polled every 2 seconds while Cookie works and every 15 otherwise. The chat is polled every 2 seconds while a reply is awaited.
- **Admin → Cookie names users and groups through its own lookup** (`admin/people`), because *Cookie: Manage* may not open *Admin → Users*.
- **A summary written by code is kept a minute,** not 15, so that one written before the case book's first run doesn't stay.

**The finish (B4):**

- **The situation analysis is removed** (§12). The migration `RemoveSituationAnalysis`:
  - drops its runs and findings;
  - deletes its three system sensors, with their events and their notification links;
  - deletes its decisions;
  - cancels its queued work.

  The audit catalog keeps `AiAnalysisStarted` for the old entries. **The scrubber stays,** in `PNMTD/Ai/`, for the knowledge base's AI tags and Cookie's privacy filter.
- **The e2e stack enables Cookie,** and `e2e/cookie.spec.ts` covers Admin → Cookie, the card, the cases, the trace and the chat on a stopped Cookie.

---

## 20. Building revision 7 (planned)

**Revision 7 changes package nine as built (§19.8).** It brings:

- case stops and comments (§4.3);
- the card in Cookie's window (§9.2);
- the user's contact details, short code and colour (§8.1);
- the availability plan, the routing on it, and what Cookie reads from it (§8.2 to §8.4, §9.4).

It is built like package nine: in phases, each with its tests, as commits on master, before 1.5 is released. **The release notes for 1.5 describe the result.**

### 20.1 Data model

| Table | What changes |
|---|---|
| `users` | **New:**<ul><li>the e-mail address, the Pushover user key and the Redmine user;</li><li>whether they take part in the plan, the short code (unique), the colour, the default week, and the column's place (§8.1)</li></ul> |
| `availability_types` | **New:** name, short code, colour, the six checkboxes, *default*, *for days off*, order. A type in use is retired, not deleted |
| `availability_entries` | **New, one row per person and day:** the type, whether the fill wrote it, who set it and when |
| `availability_days` | **New, one row per day that has something set:** the responsible, the fallback, the comment |
| `availability_settings` | **New, one row:**<ul><li>the count columns;</li><li>the working hours for each weekday, and the holidays;</li><li>the global fallback (a user or a group)</li></ul> |
| `cookie_cases` | **New:** stopped, with when, by whom and the comment |
| `cookie_case_entries` | **New kinds:** *comment*, *stopped*, *go on* |
| `cookie_duties` | **New state:** *Stopped* |
| `cookie_settings` | The sections `hours` and `notifications` go |

**A migration moves what is there:**

- Cookie's contacts go to their users;
- its weekly hours and holidays go to the plan's settings;
- the types of the Excel plan (*ANWE*, *HOME*, *VACA*, *UNAV*, *EXTN*, *KOMP*) are created with their checkboxes, *ANWE* as the default and *UNAV* for days off;
- the seed gives Cookie's group *Availability: View*.

**The recipient rules and quiet hours are dropped.** 1.5 isn't released, so no installation depends on them.

### 20.2 Server

| Piece | Job |
|---|---|
| `AvailabilityPlan` | Who works on a day, who is responsible (§8.2), the counts and the warnings, a day's working hours. `WorkingCalendar` keeps its arithmetic, on the plan's hours |
| `AvailabilityFill` | *Fill* and *Fill again*, and the nightly task that keeps 365 days filled |
| `Recipients` | The routing of §8.3: from the plan, the assignee's Redmine user and the escalation group, instead of the recipient rules |
| `CaseBook` | A stopped case has no duties, raises the state to *Info* at most, and a problem case goes on when a new member joins. The code's status reads the plan (§8.4) |
| `CookieRound` | Today's staffing in the brief. Leaves stopped cases out of the work, and reads the comments, screened and pseudonymised |
| `CookieTools` | `availability_get` (§8.4) |
| `Summaries` | *Needs you* from the plan instead of the recipient rules |

### 20.3 API

| Endpoint | What | Right |
|---|---|---|
| `GET availability/plan?from&to` | The days: responsible, fallback, comment, counts, everyone's types, the warnings | *Availability: View*, or taking part |
| `PUT availability/entries` | Types for several people and days at once | one's own column from today: taking part; the rest: *Edit* |
| `PUT availability/days` | The responsible, the fallback or the comment, for several days | *Availability: Edit* |
| `POST availability/fill` | *Fill* or *Fill again*, for everyone or for oneself | *Edit*; for oneself: taking part |
| `GET`, `PUT availability/settings` | The types, the count columns, the hours and holidays, the global fallback | *Availability: Manage* |
| `GET`, `PUT availability/people` | Who takes part, their codes, colours, default weeks and order | *Availability: Manage* |
| `PUT account/contact`, `POST account/contact/test` | One's own e-mail and Pushover key, and a test message | signed in |
| The user's endpoints under *Admin → Users* | The fields of §8.1 | admin |
| `POST cookie/cases/{id}/comments` | A comment | *Cookie: Use*, and seeing the case |
| `POST cookie/cases/{id}/stop`, `…/go-on` | Stop a case, or let Cookie go on | *Cookie: Instruct*, and seeing the case |

**Every write is audited and in the access matrix.**

### 20.4 Web UI

- **Cookie's window:** the dashboard's card above the tabs. On a case: its comments, *Comment*, *Stop* and *Go on*.
- ***Account*:** the e-mail address and the Pushover key, with *Send a test*. ***Admin → Users*:** the fields of §8.1.
- ***Admin → Availability plan*:** the tabs *Plan*, *People* and *Settings* (§9.4). **The table renders only the rows in view,** so years of days for a dozen people stay quick.
- ***Admin → Cookie*** loses *Working hours* and *Notifications*. *Redmine* keeps the escalation groups.

### 20.5 Phases

| Phase | Content |
|---|---|
| **A4** | **The user:** contact details, short code, colour, default week; *Account* and *Admin → Users*; the test message; the duties reading the contacts; the migration of the contacts; the *Notifications* tab goes |
| **A5** | **The availability plan:** the area *Availability*, the types, entries, days and settings, the fill and its nightly task, the counts and warnings, the pages *Plan*, *People* and *Settings*, the working hours moved |
| **A6** | **The routing on the plan** (§8.3) **and what Cookie reads** (§8.4): `Recipients`, the brief, `availability_get`, the code's status |
| **A7** | **Cases:** comments, *Stop* and *Go on*, in the case book, the round and the window |
| **B5** | **The finish:** the card in Cookie's window, the documentation and the 1.5 release notes, and an *As built* for revision 7 |

### 20.6 Testing

- **Who works:** the checkboxes decide, not the names; *on site* with *vacation* doesn't work; a type without checkboxes doesn't work.
- **Who is responsible:** the responsible; the fallback when the responsible doesn't work; the global fallback; *nobody to reach*; nobody outside working hours.
- **The fill:** the default week and the type for days off; closed days skipped; nothing a person set overwritten; *Fill again* replaces only what the fill wrote; the nightly task adds the new day.
- **The counts:** by checkboxes and group; a count below its minimum is flagged, and gives the code's status *Info* today.
- **Editing:** one's own column from today on, not another's and not the past; *Edit* everything.
- **The routing:** a reminder for an assignee who doesn't work goes to the escalation group; a missed deadline reaches whoever is responsible.
- **Cookie:** the brief's staffing and `availability_get` carry placeholders, not codes; the tool refuses without *View*.
- **Cases:** a stopped case's due duties are closed as *stopped*, Cookie doesn't work it, and a new member lets it go on; a comment reaches the brief, screened.
- **The migration** moves the contacts and the hours, and creates the types.
- **The endpoints** in the access matrix.

---

## 21. Building package nine point five (C and D)

**Decided on 2026-11-15: build C and D now, after revision 7.** This section says how, and where the build narrows the sections above. Package nine's rules stay: code keeps the books, every action only adds, and every limit is in code.

### 21.1 What C and D mean in the code

- **Cookie proposes; code decides whether it happens.** Each case's work ends, as in package nine, in one call with a strict schema and no tools. Its answer now has, besides the note, **proposals**: the action of §6, the targets as ids from the brief or the tools, the text, the reason, the evidence ids, a confidence and an urgency. Code never reads an address or a name from the text.
- **The executor** takes each proposal through, in this order, and logs every step in **Cookie's decision log**:
  1. **the action's level** (*Off · Propose · Auto*, per action, on *Actions*): *Off* drops it;
  2. **the confidence threshold** (per action): below it, it goes to a person (*Propose*);
  3. **the targets:** each id must exist, a person must be an active PNMT user with the channel's address, a ticket must be in a project PNMT shows, and Cookie's group must have the right for it (a note on a ticket: *Redmine: View*; a ticket: *Redmine: Link*; a note on a host: *Monitoring: Edit*);
  4. **mute and suppress** (§10.3), and **the stop of its case** (§4.3);
  5. **duplicates:** the same action on the same target for the same case within a day is dropped;
  6. **Seraph,** for everything that leaves PNMT or reaches people (§5): *approve*, *revise* (Cookie gets one retry with Seraph's reason), *reject*, or *ask a person*. **Without Seraph** (no model, or it fails), the proposal goes to a person;
  7. **the level again:** *Propose* puts it into **the decisions inbox** (a new kind, `cookie.action`: *Do it* · *Don't*, with a comment); *Auto* runs it;
  8. **running it** through the outbox (§6.1), with the placeholders restored and Cookie's name as the signature.
- **The default levels** follow §13.4: everything that leaves PNMT or reaches people starts at **Propose**; a note on a host, sensor or location, and the event of an *AI every time* rule, start at **Auto** (both stay inside PNMT, and the event has Seraph). **Thresholds start at 0.7.**
- **Cookie writes the reminders and escalations itself** (§3.4 C) when *Actions → Cookie writes the duties' texts* is on and Cookie is running: code still decides when, to whom and through which limits; Cookie writes the text from the case, Seraph reviews it, and **the template is used whenever Cookie is stopped, paused, slow or rejected.**
- **Mail rules set to *AI every time* go to Cookie** (§12) while `Cookie__Enabled=true`: a matching mail waits for Cookie's next round, Cookie decides *ok*, *fail* or *unknown* with a strict schema, the event is the action *MailEvent* (Seraph in place of the verification model), and *unknown*, a Jev hit or a rejection becomes the inbox's existing *The AI could not decide* decision. **While Cookie is stopped or paused, such mails wait, visible in the mail log.**
- **Narrowed: the analysis of unhandled mails stays with package one.** It already proposes mail rules through the inbox; moving it into Cookie's round would only rename it. Cookie sees how many mails wait unhandled in its brief. To be moved when Cookie proposes rules itself.
- **Mute and suppress** (§10.3) are rows code checks: *mute* a host, sensor, location or case; *suppress* a channel, a sensor type or a kind of case. Each has an end date, at most 30 days away. **In the chat, Cookie only fills in a draft for someone with *Instruct*; the person confirms it in PNMT's UI.** *Admin → Cookie → Mute, suppress* lists, creates and ends them. A free-text instruction with an end date goes into the prompts' `{instructions}`.
- **The big brother** (§15): code offers a case when it matters and nobody has taken care of it for a working day (§15.2, conditions 2 and 3); Cookie confirms in its answer that it needs fixing; a person with *Instruct* can ask on the case. One of the two models at random, the other for a second call on the same case or when the first fails; read tools with Cookie's group's rights, up to 60 tool calls; its own budget (`Cookie__BigBrother__BudgetUsd`) and the limits of §15.5. **The solution goes into the case, and then as a private note on the case's ticket (a proposal like any other), or, without a ticket, into the inbox** (`cookie.solution`: *Open a ticket with it* · *Someone is on it* · *Not useful*).
- **Ratings and lessons** (§11): every entry of the decision log can be rated *Right* or *Wrong* with a comment, by whoever may see its case. Once a day, or after 10 new ratings, the summariser turns them into **lessons, a versioned section** of *Admin → Cookie* (edit, disable, roll back). **A lesson marked as a suppress rule becomes a suppress object** for 30 days, shown on the admin page. Active lessons go into every prompt's `{lessons}`.
- **Seraph** also answers in the chat (a person picks *Seraph*): it explains its verdicts from the decision log, and can't act. Once a day it writes **a drift report** from the last day's decisions and a sample of Cookie's notes (1 in 10) into the decision log.
- **Narrowed: messages from MCP wait for package eight,** which has no server yet. Cookie's chat already takes a message with the asker's rights; package eight adds the token's source and the read-only rule (§10.2).

### 21.2 Data model

| Table | What it holds |
|---|---|
| `cookie_decisions` | **Cookie's decision log:** when, the case and the round, the action, the proposal (JSON), the confidence, Seraph's verdict and reason, the outcome (*done*, *proposed*, *rejected*, *off*, *below threshold*, *duplicate*, *muted*, *held back*, *failed*), the inbox decision, the outbox row, the model, the rating with its comment and who gave it, the source areas |
| `cookie_instructions` | Mute, suppress and free-text instructions: kind, what (scope and value), the text, until, a draft or confirmed, by whom, ended by whom |
| `cookie_cases` | **New:** the big brother asked (by whom, when), and the last solution |
| `cookie_messages` | **New:** to Cookie or to Seraph; a draft instruction a reply carries |
| `cookie_settings` | **New section** `lessons`; *Actions* gains the levels and thresholds, and *Cookie writes the duties' texts*; *Models* gains Seraph's and the big brother's models |
| `maillogs` | **New AI state** *waiting for Cookie* |

### 21.3 Server

| Piece | Job |
|---|---|
| `CookieExecutor` | The steps of §21.1, the decision log, running an approved proposal |
| `Seraph` | Reviews, the chat, the daily drift report |
| `DutyWriter` | Cookie's text for a reminder or an escalation, with Seraph; the template otherwise |
| `CookieMail` | The *AI every time* mails in Cookie's round |
| `Instructions` | Mute, suppress and instructions: active ones, drafts, confirming, ending, the checks |
| `BigBrother` | The offer, the call, the fallback, the solution, its budget |
| `Lessons` | Ratings, the daily summariser, lessons into prompts, rule lessons into suppress objects |
| `CookieActionDecision`, `CookieSolutionDecision` | The inbox's two new kinds |

### 21.4 API

| Endpoint | What | Right |
|---|---|---|
| `GET cookie/decisions` | The decision log, by the reader's rights (§9.2) | *Cookie: Use* |
| `PUT cookie/decisions/{id}/rating` | *Right* or *Wrong*, with a comment | *Cookie: Use*, and seeing its case |
| `POST cookie/cases/{id}/big-brother` | *Ask the big brother* | *Cookie: Instruct* |
| `POST cookie/messages` | **Now with** *to Seraph* | *Cookie: Use* |
| `POST cookie/instructions/{id}/confirm`, `…/end` | Confirm a draft from the chat; end one | *Cookie: Instruct* |
| `GET`, `POST cookie/admin/instructions` | The list, and a new one | *Cookie: Manage* |
| `PUT cookie/admin/settings/lessons` | The lessons (versioned) | *Cookie: Manage* |

### 21.5 Web UI

- **Cookie's window:** a *Decisions* tab (the log, with *Right* and *Wrong*); on a case, *Ask the big brother* and the solution; the chat with a choice of *Cookie* or *Seraph*, and *Confirm* on an instruction Cookie filled in.
- **The decisions inbox** shows the two new kinds with their options.
- ***Admin → Cookie*:** *Actions* gains the levels, thresholds and *Cookie writes the duties' texts*; new tabs *Mute, suppress* and *Lessons*; *Models* gains Seraph and the big brother; the Overview counts proposals, verdicts and the big brother's budget.

### 21.6 Phases

| Phase | Content |
|---|---|
| **C1** | **Proposals, the executor, Seraph and the inbox:** levels and thresholds, the decision log, `cookie.action`, the action kinds of §6 |
| **C2** | **Cookie writes the duties' texts,** and **the *AI every time* mails** in Cookie's round |
| **D1** | **Mute, suppress and instructions,** with drafts from the chat |
| **D2** | **The big brother** |
| **D3** | **Ratings and lessons, Seraph in the chat, the drift report** |
| **CD-UI** | The web UI of §21.5, the documentation, the release notes, and an *As built* for C and D |

### 21.7 Testing

- **the executor,** with a fake model: a proposal below its threshold goes to a person; *Off* drops it; an unknown id or a missing right is refused; a duplicate is dropped; a muted host gets nothing; Seraph's *revise* gets one retry, *reject* stops it, no Seraph sends it to a person; *Propose* raises an inbox decision, and approving it sends through the outbox;
- **the duties' texts:** Cookie's when approved, the template when Cookie is stopped or Seraph rejects;
- **the mails:** an *AI every time* mail waits for Cookie, *ok* writes the rule's OK code, *unknown* raises the decision, a stopped Cookie leaves it waiting;
- **instructions:** a draft does nothing until confirmed; an ended one stops counting; at most 30 days;
- **the big brother:** the other model on the second call and on a failure; the limits and the budget send it to a person; the solution lands on the ticket or in the inbox;
- **lessons:** a rating counts once; the summariser's suppress rule becomes a suppress object; the lessons reach the prompt;
- **the endpoints** in the access matrix.

### 21.8 As built (2026-11-15)

**Revision 7 and package nine point five are built on master.** Where the code differs from §20 and §21.1 to §21.6, this section says so; the rest is as planned.

**Revision 7:**

- **A4 to A7 are one server commit,** because the default week names the plan's types: the person on the user, the availability plan, the routing on it, and stopping a case.
- **Someone without a week of their own works on every open day:** the standard week has all seven days, and the plan's hours decide which days are closed. So a user who isn't in the plan counts as working whenever PNMT is open.
- **The plan's endpoints:** the table is for everyone signed in, and is empty for someone who isn't in the plan and has no *Availability: View*. *Fill mine* is its own endpoint (`fill/mine`, 409 for someone not in the plan); *Fill* for everyone needs *Edit*.
- **The migration creates the Excel plan's six types** (*ANWE* the default, *HOME*, *EXTN*, *VACA*, *KOMP*, *UNAV* for days off), moves Cookie's contacts to their users and its hours and holidays to the plan's settings, and deletes the `hours` and `notifications` sections.
- **Routing (§8.3):** a group that stands in, but of which nobody works today, hands over to whoever is responsible.
- **The code's status says *Info*** when, on an open day, nobody responsible works, or a shown count is below its minimum.
- **A stopped case's due duties close as *Stopped*;** *Go on* deletes them, so that they are planned again.

**Package nine point five (C and D):**

- **One inbox kind, `cookie.action`,** for every proposal that waits for a person, the big brother's solution included: with a ticket it is a private note on it, without one a proposed ticket. *Someone is on it* is *Don't* with a comment. Cookie handing something to a person is *Noted* or dismissed. The inbox's *Don't* takes a comment (`DecideRequest.Comment`).
- **What the executor checks** (§21.1):
  - a person must be an active user with the channel's address;
  - a ticket needs *Redmine: Link* in Cookie's group, `Redmine__WriteApiKey`, a project for Cookie's tickets, and a problem case without an open ticket of Cookie's;
  - a note or an answer on a ticket needs *Redmine: View*, and goes only to the case's own ticket or one linked to its host;
  - a note on a host or sensor needs *Monitoring: Edit*, and is added below the notes with the date and Cookie's name. **Locations have no notes,** so there are none on locations;
  - a duplicate is the same action on the same target for the same case within a day, done, proposed or approved.
- **Mute and suppress stop** Pushover, e-mail, tickets, notes on tickets and the big brother; not a note inside PNMT, and not a mail rule's event. The channels are *pushover*, *email*, *ticket* and *redmine-note*.
- **Seraph's *revise*** gets its retry in a case's work only. For a mail, a duty's text and the big brother's solution, *revise* goes to a person.
- **Whom Cookie may address:** each case's work carries *PeopleYouMayAddress*: the assignee, today's responsible and fallback, whoever stands in, and a ticket's escalation group, by user id, with placeholders for names.
- **The chat doesn't act.** Cookie answers and, for someone with *Instruct*, fills in an instruction (one more short call with a strict schema), which the person confirms. Proposals come from the cases' work only.
- **Seraph in the chat** answers from the last 14 days' decisions the asker may see.
- **The duties' texts:** Cookie has 90 seconds. Each attempt is in the decision log as *duty-text*.
- **The *AI every time* mails:** up to 5 a round, each screened by Jev first. Cookie's group needs *Mail: Read*; without it, the trace says the mails wait.
- **The big brother:** "a working day" is 8 working hours (24 hours when PNMT is always open). Its purposes are `cookie.bigbrother*`, outside Cookie's budget and package one's.
- **Lessons** are summarised when 10 ratings are new, or once a day when any is. **Seraph's drift report** comes once a day after 06:00, while Cookie runs. Both are the daily task's, `CookieDailyTask`.
- **Kept:** the decision log and ended instructions for a year, like the outbox.
- **After an independent review of C and D** (commit 74cb02d):
  - **Messages:** Pushover only for what is urgent, e-mail for what is important or urgent, and never to someone who doesn't work today by the availability plan. **Outside working hours a message goes to a person** instead of out (§8.3).
  - **Approving a proposal claims it once and checks it again:** the level, the target, the rights, the case's stop, mute and suppress. A proposal still waiting is a duplicate whatever its age, and one *hand to a person* per case waits at a time.
  - **In the inbox,** Cookie's proposals that carry more than *Monitoring*, *Mail* and *Cookie* are for admins, because their text may come from Redmine, Zabbix or the knowledge base.
  - **Masking:** the instructions (with who gave them) and the lessons are masked in every prompt, and so is what Seraph reads of a proposal that code built.
  - **Cookie's lines in a host's or sensor's notes** (`[Cookie, 2026-11-15 10:30] …`) aren't progress.
  - **A stopped case goes on** only for a member it didn't have when it was stopped, and its stopped duties are planned again.
  - **The big brother** answers a person's ask once, whatever comes of it. Its solution carries the areas its tools read, and is shown only to readers who have them all.
  - **One failing mail, or a failing retry** after Seraph's *revise*, goes to a person instead of failing the round.
  - **The public answers' limit** of §6.1 is counted.
- **Lessons:** a lesson a person wrote or changed is marked as theirs, and the summariser keeps it; its own replace its own. A lesson that is a rule becomes a suppress object when it is saved, by the summariser or by an admin.

**The web UI (revision 7, C and D):**

- ***Admin → Availability plan*** opens without a right for everyone in the plan; *People* and *Settings* are for *Manage*. The table draws only the rows in view and loads 60 days more at either end as it scrolls.
- **Cookie's window** has a *Decisions* tab, with *Right* and *Wrong* and a comment. A case shows its comments, *Stop* and *Go on*, *Ask the big brother*, the solution, and its decisions. In the chat, a person picks *Cookie* or *Seraph*; a draft instruction shows as a card with *Confirm* and *End now*.
- **The inbox** names Cookie's proposals, and *Don't* and *Dismiss* take a comment.
- ***Admin → Cookie*** has the action levels and thresholds and the two switches on *Actions*, and the new tabs *Mute, suppress* and *Lessons*. *Models* has Seraph and the big brother, with the .env values; the Overview counts today's acting.
- **Known:**
  - a draft confirmed or ended elsewhere shows its new state in an open chat after a reload;
  - a proposal that is for admins in the inbox still links there from the decision log, which a non-admin finds empty.
- **Not built:**
  - categories and requesters excluded from public answers (§7.2);
  - messages from MCP (package eight).
