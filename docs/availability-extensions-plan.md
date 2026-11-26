# Plan: Availability plan: a Status tab, days as responsible against a share per person, notes, a day to jump to, and the time zone shown

**Status:** **Built (§15 *As built*), not yet released** (2026-11-26). Revision 2 takes your answers to revision 1's questions (§14). Planned for 1.28.0 (1.27.0 went out with the responsibilities' MCP work while this was built). It extends the availability plan of [cookie-plan.md](cookie-plan.md) §8.2 and §9.4 and [adjustments1.md](adjustments1.md). Below, *decision N* means this plan's §13.

| | |
|---|---|
| **Goal** | <ol><li>**Fixes:** the hours in the table are off when `TZ` is set; *Periods may not overlap* stays on screen after the periods are fixed; nothing on the page says which time zone the plan uses (§3).</li><li>**The plan's table:** every month's row stands out as January's does today, and a row of its own marks each new year (§4). A day field next to *Today* jumps to any day (§5).</li><li>**Notes:** a second comment column. Anyone in the plan adds a note with *+*, and the note shows its author's name (§6).</li><li>**The responsible's name** is a setting: *Responsible* by default; one production use calls it *Hero* (§7).</li><li>**A share of the days as responsible per person,** set on the person (§8).</li><li>**A *Status* tab:** counts per person per month and per year, *Responsible for x days* against each person's share (for the year, the last 365 days and the last 30 days), and a few charts (§9 to §11).</li></ol> |
| **Not in scope** | <ul><li>Vacation entitlements and balances: PNMT doesn't know how many days anyone is owed.</li><li>A history of the settings: past days are counted with today's working hours and holidays (§9.1).</li><li>Rows for people who have left the plan (§9.1).</li><li>Stats over MCP, for Cookie, or on boards.</li><li>Showing the times in the browser's time zone: working hours are the office's wall clock (decision 6).</li><li>Renaming anything in *Admin → Responsibilities*. That is a different feature, with its own *Responsible* and *Deputy*.</li><li>A setting for the name of the fallback.</li></ul> |
| **Rights** | No new area or level. The *Status* tab has the *Plan* tab's audience: *Availability: View* and everyone in the plan (decision 1). Notes are written by everyone in the plan and by *Availability: Edit* (decision 4). The name and the shares need *Availability: Manage* (admins can also set the share under *Admin → Users*) |

---

## 1. Summary

| Part | What it is |
|---|---|
| **Fixes** | `PlanSnapshot.Hours` no longer converts local times a second time. A server error is shown only while the field is unchanged. The plan shows the zone, today's date and the time: `Europe/Zurich · Thu 26.11.2026 · 14:32` |
| **Month and year rows** | Every month's row gets the strong look that only January's has today. A new year row (`2027`) comes before January's month row. Every row stays as high as a day's |
| **Go to a day** | A day field with a calendar button next to *Today*. It takes the same typed forms as *Fill again*. A day that isn't loaded yet loads around itself. The day is kept in the URL (`?day=2027-03-03`) |
| **Notes** | A *Notes* column to the right of *Comment*. A cell shows its newest note, as *name: text*, and *+2* when there are more. A click opens all of them, with *+ Add a note*. Notes live in a table of their own, one row per note |
| **The name** | `AvailabilitySettings.ResponsibleLabel`, *Responsible* by default. The plan's pages, the Status tab, the person's form and the board's *On duty* element use it, as *Responsible for 14 days*. Cookie, the rules' recipient, the API, MCP and the audit log keep *responsible* |
| **Share** | `users.ResponsibleShare`, 0 to 500, 100 by default: 100 is a full share, 50 is half, 0 is never. It sits next to the short code and colour (`PlanFields.vue`) |
| **Status tab** | `Plan · Status · People · Settings`, with one request: `GET /availability/stats?year=`. It shows a row of numbers, the days as responsible against each share, days per person (per year or month, with CSV), the team by month, the office day by day and by weekday, and the next 14 days |

---

## 2. Today (checked in the code)

- **The page** ([AvailabilityView.vue](../PNMTD.WebUI/src/views/admin/availability/AvailabilityView.vue)) is at `/admin/availability/:tab?`. It has *Plan*, plus *People* and *Settings* for *Availability: Manage*.
- **The table** ([PlanTab.vue](../PNMTD.WebUI/src/views/admin/availability/PlanTab.vue)):
  - It is an infinite query on `planKey`. The first page is the server's default: a week back and 60 days ahead (`AvailabilityService.PlanAsync`). It then loads 60-day chunks as the table scrolls (`plan.ts`: `before`, `after`, `mergeDays`).
  - Only the rows in view are drawn, and **every row is as high as a day's**: the scrolling counts in rows (`rowHeight`). Anything new in a cell must fit on one line.
  - *Today* (`scrollToToday`) can only scroll among the days already loaded. Nothing jumps to another day.
  - `planRows` (`plan.ts`) puts a month row before each 1st. January's gets `year: true` and the strong style (`tr.month.year`); the other months are pale.
  - `setQueryData` on `planKey` at `PlanTab.vue:395` is the only place that writes the plan into the cache.
