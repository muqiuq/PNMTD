# Plan: join script options, and acknowledged differences

| | |
|---|---|
| Status | **Built on 1.21.0: J1 to J5** (§9, as built, not released yet), with the recommended answers to the four questions of §8. Revision 2 (2026-11-23): checked against the code at 1.20.1.<ul><li>The acknowledgement follows the one for OS updates (docs/linux-os-updates-plan.md §6.5), except for the expiry: a reason, one audit entry per difference, removal per difference, and acknowledged differences greyed out in the list (§3).</li><li>Network scans and objects left behind (§2.3).</li></ul>Revision 1: 2026-11-19 |
| Date | 2026-11-23 |
| Scope | The MikroTik join script (docs/mikrotik-plan.md §4.3) and "script outdated" (§3.3): the Setup tab, the status strip, the Routers tab and the overview. |
| Goals | <ol><li>Per router, a join script **without firewall rules**.</li><li>Per router, a join script **without remove commands**.</li><li>**Acknowledge** that a router differs from its join script on purpose. The warning goes away until something else differs.</li></ol> |
| Not in scope | The removal script, which only removes. Options for the user, the group, the route or the tunnel (§7). PNMTD changing the router itself: `pnmt` may not write. |

---

## 1. Today

**As of 1.20.1, none of the code this plan builds on has changed since 1.15.0:** `JoinScript`, `RouterPoll`, `MikrotikRouterService`, the MikroTik contracts, the entity, the endpoints and their rights. Since then:

- **1.17.0 added network scans and the network map.** The Setup tab's header gained *Scan now* and *Show on the network map*. A scan reads as `pnmt` with `read` alone, and leaves PNMT's own tunnel out by this installation's comment (`NetworkScanScript`, `where comment!="pnmt:7f3a"`). Neither option changes what a scan needs (§2.3).
- **1.19.0 added acknowledging high-risk OS update findings** (docs/linux-os-updates-plan.md §6.5). It is the model for §3.

### 1.1 The join script

- **`JoinScript.Build`** writes one script per mode. It always starts with `RemoveAll`: eight `remove [find where comment~"^pnmt:"]` lines for the firewall filter, route, address, peers, interface, the `pnmt` user's SSH keys, the user and the group. Then it adds everything again.
  - This is what makes the script safe to paste again (§4.3).
  - M0 found the waits and the order that this needs (§13.1).
- **Firewall rules exist only in the WireGuard modes:**
  - an accept rule for SSH from the server address (WireGuard + SSH);
  - an accept rule for the management ports from the access address (with management access);
  - a loop that repairs the rules 7.13 leaves invalid;
  - a move to the top of the filter list.

  Direct SSH gets a commented example only.
- **The script's own key file** is removed twice, with `/file remove [find name="pnmt-1.pub"]`: once before the script writes it, and once after the import.
- **There are no options.** `MikrotikRouterService.ScriptAsync` builds the script from the router's settings. Each view is audit-logged and sets `ScriptGeneratedAt`.

### 1.2 Script outdated

- **`MikrotikRouterService.Differences`** compares the latest reading with what the current script would create:
  - the tunnel's key, peer, endpoint, port, keepalive and MTU;
  - the management rule;
  - the group's `sensitive` and `test` policies;
  - PNMTD's current SSH key.

  In WireGuard only mode, it checks whether the settings changed after the script was last shown. Each difference is one sentence.
- **`ScriptOutdated`** is true when there is any difference. It shows in:
  - the Setup tab, as a warning with the list;
  - the status strip, as a warning that links to the Setup tab;
  - the `!` badge on the sensor page's Setup tab;
  - the Routers tab's *script outdated* tag, and its filter;
  - the overview's count.
- **Nothing says "this is intended".** Some routers will always show the warning, for example one whose admin manages the firewall, or one that deliberately leaves out a policy.
- **Found on the way:** `FleetAsync` and `StatusAsync` call `Differences` without the WireGuard context. As a result:
  - the Routers tab and the overview never count a WireGuard only router as outdated;
  - they miss every tunnel difference of a WireGuard + SSH router.

  Only the Setup tab sees these. §3.5 fixes it, because an acknowledgement has to mean the same everywhere.

