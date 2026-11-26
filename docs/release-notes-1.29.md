# PNMT 1.29: one suggestion per cable on the network map, and skip rules

## What's new

### One cable, one suggestion

- **VLANs are put on their physical port** (docs/network-suggestion-rules-plan.md §3). Neighbor discovery answers on every VLAN
  interface, so two routers with nine VLANs on one trunk gave ten suggestions, one per VLAN. Now they give one, on the trunk's
  ports, with the type from those ports. Its evidence names the VLANs once: *…, and on 9 VLANs on that port: vlan10, vlan20,
  vlan30 and 6 more*.
- **Bridges too:** what a router learns in a bridge counts for the member port where it was learned. With one bridge per VLAN
  (VLAN interfaces as bridge ports), the routers' link is on the trunk, and the hosts behind it are attached and suggested. Before,
  they got no suggestion at all.
- **One cable per port:** suggestions between the same two devices that share a router's port are joined into one. Two real cables
  on two ports stay two, and so do two tunnels.
- **Servers whose VLAN sub-interface, Linux bridge or bond carries the network card's MAC** (`eno1`, `eno1.10`, `vmbr0`) are
  suggested again, on the physical adapter or on the bond. Before, such a server got no suggestion. A host with one MAC per VLAN on
  one port (Hyper-V's management adapters) gets one suggestion.
- **VRRP's shared MACs** say nothing about a cable, and no longer make links.
- **A second path is Low:** a link between two routers that only the MAC table or ARP shows, while they are linked on other ports
  already, is usually the same network through another switch. Its evidence says so.
- **Connections made on a VLAN interface** are confirmed by their port's evidence, and no suggestion repeats them. The
  *Connections* tab marks such an end (*VLAN 10 on ether1*) and filters them: *On a VLAN or bridge*.

### Skip rules

- **A new tab, *Skip rules*,** next to *Suggestions*. Everyone who sees the map sees the rules; *Network map: Manage* adds, changes,
  turns off and deletes them.
- **A rule leaves rows out of the suggestions:** on all routers, a location with its sub-locations, or one router; by interface (as
  the router prints it, or the port under it, with `*` and `?`: `vlan9*`, `ether10`), interface type, VLAN IDs and ranges
  (`99`, `4000-4094`), evidence (MAC table, ARP, MNDP, LLDP, CDP, Wi-Fi, WireGuard, tunnels) and MAC prefix (`00:50:56`). Every
  condition set must match. On one router, a rule without conditions skips everything the router shows: for a lab router.
- **A port that a rule's interface or type matches is no suggestion's end,** also when the evidence is another router's.
- **The form shows what the rule would change** before it is saved, from the stored scans: the rows it skips, the open suggestions
  that go or come back, the network interfaces on what it skips.
- ***Skip like this…*** on a suggestion (with *Manage*) opens the form filled in from its evidence: the router, the interface as
  printed, the VLAN and the table.
- **Rules change only the suggestions.** The network interfaces stay listed, tagged *skipped* with the rule's name, and no
  connection is confirmed or unconfirmed by a rule. The list shows what each rule skipped at the last analysis.
- **Over MCP:** `network_summary` names the rules that are on, `network_interfaces_search` the rule that skips an interface. The
  rules are changed in the web UI only.

### Fixed

- **The scan now reads a neighbor's own port, its protocols and its platform.** RouterOS 7.25 leaves `interface-name`,
  `discovered-by` and `platform` out of the neighbor table unless they are asked for, so the far port of a router seen by neighbor
  discovery was guessed from its MAC.

## Updating from 1.28

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.29.0`. It takes a backup first.

- **One migration:** a table for the skip rules, and a column on the network interfaces. No new right.
- **The suggestions change at the first analysis** after the update: after the next scan, a change on the map, or
  `pnmtd network analyse`. Open suggestions on VLAN interfaces go, and one per cable comes. Dismissed ones on VLANs stay dismissed,
  and the dismissal doesn't carry over to the cable's suggestion: that one is the real cable.
- **Scan once** (*Scan all routers* on *Scans*, or wait for the schedule) to read the neighbors' own ports.
- **Connections accepted on VLANs before:** *Connections → On a VLAN or bridge* lists them. Edit one to its physical port, and
  delete the duplicates.

## Good to know

- **What a rule matches:** the rows as each router printed them. A rule on `vlan*` skips what came through VLAN interfaces; a rule
  on `ether1` skips everything on that port, its VLANs included.
- **Turning a rule off** keeps it, with its conditions; deleting it asks first. A rule goes with its location or router.
- **The audit log** has `NetworkSkipRuleCreated`, `…Updated` and `…Deleted`, on the rule and on its router or location.
- **The plan:** docs/network-suggestion-rules-plan.md, with what the lab showed of RouterOS's VLANs and, in *As built*, where the
  build differs from it.
