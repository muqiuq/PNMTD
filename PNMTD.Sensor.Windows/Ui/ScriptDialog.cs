using System.Globalization;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;
using PNMTD.Sensor.Windows.Scripts;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>
/// Registers a script, or changes a registered one (docs/windows-sensor-plan.md §7.2, §7.4): its name, the account it runs as (this
/// service, an account whose password is stored here, a gMSA), the password when it is needed (checked with Windows), and
/// optionally a schedule, as a Windows scheduled task. Saving copies the file and registers the task.
/// </summary>
internal sealed class ScriptDialog : Form
{
    private readonly LocalScript? existing;
    private readonly string? source;
    private readonly TextBox name;
    private readonly RadioButton asService;
    private readonly RadioButton asStored;
    private readonly RadioButton asGmsa;
    private readonly TextBox account;
    private readonly TextBox password;
    private readonly Label passwordHint;
    private readonly CheckBox scheduled;
    private readonly ComboBox scheduleKind;
    private readonly ComboBox day;
    private readonly DateTimePicker time;
    private readonly NumericUpDown minutes;
    private readonly Label minutesLabel;
    private readonly Label error;
    private readonly Button save;

    private static readonly (ScheduleKind Kind, string Label)[] Kinds =
        [(ScheduleKind.Daily, "Daily"), (ScheduleKind.Weekly, "Weekly"), (ScheduleKind.Interval, "Every N minutes"), (ScheduleKind.AtStartup, "At startup")];

    /// <summary>A new script from <paramref name="file"/>, or (with <paramref name="script"/>) a registered one's account and schedule.</summary>
    public ScriptDialog(string? file, string hash, string suggestedName, LocalScript? script)
    {
        existing = script;
        source = file;
        Text = script is null ? "Add a script" : $"Script {script.Name}";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        Icon = Assets.Icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Theme.P(20, 16, 20, 16);

        var wide = Theme.S(460);
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var row = 0;
        void Field(string label, Control control)
        {
            var key = Kit.Label(label, Theme.Strong);
            key.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            key.Margin = Theme.P(0, 7, 16, 4);
            table.Controls.Add(key, 0, row);
            table.Controls.Add(control, 1, row);
            row++;
        }
        Label Note(string text, Color? color = null)
        {
            var label = Kit.Label(text, Theme.Body, color ?? Theme.Muted);
            label.Anchor = AnchorStyles.Left;
            label.MaximumSize = new Size(wide, 0);
            return label;
        }

        Field("File", Note(script?.File ?? file ?? "", Theme.Text));
        var hashBox = Kit.Copyable(hash);
        hashBox.Width = wide;
        hashBox.Anchor = AnchorStyles.Left;
        Field("SHA-256", hashBox);
        name = Kit.Input("A name");
        name.Text = script?.Name ?? suggestedName;
        name.Width = Theme.S(300);
        name.Anchor = AnchorStyles.Left;
        name.ReadOnly = script is not null;
        Field("Name", name);
        table.Controls.Add(Note($"Copied to {Path.Combine(LocalScripts.Folder, "<name>.ps1")}: SYSTEM, Administrators and the account may read it."), 1, row++);

        var runAs = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Theme.P(0, 4, 0, 4) };
        asService = new RadioButton { Text = "This service: LocalSystem, as an administrator", AutoSize = true, Margin = Theme.P(0, 2, 0, 2) };
        asStored = new RadioButton { Text = "An account, its password stored on this server", AutoSize = true, Margin = Theme.P(0, 2, 0, 2) };
        asGmsa = new RadioButton { Text = "A group managed service account (gMSA), no password", AutoSize = true, Margin = Theme.P(0, 2, 0, 2) };
        runAs.Controls.AddRange([asService, asStored, asGmsa]);
        Field("Runs as", runAs);
        account = Kit.Input("DOMAIN\\user");
        account.Width = Theme.S(300);
        account.Anchor = AnchorStyles.Left;
        Field("Account", account);
        password = Kit.Input("");
        password.UseSystemPasswordChar = true;
        password.Width = Theme.S(300);
        password.Anchor = AnchorStyles.Left;
        Field("Password", password);
        passwordHint = Note("");
        table.Controls.Add(passwordHint, 1, row++);