- **The comment:** one per day, `availability_days.Comment` (at most 500 characters). Only *Availability: Edit* edits it, inline, and it is saved with the day's row (`PUT /availability/days`). The board's *On duty* element shows it.
- **Hours and time zone:**
  - PNMT's time zone is `Clock.TimeZone`, read from `TZ` (an IANA name). **Empty means UTC.** `deploy/.env.example` ships `TZ=` empty.
  - *System → Overview* shows the zone; the boards get it through their catalog and view. The availability plan shows it nowhere.
  - `WorkingCalendar.PeriodsOn` returns **local** wall-clock times. **Bug:** `PlanSnapshot.Hours` runs them through `Clock.ToLocal`, which reads an unspecified time as UTC and converts it again. With `TZ=Europe/Zurich`, 08:00–17:00 shows as 09:00–18:00 in winter and 10:00–19:00 in summer. 24:00 shows as 01:00 or 02:00.
  - The bug is only in the text: `IsWorking`, the ticket clocks and Cookie's reaching are right. The text goes to the plan (`AvailabilityDayItem.Hours`) and to Cookie's staffing (`CookieStaffing.cs:65`). No test checks it, so CI's runs east and west of UTC didn't catch it.
  - With `TZ` empty, the text is right, but 08:00 means 08:00 UTC, which is 09:00 or 10:00 in Zurich. Nothing on the page tells.
- **The overlap message:**
  - `periodProblems` ([hours.ts](../PNMTD.WebUI/src/views/admin/availability/hours.ts)) and `AvailabilitySettings.Periods` both sort by start and accept periods that touch. Both accept *08:00–11:30, 13:00–17:00*.
  - [HoursEditor.vue](../PNMTD.WebUI/src/views/admin/availability/HoursEditor.vue) shows the browser's check **and the server's last answer** (`props.errors['week.Monday']`).
  - [SettingsTab.vue](../PNMTD.WebUI/src/views/admin/availability/SettingsTab.vue) empties `errors` only in `save()` and `reset()`. So: a Save with a real overlap, then a fix, still shows *Periods may not overlap* until the next Save. That is the likely cause of *sometimes* (§3.2). The same goes for the types' and the count columns' messages.
- **A person's place in the plan:** [PlanFields.vue](../PNMTD.WebUI/src/views/admin/availability/PlanFields.vue) holds the short code, colour, default week and order. The *People* tab and *Admin → Users* (`UserProfileForm.vue`) both use it, through `People.ApplyPlanAsync`.
- **Days typed in a field:** `FillAgainDialog.vue` has a `DatePicker` that also reads typed days (`parseDay`, *3.11.2027, 3.11.27 or 3.11.*), with the blur ordering fixed by [fill-again-typed-days-plan.md](fill-again-typed-days-plan.md).
- **Charts:** there is no chart library. `SampleChart.vue`, `UplinkView.vue` and `UptimeBar.vue` draw SVG by hand. The only d3 packages are `d3-force` and `d3-hierarchy`.
- **Times in a zone in the browser:** `boards/time.ts` `boardTime(zone)` formats times in a given IANA zone.
- **The responsible's name in other places:**
  - the board's *On duty* element: its own `dutyLabel`, by default the step's `as` with a capital (*Responsible*);
  - the rules' recipient: `NotificationRecipients.ResponsibleLabel`, *The responsible of the day*;
  - Cookie's texts, MCP and the API's `as` (`"responsible"`, `"fallback"`, `"global fallback"`).
- **Audit:** cells, rows and fills write `AvailabilityChanged` or `AvailabilityFilled`; the settings write `AvailabilitySettingsChanged`.

---

## 3. The fixes (P1, can ship as 1.26.1)

### 3.1 The hours in the table

- `PlanSnapshot.Hours` formats `PeriodsOn`'s times as they are: `HH:mm`, and a period that ends at the next midnight as `24:00`. It doesn't call `Clock.ToLocal`.
- A test checks the text in the CI's two zones, east and west of UTC (`PNMTD_TEST_TZ`): 08:00–12:00 and 13:00–24:00 come back exactly as entered.
- This also fixes Cookie's staffing text, which reads the same method.

### 3.2 *Periods may not overlap* after the fix

- **Reproduce first:**
  1. enter 08:00–12:00 and 11:30–17:00 on a Monday;
  2. Save;
  3. change them to 08:00–11:30 and 13:00–17:00. The message stays.

  An e2e step in `availability.spec.ts` does exactly this. If it doesn't reproduce, we need the exact steps from you before guessing further (§14, question 4).
