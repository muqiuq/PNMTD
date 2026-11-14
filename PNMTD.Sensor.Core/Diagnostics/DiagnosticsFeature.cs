using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Core.State;

namespace PNMTD.Sensor.Core.Diagnostics;

/// <summary>
/// Diagnostics on the agent (docs/windows-sensor-plan.md §5.3; on Linux unchanged, docs/linux-sensor-plan.md §7): scheduled assignments by the agent's own timer, so they keep
/// running while PNMTD is away (the configuration is kept in agent.json), and Run now on request. One run at a time, its
/// checks in order, each with its timeout; a check that throws is an error and the next one runs. Results are buffered,
/// at most 1,000.
/// </summary>
public sealed class DiagnosticsFeature(ProblemReporter problems, ILogger<DiagnosticsFeature> logger) : IAgentFeature
{
    public const int MaxBuffered = 1000;
    private const string BufferFile = "diagnostics-results.json";

    private readonly Lock gate = new();
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly List<DiagnosticRunResult> pending = [];
    private readonly Dictionary<Guid, DateTimeOffset> nextRun = [];
    private readonly Dictionary<Guid, DiagnosticRunResult> lastRuns = [];
    private IReadOnlyList<DiagnosticsAssignment> assignments = [];
    private int dropped;
    private bool loaded;

    public string Name => "diagnostics";

    public IReadOnlyList<string> Capabilities => [AgentCapabilities.DiagPing, AgentCapabilities.DiagDns];

    public void Configure(AgentConfig config)
    {
        lock (gate)
        {
            assignments = config.Diagnostics;
            foreach (var gone in nextRun.Keys.Where(id => assignments.All(a => a.AssignmentId != id)).ToList())
            {
                nextRun.Remove(gone);
            }
        }
    }

