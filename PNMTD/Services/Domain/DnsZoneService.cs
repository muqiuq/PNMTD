using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.DnsZones;

namespace PNMTD.Services.Domain;

/// <summary>DNS zones. DnsCheckTask parses the zone file into records and checks them; the API only edits the zone.</summary>
public sealed class DnsZoneService(PnmtdDbContext db)
{
    private static readonly string[] SortFields = ["name", "lastchecked"];

    /// <summary>Sort: name (default), lastChecked.</summary>
    public async Task<Paged<DnsZoneItem>> ListAsync(PageQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var zones = db.DnsZones.AsNoTracking();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            zones = zones.Where(z => EF.Functions.ILike(z.ZoneName, pattern, @"\"));
        }
        zones = (field, descending) switch
        {
            ("lastchecked", false) => zones.OrderBy(z => z.LastChecked).ThenBy(z => z.ZoneName),
            ("lastchecked", true) => zones.OrderByDescending(z => z.LastChecked).ThenBy(z => z.ZoneName),
            (_, true) => zones.OrderByDescending(z => z.ZoneName),
            _ => zones.OrderBy(z => z.ZoneName),
        };
        var page = await Paging.PageAsync(zones.Select(z => new
        {
            z.Id, z.ZoneName, z.HostId, HostName = z.Host != null ? z.Host.Name : null, z.Enabled, z.RecordsMatch,
            z.RequiresProcessing, z.ForceUpdate, z.Interval, z.LastChecked, z.NextCheck,
            Records = z.DnsZoneEntries.Count,
            Mismatches = z.DnsZoneEntries.Count(e => !e.Ignore && !e.IsMatch),
            Ignored = z.DnsZoneEntries.Count(e => e.Ignore),
        }), query, ct);
        return new Paged<DnsZoneItem>(page.Items.Select(z => new DnsZoneItem(z.Id, z.ZoneName,
            z.HostId is { } hostId ? new Ref(hostId, z.HostName!) : null, z.Enabled, z.RecordsMatch, z.RequiresProcessing, z.ForceUpdate,
            z.Interval, Instants.OrNull(z.LastChecked), Instants.OrNull(z.NextCheck), z.Records, z.Mismatches, z.Ignored)).ToList(),
            page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<DnsZoneDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var zone = await db.DnsZones.AsNoTracking()
            .Include(z => z.Host)
            .Include(z => z.DnsZoneEntries)
            .AsSplitQuery()
            .SingleOrDefaultAsync(z => z.Id == id, ct)
            ?? throw ApiException.NotFound("DNS zone");
        return new DnsZoneDetail(zone.Id, zone.ZoneName, zone.Host is { } host ? new Ref(host.Id, host.Name) : null, zone.Enabled,
            zone.RecordsMatch, zone.RequiresProcessing, zone.ForceUpdate, zone.Interval, Instants.OrNull(zone.LastChecked),
            Instants.OrNull(zone.NextCheck), zone.ZoneFileContent,
            zone.DnsZoneEntries
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.RecordType).ThenBy(e => e.ReferenceValue, StringComparer.Ordinal)
                .Select(ToRecord)
                .ToList());
    }

    private static DnsRecord ToRecord(DnsZoneEntryEntity e) => new(e.Id, e.Name, e.RecordType, e.TTL, e.ReferenceValue, e.ActualValue,
        e.IsMatch, e.Ignore, e.NumOfFailuresInARow, Instants.OrNull(e.Updated));

    /// <summary>The zone name comes from the file's SOA record; DnsCheckTask reads the records within seconds.</summary>
    public async Task<DnsZoneDetail> CreateAsync(DnsZoneRequest request, CancellationToken ct)
    {
        var zoneName = await ValidateAsync(request, ct);
        var zone = new DnsZoneEntity
        {
            Id = Guid.NewGuid(),
            ZoneName = zoneName,
            LastChecked = Clock.Never,
            NextCheck = Clock.Never,
        };
        Apply(zone, request);
        zone.RequiresProcessing = true;
        db.DnsZones.Add(zone);
        await db.SaveChangesAsync(ct);
        return await GetAsync(zone.Id, ct);
    }

    public async Task<DnsZoneDetail> UpdateAsync(Guid id, DnsZoneRequest request, CancellationToken ct)
    {
        var zone = await db.DnsZones.SingleOrDefaultAsync(z => z.Id == id, ct) ?? throw ApiException.NotFound("DNS zone");
        await ValidateAsync(request, ct);
        var contentChanged = zone.ZoneFileContent != request.ZoneFileContent;
        Apply(zone, request);
        if (contentChanged)
        {
            zone.RequiresProcessing = true;
        }
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// The zone, its records, its log and the DNS sensor it reported to. A DnsCheckTask run on this
    /// zone at the same moment fails to save and logs it; the next run no longer finds the zone.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var zone = await db.DnsZones.AsNoTracking().Where(z => z.Id == id).Select(z => new { z.ZoneName, z.HostId }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("DNS zone");
        // Deleted by ExecuteDelete below: the audit learns the zone here (docs/audit-redmine-zabbix-plan.md §3.3).
        AuditLog.Target(AuditTarget.Of(AuditTargets.DnsZone, id, zone.ZoneName, zone.HostId));
        AuditLog.Summary($"{zone.ZoneName} deleted, with its DNS sensors and log");
        var textId = id.ToString();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.DnsZoneLogEntries.Where(l => l.DnsZoneId == id).ExecuteDeleteAsync(ct);
        await db.Sensors.Where(s => s.Type == SensorType.DNS && s.TextId == textId).ExecuteDeleteAsync(ct);
        await db.DnsZones.Where(z => z.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Checked on DnsCheckTask's next run (every 15 seconds), whatever its interval.</summary>
    public async Task ForceUpdateAsync(Guid id, CancellationToken ct)
    {
        var zone = await db.DnsZones.AsNoTracking().Where(z => z.Id == id).Select(z => new { z.ZoneName, z.HostId }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("DNS zone");
        AuditLog.Target(AuditTarget.Of(AuditTargets.DnsZone, id, zone.ZoneName, zone.HostId));
        AuditLog.Summary($"{zone.ZoneName}: checked on the next run");
        await db.DnsZones.Where(z => z.Id == id).ExecuteUpdateAsync(u => u.SetProperty(z => z.ForceUpdate, true), ct);
    }

    public async Task<DnsRecord> UpdateRecordAsync(Guid id, Guid recordId, DnsRecordRequest request, CancellationToken ct)
    {
        var record = await db.DnsZoneEntries.SingleOrDefaultAsync(e => e.Id == recordId && e.DnsZoneId == id, ct)
            ?? throw ApiException.NotFound("DNS record");
        record.Ignore = request.Ignore;
        await db.SaveChangesAsync(ct);
        return ToRecord(record);
    }

    /// <summary>Newest first; for one zone, or for all when <paramref name="zoneId"/> is null.</summary>
    public async Task<Paged<DnsLogEntry>> LogAsync(Guid? zoneId, PageQuery query, CancellationToken ct)
    {
        var log = db.DnsZoneLogEntries.AsNoTracking();
        if (zoneId is { } id)
        {
            if (!await db.DnsZones.AnyAsync(z => z.Id == id, ct))
            {
                throw ApiException.NotFound("DNS zone");
            }
            log = log.Where(l => l.DnsZoneId == id);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            log = log.Where(l => EF.Functions.ILike(l.Message, pattern, @"\"));
        }
        return await Paging.PageAsync(log
            .OrderByDescending(l => l.Created).ThenBy(l => l.Id)
            .Select(l => new DnsLogEntry(l.Id, l.Created, l.DnsZoneId, l.DnsZone != null ? l.DnsZone.ZoneName : null, l.EntryType, l.Message)),
            query, ct);
    }

    private async Task<string> ValidateAsync(DnsZoneRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        string? zoneName = null;
        if (string.IsNullOrWhiteSpace(request.ZoneFileContent))
        {
            v.Add("zoneFileContent", "The zone file is required.");
        }
        else
        {
            try
            {
                zoneName = new DnsZoneFile(request.ZoneFileContent).Name;
            }
            catch (Exception)
            {
                zoneName = null;
            }
            v.Check(zoneName is not null, "zoneFileContent", "Not a zone file PNMTD can read: it needs an SOA record with absolute names.");
        }
        v.Check(request.Interval is >= 60 and <= 604_800, "interval", "Between 60 and 604800 seconds.");
        if (request.HostId is { } hostId)
        {
            v.Check(await db.Hosts.AnyAsync(h => h.Id == hostId, ct), "hostId", "No such host.");
        }
        v.ThrowIfInvalid();
        return zoneName!;
    }

    private static void Apply(DnsZoneEntity zone, DnsZoneRequest request)
    {
        zone.ZoneFileContent = request.ZoneFileContent!;
        zone.HostId = request.HostId;
        zone.Interval = request.Interval;
        zone.Enabled = request.Enabled;
    }
}
