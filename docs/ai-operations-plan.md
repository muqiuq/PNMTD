# Plan: AI for mails and events, decisions, notifications, and operations tools (package one)

| | |
|---|---|
| Status | **Revision 3, for review.** Nothing is built. The review's answers are in §20.1, and the one question still open is in §20.2 |
| Date | 2026-11-13 |
| Revision | 3: **the second review applied** (§20.1):<ul><li>importance scopes mean ***at least***;</li><li>the second MikroTik update method is RouterOS's own **`install`** (download and install in one go, as WinBox does), and **the free-storage check becomes an option**. The SFTP upload is set aside (§12.2).</li></ul>2: **the review of revision 1 applied** (§20.1):<ul><li>Jev comes **through OpenRouter**, with the same key;</li><li>the models are **`deepseek/deepseek-v4.1-flash-1017`** (main) and **`z-ai/glm-5.3-flashx`** (verification);</li><li>mail contents may go to the AI;</li><li>a delete rule keeps the log row;</li><li>assignments by importance or host are **lasting scopes**;</li><li>system sensors are **created automatically**;</li><li>**the budget is set in `.env`** (30 USD by default), with **warnings at 80 % and 90 %**;</li><li>decisions need the User role;</li><li>the analysis runs hourly when something changed, and **what it sends is chosen in the notifications**, through one sensor per severity;</li><li>RouterOS alerts only for **a very bad issue fixed, on a router that is behind**.</li></ul> |
| Scope | Package one of docs/ideas.md:<ul><li>**AI in general**, through OpenRouter (an OpenAI-compatible API) with a **main model**, a **verification model**, and a **typesafe model** (TypeSafe's Jev) that screens untrusted text for prompt injection first. Every request is logged, and failed requests are retried on a schedule.</li><li>**Mails with AI:** a mail that no rule handles is analysed, and the AI proposes a rule: one that writes an event, one that deletes, or one that hands every matching mail to the AI.</li><li>**Decisions:** one page with everything a person has to decide, for mails and for later uses.</li><li>**A situation analysis:** anonymised events, system state and the uplink, checked for a big problem, a pattern or a bigger issue.</li><li>**RouterOS advisories:** MikroTik's release feed, and a Pushover alert when a critical fix is pending on a router.</li><li>**Importance and a Notification Manager:** Low, Normal or High on every host and sensor, and notification rules assigned in bulk.</li><li>**Operations tools:** an uplink page, a debug page (test Pushover, test mail), a second MikroTik update method, and a spacing fix in the routers list.</li></ul> |
| Non-goals | AI that acts without a person's decision. The one exception is a mail rule a person set to *AI every time*, which can only write that rule's events on that rule's sensor (§6.4). Tools, function calls or web access for the models. A chat with the AI. Training or fine-tuning. Sending host or sensor names, notes, locations, recipients or mail contents to the AI in the situation analysis (§9.1). Local models (any OpenAI-compatible URL is accepted, but only OpenRouter is tested). Changes to the frozen sensor API. Windows servers (package two, docs/windows-sensor-plan.md) |
| References | docs/webui-migration-plan.md (architecture, conventions, roles). docs/mikrotik-plan.md (§5.4 latest versions, §7 updates). OpenRouter's documentation: structured outputs, provider routing, usage accounting. TypeSafe's Jev API (`api.typesafe.ai`). MikroTik's release feed `https://cdn.mikrotik.com/routeros/latest-stable-and-long-term.rss` |

---

## 1. Summary

AI comes in as one shared service (`PNMTD/Ai/`). Every use is a **typed task**: fixed instructions, the input as data, and an output that must match a JSON schema generated from a C# record. Untrusted text passes through **checks in code, then the typesafe model, then the main model, then the verification model, then checks in code again**. Nothing durable happens without a person, except what a person set up in advance.

| Topic | Decision |
|---|---|
| Provider | **OpenRouter**, through the OpenAI Chat Completions API (`/api/v1/chat/completions`), with strict structured outputs (`response_format: json_schema`, `strict: true`), and with `provider.require_parameters` so that only endpoints that support them are used. The base URL is a setting, so any OpenAI-compatible server works (§3.2) |
| Models | **Main:** `deepseek/deepseek-v4.1-flash-1017`. **Verification:** `z-ai/glm-5.3-flashx`. Both are LLMs, from two vendors. **Guard:** TypeSafe's **Jev** (`typesafe/jev-1.14`), **also through OpenRouter**, which returns only probabilities for questions defined in advance, and so cannot be talked into writing anything else. All three are set in `.env`; one OpenRouter key serves them all (§3.2) |
| Prompt injection | **Defence in depth** (§3.4): normalising and scanning in code, Jev's verdict, strict schemas with enums and ids that code supplied, a second model's verdict, then code validation (for example: a proposed rule must match the mail, and is tested against the last 500 mails). **A person decides** |
| Retries | Every AI use is a work item. **On failure:** every minute, 5 times. **Then a notification**, then after 1 hour, after 3 more hours, and then every 24 hours (§3.5) |
| Request log | **Every request**, raw: the request body, the response body, the duration, the tokens and the cost (OpenRouter's `usage.cost`). **The newest 10,000 are kept.** An Admin page refreshes every 5 seconds (§3.6) |
| Decisions | **One generic inbox** (`decisions`) with a kind, a typed proposal, the options, the state, and who decided and when. Kinds in this package: a mail rule, a suspicious mail, and an analysis finding (§5) |
| Mails | **A mail that no rule handled within 5 minutes is analysed.** The AI proposes a rule as a template, and **code turns the template into regular expressions**. New rule actions: **Event** (today's), **Delete**, **Ignore**, and **AI every time** (§6) |
| System sensors | PNMT's own conditions become **sensors of a new type `SYSTEM` (90)**, **created automatically** on a host "PNMT":<ul><li>Uplink;</li><li>AI;</li><li>AI budget;</li><li>AI analysis, with one sensor each for critical, warning and info findings;</li><li>RouterOS advisories.</li></ul>Notifications for them use the normal rules, importance and history. That is how "inform the admin via Pushover" works (§4) |
| Uplink page | **Every check is recorded** (endpoint, outcome, latency, the reason for a failure), along with outages and per-endpoint statistics. Admins get a *Check now* button. The uplink's state survives a restart (§7) |
| Debug page | **Admin → Debug**: send a test Pushover and a test e-mail, and see the provider's answer. The notification providers return a result, and e-mail moves to MailKit (§8) |
| Situation analysis | **Every hour when something changed** (the interval is set in `.env`), and at once on a storm. Names become `H3`/`S17`, and addresses, URLs and mail addresses are replaced in code. **New findings become events on the system sensor of their severity**: critical (verified), warning, or info. **Which of them are sent is chosen in the notifications.** Warnings and critical findings also become decisions (§9) |
| RouterOS advisories | **The RSS feed every hour**, with its URL in `.env`. Code finds CVEs and security keywords; the AI rates the release, and the verification model confirms "critical". **An alert only when a release fixes a very bad issue** (exploited, a zero-day, or remotely exploitable without credentials) **and a router is behind it**: event 422, which a Pushover rule sends (§10) |
| Importance | **Low, Normal or High on every host and sensor.** A sensor without its own importance takes its host's. Pushover's priority follows: −1, 0 or 1 (§11) |
| Notification Manager | **A matrix of sensors × rules**, with bulk assignment by selection, by sensor, by host, and by importance. **A host or importance assignment is a scope that also covers sensors added later.** A coverage view shows the sensors no rule reaches (§11) |
| MikroTik updates | **A second method: *Download and install***, RouterOS's `install` command, which downloads and reboots in one go, as WinBox's *Download & Install* does. **The free-storage check becomes an option** (on by default), because it blocks 16 MB-flash routers, which RouterOS updates from RAM. Both are chosen in the update dialog (§12.2) |
| Budget | **`Ai__Budget__Usd`, 30 USD per month by default.** **A warning at 80 % and at 90 %**, as events on the *AI budget* sensor. At 100 %, AI work waits until the next period (§3.7) |
| Off by default | **AI is off** until `Ai__Enabled=true` and a key are set. Without AI, everything works as today, and the advisories use their code checks only |

---

## 2. Requirements, starting point and alternatives

### 2.1 Requirements

| # | Requirement (docs/ideas.md, package one) | Where |
|---|---|---|
| R1 | Process mails with AI where rules fail. The AI analyses them and gives the user decisions: add a rule (also one that only deletes a mail), or a rule that hands every matching mail to the AI | §6 |
| R2 | A typesafe model first, against prompt injection; some of the prompt-injection defence in code | §3.4 |
| R3 | Two models, main and verification, and one typesafe model | §3.2 |
| R4 | AI is used for other functions too, so it is built generally | §3 |
| R5 | A decisions page with every decision the user has to make, not only from mail | §5 |
| R6 | A page with the state of the uplink check, with details | §7 |
| R7 | A debug page with tools; the first two send a test Pushover and a test mail | §8 |
| R8 | The AI gets anonymised information about current events and system state, including the uplink check, and determines whether there is a big problem, a pattern or a bigger issue | §9 |
| R9 | MikroTik's release RSS (the link editable in `.env`): a very important pending update (a zero-day) is sent to the admin by Pushover | §10 |
| R10 | Importance (Low, Normal, High) on every host and sensor. A Notification Manager page assigns notifications globally: by importance, by host, by sensor, or by selection | §11 |
| R11 | Routers tab: more space between the check mark and the name in the Router column | §12.1 |
| R12 | A second MikroTik update method that works where the current one reported too little memory and WinBox succeeded, selectable in the update screen | §12.2 |
| R13 | Every AI request logged in the database, with everything sent and the raw response, the duration and the cost. The newest 10,000 are kept, and an admin page shows them live | §3.6 |
| R14 | Failed AI requests are repeated every minute, 5 times, then the user is notified, then after 1 hour, 3 hours, and every 24 hours | §3.5 |
| R15 | OpenRouter: the standard OpenAI API, and whatever the typesafe model needs | §3.2 |

### 2.2 Design goals

- **AI proposes, people decide.** Model output is data, never instructions. Everything a model returns has a schema, enums, limits, and ids that code gave it.
- **Fail closed.** No AI result means today's behaviour: the mail stays unprocessed and visible in the mail log, and no event is invented. A missing guard model means no AI on untrusted text.
- **One way to notify.** PNMT's own conditions are sensors (§4), so rules, importance, history, the dashboard and the Notification Manager apply to them unchanged.
- **Traceable.** Every request, every work item and every decision is kept, with who and when.
- **Bounded cost.** A daily budget, dedupe before any call, and no analysis while nothing has changed.
- **The code base's structure:**
  - services in `Services/Domain`;
  - hosted tasks with `RunGuard`;
  - an options section with its DI setup;
  - `AuthEvents` for security-relevant actions;
  - every endpoint in `EndpointAuditTests`;
  - one admin page per area, with tabs.

### 2.3 Alternatives

| Option | Why not |
|---|---|
| Free-text LLM answers, parsed afterwards | Parse errors, and output that can carry injected instructions onwards. Strict structured outputs and code validation instead (§3.3) |
| One model only | No second opinion. A verification model from another vendor makes correlated mistakes less likely (§3.2) |
| Prompt-injection detection by an LLM only | The detector has the same weakness as what it guards. Jev answers only predefined questions with probabilities. HiddenLayer showed in 2026 that Jev can still be fooled, less often than an LLM, so it is one layer of several, never the only gate (§3.4) |
| The AI writes regular expressions | Catastrophic backtracking (ReDoS), and expressions that match far too much. The AI returns a template and verbatim phrases, and **code builds escaped expressions**, tests them, and runs them with a timeout (§6.3) |
| Direct OpenAI or Anthropic APIs | OpenRouter gives one key, many models, and an OpenAI-compatible API. Any other OpenAI-compatible base URL still works |
| An "admin recipient" setting for system alerts | It would duplicate the notification rules. System sensors reuse them (§4) |
| Bulk assignment only as one-time links | Sensors added later would be missed. Scopes cover them (§11.2) |
| Server-sent events or WebSockets for the live AI log | The UI already polls with TanStack Query (`refetchInterval`, 5 s in `LastPacketsTab`). Polling is enough at this rate |

### 2.4 What the code base already provides

| Building block | Where | Used for |
|---|---|---|
| IMAP fetch: Inbox → `RECEIVED` → `ARCHIVE`, deleted after 365 days | `MailInboxCheckTask` (60 s) | The delete action (§6.4) |
| The mail log, the newest 50,000 | `maillogs`, `CleanUpMailLog`, `GlobalConfiguration.MAXIMUM_NUM_OF_MAILLOG_ENTRIES` | Mail AI state and disposition (§6.5) |
| Mail rules: regular expressions on sender, subject and body; OK/FAIL tests; message extraction; an event on a sensor | `mailinputrules`, `MailProcessTask` (60 s), `MailService` | New actions, AI proposals, the timeout fix (§6) |
| Notification rules (a recipient and a trigger type) linked to sensors; providers chosen by the recipient's form | `notificationrules`, `NotificationRuleSensorEntity`, `NotificationTask` (10 s), `PushoverNotification`, `EMailNotification`, `HttpNotification` | Scopes, importance, test sends, Pushover priority (§8, §11) |
| The uplink check: 2 or 3 URLs, an HMAC challenge every 120 s, down after 3 failures in a row; monitoring paused while down | `UplinkCheckTask`, `TaskRunDecisionMaker`, `SystemService.UplinkAsync` | The uplink page, its history, the Uplink sensor (§7) |
| Events written by PNMTD with the sensor API's rules: lock, dedupe, 2000-event limit | `PNMTD/Services/Events/SensorEventWriter.cs` | System sensors' events (§4) |
| A sensor type with its own semantics | `SensorType`, `SystemService.SensorTypes()` | `SYSTEM = 90` (§4) |
| RouterOS's latest versions every 6 hours | `VersionCatalog` (`upgrade.mikrotik.com`) | The advisories run next to it (§10) |
| The update job's steps, options and job log | `JobRunner.UpdateAsync`, `UpdateJobOptions` | The *Download and install* method and the optional storage check (§12.2) |
| Admin-only actions in the audit log | `AuthEvents` in `UserAccounts.cs` | Debug sends, AI settings (§14) |
| Roles, policies and the endpoint matrix | `Policies`, `EndpointAuditTests` | Every new endpoint |
| Server tables, live polling, tabs, status badges, the uptime bar | `ListTable`, `useServerTable` (`refetchInterval`), `TabNav`, `StatusBadge`, `UptimeBar` | The new pages (§15) |
| A fake clock for tests | `Microsoft.Extensions.TimeProvider.Testing` | Retry schedules (§18) |
| Demo data | `pnmtd dev seed` | Seeded decisions, AI log, system sensors |

### 2.5 What today's code gets wrong, fixed on the way

| Finding | Where | Fix |
|---|---|---|
| Rule expressions run **without a timeout**. One bad expression can hang mail processing (ReDoS), and AI-proposed rules would make this likelier | `MailProcessTask.IsMatch` (`new Regex(…, Compiled)`) | A 250 ms match timeout. A timeout counts as no match and is noted in the process log (§6.4) |
| An unmatched mail stays `Processed = false` **for ever** and is matched again every minute against every rule | `MailProcessTask.doWork` | An AI state, and a decision per mail shape (§6.5) |
| **Every** matching rule fires, in no defined order | `ProcessSingleEntry` | Kept for event rules. Delete and Ignore rules apply only when no event rule matched (§6.4) |
| Providers **return nothing**. A failed Pushover or mail exists only in the log | `INotificationProvider.SendNotification` (`void`) | `SendAsync` returns a result (§8.3) |
| E-mail uses `System.Net.Mail` with `EnableSsl` on every port but 25. **Implicit TLS on port 465 does not work** with it | `EMailNotification` | MailKit, already a dependency, with `SecureSocketOptions.Auto` (§8.3) |
| The **Pushover user key and the message are logged** at Information level | `NotificationService.SendNotification` | The provider and the rule's name only |
| The uplink's **failure count lives in memory**, and nothing records why a check failed | `UplinkCheckTask` | `uplink_checks`, and the state in key values (§7.2) |
| An update **refuses below 24 MiB of free storage**, which a 16 MB-flash router never has. Yet RouterOS keeps a downloaded package in RAM on such devices, and WinBox's update succeeds | `JobRunner` step 1, `MikrotikOptions.MinimumFreeStorageBytes` | §12.2 |

---

## 3. The AI foundation

### 3.1 Where AI is used

| Use | Input | Untrusted? | Models | Output | What happens with it |
|---|---|---|---|---|---|
| Mail analysis (§6.3) | An unmatched mail, the candidate sensors, related rules | **Yes** (the mail) | Guard → Main → Verify | A typed rule proposal | A decision |
| *AI every time* rule (§6.4) | A mail that matched the rule | **Yes** | Guard → Main (→ Verify, per rule) | OK, failure or unknown, and a short message | An event on the rule's sensor |
| Situation analysis (§9) | Anonymised state | Partly (sensor messages) | Guard on the messages → Main → Verify for critical findings | Findings | Events per severity; decisions for warnings and critical findings |
| RouterOS advisories (§10) | A release's changelog | **Yes** (an outside feed) | Guard → Main → Verify for critical | Severity, zero-day, exploited, CVEs | An event when a very bad issue is fixed and routers are behind |
| Debug: test the AI (§8.4) | A fixed prompt | No | Each model | A pong | Shown |

### 3.2 Providers and models

**Two clients, behind one service (`AiService`):**

- **The chat client** speaks OpenAI Chat Completions.
  - `POST {Ai:BaseUrl}/chat/completions`, default base `https://openrouter.ai/api/v1`, with `Authorization: Bearer {Ai:ApiKey}`.
  - The body has `model`, `messages`, `temperature: 0`, and `max_tokens`, plus `response_format: { type: "json_schema", json_schema: { name, strict: true, schema } }`.
  - For OpenRouter it also has `provider: { require_parameters: true, data_collection: "deny" }`.
    - **`require_parameters`** routes only to endpoints that support structured outputs; without it, OpenRouter treats `response_format` as a preference.
    - **`data_collection: "deny"`** excludes providers that store or train on prompts. Zero data retention routing is checked in A0.
  - **What is read from the response:**
    - `choices[0].message.content`, which is JSON;
    - `id`, the generation id;
    - `model`, the model that actually answered;
    - `provider`;
    - `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`, `cost`, `prompt_tokens_details.cached_tokens` and `completion_tokens_details.reasoning_tokens`. **OpenRouter always includes `usage`.**
- **The decision client** calls Jev **through OpenRouter**, with the same key and account. **No TypeSafe account is needed.**
  - **The endpoint:** OpenRouter's Decisions API, `POST https://openrouter.ai/api/alpha/decisions`, which OpenRouter still marks *alpha*. Or the System One surface, `POST https://openrouter.ai/api/v1/systemone`, which takes TypeSafe's own format.
    - **A0 picks one.** The path is a setting (`Ai:Guard:Path`), so moving from alpha to a stable path is a configuration change.
  - **The model:** `typesafe/jev-1.14`, **pinned**. The alias `~typesafe/jev-latest` would change under us; a new version is a deliberate change in `.env`.
  - **The request** has `model`, a `state` (an array of texts), and `questions`, keyed by our own names, each with a `type`:
    - `noul`: a statement, answered with a probability from 0 to 1;
    - `choice`: options with descriptions, answered with the choice, a probability per option, and a confidence;
    - `score`: ordered levels.
  - **The response** has the answers under our question names, and `usage.cost` in USD (input tokens only; output is free).
  - **Size:** up to 262,144 tokens. But **above about 32,000 bytes of text, Jev runs in a slower and less accurate long-context mode**, so the guard's `state` is capped at 30,000 bytes (the head and the tail of a longer text).
  - **A0 checks:** the authentication header (presumably OpenRouter's usual bearer token), the exact field names of `choice` and `noul`, and the cost per mail.

**Roles:**

| Role | Setting | Used for | Notes |
|---|---|---|---|
| Main | `Ai:MainModel` = **`deepseek/deepseek-v4.1-flash-1017`** | Proposals, analysis, ratings | DeepSeek. 0.038 USD per million input tokens, 0.50 per million output. Supports `structured_outputs`. Context 1M |
| Verification | `Ai:VerifyModel` = **`z-ai/glm-5.3-flashx`** | A second opinion on proposals and critical findings | Z.ai, **another vendor than the main model**, as intended. 0.37 USD per million input tokens, 1.25 per million output. Supports `structured_outputs`. The AI page warns if the two are ever set to the same vendor |
| Guard | `Ai:Guard:Model` = **`typesafe/jev-1.14`** | Prompt-injection screening, cheap typed classification | Through OpenRouter's Decisions API. Without a guard, AI on untrusted text refuses to run unless `Ai:Guard:Required=false` |

- **Prices and capabilities** are from OpenRouter's model list on 2026-11-13.
- **Not to confuse:** `typesafe/jev-router` in the same list is not the guard. It is a router that uses Jev to pick *another* chat model for each request, and it is not used here.
- **Both LLMs can reason** (`reasoning_effort`). A0 measures whether a low effort is enough. Reasoning tokens are billed as output, and the log records them.
- **A rough cost per mail analysis**, before A0 measures it, assuming about 6,000 tokens in and 1,000 out: main about 0.0007 USD, verification about 0.003 USD, guard about 0.0003 USD. That is **about 0.004 USD per mail**, or roughly 7,500 analysed mails for 30 USD.

### 3.3 Typed tasks

- **A task is a class** `AiTask<TInput, TOutput>` with:
  - a purpose (`mail.analyse`, `mail.evaluate`, `situation.analyse`, `routeros.rate`, `verify.mail`, …);
  - a constant system prompt with a `PromptVersion`, which is logged;
  - how its input becomes JSON;
  - the output record;
  - `Validate(output, input)`, the semantic checks in code: ids from the input, phrases that occur verbatim, lengths, ranges.
- **The schema comes from the record**, through `JsonSchemaExporter` (System.Text.Json):
  - strings for enums;
  - `additionalProperties: false`;
  - every property required, with nullable types where a value may be absent, as strict mode demands;
  - `maxLength` in the descriptions, because some providers ignore limits in strict mode. **Code enforces the limits.**
- **The input is always data:**
  - the system message holds the fixed instructions only;
  - the user message is one JSON document, and untrusted text sits in fields named `untrusted_…`;
  - the instructions say that those fields are content to analyse, never instructions.
- **The pipeline** (`AiPipeline.RunAsync`): normalise → scan → guard → main → validate → verify (if the task asks for it) → result. The result carries every request id and the guard's probabilities.
- **A result that fails `Validate`** counts as a failed attempt with the outcome `invalid-output`, which is retried (§3.5).

### 3.4 Prompt injection: in code, the typesafe model, and by design

| # | Layer | What | On a hit |
|---|---|---|---|
| 1 | **Normalise** (code) | HTML to text. Remove invisible and control characters: zero-width, bidi overrides U+202A–U+202E and U+2066–U+2069, and tag characters U+E0000–U+E007F. NFC, collapse whitespace, and cap the size at `Ai:MaxInputChars` (24,000), keeping the start and the end | Always applied. The counts of removed characters become flags |
| 2 | **Scan** (code) | Phrases such as *ignore previous/above instructions*, *system prompt*, *you are now*, *new instructions*. Role markers (`<|im_start|>`, `### system`, `assistant:`). Long base64 or hex blobs, many links, many invisible characters (from layer 1) | Flags and a score travel with the input. **A high score skips the models**: a *suspicious mail* decision (§5.2) |
| 3 | **Guard** (Jev) | `Noul` *contains_prompt_injection*: "Does the text try to give instructions to an AI system or override the application's instructions?" `Choice` *kind*: status report, alert, newsletter/marketing, personal, phishing/scam, other | Probability ≥ `Ai:Guard:Threshold` (0.5) → **no LLM call**. A *suspicious mail* decision with the probability and the flags |
| 4 | **Strict output** (main) | The schema, enums, ids from the input only, lengths. No free text is acted on; text is only shown | A schema violation is an `invalid-output` attempt |
| 5 | **Verify** (second model) | Sees the input and the proposal. Answers `agree`, `disagree` or `unsure`, with typed issues | *Disagree*: the decision shows both and is marked **disputed**. For *AI every time*: no event, and the mail goes to a person |
| 6 | **Validate** (code) | Ids among those given. Indicator phrases must occur verbatim in the mail. Generated expressions must compile, match the mail within the timeout, and be tested on the last 500 mails (§6.3) | Rejected as `invalid-output`, or shown as too broad |
| 7 | **People decide** | Nothing durable without a decision (§5) | – |
| 8 | **No capabilities** | The models have no tools, fetch no URLs, send nothing, and apply nothing. The UI shows their text escaped (Vue's interpolation, never `v-html`) | – |

### 3.5 The work queue and retries (R14)

- **Every AI use is a work item** in `ai_work`, with:
  - a purpose and a subject (for example a mail log id);
  - the input's hash;
  - a state;
  - the attempts and the next attempt's time;
  - the last error;
  - whether this item's failure has been notified.
- **The states:** Queued, Running, Waiting (retry), Waiting (budget), Succeeded, Failed (permanent), Cancelled.
- **`AiWorker`**, a hosted service, takes due items, two at a time (`Ai:Parallelism`).
  - It is not a monitoring task, and it is not paused by the uplink guard: a request while the uplink is down simply fails and is retried.
- **The schedule** (`Ai:Retry`, these defaults):

  | After failure of attempt | Next attempt | Also |
  |---|---|---|
  | 1–5 | 1 minute later | – |
  | 6 (the fifth retry) | 1 hour later | **Notification:** event **421** on the *AI* system sensor (§4) |
  | 7 | 3 hours later | – |
  | 8 and later | every 24 hours | – |

- **Retried:** timeouts, connection errors, HTTP 408, 429 and 5xx, 401, 402 (no credits) and 403 (a key the admin can fix), and `invalid-output`.
- **Waiting for the budget** (§3.7) is not a failure. It doesn't count as an attempt, and it doesn't write 421.
- **Not retried:** HTTP 400, which is PNMT's own bug. It fails at once, is notified at once, and the log keeps the body.
- **One outage, one notification.** 421 is written when the first item reaches its sixth failure, and further items don't add any.
  - The next success anywhere writes **220** "AI available again" on the same sensor.
  - The rule type decides about repeats, as for every sensor.
- **Stale work is cancelled:** the mail was handled in the meantime (a new rule matched it, or a person decided), or a newer analysis replaced this one.
- **An Admin can** *Retry now* or *Cancel* on the AI page (§15).

### 3.6 The request log (R13)

- **`ai_requests`**, one row per HTTP request, whatever its outcome. The columns:
  - when; the work item; the purpose; the role (main, verify or guard); the attempt;
  - the model asked for, and the model and provider that answered; the generation id; the prompt version;
  - **the raw request body** (the `Authorization` header is never stored) and **the raw response body**;
  - the HTTP status; the outcome (`ok`, `http-error`, `timeout`, `connect-error`, `invalid-output`, `blocked-by-guard`); the error;
  - the duration in ms;
  - the prompt, completion, reasoning and cached tokens;
  - **the cost**: OpenRouter's `usage.cost`, in USD, for the chat models and for Jev alike.
- **The bodies are gzip-compressed**, and capped at 1 MiB each, with a *truncated* flag.
- **Retention: the newest 10,000 rows** (`Ai:LogEntries`), removed by one `DELETE` after each batch of inserts.
- **Admin only**, because the bodies contain mail contents.
- **The page** is in §15: refreshed every 5 seconds, filtered by purpose, role, model and outcome. A request opens as its raw JSON on both sides, with the duration, the tokens, the cost and the work item's other attempts.

### 3.7 Costs

- **The budget is set in `.env`:** `Ai__Budget__Usd`, **30 USD by default**, per `Ai__Budget__Period`, **`month` by default** (calendar month in PNMT's time zone), or `day`. 0 means no limit.
- **The spending is counted in `ai_spending`:** one row per day, purpose and model, with the requests and the cost, kept 2 years.
  - It is updated with each request.
  - **It doesn't depend on the request log**, which keeps only 10,000 rows and so may hold less than a month.
- **Warnings and the limit**, as events on the *AI budget* system sensor (§4). Each is written once per period, when the spending first crosses the threshold:

  | Spent | Event | What happens |
  |---|---|---|
  | **80 %** | **425** "AI spending at 80 % of the budget: 24.10 of 30 USD this month" | Nothing else |
  | **90 %** | **426** "… at 90 % …" | Nothing else |
  | **100 %** | **427** "AI budget reached: work waits until 1 December" | **New attempts wait** until the next period, in the state *Waiting (budget)*. Running requests finish |
  | A new period | **220** "Below 80 % of the budget" | Waiting work runs again |

  - **Three codes**, so that a rule of the type *only failures on change* sends each step once.
  - **Which of them reach whom is set in the notifications**, as for every sensor.
  - An Admin can raise the limit in `.env` and restart. The waiting work then runs.
- **Dedupe before any call:**
  - mails that look like an open decision's mail attach to it (§6.2);
  - an unchanged situation is not analysed again (§9.2);
  - a release without security keywords is not rated (§10.3).
- **Spending is shown** on the AI page: this period against the budget, today, and the last 30 days, by purpose and by model.

### 3.8 Privacy

- **Mail analysis sends the mail** (sender, subject, body), and **the names of hosts and sensors** that could take the rule's event. The names are needed to propose the right sensor. **Accepted in the review** (§20.1).
- **The situation analysis is anonymised** (§9.1).
- **No data collection:** OpenRouter's `data_collection: "deny"` is on by default.
- **AI is off by default, and each use has its own switch:** `Ai:Mail:Enabled`, `Ai:Analysis:Enabled`, `Ai:Advisories:Enabled`.

---

## 4. System sensors

- **A new sensor type, `SYSTEM = 90`.** Its `Parameters` names the subsystem.
- **Created automatically** (decided in the review): at every start, PNMTD creates the sensors whose feature is configured and that don't exist yet. A feature that is configured later gets its sensor at the next start.
- **They live on a host named "PNMT"**, created with the first of them. They can be moved to another host and renamed.
- **One per subsystem**, by a unique index on the type and `Parameters`.
- **They can be disabled, but not deleted**: a delete answers 409 with "disable it instead".
- **The sensors:**

  | Parameters | Name | When it exists | Importance | Success | Problem |
  |---|---|---|---|---|---|
  | `uplink` | Uplink | `UplinkCheck` configured | High | 220 "Uplink OK (2 of 2 endpoints answer)" | **420** UPLINK_DOWN |
  | `ai` | AI | `Ai:Enabled` | High | 220 "AI available" | **421** AI_FAILING (retries exhausted, key refused) |
  | `ai-budget` | AI budget | `Ai:Enabled` | Normal | 220 "Below 80 % of the budget" | **425** AI_BUDGET_80, **426** AI_BUDGET_90, **427** AI_BUDGET_REACHED (§3.7) |
  | `ai-analysis-critical` | AI analysis: critical | `Ai:Analysis:Enabled` | High | 220 "No critical finding" | **423** AI_FINDING_CRITICAL (confirmed by the verification model) |
  | `ai-analysis-warning` | AI analysis: warnings | `Ai:Analysis:Enabled` | Normal | 220 "No warning" | **424** AI_FINDING_WARNING |
  | `ai-analysis-info` | AI analysis: info | `Ai:Analysis:Enabled` | Low | 220 "No new information", or **221** AI_FINDING_INFO, a success code because information is not a problem | – |
  | `routeros-advisories` | RouterOS advisories | `Mikrotik:Advisories:Enabled` | High | 220 "No critical RouterOS fix pending" | **422** ROUTEROS_SECURITY_UPDATE |

- **New status codes** in `PNMTStatusCodes`, `SystemService.StatusCodes()` and the docs:
  - **220–229** and **420–429** are free.
  - **220** is SYSTEM_OK, and **221** is AI_FINDING_INFO.
  - **420–427** are as in the table above.
- **Written through `SensorEventWriter`**, with its dedupe: a repeated, identical state only moves the time.
- **Their importance** (the table's defaults) can be changed like any sensor's (§11). With the Notification Manager's importance scopes, *at least High* then reaches only the critical findings and the outages, and *at least Normal* adds the warnings and the budget.
- The sensor form shows no fields for this type, only *Enabled*.
- **The sensor API is unchanged.** These sensors have tokens like every sensor, and the UI doesn't offer them.

---

## 5. Decisions (R5)

### 5.1 The model

- **A decision is a question PNMT asks a person**, with a proposal and options. One table, `decisions`, holds:
  - the kind and the state;
  - the role it needs;
  - the title, a summary, and a **typed payload** (jsonb, with the kind's version);
  - the source (its type and id) and a dedupe key;
  - the occurrences and the last time it was seen;
  - when it was created and when it expires;
  - when it was decided, by whom, and the choice;
  - the person's edits, and the result (for example the id of the rule created);
  - the AI work item.
- **The states:** Open, Accepted, Rejected, Dismissed, Expired, Superseded, Failed (applying failed; the error is kept).
- **Each kind is a class**, `IDecisionKind`, with:
  - its role;
  - its options for a payload (an id, a label, a description, and whether it opens a form);
  - `ApplyAsync(decision, choice, edits, user)`;
  - `IsStillRelevantAsync`, which supersedes a decision whose subject was handled elsewhere, for example by a new rule.
- **Dedupe:** one open decision per key. More of the same raise its occurrences and attach their subjects, such as the list of mail ids.
- **Expiry:** an open decision expires after 30 days (`Decisions:ExpireDays`). Its subjects stay as they were.

### 5.2 Kinds in this package

| Kind | Raised by | Options | Applying does |
|---|---|---|---|
| `mail.rule` | Mail analysis (§6.3) | *Create the rule* · *Edit, then create* · *Delete mails like this* · *AI every time* · *Ignore mails like this* · *Reject* | Creates the rule through `MailService` (the same validation as the form), then runs mail processing again for the attached mails |
| `mail.suspicious` | Layers 2 and 3 (§3.4) | *Delete this mail* · *Keep it* (processed, no event) · *Delete everything from this sender* (a delete rule) · *Analyse anyway* (Admin: runs the main model with the flags shown) | As named |
| `analysis.finding` | The situation analysis (§9.3) | *Acknowledge* · *Dismiss* · *Mute similar for 7 days* | Records the choice. A mute suppresses the finding's key |

- **A mail rule decision shows** the mail, with the matched and indicator phrases highlighted, the rule in readable form, the guard's probabilities, the verifier's verdict, and **what the rule would match in the last 500 mails**.

### 5.3 The page

- **Monitoring → Decisions**, with a badge showing the number of open decisions the user's role may decide.
- **The list:** open decisions first, then by priority and age, filtered by kind and state.
- **The detail:** the proposal, the evidence, the options as buttons, and the history of a decided one.
- ***Edit, then create*** opens the mail rule form prefilled (`MailInputView.vue`). Saving it accepts the decision.
- **The role is User (CanWrite):** decisions contain mail contents, and the mail log is CanWrite too. *Analyse anyway* is Admin only.

---

## 6. Mails with AI (R1, R2)

### 6.1 Today

- **`MailInboxCheckTask`**, every 60 s:
  - fetches the Inbox over IMAP;
  - stores each mail in `maillogs` (sender, recipient, subject, date, body as text);
  - moves the message to `RECEIVED`.
  - Processed mails move on to `ARCHIVE`, and archived mails are deleted after 365 days.
- **`MailProcessTask`**, every 60 s, matches every unprocessed mail against every enabled rule:
  - the sender against `FromTest`, plus `SubjectTest` and `BodyTest` if set;
  - each matching rule with a sensor writes an event: `OkCode` if `OkTest` matches, `FailCode` if `FailTest` matches, or else `DefaultCode`, with an optional message extracted by `(?<msg>…)`;
  - unmatched mails are tried again a minute later, for ever.

### 6.2 When the AI looks at a mail

- **Unmatched (A):** no rule matched within `Ai:Mail:WaitMinutes` (5). The wait gives a rule created meanwhile its chance.
- **Unsure (B):** a rule matched, but neither its `OkTest` nor its `FailTest` did (`DefaultCode` was used), and the rule has the new switch ***Ask the AI when unsure***. The AI sees the rule and proposes better tests.
- **Not:**
  - mails a rule handled;
  - mails older than 7 days when AI is first turned on, so there is no backlog flood. The AI page offers *Analyse unmatched mails of the last N days* (Admin), with the expected number of requests.
- **Before any call:** a mail with the same sender address and the same **subject shape** as an open decision attaches to that decision without a request. The subject shape is the subject with digits, dates, hex and GUIDs replaced.

### 6.3 What the AI gets, returns, and what code does with it

**Input:**
- **The mail:** sender, recipient, subject, date, the normalised body, and the scan flags.
- **The candidate sensors**, at most 100, each with its id, host, name and type. Code picks them by the similarity of host and sensor names to the sender, subject and body, and always adds the sensors of rules with the same sender domain.
- **Existing rules** with the same sender domain: their names and expressions, for consistency.
- **For trigger B:** the rule.

**Output** (`MailProposal`):

| Field | Type | Notes |
|---|---|---|
| `kind` | enum: status-report, alert, notification-noise, newsletter, spam, phishing, personal, other | |
| `action` | enum: event-rule, delete-rule, ignore-rule, ai-rule, none | |
| `sensorId` | one of the candidates, or null | Null with `newSensorName` proposes a sensor (created on acceptance, type SIMPLE, on the chosen host) |
| `from` | `{ kind: address \| domain, value }` | Must be the mail's sender or its domain |
| `subjectTemplate` | string | The subject with `{*}` for the parts that vary. Must reproduce the subject |
| `okPhrase`, `failPhrase` | strings or null | **Verbatim** from the mail |
| `messageAfter` | string or null | A verbatim phrase that the useful message follows |
| `defaultOutcome` | enum: ok, fail | |
| `confidence` | 0–1 | |
| `summary`, `reasoning` | ≤ 300 and ≤ 600 characters | Shown, never used |

**Code then does the following:**
1. **It builds the expressions:**
   - the sender: `^address$`, or `@domain$`, escaped;
   - the subject: each literal escaped, `{*}` becomes `.+?`, anchored at both ends;
   - the OK and FAIL tests: the escaped phrase;
   - the message: `escaped(messageAfter)\s*(?<msg>[^\r\n]{1,200})`.
2. **It checks them:**
   - each must compile and match this mail within 250 ms;
   - the template must reproduce the subject;
   - the phrases must occur verbatim.
   - Otherwise the attempt is `invalid-output`.
3. **It previews them against the last 500 mails:** how many each would match, and how many of those come from other senders.
   - A rule that matches more than 20 % of them, or mail from more than 3 senders, is shown as **too broad**. It can only be accepted after editing.
4. **The verification model** gets the mail, the readable rule and the preview counts, and answers `agree`, `disagree` or `unsure`, with issues.

### 6.4 Rule actions

- **`mailinputrules` gets `Action`:**

  | Action | Matching mail | Notes |
  |---|---|---|
  | **Event** (today) | Writes the event as today | All matching event rules fire, as today |
  | **AI every time** | Becomes a work item `mail.evaluate`. The guard runs, then the main model returns `outcome` (ok, fail, unknown) and `message` (≤ 200 characters, preferably verbatim), then the verification model if the rule asks for it (`VerifyAi`, on by default) | ok → `OkCode` (or 200), fail → `FailCode` (or 500). The event's message starts with "AI:". *Unknown*, a guard hit or a disagreement → no event, and a `mail.rule` decision "the AI could not decide" |
  | **Ignore** | Processed and archived, without an event | Only when no event or AI rule matched |
  | **Delete** | Processed, then **deleted from the mailbox**: `\Deleted` and expunge in `RECEIVED` instead of the move to `ARCHIVE` | Only when no event or AI rule matched. **The log row stays** (decided in the review), with its content, marked *deleted by rule …*. The mail log's usual retention removes it later |

- **The order** is: event and AI rules first, then, if none of them matched, Ignore and Delete rules. **A delete rule can therefore never remove a mail that an event rule uses.**
- **A 250 ms timeout on every rule expression.** A timeout counts as no match and is written to the process log.
- **The rule form** gets *Action* and *Ask the AI when unsure*, and for *AI every time*, *Verify with the second model*. The fields that don't apply to the action are hidden.

### 6.5 A mail's states

- **`maillogs` gets:**
  - `Disposition`: none, event, ai-event, ignored, deleted;
  - `AiState`: none, waiting, queued, analysed, blocked, failed;
  - `DecisionId`.
- **The mail log** shows both as columns and filters, and links to the decision.

---

## 7. The uplink page (R6)

### 7.1 Today

- **The configuration:** `UplinkCheck:SharedKey` and 2 or 3 URLs (`UplinkCheck:Url1..3`).
- **Every 120 s**, the task asks the next URL, in turn, with `?challenge=<32 characters>`. The answer `hash,random` must be `HMAC-SHA256(key, challenge-random)`.
- **After 3 failures in a row,** `UPLINK_OK` becomes false, and the monitoring tasks that use `TaskRunDecisionMaker` pause. After recovery they wait 5 minutes more.
- **What is kept:** only `UPLINK_OK`, the last check, the last success and the last URL in key values. The count of failures is in memory, and the reason for a failure is not recorded.

### 7.2 What changes

- **`UplinkMonitor`**, a singleton, owns the check. The task becomes its loop, and `POST /api/v1/uplink/check` (Admin) runs one check through the same path.
- **`uplink_checks`**, one row per check:
  - the time and the endpoint (its number and its host);
  - the outcome: ok, http-status, timeout, connect-error, dns, tls, bad-answer, bad-signature;
  - the HTTP status, the latency, and the error (≤ 500 characters).
  - **Kept 30 days**, about 21,600 rows.
- **The state survives a restart:** `UPLINK_FAILURES_IN_A_ROW` and `UPLINK_DOWN_SINCE` in key values.
- **A transition writes an event** on the *Uplink* system sensor: 420 "down: 3 checks failed (timeout, timeout, dns)", and 220 "up again after 14 min".
- **The request timeout** is `UplinkCheck:TimeoutSeconds` (15) instead of `HttpClient`'s 100 s.

### 7.3 The page: Monitoring → Uplink

- **The state:** Up, or *Down since 10:41*. Failures in a row (n of 3), the last check, the last success, and the next check.
- **What depends on it:** the tasks that pause while it is down (the heartbeat check, the ping check, and the MikroTik poller: the users of `TaskRunDecisionMaker`), and the 5-minute wait after recovery.
- **The configuration:**
  - the endpoints, in full for Admins, as host only for others;
  - the shared key, set or not;
  - the interval, the threshold and the timeout;
  - or "not configured", with the `.env` lines to add.
- **The endpoints:** per endpoint, the last result, the success rate over 24 hours and 7 days, and the median and p95 latency over 24 hours.
- **The timeline:** 24 hours or 7 days as an `UptimeBar`, and latency as a small line.
- **Outages:** start, end, duration, and the first error, derived from the checks.
- **The checks:** the newest first, filtered by failures, as a paged table.
- ***Check now*** (Admin).
- **Refresh:** every 15 s.
- **Visibility:** every role (CanRead), as `GET /api/v1/system/uplink` is today.

---

## 8. The debug page (R7)

### 8.1 Admin → Debug

- **One card per tool**, each with its form and its result.
- **Built in this package:** *Send a test Pushover*, *Send a test e-mail*, and *Test the AI* (§8.4, in A4).
- **Later tools:** a test webhook, an IMAP login.
- **Every send is Admin only, rate-limited** (10 per minute), and **audited** (`AuthEvents.DebugNotificationSent`, with the provider and the target; for Pushover, a rule's name or "typed key").

### 8.2 The two tools

| Tool | Inputs | Result shown |
|---|---|---|
| **Pushover** | The user key: from a notification rule (by its name, the key never shown), or typed. Optionally a device. Priority −1, 0 or 1 (2, emergency, is left out). Title and message, prefilled "PNMT test from {Identity} at {time}" | Success or failure, HTTP status, Pushover's `status`, `request` id and `errors`, and the duration. Whether `Pushover:ApiKey` is set (its last 4 characters) |
| **E-mail** | Recipient; subject and body prefilled | Success or failure. The host and port, the TLS mode used (implicit, STARTTLS, none), whether it authenticated, the server's last reply, or the error. Whether `Smtp:*` is set (never the password) |

### 8.3 The providers return a result

- **`INotificationProvider` becomes** `Task<DeliveryResult> SendAsync(NotificationMessage, CancellationToken)`.
  - `DeliveryResult` holds success, a detail and the duration.
  - `NotificationMessage` holds the recipient, subject, short and long text, and the importance (§11.5).
- **`NotificationTask` awaits it** and logs the provider, the rule and the result, without the recipient's secret.
- **E-mail uses MailKit's `SmtpClient`**, with `SecureSocketOptions.Auto`: 465 is implicit TLS, 587 and 25 use STARTTLS when offered.
- **Providers are created once**, not found by reflection on every send.
- **Tests set `Pushover:Endpoint`**, so they send to a local fake.

### 8.4 Test the AI (A4)

- Sends a fixed prompt to each configured model, the guard included.
- Shows the answer, the duration, the tokens, the cost, and the model and provider that answered.
- Logged like any request, with the purpose `debug`.

---

## 9. The situation analysis (R8)

**Removed in 1.5:** Cookie's status replaces it (docs/cookie-plan.md §12). The migration `RemoveSituationAnalysis` drops its runs and findings, its three system sensors and its decisions. This section stays as the record.

### 9.1 What the AI sees: anonymised

- **Code builds a snapshot:**
  - hosts and sensors become `H1…` and `S1…`; the mapping stays in PNMT, in the run's row (`ai_analysis_runs.mapping`);
  - per sensor: its type, importance, enabled or not, its state and since when, and its failures in the last hour and the last 24 hours;
  - the last 5 events (code, age, message) of every sensor that is failing, or that changed state more than 3 times in 24 hours;
  - totals by type and state;
  - **the uplink:** its state, outages in the last 24 hours and 7 days, the failing endpoints (`E1`, `E2`), and latency trends;
  - the background tasks' health (late or off);
  - the WireGuard gateway's state;
  - the AI queue's state;
  - the local time and weekday, for patterns.
- **A scrubber in code** replaces values in every message:
  - IPv4 and IPv6 addresses, MAC addresses, mail addresses, URLs, host names and FQDNs, GUIDs, and long serial-like tokens become `<ip:1>`, `<host:2>`, and so on;
  - the same value gets the same placeholder within a run, so patterns stay visible;
  - numbers and codes stay.
- **Never sent:** host and sensor names, notes, locations, sensor parameters, tokens, mail contents, rule recipients, users.
- **Messages are untrusted**, because devices write them. They go through layers 1 to 3 as one batch (Jev takes an array as `state`), and a flagged message becomes `<message withheld>`.

### 9.2 When it runs

- **Every hour, if something changed** (decided in the review): every `Ai__Analysis__IntervalMinutes` (60, set in `.env`), if the snapshot's structural hash changed (states, failure counts, uplink). **Without a change, no request**, and no cost.
- **Early**, at most once every 10 minutes:
  - **a storm:** 5 or more sensors turned to failure within 10 minutes;
  - the uplink came back;
  - the gateway went down.
- **Size:** the snapshot is capped at about 30,000 characters. The most important and longest-failing sensors come first.

### 9.3 What it returns, and what happens with it

- **`Situation`:**
  - `status`: normal, degraded or incident;
  - `summary`, ≤ 400 characters;
  - `findings`, each with:
    - `kind`: incident, pattern, risk or noise;
    - `severity`: info, warning or critical;
    - `title` (≤ 120) and `explanation` (≤ 800);
    - `sensors` and `hosts`, as pseudonyms from the input only;
    - `evidence` (a sensor, a code, an age);
    - `recommendation` (≤ 400);
    - `confidence`;
    - `key`, a short slug for dedupe, such as `uplink-flapping-evenings`.
- **Code maps the pseudonyms back** for display.
- **Critical findings** go to the verification model first.
  - A confirmed one stays critical.
  - A disputed one becomes a warning, marked *disputed*.
- **What is sent is chosen in the notifications** (decided in the review). Each severity has its own system sensor (§4), and a run writes, per severity:

  | Severity | Sensor | A run with new findings of it | A run without any |
  |---|---|---|---|
  | Critical (confirmed) | *AI analysis: critical* (High) | **423**, "Critical: {title}" (several: "2 critical: {title}; {title}") | 220 |
  | Warning | *AI analysis: warnings* (Normal) | **424**, likewise | 220 |
  | Info | *AI analysis: info* (Low) | **221**, likewise | 220 |

  - **"New"** means a key not reported in the last 24 hours, and not muted. A finding that persists from run to run is not sent again.
  - **Which of these reach whom**, by Pushover or by mail, and how often, is set like for any sensor:
    - a rule on the sensor, with its type: *every failure*, *on change*, or *always* for info;
    - or an importance scope: *at least High* means only critical findings.
- **Findings of warning and above also become `analysis.finding` decisions,** deduplicated by key and sensors, to acknowledge, dismiss or mute. Muted keys are skipped everywhere.
- **Where it is shown:**
  - a **Situation** card on the dashboard: the status, the summary and its time, with a link;
  - the AI page's *Analysis* tab: the runs, each with its findings and its requests.

---

## 10. RouterOS advisories (R9)

### 10.1 The feed

- **The URL:** `Mikrotik:Advisories:FeedUrl`, by default `https://cdn.mikrotik.com/routeros/latest-stable-and-long-term.rss`, set in `.env` as `Mikrotik__Advisories__FeedUrl`.
- **How often:** every `Mikrotik:Advisories:IntervalMinutes` (60).
- **Its format, as seen on 2026-11-13:** RSS 2.0, one item per release, for stable and long-term only. An item has:
  - `title` "RouterOS 7.24.6 [long-term]";
  - `link`;
  - `guid` (the title);
  - `pubDate`;
  - `content:encoded`, with "What's new in 7.24.6 (2026-10-30):" and lines `*) area - text;` separated by `<br>`.
  - Security fixes appear as lines such as `*) system - improve stability (includes CVE-2026-98432);`.
- **Parsed with `XmlReader`**, without DTDs and with a size cap of 2 MiB. An item that doesn't parse is logged and skipped.

### 10.2 What is stored

- **`mikrotik_releases`**, one row per version and channel:
  - the published time and the changelog text;
  - when it was first seen;
  - the assessment: severity (critical, important or normal), zero-day, exploited, the CVEs, a summary, whether it came from code, the AI or a verified AI, and when it was assessed;
  - the AI work item.
- **Kept:** 2 years.

### 10.3 The assessment

1. **Code, always:**
   - CVE ids (`CVE-\d{4}-\d{4,}`);
   - keywords per line: security, vulnerab…, exploit…, remote code, authentication bypass, privilege escalation, denial of service, actively exploited, zero-day, and so on.
   - **No hit:** normal, with no AI request. Most releases end here.
   - **Without AI:** *actively exploited* or *zero-day*, or a CVE together with *exploit…*, *remote code* or *authentication bypass*, means critical. Any other hit is important.
2. **With a hit, and AI on:**
   - the guard screens the changelog, because it is outside text;
   - the main model returns `severity`, `zeroDay`, `exploitedInTheWild`, `remoteWithoutCredentials`, `cves` (each must occur in the text), `areas`, `summary` and `reasoning`;
   - **critical needs the verification model's agreement.** Without it, the result stays *important*, marked "disputed".

**Critical means a very bad issue** (decided in the review), not merely a security fix. The release fixes at least one of:
- something **exploited in the wild**, or a **zero-day**;
- something **exploitable remotely without credentials**: remote code execution, authentication bypass, or a reachable service crash that takes the router down;
- a CVE that the changelog itself calls critical.

Security fixes below that (a local privilege issue, hardening, a CVE in a feature that needs a login) are **important**. Those are shown, but never alerted.

### 10.4 Who is affected, and the alert

- **The alert needs both** (decided in the review): **a release rated critical (§10.3), and at least one router behind it.**
  - A critical release with every router already on it, or newer, sends nothing.
  - An important release never alerts.
- **A router is behind on a critical fix** when a release on its channel is rated critical and is newer than the router's installed version. The versions come from `mikrotik_routers`, which the poller keeps current.
- **Any router behind** → **422** on the *RouterOS advisories* sensor, for example: "Critical RouterOS fix 7.24.6 [long-term] (CVE-2026-98432): 3 routers behind: gw-zurich, gw-bern, ap-lab".
  - A Pushover rule on that sensor, or the importance-High scope (§11.2), sends it.
  - **The routers' names are not sent to any AI.** They are only in PNMT's event.
- **When every affected router has updated** → **220** "No critical RouterOS fix pending".
- **Also shown:**
  - an *Advisories* card on the MikroTik admin page's Overview;
  - a red *security fix* badge next to *update available* in the Routers tab;
  - in a router's Updates tab, the release's changelog with its security lines highlighted.
  - **Important releases** show there too, with an amber badge, but without an event.

---

## 11. Importance and the Notification Manager (R10)

### 11.1 Importance

- **`hosts.Importance`:** Low = 0, Normal = 1, High = 2. Not null, default Normal.
- **`sensors.Importance`:** nullable; null takes the host's. **Effective importance** = the sensor's own, or else its host's.
- **Set in** the host and sensor forms, in bulk in the manager (§11.3), and in bulk on the hosts list.
- **Shown** as a small tag in the lists, with a filter.
- **Used by:**
  - notification scopes (§11.2);
  - Pushover's priority (§11.5);
  - the dashboard's order: High first among the failures;
  - the situation analysis (§9).

### 11.2 Links, scopes and exclusions

- **Links**, as today: a rule ↔ a sensor. *By sensor* and *by selection* create links.
- **Scopes**, new: `notification_rule_scopes` (the rule, the kind, and a value). **Lasting** (decided in the review): a scope covers today's sensors **and those added later**, and a sensor whose importance or host changes moves in or out of it at once.
  - **Importance:** every sensor with effective importance **at least** Low, Normal or High (decided in the review). *At least Normal* covers Normal and High sensors; *at least High* covers High only;
  - **Host:** every sensor of a host;
  - **Everything.**
- **Exclusions**, new: `notification_rule_exclusions` (a rule and a sensor), for example "everything on host X except its ping sensor".
- **Covered:** a sensor is covered by a rule when it is linked to it, or a scope matches and no exclusion does. A rule covers a sensor once, however many ways it does.
- **The evaluation:** `GetAllPendingNotificationsForLastMinutes` loads the enabled rules with their scopes and exclusions once per run (there are few), and adds the matching pairs to today's links for the pending events, which are 30 minutes at most.
- **Before a scope is saved,** the UI shows how many sensors it covers now, and how many events per day they wrote over the last 7 days.

### 11.3 The page: Monitoring → Notifications

The *Notification rules* list stays. The new page has three tabs:

- **Matrix:**
  - rows are sensors, grouped by host, with importance, type and state;
  - columns are the enabled rules (name and provider icon);
  - a cell shows ✓ linked, ◐ through a scope (the tooltip says which), ⊘ excluded, or nothing.
  - **Clicking a cell** links or unlinks. On a scope-covered cell, it offers an exclusion.
  - **Filters:** host, importance, type, state, and *not covered by any rule*.
- **Bulk:** select rows, then:
  - *Assign rules…*;
  - *Remove rules…*;
  - *Set importance…*;
  - *Assign by host…* or *by importance…*, which create scopes.
- **Coverage:**
  - the sensors no rule covers, by importance;
  - **a High sensor without a rule is a warning**, which the System health page also counts.

Viewers see the page. Changes need User (CanWrite), as for the rules today.

### 11.4 The rule's page

- The rule's page gets a **Scopes** section (*also every sensor with importance at least …*, *every sensor of host …*, *everything*) and an **Exclusions** section.
- The sensor's page lists its rules with how each one covers it.
- **Since 1.12 a rule's recipient is an address, a user, a group or the responsible of the day** (docs/notification-recipients-plan.md): the page shows whom it reaches now and whom it last reached, and the coverage lists the rules that reach nobody.

### 11.5 Pushover priority, and the importance in messages

- **Every notification carries** the sensor's effective importance:
  - **Pushover:** Low → priority −1 (quiet), Normal → 0, High → 1 (it bypasses the user's quiet hours). **Since 1.10 this mapping is a setting** on the Notification manager's *Pushover* tab, with Pushover's emergency level available (docs/cookie-object-notes-plan.md §5); the values here are its defaults;
  - **E-mail:** the subject starts with `[High]` for High;
  - **Webhooks:** unchanged.
- ~~**Off with** `Notifications:PushoverPriorityByImportance=false`.~~ Gone in 1.10: *off* is the three priorities set to 0 on the tab.

---

## 12. MikroTik changes

### 12.1 The Router column (R11)

- **The cause:** in `RoutersTab.vue`, the icon-only `StatusBadge` (the check mark) and the router's link are separated only by a space.
- **The fix:** they go into a flex row with `gap: var(--pnmt-space-2)`.
- **Also checked:** the other lists with an icon-only badge in front of a name get the same treatment. These are the Routers tab's card view, the UDP nodes tab, the hosts list, a sensor's child list, and the dashboard's panels.

### 12.2 A second update method, and an optional storage check (R12)

**Why the update fails on small routers:**
- **PNMT's own check:** step 1 refuses when the router reports less free storage than `Mikrotik:Updates:MinimumFreeStorageBytes` (24 MiB). A router with 16 MB of flash never has that.
- **RouterOS itself** keeps a downloaded package **in RAM** on such devices, and installs it at the reboot.
- **WinBox's *Check for updates → Download & Install*** runs no such check. It calls RouterOS's `install`, which downloads the package and reboots in one go. **So it succeeds where PNMT's job stops.**

**Two methods** (decided in the review):

| Method | Step 4 | Control |
|---|---|---|
| **Download, then reboot** (today's, the default) | `/system package update download`. PNMT polls the status until *Downloaded*, then **reboots the router itself** in step 5 | A cancel is possible until the reboot. PNMT sees the download finish before anything restarts |
| **Download and install** (new; what WinBox's *Download & Install* does) | **Install** (a new step): `set channel=…`, `check-for-updates`, and the latest version checked against the target, as today. Then **`/system package update install`**: RouterOS downloads the package and **reboots by itself** when the download is done | A cancel is possible only until step 4 starts. After that, RouterOS carries on alone |

**The new method, step by step:**
- **While the SSH session lasts**, PNMT polls the status, as in the download step.
- **An error before the reboot** fails the job at *Install*, with RouterOS's own text, for example "ERROR: not enough disk space". Nothing has been installed.
- **When the session drops**, or the router stops answering, the reboot has begun. *Wait* then runs as today. Its deadline adds `DownloadTimeoutMinutes` to `RebootTimeoutMinutes`, because the download happens before the reboot.
- **Verify, firmware and done** are unchanged.
- **A0 checks on the CHR lab** (`tools/chr-lab.sh`) how `install` behaves over a non-interactive SSH session:
  - whether it returns at once or blocks until the download is done;
  - whether it asks for a confirmation;
  - which statuses it shows;
  - how the session ends.
- **In both methods the router downloads from MikroTik itself.** PNMT still never transports packages, so docs/mikrotik-plan.md §7.1 stays true.

**The storage check, optional** (decided in the review):
- **The update dialog gets *Check free storage first*,** on by default.
  - **On:** step 1 as today. Below the threshold, the job fails before anything changes.
  - **Off:** step 1 logs the free storage and "storage check skipped by Alice", and goes on. RouterOS then decides. On a small router it keeps the package in RAM.
- **A hint in the dialog:** when a chosen router's last poll showed less free storage than the threshold, the dialog says so next to the option. For example: "gw-lab has 3 MiB free. On 16 MB-flash routers, RouterOS keeps the package in RAM: turn the check off, and choose *Download and install*."
- **The binary backup** (step 3) also writes to flash, and can fail on a nearly full router. The dialog mentions it next to that option, which can already be turned off.

**Choosing:**
- **The method and the storage check are chosen per batch** in the update dialog: the router's Updates tab, or the admin page's Routers tab for several routers.
- **Each router remembers** the method and the storage check it was last updated with. The dialog starts with them when every chosen router agrees, and otherwise with the defaults.
- **The job log's first line** records both.

**Considered and set aside: PNMT uploads the package over SFTP**, as dragging it into WinBox's *Files* does. It would need PNMTD to download the packages for each architecture, and the `ftp` policy for the upload. It remains the fallback if *Download and install* fails on real routers too.

**In the code:**
- `UpdateJobOptions` gains `Method` (Download or Install) and `CheckStorage` (true by default);
- `MikrotikJobStep.Install = 11`, appended so that the stored values stay valid;
- `mikrotik_routers` gains `UpdateMethod` (smallint, default 0) and `UpdateCheckStorage` (bool, default true);
- `MikrotikUpdateRequest` gains `method` and `checkStorage`;
- `JobRunner.UpdateAsync` branches at step 4.

---

## 13. Data model

One EF migration per phase:

| Migration | Tables and columns |
|---|---|
| `SystemSensorsAndUplinkChecks` (A2) | `uplink_checks` (Id, At, Endpoint smallint, EndpointHost, Outcome smallint, HttpStatus, LatencyMs, Error). The unique index on sensors (Type, Parameters) where Type = 90 |
| `ImportanceAndNotificationScopes` (A3) | `hosts.Importance` smallint not null default 1. `sensors.Importance` smallint null. `notification_rule_scopes` (Id, RuleId → cascade, Kind, Importance, HostId → cascade). `notification_rule_exclusions` (RuleId, SensorId → cascade; primary key on both) |
| `AiFoundation` (A4) | `ai_work` (Id, Purpose, SubjectType, SubjectId, InputHash, State, Attempts, NextAttemptAt, LastError, Notified, CreatedAt, FinishedAt, Result jsonb). `ai_requests` (Id, At, WorkId → set null, Purpose, Role, Attempt, ModelAsked, ModelServed, Provider, GenerationId, PromptVersion, Request bytea (gzip), Response bytea (gzip), Truncated, HttpStatus, Outcome, Error, DurationMs, PromptTokens, CompletionTokens, ReasoningTokens, CachedTokens, Cost numeric(12,6)), indexed on At. `ai_spending` (Day, Purpose, Model, primary key on the three; Requests, Cost numeric(12,6)), and `ai_budget_state` (Period, the thresholds written: 80, 90, 100) |
| `DecisionsAndMailAi` (A5) | `decisions` (as in §5.1; indexed on State, Kind; a partial unique index on DedupeKey where State = Open). `mailinputrules`: Action smallint default 0, AskAiWhenUnsure bool, VerifyAi bool default true. `maillogs`: Disposition smallint, AiState smallint, DecisionId → set null |
| `MikrotikReleasesAndUpdateMethod` (A1, A6) | `mikrotik_routers.UpdateMethod` smallint default 0, `UpdateCheckStorage` bool default true (A1). `mikrotik_releases` (Version, Channel, primary key on both; PublishedAt, Changelog, FirstSeenAt, Severity, ZeroDay, Exploited, Cves text[], Summary, AssessedBy, AssessedAt, WorkId) (A6) |
| `AiAnalysis` (A7) | `ai_analysis_runs` (Id, At, Trigger, SnapshotHash, Mapping jsonb, Status, Summary, WorkId) and `ai_findings` (Id, RunId → cascade, Key, Kind, Severity, Title, Explanation, Recommendation, Confidence, Sensors uuid[], Hosts uuid[], Verified, DecisionId) |

---

## 14. API v1

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/v1/uplink` | CanRead | The state, configuration (masked for non-Admins), endpoint statistics |
| `GET /api/v1/uplink/checks`, `GET /api/v1/uplink/outages` | CanRead | Paged |
| `POST /api/v1/uplink/check` | Admin | One check now |
| `POST /api/v1/debug/pushover`, `POST /api/v1/debug/mail` | Admin | Rate-limited, audited |
| `POST /api/v1/debug/ai` | Admin | A4 |
| `PUT /api/v1/hosts/importance`, `PUT /api/v1/sensors/importance` | CanWrite | `{ ids, importance }` (null for sensors: from the host) |
| `GET /api/v1/notifications/matrix` | CanRead | Filters, paged by host. Recipients masked for Viewers as today |
| `POST /api/v1/notifications/assign` | CanWrite | `{ ruleIds, sensorIds, mode: add \| remove }` → counts |
| `PUT /api/v1/notification-rules/{id}/scopes`, `…/exclusions` | CanWrite | The whole list |
| `GET /api/v1/notifications/coverage` | CanRead | |
| `GET /api/v1/decisions`, `GET /api/v1/decisions/{id}` | CanWrite | Filtered by the user's role |
| `POST /api/v1/decisions/{id}/decide` | CanWrite (Admin for Admin options) | `{ choice, edits }`. 409 if no longer open |
| `GET /api/v1/decisions/count` | CanWrite | For the badge |
| `GET /api/v1/ai/status` | Admin | The configuration (keys masked), the models, the budget and this period's spending, today's and 30 days' spending, the queue |
| `GET /api/v1/ai/requests`, `GET /api/v1/ai/requests/{id}` | Admin | The detail with the raw bodies |
| `GET /api/v1/ai/work`, `POST /api/v1/ai/work/{id}/retry`, `POST /api/v1/ai/work/{id}/cancel` | Admin | |
| `POST /api/v1/ai/mail/backfill` | Admin | `{ days }`. Answers with the expected number of requests first (`dryRun`) |
| `GET /api/v1/ai/analysis`, `GET /api/v1/ai/analysis/{id}`, `POST /api/v1/ai/analysis/run` | CanRead / CanRead / Admin | Findings with real names |
| `GET /api/v1/mikrotik/releases` | CanRead | The assessed releases and the routers behind each |

- **The changed contracts:**
  - `MailInputRule` and its request gain `action`, `askAiWhenUnsure` and `verifyAi`;
  - `MailLogItem` gains `disposition`, `aiState` and `decisionId`;
  - hosts and sensors gain `importance` (sensors also `effectiveImportance`);
  - `MikrotikUpdateRequest` gains `method` and `checkStorage`.
- **`AuthEvents`:** `DebugNotificationSent`, `AiWorkRetried`, `AiWorkCancelled`, `AiBackfillStarted`.
- **Every endpoint** goes into the `EndpointAuditTests` matrix. **None is anonymous.**

---

## 15. Web UI

| Page | Where | Who | Refresh |
|---|---|---|---|
| **Decisions** | Monitoring → Decisions, with a badge | User | 30 s |
| **Notifications** (matrix, bulk, coverage) | Monitoring → Notifications | Viewer reads, User changes | – |
| **Uplink** | Monitoring → Uplink | Viewer; *Check now*: Admin | 15 s |
| **AI**: tabs *Requests* (live), *Queue*, *Analysis*, *Settings* (read-only configuration, models, spending, budget) | Admin → AI | Admin | Requests and queue: 5 s |
| **Debug** | Admin → Debug | Admin | – |
| Mail inputs | The rule form: *Action*, *Ask the AI when unsure*, *Verify with the second model* | User | – |
| Mail log | Disposition and AI state columns and filters, and the link to the decision | User | – |
| Hosts and sensors | *Importance* in the forms, a tag and a filter in the lists, bulk on the hosts list | User | – |
| Dashboard | The **Situation** card (A7); High first among the failures (A3) | Viewer | 30 s, as today |
| System health | The AI worker and the advisories in the tasks list; High sensors without a rule | Admin | – |
| MikroTik | The *Advisories* card, the *security fix* badge, *Method* in the update dialog, the Router column spacing | Admin | – |

- **The new pages** are in `views/decisions/`, `views/notifications/`, `views/uplink/`, `views/admin/ai/` and `views/admin/DebugView.vue`, each with its routes in `router/index.ts` and its entries in `layouts/navigation.ts`.
- **Long AI text** is shown with `white-space: pre-wrap` in escaped interpolation, never with `v-html`.

---

## 16. Configuration

| Setting (`.env` name) | Default | Notes |
|---|---|---|
| `Ai__Enabled` | `false` | Everything AI |
| `Ai__BaseUrl` | `https://openrouter.ai/api/v1` | Any OpenAI-compatible server |
| `Ai__ApiKey` | – | **Secret** |
| `Ai__MainModel` | `deepseek/deepseek-v4.1-flash-1017` | |
| `Ai__VerifyModel` | `z-ai/glm-5.3-flashx` | Another vendor than the main model |
| `Ai__Guard__Model`, `Ai__Guard__Url` | `typesafe/jev-1.14`, `https://openrouter.ai/api/alpha/decisions` | Through OpenRouter, with `Ai__ApiKey`. A0 may switch the URL to `…/api/v1/systemone` |
| `Ai__Guard__Required`, `Ai__Guard__Threshold`, `Ai__Guard__MaxStateBytes` | `true`, `0.5`, 30000 | Above about 32 KB Jev is slower and less accurate |
| `Ai__TimeoutSeconds`, `Ai__Parallelism`, `Ai__MaxInputChars` | 60, 2, 24000 | |
| `Ai__Budget__Usd`, `Ai__Budget__Period` | 30, `month` | `month` or `day`; 0 = no limit. Warnings at 80 % and 90 % (§3.7) |
| `Ai__LogEntries` | 10000 | R13 |
| `Ai__DataCollection` | `deny` | OpenRouter's provider preference |
| `Ai__Retry__QuickAttempts`, `…__QuickDelaySeconds`, `…__LongDelaysHours` | 5, 60, `1,3,24` | R14: the last value repeats |
| `Ai__Mail__Enabled`, `Ai__Mail__WaitMinutes` | `true` (with `Ai__Enabled`), 5 | |
| `Ai__Tokens__MailAnalyse`, `…__MailEvaluate`, `…__RouterOsRate`, `…__KbTags`, `…__Verify`, `…__Debug` | 4500, 1200, 800, 400, 600, 50 | `max_tokens` per task. A reasoning model's reasoning counts too: too low, and the request ends without an answer. 1.8.2 tripled the mail limits (1500 and 400 before) |
| `Ai__Analysis__Enabled`, `Ai__Analysis__IntervalMinutes` | `true`, 60 | Runs only when something changed |
| `Mikrotik__Advisories__Enabled`, `…__FeedUrl`, `…__IntervalMinutes` | `true`, the feed above, 60 | R9 |
| `UplinkCheck__TimeoutSeconds` | 15 | |
| `Decisions__ExpireDays` | 30 | |
| `Pushover__Endpoint` | `https://api.pushover.net/1/messages.json` | For tests |

- **`deploy/.env.example`** gets an *AI (optional)* block with the keys commented out.
- **`deploy/README.md`** gets an *AI* subsection: what is sent where, the costs, the budget, and how to turn each use off.
- **`deploy/upgrade-1.0.sh`** passes `Ai__*` through, as it does `Smtp__*`.

---

## 17. Phases

| Phase | Content | Needs | Size |
|---|---|---|---|
| **A0** | **Verify** (no product code):<ul><li>the chosen models (`deepseek/deepseek-v4.1-flash-1017`, `z-ai/glm-5.3-flashx`) with strict structured outputs, on 30 real mails (anonymised for the test), and the reasoning effort they need;</li><li>the `usage` fields;</li><li>`data_collection: "deny"` and zero data retention;</li><li>Jev through OpenRouter: the Decisions API or `/api/v1/systemone`, the authentication, the exact JSON of `choice` and `noul`;</li><li>Jev on an injection corpus;</li><li>the costs per mail and per analysis, against the 30 USD budget;</li><li>RouterOS's `install` over a non-interactive SSH session on the CHR lab (returns or blocks, confirmation, statuses, how the session ends), and, with a 16 MB-flash router at hand, an update with the storage check off.</li></ul>The results go into this document | – | small |
| **A1** | **Quick wins without AI:**<ul><li>the Router column (R11);</li><li>the providers' results and MailKit;</li><li>the **Debug** page with Pushover and e-mail (R7);</li><li>the ***Download and install*** method and the optional storage check (R12)</li></ul> | – | medium |
| **A2** | **System sensors** (type 90, created automatically; codes 220–221 and 420–427, the AI ones used from A4) and the **uplink page**, `uplink_checks`, `UplinkMonitor` (R6) | – | medium |
| **A3** | **Importance**, scopes and exclusions, the **Notification Manager** (matrix, bulk, coverage), Pushover priority (R10) | – | medium-large |
| **A4** | **The AI foundation:**<ul><li>the clients;</li><li>typed tasks and schemas;</li><li>the injection layers;</li><li>`ai_work` with retries;</li><li>`ai_requests` with the 10,000 limit;</li><li>the budget;</li><li>the **AI** admin page;</li><li>*Test the AI*.</li></ul>(R2, R3, R4, R13, R14, R15) | A2 (the AI sensor) | large |
| **A5** | **Decisions** and **mails with AI**: triggers A and B, proposals, templates to expressions, preview, rule actions (delete, ignore, AI every time), the regex timeout, the mail log columns (R1, R5) | A4 | large |
| **A6** | **RouterOS advisories:** the feed, code assessment, the AI rating, affected routers, 422, the MikroTik UI (R9) | A2, A3; A4 optional | medium |
| **A7** | **The situation analysis:** the snapshot, the scrubber, the triggers, findings, the dashboard card (R8) | A4, A5 (decisions) | medium-large |

- **The order:**
  - A1–A3 need no AI, and are useful on their own;
  - A4 comes before every AI use;
  - **A6 works without AI** (code assessment), and improves with it.
- **Each phase:**
  - is one or more commits on master;
  - is documented in this file's *As built* section, as docs/mikrotik-backup-plan.md §7 was;
  - has its tests green before the next phase starts.

---

## 18. Testing

| Area | How |
|---|---|
| **The AI clients** | **A fake OpenRouter**, a local HTTP handler in the TestApp: canned answers with `usage.cost`, 429, 500, timeouts, schema violations, `require_parameters` errors. **A fake Jev** returning set probabilities |
| **Retries** | `FakeTimeProvider`: 5 quick retries, then 421 once, then 1 h, 3 h and 24 h; a success writes 220; HTTP 400 fails permanently. **The budget:** 425 at 80 %, 426 at 90 %, 427 at 100 %, each once per period; work waits, without counting attempts, until the next period, which writes 220; the spending survives the request log's trimming |
| **The request log** | The rows, the gzip, the 1 MiB cap, no `Authorization` header, **trimmed to 10,000** |
| **Injection** | A corpus in `PNMTD.Tests/Fixtures/Ai/` (mails with direct, hidden, Unicode-tag and role-marker injections), with expectations:<ul><li>the layer 2 flags;</li><li>with the guard's fake saying *injection*, **the main model is never called**;</li><li>`Validate` rejects foreign ids and phrases that are not in the mail</li></ul> |
| **Templates to expressions** | Escaping of every special character; anchoring; a property test that a built expression always matches its source subject; the 250 ms timeout on a pathological expression |
| **Mail actions** | Event, AI every time, ignore and delete, in their order; a delete never removes a mail an event rule matched; the IMAP side behind an `IMailbox` seam, so tests need no server |
| **Decisions** | Dedupe, expiry, superseding, applying each option, the role check, 409 on a decided one |
| **Scopes** | Links, importance (*at least*), host and everything scopes, exclusions, no duplicates, sensors added later are covered |
| **Uplink** | A fake endpoint with the HMAC; outcomes classified; the transitions write 420 and 220; the state survives a restart |
| **Advisories** | The feed of 2026-11-13 saved as a fixture; parsing; the code's rating; the routers behind; 422 and 220 |
| **Download and install** | On the CHR lab (`tools/chr-lab.sh`): `install`, the reboot by RouterOS, the wait and the verification, end to end. With the fake router session: an error status fails at *Install*; the storage check off logs the skip and goes on, while on it fails below the threshold; the router remembers both choices |
| **Endpoints** | The `EndpointAuditTests` matrix; the debug endpoints' rate limit and audit |
| **E2E** (`tools/e2e.sh`) | With the fake AI and the fake Pushover:<ul><li>a mail becomes a decision, which becomes a rule, which writes an event;</li><li>a bulk assignment in the matrix;</li><li>a test Pushover on the debug page</li></ul> |
| **Live** (opt-in, skipped by default) | One request per configured model with a real key from the environment |

---

## 19. Risks

| Risk | Mitigation |
|---|---|
| **Residual prompt injection** (Jev can be fooled sometimes, as can any model) | Several layers (§3.4). No capabilities. Everything durable needs a person, and *AI every time* can only write its rule's events |
| **Mail contents leave the installation** | Off by default, a switch per use, `data_collection: "deny"`, zero data retention where available (A0), and the plain statement in deploy/README.md. The situation analysis is anonymised |
| **Costs** | The budget (30 USD a month by default) with warnings at 80 % and 90 %, dedupe, no analysis without change, code first for the advisories, and spending shown on the AI page |
| **A bad rule from a proposal** | Preview on 500 mails, the *too broad* block, the verification model, a person, and the 250 ms timeout |
| **Models change or disappear** | Model ids in `.env`, `ModelServed` logged, and *Test the AI* on the debug page |
| **OpenRouter or TypeSafe down** | Retries (R14), one notification per outage, and mails simply wait |
| **A scope floods a recipient** | The preview of covered sensors and events per day before saving |
| ***Download and install* gives PNMT less control**: no cancel once step 4 starts, and RouterOS reboots when its download is done | The dialog says so. *Wait*'s deadline includes the download time. *Verify* reports "not applied" with the lines of `/log` |
| **The storage check is off and the router really is full** | RouterOS refuses the download or the install, and the job fails at step 4 with RouterOS's text, before any reboot |
| **The uplink check's history grows** | 30 days, about 22,000 rows |

---

## 20. Decisions and open questions

### 20.1 Decisions

**From the plan:**

1. **System conditions are sensors** (type 90), so notifications use the normal rules (§4).
2. **The AI never writes expressions.** Code builds them from templates and verbatim phrases (§6.3).
3. **A delete rule acts only when no event rule matched** (§6.4).
4. **Without a guard, no AI on untrusted text**, unless `Ai:Guard:Required=false` (§3.2).

**From the review of revision 1 (2026-11-13):**

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Jev's access | **Through OpenRouter**, with the same key: `typesafe/jev-1.14`, pinned | §3.2 |
| 2 | The models | **Main `deepseek/deepseek-v4.1-flash-1017`, verification `z-ai/glm-5.3-flashx`** (two vendors, both with strict structured outputs) | §3.2 |
| 3 | Mail contents and sensor names to the AI | **Yes**, with `data_collection: "deny"` | §3.8 |
| 4 | A delete rule's log row | **Kept**, with its content, until the mail log's retention | §6.4 |
| 5 | Assignment by importance or host | **Lasting scopes**, which cover sensors added later | §11.2 |
| 7 | System sensors | **Created automatically** | §4 |
| 8 | The budget | **Set in `.env`**, **30 USD by default**. **Warnings at 80 % and 90 %**; at 100 % work waits | §3.7 |
| 10 | Who decides | **The User role** (CanWrite) | §5.3 |
| 11 | The situation analysis | **Hourly when something changed**, with the interval in `.env`. **What is sent is chosen in the notifications:** a sensor per severity | §9.2, §9.3 |
| 12 | RouterOS alerts | **Only when a very bad issue is fixed and a router is behind it** | §10.3, §10.4 |

**From the review of revision 2 (2026-11-13):**

| # | Question | Decision | Where |
|---|---|---|---|
| 6 | Importance scopes | ***At least*:** *at least Normal* covers Normal and High sensors | §11.2 |
| 9 | The small routers' updates | **A second method, *Download and install*** (RouterOS's `install`, as WinBox does), and **the free-storage check as an option** in the update dialog, on by default. Both are remembered per router | §12.2 |

### 20.2 Open questions

1. **The budget's period:** the plan assumes **30 USD per month**, with `Ai__Budget__Period=day` as the alternative. Is a month right?

---

## 21. As built

Where the implementation differs from the sections above, or settles what they left open. One entry per phase, written when it is committed.

### A1: quick wins (2026-11-13)

- **The Router column (§12.1):** only the Routers tab's table lacked a gap between the status icon and the name. Its card view, the UDP nodes tab, the hosts list, a sensor's child list and the dashboard's panels already lay them out with a gap.
- **The providers (§8.3):**
  - `INotificationProvider.SendAsync` returns a `DeliveryResult`.
  - The three providers are singletons, created once (`AddNotifications`), with a named `HttpClient` (`notifications`, 30 s).
  - `NotificationService` logs the provider, the rule's name and the result, never the recipient.
  - Pushover's priority already follows the importance; until A3 every notification is Normal.
  - E-mail uses MailKit with `SecureSocketOptions.Auto`. `Smtp__Port` is now in `.env.example`; empty means 25, as before.
- **The debug page (§8):**
  - `GET /api/v1/debug` names what the tools would use (the Pushover key's last 4 characters, the SMTP host, port, sender and login, the Pushover rules).
  - `POST /api/v1/debug/pushover` and `/mail` send.
  - The rate limit is the policy `debug`: 10 a minute per user, with its own 429 text.
- **The update method (§12.2):**
  - **A migration of its own**, `MikrotikUpdateMethod`, instead of sharing one with the releases of A6. `UpdateCheckStorage` is true for the routers that exist.
  - **The router detail and the fleet list carry `LowStorage`,** computed on the server against `Mikrotik:Updates:MinimumFreeStorageBytes`, so the dialog's hint needs no copy of the threshold.
  - **The storage check's refusal now says what to do**: start again without the check, with *Download and install*.
  - **`WaitBackAsync`** is shared by the reboot of either method; *Install*'s deadline adds the download timeout to the reboot timeout.
  - **A connection that fails before `install` was sent** is a failure, not the reboot.
  - **Cancelling stops being possible at *Install*,** because the API compares steps by number and `Install = 11` is above `Reboot`. A restart of PNMTD during *Install* is reported like one during the reboot.
- **A0** runs alongside: the results are in the A0 entry.

### A0: verification (2026-11-13)

- **RouterOS's `install` over a non-interactive SSH session**, on a throwaway CHR 7.24.5 (x86_64, `CHR_ARCH=x86_64 tools/chr-lab.sh start a0 7.24.5`):
  - **it asks nothing**, and prints the update's status as it changes (`calculating download size…`, `Downloaded 8% (1.6MiB)`, `Downloaded 62% (12.4MiB)`);
  - **it returns when RouterOS reboots**, here after 4 s, with exit status 0, while the download was still printing;
  - the router came back on 7.25.1 a minute later, its log saying `installed system-7.25.1` and `router rebooted by ssh-cmd:admin@…/upgrade`.
  - So *Install* (§12.2) needs no confirmation, and the next command on the same session fails: that is the reboot.
- **`check-for-updates` is asynchronous:** right after it, the status is `finding out latest version...`. The existing steps already read `latest-version` afterwards.
- **The ARM64 CHR image without KVM** (macOS) did not reach its login prompt in time; the x86_64 one did. The lab routers already run x86_64.
- **Not verified here:** everything that needs an OpenRouter key (the models' structured outputs, `usage`, zero data retention, Jev's exact JSON). That is checked when a key is configured, with *Test the AI* (A4) and the opt-in live test.

### A2: system sensors and the uplink page (2026-11-13)

- **The importance columns came forward from A3** (`hosts.Importance`, default Normal; `sensors.Importance`, nullable), into the migration `SystemSensorsAndUplinkChecks`, because the system sensors are created with an importance.
- **The v0.12 importer** treats a target column with a default as fillable, so `hosts.Importance` doesn't stop an import.
- **System sensors** (`SystemSensors`, a singleton):
  - created at start by `SystemSensorStartup`, with the monitoring tasks (`BackgroundTasks:Enabled`), and in any case on their first event;
  - **their host** is the one that already holds a system sensor, else a host named "PNMT", created when missing;
  - **a PUT** changes only their name, host, notes, Enabled and Ignore;
  - **deleting** a system sensor, or the host that holds one, answers 409;
  - **the sensor page** shows them as "PNMT's own condition", without tokens, the type field or a delete action.
- **The uplink** (`PNMTD/UplinkCheck/UplinkMonitor.cs`; `PNMTD.Uplink` would have clashed with a UDP type):
  - **events:** a success writes 220 "Uplink OK." (collapsed by the dedupe), the third failure in a row writes 420 with the outcomes, and the next success writes 220 "Uplink up again after …";
  - **without the settings**, the uplink counts as up and the persisted state is cleared, so monitoring never stays paused by a check that no longer runs (`/api/v1/system/uplink` says so too);
  - **the latency line** is an inline SVG, without a chart library.
- **Kept the endpoint `GET /api/v1/system/uplink`** for the dashboard and System health.

### A3: importance and the Notification Manager (2026-11-13)

- **The migration is `NotificationScopes`** (the scopes and exclusions); the importance columns came with A2.
- **Bulk importance:** `PUT /api/v1/hosts/importance` and `PUT /api/v1/sensors/importance` exist. **In the UI**, sensors are set in bulk on the manager's matrix, and hosts one at a time on their page; the hosts list has an importance column and filter, but no selection.
- **The manager is Monitoring → Notification manager** (`/notifications`), with the tabs *Matrix* and *Coverage*; Monitoring → Notification rules stays as it was.
  - **The matrix's columns are the enabled rules.** A click on a cell links, unlinks, excludes or includes again, by what the cell shows.
  - **A scope's preview** (`POST …/scopes/preview`) runs while the rule's scopes are being edited, before *Save*.
- **The sensor page's notifications** list the rules that cover the sensor through a scope too (`via`), with an *Exclude* button instead of *Unlink*.
- **System health** has a *Notifications* panel: covered sensors, and High sensors without a rule.
- **The dashboard's problems** list the most important sensors and hosts first, then the longest-failing.
- **Tests that create scopes delete their rules at the end:** a scope reaches every sensor of the run's database, other classes' too.

### A4: the AI foundation (2026-11-13)

- **Where:** `PNMTD/Ai/`: `AiOptions`, `AiJson` (camelCase, kebab-case enums, strict schemas from `JsonSchemaExporter`), `ChatClient`, `DecisionClient`, `InputSanitizer` and `InjectionScanner` (`Untrusted.cs`), `AiTask`/`VerifyTask`/`AiPipeline`, `AiWorkQueue`/`AiWorker`, `AiRequestLog`, `AiBudget`. Migration `AiFoundation` (`ai_work`, `ai_requests`, `ai_spending`, `ai_budget_state`).
- **The verification model** is one generic task (`VerifyTask`): the original input, the proposal and the task's question, answered with `agree`, `disagree` or `unsure` and issues. Each use only says whether it wants a verdict.
- **Jev's answers** are read under `answers` (TypeSafe's format) or at the top level (the Decisions API); a noul's probability as `noul` or `probability`. Which of them OpenRouter returns is checked with the first real key (*Test the AI*).
- **The work queue:** one open item per purpose and subject; `AiWorker` runs with the monitoring tasks; items left *Running* by a restart run again; work for a purpose without a handler fails for good.
- **421 is written once per outage** under a lock, so that two items reaching their sixth failure at once write it once.
- **`invalid-output`:** an answer the task's checks refuse marks its log row too, so the log says why.
- **The budget** counts every request's cost (`usage.cost`, Jev's included) per day, purpose and model, independent of the request log.
- **The admin page** is *Admin → AI* (`/admin/ai/requests|queue|settings`); the *Analysis* tab comes with A7. *Admin → Debug* has *Test the AI*.
- **`deploy/upgrade-1.0.sh` is unchanged:** it copies v0.12's settings, which never had `Ai__*`.

### A5: decisions and mails with AI (2026-11-13)

- **Four kinds:** `mail.rule`, `mail.suspicious`, **`mail.outcome`** (the AI could not decide a mail of an AI-every-time rule: *It reports success*, *a failure*, or *Ignore it*; a kind of its own instead of a variant of `mail.rule`), and `analysis.finding`, whose decision side came with A5 so that A7 only raises it.
- **A mute is the decision itself:** a finding key is skipped while a decision with that key chose *mute* in the last 7 days. No table of its own.
- **Trigger B** is the purpose `mail.improve`; its decision offers *Update the rule …* with the proposed OK and FAIL tests.
- **The candidate sensors** reach the model as short references (`s1` … `s100`), mapped back in code: fewer tokens, and no ids it could invent.
- **The proposal's phrases** must occur verbatim, and its template must reproduce the subject, or the attempt is `invalid-output` and retried (§3.5).
- ***Edit, then create*** is a form inside the decision, not the rule page; the rule it creates goes through `MailService`'s validation.
- **A rule set to AI every time while AI is off** leaves its mails unprocessed until AI is on, or a person handles them.
- **Mail events are still written directly**, as `MailProcessTask` always did, not through `SensorEventWriter`.
- **Where:** `PNMTD/Decisions/` (`DecisionBook`, the kinds, `DecisionMaintenanceTask`), `PNMTD/Mails/` (`RuleTemplate`, `MailAiTasks`, `MailAiHandlers`), Monitoring → Decisions (`/decisions/:id?`, with a count in the menu), the mail rule form's *Action*, the mail log's *AI* column, and the AI page's backfill. Migration `DecisionsAndMailAi`.


### A6: RouterOS advisories (2026-11-13)

- **Where:** `PNMTD/Mikrotik/AdvisoryFeed.cs` (the hosted `AdvisoryFeed`, `RouterOsRateTask`, `RouterOsRateHandler` for `routeros.rate`), the table `mikrotik_releases` (migration `MikrotikReleases`), `GET /api/v1/mikrotik/releases` (Admin), and `Mikrotik:Advisories` (`Enabled`, `FeedUrl`, `IntervalMinutes`, at least 5).
- **The feed** is read with DTDs prohibited and at most 2 MiB; a release is stored once per version and channel, and kept 2 years. The test fixture is the feed of 2026-11-13.
- **`AssessedBy`** says who decided: `code`, `ai`, `verified` (critical, the check model agreed) or `disputed` (critical, it did not; stored as important).
- **The code's critical waits for the AI:** while a release's rating is queued it does not alert, for at most 6 hours (`AdvisoryFeed.AiGrace`); then the code's assessment alerts on its own, so a stuck queue or a spent budget hides nothing.
- **A router without a channel counts as *stable*.** Disabled sensors are not counted as behind.
- **Important releases** show the routers behind them too, on the Overview's *Advisories* card and as an amber badge; only critical ones write 422.
- **The changelog's security lines** are found by the same words in the UI (`views/sensors/mikrotik/security.ts`) as in the code's assessment, so a router's *Updates* tab highlights them without an Admin-only request.
- **The rate handler resolves the feed when it runs:** the feed enqueues through the queue, whose worker holds the handlers.

### A7: the situation analysis (2026-11-13)

- **Where:** `PNMTD/Ai/Analysis/`: `Scrubber`, `SituationSnapshots` (the snapshot), `SituationTask`, `SituationHandler` (`situation.analyse`), `SituationScheduler` (hosted). Tables `ai_analysis_runs` and `ai_findings` (migration `AiAnalysis`); `GET /api/v1/ai/analysis` and `/{id}` (CanRead), `POST /api/v1/ai/analysis/run` (Admin, audited); the AI page's *Analysis* tab and the dashboard's *Situation* card.
- **The snapshot leaves out** the three *AI analysis* sensors (their own findings would feed back) and ENCAPSULADED containers (their children are in). Disabled and ignored sensors come last, so the size cap drops them first.
- **Names in messages:** host names (3 characters or more) always become their pseudonym. Sensor names only when distinctive: a digit, a dot, a hyphen or an underscore, or 8 characters or more. Otherwise a sensor called *Backup* would turn every "backup" into `S7`.
- **Placeholders** are numbered in the order of replacement (URLs, mails, GUIDs, MACs, IPv6, IPv4, host names, tokens), so `<ip:1>` may be the second address in a text. The mapping, pseudonyms and placeholders alike, stays in the run's row; PNMT maps them back for display.
- **Messages are screened per message by the scan** (a hit becomes `<message withheld>`). Jev screens them as one batch. When it flags the batch, the analysis runs again with every message withheld, instead of not at all; the run says so (`GuardFlagged`).
- **The structural hash** covers each sensor's state, its failures in the last hour and 24 hours, and its state changes, in coarse steps (0, 1–2, 3–9, 10–99, 100+), plus the uplink's state and outages, the gateway, and late tasks. A sensor that keeps failing is no change; one that starts or stops is.
- **The schedule:** a look every minute; one run at a time; early triggers wait until 10 minutes after the last run. The uplink's return and the gateway's loss are kept until then; a storm counts only while it lasts. The same finding twice in one answer counts once. Without a change the next look at the interval is one interval later. Runs are kept 90 days.
- **Critical findings are verified one by one**, at most 3 a run (`AiPipeline.VerifyAsync`, the generic verification on one finding); the rest count as disputed.
- **"New"** compares the key together with the sensors (`DedupeKey`), as the decisions do; a finding not reported in 24 hours is new again, and sent again.
- **A finding that persists** writes nothing to its sensor (the sensor keeps its state); it attaches to the open decision, if one is open. A muted finding counts as none, so its sensor returns to 220.
- **The AI's JSON** (all tasks): numbers are strict (the web defaults made every number "string or number" with a pattern in the schema), and `<`, `>` and `&` are not escaped, so placeholders reach the model as written.
---

## Appendix: configuration and file changes

| Area | New | Changed |
|---|---|---|
| AI | `PNMTD/Ai/`: `AiOptions`, `AiSetup` (DI), `ChatClient`, `DecisionClient` (Jev), `AiTask`, `AiPipeline`, `InputSanitizer`, `InjectionScanner`, `AiWorker`, `AiRequestLog`, `AiBudget`; tasks in `PNMTD/Ai/Tasks/` | `Program.cs` (`AddAi`) |
| System sensors | `PNMTD/Services/Domain/SystemSensorService.cs` | `SensorType` (`SYSTEM = 90`), `PNMTStatusCodes` (220–221, 420–427), `SystemService` (status codes, sensor types, tasks), `SensorService` (refuses deleting type 90) |
| Decisions | `PNMTD/Decisions/` (`IDecisionKind`, kinds), `Services/Domain/DecisionService.cs`, `Api/V1/DecisionsController.cs`, `Models/Db/DecisionEntity.cs` | – |
| Mail | `PNMTD/Mails/RuleTemplate.cs`, `PNMTD/Mails/IMailbox.cs` | `MailProcessTask` (actions, order, timeout, AI triggers), `MailInboxCheckTask` (delete), `MailService`, `MailInputRuleEntity`, `MailLogEntity` |
| Uplink | `PNMTD/Uplink/UplinkMonitor.cs`, `Api/V1/UplinkController.cs`, `Models/Db/UplinkCheckEntity.cs` | `UplinkCheckTask` (the loop only), `KeyValueKeyEnums` |
| Notifications | `Models/Db/NotificationRuleScopeEntity.cs`, `…ExclusionEntity.cs`, `Api/V1/NotificationsController.cs` | `INotificationProvider`, the three providers, `NotificationService`, `NotificationTask`, `DbContextNotificationExtensions`, `NotificationRuleService`, `HostEntity`, `SensorEntity` |
| Debug | `Api/V1/DebugController.cs`, `Services/Domain/DebugService.cs` | `AuthEvents` |
| Advisories | `PNMTD/Mikrotik/AdvisoryFeed.cs`, `Models/Db/MikrotikReleaseEntity.cs` | `MikrotikOptions` (`Advisories`), `MikrotikSetup` |
| Update method | – | `JobRunner` (the install step, the optional storage check), `MikrotikEnums` (`Install`), `MikrotikRouterEntity`, `MikrotikJobService`, `UpdateDialog.vue` and the batch dialog |
| Web UI | `views/decisions/`, `views/notifications/`, `views/uplink/`, `views/admin/ai/`, `views/admin/DebugView.vue` | `router/index.ts`, `layouts/navigation.ts`, `RoutersTab.vue`, `UpdateDialog.vue`, `MailInputView.vue`, `MailLogView.vue`, the host and sensor forms, the dashboard |
| Docs and deploy | – | `deploy/.env.example`, `deploy/README.md`, `deploy/upgrade-1.0.sh`, docs/mikrotik-plan.md §7 (the second method and the storage option), `README.md` |
| Tests | `PNMTD.Tests/Ai/`, `PNMTD.Tests/Fixtures/Ai/`, `PNMTD.Tests/Fixtures/Mikrotik/releases-2026-11-13.rss`, and tests per area | `EndpointAuditTests` |
