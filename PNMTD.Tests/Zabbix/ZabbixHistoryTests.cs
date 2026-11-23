using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Dev;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Tests.Zabbix;

/// <summary>
/// The boards' history of Zabbix's problems (docs/boards-plan.md §7.5): the poll asks with <c>recent</c>, keeps the resolved ones out of the
/// snapshot, and records every shown problem with its recovery; one that leaves unseen gets an estimate; old ones are cleaned up.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ZabbixHistoryTests
{
    private const long Open = 900101;
    private const long Resolved = 900102;
    private const long Old = 900199;

    private static readonly FakeZabbix zabbix = new();
    private static TestApp app = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: FakeZabbix.Settings, services: zabbix.Install);
        zabbix.Load("70");
        app.Services.GetRequiredService<ZabbixClient>().ForgetVersion();
        app.Services.GetRequiredService<ZabbixSnapshot>().Clear();
        app.Services.GetRequiredService<ZabbixPoller>().Forget();
        await app.Services.GetRequiredService<ZabbixGroups>().ResolveAsync(true, CancellationToken.None);
        await using var db = app.NewDb();
        await db.ZabbixProblemHistory.Where(r => r.EventId >= 900100 && r.EventId < 900200).ExecuteDeleteAsync();
        var now = Clock.UtcNow;
        db.ZabbixProblemHistory.Add(new ZabbixProblemHistoryEntity
        {
            EventId = Old, TriggerId = 1, Name = "Long ago", StartedAt = now.AddDays(-12), ResolvedAt = now.AddDays(-10), FirstSeenAt = now.AddDays(-12),
            LastSeenAt = now.AddDays(-10),
        });
        await db.SaveChangesAsync();
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.ZabbixProblemHistory.Where(r => r.EventId >= 900100 && r.EventId < 900200).ExecuteDeleteAsync();
        }
        app.Dispose();
    }

    /// <summary>A copy of a problem Zabbix shows (its trigger is monitored and in PNMT Servers), with an event id of its own.</summary>
    private static JsonObject Copy(long eventId, DateTime? resolved)
    {
        var shown = app.Services.GetRequiredService<ZabbixSnapshot>().Current.Problems.First();
        var source = zabbix.Problems.Single(p => p!["eventid"]!.GetValue<string>() == shown.EventId)!;
        var copy = JsonNode.Parse(source.ToJsonString())!.AsObject();
        copy["eventid"] = eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        copy["clock"] = new DateTimeOffset(Clock.UtcNow.AddMinutes(-10)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        copy["r_eventid"] = resolved is null ? "0" : (eventId + 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        copy["r_clock"] = resolved is { } r ? new DateTimeOffset(r).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        return copy;
    }

    [TestMethod]
    public async Task ThePollKeepsTheShownProblemsWithTheirRecovery()
    {
        var poller = app.Services.GetRequiredService<ZabbixPoller>();
        var snapshot = app.Services.GetRequiredService<ZabbixSnapshot>();
        await poller.RunAsync(CancellationToken.None);
        Assert.IsNull(snapshot.Current.Error, snapshot.Current.Error);
        var shown = snapshot.Current.Problems.Select(p => long.Parse(p.EventId, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.ZabbixProblemHistory.AnyAsync(r => r.EventId == Old), "resolved more than Zabbix__HistoryDays ago: cleaned up");
            var rows = await db.ZabbixProblemHistory.AsNoTracking().Where(r => shown.Contains(r.EventId)).ToListAsync();
            Assert.AreEqual(shown.Count, rows.Count, "every problem Zabbix shows is in the history");
            Assert.IsTrue(rows.All(r => r.ResolvedAt is null && r.HostNames.Count > 0));
        }

        // Whole seconds, from one read of the clock (as Zabbix's clock is).
        var read = Clock.UtcNow;
        var resolvedAt = new DateTime(read.Ticks - read.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(-30);
        zabbix.Problems.Add(Copy(Open, null));
        zabbix.Problems.Add(Copy(Resolved, resolvedAt));
        await poller.RunAsync(CancellationToken.None);
        Assert.IsTrue(snapshot.Current.Problems.Any(p => p.EventId == Open.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.IsFalse(snapshot.Current.Problems.Any(p => p.EventId == Resolved.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "a resolved problem never reaches the snapshot: its readers see what they saw before");
        lock (zabbix.Calls)
        {
            var asked = zabbix.Calls.Last(c => c.Method == "problem.get").Params;
            Assert.IsTrue(asked["recent"]!.GetValue<bool>());
            CollectionAssert.Contains(asked["output"]!.AsArray().Select(o => o!.GetValue<string>()).ToList(), "r_clock");
        }
        DateTime lastSeen;
        await using (var db = app.NewDb())
        {
            var open = await db.ZabbixProblemHistory.AsNoTracking().SingleAsync(r => r.EventId == Open);
            Assert.IsNull(open.ResolvedAt);
            lastSeen = open.LastSeenAt;
            var resolved = await db.ZabbixProblemHistory.AsNoTracking().SingleAsync(r => r.EventId == Resolved);
            Assert.AreEqual(resolvedAt, resolved.ResolvedAt, "Zabbix's recovery time");
            Assert.IsNull(resolved.ResolvedFrom, "seen, not estimated");
        }

        // Gone without a recovery seen (as after PNMT was down): resolved between its last sighting and this poll.
        zabbix.Problems.Remove(zabbix.Problems.Single(p => p!["eventid"]!.GetValue<string>() == Open.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await poller.RunAsync(CancellationToken.None);
        await using (var db = app.NewDb())
        {
            var gone = await db.ZabbixProblemHistory.AsNoTracking().SingleAsync(r => r.EventId == Open);
            Assert.IsNotNull(gone.ResolvedAt);
            Assert.AreEqual(lastSeen, gone.ResolvedFrom);
            Assert.IsTrue(gone.ResolvedAt >= gone.ResolvedFrom);
        }
    }
}
