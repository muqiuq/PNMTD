# 2026-11-12 23:24:44 by RouterOS 7.13
# software id = 
#
/interface ethernet set [ find default-name=ether1 ] disable-running-check=no
/interface wireguard add comment=pnmt:test listen-port=13231 mtu=1420 name=pnmt
/port set 0 name=serial0
/user group add comment=pnmt:test name=pnmt-read policy=ssh,read,!local,!telnet,!ftp,!reboot,!write,!policy,!test,!winbox,!password,!web,!sniff,!sensitive,!api,!romon,!rest-api
#error exporting "/interface/bridge/calea"
/interface wireguard peers add allowed-address=10.253.0.0/30 comment=pnmt:test endpoint-address=vpn.example.com endpoint-port=51820 interface=pnmt persistent-keepalive=25s public-key="gNbcvXPEr5wWdZ+bMYMkU5FdB+LYzuD7RFDhvQIW3w4="
/ip address add address=10.253.0.17 comment=pnmt:test interface=pnmt network=10.253.0.17
/ip dhcp-client add interface=ether1
#error exporting "/ip/firewall/calea"
/ip firewall filter add action=accept chain=input comment=pnmt:test dst-port=22 in-interface=pnmt protocol=tcp src-address=10.253.0.1
/ip firewall filter add action=accept chain=input comment=pnmt:test dst-port=22,8291,80,443 in-interface=pnmt protocol=tcp src-address=10.253.0.2
/ip route add comment=pnmt:test dst-address=10.253.0.0/30 gateway=pnmt
/system identity set name=r13
/system note set show-at-login=no