---

## 2. Two options for the join script

### 2.1 The options

| Option | Default | Shown for | When off |
|---|---|---|---|
| **Firewall rules** | on | Modes whose script has rules: WireGuard + SSH, and WireGuard only with management access | The script doesn't touch the filter list: it adds, removes and moves no rule. Commented examples take their place |
| **Remove what an earlier script created** | on | every mode | The script removes no configuration. It adds what is missing, updates what is there, and leaves what is no longer needed |

- **With both on, the script is today's, byte for byte.** That path is lab-tested, and a test pins it (§6).
- **Remembered per router.** The options are stored on the router together with the script they were last shown with, as `ScriptGeneratedAt` is. The panel starts from them. So:
  - the next view gives the same script;
  - the comparison (§2.4) knows what the router was given.
- **Not a sensor setting.** The options are not on the sensor form or in the settings dialog. They describe the script, not how PNMTD reaches the router, so they sit next to the script.
- **Who may set them:** anyone who may view the script (Monitoring:Edit). Neither option widens what PNMT may do on the router, so neither is Admin-only. Management access, secrets and diagnostics stay Admin-only.

### 2.2 Without firewall rules

- **What goes:**
  - the `/ip firewall filter remove` line of `RemoveAll`;
  - both adds;
  - the invalid-rule repair;
  - the move.
- **Comment lines take their place**, as Direct SSH has today. Only the lines that apply are written: the SSH line for WireGuard + SSH, the management line with management access.

  ```routeros
  # PNMT adds no firewall rules (this router's option). PNMTD logs in through the tunnel from 10.253.0.1 to port 22.
  # If the input chain drops it, allow it, for example:
  # /ip firewall filter add chain=input in-interface=pnmt src-address=10.253.0.1 protocol=tcp dst-port=22 action=accept
  # With management access, custom tunnels come from 10.253.0.2 to ports 22,8291,80,443.
  ```

- **Rules that an earlier script added stay, and are the admin's from now on.** If PNMT removed them, it would cut itself off on any router where the admin hasn't added a rule of their own yet.
- **The hint in the panel explains the consequence.** MikroTik's default configuration has `drop all not coming from LAN`. On such a router PNMTD cannot log in until the admin either allows it or adds `pnmt` to the interface list `LAN`. Until then, the Setup tab's step *PNMTD logged in as pnmt* fails with 440, as it does today when a firewall drops the login.
- **The comparison leaves the firewall out** (§2.4).
- **Direct SSH has no such option:** its script has no rules.

### 2.3 Without removals

**This is the hard part.** Today the script is safe to paste again because it removes first. Without that, a plain `add` of the user `pnmt`, the group `pnmt-read` or the interface `pnmt` fails with "already exists" on a router that already has them. The lab found this in M1, for routers moved between installations.

So each object is **added if missing, and set otherwise**. Each object takes one line, so it works both pasted and imported:

```routeros
{ :local i [/user group find where name="pnmt-read"]; :if ([:len $i] = 0) do={ /user group add name=pnmt-read policy=ssh,read comment="pnmt:7f3a" } else={ /user group set $i policy=ssh,read comment="pnmt:7f3a" } }
```

| Object | Found by | If found, sets |
|---|---|---|
| Interface | `name="pnmt"` | `mtu`, `private-key`, `comment`. The listen port stays as it is; only a new interface picks a free one |
| Peer | `interface="pnmt"`, the first match | `public-key`, `endpoint-address`, `endpoint-port`, `allowed-address`, `persistent-keepalive`, `comment` |
| Address | `comment~"^pnmt:"`, the first match | `address`, `interface`, `comment` |
| Route | `comment~"^pnmt:"`, the first match | `dst-address`, `gateway`, `comment` |
| Firewall rules (with *Firewall rules* on) | `chain=input`, `in-interface=pnmt`, `comment~"^pnmt:"`, and `src-address`: the server address for SSH, the access address for management | `protocol`, `dst-port`, `action`, `disabled=no`, `comment` |
| Group | `name="pnmt-read"` | `policy`, `comment`. Setting `policy` also drops `sensitive` or `test` when they were turned off |
| User | `name="pnmt"` | `group`, `address`, `comment`. Never the password |
| SSH key | – | Imported as today. Nothing is removed |