- **Fix:** the tab keeps the server's errors together with what was sent for each field (`week.Monday`, `holidays.2026-12-24`, `types.3`, `countColumns.{id}`). A server message shows only while its field is still as sent. A change hides it, and the browser's own check (`periodProblems`) carries on as before.
- `hours.test.ts` gets the reported example (*08:00–11:30, 13:00–17:00*: no problems), and the server test gets it too.

### 3.3 The time zone, today's date and the time

- **API:** `AvailabilityPlanItem.TimeZone` and `AvailabilitySettingsItem.TimeZone`, both `Clock.TimeZone.Id`.
- **The plan's toolbar**, at its right end, small and muted: `Europe/Zurich · Thu 26.11.2026 · 14:32`.
  - It is the browser's clock, formatted in PNMT's zone (`boardTime(zone)`), updated every 30 s.
  - The tooltip adds the offset (*UTC+1*). When the browser is in another zone, it also gives the time there: *your browser: 08:32, America/New_York*.
  - When PNMT's date changes while the page is open, the plan is fetched again, so that *Today* and the past/future shading move to the new day.
- **The dates' tooltip** in the table says what the times are in: *08:00–17:00 (Europe/Zurich)*.
- **Settings → Working hours:**
  - its intro names the zone: *From–to as HH:mm in Europe/Zurich, PNMT's time zone (`TZ`)*;
  - when the zone is UTC, a note for Manage: *PNMT's time zone is UTC: `TZ` isn't set. To enter Swiss hours, set `TZ=Europe/Zurich` in `deploy/.env` and restart. This also moves the other wall-clock rules: uptime per day, "daily at 8", the 04:00 renewals.*
- Times are not converted to the browser's zone (decision 6).

---

## 4. Month rows and year rows (P2)

- **Every month's row** gets today's January style: the dark background and the inverted name cell (`tr.month.year`'s rules become `tr.month`'s). `MonthRow.year` goes away.
- **A year row** comes before January's month row, only before a loaded 1 January (as month rows come only before a loaded 1st):

  ```
  31.12.2026  Th  …
  ═══ 2027 ══════════════════════════════   ← year row: new, stronger still
  ▓▓▓ January 2027 ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓   ← month row: now as strong as January today
  01.01.2027  Fr  …
  ```

  - `planRows` yields `{ kind: 'year', key: 'year:2027', label: '2027' }`.
  - The row is as high as a day's, so the scrolling's arithmetic stays as it is. The keep-the-view-in-place watch counts rows by key, so it already handles one more row type.
  - Style: the year in a larger, bold font in the sticky first cell, with a 3 px line in the text colour along the top of the whole row, so that it reads as stronger than a month.
- Selection, the arrow keys and dragging work on dates, so the new row changes nothing there. A drag across the year row selects the days on both sides.
- Tests: `plan.test.ts`'s `planRows` cases. The year row comes before January's month row, never before another month's, and not when the first day loaded is in the middle of January.

---

## 5. Go to a day (P2)

- **The field:** next to *Today*, a `DatePicker` with its calendar button and a typed input, as in *Fill again*: *3.11.2027, 3.11.27 or 3.11.*, read by `parseDay`, with the same blur handling. Enter or a pick jumps. The placeholder is *Go to a day*.
- **A day already loaded:** the table scrolls to it, with two rows above it (as *Today* does). The row is outlined for 2 s, and a polite live region says *Showing Wed 3.3.2027*.
- **A day not loaded:**
  - the plan's query key gets the day: `[...planKey, { day }]`, with the first page from the day −7 to the day +53 (the default's shape), so that scrolling extends both ways from there;
  - `setQueryData` (`PlanTab.vue:395`) writes to the current key;
  - *Today* scrolls when today is loaded, and otherwise drops the day and goes back to the default query, which is usually still cached.
- **The URL:** `?day=2027-03-03`, so a day can be linked and *Back* returns. *Today* removes it. The *Status* tab's tiles and calendar link here (§10).
- **No limit** besides a readable day: a day years back shows what was planned, or empty cells.

---

## 6. Notes, the second comment column (P4)

### 6.1 What it is

- A **Notes** column to the right of *Comment*, as wide as a person drags it (`useColumnWidths`, key `notes`).
- **A cell** shows its newest note on one line: **the author's name** in bold, then the text, cut with an ellipsis, and a *+2* badge when there are more. On hover, and on the selected row, a **+** appears; on an empty cell it is all there is.
- **A click** opens a popover at the cell, placed like the type picker:
  - every note of the day, oldest first: the name, the time (*26.11. 14:32*) and the text;
  - *Edit* and *Delete* on one's own notes, and *Delete* on anyone's for *Availability: Edit*;
  - a field *Add a note*: Enter saves, Escape closes.
