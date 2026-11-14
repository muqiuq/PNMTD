using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Windows.Resilience;

/// <summary>
/// Rule 3 of docs/windows-sensor-plan.md §3.8: whatever fails is logged, reported to PNMTD as a problem, and the rest carries
/// on. Deduplicated per component and message per hour; buffered while offline (at most 200, the oldest go first, which is
/// itself reported).
/// </summary>
internal sealed class ProblemReporter(ILogger<ProblemReporter> logger)
{
    public const int MaxBuffered = 200;

    private readonly ConcurrentDictionary<string, AgentProblem> recent = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly List<AgentProblem> pending = [];
    private int droppedFromBuffer;
    private int sinceHeartbeat;

    /// <summary>The newest problems, for status.json and the window.</summary>
    public IReadOnlyList<AgentProblem> Recent => recent.Values.OrderByDescending(p => p.LastAt).Take(10).ToList();

    public int SinceHeartbeat => Volatile.Read(ref sinceHeartbeat);

    public int TakeSinceHeartbeat() => Interlocked.Exchange(ref sinceHeartbeat, 0);

    public void Report(string component, string operation, Exception exception, bool fatal = false) =>
        Report(component, operation, exception.Message, exception, fatal);

    public void Report(string component, string operation, string message, Exception? exception = null, bool fatal = false)
    {
        logger.Log(fatal ? LogLevel.Critical : LogLevel.Error, exception, "{Component}, {Operation}: {Message}", component, operation, message);
        Interlocked.Increment(ref sinceHeartbeat);
        var now = DateTimeOffset.UtcNow;
        var key = $"{component}\n{message}";
        var stack = exception?.ToString().Split('\n').Take(20) is { } lines ? string.Join('\n', lines) : null;
        var current = recent.AddOrUpdate(key,
            _ => new AgentProblem(component, operation, message, exception?.GetType().FullName, stack, 1, now, now, fatal),
            (_, p) => p with { Count = p.Count + 1, LastAt = now, Operation = operation, Stack = stack ?? p.Stack, Fatal = p.Fatal || fatal });
        // Once an hour per component and message, with how often it happened since.
        if (current.Count == 1 || now - current.FirstAt >= TimeSpan.FromHours(1) || fatal)
        {
            if (current.Count > 1)
            {
                recent[key] = current with { Count = 0, FirstAt = now };
            }
            Enqueue(current);
        }
        foreach (var (k, p) in recent)
        {
            if (now - p.LastAt > TimeSpan.FromHours(2))
            {
                recent.TryRemove(k, out _);
            }
        }
    }

    private void Enqueue(AgentProblem problem)
    {
        lock (gate)
        {
            pending.Add(problem);
            if (pending.Count > MaxBuffered)
            {
                pending.RemoveAt(0);
                droppedFromBuffer++;
            }
        }
    }

    /// <summary>The buffered problems, taken for sending; a full buffer adds a note of how many went.</summary>
    public IReadOnlyList<AgentProblem> Take()
    {
        lock (gate)
        {
            var taken = pending.ToList();
            pending.Clear();
            if (droppedFromBuffer > 0)
            {
                var now = DateTimeOffset.UtcNow;
                taken.Add(new AgentProblem("agent", "buffer", $"{droppedFromBuffer} older problems were dropped while offline (at most {MaxBuffered} are kept).",
                    null, null, droppedFromBuffer, now, now));
                droppedFromBuffer = 0;
            }
            return taken;
        }
    }

    /// <summary>Puts problems back that could not be sent.</summary>
    public void Return(IReadOnlyList<AgentProblem> problems)
    {
        lock (gate)
        {
            pending.InsertRange(0, problems);
            while (pending.Count > MaxBuffered)
            {
                pending.RemoveAt(0);
                droppedFromBuffer++;
            }
        }
    }
}

/// <summary>Rules 1 and 2 (§3.8): each unit of work in its own try/catch, with a timeout; a failure is reported, the next unit runs.</summary>
internal static class Isolated
{
    /// <summary>The parts left empty because this Windows doesn't have them, at debug level.</summary>
    public static ILogger Logger { get; set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static async Task<T?> RunAsync<T>(ProblemReporter problems, string component, string operation, TimeSpan timeout,
        Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await work(limit.Token).WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            problems.Report(component, operation, $"timed out after {timeout.TotalSeconds:0} s");
            return default;
        }
        catch (Exception e)
        {
            problems.Report(component, operation, e);
            return default;
        }
    }

    public static async Task RunAsync(ProblemReporter problems, string component, string operation, TimeSpan timeout,
        Func<CancellationToken, Task> work, CancellationToken ct) =>
        await RunAsync<bool>(problems, component, operation, timeout, async t =>
        {
            await work(t);
            return true;
        }, ct);

    /// <summary>
    /// A synchronous part of a collector (a WMI query): its own try/catch, its error added to the section's. What this Windows
    /// doesn't have (<see cref="Collectors.NotAvailableException"/>: SMART in a VM, a missing role's WMI class) is no error: the
    /// part stays empty and the rest of the section is collected as always.
    /// </summary>
    public static T? Part<T>(List<string> errors, string part, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Collectors.NotAvailableException e)
        {
            Logger.LogDebug("{Part}: {Message}; the part stays empty, not an error", part, e.Message);
            return default;
        }
        catch (Exception e)
        {
            errors.Add($"{part}: {e.GetType().Name}: {e.Message}");
            return default;
        }
    }
}
