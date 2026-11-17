# PNMT 1.14: Advanced sensors

## What's new

- **A sensor of the type *Advanced (JSON)* takes whatever an app has to say** (docs/advanced-sensor-plan.md). The app POSTs a JSON object to `/report`, the sensor's write token in the `Authorization: Bearer` header, as often as it needs to:
  - any object up to 256 KiB, nested as deep as 32 levels, gzip accepted;
  - `/report/<token>` for tools that can't set a header;
  - the answer is 202 with a preview of the verdict, and `?dryRun=true` tries without storing anything.

  `/event` and `/read` are unchanged, and work for the new type too. The reference for app developers: docs/advanced-report.md.
- **The report shows readably with no setup,** on the sensor's new *Data* tab:
  - nested objects in the app's order;
  - lists of objects, and objects of objects of the same shape, as tables: `jobs`, the IETF health format's `checks`, Spring Boot's `components`, ASP.NET's `entries`;
  - times in your zone with *3 h ago*, health words coloured, booleans as ✓ and ✗;
  - a filter, *changed since the previous report*, and the raw JSON.

  A history keeps the reports, identical ones folded into one row with a count, and compares any report with the one before it.
- **Rules decide whether all is well,** on the new *Rules* tab, without code. A rule is a path, an operator and a value:
  - comparisons, text, patterns, lists, kinds, health words, ages (*the backup finished within 26 h*), counts, and changes since the previous report;
  - one value, or every element of a list: all, any or none of them;
  - a rule fails, or only warns (296, the sensor stays OK), and says what a missing value means.

  Pick a value on the *Data* tab and *Add rule…* writes the path and suggests the condition. The editor shows, while you type, what the rule makes of the last report; *Try* runs all the rules against a stored report or pasted JSON. Rules copy from one sensor to another.
- **Fields** give chosen values a label, a format (bytes, durations, percent, dates) and a unit, a card on the overview, and a chart of 24 hours, 7 or 30 days, with the rules' thresholds drawn in.
- **One evaluator judges every report,** in the order they arrived and as of when each arrived, within a second of receipt. It writes an event only when the verdict changes: a failure that lasts one report still leaves its two events, and free space falling from 3.2 to 3.1 GB within the same failure notifies nobody again. Age rules fail on time without a report, and a sensor with an interval turns 460 when its app falls silent.
- **Four new codes:** 295 every rule holds, 296 only warnings, 495 a rule fails (the message names the rules and their values), 496 the report can't be read (not JSON, too large, too deep, a key twice).
- **Data that names people:** a sensor marked so shows its reports and values only to *Monitoring: Details*, and its events name the rules, not the values.
- **MCP and Cookie:** `advanced_sensor_get` (the verdict, the rules and their outcomes, the fields, and the report unless it names people and you lack *Details*), and `sensor_create` for the new type. Rules are made in PNMT.

## Updating from 1.13

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.14.0`. It takes a backup first.

- **One migration:** `AdvancedSensors`, five new tables: the sensors' setting and state, their rules, their fields, the reports, and the charted values. Nothing existing changes. It runs at the start.
- **A reverse proxy** that passes only `/event` and `/read` to PNMT needs `/report` too (POST, bodies up to 256 KiB).
- **Nothing to configure.** The `Advanced__*` settings (deploy/README.md, *Advanced sensors*) are optional. The rights are Monitoring's: *View* sees the reports, *Details* those that name people, *Edit* changes rules and deletes reports.

## Good to know

- **The reports are in the backups.** A report that carried a secret can be deleted on the *Data* tab, one or all; each deletion is in the audit log.
- **One report per sensor every 5 seconds;** a sooner one gets 429 with `Retry-After` and is neither stored nor judged.
- **Retention:** 500 reports per sensor (identical ones count once), 30 days, 50 MiB; 30 days of charted values. The newest two reports always stay.
- **The demo data** (`SEED=1`) has five: a nightly backup that is OK, an order service in the IETF format that warns, a disk report that fails, a payroll export that names people, and a label printer that fell silent.
- **The plan:** docs/advanced-sensor-plan.md, with the decisions (§15) and what was built (§17).
