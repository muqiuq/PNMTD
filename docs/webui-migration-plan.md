# Migration Plan: PNMT 1.0 (Vue 3 SPA inside PNMTD, .NET 10, PostgreSQL)

| | |
|---|---|
| Status | Draft for review |
| Date | 2026-11-11 |
| Revision | 3.1: review decisions applied, including the answers to the revision 3 questions (§10.2). .NET 10 everywhere, PostgreSQL instead of SQLite, a frozen sensor API, old management routes and JWT removed, a `deploy/` folder plus `start.sh`/`stop.sh`, a Viewer role, a compact UI with a redesigned dashboard, release **v1.0.0** |
| Scope | Replace the separate Blazor Server frontend (`PNMT.WebApp`, v0.11.0) with a Vue 3 single-page app served by PNMTD under `/pnmt-admin`. Upgrade every project to .NET 10. Move storage from SQLite to PostgreSQL. Add a new management API `/api/v1` and remove everything else except the sensor API. Move user management into PNMTD (username + password + TOTP). Production deployment from `deploy/`, local debugging with `start.sh`/`stop.sh` |
| Non-goals | Changing the monitoring logic. **Any observable change to the sensor API** (`/event`, `/read`) |
| Progress | **Phase 0 done** (2026-11-11): golden sensor contract recorded from v0.12.1 (`tools/contract.sh`), every project on .NET 10, the .NET 10 image passes the contract.<br>**Phase 1 done** (2026-11-11): PostgreSQL with UTC timestamps, `pnmtd db migrate` / `import-sqlite` with verification, `start.sh`/`stop.sh`, `run-tests.sh`, the `deploy/` skeleton. The PostgreSQL image passes the contract with the fixture imported.<br>**Phase 2 done** (2026-11-11): `/api/v1` for every resource of §3.7 on domain services, deny by default with the Viewer/User/Admin policies, `/pnmt-admin` hosting, `/` and the old routes as empty 404s, JWT, `/trust` and lazy loading removed. The anonymous-surface test, the role matrix and the v1 tests are green, and the image still passes the contract. <br>**Phase 3 done** (2026-11-11): `users` and `auth_log`, cookie sign-in with lockout and the login rate limit, session revocation by security stamp, antiforgery, the last-admin guard, temporary passwords, the first admin, `users.json` import with the WebApp's hashes, `pnmtd users …` with `add-user.sh`, `list-users.sh` and `reset-password.sh`. TOTP follows in phase 4. See §7.1 for where the implementation differs from this plan |
| References | `~/source/plm`: deploy folder, start/stop scripts, PostgreSQL, SQLite import, contract harness. `~/source/exam-trainer`: Vue + Vite in Podman, the same deploy pattern. `~/source/PortUpAcademy`: auth and TOTP code. `~/source/PNMT.WebApp`: the current UI |

---

## 1. Summary

**Today:** there are two containers, both on .NET 8, whose support ended on 2026-11-10.

- `PNMT.WebApp` (Blazor Server + MudBlazor) keeps its users in `users.json` and calls PNMTD over HTTP with a single shared JWT that never expires.
- PNMTD stores everything in SQLite (`pnmtd.db`) and does not know who the end users are.
- Hundreds of applications report to PNMTD through `/event` and `/read`.

**Target (v1.0.0):** there is one app container and one PostgreSQL container. PNMTD serves:

- the Vue 3 SPA under **`/pnmt-admin/`**,
- the REST API **`/api/v1`**, which only the SPA uses,
- the sensor API `/event/…` and `/read/…`, **exactly as v0.12.1 serves it**.

Everything else returns a plain 404: `/`, the old management routes, `/trust` and Swagger.

| Topic | Decision |
|---|---|
| Platform | **.NET 10** (LTS, supported until November 2028) for every project, as in plm and PortUpAcademy |
| Database | **PostgreSQL 18** in its own container, in production and in local debugging. SQLite is read once, by the importer |
| Sensor API | `/event` and `/read` are **frozen**: same routes, status codes, headers and bodies, quirks included. A golden contract recorded from v0.12.1 gates every build (§3.3, §8.1) |
| Old management API | **Removed in 1.0.0:** every root route except `/event` and `/read`, the JWT scheme and `/trust`. Their only client was the WebApp |
| UI location | Fixed subpath **`/pnmt-admin/`**. `/` and every other unknown path return a plain 404 |
| Frontend | Vue 3 + TypeScript + Vite, **PrimeVue 4** with a compact theme, Vue Router, Pinia, TanStack Query |
| API | REST API **`/api/v1`**: plural resources, dedicated DTOs, paging, ProblemDetails errors, an OpenAPI contract |
| Server-side protection | **Deny by default.** Every endpoint requires a signed-in user unless it is on a short anonymous allowlist. A reverse proxy is not needed for this (§3.6) |
| Browser auth | HttpOnly cookie on the same origin, no tokens in JavaScript, antiforgery header on unsafe methods |
| Machine auth | None in 1.0. Personal API tokens can come later |
| Users and roles | `users` table in PostgreSQL. `users.json` is imported once and **existing passwords keep working**. Roles: **Admin**, **User**, **Viewer** (read-only) |
| 2FA | TOTP ported from PortUpAcademy. The policy is **Optional with a setup prompt**. No recovery codes: `deploy/reset-password.sh` and `deploy/reset-totp.sh` cover recovery |
| UI design | Same pages and navigation as today, but compact and responsive. The dashboard is redesigned (§5.5) |
| Deployment | `deploy/` folder with a compose file (app + db), `.env` and operations scripts, modelled on plm. `start.sh`/`stop.sh` at the repo root for debugging with Podman |
| Tooling | **node and npm run only in Podman containers, never on the host** (§6.1) |
| Delivery | One image `uisach/pnmtd:v1.0.0` plus the official PostgreSQL image. `uisach/pnmt.webapp` and the `PNMT.WebApp` repo are retired |

---

## 2. Current state

### 2.1 Architecture

```
Browser ──(Blazor SignalR circuit + cookie)──► PNMT.WebApp container (:5000)
                                                  │  /var/lib/pnmt/users.json   (users)
                                                  │  /var/lib/pnmt/token.txt    (JWT for backend)
                                                  ▼  HTTP + "Authorization: Bearer <JWT>"
Sensors / scripts ──(anonymous /event, /read)──► PNMTD container (:5001) ── /var/lib/pnmtd/pnmtd.db
```

- **Runtime:** all six projects target `net8.0`. There is no `global.json` and no `Directory.Build.props`. Both images run as root.
- **WebApp:** Blazor Server, MudBlazor 6.21. It references `PNMT.ApiClient` and `PNMTD.Lib` from this repo by relative path, so both repos must be checked out side by side (CI clones PNMTD with an SSH key).
- **Backend storage:** EF Core 8 on SQLite with lazy-loading proxies and 28 migrations. The provider and the database path are hard-coded in `PnmtdDbContext.OnConfiguring`. Eight background tasks run on `System.Threading.Timer`, and each run creates its own `new PnmtdDbContext()`.
- **Backend auth:** one JWT for everything. It is obtained from `JwtToken` config or from the anonymous `GET /trust/create-web-app-token` (whoever calls first gets a token; later calls are rejected because a marker file exists). `ValidateLifetime = false`, so tokens never expire and can only be revoked by rotating `Jwt:Key`.
- **Users:** stored in `users.json` with PBKDF2-HMAC-SHA256 (10,000 iterations, 32-byte key, 16-byte salt). Roles are `Admin` and `User`. A default user `pnmt`/`pnmt` is created on first start. `PNMT.WebAppCli` offers `adduser` and `checkpassword`.
- **Session:** cookie, 7 days. No 2FA, no lockout, no audit trail beyond log lines.
- **API style:** routes at the root with mixed singular and plural names (`/host` for CRUD, `/hosts` for state). Writes return a `{ success, message, data }` envelope. EF-mapped POCOs from `PNMTD.Lib` serve as both input and output. Enums are serialized as numbers.
- **Data freshness:** pages load data on navigation only. Nothing refreshes automatically.

### 2.2 Feature inventory (parity checklist)

Everything in this table must exist in the new UI before cutover.

| Area | Current route(s) | Functionality | Backend endpoints used |
|---|---|---|---|
| Login / logout | `/login`, `POST /account/login`, `/account/logout` | Username and password form, "invalid" message | none (`users.json`) |
| Layout | all | App bar, drawer navigation (admin-only items: KeyValues, Users), instance identity, backend and frontend version | `GET /info` |
| Dashboard | `/` | KPI cards: hosts, sensors, failures (= enabled hosts not OK). "Everything is fine" card. Uplink failure card (last run, last successful run). One card per failing host listing its failing, non-ignored sensors (code, "since", 24h uptime). Last 10 error events. Host list with 30-day uptime % and a 90-day uptime bar (`uptimegraph.js`). Search sensor by secret write token | `GET /hosts`, `/state`, `/events/lasterrors`, `/sensor/bysecrettoken/{t}` |
| Events | `/events` | Grid of the last 1000 events (status, code, created, host, sensor, message), with filter, sort and 50 rows per page | `GET /events/last` |
| Hosts | `/hosts`, `/host/new`, `/host/edit/{id}` | Grid (state, name, location, id). Create form: enabled, name and location (2–64 chars) | `GET /hosts`, `POST/PUT /host` |
| Host detail | `/host/{id}` | Inline edit of enabled, name, location and notes. Created date. Delete with confirmation. Sensor table (state, name, last code, last message, 24h and 30d uptime). "Create sensor" | `GET /host/{id}`, `/hosts/{id}`, `PUT/DELETE /host` |
| Sensor create | `/sensor/new/{hostId}` | Enabled, name, TextId, type (DNS not allowed), interval (default 60), grace period (default 60) | `POST /sensor` |
| Sensor detail | `/sensor/{id}` | Inline edit that follows `SensorTypeRules` (enabled, ignore, name, TextId, type, source, interval, grace period, parameters). Read-only status. Link to the DNS zone for DNS sensors. Example write URL (`/event/{writeToken}/200/Hello`) and read URL (`/read/{readToken}/json`). Link and unlink notification rules. Events grid (10 per page) with a detail dialog. Delete | `GET/PUT/DELETE /sensor`, `/notificationrule/forsensor/{id}`, `link-sensor`/`unlink-sensor`, `/events/sensor/{id}` |
| Notification rules | `/notificationrules`, `/notificationrule/new`, `/notificationrule/edit/{id}` | Grid with inline enabled, type and recipient. Form: enabled, name, recipient, type. Linked sensors (shown as GUIDs) | `/notificationrule` CRUD |
| DNS zones | `/dnszones` | Grid (match status, name, last checked, record count). Last 30 log entries | `GET /dnszone`, `/dnszone/logs` |
| DNS zone detail | `/dnszone/{id}` | Properties with inline enabled and interval, requires-processing, next check, host. Force update. Edit. Delete. History. Records grid with expandable rows (TTL, reference value, actual value, inline ignore) | `GET/PUT/DELETE /dnszone`, `/dnszone/logs/{id}`, `PUT /dnszoneentry` |
| DNS zone editor | `/dnszone/new`, `/dnszone/edit/{id}` | Host select and an auto-growing zone file textarea. Create or save | `POST/PUT /dnszone`, `GET /host` |
| Mail inputs | `/mailinputs`, `/mailinput/new`, `/mailinput/edit/{id}` | Grid with inline enabled and sender. Form: name, sender/content/subject tests, extract-message regex, OK test + code, fail test + code, default code, host → sensor autocomplete | `/mailinput` CRUD, `/sensor/byhost/{id}` |
| Mail log | section of `/mailinputs` | Grid (created, from, to, subject, content, processed, processed-by) and a detail dialog | `GET /maillog` |
| Key values (admin) | `/keyvalues` | Inline edit unless read-only, add, delete | `/keyvalue` CRUD |
| Users (admin) | `/users`, `/user/new`, `/user/{id}` | List. Create (username, name, admin, password ≥ 10 chars, repeated). Rename. Toggle admin. Set password. Delete (blocked for the last admin) | none (`users.json`) |
| Documentation | `/docs` | Reserved status codes from `PNMTStatusCodes`, read by reflection | none |

### 2.3 Issues found during analysis

These should be **fixed during the migration, not ported**.

**Backend**