- **The comment stays as it is:** one per day, for *Availability: Edit*, shown on the *On duty* element. Notes are the team's; the comment is the planner's.

### 6.2 Who

- **Read:** whoever sees the plan.
- **Write:** everyone in the plan, and *Availability: Edit* (decision 4). Any day, the past too: a note about yesterday is fine.
- **Edit:** one's own notes. **Delete:** one's own, and anyone's for *Availability: Edit*.

### 6.3 Data and API

- **Table `availability_notes`** (`AvailabilityNoteEntity`):

  | Column | |
  |---|---|
  | `Id` | `Guid` |
  | `Date` | `DateOnly`, indexed |
  | `UserId` | the author |
  | `AuthorName` | the display name when written, at most 200. It is shown when the user no longer exists; otherwise the current display name is |
  | `Text` | 1 to 300 characters, one line, plain text |
  | `CreatedAt`, `ChangedAt` | UTC |

- At most 20 notes a day.
- `AvailabilityDayItem.Notes`: `[{ id, userId, author, text, createdAt, changedAt, mine }]`.
- `POST /availability/notes` `{ date, text }` returns the note; `PUT /availability/notes/{id}` `{ text }`; `DELETE /availability/notes/{id}`.
- Audit: `AvailabilityChanged`, with *Availability: a note on 3.12.2026 added*, *changed*, or *by Ben Keller removed*.
- The browser writes the answer into the cached page of that day, as cells do today.

---

## 7. The responsible's name (P3)

