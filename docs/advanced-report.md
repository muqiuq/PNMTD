# Reporting JSON to an Advanced sensor

An *Advanced* sensor in PNMT takes a JSON object from your app, as often as it needs to send one. PNMT shows the report on the sensor's *Data* tab, keeps a history, charts the values someone picked, and judges every report by the sensor's **rules**. The sensor is OK while every rule holds, warns or fails when one doesn't, fails when the report can't be read, and fails when no report arrived within its interval.

This page is for the people who write the app. Setting up the sensor and its rules happens in PNMT (*Add sensor → Advanced (JSON)*). The design is in [advanced-sensor-plan.md](advanced-sensor-plan.md).

## The request

```http
POST /report
Authorization: Bearer <write token>
Content-Type: application/json

{ "status": "ok", "message": "Nightly backup done", "disk": { "free_gb": 412.5 }, "jobs": [ … ] }
```

- **The URL** is PNMT's address with `/report`, for example `https://pnmt.example.com/report`. The sensor's page shows it, with the token.
- **The token** is the sensor's write token, the same as for `/event`. Send it in the `Authorization: Bearer` header: it stays out of proxies' access logs. Tools that can't set a header can POST to `/report/<write token>` instead.
- **The body** is one JSON object, read as UTF-8 whatever the `Content-Type` says (so `curl --data` works). A UTF-8 BOM is skipped. `Content-Encoding: gzip` is accepted.
- **No sign-in, no cookie, no CORS.** A browser page can't post a report: a token doesn't belong in a browser.

### The body

Everything in the object is yours: any keys, any nesting, lists of objects. PNMT keeps your order of keys. It reads three keys when they are there, all optional, at the top level:

| Key | Type | What PNMT does with it |
|---|---|---|
| `status` | string | Your app's own verdict, shown as a badge. New sensors get a rule that it is a healthy word (below). |
| `message` | string | A line for the report's headline and the history (up to 1000 characters). |
| `time` | ISO 8601 with an offset, or Unix seconds | When your app measured. PNMT shows it next to the time it received the report, and says when your clock is more than 5 minutes off. Rules on ages compare with PNMT's clock. |

Otherwise these keys are data like any other.

**Limits.** A report that breaks one is stored as *invalid* and fails the sensor (496):

- at most 256 KiB, after decompression (an installation can change it);
- at most 32 levels deep;
- no key twice in one object;
- no comments and no trailing commas.

### The answer

The answer is small JSON. **PNMT decides the sensor's code a moment later**, so the answer says the report was received, with a *preview* of the verdict that the same rules give this report now.

| Status | Body | When |
|---|---|---|
| 202 | `{ "ok": true, "reportId": "…", "preview": { "code": 495, "state": "failing", "message": "1 rule fails: Disk space (disk.free_gb = 3.2, must be at least 5)", "failed": ["Disk space"], "warnings": [] } }` | Stored. |
| 200 | `{ "ok": true, "dryRun": true, "preview": { … } }` | With `?dryRun=true`: nothing stored, nothing judged. For trying your app without a sensor of its own. |
| 422 | `{ "ok": false, "reportId": "…", "error": "invalid_json", "message": "The report isn't valid JSON: line 3, position 14: …" }` | Stored as invalid; the sensor fails (496). `error` is `invalid_json`, `not_an_object`, `too_deep` or `duplicate_key`. |
| 413 | `{ "ok": false, "reportId": "…", "error": "too_large", "message": "The report is 1.2 MiB, more than 256 KiB" }` | Recorded without its body; the sensor fails (496). |
| 429 | `{ "ok": false, "error": "too_soon", "message": "…" }` and `Retry-After` | More than one report in 5 seconds for the same sensor. Nothing stored. |
| 409 | `{ "ok": false, "error": "wrong_type", "message": "…" }` | The token is a sensor's, but not an Advanced one. |
| 401 | `{ "ok": false, "error": "no_token", "message": "…" }` | No token in the header. |
| 404 | empty | An unknown token, or a disabled sensor. |

The preview can differ from the code PNMT writes when a report arrived just before yours, or the rules changed in between. The code is what counts.

## Examples

