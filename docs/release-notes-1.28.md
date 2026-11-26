# PNMT 1.28: the availability plan's Status tab, days as responsible against a share, notes, and the time zone shown

## What's new

### The Status tab

- **A fourth tab in *Availability*,** next to *Plan*, for everyone who sees the plan (docs/availability-extensions-plan.md §9 to §11).
  Every number on it comes from cells they already see. Someone who doesn't see the plan reads why, as on the *Plan* tab.
- **A year at the top** (`?year=`), and for the current year *Until today* (the default) or *Whole year*.
- **Five tiles:** days without a working responsible, days below a column's minimum and empty cells to set, each over the next 30
  days; the team's vacation days in the year, and how many more are planned; and the share of working days on site. A tile with a
  day opens the plan at the first one.
- ***Responsible for x days* against each person's share,** for the year, the last 365 days or the last 30: a bar per person centred
  on their target, with the days, the actual and target share, and the deviation in days. Fallback days are counted apart, and so are
  days of people who have left the plan (*Others*) and days without a responsible.
- **Who gets which weekdays:** people × Monday to Friday, as days as responsible.
- **Days per person,** for the year or a month: each type, *Works*, *Responsible*, *Fallback* and *Empty*, with a bar of the split
  and a totals row. **The team by month:** columns January to December, stacked by type, for everyone or one person. **By month:**
  people × months for one measure. Both tables download as CSV (semicolons, for Excel).
- **The office:** a year calendar shaded by a count column (*On site* by default), days below the minimum outlined, a click opens the
  plan at that day; and the average by weekday.
- **Who is away** in the next 14 days.
- **What counts:** open days only, so a vacation across a weekend counts its working days. A day as responsible is the day **as
  planned**: when someone falls ill and the team changes the plan, the plan is what counts.

### The responsible's name and a share per person

- **The day's responsible has a name you choose:** *Settings → The day's responsible*, 1 to 30 characters, *Responsible* by default.
  It reads *Responsible for 14 days* or, renamed, *Hero for 14 days*: in the plan's header, titles and warnings, the hints, the
  *Status* and *People* tabs, and as the default label of the boards' *On duty* element (an element's own label still wins).
- **A share as responsible for each person,** 0 to 500 %, 100 by default: on the *People* tab's dialog and under *Admin → Users*.
  It is a weight: 100 is a full share, 50 half as many days as someone with 100, 0 never; nothing has to add up to 100. The *People*
  tab shows both numbers: *100 % → 25 %*.

### Notes

- **A *Notes* column** to the right of *Comment*. A cell shows its newest note as *name: text*, and *+2* when there are more; **+**
  adds one under your own name. A click lists the day's notes with their time.
- **Who:** everyone in the plan and *Availability: Edit* write notes, on any day. The author changes a note; the author or
  *Availability: Edit* deletes it. One line of up to 300 characters, at most 20 a day.
- **The comment stays as it was:** one per day, for *Availability: Edit*, shown on the *On duty* element. Notes are the team's.

### Finding your way in the plan

- **Go to a day** next to *Today*: picked from a calendar or typed as *Fill again* takes it, kept in the address (`?day=2027-03-03`).
  A day not loaded yet loads around itself, and its row is outlined for a moment.
- **Every month's row** now stands out as January's did, and a **year row** (`2027`) comes before each January.

### Fixed

- ***Periods may not overlap* for periods that don't overlap.** Changing a period that was already saved (08:00–17:00 to 08:00–11:30)
  changed the field but not what the page checked and saved, so *08:00–11:30, 13:00–17:00* was checked as *08:00–17:00, 13:00–17:00*.
  Periods added with *Add* always worked. Now every period takes a change.
- **The server's message goes once its part is fixed,** instead of staying until the next *Save*.
- **The plan's hours were off by the zone's offset** when `TZ` is set: 08:00 showed as 09:00 in a Zurich winter. Cookie's text on who
  is in read the same hours and is right again too. When Cookie reaches people was never affected.
- **The time zone is shown:** the plan's toolbar ends with `Europe/Zurich · Thu 26.11.2026 · 14:32`, its tooltip gives the offset and
  your browser's time when that is elsewhere, and a new day there loads the plan again. *Settings → Working hours* names the zone and
  warns when it is UTC.

## Updating from 1.27

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.28.0`. It takes a backup first.

- **One migration:** a table for the notes, and the share on users, 100 for everyone already there. No new right.
- **Check the time zone:** *System → Overview → Time zone*. If it says *UTC*, `TZ` isn't set and the working hours entered so far mean
  UTC. To enter Swiss hours, set `TZ=Europe/Zurich` in `deploy/.env` and restart; this also moves PNMT's other wall-clock rules (uptime
  per day, *daily at 8*, the 04:00 renewals).
- **Renaming the responsible** is optional: without it everything reads *Responsible* as before.

## Good to know

- **What keeps *responsible*:** Cookie's prompts and messages, the API, MCP and the audit log, which code and prompts rely on; and the
  notification rules' recipient, *The responsible of the day*.
- **Counted with today's settings:** past days are read with today's working week, holidays and types; there is no history of the
  settings. People who have left the plan have no row.
- **Not over MCP:** the Status tab and the notes are in the web UI only.
- **The audit log** has notes added, changed and removed with the plan's other changes (`AvailabilityChanged`).
- **The plan:** docs/availability-extensions-plan.md, with its decisions and, in *As built*, where the build differs from it.