- **Setting:** `AvailabilitySettings.ResponsibleLabel`, 1 to 30 characters, trimmed, *Responsible* by default. It sits in *Settings*, in a panel of its own: *The day's responsible is called [Responsible]*. It is part of the settings' JSON, so it needs no migration. One production use sets *Hero*; everyone else keeps the default.
- **The wording is *Responsible for x days***, with the label in front: *Responsible for 14 days*, *Hero for 14 days*. The texts never make a compound noun of it (no *Hero days*), never a plural, and need no article:
  - the plan: the column's header (*Responsible*), the cells' titles and labels, the warnings (*Responsible: nobody on a working day.*, *Responsible: doesn't work that day.*) and the hint under the table;
  - the *Status* tab: *Days as Responsible*, *Responsible for 14 days*, *Share as Responsible*;
  - `PlanFields`: *Share as Responsible*;
  - the board's *On duty* element: its default label for the first step. The element's own `dutyLabel` still wins;
  - not the rules' recipient: it stays *The responsible of the day* (`NotificationRecipients.ResponsibleLabel`), a constant the rules' list also sorts by in SQL (§15);
  - *Docs → Availability plan*: *the responsible (Settings can rename it)*.
- **What keeps *responsible*:** Cookie's prompts and messages, the API's `as` values, MCP and the audit log. They are a stable vocabulary that code and prompts rely on (decision 3).

---

## 8. A share of the days as responsible, per person (P3)

- **Field:** `users.ResponsibleShare`, an `int` from 0 to 500, 100 by default (migration).
  - On `PlanFields`, so in the *People* tab's dialog and in *Admin → Users*: *Share as Responsible [100] %*.
  - Help text: *100 is a full share; 50 is half as many days as someone with 100; 0 is never.*
- **It is a weight:** each person's target is their share divided by the sum of the shares of everyone in the plan. Equal shares give everyone the same target, and 30/30/40 works as well as 100/100/133. No total has to add up to 100.
- **The People tab** gets a *Share* column with both numbers: *100 % → 25 %*.
- **API:** `AvailabilityPeopleItem`, `AvailabilityPersonRequest` and `UserProfileRequest` get `responsibleShare`. `ApplyPlanAsync` checks 0 to 500.

---

## 9. The Status tab: what is counted

### 9.1 Rules for every number

- **Open days only:** a closed day (a weekend, or a holiday without hours: `PlanSnapshot.Closed`) counts for nothing. The fill leaves closed days empty, and a vacation dragged across a weekend must not count seven days a week (decision 2).
- **Today's settings:** the working week, the holidays and the types are read as they are now. If the week changes, past counts change with it. There is no history of the settings.
- **The people in the plan now**, in the plan's order:
  - days as responsible of people who have left the plan are counted on an *Others* line, outside the comparison;
  - a person who joined during the year is compared over the whole year. The 30-day and 365-day windows show it fairly soon.
- **Done and planned:** for the current year, days up to and including today are *done*, and later ones *planned*. A switch shows *Until today* (the default) or *Whole year*. A past year is all done; a future one is all planned.
- **Dates are in PNMT's zone**, as everywhere in the plan.

### 9.2 Days per person

- For each person and each month: the open days **per type** (the type of the person's cell), and the open days **without a cell**.
- **Meanings come from the types' checkboxes,** summed in the browser:
  - *On site*, *Home office*, *Work travel*, *Vacation*, *Holiday*, *Compensation*;
  - *Works* (the types whose checkboxes make them work, `works()`).

  A type with two checkboxes counts once under each meaning, and once as a type.
- **Days as responsible** and **days as fallback**, as planned (§9.3).

### 9.3 Days as responsible

- **A day as responsible** is an open day whose row names the person as its responsible (`availability_days.ResponsibleId`): **what was planned** (decision 5). When someone falls ill, the team changes the plan, and the plan is then what counts. Whether the person worked that day doesn't matter.
- Days as **fallback** are counted the same way, from `FallbackId`, and shown apart. They don't count towards the share.
- An open day without a responsible counts for no one. These days are shown as *Days without a Responsible*.
- **Windows:**

  | Window | Days |
  |---|---|
  | *Year* | 1 January to 31 December of the year chosen; done and planned (§9.1) |
  | *Last 365 days* | today −364 to today |
  | *Last 30 days* | today −29 to today |

- **For each person *i* in a window**, with `d` their days as responsible and `w` their share:
  - `D` = the sum of `d` over the people in the plan;
  - target `t_i` = `w_i / Σw`;
  - actual share `a_i` = `d_i / D`;
  - expected days `e_i` = `t_i × D`;
  - deviation = `d_i − e_i`, in days.

  The table shows *Responsible for 14 days*, the actual %, the target % and the deviation. With `D = 0`, it shows *Nobody was responsible in this window yet.*

### 9.4 The office, day by day

- For each open day of the year: each count column's count (as the plan's columns count), and whether it was below its minimum.
- The day's responsible, for *Days as Responsible by weekday*.

### 9.5 Ahead

The next 30 days (from today):

- open days without a responsible, or whose responsible doesn't work (the plan's red warning);
- days below a count column's minimum;
- empty cells that should be set (the plan's red cells);
- the next 14 days: who is away (vacation, holiday or compensation), with their days.

---

## 10. The Status tab: what it shows

The page, from top to bottom. Filters go in one row at the top: **Year** (from the first year with cells to next year), and for the current year **Until today | Whole year**.

| # | Part | Form | Why this form |
|---|---|---|---|
| 1 | **Numbers** | 5 stat tiles: *Days without a Responsible, next 30 days* · *Days below a minimum, next 30 days* · *Empty cells, next 30 days* · *Vacation days this year (done / planned)* · *Share of working days on site this year* | Single headline numbers are tiles, not charts. Each tile with a count links to the plan at the first such day (`?day=`, §5) |
| 2 | **Days as Responsible** | A **diverging bar per person**, centred on their target. It reaches right for more days than their share and left for fewer, and the figures stand beside it: *Responsible for 14 days · 18 % · target 20 % · −1.6 d*. A switch picks *Year · Last 365 days · Last 30 days*. Under it: *Others* and *Days without a Responsible* | The question is *who is ahead of or behind their share*: that is a delta to a target, so a diverging bar. Two hues and a grey midpoint, not red and green: being behind isn't an error |
| 3 | **Days as Responsible by weekday** | A small table, people × Mon–Fri, its cells shaded in one hue by count | It shows who always gets Mondays or Fridays: the fairness a total hides |
| 4 | **Days per person** | A table: one row per person; a column per type (its code chip in the header), then *Works*, *Responsible*, *Fallback* and *Empty*. A **thin segmented bar** per row shows the split by type. A select picks *the year* or one month. Totals row, and *CSV* | The figures are the point (HR asks for them), so a table, with the bar as an at-a-glance split beside the figures |
| 5 | **The team by month** | **Stacked columns**, Jan–Dec, segmented by type, in person-days. A *today* line divides done from planned. A select picks *Everyone* or one person | A split changing over time is a stacked bar chart: it shows the vacation summer and the quiet December |
| 6 | **By month** | A table: people × Jan–Dec + total, for one measure picked in a select: a type, a meaning (*On site*, *Vacation* …, *Works*), *Responsible* or *Fallback*. *CSV* | *Per month, per year* for one thing at a time, without a table 80 columns wide |
| 7 | **The office, day by day** | A **year calendar** (53 weeks × Mon–Fri). Each day is shaded in one hue by the count of a count column picked in a select (*On site* by default). A day below the minimum is outlined, with an icon in its tooltip. Closed days are left blank. A click opens the plan at that day | Magnitude over a grid is a heatmap; it shows the busy and the empty weeks at once |
| 8 | **The office by weekday** | Columns Mon–Fri: the average count of the same count column, over the open days of the year (done, or the whole year by the switch) | It answers *which day is the office day* |
| 9 | **The next 14 days** | A list: each person who is away, with their days and the type's chip | It is a list of names and dates, so it isn't a chart |

**Marks and colours:**

- **Types use their own colours,** as in the plan: the colour follows the type everywhere. The admin chooses them, so they can't be checked for colour blindness. Every chart that uses them therefore has a legend with the codes, a tooltip per mark, and a table beside it (parts 4 and 6). A 1 px outline in the muted text colour keeps pale cell colours visible.
- **The heatmaps, the weekday columns and the diverging bars** use a small chart palette in `components/colours.ts`: one sequential hue and a diverging pair with a grey midpoint. It is checked with the dataviz validator in light and dark mode. The status colours stay reserved for warnings, here only the below-minimum outline, which comes with an icon.
- **SVG by hand,** as `SampleChart.vue` (no chart library). Thin marks, rounded ends at the baseline, a 2 px gap between segments, recessive axes. Every bar, segment and cell has a hover tooltip and a larger hit target. Dark mode works through the CSS variables.
- **The components** go in `views/admin/availability/status/`: `StatusTab.vue`, `ShareDeviation.vue`, `MonthColumns.vue`, `YearCalendar.vue`, `WeekdayColumns.vue`, and `stats.ts` for the sums and scales, with Vitest tests.

---

## 11. The Status tab's API (P5)

`GET /availability/stats?year=2026` returns what the browser can't work out itself: the closed days, the responsible of each day and the windows. The browser sums the rest: months into a year, types into meanings, the weekday profiles.

```
AvailabilityStatsItem(
  int Year, DateOnly Today, string TimeZone, string ResponsibleLabel,
  IReadOnlyList<AvailabilityTypeItem> Types,
  IReadOnlyList<AvailabilityColumnItem> Columns,
  IReadOnlyList<AvailabilityStatsPerson> People,     // id, name, code, colour, share, target (0–1)
  IReadOnlyList<AvailabilityStatsMonth> Months,      // person × month: per type [done, planned], empty [done, planned],
                                                     // responsible [done, planned], fallback [done, planned]
  IReadOnlyList<AvailabilityShareWindow> Windows,    // Year, Last365, Last30: per person d, actual, expected, deviation;
                                                     // others; without a responsible; total open days
  IReadOnlyList<AvailabilityStatsDay> Days,          // each open day of the year: date, counts per column, belowMinimum,
                                                     // responsibleId
  AvailabilityStatsAhead Ahead)                      // next 30 days: without a responsible, belowMinimum, emptyCells, first day of each;
                                                     // away in the next 14 days
```

- **One read:** a single `AvailabilityPlan.ReadAsync` from the earliest to the latest day needed: from the earlier of 1 January and today −364, to the later of 31 December and today +30. That is at most about 760 days. The counts use `PlanSnapshot`'s own `Closed`, `Count` and `ResponsibleOn`, so the Status tab agrees with the plan and with Cookie. A dozen people over two years are about 9,000 cells, in memory.
- `year` ranges from 2000 to next year. Without it, the current year.
- **Who may call it:** as for `GET /availability/plan`. Someone who may not see the plan gets an empty answer, and the page says why, as the Plan tab does.
- `openapi.json` and `schema.d.ts` are regenerated (CI checks them).

---

## 12. Phases

| Phase | What | Notes |
|---|---|---|
| **P1** | The fixes of §3: the hours text, server errors only while their field is unchanged, the zone and clock in the plan and in Settings | Small. Can ship as **1.26.1** before the rest |
| **P2** | Month and year rows (§4); go to a day, with `?day=` (§5) | Browser only: no API change |
| **P3** | The name (§7) and the share (§8) | Migration: `users.ResponsibleShare` |
| **P4** | Notes (§6) | Migration: `availability_notes` |
| **P5** | The stats endpoint and its rules (§9, §11), with server tests | |
| **P6** | The Status tab (§10): tiles, tables with CSV, the charts | Needs P2's `?day=` and P3's name |
| **P7** | *Docs → Availability plan* (`AvailabilityDocs.vue`), e2e, release notes for 1.28.0, and this plan's *As built* | |

**Tests, along the way:**

- **Server** (`AvailabilityTests.cs`):
  - the hours text in both CI zones;
  - the label's check;
  - the share's range;
  - notes: who writes, edits and deletes; 20 a day; 300 characters; the author's name after the user is gone;
  - stats: closed days ignored; done and planned split at today; days as responsible follow the planned row, whether the person worked or not; fallback days apart; the three windows; target, expected and deviation, shares 0 and 50 included; *Others*; empty for someone outside the plan.

  Fake clocks start on whole seconds (as the test-clock rule says).
- **Vitest:**
  - `planRows`' year rows;
  - the server errors shown only while their field is unchanged;
  - the reported overlap example;
  - `stats.ts`: summing to meanings, the weekday averages, the scales, the diverging bar's ends.
- **e2e** (`availability.spec.ts`):
  - the overlap message gone after the fix;
  - a jump to a day outside the loaded ones, and back with *Today*;
  - a note added and shown with its name;
  - the renamed label in the header;
  - the *Status* tab with its tables and a tile that opens the plan.

---

## 13. Decisions

1. **The Status tab has the plan's audience** (revision 2: everyone). Every number on it can be worked out from cells that everyone who sees the plan already sees, so restricting it would protect nothing (§14, question 2).
2. **Only open days count.** Otherwise a vacation selected across a weekend counts seven days a week, and the fill never writes closed days anyway. Someone on work travel on a Saturday isn't counted. If that matters, closed days can get a column of their own later.
3. **The name is for people, not for code** (revision 2: Cookie stays as it is). Cookie's prompts, the API, MCP and the audit log keep *responsible*. Renaming those would change texts the AI and integrations rely on, every time the setting changes.
4. **Notes are written by those in the plan and by Edit.** *View* stays read-only, as its name promises. A note always carries its author's name, so it doesn't need the comment's stricter right.
5. **A day as responsible is the day as planned** (revision 2). When someone falls ill, people change the plan, so the plan is the record. Past and future are counted by the same rule.
6. **Times stay in PNMT's zone, and the zone is shown.** Working hours are the office's wall clock; converting them per browser would make the same plan read differently for each person. The page names the zone instead (§3.3).
7. **The share is a weight, not an absolute percentage.** Nothing has to add up to 100, a new person doesn't force everyone else's numbers to change, and 0 cleanly means never.
8. **Notes get a table of their own**, not a JSON column on `availability_days`. Each note has its own author, time and rights, and is edited and deleted on its own.

---

## 14. Revision 1's questions, answered

1. **Days as responsible:** count what was planned, because people adjust the plan when someone is ill (decision 5). The word is *Responsible for x days*; only one production use calls it *Hero*, and the label is a setting in the web UI (§7).
2. **Status tab audience:** everyone who sees the plan (decision 1).
3. **Cookie and the name:** Cookie keeps *responsible* (decision 3).
4. **The overlap message:** worked out in tests (§3.2).
5. **Production's `TZ`:** you check *System → Overview → Time zone*. If it says *UTC*, the hours entered so far mean UTC; `TZ=Europe/Zurich` fixes that and moves the other wall-clock rules with it.

---

## 15. As built

Built on 2026-11-26, for 1.28.0. Where it differs from the sections above, this section is right.

**The overlap message (§3.2), worked out in tests.** There were two causes; the first is what was reported.

1. **A saved period didn't take a change.**
   - Vue Query hands out its data read-only. `normalizeWeek` copied the week's array but kept the query's own period objects in the draft.
   - Typing into a period that was already saved changed the field, but not the draft. The field showed 11:30, while the draft kept 17:00.
   - So *08:00–11:30, 13:00–17:00*, made from the saved 08:00–17:00 and a period added with *Add*, was checked as 08:00–17:00 with 13:00–17:00: *Periods may not overlap.* The same 17:00 would have been sent with Save.
   - Periods added with *Add* are new objects and always worked; hence *sometimes*.
   - **Fix:** `normalizeWeek` copies each period. Holidays, types and count columns were already copied.
   - **Found by** the e2e test, whose fields showed 08:00–11:30 and 13:00–17:00 next to the message. `SettingsTab.test.ts` then showed the draft still holding 17:00.
2. **The server's last answer stayed** until the next Save or *Undo changes*, also after a real overlap was fixed.
   - **Fix:** `serverErrors.ts` keeps each field's messages together with the part of the draft it is about, as sent (`week.Monday`, `holidays.{date}`, `types.{i}`, `countColumns.{id}`, the sections). A message shows only while that part is unchanged.

- Both checks themselves were right all along: `periodProblems` passes *08:00–11:30, 13:00–17:00*, and so does the server (`The_hours_show_as_entered_and_periods_with_a_gap_save`).
- **Tests:**
  - `SettingsTab.test.ts` (Vitest) runs the whole sequence: a saved period's end changed, a period added, Save with a real overlap, the fix. It checks that the request carries what the fields show, and that the message goes.
  - `hours.test.ts` checks that `normalizeWeek` copies.
  - The e2e test *Settings: the overlap message goes …* does the same in the browser.

**The hours (§3.1):** `PlanSnapshot.Hours` formats `PeriodsOn`'s local times as they are, and a period that ends at the next midnight as `24:00`. The tests run in Europe/Zurich, and in America/New_York on CI. Cookie's staffing text reads the same method, so it is fixed too.

**The time zone (§3.3):**

- `AvailabilityPlanItem.TimeZone` and `AvailabilitySettingsItem.TimeZone` carry the zone.
- The toolbar shows `Europe/Zurich · Thu 26.11.2026 · 14:32` (`clockLine`, updated every 30 s). Its tooltip gives the offset, and the browser's own time when the browser is in another zone.
- A new date in PNMT's zone fetches the plan again.
- *Settings → Working hours* names the zone. When the zone is UTC, it warns that `TZ` isn't set.

**Month and year rows (§4):**

- `planRows` yields `{ kind: 'year' }` before January's month row.
- Every month row has the strong look that only January's had before. The year row is stronger still: the primary colour, a larger font, and a 3 px line on top.

**Go to a day (§5):**

- The field is `GoToDay.vue`, and the URL keeps the day as `?day=`.
- A day that isn't loaded gets a query of its own, `[...planKey, { day }]`, loading from 7 days before it to 53 days after.
- *Today* drops `?day=`. When today isn't loaded, it goes back to the default query.
- A jump outlines the row for 2 s (`tr.flash`), and a polite live region names the day.
- Moving to another query closes the notes and the selection.

**Notes (§6):**

- **Storage:** the table `availability_notes` (migration `AddAvailabilityNotesAndShare`). It has no foreign key to the user: a note keeps `AuthorName` when the user is gone.
- **API:** `POST /availability/notes`, `PUT` and `DELETE /availability/notes/{id}`, all audited as `AvailabilityChanged`.
- **Rights:** `EndpointAuditTests` lists the three endpoints as *signed-in*. The rights are checked in the code: everyone in the plan and *Edit* write notes; only the author changes a note; the author and *Edit* delete it.
- **Limits:** a note is one line of 1 to 300 characters, at most 20 a day.
- **In the table:** the cell shows the newest note as *name: text*, with *+N*. The **+** shows on hover, and faintly on an empty cell. A click opens a list under the cell, with *Add a note*, edit and delete.

**The name (§7):**

- `AvailabilitySettings.ResponsibleLabel` holds it: 1 to 30 characters, trimmed by the server, *Responsible* by default. *Settings* has a panel of its own for it, *The day's responsible*.
- **Where it shows:**
  - the plan's header, titles and warnings (*Responsible: nobody on a working day.*);
  - the hints under the table;
  - `PlanFields` (*Share as Responsible*);
  - the Status tab;
  - the board's *On duty* element, through `BoardOnDuty.ResponsibleLabel`. The element's own `dutyLabel` still wins.
- **Not changed:** the rules' recipient keeps *The responsible of the day*, and Cookie, MCP and the audit log keep *responsible*.

**The share (§8):** `users.ResponsibleShare`, 100 for everyone already there (the migration's default is 100, not 0).

- **Where it is set:** `PlanFields`, so both the *People* tab's dialog and *Admin → Users*.
- **On the People tab:** a *Share* column shows *100 % → 25 %*.
- **In requests:** a request without the share leaves it as it is, so *Move up* and *Move down* keep it.

**Status (§9 to §11):**

- **The endpoint:** `GET /availability/stats?year=` (`AvailabilityService.Stats.cs`), from one `ReadAsync`.
- **Its windows:** `yearToDate`, `year`, `last365` and `last30`. The page's *Until today | Whole year* switch picks between the first two.
- **Days as responsible** are counted as planned (decision 5): the row's `ResponsibleId`, whether the person worked or not. Fallback days are counted apart.
- **The browser side:** `status/stats.ts` sums and lays out, with Vitest tests.
- **The charts** are SVG and HTML by hand: `ShareDeviation.vue` (diverging bars), `MonthColumns.vue` (stacked by type, in the types' own colours with an outline), `YearCalendar.vue` (heatmap; a click opens the plan at that day) and `WeekdayColumns.vue`.
- **Colours:** `--pnmt-chart-*` in `design/base.css`. The diverging pair is blue #2a78d6 and orange #eb6834, with a grey midpoint, plus a 7-step blue ramp. Each has its own steps for dark mode, and the validator passed them on white and on slate 900.
- **CSV:** *Days per person* and *By month* download CSV with semicolons and a BOM, for Excel.

**Tests:**

- **Server**, in `AvailabilityTests.cs`:
  - the hours text;
  - the label;
  - the share, through the People tab and *Admin → Users*;
  - notes: their rights, their limits, and the author's name;
  - stats, in a month after every other test's days: a closed Saturday isn't counted, days away still count as planned, the targets are 2 : 1 for shares of 100 and 50, and the deviations add up.
- **Vitest:** `serverErrors.test.ts`, `status/stats.test.ts`, and new cases in `plan.test.ts` and `OnDuty.test.ts`.
- **e2e,** in `availability.spec.ts`: the clock; a jump to today +300 and back; the year row; a note under *Ada Admin*; the Status tab with CSV and a tile; the overlap sequence; and the renamed label in the header.
