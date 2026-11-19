# Plan: 3rd Party Connectors: an API for outside systems, starting with scripts on Windows and Linux servers

| | |
|---|---|
| Status | **Built (2026-11-19): C0 to C6, with the deviations of §21. Released in 1.15.0; 1.15.1 keeps a busy server's results from being dropped (§21).** **Reviewed once (2026-11-18):** a right of its own (D2); at least one network (D3); per connector, whether a call runs when the review can't (D4); plain HTTP works (D5); the target as text, filled by selectors, with no links into the library, the accounts or the agents (D6); and *Calls*, every active and processed call with the newest 100 000 kept, as the area's main view (D16, D19). **Reviewed twice (2026-11-18):** calls are queued, and **the calls table is the queue**: the endpoint checks and stores, and a background worker does the rest (D7, D20); line breaks in values when an argument allows them (D8); the output goes back only when a connector says so (D10); the pattern is in the error message (D11); one run per script and server at a time (D15). **Reviewed a third time (2026-11-18): one key per caller** (D9). **Since:** at most `Connectors__MaxRunningCalls` (10) connector runs at once over all connectors (D22). A caller is an entry of its own, with its key, its networks and the connectors granted to it (§6.1, D21). |
| Date | 2026-11-18 |
| Scope | A new area, ***3rd Party Connectors***. **A connector is a narrow door into PNMT for one outside system and one purpose.** An admin adds a connector and picks its type. The first two types are ***Windows Server Script*** and ***Linux Server Script***. For those, the admin fills in **the target as text**: the server's name, the connector's own copy of the script, optionally ***a function in the script***, and the account. **Selectors fill the fields** from what PNMT knows (servers, library scripts, scripts registered on a server, functions, accounts), but nothing is linked: changing the library or joining the agent again doesn't change or break a connector. The admin then decides each parameter: the caller passes it (*exposed*, with an optional regular expression), PNMT sets it (*fixed*), or it is left out. **The endpoint only checks a call and stores it. A background worker takes it from the table:** it reviews the call, finds the server, waits while the same script runs there already, starts the run and records the outcome. The caller either **waits for the outcome** or gets **a ticket** and asks for the outcome later. A call can be **reviewed by the typesafe model** (TypeSafe's Jev) against checks the admin writes. **The outside systems are *callers*,** each with **one API key that can be shown again**, **the networks it calls from** (a single address is a /32 or /128), and **the connectors granted to it**. One key opens all of a caller's connectors, and no other. The calls run through the existing script pipeline: the agents' local rights stay final, the arguments are never pasted into a command line, and secrets are encrypted to the agent. **The area opens on *Calls*:** every active and processed call of every connector, with the newest 100 000 kept. |
| Goals | <ol><li>**An outside system can do one thing on a server, and nothing else:** one server, one script text, one function, with the arguments the admin allows (R1–R6, R10).</li><li>**Every argument is checked before anything runs.** One bad argument refuses the whole call (R5).</li><li>**The caller chooses how to get the outcome:** wait for it, or take a ticket (R4).</li><li>**A stolen key is not enough:** the caller's networks, its grants, the limits and, optionally, the review stand behind it, and a key used from elsewhere is reported (R7, R9, R14).</li><li>**Nothing new on the agents' side of trust:** a connector can't run what the server's own policy refuses (R15).</li><li>**Everything is on record:** each call, its review, its run and its output (R12).</li><li>**Room for more types:** callers, review, limits, tickets and history are shared, and only the target is per type (§3.2, §19).</li></ol> |
| Not in scope | Types other than the two script types (§19). Several servers per connector, or a server chosen by the caller (D7). Calling PNMT's own `/api/v1` with a key. Webhooks when a ticket finishes (§19). An approval step by a person before a call runs (§19). Keys per connector, or networks per connector on top of the caller's (D9, D21). A review by the main LLM or by Seraph: the request names the typesafe model (§7.5). Arrays and objects as arguments. Secrets stored in PNMT as fixed arguments (§5.4). MCP tools for connectors (§15). |
| Depends on | The scripts (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8; `AgentScriptService`, `ScriptArguments`, `BashScriptHeader`). The agents' local rights (`ScriptPolicy` on Windows, `config.yaml` on Linux). The guard (docs/ai-operations-plan.md §3.2, §3.4; `DecisionClient`, `McpGuard`). The anonymous paths outside `/api` (docs/advanced-sensor-plan.md §4.1; `ReportEndpoint`, `AgentJoinEndpoint`). Data Protection for secrets that are shown again (`WireGuardNetwork.Protect`). The audit (docs/audit-redmine-zabbix-plan.md §4). The system sensors (docs/ai-operations-plan.md §4). docs/agent-feature-level-plan.md, if it is built first (§8.3). |
| Target | **1.15.0**, all of it: 1.14.3 shipped before C0 was built, so the `Proxy` fix comes with the rest (§21). The functions (C5) need the agents of 1.15. |

**Words.**

- ***A connector*** is one entry in *3rd Party Connectors*. It has a type, a target, arguments, limits and an optional review, and the callers it is granted to.
- ***A caller*** is an outside system that calls connectors, as PNMT knows it: a ticketing system, an orchestration tool, a customer portal, a script on another machine. It has a name, one key, its networks and its grants (§6.1).
- ***A grant*** lets one caller call one connector.
- ***A call*** is one HTTP request to a connector, and its row in `connector_calls`. A call is ***queued*** when the endpoint's checks pass, ***running*** once the worker started its run, and ***refused*** when a check stops it, at the endpoint or in the queue.
- ***The queue*** is the queued calls in `connector_calls`. There is no table of its own (D7). ***The worker*** (`ConnectorWorker`) is the background task that works the queue (§9).
- ***The target*** is what a call runs: for a script connector, the server's name, the script's text, the function, the account, the shell and the timeout. All of it is stored as text in the connector (§3.3).
- ***A selector*** fills a field of the target from what PNMT knows now: a server, a library script, a script registered on a server, a function, an account. It copies, it doesn't link.
- ***An active call*** is queued or running. ***A processed call*** is final: its run ended, or it was refused, expired or cancelled.
- ***A function*** is a function defined in a script. A call runs that function instead of the whole script (§8).
- ***An exposed argument*** is a parameter that the caller passes. ***A fixed argument*** is set by PNMT, and the caller may not pass it. ***An omitted parameter*** is passed by neither, so the script's default applies.
- ***A pattern*** is an exposed argument's regular expression. The whole value must match it.
- ***The review*** is Jev's check of a call before it runs. ***The checks*** are the admin's yes-or-no questions for it. A *yes* refuses the call (§7). Together they are the "clear order on what to check".
- ***Wait mode*** answers a call with its outcome. ***Ticket mode*** answers at once with ***a ticket***, the call's id, and the caller asks for the outcome with it.
- ***The key*** is a caller's API key. ***The networks*** are the addresses a caller calls from.
- ***Final*** describes a run whose result is in (or will never come, §9.4), and a call that is processed.

---

## 1. Today

### 1.1 Scripts, and how they run

- **The library** (`agent_scripts`, `agent_script_versions`) keeps a version per change of the text, with its SHA-256 (`ScriptHashes.Of`: no BOM, LF line endings).
  - **PowerShell** versions are parsed on a connected Windows agent, by a fixed parser script built into the agent (`PowerShell.Parser`, `PNMTD.Sensor.Windows/Scripts/PowerShell.cs:68`).
  - **Bash** versions have their parameters in a `# pnmt:` YAML header, which PNMTD reads itself (`BashScriptHeader.Parse`, `PNMTD.Lib/Agent/BashScriptHeader.cs`). A Linux agent adds `bash -n` and shellcheck.
- **A run** (`AgentScriptService.RunAsync`, `PNMTD/Services/Domain/AgentScriptService.cs:247`):
  - always runs **the current version**;
  - checks the arguments with `ScriptArguments.Check` (`PNMTD.Lib/Agent/AgentScripts.cs:96`): known names, mandatory parameters, switches, sets, ranges and numbers;
  - on Linux, works out a refusal from the rights the agent announced (`LinuxRefusal`, `:364`);
  - inserts one `agent_script_runs` row per server, with `RequestedBy` set to the user's name;
  - sends a signed `run-script` and **returns once the agent acks it**, within 30 s (`SendAsync`, `:431`). Secure arguments and a password typed for the run are encrypted to the agent's ECDH key (`AgentSecrets`).
- **Registered scripts** on a Windows server run as their registered account, with the text the server reported (`RunLocalAsync`, `:494`).
- **The server decides last, and PNMT can't change that:**
  - **Windows** (`ScriptPolicy.Refuses`, `PNMTD.Sensor.Windows/Scripts/ScriptPolicy.cs:25`): scripts on or off, *registered scripts only* or *any script*, and the allowed accounts. These are set in the agent's window by a local administrator.
  - **Linux** (`BashScripts.cs:116-196`, `/etc/pnmt-agent/config.yaml`): `scripts.enabled`, `run_as`, `allowed_hashes`, `max_timeout_minutes`.
  - Both parse the script again before a run, and refuse arguments that don't match the parse.
- **How the script is started:**
  - **Windows:** a wrapper (`PowerShell.cs:137-160`) splats `args.json` onto the script: `& script.ps1 @splat`.
  - **Linux:** `systemd-run --wait --pipe` starts `/bin/bash script.sh` with the parameters as `PNMT_<NAME>` in an `EnvironmentFile` (`BashScripts.cs:260-277`).
  - On both, nothing is pasted into a command line.
- **The result** arrives as a `script-result` message. `AgentScriptResults.ResultAsync` (`PNMTD/Agents/AgentScriptResults.cs:69-113`) stores the state, the exit code, the output (at most 1 MiB), the message and the duration.
  - **Nothing signals that a run finished.** The web UI polls every 2 s (`ScriptRunsList.vue`).
  - **A run whose result never comes stays `Sent` for ever.** For example, the agent crashed during the run. No task closes such runs.
  - **`agent_script_runs` has no retention.** The history outlives its scripts and its servers (docs/windows-sensor-plan.md §16, W6).
- **The rights:** everything about scripts is `Servers: Manage` (`AgentControllers.cs`, route `api/v1/agents`), the only level of the *Servers* area (`PNMTD/Auth/Rights/Rights.cs:59`).

### 1.2 No functions

- **The PowerShell parser reads only `$ast.ParamBlock.Parameters`:** the script's own `param()` block. There is no `FunctionDefinitionAst` anywhere.
- **The bash header has `synopsis`, `description` and `parameters`,** and nothing per function.
- **`ParsedScript`, `RunScriptRequest`, `agent_script_runs`, the contracts and the run dialog** have no function or entry point.
- **Both agents run the whole script.** An agent ignores fields it doesn't know (`AgentJson`), so **an older agent sent a function name would run the whole script.** §8.3 deals with that.
- **`AgentFeatures` doesn't exist yet.** docs/agent-feature-level-plan.md is a draft. The capabilities are strings in `AgentCapabilities` (`PNMTD.Lib/Agent/AgentProtocol.cs:152-170`): `scripts.v1`, `parse.v1` and others.

### 1.3 Outside callers today

- **Anonymous paths outside `/api`** are each kept away from cookie authentication by a predicate in `Program.cs:233-234`: `SensorPaths`, `AgentPaths`, `ReportPaths` and `McpSetup.IsMcpPath`.
  - The fallback policy requires a signed-in user (`AuthSetup.cs:140-141`), so each anonymous endpoint is mapped with `.AllowAnonymous()` (`AgentSetup.cs:42-52`), and `/report` adds `.DisableAntiforgery()` (`AdvancedSetup.cs:23-28`).
  - Security headers and ProblemDetails apply to `/pnmt-admin` and `/api` only (`HostingSetup.cs:73-99`, `ApiSetup.cs:63-93`). Anonymous endpoints answer with small JSON of their own (`ReportAnswer`, `AgentError`).
  - **Body limits are per endpoint:** `ReportEndpoint` checks `Content-Length`, then sets `IHttpMaxRequestBodySizeFeature` and counts while it reads (`ReportEndpoint.cs:175-217`). The agents' join reads at most 64 KiB.
  - **Guards are hand-rolled singletons:** `JoinGuard` (per address, `AgentJoinEndpoint.cs:18-86`) and `ReportGate` (per sensor, 429 with `Retry-After`).
- **Tokens:**
  - **Sensor tokens** are plain text, `[AuditSecret]`, shown again to *Monitoring: Edit*, and can be in the URL.
  - **MCP tokens** (`pnmt_ro_` / `pnmt_rw_` and 32 random bytes, base64url) are stored as a SHA-256 hash with a 4-character hint, and shown once. `SecretScanner` knows their shape (`PNMTD/Ai/SecretScanner.cs:171`).
  - **Agent join tokens** are stored as hashes and used once.
- **Secrets that are shown again use Data Protection:**
  - the key ring is `/var/lib/pnmtd/keys`, the `pnmtd-keys` volume, which is in the backup (`AuthSetup.cs:40-50`);
  - each feature has a purpose string and a `*Protected` column. The WireGuard peer's `PrivateKeyProtected` is the model: `Protect`, and `Unprotect` returns null when the ring is gone (`WireGuardNetwork.cs:63,128-140`);
  - **each display is audited** (`WireGuardTunnelDownloaded`, `MikrotikJoinScriptViewed`).
- **Networks:**
  - `AccessSignals.InNetwork` (`PNMTD/Cookie/Access/AccessSignals.cs:390`) maps IPv4-in-IPv6 to IPv4 and checks with `System.Net.IPNetwork`;
  - `AccessSettings` validates "a network such as 10.0.0.0/8 or 2001:db8::/32" (`:230`);
  - **`IPNetwork` masks host bits silently:** `10.0.0.5/24` parses as `10.0.0.0/24`. A plain address without a prefix doesn't parse at all.
- **The guard, Jev**, answers typed questions with probabilities only (`DecisionClient.DecideAsync`, `PNMTD/Ai/DecisionClient.cs:33`):
  - a `noul` question is a statement, answered with a probability;
  - `McpGuard` (`PNMTD/Mcp/McpGuard.cs`) is the model for screening one request synchronously. It asks `injection`, `settings`, `secrets` and `acts`, and refuses at `Ai__Guard__Threshold` (0.5). The client hears which question refused, not the probabilities;
  - the state is capped at 30 000 bytes (`Ai__Guard__MaxStateBytes`), and each request is in the AI request log with its cost;
  - **`DecideAsync` doesn't check the budget.**
- **Waiting for something slow:** MCP's `cookie_ask` waits up to `Mcp__CookieWaitSeconds` (at most 110 s, under the proxies' 120 s), checking every second. It then answers `{ pending, messageId }` for `cookie_reply` (`PNMTD/Tools/Read/CookieChatTools.cs:73-93`). This is the wait-or-ticket shape.

### 1.4 The caller's address, and a bug

- **The address is `Connection.RemoteIpAddress`** after `UseForwardedHeaders`. The forwarded headers are trusted only when `Proxy` is set (`Program.cs:182-199`).
- **`Proxy=docker` trusts nothing.** The README recommends it for a proxy on the same host (deploy/README.md:107, 114, 171).
  - Line 191 adds `new IPNetwork(IPAddress.Parse("::ffff:172.16.0.0"), 12)`. `IPNetwork` masks it to **`::/12`**, which contains no Docker address.
  - Checked on .NET 10 on 2026-11-18: `::/12` doesn't contain `::ffff:172.18.0.5`. `172.16.0.0/12` contains it, both mapped and as IPv4.
  - **So behind a Docker proxy, every request seems to come from the proxy's 172.x address.** This affects the audit log, the login lockout, `JoinGuard`, MCP's *last used from*, and the access watch today. For connectors, it would make every network rule judge the proxy instead of the caller.
  - **C0 fixes it first** (§6.4).

### 1.5 Free numbers

