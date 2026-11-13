using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PNMTD.Lib.WireGuard;
using PNMTD.WireGuard;
using PNMTD.WireGuard.Gateway;

namespace PNMTD.Tests.Mikrotik;

/// <summary>The WireGuard network's pure parts and the gateway's SOCKS5 server (docs/mikrotik-plan.md §3, §14).</summary>
[TestClass]
public sealed class WireGuardTests
{
    [TestMethod]
    public void The_address_plan()
    {
        var plan = AddressPlan.Default;
        Assert.AreEqual("10.253.0.0/16", plan.ToString());
        Assert.AreEqual("10.253.0.1", plan.ServerAddress.ToString());
        Assert.AreEqual("10.253.0.2", plan.AccessAddress.ToString());
        Assert.AreEqual("10.253.0.0/30", plan.ServerBlock);
        Assert.AreEqual("10.253.0.4", plan.Allocate([])!.ToString(), "after the server block");
        Assert.AreEqual("10.253.0.6", plan.Allocate([IPAddress.Parse("10.253.0.4"), IPAddress.Parse("10.253.0.5")])!.ToString());
        Assert.IsTrue(plan.IsPeerAddress(IPAddress.Parse("10.253.0.17")));
        Assert.IsFalse(plan.IsPeerAddress(IPAddress.Parse("10.253.0.1")));
        Assert.IsFalse(plan.IsPeerAddress(IPAddress.Parse("10.253.255.255")), "the broadcast address");
        Assert.IsFalse(plan.Contains(IPAddress.Parse("10.254.0.4")));

        var small = AddressPlan.Parse("192.168.77.0/28");
        Assert.AreEqual(11, small.Capacity);
        var all = Enumerable.Range(4, 11).Select(i => IPAddress.Parse($"192.168.77.{i}")).ToList();
        Assert.IsNull(small.Allocate(all), "full");
        Assert.AreEqual("192.168.77.17", plan.Move(IPAddress.Parse("10.253.0.17"), AddressPlan.Parse("192.168.77.0/27"))?.ToString());
        Assert.IsNull(plan.Move(IPAddress.Parse("10.253.1.0"), small), "does not fit");

        Assert.IsFalse(AddressPlan.TryParse("fd00::/64", out _, out var v6));
        StringAssert.Contains(v6, "IPv4");
        Assert.IsFalse(AddressPlan.TryParse("10.0.0.0/30", out _, out _), "too small");
        Assert.AreEqual("10.253.0.0/16", AddressPlan.Parse("10.253.7.9/16").ToString(), "host bits cleared");
    }

    [TestMethod]
    public void Keys_as_wg_makes_them()
    {
        // RFC 7748 §6.1, Alice.
        var alice = Convert.ToBase64String(Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));
        Assert.AreEqual(Convert.ToBase64String(Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")),
            WireGuardKeys.PublicKey(alice));
        // A pair from `wg genkey | wg pubkey` (M0), as RouterOS showed it.
        Assert.AreEqual("EUxIa0PxM3uZ1E4i1IpNyFk/8/I9jtkuDP1tEGiIVnY=", WireGuardKeys.PublicKey("UGsL4e17bQnWs1rb8vOGl/gGTpPHz1aec5UTmTqv+Xw="));
        var (privateKey, publicKey) = WireGuardKeys.Generate();
        Assert.IsTrue(GatewayConfig.IsKey(privateKey) && GatewayConfig.IsKey(publicKey));
        Assert.AreEqual(publicKey, WireGuardKeys.PublicKey(privateKey));
        var raw = Convert.FromBase64String(privateKey);
        Assert.AreEqual(0, raw[0] & 7, "clamped");
        Assert.AreEqual(64, raw[31] & 0xC0);
        Assert.IsFalse(GatewayConfig.IsKey("not a key"));
    }

