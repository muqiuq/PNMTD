using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
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
        Application.Run(new MainForm(logger));
        return 0;
    }
}

/// <summary>
/// The service's window (docs/windows-sensor-plan.md §10.4): a header with the logo and the agent's state, and below it joining
/// (not joined, or Re-join…) or the joined agent's tabs. One timer every 2 s; nothing is rebuilt while you type.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ILogger logger;
    private readonly AgentOs os = WindowsVersion.Read();
    private readonly Panel body = new() { Dock = DockStyle.Fill, BackColor = Theme.Page };
    private readonly Label headerPill;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private Control? view;

    public MainForm(ILogger logger)
    {
        this.logger = logger;
        Theme.Scale = DeviceDpi / 96f;
        Text = "PNMT Sensor";
        Icon = Assets.Icon;
        Font = Theme.Body;
        BackColor = Theme.Page;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(880), Theme.S(720));
        MinimumSize = new Size(Theme.S(640), Theme.S(520));

        headerPill = Kit.Pill();
        headerPill.Anchor = AnchorStyles.Right;
        // Docked from the last added: the header on top, the brand stripe under it, the view filling the rest.
        Controls.Add(body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = Math.Max(2, Theme.S(3)), BackColor = Theme.Brand });
        Controls.Add(Header());

        timer.Tick += (_, _) => Tick();
        timer.Start();
        Show(AgentFiles.ReadConfig());
        Tick();
    }

    private Control Header()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Padding = Theme.P(18, 12, 18, 12),
            ColumnCount = 3,
            RowCount = 1,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var logo = new PictureBox
        {
            Image = Assets.Logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(Theme.S(42), Theme.S(42)),
            Margin = Theme.P(0, 0, 14, 0),
            Anchor = AnchorStyles.Left,
        };
        var titles = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        titles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = Kit.Label("PNMT Sensor", Theme.Title);
        title.Margin = Padding.Empty;
        var subtitle = Kit.Label($"{AgentInfo.Version} ({AgentInfo.Architecture}) · {Environment.MachineName} · {os.Caption} {os.DisplayVersion}", Theme.Small, Theme.Muted);
        subtitle.Margin = Padding.Empty;
        titles.Controls.Add(title, 0, 0);
        titles.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(headerPill, 2, 0);
        return header;
    }

    private void Show(AgentConfigFile? config, bool rejoin = false)
    {
        Control next;
        if (config is null || rejoin)
        {
            var join = new JoinView(rejoin ? config : null, os, logger);
            join.Joined += () => Show(AgentFiles.ReadConfig());
            join.Cancelled += () => Show(AgentFiles.ReadConfig());
            next = join;
        }
        else
        {
            var status = new StatusView(config, os, logger);
            status.RejoinRequested += () => Show(AgentFiles.ReadConfig(), rejoin: true);
            status.Uninstalled += Close;
            next = status;
        }
        body.SuspendLayout();
        var old = view;
        body.Controls.Add(next);
        if (old is not null)
        {
            body.Controls.Remove(old);
            old.Dispose();
        }
        view = next;
        body.ResumeLayout();
        if (next is not JoinView)
        {
            AcceptButton = null;
        }
    }

    private void Tick()
    {
        try
        {
            var config = AgentFiles.ReadConfig();
            switch (view)
            {
                case JoinView { Busy: true }:
                    break;
                case JoinView { IsRejoin: false } when config is not null:
                    Show(config);
                    break;
                case StatusView when config is null:
                    Show(null);
                    break;
                case StatusView status when config!.JoinedAt != status.Config.JoinedAt:
                    Show(config);
                    break;
                case StatusView status:
                    status.Tick(config!);
                    break;
            }
            var (text, tone, _) = AgentState.Of(config, AgentFiles.ReadStatus(), config is null ? null : ServiceControl.Status());
            Kit.SetPill(headerPill, text, tone);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Refreshing the window failed");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The agent's state in a word, a colour and a sentence: the header's pill and the Connection card.</summary>
internal static class AgentState
{
    public static (string Text, Tone Tone, string Detail) Of(AgentConfigFile? config, AgentStatusFile? status, ServiceControllerStatus? service)
    {
        if (config is null)
        {
            return ("Not joined", Tone.Neutral, "Join PNMT with a token from Add Windows server.");
        }
        if (service is null)
        {
            return ("Not installed", Tone.Fail, "The service is not installed: re-join to install it.");
        }
        if (service != ServiceControllerStatus.Running)
        {
            return ($"Service {Words(service)}", Tone.Fail, "The service doesn't run: start it.");
        }
        if (status is null || DateTimeOffset.UtcNow - status.UpdatedAt > TimeSpan.FromMinutes(1))
        {
            return ("No status", Tone.Warn, "The service has not written its status for a minute; its log may tell why.");
        }
        if (status.Connected)
        {
            return ("Connected", Tone.Ok, $"Connected to PNMT since {status.ConnectedSince?.ToLocalTime():g}; heartbeats every minute.");
        }
        if (status.State?.StartsWith("revoked", StringComparison.Ordinal) == true)
        {
            return ("Revoked", Tone.Fail, $"PNMT {status.State}. Re-join with a new token to report again.");
        }
        return ("Offline", Tone.Warn, $"{Capital(status.State ?? "not connected")}. The service retries by itself.");
    }

    public static string Words(ServiceControllerStatus? service) => service switch
    {
        null => "not installed",
        ServiceControllerStatus.Running => "running",
        ServiceControllerStatus.Stopped => "stopped",
        ServiceControllerStatus.StartPending => "starting",
        ServiceControllerStatus.StopPending => "stopping",
        ServiceControllerStatus.Paused => "paused",
        var other => other.ToString()!.ToLowerInvariant(),
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
