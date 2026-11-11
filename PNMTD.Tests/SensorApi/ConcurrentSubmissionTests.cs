using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Tests.SensorApi;

/// <summary>
/// Reports for one sensor that arrive at the same time must end as they would one after the other
/// (docs/webui-migration-plan.md §3.4): SQLite serialised all writes, PostgreSQL needs the
/// per-sensor lock.
/// </summary>
[TestClass]
public sealed class ConcurrentSubmissionTests
{
    [TestMethod]
    public async Task ParallelIdenticalReportsDedupeAsSerialOnes()
    {
        await using var factory = new TestApp();
        using var client = factory.CreateClient();

        Guid sensorId;
        string token;
        await using (var db = factory.Services.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext())
        {
            var host = new HostEntity { Id = Guid.NewGuid(), Name = "concurrency", Enabled = true, Created = DateTime.UtcNow };
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(), Parent = host, Name = "parallel", Type = SensorType.SIMPLE, Enabled = true,
                Interval = 86400, GracePeriod = 86400, Created = DateTime.UtcNow, Parameters = "", Source = "", TextId = "",
            };
            sensor.SetNewSecretToken();
            db.Hosts.Add(host);
            db.Sensors.Add(sensor);
            await db.SaveChangesAsync();
            (sensorId, token) = (sensor.Id, sensor.SecretWriteToken!);
        }

        try
        {
            // Development: no 5-second rule, so every request reaches the dedupe rule.
            var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync($"/event/{token}/200/same")));
            Assert.IsTrue(responses.All(r => r.IsSuccessStatusCode), string.Join(", ", responses.Select(r => (int)r.StatusCode)));

            await using var db = factory.Services.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
            // One after the other: the first two are new rows, every later one updates the second.
            Assert.AreEqual(2, await db.Events.CountAsync(e => e.SensorId == sensorId));
        }
        finally
        {
            // The other tests share this database and count its hosts.
            await using var db = factory.Services.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
            await db.Events.Where(e => e.SensorId == sensorId).ExecuteDeleteAsync();
            await db.Sensors.Where(s => s.Id == sensorId).ExecuteDeleteAsync();
            await db.Hosts.Where(h => h.Name == "concurrency").ExecuteDeleteAsync();
        }
    }
}
