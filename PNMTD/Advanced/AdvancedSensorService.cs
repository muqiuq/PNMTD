using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Advanced;

/// <summary>Tests of rules (§6.6): 30 a minute per user.</summary>
public sealed class AdvancedTestGate
{
    public const int PerMinute = 30;

    private readonly ConcurrentDictionary<string, (DateTime Start, int Count)> counts = new();

    public bool TryEnter(string user)
    {
        var now = Clock.UtcNow;
        var entry = counts.AddOrUpdate(user, _ => (now, 1), (_, e) => now - e.Start >= TimeSpan.FromMinutes(1) ? (now, 1) : (e.Start, e.Count + 1));
        if (counts.Count > 10_000)
        {
            counts.Clear();
        }
        return entry.Count <= PerMinute;
    }
}

/// <summary>
/// The ADVANCED sensor through <c>/sensors</c> and its own endpoints (docs/advanced-sensor-plan.md §8): the setting, the
/// starter rule, the detail, the rules and fields, the reports, their paths and samples, trying rules, and copying them.
/// It judges nothing: every change of the rules or the settings sets <c>ReevaluateRequestedAt</c> with the change, and waits
/// up to 5 seconds for the evaluator to re-judge the newest report (§7.5).
/// </summary>
public sealed class AdvancedSensorService(PnmtdDbContext db, AdvancedEvaluator evaluator, IOptionsMonitor<AdvancedOptions> options, AdvancedTestGate tests,
    IHttpContextAccessor accessor)
{
    public const string StatusRuleName = "The app's status";

    private bool CallerHasDetails => accessor.HttpContext?.User is { } user && user.Has(Area.Monitoring, Level.Details);

    // --- through /sensors ------------------------------------------------------------------------------------------------

    /// <summary>§8.1: no parameters; an interval of 0 (no expectation) or at least 30 seconds.</summary>
    public static void Validate(SensorRequest request, ApiValidator v)
    {
        v.Check(string.IsNullOrWhiteSpace(request.Parameters), "parameters", "An Advanced sensor takes no parameters: its rules are on its Rules tab.");
        v.Check(request.Interval == 0 || request.Interval >= AdvancedLimits.MinIntervalSeconds, "interval",
            $"0 (no expectation) or at least {AdvancedLimits.MinIntervalSeconds} seconds between reports.");
    }

    /// <summary>
    /// A new sensor's row and its starter rule (§6.5), or the setting changed. Another interval or grace has the evaluator
    /// re-judge the newest report: the request is saved with the change.
    /// </summary>
    public async Task ApplyAsync(SensorEntity sensor, AdvancedSensorRequest? request, (int Interval, int Grace)? before, CancellationToken ct)
    {
        sensor.Parameters = null;
        var now = Clock.UtcNow;
        var row = await db.AdvancedSensors.SingleOrDefaultAsync(a => a.SensorId == sensor.Id, ct);
        if (row is null)
        {
            row = new AdvancedSensorEntity { SensorId = sensor.Id, Created = now };
            db.AdvancedSensors.Add(row);
            if (request?.AddStatusRule ?? true)
            {
                db.AdvancedRules.Add(new AdvancedRuleEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, Order = 0, Name = StatusRuleName, Path = "status", Operator = AdvancedOperator.Healthy,
                    WhenMissing = AdvancedWhenMissing.Pass, Created = now,
                    Note = "The app's own verdict: ok, pass, up or healthy hold; warn, warning or degraded warn; anything else fails. Apps without a status key aren't affected.",
                });
            }
        }
        row.NamesPeople = request?.NamesPeople ?? row.NamesPeople;
        if (before is { } b && (b.Interval != sensor.Interval || b.Grace != sensor.GracePeriod))
        {
            row.ReevaluateRequestedAt = now;
        }
    }

    /// <summary>After a sensor's save: the evaluator's pass of a requested re-evaluation, up to 5 seconds.</summary>
    public async Task AfterSaveAsync(Guid sensorId, CancellationToken ct)
    {
        if (await db.AdvancedSensors.AnyAsync(a => a.SensorId == sensorId && a.ReevaluateRequestedAt != null, ct))
        {
            await evaluator.RequestAsync(sensorId, wait: true, ct);
        }
    }

    /// <summary>The type changed away from Advanced (§7.4): the row, the rules, the fields, the reports and the samples go.</summary>
    public async Task RemoveAsync(Guid sensorId, CancellationToken ct)
    {
        await db.AdvancedSamples.Where(s => s.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.AdvancedSensors.Where(a => a.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.AdvancedReports.Where(r => r.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.AdvancedRules.Where(r => r.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.AdvancedFields.Where(f => f.SensorId == sensorId).ExecuteDeleteAsync(ct);
    }

    // --- reading ---------------------------------------------------------------------------------------------------------

    /// <summary><c>SensorDetail.Advanced</c> (§8.2); <paramref name="canSeeDetails"/> null: the caller's Monitoring: Details.</summary>
    public async Task<AdvancedSensorDetail?> DetailAsync(Guid sensorId, CancellationToken ct, bool? canSeeDetails = null)
    {
        var side = await db.AdvancedSensors.AsNoTracking().Include(a => a.Sensor).SingleOrDefaultAsync(a => a.SensorId == sensorId, ct);
        if (side is null)
        {
            return null;
        }
        var canSee = !side.NamesPeople || (canSeeDetails ?? CallerHasDetails);
        var rules = await db.AdvancedRules.AsNoTracking().Where(r => r.SensorId == sensorId).OrderBy(r => r.Order).ThenBy(r => r.Created).ToListAsync(ct);
        var fields = await db.AdvancedFields.AsNoTracking().Where(f => f.SensorId == sensorId).OrderBy(f => f.Order).ThenBy(f => f.Created).ToListAsync(ct);
        var latest = side.LastReportId is { } lastId
            ? await db.AdvancedReports.AsNoTracking().Where(r => r.Id == lastId).Select(Summary).SingleOrDefaultAsync(ct)
            : null;
        var judged = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId && r.Results != null)
            .OrderByDescending(r => r.LastReceivedAt).Select(r => r.Results).FirstOrDefaultAsync(ct);
        var pending = await db.AdvancedReports.CountAsync(r => r.SensorId == sensorId && r.EvaluatedAt == null, ct);
        var count = await db.AdvancedReports.CountAsync(r => r.SensorId == sensorId, ct);
        var states = AdvancedJson.Read<Dictionary<Guid, AdvancedRuleState>>(side.RuleStates) ?? [];
        var values = AdvancedJson.Read<AdvancedResults>(judged)?.Fields.ToDictionary(f => f.FieldId, f => f.Value) ?? [];

        var interval = TimeSpan.FromSeconds(Math.Max(0, side.Sensor.Interval));
        DateTime? staleAt = interval > TimeSpan.Zero && side.LastReportAt is { } last ? last + interval + TimeSpan.FromSeconds(Math.Max(0, side.Sensor.GracePeriod)) : null;
        var verdict = side.LastCode is { } code
            ? new AdvancedVerdictItem(code, side.LastMessage, states.Values.Count(s => s.Outcome == AdvancedOutcome.Fail),
                states.Values.Count(s => s.Outcome == AdvancedOutcome.Warn), side.LastEvaluatedAt)
            : null;
        var ruleItems = rules.Select(r => Item(r, states.GetValueOrDefault(r.Id), canSee)).ToList();
        var fieldItems = fields.Select(f =>
        {
            var normalized = AdvancedPath.TryParse(f.Path, out var p, out _) ? p.Text : f.Path;
            var outcomes = rules.Where(r => r.Enabled && (AdvancedPath.TryParse(r.Path, out var rp, out _) ? rp.Text : r.Path) == normalized)
                .Select(r => states.GetValueOrDefault(r.Id)?.Outcome).OfType<AdvancedOutcome>().ToList();
            AdvancedOutcome? worst = outcomes.Contains(AdvancedOutcome.Fail) ? AdvancedOutcome.Fail
                : outcomes.Contains(AdvancedOutcome.Warn) ? AdvancedOutcome.Warn
                : outcomes.Contains(AdvancedOutcome.Pass) ? AdvancedOutcome.Pass : null;
            return new AdvancedFieldItem(f.Id, f.Order, f.Name, f.Path, f.Format, f.Unit, f.Decimals, f.Pinned, f.Chart,
                canSee ? values.GetValueOrDefault(f.Id) : null, worst);
        }).ToList();
        if (latest?.MeasuredAt is { } measured)
        {
            latest = latest with { ClockSkewSeconds = (int)Math.Round((measured - latest.ReceivedAt).TotalSeconds) };
        }
        if (latest is not null && !canSee)
        {
            latest = latest with { Status = null, Message = null, Source = null };
        }
        return new AdvancedSensorDetail(side.NamesPeople, canSee, side.LastReportAt, side.LastValidReportAt, canSee ? side.LastSource : null,
            staleAt is { } s && Clock.UtcNow > s, staleAt, verdict, ruleItems, fieldItems, latest, pending, count);
    }

    private static readonly System.Linq.Expressions.Expression<Func<AdvancedReportEntity, AdvancedReportSummary>> Summary = r =>
        new AdvancedReportSummary(r.Id, r.ReceivedAt, r.LastReceivedAt, r.Repeats, r.Source, r.Bytes, r.Valid, r.ErrorKind, r.Error, r.Status, r.Message,
            r.MeasuredAt, null, r.EvaluatedAt != null, r.Code);

    private static AdvancedRuleItem Item(AdvancedRuleEntity r, AdvancedRuleState? state, bool canSee)
    {
        var spec = r.ToSpec();
        var last = state?.Result;
        if (last is not null && !canSee)
        {
            last = last with { Actual = [], Matches = [] };
        }
        var wildcard = AdvancedPath.TryParse(r.Path, out var path, out _) && path.HasWildcard;
        return new AdvancedRuleItem(r.Id, r.Order, r.Name, r.Enabled, r.Note, r.Path, r.Operator, r.Operand, r.Operand2, r.IgnoreCase, r.Quantifier,
            r.WhenMissing, r.Severity, AdvancedRules.Describe(spec), wildcard, last,
            state is { Outcome: AdvancedOutcome.Fail or AdvancedOutcome.Warn } ? state.Since : null);
    }

    /// <summary>The sensor, of type Advanced, for the audit; with <paramref name="data"/>, the caller may see its data (403 otherwise).</summary>
    private async Task<(Audit.AuditTarget Target, AdvancedSensorEntity Side)> LoadAsync(Guid sensorId, bool data, CancellationToken ct)
    {
        var s = await db.Sensors.AsNoTracking().Where(x => x.Id == sensorId && x.Type == SensorType.ADVANCED)
            .Select(x => new { x.Name, Host = x.Parent.Name, x.ParentId, x.Parent.LocationId }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Advanced sensor");
        var side = await db.AdvancedSensors.AsNoTracking().SingleOrDefaultAsync(a => a.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("Advanced sensor");
        if (data && side.NamesPeople && !CallerHasDetails)
        {
            throw ApiException.MissingRight(Area.Monitoring, Level.Details);
        }
        return (Audit.AuditTarget.Sensor(sensorId, $"{s.Host} / {s.Name}", s.ParentId, s.LocationId), side);
    }

    public async Task<AdvancedSensorDetail> RequireDetailAsync(Guid sensorId, CancellationToken ct)
    {
        await LoadAsync(sensorId, data: false, ct);
        return (await DetailAsync(sensorId, ct))!;
    }

    // --- reports ---------------------------------------------------------------------------------------------------------

    private static readonly System.Linq.Expressions.Expression<Func<AdvancedReportEntity, AdvancedReportItem>> ItemOf = r =>
        new AdvancedReportItem(r.Id, r.ReceivedAt, r.LastReceivedAt, r.Repeats, r.Source, r.Bytes, r.Valid, r.ErrorKind, r.Error, r.Status, r.Message,
            r.MeasuredAt, r.EvaluatedAt != null, r.Code, r.Failed, r.Warnings);

    public async Task<Paged<AdvancedReportItem>> ReportsAsync(Guid sensorId, AdvancedReportQuery query, CancellationToken ct)
    {
        await LoadAsync(sensorId, data: true, ct);
        var reports = db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId);
        if (Text.Pattern(query.Search) is { } pattern)
        {
            reports = reports.Where(r => EF.Functions.ILike(r.Message ?? "", pattern, "\\") || EF.Functions.ILike(r.Status ?? "", pattern, "\\"));
        }
        return await Paging.PageAsync(reports.OrderByDescending(r => r.LastReceivedAt).ThenByDescending(r => r.Id).Select(ItemOf), query, ct);
    }

    /// <summary>A report with its body and its outcomes then; <paramref name="reportId"/> null: the latest.</summary>
    public async Task<AdvancedReportDetail> ReportAsync(Guid sensorId, Guid? reportId, CancellationToken ct)
    {
        await LoadAsync(sensorId, data: true, ct);
        var row = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId && (reportId == null || r.Id == reportId))
            .OrderByDescending(r => r.LastReceivedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Report");
        var previous = await db.AdvancedReports.AsNoTracking()
            .Where(r => r.SensorId == sensorId && r.Id != row.Id && r.ReceivedAt <= row.ReceivedAt)
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        var results = AdvancedJson.Read<AdvancedResults>(row.Results);
        return new AdvancedReportDetail(new AdvancedReportItem(row.Id, row.ReceivedAt, row.LastReceivedAt, row.Repeats, row.Source, row.Bytes, row.Valid,
                row.ErrorKind, row.Error, row.Status, row.Message, row.MeasuredAt, row.EvaluatedAt != null, row.Code, row.Failed, row.Warnings),
            row.Body, results?.Rules ?? [], results?.Fields ?? [], results?.Error, previous);
    }

    /// <summary>A report that carried what it shouldn't (§13); the verdict stays until the next report.</summary>
    public async Task DeleteReportAsync(Guid sensorId, Guid reportId, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var row = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId && r.Id == reportId)
            .Select(r => new { r.ReceivedAt, r.Repeats }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Report");
        AuditLog.Summary($"{target.Name}: the report of {AdvancedValues.Absolute(row.ReceivedAt)}" + (row.Repeats > 0 ? $" (and {row.Repeats} repeats)" : "") + " deleted");
        await db.AdvancedReports.Where(r => r.Id == reportId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Every report and every sample of the sensor (§8.3).</summary>
    public async Task<int> ClearReportsAsync(Guid sensorId, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AdvancedEvaluator.LockAsync(db, sensorId, ct);
        var count = await db.AdvancedReports.Where(r => r.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.AdvancedSamples.Where(s => s.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        AuditLog.Summary($"{target.Name}: all {count} reports and their charted values deleted");
        return count;
    }

    /// <summary>The latest valid report's paths (§8.2), for the editor's suggestions.</summary>
    public async Task<IReadOnlyList<AdvancedPathItem>> PathsAsync(Guid sensorId, CancellationToken ct)
    {
        await LoadAsync(sensorId, data: true, ct);
        var body = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId && r.Valid)
            .OrderByDescending(r => r.LastReceivedAt).Select(r => r.Body).FirstOrDefaultAsync(ct);
        using var document = AdvancedReportReader.Parse(body);
        if (document is null)
        {
            return [];
        }
        return AdvancedPath.Enumerate(document.RootElement)
            .Select(p => new AdvancedPathItem(p.Path, p.Kind.ToString().ToLowerInvariant(), p.Preview)).ToList();
    }

    private sealed class SampleRow
    {
        public DateTime At { get; set; }

        public double Min { get; set; }

        public double Max { get; set; }

        public double Last { get; set; }

        public int Count { get; set; }
    }

    /// <summary>A field's chart (§8.3): per minute for 24 h, per hour for 7 and 30 days, with the thresholds of rules on its path.</summary>
    public async Task<AdvancedSamples> SamplesAsync(Guid sensorId, AdvancedSamplesQuery query, CancellationToken ct)
    {
        await LoadAsync(sensorId, data: true, ct);
        var field = await db.AdvancedFields.AsNoTracking().SingleOrDefaultAsync(f => f.SensorId == sensorId && f.Id == query.FieldId, ct)
            ?? throw ApiException.NotFound("Field");
        var range = query.Range?.Trim().ToLowerInvariant() ?? "24h";
        var span = range switch
        {
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => throw ApiException.Invalid("range", "24h, 7d or 30d."),
        };
        var to = Clock.UtcNow;
        var from = to - span;
        var fieldId = field.Id;
        List<SampleRow> rows = range == "24h"
            ? await db.Database.SqlQuery<SampleRow>($"""
                SELECT "Minute" AS "At", "Min", "Max", "Last", "Count" FROM advanced_samples
                WHERE "SensorId" = {sensorId} AND "FieldId" = {fieldId} AND "Minute" >= {from} ORDER BY "Minute"
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<SampleRow>($"""
                SELECT date_trunc('hour', "Minute", 'UTC') AS "At", min("Min") AS "Min", max("Max") AS "Max",
                       (array_agg("Last" ORDER BY "Minute" DESC))[1] AS "Last", sum("Count")::int AS "Count"
                FROM advanced_samples WHERE "SensorId" = {sensorId} AND "FieldId" = {fieldId} AND "Minute" >= {from}
                GROUP BY 1 ORDER BY 1
                """).ToListAsync(ct);
        var normalized = AdvancedPath.TryParse(field.Path, out var fp, out _) ? fp.Text : field.Path;
        var thresholds = new List<AdvancedThreshold>();
        foreach (var rule in await db.AdvancedRules.AsNoTracking().Where(r => r.SensorId == sensorId && r.Enabled).OrderBy(r => r.Order).ToListAsync(ct))
        {
            if (!AdvancedPath.TryParse(rule.Path, out var rp, out _) || rp.Text != normalized || !AdvancedRules.IsNumeric(rule.Operator))
            {
                continue;
            }
            if (AdvancedNumber.TryParse(rule.Operand, out var a))
            {
                thresholds.Add(new AdvancedThreshold(rule.Name, rule.Operator, a.Double, rule.Severity));
            }
            if (AdvancedRules.TakesOperand2(rule.Operator) && AdvancedNumber.TryParse(rule.Operand2, out var b))
            {
                thresholds.Add(new AdvancedThreshold(rule.Name, rule.Operator, b.Double, rule.Severity));
            }
        }
        return new AdvancedSamples(field.Id, range, from, to, range == "24h" ? 60 : 3600,
            rows.Select(r => new AdvancedSamplePoint(DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.Min, r.Max, r.Last, r.Count)).ToList(), thresholds);
    }

    // --- trying rules (§6.6) ---------------------------------------------------------------------------------------------

    public async Task<AdvancedTestResult> TestAsync(Guid sensorId, AdvancedTestRequest request, CancellationToken ct)
    {
        var (_, side) = await LoadAsync(sensorId, data: false, ct);
        var user = accessor.HttpContext?.User.Identity?.Name ?? "pnmtd";
        if (!tests.TryEnter($"{user}"))
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "too_many_tests",
                $"At most {AdvancedTestGate.PerMinute} tests a minute: try again in a moment.");
        }
        List<AdvancedRuleSpec> rules;
        if (request.Rules is { } drafts)
        {
            var v = new ApiValidator();
            v.Check(drafts.Count <= AdvancedLimits.MaxRules, "rules", $"At most {AdvancedLimits.MaxRules} rules.");
            for (var i = 0; i < drafts.Count; i++)
            {
                ValidateRule(drafts[i], v, $"rules[{i}].");
            }
            v.ThrowIfInvalid();
            rules = drafts.Select(d => Spec(d, d.Id ?? Guid.NewGuid())).ToList();
        }
        else
        {
            rules = (await db.AdvancedRules.AsNoTracking().Where(r => r.SensorId == sensorId).OrderBy(r => r.Order).ThenBy(r => r.Created).ToListAsync(ct))
                .Select(r => r.ToSpec()).ToList();
        }
        var fields = await db.AdvancedFields.AsNoTracking().Where(f => f.SensorId == sensorId).OrderBy(f => f.Order).ToListAsync(ct);
        var mayRead = !side.NamesPeople || CallerHasDetails;
        var now = Clock.UtcNow;
        AdvancedParsedReport? pasted = null;
        JsonDocument? stored = null;
        JsonDocument? earlier = null;
        try
        {
            AdvancedReportInput input;
            JsonElement? before = null;
            Guid? reportId = null;
            if (request.Report is { } text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                pasted = bytes.Length > options.CurrentValue.MaxBytes
                    ? AdvancedReportReader.TooLarge(bytes.Length, options.CurrentValue.MaxBytes)
                    : AdvancedReportReader.Read(bytes);
                input = pasted.Input;
                if (mayRead && rules.Any(r => r.Enabled && AdvancedRules.IsChange(r.Operator)))
                {
                    earlier = AdvancedReportReader.Parse(await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId && r.Valid)
                        .OrderByDescending(r => r.LastReceivedAt).Select(r => r.Body).FirstOrDefaultAsync(ct));
                    before = earlier?.RootElement;
                }
            }
            else
            {
                if (!mayRead)
                {
                    throw ApiException.MissingRight(Area.Monitoring, Level.Details);
                }
                var row = await db.AdvancedReports.AsNoTracking()
                    .Where(r => r.SensorId == sensorId && (request.ReportId == null || r.Id == request.ReportId))
                    .OrderByDescending(r => r.LastReceivedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
                if (row is null)
                {
                    if (request.ReportId is not null)
                    {
                        throw ApiException.NotFound("Report");
                    }
                    return new AdvancedTestResult(null, null, null, [], [], "No report yet: paste a sample to try the rules.", null);
                }
                reportId = row.Id;
                stored = row.Valid ? AdvancedReportReader.Parse(row.Body) : null;
                input = stored is not null ? AdvancedReportInput.Of(stored.RootElement) : new AdvancedReportInput(false, row.ErrorKind, row.Error, null);
                if (row.Repeats > 0 && stored is not null)
                {
                    before = stored.RootElement;
                }
                else if (rules.Any(r => r.Enabled && AdvancedRules.IsChange(r.Operator)))
                {
                    earlier = AdvancedReportReader.Parse(await db.AdvancedReports.AsNoTracking()
                        .Where(r => r.SensorId == sensorId && r.Valid && r.Id != row.Id && r.ReceivedAt <= row.ReceivedAt)
                        .OrderByDescending(r => r.ReceivedAt).Select(r => r.Body).FirstOrDefaultAsync(ct));
                    before = earlier?.RootElement;
                }
            }
            var verdict = AdvancedVerdict.Evaluate(rules, input, before, now, now, TimeSpan.Zero, TimeSpan.Zero);
            var root = pasted?.Document?.RootElement ?? stored?.RootElement;
            return new AdvancedTestResult(verdict?.Code, verdict?.State, verdict?.Message, verdict?.Results ?? [],
                root is { } r ? AdvancedEvaluator.FieldValues(fields, r) : [], input.Valid ? null : input.Error, reportId);
        }
        finally
        {
            pasted?.Dispose();
            stored?.Dispose();
            earlier?.Dispose();
        }
    }

    // --- rules -----------------------------------------------------------------------------------------------------------

    private static void ValidateRule(AdvancedRuleRequest r, ApiValidator v, string prefix = "")
    {
        var name = r.Name?.Trim();
        v.Check(name is { Length: >= 1 and <= AdvancedLimits.MaxRuleName }, prefix + "name", $"A name of 1 to {AdvancedLimits.MaxRuleName} characters.");
        if (!AdvancedPath.TryParse(r.Path, out _, out var pathError))
        {
            v.Add(prefix + "path", pathError);
        }
        v.Check(Enum.IsDefined(r.Operator), prefix + "operator", "An operator of the list (§6.3).");
        v.Check(Enum.IsDefined(r.Quantifier), prefix + "quantifier", "All, Any or None.");
        v.Check(Enum.IsDefined(r.WhenMissing), prefix + "whenMissing", "Fail, Warn or Pass.");
        v.Check(Enum.IsDefined(r.Severity), prefix + "severity", "Fail or Warn.");
        v.Check(r.Note is null || r.Note.Length <= AdvancedLimits.MaxNote, prefix + "note", $"At most {AdvancedLimits.MaxNote} characters.");
        if (Enum.IsDefined(r.Operator) && AdvancedRules.ValidateOperand(r.Operator, AdvancedRules.TakesOperand(r.Operator) ? r.Operand : null,
                AdvancedRules.TakesOperand2(r.Operator) ? r.Operand2 : null) is { } problem)
        {
            v.Add(prefix + problem.Field, problem.Message);
        }
    }

    private static AdvancedRuleSpec Spec(AdvancedRuleRequest r, Guid id) => new(id, r.Name?.Trim() ?? "", r.Enabled,
        AdvancedPath.Parse(r.Path!).Text, r.Operator, AdvancedRules.TakesOperand(r.Operator) ? r.Operand : null,
        AdvancedRules.TakesOperand2(r.Operator) ? r.Operand2?.Trim() : null, r.IgnoreCase && AdvancedRules.UsesCase(r.Operator), r.Quantifier,
        r.WhenMissing, r.Severity, Text.Trimmed(r.Note));

    private static void Fill(AdvancedRuleEntity e, AdvancedRuleSpec s)
    {
        e.Name = s.Name;
        e.Enabled = s.Enabled;
        e.Path = s.Path;
        e.Operator = s.Operator;
        e.Operand = s.Operand;
        e.Operand2 = s.Operand2;
        e.IgnoreCase = s.IgnoreCase;
        e.Quantifier = s.Quantifier;
        e.WhenMissing = s.WhenMissing;
        e.Severity = s.Severity;
        e.Note = s.Note;
    }

    /// <summary>A change of the rules or fields: under the sensor's lock, with the re-evaluation requested in the same transaction.</summary>
    private async Task<AdvancedSensorDetail> ChangeAsync(Guid sensorId, Func<AdvancedSensorEntity, Task> change, bool reevaluate, CancellationToken ct)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await AdvancedEvaluator.LockAsync(db, sensorId, ct);
            var side = await db.AdvancedSensors.SingleAsync(a => a.SensorId == sensorId, ct);
            await change(side);
            if (reevaluate)
            {
                side.ReevaluateRequestedAt = Clock.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        if (reevaluate)
        {
            await evaluator.RequestAsync(sensorId, wait: true, ct);
        }
        db.ChangeTracker.Clear();
        return (await DetailAsync(sensorId, ct))!;
    }

    public async Task<AdvancedSensorDetail> CreateRuleAsync(Guid sensorId, AdvancedRuleRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var v = new ApiValidator();
        ValidateRule(request, v);
        v.Check(await db.AdvancedRules.CountAsync(r => r.SensorId == sensorId, ct) < AdvancedLimits.MaxRules, "name",
            $"At most {AdvancedLimits.MaxRules} rules per sensor.");
        v.ThrowIfInvalid();
        var spec = Spec(request, Guid.NewGuid());
        AuditLog.Summary($"{target.Name}: rule \"{spec.Name}\" added: {AdvancedRules.Describe(spec)}");
        return await ChangeAsync(sensorId, async _ =>
        {
            var order = await db.AdvancedRules.Where(r => r.SensorId == sensorId).Select(r => (int?)r.Order).MaxAsync(ct) ?? -1;
            var entity = new AdvancedRuleEntity { Id = spec.Id, SensorId = sensorId, Order = order + 1, Created = Clock.UtcNow };
            Fill(entity, spec);
            db.AdvancedRules.Add(entity);
        }, reevaluate: true, ct);
    }

    public async Task<AdvancedSensorDetail> UpdateRuleAsync(Guid sensorId, Guid ruleId, AdvancedRuleRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var v = new ApiValidator();
        ValidateRule(request, v);
        v.ThrowIfInvalid();
        if (!await db.AdvancedRules.AnyAsync(r => r.SensorId == sensorId && r.Id == ruleId, ct))
        {
            throw ApiException.NotFound("Rule");
        }
        var spec = Spec(request, ruleId);
        AuditLog.Summary($"{target.Name}: rule \"{spec.Name}\" changed: {AdvancedRules.Describe(spec)}");
        return await ChangeAsync(sensorId, async _ =>
        {
            var entity = await db.AdvancedRules.SingleAsync(r => r.Id == ruleId, ct);
            Fill(entity, spec);
        }, reevaluate: true, ct);
    }

    public async Task<AdvancedSensorDetail> DeleteRuleAsync(Guid sensorId, Guid ruleId, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var name = await db.AdvancedRules.Where(r => r.SensorId == sensorId && r.Id == ruleId).Select(r => r.Name).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Rule");
        AuditLog.Summary($"{target.Name}: rule \"{name}\" deleted");
        return await ChangeAsync(sensorId, async _ =>
        {
            db.AdvancedRules.Remove(await db.AdvancedRules.SingleAsync(r => r.Id == ruleId, ct));
        }, reevaluate: true, ct);
    }

    /// <summary>The rules' new order; every rule once. Re-judged, because the message lists the rules in their order.</summary>
    public async Task<AdvancedSensorDetail> OrderRulesAsync(Guid sensorId, AdvancedOrderRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var ids = await db.AdvancedRules.Where(r => r.SensorId == sensorId).Select(r => r.Id).ToListAsync(ct);
        new ApiValidator().Check(request.Ids.Count == ids.Count && request.Ids.Distinct().Count() == ids.Count && request.Ids.All(ids.Contains), "ids",
            "Every rule of the sensor, once.").ThrowIfInvalid();
        AuditLog.Summary($"{target.Name}: rules reordered");
        return await ChangeAsync(sensorId, async _ =>
        {
            var rows = await db.AdvancedRules.Where(r => r.SensorId == sensorId).ToListAsync(ct);
            foreach (var row in rows)
            {
                var order = request.Ids.IndexOf(row.Id);
                if (row.Order != order)
                {
                    AuditLog.Change("Order", row.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        order.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Rule \"{row.Name}\"");
                    row.Order = order;
                }
            }
        }, reevaluate: true, ct);
    }

    /// <summary>Another Advanced sensor's rules and fields (§8.3), appended or replacing these: for an app deployed many times.</summary>
    public async Task<AdvancedSensorDetail> CopyAsync(Guid sensorId, AdvancedCopyRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        if (request.FromSensorId == sensorId)
        {
            throw ApiException.Invalid("fromSensorId", "Another Advanced sensor than this one.");
        }
        var from = await db.Sensors.AsNoTracking().Where(s => s.Id == request.FromSensorId && s.Type == SensorType.ADVANCED)
            .Select(s => s.Parent.Name + " / " + s.Name).SingleOrDefaultAsync(ct);
        if (from is null)
        {
            throw ApiException.Invalid("fromSensorId", "No such Advanced sensor.");
        }
        var rules = await db.AdvancedRules.AsNoTracking().Where(r => r.SensorId == request.FromSensorId).OrderBy(r => r.Order).ThenBy(r => r.Created).ToListAsync(ct);
        var fields = await db.AdvancedFields.AsNoTracking().Where(f => f.SensorId == request.FromSensorId).OrderBy(f => f.Order).ThenBy(f => f.Created).ToListAsync(ct);
        var keptRules = request.Replace ? 0 : await db.AdvancedRules.CountAsync(r => r.SensorId == sensorId, ct);
        var keptFields = request.Replace ? [] : await db.AdvancedFields.AsNoTracking().Where(f => f.SensorId == sensorId).Select(f => f.Chart).ToListAsync(ct);
        new ApiValidator()
            .Check(keptRules + rules.Count <= AdvancedLimits.MaxRules, "fromSensorId", $"That makes more than {AdvancedLimits.MaxRules} rules.")
            .Check(keptFields.Count + fields.Count <= AdvancedLimits.MaxFields, "fromSensorId", $"That makes more than {AdvancedLimits.MaxFields} fields.")
            .Check(keptFields.Count(c => c) + fields.Count(f => f.Chart) <= AdvancedLimits.MaxChartedFields, "fromSensorId",
                $"That makes more than {AdvancedLimits.MaxChartedFields} charted fields.")
            .ThrowIfInvalid();
        AuditLog.Summary($"{target.Name}: {rules.Count} rules and {fields.Count} fields copied from {from}" + (request.Replace ? ", replacing its own" : ""));
        return await ChangeAsync(sensorId, async _ =>
        {
            var now = Clock.UtcNow;
            if (request.Replace)
            {
                db.AdvancedRules.RemoveRange(await db.AdvancedRules.Where(r => r.SensorId == sensorId).ToListAsync(ct));
                db.AdvancedFields.RemoveRange(await db.AdvancedFields.Where(f => f.SensorId == sensorId).ToListAsync(ct));
            }
            var ruleOrder = request.Replace ? 0 : (await db.AdvancedRules.Where(r => r.SensorId == sensorId).Select(r => (int?)r.Order).MaxAsync(ct) ?? -1) + 1;
            var fieldOrder = request.Replace ? 0 : (await db.AdvancedFields.Where(f => f.SensorId == sensorId).Select(f => (int?)f.Order).MaxAsync(ct) ?? -1) + 1;
            foreach (var r in rules)
            {
                var copy = new AdvancedRuleEntity { Id = Guid.NewGuid(), SensorId = sensorId, Order = ruleOrder++, Created = now };
                Fill(copy, r.ToSpec());
                db.AdvancedRules.Add(copy);
            }
            foreach (var f in fields)
            {
                db.AdvancedFields.Add(new AdvancedFieldEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensorId, Order = fieldOrder++, Name = f.Name, Path = f.Path, Format = f.Format, Unit = f.Unit,
                    Decimals = f.Decimals, Pinned = f.Pinned, Chart = f.Chart, Created = now,
                });
            }
        }, reevaluate: true, ct);
    }

    // --- fields ----------------------------------------------------------------------------------------------------------

    private async Task ValidateFieldAsync(Guid sensorId, Guid? fieldId, AdvancedFieldRequest r, CancellationToken ct)
    {
        var v = new ApiValidator();
        var name = r.Name?.Trim();
        v.Check(name is { Length: >= 1 and <= AdvancedLimits.MaxFieldName }, "name", $"A label of 1 to {AdvancedLimits.MaxFieldName} characters.");
        if (!AdvancedPath.TryParse(r.Path, out var path, out var error))
        {
            v.Add("path", error);
        }
        else
        {
            v.Check(!path.HasWildcard, "path", "A field is one value: a path without *.");
        }
        v.Check(Enum.IsDefined(r.Format), "format", "Auto, Number, Percent, Bytes, Duration, DateTime, Text, Boolean or Status.");
        v.Check(r.Unit is null || r.Unit.Trim().Length <= AdvancedLimits.MaxUnit, "unit", $"At most {AdvancedLimits.MaxUnit} characters.");
        v.Check(r.Decimals is null or >= 0 and <= 6, "decimals", "0 to 6, or empty for as needed.");
        var others = await db.AdvancedFields.AsNoTracking().Where(f => f.SensorId == sensorId && f.Id != fieldId).Select(f => f.Chart).ToListAsync(ct);
        v.Check(fieldId is not null || others.Count < AdvancedLimits.MaxFields, "name", $"At most {AdvancedLimits.MaxFields} fields per sensor.");
        v.Check(!r.Chart || others.Count(c => c) < AdvancedLimits.MaxChartedFields, "chart", $"At most {AdvancedLimits.MaxChartedFields} charted fields per sensor.");
        v.ThrowIfInvalid();
    }

    private static void Fill(AdvancedFieldEntity e, AdvancedFieldRequest r)
    {
        e.Name = r.Name!.Trim();
        e.Path = AdvancedPath.Parse(r.Path!).Text;
        e.Format = r.Format;
        e.Unit = Text.Trimmed(r.Unit);
        e.Decimals = r.Decimals;
        e.Pinned = r.Pinned;
        e.Chart = r.Chart;
    }

    public async Task<AdvancedSensorDetail> CreateFieldAsync(Guid sensorId, AdvancedFieldRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        await ValidateFieldAsync(sensorId, null, request, ct);
        AuditLog.Summary($"{target.Name}: field \"{request.Name!.Trim()}\" added ({request.Path!.Trim()})");
        // Re-judged for the field's value in the newest report; the verdict can't change.
        return await ChangeAsync(sensorId, async _ =>
        {
            var order = await db.AdvancedFields.Where(f => f.SensorId == sensorId).Select(f => (int?)f.Order).MaxAsync(ct) ?? -1;
            var entity = new AdvancedFieldEntity { Id = Guid.NewGuid(), SensorId = sensorId, Order = order + 1, Created = Clock.UtcNow };
            Fill(entity, request);
            db.AdvancedFields.Add(entity);
        }, reevaluate: true, ct);
    }

    public async Task<AdvancedSensorDetail> UpdateFieldAsync(Guid sensorId, Guid fieldId, AdvancedFieldRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        if (!await db.AdvancedFields.AnyAsync(f => f.SensorId == sensorId && f.Id == fieldId, ct))
        {
            throw ApiException.NotFound("Field");
        }
        await ValidateFieldAsync(sensorId, fieldId, request, ct);
        AuditLog.Summary($"{target.Name}: field \"{request.Name!.Trim()}\" changed");
        return await ChangeAsync(sensorId, async _ =>
        {
            var entity = await db.AdvancedFields.SingleAsync(f => f.Id == fieldId, ct);
            var pathChanged = AdvancedPath.Parse(request.Path!).Text != entity.Path;
            Fill(entity, request);
            if (pathChanged || !entity.Chart)
            {
                // The samples were another value's.
                await db.AdvancedSamples.Where(s => s.FieldId == fieldId).ExecuteDeleteAsync(ct);
            }
        }, reevaluate: true, ct);
    }

    public async Task<AdvancedSensorDetail> DeleteFieldAsync(Guid sensorId, Guid fieldId, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var name = await db.AdvancedFields.Where(f => f.SensorId == sensorId && f.Id == fieldId).Select(f => f.Name).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Field");
        AuditLog.Summary($"{target.Name}: field \"{name}\" deleted");
        return await ChangeAsync(sensorId, async _ =>
        {
            db.AdvancedFields.Remove(await db.AdvancedFields.SingleAsync(f => f.Id == fieldId, ct));
        }, reevaluate: false, ct);
    }

    public async Task<AdvancedSensorDetail> OrderFieldsAsync(Guid sensorId, AdvancedOrderRequest request, CancellationToken ct)
    {
        var (target, _) = await LoadAsync(sensorId, data: false, ct);
        AuditLog.Target(target);
        var ids = await db.AdvancedFields.Where(f => f.SensorId == sensorId).Select(f => f.Id).ToListAsync(ct);
        new ApiValidator().Check(request.Ids.Count == ids.Count && request.Ids.Distinct().Count() == ids.Count && request.Ids.All(ids.Contains), "ids",
            "Every field of the sensor, once.").ThrowIfInvalid();
        AuditLog.Summary($"{target.Name}: fields reordered");
        return await ChangeAsync(sensorId, async _ =>
        {
            foreach (var row in await db.AdvancedFields.Where(f => f.SensorId == sensorId).ToListAsync(ct))
            {
                row.Order = request.Ids.IndexOf(row.Id);
            }
        }, reevaluate: false, ct);
    }
}
