using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Zabbix;

// Zabbix's answers as Z0 recorded them (docs/audit-redmine-zabbix-plan.md §7.2; PNMTD.Tests/Zabbix/Fixtures): ids, flags and
// clocks are strings; host groups come as "groups" (selectGroups, up to 6.0) or "hostgroups" (selectHostGroups, 6.2 on).

public sealed record ZxGroup(string Groupid, string Name);

public sealed record ZxTag(string Tag, string Value);

/// <summary>An interface: type 1 agent, 2 SNMP, 3 IPMI, 4 JMX; available 0 unknown, 1 available, 2 unavailable.</summary>
public sealed record ZxInterface(string Interfaceid, string Type, string Main, string Useip, string Ip, string Dns, string Port, string? Available, string? Error);

public sealed record ZxTemplate(string Templateid, string Name);

public sealed record ZxHost(string Hostid, string Host, string Name, string Status, [property: JsonPropertyName("maintenance_status")] string? MaintenanceStatus,
    List<ZxGroup>? Groups, List<ZxGroup>? Hostgroups, List<ZxInterface>? Interfaces, List<ZxTag>? Tags, string? Description,
    List<ZxTemplate>? ParentTemplates, JsonElement? Inventory)
{
    public IReadOnlyList<ZxGroup> AllGroups => Hostgroups ?? Groups ?? [];
}

public sealed record ZxAcknowledge(string Acknowledgeid, string Userid, string Clock, string? Message, string Action);

/// <summary>
/// A problem as <c>problem.get</c> returns it. With <c>recent</c>, also one resolved within Zabbix's <i>Display OK triggers for</i>:
/// <see cref="REventid"/> is its recovery event ("0" while open), <see cref="RClock"/> its time (docs/boards-plan.md §7.5).
/// </summary>
public sealed record ZxProblem(string Eventid, string Objectid, string Name, string Severity, string Clock, string Acknowledged, string? Suppressed,
    string? Opdata, List<ZxTag>? Tags, List<ZxAcknowledge>? Acknowledges,
    [property: JsonPropertyName("r_eventid")] string? REventid = null,
    [property: JsonPropertyName("r_clock")] string? RClock = null)
{
    public bool Resolved => REventid is { Length: > 0 } r && r != "0";
}

public sealed record ZxTriggerHost(string Hostid, string Host, string Name);

public sealed record ZxTrigger(string Triggerid, string Description, string Priority, string? Comments, string? Url, List<ZxTriggerHost>? Hosts);

public sealed record ZxItem(string Itemid, string Name, [property: JsonPropertyName("key_")] string Key, string? Lastvalue, string? Lastclock, string? Units,
    [property: JsonPropertyName("value_type")] string ValueType, string? State, string? Error, string? Status);

internal sealed record ZxError(int Code, string Message, string? Data);

internal sealed record ZxResponse<T>(T? Result, ZxError? Error);
