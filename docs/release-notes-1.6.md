# PNMT 1.6: the MCP server, and signing in once a month

Version 1.6.0. Draft: package eight is being built (docs/mcp-plan.md §19).

## What's new

- **PNMT's MCP server** (docs/mcp-plan.md, package eight). It is off until `.env` has `Mcp__Enabled=true`.
  - **Your own AI client** (Claude Code, VS Code, Cursor) reads and works in PNMT at `/mcp`, **with your rights, capped:** never the admin flag, never a *Manage* level, never approvals or PNMT's settings.
  - **Two tokens per person,** on the dashboard's *MCP access* card. **Read-only** stays on until a week after you were last in PNMT. **Read & write** stays on for 12 hours and needs 2FA on your account. A token's value is shown once, when it is made; regenerate it when it is lost.
  - **About 65 tools:** hosts, sensors and their events, the dashboard, the search, locations and their information, the knowledge base, Redmine and Zabbix, UDP nodes and MikroTik routers, servers with an agent, and Cookie's cases. With read & write: creating and changing hosts, sensors, locations, pages and mail input rules, linking tickets, UDP commands, polls, backups and RouterOS commands on routers.
  - **Deleting needs you each time:** *Allow deleting for 10 minutes* on the card opens a window for at most 20 objects.
  - **Talking to Cookie:** `cookie_ask` sends Cookie a message in your name. Jev screens it first; a read-only token can only ask questions.
  - **Nothing secret comes out:** no tokens, keys or passwords of sensors, routers or agents, and no page or field marked sensitive.
  - **Everything is logged:** *Admin → MCP → Log* keeps the newest 10,000 requests with their arguments and results; what changed is in the audit log, field by field, as *via MCP*. *Admin → MCP → Documentation* lists every tool.
  - **Strikes:** 3 refused requests within 24 hours block a token and alert every admin by Pushover and e-mail.
- **Signing in:**
  - **the password at least every 30 days;**
  - **the 2FA code once a day,** asked for over the page you are on;
  - **both renew at 04:00** in PNMT's time zone, and are set in `.env` (`Auth__LoginDays`, `Auth__TotpDays`, `Auth__ResetHour`; 0 turns a rule off);
  - sessions from before the update go on, and the first daily code is asked for at the next 04:00.

## Good to know

- **Offer `/mcp` on the internal name only,** as the UI and `/api/`. Behind nginx it needs `proxy_buffering off`.
- **A new area, *MCP*** (*View*: the log and everyone's tokens; *Manage*: turning a token off), is given to no group by the update. Admins have it.
- **`./info.sh`** shows whether MCP is on.
