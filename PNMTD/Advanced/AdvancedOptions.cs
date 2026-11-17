namespace PNMTD.Advanced;

/// <summary>Advanced sensors (docs/advanced-sensor-plan.md §11), from .env as <c>Advanced__*</c>; every one optional.</summary>
public sealed class AdvancedOptions
{
    public const string Section = "Advanced";

    /// <summary>The largest report, decompressed (256 KiB).</summary>
    public int MaxReportBytes { get; set; } = 262_144;

    /// <summary>One report per sensor in this many seconds; a sooner one gets 429 too_soon. 0: no limit.</summary>
    public int MinSecondsBetweenReports { get; set; } = 5;

    /// <summary>Reports kept per sensor; the newest two and every report waiting for the evaluator always.</summary>
    public int KeepReports { get; set; } = 500;

    public int KeepDays { get; set; } = 30;

    public long MaxBytesPerSensor { get; set; } = 52_428_800;

    /// <summary>Days of charted values.</summary>
    public int SampleDays { get; set; } = 30;

    /// <summary>The evaluator's timer: staleness, age rules, and reports whose nudge was lost.</summary>
    public int EvaluateSeconds { get; set; } = 30;

    /// <summary>A stored report wakes the evaluator at once. Off: reports wait for the timer (for tests).</summary>
    public bool Nudge { get; set; } = true;

    public int MaxBytes => Math.Clamp(MaxReportBytes, 1024, 16 * 1024 * 1024);

    public TimeSpan MinBetween => TimeSpan.FromSeconds(Math.Clamp(MinSecondsBetweenReports, 0, 3600));

    public int Keep => Math.Clamp(KeepReports, 2, 100_000);

    public TimeSpan KeepFor => TimeSpan.FromDays(Math.Clamp(KeepDays, 1, 3650));

    public long MaxBytesKept => Math.Clamp(MaxBytesPerSensor, 1024 * 1024, 10L * 1024 * 1024 * 1024);

    public TimeSpan SamplesFor => TimeSpan.FromDays(Math.Clamp(SampleDays, 1, 3650));

    public TimeSpan Evaluate => TimeSpan.FromSeconds(Math.Clamp(EvaluateSeconds, 5, 3600));
}
