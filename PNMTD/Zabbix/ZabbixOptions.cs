namespace PNMTD.Zabbix;

/// <summary>A problem's severity in Zabbix (docs/audit-redmine-zabbix-plan.md §7.4), in its order.</summary>
public enum ZabbixSeverity { NotClassified = 0, Information = 1, Warning = 2, Average = 3, High = 4, Disaster = 5 }

/// <summary>
/// Zabbix (docs/audit-redmine-zabbix-plan.md §7.1), from .env: Zabbix__Url and Zabbix__ApiToken are needed, the rest
/// optional. The token never leaves the server.
/// </summary>
public sealed class ZabbixOptions
{
    public const string Section = "Zabbix";

    /// <summary>Zabbix's frontend: the API is {Url}/api_jsonrpc.php.</summary>
    public string? Url { get; set; }

    /// <summary>An API token (Zabbix 5.4 and later) of the account PNMT uses: read access to its host groups, no more.</summary>
    public string? ApiToken { get; set; }

    /// <summary>Only hosts of these groups (names), separated by commas. Empty: all the token can see.</summary>
    public string? HostGroups { get; set; }

    /// <summary>Where browsers reach Zabbix, if not at <see cref="Url"/>: every link into Zabbix is built from it.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>The default for new Zabbix sensors: they fail from this severity up.</summary>
    public ZabbixSeverity MinSeverity { get; set; } = ZabbixSeverity.Average;

    /// <summary>How often the open problems are fetched; at least 30.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>How often the hosts are mirrored.</summary>
    public int HostSyncMinutes { get; set; } = 10;

    /// <summary>
    /// Hide the problems whose trigger, host or item is disabled, as Zabbix's frontend does (<c>trigger.get</c>'s
    /// <c>monitored</c>; docs/zabbix-hidden-problems-plan.md §4).
    /// </summary>
    public bool HideUnmonitored { get; set; } = true;

    /// <summary>Hide the problems of triggers that depend on a trigger in problem, as Zabbix's frontend does (<c>skipDependent</c>).</summary>
    public bool HideDependent { get; set; } = true;

    /// <summary>How often a hidden problem's trigger is asked again; at least 1. The shown ones are asked every poll.</summary>
    public int HiddenRecheckMinutes { get; set; } = 60;

    /// <summary>A Zabbix with a certificate of its own (self-signed): the SHA-256 of the one PNMT accepts besides valid ones.</summary>
    public string? CertificateSha256 { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiToken);

    public IReadOnlyList<string> HostGroupList =>
        (HostGroups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Where a browser opens Zabbix.</summary>
    public string BrowserUrl => (string.IsNullOrWhiteSpace(PublicUrl) ? Url! : PublicUrl).TrimEnd('/');

    public TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(30, PollSeconds));

    public TimeSpan HiddenRecheck => TimeSpan.FromMinutes(Math.Max(1, HiddenRecheckMinutes));
}