- **Waits:** `WaitFor` stays, for new objects. `WaitGone` goes, because nothing is removed. The firewall repair and the move stay as they are.
- **The script's own key file is still removed** (open question 1):
  - it is not configuration;
  - the same script wrote it a moment before;
  - on 7.13 it would otherwise stay in the router's files.
- **The header says so:** `# Options: no removals.`, and "It removes nothing: it adds what is missing and updates what is there."
- **What stays on the router.** The panel's hint lists these:
  - **After a key rotation, the old SSH key.** PNMTD keeps the retired key's private half (`mikrotik_ssh_keys` with `RetiredAt` set), so the router keeps trusting a key PNMTD no longer uses. A new difference reports it (§3.2, `retired-key`).
  - **After management access is turned off, its firewall rule.** Today's difference "The router still accepts custom tunnels on its management ports" reports it, with a hint to remove the rule by hand.
  - **After a switch to Direct SSH, the tunnel.** It is dead, because the gateway no longer has its peer.
  - **What an earlier installation created.** What the script finds is updated in place and takes this installation's comment, so network scans keep leaving it out. Anything it doesn't find keeps the old comment, for example a second peer on the interface `pnmt`. Today's removals take that away. Without them, it stays, and network scans read it as an ordinary WireGuard peer, because they leave out only this installation's comment.
- **7.13's key quirk doesn't apply.** On 7.13 a removed user's keys go to the next user of the same name (§13.1). Here nothing is removed.
- **Check in the lab before anything else** (phase J1). Each answer goes into `JoinScript`'s remarks, as M0's findings did:
  1. `set $i` with the result of a `find`, and `set [:pick $i 0]`.
  2. `/user set … address=""` clears the address. It is needed when there is no source address.
  3. Importing a key the user already has: does RouterOS add a duplicate, or fail? If it fails, the import is guarded, with `:parse` because 7.13 and later versions name the key's comment differently (`key-owner` and `info`, as `RouterPoll` reads them).
  4. Does a `set` with unchanged values on the interface or the peer drop a WinBox session that runs through the tunnel? If not, the panel can promise that a re-paste through the tunnel keeps it up.

### 2.4 What the comparison makes of them

- **Firewall rules off:** the management rule is not compared, in either direction.
- **Removals off:** everything is compared as before. A difference that only a removal can fix says so: the management rule with management access off, and the retired key. For example: "The router still accepts custom tunnels on its management ports. The join script removes nothing: remove the rule by hand, or acknowledge it."
- For everything else, the Setup tab's "paste the script again to apply it" stays true, because the script sets what it finds.

### 2.5 API and CLI

- **`GET /sensors/{id}/mikrotik/join-script?firewall=false&removals=false`:**
  - a parameter that is left out uses the router's stored option;
  - the options used are stored together with `ScriptGeneratedAt`.
- **`MikrotikScript`** gains `Options: MikrotikScriptOptions(bool Firewall, bool Removals)`.
- **The audit line names the options:** "router office-gw: join script (WireGuard + SSH, without firewall rules)".
- **`MikrotikRouterDetail`** gains `ScriptOptions`.
- **The CLI:** `pnmtd mikrotik script <router> [--no-firewall] [--no-removals]`.

---

## 3. Acknowledging differences

### 3.1 What it means

- **An acknowledgement covers a difference exactly as it was seen:** what the router has, and what PNMT expects. If either changes, it is a new difference and the warning comes back.
  - Example: "The router dials port 51820; the endpoint's port is now 51999." is acknowledged. The endpoint then moves to 52000, and the warning is shown again.
