# PNMT 1.7: Cookie watches who logs in

Version 1.7.0.

## What's new

- **The access watch** (docs/cookie-access-plan.md). It is off until an admin switches it on under *Admin → Cookie → Access*.
  - **What is watched:**
    - **servers with PNMT's agent:** console, RDP, SSH, WinRM, admins' network logons, and sudo and su;
    - **MikroTik routers:** WinBox, WebFig, the API, SSH, Telnet, FTP and the console;
    - **PNMT itself:** sign-ins to the web UI, MCP tokens switched on, and a token's first use from a new network on a day.
  - **Code compares each new login with the target's last 90 days:** the account, how, and from which network. What it has seen before stays quiet and costs nothing. What is new goes to Cookie as an *access* case.
  - **Cookie judges it:** *normal*, *unusual*, *suspicious* or *alarming*, with one sentence why. It reads the account's history, its other logins, the tickets, the knowledge base and who works today. **Code holds the verdict to the signals:** *alarming* needs a strong one, such as success after a burst of failures, a public address the account never came from, an SSH key PNMT doesn't know, a deactivated person's account, or one address on several targets.
  - **The contacts are told,** set on the *Access* tab as users or groups, for servers, routers or PNMT. **Each verdict has its own row:**
    - whether it notifies at all;
    - by e-mail, Pushover, or both;
    - who is reached outside working hours: nobody until they begin (*unusual* by default), the contacts who allow it (*suspicious*), or every contact (*alarming*).
  - **When Cookie can't judge** (stopped, paused, over budget), code sends its own notice for a strong signal after 10 minutes.
  - **Was this you?** The account's own person gets an e-mail with a link that works once, for 24 hours. A *No* makes the case alarming and tells the contacts at once. Opening the link answers nothing; only its buttons do.
  - **The answers:**
    - *Expected* closes the case, and *Remember this* keeps the same access quiet from then on, on this target, its location or everywhere;
    - *Not expected* keeps it open as an alert on the dashboard;
    - *This wasn't expected* on any login of a server's or router's *Access* tab opens a case for it;
    - a ticket only when someone asks Cookie for one.
  - **Each router has its own switch,** on its *Access* tab and on *Admin → Cookie → Access*. Switched off, its logins are still read and shown; nobody is told.
  - **New on the pages:**
    - a router's *Access* tab: its logins, who is logged in now, its users;
    - the verdict's badge on the servers' and routers' logins and in the audit log;
    - *Server accounts* on *Admin → Users*, which link a server's or router's account to its person.

## Updating from 1.6

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.7.0`. It takes a backup first.

- **The migration `AddAccessWatch`** adds two tables (`access_reviews`, `access_known`), new columns on Cookie's cases, the routers, the users and Cookie's runtime, and indexes on the logins and the audit log for the history.
- **Nothing changes on the routers.** The poll, every 5 minutes as before, also reads the log's login lines, the users and who is logged in, with the rights the `pnmt` user has. The join script stays as it is.
- **To use the watch:**
  - switch it on and add at least one contact on *Admin → Cookie → Access*;
  - Cookie's group needs ***Monitoring: Edit*** to read servers' and routers' logins, and ***Audit log: View*** for PNMT's sign-ins. Without them, only code's notice for strong signals works, and the tab says so;
  - *Was this you?* needs `PublicBaseUrl` in `.env`, so the e-mail can link to PNMT.
- **The agents' files** come from the GitHub release `sensor-1.7.0`: the same agents with the new version, so the servers' pages show older agents as outdated until they are updated (`pnmt-agent update` on Linux, the new installer on Windows).

## Good to know

- **The first 14 days of a target are a learning period,** in which only strong signals count. Servers and PNMT already have their history; a router's starts with 1.7.0's first poll.
- **Mute doesn't silence the watch:** a maintenance window is when a vendor logs in, and also when nobody would notice someone else. *Remember this* and known access are how it gets quiet. A suppress of the channel *access* stops *unusual* and *suspicious* notices for a while, never *alarming* ones.
- **The router's login lines** were checked against RouterOS 7.13. A line that matches nothing is counted on the router's *Access* tab.
- **Not yet in the user documentation:** the access watch has no section on the Documentation page yet; the plan describes it.
