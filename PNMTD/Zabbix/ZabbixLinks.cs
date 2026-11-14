using System.Globalization;

namespace PNMTD.Zabbix;

/// <summary>
/// Every URL into Zabbix's frontend (docs/audit-redmine-zabbix-plan.md §7.9), from Zabbix__PublicUrl and the version, as Z0
/// checked them on 6.0, 7.0 and 8.0. Ids come from Zabbix's answers and must be numbers; names are escaped.
/// </summary>
public sealed class ZabbixLinks(string browserUrl, ZabbixVersion? version)
{
    private string Url(string path) => $"{browserUrl.TrimEnd('/')}/{path}";

    private static string Id(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) ? id : throw new ArgumentException($"Not a Zabbix id: {id}", nameof(id));

    public string Dashboard => Url("zabbix.php?action=dashboard.view");

    /// <summary>Monitoring → Hosts, filtered to it.</summary>
    public string HostView(string technicalName) =>
        Url($"zabbix.php?action=host.view&filter_set=1&filter_host={Uri.EscapeDataString(technicalName)}");

    public string HostDashboards(string hostId) => Url($"zabbix.php?action=host.dashboard.view&hostid={Id(hostId)}");

    public string HostProblems(string hostId) => Url($"zabbix.php?action=problem.view&filter_set=1&hostids%5B%5D={Id(hostId)}");

    public string HostLatest(string hostId) => Url($"zabbix.php?action=latest.view&filter_set=1&hostids%5B%5D={Id(hostId)}");

    /// <summary>The host's configuration: a page up to 7.0, a popup from 7.2 (8.0 answers host.edit with JSON).</summary>
    public string HostConfiguration(string hostId) => version is { } v && v >= ZabbixVersion.HostPopupFrom
        ? Url($"zabbix.php?action=popup&popup=host.edit&hostid={Id(hostId)}")
        : Url($"zabbix.php?action=host.edit&hostid={Id(hostId)}");

    public string Event(string triggerId, string eventId) => Url($"tr_events.php?triggerid={Id(triggerId)}&eventid={Id(eventId)}");

    public string ItemGraph(string itemId) => Url($"history.php?action=showgraph&itemids%5B%5D={Id(itemId)}");

    public static string Number(long id) => id.ToString(CultureInfo.InvariantCulture);
}
