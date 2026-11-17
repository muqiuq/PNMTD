# PNMT 1.14: Advanced sensors, and SSL certificate sensors

Version 1.14.2: 1.14.0 and the changes of 1.14.1 and 1.14.2 below.

**1.13 wasn't released on its own:** this release brings its SSL certificate sensors too. Their notes are [release-notes-1.13.md](release-notes-1.13.md); what they say about updating is folded in below.

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

- **SSL certificate sensors,** described in [release-notes-1.13.md](release-notes-1.13.md): a sensor for the certificate of any `host:port`, failing before expiry, once expired, and on a change it alarms on until a person accepts it; public CAs, private CAs on *Admin → Certificates*, and pinned self-signed certificates.

## 1.14.1

- **A test of the advanced sensors' evaluator** failed on Linux, at exactly a sensor's interval plus its grace: its fake clock started between two microseconds, which PostgreSQL doesn't keep, so the stored report looked a fraction of a microsecond late. Its clock starts on a whole second now. Nothing changes in the running app.
- **The demo seed** (`SEED=1`) failed between 22:00 and 23:00 UTC: it counted the knowledge base's page views 90 days back in steps of 24 hours, and across October's change of time two of those steps landed on one date. It counts back in dates now.
- **Updating from 1.14.0:** `./update.sh --tag v1.14.1`. No migration, nothing to configure.

## 1.14.2

- **The tests of the timespan sensors** (*one* or *all within a timespan*) failed in the last hour before midnight in the tests' time zone: they found "the next midnight" by rounding up to the next hour and adding the hours left to 24, which during the day's last hour gave the midnight after next. They take tomorrow's date now, place their events from that midnight rather than from the machine's clock, and evaluate the next day a minute after midnight, more than the day the interval needs. Nothing changes in the running app.
- **Updating from 1.14.1:** `./update.sh --tag v1.14.2`. No migration, nothing to configure.

## Updating from 1.12

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.14.2`. It takes a backup first.

- **Two migrations,** which run at the start; nothing existing changes:
  - `CertificateSensors`, four new tables: the certificate sensors' settings and state, the certificates they have seen, the private CAs, and which sensors trust them;
  - `AdvancedSensors`, five new tables: the advanced sensors' setting and state, their rules, their fields, the reports, and the charted values.
- **Nobody gets the new rights area *Certificates* by the update,** as with Cookie, Availability and MCP: admins have it; give *Certificates: Edit* to the group that sets up certificate sensors, and *View* to whoever should read the Certificate tab.
- **A reverse proxy** that passes only `/event` and `/read` to PNMT needs `/report` too (POST, bodies up to 256 KiB).
- **Nothing to configure.** The `Advanced__*` and `Certificates__*` settings (deploy/README.md, *Advanced sensors* and *SSL certificates*) are optional. The advanced sensors' rights are Monitoring's: *View* sees the reports, *Details* those that name people, *Edit* changes rules and deletes reports.

## Good to know

- **The reports are in the backups.** A report that carried a secret can be deleted on the *Data* tab, one or all; each deletion is in the audit log.
- **One report per sensor every 5 seconds;** a sooner one gets 429 with `Retry-After` and is neither stored nor judged.
- **Retention:** 500 reports per sensor (identical ones count once), 30 days, 50 MiB; 30 days of charted values. The newest two reports always stay.
- **The demo data** (`SEED=1`) has five: a nightly backup that is OK, an order service in the IETF format that warns, a disk report that fails, a payroll export that names people, and a label printer that fell silent.
- **The plans:** docs/advanced-sensor-plan.md, with the decisions (§15) and what was built (§17); docs/ssl-certificate-plan.md (§12, §15).
