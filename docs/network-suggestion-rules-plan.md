# Plan: Network map suggestions: VLANs on the wire, one cable per port, and skip rules

**Status:** **Built (2026-11-26): V0 to V4 (§11). Released in 1.29.0.** It extends [network-map-plan.md](network-map-plan.md) (released in 1.17.0, *the map plan* below). The map plan's decisions stay: nothing becomes a connection without a person, and every end is a router or a host. The questions of §9 are decided as recommended, except the release (question 6).

| | |
|---|---|
| **Goal** | <ol><li>**One cable gives one suggestion.** Today two routers with nine VLANs on one trunk give **ten** suggestions, all High (§2.1). Afterwards they give one, on the physical ports, and its evidence names the VLANs (§3).</li><li>**Servers whose adapters share a MAC get suggested again.** Today a server with a VLAN sub-interface, a Linux bridge or a bond gets **none** (§2.2, row 6).</li><li>**Skip rules:** a person can tell the analysis what to leave out: a VLAN, an interface, an interface type, a discovery protocol, a MAC prefix, or a whole router. Each rule covers all routers, a location or one router. The form shows what a rule would remove before it is saved (§4).</li></ol> |
| **Not in scope** | <ul><li>Changing what the scan reads or stores. The rules apply to the analysis only, and the scan's tables and snapshots stay as they are.</li><li>Connections made or changed automatically. Existing connections stay as they are (§5).</li><li>Hiding devices from the Network interfaces tab. *Ignore* still does that, one MAC at a time (§8).</li><li>Rules on the servers' side, such as adapter names (§8).</li></ul> |
| **Rights** | No new area. Reading the rules needs *Network map: View*. Changing them needs *Manage* (question 1) |
| **Migrations** | **One**, `AddNetworkSkipRules`: the table `network_skip_rules` and one column on `network_interfaces` (§4.7). The built-in changes of §3 need none |

---

## 1. Summary

| Part | What it is | Setting |
|---|---|---|
| **Ends on the wire** (§3.1, §3.2) | Every end on a router's interface is moved to the physical port under it: from a VLAN to its parent, and from a bridge to the member port where the device was seen. The map plan's §3.3 already said so ("a VLAN is no connection of its own"), but the build did it only for ARP and DHCP | none, built in |
| **One cable per port** (§3.3, §3.4) | A device pair's suggestions that share a router port are one suggestion. The strongest one keeps its ends, and the rest go into its evidence, with the VLANs named once | none |
| **Servers' shared MACs** (§3.5) | One owner per host, not one per adapter. The host's end is the physical adapter | none |
| **VRRP and second paths** (§3.6, §3.7) | VRRP's shared MACs say nothing about a cable. A link that only the MAC table or ARP shows, between two routers already linked on other ports, drops to Low | none |
| **Skip rules** (§4) | Rules that leave rows out of the analysis: by router or location, interface name, type, VLAN, discovery protocol or MAC prefix. The form previews the result, and a suggestion can start a rule with *Skip like this…* | the *Skip rules* tab |

---

## 2. Today (checked in the code)

### 2.1 How one cable becomes ten suggestions

**The setup:** *office-gw* `ether1` is cabled to *core-gw* `sfp-sfpplus1`, untagged. Both routers also have `vlan10` … `vlan90` on that port. **Neighbor discovery runs on every interface in the discovery list, VLAN interfaces included.** So each router sees the other ten times:

| office-gw sees core-gw | on `interface` | its port (`interface-name`) |
|---|---|---|
| untagged | `ether1` | `sfp-sfpplus1` |
| VLAN 10 | `vlan10` | `vlan10` |
| … | … | … |
| VLAN 90 | `vlan90` | `vlan90` |

**What the analyser does with it** ([TopologyAnalyzer.cs](../PNMTD.Lib/Network/TopologyAnalyzer.cs) l.541-560):

1. Every neighbor entry becomes a candidate of its own, with the entry's interface as the near end.
2. The reverse lookup (l.552) finds core-gw's entry whose `interface-name` is `vlan10`, so the far end is core-gw `vlan10` and the candidate is **High**.
3. The key is the two ends (`ConnectionEnd.PairKey`), so `ether1|sfp-sfpplus1`, `vlan10|vlan10`, … are **ten keys**.
4. The merge in `Finish` (l.744-760) joins only an end *without* a port into one with a port. Ten ends that each name a port stay apart.

**The result:** ten High suggestions between the same two routers. *Accept selected* makes ten connections and ten lines on the map. The nine VLAN ones propose no type either, because a VLAN port has no detected type (`ConnectionTypeDetector`).

### 2.2 Every place where a VLAN or a virtual interface multiplies suggestions, or loses them

