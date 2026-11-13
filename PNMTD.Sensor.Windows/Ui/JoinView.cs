using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Join;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>
/// Joining (docs/windows-sensor-plan.md §3.3, §10.4): PNMT's URL, the token and an optional name, Join, the steps with ticks, and
/// a log that fills the rest of the window. Plain http:// is allowed after a confirmation that shows PNMT's key to compare.
/// </summary>
internal sealed class JoinView : UserControl
{
    private static readonly string[] Steps = ["Checking PNMT", "Creating the key", "Registering", "Installing the service", "Waiting for the connection"];

    private readonly ILogger logger;
    private readonly TextBox url;
    private readonly TextBox token;
    private readonly TextBox name;
    private readonly TextBox log;
    private readonly Button join;
    private readonly Label[] stepLabels;
    private int current = -1;

    public JoinView(AgentConfigFile? rejoin, AgentOs os, ILogger logger)
    {
        this.logger = logger;
        IsRejoin = rejoin is not null;
        Dock = DockStyle.Fill;
        BackColor = Theme.Page;
        Padding = Theme.P(16);

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = Theme.P(22, 16, 22, 18), ColumnCount = 2 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Paint += (_, e) => Kit.Border(card, e);
        var row = 0;
        void Span(Control control, SizeType size = SizeType.AutoSize)
        {
            card.RowStyles.Add(new RowStyle(size, size == SizeType.Percent ? 100 : 0));
            card.Controls.Add(control, 0, row);
            card.SetColumnSpan(control, 2);
            row++;
        }
        void Field(string label, Control control)
        {
            var key = Kit.Label(label, Theme.Strong);
            key.Anchor = AnchorStyles.Left;
            key.Margin = Theme.P(0, 4, 16, 4);
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(key, 0, row);
            card.Controls.Add(control, 1, row);
            row++;
        }

        Span(Kit.Heading(IsRejoin ? "Re-join PNMT" : "Join PNMT"));
        Span(Kit.Muted(IsRejoin
            ? "A re-join token, from the server's Agent tab in PNMT, replaces this server's key and keeps its history. A token from Add Windows server creates a new server instead."
            : "In PNMT, an Admin chooses Add Windows server… in the host's menu and gets a token. Enter PNMT's URL and the token: the agent creates its key, registers, installs the service PNMTSensor and connects."));
        if (!os.Supported)
        {
            Span(Kit.Banner($"{os.Caption} {os.Build} is not on the list of supported versions: the agent runs anyway, and PNMT shows it.", Tone.Warn));
        }

        url = Kit.Input("https://pnmt.example.com");
        url.Text = rejoin?.Url.TrimEnd('/') ?? "";
        token = Kit.Input("xxxx-xxxx");
        token.Font = Theme.Mono;
        token.CharacterCasing = CharacterCasing.Lower;
        token.MaxLength = 9;
        token.Anchor = AnchorStyles.Left;
        token.Width = Theme.S(150);
        name = Kit.Input("Windows (optional)");
        Field("PNMT's URL", url);
        Field("Token", token);
        if (!IsRejoin)
        {
            Field("Name", name);
        }

        join = Kit.Button(IsRejoin ? "Re-join" : "Join", ButtonKind.Primary, async (_, _) => await JoinAsync());
        var buttons = Kit.Row(join);
        if (IsRejoin)
        {
            buttons.Controls.Add(Kit.Button("Cancel", ButtonKind.Secondary, (_, _) => Cancelled?.Invoke()));
        }
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(buttons, 1, row++);

        stepLabels = Steps.Select(s => new Label { AutoSize = true, Font = Theme.Body, Margin = Theme.P(0, 8, 18, 4), UseMnemonic = false }).ToArray();
        var steps = Kit.Row(stepLabels);
        steps.Margin = Theme.P(0, 10, 0, 0);
        Span(steps);
        ShowSteps();

        log = Kit.Log();
        Span(log, SizeType.Percent);
        card.RowCount = row;
        Controls.Add(card);

        AcceptButtonFor(join);
        Write($"PNMT Sensor {AgentInfo.Version} ({AgentInfo.Architecture}) on {os.Caption} {os.DisplayVersion} ({os.Build})");
    }

    public bool IsRejoin { get; }

    /// <summary>A join runs: the window doesn't switch views under it.</summary>
    public bool Busy { get; private set; }

    public event Action? Joined;

    public event Action? Cancelled;

    private void AcceptButtonFor(Button button) => ParentChanged += (_, _) =>
    {
        if (FindForm() is { } form)
        {
            form.AcceptButton = button;
        }
    };

