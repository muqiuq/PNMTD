using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Agents;

/// <summary>
/// Join attempts (docs/windows-sensor-plan.md §8.7): <c>Agents:JoinRateLimitPerMinute</c> per address, and the join closes
/// for 15 minutes after 50 failures in an hour, whatever their addresses.
/// </summary>
public sealed class JoinGuard
{
    private readonly ConcurrentDictionary<string, (DateTime Start, int Count)> perAddress = new();
    private readonly Lock gate = new();
    private readonly Queue<DateTime> failures = new();
    private DateTime closedUntil = DateTime.MinValue;

    public const int FailuresPerHour = 50;

    public static readonly TimeSpan ClosedFor = TimeSpan.FromMinutes(15);

    public bool IsClosed
    {
        get
        {
            lock (gate)
            {
                return Clock.UtcNow < closedUntil;
            }
        }
    }

    public bool TryEnter(string address, int perMinute)
    {
        var now = Clock.UtcNow;
        var entry = perAddress.AddOrUpdate(address, _ => (now, 1),
            (_, e) => now - e.Start >= TimeSpan.FromMinutes(1) ? (now, 1) : (e.Start, e.Count + 1));
        if (perAddress.Count > 10_000)
        {
            foreach (var (key, value) in perAddress)
            {
                if (now - value.Start >= TimeSpan.FromMinutes(1))
                {
                    perAddress.TryRemove(key, out _);
                }
            }
        }
        return entry.Count <= perMinute;
    }

    public void Failed()
    {
        lock (gate)
        {
            var now = Clock.UtcNow;
            failures.Enqueue(now);
            while (failures.Count > 0 && now - failures.Peek() > TimeSpan.FromHours(1))
            {
                failures.Dequeue();
            }
            if (failures.Count >= FailuresPerHour)
            {
                closedUntil = now + ClosedFor;
                failures.Clear();
            }
        }
    }

    /// <summary>For tests.</summary>
    public void Reset()
    {
        lock (gate)
        {
            failures.Clear();
            closedUntil = DateTime.MinValue;
        }
        perAddress.Clear();
    }
}

/// <summary>
/// <c>GET</c> and <c>POST /agent/v1/join</c> (§3.3, §9.1): anonymous, authenticated by the join token. The answer carries the
/// agent's id, its sensor, PNMTD's signing key and the first configuration, signed with that key. A token is made for one
/// platform (docs/linux-sensor-plan.md §3.3): the join's must match it, and the new sensor is of the platform's type.
/// </summary>
public static class AgentJoinEndpoint
{
    private const int MaxBodyBytes = 64 * 1024;

