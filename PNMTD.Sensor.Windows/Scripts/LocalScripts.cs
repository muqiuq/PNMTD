using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Scripts;

internal enum ScheduleKind { Daily, Weekly, Interval, AtStartup }

/// <summary>When a registered script runs by itself, as a Windows scheduled task: daily or weekly at a time, every N minutes, at startup.</summary>
internal sealed record LocalSchedule(ScheduleKind Kind, string? At = null, DayOfWeek? Day = null, int? IntervalMinutes = null)
{
    public string Describe() => Kind switch
    {
        ScheduleKind.Daily => $"daily at {At}",
        ScheduleKind.Weekly => $"{Day}s at {At}",
        ScheduleKind.Interval => $"every {IntervalMinutes} min",
        _ => "at startup",
    };
}

/// <summary>
/// A script registered on this server (docs/windows-sensor-plan.md §7.2): a copy in %ProgramData%\PNMT Sensor\scripts that only
/// SYSTEM, Administrators and its account can read, its SHA-256, the account it runs as, and optionally a schedule. PNMT may run
/// it, as that account, when scripts from PNMT are allowed; the schedule runs it as a Windows scheduled task.
/// </summary>
internal sealed record LocalScript(
    string Name,
    string File,
    string Sha256,
    string? Source,
    DateTimeOffset AddedAt,
    string AddedBy,
    DateTimeOffset UpdatedAt,
    RunAsKind Kind,
    string? Account,
    LocalSchedule? Schedule)
{
    public string RunAsText => Kind switch
    {
        RunAsKind.Service => "this service (LocalSystem)",
        RunAsKind.Gmsa => $"{Account} (gMSA)",
        _ => Account ?? "?",
    };

    /// <summary>Whether PNMT's run-as names this script's account: the service, or the same account (its password stored here or typed in PNMT).</summary>
    public bool RunsAs(RunAsSpec runAs) => Kind == RunAsKind.Service
        ? runAs.Kind == RunAsKind.Service
        : runAs.Kind != RunAsKind.Service && string.Equals(Account, runAs.Account, StringComparison.OrdinalIgnoreCase);

    /// <summary>The copy as it is now, against the registered hash: an edit after the registration doesn't run unnoticed.</summary>
    public bool Matches() => System.IO.File.Exists(File) && LocalScripts.HashOf(File) == Sha256;
}

/// <summary>A registered script's scheduled task, as the Task Scheduler reports it.</summary>
internal sealed record TaskInfo(string Name, string State, DateTimeOffset? LastRun, long? LastResult, DateTimeOffset? NextRun);

/// <summary>The scripts registered on this server, in state\local-scripts.json, with their copies and scheduled tasks.</summary>
internal static partial class LocalScripts
{
    private const string RegistryFile = "local-scripts.json";

    /// <summary>The scheduled tasks' folder in the Task Scheduler.</summary>
    public const string TaskFolder = @"\PNMT Sensor\";