    [TestMethod]
    public void The_gateways_configuration_and_status()
    {
        var conf = GatewayConfig.SyncConf("PRIV=", 51820, [new GatewayPeer("B=", "10.253.0.5"), new GatewayPeer("A=", "10.253.0.4")]);
        Assert.AreEqual("[Interface]\nPrivateKey = PRIV=\nListenPort = 51820\n\n[Peer]\nPublicKey = A=\nAllowedIPs = 10.253.0.4/32\n\n" +
            "[Peer]\nPublicKey = B=\nAllowedIPs = 10.253.0.5/32\n", conf);
        var dump = "cHJpdg==\tcHVi\t51820\toff\n" +
                   "A=\t(none)\t203.0.113.7:13231\t10.253.0.4/32\t1793623193\t1234\t5678\toff\n" +
                   "B=\t(none)\t(none)\t10.253.0.5/32\t0\t0\t0\toff\n";
        var peers = GatewayConfig.ParseDump(dump);
        Assert.AreEqual(2, peers.Count);
        Assert.AreEqual("203.0.113.7:13231", peers[0].Endpoint);
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(1793623193), peers[0].LatestHandshake);
        Assert.AreEqual(1234L, peers[0].RxBytes);
        Assert.IsNull(peers[1].Endpoint);
        Assert.IsNull(peers[1].LatestHandshake, "0 is never");
    }

    [TestMethod]
    public void The_firewall_denies_by_default()
    {
        var closed = NftRuleset.Build("wg0", "10.253.0.2", [22, 8291], []);
        StringAssert.StartsWith(closed, "table inet pnmt {}\ndelete table inet pnmt\n");
        StringAssert.Contains(closed, "type filter hook forward priority 0; policy drop;");
        StringAssert.Contains(closed, "iifname \"wg0\" drop");
        Assert.IsFalse(closed.Contains("dport", StringComparison.Ordinal), "nothing forwarded without tunnels");
        var open = NftRuleset.Build("wg0", "10.253.0.2", [8291, 22, 22], [new TunnelAccess("10.253.0.40", ["10.253.0.17", "10.253.0.5"])]);
        StringAssert.Contains(open, "ip saddr 10.253.0.40 ip daddr { 10.253.0.17, 10.253.0.5 } tcp dport { 22, 8291 } accept");
        StringAssert.Contains(open, "snat ip to 10.253.0.2");
    }

    [TestMethod]
    public void A_tunnel_file_in_wg_quick_format()
    {
        var conf = WgQuickConfig.Build("alice \"laptop\"", "pnmt.example.com", "PRIV=", "10.253.0.40", 1420, "GW=", "vpn.example.com:51820", "10.253.0.0/16", 25);
        Assert.AreEqual("# PNMT tunnel \"alice \"laptop\"\" (pnmt.example.com).\n# Contains a private key: keep it like a password.\n" +
            "[Interface]\nPrivateKey = PRIV=\nAddress = 10.253.0.40/32\nMTU = 1420\n\n[Peer]\nPublicKey = GW=\nEndpoint = vpn.example.com:51820\n" +
            "# Only the tunnel network: the device's other traffic is untouched.\nAllowedIPs = 10.253.0.0/16\nPersistentKeepalive = 25\n", conf);
        Assert.AreEqual("alice--laptop.conf", WgQuickConfig.FileName("alice \"laptop\""));
        Assert.AreEqual("a-very-long-tun.conf", WgQuickConfig.FileName("a very long tunnel name"));
        Assert.AreEqual("pnmt.conf", WgQuickConfig.FileName("äöü"));
    }

    // --- SOCKS5 ---------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Socks_connects_only_to_allowed_router_ports_with_the_password()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await echo.AcceptTcpClientAsync();
                _ = Task.Run(async () => { using (c) await c.GetStream().CopyToAsync(c.GetStream()); });
            }
        });
        await using var socks = new Socks5Server(0, "pnmtd", () => "secret", a => a.ToString().StartsWith("10.253.", StringComparison.Ordinal), NullLogger.Instance);
        socks.Start();
        socks.SetAllowed(new Dictionary<string, int> { ["127.0.0.1"] = echoPort });

        // Allowed: the connection carries bytes both ways.
        using (var client = await ConnectAsync(socks.Port, "pnmtd", "secret", IPAddress.Loopback, echoPort, expect: 0))
        {
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-test\r\n"));
            var buffer = new byte[14];
            await stream.ReadExactlyAsync(buffer);
            Assert.AreEqual("SSH-2.0-test\r\n", Encoding.ASCII.GetString(buffer));
        }
        // Another port of an allowed address: not allowed (2).
        using (await ConnectAsync(socks.Port, "pnmtd", "secret", IPAddress.Loopback, echoPort + 1, expect: 2)) { }
        // A wrong password ends at the login.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, socks.Port);
            var s = client.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 2 });
            await ExpectAsync(s, [5, 2]);
            await s.WriteAsync(Login("pnmtd", "wrong"));
            await ExpectAsync(s, [1, 1]);
        }
        // Only username and password: no authentication is refused (0xFF).
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, socks.Port);
            var s = client.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 0 });
            await ExpectAsync(s, [5, 0xFF]);
        }
    }

    private static async Task<TcpClient> ConnectAsync(int socksPort, string user, string password, IPAddress target, int port, byte expect)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socksPort);
        var s = client.GetStream();
        await s.WriteAsync(new byte[] { 5, 1, 2 });
        await ExpectAsync(s, [5, 2]);
        await s.WriteAsync(Login(user, password));
        await ExpectAsync(s, [1, 0]);
        var request = new byte[10];
        request[0] = 5;
        request[1] = 1;
        request[3] = 1;
        target.GetAddressBytes().CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)port);
        await s.WriteAsync(request);
        var reply = new byte[10];
        await s.ReadExactlyAsync(reply);
        Assert.AreEqual(expect, reply[1]);
        return client;
    }

    private static byte[] Login(string user, string password) =>
        [1, (byte)user.Length, .. Encoding.UTF8.GetBytes(user), (byte)password.Length, .. Encoding.UTF8.GetBytes(password)];

    private static async Task ExpectAsync(NetworkStream s, byte[] expected)
    {
        var buffer = new byte[expected.Length];
        await s.ReadExactlyAsync(buffer);
        CollectionAssert.AreEqual(expected, buffer);
    }
}