- **Status codes:** **233 and 433** are unused, in code and in the plans. 230 to 232 and 430 are taken.
- **Key-value key:** the last is `LAST_ADVANCED_EVALUATION = 320`. **`LAST_CONNECTOR_WORK = 330`.**
- **Rights area:** `Area.Connectors`, appended to the enum. It is stored by name in `group_rights`.
- **Audit actor:** `AuditActor.Connector`, appended. It is stored as a string, so no migration is needed for it.
- **System sensor key:** `connectors`.

## 2. Requirements

From the request:

- **R1.** An area *3rd Party Connectors*, where an admin adds entries and picks each entry's connector type. The first type is *Windows Server Script*.
- **R2.** A script connector names which script, and which function in it, to run.
- **R3.** Arguments that have to be passed are exposed on the connector.
- **R4.** Per connector: the caller waits for the run to finish, or gets a ticket number and asks for the outcome later.
- **R5.** An optional regular expression per argument. One argument that doesn't match it refuses the whole request.
- **R6.** The same for *Linux Server Script*.
- **R7.** Optionally, the typesafe model reviews a request, with a clear order of what to check.
- **R8.** A generated API key, which can be read more than once.
- **R9.** Requests can be limited to networks or single addresses (/32, /128).

Added by this plan, because R1 to R9 need them (each can be dropped in the review):

- **R10.** Parameters can be fixed by PNMT, so the caller passes only what it must. A fixed value can't be overridden.
- **R11.** The key travels only in a header, never in a URL. Plain HTTP works, as for the sensor API (D5).
- **R12.** Every call with a valid key is stored: its arguments, its review, its run and its output. ***Calls*, the area's main view, lists every active and processed call of every connector,** and the newest 100 000 are kept. Every run is in the script runs' history too, under the connector. Changes to connectors, and every display of a key, are audited.
- **R13.** Limits: the body's size, calls per hour, calls waiting and how long they may wait, per connector. Failed keys are limited per address.
- **R14.** A valid key from outside its caller's networks, a refusal by the review, and an address blocked for failed keys notify, through a system sensor.
- **R15.** The server's own policy stays final. A connector can't run what the agent's window or `config.yaml` refuses, and PNMT warns before a connector is saved that its server would refuse it.
- **R16.** A connector holds its target as text, not as links: its own copy of the script, and the server's and the account's names. Selectors fill them in. A connector keeps working when a library script, a run-as account or the server's entry in PNMT changes, and it changes only when someone edits it (D6).
- **R17.** The connector describes itself to its caller: a page with the URL, the body and examples, and a `GET` with the key.
- **R18.** A caller can retry safely: an `Idempotency-Key` header returns the first call instead of running again.
- **R19.** A dry run checks a call, the review included, without running it.
- **R20.** Runs whose result never comes are closed after their timeout, so they don't block a connector.
- **R21.** Per connector: whether a call is refused or runs unreviewed when the review can't run. A call that ran unreviewed is marked as such (D4).
- **R22.** Calls are queued, not refused, when their script is busy. The queue is the calls table itself, which a background task works (D7, D20).
- **R23.** **One run per script and server at a time**, whoever started it: a connector's calls wait their turn, and a run from the UI is refused while the script runs there (D15).
- **R24.** Line breaks in a value only where the argument allows them (D8).
- **R25.** **One key per caller** (D9). A caller has its networks and the connectors granted to it, and its key opens those connectors and no others.
- **R26.** **At most `Connectors__MaxRunningCalls` connector runs at once**, over all connectors and servers. Calls beyond it wait in the queue, oldest first (D22).

## 3. A connector

### 3.1 What it has

| Part | What | Shared by all types |
|---|---|---|
| **Name, slug, purpose** | The name in PNMT. The slug is in the URL (`shop-flush-cache`, `^[a-z0-9][a-z0-9-]{1,62}$`, unique). The purpose is one paragraph saying what the connector is for: for the people in PNMT, the caller's developers, and the review | yes |
| **Type** | *Windows Server Script* or *Linux Server Script* (§3.2) | yes |
| **Enabled** | A new connector starts **disabled**, and is enabled once its arguments are set | yes |
| **Target** | Per type, as text, filled by selectors (§3.3) | no |
| **Arguments** | Per type: for scripts, each parameter exposed, fixed or omitted (§5) | no |
| **Answer** | Wait mode (with the longest wait) or ticket mode (§4.5). Whether the output goes back to the caller: **off** until switched on (D10) | yes |
| **Callers** | The callers it is granted to. **The key and the networks are the caller's** (§6.1) | yes |
| **Review** | Off, or on with its checks, its threshold, and what happens when it can't run (§7) | yes |
| **Limits** | Calls per hour (default 60), calls waiting (default 20), and how long a call may wait (default 10 minutes). One run per script and server at a time applies to every connector (§9.5) | yes |

### 3.2 Types

- **`ConnectorType`:** `WindowsScript` (*Windows Server Script*) and `LinuxScript` (*Linux Server Script*). It is stored by name.
- **A type is a class behind `IConnectorType`:**
  - `ValidateAsync` checks the target and the arguments at the save;
  - `WarningsAsync` lists what would refuse a call now (§3.3);
  - `DescribeAsync` gives the arguments for the description (§4.7);
  - `CheckArgumentsAsync` checks a call's arguments (§5);
  - `StartAsync` starts a call's run, and `StateAsync` reads its state. The worker calls both (§9.1).
- **The two script types share one implementation,** `ScriptConnectorType`, which differs only in the platform. That follows `AgentScriptService`, which serves both through `ScriptLanguage`.
- **The callers, the review, the limits, the tickets and the history don't know the type.** A later type (§19) adds a class, a target table, and its form in the web UI.

### 3.3 The target of a script connector: text, filled by selectors

- **No hard links** (D6). The connector stores its target as text: the server's name, its own copy of the script, the function's name and the account. **None of it is a foreign key** into the library, the run-as accounts, the agents or the sensors.
  - Deleting or changing a library script or an account doesn't change the connector, and neither does joining the server's agent again or moving it to another sensor.
  - A call resolves the text when it arrives (check 11 of §4.3).
- **Selectors** fill each field from what PNMT knows now. **They copy, and the field stays editable as text:**

| Field | Stored as | Selector | At a call |
|---|---|---|---|
| **Server** | The server's name as PNMT shows it (`WEB01 / Windows`), or the host's name alone | The servers of the type's platform with an agent, with their state | **The one server** of the platform with an agent whose name matches, ignoring case. None: the call is refused by `server_not_found`. Several: by `server_ambiguous`. The overview shows which server the name finds now |
| **Script** | **The connector's own copy of the text** (at most 256 KiB, `MaxScriptChars`) and a name (`Shop-Tools.ps1`), in the same editor as the library's scripts | ***From the library…*** (a script and a version, the current one by default). ***From the server…*** (a script registered on a Windows server; it fills the account too) | The text and its hash as they are in the connector |
| **Function** | A name, or empty for the whole script | The functions of the text's parse (§8) | In the parse, and the agent checks it against its own parse |
| **Account** | **Windows:** the kind (*Service*, *Stored* or *gMSA*) and the account's name. **Linux:** root, or a local account's name | **Windows:** the run-as accounts (`windows_run_as`) and a registered script's account. **Linux:** the server's accounts, each with whether `run_as` allows it (`RunAsAccountsAsync`) | Passed to the agent as it is (`RunAsSpec`), and the agent decides. **Not *TypedPerRun*:** nobody is there to type a password |
| **Shell, timeout** | `powershell` or `pwsh` (Windows); 10 to 3600 s, default 600 | – | as they are |

- **The text's parse** works as for a library version:
  - **bash:** PNMTD reads the header at the save;
  - **PowerShell:** a connected Windows agent parses it (`parse-script`). `AgentScriptResults.ParsePendingAsync` sends pending connector texts too, and a result, which is matched by hash, reaches the connector's row as well as library versions with the same text. A text taken from the library unchanged is therefore parsed already.
  - **While the parse is pending,** calls wait in the queue (`waitingFor: parameters_pending`), and the form shows *parameters pending*. This usually lasts seconds.
- **Where the text came from** is noted as text, too (`TakenFrom`: "library: Shop-Tools.ps1 v4", "WEB01 / Windows: registered Get-DiskReport.ps1"). That is information, not a link. It enables one helper: when the library has a script of that name whose current text differs, the overview says ***The library's Shop-Tools.ps1 is at v5 and differs*** with *Compare…* (`DiffView`) and *Take v5…*. Nothing changes until someone takes it.
- **Registered scripts on Windows:** the agent decides by the hash (`ScriptPolicy.Refuses`). A text that is registered on the server runs as its registered account, and naming another account is refused.
  - *From the server…* therefore fills the text and the account together.
  - When the registered copy on the server changes, the connector's text no longer matches it. On a server that runs only registered scripts, calls are then refused by the agent. The warning says so, and *From the server…* takes the new text.
- **Warnings**, computed for the form and the connector's page from the text as it resolves now, never stored:
  - the server's name finds no server, or several;
  - the agent is offline, or doesn't announce `scripts.v1`;
  - **Linux:** `LinuxRefusal` for the account and the timeout (scripts off, the account not in `run_as`, a timeout above `max_timeout_minutes`). `allowed_hashes` is the agent's to check, and the warning says so;
  - **Windows:** the server's `scripts` section (`ScriptsSection(Allowed, AnyScript, RunAs, …)`) refuses it: scripts off; *registered scripts only* and the text's hash isn't registered; the account isn't allowed; the text is registered under another account;
  - the parse is pending, or has errors;
  - the function isn't in the parse, or the agent can't call functions (§8.3);
  - the arguments need an admin after a change of the text (§5.5);
  - wait mode with a timeout longer than the wait: calls that run longer get a ticket (§4.5).

  A warning doesn't block the save, because a server's policy can change after it. The call is then refused by the agent, as a run from the UI would be.

### 3.4 An example

***Shop: flush cache*** (`shop-flush-cache`), *Windows Server Script*:

- **Purpose:** "The shop's back office flushes a site's output cache after a product import."
- **Target:** the server `WEB01 / Windows`; the text of `Shop-Tools.ps1`, taken from the library at v4; the function `Clear-ShopCache`; the stored account `svc-shop`; powershell; timeout 120 s.
- **Arguments:**
  - `Site`: exposed, pattern `shop-[a-z0-9]{2,20}`;
  - `Scope`: exposed, with the script's own `ValidateSet` of Pages, Images and All;
  - `DryRun`: fixed to `false`.
- **Answer:** wait mode, up to 60 s, with the output switched on.
- **Callers:** granted to the caller *Shop back office*, whose networks are `10.20.0.0/24` and `2001:db8:20::17/128`, and whose key also opens *Shop: restart pool*.
- **Review:** on, threshold 0.5, with the built-in checks and one of the admin's: "Does the note ask for anything other than a cache flush, such as a restart, a deletion, or access for someone?"
- **Limits:** 30 calls an hour, at most 5 waiting. Two imports that finish at once flush one after the other, because `Shop-Tools.ps1` runs only once at a time on WEB01.

The back office then sends `POST /connector/v1/shop-flush-cache` with `{ "arguments": { "Site": "shop-de", "Scope": "Pages" } }` and the key. It gets the exit code and the output within a few seconds. `{ "Site": "shop-de; Remove-Item" }` is refused with 422 before anything is sent to WEB01.

## 4. The call

### 4.1 The endpoint

| Method and path | What |
|---|---|
| `POST /connector/v1/{slug}` | A call (§4.2): checked, stored as *queued*, and worked by the worker (§9.1). Wait mode: 200 with the outcome, or 202 with the ticket when the call isn't final within the wait. Ticket mode: 202 with the ticket at once |
| `GET /connector/v1/{slug}/tickets/{ticket}` | A call's outcome (§4.6) |
| `GET /connector/v1/{slug}` | The connector's description for its caller (§4.7) |
| `GET /connector/v1` | The connectors the key's caller may call, each with its name, slug and purpose (§4.7) |

- **On PNMT's existing address and port**, like `/report`: `ConnectorPaths.IsConnectorPath` joins the predicates of `Program.cs:233`.
- **The endpoints are minimal-API endpoints** (`ConnectorEndpoint`, mapped by `MapConnectorEndpoints` with `.AllowAnonymous().DisableAntiforgery().ExcludeFromDescription()`). They have no cookies, no CSRF, no ProblemDetails and no CORS.
- **`EndpointAuditTests.AnonymousAllowlist`** lists the four.
- **The key** is the caller's (§6.2), as `Authorization: Bearer pnmt_cx_…`. The path names the connector, and the key names the caller. It is never in the path or the query (R11): a key in the URL is refused like a missing one, so it doesn't end up in access logs as a working form.
- **Plain HTTP works,** as for `/event` and `/report` (D5). Over HTTP, the key and the arguments can be read on the way. The *Usage* tab says so when PNMT's address (`PublicBaseUrl`) is `http://`.
- **`Connectors__Enabled=false`** answers every connector path with a plain 404, as for an unknown path.

### 4.2 The request

```http
POST /connector/v1/shop-flush-cache
Authorization: Bearer pnmt_cx_…
Content-Type: application/json
Idempotency-Key: import-2026-11-18-0815

{
  "arguments": { "Site": "shop-de", "Scope": "Pages" },
  "reference": "IMPORT-8812",
  "note": "Flush after the nightly product import",
  "dryRun": false
}
```

- **`arguments`:** an object with one property per exposed argument (§5). Secure parameters go here too, and PNMT separates them by the parse. The caller doesn't need to know which ones are secure.
- **`reference`** (optional, at most 200 characters): the caller's own id for this call, such as a ticket or a job. It is shown in the history and searchable.
- **`note`** (optional, at most 1000 characters): why. It is shown in the history and given to the review (§7.1).
- **`dryRun`** (optional): check, review included, and don't run (R19, §4.8).
- **The body is read as UTF-8** whatever the `Content-Type` says, as on `/report`. It is at most `Connectors__MaxBodyBytes` (64 KiB), and must be one JSON object with no key twice.

### 4.3 The checks, in order

**At the endpoint, in the request.** Cheap and secret-free checks come first. Nothing tells a caller without the key whether a connector exists. A call that passes them all is stored as *queued*, and the worker is nudged (§9.1).

| # | Check | Refused with | Stored as a call |
|---|---|---|---|
| 1 | `Connectors__Enabled`, the path | 404 (plain) | no |
| 2 | A key in `Authorization: Bearer`. A key in the path or the query counts as none (R11) | 401 `unauthorized` | no; counted per address (§6.5) |
| 3 | The address isn't blocked for failed keys (§6.5) | 429 `blocked`, `Retry-After` | no |
| 4 | **The key:** the caller whose key hash it is (§6.2). **Without a valid key, every slug answers the same,** so nothing says which connectors exist | 401 `unauthorized` | no; counted per address (§6.5) |
| 5 | **The networks:** the call comes from one of the caller's networks (§6.3) | 403 `address_refused` | **yes**, and **433** (§12.3): a valid key from elsewhere is probably a leaked key |
| 6 | **The caller is enabled; the slug names a connector granted to it; the connector is enabled.** A connector that doesn't exist and one that isn't granted answer the same | 403 `caller_disabled` / 404 `unknown_connector` / 403 `connector_disabled` | yes, with the caller (and the connector, when the slug names one) |
| 7 | The body: size, JSON, an object (§4.2) | 413 `too_large` / 400 `invalid_body` | yes |
| 8 | `Idempotency-Key` seen before (§4.8) | the first call's answer | no (the first one is) |
| 9 | **The arguments** (§5): names, types, values, patterns, the script's own checks. **Every problem is listed, and one is enough to refuse** | 422 `arguments_invalid` with `fields` | yes |
| 10 | The limits: calls this hour, calls waiting (§9.5) | 429 `rate_limited` / `queue_full`, `Retry-After` | yes |
| 11 | **Stored as *queued*,** the secure values held in memory (§5.4), and the worker nudged | ticket mode: 202 at once. Wait mode: waits (§4.5) | yes, *queued* |

