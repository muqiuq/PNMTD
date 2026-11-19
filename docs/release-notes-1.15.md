# PNMT 1.15: 3rd Party Connectors

## What's new

- **A new area, *Integrations → 3rd Party Connectors*** (docs/connectors-plan.md). A connector lets an outside system run one thing on one server through PNMT, and nothing else. Typical callers are a shop's back office, a ticketing system or a scheduler.
  - **Two types:** *Windows Server Script* and *Linux Server Script*. A connector runs a script, or one function in it, on one server with the PNMT agent, as an account the server allows.
  - **The target is text,** filled by selectors and linked to nothing: the server's name, the connector's own copy of the script, the function and the account. *From the library…* and, on Windows, *From the server…* copy a script in. Changing the library or joining an agent again changes no connector. When the library's script of the same name differs, the overview says so, with *Compare…* and *Take vN…*.
  - **Every parameter is exposed, fixed or omitted:**
    - *exposed:* the caller passes it, optionally checked by a pattern that must match the whole value;
    - *fixed:* PNMT passes the admin's value;
    - *omitted:* the script's default applies.

    One bad argument refuses the whole call, with every problem listed and the pattern named. Line breaks are allowed only in an argument marked multi-line. Secret parameters are never fixed, and a caller's secret is never stored.
  - **Wait or ticket:** the caller waits for the outcome (up to 110 s), or gets a ticket at once and asks later. A waiting caller never loses a call: past the wait, it gets its ticket. The output goes back only when the connector says so.
  - **Callers:** each outside system has one key (`pnmt_cx_…`), which can be shown again, and each display is in the audit log. It also has the networks it calls from (a single address is a /32 or /128, and at least one network is needed) and the connectors granted to it. Rotating a key stops the old one at once.
  - **The review:** optionally, the typesafe model (Jev) checks each call against the connector's purpose and the admin's own yes-or-no questions. Per connector, a call is refused or runs unreviewed when the review can't run. The caller hears which check refused, never how close it was.
  - **One run per script and server at a time,** whoever starts it. A connector's calls wait their turn, oldest first, and a run from PNMT is refused while the script runs there. At most `Connectors__MaxRunningCalls` (10) connector calls run at once.
- ***Calls*, the area's main view,** shows every active and processed call:
  - what each waits for, its place in the line, the review per question, the run with its output;
  - *Cancel…* while a call is queued, *Redact…* once it is final.

  The newest 100 000 calls are kept.
- **For the callers' developers:** each connector's *Usage* tab has its URL, its arguments and examples in curl, PowerShell and Python. The *Documentation* tab has the whole API (docs/connectors-api.md): `Idempotency-Key` for safe retries, dry runs, and `GET /connector/v1` to list a key's connectors.
- **A system sensor, *3rd Party Connectors*,** comes with the first connector. It fails with **433** when a valid key calls from outside its caller's networks, the review refuses a call, or an address is blocked for wrong keys. It returns to **233** after `Connectors__QuietMinutes` (60).
- **Functions in scripts,** for connectors and the run dialog alike:
  - **PowerShell:** a script's top-level functions are listed with their own parameters and help, with two new findings: top-level code that a function call runs first, and a mandatory script parameter that makes the functions uncallable.
  - **Bash:** the `# pnmt:` header declares functions under `functions:`, with the same keys as the script. It has two new findings: a declared function that isn't defined, and no `BASH_SOURCE` guard.
  - **The run dialog** has *Function*. PNMT never sends a function to an agent that can't call one; it refuses the run instead, because an older agent would run the whole script.
- **Runs whose result never comes** are closed as failed after their timeout and 5 minutes: "No result from the agent within the timeout". This applies to every run, so the run dialog no longer polls such a run for ever, and the script's turn on its server is free again. A result that arrives later still counts.
- **The script runs' history** shows *connector …* as who ran a script, and the function with the script.
- **`Proxy` takes a list,** comma-separated, of `docker`, addresses and networks (`Proxy=docker,10.0.0.5`). A bad entry now stops the start with a message naming it.

## Fixed

- **Behind a reverse proxy in Docker, PNMT saw the proxy's address for every request.** This affected the audit log, the sign-in lockout, the agents' join limit, MCP's *last used from* and the access watch. `Proxy=docker` trusted a range that contained no Docker address (`::ffff:172.16.0.0/12` became `::/12`). It now trusts 172.16.0.0/12, as the README says.

## Updating from 1.14

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.15.0`. It takes a backup first.

- **One migration, `Connectors`,** runs at the start:
  - six new tables: the connectors, their callers, the grants between them, a script connector's target and arguments, and the calls;
  - two new columns on the script runs: the connector whose call started a run, and the function it called.

  Nothing existing changes. At the first start, the bash scripts' headers in the library are read again once, so they list their functions.
- **Nobody gets the new rights area *3rd Party Connectors* from the update.** Admins have it. *Manage* is as strong as running scripts on servers, for the callers it lets in: give it with care. *View* sees every call and its output.
- **A reverse proxy** that passes only some paths to PNMT needs `/connector/` too (POST and GET, bodies up to 64 KiB). Its read timeout must be above 110 s for callers in wait mode.
- **Check `Proxy`** if PNMT runs behind a proxy in Docker: from 1.15 it works as documented. A caller's networks are only as good as the address PNMT sees. Behind a proxy on the same host, bind PNMT to `127.0.0.1` (`PNMTD_BIND_ADDRESS`).
- **Functions need the agents of 1.15 (`sensor-1.15.0`).** Older agents keep working for everything else, including connectors that run a whole script. Update them from *Admin → Servers → Versions*.
- **Nothing to configure.** The `Connectors__*` settings are optional; see deploy/README.md, *3rd Party Connectors*.

## Good to know

- **The server decides last.** A connector can't run what the agent's window (Windows) or `/etc/pnmt-agent/config.yaml` (Linux) refuses. The connector's page warns beforehand.
- **Calls with a wrong key are never stored,** so nobody can fill the table. Twenty wrong keys from one address within 10 minutes block it for 15.
- **A caller's secret arguments** wait in memory while their call is queued. A restart loses them, and such a call expires rather than running without them.
- **The calls are in the backups** with their arguments (never a secret one) and their runs' output. A call can be redacted once it is final.
- **The demo data** (`SEED=1`) has two connectors, *Shop: flush cache* (Windows, wait mode, with the review) and *Backup: start job* (Linux, ticket mode). It has two callers, one of them allowed from anywhere, and calls of each kind.
- **The plan:** docs/connectors-plan.md, with the decisions (§18) and what was built (§21).
