using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Scripts;
using PNMTD.Sensor.Windows.Service;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>
/// Scripts (docs/windows-sensor-plan.md §7.2): whether PNMT may run scripts here, and the scripts registered on this server, a list
/// with the selected one's details beside it. Add script… picks a .ps1 with the open-file dialog, copies it, records its SHA-256,
/// asks for the account (and its password when needed) and a schedule; a file with a registered name replaces it after asking.
/// The list and the details resize with the window.
/// </summary>
internal sealed class ScriptsTab : UserControl
{
    private readonly ILogger logger;
    private readonly CheckBox allow;
    private readonly RadioButton onlyRegistered;
    private readonly RadioButton anyScript;
    private readonly TextBox accounts;
    private readonly Label policyNote;
    private readonly ListView list;
    private readonly TableLayoutPanel details;
    private readonly SplitContainer split;
    private IReadOnlyList<TaskInfo> tasks = [];
    private bool splitPlaced;

    public ScriptsTab(ILogger logger)
    {
        this.logger = logger;
        Dock = DockStyle.Fill;
        BackColor = Theme.Page;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = Theme.P(16), BackColor = Theme.Page };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // --- may PNMT run scripts here --------------------------------------------------------------------------
        var policy = new Card("Scripts from PNMT");
        allow = policy.Add(new CheckBox { Text = "Allow PNMT to run scripts on this server", AutoSize = true, Font = Theme.Strong, Margin = Theme.P(0, 2, 0, 6) });
        onlyRegistered = policy.Add(new RadioButton { Text = "Only the scripts registered below, each as its own account", AutoSize = true, Margin = Theme.P(18, 2, 0, 2) });
        anyScript = policy.Add(new RadioButton { Text = "Any script PNMT sends, as these accounts (one per line; service for LocalSystem; empty: any account but the service):", AutoSize = true, Margin = Theme.P(18, 2, 0, 2) });
        anyScript.MaximumSize = new Size(Theme.S(900), 0);
        accounts = policy.Add(Kit.Area(54));
        accounts.Font = Theme.Body;
        accounts.Margin = Theme.P(36, 2, 0, 6);
        policyNote = Kit.Muted("");
        policy.Add(Kit.Row(Kit.Button("Save", ButtonKind.Primary, (s, _) => SavePolicy((Control)s!)), policyNote));
        policy.Add(Kit.Muted("Allowed or not, PNMT may have the agent read a script's parameters; that never runs it."));
        EventHandler changed = (_, _) => UpdatePolicyFields();
        allow.CheckedChanged += changed;
        anyScript.CheckedChanged += changed;
        layout.Controls.Add(policy, 0, 0);

        // --- the scripts registered here: the list, and the selected one's details ------------------------------------
        var scripts = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = Theme.P(18, 14, 18, 14), ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        scripts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scripts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scripts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scripts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scripts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scripts.Paint += (_, e) => Kit.Border(scripts, e);
        scripts.Controls.Add(Kit.Heading("Scripts on this server"), 0, 0);
        scripts.Controls.Add(Kit.Muted("Each is a copy of a .ps1 file with its SHA-256 and the account it runs as, and runs by itself when it has a schedule. " +
            "PNMT lists them on this server's Scripts tab, and runs them from there when allowed above."), 0, 1);
        scripts.Controls.Add(Kit.Row(
            Kit.Button("Add script…", ButtonKind.Primary, (_, _) => AddScript()),
            Kit.Button("Example script", ButtonKind.Secondary, (_, _) => OpenExample()),
            Kit.Button("Stored accounts…", ButtonKind.Secondary, (_, _) => StoredAccountsDialog()),
            Kit.Button("Refresh", ButtonKind.Secondary, (_, _) => Reload())), 0, 2);

        list = new ListView
        {
            Dock = DockStyle.Fill,
            View = System.Windows.Forms.View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Body,
        };
        list.Columns.Add("Script", Theme.S(170));
        list.Columns.Add("Runs as", Theme.S(150));
        list.Columns.Add("Schedule", Theme.S(120));
        list.Columns.Add("State", Theme.S(110));
        list.Resize += (_, _) => FitColumns();
        list.SelectedIndexChanged += (_, _) => ShowDetails();
        list.DoubleClick += (_, _) => { if (Selected() is { } s) EditScript(s); };

