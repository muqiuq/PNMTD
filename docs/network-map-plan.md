# Plan: the network map: topology from the MikroTik routers

| | |
|---|---|
| Status | **Built (2026-11-20): N1 to N7 (§11.1). Released in 1.17.0.** N0 in part: on CHR, not yet on a RouterBOARD with SFP modules. Revision 4: routers' positions saved by hand ([network-map-positions-plan.md](network-map-positions-plan.md)), replacing decision 2. Revision 3: the map and the connections over MCP (§10.2). Revision 2: the decisions of the first review (§15) |
| Date | 2026-11-20 |
| Scope | A new menu entry, **Network map**. A background **scan** reads each MikroTik router as `pnmt`: its interfaces, the bridges' MAC tables, the neighbors, ARP and the DHCP leases. From these, PNMT **suggests connections** between routers, hosts and servers. A person turns them into **connections**, each with one **type** (medium and speed, such as *DAC 10G*), and the map draws them. The requirements are in docs/mikrotikmapfeature.md. |
| Goals | <ol><li>**A map** of the routers, and on request the hosts of chosen locations. Each location is a dashed circle. The map can be zoomed and panned, and a click opens a side panel.</li><li>**Connections**, physical or virtual, each with one type. They are kept in a paginated table that can be edited.</li><li>**Suggestions**, built from what the routers see, with their evidence. **Nothing becomes a connection without a person.**</li><li>**A scan** that is started by hand (all routers, a location or one router) or on a schedule, which is **off until someone turns it on in the web UI** (then every 14 days at 02:00 by default). It works **one router at a time**, and shows what it is doing and how many routers are left.</li><li>**Network interfaces:** every MAC address the routers see. Each is **mapped automatically by MAC** to a Windows or Linux server (and to the routers' own ports), or assigned to a host by hand. **PNMT discovers network interfaces, not hosts:** hosts are only created by hand, and an interface without a host is not on the map.</li><li>**Over MCP:** the map and the connections, above all **how the routers are connected to each other and to the servers**, for an AI client with its user's rights: the connections, the path between two devices, a device's ports, the suggestions and the interfaces (§10.2).</li></ol> |
| Not in scope | Changing anything on a router: the scan only reads, as `pnmt`. SNMP, and switches or routers of other makers, except as they appear in a MikroTik's neighbor table. IPv6 neighbors, OSPF and BGP adjacencies (§14). A history of the topology, and diffs between scans. Saved positions of hosts (routers' positions: docs/network-map-positions-plan.md). Creating hosts from what the scan finds (decision 6). Connections that PNMT creates without a person. |
| References | docs/mikrotikmapfeature.md (the requirements). docs/mikrotik-plan.md: the `pnmt` user (§4.2), the session over SSH (§4.5), the poll and its schedule (§5.1–5.2), the data model (§9.2). docs/windows-sensor-plan.md §4.4 and docs/linux-sensor-plan.md §5.3: the servers' adapters. docs/audit-redmine-zabbix-plan.md §5.2: areas and levels. docs/menu-proposal.md: the menu's rules. docs/mcp-plan.md: the tool catalog (§6), the ceiling (§5.3), the kinds of tools and the window for deleting (§7). The RouterOS manual on `/interface`, `/interface ethernet`, `/interface bridge`, `/ip neighbor`, `/ip arp`, `/ip dhcp-server lease` and `/interface wifi`. |

---

## 1. Today

### 1.1 What the routers give PNMT

- **The poll** (`RouterPoll.Script`, every Interval) reads the version, resources, RouterBOARD, identity, the `pnmt` group, SSH keys and service, PNMT's own WireGuard interface, peer and firewall rules, and, for the access watch, the account log, users, groups and active sessions.
  - **It reads no interfaces, bridges, MAC tables, neighbors, ARP or leases.**
