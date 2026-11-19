namespace PNMTD.Connectors;

/// <summary><c>Connectors__*</c> (docs/connectors-plan.md §14), bound in <see cref="ConnectorSetup"/>.</summary>
public sealed class ConnectorOptions
{
    public const string Section = "Connectors";

    /// <summary>False: <c>/connector</c> answers a plain 404, and the page says connectors are off.</summary>
    public bool Enabled { get; set; } = true;

    public int MaxBodyBytes { get; set; } = 65536;

    /// <summary>Per argument.</summary>
    public int MaxValueChars { get; set; } = 4000;

    /// <summary>Under the deploy examples' proxies' 120 s.</summary>
    public int MaxWaitSeconds { get; set; } = 110;

    /// <summary>For patterns that need backtracking.</summary>
    public int PatternTimeoutMs { get; set; } = 100;

    public int ReviewTimeoutSeconds { get; set; } = 15;

    /// <summary>Reviews the worker runs at once (§9.1).</summary>
    public int WorkerParallelism { get; set; } = 4;

    /// <summary>Connector calls running at once over all connectors, 1 to 100 (§9.5, D22).</summary>
    public int MaxRunningCalls { get; set; } = 10;

    /// <summary>Failed keys per address within 10 minutes before the address is blocked (§6.5).</summary>
    public int FailedKeysPerAddress { get; set; } = 20;

    public int BlockMinutes { get; set; } = 15;

    public int IdempotencyHours { get; set; } = 24;

    /// <summary>433 → 233 after this long without another refusal (§12.3).</summary>
    public int QuietMinutes { get; set; } = 60;

    /// <summary>The newest calls of all connectors, and their runs; active calls are never deleted (§10.4).</summary>
    public int KeepCalls { get; set; } = 100_000;

    /// <summary>The worker's timer; nudges wake it in any case. For the tests.</summary>
    public int PassSeconds { get; set; } = 5;

    /// <summary>False: nudges don't wake the worker, and the tests drive it by hand.</summary>
    public bool Nudge { get; set; } = true;

    public int RunningLimit => Math.Clamp(MaxRunningCalls, 1, 100);

    public int WaitLimit => Math.Clamp(MaxWaitSeconds, 5, 110);
}
