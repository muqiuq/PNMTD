using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth;

namespace PNMTD.Tests.Performance;

/// <summary>
/// The dashboard's targets (docs/webui-migration-plan.md §5.5) at their scale: 50 hosts, 300 sensors,
/// 2000 events each (600,000 in 90 days). Opt-in, it takes about a minute: PNMTD_PERF=1.
/// Measured on an M-series Mac with Podman: a build takes about 1.1 s, a polled request 0–3 ms
/// (DashboardCache), the response 46.5 KB.
/// </summary>
[TestClass]
public sealed class DashboardPerformanceTests
{
    [TestMethod]
    public async Task DashboardMeetsItsTargetsAt300SensorsWith2000EventsEach()
    {
        if (Environment.GetEnvironmentVariable("PNMTD_PERF") != "1")
        {
            Assert.Inconclusive("Opt-in: PNMTD_PERF=1");
        }
        using var app = new TestApp();
        await using (var db = app.NewDb())
        {
            db.Database.SetCommandTimeout(600);
            // 6 sensors on each of 50 hosts, an event every 65 minutes (2000 cover 90 days), 3 % failures,
            // and three sensors that fail now.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO hosts ("Id","Name","Location","Notes","Created","Enabled")
                SELECT md5('h'||i)::uuid, 'perf-'||lpad(i::text,2,'0'), 'lab', NULL, now(), true FROM generate_series(1,50) i;
                INSERT INTO sensors ("Id","OlderSiblingId","ParentId","Type","Name","Status","SecretWriteToken","SecretReadToken","TextId","Created","Enabled","Ignore","Interval","GracePeriod","Parameters","Source")
                SELECT md5('s'||h||'-'||j)::uuid, NULL, md5('h'||h)::uuid, 10, 'sensor-'||j, NULL, 'pw'||h||'-'||j, 'pr'||h||'-'||j, NULL, now(), true, false, 0, 0, NULL, NULL
                FROM generate_series(1,50) h, generate_series(1,6) j;
                INSERT INTO events ("Id","SensorId","Created","Message","Source","Code")
                SELECT gen_random_uuid(), md5('s'||h||'-'||j)::uuid, now() - (k * interval '65 minutes'), 'msg '||k, '203.0.113.1:1',
                       CASE WHEN (h % 17 = 0 AND j = 1 AND k < 30) OR (k > 0 AND random() < 0.03) THEN 500 ELSE 200 END
                FROM generate_series(1,50) h, generate_series(1,6) j, generate_series(0,1999) k;
                ANALYZE events;
                """);
        }
        try
        {
            var client = app.Client(Roles.Viewer);
            var build = Stopwatch.StartNew();
            var body = await (await client.GetAsync("/api/v1/dashboard")).Content.ReadAsByteArrayAsync();
            build.Stop();

            // Polled across the 10-second mark, as browsers do: answered from the last build.
            var polled = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                var sw = Stopwatch.StartNew();
                await (await client.GetAsync("/api/v1/dashboard")).Content.ReadAsByteArrayAsync();
                polled.Add(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(1000);
            }
            var p95 = polled.Order().ElementAt((int)Math.Ceiling(polled.Count * 0.95) - 1);
            Console.WriteLine($"dashboard: build {build.ElapsedMilliseconds} ms, polled p95 {p95:F1} ms, {body.Length} bytes");

            Assert.IsLessThan(50_000, body.Length, "under 50 KB");
            Assert.IsLessThan(200, p95, "p95 under 200 ms");
            Assert.IsLessThan(5000, build.ElapsedMilliseconds, "a build stays in seconds");
        }
        finally
        {
            await using var db = app.NewDb();
            db.Database.SetCommandTimeout(600);
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM hosts WHERE "Name" LIKE 'perf-%'""");
        }
    }
}
