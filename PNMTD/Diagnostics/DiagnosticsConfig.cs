using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Diagnostics;

/// <summary>An agent's diagnostics assignments with their checks, for its signed configuration (docs/windows-sensor-plan.md §5.2).</summary>
public static class DiagnosticsConfig
{
    public static async Task<IReadOnlyList<DiagnosticsAssignment>> ForAgentAsync(PnmtdDbContext db, Guid targetSensorId, CancellationToken ct)
    {
        var assignments = await db.DiagnosticAssignments.AsNoTracking().Where(a => a.TargetSensorId == targetSensorId)
            .Join(db.DiagnosticSets.Include(s => s.Checks), a => a.SetId, s => s.Id, (a, s) => new { Assignment = a, Set = s })
            .ToListAsync(ct);
        return assignments.Select(x => new DiagnosticsAssignment(x.Assignment.Id, x.Set.Id, x.Set.Name, x.Assignment.Mode, x.Assignment.IntervalMinutes,
            x.Assignment.Enabled, x.Set.Checks.OrderBy(c => c.Order).Select(ToCheck).ToList())).ToList();
    }

    public static DiagnosticCheck ToCheck(DiagnosticCheckEntity c) => c.Type switch
    {
        DiagnosticCheckType.Ping => new DiagnosticCheck(c.Id, c.Name, c.Type, Ping: JsonSerializer.Deserialize<PingParameters>(c.Parameters, AgentJson.Options)),
        _ => new DiagnosticCheck(c.Id, c.Name, c.Type, Dns: JsonSerializer.Deserialize<DnsParameters>(c.Parameters, AgentJson.Options)),
    };
}

/// <summary>
/// What a check may ask for (§5.1): valid names and addresses, at most 10 pings of at most 5 s, an interval of a minute or more,
/// so a set can't make servers flood a target.
/// </summary>
public static partial class DiagnosticsRules
{
    public const int MaxChecks = 50;
    public const int MaxPings = 10;
    public const int MaxTimeoutMs = 5000;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;

    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[A-Za-z0-9_-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9_-]{1,63}(?<!-))*\.?$")]
    private static partial Regex HostName();

    public static bool IsHost(string? value) => !string.IsNullOrWhiteSpace(value) && (IPAddress.TryParse(value, out _) || HostName().IsMatch(value));

    /// <summary>What is wrong with a ping's parameters, or null.</summary>
    public static string? Ping(PingParameters? p) =>
        p is null ? "A ping check needs its parameters."
        : !IsHost(p.Target) ? "The target must be a host name or an address."
        : p.Count is < 1 or > MaxPings ? $"Between 1 and {MaxPings} pings."
        : p.TimeoutMs is < 100 or > MaxTimeoutMs ? $"A timeout between 100 and {MaxTimeoutMs} ms."
        : p.MaxLossPercent is < 0 or > 100 ? "A loss between 0 and 100 %."
        : p.MaxAvgMs is < 1 or > MaxTimeoutMs ? $"A round trip between 1 and {MaxTimeoutMs} ms, or none."
        : !Enum.IsDefined(p.Family) ? "Any, IPv4 or IPv6."
        : null;

    public static string? Dns(DnsParameters? d) =>
        d is null ? "A DNS check needs its parameters."
        : !IsHost(d.Name?.TrimEnd('.')) ? "The name must be a DNS name (or an address for PTR)."
        : !Enum.IsDefined(d.RecordType) ? "A record type: A, AAAA, CNAME, MX, TXT, SRV, PTR, NS or SOA."
        : d.Server is not null && !IPAddress.TryParse(d.Server, out _) ? "The server must be an address, or empty for the system's resolvers."
        : d.Expect is { Length: > 255 } ? "At most 255 characters to expect."
        : d.MaxMs is < 1 or > 10_000 ? "A time between 1 and 10000 ms, or none."
        : null;
}
