using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Helper;

namespace PNMTD.Services.Events;

/// <summary>The keys of PNMT's system sensors (docs/ai-operations-plan.md §4): a sensor's <c>Parameters</c>.</summary>
public static class SystemSensorKeys
{
    public const string Uplink = "uplink";
    public const string Ai = "ai";
    public const string AiBudget = "ai-budget";
    public const string RouterOsAdvisories = "routeros-advisories";

    /// <summary>The files folder of the locations and the knowledge base (docs/locations-knowledge-plan.md §5.5).</summary>
    public const string Files = "files";

    /// <summary>Redmine (docs/audit-redmine-zabbix-plan.md §8.5).</summary>
    public const string Redmine = "redmine";

    /// <summary>Zabbix (docs/audit-redmine-zabbix-plan.md §8.5).</summary>
    public const string Zabbix = "zabbix";

    /// <summary>3rd Party Connectors (docs/connectors-plan.md §12.3): created with the first connector.</summary>
    public const string Connectors = "connectors";

    /// <summary>The OS updates' high-risk check (docs/linux-os-updates-plan.md §5.4): its sources fresh, or 434.</summary>
    public const string OsAdvisories = "os-advisories";
}

/// <summary>A system sensor: its key, its name, its importance, and when it exists.</summary>
public sealed record SystemSensorDefinition(string Key, string Name, Importance Importance, Func<IConfiguration, bool> Configured);

/// <summary>
/// PNMT's own conditions as sensors of type SYSTEM (docs/ai-operations-plan.md §4): created on a host named "PNMT",
/// one per key, and written through <see cref="SensorEventWriter"/>, so notifications use the normal rules. They can
/// be renamed, moved and disabled, but not deleted. A sensor is created at start when its feature is configured,
/// and in any case on its first event.
/// </summary>
public sealed class SystemSensors(IDbContextFactory<PnmtdDbContext> dbs, IConfiguration configuration, ILogger<SystemSensors> logger)
{
    public const string HostName = "PNMT";
    public const string Source = "PNMT";

    public static readonly SystemSensorDefinition[] All =
    [
        new(SystemSensorKeys.Uplink, "Uplink", Importance.High, Domain.SystemService.UplinkConfigured),
        new(SystemSensorKeys.Ai, "AI", Importance.High, c => c.GetValue("Ai:Enabled", false)),
        new(SystemSensorKeys.AiBudget, "AI budget", Importance.Normal, c => c.GetValue("Ai:Enabled", false)),
        new(SystemSensorKeys.RouterOsAdvisories, "RouterOS advisories", Importance.High,
            c => c.GetValue("Mikrotik:Enabled", true) && c.GetValue("Mikrotik:Advisories:Enabled", true)),
        new(SystemSensorKeys.Files, "File storage", Importance.High, _ => true),
        new(SystemSensorKeys.Redmine, "Redmine", Importance.Normal, PNMTD.Redmine.RedmineSetup.Configured),
        new(SystemSensorKeys.Zabbix, "Zabbix", Importance.High, PNMTD.Zabbix.ZabbixSetup.Configured),
        // Not at start: the first connector creates it, so installations without connectors don't get it.
        new(SystemSensorKeys.Connectors, "3rd Party Connectors", Importance.High, _ => false),
        new(SystemSensorKeys.OsAdvisories, "OS advisories", Importance.High,
            c => c.GetValue("OsUpdates:Enabled", true) && c.GetValue("OsUpdates:Advisories:Enabled", false)),
    ];

    private readonly ConcurrentDictionary<string, Guid> ids = new();
    private readonly SemaphoreSlim creating = new(1, 1);

    public static SystemSensorDefinition Definition(string key) =>
        All.FirstOrDefault(d => d.Key == key) ?? throw new ArgumentException($"No system sensor {key}", nameof(key));

    /// <summary>At start: the sensors whose feature is configured.</summary>
    public async Task EnsureConfiguredAsync(CancellationToken ct)
    {
        foreach (var definition in All.Where(d => d.Configured(configuration)))
        {
            await IdAsync(definition.Key, ct);
        }
    }

    /// <summary>The sensor's id, created (with the host "PNMT" if no system sensor has a host yet) when missing.</summary>
    public async Task<Guid> IdAsync(string key, CancellationToken ct)
    {
        if (ids.TryGetValue(key, out var cached))
        {
            await using var check = await dbs.CreateDbContextAsync(ct);
            if (await check.Sensors.AnyAsync(s => s.Id == cached, ct))
            {
                return cached;
            }
            ids.TryRemove(key, out _);
        }
        await creating.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var existing = await db.Sensors.Where(s => s.Type == SensorType.SYSTEM && s.Parameters == key).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (existing is { } found)
            {
                return ids[key] = found;
            }
            var definition = Definition(key);
            var hostId = await db.Sensors.Where(s => s.Type == SensorType.SYSTEM).Select(s => (Guid?)s.ParentId).FirstOrDefaultAsync(ct)
                ?? await db.Hosts.Where(h => h.Name == HostName).Select(h => (Guid?)h.Id).FirstOrDefaultAsync(ct);
            if (hostId is null)
            {
                var host = new HostEntity
                {
                    Id = Guid.NewGuid(), Name = HostName, Enabled = true, Created = Clock.UtcNow,
                    Notes = "PNMT's own conditions: the system sensors (docs/ai-operations-plan.md §4).",
                };
                db.Hosts.Add(host);
                hostId = host.Id;
            }
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(),
                ParentId = hostId.Value,
                Type = SensorType.SYSTEM,
                Name = definition.Name,
                Parameters = key,
                Enabled = true,
                Created = Clock.UtcNow,
                Importance = definition.Importance,
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            // PNMT's own change to the configuration (docs/audit-redmine-zabbix-plan.md §3.6).
            AuditLog.Record(db, AuditEvents.SensorCreated, $"{HostName} / {definition.Name}: a system sensor", AuditWho.System,
                AuditTarget.Sensor(sensor.Id, $"{HostName} / {definition.Name}", hostId.Value));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another instance was quicker.
                await using var again = await dbs.CreateDbContextAsync(ct);
                return ids[key] = await again.Sensors.Where(s => s.Type == SensorType.SYSTEM && s.Parameters == key).Select(s => s.Id).SingleAsync(ct);
            }
            logger.LogInformation("Created the system sensor {Name} ({Key})", definition.Name, key);
            return ids[key] = sensor.Id;
        }
        finally
        {
            creating.Release();
        }
    }

    /// <summary>An event on the sensor, with the sensor API's rules (a repeated, identical state only moves the time).</summary>
    public async Task WriteAsync(string key, int code, string message, CancellationToken ct)
    {
        var id = await IdAsync(key, ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SensorEventWriter.WriteAsync(db, id, code, message, Source, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The sensor's newest event, for the state machines that write only on a change.</summary>
    public async Task<EventEntity?> LastAsync(string key, CancellationToken ct)
    {
        var id = await IdAsync(key, ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.Events.AsNoTracking().Where(e => e.SensorId == id).OrderByDescending(e => e.Created).FirstOrDefaultAsync(ct);
    }
}

/// <summary>Creates the configured system sensors at start, after the migrations (with the monitoring tasks).</summary>
public sealed class SystemSensorStartup(SystemSensors sensors, ILogger<SystemSensorStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await sensors.EnsureConfiguredAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The system sensors could not be created; they are created with their first event");
        }
    }
}
