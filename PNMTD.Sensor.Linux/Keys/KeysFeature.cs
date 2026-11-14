using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.Service;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>
/// SSH keys on the connection (docs/linux-sensor-plan.md §6): announces <c>keys.v1</c> always (the <c>ssh-keys</c> section) and
/// <c>keys.remove.v1</c> only while config.yaml allows removal (a change of the rights reconnects, so the hello says it); handles the signed
/// <c>remove-ssh-key</c> (<see cref="KeyRemoval"/>): answers <c>key-removal</c> with what was done, then the ack, then collects <c>ssh-keys</c>
/// at once. Its loop is the watch of §6.2: one inotify instance over the folders of the last scan; a change collects <c>ssh-keys</c> after 3
/// seconds of quiet, so a key added by hand reaches PNMT within about 10 seconds.
/// </summary>
internal sealed class KeysFeature(
    SshKeysCollector collector,
    KeyRemoval removal,
    SectionScheduler sections,
    ProblemReporter problems,
    IEnumerable<IAgentRightsSource> rightsSources,
    ILogger<KeysFeature> logger) : IAgentFeature
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private readonly IAgentRightsSource? rights = rightsSources.FirstOrDefault();

    public string Name => "ssh-keys";

    public IReadOnlyList<string> Capabilities => rights?.Current.Keys.Remove == true ? [AgentCapabilities.Keys, AgentCapabilities.KeysRemove] : [AgentCapabilities.Keys];

    public void Configure(AgentConfig config)
    {
    }

    public Task ReportAsync(AgentSession session, CancellationToken ct) => Task.CompletedTask;

    public async Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct)
    {
        if (request.Type != AgentMessageTypes.RemoveSshKey)
        {
            return null;
        }
        var body = AgentJson.Deserialize<RemoveSshKeyRequest>(request.Body);
        if (body is null)
        {
            return new Ack(request.RequestId, false, "The request has no body.");
        }
        logger.LogInformation("PNMT asks to remove the key {Fingerprint} from {Path} ({Account}), removal {Id}", body.Fingerprint, body.Path, body.Account, body.RemovalId);
        SshKeyRemovalResult result;
        try
        {
            result = await Task.Run(() => removal.Remove(body, request.RequestId), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            problems.Report("ssh-keys", "removing a key", e);
            result = new SshKeyRemovalResult(request.RequestId, body.RemovalId, SshKeyRemovalOutcome.Failed, 0, null, null, $"{e.GetType().Name}: {e.Message}");
        }
        await session.SendAsync(AgentMessageTypes.KeyRemoval, result, ct);
        sections.Request([SectionNames.SshKeys]);
        return result.Outcome is SshKeyRemovalOutcome.Removed or SshKeyRemovalOutcome.NotFound
            ? new Ack(request.RequestId, true)
            : new Ack(request.RequestId, false, result.Message);
    }

    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => Watch(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task Watch(CancellationToken ct)
    {
        KeyWatch watch;
        try
        {
            watch = new KeyWatch();
        }
        catch (IOException e)
        {
            problems.Report("ssh-keys", "watching the key files", $"{e.Message}: key files are read every 5 minutes only.");
            return;
        }
        using (watch)
        {
            KeyScan? watched = null;
            DateTimeOffset? changedAt = null;
            var limitSaid = false;
            while (!ct.IsCancellationRequested)
            {
                if (collector.Last is { } scan && !ReferenceEquals(scan, watched))
                {
                    watched = scan;
                    watch.Update(scan.KeyFolders, scan.Homes);
                    logger.LogDebug("ssh-keys: watching {Count} folders", watch.Count);
                    if (watch.TakeLimit() is { } limit && !limitSaid)
                    {
                        limitSaid = true;
                        problems.Report("ssh-keys", "watching the key files", limit);
                    }
                }
                if (watch.Wait(1000, out var changed))
                {
                    changedAt = DateTimeOffset.UtcNow;
                    logger.LogDebug("ssh-keys: {Path} changed", changed);
                }
                if (changedAt is { } at && DateTimeOffset.UtcNow - at >= Quiet)
                {
                    changedAt = null;
                    logger.LogDebug("ssh-keys: quiet for {Seconds} s after a change: collecting", Quiet.TotalSeconds);
                    sections.CollectSoon([SectionNames.SshKeys]);
                }
                if (watched is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }
    }
}