    public static string Folder => Path.Combine(AgentInfo.DataFolder, "scripts");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,79}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"[^A-Za-z0-9 ._-]+")]
    private static partial Regex NotNameCharacters();

    /// <summary>A name: letters, digits, space, dot, dash and underscore, 1 to 80, usable as a file and a task name.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name) && !name.EndsWith('.') && !name.EndsWith(' ');

    public static string NameFrom(string path)
    {
        var name = NotNameCharacters().Replace(Path.GetFileNameWithoutExtension(path), "-").Trim(' ', '-', '.');
        return name.Length == 0 ? "script" : name.Length > 80 ? name[..80] : name;
    }

    /// <summary>The same hash as PNMT's (<see cref="ScriptHashes"/>): the text, without a BOM and with LF line endings.</summary>
    public static string HashOf(string path) => ScriptHashes.Of(System.IO.File.ReadAllText(path));

    public static IReadOnlyList<LocalScript> All() => AgentFiles.ReadState<List<LocalScript>>(RegistryFile) ?? [];

    public static LocalScript? ByName(string name) => All().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static LocalScript? ByHash(string hash) => All().FirstOrDefault(s => s.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));

    private static void Write(IEnumerable<LocalScript> scripts) =>
        AgentFiles.WriteState(RegistryFile, scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>
    /// Registers a script, or changes a registered one's account and schedule: the copy (when <paramref name="source"/> is given),
    /// its ACL, the password (stored here when given), the scheduled task (registered, or removed without a schedule).
    /// </summary>
    public static async Task<LocalScript> SaveAsync(LocalScript? existing, string? source, string name, RunAsKind kind, string? account, string? password,
        LocalSchedule? schedule, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        AgentFiles.Secure();
        var file = existing?.File ?? Path.Combine(Folder, name + ".ps1");
        var copied = false;
        if (source is not null)
        {
            System.IO.File.Copy(source, file, overwrite: true);
            copied = existing is null;
        }
        try
        {
            account = kind == RunAsKind.Service ? null : account;
            Secure(file, account);
            if (kind == RunAsKind.Stored && !string.IsNullOrEmpty(password))
            {
                StoredAccounts.Set(account!, password);
            }
            var now = DateTimeOffset.UtcNow;
            var script = new LocalScript(name, file, HashOf(file), source ?? existing?.Source, existing?.AddedAt ?? now,
                existing?.AddedBy ?? $"{Environment.UserDomainName}\\{Environment.UserName}", now, kind, account, schedule);
            if (schedule is not null)
            {
                await RegisterTaskAsync(script, kind == RunAsKind.Stored ? (string.IsNullOrEmpty(password) ? StoredAccounts.Password(account!) : password) : null, ct);
            }
            else if (existing?.Schedule is not null)
            {
                await RunTaskScriptAsync(new { op = "unregister", name }, ct);
            }
            Write(All().Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Append(script));
            return script;
        }
        catch when (copied)
        {
            TryDelete(file);
            throw;
        }
    }

    /// <summary>A new version of a registered script's file: the copy and the hash change, the account and the schedule stay.</summary>
    public static LocalScript Replace(LocalScript existing, string source)
    {
        System.IO.File.Copy(source, existing.File, overwrite: true);
        Secure(existing.File, existing.Account);
        var updated = existing with { Sha256 = HashOf(existing.File), Source = source, UpdatedAt = DateTimeOffset.UtcNow };
        Write(All().Select(s => s.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase) ? updated : s));
        return updated;
    }

    /// <summary>Unregisters a script: its scheduled task, its copy and its entry go. A stored password stays (other scripts may use it).</summary>
    public static async Task RemoveAsync(LocalScript script, CancellationToken ct)
    {
        if (script.Schedule is not null)
        {
            await RunTaskScriptAsync(new { op = "unregister", name = script.Name }, ct);
        }
        TryDelete(script.File);
        Write(All().Where(s => !s.Name.Equals(script.Name, StringComparison.OrdinalIgnoreCase)));
    }

    public static Task StartTaskAsync(string name, CancellationToken ct) => RunTaskScriptAsync(new { op = "start", name }, ct);

    /// <summary>For the uninstall: every task in the PNMT Sensor folder.</summary>
    public static Task RemoveAllTasksAsync(CancellationToken ct) => RunTaskScriptAsync(new { op = "unregister-all" }, ct);

    public static async Task<IReadOnlyList<TaskInfo>> TasksAsync(CancellationToken ct)
    {
        var json = await RunTaskScriptAsync(new { op = "info" }, ct);
        return string.IsNullOrWhiteSpace(json) ? [] : AgentJson.Deserialize<List<TaskInfo>>(json) ?? [];
    }

    private static Task RegisterTaskAsync(LocalScript script, string? password, CancellationToken ct) => RunTaskScriptAsync(new
    {
        op = "register",
        name = script.Name,
        file = script.File,
        shell = PowerShell.Executable("powershell"),
        kind = script.Kind.ToString(),
        account = script.Account,
        password,
        description = $"Registered in the PNMT Sensor window. SHA-256 at the registration: {script.Sha256}",
        schedule = new
        {
            kind = script.Schedule!.Kind.ToString(),
            at = script.Schedule.At,
            day = script.Schedule.Day?.ToString(),
            intervalMinutes = script.Schedule.IntervalMinutes,
        },
    }, ct);

    /// <summary>
    /// The copy: SYSTEM and Administrators, and the script's account may read it (a scheduled task runs it as that account). An
    /// account Windows doesn't know fails here, before anything else is saved.
    /// </summary>
    private static void Secure(string file, string? account)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        if (account is not null)
        {
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            }
            catch (IdentityNotMappedException)
            {
                throw new InvalidOperationException($"Windows doesn't know the account {account}.");
            }
        }
        new FileInfo(file).SetAccessControl(security);
    }

    private static void TryDelete(string file)
    {
        try
        {
            System.IO.File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The Task Scheduler through its PowerShell module. Input and output travel Base64-encoded UTF-8: Windows PowerShell reads and
    /// writes the console in the OEM code page, which would garble names and passwords with umlauts.
    /// </summary>
    private static async Task<string> RunTaskScriptAsync(object input, CancellationToken ct)
    {
        var folder = Path.Combine(AgentInfo.StateFolder, "runs");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"tasks-{Guid.NewGuid():N}.ps1");
        await System.IO.File.WriteAllTextAsync(file, TaskScript, new UTF8Encoding(true), ct);
        try
        {
            var json = JsonSerializer.Serialize(input);
            var (code, output, errors) = await PowerShell.RunFileAsync("powershell", file, [], Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
                TimeSpan.FromSeconds(90), ct);
            if (code != 0)
            {
                throw new InvalidOperationException(errors.Trim() is { Length: > 0 } message ? message : $"The Task Scheduler failed ({code}).");
            }
            var last = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return last is null ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(last));
        }
        finally
        {
            TryDelete(file);
        }
    }

    private const string TaskScript = """
        $ErrorActionPreference = 'Stop'
        trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        $in = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd().Trim())) | ConvertFrom-Json
        $path = '\PNMT Sensor\'
        function Out-Result($value) {
          [Console]::Out.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $value -Depth 4 -Compress))))
        }
        switch ($in.op) {
          'register' {
            $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $in.file
            $action = New-ScheduledTaskAction -Execute $in.shell -Argument $arguments
            $s = $in.schedule
            switch ($s.kind) {
              'Daily' { $trigger = New-ScheduledTaskTrigger -Daily -At $s.at }
              'Weekly' { $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $s.day -At $s.at }
              'Interval' { $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $s.intervalMinutes) }
              default { $trigger = New-ScheduledTaskTrigger -AtStartup }
            }
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            $task = @{ TaskName = $in.name; TaskPath = $path; Action = $action; Trigger = $trigger; Settings = $settings; Description = $in.description; Force = $true }
            switch ($in.kind) {
              'Service' { Register-ScheduledTask @task -Principal (New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest) | Out-Null }
              'Gmsa' { Register-ScheduledTask @task -Principal (New-ScheduledTaskPrincipal -UserId $in.account -LogonType Password -RunLevel Highest) | Out-Null }
              default { Register-ScheduledTask @task -User $in.account -Password $in.password -RunLevel Highest | Out-Null }
            }
            $in.password = $null
          }
          'unregister' { Get-ScheduledTask -TaskPath $path -TaskName $in.name -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false }
          'unregister-all' { Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false }
          'start' { Start-ScheduledTask -TaskPath $path -TaskName $in.name }
          'info' {
            $tasks = @(Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | ForEach-Object {
              $i = $_ | Get-ScheduledTaskInfo
              $ran = $i.LastRunTime -and $i.LastRunTime.Year -gt 2000
              [ordered]@{
                name = $_.TaskName
                state = [string]$_.State
                lastRun = if ($ran) { $i.LastRunTime.ToString('o') } else { $null }
                lastResult = if ($ran) { [int64]$i.LastTaskResult } else { $null }
                nextRun = if ($i.NextRunTime) { $i.NextRunTime.ToString('o') } else { $null }
              }
            })
            Out-Result $tasks
          }
        }
        """;
}