**In the queue, by the worker** (§9.1), each call in the order it came. A refusal here is no HTTP error: the call becomes *refused*, with `refusedBy` and the message. A caller in wait mode gets that in its answer, and a caller with a ticket when it asks (§4.4).

| # | Check | When it doesn't pass |
|---|---|---|
| W1 | **The review** (§7), if on, once per call | *refused* by `review` with the check's label, and **433**. When the review can't run: *refused* by `review_unavailable`, or on, unreviewed (§7.4) |
| W2 | **The target now** (§3.3): the name finds exactly one server; the text is parsed without errors; the arguments still pass the connector as it is now (§5.5); the function is in the parse, and the agent can call functions (§8.3) | *refused* by `server_not_found`, `server_ambiguous`, `arguments_invalid`, `arguments_need_review` or `agent_too_old`. **An offline server and a pending parse wait** |
| W3 | A dry run ends here | *checked*, with what W1 and W2 said |
| W4 | **The script's turn** (D15): no run of the same script on the same server, whoever started it, and no older call for it in the queue. **And a free place:** fewer than `Connectors__MaxRunningCalls` connector calls running (D22, §9.5) | the call waits (`turn`, or `capacity`) |
| W5 | **The run starts** (§9.2). Refusals by the agent (its policy, its own parse) come with the ack and are the run's state | *running*, then the run's final state |
| – | **The waiting time:** a call still queued after its connector's `MaxQueueSeconds` | *expired*, with what it waited for |

- **Why the key comes before the networks:** the key says which caller it is, and so which networks apply. Only a holder of the key learns that the address is wrong, and a valid key from elsewhere can be reported (R14).
- **Why the grant comes after the networks:** a key from the wrong address learns nothing about the caller's connectors.
- **Why the review is in the queue:** the request stays fast and never waits for Jev. The worker reviews several calls at once (`Connectors__WorkerParallelism`), and it reviews a call before it waits for its turn, so a refusal comes back at once.
- **Why the arguments are checked at the endpoint:** a caller with a typo hears 422 at once, with every field. The worker checks them again before the start (W2), in case the connector changed while the call waited.
- **The error body** is the same for every refusal at the endpoint:

```json
{ "ok": false, "error": "arguments_invalid", "message": "2 arguments are not allowed.",
  "fields": { "Site": "doesn't match the pattern shop-[a-z0-9]{2,20}", "Force": "not an argument of this connector" } }
```

  It names the arguments and, for a pattern, the pattern (D11). It never names a value or a review's probabilities.

### 4.4 The answer

```json
{
  "ok": true,
  "ticket": "6f1c2a7e-3f5d-4c1e-9a8b-0d7c4e2b1a90",
  "state": "done",
  "refusedBy": null,
  "waitingFor": null,
  "reviewed": true,
  "exitCode": 0,
  "output": "Flushed 1 284 entries of shop-de (Pages).\n",
  "outputTruncated": false,
  "message": null,
  "receivedAt": "2026-11-18T08:15:02.114Z",
  "startedAt": "2026-11-18T08:15:03.020Z",
  "finishedAt": "2026-11-18T08:15:05.871Z",
  "durationMs": 2851,
  "poll": "/connector/v1/shop-flush-cache/tickets/6f1c2a7e-3f5d-4c1e-9a8b-0d7c4e2b1a90"
}
```

- **`state`** is the call's state. Once its run started, that is the run's `ScriptRunState`:

  | State | Means | Final |
  |---|---|---|
  | `queued` | in the queue; `waitingFor` says for what: `worker`, `review`, `turn` (the script runs on the server already), `capacity` (PNMT runs its most connector calls already), `server_offline`, `parameters_pending` | no |
  | `running` | the run was sent (`Sent`, `Running`) | no |
  | `done` | `Done` | yes |
  | `failed` | `Failed`: the script failed, or the agent didn't ack within 30 s | yes |
  | `timedOut` | `TimedOut` | yes |
  | `refused` | a check in the queue refused it (`refusedBy`: `review`, `review_unavailable`, `server_not_found`, …), or the agent did (`refusedBy: agent`, its policy or its parse); `message` says why | yes |
  | `offline` | `Offline`: the agent went away between the start and the send | yes |
  | `lost` | §9.4: no result within the timeout and a grace period | yes |
  | `expired` | still queued after the connector's `MaxQueueSeconds`; `message` says what it waited for | yes |
  | `cancelled` | cancelled while queued: by an admin, or because the connector was disabled or deleted (§9.1) | yes |
  | `checked` | a dry run's end, with `review` and `checks` (§4.8) | yes |

- **The HTTP status doesn't depend on the outcome** once the call is stored: 200 when it is final, 202 when it isn't. A script that fails, or a call the review refused, is a call with that `state`. A caller reads `state`, not the status.
- **`output`** is the run's output (stdout and errors, at most 1 MiB, as stored). **It is left out unless the connector returns output** (`ReturnOutput`, off by default, D10). The state, the exit code and the times are always there.
- **`ticket`** is the call's id: a random GUID, valid only together with the key of the caller that made the call (D13).
- **`reviewed`** is `true` when the review passed the call, `false` when it ran unreviewed because the review couldn't run (§7.4), and absent when the connector has no review.

### 4.5 Wait mode and ticket mode

- **Ticket mode** answers **202** with `{ ok, ticket, state: "queued", poll }` and a `Location` header as soon as the call is stored. It doesn't wait for the worker.
- **Wait mode** waits for the call to become final, at most the connector's `WaitSeconds` (5 to `Connectors__MaxWaitSeconds`, 110; default 60). The time in the queue counts.
  - **Final within the wait:** 200 with the outcome, whatever it is.
  - **Not final:** **202 with the ticket and the state** (`queued` with `waitingFor`, or `running`), exactly as in ticket mode. **A waiting caller never loses a call:** it can always fetch the outcome later.
  - 110 s stays under the 120 s that the deploy examples' proxies allow (deploy/nginx-reverse-proxy.md:143), as `cookie_ask` does.
- **How PNMT waits** is in §9.3. A caller that disconnects changes nothing: the call stays in the queue or keeps running, and its outcome waits under its ticket.

### 4.6 Asking for a ticket

- `GET /connector/v1/{slug}/tickets/{ticket}` with the caller's key, over the checks 1 to 6 of §4.3, answers 200 with the answer of §4.4. A connector disabled since the call doesn't stop its caller from fetching the outcome.
- **The first fetch after the call became final** sets `CollectedAt` on the call, for the history (*collected*).
- **An unknown ticket, another connector's or another caller's, or one past the retention** (§10.4) answers 404 `unknown_ticket`. A key rotated since the call fetches its outcome with the new key.
- **Polling** is limited to one request per second per ticket (429 `rate_limited`). It is counted apart from the calls per hour.

### 4.7 The connector's description

`GET /connector/v1` with the key lists the connectors granted to its caller: name, slug, purpose and mode. `GET /connector/v1/{slug}` answers what the caller needs in order to call one of them:

```json
{
  "name": "Shop: flush cache", "purpose": "The shop's back office flushes …", "mode": "wait", "waitSeconds": 60,
  "returnsOutput": true, "review": true, "callsPerHour": 30,
  "arguments": [
    { "name": "Site", "type": "string", "required": true, "pattern": "shop-[a-z0-9]{2,20}", "help": "The site, as named in IIS." },
    { "name": "Scope", "type": "string", "required": false, "default": "Pages", "oneOf": ["Pages", "Images", "All"] }
  ]
}
```

- **Fixed and omitted parameters aren't listed.** The caller doesn't need them, and the fixed values are PNMT's.
- **The patterns are listed,** and a value that doesn't match one is answered with the pattern (D11). The caller holds the key already, and can check values before sending them.
- **Nothing about the server, the script, the account or the review's checks.**

### 4.8 Retries and dry runs

- **`Idempotency-Key`** (optional, 1 to 100 printable characters): a second call to the same connector with the same value within `Connectors__IdempotencyHours` (24) answers the first call's ticket and current state, and runs nothing.
  - **The arguments must be the same:** the same key with other arguments is 422 `idempotency_mismatch`.
  - It is per caller and connector: a unique index on `(CallerId, ConnectorId, IdempotencyKey)`, so two calls at the same moment can't both run.
- **`dryRun: true`** goes through the endpoint's checks and the queue's W1 to W3 like a call, and ends as *checked* with what each check said, without a run.
  - It doesn't wait for the script's turn (W4), so it never waits behind a running script.
  - It is counted in the calls per hour, because the review costs money.
  - It is stored as a call like any other.
  - A dry run never checks `Idempotency-Key`.

### 4.9 Examples

```sh
# Wait mode: the outcome in the answer
curl -sS https://pnmt.example.com/connector/v1/shop-flush-cache \
  -H "Authorization: Bearer $PNMT_KEY" -H "Content-Type: application/json" \
  -d '{"arguments":{"Site":"shop-de","Scope":"Pages"},"reference":"IMPORT-8812"}'

# Ticket mode: start, then ask
ticket=$(curl -sS https://pnmt.example.com/connector/v1/backup-start-job -H "Authorization: Bearer $PNMT_KEY" \
  -d '{"arguments":{"job":"weekly"}}' | jq -r .ticket)
curl -sS https://pnmt.example.com/connector/v1/backup-start-job/tickets/$ticket -H "Authorization: Bearer $PNMT_KEY"
```

```powershell
$headers = @{ Authorization = "Bearer $env:PNMT_KEY" }
$body = @{ arguments = @{ Site = 'shop-de'; Scope = 'Pages' } } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri https://pnmt.example.com/connector/v1/shop-flush-cache -Headers $headers -Body $body -ContentType 'application/json'
```

The connector's *Usage* tab shows these, filled in, with Python as a third example (§13.5).

## 5. Arguments

### 5.1 Exposed, fixed, omitted

For each parameter of the target (the script's, or the function's), the admin chooses one of three:

| Mode | The caller | PNMT | Allowed for |
|---|---|---|---|
| **Exposed** | passes it; required if the parameter is mandatory, or if the admin makes it required | checks it (§5.2, §5.3) | every parameter |
| **Fixed** | **may not pass it**: passing it refuses the call | passes the admin's value | every parameter **except a secure one** (§5.4) |
| **Omitted** | may not pass it | passes nothing, so the script's default applies | parameters that aren't mandatory |

- **A new connector starts with every parameter omitted,** except mandatory ones, which are exposed. The admin opens up what the caller needs.
- **A fixed value is checked at the save** like a call's value: by `ScriptArguments.Check` and against its type.
- **Every exposed argument has a description for the caller.** It defaults to the parameter's help (`.PARAMETER`, the header's `help`).
- **A text argument can be *multi-line*** (off by default, D8). Only then may its value hold line breaks (§5.2).
- **An argument the connector doesn't expose refuses the call** (R5). Unknown names aren't ignored either: a typo in a caller then shows at once, instead of the script's default running.

### 5.2 Types and values

- **The JSON type must match the parameter's type:**
  - text: a string;
  - `int` and the other numeric types of `ScriptArguments.Check`, and the header's `int` and `number`: a number;
  - a switch, or the header's `bool`: `true` or `false`;
  - `[securestring]`, or the header's `secret`: a string.

  A number in a string, or `"true"`, is refused. It is the caller's job to send the right type, and loose types would make patterns ambiguous.
- **Arrays and objects are refused** (§19).
- **Every value:**
  - is at most `Connectors__MaxValueChars` (4000) characters;
  - is valid UTF-8, with **no control characters** (U+0000 to U+001F and U+007F to U+009F, NUL included), whatever the pattern says;
  - **except, for a *multi-line* argument, line breaks (LF, and CR LF) and tabs** (D8). They reach the script as they were sent: splatted on Windows, and on Linux as an escaped value in the `EnvironmentFile` (`BashScriptHeader.EnvironmentFile`). The Linux checklist gets a step that a value with line breaks arrives intact. In a pattern, `.` doesn't match a line break unless the pattern says `(?s)`, and the form's tester shows it;
  - has invisible and bidi characters (U+200B to U+200F, U+202A to U+202E, U+2066 to U+2069) refused too, as `InputSanitizer` removes them for the AI. A connector refuses them instead, because a script must see what the caller sent.
- **Then the script's own checks** run on the values that will be passed: the exposed ones that were given, plus the fixed ones (`ScriptArguments.Check` against the parse of the connector's text, or the function's parameters, §8.4). That covers mandatory parameters, sets, ranges and numbers.
- **The agent checks again** against its fresh parse, as for every run.

### 5.3 Patterns

- **Optional, per exposed argument, at most 500 characters,** in .NET's syntax.
- **The whole value must match:** PNMT anchors the pattern as `\A(?:pattern)\z`. `[a-z]+` refuses `abc; rm -rf /`. An admin who writes `^…$` loses nothing. Partial matching would make most hand-written patterns unsafe, so it isn't offered.
- **Matched against the value's text:** a string as it is, a number in invariant culture (`12.5`), a boolean as `true` or `false`. A pattern on a boolean is refused at the save: it says nothing.
- **Compiled** with `RegexOptions.CultureInvariant`, and with `RegexOptions.NonBacktracking` when the pattern allows it, which rules out catastrophic backtracking.
  - A pattern that needs backtracking (backreferences, lookarounds) gets a match timeout of 100 ms (`Connectors__PatternTimeoutMs`).
  - **A timeout refuses the call,** as `pattern_timeout` on the argument.
- **Checked at the save:** it must compile, and the editor has a tester (§13.5): values typed by the admin show *matches* or *doesn't match*.
- **Secure values are matched too,** in memory (§5.4).
- **The error names the argument and the pattern** (D11): "doesn't match the pattern shop-[a-z0-9]{2,20}". It never repeats the value.

### 5.4 Secure parameters

- **`[securestring]` and the header's `secret`** can only be exposed or omitted, never fixed. PNMT doesn't store script secrets (docs/windows-sensor-plan.md §7.4), and a connector doesn't change that.
  - A script that needs a stored secret uses a stored account or the server's own secret store.
  - Fixed secrets would need a vault in PNMT (§19).