    public static async Task<IResult> InfoAsync(AgentKeyStore keys, IOptionsMonitor<AgentOptions> options, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return Results.NotFound();
        }
        var key = await keys.GetAsync(ct);
        return Results.Json(new JoinInfo("pnmt", ServerInfo.Version, AgentProtocol.Version, key.Fingerprint), AgentJson.Options);
    }

    public static async Task<IResult> JoinAsync(HttpContext http, IDbContextFactory<PnmtdDbContext> dbs, AgentKeyStore keys, AgentHub hub,
        JoinGuard guard, IOptionsMonitor<AgentOptions> options, ILogger<AgentHub> logger, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled)
        {
            return Results.NotFound();
        }
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (guard.IsClosed)
        {
            return Error(StatusCodes.Status429TooManyRequests, "join_closed", "Joining is closed for 15 minutes after too many failed attempts. Try again later.");
        }
        if (!guard.TryEnter(address, settings.JoinRateLimitPerMinute))
        {
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many join attempts from this address. Try again in a minute.");
        }

        JoinRequest? request;
        try
        {
            if (http.Request.ContentLength is > MaxBodyBytes)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, "too_large", "The join request is too large.");
            }
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, ct);
            if (body.Length > MaxBodyBytes)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, "too_large", "The join request is too large.");
            }
            request = JsonSerializer.Deserialize<JoinRequest>(body.ToArray(), AgentJson.Options);
        }
        catch (JsonException)
        {
            request = null;
        }
        var problem = request is null ? "The join request is not valid JSON." : Validate(request);
        if (problem is not null)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", problem);
        }
        var token = JoinTokens.Normalize(request!.Token);
        using var agentKey = AgentSigning.ImportPublicKey(request.PublicKey);
        if (token is null || agentKey is null)
        {
            await FailedAsync(dbs, guard, token is null ? "a malformed token" : "a key that is not P-256", address, ct);
            return token is null
                ? Error(StatusCodes.Status400BadRequest, "invalid_token", "The token is not of the form xxxx-xxxx.")
                : Error(StatusCodes.Status400BadRequest, "invalid_key", "The public key is not an ECDSA P-256 key.");
        }

        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hash = JoinTokens.Hash(token);
        var now = Clock.UtcNow;
        var row = await db.AgentJoinTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        var host = row is null ? null : await db.Hosts.SingleOrDefaultAsync(h => h.Id == row.HostId, ct);
        if (row is null || row.UsedAt is not null || row.RevokedAt is not null || row.ExpiresAt < now || host is null)
        {
            await transaction.RollbackAsync(ct);
            var why = row is null ? "an unknown token" : row.UsedAt is not null ? $"token {row.Prefix}… used before" : row.RevokedAt is not null
                ? $"token {row.Prefix}… revoked" : row.ExpiresAt < now ? $"token {row.Prefix}… expired" : $"token {row.Prefix}…: its host is gone";
            await FailedAsync(dbs, guard, why, address, ct);
            return Error(StatusCodes.Status403Forbidden, "invalid_token",
                "This token is unknown, used, revoked or expired. Create a new one in PNMT: Add server.");
        }
        // The token's platform decides; an agent of the other platform is refused before the token is used up.
        if (request.Platform != row.Platform)
        {
            await transaction.RollbackAsync(ct);
            await FailedAsync(dbs, guard, $"token {row.Prefix}… is for {row.Platform}, the agent runs on {request.Platform}", address, ct);
            return Error(StatusCodes.Status403Forbidden, "wrong_platform",
                $"This token was made for a {row.Platform} server; this agent runs on {request.Platform}. Create a token for {request.Platform} in PNMT: Add server.");
        }
        // Single use, also against a second join in the same moment.
        var claimed = await db.AgentJoinTokens.Where(t => t.Id == row.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.UsedAt, now).SetProperty(t => t.UsedFromAddress, Cuts.Cut(address, 100)), ct);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(ct);
            await FailedAsync(dbs, guard, $"token {row.Prefix}… used before", address, ct);
            return Error(StatusCodes.Status403Forbidden, "invalid_token", "This token was used a moment ago.");
        }

        var publicKey = AgentSigning.PublicKey(agentKey);
        var fingerprint = AgentSigning.Fingerprint(publicKey);
        var signing = await keys.GetAsync(ct);
        var platform = AgentPlatformInfo.Of(row.Platform);
        AgentEntity agent;
        SensorEntity sensor;
        var rejoin = row.SensorId is not null;
        if (row.SensorId is { } existingId)
        {
            sensor = await db.Sensors.SingleOrDefaultAsync(s => s.Id == existingId && s.Type == platform.SensorType, ct)
                ?? throw new InvalidOperationException("The re-join token's server is gone.");
            agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == existingId, ct)
                ?? new AgentEntity { SensorId = existingId }.Also(a => db.Agents.Add(a));
        }
        else
        {
            sensor = new SensorEntity
            {
                Id = Guid.NewGuid(),
                ParentId = host.Id,
                Type = platform.SensorType,
                Name = row.Name ?? platform.Name,
                Enabled = true,
                Interval = Math.Clamp(settings.HeartbeatSeconds, 15, 600),
                GracePeriod = 120,
                Created = now,
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            agent = new AgentEntity { SensorId = sensor.Id };
            db.Agents.Add(agent);
        }
        agent.Platform = row.Platform;
        agent.AgentId = Guid.NewGuid();
        agent.PublicKey = publicKey;
        agent.KeyFingerprint = fingerprint;
        agent.JoinedAt = now;
        agent.RevokedAt = null;
        agent.RevokedReason = null;
        agent.UninstalledAt = null;
        agent.Connected = false;
        agent.MachineId = Cuts.CutOrNull(request.MachineGuid, 64);
        agent.Hostname = Cuts.Cut(request.Hostname.Trim(), 255);
        agent.Fqdn = Cuts.CutOrNull(request.Fqdn, 255);
        agent.Domain = Cuts.CutOrNull(request.Domain, 255);
        agent.OsCaption = Cuts.CutOrNull(request.Os.Caption, 200);
        agent.OsEdition = Cuts.CutOrNull(request.Os.Edition, 100);
        agent.OsBuild = Cuts.CutOrNull(request.Os.Build, 50);
        agent.OsDisplayVersion = Cuts.CutOrNull(request.Os.DisplayVersion, 50);
        agent.OsSupported = request.Os.Supported;
        agent.Architecture = Cuts.CutOrNull(request.Architecture, 20);
        agent.AgentVersion = Cuts.CutOrNull(request.AgentVersion, 50);
        row.ResultSensorId = sensor.Id;

        var name = $"{host.Name} / {sensor.Name}";
        // Windows calls the machine's id MachineGuid; Linux's is /etc/machine-id.
        var machineIdName = row.Platform == AgentPlatform.Windows ? "MachineGuid" : "machine id";
        AgentLog.Add(db, sensor.Id, name, AgentLogKind.Joined,
            $"{(rejoin ? "Re-joined" : "Joined")} as {agent.Hostname} ({agent.OsCaption} {agent.OsBuild}) with token {row.Prefix}…. " +
            $"The server's key: {fingerprint}. PNMTD's key: {signing.Fingerprint}.",
            new { ServerKey = fingerprint, PnmtdKey = signing.Fingerprint, Token = row.Prefix, agent.MachineId }, address);
        if (agent.MachineId is { } machine)
        {
            var same = await db.Agents.Where(a => a.MachineId == machine && a.SensorId != sensor.Id)
                .Select(a => new { a.SensorId, Name = a.Sensor.Parent.Name + " / " + a.Sensor.Name }).ToListAsync(ct);
            foreach (var other in same)
            {
                AgentLog.Add(db, sensor.Id, name, AgentLogKind.Warning, $"Possibly the same machine as {other.Name} (the same {machineIdName}).", address: address);
                AgentLog.Add(db, other.SensorId, other.Name, AgentLogKind.Warning, $"Possibly the same machine as {name}, which just joined (the same {machineIdName}).", address: address);
            }
        }
        AgentLog.Audit(db, AuditEvents.AgentJoined, $"{name}: {agent.Hostname} joined with token {row.Prefix}… (key {fingerprint})", address);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (rejoin)
        {
            await hub.DisconnectAsync(sensor.Id, AgentCloseCodes.Revoked, "re-joined with a new key");
        }
        logger.LogInformation("{Platform} agent {Hostname} joined as {Name} from {Address}", row.Platform, agent.Hostname, name, address);

        var config = await AgentConfiguration.BuildAsync(db, keys, settings, sensor.Id, ct);
        var grant = new JoinGrant(agent.AgentId, sensor.Id, sensor.Name, host.Name, fingerprint, config);
        var signed = AgentSigning.Sign(signing.Key, AgentMessageTypes.Join, agent.AgentId, grant, AgentProtocol.RequestLifetime);
        return Results.Json(new JoinResponse(signing.PublicKey, signing.Fingerprint, signed), AgentJson.Options);
    }

    private static string? Validate(JoinRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 255)
        {
            return "The host name must have 1 to 255 characters.";
        }
        if (request.Os is null || string.IsNullOrWhiteSpace(request.Os.Caption) || string.IsNullOrWhiteSpace(request.Os.Build))
        {
            return "The OS caption and build are missing.";
        }
        if (string.IsNullOrWhiteSpace(request.AgentVersion) || string.IsNullOrWhiteSpace(request.Architecture))
        {
            return "The agent's version and architecture are missing.";
        }
        return null;
    }

    private static async Task FailedAsync(IDbContextFactory<PnmtdDbContext> dbs, JoinGuard guard, string why, string address, CancellationToken ct)
    {
        guard.Failed();
        await using var db = await dbs.CreateDbContextAsync(ct);
        AgentLog.Audit(db, AuditEvents.AgentJoinFailed, $"join refused: {why}", address);
        await db.SaveChangesAsync(ct);
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new AgentError(code, message), AgentJson.Options, statusCode: status);

    private static T Also<T>(this T value, Action<T> init)
    {
        init(value);
        return value;
    }
}
