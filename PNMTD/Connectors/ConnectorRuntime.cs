using System.Collections.Concurrent;
using PNMTD.Lib.Helper;

namespace PNMTD.Connectors;

/// <summary>
/// Failed keys per address (docs/connectors-plan.md §6.5): <c>Connectors__FailedKeysPerAddress</c> within 10 minutes block the address
/// on <c>/connector</c> for <c>Connectors__BlockMinutes</c>. Pruned above 10 000 addresses, as <c>JoinGuard</c> is.
/// </summary>
public sealed class ConnectorGuard
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTime Start, int Count, DateTime? BlockedUntil)> addresses = new();

    /// <summary>When the address is blocked, until when.</summary>
    public DateTime? BlockedUntil(string address)
    {
        var now = Clock.UtcNow;
        return addresses.TryGetValue(address, out var e) && e.BlockedUntil is { } until && until > now ? until : null;
    }

    /// <summary>Counts a failed key; true when this one blocked the address.</summary>
    public bool Failed(string address, int perWindow, TimeSpan blockFor)
    {
        var now = Clock.UtcNow;
        var blocked = false;
        (DateTime Start, int Count, DateTime? BlockedUntil) Next((DateTime Start, int Count, DateTime? BlockedUntil) e)
        {
            var count = e.Count + 1;
            if (count >= Math.Max(1, perWindow))
            {
                blocked = true;
                return (now, 0, now + blockFor);
            }
            return (e.Start, count, null);
        }
        addresses.AddOrUpdate(address, _ => Next((now, 0, null)),
            (_, e) => e.BlockedUntil is { } until && until > now ? e : Next(now - e.Start >= Window ? (now, 0, null) : e));
        if (addresses.Count > 10_000)
        {
            foreach (var (key, value) in addresses)
            {
                if (now - value.Start >= Window && (value.BlockedUntil is null || value.BlockedUntil < now))
                {
                    addresses.TryRemove(key, out _);
                }
            }
        }
        return blocked;
    }

    /// <summary>For tests.</summary>
    public void Reset() => addresses.Clear();
}

/// <summary>
/// The secure arguments of queued calls, in memory only (§5.4), like MikroTik's credential vault: removed when the run starts or the
/// call ends. A restart loses them, and such a call expires rather than running without them.
/// </summary>
public sealed class ConnectorSecretVault
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<string, string>> secrets = new();

    public void Put(Guid callId, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > 0)
        {
            secrets[callId] = values;
        }
    }

    public IReadOnlyDictionary<string, string>? Get(Guid callId) => secrets.TryGetValue(callId, out var values) ? values : null;

    public void Remove(Guid callId) => secrets.TryRemove(callId, out _);

    /// <summary>For tests: a restart.</summary>
    public void Clear() => secrets.Clear();
}

/// <summary>
/// The answers that wait for their call (§9.3): <see cref="WaitAsync"/> returns when the worker made the call final, or the time is up.
/// The worker completes the signal after the transaction that made the call final.
/// </summary>
public sealed class ConnectorCallSignals
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> waiting = new();

    public Task WaitAsync(Guid callId, TimeSpan timeout, CancellationToken ct)
    {
        var source = waiting.GetOrAdd(callId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return source.Task.WaitAsync(timeout, ct).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Complete(Guid callId)
    {
        if (waiting.TryRemove(callId, out var source))
        {
            source.TrySetResult();
        }
    }

    /// <summary>A waiter that gave up removes its signal, unless the worker took it meanwhile.</summary>
    public void Forget(Guid callId) => waiting.TryRemove(callId, out _);
}

/// <summary>
/// Wakes the worker (§9.1): after the endpoint stored a call, when a run ends, when an agent connects, when a parse arrives. The
/// agents' side nudges through this, so it needs no reference to the worker.
/// </summary>
public sealed class ConnectorQueue
{
    public event Action? Nudged;

    public void Nudge() => Nudged?.Invoke();
}

/// <summary>A ticket is asked for at most once a second (§4.6), counted apart from the calls per hour.</summary>
public sealed class ConnectorPollGate
{
    private readonly ConcurrentDictionary<Guid, DateTime> last = new();

    public bool TryEnter(Guid ticket)
    {
        var now = Clock.UtcNow;
        var accepted = false;
        last.AddOrUpdate(ticket, _ =>
        {
            accepted = true;
            return now;
        }, (_, previous) =>
        {
            accepted = now - previous >= TimeSpan.FromSeconds(1);
            return accepted ? now : previous;
        });
        if (last.Count > 50_000)
        {
            foreach (var (key, value) in last)
            {
                if (now - value > TimeSpan.FromMinutes(1))
                {
                    last.TryRemove(key, out _);
                }
            }
        }
        return accepted;
    }

    public void Reset() => last.Clear();
}
