# VLAN fixtures (docs/network-suggestion-rules-plan.md §10, V0)

Recorded on 2026-11-26 from two CHRs 7.25.1 (x86_64), `vla` and `vlb`, joined by a cable: `tools/chr-lab.sh pair vla vlb`
gives each an `ether2` on a UDP socket between their containers. Each table is the scan's own script
(`PNMTD.Lib/Network/NetworkScanScript.cs`), run as admin; neighbor discovery on all interfaces, with MNDP, LLDP and CDP.

| Folder | The routers' configuration (`vla` .1, `vlb` .2) |
|---|---|
| `on-port` | `vlan10` and `vlan20` on `ether2`; addresses 10.77.0.x on `ether2`, 10.77.10.x and 10.77.20.x on the VLANs |
| `bridge` | `bridge1` with `vlan-filtering=yes`, `ether2` its port, VLANs 10 and 20 tagged on both; `vlan10` and `vlan20` on `bridge1`; the addresses on `bridge1` and the VLANs |
| `bridge-per-vlan` | `vlan10` and `vlan20` on `ether2`, each the port of a bridge of its own (`br10`, `br20`); the addresses on `ether2`, `br10` and `br20` |

What they show is noted in `NetworkScanScript`'s remarks. `NetworkPureTests` parses them and analyses each pair: one
suggestion, `ether2` ↔ `ether2`.
