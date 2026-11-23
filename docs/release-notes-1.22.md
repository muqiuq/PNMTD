# PNMT 1.22: MikroTik join scripts with options, and differences acknowledged

## What's new

### The join script's options

- **Two checkboxes above a router's join script** in its *Setup* tab (docs/mikrotik-join-options-plan.md §2):
  - ***Firewall rules*** (only for the modes whose script has rules: WireGuard and SSH, and WireGuard only with management access).
    Without it, the script doesn't touch the router's firewall, and it shows the rules as comments to add by hand where wanted.
  - ***Remove what an earlier script created.*** Without it, the script removes nothing: it adds what is missing and sets what is
    there, and imports PNMTD's SSH key only when the user `pnmt` doesn't have it yet. It still removes its own key file after the import.
- **With both on, the script is exactly the one of 1.21.** Each router keeps the options its script was last shown with, so the next
  script starts from them.
- **The CLI:** `pnmtd mikrotik script` takes `--no-firewall` and `--no-removals` (and `--firewall`, `--removals`).

### Differences acknowledged as intended

- **When a router differs from its join script on purpose,** *Acknowledge…* (in the *Setup* tab and in the status strip) takes the
  differences to acknowledge and a reason, required, at most 500 characters (§3). The warning goes.
- **An acknowledged difference stays in the *Setup* tab,** greyed out, with who, when and why, until what it covers changes: a different
  value on the router or in PNMT opens it again. There is no expiry. *Remove acknowledgement* opens it again by hand.
- **Rights:** *Monitoring: Edit* acknowledges. The differences in what only an Admin can change (the policies, the management rule and
  retired keys) need *MikroTik & WireGuard: Manage*.
- **Every acknowledgement and every removal is in the audit log,** one entry per difference, also in the router's own history.

### Fixed

- **The Routers tab and the overview count differences as the *Setup* tab does.** Before, they never counted a WireGuard-only router's
  differences, and none of the tunnel's.

## Updating from 1.21

In the `deploy/` folder: `git pull`, then `./update.sh --tag v1.22.0`. It takes a backup first.

- **One migration runs at the start:** `MikrotikJoinOptions` adds three columns to the routers' table. Existing routers start with both
  options on, so their next script is as before.
- **A router that still accepts a retired PNMTD SSH key shows a new difference** (*retired-key*) until its new join script is pasted.
  Routers whose key was rotated and whose script was pasted again are not affected.
- **Nothing to configure.**

## Good to know

- **Without removals, a script can't clean up,** so what an earlier script made under another name or tag stays. The *Setup* tab says
  "The join script removes nothing:" in the differences that only a removal would fix.
- **Without firewall rules, the *Setup* tab no longer compares the firewall,** so its management rule is never a difference.
- **An acknowledgement covers the difference exactly as it was seen.** Pasting the new script, or changing a setting, makes it match or
  differ anew; it doesn't carry an old acknowledgement over.
- **Tested on RouterOS 7.13 and 7.25.1** (CHR): several pastes without removals, then the defaults, then the removal script, without a
  RouterOS error. Not tested: whether a paste through the tunnel drops a WinBox session open through it.
- **`pnmtd dev seed`** gives `office-basel-gw` one difference to acknowledge: Diagnostics on, and its group without the `test` policy.
- **The plan:** docs/mikrotik-join-options-plan.md, with the lab's findings and, in *As built*, where the build differs from it.
