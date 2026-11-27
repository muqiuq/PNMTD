using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Notifications;
using PNMTD.Services.Events;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Licenses;

/// <summary>Who caused an evaluation: a person's change (the notices' actor, never told what they did), or PNMT.</summary>
public readonly record struct LicenseActor(Guid? Id, string Name)
{
    public static readonly LicenseActor Pnmt = new(null, "PNMT");
}

/// <summary>
/// The evaluation (docs/licences-plan.md §5.5), only while the feature is on: the automatic renewals that are due, then the charges
/// that are due, then every item that isn't retired, with hosts or without: its verdict stored on it, an event for each host sensor
/// whose key differs, and the owner's notices. One at a time, the timer's run and a single item's after a change alike.
/// </summary>
public sealed class LicenseEvaluator(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<LicenseOptions> options, IConfiguration configuration,
    UserNotifications notices, ILogger<LicenseEvaluator> logger)
{
    public const string Source = "Licence evaluation";

    private readonly SemaphoreSlim gate = new(1, 1);

    public bool Enabled => LicenseSetup.Enabled(configuration);

    /// <summary>Every item; the task's run. Writes <c>LAST_LICENSE_EVALUATION</c>.</summary>
    public Task RunOnceAsync(CancellationToken ct) => EvaluateAsync(null, LicenseActor.Pnmt, ct);

    /// <summary>One item after a change, or all (<paramref name="only"/> null), under the same lock.</summary>
    public async Task EvaluateAsync(Guid? only, LicenseActor actor, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            await EvaluateLockedAsync(db, only is { } id ? [id] : null, actor, ct);
            if (only is null)
            {
                db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_LICENSE_EVALUATION);
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>These items (null: all that aren't retired).</summary>
    public async Task EvaluateManyAsync(IReadOnlyList<Guid> ids, LicenseActor actor, CancellationToken ct)
    {
        if (!Enabled || ids.Count == 0)
        {
            return;
        }
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            await EvaluateLockedAsync(db, ids, actor, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EvaluateLockedAsync(PnmtdDbContext db, IReadOnlyList<Guid>? only, LicenseActor actor, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var today = Clock.Today;
        var items = await db.Licenses.Where(l => l.Status != LicenseStatus.Retired && (only == null || only.Contains(l.Id))).OrderBy(l => l.Number)
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            return;
        }
        var ids = items.Select(i => i.Id).ToList();
        var assignments = (await db.LicenseAssignments.Where(a => ids.Contains(a.LicenseId)).ToListAsync(ct)).ToLookup(a => a.LicenseId);
        var templateIds = items.Where(i => i.TemplateId != null).Select(i => i.TemplateId!.Value).Distinct().ToList();
        var templates = await db.LicenseTemplates.AsNoTracking().Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.EndOfLifeOn, t.EndOfLifeNote }).ToDictionaryAsync(t => t.Id, ct);
        var lastRenewals = (await db.LicenseRenewals.AsNoTracking().Where(r => ids.Contains(r.LicenseId))
            .Select(r => new { r.LicenseId, r.Kind, r.CreatedAt }).ToListAsync(ct))
            .GroupBy(r => r.LicenseId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First().Kind);
        var lastPeriods = (await db.LicenseCharges.AsNoTracking()
            .Where(c => ids.Contains(c.LicenseId) && c.Source == LicenseChargeSource.Automatic && c.Kind == LicenseChargeKind.Period && c.PeriodFrom != null)
            .Select(c => new { c.LicenseId, c.PeriodFrom }).ToListAsync(ct))
            .GroupBy(c => c.LicenseId).ToDictionary(g => g.Key, g => g.Max(c => c.PeriodFrom!.Value));
        var purchased = (await db.LicenseCharges.AsNoTracking()
            .Where(c => ids.Contains(c.LicenseId) && c.Source == LicenseChargeSource.Automatic && c.Kind == LicenseChargeKind.Purchase)
            .Select(c => c.LicenseId).Distinct().ToListAsync(ct)).ToHashSet();
        List<Guid>? unowned = null;

        foreach (var item in items)
        {
            var changed = false;
            var lastAutomatic = lastRenewals.TryGetValue(item.Id, out var lastKind) && lastKind == LicenseRenewalKind.AutomaticRenewal;

            // 1. The automatic renewals that are due (§5.6): a term at a time, up to today.
            if (item.Status == LicenseStatus.Active && item.Renewal == LicenseRenewal.Automatic && item.ExpiresOn is { } expires
                && item.TermMonths is > 0 && today > expires)
            {
                var from = expires;
                // The expiry it had is what its state announced, even if it was never evaluated before: the renewal shows 246.
                item.AnnouncedExpiresOn ??= from;
                var guard = 0;
                while (item.ExpiresOn < today && guard++ < 240)
                {
                    var previous = item.ExpiresOn!.Value;
                    var next = previous.AddDays(1).AddMonths(item.TermMonths.Value).AddDays(-1);
                    db.LicenseRenewals.Add(new LicenseRenewalEntity
                    {
                        Id = Guid.NewGuid(), LicenseId = item.Id, Kind = LicenseRenewalKind.AutomaticRenewal, On = previous.AddDays(1),
                        PreviousExpiresOn = previous, NewExpiresOn = next, CreatedAt = Clock.UtcNow, CreatedBy = "automatically",
                    });
                    item.ExpiresOn = next;
                }
                item.AutoRenewedThrough = item.ExpiresOn;
                lastAutomatic = true;
                Audit.AuditLog.Record(db, Audit.AuditEvents.LicenseRenewedAutomatically,
                    $"{LicenseWords.Number(item.Type, item.Number)} {item.Name} renewed automatically until {Clock.FormatDay(item.ExpiresOn!.Value)} (was {Clock.FormatDay(from)})",
                    Audit.AuditWho.System, Target(item));
                changed = true;
            }

            // 2. The charges that are due (§6.2), after the renewals, so a contract renewed today is charged today.
            var cost = CostItem.From(item);
            if (item.RecordCharges && cost.Periodic)
            {
                var start = item.ChargesFrom ?? cost.Start;
                var from = lastPeriods.TryGetValue(item.Id, out var last) && last >= start ? last.AddDays(1) : start;
                foreach (var due in CostMath.DueCharges(cost, from, today))
                {
                    db.LicenseCharges.Add(new LicenseChargeEntity
                    {
                        Id = Guid.NewGuid(), LicenseId = item.Id, Kind = LicenseChargeKind.Period, Source = LicenseChargeSource.Automatic, On = due.Day,
                        PeriodFrom = due.Day, PeriodTo = due.PeriodTo, Amount = CostMath.Cents(due.Amount), Currency = item.Currency,
                        Estimated = item.Billing == LicenseBilling.Variable, CreatedAt = Clock.UtcNow,
                    });
                    changed = true;
                }
            }
            else if (item.RecordCharges && item.Billing == LicenseBilling.OneTime && CostMath.UnitPrice(cost) is { } price && !purchased.Contains(item.Id)
                && cost.Start <= today && cost.Start >= (item.ChargesFrom ?? cost.Start))
            {
                db.LicenseCharges.Add(new LicenseChargeEntity
                {
                    Id = Guid.NewGuid(), LicenseId = item.Id, Kind = LicenseChargeKind.Purchase, Source = LicenseChargeSource.Automatic, On = cost.Start,
                    Amount = CostMath.Cents(price), Currency = item.Currency, CreatedAt = Clock.UtcNow,
                });
                purchased.Add(item.Id);
                changed = true;
            }

            // 3. The verdict, stored on the item; the host sensors; the owner's notices.
            var own = assignments[item.Id].ToList();
            var template = item.TemplateId is { } tid && templates.TryGetValue(tid, out var t) ? t : null;
            var (eol, eolNote) = item.EndOfLifeFromTemplate && template is not null ? (template.EndOfLifeOn, template.EndOfLifeNote) : (item.EndOfLifeOn, item.EndOfLifeNote);
            var facts = Facts(item, eol, eolNote, own.Sum(a => a.Seats), lastAutomatic);
            var verdict = LicenseVerdict.Evaluate(facts, today);
            if (verdict is null)
            {
                continue;
            }
            if (verdict.Key != item.VerdictKey)
            {
                item.VerdictCode = verdict.Code;
                item.VerdictKey = verdict.Key;
                item.VerdictMessage = Cut(verdict.Message, 1000);
                item.VerdictAt = Clock.UtcNow;
                // An OK state announces its expiry; the first state of all does too, so a renewal after a warning shows 246 (§4).
                if (verdict.Ok || item.AnnouncedExpiresOn is null)
                {
                    item.AnnouncedExpiresOn = item.ExpiresOn;
                }
                changed = true;
            }
            var stale = own.Where(a => a.SensorId != null && a.SensorKey != item.VerdictKey).ToList();

            var noticeKey = NoticeKey(verdict, lastAutomatic, today, o.RemindDays, item.ExpiresOn);
            if (noticeKey is not null && noticeKey != item.NoticeKey)
            {
                IReadOnlyList<Guid> recipients = item.OwnerId is { } ownerId ? [ownerId] : (unowned ??= await UnownedChoosersAsync(db, ct));
                await NotifyAsync(db, item, item.OwnerId is null ? UserNotificationKinds.LicenseUnowned : UserNotificationKinds.LicenseWarning,
                    recipients, verdict.Message, actor, ct);
                changed = true;
            }
            var settled = noticeKey ?? verdict.Key;
            if (item.NoticeKey != settled)
            {
                item.NoticeKey = settled;
                changed = true;
            }
            if (ReviewDue(item, today) is { } reviewDue && item.ReviewNoticeOn != reviewDue)
            {
                if (item.OwnerId is { } owner)
                {
                    await NotifyAsync(db, item, UserNotificationKinds.LicenseReview, [owner],
                        $"Review due since {Clock.FormatDay(reviewDue)} · {item.Name} ({LicenseWords.Number(item.Type, item.Number)})", actor, ct);
                }
                item.ReviewNoticeOn = reviewDue;
                changed = true;
            }

            if (stale.Count > 0)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                foreach (var assignment in stale)
                {
                    await SensorEventWriter.WriteAsync(db, assignment.SensorId!.Value, item.VerdictCode ?? PNMTStatusCodes.LICENSE_OK,
                        item.VerdictMessage, Source, ct);
                    assignment.SensorKey = item.VerdictKey;
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            else if (changed)
            {
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>The facts of §4 from the item, its end of life, its used seats and its newest renewal.</summary>
    public static LicenseFacts Facts(LicenseEntity item, DateOnly? eol, string? eolNote, int usedSeats, bool lastAutomatic) =>
        new(item.Type, item.Kind, item.Number, item.Name, item.Status, item.ExpiresOn, item.Renewal, item.WarnDays, item.NoticeDays,
            item.WarnBeforeRenewal, eol, eolNote, item.EolWarnDays, item.SeatsFromHosts, item.Seats, usedSeats, item.SeatUnit, item.AnnouncedExpiresOn,
            lastAutomatic);

    /// <summary>
    /// What the owner was last told (§5.5): a finding's key, with <i>remind</i> once its date is within <c>RemindDays</c>; an automatic
    /// renewal; nothing for any other OK state.
    /// </summary>
    public static string? NoticeKey(LicenseVerdictResult verdict, bool lastAutomatic, DateOnly today, int remindDays, DateOnly? expiresOn)
    {
        if (!verdict.Ok)
        {
            var worst = verdict.Findings[0];
            var remind = remindDays > 0 && worst.Code is PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE or PNMTStatusCodes.LICENSE_EOL_SOON
                && worst.On is { } on && on >= today && on.DayNumber - today.DayNumber <= remindDays;
            return remind ? $"{verdict.Key}|remind" : verdict.Key;
        }
        return verdict.Code == PNMTStatusCodes.LICENSE_RENEWED && lastAutomatic ? $"renewed|{expiresOn:yyyy-MM-dd}" : null;
    }

    /// <summary>The day a review fell due (§5.7), when it has; null without a rhythm or before it.</summary>
    public static DateOnly? ReviewDue(LicenseEntity item, DateOnly today)
    {
        if (item.ReviewMonths is not > 0 || item.Status == LicenseStatus.Retired)
        {
            return null;
        }
        var due = (item.ReviewedOn ?? DateOnly.FromDateTime(Clock.ToLocal(item.CreatedAt))).AddMonths(item.ReviewMonths.Value);
        return due <= today ? due : null;
    }

    private async Task NotifyAsync(PnmtdDbContext db, LicenseEntity item, string kind, IReadOnlyList<Guid> recipients, string detail, LicenseActor actor,
        CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            return;
        }
        try
        {
            await notices.EnqueueAsync(db, new UserNotice(kind, recipients, item.Id, kind, item.Id, actor.Id, actor.Name, Cut(detail, 300)), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The notice about {Number} couldn't be queued", LicenseWords.Number(item.Type, item.Number));
        }
    }

    /// <summary>Everyone who chose the notices about items nobody owns (off by default, §5.5); the right is checked when sending.</summary>
    public static Task<List<Guid>> UnownedChoosersAsync(PnmtdDbContext db, CancellationToken ct) =>
        db.UserNotificationPreferences.AsNoTracking()
            .Where(p => p.Kind == UserNotificationKinds.LicenseUnowned && p.Channel == UserNotificationChannel.Email && p.Enabled)
            .Select(p => p.UserId).Distinct().ToListAsync(ct);

    public static Audit.AuditTarget Target(LicenseEntity item) =>
        Audit.AuditTarget.Of(Audit.AuditTargets.License, item.Id, $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}");

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>The evaluation's timer (§5.5): every minute behind a run guard, the first half a minute after the start.</summary>
public sealed class LicenseEvaluatorTask(LicenseEvaluator evaluator, ILogger<LicenseEvaluatorTask> logger) : IHostedService, IDisposable
{
    private readonly RunGuard runGuard = new();
    private readonly CancellationTokenSource stopping = new();
    private Timer? timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        await stopping.CancelAsync();
    }

    public void Dispose()
    {
        timer?.Dispose();
        stopping.Dispose();
    }

    private async Task TickAsync()
    {
        if (!runGuard.TryEnter())
        {
            return;
        }
        try
        {
            await evaluator.RunOnceAsync(stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The licence evaluation failed");
        }
        finally
        {
            runGuard.Exit();
        }
    }
}

/// <summary>
/// The item sensors follow the switch (docs/licences-plan.md §13.3), at every start after the migrations, whatever
/// <c>BackgroundTasks:Enabled</c> says: off, every item sensor goes with its events and the assignments stay; on, every assignment without
/// a sensor gets one again, evaluated at once. Audited once, and nothing when nothing changes.
/// </summary>
public static class LicenseSensorSync
{
    public static async Task RunAsync(PnmtdDbContext db, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (!LicenseSetup.Enabled(configuration))
        {
            var sensors = await db.Sensors.Where(s => s.Type == SensorType.LICENSE).Select(s => new { s.Id, s.ParentId }).ToListAsync(ct);
            if (sensors.Count == 0)
            {
                return;
            }
            var ids = sensors.Select(s => s.Id).ToList();
            var assignments = await db.LicenseAssignments.CountAsync(a => a.SensorId != null && ids.Contains(a.SensorId.Value), ct);
            await db.LicenseAssignments.Where(a => a.SensorId != null && ids.Contains(a.SensorId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.SensorId, (Guid?)null).SetProperty(a => a.SensorKey, (string?)null), ct);
            await db.Sensors.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
            Audit.AuditLog.Record(db, Audit.AuditEvents.LicenseSensorsRemoved,
                $"Licences & services is turned off: {sensors.Count} item sensor{(sensors.Count == 1 ? "" : "s")} removed with their events; their {assignments} assignment{(assignments == 1 ? "" : "s")} stay.",
                Audit.AuditWho.System, new Audit.AuditTarget(Audit.AuditTargets.Host, null, "item sensors", sensors.Select(s => s.ParentId).Distinct().Take(1000).ToList()));
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Licences & services is off: {Count} item sensors removed", sensors.Count);
            return;
        }
        var missing = await db.LicenseAssignments.Where(a => a.SensorId == null)
            .Join(db.Licenses, a => a.LicenseId, l => l.Id, (a, l) => new { Assignment = a, l.Type, l.Name, l.Number, l.Status })
            .Where(x => x.Status != LicenseStatus.Retired).ToListAsync(ct);
        if (missing.Count == 0)
        {
            return;
        }
        var now = Clock.UtcNow;
        foreach (var m in missing)
        {
            var sensor = LicenseSensors.New(m.Assignment.HostId, m.Type, m.Name, m.Number, now);
            db.Sensors.Add(sensor);
            m.Assignment.SensorId = sensor.Id;
            m.Assignment.SensorKey = null;
        }
        Audit.AuditLog.Record(db, Audit.AuditEvents.LicenseSensorsMade,
            $"Licences & services is turned on: {missing.Count} item sensor{(missing.Count == 1 ? "" : "s")} made from the assignments.",
            Audit.AuditWho.System, new Audit.AuditTarget(Audit.AuditTargets.Host, null, "item sensors",
                missing.Select(m => m.Assignment.HostId).Distinct().Take(1000).ToList()));
        await db.SaveChangesAsync(ct);
        await services.GetRequiredService<LicenseEvaluator>().EvaluateManyAsync(missing.Select(m => m.Assignment.LicenseId).Distinct().ToList(),
            LicenseActor.Pnmt, ct);
        logger.LogInformation("Licences & services is on: {Count} item sensors made", missing.Count);
    }
}

/// <summary>An item sensor as PNMT makes it (§5.3): no interval, no grace period, the host's importance.</summary>
public static class LicenseSensors
{
    public static SensorEntity New(Guid hostId, LicenseType type, string name, int number, DateTime now)
    {
        var sensor = new SensorEntity
        {
            Id = Guid.NewGuid(), ParentId = hostId, Type = SensorType.LICENSE, Name = LicenseWords.SensorName(type, name, number), Enabled = true,
            Interval = 0, GracePeriod = 0, Created = now,
        };
        sensor.SetNewSecretToken();
        return sensor;
    }
}