- **As for OS updates** (docs/linux-os-updates-plan.md §6.5):
  - **one or several differences at once,** chosen in a dialog, all of them by default;
  - **a reason is required** (at most 500 characters), for example "We manage this router's firewall ourselves" or "This router dials vpn.internal, on purpose";
  - **an acknowledged difference stays in the list,** greyed out, with who acknowledged it, when and why;
  - **each one can be removed** on its own, with *Remove acknowledgement*.
- **Unlike for OS updates, there is no expiry.** A high-risk finding is a risk that is accepted for a while, and it is reconsidered after 1 to 30 days. A difference here is a deliberate configuration, which stays right until it changes. It then ends by itself, as a finding's acknowledgement ends when the evidence gets stronger: the person acknowledged what they saw, not something else.
- **A difference that is gone no longer matters.** Its acknowledgement does nothing, and is dropped at the next acknowledgement or removal on that router. Such an ending writes no audit entry, because it is noticed when the router is read, not written. The warning that comes back is the signal.
- **The warning disappears everywhere:** the Setup tab, the status strip, the tab badge, the Routers tab's tag and filter, and the overview's count.
- **It hides a warning, not a fact.** Two lists stay as they are:
  - before retiring keys, the SSH keys page still lists routers that accept only an old key;
  - after a network change, the network page still lists routers that need a new script.
- **Whoever may change a setting may acknowledge its difference:**
  - **CanWrite** (Monitoring:Edit): the tunnel, PNMTD's current SSH key, and WireGuard only's "settings changed".
  - **Admin** (Mikrotik:Manage): `sensitive`, `test`, the management rule and the retired key. Hiding these would hide that PNMT may do more on the router than its settings say. `Validate` already applies the same rule to changing those settings.
- **The audit log records each step,** against the router's sensor, one entry per difference as OS updates do:
  - `MikrotikDifferenceAcknowledged`: who, the difference's text and the reason;
  - `MikrotikDifferenceAcknowledgementEnded`: removed by a person, and who.

### 3.2 Each difference gets a key

`Differences` returns records instead of sentences: `ScriptDifference(string Key, string Text, string Seen, string Expected, bool Admin)`. The ID is the key, a colon, and the first 16 hex digits of SHA-256 over the key, `Seen` and `Expected`.

| Key | Seen | Expected | Admin |
|---|---|---|---|
| `settings-changed` (WireGuard only) | when the script was last shown | when the settings last changed (the latest of the three times) | |
| `no-tunnel` | – | the peer's public key | |
| `tunnel-key` | the router's public key | the peer's public key | |
| `peer-key` | the router's peer key | the gateway's public key | |
| `endpoint-host`, `endpoint-port`, `keepalive`, `mtu` | the router's value | the network's value | |
| `management-rule` | whether the rule exists | management access | ✓ |
| `policy-sensitive`, `policy-test` | whether the group has the policy | the setting | ✓ |
| `ssh-key` | the router's key owners | the current key's comment | |
| **`retired-key`** (new) | the retired keys the router still has | – | ✓ |

The texts stay as they are today, so the existing tests keep their strings.

### 3.3 Data model

On `mikrotik_routers`, in one migration, `MikrotikJoinOptions`:

| Column | Type | Content |
|---|---|---|
| `script_firewall` | bool, default true | The options of the script that was last shown (§2.1) |
| `script_removals` | bool, default true | |
| `acknowledged_differences` | jsonb, null | `[{ "id": "endpoint-port:3f2a…", "at": "…", "byId": "…", "byName": "alice", "reason": "…" }]`. `byName` is the user's name at the time, cut to 200 characters, as OS updates store `AcknowledgedByName` |

A jsonb column, not a table as OS updates have: a router has a handful of differences at most, they are computed rather than stored, and nothing queries them across routers.

### 3.4 API

