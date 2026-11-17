using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PNMTD.Advanced;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// Advanced sensors for the demo (docs/advanced-sensor-plan.md §11): a nightly backup that is OK, an order queue in the IETF
/// health format that warns, a disk report that fails, a payroll export whose data names people, and a label printer that
/// stopped reporting. Each with a week of reports and charted values, judged by the same pure verdict the evaluator uses, and
/// an event per verdict change. The failing ones are ignored, so the demo's problems stay the six the dashboard counts; none
/// that is OK has an interval that runs out during a demo.
/// </summary>
internal static class DevSeedAdvanced
{
    private const string Source = "192.0.2.40";

    public static int Seed(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var count = 0;
        count += Backup(db, hosts, now);
        count += Queue(db, hosts, now);
        count += Disk(db, hosts, now);
        count += Payroll(db, hosts, now);
        count += Printer(db, hosts, now);
        db.SaveChanges();
        return count;
    }

    private sealed class Demo(PnmtdDbContext db, SensorEntity sensor, AdvancedSensorEntity side)
    {
        public SensorEntity Sensor { get; } = sensor;

        public List<AdvancedRuleEntity> Rules { get; } = [];

        public List<AdvancedFieldEntity> Fields { get; } = [];

        private readonly Dictionary<Guid, AdvancedRuleState> states = [];
        private AdvancedReportEntity? last;
        private string? lastBody;
        private DateTime lastEvent = DateTime.MinValue;

        public void Rule(string name, string path, AdvancedOperator op, string? operand = null, AdvancedQuantifier quantifier = AdvancedQuantifier.All,
            AdvancedWhenMissing whenMissing = AdvancedWhenMissing.Fail, AdvancedSeverity severity = AdvancedSeverity.Fail, string? note = null)
        {
            var rule = new AdvancedRuleEntity
            {
                Id = Guid.NewGuid(), SensorId = Sensor.Id, Order = Rules.Count, Name = name, Path = path, Operator = op, Operand = operand,
                Quantifier = quantifier, WhenMissing = whenMissing, Severity = severity, Note = note, Created = Sensor.Created,
            };
            Rules.Add(rule);
            db.AdvancedRules.Add(rule);
        }

        public AdvancedFieldEntity Field(string name, string path, AdvancedFieldFormat format, string? unit = null, bool pinned = false, bool chart = false,
            int? decimals = null)
        {
            var field = new AdvancedFieldEntity
            {
                Id = Guid.NewGuid(), SensorId = Sensor.Id, Order = Fields.Count, Name = name, Path = path, Format = format, Unit = unit, Pinned = pinned,
                Chart = chart, Decimals = decimals, Created = Sensor.Created,
            };
            Fields.Add(field);
            db.AdvancedFields.Add(field);
            return field;
        }

        /// <summary>A report received at <paramref name="at"/>, judged as the evaluator would, with an event when the verdict changes.</summary>
        public void Report(DateTime at, object payload, bool keep = true)
        {
            var body = JsonSerializer.Serialize(payload);
            using var parsed = AdvancedReportReader.Read(Encoding.UTF8.GetBytes(body));
            using var previous = AdvancedReportReader.Parse(lastBody);
            var specs = Rules.Select(r => r.ToSpec()).ToList();
            var verdict = AdvancedVerdict.Evaluate(specs, parsed.Input, previous?.RootElement, at, at, TimeSpan.FromSeconds(Sensor.Interval),
                TimeSpan.FromSeconds(Sensor.GracePeriod), withValues: !side.NamesPeople)!;
            var values = parsed.Document is null ? [] : AdvancedEvaluator.FieldValues(Fields, parsed.Document.RootElement);
            if (keep)
            {
                if (last is not null && last.Sha256 == parsed.Sha256)
                {
                    last.Repeats++;
                    last.LastReceivedAt = at;
                }
                else
                {
                    last = new AdvancedReportEntity
                    {
                        Id = Guid.NewGuid(), SensorId = Sensor.Id, ReceivedAt = at, LastReceivedAt = at, Source = Source, Bytes = parsed.Bytes,
                        Sha256 = parsed.Sha256, Body = parsed.Text, Valid = parsed.Valid, ErrorKind = parsed.Kind, Error = parsed.Error,
                        Status = parsed.Status, Message = parsed.Message, MeasuredAt = parsed.MeasuredAt,
                    };
                    db.AdvancedReports.Add(last);
                }
                last.EvaluatedAt = at.AddMilliseconds(400);
                last.Code = verdict.Code;
                last.Failed = verdict.Failed;
                last.Warnings = verdict.Warnings;
                last.Results = AdvancedJson.Write(new AdvancedResults(verdict.Results, values));
                side.LastReportId = last.Id;
            }
            side.LastReportAt = at;
            side.LastValidReportAt = at;
            side.LastSource = Source;
            side.LastEvaluatedAt = at.AddMilliseconds(400);
            foreach (var result in verdict.Results)
            {
                states[result.RuleId] = states.TryGetValue(result.RuleId, out var s) && s.Outcome == result.Outcome
                    ? s with { Result = result }
                    : new AdvancedRuleState(result.Outcome, at, result);
            }
            side.RuleStates = AdvancedJson.Write(states);
            foreach (var field in Fields.Where(f => f.Chart))
            {
                if (values.FirstOrDefault(v => v.FieldId == field.Id)?.Value is { } v && AdvancedValues.TryNumber(v, out var n))
                {
                    Sample(field, at, n.Double);
                }
            }
            if (verdict.Key != side.LastVerdictKey)
            {
                var created = at.AddMilliseconds(400);
                lastEvent = created > lastEvent ? created : lastEvent.AddTicks(10);
                db.Events.Add(new EventEntity
                {
                    Id = Guid.NewGuid(), SensorId = Sensor.Id, Code = verdict.Code, Message = verdict.Message, Source = Source, Created = lastEvent,
                });
                side.LastVerdictKey = verdict.Key;
                side.LastCode = verdict.Code;
                side.LastMessage = verdict.Message;
            }
            lastBody = body;
        }

