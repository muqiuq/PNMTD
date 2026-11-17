# PNMT 1.12: notification rules to a user, a group, or the responsible of the day

Version 1.12.2: 1.12.0 and the changes of 1.12.1 and 1.12.2 below.

## What's new

- **A notification rule can tell a person PNMT knows, not only an address** (docs/notification-recipients-plan.md). *Send to* on a rule is now *An address*, *A user*, *A group* or *The responsible*. A user is reached at the e-mail address and Pushover key of their account (Account → Contact); a group tells every active member at the time of the send, each at their own details; the rule says which channels it uses, e-mail, Pushover or both. People without the chosen details are skipped and named on the rule's page.
- **The responsible of the day is a recipient.** The rule asks the availability plan at the moment of the send: the day's responsible if they work, else the fallback, else the plan's global fallback. When the plan names nobody (a closed day, nobody works), the rule's stand-in is told: a user or a group, whether or not they work. A rule alerts at any hour by default; *in working hours only* makes the night and the closed days the stand-in's, or nobody's.
- **The message says why the person got it:** *— Rule Storage on duty: to you as the responsible today*, *as a member of Storage team*, *as the stand-in for the responsible*.
- **A rule that reaches nobody is visible.** The rule's page shows *Reaches now* and *Last sent* (when, and to whom). The Notification manager's Coverage tab lists enabled rules that reach nobody right now and why; their sensors count as uncovered; the matrix flags their columns; System health counts them.
- **The address stays exactly as it was.** Same field, same validation, same providers, same masking for a Viewer, preselected in the form, and the API's default when the kind is left out. An address rule made in 1.12 is indistinguishable from one made in 1.11.
- **Whoever edits rules can pick users and groups** without being an admin: the picker lists names and whether a person has an address or a key, never the values. MCP's and Cookie's read tools name the people a rule reaches as *a user*, *a group*, *the responsible*.

## 1.12.1

- ***Add router* on Admin → MikroTik & WireGuard → Routers:** the host the router goes on, an existing one or a new one made right there with a name and a location, then the router form of that host, whose Setup tab shows the join script. Before, a router could only be added from a host's page. The button needs *Monitoring: Edit*, as the router form does.
- ***Add server* can make the host too:** *An existing host* or *A new host* in the dialog, for Windows and Linux alike; the token is for the new host, which is made once, when the token is. From a host's page the host stays preselected. Making a host needs *Monitoring: Edit*; without it the dialog is as before.
- **Updating from 1.12.0:** `./update.sh --tag v1.12.1`. No migration, nothing to configure.

## 1.12.2

- **The WireGuard gateway's container has a health check of its own:** `wg0` up and its SOCKS port answering. Before, Docker applied the image's check, which asks the app's `/healthz`, called the working gateway unhealthy, and `./update.sh` stopped at *container pnmtd-pnmtd-wg-1 is unhealthy* although the gateway ran fine. The check comes with `git pull`, before the image does.
- **The web UI's packages are checked for a compromise, not only for known vulnerabilities** (docs/webui-migration-plan.md §7.1): the image builds without a network once the packages are installed; every package version must be a week old by the registry's record, still on the registry, and carry the lock file's integrity there; the registry's signatures and build provenance are verified; and GuardDog's rules for malicious packages run over every package whenever they change. Nothing changes in the running app.
- **A browser test** matched a number by substring and failed whenever the clock contained it; it matches exactly now.
- **Updating from 1.12.1:** `./update.sh --tag v1.12.2`. No migration, nothing to configure.

## Updating from 1.11

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.12.2`. It takes a backup first.

- **One migration:** `NotificationRecipients` on `notificationrules`: the recipient kind, the user and the group (set to null when they are deleted), the channels, the hours, and the last delivery. Every new column has a default, so existing rules stay address rules reached as before; a v0.12 import needs nothing new. It runs at the start.
- **The audit** records a rule to people with a summary naming the person or group; the field changes are recorded as before. No new event types.
- **Nothing changes for existing rules,** their sensors, scopes, exclusions, or the Pushover priorities.

## Good to know

- **A rule to one user who has neither an address nor a key is refused** when it is saved: one person is a fact the editor can fix now. A group may be empty or unreachable, since membership changes; the page and the coverage say so.
- **A group rule tells every active member,** not only those who work today. The plan's judgement is the responsible kind's.
- **A deleted user or group leaves the rule in place,** shown as *removed*, reaching nobody until it is changed; the coverage lists it.
- **The plan:** docs/notification-recipients-plan.md, with the decisions (§11) and what was built (§13).