        details = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Surface };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var detailsPage = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface, Padding = Theme.P(14, 0, 4, 0) };
        detailsPage.Controls.Add(details);

        split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Theme.Surface, Margin = Theme.P(0, 8, 0, 0) };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(detailsPage);
        // Placed once it has its size: half each, neither side narrower than it can show.
        split.SizeChanged += (_, _) => PlaceSplit();
        scripts.Controls.Add(split, 0, 3);
        layout.Controls.Add(scripts, 0, 1);
        Controls.Add(layout);

        LoadPolicy();
        Reload();
    }

    // --- the policy -------------------------------------------------------------------------------------------------

    private void LoadPolicy()
    {
        var policy = ScriptPolicy.Read();
        allow.Checked = policy.Enabled;
        anyScript.Checked = policy.AnyScript;
        onlyRegistered.Checked = !policy.AnyScript;
        accounts.Text = string.Join("\r\n", policy.RunAs);
        UpdatePolicyFields();
    }

    private void UpdatePolicyFields()
    {
        onlyRegistered.Enabled = anyScript.Enabled = allow.Checked;
        accounts.Enabled = allow.Checked && anyScript.Checked;
    }

    private async void SavePolicy(Control button)
    {
        var policy = new ScriptPolicy(allow.Checked, anyScript.Checked,
            accounts.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            // The service announces what it may do in its hello: it restarts to tell PNMT.
            await Task.Run(() =>
            {
                policy.Save();
                ServiceControl.Restart();
            });
            logger.LogInformation("Scripts from PNMT: {State}", policy.Enabled ? policy.AnyScript ? "any script" : "registered scripts only" : "not allowed");
            Kit.SetText(policyNote, $"{DateTime.Now:T}: saved; the service restarted and tells PNMT.");
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Saving", e);
        }
        finally
        {
            UseWaitCursor = false;
            button.Enabled = true;
            ShowDetails();
        }
    }

    // --- the list ---------------------------------------------------------------------------------------------------

    /// <summary>The registered scripts, and then their tasks' state from the Task Scheduler (a second or two, off the UI thread).</summary>
    private async void Reload(string? select = null)
    {
        select ??= Selected()?.Name;
        Fill(select);
        try
        {
            tasks = await Task.Run(() => LocalScripts.TasksAsync(CancellationToken.None));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Reading the scheduled tasks failed");
            tasks = [];
        }
        Fill(Selected()?.Name ?? select);
    }

    private void Fill(string? select)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var script in LocalScripts.All())
        {
            var item = new ListViewItem([script.Name, script.RunAsText, script.Schedule?.Describe() ?? "on request", State(script)]) { Tag = script };
            if (!script.Matches())
            {
                item.ForeColor = Theme.Fail;
            }
            list.Items.Add(item);
            if (string.Equals(script.Name, select, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
            }
        }
        if (list.SelectedItems.Count == 0 && list.Items.Count > 0)
        {
            list.Items[0].Selected = true;
        }
        list.EndUpdate();
        FitColumns();
        ShowDetails();
    }

    private string State(LocalScript script)
    {
        if (!script.Matches())
        {
            return "file changed";
        }
        if (script.Schedule is null)
        {
            return "ok";
        }
        var task = TaskOf(script);
        return task is null ? "task missing" : task.LastResult is null or 0 ? task.State.ToLowerInvariant() : $"last run failed";
    }

    private TaskInfo? TaskOf(LocalScript script) => tasks.FirstOrDefault(t => t.Name.Equals(script.Name, StringComparison.OrdinalIgnoreCase));

    private LocalScript? Selected() => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as LocalScript : null;

    /// <summary>The name column takes the width the others leave.</summary>
    private void FitColumns()
    {
        if (list.Columns.Count < 4 || list.ClientSize.Width <= 0)
        {
            return;
        }
        var others = list.Columns[1].Width + list.Columns[2].Width + list.Columns[3].Width;
        list.Columns[0].Width = Math.Max(Theme.S(120), list.ClientSize.Width - others - 2);
    }

    private void PlaceSplit()
    {
        if (split.Width <= 0)
        {
            return;
        }
        var min = Theme.S(220);
        if (split.Width < 2 * min + split.SplitterWidth)
        {
            return;
        }
        split.Panel1MinSize = min;
        split.Panel2MinSize = min;
        if (!splitPlaced)
        {
            split.SplitterDistance = (split.Width - split.SplitterWidth) / 2;
            splitPlaced = true;
        }
    }

    // --- the details --------------------------------------------------------------------------------------------------

    private void ShowDetails()
    {
        details.SuspendLayout();
        while (details.Controls.Count > 0)
        {
            var control = details.Controls[0];
            details.Controls.RemoveAt(0);
            control.Dispose();
        }
        details.RowStyles.Clear();
        details.RowCount = 0;
        var script = Selected();
        if (script is null)
        {
            Add(Kit.Heading("No script yet"));
            Add(Kit.Muted("Add script… picks a .ps1 file. The example script shows the shape PNMT's parser likes: open it, adapt it, save it, and add it."));
            details.ResumeLayout();
            return;
        }

        Add(Kit.Heading(script.Name));
        var matches = script.Matches();
        Add(matches
            ? Kit.Banner("The file matches its SHA-256.", Tone.Ok)
            : Kit.Banner("The file changed after it was registered: PNMT refuses it, and a scheduled task runs the changed file. Replace… it with the file you want.", Tone.Fail));
        var facts = Add(Kit.Facts());
        Kit.Fact(facts, "File", Kit.Copyable(script.File));
        Kit.Fact(facts, "SHA-256", Kit.Copyable(script.Sha256));
        Kit.Fact(facts, "Runs as", Kit.Label(script.RunAsText + (script.Kind == RunAsKind.Stored ? " (password stored here)" : "")));
        if (script.Schedule is null)
        {
            Kit.Fact(facts, "Schedule", Kit.Label("none: on request from PNMT"));
        }
        else
        {
            Kit.Fact(facts, "Schedule", Kit.Label($"{script.Schedule.Describe()}, task {LocalScripts.TaskFolder}{script.Name}"));
            var task = TaskOf(script);
            Kit.Fact(facts, "Task", task is null
                ? Kit.Label("not found in the Task Scheduler: save the script again", Theme.Body, Theme.Fail)
                : Kit.Label($"{task.State.ToLowerInvariant()}; last run {(task.LastRun is { } last ? $"{last.ToLocalTime():g}, result {Result(task.LastResult)}" : "never")}" +
                    (task.NextRun is { } next ? $"; next {next.ToLocalTime():g}" : ""), Theme.Body, task.LastResult is null or 0 ? Theme.Text : Theme.Warn));
        }
        Kit.Fact(facts, "Added", Kit.Label($"{script.AddedAt.ToLocalTime():g} by {script.AddedBy}" +
            (script.UpdatedAt > script.AddedAt.AddSeconds(5) ? $"; changed {script.UpdatedAt.ToLocalTime():g}" : "")));
        if (script.Source is { } from)
        {
            Kit.Fact(facts, "From", Kit.Copyable(from));
        }
        var policy = ScriptPolicy.Read();
        Add(Kit.Muted(policy.Enabled
            ? $"PNMT may run it here as {script.RunAsText}."
            : "PNMT may not run scripts here (Scripts from PNMT, above); a schedule still runs it."));
        var buttons = Add(Kit.Row(
            Kit.Button("View", ButtonKind.Secondary, (_, _) => ViewScript(script)),
            Kit.Button("Edit…", ButtonKind.Secondary, (_, _) => EditScript(script)),
            Kit.Button("Replace…", ButtonKind.Secondary, (_, _) => ReplaceScript(script))));
        if (script.Schedule is not null)
        {
            buttons.Controls.Add(Kit.Button("Run the task now", ButtonKind.Secondary, (s, _) => RunTask(script, (Control)s!)));
        }
        buttons.Controls.Add(Kit.Button("Remove", ButtonKind.Danger, (_, _) => RemoveScript(script)));
        details.ResumeLayout();
    }

    private T Add<T>(T control) where T : Control
    {
        details.RowCount++;
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.Controls.Add(control, 0, details.RowCount - 1);
        return control;
    }

    private static string Result(long? code) => code switch
    {
        null => "–",
        0 => "0 (ok)",
        0x41301 => "still running",
        0x41303 => "not run yet",
        var c => $"{c} (0x{c:X})",
    };

    // --- actions ----------------------------------------------------------------------------------------------------

    private string? PickFile(string title)
    {
        using var open = new OpenFileDialog
        {
            Title = title,
            Filter = "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(ExampleScript.Folder) ? ExampleScript.Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return open.ShowDialog(this) == DialogResult.OK ? open.FileName : null;
    }

    private void AddScript()
    {
        if (PickFile("Add a PowerShell script") is not { } file)
        {
            return;
        }
        try
        {
            var hash = LocalScripts.HashOf(file);
            var name = LocalScripts.NameFrom(file);
            if (LocalScripts.ByName(name) is { } existing)
            {
                if (existing.Sha256 == hash)
                {
                    Dialogs.Info(this, $"{name} is registered already", "The file is the same as the registered one.");
                    Reload(name);
                    return;
                }
                var replace = new TaskDialogButton("Replace it");
                var other = new TaskDialogButton("Add under another name…");
                var cancel = TaskDialogButton.Cancel;
                var page = new TaskDialogPage
                {
                    Caption = "PNMT Sensor",
                    Heading = $"A script named {name} is registered already",
                    Text = $"Registered: SHA-256 {existing.Sha256[..16]}…, added {existing.AddedAt.ToLocalTime():g}, runs as {existing.RunAsText}.\n" +
                        $"This file: SHA-256 {hash[..16]}…\n\nReplacing keeps its account and schedule. PNMT then runs only the new version here.",
                    Icon = TaskDialogIcon.Warning,
                    AllowCancel = true,
                };
                page.Buttons.Add(replace);
                page.Buttons.Add(other);
                page.Buttons.Add(cancel);
                page.DefaultButton = cancel;
                var choice = TaskDialog.ShowDialog(this, page);
                if (choice == replace)
                {
                    Replace(existing, file);
                    return;
                }
                if (choice != other)
                {
                    return;
                }
                name = Unused(name);
            }
            if (LocalScripts.ByHash(hash) is { } same)
            {
                Dialogs.Info(this, "Registered already", $"The same script is registered as {same.Name}.");
                Reload(same.Name);
                return;
            }
            using var dialog = new ScriptDialog(file, hash, name, null);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Saved is { } saved)
            {
                logger.LogInformation("Registered the script {Name} ({Hash}) from {File}, as {Account}, {Schedule}", saved.Name, saved.Sha256, file, saved.RunAsText,
                    saved.Schedule?.Describe() ?? "no schedule");
                Reload(saved.Name);
            }
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Adding the script", e);
        }
    }

    private static string Unused(string name)
    {
        for (var n = 2; ; n++)
        {
            var candidate = $"{(name.Length > 75 ? name[..75] : name)} {n}";
            if (LocalScripts.ByName(candidate) is null)
            {
                return candidate;
            }
        }
    }

    private void ReplaceScript(LocalScript script)
    {
        if (PickFile($"Replace {script.Name}") is not { } file)
        {
            return;
        }
        try
        {
            var hash = LocalScripts.HashOf(file);
            if (hash == script.Sha256 && script.Matches())
            {
                Dialogs.Info(this, "Nothing to replace", "The file is the same as the registered one.");
                return;
            }
            if (LocalScripts.ByHash(hash) is { } same && same.Name != script.Name)
            {
                Dialogs.Info(this, "Registered already", $"The same script is registered as {same.Name}.");
                return;
            }
            if (Dialogs.Confirm(this, $"Replace {script.Name}?", $"With {file}\nSHA-256 {hash[..16]}… (now {script.Sha256[..16]}…).\n\nIts account and schedule stay.", "Replace"))
            {
                Replace(script, file);
            }
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Replacing the script", e);
        }
    }

    private void Replace(LocalScript script, string file)
    {
        var updated = LocalScripts.Replace(script, file);
        logger.LogInformation("Replaced the script {Name}: {Old} → {New}", script.Name, script.Sha256, updated.Sha256);
        Reload(script.Name);
    }

    private void EditScript(LocalScript script)
    {
        using var dialog = new ScriptDialog(null, script.Sha256, script.Name, script);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Saved is { } saved)
        {
            logger.LogInformation("Changed the script {Name}: as {Account}, {Schedule}", saved.Name, saved.RunAsText, saved.Schedule?.Describe() ?? "no schedule");
            Reload(saved.Name);
        }
    }

    private async void RemoveScript(LocalScript script)
    {
        if (!Dialogs.Confirm(this, $"Remove {script.Name}?",
            "Its copy" + (script.Schedule is null ? "" : " and its scheduled task") + " are deleted, and PNMT can no longer run it here. The file you added it from stays.", "Remove"))
        {
            return;
        }
        UseWaitCursor = true;
        try
        {
            await Task.Run(() => LocalScripts.RemoveAsync(script, CancellationToken.None));
            logger.LogInformation("Removed the script {Name}", script.Name);
            Reload();
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Removing the script", e);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async void RunTask(LocalScript script, Control button)
    {
        button.Enabled = false;
        try
        {
            await Task.Run(() => LocalScripts.StartTaskAsync(script.Name, CancellationToken.None));
            logger.LogInformation("Started the task of {Name}", script.Name);
            await Task.Delay(1500);
            Reload(script.Name);
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Starting the task", e);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    /// <summary>A read-only copy in the temp folder, opened in the editor: an edit there can't change the registered script.</summary>
    private void ViewScript(LocalScript script)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "PNMT Sensor");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, $"{script.Name} (registered, read-only).ps1");
            if (File.Exists(copy))
            {
                File.SetAttributes(copy, FileAttributes.Normal);
            }
            File.Copy(script.File, copy, overwrite: true);
            File.SetAttributes(copy, FileAttributes.ReadOnly);
            Dialogs.Edit(copy);
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Opening the script", e);
        }
    }

    private void OpenExample()
    {
        try
        {
            var file = ExampleScript.Write();
            Dialogs.Edit(file);
            logger.LogInformation("Opened the example script {File}", file);
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Opening the example", e);
        }
    }

    private void StoredAccountsDialog()
    {
        using var dialog = new StoredAccountsDialog(logger);
        dialog.ShowDialog(this);
        ShowDetails();
    }
}
