using Microsoft.EntityFrameworkCore;
using PNMTD.Data;

namespace PNMTD.Services.Domain;

/// <summary>
/// The monitoring state, computed in PostgreSQL (docs/webui-migration-plan.md §3.4, issue 14):
/// v0.12 loaded every event of every sensor into memory for each dashboard call.
/// <list type="bullet">
/// <item>An event is a success when its code is 399 or below.</item>
/// <item>Timespan sensors (ONE/ALL_WITHIN_TIMESPAN) count only their evaluations (280/281/480/481),
///   not the reports they evaluate, as the sensor API's /read does.</item>
/// <item>A sensor's state holds from one event to the next; the last one holds until now. Before the
///   first event there is no data.</item>
/// </list>
/// All queries use the (SensorId, Created) index; none reads an event twice.
/// </summary>
public sealed class MonitoringQueries(PnmtdDbContext db)
{
    public sealed class LatestRow
    {
        public Guid SensorId { get; set; }
        public DateTime? LastAt { get; set; }
        public int? LastCode { get; set; }
        public string? LastMessage { get; set; }
        /// <summary>The first failing event after the last success, while the sensor fails.</summary>
        public DateTime? FailingSince { get; set; }
    }

    public sealed class BucketRow
    {
        public Guid SensorId { get; set; }
        public int Bucket { get; set; }
        public double UpSeconds { get; set; }
        public double KnownSeconds { get; set; }
    }

    public sealed class StateChangeRow
    {
        public Guid SensorId { get; set; }
        public DateTime At { get; set; }
        public int Code { get; set; }
        public int PreviousCode { get; set; }
        public string? Message { get; set; }
        public DateTime? FailingSince { get; set; }
    }