- **The `pnmt` user has the `ssh` and `read` policies.** Every table this plan needs is a `print` or `monitor`, which `read` allows. N0 confirms this for each table (§11).
- **A session is reused, not reinvented:**
  - `RouterPollService.TargetAsync` gives the target (direct, or through the gateway's SOCKS);
  - `PnmtCredentialsAsync` gives PNMTD's keys;
  - `Connector.ConnectAsync` opens the session, with the pinned host key;
  - `LockAsync(sensorId)` holds the router against polls and jobs.
- **The output limits are tight:** `Ssh.MaxOutputBytes` is 1 MiB and `CommandTimeoutSeconds` is 60 s. A core switch's MAC table can come close to that (§4.4).
- **The lab is CHR** (`tools/chr-lab.sh`, 7.13 and 7.25.1). It has no SFP cages, no switch chip and no Wi-Fi. **Media and speeds need a real RouterBOARD** (§11).

### 1.2 Hosts, locations and servers

- **A router is a sensor of type `MIKROTIK`** on a host. The host is the router itself, or a site with several routers. Its location is `router.Sensor.Parent.LocationId`.
- **Hosts have no IP or MAC.** A host's location is `hosts.location_id`. **Locations are a tree**, at most 6 levels deep. `GET /api/v1/hosts?locationId=` already includes the sub-locations (`HostService`, through `LocationTree.SelfAndDescendants`).
- **A server is a `WINDOWS` or `LINUX` sensor with an agent.**
  - Its adapters arrive in the `network` section: `AdapterInfo` with `Name`, `Kind`, `Mac` (upper case, `AA:BB:…`), `SpeedBps`, `Addresses`, `VlanId` and `Virtual`.
  - The section is stored **as gzip JSON in `agent_sections.content`**. No column can be queried by MAC.
  - The server page shows it (`AgentNetworkTab.vue`).

### 1.3 The web UI

- **No graph library.** The charts are hand-written SVG (`SampleChart.vue`, `UplinkView.vue`). Mermaid only renders markdown diagrams. There is no pan or zoom code.
- **The parts that exist:**
  - `TabNav.vue`, with the tab in `?tab=`, as `MikrotikView.vue` does;
  - `useServerTable` with `ListTable.vue`, for paged tables (`RoutersTab.vue` is the example);
  - PrimeVue `Drawer` on the right, for side panels (`EventsView.vue`);
  - progress by vue-query polling: 3 s while something runs, 30 s otherwise (`JobsTab.vue`).
- **The menu** is `src/layouts/navigation.ts`. Its test checks the groups, and that no icon is used twice.

### 1.4 Rights

The areas are fixed in code: the `Area` enum and `RightsCatalog` in `PNMTD/Auth/Rights/Rights.cs`, and `levelOrder` in `src/api/rights.ts`. Recent features each got an area of their own (Certificates, Connectors). So does this one (§9.1).

---

## 2. Requirements

From docs/mikrotikmapfeature.md, numbered for the rest of the plan:

| # | Requirement | Where |
|---|---|---|
| R1 | The topology from DHCP, the MAC tables, neighbor discovery, ARP, and more | §4 |
| R2 | Physical and virtual connections. Each has **one type** that holds the medium and the speed: BASE-T 100M, 1G, 10G; DAC 1G, 10G; Fiber MMF 1G, and so on | §3 |
| R3 | The type is read from the interface information | §3.3 |
| R4 | Suggestions, which a person turns into connections. The map shows the connections | §5, §6 |
| R5 | A menu entry of its own. The first page is the graph: circles with the host names, and the connections. **The hosts of a location sit together, inside a dashed circle** | §8.2 |
| R6 | A click on a host opens a side panel, and from there the details | §8.3 |
| R7 | Only routers by default. Filters add the hosts of one or more locations, among other options. Zoom and pan | §8.2 |
| R8 | Tab 2: the connections, paginated and editable | §8.4 |
| R9 | A tab of suggestions, which can be made connections | §8.5 |
| R10 | A background scan, by hand or on a schedule (every 14 days by default; the schedule starts off, §15), **one router at a time**. It shows what it does and how many routers are left, in the tab where it is started. Its scope is all routers, a location, or one router | §7, §8.7 |
| R11 | A tab of network interfaces, taken from the routers' information. They are mapped to Windows and Linux servers by MAC address | §6, §8.6 |
| R12 | **The map and the connections over MCP**, above all how the routers are connected to each other and to the servers (added after the review) | §10.2 |

---

## 3. Connections and their types

### 3.1 A connection

- **It joins two ends.** Each end is a **router** (a `MIKROTIK` sensor) or a **host**, optionally with an **interface**:
  - a router's interface is the port's name (`sfp-sfpplus1`);
  - a host's interface is free text, offered from the server's adapters (`Ethernet0`, `eno1`).
- **Both ends are known to PNMT:** routers and hosts. **Hosts are only created by hand** (decision 6).
  - A switch, an access point or a printer that PNMT doesn't know is only a network interface (§6) until then: it isn't on the map, and it gets no suggestions.
  - Once a person creates the host and assigns the interface to it, the next analysis suggests its connections.
- **It has one type** (§3.2), and from the type follows whether it is **physical or virtual**.
- **Notes**, and who created and changed it and when.
- **Its scan evidence:** when a scan last saw it, and since when no scan has (§5.5).
- **A pair of ends exists once.** A connection from a router's port to itself is refused.

### 3.2 The types

**A fixed list in code** (`PNMTD.Lib/Network/ConnectionTypes.cs`). A type is stored by its code. A new type is a code change, with no migration.

| Medium | Codes (label) | Physical |
|---|---|---|
| BASE-T (copper, RJ45) | `baset-10m` … `baset-10g`: 10M, 100M, 1G, 2.5G, 5G, 10G ("BASE-T 1G") | yes |
| DAC (direct attach copper) | `dac-1g`, `dac-10g`, `dac-25g`, `dac-40g`, `dac-100g` ("DAC 10G") | yes |
| AOC (active optical cable) | `aoc-10g`, `aoc-25g`, `aoc-40g`, `aoc-100g` | yes |
| Fiber, multi-mode | `mmf-1g`, `mmf-10g`, `mmf-25g`, `mmf-40g`, `mmf-100g` ("Fiber MMF 10G") | yes |
| Fiber, single-mode | `smf-1g`, `smf-10g`, `smf-25g`, `smf-40g`, `smf-100g` ("Fiber SMF 10G") | yes |
| Wi-Fi | `wifi` ("Wi-Fi") | yes |
| WireGuard | `wireguard` | no |
| Tunnel | `tunnel` (EoIP, GRE, IPIP, VXLAN, or any other VPN) | no |
| Virtual machine | `vm` (a guest on its hypervisor's virtual switch) | no |
| Other | `other-physical`, `other-virtual` | yes, no |

- **The API serves the list** (`GET /api/v1/network-map/connection-types`): code, label, medium, speed in Mbit/s (null for the virtual types) and physical. The UI sorts and groups its select by it.
- **A code that is no longer in the list** still shows, as its code, and can be changed.

### 3.3 The type from the interface (R3)

**`ConnectionTypeDetector`** (pure, in `PNMTD.Lib/Network`) proposes a type for a router port from what the scan read (§4.1). The person can always change it.

| What the port is | Read from *(N0)* | Proposed |
|---|---|---|
| RJ45 port with link | `/interface ethernet monitor`: `rate` (10Mbps … 10Gbps) | `baset-<rate>` |
| SFP cage, copper pigtail | `sfp-module-present`, `sfp-connector-type` = copper pigtail or "no separable connector", a copper link length, no wavelength | `dac-<rate>` |
| SFP cage, separable optics | `sfp-connector-type` LC, SC or MPO, and `sfp-wavelength` 850 nm or an OM link length | `mmf-<rate>` |
| | `sfp-wavelength` 1270–1610 nm, or an SM link length | `smf-<rate>` |
| SFP cage, fixed optics | No separable connector, but a wavelength | `aoc-<rate>` |
| SFP module with RJ45 | `sfp-connector-type` RJ45 | `baset-<rate>` |
| Combo port | Whichever side is active *(N0)* | As above |
| `wifi`, `wlan` interface, or a Wi-Fi client | The interface type, the registration table | `wifi` |
| `wireguard` | The interface type | `wireguard` |
| `eoip`, `gre`, `ipip`, `vxlan` | The interface type | `tunnel` |
| VLAN | **Its parent's type.** A VLAN is no connection of its own: the connection is on the physical port, and the evidence names the VLAN. Built for every rule, not only ARP, in [network-suggestion-rules-plan.md](network-suggestion-rules-plan.md) §3 | The parent's |
| Bridge | **The member port** where the MAC was learned (`on-interface`) | The member's |
| Bond | **Each member is its own connection** in this plan. The evidence says "member of bond1" | The member's |
| No link, no module, or unclear | | none: the person chooses |

- **Two ends that disagree** (one router says 10G, the other 1G) give a suggestion whose evidence says so. The proposal is the lower speed.
- **A server's side** helps: an adapter's `SpeedBps` that matches the router port's rate is part of the evidence.

---

## 4. What the scan reads (R1)

### 4.1 The tables

One SSH session per router, as `pnmt`. Each table is **one exec of a one-line script** that prints JSON, as the poll does (`:serialize to=json` of `print as-value`), and each has its own timeout and output limit. **A failed table doesn't fail the router.** Its error is kept, and the previous scan's rows of that table stay (§4.3).

| # | Step (shown in the UI) | RouterOS | What it gives | Used for |
|---|---|---|---|---|
| 1 | Interfaces | `/interface print` | Name, default name, type, MAC, MTU, running, disabled, comment | The router's ports and their MACs |
| 2 | Ethernet and SFP | `/interface ethernet print`, then `monitor <each> once` in a `:foreach`, each inside `:do … on-error` *(N0: `once as-value` as `read`, and its time on 48 ports)* | Rate, duplex, link, auto-negotiation; SFP module, connector, wavelength, vendor, part number, link lengths | The type (§3.3) |
| 3 | Bridges and VLANs | `/interface bridge port print`, `/interface bridge vlan print`, `/interface vlan print`, `/interface bonding print` | Which port is in which bridge; the VLAN IDs and parents; bond members | Resolving a bridge or VLAN to its physical port |
| 4 | MAC table | `/interface bridge host print where !local` *(N0: the flag's name, and entries learned in hardware)* | MAC, `on-interface` (the port), bridge, VLAN | **Which MACs are behind which port** |
| 5 | Neighbors | `/ip neighbor print`, `/ip neighbor discovery-settings print` | On each port: identity, MAC, address, platform, board, version, **the far end's port** (`interface-name`), discovered by MNDP, CDP or LLDP | **Router to router**, and switches that speak LLDP or CDP. The discovery settings explain a port with no neighbors |
| 6 | ARP | `/ip arp print` | IP, MAC, the L3 interface, complete or not | IPs for MACs; devices on routed ports |
| 7 | DHCP leases | `/ip dhcp-server print`, `/ip dhcp-server lease print` | IP, MAC, host name, client ID, server, status, last seen | IPs and **host names** for MACs |
| 8 | Addresses | `/ip address print` | The router's networks per interface | Two routers in one network; tunnel ends |
| 9 | Tunnels | `/interface wireguard print proplist=name,public-key,listen-port`, `/interface wireguard peers print proplist=interface,public-key,endpoint-address,current-endpoint-address,allowed-address`, `/interface eoip print`, `gre`, `ipip`, `vxlan` | Peers' public keys, remote addresses | **Virtual connections** between routers. PNMT's own tunnel (comment `pnmt:<tag>`) is left out |
| 10 | Wi-Fi | `/interface wifi registration-table print`, else `/interface wireless registration-table print`, each parsed at run time like `/system routerboard` | Client MAC, interface, signal, rates | `wifi` connections |

- **Secrets stay out.** The WireGuard prints name their properties (no `private-key`, no `preshared-key`), as the poll does (`RouterPoll`, §5.1 of docs/mikrotik-plan.md). The other tables hold none.
- **A menu that doesn't exist** (Wi-Fi on a CHR, `wireless` on a router with `wifi`) is parsed with `:parse` inside `:do … on-error`. Its absence is not an error.
- **The proplist rule of M0 holds:** a proplist may name only properties that every version has. The prints above without a proplist take every column. The parser reads what is there.

### 4.2 The parser

**`NetworkScanScript`** (the scripts) and **`NetworkScanReading`** (the parsed result), in `PNMTD.Lib/Network`. Both are pure and tested with recorded output from 7.13, 7.25.1 and a RouterBOARD (`PNMTD.Tests/Fixtures/Network/<version>/*.json`).

- **MACs are normalised** to upper case with colons (`Mac.Normalize`), as the agents send them.
- **A MAC is marked random** when it is locally administered (bit `0x02` of the first octet), as phones' private addresses are. Random MACs are kept, but never matched automatically, and they make no suggestions of their own (decision 5).
- **Numbers that `:serialize` turns into floats** (`22.000000`) are read with `RouterOsValues`, as in the poll.

### 4.3 What is stored

- **The latest reading of each router, raw**, as gzip in `network_router_snapshots`. The analysis (§5) can then run again after an analyser change, without a new scan.
- **The parsed rows**, for the API and the analysis:
  - `network_router_ports`, one row per interface;
  - `network_observations`, one row per MAC table entry, ARP entry, lease, neighbor, tunnel peer or Wi-Fi client.
- **Replaced per table.** When a table succeeds, that router's rows of that table are replaced. When it fails, the old ones stay, with their older `seen_at`.

### 4.4 Size and time

- **The limits are the scan's own:** `NetworkMap:Scan:MaxOutputBytes` (default 8 MiB) and `NetworkMap:Scan:TableTimeoutSeconds` (default 120), not the poll's 1 MiB and 60 s.
  - A table over the limit fails as "Output over 8 MiB".
  - The other tables of that router still run.
- **A core switch with 3,000 MACs** gives about 450 KB of JSON. N0 measures `:serialize` with 5,000 static bridge host entries on CHR. If it is slow or hits RouterOS's memory, that table falls back to `print terse` with a line parser.
- **A router takes seconds:** about ten execs. 500 routers one at a time take well under an hour.

---

## 5. Suggestions (R4)

### 5.1 The analysis

**`TopologyAnalyzer`** (pure, `PNMTD.Lib/Network`) takes:

- the latest ports and observations of every router;
- the routers and hosts, with their locations;
- the servers' MACs (§6.2);
- the manual assignments of interfaces;
- the connections, and the dismissed suggestions' keys.

It returns:

- the **suggestions**;
- the **matches** of the network interfaces (§6);
- each port's **role** (§5.2);
- each connection's **confirmation** (§5.5).

It runs **at the end of each scan run, and at most every 5 minutes during a long one** (`NetworkMap:Analysis:IntervalMinutes`). It runs on everything that is stored, not only on the routers just scanned. 500 routers with 200 MACs each are 100,000 rows, which is comfortable in memory.

### 5.2 Port roles

Before any MAC is placed, each router port gets a role:

| Role | When |
|---|---|
| **To a router** | A neighbor on the port is a router known to PNMT (by MAC, identity or serial number); or another known router's own MAC is learned on it; or a WireGuard or tunnel peer is a known router |
| **To a neighbor** | A neighbor that PNMT doesn't know: a switch, an AP, a router of another installation |
| **Edge** | Neither |
| **Virtual** | WireGuard, tunnels, VLANs and bridges themselves |

**A MAC learned on a port "to a router" is not attached there.** It is attached on the far router, or further on. This removes the uplinks, through which every MAC of a network is learned.

### 5.3 The rules

| Kind | Evidence | Confidence |
|---|---|---|
| **Router ↔ router, by neighbor discovery** | A sees B on port X, B's port Y (`interface-name`) | **High** when B also sees A on Y; **Medium** from one side only |
| **Router ↔ router, by WireGuard** | A's peer's public key is B's interface's public key | **High**. Type `wireguard` |
| **Router ↔ router, by tunnel** | A's EoIP, GRE, IPIP or VXLAN remote address is one of B's addresses | **Medium**. Type `tunnel` |
| **Router ↔ router, by network** | A and B have addresses in one network on X and Y, and A's ARP has B's MAC on X | **Medium**. It merges with the neighbor suggestion when both point at the same ports |
| **Router ↔ host** | A MAC of the host (a server's adapter, or an assigned interface, §6) is learned on edge port X of A | **High** when the port has only this host's MACs; **Medium** when it shares the port with others (an unmanaged switch, or a hypervisor's guests) |
| **Router ↔ host, routed port** | An ARP entry on a physical port X (not a bridge) has a MAC of the host | **Medium** |
| **Router ↔ host, Wi-Fi** | A MAC of the host is in A's registration table | **High**. Type `wifi` |
| **Router ↔ host, by neighbor discovery** | A neighbor on port X whose MAC is assigned to the host, or is a server's adapter: "office-gw sees core-sw (CRS326, LLDP) on sfp-sfpplus1, its port sfp1" | **High**, with the far port from `interface-name` |
| **Behind a connected host** | A host's MAC is learned on edge port X of A, and X already has a connection to another host Y: a switch or a hypervisor that a person added | **Medium**: host ↔ Y, without a port, **instead of** A.X ↔ host |
| **Differs from a connection** | A connection says A.X ↔ B, the scan sees A.X ↔ C | Its confidence. Accepting **changes the connection** |

- **Every end of a suggestion is a router or a host.** A neighbor or a MAC that matches no host gives no suggestion and no node. It is a network interface with its data (§6), until a person creates its host and assigns it (decision 6).
- **One suggestion per pair of ends.** Its key is the two ends, normalised. Evidence from several rules merges into one suggestion, and the highest confidence wins.
- **One suggestion per cable**, and **skip rules** that leave rows out: [network-suggestion-rules-plan.md](network-suggestion-rules-plan.md) §3, §4. Ends sit on the physical port; suggestions of one device pair that share a router port are one; a router-to-router link only the MAC table or ARP shows, between routers linked on other ports, is Low.
- **The evidence is sentences:** "office-gw sees branch-gw on ether2 by MNDP (its port ether1)", "srv-db's adapter eno1 (AA:BB:…) is the only MAC on ether7", "Both sides 10 Gbit/s". It is also stored as data, for the UI's icons.
- **The type proposed** comes from §3.3, from the router's side. For router to router, both sides are compared.
- **A MAC seen on edge ports of two routers** (a device that moved, or a table that is out of date): the newer scan wins, with **Low** confidence and both in the evidence.
- **No suggestion** for random MACs, for a pair that already has a connection, or for a dismissed key.

### 5.4 Its states

| State | Meaning |
|---|---|
| **Open** | Shown in the tab. It goes away when an analysis no longer finds its evidence |
| **Accepted** | Became a connection (`connection_id`). Kept, for the history |
| **Dismissed** | **Stays dismissed** while the same key keeps coming back. *Restore* reopens it. Dismissed suggestions that no scan has seen for `retention_days` are removed |

### 5.5 Connections and the scan

- **A connection with evidence in the latest scans is confirmed:** `confirmed_at` is the analysis' time.
- **A connection that was confirmed, and is no longer seen** in the **last two** scans of its router (MAC tables age out, so a device that was off once is no warning), gets `unconfirmed_since`. The table and the map show it as a warning.
- **A connection that no scan has ever seen** (a host behind an unmanaged switch, a fibre to a building without a MikroTik) is shown as *manual*, without a warning.
- **Nothing is ever removed or changed by a scan.** Only the person decides.

---

## 6. Network interfaces (R11)

### 6.1 What one is

**One row per MAC** that any router sees, in `network_interfaces`:

- the IPs (ARP, leases) and the DHCP host name;
- for a neighbor: its identity, platform, board and the far port ("core-sw, CRS326, LLDP, its port sfp1"), which help a person tell what it is;
- **where it is attached:** the router, port and VLAN that §5.2 gives (the edge port), or "behind core-sw" or "behind a known router" when no edge port is found;
- its sources (MAC table, ARP, DHCP, neighbor, Wi-Fi);
- first and last seen;
- random or not;
- **its match** (§6.2).

The routers' own ports aren't rows: the side panel lists them (§8.3), and their MACs match them (§6.2).

### 6.2 The automatic match

| Match | How | Shown as |
|---|---|---|
| **A router's port** | The MAC is a port's MAC in `network_router_ports` | "office-gw · ether1" |
| **A server's adapter** | The MAC is in `agent_adapter_macs` (below) | "srv-db · eno1 (Linux)" |
| **Manual** | A person assigned the MAC to a host (decision 4) | "Printer 2nd floor (by Philipp)" |
| **Ambiguous** | More than one of the above: a cloned MAC, or a VM template | "2 matches", nothing applied |
| **None** | | "Unassigned" |

- **A manual assignment wins** over an automatic one, and stays until it is cleared.
- **A hint, not a match:** a DHCP host name that equals a host's name ("Possibly host *nas01*"). One click assigns it.
- **`agent_adapter_macs`:** the servers' MACs, as a table that can be queried.
  - **Written by `AgentSections.StoreAsync`** whenever a `network` section is stored: the server's rows are replaced from `NetworkSection.Adapters`.
  - Each row has the server, the MAC, and the adapter's name, kind, speed and `Virtual`.
  - **Filled once for the servers that exist**, by a custom migration (`ICustomMigration`) that reads the stored sections.
  - Deleted with the sensor.
  - **So a server that reports later matches at once**, without a scan.
- **Ignored interfaces** (a test device, a guest phone) stay in the table, but are filtered out by default, and make no suggestions.
- **Retention:** an interface that no router has seen for `retention_days` (default 90) is removed, unless it has a manual assignment.

---

## 7. The scan (R10)

### 7.1 A run and its routers

- **A run** has a scope (all routers, a location with its sub-locations, or one router), a trigger (by hand or the schedule), who started it, and when.
- **Its routers are fixed when it starts:** the enabled `MIKROTIK` sensors in scope, ordered by location, then name.
- **One router at a time, over all runs.**
  - **The worker takes the next router of the oldest run**, but runs of one router go first. So *Scan this router* doesn't wait behind a scan of 500.
  - Locations come before all routers.
- **A router is skipped, with the reason:**
  - when its mode is WireGuard only ("no login");
  - when it isn't `Ready` yet;
  - while PNMTD's uplink is down;
  - while the gateway is down;
  - when its lock isn't free within `NetworkMap:Scan:LockWaitSeconds` (default 120), because a job holds it ("busy: an update runs").
- **Starting the same scope again** while it is queued or running gives 409, and links to the run.

### 7.2 The worker

**`NetworkScanner`**, a `BackgroundService` in `PNMTD/Network/`, in the style of `JobRunner`:

- **It wakes on a new run, and every 10 seconds.**
- **For each router**, it:
  1. takes `RouterPollService.LockAsync(sensorId)`, so the router's polls pause while it runs;
  2. opens one session with `TargetAsync` and `PnmtCredentialsAsync`;
  3. runs the tables of §4.1 in order. **Before each one, it writes the step to the run**, which is what the UI shows;
  4. stores per table;
  5. writes the router's result: OK, failed (with the error) or skipped, its counts and its duration.
- **After a restart, a router that was running is queued again.** A scan only reads, so running it twice does no harm. Jobs instead fail their orphans (§7.5 of docs/mikrotik-plan.md).
- **Cancel** closes the session and cancels the rest of the run. The router that was running keeps the tables it finished.
- **It runs with `Mikrotik:Enabled` and `NetworkMap:Enabled`**, not under `BackgroundTasks:Enabled`, like `JobRunner`. The integration tests can therefore run it (`RunHereAsync`).

### 7.3 The schedule

- **Off at first** (§15): the first scan is started by hand, and the schedule is turned on in the Scans tab (Manage).
- **`network_map_settings.scan_schedule_enabled`**: default **false**.
- **`scan_interval_days`**: default **14**. It is kept while the schedule is off, so turning the schedule on gives 14 days.
- **`scan_hour`**: the hour of day in the server's time zone when a scheduled run may start. The default is 2 (02:00), and it is set in the Scans tab (decision 3).
- **A scheduled run of all routers starts** when all of these hold:
  - the schedule is on;
  - the last full run (scheduled or by hand) started at least the interval ago;
  - the hour has come.

  So a run by hand on Monday moves the next scheduled one.
- **Without a full run, the schedule waits**, even when it is on. The Scans tab says "The schedule counts from the first full scan: start it by hand."
- **The check** runs in a small `NetworkScanSchedule` hosted service, under `BackgroundTasks:Enabled`, with `RunGuard`.
  - It writes `LAST_NETWORK_SCAN_SCHEDULE_RUN`, so a stopped scheduler shows on System health as a late task.
- **History:** runs and their routers are kept for 365 days (`NetworkMap:Scan:HistoryDays`).

---

## 8. Web UI

### 8.1 The page

- **Menu:** **Network map** (`pi pi-map`, unused so far), in the group *Monitoring*, after *Locations*, with the right `['NetworkMap', 'View']`. Update `navigation.test.ts`.
- **Route:** `/network-map`, tabs in `?tab=` with `TabNav`.

  | Tab | Key | Badge |
  |---|---|---|
  | **Map** | (none) | |
  | **Connections** | `connections` | the unconfirmed ones (§5.5) |
  | **Suggestions** | `suggestions` | the open ones |
  | **Scans** | `scans` | while a run is going |
  | **Network interfaces** | `interfaces` (Details) | |

- **While a run is going, a thin strip shows on every tab:** "Scanning *office-gw* (MAC table) · 12 of 40 · 27 left". It links to *Scans*.

### 8.2 Map (R5, R7)

```
┌ Filters ──────────────────────────────────────────────────────────────────────────────┐
│ Routers: [All ▾]  Hosts of: [Zürich ×] [Bern ×] [+]  [Connections ▾]  [Search…]     ⚙ │
├───────────────────────────────────────────────────────────────────┬───────────────────┤
│        ╭ ─ ─ Zürich ─ ─ ╮                 ╭ ─ ─ Bern ─ ─ ╮        │ office-gw         │
│      ╭                    ╮             ╭                  ╮      │ Router · Zürich   │
│     │   (●) office-gw ━━━━━━━━━━━━━━━━━━━━━ (●) bern-gw     │     │ ● OK · 7.25.1     │
│     │    ┃ ┃                │  DAC 10G  │       ┋           │     │ ───────────────── │
│     │  (○)srv-db (○)nas01   │           │    (○) bern-ap    │     │ Connections (3)   │
│      ╰                    ╯             ╰                  ╯      │ Ports (12)        │
│        ╰ ─ ─ ─ ─ ─ ─ ─ ─ ╯                ╰ ─ ─ ─ ─ ─ ─ ─ ╯       │ [Open router] …   │
│                                                   [＋] [－] [Fit] │                   │
└───────────────────────────────────────────────────────────────────┴───────────────────┘
```

- **Nodes are circles with the name below them:**
  - a router is larger, with a router glyph;
  - a host is smaller; a Windows or Linux server has its OS glyph;
  - the fill is the worst state of the sensors on it (OK, warning, error, unknown), in the design's state colours.
- **A host that carries a `MIKROTIK` sensor is drawn as its router(s)**, not once more as a host.
- **Only routers and hosts are nodes.** A network interface without a host is not drawn (decision 6). It is in its tab.
- **Edges are the connections:**
  - the width grows with the speed (≤100M, 1G, 2.5–10G, ≥25G);
  - a virtual connection is dashed, Wi-Fi dotted;
  - an unconfirmed connection is in the warning colour;
  - the type shows as a label on hover, and always when zoomed in.
- **Locations (R5):** each top-level location on the map is a **dashed circle** around its nodes, with its name on top and a light tint.
  - **Only top-level locations are circles** (decision 1). A host in *Zürich › Building A › Room 3* sits in the circle *Zürich*. There is no nesting, and no choice of level.
  - Nodes without a location are outside every circle.
- **Filters (R7), all in the URL so that a view can be shared:**
  - **Routers:** all (default), or those of chosen locations, or only those with a problem;
  - **Hosts of:** none (default), or one or more locations, with their sub-locations;
  - **Only hosts with a connection** (on): a location of 200 PCs without connections would only add clutter;
  - **Connections:** physical, virtual, or by medium;
  - **Show suggestions** (off): the open suggestions as thin grey dotted lines, to review them where they are;
  - **Search**: the chosen node is centred and its panel opened.
- **Edges to nodes that the filters hide** are left out. The node's panel says "+3 connections to hosts not shown".
- **Zoom and pan:** the wheel and pinch zoom, dragging the background pans, and `+`, `−` and *Fit* buttons. Dragging a node moves it until the page is left; **a router's position can be saved by hand** (decision 2, docs/network-map-positions-plan.md).
- **Focus:** `?focus=router:<id>` or `?focus=host:<id>` centres a node and opens its panel; `?focus=connection:<id>` opens a connection's panel. MCP's links use them (§10.2). The router's and host's pages link to it ("Show on the network map").
- **Empty map:** "No connections yet." With Manage, *Start a scan*, and *Review suggestions* when there are any.

**The layout** (`networkLayout.ts`, pure, with a vitest) works in two stages, so that the circles never overlap:

1. **Groups:** each group is one body, sized by its members. A force simulation places them, with collision and with pulls along the connections between groups.
2. **Members:** each group's nodes are laid out around its centre, with their own simulation. The circle is the smallest one that encloses them (`packEnclose`), plus padding.

- **The same data gives the same picture:** the input is sorted, and the random source is seeded.
- **The simulation runs to its end before the first frame** (no animation), behind a spinner. If 1,000 nodes take over 300 ms, it moves to a Web Worker (§13).

**Libraries:**

- **`d3-force` and `d3-hierarchy`** (ISC, with only `d3-dispatch`, `d3-quadtree` and `d3-timer` as dependencies), through `tools/guarddog.sh`.
- **Pan and zoom are our own** composable, `usePanZoom` (wheel, pointers, pinch, fit), with a vitest.
- **The drawing is Vue-rendered SVG**, as the charts are. The rejected alternatives are in §14.

### 8.3 The side panel (R6)

A `Drawer` on the right, **not modal**, so the map stays usable. A click on another node changes it.

| For | Shows | Links and actions |
|---|---|---|
| **A router** | Name, host, location path, state and message, model, RouterOS version, identity, last scan (and its result). **Connections**, each with the far end, the ports and the type. **Ports** from the scan: name, type, rate, link, MAC count, and the connection or suggestion on it. "23 MACs seen, 4 unassigned" | *Open router* (the sensor page), *Open host*, *Connections* / *Suggestions* / *Network interfaces* (the tab, filtered to this router), *Add connection* (Edit), *Scan this router* (Commands Run) |
| **A host** | Name, location, the sensors' states. For a server: OS, and its adapters with MAC and speed. Its interfaces as the routers see them (MAC, IP, router · port). Its connections | *Open host*, *Open server*, the filtered tabs, *Add connection* (Edit) |
| **A connection** (a click on an edge) | Both ends with ports, type, physical or virtual, notes, created and changed by, the scan evidence (confirmed when, or unconfirmed since) | *Edit*, *Delete* (Edit) |
| **A suggestion** (with *Show suggestions*) | Ends, proposed type, confidence, evidence | *Make connection*, *Dismiss* (Edit) |

### 8.4 Connections (R8)

- **`useServerTable` and `ListTable`**, with `syncUrl`.
  - **Columns:** end A (device · port), end B, type (a tag in the medium's colour), physical or virtual, locations, scan (✓ date, ⚠ unconfirmed since, *manual*), notes, changed.
  - **Filters:** location, router or host, type, physical or virtual, *only unconfirmed*. The search covers names, ports and notes.
- ***New connection*** (Edit) opens a dialog:
  - each end is a router or host from a search;
  - the port is a select of the router's scanned ports, or of the server's adapters, or free text;
  - the type is a select grouped by medium, with the detector's proposal preselected when a router port is chosen;
  - notes.
- **Edit and Delete** per row, with a confirmation for Delete. Both are audited.

### 8.5 Suggestions (R9)

- **Columns:** confidence (a High, Medium or Low tag), end A, end B, proposed type, the first evidence sentence ("+2 more"), first and last seen. The expansion shows all the evidence.
- **Filters:** state (open by default, dismissed, accepted), confidence, kind, router, location.
- **Actions (Edit):**
  - ***Make connection*** opens the dialog of §8.4, prefilled: the type, ports and notes can be changed before saving;
  - ***Accept*** takes the proposal as it is;
  - ***Dismiss***, and ***Restore*** in the dismissed filter;
  - **with a selection:** *Accept selected* and *Dismiss selected*.

### 8.6 Network interfaces (R11)

- **Columns:** MAC (with a *random* tag), IPs, DHCP host name, attached at (router · port · VLAN, or "behind core-sw"), sources (icons), the match (§6.2, with how it was made), first and last seen.
- **Filters:** *unassigned only*, router, location, source, VLAN, *seen in the last scan*, and *show ignored* (off). The search covers MAC (a prefix too), IP and host name.
- **Actions (Edit):** *Assign to host…*, *Clear assignment*, *Ignore*, and *Use hint* when a host name hint exists.
- **Details level only** (§9.1): DHCP host names and Wi-Fi clients often name people ("Philipps-iPhone").

### 8.7 Scans (R10)

- **Now:**
  - "Scanning *office-gw*: reading the MAC table (step 4 of 10)";
  - a progress bar: "12 of 40 routers · 1 failed · 2 skipped · 25 left";
  - "Started 10:02 by Philipp · all routers · about 6 min left" (from the average time per router so far);
  - *Cancel* (Manage).
- **Queue:** the runs waiting, each with *Cancel*.
- **Start:** *Scan all routers* and *Scan a location…* (the location tree) need Manage; *Scan a router…* needs Commands Run (§9.1).
- **Schedule (Manage):**
  - **on or off: off until someone turns it on** (§7.3);
  - the interval: a field like `BackupScheduleField`, in days: Weekly, **Every 14 days** (the default), Monthly, Custom;
  - the hour, in the server's time zone: **02:00** by default;
  - the next scheduled run, and the last full one. Without a full run: "The schedule counts from the first full scan: start it by hand."
- **History:** a paged table of the runs: scope, trigger, by, started, duration, OK / failed / skipped. The expansion lists the routers with their state, step, error, counts and duration.
- **Polling:** every 3 s while a run is queued or running, else every 30 s, as `JobsTab.vue` does.

### 8.8 Other places

- **The router's sensor page and the host page:** *Show on the network map* (`?focus=`).
- **The router's sensor page:** *Scan now* (Commands Run), next to *Poll now*.
- **`e2e/pages.spec.ts`:** the new page in its list.

---

## 9. Server side

### 9.1 Rights

**A new area, `NetworkMap`** (decision 7):

| Level | Allows |
|---|---|
| **View** | The map, connections, suggestions and scans. The names of the routers and hosts on the map |
| **Details** | View, plus the network interfaces: MACs, IPs, DHCP host names, Wi-Fi clients |
| **Edit** | Details, plus creating, changing and deleting connections; accepting, dismissing and restoring suggestions; assigning and ignoring interfaces |
| **Manage** | Edit, plus scans of all routers or of a location, cancelling scans, and the schedule and settings |

- **A scan of one router needs Commands Run**, as *Poll now* and *Backup now* do: it only reads, as `pnmt`. So the router's page, the side panel and MCP (`network_scan_router`) can offer it to the people who may poll. `RightsCatalog`'s text for Commands Run names it.

- **The side panel's links** go to pages that check their own rights. The router guard sends a user without the right to the dashboard, as today.
- **The text goes into `RightsCatalog`**, so the group page and *Your access* explain it.

### 9.2 Data model (one migration, `AddNetworkMap`)

**`network_map_settings`** (one row): `scan_schedule_enabled` bool (false), `scan_interval_days` int (14), `scan_hour` smallint (2), `retention_days` int (90), `changed_at`, `changed_by`.

**`network_scans`**:

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `scope` | smallint | `All`, `Location`, `Router` |
| `location_id`, `sensor_id` | uuid null | The scope's location or router. `SET NULL` on delete; the name is kept in `scope_name` |
| `trigger` | smallint | `Manual`, `Schedule` |
| `requested_by`, `requested_by_name` | | null for the schedule |
| `state` | smallint | `Queued`, `Running`, `Done`, `Cancelled` |
| `total`, `succeeded`, `failed`, `skipped` | int | |
| `current_sensor_id`, `current_step` | | What the UI shows (§8.7) |
| `created_at`, `started_at`, `finished_at` | | |

**`network_scan_items`**: `scan_id` (cascade), `sensor_id` (null, `SET NULL`), `router_name`, `position`, `state` (`Queued`, `Running`, `Succeeded`, `Failed`, `Skipped`, `Cancelled`), `step`, `started_at`, `finished_at`, `error`, `table_errors` jsonb, `counts` jsonb (ports, MACs, ARP, leases, neighbors, peers, Wi-Fi clients), `duration_ms`.

**`network_router_snapshots`**: `sensor_id` (PK, cascade), `scanned_at`, `routeros_version`, `raw` (bytea, gzip), `discovery_settings` jsonb, `table_errors` jsonb.

**`network_router_ports`**:

| Column | Type | Notes |
|---|---|---|
| `sensor_id`, `name` | PK | Cascade with the sensor |
| `default_name`, `type`, `mac`, `mtu`, `running`, `disabled`, `comment` | | `/interface` |
| `parent`, `vlan_id`, `bond` | | The bridge, VLAN parent or bond |
| `rate_mbps`, `full_duplex` | int null, bool null | The monitor |
| `sfp` | jsonb null | Module, connector, wavelength, vendor, part number, link lengths |
| `detected_type` | text null | §3.3 |
| `role`, `mac_count` | smallint, int | §5.2 |
| `seen_at` | timestamptz | |

**`network_observations`**: `id` bigserial; `sensor_id` (cascade); `source` (`BridgeHost`, `Arp`, `DhcpLease`, `Neighbor`, `WireGuardPeer`, `Tunnel`, `Address`, `WifiClient`); `interface`, `bridge`, `vlan_id`; `mac`, `ip` (inet), `host_name`; for neighbors `identity`, `platform`, `board`, `version`, `remote_interface`, `discovered_by`; `remote_key` (a peer's public key or a tunnel's remote address); `extra` jsonb; `seen_at`. Indexes on `mac` and on (`sensor_id`, `source`).

**`network_interfaces`**:

| Column | Type | Notes |
|---|---|---|
| `mac` | text PK | Normalised, 17 characters |
| `ips` | inet[] | |
| `host_name` | text null | From the leases |
| `random`, `ignored` | bool | |
| `sources` | smallint | Flags |
| `attached_sensor_id`, `attached_port`, `attached_vlan`, `attached_note` | | §6.1 |
| `match_kind` | smallint | `None`, `RouterPort`, `ServerAdapter`, `Manual`, `Ambiguous` |
| `match_host_id`, `match_sensor_id`, `match_interface` | | The host; the router or server sensor; the port or adapter name |
| `hint_host_id` | uuid null | §6.2 |
| `assigned_by`, `assigned_at` | | Manual |
| `first_seen_at`, `last_seen_at` | | |

**`agent_adapter_macs`**: `sensor_id` (cascade), `mac`, `adapter`, `kind`, `speed_bps`, `virtual`. PK (`sensor_id`, `mac`, `adapter`), index on `mac`.

**`network_connections`**:

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `a_host_id`, `a_sensor_id`, `a_interface` | uuid, uuid null, text null | The host (for a router, its host), the router or null, the port. The host is `ON DELETE CASCADE`, the router too |
| `b_host_id`, `b_sensor_id`, `b_interface` | | The same |
| `type` | text | §3.2 |
| `notes` | text null | |
| `source` | smallint | `Manual`, `Suggestion` |
| `evidence` | jsonb null | The suggestion's evidence when it was accepted |
| `confirmed_at`, `unconfirmed_since`, `misses` | | §5.5 |
| `created_at`, `created_by`, `updated_at`, `updated_by` | | |

A unique index on the normalised ends: the smaller end first, by host, sensor and interface. A check that the two ends differ.

**`network_suggestions`**: `id`; `key` (unique); `kind`; the ends as in `network_connections`; `type`; `confidence`; `evidence` jsonb; `state`; `connection_id` (null, `SET NULL`); `decided_by`, `decided_at`; `first_seen_at`, `last_seen_at`.

- **The entities are `[Audited]`** except the scan data (observations, ports, snapshots, interfaces' scan fields, `agent_adapter_macs`), which are `[NotAudited("written by the scan")]`.
- **Deleting a router** removes its ports, observations and snapshot, and its connections (with their audit rows). Its open suggestions go at the next analysis.

### 9.3 Components

```
PNMTD.Lib/Network/                 pure, tested with recorded output
  Mac.cs                           normalise, random (locally administered)
  ConnectionTypes.cs               the list of §3.2
  ConnectionTypeDetector.cs        §3.3
  NetworkScanScript.cs             the scripts of §4.1, with RouterOS quoting
  NetworkScanReading.cs            the parser
  TopologyAnalyzer.cs              roles, suggestions, matches, confirmations (§5, §6)
  TopologyPaths.cs                 the path between two devices, and its single points (§10.2)
PNMTD/Network/
  NetworkMapOptions.cs, NetworkMapSetup.cs
  NetworkScanner.cs                BackgroundService: runs, one router at a time (§7.2)
  NetworkScanSchedule.cs           hosted, under BackgroundTasks:Enabled (§7.3)
  NetworkScanStore.cs              stores a router's tables (§4.3)
  NetworkAnalysis.cs               loads the inputs, runs TopologyAnalyzer, writes its results
  NetworkRetentionTask.cs          interfaces, dismissed suggestions, history (§6.2, §7.3)
PNMTD/Agents/AgentSections.cs      writes agent_adapter_macs with each network section
PNMTD/Data/CustomMigrations/       the backfill of agent_adapter_macs
PNMTD/Services/Domain/NetworkMapService.cs, NetworkConnectionService.cs, NetworkScanService.cs
PNMTD/Api/V1/NetworkMapControllers.cs, Contracts/NetworkMap.cs
PNMTD/Cli/NetworkCommand.cs
PNMTD/Tools/Read/NetworkReadTools.cs, Write/NetworkWriteTools.cs   the MCP tools (§10.2), and the delete and the action
                                 in Delete/DeleteTools.cs and Actions/CommandTools.cs
```

### 9.4 Configuration

**In the database, on the Scans tab:** the schedule (on or off), the interval, the hour, and the retention.

**In `appsettings` and the environment:**

| Setting | Default | |
|---|---|---|
| `NetworkMap:Enabled` | true | Off: no scans, and the menu entry is hidden (as `feature`) |
| `NetworkMap:Scan:TableTimeoutSeconds` | 120 | Per table |
| `NetworkMap:Scan:MaxOutputBytes` | 8388608 | Per table |
| `NetworkMap:Scan:LockWaitSeconds` | 120 | §7.1 |
| `NetworkMap:Scan:HistoryDays` | 365 | Runs and their routers |
| `NetworkMap:Analysis:IntervalMinutes` | 5 | During a run (§5.1) |

---

## 10. API v1, MCP and CLI

### 10.1 REST

| Method | Path | Right | |
|---|---|---|---|
| GET | `/api/v1/network-map/graph` | View | `?routers=all\|locations\|problems&routerLocationIds&hostLocationIds&onlyConnected&suggestions&media`. Nodes (kind, ids, name, location, state, OS), groups (the top-level locations), edges (connections, and suggestions on request). A router's saved position on its node; the client lays out the rest (docs/network-map-positions-plan.md) |
| GET | `/api/v1/network-map/connection-types` | View | §3.2 |
| GET | `/api/v1/network-map/devices/{kind}/{id}` | View | The side panel: router or host, with connections, ports, counts, last scan |
| GET, POST | `/api/v1/network-map/connections` | View, Edit | Paged (`page`, `pageSize`, `sort`, `search`, the filters of §8.4) |
| PUT, DELETE | `/api/v1/network-map/connections/{id}` | Edit | |
| GET | `/api/v1/network-map/suggestions` | View | Paged |
| POST | `/api/v1/network-map/suggestions/{id}/accept` | Edit | The body can change the type, ports and notes |
| POST | `/api/v1/network-map/suggestions/accept`, `/dismiss` | Edit | Many: `ids` |
| POST | `/api/v1/network-map/suggestions/{id}/dismiss`, `/restore` | Edit | |
| GET | `/api/v1/network-map/interfaces` | Details | Paged |
| PUT, DELETE | `/api/v1/network-map/interfaces/{mac}/assignment` | Edit | `hostId` (and optional interface name) |
| POST | `/api/v1/network-map/interfaces/{mac}/ignore`, `/unignore` | Edit | |
| GET | `/api/v1/network-map/scans/status` | View | The running run, its step, the queue, the last full run, the next scheduled one |
| GET | `/api/v1/network-map/scans`, `/scans/{id}` | View | History, paged; one run with its routers |
| POST | `/api/v1/network-map/scans` | Manage; one router: Commands Run | `{ scope, locationId?, sensorId? }`. 409 when the same scope is queued or running |
| POST | `/api/v1/network-map/scans/{id}/cancel` | Manage | |
| GET, PUT | `/api/v1/network-map/settings` | View, Manage | |

- **Audit events:** `NetworkConnectionCreated`, `…Updated`, `…Deleted`; `NetworkSuggestionAccepted`, `…Dismissed`, `…Restored`; `NetworkInterfaceAssigned`, `…Cleared`, `…Ignored`; `NetworkScanStarted`, `…Cancelled`; `NetworkMapSettingsChanged`. Scheduled runs are not audited.
- **`EndpointAuditTests`** covers every new endpoint.

### 10.2 MCP (R12)

**The map's information is available over MCP**, through the tool catalog that MCP and Cookie share (docs/mcp-plan.md §6). An AI client can then answer "how is branch-gw connected to the core?", "which port is srv-db on, and how fast?" or "what breaks if the fibre between the two buildings goes?", **with its user's rights and nothing more.**

**The ceiling** (`McpRights`, docs/mcp-plan.md §5.3):

| Area | Read-write token | Read-only token | Why |
|---|---|---|---|
| NetworkMap | **Edit** | **Details** | Connections, suggestions and assignments are daily work. The interfaces are a reading level. *Manage* (scans of many routers, the schedule, the settings) stays in the UI, as every *Manage* area does |

**Reading** (kind *read*, both tokens, `ToolAudience.Both`):

| Tool | What | Right |
|---|---|---|
| `network_summary` | Routers on the map, hosts with connections, connections by medium and by physical or virtual, the unconfirmed ones, open suggestions by confidence, unassigned interfaces. The last full scan, a running scan with its step and progress, the schedule | NetworkMap View |
| `network_connections_list` | **The map as data.** Each connection with both ends (kind, id, name, port, site, `url`), its type (`{ code, label }`), physical or virtual, and its scan state (confirmed when, unconfirmed since, or manual). Filters: `locationId` (with its sub-locations), `deviceId`, **`routersOnly`** (router ↔ router: how the routers are connected), `physical`, `type`, `unconfirmed`, `search`. Paged | View |
| `network_device_get` | A router or a host, as its side panel (§8.3). Its connections, with the far ends. For a router, its ports from the scan (name, type, rate, link, proposed type, role, MAC count) and its last scan (when, the result, failed tables). For a server, its adapters with MAC and speed. **With Details,** the interfaces seen on each port | View (Details) |
| `network_path_get` | **How two devices are connected.** The shortest path over the connections, each hop with its ends, ports and type. Also: the slowest hop, the virtual hops, and **the connections without which there is no other path** (single points of failure). `physicalOnly` leaves the virtual connections out | View |
| `network_suggestions_list` | Suggestions with their evidence and confidence, open by default. The filters of §8.5. Paged | View |
| `network_interfaces_search` | A MAC (or its prefix), an IP or a DHCP host name: where it is attached (router, port, VLAN), its match, first and last seen. Paged | Details |

**Writing, deleting and the action** (read-write token only):

| Tool | Kind | What | Right | Audit event |
|---|---|---|---|---|
| `network_connection_create` | write | Two ends (a router or host, and the port), the type, notes | NetworkMap Edit | `NetworkConnectionCreated` |
| `network_connection_update` | write | A patch: the ports, type, notes | Edit | `NetworkConnectionUpdated` |
| `network_suggestion_decide` | write | `accept`, optionally with another type, ports or notes; or `dismiss`. One suggestion per call | Edit | `NetworkSuggestionAccepted`, `NetworkSuggestionDismissed` |
| `network_interface_assign` | write | A MAC to an existing host, or `hostId: null` to clear it | Edit | `NetworkInterfaceAssigned`, `NetworkInterfaceCleared` |
| `network_connection_delete` | delete | One connection, **only in the window a person opens** (docs/mcp-plan.md §7.5). It is recorded as it was | Edit | `NetworkConnectionDeleted` |
| `network_scan_router` | action | Queues a scan of one router, as `router_poll` polls one. It returns the run. Its result shows in `network_device_get` and `network_summary`. Idempotent: a router that is already queued gives the run it is in | Commands Run | `NetworkScanStarted` |

**An example:** `network_path_get` from srv-db to core-gw.

```json
{
  "from": { "kind": "Host", "id": "…", "name": "srv-db", "url": "…/network-map?focus=host:…" },
  "to": { "kind": "Router", "id": "…", "name": "core-gw", "url": "…/network-map?focus=router:…" },
  "hops": [
    { "connectionId": "…", "a": { "name": "srv-db", "port": "eno1" }, "b": { "name": "office-gw", "port": "ether7" },
      "type": { "code": "baset-10g", "label": "BASE-T 10G" }, "physical": true, "scan": { "state": "Confirmed", "at": "2026-11-18T02:14:00+01:00" } },
    { "connectionId": "…", "a": { "name": "office-gw", "port": "sfp-sfpplus1" }, "b": { "name": "core-gw", "port": "sfp-sfpplus3" },
      "type": { "code": "smf-10g", "label": "Fiber SMF 10G" }, "physical": true, "scan": { "state": "Confirmed", "at": "2026-11-18T02:16:00+01:00" } }
  ],
  "slowest": "10G",
  "virtualHops": 0,
  "singlePoints": ["…the first hop's id…", "…the second hop's id…"],
  "lastFullScan": "2026-11-18T02:00:00+01:00"
}
```

**The rules:**

- **The path** is `TopologyPaths` (pure, `PNMTD.Lib/Network`). It is a breadth-first search over the connections, never over suggestions, with at most 12 hops. Ties go to the path whose slowest hop is faster. For the single points, each hop is left out in turn and the search is run again.
- **Text from outside is wrapped as `<untrusted>`** (docs/mcp-plan.md §6.5). The scan's data is set by devices and people, not by PNMT:
  - DHCP host names;
  - the neighbors' identities, platforms and boards;
  - port comments;
  - connection notes.

  **A DHCP client can give itself any name, so this is exactly where an injection would hide.** The evidence sentences quote these values the same way. `InjectionScanner` withholds what rates 0.8 or higher.
- **Every result says how old it is:** each router's `scannedAt`, and the last full scan. The AI can then say "as of the scan on 18 November", and it can call `network_scan_router` for a newer reading.
- **Compact and paged** (docs/mcp-plan.md §6.3):
  - an end is `{ kind, id, name, port, site }`;
  - a type is `{ code, label }`;
  - lists are `{ items, nextCursor, total }`.

  The backbone of 500 routers fits in a few pages under `Mcp__MaxResultChars`.
- **Links** point into the map: `?focus=router:<id>`, `?focus=host:<id>`, `?focus=connection:<id>`, and `?tab=suggestions` with the suggestion's id.
- **No tool creates a host** (decision 6). `host_create` exists for that under Monitoring Edit, when a person asks for it.
- **Cookie** gets the read tools too, because their audience is `Both`. It sees them with its group's rights and its pseudonyms. Its scrubber already turns MACs into `<mac:n>`.
- **Not over MCP:**
  - scans of all routers or of a location, cancelling scans, and the schedule and settings (Manage);
  - ignoring interfaces and restoring dismissed suggestions, which are housekeeping in the UI.
- **The server's instructions** (`McpInstructions.cs`) get one line: *For the network: `network_connections_list` (`routersOnly` for the backbone) and `network_path_get`. Connections were confirmed by people. Suggestions are only what the scan proposes.*
- **docs/mcp-plan.md** gets the new rows in §5.3's table and in §7's tables. The tool list on Admin → MCP comes from the catalog.

### 10.3 CLI and dev seed

- **CLI:**
  - `pnmtd network scan <router> | --location <name> | --all`;
  - `pnmtd network scan <router> --raw`: the tables as the router printed them, for debugging the parser, like `pnmtd mikrotik poll --raw`;
  - `pnmtd network analyse`: runs the analysis on what is stored.
- **`pnmtd dev seed`** adds two sites with routers, servers and a switch, with their tables. Suggestions and the map then have content without a router. The simulator replays fixtures, as `DevSeedMikrotik` does.

---

## 11. Phases

| Phase | What | Done when | Estimate |
|---|---|---|---|
| **N0** | **Lab.** The tables of §4.1 as `pnmt` on CHR 7.13 and 7.25.1, recorded as fixtures. 5,000 static bridge host entries, for size and time. **On a real RouterBOARD with SFP+** (a CRS or CCR, and a hAP for Wi-Fi): `monitor once as-value`, the SFP fields with a DAC, an SR (850 nm), an LR (1310 nm) and an RJ45 module, a combo port, entries learned in hardware, and LLDP towards a non-MikroTik switch | Each *(N0)* of this plan is answered and noted in `NetworkScanScript`'s remarks. The fixtures are in `PNMTD.Tests/Fixtures/Network/` | 1 d, plus the hardware |
| **N1** | `PNMTD.Lib/Network`: `Mac`, `ConnectionTypes`, the detector, the scripts and the parser | The parser and detector tests of §12 pass on every fixture | 1.5 d |
| **N2** | The migration; `agent_adapter_macs` with its writer and backfill; `NetworkScanner`, the schedule, the store; the scan endpoints; the CLI; System health | **Lab:** `pnmtd network scan --all` scans both CHRs one after the other, a cancel stops the second, and a restart resumes. The scanner tests pass | 2.5 d |
| **N3** | `TopologyAnalyzer` and `NetworkAnalysis`; connections, suggestions and interfaces with their endpoints and audit; retention; dev seed | The analyser's scenarios (§12) pass, and the API tests | 3 d |
| **N4** | Web UI: the page, the tabs Connections, Suggestions, Network interfaces and Scans, the dialogs, the strip; the rights in the UI | Vitest and the e2e tests of §12 | 2.5 d |
| **N5** | Web UI: the map: the layout, the groups, pan and zoom, the filters, the side panel, focus links | Vitest of the layout and pan/zoom; e2e of the map | 3 d |
| **N6** | **MCP** (§10.2): the tools in `PNMTD/Tools`, `TopologyPaths`, the ceiling and the cap in `McpRights`, the server's instructions | The MCP tests of §12 pass, and the canary test. **By hand:** Claude Code with a read-only token answers "how is branch-gw connected to core-gw?" from the dev seed, with the path and its single points | 1.5 d |
| **N7** | Docs: the Documentation page (rights, the scan, the types), release notes, docs/mikrotik-plan.md §4.2's table of operations, docs/mcp-plan.md §5.3 and §7 | | 0.5 d |

**Total about 15.5 days.** N0 decides how the ethernet table is read. Without real hardware, the media stay a person's choice (`detected_type` null) until it is available.

### 11.1 As built (2026-11-20)

**N0 on CHR 7.13 and 7.25.1**, as a user of the `pnmt-read` group (`ssh`, `read`), recorded in `PNMTD.Tests/Fixtures/Network/`:

- **Every table reads with `read` alone**, `monitor … once as-value` in a `:foreach` included, and `where comment!="pnmt:…"` leaves PNMT's own tunnel out of the WireGuard prints.
- **`/interface print as-value` leaves columns out** without a proplist: 7.13 `running`, `disabled` and `default-name`, 7.25.1 `default-name`. The scan names them. A proplist naming a flag a version doesn't print (`slave`) is ignored there, not refused.
- **`/interface wifi` exists on both CHRs** and prints nothing; `/interface wireless` doesn't, and its `:parse` inside `:do … on-error` leaves it out.
- **`/ip neighbor discovery-settings get`** returns the settings as an object.
- **A CHR's virtio port reports no `rate`**, so its type stays unclear, as intended.
- **Still open, on a RouterBOARD** (a CRS or CCR, a hAP for Wi-Fi): the SFP fields with DAC, SR, LR and RJ45 modules, a combo port, entries learned in hardware, LLDP towards a switch of another maker, and 5,000 bridge host entries for size and time. Until then, the detector's SFP rules follow the manual's field names, and the tests use them as written.

**Where the build differs from the plan:**

- **Names:** a router is shown by its host's name when the host has only that router (a sensor is often just "router"), else as "host / sensor".
- **Behind a known host (§5.3):** also behind a port whose neighbor is a host (by its assigned or adapter MAC), not only behind a connection. It suggests only hosts that have no connection yet.
- **Types:** `ips` and the observations' addresses are `text`, not `inet`. They are shown and searched, never computed with.
- **`NetworkMap:Enabled=false`** stops the scans and says so on the Scans tab. The menu entry follows the rights alone: the menu's features are Redmine's and Zabbix's.
- **`NetworkMap:Scan:Background`** (true) turns the worker's own loop off for the tests, which run the queue themselves.
- **Three more endpoints:** `GET connections/{id}`, `GET summary` and `GET path`, which the side panel and MCP use.
- **The graph libraries:** `d3-force` and `d3-hierarchy` were already in the lock file through Mermaid. They are direct dependencies now, at those versions, so no new package came in.

**Tests:** `PNMTD.Tests/Network` (the parser on the fixtures, the detector, the paths, the analysis' scenarios and 500 routers, the scanner, the API with its rights and audit, the schedule, the backfill, MCP), `DevSeedTests`, `EndpointAuditTests`; vitest for the layout, pan and zoom and the formats; `e2e/network-map.spec.ts` and the pages.

---

## 12. Testing

- **`PNMTD.Lib`, parser and detector:**
  - each fixture parses, and its counts are as recorded;
  - a missing Wi-Fi menu is no error;
  - the floats of `:serialize` are read correctly;
  - MACs are normalised, and random ones marked;
  - each SFP module of N0 gets its type; RJ45 rates give BASE-T; VLAN, bridge and bond resolve to the physical port;
  - no script contains `private-key` or `preshared-key`.
- **`TopologyAnalyzer`, as scenarios** (small worlds built in code):
  - two routers that see each other: one High suggestion with both ports; seen from one side only: Medium;
  - a WireGuard pair: `wireguard`, High; PNMT's own tunnel makes none;
  - a server alone on a port: High, with the 10G type; with 5 other MACs on the port: Medium; once that port has a connection to a switch host, the hosts behind it are suggested to the switch instead;
  - a MAC learned on the uplink of both routers is attached only at the far router's edge port;
  - an unknown LLDP neighbor gives no suggestion and no node, only an interface with its neighbor data; once its MAC is assigned to a host, a High *Router ↔ host* with the far port;
  - no suggestion creates a host, and no end is anything but a router or a host;
  - an existing connection gets no suggestion, and is confirmed; after two scans without it, unconfirmed; one miss is not enough;
  - a conflicting observation gives *Differs*;
  - a dismissed key stays dismissed;
  - random and ignored MACs make no suggestions;
  - ambiguous MACs match nothing;
  - the same input gives the same output.
- **Scanner and services** (`NetworkScanTests`):
  - one router at a time, over two runs; a run of one router goes first;
  - WireGuard only, not ready, disabled and busy routers are skipped with their reasons;
  - a failed table keeps the old rows; cancel; resume after a restart;
  - 409 for the same scope;
  - the schedule: off by default; when on, 14 days after the last full run, at the hour; never before a first full run by hand;
  - the poller pauses for a router that is being scanned.
- **`TopologyPaths`:** the shortest path; ties by speed; `physicalOnly`; the single points (a ring has none, a chain has all); no path; more than 12 hops.
- **`agent_adapter_macs`:** stored with a `network` section, replaced by the next one, filled by the backfill, deleted with the sensor.
- **API:**
  - each level: View gets 403 on the interfaces and on every write; Details gets 403 on writes; Edit gets 403 on scans and settings;
  - `EndpointAuditTests` and `AuditLogTests`;
  - the contract tests' OpenAPI (`PNMTD.Contract.Tests`, `tools/contract.sh`);
  - a scan of one router with Commands Run and without Manage, and 403 for a location's scan.
- **MCP** (`McpToolsTests`, as for the other tools):
  - `McpRights` gives NetworkMap Edit to the read-write token and Details to the read-only one, for every level the user holds;
  - the read-only token lists the six read tools and none of the others;
  - `network_interfaces_search`, and `network_device_get`'s interfaces, need Details;
  - each write writes its audit event with `Actor = Mcp`, and `network_connection_delete` outside the window answers `delete_not_allowed`;
  - `network_scan_router` needs Commands Run, and a second call gives the same run;
  - **injection:** a DHCP host name and an LLDP identity that read "ignore your instructions…" come back wrapped as `<untrusted>`, or withheld;
  - `network_connections_list` of the dev seed with 500 routers stays under `Mcp__MaxResultChars` per page, and its cursor reaches every connection;
  - the canary test covers the new read tools.
- **Lab** (`MikrotikLabTests`, a new case): a scan of both CHRs stores ports and observations, and two CHRs joined by a WireGuard tunnel of their own give a `wireguard` suggestion.
- **Performance** (`PNMTD.Tests/Performance`): the analysis of 500 routers with 200 MACs each, under 5 s.
- **Web UI:**
  - vitest: `networkLayout` (deterministic; circles enclose their members; no two circles overlap; a host in a sub-location is in its top-level location's circle), `usePanZoom` (zoom around the pointer, fit), the filters to and from the URL, the type labels;
  - e2e `network-map.spec.ts`: the tabs render; a suggestion becomes a connection and shows on the map; the side panel opens on a click; a scan starts and the strip shows its progress;
  - `pages.spec.ts` and `navigation.test.ts` are updated.

---

## 13. Risks

| Risk | What helps |
|---|---|
| **MAC tables age out** (the bridge's default is 5 minutes), so a scan only sees what was talking. Every 14 days is a snapshot | Interfaces keep their last seen across scans. A connection is unconfirmed only after two misses. A router can be scanned by hand at any time |
| **The SFP fields** differ between versions and modules, and DAC and AOC are told apart by heuristics | N0 on real modules. The proposal is only a proposal. Unclear gives no type |
| **Neighbor discovery is off** on many networks, or limited to an interface list | The discovery settings are read, and the side panel says "Neighbor discovery is off on ether1–8". The MAC table still works |
| **Large tables** make `:serialize` slow, or hit the output limit | Separate execs with their own limits; the N0 measurement; `print terse` as the fallback |
| **The router's CPU** on a large CRS while it prints its table | One router at a time; scheduled runs at 02:00 by default |
| **Personal data:** DHCP host names and Wi-Fi clients name people and their devices | The interfaces need Details. Retention (90 days). Random MACs make no suggestions |
| **An AI reads text that devices set:** DHCP host names and LLDP identities can say anything | Wrapped as `<untrusted>`, and withheld from 0.8 (§10.2). The tools only read, except for the writes of a read-write token, each in the audit |
| **The map's speed** with thousands of SVG elements | "Only hosts with a connection" by default; labels only when zoomed in. Canvas is the way out if it is needed |
| **The layout** changes when the data does, so people lose their bearings | The groups stay put, because top-level locations change rarely, and the same data gives the same picture. A router's position can be saved by hand (decision 2) |

---

## 14. Considered and left out

- **Reading the tables at every poll** (5 minutes). They are large, and the topology changes rarely. A separate scan keeps the poll small and its output under 1 MiB.
- **Scans as `mikrotik_jobs`.** Jobs are built for updates and commands: permanent logs, passwords in the vault, and failed orphans. A scan of 500 routers every 14 days would add 13,000 permanent jobs a year. It only reads, and can simply be run again.
- **Hosts created from what the scan finds:** a host for an unknown LLDP neighbor, or a switch host for a port with several MACs. Decided against (decision 6): PNMT discovers network interfaces, not hosts. A person creates the host and assigns the interface.
- **Nested location circles, or a choice of level** (decision 1). Saved positions came later, for routers (decision 2).
- **Connections made automatically** for High confidence. The requirement says a person decides. *Accept selected* makes it quick.
- **Graph libraries:**
  - **cytoscape.js** is large and has its own canvas renderer, and its compound nodes are boxes;
  - **vis-network** draws its clusters as nodes, not as circles around them;
  - **Mermaid** has neither pan nor zoom, and no clicks.

  `d3-force` with SVG of our own matches the hand-written charts and keeps the dependencies small.
- **SNMP and LLDP-MED from other makers' switches.** Only what a MikroTik sees of them.
- **IPv6 neighbors, OSPF and BGP sessions, traceroutes.** They could add virtual connections later, as new rules of §5.3.
- **Vendors from the MAC's OUI.** It needs the IEEE list (several MB) kept up to date. It would help with unassigned interfaces, so it is a candidate for later.
- **Virtual machines on their hypervisor.** The agents don't say which guest runs where. A server whose hardware says "virtual machine", behind the port of a hypervisor, could later give a `vm` suggestion.
- **PNMTD itself as a node**, with the WireGuard tunnels to every router. That is monitoring, not topology, and it would join everything to one point.
- **Bonds as one connection.** Each member is one, in this plan. A bond could later group them.

---

## 15. Decisions (review of 2026-11-20)

- **MCP** (added after the review): the map and the connections are available over MCP, above all how the routers are connected to each other and to the servers (§10.2).
- **The schedule starts off.** The first scan is started by hand. The schedule is turned on in the Scans tab, and its interval is then 14 days by default (§7.3, §8.7).

1. **Circles:** only the top-level locations, with no nesting and no choice of level (§8.2).
2. ~~**Positions are not saved.**~~ Replaced on 2026-11-20 by [network-map-positions-plan.md](network-map-positions-plan.md): routers' positions are saved by hand, one layout for everyone; hosts are still placed automatically (§8.2).
3. **Scheduled scans start at 02:00 server time** by default. The hour is set in the Scans tab (§7.3, §8.7).
4. **By hand, a MAC is assigned to a host only**, for hosts without an agent: printers, APs, cameras. A router's ports match it anyway (§6.2).
5. **Random MACs** are kept and shown, but never matched or suggested. A manual assignment is possible (§4.2).
6. **Hosts are only created by hand.** PNMT discovers network interfaces, not hosts.
   - An interface without a host is only in the Network interfaces tab.
   - It is neither on the map nor in a suggestion.
   - No suggestion creates a host (§3.1, §5.3).
7. **A rights area of its own, `NetworkMap`**, with View, Details, Edit and Manage (§9.1).
8. **A router isn't scanned when it first becomes Ready.** A scheduled run takes it, or *Scan now* on its page (§8.8).

No questions are open.

---

## Appendix: files

| File | Change |
|---|---|
| `PNMTD.Lib/Network/*` | New: §9.3 |
| `PNMTD/Network/*` | New: §9.3 |
| `PNMTD/Models/Db/NetworkMapEntities.cs`, `PNMTD/Models/Enums/NetworkMapEnums.cs`, a migration | The tables of §9.2 |
| `PNMTD/Data/PnmtdDbContext.cs` | The DbSets and `ConfigureNetworkMap` |
| `PNMTD/Data/CustomMigrations/` | The backfill of `agent_adapter_macs` |
| `PNMTD/Agents/AgentSections.cs` | Writes `agent_adapter_macs` with the `network` section |
| `PNMTD/Auth/Rights/Rights.cs`, `PNMTD.WebUI/src/api/rights.ts` | The area `NetworkMap` and its texts |
| `PNMTD/Api/V1/NetworkMapControllers.cs`, `Contracts/NetworkMap.cs` | §10 |
| `PNMTD/Audit/AuditEvents.cs` | §10 |
| `PNMTD/Tools/Read/NetworkReadTools.cs`, `PNMTD/Tools/Write/NetworkWriteTools.cs`, `PNMTD/Tools/Delete/DeleteTools.cs`, `PNMTD/Tools/Actions/CommandTools.cs`, `PNMTD/Tools/ToolCatalog.cs` | The tools of §10.2 |
| `PNMTD/Mcp/McpRights.cs`, `PNMTD/Mcp/McpInstructions.cs` | The ceiling and the cap for NetworkMap; the line for the network |
| `PNMTD.Lib/Network/TopologyPaths.cs` | New: §10.2 |
| `PNMTD/Cli/NetworkCommand.cs`, `PNMTD/Cli/DevSeedMikrotik.cs` | The CLI; the seed's sites, servers and tables |
| `PNMTD/Models/Enums/KeyValueKeyEnums.cs`, `PNMTD/Services/Domain/SystemService.cs` | `LAST_NETWORK_SCAN_SCHEDULE_RUN`, and its late-task check on System health |
| `appsettings.json`, `deploy/.env.example` | `NetworkMap__*` |
| `PNMTD.WebUI/package.json`, lock file | `d3-force`, `d3-hierarchy` (through `tools/guarddog.sh`) |
| `PNMTD.WebUI/src/views/network/` | New: `NetworkMapView.vue`; `MapTab.vue`, `networkLayout.ts`, `MapCanvas.vue`, `MapFilters.vue`, `DevicePanel.vue`; `ConnectionsTab.vue`, `ConnectionDialog.vue`; `SuggestionsTab.vue`; `InterfacesTab.vue`; `ScansTab.vue`, `ScanStrip.vue`, `ScanScheduleField.vue` |
| `PNMTD.WebUI/src/composables/usePanZoom.ts` | New |
| `PNMTD.WebUI/src/layouts/navigation.ts`, `navigation.test.ts`, `src/router/index.ts` | The entry and the route |
| `PNMTD.WebUI/src/api/resources.ts`, `openapi.json`, `schema.d.ts` | The calls and keys; the regenerated schema |
| The router's sensor page, the host page | *Show on the network map*, *Scan now* (Commands Run) |
| Tests | As in §12 |
| `docs/mikrotik-plan.md`, `docs/mcp-plan.md`, the Documentation page, release notes | The `pnmt` operations table (§4.2), the scan, the rights; MCP's ceiling and tool tables |