- **`MikrotikRouterDetail`:**
  - `ScriptOutdated` now means **open** differences, those not acknowledged.
  - `ScriptDifferences` keeps its shape, `string[]`, with the open differences' texts, so older clients keep working.
  - **New:** `Differences: MikrotikScriptDifference[]`, each with `Id`, `Text`, `Admin` and `Acknowledgement?`. It holds all differences, open and acknowledged.
  - **New:** `MikrotikDifferenceAcknowledgement(string? ByName, string Reason, DateTime At)`, shaped like `OsFindingAcknowledgement` but without `Until`.
- **`POST /sensors/{id}/mikrotik/differences/acknowledge`**, with the body `{ "ids": [...], "reason": "…" }`, answers with `MikrotikRouterDetail`:
  - it needs Monitoring:Edit;
  - the request is checked as OS updates check theirs: 1 to 500 IDs, and a reason of 1 to 500 characters;
  - it answers 403 when an Admin difference is among the IDs and the caller is not an Admin;
  - IDs that are no longer current are ignored, so only what is still as the user saw it gets acknowledged;
  - each difference writes its own `MikrotikDifferenceAcknowledged` entry.
- **`DELETE /sensors/{id}/mikrotik/differences/{differenceId}/acknowledgement`** answers with `MikrotikRouterDetail`:
  - it needs Monitoring:Edit, and Admin for an Admin difference;
  - an ID without an acknowledgement answers with the detail as it is, as OS updates' `UnacknowledgeAsync` does;
  - it writes `MikrotikDifferenceAcknowledgementEnded`.
- **Open differences only** count for `MikrotikFleetItem.ScriptOutdated`, `MikrotikStatus.ScriptsOutdated` and the `scriptOutdated` filter.
- **Registration:** both endpoints go into the `EndpointAuditTests` matrix, and both events into the `AuditEvents` registry, under Mikrotik and Sensor (next to `MikrotikHostKeyAccepted`).

### 3.5 The fleet and the overview agree with the Setup tab

- **`FleetAsync` and `StatusAsync` get the same inputs as the Setup tab:**
  - the network settings, loaded once;
  - the routers' peers and their status, one query each;
  - the comments of the retired keys.

  They pass these to `Differences`, then apply the acknowledgements. This fixes the gap described in §1.2.
- **One helper,** `OpenDifferences(router, reading, context)`, serves `Detail`, `FleetAsync` and `StatusAsync`, so the three can't drift apart again.

---

## 4. Web UI

### 4.1 Setup tab: the join script panel

```
┌ Join script ─────────────────────────────────────────────────────────────┐
│ Paste it into a terminal on the router …                                 │
│                                                                          │
│ [x] Firewall rules                                                       │
│     Accepts PNMTD's SSH from 10.253.0.1 on the interface pnmt, first in  │
│     the filter list.                                                     │
│ [x] Remove what an earlier script created                                │
│     Pasting it again replaces what PNMT created (comment pnmt:…).        │
│                                                                          │
│ [Show join script]                                                       │
└──────────────────────────────────────────────────────────────────────────┘
```

- **Checkboxes** (`primevue/checkbox`, as the availability views use) start from `scriptOptions`. *Firewall rules* appears only where §2.1 says.
- **An option that is off gets a hint below it,** with the text of §2.2 or §2.3: a warning for firewall rules, information for removals.
- **While the script is shown, a change fetches it again.** That is another audit-logged view, and the panel already says that every view is recorded.
- **The script's header line,** `# Options: …`, names the options in the script too.

### 4.2 The warning

- **In the Setup tab, the warning gets *Acknowledge…*** under the list, for CanWrite. It opens `AcknowledgeDifferencesDialog`, which is built like OS updates' `AcknowledgeFindingsDialog` without the *For* field:
  - the open differences, each with a checkbox, all checked;
  - for a User, the Admin ones are unchecked and disabled, with "Only an Admin can acknowledge the policies, the management rule and retired keys.";
  - *Reason*, required.
- **The list shows acknowledged differences too,** greyed out, as OS updates' `HighRiskPanel` does: "Acknowledged by alice 3 hours ago: We manage this router's firewall ourselves", with *Remove acknowledgement*, which asks for confirmation first.
  - The warning itself appears only while a difference is open. With all of them acknowledged, the panel shows them under a muted heading, *Acknowledged differences*.