    /// <summary>The newest relevant event of each sensor, and since when it fails.</summary>
    public async Task<Dictionary<Guid, LatestRow>> LatestAsync(IReadOnlyCollection<Guid> sensorIds, CancellationToken ct)
    {
        if (sensorIds.Count == 0)
        {
            return [];
        }
        var ids = sensorIds.ToArray();
        var rows = await db.Database.SqlQuery<LatestRow>($"""
            SELECT s."Id" AS "SensorId", le."Created" AS "LastAt", le."Code" AS "LastCode",
                   le."Message" AS "LastMessage", ff."Created" AS "FailingSince"
            FROM sensors s
            LEFT JOIN LATERAL (
                SELECT e."Created", e."Code", e."Message" FROM events e
                WHERE e."SensorId" = s."Id" AND (s."Type" NOT IN (60, 61) OR e."Code" IN (280, 281, 480, 481))
                ORDER BY e."Created" DESC LIMIT 1) le ON TRUE
            LEFT JOIN LATERAL (
                SELECT e."Created" FROM events e
                WHERE le."Code" > 399 AND e."SensorId" = s."Id" AND e."Code" <= 399
                  AND (s."Type" NOT IN (60, 61) OR e."Code" IN (280, 281))
                ORDER BY e."Created" DESC LIMIT 1) lastok ON TRUE
            LEFT JOIN LATERAL (
                SELECT e."Created" FROM events e
                WHERE le."Code" > 399 AND e."SensorId" = s."Id" AND e."Code" > 399
                  AND (s."Type" NOT IN (60, 61) OR e."Code" IN (480, 481))
                  AND (lastok."Created" IS NULL OR e."Created" > lastok."Created")
                ORDER BY e."Created" LIMIT 1) ff ON TRUE
            WHERE s."Id" = ANY({ids})
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.SensorId);
    }

    /// <summary>
    /// Seconds up and seconds with data, per sensor and bucket. Bucket k (1-based) runs from
    /// <paramref name="starts"/>[k-1] to the next start, the last one to <paramref name="end"/>.
    /// Buckets without data for a sensor are missing from the result.
    /// </summary>
    public async Task<List<BucketRow>> UptimeAsync(IReadOnlyCollection<Guid> sensorIds, IReadOnlyList<DateTime> starts, DateTime end,
        CancellationToken ct)
    {
        if (sensorIds.Count == 0 || starts.Count == 0)
        {
            return [];
        }
        var ids = sensorIds.ToArray();
        var bounds = starts.Append(end).ToArray();
        var from = starts[0];
        // width_bucket finds the bucket of an interval's start and end, so each interval meets only
        // the buckets it overlaps instead of being compared with all of them. About 0.9 s for 600,000
        // events (300 sensors at the 2000-event cap, 90 days); DashboardCache keeps that off requests.
        return await db.Database.SqlQuery<BucketRow>($"""
            WITH sensor AS (
                SELECT s."Id", s."Type" IN (60, 61) AS timespan FROM sensors s WHERE s."Id" = ANY({ids})
            ), ev AS (
                SELECT e."SensorId" AS sid, e."Created" AS t, e."Code" <= 399 AS ok
                FROM events e JOIN sensor s ON s."Id" = e."SensorId"
                WHERE e."Created" >= {from} AND e."Created" < {end}
                  AND (NOT s.timespan OR e."Code" IN (280, 281, 480, 481))
                UNION ALL
                SELECT s."Id", p."Created", p."Code" <= 399
                FROM sensor s CROSS JOIN LATERAL (
                    SELECT e."Created", e."Code" FROM events e
                    WHERE e."SensorId" = s."Id" AND e."Created" < {from}
                      AND (NOT s.timespan OR e."Code" IN (280, 281, 480, 481))
                    ORDER BY e."Created" DESC LIMIT 1) p
            ), iv AS (
                SELECT sid, ok, GREATEST(t, {from}) AS s,
                       LEAST(COALESCE(LEAD(t) OVER (PARTITION BY sid ORDER BY t), {end}), {end}) AS e
                FROM ev
            ), b AS (
                SELECT {bounds}::timestamptz[] AS bounds
            ), kv AS (
                SELECT iv.sid, iv.ok, iv.s, iv.e,
                       width_bucket(iv.s, b.bounds) AS k1, width_bucket(iv.e - interval '1 microsecond', b.bounds) AS k2
                FROM iv CROSS JOIN b
                WHERE iv.e > iv.s
            ), parts AS (
                -- Almost every interval lies within one bucket; only the others are split.
                SELECT sid, ok, k1 AS k, EXTRACT(EPOCH FROM e - s)::float8 AS seconds FROM kv WHERE k1 = k2
                UNION ALL
                SELECT kv.sid, kv.ok, k,
                       EXTRACT(EPOCH FROM LEAST(kv.e, b.bounds[k + 1]) - GREATEST(kv.s, b.bounds[k]))::float8
                FROM kv CROSS JOIN b CROSS JOIN LATERAL generate_series(kv.k1, kv.k2) AS k
                WHERE kv.k1 <> kv.k2
            )
            SELECT sid AS "SensorId", k AS "Bucket",
                   COALESCE(SUM(seconds) FILTER (WHERE ok), 0)::float8 AS "UpSeconds",
                   SUM(seconds)::float8 AS "KnownSeconds"
            FROM parts
            WHERE seconds > 0
            GROUP BY sid, k
            """).ToListAsync(ct);
    }

    /// <summary>
    /// Transitions between success and failure in [from, to], newest first. The event before
    /// <paramref name="from"/> decides whether the first event in the window is one. For a recovery,
    /// FailingSince is when that failure began (looked up before the window when it began earlier).
    /// </summary>
    public async Task<List<StateChangeRow>> StateChangesAsync(IReadOnlyCollection<Guid> sensorIds, DateTime from, DateTime to, int limit,
        CancellationToken ct)
    {
        if (sensorIds.Count == 0)
        {
            return [];
        }
        var ids = sensorIds.ToArray();
        return await db.Database.SqlQuery<StateChangeRow>($"""
            WITH sensor AS (
                SELECT s."Id", s."Type" IN (60, 61) AS timespan FROM sensors s WHERE s."Id" = ANY({ids})
            ), ev AS (
                SELECT e."SensorId" AS sid, e."Created" AS t, e."Code" AS code, e."Message" AS msg
                FROM events e JOIN sensor s ON s."Id" = e."SensorId"
                WHERE e."Created" >= {from} AND e."Created" <= {to}
                  AND (NOT s.timespan OR e."Code" IN (280, 281, 480, 481))
                UNION ALL
                SELECT s."Id", p."Created", p."Code", p."Message"
                FROM sensor s CROSS JOIN LATERAL (
                    SELECT e."Created", e."Code", e."Message" FROM events e
                    WHERE e."SensorId" = s."Id" AND e."Created" < {from}
                      AND (NOT s.timespan OR e."Code" IN (280, 281, 480, 481))
                    ORDER BY e."Created" DESC LIMIT 1) p
            ), x AS (
                SELECT sid, t, code, msg, code <= 399 AS ok,
                       LAG(code) OVER (PARTITION BY sid ORDER BY t) AS prev_code
                FROM ev
            ), tr AS (
                SELECT sid, t, code, msg, ok, prev_code,
                       LAG(t) OVER (PARTITION BY sid ORDER BY t) AS prev_t
                FROM x
                WHERE prev_code IS NOT NULL AND (prev_code <= 399) <> ok AND t >= {from}
            )
            SELECT tr.sid AS "SensorId", tr.t AS "At", tr.code AS "Code", tr.prev_code AS "PreviousCode",
                   tr.msg AS "Message",
                   CASE WHEN tr.ok THEN COALESCE(tr.prev_t, fs.since) END AS "FailingSince"
            FROM tr
            LEFT JOIN LATERAL (
                SELECT min(e."Created") AS since
                FROM events e JOIN sensor s ON s."Id" = e."SensorId"
                WHERE tr.ok AND tr.prev_t IS NULL
                  AND e."SensorId" = tr.sid AND e."Created" < tr.t AND e."Code" > 399
                  AND (NOT s.timespan OR e."Code" IN (480, 481))
                  AND e."Created" > COALESCE((
                      SELECT max(e2."Created") FROM events e2
                      WHERE e2."SensorId" = tr.sid AND e2."Created" < tr.t AND e2."Code" <= 399
                        AND (NOT s.timespan OR e2."Code" IN (280, 281))), '-infinity'::timestamptz)
            ) fs ON TRUE
            ORDER BY tr.t DESC
            LIMIT {limit}
            """).ToListAsync(ct);
    }
}
