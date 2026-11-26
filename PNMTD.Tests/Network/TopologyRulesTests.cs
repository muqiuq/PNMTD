using PNMTD.Lib.Network;

namespace PNMTD.Tests.Network;

/// <summary>The skip rules' text and matching (docs/network-suggestion-rules-plan.md §4.1).</summary>
[TestClass]
public sealed class TopologyRulesTests
{
    [TestMethod]
    [DataRow("vlan*", "vlan10", true)]
    [DataRow("vlan*", "VLAN10", true)]
    [DataRow("vlan*", "ether1", false)]
    [DataRow("ether1?", "ether10", true)]
    [DataRow("ether1?", "ether1", false)]
    [DataRow("*-guest", "bridge-guest", true)]
    [DataRow("sfp-sfpplus1", "sfp-sfpplus1", true)]
    [DataRow("sfp.sfpplus1", "sfp-sfpplus1", false)]
    public void Patterns_match_whole_names(string pattern, string name, bool matches) =>
        Assert.AreEqual(matches, SkipRuleSyntax.Glob(pattern).IsMatch(name));

    [TestMethod]
    public void VLAN_lists_parse_and_bad_ones_are_refused()
    {
        CollectionAssert.AreEqual(new[] { new VlanRange(10, 10), new VlanRange(20, 29), new VlanRange(99, 99) },
            SkipRuleSyntax.ParseVlans(" 10, 20-29 ,99")!.ToArray());
        Assert.AreEqual("10,20-29", SkipRuleSyntax.FormatVlans(SkipRuleSyntax.ParseVlans("10,20-29")!));
        Assert.AreEqual(0, SkipRuleSyntax.ParseVlans("")!.Count);
        foreach (var bad in new[] { "0", "4095", "20-10", "ten", "1-2-3", ",", "-5" })
        {
            Assert.IsNull(SkipRuleSyntax.ParseVlans(bad), bad);
        }
    }

    [TestMethod]
    public void MAC_prefixes_are_normalised()
    {
        Assert.AreEqual("00:50:56", SkipRuleSyntax.MacPrefix("00-50-56"));
        Assert.AreEqual("00:50:56", SkipRuleSyntax.MacPrefix("005056"));
        Assert.AreEqual("00:00:5E:00:01", SkipRuleSyntax.MacPrefix("00:00:5e:00:01"));
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", SkipRuleSyntax.MacPrefix("aabb.ccdd.eeff"));
        foreach (var bad in new[] { "0", "00:5", "zz", "00:11:22:33:44:55:66" })
        {
            Assert.IsNull(SkipRuleSyntax.MacPrefix(bad), bad);
        }
    }

    [TestMethod]
    public void A_rule_with_a_VLAN_skips_rows_but_never_a_whole_port()
    {
        var router = Guid.NewGuid();
        var rule = new TopologyRule { Routers = new HashSet<Guid> { router }, InterfacePattern = "ether2", Vlans = [new VlanRange(20, 20)] };
        var row = new Observation(router, ObservationSource.BridgeHost) { Interface = "ether2", VlanId = 20, Mac = "00:11:22:33:44:55" };
        Assert.IsTrue(rule.Matches(row, ["ether2"], _ => "ether", 20));
        Assert.IsFalse(rule.Matches(row, ["ether2"], _ => "ether", 30));
        Assert.IsFalse(rule.MatchesEnd(router, ["ether2"], _ => "ether"));
        Assert.IsTrue(new TopologyRule { Routers = new HashSet<Guid> { router }, InterfacePattern = "ether2" }.MatchesEnd(router, ["ether2"], _ => "ether"));
        Assert.IsFalse(new TopologyRule { Routers = new HashSet<Guid> { router }, InterfacePattern = "ether2" }.MatchesEnd(Guid.NewGuid(), ["ether2"], _ => "ether"),
            "another router");
    }

    [TestMethod]
    public void The_routers_own_facts_and_leases_are_no_evidence_for_a_table_rule()
    {
        var rule = new TopologyRule { Evidence = SkipEvidence.MacTable | SkipEvidence.Arp };
        var lease = new Observation(Guid.NewGuid(), ObservationSource.DhcpLease) { Interface = "bridge", Mac = "00:11:22:33:44:55" };
        Assert.IsFalse(rule.Matches(lease, ["bridge"], _ => "bridge", null));
        var arp = lease with { Source = ObservationSource.Arp };
        Assert.IsTrue(rule.Matches(arp, ["bridge"], _ => "bridge", null));
    }
}