- **In the status strip, the warning gets *Acknowledge…*.** It opens the same dialog, which also lists the differences that the strip doesn't show.
- **The badge, the Routers tab and the overview** follow `scriptOutdated` already. They need nothing but the regenerated schema.

---

## 5. Phases

| Phase | What | Done when | Estimate |
|---|---|---|---|
| J1 | The lab questions of §2.3, on CHR 7.13 and 7.25.1 (`tools/chr-lab.sh`) | Each one is answered and noted in `JoinScript`'s remarks | 0.5 d |
| J2 | `JoinScriptSettings.Firewall` and `Removals` and the builder; the stored options, the endpoint's parameters and the CLI; the comparison without the firewall; the `retired-key` difference | The tests of §6 pass. **Lab, Direct SSH:** a join without removals on a fresh router, pasted twice more, then polled, gives no differences and no `failure:` in the import output; then a join with the defaults. **By hand, as in M3,** with `dotnet run` on the test database, not `./start.sh` on the dev one: WireGuard + SSH without firewall rules on a router with the default configuration fails with 440, and with `pnmt` in `LAN` becomes Ready | 1.5 d |
| J3 | Keyed differences, acknowledgements with their reason, the two endpoints, the audit events, and the fleet and status fix (§3.5) | The tests of §6 pass | 1 d |
| J4 | The web UI (§4) | The e2e tests of §6 pass | 1 d |
| J5 | Docs: §4.3's table and rules and §3.3 in docs/mikrotik-plan.md, and the release notes | | 0.25 d |

---

## 6. Testing

- **`RouterOsTests`:**
  - with the default options, each mode's script equals today's, as golden strings;
  - with firewall rules off, no line starts with `/ip firewall`, and the commented example is there;
  - with removals off, no line removes anything except the key file's two lines;
  - with removals off, every add has its set branch, and each block is still one line;
  - quoting holds for a router name and an endpoint that contain quotes.
- **`MikrotikRouterTests`, `WireGuardRouterTests`:**
  - a view with options stores them, a later view without parameters uses them, and the audit line names them;
  - with firewall rules off, there is no management-rule difference, in either direction;
  - acknowledging hides the difference and makes `ScriptOutdated` false, and the difference keeps its text with who, when and why;
  - a changed value comes back, for example when the endpoint's port changes again;
  - a User gets 403 for `policy-test`, when acknowledging it and when removing its acknowledgement;
  - a missing or overlong reason gives 400 on `reason`;
  - IDs that are no longer current are ignored;
  - removing an acknowledgement brings the warning back, and removing one that doesn't exist changes nothing;
  - acknowledging two differences writes two audit entries, and a removal writes one;
  - the fleet and the status count a WireGuard only router whose settings changed, and the tunnel differences (the gap of §1.2), and apply the acknowledgements;
  - a router with a retired key gets the `retired-key` difference.
- **`EndpointAuditTests`, `AuditLogTests`:** the new endpoints and events.
- **`MikrotikLabTests`:** a second pass with removals off (J2).
- **e2e, `mikrotik.spec.ts`:**
  - the checkboxes change the script;
  - *Acknowledge…* with a reason hides the warning in the Setup tab and the status strip, and the badge, and the difference stays greyed out with the reason;
  - *Remove acknowledgement* brings the warning back.

---

## 7. Considered and left out