    public IEnumerable<DiagnosticsStatus> Diagnostics
    {
        get
        {
            lock (gate)
            {
                return assignments.Select(a =>
                {
                    lastRuns.TryGetValue(a.AssignmentId, out var last);
                    var failed = last?.Results.Where(r => r.Outcome != DiagnosticOutcome.Pass).ToList();
                    return new DiagnosticsStatus(a.AssignmentId, a.SetName, a.Mode.ToString(), a.IntervalMinutes, a.Enabled, last?.FinishedAt,
                        last is null ? null : failed!.Count == 0 ? "pass" : "fail",
                        failed is { Count: > 0 } ? string.Join(", ", failed.Take(3).Select(f => $"{f.Name}: {f.Message}")) : null);
                }).ToList();
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!loaded)
        {
            loaded = true;
            if (AgentFiles.ReadState<List<DiagnosticRunResult>>(BufferFile) is { } saved)
            {
                lock (gate)
                {
                    pending.AddRange(saved.TakeLast(MaxBuffered));
                }
            }
            if (AgentFiles.ReadConfig()?.Config is { } known)
            {
                Configure(known);
            }
        }
        var savedAt = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            List<DiagnosticsAssignment> due;
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                due = assignments.Where(a => a.Enabled && a.Mode == DiagnosticMode.Scheduled
                    && (!nextRun.TryGetValue(a.AssignmentId, out var next) || next <= now)).ToList();
                foreach (var assignment in due)
                {
                    // A little spread, so servers don't all ping at once.
                    nextRun[assignment.AssignmentId] = now + TimeSpan.FromMinutes(Math.Max(1, assignment.IntervalMinutes)) + TimeSpan.FromSeconds(Random.Shared.Next(0, 15));
                }
            }
            foreach (var assignment in due)
            {
                await RunAssignmentAsync(assignment, Guid.NewGuid(), "schedule", ct);
            }
            if (DateTimeOffset.UtcNow - savedAt > TimeSpan.FromMinutes(1))
            {
                savedAt = DateTimeOffset.UtcNow;
                lock (gate)
                {
                    AgentFiles.WriteState(BufferFile, pending.ToList());
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    public async Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct)
    {
        if (request.Type != AgentMessageTypes.RunDiagnostics)
        {
            return null;
        }
        var run = AgentJson.Deserialize<RunDiagnosticsRequest>(request.Body);
        DiagnosticsAssignment? assignment;
        lock (gate)
        {
            // Only an assignment this agent already has, with its checks as configured (§6).
            assignment = assignments.FirstOrDefault(a => a.AssignmentId == run?.AssignmentId);
        }
        if (run is null || assignment is null)
        {
            return new Ack(request.RequestId, false, "No such assignment in this agent's configuration.");
        }
        _ = Task.Run(() => RunAssignmentAsync(assignment, run.RunId, "manual", CancellationToken.None), CancellationToken.None);
        return new Ack(request.RequestId, true);
    }

    public async Task ReportAsync(AgentSession session, CancellationToken ct)
    {
        List<DiagnosticRunResult> batch;
        int lost;
        lock (gate)
        {
            if (pending.Count == 0 && dropped == 0)
            {
                return;
            }
            batch = pending.Take(50).ToList();
            pending.RemoveRange(0, batch.Count);
            lost = dropped;
            dropped = 0;
        }
        if (lost > 0)
        {
            problems.Report("diagnostics", "buffer", $"{lost} results were dropped while offline (at most {MaxBuffered} are kept).");
        }
        if (batch.Count == 0)
        {
            return;
        }
        try
        {
            await session.SendAsync(AgentMessageTypes.DiagResult, new DiagnosticResultBatch(batch), ct);
        }
        catch
        {
            lock (gate)
            {
                pending.InsertRange(0, batch);
            }
            throw;
        }
    }

    private async Task RunAssignmentAsync(DiagnosticsAssignment assignment, Guid runId, string trigger, CancellationToken ct)
    {
        await running.WaitAsync(ct);
        try
        {
            var started = DateTimeOffset.UtcNow;
            var results = new List<DiagnosticCheckResult>();
            foreach (var check in assignment.Checks)
            {
                var watch = Stopwatch.StartNew();
                DiagnosticCheckResult result;
                try
                {
                    result = check.Type switch
                    {
                        DiagnosticCheckType.Ping => await PingAsync(check, ct),
                        _ => await DnsAsync(check, ct),
                    };
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    result = new DiagnosticCheckResult(check.Id, check.Name, check.Type, DiagnosticOutcome.Error, $"{e.GetType().Name}: {e.Message}");
                }
                results.Add(result with { DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1) });
            }
            var finished = new DiagnosticRunResult(assignment.AssignmentId, runId, trigger, started, DateTimeOffset.UtcNow, results);
            lock (gate)
            {
                lastRuns[assignment.AssignmentId] = finished;
                pending.Add(finished);
                if (pending.Count > MaxBuffered)
                {
                    pending.RemoveAt(0);
                    dropped++;
                }
            }
            logger.LogInformation("Diagnostics {Set} ({Trigger}): {Passed} of {Count} passed", assignment.SetName, trigger,
                results.Count(r => r.Outcome == DiagnosticOutcome.Pass), results.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            problems.Report("diagnostics", $"running {assignment.SetName}", e);
        }
        finally
        {
            running.Release();
        }
    }

    // --- the checks (§5.1) ------------------------------------------------------------------------------------

    private static async Task<DiagnosticCheckResult> PingAsync(DiagnosticCheck check, CancellationToken ct)
    {
        var p = check.Ping ?? throw new InvalidOperationException("A ping check without its parameters.");
        var count = Math.Clamp(p.Count, 1, 10);
        var timeout = Math.Clamp(p.TimeoutMs, 100, 5000);
        var address = IPAddress.TryParse(p.Target, out var literal) ? literal : (await Dns.GetHostAddressesAsync(p.Target, Family(p.Family), ct)).FirstOrDefault()
            ?? throw new InvalidOperationException($"{p.Target} does not resolve{(p.Family == IpFamily.Any ? "" : $" to {p.Family}")}.");
        using var ping = new Ping();
        var times = new List<double>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeout), cancellationToken: ct);
            if (reply.Status == IPStatus.Success)
            {
                times.Add(reply.RoundtripTime);
            }
            if (i + 1 < count)
            {
                await Task.Delay(200, ct);
            }
        }
        var loss = (int)Math.Round(100.0 * (count - times.Count) / count);
        var values = new PingValues(count, times.Count, loss, times.Count > 0 ? times.Min() : null, times.Count > 0 ? Math.Round(times.Average(), 1) : null,
            times.Count > 0 ? times.Max() : null, address.ToString());
        var pass = loss <= p.MaxLossPercent && (p.MaxAvgMs is not { } maxAvg || values.AvgMs is { } avg && avg <= maxAvg);
        var message = times.Count == 0 ? "100 % loss"
            : $"{loss} % loss, avg {values.AvgMs} ms{(p.MaxAvgMs is { } m && values.AvgMs > m ? $" (above {m} ms)" : "")}";
        return new DiagnosticCheckResult(check.Id, check.Name, check.Type, pass ? DiagnosticOutcome.Pass : DiagnosticOutcome.Fail, message, Ping: values);
    }

    private static AddressFamily Family(IpFamily family) => family switch
    {
        IpFamily.IPv4 => AddressFamily.InterNetwork,
        IpFamily.IPv6 => AddressFamily.InterNetworkV6,
        _ => AddressFamily.Unspecified,
    };

    private static async Task<DiagnosticCheckResult> DnsAsync(DiagnosticCheck check, CancellationToken ct)
    {
        var d = check.Dns ?? throw new InvalidOperationException("A DNS check without its parameters.");
        var watch = Stopwatch.StartNew();
        List<string> answers;
        string? resolver;
        string? code = null;
        if (d.Server is null && d.RecordType is DnsRecordKind.A or DnsRecordKind.AAAA)
        {
            // The system's resolver, as applications see it (hosts file, suffixes).
            resolver = "system";
            try
            {
                answers = (await Dns.GetHostAddressesAsync(d.Name, d.RecordType == DnsRecordKind.A ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, ct))
                    .Select(a => a.ToString()).ToList();
            }
            catch (SocketException e)
            {
                answers = [];
                code = e.SocketErrorCode == SocketError.HostNotFound ? "NXDOMAIN" : e.SocketErrorCode.ToString();
            }
        }
        else
        {
            var client = d.Server is { } server
                ? new LookupClient(new LookupClientOptions(IPAddress.Parse(server)) { Timeout = TimeSpan.FromSeconds(5), Retries = 1, UseCache = false })
                : new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 1, UseCache = false });
            var response = d.RecordType == DnsRecordKind.PTR && IPAddress.TryParse(d.Name, out var ip)
                ? await client.QueryReverseAsync(ip, ct)
                : await client.QueryAsync(d.Name, Enum.Parse<QueryType>(d.RecordType.ToString()), cancellationToken: ct);
            resolver = response.NameServer?.Address?.ToString() ?? d.Server;
            code = response.HasError ? response.Header.ResponseCode.ToString().ToUpperInvariant() switch { "NOTEXISTENTDOMAIN" => "NXDOMAIN", var c => c } : null;
            answers = response.Answers.Select(Answer).Where(a => a is not null).Select(a => a!).ToList();
        }
        var ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        var values = new DnsValues(answers, resolver, ms, code);
        string message;
        var pass = true;
        if (answers.Count == 0)
        {
            pass = false;
            message = code ?? "no answer";
        }
        else if (d.Expect is { Length: > 0 } expect && !answers.Any(a => a.Equals(expect, StringComparison.OrdinalIgnoreCase) || a.Contains(expect, StringComparison.OrdinalIgnoreCase)))
        {
            pass = false;
            message = $"{expect} not in the answers ({string.Join(", ", answers.Take(3))})";
        }
        else if (d.MaxMs is { } max && ms > max)
        {
            pass = false;
            message = $"{ms} ms, above {max} ms";
        }
        else
        {
            message = $"{string.Join(", ", answers.Take(3))}{(answers.Count > 3 ? ", …" : "")} in {ms} ms";
        }
        return new DiagnosticCheckResult(check.Id, check.Name, check.Type, pass ? DiagnosticOutcome.Pass : DiagnosticOutcome.Fail, message, Dns: values);
    }

    private static string? Answer(DnsResourceRecord record) => record switch
    {
        ARecord a => a.Address.ToString(),
        AaaaRecord a => a.Address.ToString(),
        CNameRecord c => c.CanonicalName.Value.TrimEnd('.'),
        MxRecord m => $"{m.Preference} {m.Exchange.Value.TrimEnd('.')}",
        TxtRecord t => string.Join("", t.Text),
        SrvRecord s => $"{s.Priority} {s.Weight} {s.Port} {s.Target.Value.TrimEnd('.')}",
        PtrRecord p => p.PtrDomainName.Value.TrimEnd('.'),
        NsRecord n => n.NSDName.Value.TrimEnd('.'),
        SoaRecord s => $"{s.MName.Value.TrimEnd('.')} {s.Serial}",
        _ => null,
    };
}
