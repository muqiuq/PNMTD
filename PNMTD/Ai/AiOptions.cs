namespace PNMTD.Ai;

/// <summary>
/// The <c>Ai</c> section (docs/ai-operations-plan.md §16): off until <c>Ai__Enabled=true</c> and a key. The models are
/// OpenRouter ids; Jev, the guard, comes through OpenRouter too, with the same key.
/// </summary>
public sealed class AiOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string? ApiKey { get; set; }

    public string MainModel { get; set; } = "deepseek/deepseek-v4.1-flash-1017";

    public string VerifyModel { get; set; } = "z-ai/glm-5.3-flashx";

    public GuardOptions Guard { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 60;

    public int Parallelism { get; set; } = 2;

    public int MaxInputChars { get; set; } = 24_000;

    public BudgetOptions Budget { get; set; } = new();

    /// <summary>The request log keeps the newest this many (R13).</summary>
    public int LogEntries { get; set; } = 10_000;

    /// <summary>OpenRouter's provider preference: deny excludes providers that store or train on prompts.</summary>
    public string DataCollection { get; set; } = "deny";

    public RetryOptions Retry { get; set; } = new();

    public MailOptions Mail { get; set; } = new();

    public AnalysisOptions Analysis { get; set; } = new();

    /// <summary>On, with a key: the AI can be called.</summary>
    public bool Usable => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    public bool IsOpenRouter => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Host.EndsWith("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>The vendor of a model id: "deepseek" of "deepseek/deepseek-v4.1-flash-1017".</summary>
    public static string Vendor(string model) => model.Split('/')[0].Trim('~').ToLowerInvariant();

    public sealed class GuardOptions
    {
        public string Model { get; set; } = "typesafe/jev-1.14";

        public string Url { get; set; } = "https://openrouter.ai/api/alpha/decisions";

        /// <summary>Without a guard, no AI on untrusted text (§3.2).</summary>
        public bool Required { get; set; } = true;

        public double Threshold { get; set; } = 0.5;

        /// <summary>Above about 32 KB Jev runs in a slower, less accurate long-context mode.</summary>
        public int MaxStateBytes { get; set; } = 30_000;

        public bool Configured => !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(Url);
    }

    public sealed class BudgetOptions
    {
        /// <summary>0: no limit.</summary>
        public decimal Usd { get; set; } = 30m;

        /// <summary>month (calendar month in TZ) or day.</summary>
        public string Period { get; set; } = "month";
    }

    public sealed class RetryOptions
    {
        public int QuickAttempts { get; set; } = 5;

        public int QuickDelaySeconds { get; set; } = 60;

        /// <summary>The delays after the quick retries, in hours; the last one repeats.</summary>
        public string LongDelaysHours { get; set; } = "1,3,24";

        public IReadOnlyList<double> LongDelays =>
            LongDelaysHours.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => double.TryParse(h, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 24)
                .DefaultIfEmpty(24).ToList();
    }

    public sealed class MailOptions
    {
        public bool Enabled { get; set; } = true;

        public int WaitMinutes { get; set; } = 5;
    }

    public sealed class AnalysisOptions
    {
        public bool Enabled { get; set; } = true;

        public int IntervalMinutes { get; set; } = 60;
    }
}