    private async Task JoinAsync()
    {
        Busy = true;
        foreach (var control in new Control[] { url, token, name, join })
        {
            control.Enabled = false;
        }
        current = -1;
        ShowSteps();
        var progress = new Progress<string>(OnStep);
        try
        {
            var result = await Task.Run(() => Joiner.JoinAsync(url.Text, token.Text, name.Text, progress, ConfirmHttpAsync, CancellationToken.None));
            Write($"Joined as {result.Grant.HostName} / {result.Grant.SensorName}.");
            Write($"This server's key: {result.AgentFingerprint}");
            Write($"PNMT's key:        {result.PnmtdFingerprint}");
            logger.LogInformation("Joined as {Host} / {Sensor}; key {Key}", result.Grant.HostName, result.Grant.SensorName, result.AgentFingerprint);
            Program.WriteEventLog($"Joined PNMT as {result.Grant.HostName} / {result.Grant.SensorName} (key {result.AgentFingerprint}).", System.Diagnostics.EventLogEntryType.Information);
            var page = new TaskDialogPage
            {
                Caption = "PNMT Sensor",
                Heading = $"Joined as {result.Grant.HostName} / {result.Grant.SensorName}",
                Text = $"This server's key: {result.AgentFingerprint}\nPNMT's key: {result.PnmtdFingerprint}\n\nPNMT's agent log shows the same keys." +
                    (result.Connected ? "" : "\n\nThe service is installed but not connected yet: its log tells why."),
                Icon = result.Connected ? TaskDialogIcon.ShieldSuccessGreenBar : TaskDialogIcon.Warning,
            };
            TaskDialog.ShowDialog(this, page);
            Busy = false;
            Joined?.Invoke();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Join failed: {Message}", e.Message);
            if (current >= 0)
            {
                Mark(current, "✗", Theme.Fail, Theme.Strong);
            }
            Write($"Not joined: {e.Message}");
            Busy = false;
            foreach (var control in new Control[] { url, token, name, join })
            {
                control.Enabled = true;
            }
        }
    }

    private void OnStep(string step)
    {
        Write(step.EndsWith('.') ? step : step + "…");
        var index = Array.IndexOf(Steps, step);
        if (index >= 0)
        {
            current = index;
            ShowSteps();
        }
        else if (step == "Connected")
        {
            current = Steps.Length;
            ShowSteps();
        }
    }

    private void ShowSteps()
    {
        for (var i = 0; i < Steps.Length; i++)
        {
            if (i < current)
            {
                Mark(i, "✓", Theme.Ok, Theme.Body);
            }
            else if (i == current)
            {
                Mark(i, "●", Theme.Brand, Theme.Strong);
            }
            else
            {
                Mark(i, "○", Theme.Muted, Theme.Body);
            }
        }
    }

    private void Mark(int index, string symbol, Color color, Font font)
    {
        stepLabels[index].Text = $"{symbol}  {Steps[index]}";
        stepLabels[index].ForeColor = color;
        stepLabels[index].Font = font;
    }

    private void Write(string line) => log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\r\n");

    /// <summary>
    /// Plain HTTP (asked on the UI thread): anyone on the way can read the token, the reports and the logins, and at the join could
    /// pose as PNMT. Continuing needs a tick that PNMT's key, as this URL answers, matches the one PNMT's Add server dialog shows.
    /// </summary>
    private Task<bool> ConfirmHttpAsync(Uri address, string fingerprint) =>
        Task.FromResult((bool)Invoke(new Func<bool>(() => ConfirmHttp(address, fingerprint))));

    private bool ConfirmHttp(Uri address, string fingerprint)
    {
        var go = new TaskDialogButton("Join over HTTP") { Enabled = false };
        var cancel = TaskDialogButton.Cancel;
        var check = new TaskDialogVerificationCheckBox("PNMT's key above matches the one in PNMT's Add server dialog");
        check.CheckedChanged += (_, _) => go.Enabled = check.Checked;
        var page = new TaskDialogPage
        {
            Caption = "PNMT Sensor",
            Heading = "PNMT is reached over plain HTTP",
            Text = $"{address} is not encrypted. Anyone on the way can read the join token, the reports and the logins, and at the join could pose as PNMT.\n\n" +
                $"PNMT's key, as this URL answers:\n{fingerprint}\n\nContinue only on a network you trust, and only if the key matches.",
            Icon = TaskDialogIcon.Warning,
            Verification = check,
            AllowCancel = true,
        };
        page.Buttons.Add(go);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;
        var confirmed = TaskDialog.ShowDialog(this, page) == go;
        Write(confirmed ? $"Plain HTTP confirmed; PNMT's key {fingerprint}." : "Plain HTTP not confirmed.");
        logger.LogInformation("Plain HTTP to {Url} {State}; PNMT's key {Key}", address, confirmed ? "confirmed" : "declined", fingerprint);
        return confirmed;
    }
}
