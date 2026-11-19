using Microsoft.EntityFrameworkCore;
using PNMTD.Auth;
using PNMTD.Models.Db;
using static PNMTD.Tests.Connectors.ConnectorHarness;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// An agent's answers to PNMTD's requests (acks, script and parse results) count with the reports, not the messages per minute
/// (docs/windows-sensor-plan.md §8.7): a server busy with a connector's calls sends two per run, and a dropped result left its run
/// "sent" until it was closed as lost.
/// </summary>
[TestClass]
public sealed class AgentAnswersTests
{
    [TestMethod]
    public async Task Results_beyond_the_messages_per_minute_still_arrive()
    {
        using var app = new TestApp(settings: Settings(new Dictionary<string, string?> { ["Agents:MessagesPerMinute"] = "3" }), services: Install);
        var admin = app.Client(Roles.Admin);
        await using var server = await ServerAsync(app, admin, "cx-answers");
        var connector = await ConnectorAsync(admin, Request($"Answers {Guid.NewGuid().ToString("N")[..6]}", server.Name, scriptName: "answers.sh"), BackupArguments);
        var caller = await CallerAsync(admin, "Answers", [connector.Id], "10.20.0.0/24");
        for (var i = 0; i < 4; i++)
        {
            var body = await JsonAsync(await CallAsync(Client(app, caller.Key), connector.Slug, new { arguments = new { job = "nightly" } }));
            Assert.AreEqual("done", body.GetProperty("state").GetString(), $"call {i + 1}: {body}");
        }
        await using var db = app.NewDb();
        Assert.AreEqual(4, await db.AgentScriptRuns.CountAsync(r => r.ConnectorId == connector.Id && r.State == Lib.Agent.ScriptRunState.Done));
        Assert.IsFalse(await db.AgentLog.AnyAsync(l => l.SensorId == server.SensorId && l.Message.StartsWith("Too many messages")));
    }
}
