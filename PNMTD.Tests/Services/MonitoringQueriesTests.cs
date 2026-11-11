using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Tests.Services;

/// <summary>
/// The monitoring SQL (state, failing since, uptime per bucket, state changes) against event
/// timelines with known answers. Times are wall-clock times in the test zone (TestDefaults), so the
/// days are the zone's days.
/// </summary>
[TestClass]
public sealed class MonitoringQueriesTests
{
    private PnmtdDbContext db = null!;
    private Guid hostId;

    private static DateTime Local(int day, int hour) => Clock.ToUtc(new DateTime(2026, 6, day, hour, 0, 0));

    [TestInitialize]
    public void Init()
    {
        db = PnmtdDbContext.Create(DatabaseSettings.ConnectionString(TestDefaults.Configuration));
        hostId = Guid.NewGuid();
        db.Hosts.Add(new HostEntity { Id = hostId, Name = $"monitoring-{hostId:N}", Enabled = true, Created = Clock.UtcNow });
        db.SaveChanges();
        // Noon on 15 June, local time.
        Clock.Provider = new FakeTimeProvider(new DateTimeOffset(Local(15, 12)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        Clock.Provider = TimeProvider.System;
        db.Hosts.Where(h => h.Id == hostId).ExecuteDelete();
        db.Dispose();
    }

    private Guid Sensor(SensorType type = SensorType.SIMPLE, params (DateTime At, int Code)[] events)
    {
        var sensor = new SensorEntity
        {
            Id = Guid.NewGuid(), ParentId = hostId, Name = $"s-{events.Length}-{Guid.NewGuid():N}", Type = type, Enabled = true,
            Created = Clock.UtcNow, SecretWriteToken = Guid.NewGuid().ToString(), SecretReadToken = Guid.NewGuid().ToString(),
        };
        db.Sensors.Add(sensor);
        foreach (var (at, code) in events)
        {
            db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = sensor.Id, Created = at, Code = code, Message = $"m{code}", Source = "test" });
        }
        db.SaveChanges();
        return sensor.Id;
    }

    private MonitoringReader Reader() => new(db, new MonitoringQueries(db));

    [TestMethod]
    public async Task UptimePerLocalDayCountsOnlyTimeWithData()
    {
        // OK from the 13th at noon, failed from midnight to noon on the 14th, OK since.
        var s = Sensor(SensorType.SIMPLE, (Local(13, 12), 200), (Local(14, 0), 500), (Local(14, 12), 200));
        var none = Sensor();

        var days = await Reader().DaysAsync([s, none], 5, default);

        Assert.AreEqual(DateOnly.FromDateTime(new DateTime(2026, 6, 11)), days.Start);
        CollectionAssert.AreEqual(new double?[] { null, null, 1.0, 0.5, 1.0 }, days.Values(s));
        CollectionAssert.AreEqual(new double?[] { null, null, null, null, null }, days.Values(none));
        // 36 of 48 hours with data were OK.
        Assert.AreEqual(0.75, days.Overall(s, 5));
        // A sensor without data does not pull the host down (issue 15).
        CollectionAssert.AreEqual(days.Values(s), days.Average([s, none]));
    }

    [TestMethod]
    public async Task HostDaysAverageTheSensorsWithDataThatDay()
    {
        var s = Sensor(SensorType.SIMPLE, (Local(13, 12), 200), (Local(14, 0), 500), (Local(14, 12), 200));
        // Failed since 08:00 today: four hours of data, none of them OK.
        var u = Sensor(SensorType.SIMPLE, (Local(15, 8), 500));

        var days = await Reader().DaysAsync([s, u], 5, default);

        CollectionAssert.AreEqual(new double?[] { null, null, null, null, 0.0 }, days.Values(u));
        CollectionAssert.AreEqual(new double?[] { null, null, 1.0, 0.5, 0.5 }, days.Average([s, u]));
    }

    [TestMethod]
    public async Task TheEventBeforeTheWindowCarriesItsState()
    {
        var s = Sensor(SensorType.SIMPLE, (Local(1, 0), 500));

        var days = await Reader().DaysAsync([s], 3, default);

        CollectionAssert.AreEqual(new double?[] { 0.0, 0.0, 0.0 }, days.Values(s));
    }