```sh
# a file, the token in the header
curl -H "Authorization: Bearer $TOKEN" --data @status.json https://pnmt.example.com/report

# inline, the token in the path
curl --data '{"status":"ok","queue":{"length":12}}' https://pnmt.example.com/report/$TOKEN

# forward an app's own health endpoint from cron
curl -s http://localhost:8080/health | curl -H "Authorization: Bearer $TOKEN" --data-binary @- https://pnmt.example.com/report

# try it without storing anything
curl -H "Authorization: Bearer $TOKEN" --data @status.json "https://pnmt.example.com/report?dryRun=true"

# compressed
gzip -c status.json | curl -H "Authorization: Bearer $TOKEN" -H "Content-Encoding: gzip" --data-binary @- https://pnmt.example.com/report
```

```powershell
$status = @{ status = 'ok'; disk = @{ free_gb = 412.5 }; jobs = @(@{ name = 'db'; state = 'done' }) }
Invoke-RestMethod -Method Post -Uri https://pnmt.example.com/report -Headers @{ Authorization = "Bearer $token" } `
  -ContentType 'application/json' -Body ($status | ConvertTo-Json -Depth 10)
```

`ConvertTo-Json` cuts at depth 2 unless told otherwise.

```python
import requests

status = {"status": "ok", "disk": {"free_gb": 412.5}, "jobs": [{"name": "db", "state": "done"}]}
answer = requests.post("https://pnmt.example.com/report", json=status,
                       headers={"Authorization": f"Bearer {token}"}, timeout=10)
answer.raise_for_status()
print(answer.json()["preview"]["message"])
```

```js
// Node 18 or later
const answer = await fetch('https://pnmt.example.com/report', {
  method: 'POST',
  headers: { Authorization: `Bearer ${process.env.PNMT_TOKEN}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ status: 'ok', queue: { length: 12 } }),
})
if (!answer.ok && answer.status !== 422) throw new Error(`PNMT answered ${answer.status}`)
```

```csharp
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);
var answer = await http.PostAsJsonAsync("https://pnmt.example.com/report",
    new { status = "ok", disk = new { free_gb = 412.5 } });
answer.EnsureSuccessStatusCode();
```

## Health formats

Common health endpoints work as they are; forward their JSON:

| Format | Status words | Parts |
|---|---|---|
| The IETF draft *Health Check Response Format for HTTP APIs* (`application/health+json`) | `pass`, `warn`, `fail` | `checks`, with `observedValue`, `observedUnit`, `status` and `time` |
| Spring Boot Actuator's `/actuator/health` | `UP`, `DOWN` | `components` |
| ASP.NET Core's HealthChecks UI writer | `Healthy`, `Degraded`, `Unhealthy` | `entries` |

The starter rule reads all three formats' status words, and their parts show as tables on the *Data* tab.

## What the rules can do

The people who run PNMT write the rules; your app only sends the data. It helps to know what they can check:

- **A path** names values: `disk.free_gb`, `["free space"]`, `jobs[0]`, `jobs[-1]`, `jobs[*].state` (every element), `checks.*` (every member). A rule over several values says whether **all**, **any** or **none** must hold.
- **Comparisons:** equal, not equal, less than, at most, more than, at least, between, outside. Numbers in strings (`"3.2"`) count as numbers.
- **Text:** contains, starts with, ends with, matches a regular expression, one of a list.
- **Kinds:** true, false, null, empty, exists, absent.
- **State:** healthy: `ok`, `pass`, `passed`, `up`, `healthy`, `success`, `green` or `true` hold; `warn`, `warning`, `degraded` and `yellow` warn; anything else fails.
- **Ages:** a time (ISO 8601 with an offset, or Unix seconds) older or newer than a duration, such as *the backup finished within 26 h*. Ages are judged again every 30 seconds, so a rule fails on time even when no report comes.
- **Counts:** a list's length, an object's number of members, or the number of matches.
- **Changes** since your previous report: changed, unchanged, increased, decreased, didn't decrease. Send the same report twice, and *increased* fails.

A rule can **warn** instead of fail (296, the sensor stays OK), and says what happens when a value is **missing** (fail, warn or pass). An event, and so a notification, comes only when the verdict changes: a value moving within the same failure doesn't notify again.

**What makes rules easy:** stable keys and units (`free_gb`, not `free`), numbers as numbers, times with an offset, and a list of objects with the same keys for things of the same kind.
