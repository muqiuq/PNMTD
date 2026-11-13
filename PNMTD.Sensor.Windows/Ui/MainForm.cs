using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Join;
using PNMTD.Sensor.Windows.Logging;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Ui;

internal static class WindowApp
{
    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(AgentInfo.LogFolder);
        using var logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, "pnmt-sensor-ui");
        var logger = logs.CreateLogger("window");
        Application.ThreadException += (_, e) =>
        {
            logger.LogError(e.Exception, "The window failed: {Message}", e.Exception.Message);
            MessageBox.Show(e.Exception.Message, "PNMT Sensor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        logger.LogInformation("The window opened ({Version}).", AgentInfo.Version);
        Application.Run(new MainForm(logs, logger));
        return 0;
    }
}

/// <summary>
/// The service's window (docs/windows-sensor-plan.md §10.4). Not joined: the URL, the token and a name, Join, the steps
/// and both fingerprints. Joined: the service with Start, Stop and Restart; the connection and the last problems; the
/// agent and both keys; the diagnostics loaded; the audit policy's gaps; Collect now, Open log folder, Debug logging,
/// Re-join… and Uninstall…; and Update when this executable is newer than the installed one.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly DailyFileLoggerProvider logs;
    private readonly ILogger logger;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private readonly Panel body = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
    private bool joining;

    public MainForm(DailyFileLoggerProvider logs, ILogger logger)
    {
        this.logs = logs;
        this.logger = logger;
        Text = $"PNMT Sensor {AgentInfo.Version}";
        Width = 760;
        Height = 720;
        MinimumSize = new Size(560, 480);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(body);
        refresh.Tick += (_, _) => Render();
        refresh.Start();
        Render();
    }

    private void Render()
    {
        if (joining)
        {
            return;
        }
        var config = AgentFiles.ReadConfig();
        var key = config is null ? "join" : "status";
        var scroll = body.AutoScrollPosition;
        body.SuspendLayout();
        body.Controls.Clear();
        var stack = Stack();
        stack.Controls.Add(Title($"PNMT Sensor {AgentInfo.Version} ({AgentInfo.Architecture})"));
        var os = WindowsVersion.Read();
        stack.Controls.Add(Line($"{os.Caption} {os.DisplayVersion} ({os.Build})"));
        if (!os.Supported)
        {
            stack.Controls.Add(Line("This Windows version is not on the supported list: the agent runs anyway, and PNMT shows it.", Color.DarkOrange));
        }
        if (key == "join")
        {
            JoinView(stack, null);
        }
        else
        {
            StatusView(stack, config!);
        }
        body.Controls.Add(stack);
        body.ResumeLayout();
        body.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    // --- not joined -------------------------------------------------------------------------------------------

    private void JoinView(FlowLayoutPanel stack, AgentConfigFile? rejoin)
    {
        refresh.Stop();
        stack.Controls.Add(Heading(rejoin is null ? "Join PNMT" : "Re-join PNMT"));
        stack.Controls.Add(Line("In PNMT, an Admin chooses Add Windows server on the host and gets a token. Enter PNMT's URL and the token here."));
        var url = Input("PNMT's URL", rejoin?.Url ?? "https://", stack);
        var token = Input("Token (xxxx-xxxx)", "", stack);
        var name = Input("Name (optional)", "", stack);
        var steps = new ListBox { Width = 640, Height = 120 };
        var fingerprints = Line("");
        var join = new Button { Text = "Join", AutoSize = true };
        join.Click += async (_, _) =>
        {
            joining = true;
            join.Enabled = false;
            steps.Items.Clear();
            var progress = new Progress<string>(s => steps.Items.Add($"✓ {s}"));
            try
            {
                var result = await Task.Run(() => Joiner.JoinAsync(url.Text, token.Text, name.Text, progress, CancellationToken.None));
                fingerprints.Text = $"Joined as {result.Grant.HostName} / {result.Grant.SensorName}.\r\nThis server's key: {result.AgentFingerprint}\r\nPNMT's key: {result.PnmtdFingerprint}\r\nCompare them with PNMT's agent log.";
                logger.LogInformation("Joined as {Host} / {Sensor}; key {Key}", result.Grant.HostName, result.Grant.SensorName, result.AgentFingerprint);
                Program.WriteEventLog($"Joined PNMT as {result.Grant.HostName} / {result.Grant.SensorName}.", System.Diagnostics.EventLogEntryType.Information);
                MessageBox.Show(this, fingerprints.Text, "Joined", MessageBoxButtons.OK, MessageBoxIcon.Information);
                joining = false;
                refresh.Start();
                Render();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Join failed: {Message}", e.Message);
                steps.Items.Add($"✗ {e.Message}");
                join.Enabled = true;
                joining = false;
            }
        };
        stack.Controls.Add(join);
        stack.Controls.Add(steps);
        stack.Controls.Add(fingerprints);
        if (rejoin is not null)
        {
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            cancel.Click += (_, _) =>
            {
                refresh.Start();
                Render();
            };
            stack.Controls.Add(cancel);
        }
    }

    // --- joined -------------------------------------------------------------------------------------------------

    private void StatusView(FlowLayoutPanel stack, AgentConfigFile config)
    {
        var status = AgentFiles.ReadStatus();
        var service = ServiceControl.Status();

        stack.Controls.Add(Heading("The service"));
        stack.Controls.Add(Line($"{AgentInfo.DisplayName}: {service?.ToString() ?? "not installed"}", service == ServiceControllerStatus.Running ? Color.DarkGreen : Color.DarkRed));
        stack.Controls.Add(Buttons(
            ("Start", () => Do(() => ServiceControl.Start())),
            ("Stop", () => Do(ServiceControl.Stop)),
            ("Restart", () => Do(ServiceControl.Restart))));
        if (ServiceControl.InstalledVersion() is { } installed && Version.TryParse(AgentInfo.Version.Split('-')[0], out var mine) && mine > installed)
        {
            stack.Controls.Add(Line($"The installed agent is {installed}; this one is {mine}.", Color.DarkOrange));
            stack.Controls.Add(Buttons(("Update", () => Do(UpdateInstalled))));
        }

        stack.Controls.Add(Heading("The connection"));
        if (status is null || DateTimeOffset.UtcNow - status.UpdatedAt > TimeSpan.FromMinutes(1))
        {
            stack.Controls.Add(Line("The service has not written its status for a minute: is it running?", Color.DarkOrange));
        }
        else
        {
            stack.Controls.Add(Line(status.Connected
                ? $"Connected to {config.Url} since {status.ConnectedSince?.ToLocalTime():g}; last heartbeat {status.LastHeartbeatAt?.ToLocalTime():T}."
                : $"Not connected: {status.State}", status.Connected ? Color.DarkGreen : Color.DarkRed));
            foreach (var p in status.Problems.Take(5))
            {
                stack.Controls.Add(Line($"Problem: {p.Component}, {p.Operation}: {p.Message} ({p.Count}×, last {p.LastAt.ToLocalTime():g})", Color.DarkOrange));
            }
        }

        stack.Controls.Add(Heading("The agent"));
        stack.Controls.Add(Line($"PNMT: {config.HostName} / {config.SensorName}\r\nAgent id: {config.AgentId}\r\nThis server's key: {config.AgentKeyFingerprint}\r\nPNMT's key: {config.PnmtdFingerprint}"));

        if (status is { Diagnostics.Count: > 0 })
        {
            stack.Controls.Add(Heading("Diagnostics loaded"));
            foreach (var d in status.Diagnostics)
            {
                var schedule = d.Mode == "Scheduled" ? $"every {d.IntervalMinutes} min" : "by hand";
                stack.Controls.Add(Line($"{d.SetName}: {schedule}{(d.Enabled ? "" : " (off)")}; last run {d.LastRunAt?.ToLocalTime():g}: {d.LastOutcome ?? "–"} {d.LastMessage}"));
            }
        }
        if (status is { AuditGaps.Count: > 0 })
        {
            stack.Controls.Add(Heading("The audit policy's gaps"));
            stack.Controls.Add(Line("Logins can't be seen completely. The agent changes nothing; run these as an administrator:"));
            foreach (var gap in status.AuditGaps)
            {
                var box = new TextBox { Text = gap, ReadOnly = true, Width = 680, Font = new Font("Consolas", 9f) };
                stack.Controls.Add(box);
            }
        }

        stack.Controls.Add(Heading("Actions"));
        var debug = new CheckBox { Text = "Debug logging", AutoSize = true, Checked = config.LogLevel == "Debug" };
        debug.CheckedChanged += (_, _) => Do(() => AgentFiles.WriteConfig(config with { LogLevel = debug.Checked ? "Debug" : null }));
        stack.Controls.Add(Buttons(
            ("Collect now", () => Do(() => File.WriteAllText(AgentInfo.CollectNowFile, DateTimeOffset.UtcNow.ToString("O")))),
            ("Open log folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AgentInfo.LogFolder}\"") { UseShellExecute = true })),
            ("Re-join…", () => RejoinView(config)),
            ("Uninstall…", Uninstall)));
        stack.Controls.Add(debug);
        stack.Controls.Add(Line($"Log: {logs.CurrentFile.Replace("pnmt-sensor-ui", "pnmt-sensor")}", Color.Gray));
    }

    private void RejoinView(AgentConfigFile config)
    {
        body.Controls.Clear();
        var stack = Stack();
        stack.Controls.Add(Title("PNMT Sensor"));
        stack.Controls.Add(Line("A re-join token (from the server's Agent tab in PNMT) replaces this server's key and keeps its history; a new token creates a new server."));
        JoinView(stack, config);
        body.Controls.Add(stack);
    }

    private void UpdateInstalled()
    {
        ServiceControl.Stop();
        ServiceControl.InstallProgram();
        ServiceControl.Start();
        logger.LogInformation("Updated the installed agent to {Version}.", AgentInfo.Version);
    }

    private void Uninstall()
    {
        var answer = MessageBox.Show(this,
            "Stop and delete the service, delete this server's key and the agent's data?\r\nPNMT keeps the server and its history.\r\n\r\nYes: keep the logs. No: delete the logs too.",
            "Uninstall PNMT Sensor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (answer == DialogResult.Cancel)
        {
            return;
        }
        try
        {
            var done = Uninstaller.UninstallAsync(answer == DialogResult.No, CancellationToken.None).GetAwaiter().GetResult();
            Program.WriteEventLog("PNMT Sensor uninstalled.", System.Diagnostics.EventLogEntryType.Information);
            MessageBox.Show(this, string.Join("\r\n", done), "Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Uninstall failed");
            MessageBox.Show(this, e.Message, "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Do(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Message}", e.Message);
            MessageBox.Show(this, e.Message, "PNMT Sensor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Render();
    }

    // --- layout helpers ------------------------------------------------------------------------------------------

    private static FlowLayoutPanel Stack() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
    };

    private static Label Title(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, 8) };

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 14, 0, 4) };

    private static Label Line(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(680, 0),
        ForeColor = color ?? SystemColors.ControlText,
        Margin = new Padding(0, 2, 0, 2),
    };

    private static TextBox Input(string label, string value, FlowLayoutPanel stack)
    {
        stack.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
        var box = new TextBox { Text = value, Width = 480 };
        stack.Controls.Add(box);
        return box;
    }

    private static FlowLayoutPanel Buttons(params (string Text, Action Click)[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(700, 0) };
        foreach (var (text, click) in buttons)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => click();
            row.Controls.Add(button);
        }
        return row;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
        }
        base.Dispose(disposing);
    }
}