- **Firewall rules, but not moved to the top.** With removals on, every paste would remove the rules and add them again at the end, below the drop rules the admin had moved them above. It works only together with *no removals*. It is left out until someone needs it.
- **Options for the user, the group, the route and the tunnel.** Without them the mode doesn't work. The mode is already that option: WireGuard only has no user.
- **Add-or-set as the only script,** removing only what is no longer needed. It is cleaner, but it rewrites the lab-tested path. Maybe later, once the no-removals path has proven itself.
- **Acknowledging until the settings change,** keyed on `ScriptChangedAt`. It is simpler, but a router that drifts on its own, for example when someone edits it by hand, would stay hidden.
- **An expiry, as OS updates have.** A deliberate configuration would then nag again every 30 days, although nothing changed. An acknowledgement ends when what it covers changes (§3.1).
- **One click, without a reason.** That is closest to "a button that makes the message disappear", but the next person sees a hidden difference and no reason for it. It would also differ from OS updates. Open question 4.
- **Saying why a warning came back** ("acknowledged before by alice; the value changed"), as OS updates' 466 does. It needs a write when the router is read, or a comparison with the acknowledgements that no longer match. Maybe later.
- **The options as sensor settings,** on the form and in the settings dialog. They describe the script, not how PNMTD reaches the router, and they would sit far from where the admin decides.

---

## 8. Open questions

**Answered on 2026-11-23 ("build it"): all four as recommended.**

1. **Without removals, should the script still remove its own key file?** Recommended: **yes.** It is not configuration, the same script wrote it a moment before, and 7.25 removes it by itself. The alternative is a script without a single `remove`, which leaves a public key file on 7.13.
2. **Should only Admins acknowledge the differences of the Admin settings** (`sensitive`, `test`, the management rule, retired keys)? Recommended: **yes.** It is the same rule as for changing those settings.
3. **Should the options be remembered per router, or apply per view only?** Recommended: **per router.** Per view, every view starts with both on and nothing is stored, so the comparison doesn't know what the router got. The firewall would then always need an acknowledgement.
4. **Should acknowledging need a reason?** Recommended: **yes, as for OS updates.** An acknowledgement says "this is intended", and only a reason tells the next person why. It costs one dialog, which the status strip needs anyway to show what is being acknowledged. The alternative is a one-click *Acknowledge* without a reason (§7).

---

## 9. As built (2026-11-23)

**J1 on CHR 7.13 and 7.25.1** (`tools/chr-lab.sh`; 7.25.1 as emulated x86_64, because its ARM64 image panicked at boot in the lab). The sequence ran on each:

1. the defaults, under another installation's tag (`pnmt:0ld0`);
2. without removals, three times, the third with Diagnostics on and an MTU of 1380;
3. Direct SSH without removals, and without a source address;
4. without firewall rules;
5. the defaults again;
6. the removal script.

No import printed a RouterOS error. The findings are in `JoinScript`'s remarks:

- **`set $i` with the result of a `find`, and `set [:pick $i 0]`, work.** Everything the script found took this installation's comment, the listen port stayed, and there were still two firewall rules, with PNMT's first.
- **`/user set … address=""` clears the address.**
- **7.13 imports a key the user already has again,** one more copy at each paste. So without removals, the script imports PNMTD's key only when the user doesn't have it yet (`ImportOnce`).
  - The key is found by its comment, which RouterOS calls `key-owner` on 7.13 and `info` on 7.25.1. Each name is read inside `:parse` and `on-error`, so a version that has neither can't fail the line: it imports, a duplicate at worst.
  - Each of PNMTD's keys has a comment of its own (`pnmt-<id>@<host>`), so the comment names the key.
- **Not answered:** whether a `set` with unchanged values drops a WinBox session through the tunnel. The lab has no gateway, and the panel doesn't promise it.
- **`MikrotikLabTests` has the second pass of J2:** two pastes without removals over the first join, then the defaults. After each it checks for no RouterOS error, one key, a successful poll and no difference. It passed twice in a row on both routers. A first run, right after the sequence above had been run by hand, failed, and its message wasn't kept.

**Where the build differs from the plan:**