        private readonly Dictionary<(Guid, DateTime), AdvancedSampleEntity> samples = [];

        public void Sample(AdvancedFieldEntity field, DateTime at, double value)
        {
            var minute = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            if (samples.TryGetValue((field.Id, minute), out var s))
            {
                s.Min = Math.Min(s.Min, value);
                s.Max = Math.Max(s.Max, value);
                s.Last = value;
                s.Count++;
                return;
            }
            s = new AdvancedSampleEntity { SensorId = Sensor.Id, FieldId = field.Id, Minute = minute, Min = value, Max = value, Last = value, Count = 1 };
            samples[(field.Id, minute)] = s;
            db.AdvancedSamples.Add(s);
        }

        /// <summary>The verdict that a timer's pass would write now: a missing report.</summary>
        public void Missing(DateTime at)
        {
            var interval = TimeSpan.FromSeconds(Sensor.Interval);
            var grace = TimeSpan.FromSeconds(Sensor.GracePeriod);
            var stale = AdvancedVerdict.Evaluate([], new AdvancedReportInput(true, null, null, null), null, at, side.LastReportAt, interval, grace)!;
            db.Events.Add(new EventEntity
            {
                Id = Guid.NewGuid(), SensorId = Sensor.Id, Code = stale.Code, Message = stale.Message, Source = AdvancedEvaluator.TimerSource,
                Created = at > lastEvent ? at : lastEvent.AddTicks(10),
            });
            side.LastVerdictKey = stale.Key;
            side.LastCode = stale.Code;
            side.LastMessage = stale.Message;
        }
    }

