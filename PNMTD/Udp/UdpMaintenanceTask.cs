using PNMTD.Services.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Udp;

/// <summary>
/// Hourly (docs/udp-iot-plan.md §4.6, §4.11): commands and offers that expired without anyone to
/// notice (a node that no longer reports), and downloads without a GET for <c>Udp:Ota:StallHours</c>.
/// Each change writes its command-log row. It never deletes anything: commands, updates and their log
/// are permanent.
/// </summary>
public sealed class UdpMaintenanceTask(IDbContextFactory<PnmtdDbContext> dbs, UdpNodeCache cache, IOptionsMonitor<UdpOptions> options,
    ILogger<UdpMaintenanceTask> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                if (await RunAsync(stoppingToken) > 0)
                {
                    cache.Invalidate();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "UdpMaintenanceTask failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The number of commands and updates it changed.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var names = await db.Sensors.Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var changed = 0;

        var commands = await db.UdpCommands.Where(c => (c.State == UdpCommandState.Queued || c.State == UdpCommandState.Sent)
            && c.ExpiresAt != null && c.ExpiresAt <= now).ToListAsync(ct);
        foreach (var c in commands)
        {
            c.State = UdpCommandState.Expired;
            c.CompletedAt = now;
            db.UdpCommandLog.Add(Row(c.SensorId, c.DeviceId, Name(names, c.SensorId, c.NodeName), UdpLogKind.Expired, now).Also(e =>
            {
                e.CommandId = c.Id;
                e.CmdId = c.CmdId;
                e.Note = c.Attempts > 0 ? $"expired after {c.Attempts} attempts without a result" : "expired before the node reported";
            }));
            changed++;
        }

        var stall = now.AddHours(-Math.Max(1, options.CurrentValue.Ota.StallHours));
        var jobs = await db.UdpOtaJobs.Where(j =>
            ((j.State == UdpOtaState.Queued || j.State == UdpOtaState.Offered) && j.ExpiresAt <= now)
            || (j.State == UdpOtaState.Downloading && (j.LastRequestAt ?? j.StartedAt) < stall)).ToListAsync(ct);
        foreach (var j in jobs)
        {
            var stalled = j.State == UdpOtaState.Downloading;
            j.State = stalled ? UdpOtaState.Failed : UdpOtaState.Expired;
            j.FinishedAt = now;
            db.UdpCommandLog.Add(Row(j.SensorId, j.DeviceId, Name(names, j.SensorId, j.NodeName), stalled ? UdpLogKind.OtaFailed : UdpLogKind.OtaExpired, now).Also(e =>
            {
                e.OtaJobId = j.Id;
                e.CmdId = j.UpdateId;
                e.Note = stalled ? $"stalled: no GET for {options.CurrentValue.Ota.StallHours} hours" : "the offer expired";
            }));
            changed++;
        }
        await db.SaveChangesAsync(ct);
        return changed;
    }

    private static string Name(Dictionary<Guid, string> names, Guid? sensorId, string snapshot) =>
        sensorId is { } id && names.TryGetValue(id, out var name) ? name : snapshot;

    private static UdpCommandLogEntity Row(Guid? sensorId, long deviceId, string name, UdpLogKind kind, DateTime now) => new()
    {
        Created = now,
        SensorId = sensorId,
        DeviceId = deviceId,
        NodeName = name,
        Kind = kind,
    };
}
