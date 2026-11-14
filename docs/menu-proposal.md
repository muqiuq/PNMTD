# Proposal: the menu

**Built** (2026-11-14), with the defaults below: *Devices* and *Admin* fold and start folded.

## Today

| Group | Entries |
|---|---|
| Monitoring | Dashboard, Decisions, Hosts, Locations, Events, DNS zones, Mail inputs, Mail log, Notification rules, Notification manager, Uplink (11) |
| Knowledge | Knowledge base |
| Integrations | Redmine, Zabbix |
| Admin | Key values, Users, Groups, Audit log, UDP nodes, MikroTik & WireGuard, Servers, SSH keys, Diagnostics, Location fields, Knowledge base, System health, AI, Debug (14) |
| Help | Documentation |

What's wrong with it:

- **Too long for the screen:** an admin sees 29 entries, and the last ones are cut off at 900 px.
- **Two mixed groups:** *Monitoring* mixes what is looked at daily with what is set up once. *Admin* mixes devices with settings.
- **Duplicates:** *Knowledge base* is there twice. Five icons are used twice: Dashboard and Notification manager, Hosts and MikroTik, Key values and SSH keys, Decisions and Mail log; the book is used three times, for both *Knowledge base* entries and *Documentation*.

## Proposed

| Group | Entries | Changes |
|---|---|---|
| **Overview** | Dashboard, Decisions, Events | new group |
| **Monitoring** | Hosts, Locations, DNS zones, Uplink | |
| **Alerts** | Notification rules, Notification manager | from *Monitoring* |
| **Mail** | Mail inputs, Mail log | from *Monitoring*; `Mail__HideMenu` hides the group |
| **Knowledge** | Knowledge base | |
| **Integrations** | Redmine, Zabbix | |
| **Devices** | Servers, MikroTik & WireGuard, UDP nodes, SSH keys, Diagnostics | from *Admin* |
| **Admin** | Users, Groups, Audit log, System health, AI, Location fields, Knowledge base settings, Key values, Debug | *Knowledge base* renamed to *Knowledge base settings* |

- **Groups fold,** and the drawer remembers which are open. *Devices* and *Admin* start folded.
- **Documentation moves to the user menu** (top right), so *Help* goes.
- **Each icon is used once.**
- **Rights decide as before:** a group shows only the entries the user may open, and disappears when none are left.

Only `layouts/navigation.ts` and the drawer change. Pages, routes and rights stay as they are.

## Decided

1. *Devices* keeps its *Manage* rights: nobody sees more than before.
2. *Alerts* and *Mail* stay two groups.
3. Every group folds; *Devices* and *Admin* start folded. A folded group still shows the entry of the page one is on, and the icon rail shows every entry.
