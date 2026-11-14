using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Tests.Zabbix;

/// <summary>The ZABBIX sensor's rules (docs/audit-redmine-zabbix-plan.md §7.7), without Zabbix.</summary>
[TestClass]
public sealed class ZabbixSensorRuleTests
{
    private static readonly ZabbixHostEntity Host = new()
    {
        HostId = 1, TechnicalName = "web01", VisibleName = "Web 01", Monitored = true,
        Interfaces = """[{"type":"Agent","address":"10.0.4.17","port":"10050","availability":"Unavailable","error":"refused","main":true}]""",
    };

    private static ZabbixProblem Problem(string id, ZabbixSeverity severity, string name, bool acknowledged = false, bool suppressed = false, int minutesAgo = 5) =>
        new(id, "t" + id, name, severity, DateTime.UtcNow.AddMinutes(-minutesAgo), acknowledged, 0, suppressed, null, [], [new ZxTriggerHost("1", "web01", "Web 01")]);

    private static readonly IReadOnlyList<ZabbixProblem> Problems =
    [
        Problem("1", ZabbixSeverity.High, "/var is 95 % full", acknowledged: true, minutesAgo: 30),
        Problem("2", ZabbixSeverity.Average, "High CPU load"),
        Problem("3", ZabbixSeverity.Warning, "Replication lag", suppressed: true),
    ];

    [TestMethod]
    public void EachRuleGivesItsState()
    {
        var rules = new ZabbixSensorEntity { MinSeverity = ZabbixSeverity.Average };
        var two = ZabbixSensorService.Evaluate(rules, Host, Problems);
        Assert.AreEqual(PNMTStatusCodes.ZABBIX_PROBLEM, two.Code);
        Assert.AreEqual("2 problems: High: /var is 95 % full; Average: High CPU load", two.Message);
        CollectionAssert.AreEquivalent(new[] { "1", "2" }, two.ProblemIds.ToArray());

        var unacknowledged = ZabbixSensorService.Evaluate(new ZabbixSensorEntity { MinSeverity = ZabbixSeverity.Average, CountAcknowledged = false }, Host, Problems);
        Assert.AreEqual("1 problem: Average: High CPU load", unacknowledged.Message);

        var ok = ZabbixSensorService.Evaluate(new ZabbixSensorEntity { MinSeverity = ZabbixSeverity.Disaster }, Host, Problems);
        Assert.AreEqual((PNMTStatusCodes.ZABBIX_OK, "No problems at Disaster or above (1 High, 1 Average)"), (ok.Code, ok.Message), "suppressed left out");

        var suppressed = ZabbixSensorService.Evaluate(new ZabbixSensorEntity { MinSeverity = ZabbixSeverity.Warning, CountSuppressed = true }, Host, Problems);
        Assert.AreEqual(3, suppressed.ProblemIds.Count);

        var unavailable = ZabbixSensorService.Evaluate(new ZabbixSensorEntity { MinSeverity = ZabbixSeverity.Disaster, UnavailableFails = true }, Host, Problems);
        Assert.AreEqual((PNMTStatusCodes.ZABBIX_HOST_UNAVAILABLE, "Agent interface 10.0.4.17:10050 unavailable: refused"), (unavailable.Code, unavailable.Message));
        Assert.AreEqual(PNMTStatusCodes.ZABBIX_OK, ZabbixSensorService.Evaluate(new ZabbixSensorEntity { MinSeverity = ZabbixSeverity.Disaster }, Host, []).Code,
            "unavailable fails only when the sensor says so");

        foreach (var gone in new[] { null, new ZabbixHostEntity { Monitored = true, GoneAt = DateTime.UtcNow }, new ZabbixHostEntity { Monitored = false } })
        {
            Assert.AreEqual(PNMTStatusCodes.ZABBIX_HOST_MISSING, ZabbixSensorService.Evaluate(rules, gone, Problems).Code);
        }

        var many = Enumerable.Range(1, 40).Select(i => Problem($"{i}", ZabbixSeverity.High, $"a long problem's name, number {i}")).ToList();
        var cut = ZabbixSensorService.Evaluate(rules, Host, many);
        Assert.AreEqual(ZabbixSensorService.MaxMessage, cut.Message.Length);
        Assert.AreEqual(40, cut.ProblemIds.Count);
        Assert.AreEqual("web01", ZabbixSensorService.FirstLabel("WEB01.example.com"));
        Assert.AreEqual("10.0.4.17", ZabbixSensorService.FirstLabel("10.0.4.17"));
    }
}