    private static Demo? Create(PnmtdDbContext db, Dictionary<string, Guid> hosts, string host, string name, int interval, int grace, DateTime now,
        bool namesPeople = false, bool statusRule = true, string? notes = null)
    {
        if (!hosts.TryGetValue(host, out var hostId))
        {
            return null;
        }
        var sensor = new SensorEntity
        {
            Id = Guid.NewGuid(), ParentId = hostId, Type = SensorType.ADVANCED, Name = name, Enabled = true, Interval = interval, GracePeriod = grace,
            Created = now.AddDays(-8), Notes = notes,
        };
        sensor.SetNewSecretToken();
        db.Sensors.Add(sensor);
        var side = new AdvancedSensorEntity { SensorId = sensor.Id, NamesPeople = namesPeople, Created = sensor.Created };
        db.AdvancedSensors.Add(side);
        var demo = new Demo(db, sensor, side);
        if (statusRule)
        {
            demo.Rule(AdvancedSensorService.StatusRuleName, "status", AdvancedOperator.Healthy, whenMissing: AdvancedWhenMissing.Pass);
        }
        return demo;
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Daily, the last two hours ago, with an age rule, a warning rule and charted size and duration: OK.</summary>
    private static int Backup(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var demo = Create(db, hosts, "backup-srv", "Nightly backup", 86_400, 3_600, now);
        if (demo is null)
        {
            return 0;
        }
        demo.Rule("Last backup", "backup.finished", AdvancedOperator.NewerThan, "26h", note: "The job runs at 02:00; a day without a backup is a problem.");
        demo.Rule("No failed job", "jobs[*].state", AdvancedOperator.Eq, "failed", AdvancedQuantifier.None);
        demo.Rule("No job errors", "jobs[*].errors", AdvancedOperator.Le, "0", severity: AdvancedSeverity.Warn);
        demo.Rule("Repository space", "repository.free_gb", AdvancedOperator.Ge, "500");
        demo.Field("Size", "backup.size_bytes", AdvancedFieldFormat.Bytes, pinned: true, chart: true);
        demo.Field("Duration", "backup.duration_s", AdvancedFieldFormat.Duration, pinned: true, chart: true);
        demo.Field("Finished", "backup.finished", AdvancedFieldFormat.DateTime, pinned: true);
        demo.Field("Repository free", "repository.free_gb", AdvancedFieldFormat.Number, "GB", chart: true, decimals: 1);
        // Two hours ago, whatever the time of day: the demo stays OK for a day, as a stack that runs at night would otherwise
        // see the backup go missing within hours (and the dashboard's count of problems change).
        var latest = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddHours(-2).AddSeconds(-41);
        for (var day = 7; day >= 0; day--)
        {
            var finished = latest.AddDays(-day);
            var started = finished.AddSeconds(-(3_600 + day * 37));
            demo.Report(finished.AddSeconds(4), new
            {
                status = "ok",
                message = "Nightly backup done",
                time = Iso(finished),
                backup = new { started = Iso(started), finished = Iso(finished), size_bytes = 48_213_307_392L - day * 412_000_000L, duration_s = 3_600 + day * 37, files = 1_284_311 - day * 913 },
                repository = new { path = "/srv/veeam", free_gb = 1_412.5 + day * 18.2, total_gb = 16_000 },
                jobs = new object[]
                {
                    new { name = "databases", state = "done", errors = 0, duration_s = 1_204 },
                    new { name = "file shares", state = "done", errors = 0, duration_s = 2_210 + day * 30 },
                    new { name = "mailboxes", state = "done", errors = day == 3 ? 2 : 0, duration_s = 186 },
                },
            });
        }
        return 1;
    }

    /// <summary>The IETF health format every 5 minutes, a queue over its warning threshold: 296.</summary>
    private static int Queue(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var demo = Create(db, hosts, "app-01", "Order service", 0, 0, now,
            notes: "Forwards its /health (application/health+json) every 5 minutes from cron.");
        if (demo is null)
        {
            return 0;
        }
        demo.Rule("Queue length", "checks[\"queue:length\"][0].observedValue", AdvancedOperator.Le, "500", severity: AdvancedSeverity.Warn,
            note: "Above 500 orders wait more than a minute.");
        demo.Rule("Database answers in time", "checks[\"postgres:responseTime\"][0].observedValue", AdvancedOperator.Lt, "250");
        demo.Rule("Every check passes", "checks.*[*].status", AdvancedOperator.Healthy, severity: AdvancedSeverity.Warn);
        var queue = demo.Field("Queue length", "checks[\"queue:length\"][0].observedValue", AdvancedFieldFormat.Number, "orders", pinned: true, chart: true);
        var latency = demo.Field("Database", "checks[\"postgres:responseTime\"][0].observedValue", AdvancedFieldFormat.Number, "ms", pinned: true, chart: true,
            decimals: 1);
        demo.Field("Version", "version", AdvancedFieldFormat.Text, pinned: true);
        var random = new Random(7);
        var start = now.AddDays(-7);
        var steps = (int)((now - start).TotalMinutes / 5);
        for (var i = 0; i <= steps; i++)
        {
            var at = start.AddMinutes(i * 5).AddSeconds(3);
            var hour = Clock.ToLocal(at).Hour;
            var busy = hour is >= 8 and <= 18 ? 1.0 : 0.25;
            // The last two hours: a backlog builds up.
            var backlog = now - at < TimeSpan.FromHours(2) ? 520 + (2 - (now - at).TotalHours) * 160 : 0;
            var length = Math.Round(busy * (60 + random.Next(0, 120)) + backlog);
            var ms = Math.Round(8 + busy * random.NextDouble() * 30, 1);
            var keep = now - at < TimeSpan.FromHours(10);
            var status = length > 500 ? "warn" : "pass";
            if (!keep)
            {
                demo.Sample(queue, at, length);
                demo.Sample(latency, at, ms);
                continue;
            }
            demo.Report(at, new
            {
                status,
                version = "3.4.1",
                releaseId = "3.4.1-b812",
                serviceId = "orders",
                description = "Order service (shop backend)",
                checks = new Dictionary<string, object[]>
                {
                    ["queue:length"] = [new { componentType = "component", observedValue = length, observedUnit = "orders", status, time = Iso(at) }],
                    ["postgres:responseTime"] = [new { componentId = "orders-db", componentType = "datastore", observedValue = ms, observedUnit = "ms", status = "pass", time = Iso(at) }],
                    ["payments:reachable"] = [new { componentType = "component", observedValue = true, status = "pass", time = Iso(at) }],
                },
            });
        }
        return 1;
    }

    /// <summary>A disk report below its threshold, and a volume nearly full: 495, ignored.</summary>
    private static int Disk(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var demo = Create(db, hosts, "nas-02", "Disk report", 0, 0, now,
            notes: "Seeded as ignored, so that the demo's problems stay the six the dashboard counts (docs/advanced-sensor-plan.md §11).");
        if (demo is null)
        {
            return 0;
        }
        demo.Sensor.Ignore = true;
        demo.Rule("Disk space", "disk.free_gb", AdvancedOperator.Ge, "5");
        demo.Rule("No volume above 95 %", "volumes[*].used_pct", AdvancedOperator.Gt, "95", AdvancedQuantifier.None);
        var free = demo.Field("Free", "disk.free_gb", AdvancedFieldFormat.Number, "GB", pinned: true, chart: true, decimals: 1);
        for (var hour = 7 * 24; hour >= 0; hour--)
        {
            var at = now.AddHours(-hour).AddMinutes(-3);
            var gb = Math.Round(9.5 - (7 * 24 - hour) * 0.038, 1);
            if (hour > 12)
            {
                demo.Sample(free, at, gb);
                continue;
            }
            demo.Report(at, new
            {
                status = "ok",
                host = "nas-02",
                disk = new { mount = "/", free_gb = gb, total_gb = 2_000 },
                volumes = new object[]
                {
                    new { name = "/", used_pct = Math.Round(100 - gb / 20, 1) },
                    new { name = "/data", used_pct = 99.1 },
                    new { name = "/backup", used_pct = 61.4 },
                },
            });
        }
        return 1;
    }

    /// <summary>A payroll export whose data names people: OK, and only for Monitoring: Details.</summary>
    private static int Payroll(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var demo = Create(db, hosts, "app-03", "Payroll export", 0, 0, now, namesPeople: true);
        if (demo is null)
        {
            return 0;
        }
        demo.Rule("Payslips exported", "exported", AdvancedOperator.CountGe, "1");
        demo.Rule("No failed payslip", "employees[*].state", AdvancedOperator.Eq, "failed", AdvancedQuantifier.None);
        demo.Field("Exported", "count", AdvancedFieldFormat.Number, pinned: true);
        var at = now.AddDays(-2).AddHours(-3);
        demo.Report(at, new
        {
            status = "ok",
            message = "Exported 4 payslips for November, run by jane.doe",
            run_by = "jane.doe",
            count = 4,
            exported = new[] { "E-1001", "E-1002", "E-1003", "E-1004" },
            employees = new object[]
            {
                new { id = "E-1001", name = "Jane Doe", state = "exported" },
                new { id = "E-1002", name = "Max Muster", state = "exported" },
                new { id = "E-1003", name = "Erika Mustermann", state = "exported" },
                new { id = "E-1004", name = "Luca Rossi", state = "exported" },
            },
        });
        return 1;
    }

    /// <summary>A label printer that reported every 15 minutes, and stopped two hours ago: 460, ignored.</summary>
    private static int Printer(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var demo = Create(db, hosts, "app-05", "Label printer", 900, 300, now,
            notes: "Seeded as ignored, so that the demo's problems stay the six the dashboard counts (docs/advanced-sensor-plan.md §11).");
        if (demo is null)
        {
            return 0;
        }
        demo.Sensor.Ignore = true;
        demo.Rule("Counter moves on", "labels_printed", AdvancedOperator.NotDecreased);
        demo.Rule("Ribbon left", "ribbon_pct", AdvancedOperator.Ge, "10", severity: AdvancedSeverity.Warn);
        demo.Field("Ribbon", "ribbon_pct", AdvancedFieldFormat.Percent, pinned: true, chart: true);
        demo.Field("Labels", "labels_printed", AdvancedFieldFormat.Number, pinned: true);
        var stop = now.AddHours(-2);
        var labels = 18_234;
        for (var at = now.AddDays(-3); at <= stop; at = at.AddMinutes(15))
        {
            labels += 7;
            var pct = Math.Round(64 - (at - now.AddDays(-3)).TotalHours * 0.4, 1);
            // Judged and charted throughout; the history keeps the last 12 hours.
            demo.Report(at, new { status = "ok", labels_printed = labels, ribbon_pct = pct, paper = "loaded", model = "ZD421" },
                keep: stop - at < TimeSpan.FromHours(12));
        }
        demo.Missing(stop.AddMinutes(20).AddSeconds(30));
        return 1;
    }
}
