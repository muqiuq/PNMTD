# PNMT 1.6: the MCP server, and signing in once a month

Version 1.6.0. **It is also the release of 1.4 and 1.5,** which weren't released on their own: read [docs/release-notes-1.4.md](release-notes-1.4.md) (groups and rights, the audit log, Redmine and Zabbix) and [docs/release-notes-1.5.md](release-notes-1.5.md) (Cookie and the availability plan) too.

## What's new

- **PNMT's MCP server** (docs/mcp-plan.md, package eight). It is off until `.env` has `Mcp__Enabled=true`.
  - **Your own AI client** (Claude Code, VS Code, Cursor) reads and works in PNMT at `/mcp`, **with your rights, capped:** never the admin flag, never a *Manage* level, never approvals or PNMT's settings.
  - **Two tokens per person,** on the dashboard's *MCP access* card. **Read-only** stays on until a week after you were last in PNMT. **Read & write** stays on for 12 hours and needs 2FA on your account. A token's value is shown once, when it is made; regenerate it when it is lost.
  - **74 tools:** hosts, sensors and their events and history, the dashboard, the search, locations and their information, the knowledge base with its revisions and tags, notification rules (recipients masked), the uplink, DNS zones, mail input rules and the mail log, Redmine and Zabbix, UDP nodes, MikroTik routers and their configuration changes, servers with an agent and diagnostics, and Cookie's cases, decisions and trace. With read & write: creating and changing hosts, sensors, locations, pages and mail input rules, linking tickets, UDP commands, polls, backups and RouterOS commands on routers.
  - **Deleting needs you each time:** *Allow deleting for 10 minutes* on the card opens a window for at most 20 objects.
  - **Who did it:** messages over MCP are labelled *via MCP* in Cookie's chat, and a refused one shows why; a page's history marks revisions made over MCP; *Admin → Users* shows when each person was last online.
  - **Talking to Cookie:** `cookie_ask` sends Cookie a message in your name. Jev screens it first; a read-only token can only ask questions.
  - **Nothing secret comes out:** no tokens, keys or passwords of sensors, routers or agents, and no page or field marked sensitive.
  - **Everything is logged:** *Admin → MCP → Log* keeps the newest 10,000 requests with their arguments and results; what changed is in the audit log, field by field, as *via MCP*. *Admin → MCP → Documentation* lists every tool.
  - **Strikes:** 3 refused requests within 24 hours block a token and alert every admin by Pushover and e-mail.
- **Signing in:**
  - **the password at least every 30 days;**
  - **the 2FA code once a day,** asked for over the page you are on;
  - **both renew at 04:00** in PNMT's time zone, and are set in `.env` (`Auth__LoginDays`, `Auth__TotpDays`, `Auth__ResetHour`; 0 turns a rule off);
  - sessions from before the update go on, and the first daily code is asked for at the next 04:00.

## Updating from 1.3

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.6.0`. It takes a backup first. The migrations of 1.4, 1.5 and 1.6 run when the new image starts; the updating notes of 1.4 and 1.5 apply as well.

- **Nobody is signed out by the update.** The first daily 2FA code is asked for at the next 04:00, and the password 30 days after that.
- **MCP stays off** until `Mcp__Enabled=true` (`.env.example`, deploy/README.md "MCP"). Behind nginx, `/mcp` gets a location of its own on the internal server only ([deploy/nginx-reverse-proxy.md](../deploy/nginx-reverse-proxy.md)).
- **The agents' files** come from the GitHub release `sensor-1.6.0`, fetched by PNMTD as before. Its agents are built from the same code as 1.3.0's, but carry the new version: until they are updated (`pnmt-agent update` on Linux, the new installer on Windows), the servers' pages show them as outdated.

## Good to know

- **Offer `/mcp` on the internal name only,** as the UI and `/api/`. Behind nginx it needs `proxy_buffering off`.
- **A new area, *MCP*** (*View*: the log and everyone's tokens; *Manage*: turning a token off), is given to no group by the update. Admins have it.
- **`./info.sh`** shows whether MCP is on.