1. Every `PUT` resets `Created` to the current time, because `ToEntity()` sets `Created = DateTime.Now` ([HostPocoExtensions.cs:26](../PNMTD/Models/Poco/Extensions/HostPocoExtensions.cs#L26), [SensorPocoExtensions.cs:65](../PNMTD/Models/Poco/Extensions/SensorPocoExtensions.cs#L65)).
2. `PUT /sensor` builds the entity from the full client payload, including `SecretWriteToken`, `SecretReadToken` and `Status`. A client that leaves fields out nulls them, and a client can set tokens to anything. The unknown-id check discards its result ([SensorController.cs:92](../PNMTD/Controllers/SensorController.cs#L92): `NotFound();` without `return`).
3. `GET /maillog` calls `Take(1000)` before `OrderByDescending`, so it does not return the newest mails ([MailLogController.cs:24](../PNMTD/Controllers/MailLogController.cs#L24)).
4. Many endpoints use `.Single()`, so an unknown id returns 500 instead of 404.
5. Validation exists only in the Blazor form classes. The API accepts empty names, unparsable interval descriptions, invalid regexes and recipients that match no notification provider.
6. `/trust/create-web-app-token` issues an admin-equivalent token to the first anonymous caller, and JWT lifetime is not validated ([TrustController.cs](../PNMTD/Controllers/TrustController.cs), [Program.cs:179](../PNMTD/Program.cs#L179)). *Removed in 1.0.*
7. `UseHttpsRedirection()` runs before `UseForwardedHeaders()` ([Program.cs:223-224](../PNMTD/Program.cs#L223-L224)). Forwarded headers must be applied first. *1.0 drops the HTTPS redirect altogether (§3.3).*
8. Timestamps use `DateTime.Now` (about 60 places) and are stored and serialized without an offset. *1.0 stores UTC (§3.4).*
9. `/keyvalue` is admin-only in the UI but not in the API. *Removed with the old routes. In v1 it is admin-only on the server.*
10. Some endpoints break HTTP semantics:
    - `GET /sensor/newsecrettoken/{id}` changes state, rotating both tokens, on a GET.
    - `POST /keyvalue` returns 200 with `success: false` and a raw `DbUpdateException` message when the key already exists.
    - "Force update" of a DNS zone is a flag smuggled through `PUT`.
11. .NET 8 has been out of support since 2026-11-10. Every project targets `net8.0`. Swashbuckle 7 and `Microsoft.OpenApi` 1.x, `Serilog.Extensions.Logging.File` 3.0 (Serilog 2 plus the deprecated RollingFile sink) and `System.CommandLine` beta4 all need replacing.
12. Background timers can overlap. Only `MailInboxCheckTask` and `DnsCheckTask` guard against it, so a slow `NotificationTask` run can send duplicate notifications.
13. `DnsCheckTask` trims its log with `Skip(500)` and no `OrderBy`, which relies on SQLite rowid order ([DnsCheckTask.cs:323](../PNMTD/Tasks/DnsCheckTask.cs#L323)). A null DNS result throws an NRE that aborts the zone (line 213).
14. Host state and the dashboard load the **whole events table** into memory per host, plus three uptime queries per sensor ([DbSetHostEntityExtensions.cs:65-121](../PNMTD/Data/DbSetHostEntityExtensions.cs#L65-L121)). The dashboard response is about 1 MB for 300 sensors.
15. Host-level uptime averages include −1 "no data" days of new sensors, which produces fake red days ([HostsController.cs:29-33](../PNMTD/Controllers/HostsController.cs#L29-L33)).
16. "Since" on the dashboard is the time of the *latest* event, not when the failure started (DbSetHostEntityExtensions.cs:81, 111). A sensor that never reported shows as OK with code −1.
17. The sensor token columns have no unique index. Migration `20230809211713_AddSecretToken` gave all existing rows the same default token. Any duplicate makes every request with that token fail with 500.
18. Events of ENCAPSULADED siblings are never trimmed, and `MailProcessTask` skips the 2000-event cap.
19. `DbTestHelper` seeding ships in the production assembly, and the "Created DB" log condition is inverted. In Development the app listens only on 7328 (Kestrel config), although `UseUrls` says 7327.

**WebApp**

20. Default credentials `pnmt`/`pnmt` (`WebAppUserManager.cs:61`).
21. The last admin can be demoted, because only deletion is guarded. Non-admins cannot change their own password, since there is no account page. `users.json` is written without locking.
22. DNS history pagination: page 1 skips the newest 10 entries, and integer division drops the last partial page (`DnsZoneComp.razor:80-81`, `DnsZonesComp.razor:68-69`).
23. Uptime bar (`uptimegraph.js`):
    - Values from 0.75 up to (but not including) 0.9 are shown in green.
    - Of 91 values only 90 are drawn, so **today is never shown**.
    - `preserveAspectRatio="xMinYMid slice"` crops the newest ~40 days on a phone.
    - There are no tooltips or dates.
24. Status display:
    - Ignored sensors look exactly like other sensors.
    - Disabled and ignored use different icons on different pages.
    - Status codes appear as raw numbers everywhere except `/docs`.
    - The dashboard never shows the failing sensor's last message, which is the actual error.
25. Functional gaps:
    - Notification rules and mail input rules cannot be deleted, although the API supports it.
    - Linked sensors are shown as GUIDs.
    - Token rotation has no UI.
    - Parameters cannot be set when creating a sensor.
    - The new-sensor form has no valid default type (enum value 0 is undefined).
    - The mail log shows `ProcessedById` as a GUID.
    - The inline Sender edit on mail inputs renders no input (`MailInputsComp.razor:55`).
26. Label errors:
    - The key-value form fields are labelled "Name" and "Sender test".
    - The column titles "Name" and "Subject" are duplicated.
    - The host edit page is titled "New Host".
    - "To" is labelled "Code" in the mail log dialog.
    - The new-sensor form says "Internval".
27. The events page loads 1000 rows into the client, and a sensor's events load up to 2000 rows.
28. On a phone:
    - The drawer starts open and covers the content.
    - The three KPI cards stay squeezed side by side.
    - Tables overflow sideways.
    - The search box is at the very bottom.

**Known monitoring bugs that 1.0 does *not* fix** (non-goal: fixing them would change what sensors and `/read` return; they belong in a follow-up release with its own contract update):

- `TimespanConditionsCheckTask` only selects ONE_WITHIN_TIMESPAN sensors. ALL_WITHIN_TIMESPAN is never evaluated, so its `/read` always returns null/null.
- The state query checks `ONE_WITHIN_TIMESPAN_FAILED` twice and never `ALL_WITHIN_TIMESPAN_FAILED` (DbSetHostEntityExtensions.cs:98, 100).
- `MailProcessTask` inserts events for disabled sensors.

---

## 3. Target architecture

```
Browser ──(HTTPS via proxy, same origin, cookie)──► pnmtd container (:8080, published as :5001)
                                          /pnmt-admin/**   → Vue SPA (static files + index.html fallback)
                                          /api/v1/**       → REST API (cookie session, deny by default)
Sensors / apps ──(anonymous, token in path)────►  /event/**, /read/**   (frozen v0.12.1 contract)
                                          /, anything else → 404, empty body
                                          /healthz         → "ok" / "unavailable" for the container health check
                                                │
                                                ├─► db container: PostgreSQL 18   (volume pnmtd-pgdata)
                                                └─  volume pnmtd-keys: Data Protection key ring
```

### 3.1 Repository layout

```
PNMTD.slnx                    replaces PNMTD.sln
global.json                   SDK 10.0.100, rollForward latestFeature
Directory.Build.props         net10.0, nullable, analyzers, lock files (as in plm)
Directory.Packages.props      central package versions
Dockerfile                    web (node) → build (sdk:10.0) → runtime (aspnet:10.0-alpine)   (§6.3)
start.sh, stop.sh             local debugging with Podman                                      (§6.2)
run-tests.sh                  every check, in containers                                       (§8)
dev.env                       gitignored settings for start.sh (deploy/.env format)
tools/npm.sh                  npm in a container                                               (§6.1)
tools/vite-dev.config.mjs     polling watcher for Vite under Podman
deploy/                       production stack and operations scripts                          (§6.4)
PNMTD/                        ASP.NET Core host
  Api/V1/                     v1 controllers + Contracts/ (request/response DTOs)
  SensorApi/                  the frozen /event and /read endpoints                            (§3.3)
  Services/Domain/            HostService, SensorService, DashboardService, …
  Data/                       PnmtdDbContext (Npgsql) + Migrations/ (new PostgreSQL baseline)
  Import/                     SqliteImporter, UsersJsonImporter (one-time upgrade from v0.12)
  Cli/                        `pnmtd db | users | import-sqlite …`
  wwwroot/pnmt-admin/         Vue build output (gitignored, filled by the Dockerfile)
PNMTD.Lib/                    + Security/ (PasswordHasher, TotpService, Base32, SecurePasswordGenerator)
PNMTD.WebUI/                  Vue project (package.json, vite.config.ts, src/…)
PNMTD.Tests/                  unit and integration tests (integration tests against PostgreSQL)
PNMTD.Contract.Tests/         sensor API golden files recorded from v0.12.1                    (§8.1)
```

**Removed:**

- `PNMT.ApiClient`: its only consumer is the WebApp.
- `PNMTD.Cli`: it only issued JWTs. The admin commands move into the main binary.
- The duplicate `PNMTD/Dockerfile`, and the stale `<DockerfileContext>` in `PNMTD.csproj`.
- `PNMTD.DevConsole`: no longer used (§10.2).

### 3.2 .NET 10

- **Projects:** every project targets `net10.0` via `Directory.Build.props`, with `Nullable`, `ImplicitUsings`, `LangVersion latest` and `AnalysisLevel latest-recommended`, as in plm. `TreatWarningsAsErrors` is turned on once the warnings that the upgrade surfaces are fixed.
- **Build:**
  - `global.json` pins SDK `10.0.100` with `rollForward: latestFeature`.
  - The solution moves to `PNMTD.slnx`.
  - Lock files are on (`RestorePackagesWithLockFile`), and the image restores with `--locked-mode`.
- **EF Core 10:** `Migrate()` throws on pending model changes, so CI runs `dotnet ef migrations has-pending-model-changes`.
- **Images:** `sdk:10.0` for the build and the Ubuntu-based `aspnet:10.0` for the runtime.
  - **Why not Alpine:** under Podman, musl's resolver fails a whole lookup when the DNS answers the AAAA query with SERVFAIL. Ubuntu also keeps ICU culture behaviour, as v0.12.1 had on Debian.
  - The app runs as the image's non-root `app` user, listens on port 8080 (`ASPNETCORE_HTTP_PORTS`), and serves `/healthz` on that port.
  - **PING sensors (verified in phase 0):** without `CAP_NET_RAW`, .NET's `Ping` runs the system `ping`.
    - The image installs `iputils-ping` and strips its `cap_net_raw` file capability, because a file capability makes `exec` fail outright under `cap_drop: ALL` and `no-new-privileges`.
    - `ping` then uses an unprivileged ICMP socket, which needs `net.ipv4.ping_group_range` to include the app's group. The compose file and `start.sh` set it to `0 2147483647`, which is also Docker's default. Podman's default `0 0` makes every PING fail with 470.

| Package | Today | 1.0 |
|---|---|---|
| `Microsoft.EntityFrameworkCore`, `.Design` | 8.0.14 | 10.x |
| `Microsoft.EntityFrameworkCore.Sqlite` | 8.0.14 | removed. The importer uses `Microsoft.Data.Sqlite` directly |
| `Microsoft.EntityFrameworkCore.Proxies` | 8.0.14 | removed (no lazy loading, §3.4) |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | – | 10.x |
| `Microsoft.AspNetCore.Authentication.JwtBearer` (+ `System.IdentityModel.Tokens.Jwt`) | 8.0.14 | removed together with the JWT scheme |
| `Swashbuckle.AspNetCore` | 7.3.1 | removed. `Microsoft.AspNetCore.OpenApi` 10 (`AddOpenApi`) with build-time document generation for the TypeScript types. A Swagger UI only in Development |
| `Microsoft.AspNetCore.OpenApi` | 8.0.14 (referenced, unused) | 10.x, now used |
| `Serilog.Extensions.Logging.File` | 3.0.0 | removed. The built-in console logger is enough: logs go to `docker compose logs`, rotated by the log driver |
| `System.CommandLine` | 2.0.0-beta4 | removed with `PNMTD.Cli`. The `pnmtd …` commands (phase 1) use 2.0 stable |
| `MSTest.*`, `Microsoft.NET.Test.Sdk`, `Mvc.Testing` | 3.8 / 17.13 / 8.0 | MSTest 4 on Microsoft.Testing.Platform, `Mvc.Testing` 10 |
| `Newtonsoft.Json` | used by 6 test files, never declared | declared explicitly. Only the tests of the old management API use it, and they go with those routes in phase 2 |
| `MailKit`, `DnsClient` | 4.11 / 1.8 | latest |
| `MudBlazor` | 6.8.0 (not referenced) | removed |
| `Microsoft.Extensions.TimeProvider.Testing` | – | new, for tests (§3.4) |

### 3.3 Sensor API (frozen contract)

Hundreds of applications post to these URLs. Many are scripts or devices that cannot be updated, so 1.0 serves them **exactly** as v0.12.1 does. "Exactly" is defined by the golden contract recorded from the v0.12.1 image (§8.1), not by reading the code: it pins routes, methods, status codes, `Content-Type`, body bytes and the quirks below.

| Endpoint | Behaviour that stays |
|---|---|
| `GET /event/{secretToken}/{code}/{message?}` | Looks up an enabled sensor by `SecretWriteToken`. `Ignore` is not checked. A message starting with `$` is Base64 of UTF-8 text and is decoded before the rate limit. GET messages are not trimmed. Success: 200 `application/json; charset=utf-8` `{"success":true,"message":null,"data":"<lower-case guid>"}` |
| `POST /event/{secretToken}/{code}` | `Content-Type` is ignored. The body is read as raw UTF-8 text (BOM detected), trimmed, then `$`-Base64 decoded. **ENCAPSULADED** sensors take a JSON array of `{ Name, Code, Message }` with case-sensitive property names. Each `Name` gets a sibling sensor (created on demand, at most 65). The route `code` is ignored, and `data` is the id of the last item's event |
| `GET /read/{secretToken}/{option?}` | Looks up an enabled sensor by `SecretReadToken`. Output formats:<ul><li>`property`: `lastEventSuccess = True\nlastEventMessage = …\n`</li><li>`raw`: `True;…`</li><li>anything else, including `json` or no option: JSON written with the default System.Text.Json encoder</li></ul>The response is `text/plain; charset=utf-8`, and with `Accept: application/json` the string is JSON-encoded a second time. Timespan sensors read their newest 280/281/480/481 event |

| Case | Response that stays |
|---|---|
| Unknown token or disabled sensor | 404 `application/problem+json` `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"traceId":"…"}` |
| `code` is not an int | 400 ProblemDetails validation body (`errors.code`) |
| Less than 5 s since the sensor's newest event (not in Development) | 425 `{"status":425,"detail":"Too many requesteds","traceId":"…"}`, typo included |
| No route matches (for example a `/` inside the message) | 404, empty body |
| Wrong method (POST on a GET route, HEAD, OPTIONS) | 405, empty body, `Allow` header |
| Unhandled error (invalid Base64, invalid regex, invalid ENCAPSULADED JSON, duplicate token) | 500, empty body |
| Path matching | Case-insensitive (`/EVENT/…`). The query string is ignored |

**Side effects that stay the same:**

- **Dedupe:** no new row is written when the last two events and the new one share code, message and source IP. Only `Created` is bumped.
- **Value checks:** VALUECHECK and HEARTBEAT_VALUECHECK rewrite the code to 275/475 using the regex in `Parameters`.
- **Retention:** at most 2000 events are kept per sensor.
- **Notifications** go out within about 10 s.
- **Source:** `Source` is stored as the remote `ip:port`.

**Implementation rules:**

- **Isolation from v1:**
  - The sensor endpoints live in their own controller (`SensorApi/`) and share no DTOs or serializer settings with `/api/v1`.
  - Their output is pinned to the v0.12 settings: MVC web defaults for the event response, default STJ options for the `/read` JSON.
- **Global behaviour does not apply to sensor paths:**
  - They are anonymous.
  - None of these apply to them: the cookie scheme, antiforgery, the login rate limiter, security headers or the v1 ProblemDetails handling.
  - There is **no HTTPS redirect** anywhere in the app. TLS ends at the reverse proxy as today, so `http://` sensor URLs never receive a 307.
- **Pinning the error shapes:** deny-by-default authorization (§3.6) also applies to requests that match no endpoint, and to the 405 endpoints that routing creates. The plain 404 and 405 on sensor paths therefore need explicit handling (§3.5). The contract replay decides whether it worked.
- **Same address:** the published port (default 5001) and the paths stay the same, so `http(s)://<host>[:5001]/event/…` keeps working after the upgrade (§6.5).
- **Allowed internal changes:** changes that do not alter responses are allowed. Examples are UTC storage, per-sensor locking for dedupe and rate limit (§3.4), trimming sibling events, and new indexes.
- **Duplicate tokens** (issue 17): today every request with such a token fails with 500. The importer gives each duplicate a new token and lists it in its report. The new schema has unique indexes on both token columns.
- **URLs in the UI:** the UI shows the same URL shapes as today, built by the API from `PublicBaseUrl`:
  - `{PublicBaseUrl}/event/{writeToken}/{code}/{message}`
  - `{PublicBaseUrl}/read/{readToken}/json`

### 3.4 PostgreSQL

**Containers.** `docker.io/library/postgres:18-alpine` with a named volume, on the stack network only (no published port), with a `pg_isready` health check. `start.sh` uses the same image (§6.2).

**Data access.**

- `AddDbContextFactory<PnmtdDbContext>` (pooled) with Npgsql, configured from the `Database__*` settings.
- The 10 `new PnmtdDbContext()` call sites become factory calls.
- The hard-coded `OnConfiguring` (SQLite, a database path per OS) is removed.
- Lazy-loading proxies are removed: queries use explicit `Include`s and projections, because every N+1 is now a network round trip.

**Migrations.**

- A new baseline migration for PostgreSQL. The 28 SQLite migrations are deleted (git history keeps them).
- The app applies pending migrations at start. `pnmtd db migrate` does the same for scripts.
- The app refuses to start on a database that a newer version has migrated.

**Schema changes in the baseline:**

- All table names lower-case (`NotificationRuleSensor` becomes `notificationrulesensor`).
- Unique indexes on `SecretWriteToken` and `SecretReadToken`.
- Indexes `events (SensorId, Created DESC)` and `events (Created)`.
- `timestamptz` for every timestamp, and nullable where `DateTime.MinValue` meant "never".
- The new `users` and `auth_log` tables (§4.3).

**Time.**

- All timestamps are stored in UTC. `DateTime.Now` is replaced by an injected `TimeProvider`.
- Rules that depend on the local day use the configured time zone `TZ` (IANA name, default UTC, which is what the official images used when `TZ` was unset). These rules are uptime per day and interval descriptors such as `daily 8`.
- API v1 returns ISO 8601 with an offset. The UI shows the browser's local time.
- The sensor API returns no timestamps, so its responses are unaffected.

**Fixes that the switch requires.** SQLite serialised all writes; PostgreSQL runs them in parallel.

- Background tasks become `BackgroundService` + `PeriodicTimer`: one run at a time, with a fresh context per run. This fixes issue 12.
- Event submission (dedupe, rate limit, sibling creation) runs in a transaction that holds a per-sensor `pg_advisory_xact_lock`. The result matches what serialised SQLite produced.
- KeyValue upserts use `INSERT … ON CONFLICT`. The KeyValue helpers stop calling `SaveChanges()` in the middle of loops.
- No `DateTime.Now` inside queries (DnsCheckTask.cs:153). Pass the value as a parameter.
- Every `Skip`/`Take` gets an `OrderBy` (issue 13).
- No equality joins on timestamps (MailInboxCheckTask.cs:155-160, DbContextNotificationExtensions.cs:62, 78). Precision drops from 100 ns to 1 µs, so these compare ids or ranges instead.
- Host state and the dashboard are computed in SQL (`DISTINCT ON` for the latest event per sensor, per-day aggregates for uptime), instead of loading the events table into memory. This fixes issue 14.

**Import from v0.12 (`pnmtd import-sqlite`).**

1. **Read safely.** The importer reads a *copy* of `pnmtd.db` with `Microsoft.Data.Sqlite`, read-only and independent of the EF model. It refuses unless the newest applied migration is `20250404182653_DoneZoneHostLink` (v0.12.x).
2. **Copy.** It copies all tables in foreign-key order inside one transaction and converts the data on the way:
   - timestamps from `--source-time-zone` (default: the old container's time zone, usually UTC) to UTC,
   - upper-case GUID text to `uuid`,
   - KeyValue timestamp strings to ISO 8601.
3. **Users.** It imports `users.json` (§4.8) in the same run (`--users`).
4. **Verify.** Before it commits, it checks:
   - row counts per table,
   - the event count and newest event id per sensor,
   - the token sets,
   - foreign-key integrity.

   It commits nothing unless every check passes. It prints a report and writes it to `deploy/logs/`.
5. **Rehearse.** `--reset` replaces an existing import, for rehearsals (`IMPORT=1 ./start.sh`).

**Backup.** `deploy/backup.sh` writes `pg_dump --format=custom` plus the keys volume, and `deploy/restore.sh` puts them back. A new PostgreSQL major version means: back up, switch the image, restore. It never means just a new tag on the old volume (as in plm).

**Tests.** Integration tests run against a throwaway PostgreSQL container (in `run-tests.sh`, and as a service container in CI). The SQLite in-memory test factory is removed.

### 3.5 Hosting the SPA under `/pnmt-admin`

**The base path is a single constant.** `/pnmt-admin` is set in `PNMTD.WebUI/vite.config.ts` (`base`) and in `WebUi.BasePath` on the server. A test checks that the built `index.html` references only `/pnmt-admin/` assets. The path is fixed by design, not a runtime setting.

**Vite and router setup:**

```ts
// vite.config.ts — Vite only ever runs in a container (§6.1)
const api = process.env.PNMTD_API_URL ?? 'http://pnmtd:8080'   // the app container on the dev network
export default defineConfig({
  base: '/pnmt-admin/',
  build: { outDir: 'dist', emptyOutDir: true },               // the Dockerfile copies dist/ to wwwroot/pnmt-admin
  server: {
    proxy: {
      '/api': api,
      '^/(event|read)/': api,   // regex: a plain '/event' prefix would also capture '/events'
    },
  },
})
```

- **Router:** `createWebHistory(import.meta.env.BASE_URL)`. In-app links always go through the router.
- **Assets:** static files are referenced via `import.meta.env.BASE_URL`, never with a hard-coded `/`.
- **API client:** uses the absolute base `/api/v1`, so it does not depend on the UI path.
- **Development:** `WEB=dev ./start.sh` serves Vite at `http://localhost:5173/pnmt-admin/` and proxies to the app container. The proxy makes both look like one origin, so cookies work and no CORS is needed.

**Server pipeline:** the order matters.

```csharp
const string UiBase = WebUi.BasePath;                          // "/pnmt-admin"

app.UseForwardedHeaders();             // first: real client IP and scheme (Proxy setting)
app.UseSecurityHeaders();              // CSP, nosniff, frame-ancestors: /pnmt-admin and /api only
app.UseApiErrors();                    // /api only: exceptions and empty 401/403 become ProblemDetails
app.UseWebUiFiles();                   // /pnmt-admin → /pnmt-admin/ (301), then the build's files (anonymous)
app.UseRouting();
app.UsePlainNotFound();                // no endpoint: empty 404; routing's 405: run it, empty with Allow
app.UseRateLimiter();                  // phase 3: policies on /api/v1/auth/login* only
app.UseWhen(c => !SensorPaths.IsSensorPath(c.Request.Path), b => b.UseAuthentication());
app.UseAuthorization();                // deny by default (§3.6)

app.MapControllers();                  // /api/v1/**, /event/**, /read/**
app.MapFallbackToFile($"{UiBase}/{{**path:nonfile}}", "pnmt-admin/index.html", files).AllowAnonymous();   // SPA deep links
app.MapFallback("/api/{**rest}", () => Results.Problem(statusCode: 404));                                 // never index.html for API typos
// No catch-all MapFallback: routing creates its 405 endpoint only when no endpoint for the path
// accepts the method, and a catch-all accepts every method, so v0.12's 405s would become 404s.
// UsePlainNotFound answers the unmatched requests instead, before authentication.
// No UseHttpsRedirection: TLS ends at the reverse proxy, and sensor URLs must never be redirected (§3.3).
```

| Request | Response |
|---|---|
| `/pnmt-admin` | 301 to `/pnmt-admin/` |
| `/pnmt-admin/`, `/pnmt-admin/hosts/<id>` | `index.html` (`Cache-Control: no-cache`) |
| `/pnmt-admin/assets/app-3f9a.js` | file (`Cache-Control: public, max-age=31536000, immutable`) |
| `/pnmt-admin/assets/missing.js` | 404. The `nonfile` constraint stops the fallback from matching it |
| `/`, `/foo`, `/host`, `/hosts`, `/swagger`, `/trust/…` | 404, empty body |
| `/api/v1/unknown` | 401 when signed out, 404 ProblemDetails when signed in |
| `/event/…`, `/read/…` | exactly as in v0.12.1 (§3.3) |

**Other consequences of the subpath:**

- **Cookies:** the auth cookie uses `Path=/`, because the API lives outside the UI path. The short-lived 2FA cookie is scoped to `Path=/api/v1/auth`.
- **Sensor URLs:** the URLs shown in the UI (`writeUrl`, `readUrl`) are built by the API from `PublicBaseUrl` or the request origin, never from the UI path.
- **CSP** (UI responses only): `default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self'; frame-ancestors 'none'`.
  - PrimeVue's styled mode injects `<style>` elements, and QR codes are data URLs.
  - Scripts stay `'self'` only. Fonts are self-hosted, with no CDN.
- **Path prefixes:** the app expects `/pnmt-admin`, `/api`, `/event` and `/read` at the root of its host. A reverse proxy must forward these paths **unchanged**: no StripPrefix and no extra outer prefix.

### 3.6 Server-side protection and reverse proxy

**The server protects itself.** Access control does not depend on a reverse proxy. A proxy may restrict paths as defence in depth, but forgetting a rule there opens nothing.

| Anonymous endpoint | Why |
|---|---|
| `/event/**`, `/read/**` | The sensor contract (the secret token is in the path) |
| `/pnmt-admin/**` static files | The SPA shell must load before login. It contains no data and no configuration |
| `POST /api/v1/auth/login`, `POST /api/v1/auth/login/totp` | Login. Both are rate limited |
| `GET /api/v1/auth/me` | Session bootstrap. Answers 401 when signed out |
| `GET /api/v1/system/info` | Signed out it returns only `identity`, for the login screen |
| `/healthz` | The container health check. It answers only `ok` (200) or `unavailable` (503) |

Everything else requires a signed-in user (401 otherwise) and the right role (403 otherwise).

- **Deny by default:** the authorization `FallbackPolicy` requires an authenticated user. Only the endpoints in the table carry `[AllowAnonymous]`, plus the plain-404 fallbacks from §3.5.
- **Role policies:** `CanRead` (Viewer, User, Admin), `CanWrite` (User, Admin) and `Admin`.
  - Every v1 controller declares its policy at class level, and write actions declare `CanWrite`.
  - Responses also omit fields a role may not see (§4.10).
- **Endpoint audit test:** at test startup it enumerates every endpoint. It fails if the anonymous set differs from the table, or if a v1 endpoint has no explicit policy. A new endpoint therefore cannot slip in unprotected.
- **Role matrix test:** every v1 endpoint × anonymous / Viewer / User / Admin → the expected 401 / 403 / 2xx.
- **Further guards:** antiforgery on unsafe cookie requests, plus rate limit and lockout on login (§4.7).

**Reverse proxy example** (Traefik v3). Sensor paths are public and everything else is limited to internal networks:

```yaml
labels:
  - "traefik.enable=true"
  - "traefik.http.services.pnmtd.loadbalancer.server.port=8080"
  # sensor API: public. Case-insensitive, because the app matches /EVENT/ too.
  - "traefik.http.routers.pnmtd-ingest.rule=Host(`pnmt.example.com`) && PathRegexp(`(?i)^/(event|read)/`)"
  - "traefik.http.routers.pnmtd-ingest.priority=100"
  - "traefik.http.routers.pnmtd-ingest.entrypoints=websecure"
  - "traefik.http.routers.pnmtd-ingest.tls.certresolver=letsencrypt"
  # UI, API and everything else: internal networks only (defence in depth; the app enforces login itself)
  - "traefik.http.routers.pnmtd-admin.rule=Host(`pnmt.example.com`)"
  - "traefik.http.routers.pnmtd-admin.priority=1"
  - "traefik.http.routers.pnmtd-admin.entrypoints=websecure"
  - "traefik.http.routers.pnmtd-admin.tls.certresolver=letsencrypt"
  - "traefik.http.routers.pnmtd-admin.middlewares=pnmtd-admin-allow"
  - "traefik.http.middlewares.pnmtd-admin-allow.ipallowlist.sourcerange=10.0.0.0/8,192.168.0.0/16"
```

Traefik v2 calls the middleware `ipwhitelist`. With `Proxy=docker` set in PNMTD, the real client IP still reaches rate limiting and the audit log. `deploy/README.md` also carries nginx and Caddy snippets.

### 3.7 API v1

The API serves the UI. Everything the UI shows comes from it, including sensor URLs and status-code documentation, and no business logic lives only in the UI.

#### Conventions

- **Versioning:** base path `/api/v1`. Additive changes stay in v1. Breaking changes go to `/api/v2`.
- **Resources:** plural kebab-case nouns. Actions are explicit sub-resources (`POST /dns-zones/{id}/force-update`), not flags passed through `PUT`.
- **Contracts:** dedicated request and response DTOs in `PNMTD/Api/V1/Contracts`, never EF entities or the old POCOs. `Create…Request` and `Update…Request` contain **only writable fields**. Clients can no longer set `Created`, tokens, `Status` or computed state, which fixes issues 1–2 by design.
- **Writes:**
  - `POST` → 201 with a `Location` header and the resource.
  - `PUT` replaces the writable fields → 200 with the resource.
  - `DELETE` → 204.
  - Inline edits in the UI send a `PUT` with the cached resource's writable fields.
- **Lists:** `?page=1&pageSize=50&sort=-created&search=…` plus resource-specific filters. The response is `{ items, page, pageSize, totalCount }`, and `pageSize` is capped at 500.
- **Errors:**
  - ProblemDetails (RFC 9457) with a stable `code` extension: `not_found`, `validation_failed`, `duplicate_key`, `last_admin`, `invalid_credentials`, `totp_setup_required`, `forbidden_role`, …
  - Validation errors list `errors` per field.
  - The handling is scoped to `/api`, so the sensor API's empty-body errors stay as they are.
- **Types:** enums as strings (`"HEARTBEAT"`), ids as GUIDs, timestamps in ISO 8601 UTC (`2026-11-11T09:15:00Z`). Per-day series carry dates (`2026-11-11`) in the configured `TZ`, which `/system/info` reports.
- **Validation** happens on the server:
  - host name 2–64 chars
  - `IntervalDescriptionHelper.TryParse` for `ONE/ALL_WITHIN_TIMESPAN` parameters
  - `Regex` compile check for `ExtractMessageRegex` and VALUECHECK parameters
  - the recipient must match a notification provider: e-mail, `pushover://` or `http(s)://`
  - DNS sensors cannot be created manually
  - key-value keys must be unique (409)
- **Service layer:** `PNMTD/Services/Domain/*Service` takes over the logic that lives in controllers today:
  - host state and uptime aggregation (now in SQL),
  - cascade and sibling deletes,
  - DNS zone delete sequencing,
  - whitelisted updates.

  The v1 controllers, the sensor API and the background tasks all call these services.
- **OpenAPI:**
  - `Microsoft.AspNetCore.OpenApi` generates the document at build time into `PNMTD.WebUI/src/api/openapi.json`.
  - `tools/npm.sh run gen:api` turns it into `schema.d.ts`.
  - A Swagger UI at `/api/docs` exists in Development only. Production serves neither the document nor a UI.

#### Resources

All paths are relative to `/api/v1`. Access follows the role matrix in §4.10.

| Resource | Endpoints | Improvements over today |
|---|---|---|
| Dashboard | `GET /dashboard` | One call returns everything §5.5 shows: summary counts, problems with `failingSince`, recent state changes, and hosts with 30-day uptime and a 90-day series. It is computed in SQL and cached for about 10 s. Today this takes three calls, about 1 MB, and client-side filtering |
| Search | `GET /search?q=` | Hosts and sensors by name, for the global search |
| Hosts | `GET /hosts?state&search&page` · `POST /hosts` · `GET/PUT/DELETE /hosts/{id}` | The list includes state, sensor counts (ok, failing, no data, ignored, disabled) and 30-day uptime. The detail view merges today's `HostPoco` and `HostStatePoco`: host fields, sensors with last state and `failingSince`, and the uptime series |
| Sensors | `GET /sensors?hostId&type&state&search&page` · `POST /sensors` · `GET/PUT/DELETE /sensors/{id}` · `POST /sensors/{id}/tokens/rotate` `{ write, read }` · `POST /sensors/lookup` `{ token }` | Responses contain ready-made `writeUrl`/`readUrl` (omitted for Viewers). Tokens cannot be written via `PUT`. Rotation is a POST and can rotate one token or both. Lookup takes a write *or* read token in the body, so tokens stay out of access logs |
| Sensor events | `GET /sensors/{id}/events?status&from&to&page` | Paged on the server |
| Sensor ↔ rules | `GET /sensors/{id}/notification-rules` · `PUT/DELETE /sensors/{id}/notification-rules/{ruleId}` | Idempotent link and unlink, instead of an "Already exists" error |
| Events | `GET /events?hostId&sensorId&status&from&to&search&page` · `GET /events/{id}` · `GET /events/state-changes?from` | Server-side paging and filters, instead of "last 1000". State changes are derived transitions (OK → failed, failed → OK with duration) |
| Notification rules | `GET/POST /notification-rules` · `GET/PUT/DELETE /notification-rules/{id}` · `GET /notification-rules/{id}/sensors` · `PUT/DELETE /notification-rules/{id}/sensors/{sensorId}` | Linked sensors come with sensor and host names. The recipient is validated, and the response includes the matched `provider` (`email`, `pushover`, `http`) |
| DNS zones | `GET/POST /dns-zones` · `GET/PUT/DELETE /dns-zones/{id}` · `POST /dns-zones/{id}/force-update` · `GET /dns-zones/{id}/log?page` · `GET /dns-zones/log?page` · `PUT /dns-zones/{id}/records/{recordId}` | Force update is an explicit action. Logs are paged |
| Mail input rules | `GET/POST /mail-input-rules` · `GET/PUT/DELETE /mail-input-rules/{id}` · *should:* `POST /mail-input-rules/test` `{ rule, mailLogId }` | The response includes the target sensor and host names. Regex and codes are validated |
| Mail log | `GET /mail-log?search&processed&page` · `GET /mail-log/{id}` | Correct ordering (newest first) and paging. The matched rule name is included |
| Key values (admin) | `GET/POST /key-values` · `PUT/DELETE /key-values/{id}` | 409 on a duplicate key. Known keys (`KeyValueKeyEnums`) carry a description |
| System | `GET /system/info` · `GET /system/uplink` · `GET /system/tasks` · `GET /system/status-codes` | `info` without login returns only `identity`. With login it adds `version`, `publicBaseUrl`, `timeZone`, `totpPolicy` and the caller's role. `tasks` reports the last run of each background task against its expected interval. `status-codes` replaces the reflection-based Blazor page and feeds the UI's code labels |
| Auth, account, users, auth log | See §4.9 | |

#### Removed routes

Every old root route returns 404 in 1.0: `/host`, `/hosts`, `/sensor`, `/events/…`, `/notificationrule`, `/dnszone`, `/dnszoneentry`, `/mailinput`, `/maillog`, `/keyvalue`, `/info`, `/state`, `/trust/create-web-app-token` and `/swagger`. Their only client was the WebApp. The JWT scheme and its `Jwt:*` settings go with them.

Only `/event/…` and `/read/…` remain at the root, and they are covered by §3.3.

---

## 4. User management and authentication

### 4.1 Goals

- Users are stored in PNMTD (PostgreSQL) and log in with a username and password. TOTP is optional by default, with a setup prompt.
- Existing `users.json` users migrate without a password reset.
- No default credentials. Brute-force protection. An audit log.
- An admin who loses their password or authenticator recovers with `deploy/reset-password.sh` or `deploy/reset-totp.sh`, without touching the database by hand.
- Read-only accounts for people who only watch the dashboard.

### 4.2 What to take from PortUpAcademy

PortUpAcademy (`~/source/PortUpAcademy`) already targets `net10.0`. It uses SQL Server, so entity configuration is ported to Npgsql types.

| PortUpAcademy | Use in PNMTD | Changes |
|---|---|---|
| `PortUpAcademy.Lib/Security/TotpService.cs` | Port to `PNMTD.Lib/Security` | Add an overload that returns the matched time step, for replay protection |
| `PortUpAcademy.Lib/Security/Base32.cs` | Port as is | – |
| `PortUpAcademy.Lib/Security/PasswordHasher.cs` | Port | Add `NeedsRehash()`. The encoded format `pbkdf2-sha256$<iter>$<salt>$<key>` can already represent legacy hashes (§4.8) |
| `PortUpAcademy.Lib/Security/SecurePasswordGenerator.cs` | Port | Seeded admin, temporary passwords from resets and `add-user.sh` |
| `PortUpAcademy.Web/Auth/AuthService.cs` | Pattern | Split into two steps. Verify against a **dummy hash** when the user does not exist: the original's comment says it equalizes timing, but `user is not null && Verify(…)` short-circuits. Add lockout |
| `PortUpAcademy.Web/Auth/TotpEnrollmentService.cs` | Pattern | Store the pending secret **server-side**: in PortUpAcademy it lives in Blazor server component state, which an SPA doesn't have. Disabling 2FA requires password + code |
| `PortUpAcademy.Web/Endpoints/AuthEndpoints.cs` | Pattern (safe return URL, failure reasons, login log) | JSON API under `/api/v1/auth` instead of form posts and redirects. Return URLs must stay inside `/pnmt-admin/` |
| `PortUpAcademy.Web/Auth/AuthClaimsBuilder.cs` | Pattern | Claims: `NameIdentifier` (Guid), `Name`, `Role`, security stamp |
| `AuthenticationServiceCollectionExtensions.cs` | Pattern (persisted Data Protection keys, cookie options) | `SameSite=Strict`. Return 401/403 instead of redirects |
| `LoginLogEntry` + `LoginLogEventType` | Pattern | `auth_log` table with retention |
| `ApiTokenAuthenticationHandler` + `ApiTokenService` | Later (phase 8) | Personal API tokens, once a script needs `/api/v1` |
| `QrCodeService` (QRCoder) | Not needed | The QR code is rendered in the browser |

### 4.3 Data model (part of the PostgreSQL baseline)

Follows the existing conventions: Guid keys, lower-case table names, `timestamptz`.

**`users`**

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | Keeps the ids from `users.json` |
| `Username` | text | Unique index on `NormalizedUsername` (lower-invariant) |
| `DisplayName` | text | Was `Name` |
| `PasswordHash` | text | Encoded PBKDF2 format |
| `Role` | text | `Admin` · `User` · `Viewer` (§4.10) |
| `IsActive` | bool | |
| `MustChangePassword` | bool | Set for the seeded admin and after every reset |
| `TemporaryPasswordExpiresAt` | timestamptz? | A temporary password works for 7 days |
| `SecurityStamp` | text | Rotated on password, 2FA, role or active-state change. Invalidates other sessions |
| `TotpSecretProtected` | text? | Encrypted with Data Protection (`PNMTD.Totp.v1`) |
| `IsTotpEnabled` | bool | |
| `TotpLastUsedStep` | bigint? | Replay protection |
| `PendingTotpSecretProtected`, `PendingTotpCreatedAt` | text?, timestamptz? | Enrollment in progress (expires after 15 min) |
| `FailedLoginCount`, `LockoutEnd` | int, timestamptz? | |
| `CreatedAt`, `LastLoginAt`, `PasswordChangedAt` | timestamptz(?) | |

**`auth_log`**

- **Columns:** `Id`, `CreatedAt`, `UserId?`, `Username`, `EventType`, `IpAddress`, `UserAgent`, `Detail`.
- **Event types:** `LoginSucceeded`, `LoginFailed`, `LockedOut`, `TotpFailed`, `Logout`, `TotpEnabled`, `TotpDisabled`, `TotpReset`, `PasswordChanged`, `PasswordReset`, `UserCreated`, `UserUpdated`, `RoleChanged`, `UserDeleted`.
- **Retention** follows the existing `MAXIMUM_NUM_OF_*` pattern (`MAXIMUM_NUM_OF_AUTH_LOG_ENTRIES = 10000`).
- **Origin:** resets done by a deploy script are logged with `Detail = "cli"`.

### 4.4 Passwords

- **Hashing:** PBKDF2-HMAC-SHA256, 210,000 iterations, 16-byte salt, 32-byte key, compared in constant time (ported `PasswordHasher`).
- **Rehash:** `NeedsRehash()` checks the iteration count. If it is lower than current, the hash is updated transparently after the next successful login.
- **Policy:** at least 10 characters (as today), at most 256, no composition rules. Changing your own password requires the current password.
- **Temporary passwords:** passwords from an admin reset, `add-user.sh` or `reset-password.sh` are generated, shown once and expire after 7 days. They must be changed at the next login.

### 4.5 Login flow

Login is a two-step JSON API, so the password is sent only once:

```
POST /api/v1/auth/login { username, password }                       [rate limited]
 ├─ locked out                         → 429 problem { code: "locked_out" } + Retry-After
 ├─ unknown user / wrong pw / inactive → 401 problem { code: "invalid_credentials" }   (+ counter, audit)
 ├─ TOTP enabled                       → 200 { next: "totp" } + "pnmt.2fa" cookie (5 min, Path=/api/v1/auth)
 └─ OK                                 → 200 { next: "done", user, flags } + "pnmt.auth" cookie (Path=/)

POST /api/v1/auth/login/totp { code }                                [requires pnmt.2fa cookie, rate limited]
 ├─ invalid / replayed / expired       → 401 problem { code: "invalid_code" }          (+ counter, audit)
 └─ OK                                 → 200 { next: "done", user, flags } + "pnmt.auth" cookie, pnmt.2fa cleared

flags = { mustChangePassword, totpSetupRequired, totpSetupSuggested }
```

After login the SPA goes to `returnUrl` if it is a path under `/pnmt-admin/`, and to `/pnmt-admin/` otherwise.

### 4.6 TOTP

- **Parameters:** HMAC-SHA1, 6 digits, 30 s period, ±1 step of clock drift. This is compatible with Google Authenticator, Microsoft Authenticator, Aegis, Bitwarden, 1Password and similar apps. The issuer is `Auth:TotpIssuer`, default `PNMT (<Identity>)`, so several instances can be told apart in the app.
- **Enrollment:**
  1. `POST /api/v1/account/totp/setup` creates a secret, stores it encrypted as *pending*, and returns `{ secret, otpauthUri }`. The SPA shows the QR code and the secret in groups of four characters.
  2. `POST /api/v1/account/totp/confirm { code }` validates the code against the pending secret, enables TOTP, stores the matched step and rotates the security stamp.
- **Replay protection:** a code is accepted only if its time step is greater than `TotpLastUsedStep`.
- **Disable:** `POST /api/v1/account/totp/disable { password, code }` requires re-authentication.
- **No recovery codes.** A user who lost their authenticator asks an admin. The admin uses "Reset 2FA" in the user list, or `deploy/reset-totp.sh <name>` on the server. Both turn TOTP off, sign out every session and log `TotpReset`, and the next login offers setup again.
- **Policy (`Auth:TotpPolicy`):**
  - **`Optional` (default):** after a login without 2FA, the SPA shows a setup prompt ("Protect your account with two-factor authentication", with *Set up now* and *Later*). *Later* lasts until the next login. The user list marks accounts without 2FA, and `deploy/info.sh` warns about admins without 2FA.
  - **`RequiredForAdmins` / `Required`:** the session carries a `pnmt:totp_setup_pending` claim. A global authorization policy then answers every `/api/v1` call except `/api/v1/auth/*` and `/api/v1/account/totp/*` with `403 { code: "totp_setup_required" }`, and the SPA router redirects to setup. `MustChangePassword` uses the same mechanism (`pnmt:must_change_password`).

### 4.7 Sessions, CSRF and brute force

- **Cookie `pnmt.auth`:**
  - HttpOnly, `SameSite=Strict`, `Path=/`.
  - `Secure` when the request is HTTPS, which depends on the forwarded headers being correct.
  - Sliding expiry of `Auth:SessionDays` (default 7).
  - The cookie handler never redirects: unauthenticated requests get 401 and forbidden ones get 403.
- **Session revocation:** `OnValidatePrincipal` compares the cookie's stamp, role and `IsActive` with the database, cached for about one minute. Changing a password, 2FA or a role, or deactivating a user, therefore ends other sessions. A "Sign out all sessions" button and the deploy scripts do the same.
- **Data Protection:** `PersistKeysToFileSystem("/var/lib/pnmtd/keys")` (volume `pnmtd-keys`) and `SetApplicationName("PNMTD")`. This lets cookies survive restarts and encrypts TOTP secrets. **`backup.sh` saves the keys together with the database.**
- **Authentication scheme:** cookie only. There is no Bearer scheme in 1.0.
- **Antiforgery:**
  - Uses ASP.NET Core antiforgery with the header `X-XSRF-TOKEN`.
  - The token cookie is issued by `GET /api/v1/auth/me`.
  - It is validated on unsafe methods of all `/api/v1` requests except the login endpoints.
  - The sensor API is not affected (§3.3).
- **Rate limiting:** the built-in `AddRateLimiter` with a fixed window of 10 requests per minute per client IP, on `/api/v1/auth/login*` only. Behind a reverse proxy this needs a correct `Proxy` setting. The sensor API's own 425 rule is separate and unchanged.
- **Lockout:** after 5 consecutive failures (password or TOTP), the account is locked for 5 minutes, doubling up to 60 minutes. A success resets the counter, and admins (UI) and `reset-password.sh` can unlock. The error message stays generic.

### 4.8 Bootstrap and import of existing users

**Import:** `pnmtd import-sqlite --users <users.json>` (run by `deploy/upgrade-1.0.sh`, §6.5) imports the WebApp's users in the same transaction as the monitoring data:

- **Fields:** `Id`, `Username`, `Name` → `DisplayName`, and `IsAdmin` → `Role` (`Admin`, otherwise `User`) are mapped.
- **Hashes:** they are converted losslessly. `PasswordHash` + `Salt` become `pbkdf2-sha256$10000$<Salt>$<PasswordHash>`. This works because `KeyDerivation.Pbkdf2(HMACSHA256, 10000, 32)` and `Rfc2898DeriveBytes.Pbkdf2(…, SHA256, 32)` are the same algorithm. A unit test must prove this with a fixture generated by the old `WebAppUser.SetPassword`.
- **Oldest format:** handled the same way `WebAppUserManager` does. A single user without `Id` or `Name` gets a new Guid, `Name = Username` and `Role = Admin`.
- **Default password:** if the user `pnmt` still has the password `pnmt`, `MustChangePassword` is set to true.
- **Report:** the import report lists the imported users and their flags.

**Seed:** if the users table is still empty after migrations (a new install), the app creates the admin `Auth:InitialAdmin:Username` (default `admin`) with `Auth:InitialAdmin:Password`. `deploy/init.sh` generates that password into `.env` and prints it once. Without it, the app logs a random 20-character password once. `MustChangePassword` is set to true.

Default credentials are never created again.

### 4.9 Endpoints

All paths are relative to `/api/v1`.

| Method | Path | Access | Purpose |
|---|---|---|---|
| POST | `/auth/login` | anonymous, rate limited | Step 1 |
| POST | `/auth/login/totp` | `pnmt.2fa` cookie | Step 2 |
| POST | `/auth/logout` | signed in | |
| GET | `/auth/me` | anonymous → 401, or signed in | Session bootstrap for the SPA. Returns user, role and flags, and issues the XSRF token |
| POST | `/account/password` | signed in | Change own password (current + new) |
| POST | `/account/totp/setup` · `/confirm` · `/disable` | signed in | 2FA self-service |
| POST | `/account/sessions/revoke` | signed in | Sign out all sessions |
| GET, POST | `/users` | admin | List (paged); create with a role (temporary password, shown once) |
| GET, PUT, DELETE | `/users/{id}` | admin | Last-admin guard on delete, demote and deactivate (409 `last_admin`) |
| POST | `/users/{id}/reset-password` · `/reset-totp` · `/unlock` | admin | |
| GET | `/auth-log?userId&type&page` | admin | Audit log |

### 4.10 Roles, CLI and deploy scripts

| Capability | Viewer | User | Admin |
|---|:---:|:---:|:---:|
| Dashboard, hosts, sensors, events, DNS zones, notification rules, mail input rules, docs: read | ✓ | ✓ | ✓ |
| Sensor tokens and write/read URLs, mail log contents | – | ✓ | ✓ |
| Create, edit and delete monitoring objects, rotate tokens, force a DNS update, ignore a sensor | – | ✓ | ✓ |
| Own account: password, 2FA, sign out all sessions | ✓ | ✓ | ✓ |
| Users, auth log, key values, system health | – | – | ✓ |

- **Enforcement:** the API enforces the matrix (403 `forbidden_role`). Sensor responses for a Viewer contain no token or URL fields.
- **UI:** the UI hides what the role cannot do, so there are no dead buttons.
- **CLI:** admin commands live in the main binary and run before the web host starts. The image provides a `pnmtd` wrapper:

  ```
  pnmtd db migrate
  pnmtd import-sqlite --from <pnmtd.db> [--users <users.json>] [--source-time-zone <tz>] [--reset]
  pnmtd users list
  pnmtd users add <name> [--role admin|user|viewer]
  pnmtd users reset-password <name>     # temporary password, sessions revoked, lockout lifted
  pnmtd users reset-totp <name>         # TOTP off, sessions revoked
  pnmtd users unlock <name>
  pnmtd users set-role <name> <role>
  ```

  Passwords are generated and printed once, or read from standard input, and never passed as arguments.
- **Deploy scripts:** `deploy/add-user.sh`, `list-users.sh`, `reset-password.sh` and `reset-totp.sh` wrap these commands (§6.4).
- **Retired tools:** `PNMT.WebAppCli` and `PNMTD.Cli` are retired.

---

## 5. Frontend design

### 5.1 Stack

| Concern | Choice | Why |
|---|---|---|
| Framework | Vue 3.5, `<script setup>`, TypeScript (strict), `vue-tsc` | Type safety against the API |
| Build | Vite with `base: '/pnmt-admin/'`. Runs only in containers (§6.1) | Fast dev server with a proxy. Hashed assets under the subpath |
| Components | **PrimeVue 4**, styled mode, Aura preset with PNMT design tokens | See below |
| Charts | None. Own SVG components (`UptimeBar`, `Sparkline`) | The only charts are uptime strips, so a chart library would be dead weight |
| Icons, fonts | PrimeIcons. Inter (variable, `@fontsource`, self-hosted), and a monospace font for tokens and codes | No CDN: works with the CSP and on intranets |
| Routing | Vue Router, `createWebHistory(import.meta.env.BASE_URL)` | Deep links under `/pnmt-admin/…`. `meta.requires` guards (auth, role, totp-setup, must-change-password) |
| Server state | `@tanstack/vue-query` | Caching, background refetch (dashboard auto-refresh), invalidation after mutations |
| Client state | Pinia | Session, current user and role, UI preferences |
| API types | `openapi-typescript` + `openapi-fetch` | Types are generated from the `/api/v1` OpenAPI document, so contract changes show up as TypeScript errors |
| QR code | `qrcode` (npm) | Renders the `otpauth://` URI in the browser, so no server-side QR library is needed |
| Zone file editor | CodeMirror 6 (*should*) | Line numbers and highlighting, instead of a plain textarea |
| Tests | Vitest + Vue Test Utils. Playwright for e2e and layout checks | |
| Lint | ESLint + Prettier | |
| Dependencies | Exact versions, `package-lock.json`, `npm ci`. An npm allowlist check as in exam-trainer (*could*) | |

**Why PrimeVue 4:**

- **Data tables:** its `DataTable` in lazy mode maps 1:1 onto the v1 paging contract (server-side sort, filter and paging). It also offers `size="small"` rows, frozen columns and column toggles.
- **Components:** it has the rest of what this UI needs:
  - `InputOtp` for TOTP,
  - `AutoComplete` for host and sensor pickers and the global search,
  - `Drawer`, `Dialog`, `Toast`, `ConfirmPopup`, `Tag`, `Select`,
  - `DatePicker` for time ranges.
- **Design tokens:** density and the status colours are set in one place, for light and dark.

**Alternatives considered:**

- **Vuetify:** Material spacing works against the compact goal and would need overriding everywhere.
- **Own components, as in exam-trainer:** this UI is mostly data tables and forms, and building those from scratch would take weeks.

The UI stays English-only, as today.

### 5.2 Structure

```
PNMTD.WebUI/src/
  main.ts, App.vue
  router/        createWebHistory(import.meta.env.BASE_URL) + guards (auth, role, totp-setup, must-change-password)
  api/           client.ts (openapi-fetch, baseUrl '/api/v1', XSRF header, 401 handling), openapi.json + schema.d.ts
                 (generated), one query module per resource
  stores/        session.ts, preferences.ts (Pinia)
  design/        tokens.ts (PrimeVue preset: density, status colours, light/dark), base.css
  layouts/       AppLayout.vue (drawer + app bar), AuthLayout.vue (login and 2FA screens)
  components/    StatusBadge, CodeTag, UptimeBar, Sparkline, RelativeTime, InlineField, PageHeader, KpiTile,
                 ResponsiveTable, FilterBar, CopyField, EmptyState, ErrorState, SensorUrlPanel, QrCode
  composables/   useConfirm, useNotify, useInlineSave, useServerTable (maps v1 paging to DataTable lazy mode), useRole
  views/         dashboard/, events/, hosts/, sensors/, notification-rules/, dns-zones/,
                 mail/, admin/, account/, auth/, docs/
```

### 5.3 Design principles

**Keep the general construct.** Navigation, pages, entities and flows stay as they are today (§2.2), so existing users find everything in the same place. What changes is density, visual hierarchy and how much useful information each screen carries.

- **Compact:**
  - 14 px base text, 32 px table rows, a 4/8 px spacing scale, small inputs.
  - No banner cards: every page starts with one 40 px header line (breadcrumb and title, key status, primary action on the right). Today the banner and button row take about 150 px before any content.
  - Fluid width on desktop, instead of a 1280 px container.
- **One visual language:** PrimeVue tokens for everything, and no Bootstrap remnants.
- **Status at a glance:** one `StatusBadge` everywhere. It combines icon, colour and text, so it can be read without colour:

  | State | Icon | Colour | Notes |
  |---|---|---|---|
  | OK | check-circle | green | |
  | Failing | times-circle | red | |
  | No data | dashed circle | grey | Sensor never reported (today: "OK, code −1") |
  | Ignored | eye-slash | grey, row muted | Today indistinguishable from OK/failing |
  | Disabled | power-off | grey, row muted | |
  | Degraded | exclamation-triangle | amber | Uplink down, background task overdue |

- **Codes with meaning:** `CodeTag` always shows code and name (`460 Heartbeat missing`), coloured by range (≤ 399 success), with names from `/system/status-codes`.
- **Time:** relative ("5 min ago"), with the absolute time as a tooltip. "Failing since" means since the failure *started*.
- **Long text:** messages, notes and values are cut to one line, with a tooltip and a click to expand.
- **Destructive actions:** they live in a ⋯ menu or at the end of a form, always with confirmation, and never as the first button (today Delete is the first button on every detail page).
- **Detail pages:** a header strip, then a two-column grid from 1200 px (configuration on the left, live data on the right), and one column below that.
- **Responsive:**
  - Breakpoints at 600, 960, 1200 and 1600 px.
  - Below 600 px the drawer becomes an overlay that starts closed, tables turn into two-line lists (`ResponsiveTable`), and filter bars fold into a filter button.
  - No horizontal page scrolling down to 360 px width.
- **Themes:** light and dark. The system setting is the default, and the user menu has a toggle.

### 5.4 Shell

- **Drawer:**
  - The same groups as today: *Monitoring* (Dashboard, Hosts, Events, DNS zones, Mail inputs, Mail log, Notification rules), *Admin* (Key values, Users, Auth log, System health) and *Help* (Documentation).
  - It collapses to an icon rail on desktop.
  - Items the role cannot use are hidden.
  - Identity and version are pinned at the bottom.
- **App bar (48 px):**
  - Instance identity.
  - Global search (Ctrl/⌘ K: hosts and sensors by name, plus exact token lookup). It replaces the dashboard's token search box.
  - "Updated 12 s ago" with a manual refresh.
  - Theme toggle.
  - User menu (Account, Security, Logout).
- **Failure count:** shown as a badge on the Dashboard item and in the tab title (`(3) PNMT · <identity>`), so a background tab still shows problems.

### 5.5 Dashboard

**Goal:** answer "is anything wrong, and what exactly?" at a glance, without page scrolling, with 50+ hosts and 300+ sensors, and still work on a phone.

**What is wrong today** (§2.3 issues 14–16 and 23–24, and the UI survey):

- **Space:** the dashboard is about 5,000 px tall with 50 hosts. Every failing host takes a heavy card, and a single "Everything is fine" card holds an h1.
- **Missing information:**
  - Failures don't show the error message.
  - "Since" is wrong.
  - Host rows have no status.
  - The counts mislead: "Failures" counts hosts, and "Sensors" includes disabled and ignored ones.
  - Uplink, DNS and mail health are invisible.
- **Duplication:** failing hosts appear twice, and the "last 10 error events" often repeat one flapping sensor.
- **Freshness:** nothing refreshes.

**Layout (≥ 1200 px):**

```
┌ Summary strip ─────────────────────────────────────────────────────────────────────────────┐
│▌3 hosts failing │ Sensors 286 ok · 7 failing · 3 no data · 4 ignored · 2 off │ Uplink ok   │
│▌                │ Tasks ok (last 8 s) │ DNS 12 ok · 1 mismatch │ Mail checked 40 s ago      │
├ Problems ──────────────────────────────────────────────┬ Hosts  [filter…] [Rows|Tiles] ⇅ ───┤
│ ✖ backup-srv · 2 failing                        open › │ ✖ backup-srv   97.1%  ▮▮▮▮▮▮▮▯▮▮▮▮…│
│   ✖ disk     475 Value check failed  "93 % used"       │ ✖ fw-01        99.0%  ▮▮▮▮▮▮▮▮▮▮▮▯…│
│              since 2 h 14 min · last 1 min · 24h ▮▮▮▯▯ │ ● mail         100 %  ▮▮▮▮▮▮▮▮▮▮▮▮…│
│   ✖ nightly  460 Heartbeat missing   since 1 d 3 h     │ ● web-01       99.9%  ▮▮▮▮▮▮▮▮▮▮▮▮…│
│ ✖ fw-01 · 1 failing                             open › │ …  (the panel scrolls, the page    │
│   ✖ ping     470 Ping failed         since 5 min       │     does not)                      │
├ Recent state changes (24 h) ───────────────── all › ───┤                                    │
│ 10:42  fw-01 / ping      OK → 470 Ping failed          │ ▸ Disabled (3)                     │
│ 10:31  web-01 / http     460 → OK  (down 3 h 12 min)   │                                    │
└────────────────────────────────────────────────────────┴────────────────────────────────────┘
```

1. **Summary strip** (one row, about 64 px):
   - **Overall state:** coloured bar on the left (green, red or amber).
   - **Explicit counts:** failing hosts, and sensors split into OK, failing, no data, ignored and disabled.
   - **Health that is hidden today:**
     - uplink state,
     - background tasks (last run of each against its interval, from the `LAST_*` keys),
     - DNS mismatches,
     - last mailbox check.
   - **Navigation:** every tile opens the matching filtered list (for example *Hosts → failing*, *Events → failed, last 24 h*).
2. **Problems:** one compact list of failing, non-ignored sensors, grouped by host.
   - **Order:** longest-failing first, or by name via a toggle.
   - **Each sensor** takes one or two lines:
     - status and name, with a type icon,
     - `CodeTag`,
     - the **last message** (the actual error),
     - **failing since** (start of the failure),
     - last event,
     - a 24-hour strip.
   - **Navigation:** a click opens the sensor, and the host line opens the host.
   - *Should:* an inline "Ignore" for User and Admin, with an undo toast.
   - **Nothing failing:** a single green line ("No failures · 286 sensors OK") instead of a card.
3. **Recent state changes:** transitions from the last 24 h (OK → failed, and failed → OK with the outage duration), newest first, at most 15. They replace the "last 10 error events", which repeat flapping sensors. "All ›" opens Events.
4. **Hosts panel:**
   - **Contents:** all enabled hosts, failing ones first, then by name. A filter box and sorting by name, uptime or state.
   - **Disabled hosts** are counted in the summary and collapsed at the bottom, instead of being dropped silently.
   - **Two modes**, remembered per browser:
     - **Rows** (default on desktop): 26 px per host with status, name, 30-day % and a 90-day `UptimeBar` that ends *today* and has a tooltip per day (date, %). About 25 hosts are visible at 900 px height, and the panel scrolls internally.
     - **Tiles:** a dense grid of host tiles (name, status colour, 30-day %, failing count). 50 hosts fit in about 300 px.
5. **Freshness:**
   - Refetch every 30 s and when the window regains focus.
   - No full-screen overlay: stale data stays visible, and "updated … ago" shows its age.
   - A connection problem shows a thin "Connection lost, retrying" banner instead of blanking the page.

**Mobile (< 600 px):**

- The summary becomes a 2×3 grid of small tiles.
- Problems become one card per host with two-line sensor rows.
- Recent changes are collapsed.
- Hosts default to tiles in 2 columns.
- `UptimeBar` draws as many days as fit at ≥ 3 px per day (at least 30), always ending today, so it never crops the recent days the way today's bar does.

**Data:** `GET /api/v1/dashboard` returns all of the above in one response.

- **Host-level day values** average only the sensors that have data on that day. This fixes the fake red days (issue 15).
- **`failingSince`** is the first failing event after the last success.
- **Performance:**
  - The response is cached on the server for about 10 s, because every open browser polls.
  - Target: under 50 KB for 50 hosts and 300 sensors (today about 1 MB).
  - Target: p95 under 200 ms with 2000 events per sensor.

**Acceptance targets** (Playwright, `SEED=1` fixture: 50 hosts, 300 sensors, 6 failing sensors in 3 hosts):

- At 1440×900: the summary, all problems and at least 20 host rows are visible without page scrolling.
- At 1920×1080: the same, plus the recent state changes.
- At 390×844: the summary and the first failing host are above the fold. There is no horizontal scrolling at 360 px.
- Both themes, with screenshots attached to the CI run.

### 5.6 Other views

The pages and routes are the same as today, rebuilt on the principles above. Routes are relative to `/pnmt-admin`. Improvements are tagged *must*, *should* or *could*; only *must* blocks the release.

| Area | New route(s) | Layout | Improvements on top of parity (§2.2) |
|---|---|---|---|
| Login | `/login`, `/login/2fa` | Centered card | `InputOtp` step, lockout feedback, `returnUrl`, no default credentials. *Must* |
| Account | `/account`, `/account/security`, `/account/setup-2fa` | Two columns | **New.** Own password, 2FA setup and disable, sign out all sessions. The setup prompt after login (§4.6). *Must* |
| Dashboard | `/` | §5.5 | §5.5. *Must*, except inline Ignore (*should*) |
| Hosts | `/hosts`, `/hosts/new` | Table, with a list on mobile | Columns: status, name, location, sensors (ok / failing / total), 30-day % with a 30-day strip, last event. Quick filters (All, Failing, Disabled), search. The raw Id column is dropped. *Must* |
| Host detail | `/hosts/:id` | Header strip: status, name, location, enabled switch, 90-day bar, ⋯ (edit, delete). Left: properties (inline) and notes. Right: sensors | The sensors table shows all states incl. ignored and no data, with type, `CodeTag`, last message, last event, failing since, 24h/30d, and "Add sensor" in its header. The delete dialog states how many sensors and events will be removed. *Must* |
| Sensor create | `/hosts/:id/sensors/new` | One two-column form | Type-dependent Parameters field with help (PING target, VALUECHECK regex, timespan descriptors such as `daily 8`, `weekly monday` or `monthly 15`), validated by the server. Valid default type, "Interval" spelled right. Shows the URLs after creation. *Must* |
| Sensor detail | `/sensors/:id` | Header strip: `StatusBadge`, name, host breadcrumb, type, `CodeTag`, failing since / last event, ⋯ (delete, rotate tokens). Left: *Configuration* (type-aware inline edit), *Integration*, *Notifications*. Right: *Status & uptime* and *Events* | Integration panel: write and read URLs with copy buttons, plus curl tabs (GET, POST, Base64 `$` prefix, JSON for `ENCAPSULADED`), and token rotation. Hidden for Viewers. Rules are linked via autocomplete. The 90-day bar and 24h/30d. Paged events on the first screen. Sibling sensors for `ENCAPSULADED`. *Must* |
| Events | `/events` | Full-height table | Filter bar: time presets (1 h, 24 h, 7 d, custom), status, host, sensor, text. `CodeTag`, relative time, detail drawer, a "Live" toggle (10 s). Server paging. *Must* |
| Notification rules | `/notification-rules`, `/notification-rules/new`, `/notification-rules/:id` | Table, detail with two columns | Type, recipient, matched provider, linked-sensor count. Linked sensors by host and sensor name, with link and unlink from the rule side. Delete. An explanation of each rule type. *Must* |
| DNS zones | `/dns-zones` | Table, history in a collapsible panel | Status, name, host, records with mismatch count, last and next check. Fixed history pagination. *Must* |
| DNS zone | `/dns-zones/:id`, `/dns-zones/new`, `/dns-zones/:id/edit` | Header strip with actions. Records table | Reference and actual values side by side, with mismatches highlighted. "Mismatches only" is on by default when there are any. Force update polls until the zone is processed. *Must.* CodeMirror zone editor. *Should* |
| Mail inputs | `/mail-inputs`, `/mail-inputs/new`, `/mail-inputs/:id` | Table. The editor has two columns: match rules on the left, outcome and target sensor on the right | Delete. Regex validation. *Must.* Live regex test against recent mail-log entries. *Should* |
| Mail log | `/mail-log` | Full-height table, detail drawer | Own page, newest first, server paging and search, rule names instead of GUIDs. Not visible to Viewers. *Must* |
| Key values | `/admin/key-values` | One table, inline edit, add row inline | Correct labels, descriptions for known keys, search, read-only rows marked. *Must* |
| Users | `/admin/users` | Table, detail drawer | Role, 2FA status, last login, locked, active. Reset password (shown once), reset 2FA, unlock, last-admin guard on demote and deactivate. *Must* |
| Auth log | `/admin/auth-log` | Table | **New.** *Must* |
| System health | `/admin/system` | Two panels | **New.** Last run of each background task against its interval, uplink state and history. *Should* |
| Documentation | `/docs` | Searchable sections | Served from the API: status codes, sensor types, notification rule types, and a sensor API reference with examples that match §3.3. *Must* |

### 5.7 Cross-cutting behaviour

- **InlineField** replaces `EditableTextComponent`:
  - Enter or blur saves, Esc cancels.
  - Saves are optimistic. On error they roll back with a toast, and server validation messages appear at the field.
  - It supports text, number, bool (as a switch, not "True"/"False") and enum values, and sends a `PUT` with the cached resource's writable fields.
- **UptimeBar:**
  - SVG that scales to its width and always ends today. Tooltip per day.
  - Thresholds: ≥ 0.99 green, ≥ 0.9 amber, < 0.9 red, no data grey. This fixes issue 23.
- **RelativeTime** updates itself, with the absolute time as a tooltip. It works correctly because v1 timestamps are UTC with an offset.
- **Data freshness:** the dashboard and lists refetch every 30 s and when the window regains focus. Mutations invalidate the affected queries.
- **Errors** are mapped by the ProblemDetails `code`:
  - 401: clear the session and go to `/pnmt-admin/login?returnUrl=…`.
  - 403 `totp_setup_required` / `must_change_password`: redirect to the matching screen.
  - 403 `forbidden_role`: a toast ("Not allowed for your role"). This should not happen, since the UI hides those actions.
  - `validation_failed`: show the errors on the form fields.
  - Anything else shows a toast.
- **Forms:** client rules mirror the server rules for quick feedback, but the **server stays the source of truth**.

---

## 6. Development, packaging and deployment

### 6.1 Tooling rule: no node on the host

- **Containers only:** `node`, `npm`, `npx`, Vite, Vitest, ESLint and Playwright run only in containers: Podman locally, Docker in CI and in image builds. Nothing in the repo expects node on the host.
- **Image:** `docker.io/library/node:24-slim` (LTS). Debian rather than Alpine: as noted in exam-trainer, musl's resolver fails lookups when Podman's DNS answers AAAA with SERVFAIL.
- **node_modules:** kept in the named volume `pnmtd-web-node-modules` (Linux binaries, never the Mac's). They are reinstalled with `npm ci` whenever `package-lock.json` changes.
- **`tools/npm.sh`:** ad-hoc commands with the same image and volume:

  ```
  tools/npm.sh install --save-exact primevue@4.x.y
  tools/npm.sh run gen:api        # openapi.json → src/api/schema.d.ts
  tools/npm.sh run lint
  ```

- **.NET:** the SDK may be installed on the host for IDE debugging. `start.sh`, `run-tests.sh` and the image build compile in containers anyway, so a green run does not depend on the host.

### 6.2 `start.sh` and `stop.sh` (debugging)

These are modelled on plm and exam-trainer. They use Podman by default (`ENGINE=docker` also works) and are not how production runs.

```
./start.sh               build the image and (re)start PostgreSQL + PNMTD: http://localhost:7327/pnmt-admin/
WEB=dev ./start.sh       also Vite with HMR: http://localhost:5173/pnmt-admin/ (proxies /api, /event, /read to the app)
SEED=1 ./start.sh        fill an empty database with demo data (the Playwright fixture: 50 hosts, 300 sensors)
IMPORT=1 ./start.sh      import the v0.12 data in LEGACY_DIR again, replacing the database (rehearses the upgrade)
VERBOSE=1 ./start.sh     show the build log, which is otherwise printed only on failure
PORT=… WEB_PORT=…        other host ports
./stop.sh                stop and remove the containers and the network; volumes are kept
./stop.sh --data         also offer to delete each volume, one question each
```

`start.sh` works in this order:

1. **Settings:** creates `dev.env` on the first run (gitignored, mode 0600, `deploy/.env` format). It generates `PNMTD_DB_PASSWORD` and an admin password into it.
2. **Ports:** checks that its ports are free and names the container that holds one if not.
3. **Image:** builds it from the same `Dockerfile` as production (node runs in its build stage).
4. **Database:** creates the network `pnmtd-dev` and starts `pnmtd-db` (`postgres:18-alpine`, volume `pnmtd-pgdata`). It waits for `pg_isready` over TCP.
5. **Migrations and data:**
   - Runs `pnmtd db migrate`.
   - On an empty database, or with `IMPORT=1`, it imports `LEGACY_DIR` if set: a v0.12 `pnmtd-data` folder plus an optional `users.json`, mounted read-only. It runs `pnmtd import-sqlite --reset` and prints the report.
   - With `SEED=1` and no import, it runs `pnmtd dev seed`, which exists only in Development. This replaces the implicit `DbTestHelper` seeding.
6. **App:** starts `pnmtd` with `ASPNETCORE_ENVIRONMENT=Development`, host port 7327 → 8080, volume `pnmtd-keys-dev` and the `ping_group_range` sysctl, then waits for `/healthz`.
7. **Vite (`WEB=dev` only):** starts `pnmtd-web` (`node:24-slim`) with `PNMTD.WebUI` bind-mounted, node_modules in their volume, and `tools/vite-dev.config.mjs`. That config uses a polling watcher, because the Podman VM passes no file events through the mount.
8. **Summary:** prints the URLs, the admin login (development only), what was imported and the next steps.

Vue edits are live with `WEB=dev`. .NET changes need a rerun. `stop.sh` removes `pnmtd`, `pnmtd-web` and `pnmtd-db` (the app before its database), then the network. With `--data` it asks about `pnmtd-pgdata`, `pnmtd-keys-dev` and `pnmtd-web-node-modules` one by one.

In Development the sensor API skips its 5-second rule (unchanged behaviour). The contract replay therefore runs the image in Production mode (§8.1).

### 6.3 Dockerfile (multi-stage)

```dockerfile
# 1. Web UI. Node only ever runs here, or in the containers that start.sh and tools/npm.sh start.
FROM --platform=$BUILDPLATFORM docker.io/library/node:24-slim AS web
WORKDIR /web
ENV NPM_CONFIG_UPDATE_NOTIFIER=false
COPY PNMTD.WebUI/package.json PNMTD.WebUI/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY PNMTD.WebUI/ ./
RUN npm run build                                            # → /web/dist

# 2. .NET: one portable, framework-dependent build for amd64 and arm64
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY PNMTD/PNMTD.csproj PNMTD/packages.lock.json PNMTD/
COPY PNMTD.Lib/PNMTD.Lib.csproj PNMTD.Lib/packages.lock.json PNMTD.Lib/
RUN dotnet restore PNMTD/PNMTD.csproj --locked-mode
COPY PNMTD/ PNMTD/
COPY PNMTD.Lib/ PNMTD.Lib/
COPY --from=web /web/dist PNMTD/wwwroot/pnmt-admin/
RUN dotnet publish PNMTD/PNMTD.csproj -c Release -o /app --no-restore -p:UseAppHost=false && rm -f /app/*.pdb

# 3. Runtime: Ubuntu, not Alpine (§3.2)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget iputils-ping \
 && rm -rf /var/lib/apt/lists/* \
 && cp /usr/bin/ping /usr/bin/ping.plain && mv -f /usr/bin/ping.plain /usr/bin/ping \
 && mkdir -p /var/lib/pnmtd/keys && chown -R app:app /var/lib/pnmtd \
 && printf '#!/bin/sh\nexec dotnet /app/PNMTD.dll "$@"\n' > /usr/local/bin/pnmtd && chmod 0755 /usr/local/bin/pnmtd
COPY --from=build /app /app
USER app
WORKDIR /app
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD wget -q -O /dev/null "http://127.0.0.1:${ASPNETCORE_HTTP_PORTS:-8080}/healthz" || exit 1
ENTRYPOINT ["dotnet", "/app/PNMTD.dll"]
```

The Ubuntu image already contains `tzdata`, which `TZ` needs (§3.4). [Dockerfile](../Dockerfile) is the current state of this sketch. Add `**/node_modules`, `PNMTD.WebUI/dist`, `PNMTD/wwwroot/pnmt-admin`, `artifacts/`, `dev.env` and `.tmpcheck/` to `.dockerignore` and `.gitignore`.

### 6.4 The `deploy/` folder (production)

The production stack is a published image plus PostgreSQL: two containers and two named volumes, operated by scripts. It is modelled on plm's `deploy/`. Nothing in it builds from source.

```
deploy/
  README.md            install, operate, back up, reverse proxy (Traefik, nginx, Caddy), what lives where
  UPGRADING-1.0.md     from a v0.12 installation (two containers, SQLite) to 1.0                   (§6.5)
  docker-compose.yml   db + pnmtd; local changes go into docker-compose.override.yml (gitignored)
  .env.example         every setting, commented
  lib.sh               shared helpers: read .env without sourcing it, find the container, run `pnmtd …` in it
  init.sh              writes .env (0600) with a generated DB password and initial admin password
  info.sh              running? version, URL, health, warnings
  update.sh            backup.sh, then pull and up -d; migrations apply at start
  backup.sh            backups/pnmtd-<stamp>/: database.pgdump (pg_dump -Fc) + keys.tar.gz
  restore.sh           puts a backup folder back, here or into a fresh deployment
  add-user.sh          <name> [--role admin|user|viewer]; password generated, shown once
  list-users.sh        username, role, 2FA, last login, locked, must-change
  reset-password.sh    <name>: temporary password (7 days), all sessions signed out, lockout lifted; 2FA unchanged
  reset-totp.sh        <name>: 2FA off, all sessions signed out; the next login offers setup again
  upgrade-1.0.sh       one-time move from v0.12                                                     (§6.5)
  logs/, backups/      created by init.sh, gitignored
```

```yaml
# deploy/docker-compose.yml
name: pnmtd
services:
  db:
    image: docker.io/library/postgres:18-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: pnmtd
      POSTGRES_DB: pnmtd
      POSTGRES_PASSWORD: ${PNMTD_DB_PASSWORD:?PNMTD_DB_PASSWORD is not set: run ./init.sh}
    volumes:
      - pnmtd-pgdata:/var/lib/postgresql     # a new PostgreSQL major: backup.sh, new image, restore.sh
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -q -h 127.0.0.1 -U pnmtd -d pnmtd"]
      interval: 10s
      timeout: 5s
      retries: 5

  pnmtd:
    image: ${PNMTD_IMAGE:-uisach/pnmtd}:${PNMTD_IMAGE_TAG:-v1.0.0}
    restart: unless-stopped
    depends_on:
      db: { condition: service_healthy }
    ports:
      - "${PNMTD_BIND_ADDRESS:-0.0.0.0}:${PNMTD_PORT:-5001}:8080"   # v0.12's port: sensor URLs keep working
    env_file: .env                           # Identity, Proxy, PublicBaseUrl, TZ, Smtp__*, Auth__*, …
    environment:
      Database__Host: db
      Database__Name: pnmtd
      Database__Username: pnmtd
      Database__Password: ${PNMTD_DB_PASSWORD}
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    sysctls:
      net.ipv4.ping_group_range: "0 2147483647"   # PING sensors as non-root (§3.2)
    volumes:
      - pnmtd-keys:/var/lib/pnmtd/keys       # Data Protection keys: sessions and TOTP secrets
    healthcheck:                             # repeated here: Podman's OCI images drop HEALTHCHECK
      test: ["CMD-SHELL", "wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1"]
      interval: 30s
      timeout: 5s
      start_period: 60s
      retries: 3
    stop_grace_period: 40s
    logging:
      driver: json-file
      options: { max-size: "10m", max-file: "3" }

volumes:
  pnmtd-pgdata:
  pnmtd-keys:
```

**How the scripts behave:**

- **Installation:**

  ```
  git clone … /srv/pnmtd && cd /srv/pnmtd/deploy
  ./init.sh --url https://pnmt.example.com
  docker compose up -d
  ./info.sh
  ```

- **`.env` is never sourced:** generated passwords may contain shell syntax, so `lib.sh` reads it line by line.
- **Running commands:** scripts use `docker compose exec pnmtd pnmtd …`, or a one-shot `run --rm` when the app is stopped.
- **Passwords** are never passed as arguments, which would leak them into the shell history, the process list and `docker inspect`.
- **`init.sh`** refuses to overwrite an existing `.env` without `--force`. Even with `--force` it keeps `PNMTD_DB_PASSWORD`, because the database keeps the password it was created with.
- **`info.sh`** reports what the *running* app sees and warns about:
  - an admin without 2FA,
  - `PublicBaseUrl` not set,
  - a reverse proxy without `Proxy`,
  - plain HTTP,
  - no backup yet,
  - a `config.json` left over from v0.12,
  - a database it cannot reach.
- **Cron example** (in the README): `30 3 * * * /srv/pnmtd/deploy/backup.sh --quiet --keep 14`.

**What lives where:**

| Where | Holds | Backed up by |
|---|---|---|
| volume `pnmtd_pnmtd-pgdata` | the database | `backup.sh` (`pg_dump`) |
| volume `pnmtd_pnmtd-keys` | Data Protection keys (sessions, TOTP secrets) | `backup.sh` |
| `deploy/.env` | DB password, initial admin password, settings | you. It is not in any backup |
| `deploy/backups/` | backup folders and `upgrade-1.0-<stamp>.tar.gz` | you |

### 6.5 Upgrade from v0.12

`deploy/UPGRADING-1.0.md` explains it, and `deploy/upgrade-1.0.sh` does it.

**What changes for operators:**

- One URL for the UI (`/pnmt-admin/` on the backend address) instead of port 5000.
- A database container.
- Settings move into `.env`.
- 2FA is offered at the first login.

**What does not change:** the sensor URLs, the data, and the users' passwords.

1. **Before you start:** note the address the sensors use (`<host>:5001` or the backend hostname). It must not change.
2. **Prepare the new deployment:** `git clone … /srv/pnmtd && cd /srv/pnmtd/deploy && ./init.sh`. The settings from the old `docker-compose.yml` keep their names in `.env` (`Smtp__Host`, `Mailbox__…`, `UplinkCheck__…`, `Identity`, `Proxy`, …). The WebApp's `externalApiurl` becomes `PublicBaseUrl`. `upgrade-1.0.sh --copy-settings` reads them from the old container (`docker inspect`) and offers to copy them.
3. **Run the upgrade:** `./upgrade-1.0.sh --legacy /srv/pnmt [--source-time-zone Europe/Zurich]`. The argument is the folder with the old `docker-compose.yml`, `pnmtd-data/` and `pnmtwebapp-data/`. In order, the script:
   1. checks the old version (v0.12.x via the migration history), free disk space, and Docker and compose;
   2. pulls the 1.0 and PostgreSQL images while the old stack still runs;
   3. stops the old containers without removing them, and archives `pnmtd-data` and `pnmtwebapp-data` to `backups/upgrade-1.0-<stamp>.tar.gz`;
   4. starts `db` and runs `pnmtd import-sqlite --from /legacy/pnmtd-data/pnmtd.db --users /legacy/pnmtwebapp-data/users.json`, with both mounted read-only. The import commits only if every check passes (§3.4);
   5. starts 1.0 on the old published port;
   6. prints the import report, the duplicate tokens it replaced (if any) and the users who must change their password.

   If the import stops, the script starts the old containers again. Nothing in the old folders has changed.
4. **Reverse proxy:** keep the backend hostname pointing at port 5001. Redirect the old UI hostname to `https://<backend host>/pnmt-admin/`.
5. **First login:** open `/pnmt-admin/`, log in with your existing password and set up 2FA when prompted.
6. **Rollback:** `docker compose down` here and `docker compose up -d` in the old folder. The old data is untouched until you delete it.
7. **Clean up:** delete the leftovers once 1.0 has run for a while: `token.txt`, `create-web-app-token.txt`, the `Jwt__*` and WebApp settings, and the old folders.

**Downtime** runs from stopping the old stack until 1.0 answers: typically a minute or two plus the import. Sensors that report during that window get connection errors.

So that heartbeat sensors do not all fail at once and trigger a burst of notifications, the heartbeat check waits 5 minutes after startup. It reuses the existing gate that already applies after an uplink recovery (see §10.2).

**Rehearsal:** run `LEGACY_DIR=<copy of the production data> IMPORT=1 ./start.sh` locally, and replay the sensor contract against the imported copy (§8.1).

### 6.6 CI

- **`dotnet.yml`** becomes a workflow that runs `./run-tests.sh`, the same containers as locally:
  - locked restore, build, unit tests and PostgreSQL integration tests, `has-pending-model-changes`;
  - web: `npm ci`, lint, type-check, Vitest and build in the node container;
  - contract drift check: regenerate `openapi.json` and `schema.d.ts` and fail on a diff;
  - **sensor contract replay** against the built image in Production mode;
  - Playwright smoke and layout tests (§5.5) against the built image with PostgreSQL.
- **`push.yml`** builds a multi-arch image (amd64 + arm64) on release and pushes `uisach/pnmtd:v1.0.0` and `latest`.

### 6.7 Retirement

- **WebApp:** archive the `PNMT.WebApp` repo with a README pointer, and add a deprecation note to the `uisach/pnmt.webapp` Docker Hub page.
- **Remove from this repo:**
  - `PNMT.ApiClient`, `PNMTD.Cli`, `TrustController`, `JwtTokenHelper` and the `Jwt` settings.
  - The duplicate `PNMTD/Dockerfile` and the `MudBlazor` entry in `Directory.Packages.props`.
  - `DbTestHelper` from the production code path.
  - The root `docker-compose.yml` (replaced by `deploy/`) and `examples/docker-compose-with-reverse-proxy/` (replaced by `deploy/README.md`).
- **README:** features, a link to `deploy/README.md`, the `/pnmt-admin/` URL, the sensor API reference and a new screenshot. Also fix the image links that broke when the logos moved to `docs/logos/`.

---

## 7. Phases

The effort figures are rough estimates for one developer.

| # | Phase | Scope | Done when | Effort |
|---|---|---|---|---|
| 0 | Sensor contract and .NET 10 | **First, before any code changes:** record the golden sensor contract from the v0.12.1 image (§8.1). Delete `PNMT.ApiClient`, `PNMTD.Cli` and `PNMTD.DevConsole` (§10.2). Then upgrade every remaining project to .NET 10 (`Directory.Build.props`, `global.json`, `.slnx`, lock files, packages from §3.2). Dockerfile on .NET 10, non-root, health port. Verify PING as non-root | The existing tests are green on .NET 10, and the contract replay against the .NET 10 build is green | 2–3 d |
| 1 | PostgreSQL and tooling | Npgsql, `IDbContextFactory`, no lazy loading, UTC with `TimeProvider`, PostgreSQL baseline with indexes, the fixes in §3.4, tasks on `PeriodicTimer`, `pnmtd db migrate`, `import-sqlite` with verification. `start.sh`/`stop.sh`, `tools/npm.sh`, `run-tests.sh`. `deploy/` skeleton: compose, `.env.example`, `lib.sh`, `init.sh`, `info.sh`, `backup.sh`, `restore.sh`, `update.sh` | Importing a real v0.12 database passes every check. The contract replay is green on PostgreSQL. Integration tests run against a throwaway PostgreSQL | 6–9 d |
| 2 | API v1 and removal | Middleware order, SPA hosting and fallbacks, `/` → 404, deny by default, security headers, Data Protection. `Services/Domain`. v1 contracts and controllers for all resources in §3.7 (paging, ProblemDetails, validation, string enums, UTC timestamps). OpenAPI at build time. Remove the old routes, JWT and `/trust` | v1 contract tests and the anonymous-surface test are green. The contract replay is still green | 5–8 d |
| 3 | Users, roles and auth | `users` and `auth_log`. Hasher. `users.json` import and seed. Cookie auth, login, logout and `me`. Account password. User admin with roles and the last-admin guard. Stamp validation, antiforgery, rate limiter, lockout. `pnmtd users …` with `add-user.sh`, `list-users.sh` and `reset-password.sh` | Auth tests are green, including the role matrix and the legacy-hash fixture | 4–6 d |
| 4 | TOTP | Ported `TotpService` with replay protection. Enrollment. Login step 2. `Optional` policy with the prompt. Admin reset, `pnmtd users reset-totp` and `reset-totp.sh` | RFC 6238 vectors and the flow tests are green | 2 d |
| 5 | Frontend foundation | Scaffold inside the node container (TS, Router, Pinia, Vitest, ESLint). PrimeVue with the compact preset in light and dark. Layouts and guards. Generated API types and client. Login, 2FA, setup prompt, account and security screens. Shared components (§5.2) | A user can log in at `/pnmt-admin/login` with TOTP, change their password and log out. Component tests are green | 4–5 d |
| 6 | Dashboard and feature pages | Dashboard first (API and §5.5), then Hosts → Sensors → Events → Notification rules → DNS zones → Mail inputs and log → Key values → Users, auth log and system health → Docs | Every row of §2.2 is checked off. The *must* improvements of §5.6 and the dashboard targets of §5.5 are met on desktop and phone | 10–14 d |
| 7 | Upgrade path and release | `upgrade-1.0.sh`, `UPGRADING-1.0.md`, `deploy/README.md`, the main README, CI with the contract gates, Playwright. A rehearsal with a copy of a production v0.12 installation. Release **v1.0.0**. Retire the WebApp (§6.7) | The rehearsal passes: the import is verified, the replay is green, and sensors keep reporting across the switch | 3–4 d |
| 8 | *Later* | Personal API tokens (once a script needs `/api/v1`). Server-Sent Events for live updates. Acknowledge and mute. The monitoring bug fixes from §2.3 with a contract update. A `manage.sh` console as in plm. i18n | – | – |

### 7.1 Implementation notes (where the code differs from the sections above)

Phases 0 and 1:

- **Lazy loading stays until phase 2.** The old controllers and the tasks rely on it, and the domain services replace both with explicit queries.
  - PostgreSQL has no MARS, so every query that lazy-loads while its reader is open was materialized with `ToList()` first.
  - KeyValue upserts with `ON CONFLICT` also move to phase 2.
- **Background tasks:** they keep their timers. A `RunGuard` (an atomic flag) makes a callback that finds the previous run still going return at once. That is the same guarantee `PeriodicTimer` would give, with a smaller change. `BackgroundTasks:Enabled=false` turns them off (the integration tests).
- **Time:** a static `Clock` (`PNMTD.Lib/Helper/Clock.cs`) serves the code from before 1.0.
  - Instants are UTC, and "local" means `TZ` (default UTC), never the host's zone.
  - A value converter turns every `DateTime` into UTC before PostgreSQL sees it.
  - The integration tests run in Europe/Zurich by default (`PNMTD_TEST_TZ`), and CI also runs them in America/New_York.
- **Command line:** a small hand-rolled parser replaces `System.CommandLine`.
- **Duplicate tokens stay unchanged** (§3.3), and the token indexes are not unique. The import report lists the duplicates.
- **Importer:**
  - `--users` is refused until the users table exists (phase 3).
  - Its test fixture `PNMTD.Tests/Fixtures/v0.12-sample.db` was written by a real v0.12.1 (`tools/contract.sh make-import-fixture`) and has data in every table.
- **Tooling:**
  - `run-tests.sh` runs dotnet with the SDK on the host. Node joins as a containerised group in phase 5.
  - `tools/test-db.sh` provides the throwaway PostgreSQL, and `tools/deploy-test.sh` exercises `deploy/` for real (init, up, backup, restore).
- **Bug fixes along the way:**
  - The tasks' exceptions were never logged (`LogError("…", ex)` passed the exception as a format argument).
  - The DNS log trim deleted the newest entries instead of the oldest.
  - The pending-notification query loaded every event without a notification row into memory every 10 s.
  - Concurrent reports for one sensor could race the dedupe rule. A transaction-scoped `pg_advisory_xact_lock` per sensor now serialises them, and a test proves the race without it.

Phase 2:

- **Routing (§3.5):**
  - There is no catch-all `MapFallback`. It would have turned every v0.12 405 into a 404, because routing creates its 405 endpoint only when no endpoint for the path accepts the method. `UsePlainNotFound` answers requests that match no endpoint with an empty 404, and runs routing's 405 endpoint before authentication.
  - Security headers apply to `/pnmt-admin` and `/api` only. The golden contract pins `GET /` with v0.12's headers, and the empty 404s elsewhere answer the same way.
  - Authentication does not run for `/event` and `/read`, so a browser's session cookie can never earn a sensor response a `Set-Cookie` renewal.
- **Isolation from the sensor API:**
  - v1 has its own JSON input and output formatters, which take requests under `/api` only. MVC's JSON options stay at v0.12's defaults.
  - The `code` extension is added to ProblemDetails for `/api` only. The sensor API's 404, 400 and 425 come from the same ProblemDetails factory and stay unchanged.
- **Sign-in is phase 3.** Until then only the tests sign in (`TestSignIn`, one header per role), so the new API cannot be used from a browser yet (there is no interim release, §10.2). Antiforgery and the login rate limiter come with the login endpoints. The cookie scheme, the policies, deny by default and the Data Protection key ring are in place.
- **Contracts:**
  - Where create and update take the same fields, one request type serves both (`HostRequest`, `SensorRequest`, …).
  - ENCAPSULADED relations are named `parentSensorId` and `children` in v1 (`OlderSibling` in the database).
  - `writeUrl` is `{base}/event/{token}`, to which a client appends `/{code}/{message}`. `readUrl` is `{base}/read/{token}/json`.
  - `/system/sensor-types` and `/system/notification-rule-types` join `/system/status-codes` for the docs page.
- **State rules:**
  - Precedence is Disabled (the sensor, its host or its ENCAPSULADED parent), then Ignored, then NoData, Failing or Ok.
  - An ENCAPSULADED parent takes the worst state of its children, and only the children are counted.
  - Timespan sensors count only their evaluations (280/281/480/481), for uptime too.
- **Uptime:**
  - Fractions from 0 to 1. Overall values have 4 decimals, the values of a bar 3.
  - A host's day is the average of its sensors that have data that day (issue 15).
  - 30 days means the last 30 local days, weighted by time. The 24-hour strip uses UTC hours, which differ from local hours only in zones with a :30 or :45 offset.
- **Dashboard performance:** a plain 10-second cache would still make one request wait about a second at the §5.5 scale. `DashboardCache` therefore answers from the last build and replaces a build older than 10 seconds in the background (stale while revalidating).
  - Measured with 50 hosts, 300 sensors and 600,000 events: a build takes 1.1–1.4 s, a polled request 0–7 ms (p95), and the response is 43–47 KB.
  - `PNMTD_PERF=1` runs that measurement as a test.
  - The hosts list (30 days, not cached) takes about 0.5 s at that scale.
- **Domain services:** the services serve the v1 controllers. The sensor API keeps its controller (`SensorApi/`), and the background tasks keep their code, with explicit `Include`s instead of lazy loading. Moving those onto the services changes no behaviour and is left for when they change anyway.
- **Data changes:**
  - **KeyValue upserts** use `INSERT … ON CONFLICT`. Two call sites had relied on the helpers' hidden `SaveChanges()`: the mail-log trim and part of the ping task's events. They save explicitly now.
  - **NotificationTask** writes its `LAST_…` timestamp on every run, so `/system/tasks` can tell a stalled task from a quiet one.
  - **Deleting a DNS zone** also deletes the zone's DNS sensor, and no longer sleeps 2 s.
  - **Deleting a host** keeps its DNS zones, without a host, and its mail input rules, without a target. The ENCAPSULADED children of the host's sensors are deleted with them.
- **Viewer:**
  - No `integration` (tokens, URLs) on sensors.
  - Pushover and webhook recipients are masked to scheme and host.
  - No mail log.
  - `POST /sensors/lookup` needs User, because it is about tokens.
- **Key values:**
  - Entries created through the API are never read-only.
  - A task's read-only entry answers `PUT` with 409 `read_only`, and can be deleted (the task writes it again).
- **OpenAPI:**
  - Debug builds write the document to `PNMTD.WebUI/src/api/openapi.json` (`Microsoft.Extensions.ApiDescription.Server`), and CI checks that the committed copy is current.
  - At runtime nothing serves a document or a Swagger UI, not even in Development: the committed file is the reference.
- **Tests:**
  - 139 tests in four time zones, plus the opt-in performance test.
  - The endpoint audit compares the anonymous endpoints with §3.6.
  - The role matrix runs all 55 v1 endpoints as anonymous, Viewer, User and Admin. A new endpoint fails it until the matrix lists it.

Phase 3:

- **Schema:** `users` and `auth_log` arrive as a second migration (`AddUsersAndAuthLog`) rather than in the baseline, so existing development and test databases keep working. The TOTP columns are part of it, so phase 4 adds no migration. `LockoutCount` is new: it lets each lockout double the next.
- **Sessions:**
  - The cookie's stamp, role and active state are compared with the database, cached for a minute. This server forgets a user's entry the moment it changes the user, so its own changes end sessions at once. `pnmtd users …` runs as another process, so its changes take up to the minute.
  - Changing your own password, and "sign out all sessions", end every *other* session; the current one continues with a renewed cookie.
  - Logout ends the session on that device only.
- **Must change the password:** while the session carries `pnmt:must_change_password`, a middleware answers every `/api/v1` call except `/auth/*`, `/account/password` and `/system/info` with 403 `must_change_password`. The seeded admin must change the password but has no expiry, because `init.sh` may run long before the first sign-in.
- **Antiforgery:**
  - The request token goes in the readable `XSRF-TOKEN` cookie; the HttpOnly cookie token is `pnmt.antiforgery`.
  - A resource filter on all v1 controllers requires the token for unsafe calls of **cookie** sessions only, except the login. A signed-out request still gets its 401 first.
  - Unlike the plan's §4.7 wording, the logout is protected too.
- **Login:**
  - Unknown users are verified against a dummy hash.
  - Failure reasons (unknown user, wrong password, inactive, expired temporary password, locked out) appear only in the audit log.
  - Only wrong passwords count towards a lockout.
  - While locked, even the right password gets 429 `locked_out` with `Retry-After`.
  - The rate limit is read per client (`Auth:LoginRateLimit:PermitLimit`), so it can be configured. The tests raise it.
  - Usernames are case-insensitive: `NormalizedUsername` is unique.
- **Temporary passwords:** generated passwords are 20 characters without look-alikes, valid for 7 days. `--password-stdin` sets a permanent password of the operator's choice instead.
- **Import:**
  - `import-sqlite --users` writes the users in the same transaction and reads the hashes back before committing.
  - It refuses a users table that already has entries (a server that started first seeded its admin), unless `--reset`.
  - `start.sh` takes `LEGACY_USERS=…/users.json`, and its `dev.env` gets a generated first-admin password, like `init.sh`'s `.env`.
- **Tests:**
  - The test sign-in now signs in as one seeded user per role, so account endpoints work in the role matrix.
  - The cookie tests use a small browser helper that keeps cookies and echoes the XSRF token, as the SPA will.
  - `tools/deploy-test.sh` signs in as `init.sh`'s admin, changes the password with the XSRF token via curl, and runs `add-user.sh`, `reset-password.sh` and `list-users.sh` against the running stack.

**Total: about 36–51 person-days.**

- Phase 5 can start once the auth endpoints of phase 3 exist.
- Phase 6 pages can start one at a time, as soon as each v1 resource exists.

---

## 8. Testing

### 8.1 Sensor API contract (release gate)

**Harness.** `PNMTD.Contract.Tests` uses MSTest and `HttpClient`, as in plm's contract harness. `tools/contract.sh` drives it:

```
tools/contract.sh verify [IMAGE]   replay against IMAGE (default: pnmtd:dev, built from the working tree)
tools/contract.sh record           re-record the golden file from the v0.12.1 image (only if a case is added)
tools/contract.sh make-fixture     re-create the fixture database (73 sensors, deterministic tokens)
```

The fixture is a v0.12.1 SQLite database, so later builds load the same data through `pnmtd import-sqlite`. The state probe reads v0.12.1's management API for the baseline, and the database for 1.0 builds (`DatabaseProbe`, over a loopback port that `tools/contract.sh` publishes).

- It **records** request/response pairs against the v0.12.1 image, built from the tag, running in Production mode with a fixed fixture database. The golden files are committed.
- It **verifies** them against any base URL.

**What is compared:**

- status, `Content-Type` and `Allow`;
- the absence of any header v0.12.1 did not send;
- the body bytes.

Normalised before comparing: `traceId` and generated GUIDs.

**Cases:**

- **GET `/event`:** with and without a message, with a trailing slash, percent-encoding, `+`, Unicode, a long message, `$`-Base64 (valid, invalid, URL-safe, containing `/` or `%2F`).
- **Code parameter:** not numeric, overflow.
- **Token lookup:** unknown token, disabled sensor, ignored sensor, duplicate token.
- **Rate limit:** the 425 response.
- **POST `/event`:**
  - plain text, an empty body, a form `Content-Type`, a JSON `Content-Type`, a BOM, `$`-Base64;
  - ENCAPSULADED: valid, invalid, empty, `null`, `[]`, lower-case names, more than 64 siblings.
- **Value checks:** VALUECHECK match, no match, invalid regex.
- **`/read`:** options `property`, `raw`, `json`, `JSON`, none, unknown; `Accept: application/json`; timespan sensors.
- **Routing:** HEAD, OPTIONS and wrong methods (405); upper-case paths; wrong segment counts.

**State checks after each case:**

- the stored events (code, message, `Source` format),
- dedupe,
- the value-check code rewrite,
- sibling creation,
- the 2000-event cap.

**When it runs:** against the .NET 10 build (phase 0), the PostgreSQL build (phase 1), every later build in CI, and an imported copy of a real v0.12 database (phase 7).

### 8.2 Hosting, routing and protection

- `/pnmt-admin` returns 301 to `/pnmt-admin/`, and `/pnmt-admin/hosts/<id>` returns `index.html` with `no-cache`.
- Hashed assets are served with `immutable`, and a missing asset returns 404 (not `index.html`).
- `/`, `/foo`, `/host`, `/hosts`, `/info`, `/swagger` and `/trust/create-web-app-token` return 404 with an empty body.
- `/api/v1/foo` returns 401 when signed out and a 404 ProblemDetails when signed in.
- **Anonymous surface test:** the anonymous endpoints equal the §3.6 table exactly. **Role matrix test:** every v1 endpoint × anonymous / Viewer / User / Admin.
- The built `index.html` references only `/pnmt-admin/` assets.

### 8.3 API v1 contract

- Paging envelope, 201 with `Location`, 204 on delete.
- ProblemDetails `code` values, enums as strings, UTC timestamps.
- `PUT` ignores non-writable fields: `Created`, tokens and `Status` stay unchanged.
- Validation rules, and 409s for duplicate keys and the last admin.
- Viewer responses contain no token or URL fields.
- Dashboard: counts, `failingSince`, host day values that skip sensors without data, the payload size limit.

### 8.4 PostgreSQL, import and background tasks

- **Migrations:** they apply to an empty database, `has-pending-model-changes` is clean, and the app refuses a database migrated by a newer version.
- **Importer:** fixtures from v0.12.1 (empty, with data, with duplicate tokens, with an old single-user `users.json`). Checks pass and fail as expected, nothing is committed on failure, time zones are converted, and `--reset` reruns cleanly.
- **Concurrency:** parallel submissions to one sensor give the same dedupe and rate-limit outcome as serial ones. KeyValue upserts race without errors.
- **Background tasks:** runs never overlap (`FakeTimeProvider`), and the heartbeat startup gate works.
- **Time zones:** uptime per day and the interval descriptors are tested with `TZ` east and west of UTC, as plm does. Several of these checks would pass vacuously in UTC.

### 8.5 Auth and TOTP

- Login success and failure, inactive user, lockout and unlock, rate limit 429.
- TOTP step: valid, invalid, **replayed** and an expired 2FA cookie.
- Enrollment confirm and disable (requires password + code). `TotpPolicy` enforcement and the `Optional` prompt flag.
- Admin reset and `pnmtd users reset-totp`.
- Stamp rotation ends other sessions. The last-admin guard covers delete, demote and deactivate. A temporary password expires.
- Antiforgery is required for cookie POSTs.
- `users.json` import: the current format, the oldest single-user format, and a legacy hash created with the old `WebAppUser.SetPassword` that verifies and is rehashed on login.
- **TOTP unit tests:** RFC 6238 Appendix B vectors (SHA1, secret `12345678901234567890`) and Base32 round-trips.
- **Deploy scripts:** `add-user.sh`, `reset-password.sh` and `reset-totp.sh` against a running stack (the `deploy` group of `run-tests.sh`).

### 8.6 Frontend

- **Vitest:**
  - `UptimeBar`: thresholds, today included, and the day count adapting to the width.
  - `StatusBadge` states and `CodeTag`.
  - `RelativeTime`.
  - `InlineField`: save, cancel and rollback.
  - The API client: 401 handling, the XSRF header, ProblemDetails mapping.
- **Playwright** at `/pnmt-admin/`, with the `SEED=1` fixture:
  - Log in with TOTP (code generated in the test).
  - Create a host and a sensor, submit a failing event through the shown `/event` URL, and check that the dashboard shows the failure with its message and "since".
  - A Viewer sees no tokens and no edit actions.
  - Reload a deep link, then log out.
  - The density targets of §5.5 at three viewports, in light and dark.

**Before release:** run through the §2.2 checklist by hand, rehearse the upgrade with a copy of a production installation, and check the dashboard on a phone.

---

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| A change breaks reporting for hundreds of sensor applications | Golden contract recorded from v0.12.1, replayed on every build and against an imported production copy. Sensor endpoints are isolated from v1 settings and from the global middleware. No HTTPS redirect. Same port and paths |
| Data loss or corruption in the move from SQLite to PostgreSQL | The importer works on a copy and verifies counts, ids and foreign keys before it commits. It commits nothing on failure. The old data is archived and left untouched, rollback means starting the old stack, and the upgrade is rehearsed with `IMPORT=1` |
| Timestamps shift by the time-zone offset after the move to UTC | An explicit `--source-time-zone`, a `TZ` setting for local-day rules, and tests in several time zones |
| PostgreSQL exposes races that serialised SQLite writes hid | Per-sensor advisory locks, `ON CONFLICT` upserts, non-overlapping tasks, concurrency tests |
| Heartbeat failures and a notification burst right after the switch | Short downtime, and a 5-minute heartbeat gate after startup |
| A forgotten proxy rule exposes the API | Deny by default, the anonymous-surface and role-matrix tests. Proxy rules are only defence in depth |
| A proxy rule matches only lower-case `/event/`, while the app also accepts `/EVENT/` | The example rule is case-insensitive, and the contract includes upper-case paths |
| PING sensors stop working in the non-root container | Verified in phase 0, with the sysctl or `NET_RAW` documented in the compose file |
| The base path drifts between the Vite config and the server | A single constant on each side, plus a build test on the generated `index.html` |
| A reverse proxy strips or adds a path prefix, so assets and API calls return 404 | Document that paths must be forwarded unchanged |
| Data Protection keys are lost: sessions become invalid and TOTP secrets unreadable | The keys volume is part of `backup.sh`. Affected users are reset with `reset-totp.sh` and set up 2FA again |
| The only admin loses their password or authenticator | `deploy/reset-password.sh` and `deploy/reset-totp.sh` |
| A misconfigured reverse proxy makes rate limiting and lockout use the proxy's IP | Apply `UseForwardedHeaders` first. Document the `Proxy` setting. `info.sh` warns, and the audit log records the client IP |
| node tooling creeps onto the host | `tools/npm.sh`, `WEB=dev ./start.sh`, and image builds that run npm. Nothing in the repo expects node on the host |
| The redesign delays cutover | Parity first. Improvements are tagged *must*, *should* and *could*, and only the *must* ones block release |
| An attacker can lock a known username out | This is acceptable for small installs: lockouts are short and admins can unlock |

---

## 10. Decisions and open questions

### 10.1 Decisions from the review of revision 2

| Topic | Decision |
|---|---|
| API location | `/api/v1` at the root, as recommended |
| Old management routes | Removed in 1.0.0. Only `/event` and `/read` stay, unchanged (§3.3) |
| Root `/` | Plain 404. There is no redirect setting |
| Default `TotpPolicy` | `Optional` with a setup prompt |
| Recovery codes | None. `deploy/reset-password.sh` and `deploy/reset-totp.sh` instead |
| Legacy JWT | There are no other clients, so JWT and `/trust` are removed. Personal API tokens wait until something needs them (phase 8) |
| Version | **v1.0.0** |
| Read-only role | Yes: **Viewer** (§4.10) |
| UI library | PrimeVue 4 (§5.1) |
| Platform | .NET 10 for every project |
| Database | PostgreSQL 18 in a container, in production and in debugging |
| Deployment | `deploy/` folder plus `start.sh`/`stop.sh`, modelled on plm |
| Tooling | node and npm only in Podman containers |
| UI | Same construct, compact and responsive, dashboard redesigned (§5.3–5.6) |
| TLS | Stays at the reverse proxy. The app serves HTTP only, as today |

### 10.2 Decisions from the review of revision 3

| Topic | Decision |
|---|---|
| Interim release on .NET 10 | **No.** Phases 0–7 ship together as v1.0.0. Since nothing is released in between, `PNMT.ApiClient`, `PNMTD.Cli` and `PNMTD.DevConsole` are deleted in phase 0 instead of being upgraded first |
| Default published address | Stays `0.0.0.0:5001`, compatible with every existing sensor URL. It can be overridden in `.env` |
| `PNMTD.DevConsole` | Deleted |
| Heartbeat startup gate | Yes: heartbeat checks wait 5 minutes after startup (§6.5) |

---

## Appendix: configuration changes

| Setting | Status | Notes |
|---|---|---|
| `PNMTD_IMAGE_TAG`, `PNMTD_PORT` (5001), `PNMTD_BIND_ADDRESS` (0.0.0.0) | new | Read by `deploy/docker-compose.yml` |
| `PNMTD_DB_PASSWORD` | new | Generated by `init.sh`. Used by both the database and the app |
| `Database__Host` / `__Port` / `__Name` / `__Username` / `__Password` | new | Set by the compose file, and by `start.sh` |
| `TZ` | new meaning | IANA time zone for local-day rules (uptime days, interval descriptors). Default UTC |
| `PublicBaseUrl` | new | Base for the `writeUrl`/`readUrl` values returned by the API. Replaces the WebApp's `externalApiurl` |
| `Auth__TotpPolicy` | new | `Optional` (default) · `RequiredForAdmins` · `Required` |
| `Auth__TotpIssuer` | new | Default `PNMT (<Identity>)` |
| `Auth__SessionDays` | new | Default 7 |
| `Auth__InitialAdmin__Username` / `__Password` | new | Only used when the users table is empty. `init.sh` generates the password |
| `Auth__Lockout__MaxFailedAttempts` / `__Minutes` | new | Defaults 5 / 5 |
| `Identity`, `Proxy`, `Smtp__*`, `Pushover__*`, `Mailbox__*`, `UplinkCheck__*`, `dns__server1/2`, `Development__*` | unchanged | They move from the old compose `environment:` into `.env` under the same names |
| `Jwt__Issuer` / `Jwt__Audience` / `Jwt__Key` | removed | No JWT scheme in 1.0 |
| `WebUi__RedirectRoot`, `Auth__LegacyUsersFile` | dropped | Proposed in revision 2 only. `/` is always 404, and `users.json` is passed to `import-sqlite --users` |
| `apiurl`, `externalApiurl`, `externalDomain`, `JwtToken` | removed | WebApp settings |
| `/var/lib/pnmtd/config.json`, `/var/lib/pnmtd/logs/` | removed | Settings live in `.env`, and logs go to `docker compose logs`. `info.sh` warns if a `config.json` is still mounted |
