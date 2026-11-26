<script setup lang="ts">
import { computed } from 'vue'

/** The documentation of the network map (docs/network-map-plan.md, docs/network-suggestion-rules-plan.md). */
const props = defineProps<{ search: string }>()
const text =
  'network map topology connections suggestions scan schedule mac table arp dhcp leases neighbor discovery lldp mndp cdp ' +
  'wireguard tunnel eoip gre interfaces mac address random assigned base-t dac aoc fiber mmf smf wi-fi virtual physical ' +
  'confirmed unconfirmed rights view details edit manage mcp path single point vlan bridge bond vrrp skip rules skip rule ' +
  'second path cable port physical port preview'
const show = computed(() => {
  const q = props.search.trim().toLowerCase()
  return !q || text.toLowerCase().includes(q)
})
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="network-docs-title">
    <div id="network-docs-title" class="panel-header">Network map</div>
    <div class="panel-body prose">
      <p>
        <strong>The network map shows how the MikroTik routers, the hosts and the servers are connected.</strong> A scan
        reads what the routers see; PNMT suggests connections from it; a person makes them connections, and the map
        draws them. PNMT discovers network interfaces, not hosts: hosts are only created by hand.
      </p>
      <ul>
        <li>
          <strong>The scan</strong> logs in to each router as PNMT's read-only user <code>pnmt</code>, one router at a
          time, and reads its interfaces with their links and SFP modules, its bridges and VLANs, the MAC table, the
          neighbors (MNDP, CDP, LLDP), ARP, the DHCP leases, its addresses, its tunnels (WireGuard, EoIP, GRE, IPIP) and
          its Wi-Fi clients. Each table is read on its own: a failed one keeps the last scan's rows. It is started on
          <em>Scans</em> for all routers or a location (Network map: Manage), or for one router (Commands: Run, as a
          poll), and runs on a schedule once someone turns it on: every 14 days at 02:00 by default, counting from the
          first full scan by hand.
        </li>
        <li>
          <strong>Suggestions</strong> come with their evidence and confidence: two routers that see each other by
          neighbor discovery (High from both sides), a WireGuard peer that is another router's key, a tunnel to another
          router's address, a server's MAC alone on a router's port, a Wi-Fi client, a host behind a switch that is
          connected already. Every end is a router or a host. <em>Dismiss</em> keeps a suggestion away while the scans
          keep finding it.
        </li>
        <li>
          <strong>One cable, one suggestion.</strong> What a router sees on a VLAN counts for the physical port under
          it, and what it sees in a bridge for the member port where the device was learned: nine VLANs on one trunk are
          one suggestion on the trunk's ports, and its evidence names the VLANs once. Suggestions between the same two
          devices that share a router's port are the same cable and are joined; two real cables on two ports stay two. A
          server whose VLAN sub-interface, bridge or bond carries the network card's MAC is suggested on its physical
          adapter (or the bond). VRRP's shared MACs say nothing about a cable. A link between two routers that only the
          MAC table or ARP shows, while they are linked on other ports already, is <em>Low</em>: usually the same
          network through another switch. A connection made on a VLAN interface is confirmed by its port's evidence, and
          the Connections tab marks it (<em>On a VLAN or bridge</em>).
        </li>
        <li>
          <strong>Skip rules</strong> (tab <em>Skip rules</em>; changed with Network map: Manage) leave rows out of the
          suggestions: on all routers, a location with its sub-locations, or one router; by interface (as the router
          prints it or the port under it, with <code>*</code> and <code>?</code>: <code>vlan9*</code>,
          <code>ether10</code>), interface type, VLAN IDs and ranges (<code>99</code>, <code>4000-4094</code>), evidence
          (MAC table, ARP, MNDP, LLDP, CDP, Wi-Fi, WireGuard, tunnels) and MAC prefix. Every condition set must match; a
          rule without conditions is allowed on one router only, and skips all it shows. A port that a rule's interface
          or type matches is no suggestion's end, also when the evidence is the other router's. The form shows, from the
          stored scans, what the rule would remove or bring back before it is saved, and <em>Skip like this…</em> on a
          suggestion starts one from its evidence. Rules change only the suggestions: the interfaces stay listed (tagged
          <em>skipped</em>), and no connection is confirmed or unconfirmed by them. Turning a rule off keeps it.
        </li>
        <li>
          <strong>A connection has one type</strong>, its medium and speed: BASE-T, DAC, AOC, Fiber MMF and SMF from 10M
          to 100G, Wi-Fi, WireGuard, Tunnel, Virtual machine, Other. The type of a router's port is proposed from what
          its monitor shows. A connection the latest scans show is <em>confirmed</em>; one they no longer show after two
          scans of its routers is <em>no longer seen</em>; one made by hand that no scan can see stays <em>by hand</em>.
        </li>
        <li>
          <strong>Network interfaces</strong> (Network map: Details) are the MACs the routers see, where each is
          attached, and what it is matched to: a Windows or Linux server's adapter, a router's port, or a host a person
          assigned it to. Random MACs (phones' private addresses) are kept but never matched or suggested. Interfaces no
          router has seen for 90 days go, unless assigned.
        </li>
        <li>
          <strong>Arranging the map</strong>: drag a router, or a location by its name, to where you want it; a focused
          router or location name also moves with the arrow keys (Shift: further), and Escape puts a drag back. A router
          moved but not saved has a dashed ring, and a bar offers <em>Save positions</em> and <em>Discard</em> (Network
          map: Edit). Saving writes the moved routers and the shown routers that had no saved position yet, so nothing
          moves afterwards; from then on everyone sees them there, under every filter. Hosts are never saved: they are
          placed around their routers. A router added later is placed automatically, near the routers it is connected
          to. The router's panel has <em>Back to automatic</em>, and the <em>Layout</em> menu next to <em>Fit</em> has
          <em>Save this layout</em> and <em>Back to automatic for all routers</em>. A location's circle always encloses
          its routers: one dragged far from the others makes it large.
        </li>
        <li>
          <strong>Rights</strong>: View the map, connections, suggestions, skip rules and scans; Details the interfaces,
          which can name people; Edit connections, suggestions, assignments and the routers' positions; Manage the scans
          of many routers, the schedule and the skip rules.
        </li>
        <li>
          <strong>Over MCP</strong>: <code>network_summary</code>, <code>network_connections_list</code> (with
          <code>routersOnly</code>: how the routers are connected), <code>network_path_get</code> (the path between two
          devices, its slowest hop and single points of failure), <code>network_device_get</code>,
          <code>network_suggestions_list</code> and <code>network_interfaces_search</code>; with the read-write token
          also creating, changing and (in the window for deleting) deleting connections, deciding suggestions, assigning
          interfaces and scanning one router. <code>network_summary</code> names the skip rules that are on, and
          <code>network_interfaces_search</code> the rule that skips an interface; the rules are changed in the web UI
          only. What devices call themselves comes as untrusted text.
        </li>
      </ul>
    </div>
  </section>
</template>