| # | Where | What happens | Effect |
|---|---|---|---|
| 1 | Router ↔ router by neighbor (l.543-560) | One candidate per entry, as in §2.1. Nothing moves a VLAN to its parent | One suggestion per VLAN |
| 2 | The far port (l.553) | `Port(b, n.RemoteInterface) is { Type: not "bridge" }` refuses a bridge but lets a VLAN through | When the near side names the physical port: `ether1 ↔ vlan10`, `ether1 ↔ vlan20`, …, each Medium |
| 3 | Router ↔ router by MAC table (l.593-607, `macsOnPort` l.252-264) | The table is keyed by `on-interface`. With **one bridge per VLAN** (the design without vlan-filtering, where VLAN interfaces are bridge ports), that is `vlan10`, `vlan20`, … | One suggestion per VLAN |
| 4 | Router ↔ host by neighbor (l.654-660) | As row 1, for a host's MAC: a MikroTik switch assigned to a host, or a server that sends MNDP or CDP on its VLANs | One suggestion per VLAN |
| 5 | Router ↔ host by MAC table (l.698-718) | A host with **one MAC per VLAN** (Hyper-V's management vNICs, macvlan) on one router port. Each MAC is a candidate with its own adapter as the host's end, so each gets a key of its own | One suggestion per vNIC, all on the same router port |
| 6 | Router ↔ host, the owner (l.645) | `owners.Count != 1` counts **adapters, not hosts**. A VLAN sub-interface (`eno1.10`), a Linux bridge (`vmbr0`) and bond members carry their parent's MAC, so the agent reports that MAC two or more times (`agent_adapter_macs`, l.237-243) | **No suggestion at all** for such a server, the opposite problem. `Match` (l.524) groups by host, so the Network interfaces tab still shows the match |
| 7 | Attachment (l.468-502), roles (l.370) | A VLAN port's role is *Virtual*, and only *Edge* ports attach a MAC | Hosts behind a bridge-per-VLAN port are never attached, so never suggested |
| 8 | Coverage (l.770) and `connectionsAtPort` (l.213-218) | Ends are compared by name, as stored | A connection on `ether1` doesn't cover the VLAN suggestions. One made on `vlan10` is never confirmed by the physical evidence |

**What already works:**

- **ARP and DHCP leases on a VLAN** are moved to the VLAN's parent when the scan is parsed ([NetworkScanReading.cs](../PNMTD.Lib/Network/NetworkScanReading.cs) l.374-381). The VLAN goes into `VlanId`, and its name into `Bridge`. So the ARP rule gives one candidate per physical port.
- **A vlan-filtering bridge's MAC table** names the member port as `on-interface`, with `vid`. All the VLANs on a port fall into one `macsOnPort` entry.
- **The lab fixtures have no VLANs** (`PNMTD.Tests/Fixtures/Network/7.13`, `7.25.1`). So it is not recorded how RouterOS 7 names a neighbor seen through a VLAN on a vlan-filtering bridge: `vlan10`, `ether1,bridge` or `ether1,vlan10`. Nor is it recorded what `interface-name` says for a port in a bridge: `bridge` or `bridge/ether2`. **V0 records both** (§10). Until then, §3.1 handles every shape.

---

## 3. Built in: ends on the wire (no setting)

### 3.1 The physical port under an interface

A pure function in the analyser, **`Wire(router, name) → (port or none, VLAN name, VLAN ID)`**, from the stored ports (`network_router_ports.vlan_parent`, `.parent`, `.type`):

| Interface as printed | Physical port | Kept for the evidence |
|---|---|---|
| A port: `ether1`, `sfp-sfpplus1`, `wlan1` | itself | – |
| `vlan10` on `ether1` | `ether1` | `vlan10`, 10 |
| A VLAN on a VLAN (QinQ) | the outer parent's port, recursively, at most 4 steps | both VLANs |
| `vlan10` on `bond1` | `bond1`. A bond is an end, as today: the member is unknown | `vlan10`, 10 |
| `vlan10` on a vlan-filtering `bridge` | the member the row names (`on-interface`, or the neighbor's port in its `interface` list), **else none** | `vlan10`, 10 |
| `bridge` | the member the row names, else none | – |
| `vrrp10` on `vlan10` | `vlan10`'s physical port | `vrrp10` |
| `wg…`, `eoip…`, `gre…`, `l2tp-in…` and the other tunnels | **itself**: they are the ends of virtual connections | – |
| A name the router has no port for | itself | – |

- **The far port** (`interface-name`) is resolved on the far router with the same function. `bridge/ether2` is read as `ether2` when that is one of the far router's ports *(V0)*.
- **At analysis time, from what is stored.** No rescan is needed: the next analysis (after any scan, after a person's change, or `pnmtd network analyse`) applies it to every router's last scan.
- **The parser is changed only if V0 shows** that a neighbor's `interface` list doesn't start with the port. Today it keeps the first and last items (l.219-224).

### 3.2 Where it applies

- **Neighbor rules** (router ↔ router, router ↔ host):
  - the near end is `Wire(a, interface)`, and the far end is `Wire(b, interface-name)`;
  - the reverse lookup compares the resolved ports, so core-gw's `vlan10` entry matches office-gw's `ether1`.
- **MAC table:** `macsOnPort` is keyed by `Wire(router, on-interface)`. Rows 3 and 7 of §2.2 go away, and hosts behind a bridge-per-VLAN port are attached to its parent's port.
- **ARP:** already resolved at parse time. `Wire` changes nothing there, because a physical port resolves to itself.
- **`PortOfMac`** (l.315) and **`NonBridge`** (l.623) give way to `Wire`. A VLAN shares its parent's MAC, so the parent is the router's port of that MAC.
- **Roles:** observations count for the physical port. A VLAN port itself keeps the role *Virtual* in the side panel.
- **Types:** a suggestion's type comes from the physical port (`CombineTypes`). The VLAN ones of today have none.
- **Connections:**
  - their ends are resolved too, for coverage and confirmation (l.770);
  - `connectionsAtPort` and *Differs* use the resolved ends as well;
  - so a connection that was made on `vlan10` counts as being on `ether1` (§5).

### 3.3 One cable per port

**A physical port holds one cable.** So two candidates between the same two devices that share a router port are the same cable. In `Finish`, before today's merge:

1. **Group** the candidates by resolved ends, as today (one group per key).
2. **Within a device pair**, take the groups strongest first:
   - by confidence;
   - then by rule: neighbor before WireGuard and tunnel, before MAC table, before ARP;
   - then by key, so the result is the same each time.
3. **A group that shares a router port** with one already taken (same router, same resolved port, on either side) **is folded into it**. The stronger group keeps its ends, and the weaker one's ends go into its evidence: "core-gw's MAC table says ether2". A group that shares no port is taken as a suggestion of its own.
4. **Router ↔ host:** the router's port decides. Two candidates for the same host on the same router port are one cable, whatever the host's adapter. The host's end follows §3.5.
5. **Virtual connections** (the `wireguard` and `tunnel` rules) are never folded into physical ones: two WireGuard tunnels between two routers stay two suggestions.
6. **Then today's merge** (l.749-760): an end without a port goes into a pair that names one.

**Two real cables stay two.** `ether1 ↔ ether1` and `ether2 ↔ ether2`, each seen by neighbor discovery, share no port. A VLAN that seems to run from `ether1` to the far `ether2` is folded into the stronger one, and its evidence says so. That evidence points to a switch in between (§3.7).

### 3.4 Evidence that names the VLANs once

Today, folding ten candidates would leave ten evidence sentences that differ only by the VLAN. Instead:

- **The sentences of one rule and one physical pair are joined into one:** "office-gw sees core-gw on ether1 by MNDP (its port sfp-sfpplus1), and on 9 VLANs on that port: vlan10, vlan20, vlan30 and 6 more". The reverse direction is joined the same way.
- **The evidence keeps more as data** (the JSON `evidence`, additive):
  - the router;
  - the interface as printed;
  - the VLAN ID and the VLANs folded in.

  The UI lists the VLANs in the expansion, and *Skip like this…* (§4.5) prefills from them.

### 3.5 Servers whose adapters share a MAC

- **One owner per host:** `hostMacs` is grouped by host before `owners.Count` is checked (l.645), as `Match` already does (l.524). Row 6 of §2.2 goes away. Two different hosts with one MAC are still *Ambiguous*.
- **The host's end** is, in this order:
  1. the one adapter with that MAC that isn't `Virtual` (`eno1`, not `eno1.10` or `vmbr0`);
  2. else the bond (`Kind = "Bond"`);
  3. else none, and the person picks.

  `TopologyAdapter` gains `Kind`. The column already exists in `agent_adapter_macs` but isn't loaded ([NetworkAnalysis.cs](../PNMTD/Network/NetworkAnalysis.cs) l.123-125).
- **One MAC per VLAN on one port** (Hyper-V's vNICs, row 5) is one cable by §3.3. Its end is the physical adapter if the agent reports one with a MAC seen on that port, else none.

### 3.6 VRRP

- **`vrrp` joins the virtual types** (l.144).
- **VRRP's MACs** (`00:00:5E:00:01:xx`, and `00:00:5E:00:02:xx` for IPv6) are shared by the routers of a group by design. They are left out of `routerMacs`, so they make no port *To a router* and no MAC-table link. The routers' VRRP interfaces are often one per VLAN, so they would multiply links too.

### 3.7 A second path to the same router

**The setup:** office-gw `ether1` is cabled to core-gw. office-gw `ether2` goes to a switch, which carries VLAN 20 to core-gw by another path. Then office-gw's MAC table learns core-gw's MAC on `ether2` as well, and today that gives a second Medium suggestion.

- **New rule:** a candidate between two routers that **only the MAC table or ARP shows**, when the pair already has a connection or a neighbor-discovery suggestion on other ports, is **Low**. Its evidence says: "core-gw is also reached on ether1 by MNDP. This is probably the same network through another device".
- **It is not dropped:** a real second cable on a port without discovery would otherwise vanish. Low is the filter's way of hiding it.
- **A rule on VLAN 20** removes it for good (§4.3).

### 3.8 The example afterwards

| Case | Today | Afterwards |
|---|---|---|
| §2.1, nine VLANs on one trunk | 10 suggestions, High | **1:** office-gw `ether1` ↔ core-gw `sfp-sfpplus1`, High, with the type from both ports. "… and on 9 VLANs on that port: vlan10, vlan20, vlan30 and 6 more" |
| Proxmox server, `eno1` = `vmbr0` = `vmbr0.20` | none | 1: office-gw `ether5` ↔ hv01 `eno1`, High or Medium as the port's other MACs decide |
| Hyper-V server with two management vNICs | 2, Medium | 1, Medium, with both vNICs in the evidence |
| One bridge per VLAN (`bridge10` with `vlan10` as its port) | one router ↔ router per VLAN, no hosts | one per physical port, and the hosts behind it attached and suggested |
| Two real cables | 2 | 2 |

---

## 4. Skip rules (configured)

§3 removes what is the same everywhere. **The rest depends on the site:**

- a guest VLAN full of phones;
- a management VLAN that every switch carries to every router;
- a mirror port that sees every MAC;
- a lab router;
- an old IP phone system that speaks CDP.

These cases need a rule written for the site.

### 4.1 What a rule is

| Field | Values | What it is compared with |
|---|---|---|
| **Name** | text, required (100) | – |
| **On** | All routers · a location, with its sub-locations · one router | the router that shows the row. For a far port, the far router |
| **Interface** | a pattern with `*` and `?`, case-insensitive: `vlan9*`, `ether10`, `*-guest` | the interface **as the router printed it** (`vlan20`), **and the physical port** it resolves to (`ether1`). So `vlan*` skips what came through VLAN interfaces, and `ether1` skips everything on that port, its VLANs included |
| **Type** | one or more RouterOS types: `vlan`, `bridge`, `wg`, `eoip`, `l2tp-in`, … | the printed interface's type, and the physical port's |
| **VLAN** | IDs and ranges: `99`, `4000-4094`, `10,20-29` | the row's VLAN: a MAC table row's `vid`, the VLAN of an ARP entry, a lease or a neighbor |
| **Evidence** | MAC table · ARP · MNDP · LLDP · CDP · Wi-Fi · WireGuard · Tunnel | the table the row comes from. A neighbor seen by several protocols (`mndp,lldp`) is skipped only when all of them are |
| **MAC prefix** | 1 to 6 octets: `00:50:56`, `00:00:5E:00:01`. Written with `:`, `-` or nothing | the MAC of the device seen, not the router's own |
| **Enabled** | on (the default) or off | – |

- **The conditions of one rule must all match** (and). **A row is skipped when any enabled rule matches** (or).
- **At least one condition is needed.** The one exception: *one router* with no condition skips everything that router shows, for a lab router. *All routers* or a location with no condition is refused.
- **No order and no allow rules.** "Only VLAN 1" is written as "VLAN 2-4094".

### 4.2 What a rule does, and what it doesn't

1. **The rows it matches are not evidence.** They make no suggestion, give no port its role, and attach no MAC for the suggestions.
2. **A port that its *Interface* or *Type* matches is no suggestion's end,** even when the evidence came from the other router: the far router's neighbor entry or MAC table.
3. **It doesn't change what PNMT has seen:**
   - the Network interfaces tab and the connections' confirmations use every row (§4.4);
   - the scan's rows and snapshots stay;
   - **so a rule never makes a connection unconfirmed.**
4. **Suggestions follow at once.** After a rule is created, changed, turned on or off, or deleted, an analysis runs a few seconds later (`NetworkAnalysis.RequestSoon`), as after an assignment. Open suggestions without evidence go, as today (map plan §5.4). Accepted and dismissed ones stay, and connections never change.

### 4.3 Examples

| Rule | On | Conditions | Why |
|---|---|---|---|
| Guests | All routers | VLAN `99` | Guests' devices are no part of the topology |
| Management VLAN | All routers | VLAN `4000` | It reaches every router through every switch, so each uplink shows every router's MAC |
| Mirror port | office-gw | Interface `ether10` | Port mirroring shows every MAC of the network |
| Second path through the switch (§3.7) | office-gw | VLAN `20`, Interface `ether2` | core-gw's MAC arrives there through a switch |
| Dial-ins | All routers | Type `l2tp-in`, `sstp-in`, `ovpn-in` | One suggestion per dial-in user |
| The old phone system | Location *Main building* | Evidence CDP | Its CDP names ports that don't exist |
| Lab router | lab-chr | (none) | Its tables are test data |
| Test VMs | All routers | MAC prefix `00:50:56` | VMs that keep moving between hypervisors |

### 4.4 In the analysis

- **`TopologyInput.Rules`**: a pure list of `TopologyRule` (new, `PNMTD.Lib/Network/TopologyRules.cs`):
  - the router IDs, or all;
  - the pattern, the types, the VLAN ranges, the evidence and the MAC prefix;
  - `Matches(observation, port)` and `MatchesEnd(router, port)`.

  `NetworkAnalysis.LoadAsync` turns *a location* into its routers: a router's location is its host's, with the sub-locations (`LocationTree.SelfAndDescendants`). The analyser never sees locations.
- **Two runs when a rule is enabled:**

  | Run | Input | Gives |
  |---|---|---|
  | **Without the rules** | everything stored | the network interfaces, the connections' checks (§5.5 of the map plan) |
  | **With the rules** | the matching rows left out, and the matching ports no end | the port roles, the suggestions, and each rule's hits |

  Without enabled rules, there is one run as today. Both runs apply §3. The analysis stays pure: the same input gives the same output.
- **Hits:** each rule's skipped rows and the routers they came from, at the last analysis. They are shown in the list, so a rule that matches nothing stands out.
- **The interfaces' tag:** a network interface whose attachment row a rule matches gets that rule's ID. The UI then explains why a host there has no suggestion.

### 4.5 Web UI

**A tab *Skip rules*,** after *Suggestions* (`?tab=rules`). Everyone with View sees it, and only Manage changes it (question 1).

- **The table:**
  - name;
  - *on*: all, a location, or a router;
  - the conditions as chips (`VLAN 99`, `vlan*`, `CDP`);
  - enabled, as a toggle;
  - "Last analysis: 1,240 rows on 3 routers";
  - changed by and when.

  **Actions:** *Add rule*, *Edit*, *Delete* with a confirmation. Turning a rule off is the safe alternative to deleting it.
- **The form:**
  - the fields of §4.1, with the location tree and the router picker as on the Scans tab;
  - the types offered from the types the scans have seen;
  - VLANs as text, checked as typed.
- **The preview, below the form.** Debounced, from the stored scans: "This rule removes 9 open suggestions" with their ends, "brings back 2" when a rule is loosened or turned off, and "3 network interfaces are on what it skips". Save stays possible when it removes nothing: a rule can be meant for the next scans.
- **From a suggestion,** in the Suggestions tab (Manage): ***Skip like this…*** opens the form prefilled from the evidence's data (§3.4):
  - the router;
  - the interface as printed, such as `vlan20`;
  - the VLAN and the evidence.

  The person narrows or widens it, and the preview shows the effect.
- **The Network interfaces tab:** a tag *skipped*, with the rule's name on hover.
- **The Connections tab:**
  - an end on a VLAN or a bridge gets a tag "VLAN 10 on ether1", with a filter *On a virtual port*;
  - this helps clean up connections that were accepted on VLANs (§5).

### 4.6 API, MCP, audit

| Method | Path | Right | |
|---|---|---|---|
| GET | `/api/v1/network-map/skip-rules` | View | All rules, with their hits. Not paged: a site has a handful |
| POST | `/api/v1/network-map/skip-rules` | Manage | 400 for an invalid pattern, VLAN or prefix, or for no condition outside *one router* |
| PUT, DELETE | `/api/v1/network-map/skip-rules/{id}` | Manage | |
| POST | `/api/v1/network-map/skip-rules/preview` | Manage | The draft and, when editing, the ID it replaces. It answers with the suggestions that go and come, and the interfaces it touches. It runs the analysis in memory and writes nothing |

- **Audit:** `NetworkSkipRuleCreated`, `…Updated`, `…Deleted`. The entity is `[Audited]`; the hits are `[AuditIgnore]`. `EndpointAuditTests` covers the four endpoints.
- **MCP:**
  - `network_summary` lists the enabled rules by name, so an AI can answer "why isn't srv-x suggested?";
  - `network_interfaces_search` names the rule that skips an interface's attachment;
  - **no write tool:** *Manage* stays in the UI, as everywhere (map plan §10.2).
- **CLI:** `pnmtd network analyse` also prints the rules' hits.

### 4.7 Data model

**`network_skip_rules`**:

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `name` | text (100) | |
| `enabled` | bool | true |
| `scope` | smallint | `All`, `Location`, `Router` |
| `location_id`, `sensor_id` | uuid null | **Cascade**: the rule goes with its location or router, as the router's connections do. A rule widened to all routers by `SET NULL` would be worse |
| `interface_pattern` | text null (100) | |
| `interface_types` | text[] null | |
| `vlans` | text null (200) | As typed, checked and parsed |
| `evidence` | smallint null | Flags |
| `mac_prefix` | text null (17) | Normalised: upper case with `:` |
| `hits`, `hit_routers`, `hits_at` | int, int, timestamptz null | Written by the analysis, `[AuditIgnore]` |
| `created_at`, `created_by`, `updated_at`, `updated_by` | | |

**`network_interfaces.skipped_rule_id`**: uuid null, `SET NULL` when the rule is deleted. Written by the analysis (§4.4).

---

## 5. What changes for existing data

- **Suggestion keys change** for every suggestion that had a VLAN end, at the first analysis after the update:
  - the open VLAN suggestions go, and one per cable comes;
  - accepted ones keep their connections;
  - dismissed VLAN keys stay dismissed, never come back, and go with the retention.
- **A dismissal doesn't carry over** to the new physical suggestion, and this is deliberate: the VLAN ones were noise, but the new one is the real cable.
- **Connections made on VLAN ends** are covered by their resolved ends. They are confirmed by the physical evidence, and no suggestion repeats them. If ten were accepted in §2.1's case, the map shows ten lines until a person deletes nine. The filter *On a virtual port* finds them (§4.5).
- **The evidence's sentences change.** The JSON only gains fields, so the MCP tools and the contract keep their shape.
- **The map plan:** its §3.3 rows *VLAN* and *Bridge* become true, and §5.3 gains §3.3 and §3.7 of this plan. The map plan gets a pointer to this plan in both places.
- **No rescan is needed** (§3.1).

---

## 6. Testing

- **`TopologyAnalyzerTests`, new scenarios:**
  - **§2.1's world:** nine VLANs on one cable, neighbors in both directions, gives **exactly one** suggestion, `ether1 ↔ sfp-sfpplus1`, High, with the type. Its evidence names the nine VLANs in one sentence;
  - the same with the VLANs on a vlan-filtering bridge, in each neighbor shape of §2.2 (`vlan10`; `ether1,bridge`; far port `vlan10`, `bridge`, `bridge/ether1`);
  - one bridge per VLAN: one router ↔ router per physical port, and a host behind it attached and suggested;
  - QinQ, and a VLAN on a bond;
  - a server with `eno1`, `eno1.10` and `vmbr0` on one MAC: one suggestion with `eno1` (today none). A bond's members: the bond. Two hosts with one MAC: still none;
  - two vNICs of one host on one router port: one suggestion;
  - **two real cables stay two;** two WireGuard tunnels stay two;
  - a VLAN that seems to cross from `ether1` to the far `ether2` is folded into the stronger one and named in its evidence;
  - a second path by MAC table only: Low (§3.7);
  - VRRP's MAC learned on a port makes no link;
  - a connection on `vlan10` is confirmed by the evidence on `ether1`, and no suggestion repeats it;
  - every existing scenario keeps its result.
- **Rules (`TopologyRules` and the analyser):**
  - each condition alone, and together;
  - the scopes, with a location's sub-locations resolved in `LoadAsync` (`NetworkMapTests`);
  - a far port that a rule skips is no end;
  - a neighbor seen by MNDP and LLDP, with MNDP skipped, still counts;
  - **a rule never unconfirms a connection,** and the interfaces still list what it skips, tagged;
  - a disabled rule does nothing;
  - glob, VLAN ranges and MAC prefixes parse, and invalid ones are refused;
  - the same input gives the same output;
  - **the 500-router test with three rules** stays within its bound.
- **API (`NetworkMapTests`):**
  - View reads; Edit gets 403 on writes and on the preview; Manage writes;
  - the preview writes nothing;
  - deleting a router or location deletes its rules;
  - `EndpointAuditTests`, `AuditLogTests`, the contract tests' OpenAPI.
- **MCP (`NetworkMcpTests`):** `network_summary` names the enabled rules; `network_interfaces_search` names the skipping rule; there is still no write tool for rules.
- **Parser (`NetworkPureTests`):** V0's VLAN fixtures parse, and their counts are as recorded.
- **Web UI:**
  - vitest for the form's checks (pattern, VLANs, prefix) and the chips;
  - **e2e:** a suggestion's *Skip like this…* opens the form prefilled, the preview counts it, saving removes it, and turning the rule off brings it back;
  - the tab in `pages.spec.ts`.

---

## 7. Risks

| Risk | What helps |
|---|---|
| **Folding hides a real second cable** | Only suggestions that share a port are folded, so two cables on two ports stay two. What was folded is named in the evidence. There is a test for parallel cables |
| **RouterOS names neighbors on VLANs differently** than assumed | V0 records it on two CHRs. `Wire` and the tests cover every shape in §2.2 |
| **A VLAN on a bridge without its member** gives an end without a port. That is less specific than today's `vlan10`, but today's port is the wrong one | It folds into the physical suggestion as soon as any row names the port (§3.3 step 6) |
| **A rule that is too broad** hides everything | The preview before saving. *All routers* needs a condition. The hits in the list. Turning off instead of deleting |
| **"Why is X not suggested?"** gets harder to answer | The interfaces' tag, the rule's hits, and the rules in `network_summary` |
| **Two runs** double the analysis' time | Only while a rule is enabled. The 500-router test runs with rules |

---

## 8. Considered and left out

- **A cap per device pair** ("at most 2 suggestions between two devices"). It is arbitrary: it would hide real cables and keep wrong ones, depending only on the order.
- **Dropping VLAN evidence altogether.** Sometimes a VLAN is all there is: neighbor discovery may run only on the VLAN interfaces. §3 keeps it as evidence for the physical port.
- **Rules per device pair** ("never between A and B"). With §3, the keys are stable per cable, so a dismissal keeps holding. An *Interface* rule covers a port.
- **Rules on the servers' adapters** (`docker*`, `vEthernet*`). §3.3 and §3.5 already make one suggestion per cable, and a MAC prefix covers the rest. They are a candidate for later.
- **The direct neighbor by LLDP.** A standards-compliant switch doesn't forward LLDP, though some unmanaged ones do. So an LLDP neighbor X on a port, where the MNDP or MAC table evidence points at router B, usually means B is behind X. That would be strong evidence for §3.7, but it needs real switches in the lab, which the map plan's N0 still lacks. It is for later.
- **Rules that also hide devices from the Network interfaces tab** (a guest VLAN, for privacy). *Ignore* does that per MAC, and the interfaces need *Details*. Mixing both effects into one rule would make "why is X missing?" harder. A second effect, *hide*, could come later.
- **Rules over MCP.** *Manage* stays in the UI.

---

## 9. Questions for the review (decided 2026-11-26: as recommended; released together in 1.29.0)

| # | Question | Recommendation |
|---|---|---|
| 1 | Who may change the rules? | **Manage.** A rule acts on every router's suggestions, as the schedule and the settings do. The alternative is *Edit*, since dismissing and ignoring are Edit, but then a rule over all routers could be made in passing |
| 2 | A tab of its own, or a section of the Suggestions tab? | **A tab, *Skip rules*.** It is found and linked (`?tab=rules`), and it has room for the preview. *Skip like this…* leads there from the suggestions |
| 3 | Do rules also hide from the Network interfaces tab? | **No** (§4.2 point 3, §8). The tab shows what the routers see, and a tag says which rule skips it |
| 4 | Do confirmations ignore the rules? | **Yes** (§4.4). A rule changes what PNMT proposes, never the state of a connection a person made |
| 5 | §3.7: Low, or no suggestion? | **Low.** It stays visible, and the confidence filter hides it. A VLAN rule removes it for good |
| 6 | The release | **§3 (V0, V1) first, as 1.26.1**: it fixes what the map plan's §3.3 promised, and needs no migration. **The rules (V2–V4) with the next minor** after the responsibilities' 1.27.0, or with 1.27.0 if both are ready together |

---

## 10. Phases

| Phase | What | Done when | Estimate |
|---|---|---|---|
| **V0** | **Lab with VLANs.** Two CHRs joined by a second NIC on a shared QEMU socket network (`tools/chr-lab.sh pair <a> <b>`). Configured with VLANs on `ether2`, on a vlan-filtering bridge, one bridge per VLAN, and a VRRP pair. Neighbors, MAC table, ARP, interfaces and bridges recorded into `PNMTD.Tests/Fixtures/Network/vlans/` | The shapes of §2.2 are known and noted in `NetworkScanScript`'s remarks; the fixtures parse | 0.5–1 d |
| **V1** | **§3 in the analyser:** `Wire`, its use in every rule, one cable per port, the joined evidence, the servers' shared MACs with `Kind`, VRRP, second paths as Low, the resolved coverage. The parser only if V0 asks for it | §6's scenarios pass, and the existing ones are unchanged. **By hand:** the dev seed with VLANs added shows one suggestion per cable | 2 d |
| **V2** | **Rules, server side:** the entity and migration, `TopologyRules`, the two runs, the hits and the interfaces' tag, the service, the endpoints with the preview, audit, `network_summary` and `network_interfaces_search`, the CLI line | §6's rule, API and MCP tests pass | 2 d |
| **V3** | **Rules, web UI:** the tab, the form with the preview, *Skip like this…*, the interfaces' tag, the Connections tab's tag and filter | Vitest and the e2e test of §6 | 2 d |
| **V4** | **Docs:** `NetworkMapDocs.vue` (ends on the wire, one cable per port, skip rules), the map plan's pointers, the release notes, this plan's *As built* | `run-tests.sh` green | 0.5 d |

**Total about 7.5 days.** V1 alone fixes §2.1, and can ship before V2.

---

## 11. As built (2026-11-26)

**V0, the lab** (two CHRs 7.25.1, recorded in `PNMTD.Tests/Fixtures/Network/vlans/`, with a README):

- **`tools/chr-lab.sh pair <a> <b>`** boots two CHRs whose `ether2` is one cable: a QEMU UDP socket between their containers on the network `pnmtd-chr-link`. In a pair, each router's MACs come from its name, since QEMU gives every VM the same ones.
- **§2.1 is what RouterOS does.** Neighbor discovery answers on every VLAN interface: one entry each, with the VLAN in `interface` and `interface-name`, by MNDP, LLDP and CDP.
- **The shapes of §2.2:**
  - through a bridge port, `interface` is `["ether2","bridge1"]` and `interface-name` the far port;
  - through a VLAN on a vlan-filtering bridge, only `["vlan10"]`, without LLDP;
  - through a VLAN that is a bridge's port, `["vlan10","br10"]`, and `interface-name` is **`br10/vlan10`**;
  - a vlan-filtering bridge's MAC table names the member with `vid`; one bridge per VLAN names the VLAN interface as `on-interface`.

  `Wire` handles each one, and `NetworkPureTests` analyses each pair: one suggestion, `ether2 ↔ ether2`.
- **Found on the way, and fixed: `/ip neighbor print as-value` without a proplist leaves `interface-name`, `discovered-by` and `platform` out on 7.25.1.** The scan never had the far port or the protocol of a neighbor. Its script now names its columns. 7.13 accepts every one, and refuses an unknown one, so the list is safe on both.
- **The lab's ARM64 CHRs no longer boot** on this machine ("Kernel panic: No working init found", with the old lab image and a rebuilt one alike). The pair ran as x86_64 under emulation (`CHR_ARCH=x86_64`). A VRRP pair was not recorded: §3.6 rests on RouterOS's documented MACs.

**Where the build differs from the plan:**

- **QinQ** keeps the VLAN as printed (the inner one), not both (§3.1).
- **The tag of an interface** names the oldest rule that matches its attachment row.
- **The preview** runs the analysis four times in memory (with and without the draft, each as the two runs of §4.4).
- **The dev seed** has two VLANs on the Basel ↔ Bern fibre. `DevSeedTests` checks that they give one suggestion, with both VLANs in its evidence.
- **vue-tsc gets 6 GiB of heap** (`typecheck` and `build` in `PNMTD.WebUI/package.json`). With the new endpoints' types and components, it ran out of node's default heap in about one run of three. HEAD never did in three runs.
- **One release, 1.29.0** (question 6): 1.27.0 and 1.28.0 shipped meanwhile, so §3 and the rules went out together. Release notes: [release-notes-1.29.md](release-notes-1.29.md).

**Tests:**

- `TopologyAnalyzerTests`: the scenarios of §6, the rules, and the 500 routers with and without rules;
- `TopologyRulesTests`, and `NetworkPureTests` on the lab's fixtures;
- `NetworkMapTests`: VLANs end to end through the scan, the rules with their rights, preview, hits, tags and audit, and the cascade;
- `NetworkMcpTests`, `EndpointAuditTests`, `DevSeedTests`;
- vitest `skipRules.test.ts`;
- e2e `network-map.spec.ts` (*Skip like this…*, the preview, off and on, delete), and the tab in `pages.spec.ts`.

---

## Appendix: files

| File | Change |
|---|---|
| `PNMTD.Lib/Network/TopologyAnalyzer.cs` | `Wire` and its use (§3.1, §3.2), one cable per port (§3.3), joined evidence (§3.4), owners per host (§3.5), VRRP (§3.6), second paths (§3.7); the rules and the two runs (§4.4) |
| `PNMTD.Lib/Network/TopologyRules.cs` | New: `TopologyRule`, the glob, VLAN ranges, MAC prefix, `Matches`, `MatchesEnd` |
| `PNMTD.Lib/Network/NetworkScanReading.cs` | Only if V0 shows that a neighbor's `interface` list doesn't start with the port |
| `PNMTD/Network/NetworkAnalysis.cs` | Loads the adapters' `Kind` and the rules (locations to routers); writes the hits and `skipped_rule_id` |
| `PNMTD/Models/Db/NetworkMapEntities.cs`, `PNMTD/Models/Enums/NetworkMapEnums.cs`, `PNMTD/Data/PnmtdDbContext.cs`, a migration | `network_skip_rules`, `network_interfaces.skipped_rule_id` (§4.7) |
| `PNMTD/Network/NetworkSkipRuleService.cs` | New: the list, the changes, the checks, the preview |
| `PNMTD/Api/V1/NetworkMapControllers.cs`, `PNMTD/Api/V1/Contracts/NetworkMap.cs` | §4.6 |
| `PNMTD/Audit/AuditEvents.cs` | The three events |
| `PNMTD/Tools/Read/NetworkReadTools.cs` | `network_summary`, `network_interfaces_search` |
| `PNMTD/Cli/NetworkCommand.cs`, `PNMTD/Cli/DevSeedNetwork.cs` | The hits in `analyse`; VLANs in the seed |
| `PNMTD.WebUI/src/views/network/SkipRulesTab.vue`, `SkipRuleDialog.vue` | New: §4.5 |
| `PNMTD.WebUI/src/views/network/NetworkMapView.vue`, `SuggestionsTab.vue`, `InterfacesTab.vue`, `ConnectionsTab.vue` | The tab; *Skip like this…*; the tag; the tag and filter |
| `PNMTD.WebUI/src/api/resources.ts`, `openapi.json`, `schema.d.ts` | The calls; the regenerated schema |
| `PNMTD.WebUI/src/views/docs/NetworkMapDocs.vue` | V4 |
| `tools/chr-lab.sh`, `tools/chr-lab/run.py` | `pair <a> <b>`: a second NIC on a shared socket network, and MACs of each router's own (V0) |
| `PNMTD.Lib/Network/NetworkScanScript.cs` | The neighbors' proplist, and the lab's findings in the remarks (V0) |
| `PNMTD.Tests/Fixtures/Network/vlans/`, `PNMTD.Tests/Network/*`, `PNMTD.WebUI/e2e/network-map.spec.ts` | §6 |
| `docs/network-map-plan.md` | Pointers from §3.3 and §5.3 to this plan |
