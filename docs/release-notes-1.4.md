# PNMT 1.4: groups and rights, the audit log, Redmine and Zabbix

Version 1.4.0.

## What's new

- **Groups and rights instead of roles.** A user is an admin, with every right, or has what their groups give: a level in each of 16 areas (Monitoring *View* or *Edit*, Commands *Run*, Mail *Read*, Locations and the knowledge base *View*, *Edit* or *Manage*, Redmine *View* or *Link*, Zabbix *View* or *Details*, and *Manage* for UDP nodes, MikroTik, Servers, SSH keys, Diagnostics, AI, System; Audit log *View*).
  - A member of several groups has the highest level any of them gives. There are no exceptions for one user and no rights on single objects.
  - *Admin → Groups* edits a group's rights and members; *Admin → Users* shows each user's effective rights, and *Account → Security* one's own.
  - A refusal says which right is missing.
- **The audit log records everything.** *Admin → Audit log* (it was the auth log) lists every change, sign-in and refusal: who (a user, the command line, an agent or PNMT itself), when, from where, what it was about, and each field's old and new value. Secrets show only that they changed.
  - Filters by category, event, outcome, who, time, object and request, all in the address; *Download CSV* exports what the filters show.
  - Hosts and sensors have a *History* tab with their own entries.
  - The detail column uses the whole width on large screens; it was cut at 24 rem.
- **Redmine, optional** (5.1 or later): *Integrations → Redmine* shows the tickets of the projects you name.
  - An overview of the most important ones: urgent, new and unassigned, overdue, linked to a problem in PNMT, without an update for 14 days, recently updated. A search with filters, where a number opens the ticket.
  - **Every ticket has two buttons:** *Details*, PNMT's full page of the ticket with its description, history and links, and *Redmine*, the ticket in Redmine.
  - **Tickets can be linked** to hosts, sensors, locations and knowledge base pages, which show them in a *Tickets* panel, with *Link ticket…* and *New in Redmine* (Redmine's form, prefilled). A failing sensor with an open linked ticket shows it, on the dashboard too: someone is on it.
  - The global search finds `#1234`.
- **Zabbix, optional** (6.0 or later, tested with 6.0, 7.0 and 8.0): *Integrations → Zabbix*.
  - An overview of the open problems by severity, and by host group or a tag (`class`, `component`, `scope`, …), with the hosts at a glance.
  - The problems with filters, each opening to its description and acknowledgements; the hosts, and a page per host with its problems, latest data, inventory and links into Zabbix.
  - **Zabbix hosts become sensors:** attached to a PNMT host (by hand, from the host's page, or in bulk from *Suggestions*, which match by name and address), a Zabbix sensor fails from a severity up (Average by default) and notifies like any other sensor. A PNMT host can have several; a Zabbix host is attached once.
- **Two new system sensors,** *Redmine* (429) and *Zabbix* (430), on the host *PNMT*: they fail once when the integration stops answering, instead of every sensor at once. *Admin → System health* shows both integrations and tests them.

## Updating from 1.3

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.4.0`. It takes a backup first. The migrations run when the new image starts.

- **Nobody's access changes.** The update turns the roles into the flag and two groups, once: *Admin* becomes the admin flag, *User* the group *Operators*, *Viewer* the group *Viewers*, each with exactly what the role allowed. Change the groups afterwards, or add your own.
- **The scripts take groups instead of a role:** `./add-user.sh <name> --groups Operators` or `--admin`; `--role` is gone, and so is the command line's `users set-role` (now `users admin <name> on|off` and `users groups <name> <groups>`). `./list-users.sh` shows the admin flag or the groups.
- **The audit log keeps its old entries** (the auth log's) and keeps entries for `Audit__KeepDays` (400) days and at most `Audit__MaxEntries` (500,000), instead of 10,000. Both are optional in `.env`.
- **Redmine and Zabbix are off** until `.env` has their settings. `deploy/.env.example` has both blocks, and [deploy/README.md](../deploy/README.md) the read-only accounts they need: for Zabbix a user role allowing five API methods, a user group with read access to the host groups PNMT should see, and an API token.
- **`./info.sh`** shows whether Redmine and Zabbix are on, and where.

## Good to know

- **PNMT changes nothing in Redmine or Zabbix.** It reads them with a key or token you give it, which never reaches a browser or a log.
- **Viewers see Zabbix but not Redmine:** tickets hold customers' and colleagues' text, as mails do. Give a group Redmine *View* when that is wanted.
- **If Zabbix also mails PNMT** through a mail input, both alert for the same problem: retire the mail rule once the host's Zabbix sensor is in.
