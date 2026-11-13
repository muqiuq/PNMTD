# 2026-11-12 23:25:04 by RouterOS 7.25.1
# system id = 2v2Zs7RKtIF
#
/interface ethernet set [ find default-name=ether1 ] disable-running-check=no
/interface wireguard add comment=pnmt:test listen-port=13231 mtu=1420 name=pnmt
/interface wireguard peers add allowed-address=10.253.0.0/30 !client-listen-port comment=pnmt:test endpoint-address=vpn.example.com endpoint-port=51820 interface=pnmt name=peer1 persistent-keepalive=25s public-key="gNbcvXPEr5wWdZ+bMYMkU5FdB+LYzuD7RFDhvQIW3w4="
/ip address add address=10.253.0.17 comment=pnmt:test interface=pnmt network=10.253.0.17
/ip dhcp-client add interface=ether1 name=client1
/ip firewall filter add action=accept chain=input comment=pnmt:test dst-port=22 in-interface=pnmt protocol=tcp src-address=10.253.0.1
/ip firewall filter add action=accept chain=input comment=pnmt:test dst-port=22,8291,80,443 in-interface=pnmt protocol=tcp src-address=10.253.0.2
/ip firewall filter add action=accept chain=input comment="lab ssh" src-address=10.0.2.2
/ip firewall filter add action=accept chain=input comment=defconf connection-state=established,related
/ip firewall filter add action=drop chain=input comment="defconf: drop all"
/ip firewall filter add action=fasttrack-connection chain=forward comment=defconf connection-state=established,related
/ip route add comment=pnmt:test dst-address=10.253.0.0/30 gateway=pnmt
/system identity set name=r25
/user group add comment=pnmt:test name=pnmt-read policy=ssh,read,!local,!telnet,!ftp,!reboot,!write,!policy,!test,!winbox,!password,!web,!sniff,!sensitive,!api,!romon,!rest-api
/user ssh-keys add info=pnmt@lab key="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJHfraviAflbzG+6DsQRh5WgMrkjjvKHPvsoPjc7cLw+" user=pnmt