        scheduled = new CheckBox { Text = "Run it on a schedule (a Windows scheduled task)", AutoSize = true, Margin = Theme.P(0, 6, 0, 2) };
        Field("Schedule", scheduled);
        scheduleKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.S(150), Margin = Theme.P(0, 2, 8, 2) };
        scheduleKind.Items.AddRange(Kinds.Select(k => (object)k.Label).ToArray());
        day = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.S(120), Margin = Theme.P(0, 2, 8, 2) };
        day.Items.AddRange(Enum.GetNames<DayOfWeek>().Cast<object>().ToArray());
        time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = Theme.S(80), Margin = Theme.P(0, 2, 8, 2) };
        minutes = new NumericUpDown { Minimum = 5, Maximum = 1440, Value = 60, Width = Theme.S(80), Margin = Theme.P(0, 2, 4, 2) };
        minutesLabel = Note("minutes");
        minutesLabel.Margin = Theme.P(0, 5, 0, 2);
        var when = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Theme.P(0, 2, 0, 2) };
        when.Controls.AddRange([scheduleKind, day, time, minutes, minutesLabel]);
        table.Controls.Add(when, 1, row++);

        error = Note("", Theme.Fail);
        error.Margin = Theme.P(0, 10, 0, 0);
        table.Controls.Add(error, 1, row++);
        save = Kit.Button(script is null ? "Add" : "Save", ButtonKind.Primary, async (_, _) => await SaveAsync());
        var cancel = Kit.Button("Cancel", ButtonKind.Secondary, (_, _) => { });
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = Kit.Row(save, cancel);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = Theme.P(0, 12, 0, 0);
        table.Controls.Add(buttons, 1, row++);
        Controls.Add(table);
        AcceptButton = save;
        CancelButton = cancel;

        // The values of the script, or the defaults: this service, daily at 02:00.
        (script?.Kind switch { RunAsKind.Stored => asStored, RunAsKind.Gmsa => asGmsa, _ => asService }).Checked = true;
        account.Text = script?.Account ?? "";
        var schedule = script?.Schedule;
        scheduled.Checked = schedule is not null;
        scheduleKind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == (schedule?.Kind ?? ScheduleKind.Daily)));
        day.SelectedItem = (schedule?.Day ?? DayOfWeek.Monday).ToString();
        time.Value = DateTime.Today.Add(TimeOnly.TryParseExact(schedule?.At, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToTimeSpan() : TimeSpan.FromHours(2));
        minutes.Value = Math.Clamp(schedule?.IntervalMinutes ?? 60, 5, 1440);
        foreach (var control in new Control[] { asService, asStored, asGmsa, scheduled, scheduleKind, account })
        {
            switch (control)
            {
                case RadioButton radio:
                    radio.CheckedChanged += (_, _) => UpdateFields();
                    break;
                case CheckBox check:
                    check.CheckedChanged += (_, _) => UpdateFields();
                    break;
                case ComboBox combo:
                    combo.SelectedIndexChanged += (_, _) => UpdateFields();
                    break;
                default:
                    control.TextChanged += (_, _) => UpdateFields();
                    break;
            }
        }
        UpdateFields();
    }

    /// <summary>The registered script, once saved.</summary>
    public LocalScript? Saved { get; private set; }

    private RunAsKind Kind => asStored.Checked ? RunAsKind.Stored : asGmsa.Checked ? RunAsKind.Gmsa : RunAsKind.Service;

    private ScheduleKind SelectedSchedule => Kinds[Math.Max(0, scheduleKind.SelectedIndex)].Kind;

    private void UpdateFields()
    {
        account.Enabled = Kind != RunAsKind.Service;
        account.PlaceholderText = Kind == RunAsKind.Gmsa ? "DOMAIN\\svc-name$" : "DOMAIN\\user";
        password.Enabled = Kind == RunAsKind.Stored;
        var stored = Kind == RunAsKind.Stored && account.Text.Trim().Length > 0 && StoredAccounts.Password(account.Text.Trim()) is not null;
        password.PlaceholderText = stored ? "stored: leave empty to keep it" : "";
        Kit.SetText(passwordHint, Kind switch
        {
            RunAsKind.Stored => "Checked with Windows, then encrypted on this server (DPAPI). It never reaches PNMT.",
            RunAsKind.Gmsa => "Windows fetches a gMSA's password itself; the server must be allowed to use the account.",
            _ => "LocalSystem has every right on this server: only for scripts you trust.",
        });
        foreach (var control in new Control[] { scheduleKind, day, time, minutes, minutesLabel })
        {
            control.Enabled = scheduled.Checked;
        }
        day.Visible = SelectedSchedule == ScheduleKind.Weekly;
        time.Visible = SelectedSchedule is ScheduleKind.Daily or ScheduleKind.Weekly;
        minutes.Visible = minutesLabel.Visible = SelectedSchedule == ScheduleKind.Interval;
    }

    private async Task SaveAsync()
    {
        error.Text = "";
        var scriptName = name.Text.Trim();
        var kind = Kind;
        var who = kind == RunAsKind.Service ? null : account.Text.Trim();
        var secret = kind == RunAsKind.Stored ? password.Text : null;
        var problem =
            !LocalScripts.IsValidName(scriptName) ? "A name of 1 to 80 letters, digits, spaces, dots, dashes and underscores." :
            existing is null && LocalScripts.ByName(scriptName) is not null ? $"A script named {scriptName} is registered already." :
            kind != RunAsKind.Service && string.IsNullOrEmpty(who) ? "The account, as DOMAIN\\user." :
            kind == RunAsKind.Gmsa && !who!.EndsWith('$') ? "A gMSA's name ends with $." :
            kind == RunAsKind.Stored && string.IsNullOrEmpty(secret) && StoredAccounts.Password(who!) is null ? $"The password of {who}: none is stored yet." :
            kind == RunAsKind.Stored && !string.IsNullOrEmpty(secret) ? Win32.CheckLogon(who!, secret!) :
            null;
        if (problem is not null)
        {
            error.Text = problem;
            return;
        }
        LocalSchedule? schedule = !scheduled.Checked ? null : SelectedSchedule switch
        {
            ScheduleKind.Daily => new LocalSchedule(ScheduleKind.Daily, time.Value.ToString("HH:mm", CultureInfo.InvariantCulture)),
            ScheduleKind.Weekly => new LocalSchedule(ScheduleKind.Weekly, time.Value.ToString("HH:mm", CultureInfo.InvariantCulture), Enum.Parse<DayOfWeek>((string)day.SelectedItem!)),
            ScheduleKind.Interval => new LocalSchedule(ScheduleKind.Interval, IntervalMinutes: (int)minutes.Value),
            _ => new LocalSchedule(ScheduleKind.AtStartup),
        };
        save.Enabled = false;
        UseWaitCursor = true;
        try
        {
            Saved = await Task.Run(() => LocalScripts.SaveAsync(existing, source, scriptName, kind, who, secret, schedule, CancellationToken.None));
            password.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e)
        {
            error.Text = e.Message;
        }
        finally
        {
            UseWaitCursor = false;
            save.Enabled = true;
        }
    }
}
