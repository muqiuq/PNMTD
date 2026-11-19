# PNMT 3rd Party Connectors: the API for callers

A connector lets your system do one thing on a server through PNMT, such as flush a cache, start a backup job or restart a pool.
An administrator sets the connector up in PNMT and gives you three things:

- **the connector's URL:** `https://pnmt.example.com/connector/v1/<slug>`;
- **a key:** `pnmt_cx_…`, the key of your *caller*, the entry PNMT keeps for your system;
- **the networks** your calls may come from. A call from anywhere else is refused, even with the right key.

One key opens every connector granted to your caller, and no other.

## A call

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

| Field | What |
|---|---|
| `arguments` | One property per argument the connector exposes. `GET /connector/v1/<slug>` lists them (below) |
| `reference` | Optional, at most 200 characters: your own id for the call, such as a job or a ticket. PNMT shows it and searches by it |
| `note` | Optional, at most 1000 characters: why. PNMT shows it, and a connector with a review reads it |
| `dryRun` | Optional: check the call, the review included, and run nothing |

- **The key goes in the `Authorization` header only.** A key in the URL counts as no key.
- **The body** is one JSON object of at most 64 KiB, read as UTF-8, with no key twice and no other properties.
- **Plain HTTP works,** but then the key and the arguments travel readable. Use HTTPS where the way isn't trusted.

## Arguments

- **Only the arguments the connector exposes.** An unknown one refuses the call, and so does one PNMT sets itself. A typo shows at once instead of the script's default running.
- **The JSON type must match:** a string for text, a number for a number, `true` or `false` for a switch. `"7"` for a number, or `"true"` for a switch, is refused.
- **No arrays or objects,** at most 4000 characters per value, and no control characters. Line breaks and tabs are allowed only in an argument marked `multiline`.
- **A pattern matches the whole value.** `[a-z]+` refuses `abc; rm -rf /`.
- **Every problem is listed, and one is enough to refuse the call:**

```json
{ "ok": false, "error": "arguments_invalid", "message": "2 arguments are not allowed.",
  "fields": { "Site": "doesn't match the pattern shop-[a-z0-9]{2,20}", "Force": "not an argument of this connector" } }
```

PNMT checks the arguments before anything runs, and the server's agent checks them again.

## The answer

A call PNMT accepted gets an answer of this shape:

```json
{
  "ok": true,
  "ticket": "6f1c2a7e-3f5d-4c1e-9a8b-0d7c4e2b1a90",
  "state": "done",
  "refusedBy": null,
  "waitingFor": null,
  "reviewed": true,
  "exitCode": 0,
  "output": "Flushed 1284 entries of shop-de (Pages).\n",
  "outputTruncated": false,
  "message": null,
  "receivedAt": "2026-11-18T08:15:02.114Z",
  "startedAt": "2026-11-18T08:15:03.020Z",
  "finishedAt": "2026-11-18T08:15:05.871Z",
  "durationMs": 2851,
  "poll": "/connector/v1/shop-flush-cache/tickets/6f1c2a7e-3f5d-4c1e-9a8b-0d7c4e2b1a90"
}
```

- **Read `state`, not the HTTP status.** The status is 200 when the call is final and 202 while it isn't. A script that failed, or a call the review refused, is a 200 with that state.
- **`output`** is there only when the connector returns output. The exit code and the times are always there.
- **`reviewed`** is `true` when the review passed the call, and `false` when it ran without one because the review couldn't run. It is absent when the connector has no review.

| `state` | Means | Final |
|---|---|---|
| `queued` | waiting; `waitingFor` says for what: `worker`, `review`, `turn` (the script runs on the server already), `capacity` (PNMT runs its most calls already), `server_offline`, `parameters_pending` | no |
| `running` | the run was sent to the server | no |
| `done` | the script ended; see `exitCode` | yes |
| `failed` | the script failed, or the server didn't take it | yes |
| `timedOut` | the script ran longer than its timeout and was stopped | yes |
| `refused` | a check refused the call; `refusedBy` says which (`review`, `review_unavailable`, `server_not_found`, `agent`, …) and `message` why | yes |
| `offline` | the server's agent went away just as the run was sent | yes |
| `lost` | no result came from the server within the timeout | yes |
| `expired` | the call waited longer than the connector allows | yes |
| `cancelled` | an administrator cancelled the call, or disabled the connector, while it waited | yes |
| `checked` | a dry run's end; `refusedBy` says what would have refused it | yes |

