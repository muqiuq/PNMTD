using PNMTD.Agents;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.OsUpdates;

/// <summary>
/// 467's message for a server (docs/linux-os-updates-plan.md §5.4), kept on its agent row so the heartbeat reads it without a query: the
/// last upgrade PNMT ran failed (until a later one succeeds, or a person marks it handled), dpkg was interrupted, or the lists weren't
/// refreshed for <c>OsUpdates__ListsStaleDays</c>. Null while none applies, or while OS updates are off for the server or in PNMT.
/// </summary>
public sealed class OsUpdateConditions(IOptionsMonitor<OsUpdatesOptions> options)
{
    public async Task RefreshAsync(PnmtdDbContext db, AgentEntity agent, CancellationToken ct) =>
        agent.UpdateFailedMessage = await MessageAsync(db, agent, Clock.UtcNow, ct);

    public async Task<string?> MessageAsync(PnmtdDbContext db, AgentEntity agent, DateTime now, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled || !agent.OsUpdatesEnabled || agent.Platform != AgentPlatform.Linux)
        {
            return null;
        }
        var host = agent.Hostname;
        var lastUpgrade = await db.OsUpdateRuns.AsNoTracking()
            .Where(r => r.SensorId == agent.SensorId && r.Kind == OsUpdateKind.Upgrade && r.Trigger != OsUpdateTrigger.External
                        && (r.State == OsUpdateState.Succeeded || r.State == OsUpdateState.Warnings || r.State == OsUpdateState.Failed))
            .OrderByDescending(r => r.FinishedAt).FirstOrDefaultAsync(ct);
        if (lastUpgrade is { State: OsUpdateState.Failed, DismissedAt: null })
        {
            var at = (lastUpgrade.FinishedAt ?? lastUpgrade.RequestedAt).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            return Cuts.Cut($"{host}: the upgrade on {at} failed{(lastUpgrade.ExitCode is { } code ? $" (exit {code})" : "")}"
                            + (lastUpgrade.Message is { } m ? $": {m}" : ""), 2000);
        }
        if (agent.DpkgInterrupted)
        {
            return $"{host}: dpkg was interrupted: run dpkg --configure -a on the server";
        }
        // Stale lists are PNMT's concern only where it refreshes them: not when the server's config.yaml turned the update off.
        var updateAllowed = Agents.ServerRights.Read(agent.Rights)?.OsUpdates?.Update != false;
        if (o.DailyUpdate && updateAllowed && agent.ListsUpdatedAt is { } lists && now - lists > TimeSpan.FromDays(Math.Clamp(o.ListsStaleDays, 1, 365)))
        {
            var last = await db.OsUpdateRuns.AsNoTracking()
                .Where(r => r.SensorId == agent.SensorId && r.Kind == OsUpdateKind.Update && r.Message != null && r.FinishedAt != null)
                .OrderByDescending(r => r.FinishedAt).Select(r => r.Message).FirstOrDefaultAsync(ct);
            var days = (int)Math.Floor((now - lists).TotalDays);
            return Cuts.Cut($"{host}: the package lists weren't refreshed for {days} days (last {lists.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})"
                            + (last is not null ? $": {last}" : ""), 2000);
        }
        return null;
    }
}

/// <summary>Tells the evaluator (<see cref="OsAdvisoryEvaluator"/>) to look again: at one server, or at every one.</summary>
public sealed class OsAdvisoryNudge
{
    private readonly Channel<Guid?> channel = Channel.CreateBounded<Guid?>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Sensor(Guid sensorId) => channel.Writer.TryWrite(sensorId);

    public void All() => channel.Writer.TryWrite(null);

    public ChannelReader<Guid?> Reader => channel.Reader;
}

/// <summary>
/// A server's os-updates section (docs/linux-os-updates-plan.md §4.1, §5.2): its pending packages replaced by diffing (so FirstSeenAt
/// stays), its counts, lists' age and dpkg's state on the agent row, 467's message, and the evaluator nudged.
/// </summary>
public sealed class OsUpdatePackages(OsUpdateConditions conditions, OsAdvisoryNudge nudge, ILogger<OsUpdatePackages> logger)
{
    public async Task SectionAsync(PnmtdDbContext db, AgentEntity agent, SectionMessage message, CancellationToken ct)
    {
        OsUpdatesSection? section;
        try
        {
            section = AgentJson.Deserialize<OsUpdatesSection>(message.Data);
        }
        catch (JsonException e)
        {
            logger.LogInformation("{Host}: the os-updates section didn't parse: {Message}", agent.Hostname, e.Message);
            return;
        }
        if (section is null)
        {
            return;
        }
        var now = Clock.UtcNow;
        var incoming = section.Packages.Take(OsUpdatesSection.MaxPackages)
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new OsPendingPackageEntity
            {
                SensorId = agent.SensorId, Name = Cuts.Cut(p.Name, 200), Arch = Cuts.Cut(p.Arch ?? "", 40), Installed = Cuts.CutOrNull(p.Installed, 200),
                Candidate = Cuts.Cut(p.Candidate, 200), Source = Cuts.Cut(p.Source ?? p.Name, 200), SourceVersion = Cuts.Cut(p.SourceVersion ?? p.Candidate, 200),
                InstalledSourceVersion = Cuts.CutOrNull(p.InstalledSourceVersion ?? p.Installed, 200), Origin = Cuts.CutOrNull(p.Origin, 300), Security = p.Security,
                InUpgrade = p.InUpgrade, Phased = p.Phased, FirstSeenAt = now,
            })
            .DistinctBy(p => (p.Name, p.Arch)).ToList();
        var existing = await db.OsPendingPackages.Where(p => p.SensorId == agent.SensorId).ToListAsync(ct);
        var byKey = existing.ToDictionary(p => (p.Name, p.Arch));
        foreach (var package in incoming)
        {
            if (byKey.Remove((package.Name, package.Arch), out var row))
            {
                row.Installed = package.Installed;
                row.Candidate = package.Candidate;
                row.Source = package.Source;
                row.SourceVersion = package.SourceVersion;
                row.InstalledSourceVersion = package.InstalledSourceVersion;
                row.Origin = package.Origin;
                row.Security = package.Security;
                row.InUpgrade = package.InUpgrade;
                row.Phased = package.Phased;
            }
            else
            {
                db.OsPendingPackages.Add(package);
            }
        }
        db.OsPendingPackages.RemoveRange(byKey.Values);
        agent.PendingUpdates = Math.Max(section.PackagesTotal, incoming.Count(p => !p.Phased));
        agent.PendingSecurity = incoming.Count(p => p.Security);
        agent.ListsUpdatedAt = section.ListsUpdatedAt?.UtcDateTime;
        agent.DpkgInterrupted = section.DpkgInterrupted;
        agent.OsDistribution = Cuts.CutOrNull(section.Distribution, 60);
        await conditions.RefreshAsync(db, agent, ct);
        nudge.Sensor(agent.SensorId);
    }
}