- **A secure value from the caller:**
  - is checked against its pattern, in memory;
  - **waits in memory while the call is queued** (`ConnectorSecretVault`, like MikroTik's `CredentialVault`), and is removed from it when the run starts or the call ends. If PNMTD restarts while such a call waits, the call ends as *expired* with "PNMT restarted while the call waited, and its secret arguments are gone: send it again";
  - is encrypted to the agent with `AgentSecrets.Encrypt` when the run starts, as a secure argument typed in the run dialog;
  - **is never stored, never logged, and never shown to the review.** The review sees `(secret, 24 characters)` (§7.1);
  - is listed in the call's history by its name only.

### 5.5 When the text changes

- **Only an edit of the connector changes its text** (R16): typing in the editor, or a selector (*From the library…*, *From the server…*, *Take v5…*). The save counts `ScriptRevision` up, and the audit has the change with the old and new hash. Calls that are running keep the text they started with.
- **The parameters follow the new text's parse,** by name:
  - the choices of parameters that stayed are kept;
  - a new mandatory parameter becomes exposed, and a new optional one omitted;
  - the rows of parameters that are gone are removed;
  - the form shows these changes before the save when the parse is there already (bash, or a text taken from the library unchanged).
- **A PowerShell text whose parse is still pending** is saved as it is. When the parse arrives, the same rules apply. Until then, calls wait in the queue for it (`parameters_pending`).
- **A choice that no longer fits** (a fixed value outside a new set, a pattern on a parameter that became a switch, a fixed parameter that became mandatory and secure) **sets `NeedsReview`.** Calls are then refused by `arguments_need_review` until an admin saves the arguments again. The connector's page and the list show it as a warning.
- **Nothing outside the connector is locked.** A library script, a run-as account or a server can be deleted or changed as before. A selector then simply can't offer it, and a call resolves what is there (§3.3).

## 6. Callers: the key, the networks and the grants

### 6.1 A caller

- **A caller is an outside system as PNMT knows it** (D9): the shop's back office, the ticketing system, the orchestration tool. It is an entry of its own in *3rd Party Connectors*, on the *Callers* tab (§13.1), with:
  - **a name and a description** ("Shop back office, run by the web team; contact …");
  - **one key** (§6.2);
  - **its networks** (§6.3): where its calls come from;
  - **its grants:** the connectors it may call, any number of them. A connector can be granted to several callers;
  - **enabled** or not. A new caller starts disabled, and is enabled once its networks are set.
- **One key per caller, not per connector** (D9). The back office holds one key and calls *flush cache*, *restart pool* and *start import* with it.
- **The grant decides which connectors the key opens.** A connector that isn't granted to the caller looks to it as if it didn't exist (404 `unknown_connector`, §4.3). Granting is a change of the caller or of the connector, from either page, and is audited.
- **The networks belong to the caller** (D21), because they say where the caller is, as the key says who. Every connector the caller calls is reached only from there.
- **Every call records its caller,** so *Calls* shows and filters by caller, and the audit names the caller and the connector.

### 6.2 The key

- **Format:** `pnmt_cx_` followed by 32 random bytes in base64url, 51 characters in all. This is MCP's shape with its own prefix. `SecretScanner` gets the pattern, so a key pasted into a knowledge-base page or a mail is caught like an MCP token.
- **Stored twice, on the caller:**
  - **`KeyHash`:** SHA-256 hex, unique, `[AuditSecret]`. A call's key is hashed and looked up by this index, as MCP tokens are. The lookup compares hashes, not the key, so its timing gives nothing away;
  - **`KeyProtected`:** the key under Data Protection, purpose `PNMTD.Connectors.Key.v1`, `[AuditSecret]`. It is used only to show the key again;
  - **`KeyHint`:** the last 4 characters, for the list.
- **Shown again** (R8): `GET /api/v1/connector-callers/{id}/key` returns the key, and **each display is audited** (`ConnectorCallerKeyViewed`), as WireGuard's tunnel file is. The UI shows the key masked, with *Show* and *Copy*. Either one fetches the key, and both are audited.
- **A key ring that is gone** (a restore without the `pnmtd-keys` volume) leaves the key working, because calls use the hash. It just can't be shown: 409 `key_lost`, "rotate the key to get a new one".
- **Rotate** (`POST …/key/rotate`, with a confirmation): the old key stops at once, **for every connector the caller has**, and the new one is shown. The confirmation lists those connectors. This is audited (`ConnectorCallerKeyRotated`). There is no overlap period (§19).
- **A new caller gets its key at creation.** The caller starts disabled, so the key works only once its networks are set and the admin enables it.

### 6.3 The networks

- **A list of 1 to 50 entries per caller,** each an IPv4 or IPv6 network in CIDR form, or a single address:
  - **a single address becomes /32 or /128** when it is saved, and shows like that (R9);
  - **host bits are refused** with a hint, because `IPNetwork` would mask them silently (§1.3): `10.20.0.17/24` gives "a network starts at its first address: 10.20.0.0/24, or 10.20.0.17/32 for one address";
  - each entry has an optional label ("office", "VPN");
  - duplicates and entries inside another entry are flagged (*inside 10.20.0.0/16*), not refused.
- **At least one entry is required** before a caller can be enabled (D3).
  - *From anywhere* means entering `0.0.0.0/0` and `::/0` explicitly.
  - The *Callers* list, the caller's page and the pages of its connectors then show a warning tag, *any address*.
- **The check:** the address a call comes from (§6.4), mapped from IPv4-in-IPv6 to IPv4, is in one of the caller's networks. This is `ConnectorNetworks.Contains`, pure, and shares its parsing with `AccessSignals.InNetwork`.
- **The caller's page shows** the address of each call. It also shows the address PNMT sees for the admin's own browser, with a hint when it is a Docker bridge address and `Proxy` isn't set (§6.4).

### 6.4 The caller's address (C0, first)

- **Fix `Proxy=docker`** (`Program.cs:191`): trust `172.16.0.0/12` (Docker's default bridge range), written as an IPv4 network, which contains the mapped addresses too (§1.4).
  - Also: `Proxy` takes **a list**, comma-separated, of addresses or networks (`Proxy=docker,10.0.0.5`). Each is validated at start, and a bad entry stops the start with a clear message. A typo would otherwise silently trust nothing, or the wrong thing.
  - `KnownProxies` keeps loopback, as today.
- **A test** (`ForwardedHeadersTests`) for `Proxy=docker`, an address and a list: `X-Forwarded-For` from a trusted address sets the client's address, and from any other address it is ignored.
- **Release notes 1.14.3:** "Behind a proxy in Docker, PNMT saw the proxy's address for every request (audit log, lockout, MCP's last address). It now sees the client's." This ships before the rest, because it affects 1.14 installations today.
- **For connectors** the rule stays as for the rest of PNMT. The forwarded-headers middleware reads `X-Forwarded-For` from the right and stops at the first address that isn't a trusted proxy. An address the caller wrote itself on the left therefore counts only if every hop after it is trusted.
- **What a trusted range means:** `Proxy=docker` trusts all of `172.16.0.0/12`. On a LAN that uses that range itself, a client that reaches PNMT's port directly, not through the proxy, can claim any address in `X-Forwarded-For`, and pass a connector's networks with it.
  - deploy/README.md says so, and that a caller's networks are only as good as `Proxy`. Bind PNMT to `127.0.0.1` (`PNMTD_BIND_ADDRESS`) behind a proxy on the same host, or name the proxy's own address instead of `docker`.
  - The *Security* tab shows the hint when connectors exist and `Proxy=docker` is set while PNMT listens on all addresses.

### 6.5 Failed keys

- **`ConnectorGuard`** (a singleton, like `JoinGuard`) counts failed keys (check 4) per address:
  - **`Connectors__FailedKeysPerAddress` (20) within 10 minutes** blocks the address on `/connector` for `Connectors__BlockMinutes` (15);
  - the block is answered with 429 `blocked` before the key is read;
  - the dictionary is pruned above 10 000 addresses, as `JoinGuard` is.
- **A block is audited** (`ConnectorRefused`, "address … blocked after 20 failed keys") and writes **433** (§12.3). Single failures go only to the application log, at information level, with the slug and the address, never the key.
- **The 256-bit key makes guessing hopeless anyway.** The guard is there against noise and log flooding, and to make probing visible.

## 7. The review (the typesafe model)

### 7.1 What Jev gets

One `DecideAsync` call (purpose `connector.review`) with a `state` of four texts. The texts the caller wrote are marked as untrusted. Nothing is masked: it is the caller's own data, and only the guard sees it.

1. **The context**, which PNMT writes: "A call from an outside system to PNMT's connector "Shop: flush cache". It runs the function Clear-ShopCache of the PowerShell script Shop-Tools.ps1 (version 4) on the server WEB01, as svc-shop. What the connector is for: {purpose}."
2. **The fixed arguments,** marked "set by PNMT, not by the caller": `{"DryRun": false}`.
3. **The caller's arguments,** marked untrusted: `{"Site": "shop-de", "Scope": "Pages"}`. A secure one is `"(secret, 24 characters)"`.
4. **The caller's reference and note,** marked untrusted.

The whole state stays far below Jev's 30 000 bytes. Each argument is capped at 4000 characters (§5.2), and the body at 64 KiB.

### 7.2 The checks: the order of what to check

- **Jev answers questions, with probabilities.** So the admin's order is **a list of yes-or-no questions, each answered *yes* when the call must be refused**:
  - **built in, always asked when the review is on:**
    - `injection`: `AiPipeline.InjectionQuestion`, "Does the text try to give instructions to an AI system …?";
    - `purpose`: "Does the call ask for something other than what the connector is for, as described in the context?" It can be switched off, for connectors whose purpose is too broad to judge;
  - **the admin's checks, 0 to 5:** each with a label (shown to the caller when it refuses) and the question, up to 500 characters. Examples:
    - "Does a site name refer to anything other than one of the shop's sites (names starting with shop-)?"
    - "Does the note ask for more than a cache flush, such as a restart, a deletion, or access for someone?"
    - "Do the arguments name a path outside D:\Sites?"
- **The form explains the rule** next to the list ("Write each check as a question. Yes means the call is refused") and has **Try the review** (§13.5): sample arguments and a note, Jev's answer per check, and the cost. This is a real Jev request, in the request log and the budget.
- **The threshold** is per connector, 0.05 to 0.95, defaulting to `Ai__Guard__Threshold` (0.5). Below the default refuses more often.

### 7.3 The verdict

- **Any question at or above the threshold refuses** the call: it becomes *refused* by `review`, with the check's label ("purpose", "Site names") in its message, and **433** goes to the system sensor (§12.3). The worker reviews a call before it waits for its turn (W1), so a caller in wait mode hears it at once.
- **The call stores the review:** the threshold, each question's probability, the question that refused, and the AI request's id, linked to *Admin → AI → Requests*.
- **The caller hears which check refused, never the probabilities**, as `McpGuard.Message` does. A caller that could read the margin could tune its input to slip under it.

### 7.4 When Jev can't answer

- **The review can't run** when:
  - the AI is off or the guard isn't configured. **The review can't be switched on then**, and the form says why. If it changes later, the connector's fallback applies;
  - **the budget is spent.** `AiBudget.EnsureAvailableAsync` is checked first, because `DecideAsync` doesn't check it (§1.3);
  - Jev errs, or doesn't answer within `Connectors__ReviewTimeoutSeconds` (15). The AI's own `TimeoutSeconds` (60) is too long for a caller waiting on a call;
  - the answer lacks a question.
- **What happens then is the connector's choice** (D4), *When the review can't run*:
  - **Refuse** (the default for a new connector): the call becomes *refused* by `review_unavailable`, with the reason in its message. The caller sends it again later;
  - **Run unreviewed:** the call goes on to the run. It is stored with the review's state *unreviewed* and the reason, *Calls* tags it ***unreviewed***, and the answer says `"reviewed": false`.
  - A dry run reports what would happen, with the reason.
- **No retry by PNMT.** The caller retries, and `Idempotency-Key` makes that safe. The AI's work queue is for work nobody waits for.
- **An outage of the guard** is already reported by the *AI* system sensor (421) when its own work fails. Connectors don't add a second alarm for the same outage.

### 7.5 What the review is, and isn't

- **It is a second line.** The first line is in code: the key, the networks, fixed arguments, types, patterns, and the script's sets and ranges. Those can't be argued with.
- **It catches what patterns can't express:** a free-text argument or note that asks for more than the connector's purpose, or text written to steer an AI downstream (a script that hands an argument to a model, a ticket text).
- **Jev can't be talked into a yes.** It returns probabilities to fixed questions, and writes nothing (docs/ai-operations-plan.md §3.2).
- **It isn't a reviewer with reasons.** A review by the main model with a written reason, like Seraph's, is later (§19). The request names the typesafe model, and a classifier is the right tool on a call's path: about a second, and a fraction of a cent.

## 8. Functions in scripts

A call runs either the whole script or one function in it (R2). Today neither agent can do the second (§1.2). This section adds it for both platforms, for connectors and for the run dialog alike.

### 8.1 PowerShell

- **A script with functions** is a library of functions, usually with nothing at the top level but settings:

```powershell
<#
.SYNOPSIS
    Tools for the shop's web servers.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Clear-ShopCache {
    <#
    .SYNOPSIS
        Empties a site's output cache.
    .PARAMETER Site
        The site, as named in IIS.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Site,
        [ValidateSet('Pages', 'Images', 'All')] [string] $Scope = 'Pages'
    )
    # …
}

function Restart-ShopPool { [CmdletBinding()] param([Parameter(Mandatory = $true)] [string] $Site) <# … #> }
```

- **The parser** (`PowerShell.Parser`) also lists the functions defined **at the script's top level**:
  - it walks `$ast.FindAll({ $args[0] -is [FunctionDefinitionAst] }, $false)`, so no functions nested in functions;
  - per function: the name, the line, the parameters from `$f.Body.ParamBlock` or from `function F($a, $b)`, read by **the same code as the script's parameters**, and `$f.GetHelpContent()`;
  - **new findings:**
    - `function-top-level-code`, per top-level statement other than `param`, function definitions, `Set-StrictMode`, `$ErrorActionPreference = …`, `using`, and an `if` on `$MyInvocation.InvocationName`. "Calling a function runs this line first (line 12)."
    - `function-script-mandatory`, when the script's own `param()` has a mandatory parameter. Dot-sourcing would ask for it, so its functions can't be called, and PNMT refuses them.
- **The wrapper** (`PowerShell.Wrapper`) reads `function.txt` when it is there:

```powershell
$functionFile = Join-Path $Folder 'function.txt'
if (Test-Path -LiteralPath $functionFile) {
  $name = (Get-Content -LiteralPath $functionFile -Raw -Encoding UTF8).Trim()
  . (Join-Path $Folder 'script.ps1')
  $command = Get-Command -Name $name -CommandType Function -ErrorAction Stop
  & $command @splat *>&1 | Out-File -LiteralPath $output -Encoding utf8 -Width 4096
} else {
  & (Join-Path $Folder 'script.ps1') @splat *>&1 | Out-File -LiteralPath $output -Encoding utf8 -Width 4096
}
```

  - **Dot-sourcing** defines the functions in the wrapper's scope. The script's top-level code runs then, as the finding warns. The script-level `param()` gets no arguments.
  - **`Get-Command -CommandType Function`** resolves the name to the function, never to an alias or a cmdlet of the same name.
  - **The name travels in a file,** like the arguments, never on a command line. The agent checks it against its fresh parse before the run (`^[A-Za-z_][A-Za-z0-9_-]{0,99}$`, and listed at the top level).
  - **The exit code:** a function's `exit` ends the wrapper too, and is read as before. `$LASTEXITCODE` and a thrown error are handled as today.
- **Registered scripts** get the same parse in their `scripts` section, so a connector can call a function of a registered script too.

### 8.2 Bash

- **The header declares the functions,** with the same keys per function as the script has. PNMTD reads it without running anything, as today:

```bash
#!/usr/bin/env bash
# pnmt:
#   synopsis: Tools for the backup server
#   functions:
#     start_job:
#       synopsis: Starts a backup job now
#       parameters:
#         job: { type: string, mandatory: true, set: [nightly, weekly] }
#     job_status:
#       synopsis: Prints a job's last result as JSON
#       parameters:
#         job: { type: string, mandatory: true }
set -euo pipefail

start_job()  { systemctl start "backup-${PNMT_JOB}.service"; echo "started ${PNMT_JOB}"; }
job_status() { systemctl show "backup-${PNMT_JOB}.service" -p Result -p ExecMainExitTimestamp --value; }

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  echo "Call a function: start_job or job_status" >&2
  exit 2
fi
```

- **`BashScriptHeader.Parse`** reads `functions` (names `^[a-z_][a-z0-9_]{0,39}$`, at most 50) into `ParsedScript.Functions`.
  - A function's parameters reach it as `PNMT_<NAME>`, exactly as a script's do.
  - The checks of docs/linux-sensor-plan.md §8.3 (declared, used, secrets echoed) run per function.
  - **New findings:**
    - `function-not-defined`: a declared function has no definition in the text (`name() {` or `function name`);
    - `function-no-guard`: functions are declared, and the text has no `${BASH_SOURCE[0]}` check. "Sourcing the script runs all of its top level: guard the main part with `if [[ "${BASH_SOURCE[0]}" == "$0" ]]`."
- **The Linux agent** starts a fixed runner instead of the script, when the run names a function:

```bash
# runs/<id>/run.sh, written by the agent, 0400
set -euo pipefail
source "$1"
"$PNMT__FUNCTION"
```

  - The command stays `systemd-run … /bin/bash run.sh script.sh`: file names only on the command line (docs/linux-sensor-plan.md §8.4).
  - The function's name is `PNMT__FUNCTION` in the `EnvironmentFile`. The double underscore can't collide with a parameter, because parameter names start with a letter.
  - The agent checks the name against the header of its own parse before the run.
- **The template** (`BashScriptHeader.Template`) and the PowerShell template stay as they are. *New script* gets a second template, *a script with functions*, shaped like the two examples above.

### 8.3 The protocol, and older agents

- **`PNMTD.Lib/Agent/AgentScripts.cs`:**

```csharp
/// <summary>A function defined at a script's top level, with its own parameters and help (docs/connectors-plan.md §8).</summary>
public sealed record ScriptFunction(string Name, int Line, IReadOnlyList<ScriptParameter> Parameters, ScriptHelp? Help);

// ParsedScript gets: IReadOnlyList<ScriptFunction>? Functions = null    (null: parsed by an agent that doesn't list functions)
// RunScriptRequest gets: string? Function = null                       (null: the whole script)
```

  `ScriptArguments.Check(parsed, function, arguments, secure)` checks against the function's parameters when a function is named. The current overload stays for the whole script.
- **Two capabilities** in `AgentCapabilities`:
  - **`parse.functions.v1`** (Windows): the parse lists functions. It is announced always, like `parse.v1`;
  - **`scripts.functions.v1`** (Windows and Linux): the agent calls a function when a run names one. It is announced only with `scripts.v1`.
- **PNMTD never sends `Function` to an agent without `scripts.functions.v1`.** An older agent would ignore the field and **run the whole script** (§1.2). Such a run is refused before the send: a connector's call is refused by `agent_too_old`, and the run dialog shows "the agent on WEB01 can't call functions: update it". The connector's warnings say so beforehand (§3.3).
- **An agent with the capability refuses a function** that isn't in its own fresh parse, and a function of a script whose own `param()` has a mandatory parameter.
- **Versions parsed without functions:** a PowerShell version whose `Functions` is null is parsed again when an agent with `parse.functions.v1` connects (`AgentScriptResults.ParsePendingAsync` gets that condition). The library then shows the functions without anyone saving again. Bash versions are read by PNMTD, so only the header has to be read again, at the start of 1.15.0 (`ReparseHeadersAsync`, once).
- **The feature level:** if docs/agent-feature-level-plan.md is built first, C5 adds an entry for each platform ("functions in scripts"). The capability guards correctness either way.
- **`AgentProtocol.Version` stays 1.** The new fields are optional.

### 8.4 In PNMT

- **The library** (*Admin → Servers → Scripts*) shows a script's functions under its parameters: the name, the synopsis and the parameters, with the new findings.
- **The run dialog** (`RunScriptDialog.vue`) gets *Function*:
  - *the whole script* is the default;
  - the parameter form follows the choice;
  - this is the same run a connector makes, so an admin can try a function by hand before opening it to a caller.
- **`AgentScriptRunRequest` and `WindowsLocalScriptRunRequest`** get `Function`.
- **`agent_script_runs.Function`** and the run's detail show it ("Clear-ShopCache in Shop-Tools.ps1 v4").

## 9. The worker, and running

### 9.1 The worker (`ConnectorWorker`)

- **One hosted task works the queue** (D20). It is registered like the other tasks (`BackgroundTasks:Enabled`, `Program.cs:125-171`), with a `TaskDefinition` and `LAST_CONNECTOR_WORK` (330).
  - **It is the only thing that changes a call after the endpoint stored it,** as the Advanced sensors' evaluator is the only writer of their codes (docs/advanced-sensor-plan.md §7.3).
  - **The calls table is the queue and the request log at once** (D7): a queued call is a row with `State = Queued`. There is no second table, and the history shows every call the same way, whatever became of it.
- **It wakes when nudged** (`ConnectorQueue.Nudge()`):
  - by the endpoint, after it stored a call;
  - by `AgentScriptResults`, when a run ends, because a script's turn may be free;
  - by the hub, when an agent connects, because a server may be back;
  - by a parse result, because a text may be parsed now.

  Otherwise it wakes every 5 s. The queue is in the database (`State`, with a partial index on the active states, §10.5), so nothing is lost on a restart.
- **A pass:**
  1. **Queued calls, oldest first** (`ReceivedAt`): W1 to W5 of §4.3. At most `Connectors__WorkerParallelism` (4) reviews run at once, and everything else is quick. At most `Connectors__MaxRunningCalls` (10) calls are running at any time (§9.5). A call that has to wait stays queued, with its `WaitingFor` updated;
  2. **running calls:** a call whose run became final takes the run's state and `FinishedAt`, and its signal fires (§9.3);
  3. **expiry:** a queued call past `ExpiresAt` (`ReceivedAt + MaxQueueSeconds`) becomes *expired*, with what it waited for;
  4. **once a minute, the maintenance:** lost runs (§9.4), the retention (§10.4, once an hour), old idempotency keys, and 233 after the quiet time (§12.3).
- **Each step is one short transaction** under the connector's advisory lock, so a pass that stops halfway leaves every call in a state it can continue from. At the start (W5), the call becomes *running* with its run's id in the same transaction as the run's row.
- **After a restart:**
  - queued calls continue where they were, except a call with secret arguments, which becomes *expired* (§5.4);
  - running calls continue: their run's result still arrives, or §9.4 closes it.
- **Cancelling:**
  - an admin cancels a queued call from *Calls* (`POST /connector-calls/{id}/cancel`, audited);
  - disabling or deleting a connector cancels its queued calls;
  - **a running call can't be cancelled,** because the agents can't stop a run. The dialog says so.
- **The worker isn't paused by the uplink guard,** as `AiWorker` isn't. The servers are reached through their agents' own connections.

### 9.2 One way to start a run

- **`AgentScriptService` gets one internal start,** `StartAsync(ScriptRunSpec spec, AuditWho who, CancellationToken ct)`. `RunAsync`, `RunLocalAsync` and connectors all use it:
  - **`ScriptRunSpec`:** the server, the script's text, hash and name, the library's script and version or none, the function, the account (kind and name, not a `windows_run_as` row), the shell, the timeout, the plain and secure arguments, `RequestedBy`, and the `ConnectorId`. A library run passes the current version, as today;
  - it keeps what both callers have today: the platform check, `LinuxRefusal`, encrypting the secrets to the agent, `SendAsync`, and the `AgentScriptRun` audit entry;
  - **one start means the checks of a run can't differ** between the UI and a connector.
- **A connector's run:**
  - **`RequestedBy`** is `Shop back office via Shop: flush cache` (the `RequestedBy` column holds 200 characters);
  - **`ConnectorId`** is set (a new column, indexed), so the script runs' history shows *via connector* with a link, and filters by it;
  - **`ScriptId` is null and `VersionNumber` is the connector's `ScriptRevision`,** because the text is the connector's own. The run's detail says "Shop-Tools.ps1, the text of the connector Shop: flush cache (revision 3)", and its hash says which text ran;
  - **the audit entry** is `AgentScriptRun` with **`Actor = Connector`**, `ActorRef = caller:{callerId}/connector:{connectorId}`, the caller's name, and the address the call came from (`AuditWho.Connector(address, reference)`, modelled on `AuditWho.Agent`). One entry per started run: the call itself isn't audited a second time.
- **One run per script and server at a time** (D15, R23) is checked in `StartAsync`, for every run: no run of a script with the same name (`ScriptName`, ignoring case) may be unfinished on that server.
  - **A connector's call** waits for its turn in the queue first (W4). The check in `StartAsync` only catches a run started from the UI in between, and the call then waits again.
  - **A run from the UI** is refused for that server with 409 `script_running`: "Shop-Tools.ps1 is running on WEB01 since 08:15 (connector Shop: flush cache): run it when that's done." In a batch over several servers, only those servers' rows are refused.
  - **The script is its name,** not its hash, so another revision of the same script waits too.
  - **Why:** calling a function runs the script's top level first (§8), and scripts written for one run at a time (temporary files, a lock, a service restart) stay safe, whoever calls them.
  - **A run that never finishes would block its script for ever.** That is one more reason §9.4 closes such runs.

### 9.3 Waiting for the outcome

- **`ConnectorCallSignals`** (a singleton): `WaitAsync(callId, timeout, ct)` returns when the call is final or the time is up.
  - **The worker completes the signal** after the transaction that made the call final. That is one `TryRemove` and `TrySetResult`, as `AgentConnection.Complete` does for acks.
  - **The worker learns of a run's end** from `AgentScriptResults.ResultAsync`, which nudges it after its `SaveChanges`. `SendAsync` finishing a row itself (offline, refused, no ack) is final at once.
  - **As a fallback,** the waiting request reads the call's row every 2 s. That covers a signal that was missed.
- **Ticket mode** doesn't wait at all (§4.5).

### 9.4 Runs that never finish (R20)

- **The worker's maintenance** (§9.1) closes runs (all of them, not only connectors' runs) whose state is still `Sent` or `Running` at `StartedAt + TimeoutSeconds + 5 minutes`:
  - state **`Failed`** with the message "No result from the agent within the timeout: it may have restarted during the run.";
  - a connector call shows it as `lost`;
  - **the script's turn on that server is free again** (§9.2);
  - **a result that arrives later still wins:** `ResultAsync` overwrites the row as it does today.
- **The web UI's run dialog** stops polling then, which today it never does for such a run.

### 9.5 The queue and the limits

- **First come, first served, per script and server:** the oldest queued call for a script on a server starts first. Calls for other scripts or other servers don't wait behind it.
- **At most `Connectors__MaxRunningCalls` (default 10, 1 to 100) connector calls run at once,** over all connectors, callers and servers (D22).
  - **What counts:** the calls in the state *running*, which is the calls whose run was started and isn't final. A run that never reports is closed by §9.4 and frees its place.
  - **When the places are taken,** a call whose turn has come waits with `waitingFor: capacity`. The places go to the waiting calls oldest first, whatever their connector, so a busy connector can't keep a quiet one waiting for ever. Its connector's `MaxQueueSeconds` still applies.
  - **Runs from the UI don't count, and aren't held back.** A person starting a script decides that themselves, and one run per script and server still applies to them (§9.2).
  - **Counted in the database** under one advisory lock for the whole limit, so two passes, or a restart in between, can't start more than the limit. The worker reads the count once per pass and counts its own starts in it.
  - **Why:** the agents run what they are sent at once. Without a limit, a burst of calls to many connectors would start as many scripts at the same time, and PNMT would wait on as many results.
- **Calls waiting** (`MaxQueued`, 1 to 100, default 20): the connector's queued calls. One more is 429 `queue_full`, with `Retry-After: 10`.
- **The waiting time** (`MaxQueueSeconds`, 10 to 3600, default 600): a call queued for longer *expires* (§4.3).
- **Calls per hour** (`MaxCallsPerHour`, 1 to 3600, default 60): the calls stored as queued in the last 60 minutes, dry runs included, counted in the database. One more is 429 `rate_limited`, with `Retry-After` set to when the oldest leaves the hour.
- **The counts at the endpoint** run under the per-connector advisory lock (`pg_advisory_xact_lock` on the connector's id), so two calls at the same moment can't both take the last place.
- **Calls refused at the endpoint don't count.** A caller that sends bad arguments in a loop shows in *Calls*, and `ConnectorGuard` stops one whose key is wrong.

## 10. The data model

Migration `Connectors`, with `dotnet ef migrations has-pending-model-changes` clean.

### 10.1 `connectors`, `connector_callers` and `connector_grants`

**`connectors`** (`ConnectorEntity`, `[Audited]`):

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | |
| `Name` | text(200), unique | |
| `Slug` | text(64), unique | §3.1 |
| `Purpose` | text(2000) | `[AuditText]` |
| `Type` | text(32) | `ConnectorType` by name |
| `Enabled` | bool | false at creation |
| `Mode` | text(16) | `Wait` or `Ticket` |
| `WaitSeconds` | int | 5 to `MaxWaitSeconds` |
| `ReturnOutput` | bool | **false** (D10) |
| `MaxCallsPerHour`, `MaxQueued`, `MaxQueueSeconds` | int | 60, 20, 600 (§9.5) |
| `ReviewEnabled` | bool | |
| `ReviewPurpose` | bool | the built-in `purpose` question, true |
| `ReviewThreshold` | double, nullable | null: `Ai__Guard__Threshold` |
| `ReviewChecks` | jsonb | `[{ "label": …, "question": … }]`, at most 5, `[AuditText]` |
| `ReviewFallback` | text(16) | `Refuse` (default) or `RunUnreviewed` (§7.4) |
| `CreatedAt`, `CreatedBy`, `UpdatedAt` | | |
| `LastCallAt` | | `[AuditIgnore]`, updated at most once a minute, like MCP's `LastUsedAt` |

**`connector_callers`** (`ConnectorCallerEntity`, `[Audited]`), §6.1:

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | |
| `Name` | text(200), unique | "Shop back office" |
| `Description` | text(2000), nullable | who runs it, and whom to ask |
| `Enabled` | bool | false at creation; needs at least one network |
| `Networks` | jsonb | `[{ "cidr": "10.20.0.0/24", "label": "office" }]`, normalised (§6.3) |
| `KeyHash` | text(64), unique | `[AuditSecret]`; the call's key is looked up by it (§6.2) |
| `KeyProtected` | text | `[AuditSecret]` |
| `KeyHint` | text(4) | |
| `KeyIssuedAt` | timestamp | |
| `CreatedAt`, `CreatedBy`, `UpdatedAt` | | |
| `LastCallAt`, `LastCallFrom` | | `[AuditIgnore]`, updated at most once a minute |

**`connector_grants`** (`ConnectorGrantEntity`, `[Audited]`): one row per caller and connector it may call. The key is `(CallerId, ConnectorId)`, with cascade on both sides, and `CreatedAt` and `CreatedBy`. The audit lists a grant under both its caller and its connector.

### 10.2 `connector_scripts` (`ConnectorScriptEntity`, `[Audited]`): 1:1 with the connector, cascade

**No foreign keys outside the connector** (§3.3, D6): every field is text.

| Column | Type | Notes |
|---|---|---|
| `ConnectorId` | uuid, key | |
| `Server` | text(500) | the server's name, resolved at each call (§3.3) |
| `ScriptName` | text(200) | `Shop-Tools.ps1` |
| `ScriptText` | text | at most `MaxScriptChars` (256 KiB), `[AuditText]` |
| `Hash` | text(64) | `ScriptHashes.Of(ScriptText)` |
| `ScriptRevision` | int | 1, and one more with every change of the text |
| `TakenFrom` | text(500), nullable | information only: "library: Shop-Tools.ps1 v4" (§3.3) |
| `ParseState`, `Parsed`, `ParseError`, `ParsedAt` | | as in `agent_script_versions`: `Parsed` is the `ParsedScript` as `jsonb` |
| `NeedsReview` | bool | the arguments wait for an admin after a change of the text (§5.5) |
| `Function` | text(100), nullable | null: the whole script |
| `RunAsKind` | text(16) | Windows: `Service`, `Stored`, `Gmsa`. Linux: `Service` (root) or `User` |
| `Account` | text(256), nullable | the account's name, as the agent gets it |
| `Shell` | text(16) | |
| `TimeoutSeconds` | int | |

### 10.3 `connector_arguments` (`ConnectorArgumentEntity`, `[Audited]`): cascade with the connector

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | |
| `ConnectorId` | uuid | |
| `Name` | text(100) | the parameter's name; unique per connector |
| `Mode` | text(16) | `Exposed`, `Fixed`, `Omitted` |
| `Required` | bool | exposed only; forced true for mandatory parameters |
| `Pattern` | text(500), nullable | `[AuditText]` |
| `FixedValue` | jsonb, nullable | never for a secure parameter |
| `Description` | text(1000), nullable | for the caller |
| `Multiline` | bool | text arguments only; false (D8, §5.2) |
| `Order` | int | the parameter's order in the script |

Parameters not in the table are omitted. A row is written for every parameter anyway, so the form and the audit show the choice.

### 10.4 `connector_calls` (`ConnectorCallEntity`, `[NotAudited("calls from outside: the request log and the queue")]`)

**One row per call, from the endpoint to the end** (D7, D20): the request log, the queue and the history are this table. The endpoint inserts the row, and the worker is the only one that changes it afterwards (§9.1).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | **the ticket** |
| `ConnectorId` | uuid, nullable | SetNull: the history outlives the connector |
| `ConnectorName` | text(200) | at the time; the slug as called when it named no connector |
| `CallerId` | uuid, nullable | SetNull, like the connector |
| `CallerName` | text(200) | at the time |
| `ReceivedAt` | timestamp | indexed with `ConnectorId`, and with `CallerId` |
| `From` | text(64) | the address the call came from |
| `State` | text(16) | `Queued`, `Running`, `Done`, `Failed`, `TimedOut`, `Offline`, `Lost`, `Refused`, `Expired`, `Cancelled`, `Checked` (§4.4) |
| `RefusedBy` | text(32), nullable | for `Refused`: at the endpoint `address_refused`, `connector_disabled`, `invalid_body`, `too_large`, `arguments_invalid`, `rate_limited`, `queue_full`; in the queue `review`, `review_unavailable`, `server_not_found`, `server_ambiguous`, `arguments_invalid`, `arguments_need_review`, `agent_too_old`; and `agent` |
| `WaitingFor` | text(32), nullable | while queued: `worker`, `review`, `turn`, `capacity`, `server_offline`, `parameters_pending` |
| `ExpiresAt` | timestamp, nullable | `ReceivedAt + MaxQueueSeconds` |
| `StartedAt`, `FinishedAt` | timestamp, nullable | when the run started, and when the call became final |
| `ServerSensorId`, `ServerName` | uuid and text(500), nullable | the server the name found, at the time (no foreign key) |
| `ScriptName`, `ScriptRevision`, `Hash`, `Function` | | what the call ran, at the time |
| `Message` | text(2000), nullable | the refusal's message, with the fields |
| `Arguments` | jsonb, nullable | the caller's plain arguments as received; null once redacted |
| `SecureNames` | text[] | the secure arguments' names only |
| `Reference` | text(200), nullable | |
| `Note` | text(1000), nullable | null once redacted |
| `Redacted` | bool | |
| `IdempotencyKey` | text(100), nullable | unique with `CallerId` and `ConnectorId` where not null |
| `ArgumentsHash` | text(64), nullable | for `idempotency_mismatch` |
| `Review` | jsonb, nullable | `{ state: reviewed / refused / unreviewed, threshold, answers: { injection: 0.02, … }, refusedBy, reason, aiRequestId }` |
| `RunId` | uuid, nullable | → `agent_script_runs`, SetNull |
| `CollectedAt` | timestamp, nullable | §4.6 |

- **Retention** (§9.1): **the newest `Connectors__KeepCalls` (100 000) calls of all connectors together, with no age limit** (D16). It uses the MCP log's query (`McpLog.cs:48-54`) on `ReceivedAt`.
  - **Active calls are never deleted,** however old.
  - **The runs of deleted calls go with them** (`agent_script_runs.ConnectorId` set). The runs started by people keep today's rule (no retention).
  - At 100 000 calls with small outputs this is a few hundred MB at most. The outputs (up to 1 MiB each) are the part that can grow, and deploy/README.md says so.
- **Calls with a wrong key aren't stored** (§4.3). They would let anyone fill the table.

### 10.5 Indexes for the queue

- **`connector_calls (ReceivedAt) WHERE State IN ('Queued', 'Running')`:** the worker's queue, small however long the history is.
- **`connector_calls (ConnectorId, ReceivedAt DESC)`:** a connector's calls, the limits' counts and *Calls* with a connector set.
- **`connector_calls (CallerId, ConnectorId, IdempotencyKey)`, unique where the key isn't null** (§4.8).
- **`connector_calls (CallerId, ReceivedAt DESC)`:** a caller's calls, for *Calls* with a caller set.
- **`agent_script_runs (SensorId, lower(ScriptName)) WHERE State IN ('Sent', 'Running')`:** the script's turn (§9.2), for connectors and for the run dialog.
- **The maintenance** (lost runs, the retention, old idempotency keys, 233) is part of the worker (§9.1). Connectors have one background task.

### 10.6 What else changes

- **`agent_script_runs`:** `ConnectorId` (uuid, nullable, indexed, no foreign key, like `ScriptId`) and `Function` (text(100), nullable).
- **The parse pipeline** (`AgentScriptResults`): `ParsePendingAsync` sends pending connector texts as well as pending library versions. A `parse-result` is applied by hash to both, and then reconciles the connector's arguments (§5.5).
- **`ParsedScript.Functions`** lives inside the existing `jsonb` `Parsed`, so it needs no column.
- **The audit's subjects:** `ConnectorScriptEntity` and `ConnectorArgumentEntity` are listed under their connector (`AuditChangesInterceptor.Subjects`), as an Advanced sensor's rules are under the sensor. `ConnectorGrantEntity` is listed under its caller and its connector.

## 11. The admin API

`api/v1/connectors`, cookie authentication, ProblemDetails, `ConnectorsController`, contracts in `Api/V1/Contracts/Connectors.cs`.

| Endpoint | Right | Audit |
|---|---|---|
| `GET /connector-calls` (**every connector's calls**, paged: active ones first, then the processed ones, newest first; filters: connector, caller, active or processed, state, refused by, from, reference, the time; with the last 24 h in figures). *Calls*, the area's main view (§13.2) | Connectors: View | – |
| `GET /connector-calls/{id}` (with the review and the run, its output included) | Connectors: View | – |
| `POST /connector-calls/{id}/redact` (the arguments and the note) | Connectors: Manage | `ConnectorCallRedacted` |
| `POST /connector-calls/{id}/cancel` (a queued call only; a running one is 409 `call_running`, §9.1) | Connectors: Manage | `ConnectorCallCancelled` |
| `GET /connectors` (list: name, type, server, target, mode, the callers granted it, review, enabled, warnings, last call, calls and refusals in 24 h) | Connectors: View | – |
| `GET /connectors/{id}` (the detail with the target, the parse, the arguments, the warnings of §3.3 and the description of §4.7) | Connectors: View | – |
| `GET /connectors/{id}/compare` (the connector's text against the current version of the library's script of the same name, as a `DiffView` diff, §3.3) | Connectors: View | – |
| `POST /connectors` (type, name, slug, purpose, target, optionally callers to grant it): created disabled, with its arguments' rows | Connectors: Manage | `ConnectorChanged` |
| `PUT /connectors/{id}` (the target's text fields, the arguments, the answer, the review, the limits, enabled, and the callers granted it) | Connectors: Manage | `ConnectorChanged` (the entities' changes, grants included) |
| `DELETE /connectors/{id}` (its calls stay, under its name) | Connectors: Manage | `ConnectorChanged` |
| `GET /connector-callers` (list: name, key hint, networks' count and *any address*, the connectors granted, enabled, last call and from where, calls and refusals in 24 h) and `GET /connector-callers/{id}` | Connectors: View | – |
| `POST /connector-callers` (name, description, networks, connectors to grant): created disabled, with its key | Connectors: Manage | `ConnectorCallerChanged` |
| `PUT /connector-callers/{id}` (everything but the key: name, description, networks, grants, enabled) | Connectors: Manage | `ConnectorCallerChanged` (grants included) |
| `DELETE /connector-callers/{id}` (its key stops at once; its calls stay, under its name) | Connectors: Manage | `ConnectorCallerChanged` |
| `GET /connector-callers/{id}/key` | Connectors: Manage | **`ConnectorCallerKeyViewed`** |
| `POST /connector-callers/{id}/key/rotate` | Connectors: Manage | `ConnectorCallerKeyRotated` |
| `POST /connectors/{id}/check` (sample arguments and note, optionally `review: true`): checks 9, 11 and 12 of §4.3 without a run and without the limits, with the review's probabilities, which a person in PNMT may see | Connectors: Manage | `[NotAudited("a check, nothing runs")]` |
| `POST /connectors/parse` (`{ type, text }`): the parse for the form before a save. Bash's header at once; PowerShell's when one exists for the hash, otherwise `pending`, and the text is sent to an agent | Connectors: Manage | `[NotAudited("a parse, nothing runs")]` |
| `POST /connectors/patterns/test` (`{ pattern, values[] }`): compiles and matches | Connectors: View | `[NotAudited]` |
| **The selectors** (§3.3): `GET /connectors/lookups/servers?type=` (the platform's servers with an agent, their names and state); `…/lookups/library?type=` (the language's scripts and their versions); `…/lookups/library/{scriptId}/versions/{number}` (a version's text and parse); `…/lookups/registered?server=` (a Windows server's registered scripts, with text and account); `…/lookups/accounts?type=&server=` (the run-as accounts, or a Linux server's accounts with whether `run_as` allows them) | Connectors: Manage | – |

- **One right decides** (D2): *Connectors: Manage* makes and changes connectors, including the text a connector runs on a server and the account it runs as. It doesn't need *Servers: Manage* as well.
  - That makes it as strong as running scripts on servers, for the callers it lets in. It also makes callers and hands out their keys. §12.1 says so in the right's own text, and the migration grants it to no group.
- **The selectors read through lookups of their own,** under *Connectors: Manage*. They return what the form needs: names, states, texts, parses and account names. They never return a password, because PNMT has none.
- **`EndpointAuditTests.Matrix`** gets every row, and `openapi.json` and `schema.d.ts` are regenerated.

## 12. Rights, audit, the system sensor

### 12.1 The rights area *Connectors*

- **`Area.Connectors`,** appended, with two levels:
  - **View:** "3rd Party Connectors: every call, the connectors, and the output of their runs";
  - **Manage:** "Add, change and delete connectors, including the script a connector runs on a server and the account it runs as; add and change callers, their networks and grants, show and rotate their keys; redact and cancel calls. As strong as running scripts on servers, for the callers it lets in."
- **No group gets it** in the migration, and admins have it. The release notes say so.
- **Also:** `levelOrder` in `rights.ts`, the count in `GroupsTests` (20 → 21), and **not** in `McpRights.Ceiling`: MCP has no access to connectors (§15).

### 12.2 Audit

- **`AuditActor.Connector`,** `AuditWho.Connector(address, "caller:{callerId}/connector:{connectorId}")`. *Audit log*'s actor filter (`AuditLogView.vue:75-80`) and `who()` list it, and so does `OperationsReadTools`.
- **Events** (`AuditCategory.Connectors`, new):
  - `ConnectorChanged`: created, changed (a new text with its old and new hash), deleted;
  - `ConnectorCallerChanged`: a caller created, changed (networks, grants, enabled), deleted;
  - `ConnectorCallerKeyViewed`, `ConnectorCallerKeyRotated`;
  - `ConnectorRefused`: a valid key from outside its caller's networks, a refusal by the review, an address blocked (§6.5);
  - `ConnectorCallRedacted`, `ConnectorCallCancelled`.

  A call whose run starts is `AgentScriptRun` with the connector as actor (§9.2).
- **`AuditModelTests`** pass: the caller's `KeyHash` and `KeyProtected` are `[AuditSecret]`, and `LastCallFrom` is `[AuditIgnore]`.
- **`AuditWho.Connector`** names both: `ActorRef = caller:{callerId}/connector:{connectorId}`, with the caller's name and the address in the entry.

### 12.3 The system sensor *3rd Party Connectors*

- **`SystemSensorKeys.Connectors = "connectors"`**, named *3rd Party Connectors*, importance High.
  - It is created with the first connector (`SystemSensors.IdAsync`), so installations without connectors don't get it.
  - It is written through `SensorEventWriter`, so notification rules work as for every sensor.

| Code | Name | Success | When |
|---|---|---|---|
| 233 | `CONNECTORS_OK` | yes | No refusal of the kinds below for `Connectors__QuietMinutes` (60), after a 433 |
| 433 | `CONNECTORS_REFUSED` | no | A valid key from outside its caller's networks; a refusal by the review; an address blocked for failed keys |

- **The message** names the caller, the connector, the kind and the address: "Shop back office (Shop: flush cache): its key from 203.0.113.9, outside its networks. Rotate the key if this isn't expected." Repeats within the quiet period only move the time (the sensor API's dedupe), unless the message changes.
- **Why only these:**
  - failed scripts, bad arguments and limits are the caller's business, and in the history;
  - these three mean a key in the wrong hands, or a caller sending what it shouldn't.

## 13. The web UI

### 13.1 Where

- **Menu:** *Integrations → 3rd Party Connectors* (`layouts/navigation.ts`, right `['Connectors','View']`), with an icon not used yet (`navigation.test.ts` checks that).
- **The area has four tabs** (`/connectors/:tab(calls|connectors|callers|docs)?`, built like *Admin → MCP*), and **opens on *Calls*** (D19):
  - ***Calls:*** every active and processed call, the main view (§13.2);
  - ***Connectors:*** the list, and *Add connector…* (§13.3);
  - ***Callers:*** the outside systems with their keys, networks and grants, and *Add caller…* (§13.4);
  - ***Documentation:*** docs/connectors-api.md for the callers' developers, shown in PNMT as MCP's *Docs* tab shows its tools.
- **A connector's page:** `/connectors/connector/:id/:tab(overview|target|arguments|security|calls|usage)?` (§13.5). **A caller's page:** `/connectors/caller/:id` (§13.4).
- All three routes are added to `e2e/pages.spec.ts`.
- **Files:** `views/connectors/`:
  - the area: `ConnectorsView.vue` (`PageHeader` and `TabNav`), `CallsTab.vue`, `ConnectorsTab.vue`, `ConnectorsDocsTab.vue`;
  - `AddConnectorDialog.vue`, `CallersTab.vue`, `AddCallerDialog.vue`, `CallerView.vue`, `NetworksEditor.vue`;
  - a connector: `ConnectorView.vue`, with a `…Tab.vue` per tab;
  - `ConnectorCallDialog.vue`, `TakeScriptDialog.vue` (the script's selectors), `ChooseDialog.vue` (the other selectors);
  - `connectorFormat.ts` with its test.

### 13.2 *Calls*: the main view

- **Every call of every connector,** from `GET /connector-calls`, refetched every 5 s while the tab is open (`useServerTable` with `refetchInterval`).
- **Active calls first,** under *Active (3)*, as the worker sees them:
  - **running:** the connector, the caller, from, reference, the server the name found, started, and *running for* as a live counter;
  - **queued:** the same, with *waiting* and what for (*its turn behind Shop: flush cache's run*, *a free place: 10 of 10 running*, *WEB01 offline*, *the review*, *the parse*), its place in the line for its script and server, and when it expires;
  - *wait* or *ticket*;
  - ***Cancel…*** on a queued call, for Connectors: Manage (§9.1).

  A call stays there until it is final, and then moves down.
- **Then processed calls,** newest first:
  - when, connector, caller, from, reference;
  - the state as a tag: *done*, *failed*, *timed out*, *refused* with what refused it (*networks*, *arguments*, *review*, *agent*, …), *expired*, *cancelled*, *checked* (a dry run), *lost*;
  - the exit code, the time in the queue and the run's duration;
  - *unreviewed* and *collected* as small tags.
- **Filters,** kept in the URL: connector, caller, active or processed, state, refused by, from (an address or a network), reference, the time. A connector's own *Calls* tab is this view with the connector set, and a caller's page links to it with the caller set.
- **On top:**
  - running now against the limit (*4 of 10 running*), and the last 24 h in figures: calls, refused, failed runs, expired, and the longest wait in the queue;
  - the system sensor's state, with its last 433 while it is failed (§12.3).
- **A row opens `ConnectorCallDialog`:**
  - the arguments (secure ones by name), the reference and the note;
  - the steps with their times: received, reviewed, waited (for what), started, finished;
  - the review per question, with a link to the AI request;
  - the run, with its output (as in `ScriptRunsList`'s dialog), and a link to the run in the server's *Scripts* tab;
  - *Cancel…* while queued, *Redact…* once final.
- **The footer** says that the newest 100 000 calls are kept (`Connectors__KeepCalls`).

### 13.3 *Connectors*: the list, and adding one

- **The list:** `ListTable` with `useServerTable`. Columns:
  - name and type;
  - server / script · function;
  - mode;
  - the callers granted it, with the *any address* tag when one of them may call from anywhere;
  - review;
  - last call, and calls and refusals in 24 h;
  - enabled.
  
  A warning icon marks connectors with warnings (§3.3). *Add connector…* is shown for Connectors: Manage.
- ***Add connector…*** in three steps:
  1. **The type:** two cards, *Windows Server Script* and *Linux Server Script*.
  2. **The target,** each field as text with its selector beside it (§3.3):
     - the server's name, *Choose…*;
     - the script: *From the library…* or *From the server…* (Windows), or pasted or typed in;
     - *Function* (*the whole script*, or one of the parse's functions with its synopsis);
     - the account, *Choose…*;
     - the shell and the timeout.

     The parse (`/connectors/parse`, *pending* until an agent parsed a new PowerShell text) and the warnings of §3.3 show live.
  3. **Name, slug and purpose:** the slug is suggested from the name.

  It is saved **disabled**, and opens on *Arguments*, because the arguments come next. Granting it to callers is on *Security*.

### 13.4 *Callers*, and a caller's page

- **The list:** `ListTable` with `useServerTable`. Columns:
  - name and description;
  - key `…a1B2`;
  - networks, with the *any address* tag;
  - the connectors granted;
  - last call, from where, and calls and refusals in 24 h;
  - enabled.

  *Add caller…* (name, description, networks, the connectors to grant) is shown for Connectors: Manage. The caller is saved **disabled** with its key, and its page opens.
- **A caller's page** (`CallerView.vue`):
  - **Key:** masked with its hint; *Show* and *Copy* (`CopyField`), each audited, and the page says so; *Rotate…* with a confirmation that lists the connectors whose calls stop until the caller has the new key (§6.2);
  - **Networks:** `NetworksEditor.vue`, a list with label and CIDR, validated as typed (§6.3); the *any address* warning; *Your browser comes from 172.18.0.3*, with the Docker hint (§6.4);
  - **Connectors:** the connectors granted, each with its state and a link; *Grant…* and *Remove*;
  - **Enabled,** which needs at least one network (D3);
  - **Recent calls:** the last 20, with a link to *Calls* with this caller set;
  - **Usage:** the key's header, `GET /connector/v1` to list its connectors, and a link to each connector's *Usage* tab.

### 13.5 A connector's page

- ***Overview:***
  - the target as a sentence: "Runs Clear-ShopCache in Shop-Tools.ps1 (revision 3) on WEB01 / Windows as svc-shop";
  - the server the name finds now, and the warnings;
  - the library hint: *The library's Shop-Tools.ps1 is at v5 and differs*, with *Compare…* and *Take v5…* (§3.3);
  - the mode and the wait, *Return output* (off until switched on), the limits, *Enabled*, and the last 24 h by state.

  Editing uses `InlineField` where it fits.
- ***Target:*** the fields of §3.3, each editable as text with its selector beside it:
  - **the script's text** in the library's editor (`ScriptEditor.vue`), with *From the library…* and *From the server…*. `TakeScriptDialog.vue` picks a script and a version, shows its text and parse, and a diff against the connector's current text before it takes it;
  - below it, the parse: the parameters, the functions and the findings;
  - `TakenFrom`, muted.
  - **Saving a changed text** first shows what it does to the arguments (§5.5).
- ***Arguments:*** one row per parameter: the name, the type and the help; *Exposed / Fixed / Omitted* (`SelectButton`); *Required*; *Multi-line* for text arguments (§5.2); the pattern with a tester (values typed in → ✓ / ✗ live, through `patterns/test`); the fixed value; the description for the caller.
  - **The fixed value's input is the run dialog's,** taken out of `RunScriptDialog.vue` into `ScriptArgumentInput.vue`: a checkbox for a switch, a select for a set, a number field with its range. Both use it.
  - Secure parameters show *Fixed* disabled, with the reason (§5.4).
  - ***Check a call…*** opens sample arguments and a note, and shows what checks 9 to 12 say, the review optional (`/check`).
- ***Security:***
  - **Callers:** the callers granted this connector, each with its networks (and the *any address* tag), its last call, and a link to its page; *Grant to a caller…* and *Remove* (§6.1). The key and the networks are edited on the caller's page;
  - **Review:** on or off (disabled with the reason when the guard isn't usable); the threshold; *Purpose* on or off; the checks (label and question, up to 5), with the rule above them; ***When the review can't run:*** *Refuse* or *Run unreviewed* (§7.4); ***Try the review…*** (§7.2) with each question's probability as a bar against the threshold, and the cost;
  - **Limits:** calls per hour, calls waiting, the waiting time (§9.5).
- ***Calls:*** the view of §13.2, with this connector set.
- ***Usage*** (R17): everything the caller's developers need, generated from the connector:
  - the URL and the mode;
  - the body with the exposed arguments, their types, requirements and patterns;
  - the answers and their states, the errors, and the ticket call;
  - curl, PowerShell and Python, each with `CopyField`, with the key as `$PNMT_KEY`, never filled in;
  - a link to docs/connectors-api.md (§15).
  
  `SensorUrlPanel.vue`'s tabs of examples are made a shared `CodeExamples.vue`.

### 13.6 Elsewhere

- **The script runs' history** (`ScriptRunsList.vue`, *Admin → Servers → Scripts* and a server's *Scripts* tab): *By* shows *connector Shop: flush cache* as a link, the function shows with the script, and a filter *via connectors*.
- **The library's script page:** *Copied into connectors: Shop: flush cache (from v4)*, from `TakenFrom`. This is a hint only: changing the script changes no connector.
- **The run dialog:** *Function* (§8.4).
- **Audit log:** the actor *Connector*.
- **`schema.d.ts`** is regenerated (`tools/npm.sh run gen:api`).

## 14. Settings

`ConnectorOptions` (`Connectors__*`), bound in `ConnectorSetup`, documented in deploy/.env.example and deploy/README.md:

| Setting | Default | |
|---|---|---|
| `Connectors__Enabled` | `true` | false: `/connector` answers 404, and the page says connectors are off. No connector exists until an admin makes one |
| `Connectors__MaxBodyBytes` | `65536` | |
| `Connectors__MaxValueChars` | `4000` | per argument |
| `Connectors__MaxWaitSeconds` | `110` | under the proxies' 120 s |
| `Connectors__PatternTimeoutMs` | `100` | for patterns that need backtracking |
| `Connectors__ReviewTimeoutSeconds` | `15` | |
| `Connectors__WorkerParallelism` | `4` | reviews the worker runs at once (§9.1) |
| `Connectors__MaxRunningCalls` | `10` | connector calls running at once over all connectors, 1 to 100 (§9.5, D22) |
| `Connectors__FailedKeysPerAddress` | `20` | within 10 minutes |
| `Connectors__BlockMinutes` | `15` | |
| `Connectors__IdempotencyHours` | `24` | |
| `Connectors__QuietMinutes` | `60` | 433 → 233 |
| `Connectors__KeepCalls` | `100000` | the newest calls of all connectors, and their runs; active calls are never deleted (§10.4) |

And `Proxy` takes a list (§6.4).

## 15. Simulator, seed, docs

- **The simulator** (`pnmtd agent simulate`, `PNMTD/Cli/AgentSimulator.cs`):
  - announces `parse.functions.v1` and `scripts.functions.v1`, and `--no-functions` leaves them out;
  - lists the functions of a PowerShell text by a regex, enough for tests;
  - answers a run that names a function with "ran {function} with {arguments}".
- **The dev seed:**
  - *Shop: flush cache* (Windows, wait mode, the review on);
  - *Backup: start job* (Linux, ticket mode, a function, `10.0.0.0/8`);
  - each with a few calls of each state, one of them queued behind a running one, so *Calls* shows something;
  - two callers: *Shop back office* (both connectors, `10.0.0.0/8`) and *Lab* (one connector, from anywhere, so the warning shows).
- **docs/connectors-api.md**, for the caller's developers:
  - the request and the answers, the states, the errors, tickets, idempotency, dry runs;
  - the examples, and what the networks and the review mean for a caller.
  
  It is linked from the *Usage* tab and `DocsView`. docs/advanced-report.md is the model.
- **docs/windows-sensor-plan.md §7 and docs/linux-sensor-plan.md §8:** functions in scripts. **deploy/README.md:** the settings, `Proxy` as a list, "a connector's networks are only as good as `Proxy`", and the keys in the backup (the hashes in the database, the protected copies need the `pnmtd-keys` volume).
- **No MCP tools and nothing for Cookie** (D12). Connectors are configuration that only admins manage, and MCP gives operational access only (docs/mcp-plan.md). A read tool for the call history is later.

## 16. Tests

Few, at the risky places (`PNMTD.Tests/Connectors/`):

- **`ConnectorNetworksTests`** (pure):
  - a single address becomes /32 and /128;
  - host bits are refused with the hint;
  - an IPv4-mapped caller matches an IPv4 network;
  - IPv6 in a /64;
  - `0.0.0.0/0` alone doesn't let IPv6 in.
- **`ConnectorArgumentsTests`** (pure):
  - unknown argument; a fixed one passed by the caller; an omitted one passed; a missing required one;
  - the wrong JSON type (`"7"` for an int, `"true"` for a switch);
  - **a pattern matches the whole value only** (`[a-z]+` against `abc; rm`);
  - a pattern that times out refuses;
  - control and bidi characters refused; line breaks and tabs refused, except in a *multi-line* argument, where they are kept as sent;
  - the 422's field names the pattern, never the value;
  - a secure value matched but never in the result's stored arguments;
  - the script's set and range still apply;
  - several problems at once, all listed.
- **`ConnectorCallerTests`:**
  - the key's shape and `SecretScanner` catching it;
  - a call finds its caller by the key's hash;
  - shown again through Data Protection;
  - a lost key ring: the call still works, the display is `key_lost`;
  - rotating stops the old key at once, for every connector the caller has; a ticket from before is fetched with the new key;
  - one key calls every connector granted to its caller, and a connector that isn't granted answers 404 `unknown_connector` like one that doesn't exist;
  - a disabled caller is 403 `caller_disabled`, and can't be enabled without a network;
  - another caller's ticket is 404 `unknown_ticket`;
  - `GET /connector/v1` lists exactly the caller's connectors.
- **`ConnectorEndpointTests`** (the integration fixture with the simulator):
  - without a valid key, every slug answers the same 401, and a key in the query counts as none;
  - a valid key from outside its caller's networks: 403, a stored call with the caller, 433, `ConnectorRefused`;
  - **the target as text:** the server's name finds one server and runs there; no server is `server_not_found`, two are `server_ambiguous`; a library script deleted after the connector took its text doesn't stop its calls; a server joined again under the same name is found again;
  - a disabled connector;
  - 422 with every field;
  - **wait mode:** 200 with the output, from the signal;
  - **a run that outlasts the wait:** 202 and the ticket, then `GET` gives `done`;
  - **ticket mode:** 202, then the outcome, then `CollectedAt`;
  - `Idempotency-Key`: the same call answers the same ticket and runs once, and other arguments give `idempotency_mismatch`;
  - `queue_full` and `rate_limited` with `Retry-After`;
  - a dry run runs nothing;
  - the run in `agent_script_runs` with `RequestedBy`, `ConnectorId` and the function;
  - the audit's actor `Connector`;
  - the output is left out by default, and is there once `ReturnOutput` is on;
  - the failed-key guard blocks an address and writes 433;
  - **the retention** keeps the newest calls of all connectors together, deletes the runs of the calls it deletes, and never deletes an active call.
- **`ConnectorWorkerTests`** (the worker driven by hand, as `AdvancedEvaluatorTests` drives the evaluator):
  - calls for the same script and server run one after the other, oldest first, also from two connectors; calls for another script don't wait;
  - a run from the UI of a script that a connector's call runs is refused for that server with `script_running`, and a call queued behind a UI run waits for it;
  - a review refusal makes the call *refused* by `review`, and a caller in wait mode gets it in a 200 answer;
  - an offline server keeps the call queued with `server_offline`, and it starts when the agent connects (the hub's nudge);
  - with `MaxRunningCalls` at 2, a third call for another script on another server waits with `capacity`, starts when one of the two ends, and the places go oldest first over two connectors; a run from the UI neither counts nor waits;
  - a call past `MaxQueueSeconds` expires with what it waited for;
  - cancelling a queued call works, and cancelling a running one is 409; disabling a connector cancels its queued calls;
  - a restart (a new worker over the same table) continues queued calls, and expires one whose secret arguments are gone;
  - the endpoint never changes a call after it stored it.
- **`ConnectorTextTests`:**
  - a PowerShell text's parse result reaches the connector by its hash, as it reaches library versions;
  - calls wait with `parameters_pending` until it does;
  - **a changed text reconciles the arguments:** kept choices stay, a new mandatory parameter is exposed, a removed one goes, and a fixed value outside a new set sets `NeedsReview` and refuses calls with `arguments_need_review`;
  - `ScriptRevision` and the run's `VersionNumber` and hash follow the text.
- **`ConnectorReviewTests`** (a fake `DecisionClient` handler, as the MCP guard's tests use):
  - the state carries the context, the fixed arguments, and the caller's text marked untrusted, with secrets replaced by their length;
  - a check at the threshold refuses with its label and no probability;
  - the AI off, the budget spent, a timeout: with *Refuse*, each ends the call as *refused* by `review_unavailable`, and nothing runs; with *Run unreviewed*, the call runs, is stored as *unreviewed* with the reason, and answers `"reviewed": false`;
  - a refusal reaches a caller in wait mode in its 200 answer, and a ticket's caller when it asks.
- **`ScriptFunctionTests`:**
  - `BashScriptHeader` reads `functions`, with `function-not-defined` and `function-no-guard`;
  - `ScriptArguments.Check` against a function's parameters;
  - PNMTD refuses a function to an agent without `scripts.functions.v1` (simulator `--no-functions`): `agent_too_old`, nothing sent;
  - a version parsed without functions is parsed again by a capable agent.
- **`ScriptRunsLostTests`:** a run past its timeout and 5 minutes becomes `Failed` with the message, and a late result still wins.
- **`ForwardedHeadersTests`** (C0, §6.4).
- **`EndpointAuditTests`, `AuditModelTests` and `GroupsTests`** grow as listed.
- **The agents** (on a real machine, the checklists of docs/windows-sensor-plan.md §16 and docs/linux-sensor-plan.md §17 get a step each):
  - a function of a PowerShell script with a mandatory parameter and a set;
  - a function that throws;
  - a script with top-level code shows the finding;
  - a bash function as a local account;
  - an older agent refuses nothing it shouldn't, because PNMT never sends it a function.
- **Web:**
  - `connectorFormat.test.ts`: the target sentence, the outcome labels and severities, the network hints;
  - a component test of `ScriptArgumentInput`;
  - **`e2e/connectors.spec.ts`:** the area opens on *Calls*; add a Linux connector on the seeded simulator, its script taken from the library with the selector; expose an argument with a pattern; set a network; show and copy the key; call it with Playwright's `request` from inside the network, wrong first (422), then right (200); see both calls on *Calls*, the right one first as active, then processed; screenshots.

## 17. Security and privacy

- ***Connectors: Manage* is a strong right.** It decides which text runs on which server, and as which account. It is a right of its own (D2), the migration grants it to no group, and every change is audited with the text's hashes.
- **The server's own policy stays final** (R15). An agent that refuses a script refuses a connector's call too. A connector's text, taken from anywhere, is still checked by the agent: its hash against `allowed_hashes` or the registered scripts, its account against the allowed ones.
- **The target as text resolves at each call** (D6). That is the point of it, and it has one consequence: if a host is renamed so that the name finds another server, calls go there. Two servers with the name refuse the call (`server_ambiguous`). *Calls* shows the server each call found, and the overview shows the server the name finds now.
- **The key:**
  - 256 random bits;
  - one per caller (D9). **A leaked key opens every connector granted to its caller,** which is the price of one key. The caller's networks stand behind it, the grants limit it to that caller's connectors, and rotating it stops it for all of them at once;
  - only in a header (R11). Plain HTTP works (D5), and then the key and the arguments travel readable: use HTTPS where the way isn't trusted;
  - found by its hash, so the lookup's timing gives nothing away;
  - stored as a hash, with the copy for display under Data Protection;
  - every display audited;
  - rotation is immediate;
  - a valid key from the wrong address notifies (433).
- **The caller's networks** stand behind its key, and they are only as good as the address PNMT sees. Hence C0 first, `Proxy` validated at start, and the hint on the caller's page (§6.4).
- **Nothing tells an outsider what exists:** without a valid key every slug looks the same, and a caller sees only the connectors granted to it.
- **Arguments are the risky part, and code decides them:**
  - only the exposed ones;
  - the right JSON types;
  - no control or bidi characters;
  - patterns that match the whole value, without catastrophic backtracking;
  - the script's own sets and ranges, checked again by the agent;
  - splatted on Windows, and passed as an environment file on Linux, **never pasted into a command line**.
- **Functions don't widen anything:** the function's name is checked against the agent's own parse, travels in a file, and resolves only to a function. An older agent is never sent one.
- **The review fails closed unless the connector says otherwise** (D4), and the caller never sees its margins (§7). A call that ran unreviewed is marked as such. The review is a second line behind the code, not instead of it.
- **Secrets from the caller** wait in memory while their call is queued, are encrypted to the agent when the run starts, and are never stored, logged or shown to Jev. A restart loses them, and the call expires rather than running without them. Fixed secrets don't exist.
- **The output can hold internal data.** `ReturnOutput` keeps it in PNMT. In PNMT it is shown only to *Connectors: View* and, in the script runs' history, to *Servers: Manage*.
- **Every cost is bounded:**
  - the body (64 KiB), the values (4000 characters), the patterns (500 characters, 100 ms);
  - calls per hour, calls waiting and the waiting time per connector, one run per script and server, polling once per second per ticket;
  - failed keys per address;
  - calls with a wrong key are never stored.
- **The history holds the caller's arguments and notes.** They can be redacted, only the newest 100 000 calls are kept, and they are in PNMT's backups (`pg_dump`), which deploy/README.md says.

## 18. Decisions and open questions

Referred to elsewhere as D1 to D22. **Decided** marks the answers of the reviews of 2026-11-18 and after.

1. **The area is *3rd Party Connectors* in the UI, `Connector` in code, `/connector/v1` on the wire.** A connector is per purpose, not per caller.
2. **Decided: a right of its own.** The area *Connectors* (View, Manage) decides alone. *Servers: Manage* isn't needed as well, and the selectors read through lookups of their own (§11, §12.1).
3. **Decided: at least one network** for an enabled caller (it was a connector's before D9). *Anywhere* is `0.0.0.0/0` and `::/0`, entered deliberately and shown as a warning (§6.3).
4. **Decided: per connector** (*When the review can't run*): **Refuse**, the default for a new connector, or **Run unreviewed**, marked as such in the call, in *Calls* and in the answer (§7.4).
5. **Decided: plain HTTP works,** as for the sensor API. There is no HTTPS check and no setting for it (§4.1).
6. **Decided: the target is text, with no hard links.** The connector keeps its own copy of the script, and the server's and the account's names. Selectors fill them from the library, the servers and the accounts, and a call resolves them when it arrives. Reconfiguring the agent, or changing the library, doesn't break or change a connector (§3.3, §5.5).
7. **Decided: calls are queued,** and the queue is logged and stored the same way as every call: it is the calls table, so the data model stays one table (§9.1, §10.4). One server per connector; several servers, or a server chosen by the caller from a list, are later (§19).
8. **Decided: line breaks only where enabled.** A text argument's *Multi-line* switch (off by default) allows line breaks and tabs; every other control character stays refused (§5.2).
9. **Decided: one key per caller.** A *caller* is an entry of its own (the shop's back office, the ticketing system) with one key, its networks and the connectors granted to it. Its key opens exactly those connectors (§6.1). A leaked key therefore opens all of that caller's connectors and no others, and rotating it stops them all at once (§17).
10. **Decided: the output goes back only when configured.** `ReturnOutput` is off for a new connector, and switched on per connector (§4.4).
11. **Decided: the pattern is in the error message** of a value that doesn't match it, and in the caller's description (§4.3, §4.7, §5.3).
12. **No MCP tools and nothing for Cookie** in this plan (§15).
13. **The ticket is the call's id,** a random GUID, valid only with the key of the caller that made the call. No separate short ticket number: it would need its own guessing protection.
14. **Functions are real functions** (§8), called by the agent, not a convention inside the script. Until C5 ships, a dispatcher script with a fixed `-Action` argument gives the same with no agent change (§5.1).
15. **Decided: one run per script and server at a time,** whoever starts it (§9.2, §9.5). A function call still runs the script's top level first (dot-sourcing, `source`). That is allowed, with the findings of §8.1 and §8.2, and the one-at-a-time rule means two runs never share it.
16. **Decided: the newest 100 000 calls** of all connectors together are kept, with no age limit, and the runs of the calls that go with them. Active calls are never deleted (§10.4).
17. **Runs that never finish are closed for everyone** (§9.4), not only for connectors. It fixes the run dialog's endless polling too.
18. **C0 ships first, as 1.14.3,** because the bug affects every installation behind a Docker proxy today.
19. **Decided: the area has tabs, and opens on *Calls*,** every active and processed call of every connector. *Connectors*, *Callers* and *Documentation* are the other three (§13.1, §13.2).
20. **Decided: a background task handles the calls.** The endpoint checks a call and stores it, and the worker does everything after that: review, target, turn, start, outcome. The calls table is its work list (§9.1).
21. **Open: the networks belong to the caller,** with its key (§6.1). Proposed: only there. A connector that should be reached from fewer places than its callers' networks is granted to a caller with fewer networks. Networks per connector, on top of the caller's, are in §19 if that proves too coarse.
22. **Decided: a limit over all connectors,** `Connectors__MaxRunningCalls`, 10 by default: at most that many connector calls run at once. Beyond it, calls wait in the queue, oldest first, with `waitingFor: capacity`. Runs from the UI don't count (§9.5).

## 19. Later

- **More connector types,** on the same key, networks, review, limits, tickets and history:
  - **MikroTik command:** a RouterOS script on a router, through the job runner (docs/mikrotik-plan.md §8);
  - **UDP node command** (docs/udp-iot-plan.md);
  - **read state:** a host's or a sensor's state for a dashboard elsewhere, narrower than a read token;
  - **write an event or a note:** a narrower `/event`, or a note on a host.
- **A callback** when a ticket becomes final: a URL per connector, a body signed with HMAC, and retries.
- **An approval step:** a call waits in *Decisions* for a person, and the caller polls its ticket. A four-eyes mode for risky scripts.
- **A review with reasons** by the main model or Seraph, after Jev, for connectors that want a written judgement.
- **Several servers per connector,** or a server chosen by the caller from a list the admin allows.
- **Keys:** an overlap period when rotating; an expiry; HMAC-signed requests or mTLS for callers that can do them.
- **Networks per connector,** on top of the caller's, if granting by caller proves too coarse (D21).
- **Limits per caller,** such as calls per hour over all of a caller's connectors.
- **A vault for fixed secrets,** encrypted to the agent at call time.
- **Arrays and objects as arguments,** and multi-line values (D8).
- **An OpenAPI document per connector,** generated from its description.
- **A read tool** for the call history, over MCP and for Cookie.

## 20. Build order

| Phase | Content | Size |
|---|---|---|
| **C0** | `Proxy=docker` trusts `172.16.0.0/12`; `Proxy` as a validated list; `ForwardedHeadersTests`; deploy/README.md; **release 1.14.3** (§6.4) | small |
| **C1** | The model: `ConnectorType`, the entities and the migration `Connectors`, `agent_script_runs.ConnectorId` and `Function`; `ConnectorOptions`; `Area.Connectors`; `AuditActor.Connector` and the events; the system sensor and its codes; `LAST_CONNECTOR_WORK`. **Callers and grants** (§6.1), their keys (§6.2) and networks (§6.3), the keys and networks pure first. The admin API without `check` (§11), callers included, with `EndpointAuditTests`, `AuditModelTests`, `GroupsTests` and `ConnectorCallerTests` | medium |
| **C2** | The call: `AgentScriptService.StartAsync` taken out of `RunAsync` and `RunLocalAsync` (§9.2); the target as text, resolved at each call, and the connector's text in the parse pipeline with the arguments' reconciling (§3.3, §5.5, §10.6); `ConnectorPaths`, `ConnectorEndpoint` and its checks, storing calls as queued (§4.3); the arguments (§5); **`ConnectorWorker`**: the queue, the target, one run per script and server, the limit over all connectors, expiry, cancelling, lost runs and the retention (§9.1, §9.4, §9.5); `ConnectorSecretVault`; wait and ticket mode with `ConnectorCallSignals` (§9.3); idempotency and dry runs; the limits and `ConnectorGuard`; the description; `POST …/check`, `…/parse` and the lookups; the seed. `ConnectorArgumentsTests`, `ConnectorTextTests`, `ConnectorEndpointTests`, `ConnectorWorkerTests` and `ScriptRunsLostTests`. **Whole scripts only**: functions wait for C5 | large |
| **C3** | The review (§7): the state, the questions, the threshold, the fallback per connector, the budget's check, the stored answers, 433, *Try the review*. `ConnectorReviewTests` | small |
| **C4** | The web UI (§13): the area's tabs with *Calls* as the main view, *Connectors*, *Callers* and *Documentation*; *Add connector…* with the selectors; *Add caller…*, the caller's page and `NetworksEditor`; a connector's six tabs; `ScriptArgumentInput` and `CodeExamples` taken out; the runs' history and the library's hint; the audit's actor; `schema.d.ts`; the unit tests | large |
| **C5** | Functions (§8): `ScriptFunction`, `ParsedScript.Functions` and `RunScriptRequest.Function`; `BashScriptHeader`'s `functions` and findings; the PowerShell parser and wrapper; the Linux runner; both capabilities; the parse again for versions without functions; the run dialog's *Function*; the simulator. `ScriptFunctionTests`; the agents' checklists on real machines; **a new agent release** | medium |
| **C6** | docs/connectors-api.md, `DocsView`, both sensor plans' script sections, deploy/README.md and .env.example; `e2e/connectors.spec.ts` and the screenshots; the release notes for 1.15.0 (the new area granted to no group; agents need the update for functions) | small |

- **C1 to C4 make a working feature** for whole scripts, registered scripts included, with fixed arguments for dispatcher scripts. C5 can follow in the same release, or in the next one if the agents' release isn't ready.
- **A migration means a minor version,** per the release recipe: 1.15.0.

## 21. What was built

Built on 2026-11-19 in one go, C0 to C6, as the plan says, except for the points below. The tests are in `PNMTD.Tests/Connectors/` (pure, endpoint, worker, review, text, functions), `ForwardedHeadersTests`, `DevSeedTests`, and the web's `connectorFormat.test.ts`, `ScriptArgumentInput.test.ts`, `TakeScriptDialog.test.ts` and `e2e/connectors.spec.ts`.

- **C0 is in 1.15.0,** not in 1.14.3, which had shipped already. `ProxySetting` parses the list, `docker` is 172.16.0.0/12, and a bad entry stops the start.
- **`connector_arguments.Parameter`** (jsonb, not in §10.3) holds each parameter as the parse found it at the last reconcile. The endpoint checks types, sets, ranges and which arguments are secret from it. A call is therefore checked fully even while a new PowerShell text waits for its parse, and a secret can never land in the table by being mistaken for a plain argument.
- **Two refusals were added to §4.3:**
  - `script_invalid` (in the queue): the text has errors, no agent could parse it, or it lacks the connector's function;
  - `arguments_need_review` is also answered **at the endpoint**, as 409, while the arguments wait for an admin (§5.5). The worker's W2 still refuses calls queued before the change.
- **A dry run always ends as `checked`.** `refusedBy` and `message` say what would have refused it, the review included, so its caller reads one state.
- **A blocked address is answered before the key is read** (§6.5's order), so check 3 comes before check 2.
- **Two endpoints were added to §11:** `GET /connector-calls/figures`, the top of *Calls*, and `GET /connectors/docs`, docs/connectors-api.md, embedded at the build (the Dockerfile copies that one file of `docs/`).
- **`AgentScriptService.StartAsync(spec, busyMode, gate, withRow, audit)`:**
  - `busyMode` is `Conflict` for one server (409 `script_running`), `Refuse` for a batch (a refused row per busy server), and `Wait` for a connector's call;
  - `gate` takes the limit's advisory lock and counts the running calls;
  - `withRow` makes the call running in the run's transaction, after the run's row is written;
  - one script's lock is the first 8 bytes of a SHA-256 of the server and the script's lower-case name.
- **Two test settings:** `Connectors__PassSeconds` (the timer, 5) and `Connectors__Nudge` (true) let the tests drive the worker by hand. The simulator got `HoldRuns`, which acks a run and holds its result, and `--no-functions`.
- **The UI:**
  - the function is a text field with a selector beside it, like the other fields of the target;
  - `CodeExamples.vue` wasn't taken out of `SensorUrlPanel.vue`: the *Usage* tab builds its own three examples (`connectorFormat.examples`);
  - `ScriptArgumentInput.vue` was taken out of the run dialog, as planned.
- **The e2e test** calls a connector on the seeded web-01, whose agent isn't connected in the e2e stack: the right call is answered 202, queued for the server, instead of 200.
- **Bash's parse always lists its functions,** an empty list when there are none: only a PowerShell parse by an older agent has none. The one-time re-read of §8.3 is the custom migration `CustomMigration_ReparseBashHeaders`.
- **Functions on the agents:**
  - the Windows agent announces `parse.functions.v1` always, and `scripts.functions.v1` with scripts;
  - its parser lists top-level functions (`FindAll(…, $false)`) with the two findings;
  - its wrapper dot-sources the script and resolves the name with `Get-Command -CommandType Function`;
  - the Linux agent announces `scripts.functions.v1` with scripts, checks the function against its own header, and runs `run.sh` (0400) with `PNMT__FUNCTION` in the environment file.

  Both need the checklists of docs/windows-sensor-plan.md §16 and docs/linux-sensor-plan.md §17 on real machines before the agents' release.
- **1.15.1:** an agent's answers (acks, script and parse results) counted against `Agents__MessagesPerMinute` (60), so a server with many runs in a minute had results dropped, and the runs stayed *sent* until they were lost. They count with the reports (300) now. `AgentAnswersTests` checks it.

