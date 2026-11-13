using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Windows;

/// <summary>Writes the agents' permanent log (docs/windows-sensor-plan.md §8.3), and audit entries without a signed-in user.</summary>
public static class WindowsAgentLog
{
    public static void Add(PnmtdDbContext db, Guid? sensorId, string agentName, WindowsAgentLogKind kind, string message,
        object? details = null, string? address = null)
    {
        db.WindowsAgentLog.Add(new WindowsAgentLogEntity
        {
            Id = Guid.NewGuid(),
            SensorId = sensorId,
            AgentName = Cuts.Cut(agentName, 500),
            At = Clock.UtcNow,
            Kind = kind,
            Message = Cuts.Cut(message, 4000),
            Details = details is null ? null : JsonSerializer.Serialize(details, Lib.Agent.AgentJson.Options),
            Address = address is null ? null : Cuts.Cut(address, 100),
        });
    }

    /// <summary>"host / sensor", the name the log keeps once the server is gone.</summary>
    public static async Task<string> NameAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct) =>
        await db.Sensors.Where(s => s.Id == sensorId).Select(s => s.Parent.Name + " / " + s.Name).SingleOrDefaultAsync(ct) ?? sensorId.ToString();

    /// <summary>An audit entry for something an agent did (a join): no user, the agent's address.</summary>
    public static void Audit(PnmtdDbContext db, string eventType, string detail, string? address)
    {
        db.AuthLog.Add(new AuthLogEntity
        {
            Id = Guid.NewGuid(),
            CreatedAt = Clock.UtcNow,
            Username = "agent",
            EventType = eventType,
            IpAddress = address,
            Detail = Cuts.Cut($"agent: {detail}", 1000),
        });
    }
}

internal static class Cuts
{
    public static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public static string? CutOrNull(string? value, int max) => value is null ? null : Cut(value.Trim(), max) is { Length: > 0 } v ? v : null;
}
