using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary>A DNS zone: its records are compared with what the resolvers answer every Interval seconds.</summary>
public sealed record DnsZoneItem(
    Guid Id,
    string ZoneName,
    Ref? Host,
    bool Enabled,
    bool RecordsMatch,
    bool RequiresProcessing,
    bool ForceUpdate,
    int Interval,
    DateTime? LastChecked,
    DateTime? NextCheck,
    int RecordCount,
    int MismatchCount,
    int IgnoredCount);

public sealed record DnsZoneDetail(
    Guid Id,
    string ZoneName,
    Ref? Host,
    bool Enabled,
    bool RecordsMatch,
    bool RequiresProcessing,
    bool ForceUpdate,
    int Interval,
    DateTime? LastChecked,
    DateTime? NextCheck,
    string ZoneFileContent,
    IReadOnlyList<DnsRecord> Records);

/// <summary>A record of the zone file next to what was resolved.</summary>
public sealed record DnsRecord(
    Guid Id,
    string Name,
    DnsZoneResourceType RecordType,
    int Ttl,
    string ReferenceValue,
    string? ActualValue,
    bool IsMatch,
    bool Ignore,
    int FailuresInARow,
    DateTime? Updated);

/// <summary>
/// POST and PUT <c>/dns-zones</c>. The zone name comes from the zone file (its SOA record). With a
/// host, the zone reports to a DNS sensor on it. Interval: at least 60 seconds.
/// </summary>
public sealed class DnsZoneRequest
{
    public string? ZoneFileContent { get; init; }

    public Guid? HostId { get; init; }

    public int Interval { get; init; } = 600;

    public bool Enabled { get; init; } = true;
}

/// <summary><c>PUT /dns-zones/{id}/records/{recordId}</c>: an ignored record is not compared.</summary>
public sealed class DnsRecordRequest
{
    public bool Ignore { get; init; }
}

public sealed record DnsLogEntry(Guid Id, DateTime Created, Guid? DnsZoneId, string? ZoneName, DnsZoneLogEntryType EntryType, string Message);