    /// <summary>The 24-hour strip uses UTC hours, whatever the zone (one with a :45 offset included).</summary>
    [TestMethod]
    public async Task HourlyBucketsEndWithTheCurrentHour()
    {
        var now = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        Clock.Provider = new FakeTimeProvider(new DateTimeOffset(now));
        var s = Sensor(SensorType.SIMPLE, (now.AddHours(-2), 200), (now.AddHours(-1), 500));

        var hours = await Reader().HoursAsync([s], 4, default);

        // 07:00, 08:00, 09:00 and the current hour, which has only just begun: no time in it yet.
        CollectionAssert.AreEqual(new double?[] { null, 1.0, 0.0, null }, hours.Values(s));
    }

    [TestMethod]
    public async Task TimespanSensorsCountOnlyTheirEvaluations()
    {
        // A failed evaluation at 06:00; the reports after it (200) are what it evaluates, not its state.
        var t = Sensor(SensorType.ONE_WITHIN_TIMESPAN, (Local(15, 6), 480), (Local(15, 7), 200), (Local(15, 9), 200));

        var latest = await new MonitoringQueries(db).LatestAsync([t], default);
        var days = await Reader().DaysAsync([t], 1, default);

        Assert.AreEqual(480, latest[t].LastCode);
        Assert.AreEqual(Local(15, 6), latest[t].FailingSince);
        CollectionAssert.AreEqual(new double?[] { 0.0 }, days.Values(t));
    }

    [TestMethod]
    public async Task FailingSinceIsTheFirstFailureAfterTheLastSuccess()
    {
        var recovered = Sensor(SensorType.SIMPLE, (Local(14, 1), 500), (Local(14, 2), 200));
        var failing = Sensor(SensorType.SIMPLE, (Local(14, 1), 200), (Local(14, 2), 500), (Local(14, 3), 470));
        var neverOk = Sensor(SensorType.SIMPLE, (Local(14, 1), 500), (Local(14, 2), 500));
        var noData = Sensor();

        var latest = await new MonitoringQueries(db).LatestAsync([recovered, failing, neverOk, noData], default);

        Assert.AreEqual(200, latest[recovered].LastCode);
        Assert.IsNull(latest[recovered].FailingSince);
        Assert.AreEqual(470, latest[failing].LastCode);
        Assert.AreEqual("m470", latest[failing].LastMessage);
        Assert.AreEqual(Local(14, 2), latest[failing].FailingSince);
        Assert.AreEqual(Local(14, 1), latest[neverOk].FailingSince);
        Assert.IsNull(latest[noData].LastCode);
    }

    [TestMethod]
    public async Task StateChangesAreTransitionsNewestFirst()
    {
        // OK before the window, then failed, recovered, failed again.
        var s = Sensor(SensorType.SIMPLE, (Local(13, 0), 200), (Local(14, 2), 500), (Local(14, 3), 500), (Local(14, 5), 200), (Local(14, 9), 470));
        // Failed since before the window and recovered within it: the failure's start is looked up.
        var r = Sensor(SensorType.SIMPLE, (Local(12, 0), 200), (Local(12, 6), 500), (Local(14, 6), 200));

        var changes = await new MonitoringQueries(db).StateChangesAsync([s, r], Local(14, 0), Local(15, 12), 50, default);

        CollectionAssert.AreEqual(
            new[] { (s, Local(14, 9), 470, 200), (r, Local(14, 6), 200, 500), (s, Local(14, 5), 200, 500), (s, Local(14, 2), 500, 200) },
            changes.Select(c => (c.SensorId, c.At, c.Code, c.PreviousCode)).ToArray());
        Assert.AreEqual(Local(14, 2), changes.Single(c => c.SensorId == s && c.Code == 200).FailingSince);
        Assert.AreEqual(Local(12, 6), changes.Single(c => c.SensorId == r).FailingSince);
        Assert.IsNull(changes.First().FailingSince);
    }
}