- **Column names** follow the table's PascalCase: `ScriptFirewall`, `ScriptRemovals`, `AcknowledgedDifferences` (`[AuditIgnore]`: the events say what happened). The migration `MikrotikJoinOptions` starts existing routers with both options on.
- **The firewall option means something only where the script has rules.** Elsewhere the script is as with it on, so PNMT's old rules are still removed, and the audit line doesn't name the option. It is stored as given.
- **`MikrotikScript.Options` is null** for the removal script, which has no options. The CLI refuses them with `--removal`.
- **Names in acknowledgements are shown to everyone who sees the router,** as backups' `CreatedByName` are. OS updates hide them without Monitoring's Details.
- **A difference's `Admin` follows from its key,** so removing an acknowledgement checks the right even after the difference is gone.
- **WireGuard only's `settings-changed` compares its times to the second** in its id. PostgreSQL keeps microseconds, the clock ticks in 100 ns, and an id must stay the same after a save.
- **The seed:** basel's Diagnostics are on and its group lacks `test`, a difference for the e2e test to acknowledge.
- **For the release notes:** once updated, a router that still accepts a retired PNMTD key shows the new difference `retired-key` until its new script is pasted.

**Tests:**

- `RouterOsTests`: the default scripts against golden files (`Fixtures/Mikrotik/join/`, written from 1.20.1's builder before the change), without firewall rules, and without removals.
- `MikrotikRouterTests`: the stored options, acknowledging with a reason until a value changes, the Admin's differences, removal, the audit entries, and the retired key.
- `WireGuardRouterTests`: the firewall left out, and the fleet and the overview counting what the Setup tab shows.
- `EndpointAuditTests`, and `MikrotikLabTests` (J2).
- Vitest for `mikrotikDraft.ts`'s helpers, and `e2e/mikrotik.spec.ts` on basel.

---

## Appendix: files

| File | Change |
|---|---|
| `PNMTD.Lib/Mikrotik/JoinScript.cs` | `JoinScriptSettings.Firewall` and `Removals`; the add-or-set builder; the commented firewall example; the header's options line; J1's findings in the remarks |
| `PNMTD/Services/Domain/MikrotikRouterService.cs` | `ScriptAsync` takes and stores the options; `Differences` returns keyed records; `OpenDifferences`; `AcknowledgeAsync` and `UnacknowledgeAsync`, modelled on `OsUpdatesService`'s; the WireGuard context and retired keys in `FleetAsync` and `StatusAsync` |
| `PNMTD/Models/Db/MikrotikEntities.cs`, a migration | The three columns of §3.3 |
| `PNMTD/Api/V1/Contracts/Mikrotik.cs` | `MikrotikScriptOptions`, `MikrotikScriptDifference`, `MikrotikDifferenceAcknowledgement`, `MikrotikDifferenceAcknowledgeRequest` (`ids`, `reason`); the new fields of `MikrotikScript` and `MikrotikRouterDetail` |
| `PNMTD/Api/V1/MikrotikControllers.cs` | The join script's parameters; `POST differences/acknowledge` and `DELETE differences/{differenceId}/acknowledgement` |
| `PNMTD/Audit/AuditEvents.cs` | `MikrotikDifferenceAcknowledged`, `MikrotikDifferenceAcknowledgementEnded` |
| `PNMTD/Cli/MikrotikCommand.cs` | `--no-firewall`, `--no-removals` |
| `PNMTD.WebUI/src/views/sensors/mikrotik/SetupTab.vue` | The checkboxes and their hints; *Acknowledge…*; acknowledged differences greyed out, with *Remove acknowledgement* |
| `PNMTD.WebUI/src/views/sensors/mikrotik/AcknowledgeDifferencesDialog.vue` (new) | The differences to choose and the reason, modelled on `linux/updates/AcknowledgeFindingsDialog.vue` |
| `PNMTD.WebUI/src/views/sensors/mikrotik/MikrotikStatusStrip.vue` | *Acknowledge…*, opening the same dialog |
| `PNMTD.WebUI/src/views/sensors/mikrotik/mikrotikDraft.ts` | `scriptHasRules(m)`, which decides when *Firewall rules* is shown, with a vitest |
| `PNMTD.WebUI/src/api/resources.ts`, `openapi.json`, `schema.d.ts` | `joinScript(id, options)`, `acknowledgeDifferences`, `unacknowledgeDifference`; the regenerated schema |
| Tests | As in §6 |
| `docs/mikrotik-plan.md`, release notes | §4.3's table and rules, §3.3 |
