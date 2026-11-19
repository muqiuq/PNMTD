using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PNMTD.Hosting;

namespace PNMTD.Tests.Hosting;

/// <summary>
/// <c>Proxy</c> (docs/connectors-plan.md §6.4): docker, an address, a network and a list. X-Forwarded-For from a trusted
/// proxy sets the client's address, from anyone else it is ignored, and a bad entry stops the start.
/// </summary>
[TestClass]
public sealed class ForwardedHeadersTests
{
    private static async Task<string?> ClientAsync(string proxy, string from, string forwardedFor = "203.0.113.9")
    {
        var options = new ForwardedHeadersOptions();
        ProxySetting.Configure(options, proxy);
        string? seen = null;
        var middleware = new ForwardedHeadersMiddleware(c =>
        {
            seen = c.Connection.RemoteIpAddress?.ToString();
            return Task.CompletedTask;
        }, NullLoggerFactory.Instance, Options.Create(options));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(from);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        await middleware.Invoke(context);
        return seen;
    }

    [TestMethod]
    [DataRow("172.18.0.5")]
    [DataRow("::ffff:172.18.0.5")]
    [DataRow("172.31.255.254")]
    public async Task Docker_trusts_the_bridge_range_mapped_or_not(string from) =>
        Assert.AreEqual("203.0.113.9", await ClientAsync("docker", from));

    [TestMethod]
    [DataRow("172.32.0.1")]
    [DataRow("192.168.1.80")]
    [DataRow("::ffff:10.0.0.1")]
    public async Task Docker_ignores_everyone_else(string from) =>
        Assert.AreEqual(IPAddress.Parse(from).ToString(), await ClientAsync("docker", from));

    [TestMethod]
    public async Task An_address_and_a_list()
    {
        Assert.AreEqual("203.0.113.9", await ClientAsync("192.168.1.80", "192.168.1.80"));
        Assert.AreEqual("203.0.113.9", await ClientAsync("192.168.1.80", "::ffff:192.168.1.80"));
        Assert.AreEqual("203.0.113.9", await ClientAsync("::ffff:192.168.1.80", "192.168.1.80"));
        Assert.AreEqual("192.168.1.81", await ClientAsync("192.168.1.80", "192.168.1.81"));
        Assert.AreEqual("203.0.113.9", await ClientAsync("docker, 10.0.0.5, 2001:db8::/32", "10.0.0.5"));
        Assert.AreEqual("203.0.113.9", await ClientAsync("docker, 10.0.0.5, 2001:db8::/32", "2001:db8::17"));
        Assert.AreEqual("203.0.113.9", await ClientAsync("docker, 10.0.0.5, 2001:db8::/32", "172.20.0.2"));
        Assert.AreEqual("10.0.0.6", await ClientAsync("docker, 10.0.0.5, 2001:db8::/32", "10.0.0.6"));
    }

    [TestMethod]
    public async Task The_chain_is_read_from_the_right_up_to_the_first_untrusted_address()
    {
        // A client writes its own X-Forwarded-For; the trusted proxy appends the client's real address.
        Assert.AreEqual("198.51.100.7", await ClientAsync("docker", "172.18.0.5", "10.20.0.17, 198.51.100.7"));
        Assert.AreEqual("10.20.0.17", await ClientAsync("docker, 198.51.100.0/24", "172.18.0.5", "10.20.0.17, 198.51.100.7"));
    }

    [TestMethod]
    [DataRow("dokker")]
    [DataRow("10.0.0.5/24")]
    [DataRow("10.0.0.0/33")]
    [DataRow(",")]
    [DataRow("docker, 300.1.1.1")]
    public void A_bad_entry_stops_the_start(string value)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => ProxySetting.Parse(value));
        StringAssert.StartsWith(error.Message, "Proxy: ");
    }
}
