using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.Resilience;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Service;

/// <summary>
/// Runs the collectors (docs/windows-sensor-plan.md §4.1, §3.8 rule 8): one at a time, at below-normal priority, each on its
/// interval, all of them every 6 hours and on request; each with a 30 s timeout, a failure reported and the next one run.
/// Keeps the latest section of each kind (sent again after a reconnect) and which ones changed since they were sent.
/// </summary>
internal sealed class SectionScheduler(IServiceProvider services, ProblemReporter problems, ILogger<SectionScheduler> logger)
{
    // Resolved when first used: the access collector needs the login stream, which asks this scheduler for the event log.
    private List<Collector>? collectors;

    private List<Collector> all => collectors ??= services.GetServices<Collector>().ToList();
    private readonly ConcurrentDictionary<string, SectionMessage> latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> sentHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Hash)> sentAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> due = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> forced = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0);
    private DateTimeOffset nextFull = DateTimeOffset.MinValue;

    public TimeSpan FullUpdate { get; set; } = TimeSpan.FromHours(6);

    public IReadOnlyCollection<string> Names => all.Select(c => c.Name).ToList();

    public IReadOnlyDictionary<string, string> Hashes => latest.ToDictionary(p => p.Key, p => p.Value.Hash);

    public SectionMessage? Latest(string name) => latest.GetValueOrDefault(name);

    /// <summary>Collects these sections (all when empty) now, and sends them even if unchanged.</summary>
    public void Request(IReadOnlyCollection<string> names)
    {
        foreach (var collector in all.Where(c => names.Count == 0 || names.Contains(c.Name)))
        {
            due[collector.Name] = DateTimeOffset.MinValue;
            forced[collector.Name] = true;
        }
        wake.Release();
    }

    /// <summary>After a reconnect: every section's latest goes out again (§3.5).</summary>
    public void ResendAll()
    {
        logger.LogDebug("Connected: every collected section goes out again ({Sections})", string.Join(", ", latest.Keys.Order()));
        sentHashes.Clear();
    }

    /// <summary>The sections to send: changed since sent, or asked for.</summary>
    public List<SectionMessage> TakeForSending()
    {
        var result = new List<SectionMessage>();
        foreach (var (name, message) in latest)
        {
            var wasForced = forced.TryRemove(name + ":collected", out _);
            if (wasForced || !sentHashes.TryGetValue(name, out var hash) || hash != message.Hash)
            {
                result.Add(message);
            }
        }
        return result;
    }

    public void Sent(SectionMessage message)
    {
        sentHashes[message.Name] = message.Hash;
        sentAt[message.Name] = (DateTimeOffset.UtcNow, message.Hash);
    }

    /// <summary>Whether the named sections were collected since <paramref name="since"/>: for a collect request's answer.</summary>
    public bool CollectedSince(IReadOnlyCollection<string> names, DateTimeOffset since) =>
        all.Where(c => names.Count == 0 || names.Contains(c.Name)).All(c => latest.TryGetValue(c.Name, out var m) && m.CollectedAt >= since);

    public async Task RunAsync(CancellationToken ct)
    {
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            due[SectionNames.Network] = DateTimeOffset.UtcNow.AddSeconds(5);
            wake.Release();
        };
        foreach (var collector in all)
        {
            due.TryAdd(collector.Name, DateTimeOffset.MinValue);
        }
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= nextFull)
            {
                nextFull = now + FullUpdate;
                foreach (var collector in all)
                {
                    due[collector.Name] = DateTimeOffset.MinValue;
                    forced[collector.Name] = true;
                }
            }
            foreach (var collector in all.Where(c => due.GetValueOrDefault(c.Name) <= DateTimeOffset.UtcNow))
            {
                due[collector.Name] = DateTimeOffset.UtcNow + collector.Interval;
                await CollectAsync(collector, ct);
            }
            var next = due.Values.DefaultIfEmpty(DateTimeOffset.UtcNow.AddMinutes(1)).Min();
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await wake.WaitAsync(wait < TimeSpan.FromMinutes(5) ? wait : TimeSpan.FromMinutes(5), ct);
            }
        }
    }

    private async Task CollectAsync(Collector collector, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        logger.LogDebug("Collecting {Section} (at most {Timeout:0} s){Forced}", collector.Name, collector.Timeout.TotalSeconds,
            forced.ContainsKey(collector.Name) ? ", asked for" : "");
        var message = await Isolated.RunAsync(problems, $"collector {collector.Name}", "collect", collector.Timeout, token =>
            Task.Factory.StartNew(() =>
            {
                var thread = Thread.CurrentThread;
                var priority = thread.Priority;
                thread.Priority = ThreadPriority.BelowNormal;
                try
                {
                    var errors = new List<string>();
                    var data = collector.Collect(errors, token);
                    foreach (var error in errors)
                    {
                        logger.LogWarning("{Section}: {Error}", collector.Name, error);
                    }
                    return collector.ToMessage(data, errors);
                }
                finally
                {
                    thread.Priority = priority;
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default), ct);
        var took = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        if (message is null)
        {
            // Isolated reported it (a problem, also in PNMT's agent log); the section keeps what was sent last.
            logger.LogDebug("Collecting {Section} failed after {Ms:0} ms: nothing new is sent", collector.Name, took);
            return;
        }
        var sentHash = sentHashes.GetValueOrDefault(collector.Name);
        latest[collector.Name] = message;
        var wasForced = forced.TryRemove(collector.Name, out _);
        if (wasForced)
        {
            forced[collector.Name + ":collected"] = true;
        }
        logger.LogDebug("Collected {Section} in {Ms:0} ms: {Bytes} bytes, hash {Hash}{Errors}; {Next}", collector.Name, took, Bytes(message), message.Hash,
            message.Errors is { Count: > 0 } e ? $", {e.Count} errors" : "",
            wasForced ? "asked for: sent" : sentHash is null ? "not sent yet: sent" : sentHash != message.Hash ? "changed: sent" : "unchanged: not sent again");
    }

    public static int Bytes(SectionMessage message) => System.Text.Encoding.UTF8.GetByteCount(message.Data.GetRawText());

    /// <summary>The sections as the window shows them: when collected, how big, with how many errors, and when sent.</summary>
    public IReadOnlyList<SectionStatus> Status() => all.Select(c =>
    {
        var message = latest.GetValueOrDefault(c.Name);
        var sent = sentAt.GetValueOrDefault(c.Name);
        return new SectionStatus(c.Name, message?.CollectedAt, message is null ? null : Bytes(message), message?.Errors?.Count ?? 0,
            sent.At == default ? null : sent.At, sent.Hash is not null && sent.Hash == message?.Hash);
    }).ToList();

    /// <summary>Runs every collector once and answers the sections: <c>pnmt-sensor.exe collect</c>, for support.</summary>
    public async Task<List<SectionMessage>> CollectOnceAsync(CancellationToken ct)
    {
        foreach (var collector in all)
        {
            await CollectAsync(collector, ct);
        }
        return latest.Values.OrderBy(m => m.Name).ToList();
    }
}
