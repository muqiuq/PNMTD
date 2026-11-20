namespace PNMTD.Redmine;

/// <summary>
/// Redmine (docs/audit-redmine-zabbix-plan.md §6.1), from .env: Redmine__Url, Redmine__ApiKey and Redmine__Projects are
/// needed, the rest optional. The key never leaves the server.
/// </summary>
public sealed class RedmineOptions
{
    public const string Section = "Redmine";

    /// <summary>Where PNMT reaches Redmine.</summary>
    public string? Url { get; set; }

    /// <summary>The API key of the account PNMT uses: read access to the projects, no more.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The projects PNMT shows: ids or identifiers, separated by spaces or commas.</summary>
    public string? Projects { get; set; }

    public bool IncludeSubprojects { get; set; } = true;

    /// <summary>Where browsers reach Redmine, if not at <see cref="Url"/>: every link to Redmine is built from it.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Redmine's text formatting (common_mark, markdown, textile or plain): its API doesn't say.</summary>
    public string TextFormatting { get; set; } = "common_mark";

    public bool ShowPrivateNotes { get; set; }

    /// <summary>
    /// Cookie's key for writing (docs/cookie-plan.md §19.1): creating tickets and adding notes. Separate from <see cref="ApiKey"/>,
    /// which only reads; without it, nothing is written.
    /// </summary>
    public string? WriteApiKey { get; set; }

    public bool CanWrite => Enabled && !string.IsNullOrWhiteSpace(WriteApiKey);

    /// <summary>How often the copy of linked tickets is refreshed.</summary>
    public int SyncMinutes { get; set; } = 10;

    /// <summary>When an open ticket counts as waiting, for the overview's list.</summary>
    public int StaleDays { get; set; } = 14;

    /// <summary>A Redmine with a certificate of its own: the SHA-256 of the one PNMT accepts, instead of no validation.</summary>
    public string? CertificateSha256 { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    // The worker and its snapshots (docs/redmine-queue-outbound-plan.md §3, §4, §9).

    /// <summary>Calls in flight at most; one of them is kept for the button, write and interactive lanes.</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>How old a watched snapshot may get.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>How long a request keeps a snapshot watched.</summary>
    public int WatchMinutes { get; set; } = 5;

    /// <summary>When a snapshot nobody asked for is removed.</summary>
    public int KeepMinutes { get; set; } = 60;

    public int MaxSnapshots { get; set; } = 500;

    /// <summary>How long a button or interactive caller waits for a job.</summary>
    public int WaitSeconds { get; set; } = 30;

    /// <summary>The background lane's pause after Redmine answered 429 or 503, unless it says how long itself.</summary>
    public int BackoffSeconds { get; set; } = 30;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey) && ProjectList.Count > 0;

    public IReadOnlyList<string> ProjectList =>
        (Projects ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    /// <summary>Where a browser opens Redmine.</summary>
    public string BrowserUrl => (string.IsNullOrWhiteSpace(PublicUrl) ? Url! : PublicUrl).TrimEnd('/');
}