## Wait or ticket

The connector decides how you get the outcome.

- **Wait mode:** the answer comes when the call is final, after at most the connector's wait (`waitSeconds` in its description, at most 110 s). A call that takes longer is answered with 202, its ticket and its state. You never lose a call: fetch the outcome later.
- **Ticket mode:** the answer comes at once, as 202 with the ticket and a `Location` header.

```sh
curl -sS https://pnmt.example.com/connector/v1/backup-start-job/tickets/$ticket -H "Authorization: Bearer $PNMT_KEY"
```

- **Ask at most once a second per ticket,** or you get 429.
- **A ticket is valid only with your caller's key.** After a key rotation, the new key fetches tickets from before.

## Retries and dry runs

- **`Idempotency-Key`** (1 to 100 printable characters): the same call with the same key within 24 hours answers the first call's ticket and runs nothing. The same key with other arguments is refused with `idempotency_mismatch`. Use one key per intended run, and retry with the same one after a timeout or a lost connection.
- **`"dryRun": true`** goes through every check, the review included, and runs nothing. It counts against the calls per hour.

## The connector's description

`GET /connector/v1` lists the connectors your key may call. `GET /connector/v1/<slug>` describes one:

```json
{
  "name": "Shop: flush cache", "slug": "shop-flush-cache", "purpose": "The shop's back office flushes …", "mode": "wait",
  "waitSeconds": 60, "returnsOutput": true, "review": true, "callsPerHour": 30,
  "url": "https://pnmt.example.com/connector/v1/shop-flush-cache",
  "arguments": [
    { "name": "Site", "type": "string", "required": true, "pattern": "shop-[a-z0-9]{2,20}", "help": "The site, as named in IIS." },
    { "name": "Scope", "type": "string", "required": false, "default": "Pages", "oneOf": ["Pages", "Images", "All"] }
  ]
}
```

## Errors

A call refused before PNMT accepted it gets the error body with its HTTP status:

| Status | `error` | Means |
|---|---|---|
| 401 | `unauthorized` | No key, or not a valid one. Every connector answers this, existing or not |
| 403 | `address_refused` | The key is valid, but your call came from outside your caller's networks. PNMT reports this as a possibly leaked key |
| 403 | `caller_disabled`, `connector_disabled` | Disabled in PNMT |
| 404 | `unknown_connector` | Your caller has no connector of that name |
| 404 | `unknown_ticket` | No such ticket for your key and this connector, or past the retention |
| 400 | `invalid_body` | The body isn't one JSON object with the four properties above |
| 409 | `arguments_need_review` | The connector's script changed, and an administrator has to look at its arguments |
| 413 | `too_large` | The body is larger than 64 KiB |
| 422 | `arguments_invalid` | See *Arguments*; `fields` names each problem |
| 422 | `idempotency_mismatch` | The `Idempotency-Key` was used with other arguments |
| 429 | `rate_limited`, `queue_full`, `blocked` | Too many calls an hour, too many waiting, or too many wrong keys from your address. `Retry-After` says when to try again |

## Examples

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

```python
import os, requests
r = requests.post("https://pnmt.example.com/connector/v1/shop-flush-cache",
                  headers={"Authorization": f"Bearer {os.environ['PNMT_KEY']}"},
                  json={"arguments": {"Site": "shop-de", "Scope": "Pages"}}, timeout=120)
answer = r.json()
print(answer["state"], answer.get("exitCode"))
```

## Good to know

- **Keep the key like a password.** It opens every connector granted to your caller, from your networks. If it leaks, ask for a rotation: the old key stops at once.
- **One run per script and server at a time.** A call for a script that is running on its server waits its turn (`waitingFor: turn`), and the oldest call goes first.
- **The review**, when a connector has one, is a classifier that answers fixed questions about your call. It reads your arguments, reference and note, but never a secret argument's value. You hear which check refused a call, but not how close it was.
- **Everything is on record in PNMT:** each call with its arguments (never a secret), its review, its run and its output.
