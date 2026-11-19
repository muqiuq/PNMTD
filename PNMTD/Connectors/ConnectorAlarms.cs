using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Services.Events;

namespace PNMTD.Connectors;

/// <summary>
/// The refusals that may mean a key in the wrong hands (docs/connectors-plan.md §12.3): a valid key from outside its caller's networks,
/// a refusal by the review, an address blocked for failed keys. Each is a <c>ConnectorRefused</c> audit entry and a 433 on the system
/// sensor <i>3rd Party Connectors</i>; 233 follows after Connectors__QuietMinutes without another.
/// </summary>
public sealed class ConnectorAlarms(IDbContextFactory<PnmtdDbContext> dbs, SystemSensors sensors, IOptionsMonitor<ConnectorOptions> options,
    ILogger<ConnectorAlarms> logger)
{
    public async Task RefusedAsync(string message, AuditWho who, AuditTarget? target, CancellationToken ct)
    {
        try
        {
            await using (var db = await dbs.CreateDbContextAsync(ct))
            {
                AuditLog.Record(db, AuditEvents.ConnectorRefused, message, who, target, AuditOutcome.Denied);
                await db.SaveChangesAsync(ct);
            }
            await sensors.WriteAsync(SystemSensorKeys.Connectors, PNMTStatusCodes.CONNECTORS_REFUSED, Cuts.Cut(message, 1000), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "A connector's refusal could not be recorded: {Message}", message);
        }
    }

    /// <summary>233 once the quiet time passed since the last 433; nothing for an installation without the sensor.</summary>
    public async Task QuietAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Type == SensorType.SYSTEM && s.Parameters == SystemSensorKeys.Connectors)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (sensor is null)
        {
            return;
        }
        var last = await db.Events.AsNoTracking().Where(e => e.SensorId == sensor).OrderByDescending(e => e.Created).Select(e => new { e.Code, e.Created })
            .FirstOrDefaultAsync(ct);
        var quiet = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.QuietMinutes));
        if (last is { Code: PNMTStatusCodes.CONNECTORS_REFUSED } && Clock.UtcNow - Clock.AsUtc(last.Created) >= quiet)
        {
            await sensors.WriteAsync(SystemSensorKeys.Connectors, PNMTStatusCodes.CONNECTORS_OK,
                $"No refused call of the kinds that may mean a leaked key for {quiet.TotalMinutes:0} minutes.", ct);
        }
    }
}
