# PNMT 1.17: the network map

## What's new

- **The network map** (*Monitoring → Network map*, docs/network-map-plan.md) shows how the MikroTik routers, the hosts and the servers are connected.
  - **A scan reads what the routers see.** It logs in to each router as the read-only `pnmt` user, one router at a time, and reads its interfaces with their links and SFP modules, bridges and VLANs, the MAC table, the neighbors (MNDP, CDP, LLDP), ARP, the DHCP leases, its addresses, its tunnels and its Wi-Fi clients. Nothing on a router changes.
    - It is started on the *Scans* tab, for all routers or a location (*Network map: Manage*), or for one router (*Commands: Run*, as a poll is).
    - The tab shows each router's progress and why one was skipped.
    - A schedule takes over once someone turns it on: every 14 days at 02:00 server time by default.
  - **PNMT suggests connections** from the scan, each with its evidence and a confidence:
    - two routers that see each other by neighbor discovery;
    - a WireGuard peer that is another router's key, or a tunnel to another router;
    - a server's MAC alone on a router's port;
    - a Wi-Fi client;
    - a host behind a switch that is connected already.

    A person makes a suggestion a **connection**, with one **type**, its medium and speed (*BASE-T 1G*, *DAC 10G*, *Fiber SMF 10G*, *WireGuard*…). A connection the scans no longer see is marked *no longer seen*, not deleted.
  - **The map:**
    - the routers by default, and the hosts of chosen locations on request;
    - each top-level location a dashed circle;
    - lines that grow with the speed, virtual ones dashed;
    - zoom and pan, filters in the URL, and a side panel for a router, a host or a connection.
  - **Network interfaces** (*Network map: Details*): the MACs the routers see, where each is attached, and what it is matched to: a Windows or Linux server's adapter, a router's port, or a host someone assigned it to. PNMT discovers interfaces, not hosts: hosts are still only created by hand.
  - **Over MCP:** `network_summary`, `network_connections_list` (with `routersOnly`: how the routers are connected), `network_path_get` (the path between two devices, its slowest hop and its single points of failure), `network_device_get`, `network_suggestions_list` and `network_interfaces_search`. With the read-write token, an agent can also make, change and delete connections, decide suggestions, assign interfaces and scan one router.
  - **From the server:**
    - `pnmtd network scan <router> --raw` prints what a router returns, table by table;
    - `pnmtd network status` shows the running and queued scans and the schedule.
- **Routers stay where you put them** (docs/network-map-positions-plan.md).
  - Drag a router, or a whole location by its name, and save with *Save positions* (*Network map: Edit*). From then on, everyone sees the routers there, under every filter.
  - A router moved but not saved has a dashed ring. Leaving with unsaved moves asks first, and the arrow keys move a router too.
  - *Back to automatic* is in a router's panel, and for all routers in the map's ⋯ menu.
- **3rd Party Connectors:** the *Documentation* tab has *Download as Markdown*, to hand the API on to the callers' developers.

## Updating from 1.16

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.17.0`. It takes a backup first.

- **Two migrations run at the start:**
  - `NetworkMap` adds ten tables;
  - `NetworkRouterPositions` adds one.

  A one-time step copies the servers' MACs from their last network report, so that servers match before their agents report again. Nothing existing changes.
- **A new rights area, *Network map*,** with View, Details, Edit and Manage. Admins have it; **no group has it after the update**, *Viewers* and *Operators* included. Give it to the groups that should see the map in *Admin → Groups*.
- **The schedule starts off.** Start the first scan by hand on *Network map → Scans*, then turn the schedule on there if you want it.
- **The routers need nothing new:** the `pnmt` user's `read` policy covers every table the scan reads.
- **Nothing to configure.** The `NetworkMap__*` settings are optional; see deploy/README.md, *MikroTik routers and WireGuard*, and `.env.example`.

## Good to know

- **The interfaces can name people**: DHCP host names, and Wi-Fi clients' MACs. That's why they need *Details*, which View alone doesn't give. Interfaces no router has seen for 90 days are removed, unless someone assigned them to a host.
- **Random MACs** (phones' private addresses) are kept and shown, but never matched or suggested.
- **SFP modules:** the DAC and fiber types are proposed from the fields that RouterOS documents. So far the scan has been tried on CHR, without SFP ports. If a type looks wrong on a RouterBOARD, the output of `pnmtd network scan <router> --raw` shows what the router reported.
- **The plans:** docs/network-map-plan.md and docs/network-map-positions-plan.md, with the decisions and what was built.
