# PNMT 1.10: Cookie's own notes, a reading level for logins, and the Pushover priorities as a setting

Version 1.10.0.

## What's new

- **Cookie's notes are its own** (docs/cookie-object-notes-plan.md §3). Until now Cookie appended a dated line to a host's or sensor's notes, the field people write in. Now its notes are rows of their own, shown in a panel *Cookie's notes* on the host's and the sensor's page: the date, the text, and a link to the case for *Cookie: Use*. Each note carries the areas of its sources, so a note drawn from a mail or a ticket is shown only to readers who may see those. A person with *Monitoring: Edit* removes a note; the audit records it (`CookieNoteRemoved`). Cookie reads its notes back in the case's brief and in `host_get` and `sensor_get`, as its own words, and is told not to repeat one. At most 20 notes live on one object (*Admin → Cookie → Actions*, *Notes on one host or sensor*). **A note needs *Monitoring: View* in Cookie's group,** not *Edit*, and the people's notes are never touched again.
- **A level *Details* in *Monitoring*,** between *View* and *Edit* (§4): what names people. Servers' and routers' logins, the access cases and notices about them, *This wasn't expected*, a server's event log, journal and SSH keys, and the access watch's tools need *Details* instead of *Edit*. *Edit* includes it, so a group with *Edit* loses nothing and a group with *View* gains nothing. The read-only MCP token reaches *Details* too, for a user who has it. **Cookie's group needs *Monitoring: Details* for the logins and never *Monitoring: Edit*;** the Rights tab under *Admin → Cookie* says so.
- **The Pushover priorities are a setting** (§5). The *Pushover* tab on the Notification manager says what a Low, Normal and High message is on the phone, with all five of Pushover's priorities. Emergency repeats until it is acknowledged, with the retry and expiry set there. Every Pushover PNMT sends follows it: the rules', Cookie's, the access watch's and MCP's strike notices; the tab lists who sends at which importance. Readers of the manager see it, *Monitoring: Edit* changes it, and the change is audited. The debug page's test Pushover takes every priority.

## Updating from 1.9

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.10.0`. It takes a backup first.

- **Two migrations:** `notification_settings` (one row, created on the first save) and `cookie_object_notes`. Both run at the start.
- **`Notifications__PushoverPriorityByImportance` is gone.** Until the first save the priorities are what the switch's default gave: Low −1, Normal 0, High 1. An installation that had set it to `false` sets the three priorities to 0 on the tab.
- **Cookie's group:** lower *Monitoring* from *Edit* to *Details* under *Admin → Groups*. Nothing does it by itself. With *View* only, Cookie writes notes but doesn't judge servers' and routers' logins, and the Access tab says so.
- **Groups with *Edit*** see no change. A group that should see logins without editing hosts gets *Details*.
- **Cookie's old lines in the people's notes stay as text;** nothing moves them.

## Good to know

- **Emergency receipts aren't read:** the phone repeats until the person acknowledges or the expiry passes; PNMT doesn't learn who acknowledged.
- **The priorities are per importance, not per sender.** Cookie's urgent messages, suspicious and alarming logins and MCP's strike notices are High, the rest Normal; only a Low sensor is Low. A row per sender is an open question of the plan (§9, decision 12).
- **The plan:** docs/cookie-object-notes-plan.md, with the decisions (§9) and what was built (§11).
