using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Join;
using PNMTD.Sensor.Windows.Logging;
using PNMTD.Sensor.Windows.Scripts;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>
/// The joined agent (docs/windows-sensor-plan.md §10.4), in four tabs (WMI: <see cref="WmiTab"/>). Status: the connection and the service, this server in
/// PNMT with both keys, problems, diagnostics, the audit policy's gaps, and the actions. Scripts: whether PNMT may run scripts, the
/// scripts registered here and the stored accounts (<see cref="ScriptsTab"/>). Log: the service's log file, following. Built
/// once; every tick updates what changed and leaves inputs alone.
/// </summary>
internal sealed class StatusView : UserControl
{
    private readonly ILogger logger;
    private readonly AgentOs os;
    private readonly TabControl tabs;
    private readonly TabPage logPage;

    // Status
    private Label connectionPill = null!;
    private Label connectionText = null!;
    private TextBox urlBox = null!;
    private Label transport = null!;
    private Label since = null!;
    private Label heartbeat = null!;
    private Label service = null!;
    private Button start = null!;
    private Button stop = null!;
    private Button restart = null!;
    private Card updateCard = null!;
    private Label updateText = null!;
    private Card problemsCard = null!;
    private Card diagnosticsCard = null!;
    private Card sectionsCard = null!;
    private TableLayoutPanel sectionsTable = null!;
    private string sectionsShown = "";
    private Card auditCard = null!;
    private Label server = null!;
    private TextBox agentId = null!;
    private TextBox agentKey = null!;
    private TextBox pnmtdKey = null!;
    private Label actionNote = null!;
    private string problemsShown = "";
    private string diagnosticsShown = "";
    private string gapsShown = "";

    // Log
    private TextBox logBox = null!;
    private Label logPath = null!;
    private CheckBox follow = null!;
    private CheckBox debug = null!;
    private (string Name, long Length) logShown;
    private bool settingDebug;

