# PNMT 1.9: Cookie without Redmine or Zabbix, and a chat that tells the truth

Version 1.9.0.

## What's new

- **Cookie's chat says what Cookie does** (docs/cookie-fallback-plan.md §5). Asked to send something, Cookie used to deny abilities it has. Now code tells it, in every chat reply, which actions it may propose from a case and under which conditions (Pushover only for what is urgent, e-mail for what is important or urgent, in working hours, to PNMT users), and that nothing asked in the chat becomes a proposal. The default text of *A person's request* changed accordingly; an installation that overrode it keeps its own.
- **Cookie without Redmine or Zabbix** (docs/cookie-fallback-plan.md §3, §4):
  - **One reading of what is available,** used by the prompts, the tools, the executor, the duties, the chat and the admin tabs: Redmine as *none*, *read-only* or *writable*, Zabbix as *available* or not. An integration counts as gone when it isn't configured, or when its reads have failed for longer than **30 minutes**; the first successful read brings it back.
  - **Without a writable Redmine, Cookie proposes no tickets and no notes on tickets,** isn't offered the ticket tool, and is told what to do instead: **what would have been a ticket goes by e-mail to whoever is responsible, and by Pushover too when it is urgent.** A proposal that comes anyway is refused with the reason.
  - **The problem ticket's duty does the same:** after two days without progress, an e-mail to whoever is responsible today (the plan's responsible, fallback or stand-ins), with the ticket's content, and Pushover for a host of importance High. **A short e-mail follows when the problem is resolved,** to the same people. Until now the duty ended as *not possible* and told nobody.
  - **Without Zabbix,** Cookie doesn't look for Zabbix problems or acknowledgements. An unreachable Zabbix or Redmine closes no case.
  - ***Admin → Cookie → Overview*** shows what Cookie counts as available and why not; ***Actions*** says *Unavailable now* under the ticket actions. The API's acting item carries it as `integrations`.

## Updating from 1.8

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.9.0`. It takes a backup first.

- **No migration and no new setting.** Availability follows `Redmine__*`, `Zabbix__*`, *Admin → Cookie → Redmine* and the reads the case book and the Zabbix poller do anyway.
- **An installation without Redmine** sees the ticket actions as unavailable at once, and its next unresolved problem goes by e-mail. **E-mails replace notes there,** so the output limits on *Admin → Cookie → Actions* may need raising.
- **An installation with Redmine** sees no change, apart from the chat's answers.

## Good to know

- **The grace period is a constant** (30 minutes, `CookieIntegrations.Grace`), chosen so that a Redmine restart or a short network cut changes nothing, while a reminder that is due doesn't wait longer than that for a note it can't write.
- **The problem e-mail uses the template,** not Cookie's own writing; Cookie's texts stay with the ticket reminders.
- **Cookie's model is unchanged.** The research on a cheaper model is recorded in docs/cookie-fallback-plan.md §11, decided separately.
