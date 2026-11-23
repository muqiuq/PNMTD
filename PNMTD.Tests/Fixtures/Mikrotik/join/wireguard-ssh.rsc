# PNMT join script for "office "gw"" ("pnmt.example.com", generated 2026-11-12). Mode: WireGuard + SSH.
# It contains this router's WireGuard private key: keep it like a password.
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
{ :local n 0; :while ([:len [/interface find where name="pnmt"]] > 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
{ :local port 13231; :while ([:len [/interface wireguard find listen-port=$port]] > 0) do={ :set port ($port + 1) }; /interface wireguard add name=pnmt listen-port=$port mtu=1420 private-key="UGsL4e17bQnWs1rb8vOGl/gGTpPHz1aec5UTmTqv+Xw=" comment="pnmt:7f3a" }
{ :local n 0; :while ([:len [/interface find where name="pnmt"]] = 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/interface wireguard peers add interface=pnmt public-key="gNbcvXPEr5wWdZ+bMYMkU5FdB+LYzuD7RFDhvQIW3w4=" endpoint-address="vpn.example.com" endpoint-port=51820 allowed-address=10.253.0.0/30 persistent-keepalive=25s comment="pnmt:7f3a"
/ip address add address=10.253.0.17/32 interface=pnmt comment="pnmt:7f3a"
/ip route add dst-address=10.253.0.0/30 gateway=pnmt comment="pnmt:7f3a"
/ip firewall filter add chain=input in-interface=pnmt src-address=10.253.0.1 protocol=tcp dst-port=22 action=accept comment="pnmt:7f3a"
{ :local n 0; :while ([:len [/ip firewall filter find where comment="pnmt:7f3a" and invalid]] > 0 and $n < 50) do={ :delay 100ms; /ip firewall filter set [find where comment="pnmt:7f3a" and invalid] in-interface=pnmt; :set n ($n + 1) } }
{ :local all [/ip firewall filter find where dynamic=no]; :if ([/ip firewall filter get [:pick $all 0] comment] != "pnmt:7f3a") do={ /ip firewall filter move [find where comment="pnmt:7f3a"] destination=[:pick $all 0] } }
/user group add name=pnmt-read policy=ssh,read comment="pnmt:7f3a"
/user add name=pnmt group=pnmt-read address=10.253.0.1/32 password=[:rndstr length=32] comment="pnmt:7f3a"
/file remove [find name="pnmt-1.pub"]
{ :local n 0; :while ([:len [/file find where name="pnmt-1.pub"]] > 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/file add name="pnmt-1.pub" contents="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJHfraviAflbzG+6DsQRh5WgMrkjjvKHPvsoPjc7cLw+ pnmt-abc123@pnmt.example.com"
{ :local n 0; :while ([:len [/file find where name="pnmt-1.pub"]] = 0 and $n < 50) do={ :delay 100ms; :set n ($n + 1) } }
/user ssh-keys import user=pnmt public-key-file="pnmt-1.pub"
/file remove [find name="pnmt-1.pub"]
:foreach s in=[/ip service find where name="ssh" and disabled=yes] do={ :put "PNMT: warning: the SSH service is disabled. Enable it with: /ip service enable ssh" }
:put "PNMT: joined as 10.253.0.17, dialling vpn.example.com:51820. PNMT logs in within a minute."
