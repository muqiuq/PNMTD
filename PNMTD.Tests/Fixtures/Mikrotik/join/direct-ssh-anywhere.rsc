# PNMT join script for "office "gw"" ("pnmt.example.com", generated 2026-11-12). Mode: Direct SSH.
# Everything it creates carries the comment "pnmt:7f3a" (this installation): pasting it again replaces it,
# and so does what an earlier PNMT installation created (a comment pnmt:…).
/ip firewall filter remove [find where comment~"^pnmt:"]
/ip route remove [find where comment~"^pnmt:"]
/ip address remove [find where comment~"^pnmt:"]
/interface wireguard peers remove [find where comment~"^pnmt:"]
/interface wireguard remove [find where comment~"^pnmt:"]
/user ssh-keys remove [find where user="pnmt"]
/user remove [find where comment~"^pnmt:"]
/user group remove [find where comment~"^pnmt:"]
/user group add name=pnmt-read policy=ssh,read,test comment="pnmt:7f3a"
/user add name=pnmt group=pnmt-read password=[:rndstr length=32] comment="pnmt:7f3a"
/file remove [find name="pnmt-1.pub"]
{ :local n 0; :while ([:len [/file find where name="pnmt-1.pub"]] > 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/file add name="pnmt-1.pub" contents="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJHfraviAflbzG+6DsQRh5WgMrkjjvKHPvsoPjc7cLw+ pnmt-abc123@pnmt.example.com"
{ :local n 0; :while ([:len [/file find where name="pnmt-1.pub"]] = 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/user ssh-keys import user=pnmt public-key-file="pnmt-1.pub"
/file remove [find name="pnmt-1.pub"]
:foreach s in=[/ip service find where name="ssh" and disabled=yes] do={ :put "PNMT: warning: the SSH service is disabled. Enable it with: /ip service enable ssh" }
# PNMT connects to this router's SSH port (22). If the firewall blocks it, allow PNMT's address, for example:
# /ip firewall filter add chain=input src-address=<PNMT's address> protocol=tcp dst-port=22 action=accept comment="pnmt:7f3a"
# Then move it above any rule that drops input (WinBox: drag it up; or /ip firewall filter move).
:put "PNMT: the user pnmt is ready. PNMT logs in at its next poll."
