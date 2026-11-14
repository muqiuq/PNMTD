namespace PNMTD.Cookie;

/// <summary>
/// The <c>Cookie</c> section of .env (docs/cookie-plan.md §13.3, §14.3): available with <c>Cookie__Enabled=true</c>, and still
/// stopped until an admin starts it. Everything else is on Admin → Cookie.
/// </summary>
public sealed class CookieOptions
{
    public const string SectionName = "Cookie";

    public bool Enabled { get; set; }

    /// <summary>Off or Code (§13.2); LocalModel comes with package nine point five.</summary>
    public string Privacy { get; set; } = "Code";

    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash-1017";

    /// <summary>In order, sticky per round (§14.3); empty lets OpenRouter choose.</summary>
    public string Providers { get; set; } = "nebius,mistral,together";

    /// <summary>Only zero-data-retention endpoints.</summary>
    public bool Zdr { get; set; } = true;

    /// <summary>fp8 or better for tool use (§14.2); empty for any.</summary>
    public string MinQuantization { get; set; } = "fp8";

    /// <summary>Cookie's own budget per calendar month (§19.1); 0 for none.</summary>
    public decimal BudgetUsd { get; set; } = 30m;

    /// <summary>How often the case book looks at the sensors and Zabbix's problems.</summary>
    public int CaseBookSeconds { get; set; } = 30;

    /// <summary>How often it reads the watched tickets.</summary>
    public int TicketsMinutes { get; set; } = 5;

    public bool PrivacyOff => string.Equals(Privacy, "Off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Cookie's services (docs/cookie-plan.md §19.3).</summary>
public static class CookieSetup
{
    public static IServiceCollection AddCookie(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CookieOptions>(configuration.GetSection(CookieOptions.SectionName));
        services.AddSingleton<CookieSettings>();
        services.AddSingleton<CookieSignals>();
        services.AddSingleton<CaseBook>();
        services.AddSingleton<CaseBookTask>();
        services.AddScoped<CookieService>();
        return services;
    }
}

/// <summary>
/// What wakes Cookie (docs/cookie-plan.md §4.1): the case book raises it when a case opens, closes or changes, or a deadline
/// falls due; a message and Run now raise it too. Several raises before the next wait count once.
/// </summary>
public sealed class CookieSignals
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly Lock gate = new();
    private readonly List<string> reasons = [];

    public void Raise(string reason)
    {
        lock (gate)
        {
            if (reasons.Count < 50)
            {
                reasons.Add(reason);
            }
            if (signal.CurrentCount == 0)
            {
                signal.Release();
            }
        }
    }

    /// <summary>True when raised within <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => await signal.WaitAsync(timeout, ct);

    /// <summary>The reasons since the last call.</summary>
    public IReadOnlyList<string> Take()
    {
        lock (gate)
        {
            var taken = reasons.Distinct().ToList();
            reasons.Clear();
            return taken;
        }
    }
}
