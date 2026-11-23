# Plan: typing the days in *Fill again*

| | |
|---|---|
| Status | Built (§8). Released in 1.20.1. |
| Date | 2026-11-23 |
| Scope | The **Days** field of *Fill again* in the availability plan: `PNMTD.WebUI/src/views/admin/availability/FillAgainDialog.vue`. |
| Goals | <ol><li>The first and the last day can be **typed**, besides being picked in the calendar.</li><li>A day that can't be read, or lies outside the window, **says so in the dialog**, before anything is sent.</li><li>The request stays the same: `from`, `to`, and the rest of the dialog unchanged.</li></ol> |
| Not in scope | The server: it already checks the window (§1.2). The custom ranges of Events and the audit log (§7). |

---

## 1. Today

### 1.1 The field

- **One `DatePicker` in range mode**, with `:manual-input="false"`: the text can't be edited, only the calendar picks.
- `min-date` is today, `max-date` today + 364 (`DAYS = 365`, a copy of `fillDays` in `plan.ts`), `date-format="d.m.yy"`. In PrimeVue's format `yy` is the four-digit year: `3.11.2027`.
- On opening, the whole window is chosen: today to the 365th day.
- `to` is `range[1] ?? range[0]`: one day is picked by clicking it once.

### 1.2 Sending

- `submit` emits `fill` with `from` and `to` as `yyyy-MM-dd` and **closes the dialog at once**. `PlanTab.fill` sends it.
- `AvailabilityService.FillAsync` checks `from >= today`, `to <= today + 364` and `from <= to`. Its messages are *"The fill writes the future only: from today on."*, *"At most 365 days ahead."* and *"Not before from."*
- A rejected request shows only after the dialog has gone.

## 2. Why not just turn manual input on

PrimeVue 4.5.5 (`primevue/datepicker/index.mjs`, `parseValue`, `isValidSelection`, `onInput`, `onBlur`) reads typed text in range mode like this:

- **The separator is exactly `" - "`.** `3.11.2027-4.11.2027` isn't read.
- **One typed day is never accepted.** A range needs two days, so one day has to be typed as `3.11.2027 - 3.11.2027`.
- **`yy` needs all four digits.** `3.11.27` and `3.11.` aren't read.
- **Days outside the window get through.** `isValidSelection` checks the order only when every day is selectable. When one isn't, it returns *valid*, so a typed `1.1.2020 - 2.1.2020` goes into the model past `min-date`. Only the server would catch it, after the dialog has closed.
- **Text it can't read is dropped silently.** `onInput` ignores it, and `onBlur` writes the last good value back into the field.
- **Enter sends the old value.** Type `5.12.27` over the last day and press Enter: the model still holds the default end, and the whole year is filled again.

The one switch would give a field that looks typeable but misreads silently. This plan builds the typing on top instead.

## 3. The design

### 3.1 Two fields: From and To

```
Days
┌ From ─────────────────┬───┐  ┌ To ───────────────────┬───┐
│ 23.11.2026            │ ▦ │  │ 22.11.2027            │ ▦ │
└───────────────────────┴───┘  └───────────────────────┴───┘
From today on, at most 365 days ahead: until 22.11.2027.
Type 3.11.2027, 3.11.27 or 3.11.
```

- **Two single-day `DatePicker`s side by side**, each with its calendar button and manual input on. The calendar works as it does today.
- `min-date` and `max-date` as now. **To's `min-date` is From**, so the calendar greys out the days before it.
- **The defaults are unchanged:** From is today and To the 365th day.
- **One day** is the same day in both fields.
- In single-day mode, PrimeVue checks `min-date` and `max-date` correctly. The range-mode hole of §2 is gone.
- **The hint names the last day** (*until 22.11.2027*) and the forms that can be typed.

### 3.2 What can be typed: `parseDay`

There is a new pure function in `src/format.ts`:

```ts
parseDay(text: string, today: Date): Date | null
```

It returns `null` when the text isn't a day. Whitespace is trimmed.

| Typed | Read as |
|---|---|
| `3.11.2027`, `03.11.2027` | 3 November 2027 |
| `3.11.27` | 3 November 2027: two digits are 20yy |
| `3.11.` or `3.11` | **the next 3 November on or after today** |
| `2027-11-03` | 3 November 2027 (ISO, as pasted from elsewhere) |
| `31.11.2027`, `29.2.2027`, `3.13.2027` | `null`: the day doesn't exist |

Notes:
- **`3.11.` without a year needs no guessing.** In a window of 365 days, from today, each day and month occurs at most once, so the next one on or after today is the only one in the window. When it falls outside the window, §3.4 rejects it. That happens to 29.2. when the window has no leap day. With a leap day, one other date is missing instead: from 1.1.2028, the window ends on 30.12.2028.
- **A day that doesn't exist** is caught by a round trip: build the `Date`, and compare its day, month and year with what was typed.
- **Only dots and ISO are read**, matching how dates are shown everywhere in the WebUI (`formatDay`). `3/11/2027` is ambiguous between countries and is not read.