    public StatusView(AgentConfigFile config, AgentOs os, ILogger logger)
    {
        Config = config;
        this.os = os;
        this.logger = logger;
        Dock = DockStyle.Fill;
        BackColor = Theme.Page;
        Padding = Theme.P(12, 10, 12, 12);
        tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.Body, Padding = new Point(Theme.S(16), Theme.S(5)) };
        tabs.TabPages.Add(Tab("Status", BuildStatus()));
        tabs.TabPages.Add(Tab("Scripts", new ScriptsTab(logger)));
        tabs.TabPages.Add(Tab("WMI", new WmiTab(logger)));
        logPage = Tab("Log", BuildLog());
        tabs.TabPages.Add(logPage);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == logPage)
            {
                RefreshLog(force: true);
            }
        };
        Controls.Add(tabs);
        Tick(config);
    }

    public AgentConfigFile Config { get; private set; }

    public event Action? RejoinRequested;

    public event Action? Uninstalled;

    private static TabPage Tab(string title, Control content)
    {
        var page = new TabPage(title) { BackColor = Theme.Page, UseVisualStyleBackColor = false, Padding = Padding.Empty };
        page.Controls.Add(content);
        return page;
    }

    // --- Status ------------------------------------------------------------------------------------------------

    private Control BuildStatus()
    {
        var (page, stack) = Kit.Page();

        var connection = new Card("Connection");
        connectionPill = Kit.Pill();
        connectionText = Kit.Label("");
        connection.Add(Kit.Pair(connectionPill, connectionText));
        var facts = connection.Add(Kit.Facts());
        urlBox = Kit.Fact(facts, "PNMT", Kit.Copyable(Config.Url));
        transport = Kit.Fact(facts, "Transport", Kit.Label(""));
        since = Kit.Fact(facts, "Connected since", Kit.Label(""));
        heartbeat = Kit.Fact(facts, "Last heartbeat", Kit.Label(""));
        service = Kit.Fact(facts, "Service", Kit.Label(""));
        start = Kit.Button("Start", ButtonKind.Secondary, (s, _) => Run("Started the service", () => ServiceControl.Start(), (Control)s!));
        stop = Kit.Button("Stop", ButtonKind.Secondary, (s, _) => Run("Stopped the service", ServiceControl.Stop, (Control)s!));
        restart = Kit.Button("Restart", ButtonKind.Secondary, (s, _) => Run("Restarted the service", ServiceControl.Restart, (Control)s!));
        connection.Add(Kit.Row(start, stop, restart));
        Kit.Add(stack, connection);

        updateCard = new Card("Update");
        updateText = updateCard.Add(Kit.Label(""));
        updateCard.Add(Kit.Row(Kit.Button("Update", ButtonKind.Primary, (s, _) => Run($"Updated the installed agent to {AgentInfo.Version}", ServiceControl.Update, (Control)s!))));
        updateCard.Visible = false;
        Kit.Add(stack, updateCard);

        problemsCard = new Card("Problems") { Visible = false };
        Kit.Add(stack, problemsCard);

        var mine = new Card("This server in PNMT");
        var serverFacts = mine.Add(Kit.Facts());
        server = Kit.Fact(serverFacts, "Server", Kit.Label("", Theme.Strong));
        agentId = Kit.Fact(serverFacts, "Agent id", Kit.Copyable(""));
        agentKey = Kit.Fact(serverFacts, "This server's key", Kit.Copyable(""));
        pnmtdKey = Kit.Fact(serverFacts, "PNMT's key", Kit.Copyable(""));
        Kit.Fact(serverFacts, "Windows", Kit.Label($"{os.Caption} {os.DisplayVersion} ({os.Build})" + (os.Supported ? "" : " · not a supported version, runs anyway"),
            Theme.Body, os.Supported ? Theme.Text : Theme.Warn));
        Kit.Fact(serverFacts, "Agent", Kit.Label($"{AgentInfo.Version} ({AgentInfo.Architecture})"));
        Kit.Add(stack, mine);

        sectionsCard = new Card("Sections") { Visible = false };
        Kit.Add(stack, sectionsCard);

        diagnosticsCard = new Card("Diagnostics") { Visible = false };
        Kit.Add(stack, diagnosticsCard);

        auditCard = new Card("The audit policy's gaps") { Visible = false };
        Kit.Add(stack, auditCard);

        var actions = new Card("Actions");
        actions.Add(Kit.Row(
            Kit.Button("Collect now", ButtonKind.Secondary, (_, _) => CollectNow()),
            Kit.Button("Open log folder", ButtonKind.Secondary, (_, _) => OpenLogFolder()),
            Kit.Button("Re-join…", ButtonKind.Secondary, (_, _) => RejoinRequested?.Invoke()),
            Kit.Button("Uninstall…", ButtonKind.Danger, (_, _) => Uninstall())));
        actionNote = actions.Add(Kit.Muted(""));
        Kit.Add(stack, actions);
        return page;
    }

    /// <summary>Every two seconds: the connection, the service, and the lists when they changed.</summary>
    public void Tick(AgentConfigFile config)
    {
        Config = config;
        var status = AgentFiles.ReadStatus();
        var serviceState = ServiceControl.Status();
        var (text, tone, detail) = AgentState.Of(config, status, serviceState);
        Kit.SetPill(connectionPill, text, tone);
        Kit.SetText(connectionText, detail);
        Kit.SetText(urlBox, config.Url);
        Kit.SetText(transport, config.PlainHttp ? "plain HTTP, confirmed at the join: not encrypted" : "HTTPS");
        transport.ForeColor = config.PlainHttp ? Theme.Warn : Theme.Text;
        Kit.SetText(since, status?.Connected == true && status.ConnectedSince is { } s ? s.ToLocalTime().ToString("g") : "–");
        Kit.SetText(heartbeat, status?.LastHeartbeatAt is { } h ? h.ToLocalTime().ToString("T") : "–");
        Kit.SetText(service, $"{AgentInfo.ServiceName}: {AgentState.Words(serviceState)}");
        start.Enabled = serviceState is ServiceControllerStatus.Stopped;
        stop.Enabled = restart.Enabled = serviceState is ServiceControllerStatus.Running;

        Kit.SetText(server, $"{config.HostName} / {config.SensorName}");
        Kit.SetText(agentId, config.AgentId.ToString());
        Kit.SetText(agentKey, config.AgentKeyFingerprint);
        Kit.SetText(pnmtdKey, config.PnmtdFingerprint);

        var installed = ServiceControl.InstalledVersion();
        var newer = installed is not null && Version.TryParse(AgentInfo.Version.Split('-')[0], out var mine) && mine > installed
            && !string.Equals(Path.GetFullPath(AgentInfo.CurrentExecutable), Path.GetFullPath(AgentInfo.InstalledExecutable), StringComparison.OrdinalIgnoreCase);
        updateCard.Visible = newer;
        if (newer)
        {
            Kit.SetText(updateText, $"The installed agent is {installed}; this executable is {AgentInfo.Version}. Update stops the service, replaces the program and starts it again; the identity and the key stay.");
        }

        ShowProblems(status?.Problems ?? []);
        ShowSections(status?.Sections ?? []);
        ShowDiagnostics(status?.Diagnostics ?? []);
        ShowGaps(status?.AuditGaps ?? []);
        SetDebug(config.LogLevel == "Debug");
        if (tabs.SelectedTab == logPage && follow.Checked)
        {
            RefreshLog(force: false);
        }
    }

    private void ShowProblems(IReadOnlyList<AgentProblem> problems)
    {
        var shown = string.Join("\n", problems.Select(p => $"{p.Component}{p.Message}{p.Count}{p.LastAt}"));
        if (shown == problemsShown)
        {
            return;
        }
        problemsShown = shown;
        problemsCard.ClearBody();
        foreach (var p in problems.Take(10))
        {
            problemsCard.Add(Kit.Label($"{p.Component}, {p.Operation}: {p.Message}  ({p.Count}×, last {p.LastAt.ToLocalTime():g})", Theme.Body,
                p.Fatal ? Theme.Fail : Theme.Warn));
        }
        problemsCard.Add(Kit.Muted("PNMT has them too, in the server's Agent tab."));
        problemsCard.Visible = problems.Count > 0;
    }

    /// <summary>What the service collected and sent, per section: when a section is missing in PNMT, this says which side it is on.</summary>
    private void ShowSections(IReadOnlyList<SectionStatus> sections)
    {
        var shown = string.Join("\n", sections.Select(x => $"{x.Name}{x.CollectedAt}{x.Bytes}{x.Errors}{x.SentAt}{x.SentCurrent}"));
        if (shown == sectionsShown)
        {
            return;
        }
        sectionsShown = shown;
        if (sectionsTable is null)
        {
            sectionsCard.Add(Kit.Muted("What the service collected, and when it last sent it to PNMT. An unchanged section is sent again only when asked for " +
                "(Collect now, PNMT's Collect now) and every 6 hours."));
            sectionsTable = sectionsCard.Add(new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 5,
                Margin = Theme.P(0, 6, 0, 0),
            });
        }
        sectionsTable.SuspendLayout();
        while (sectionsTable.Controls.Count > 0)
        {
            var control = sectionsTable.Controls[0];
            sectionsTable.Controls.RemoveAt(0);
            control.Dispose();
        }
        sectionsTable.RowStyles.Clear();
        sectionsTable.RowCount = 0;
        void Row(Color color, params string[] cells)
        {
            var row = sectionsTable.RowCount++;
            sectionsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (var i = 0; i < cells.Length; i++)
            {
                var label = Kit.Label(cells[i], row == 0 ? Theme.Strong : Theme.Body, row == 0 ? Theme.Muted : i == 0 ? Theme.Text : color);
                label.Margin = Theme.P(0, 2, 18, 2);
                sectionsTable.Controls.Add(label, i, row);
            }
        }
        Row(Theme.Muted, "Section", "Collected", "Size", "Errors", "Sent to PNMT");
        foreach (var x in sections)
        {
            var sent = x.SentAt is not { } at ? "not yet" : $"{at.ToLocalTime():T}{(x.SentCurrent ? "" : ", an older version")}";
            Row(x.CollectedAt is null || x.SentAt is null ? Theme.Warn : Theme.Text,
                x.Name,
                x.CollectedAt is { } c ? c.ToLocalTime().ToString("T") : "not yet",
                x.Bytes is { } b ? b >= 1024 ? $"{b / 1024.0:0.#} KB" : $"{b} B" : "–",
                x.Errors == 0 ? "–" : x.Errors.ToString(),
                sent);
        }
        sectionsTable.ResumeLayout();
        sectionsCard.Visible = sections.Count > 0;
    }

    private void ShowDiagnostics(IReadOnlyList<DiagnosticsStatus> diagnostics)
    {
        var shown = string.Join("\n", diagnostics.Select(d => $"{d.SetName}{d.Mode}{d.IntervalMinutes}{d.Enabled}{d.LastRunAt}{d.LastOutcome}{d.LastMessage}"));
        if (shown == diagnosticsShown)
        {
            return;
        }
        diagnosticsShown = shown;
        diagnosticsCard.ClearBody();
        foreach (var d in diagnostics)
        {
            var pill = Kit.Pill();
            Kit.SetPill(pill, d.LastOutcome ?? "no run yet", d.LastOutcome switch { "pass" => Tone.Ok, "fail" => Tone.Fail, _ => Tone.Neutral });
            var schedule = d.Mode == "Scheduled" ? $"every {d.IntervalMinutes} min" : "by hand";
            var last = d.LastRunAt is { } at ? $"; last run {at.ToLocalTime():g}" : "";
            diagnosticsCard.Add(Kit.Pair(pill, Kit.Label($"{d.SetName}: {schedule}{(d.Enabled ? "" : " (paused)")}{last}{(d.LastMessage is { } m ? $" · {m}" : "")}")));
        }
        diagnosticsCard.Visible = diagnostics.Count > 0;
    }

    private void ShowGaps(IReadOnlyList<string> gaps)
    {
        var shown = string.Join("\n", gaps);
        if (shown == gapsShown)
        {
            return;
        }
        gapsShown = shown;
        auditCard.ClearBody();
        auditCard.Add(Kit.Muted("Logins can't be seen completely. The agent changes nothing itself: run these in an administrator's PowerShell."));
        foreach (var gap in gaps)
        {
            var command = Kit.Copyable(gap);
            command.BackColor = Theme.Page;
            var copy = Kit.Button("Copy", ButtonKind.Secondary, (_, _) => Clipboard.SetText(gap));
            var line = Kit.Pair(command, copy);
            // The command stretches, the button stays: swap the columns' sizing.
            line.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
            line.ColumnStyles[1] = new ColumnStyle(SizeType.AutoSize);
            command.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            copy.Anchor = AnchorStyles.Right;
            auditCard.Add(line);
        }
        auditCard.Visible = gaps.Count > 0;
    }

    private void CollectNow()
    {
        try
        {
            Directory.CreateDirectory(AgentInfo.StateFolder);
            File.WriteAllText(AgentInfo.CollectNowFile, DateTimeOffset.UtcNow.ToString("O"));
            Kit.SetText(actionNote, $"{DateTime.Now:T}: asked the service to collect every section; it sends them within a few seconds.");
        }
        catch (Exception e)
        {
            Error("Collect now", e);
        }
    }

    private void OpenLogFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AgentInfo.LogFolder}\"") { UseShellExecute = true });

    private async void Uninstall()
    {
        var go = new TaskDialogButton("Uninstall");
        var cancel = TaskDialogButton.Cancel;
        var deleteLogs = new TaskDialogVerificationCheckBox("Also delete the log files");
        var page = new TaskDialogPage
        {
            Caption = "PNMT Sensor",
            Heading = "Uninstall PNMT Sensor?",
            Text = "Stops and deletes the service and the scheduled tasks of its scripts, and deletes this server's keys, its stored accounts, " +
                "its scripts and the agent's data. " +
                "PNMT keeps the server and its history, marked uninstalled. The program goes at the next restart.",
            Icon = TaskDialogIcon.Warning,
            Verification = deleteLogs,
        };
        page.Buttons.Add(go);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;
        if (TaskDialog.ShowDialog(this, page) != go)
        {
            return;
        }
        UseWaitCursor = true;
        try
        {
            var done = await Task.Run(() => Uninstaller.UninstallAsync(deleteLogs.Checked, CancellationToken.None));
            Program.WriteEventLog("PNMT Sensor uninstalled.", EventLogEntryType.Information);
            logger.LogInformation("Uninstalled: {Done}", string.Join(" ", done));
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "PNMT Sensor",
                Heading = "Uninstalled",
                Text = string.Join("\n", done),
                Icon = TaskDialogIcon.Information,
            });
            Uninstalled?.Invoke();
        }
        catch (Exception e)
        {
            Error("Uninstall", e);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    // --- Log -----------------------------------------------------------------------------------------------------

    private Control BuildLog()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Theme.P(16), BackColor = Theme.Page };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        follow = new CheckBox { Text = "Follow", Checked = true, AutoSize = true, Margin = Theme.P(8, 10, 12, 4) };
        debug = new CheckBox { Text = "Debug logging", AutoSize = true, Margin = Theme.P(0, 10, 0, 4) };
        debug.CheckedChanged += (_, _) => SaveDebug();
        table.Controls.Add(Kit.Row(
            Kit.Button("Refresh", ButtonKind.Secondary, (_, _) => RefreshLog(force: true)),
            Kit.Button("Open log folder", ButtonKind.Secondary, (_, _) => OpenLogFolder()),
            follow, debug), 0, 0);
        logPath = Kit.Muted("");
        table.Controls.Add(logPath, 0, 1);
        logBox = Kit.Log();
        table.Controls.Add(logBox, 0, 2);
        return table;
    }

    /// <summary>The service's newest log file, its last 1,000 lines; only re-read when the file grew.</summary>
    private void RefreshLog(bool force)
    {
        var file = new DirectoryInfo(AgentInfo.LogFolder).Exists
            ? new DirectoryInfo(AgentInfo.LogFolder).GetFiles("pnmt-sensor-????-??-??.log").MaxBy(f => f.Name)
            : null;
        if (file is null)
        {
            Kit.SetText(logBox, "No log file yet.");
            return;
        }
        if (!force && logShown == (file.Name, file.Length))
        {
            return;
        }
        logShown = (file.Name, file.Length);
        var lines = DailyFileLoggerProvider.TailOf(AgentInfo.LogFolder, "pnmt-sensor", 1000);
        logBox.Text = string.Join("\r\n", lines);
        if (follow.Checked)
        {
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }
        Kit.SetText(logPath, $"{file.FullName} · the last {lines.Count} lines · 10 days of files are kept");
    }

    private void SetDebug(bool on)
    {
        if (debug.Checked != on)
        {
            settingDebug = true;
            debug.Checked = on;
            settingDebug = false;
        }
    }

    /// <summary>Debug logging, in agent.json: the service takes it within 10 seconds, without a restart.</summary>
    private void SaveDebug()
    {
        if (settingDebug)
        {
            return;
        }
        try
        {
            Config = Config with { LogLevel = debug.Checked ? "Debug" : null };
            AgentFiles.WriteConfig(Config);
            logger.LogInformation("Debug logging {State}", debug.Checked ? "on" : "off");
        }
        catch (Exception e)
        {
            Error("Debug logging", e);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------------------

    /// <summary>A slow action (the service manager waits) off the UI thread, with the button disabled meanwhile.</summary>
    private async void Run(string what, Action action, Control button, Action? done = null)
    {
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await Task.Run(action);
            logger.LogInformation("{What}", what);
            done?.Invoke();
        }
        catch (Exception e)
        {
            Error(what, e);
        }
        finally
        {
            UseWaitCursor = false;
            button.Enabled = true;
            Tick(Config);
        }
    }

    private void Error(string what, Exception e)
    {
        logger.LogError(e, "{What} failed", what);
        TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "PNMT Sensor",
            Heading = $"{what} failed",
            Text = e.Message,
            Icon = TaskDialogIcon.Error,
        });
    }
}