### 3.3 When the text is read

- **On blur.** `DatePicker` emits `blur` with the typed text, before it writes its last value back into the field (`onBlur`). The handler calls `parseDay`:
  - **It's a day:** it is set as the model. The `modelValue` watcher then writes it back in the normal form, so `3.11.` becomes `3.11.2027`.
  - **It isn't a day:** the model becomes `null`, so the field is empty. The text is kept for the message of §3.4.
- **On submit,** whether by the button or by Enter in a field. Each field's current text is tracked with `@input`. Both texts go through `parseDay` first, then §3.4 checks them. **Enter sends what is shown, never the old value.**
- **Picking in the calendar** sets the model as it does today, and clears that field's message.

### 3.4 Messages in the dialog

Messages show under the field, as in the other forms: `:invalid` on the field and `<small class="field-error">`. The wording follows the server's.

| Field | When | Message |
|---|---|---|
| From, To | Typed text isn't a day | *"3.13.2027" isn't a day: type 3.11.2027, 3.11.27 or 3.11.* |
| From, To | Empty | *A day, please.* |
| From | Before today | *From today on.* |
| From, To | After the window | *At most 365 days ahead: until 22.11.2027.* |
| To | Before From | *Not before From.* |

- The checks live in a pure function, `fillDaysErrors(from, to, today)` in `plan.ts`. It returns `{ from?: string, to?: string }`, so they can be tested without mounting the dialog.
- **Fill again stays enabled** (§8): sending reads both fields, and while either has a message the dialog stays open and shows it. The dialog closes only when both days are valid. If the window moves on at midnight while the dialog is open, the server still says no.

## 4. Changes

| File | Change |
|---|---|
| `src/format.ts` | `parseDay` (§3.2). |
| `src/format.test.ts` | The table of §3.2. A year without digits across New Year: today 23.11.2026 reads `3.1.` as 3.1.2027 and `25.11.` as 25.11.2026. 29.2. with and without a leap day in the window. Whitespace. |
| `src/views/admin/availability/plan.ts` | `fillDaysErrors` (§3.4). |
| `src/views/admin/availability/plan.test.ts` | Each message of §3.4. The first and the last day of the window are both valid. From = To is valid. |
| `src/views/admin/availability/FillAgainDialog.vue` | `range` becomes `from` and `to`, with two `DatePicker`s. Blur and submit read the text (§3.3); messages show (§3.4); the hint names the last day. `DAYS` comes from `fillDays` in `plan.ts`, not its own copy. The emitted body is unchanged. |
| `docs/release-notes-<next>.md` | One line: *Fill again: the first and last day can be typed, for example 3.11. for the next 3 November.* |

No server changes, no change to `openapi.json`, no new package.

## 5. Steps

1. `parseDay` and its tests.
2. `fillDaysErrors` and its tests.
3. The dialog.
4. `tools/npm.sh run lint` and `tools/npm.sh test`.
5. Check in the browser:
   - type each form of §3.2 and press Tab;
   - type, then press Enter at once;
   - type a day that doesn't exist, the past, 23.11.2027, and a To before From;
   - pick a day in each calendar after typing;
   - one day (From = To);
   - reopen the dialog: the defaults return.

## 6. Risks

- **The blur order is PrimeVue's, not ours.** Typed text works because `onBlur` emits `blur` before it rewrites the field, and because the `modelValue` watcher rewrites the field when the model changes from outside. Both hold in 4.5.5. A PrimeVue update has to be checked against §3.3; step 5's checks cover it.
- **`onInput` already updates the model for complete `d.m.yyyy` text while typing.** That is harmless: blur and submit read the text again and win.

## 7. Later, if wanted

- **Events and the audit log** use the same range `DatePicker` with `:manual-input="false"` for their custom range (`EventsView.vue`, `AuditLogView.vue`). `parseDay` and the two-field pattern would carry over: there the window is the past (`max-date` today), so `3.11.` would read as the last 3 November on or before today.

## 8. As built

- **As planned:** `parseDay` in `format.ts`, `fillDaysErrors` and `typedDayForms` in `plan.ts`, the two fields in `FillAgainDialog.vue`, read on blur and on sending. The dialog takes `fillDays` from `plan.ts` instead of its own copy.
- **Fill again is never disabled**, unlike the first version of §3.4. A form's Enter does nothing while its submit button is disabled. A wrong day corrected and sent with Enter would have done nothing, because the correction is only read when sending. Sending reads, checks, and stays open with the messages instead.
- **The days are a `fieldset`** with the legend *Days*, From and To as labels inside it.
- **Tests:** `format.test.ts` (§3.2) and `plan.test.ts` (§3.4). `e2e/availability.spec.ts` covers a day that doesn't exist, the past, `d.m.` written out, To before From with Enter, and `d.m.yy` sent with Enter. It checks the request's `from` and `to`.
