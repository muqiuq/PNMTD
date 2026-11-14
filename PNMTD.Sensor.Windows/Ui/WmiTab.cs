using System.Globalization;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>
/// WMI (docs/windows-sensor-plan.md §16, "WMI"): every query the service runs, built-in and own, with its section, status, rows,
/// time and last run, as the service last wrote it (<c>state\wmi-status.json</c>); the selected one's query, error and, for an
/// own query, its rows. PNMT switches queries and sets timeouts (the server's WMI tab); here a local administrator can switch
/// any of them off on this server, which wins.
/// </summary>
internal sealed class WmiTab : UserControl
{
    private readonly ILogger logger;
    private readonly ListView list;
    private readonly TableLayoutPanel details;
    private readonly SplitContainer split;
    private readonly Label note;
    private readonly System.Windows.Forms.Timer timer;
    private DateTime shownWritten;
    private WmiSection? section;
    private bool splitPlaced;

    public WmiTab(ILogger logger)
    {
        this.logger = logger;
        Dock = DockStyle.Fill;
        BackColor = Theme.Page;

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = Theme.P(18, 14, 18, 14), ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Paint += (_, e) => Kit.Border(card, e);
        card.Controls.Add(Kit.Heading("WMI queries"), 0, 0);
        card.Controls.Add(Kit.Muted("What the service asks WMI, per section, and what came back. PNMT switches queries off, gives them another timeout and " +
            "adds its own (the server's WMI tab in PNMT); here you can switch any of them off on this server, which wins over PNMT."), 0, 1);
        note = Kit.Muted("");
        card.Controls.Add(Kit.Row(Kit.Button("Refresh", ButtonKind.Secondary, (_, _) => AskService(null)), note), 0, 2);

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
        foreach (var (title, width) in new[] { ("Query", 210), ("Section", 100), ("Status", 100), ("Rows", 50), ("Time", 70), ("Last run", 80) })
        {
            list.Columns.Add(title, Theme.S(width));
        }
        list.Resize += (_, _) => FitColumns();
        list.SelectedIndexChanged += (_, _) => ShowDetails();

        details = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Surface };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var detailsPage = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface, Padding = Theme.P(14, 0, 4, 0) };
        detailsPage.Controls.Add(details);

        split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Theme.Surface, Margin = Theme.P(0, 8, 0, 0) };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(detailsPage);
        split.SizeChanged += (_, _) => PlaceSplit();
        card.Controls.Add(split, 0, 3);

        var page = new Panel { Dock = DockStyle.Fill, Padding = Theme.P(16), BackColor = Theme.Page };
        page.Controls.Add(card);
        Controls.Add(page);

        timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => Reload(force: false);
        timer.Start();
        Disposed += (_, _) => timer.Dispose();
        Reload(force: true);
    }

    private static string StatusPath => Path.Combine(AgentInfo.StateFolder, WmiCollector.StatusFile);

    /// <summary>The service's last snapshot, when it changed (every minute, and after a change here or in PNMT).</summary>
    private void Reload(bool force)
    {
        if (!Visible && !force)
        {
            return;
        }
        try
        {
            var written = File.Exists(StatusPath) ? File.GetLastWriteTimeUtc(StatusPath) : DateTime.MinValue;
            if (!force && written == shownWritten)
            {
                return;
            }
            shownWritten = written;
            section = AgentFiles.ReadState<WmiSection>(WmiCollector.StatusFile);
            Kit.SetText(note, section is null
                ? "The service hasn't written the WMI state yet: it does within a minute of starting."
                : $"As the service saw it at {written.ToLocalTime():T}: {section.Queries.Count} built-in queries, {section.Custom.Count} own.");
            Fill();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogDebug("WMI state: {Message}", e.Message);
        }
    }

    private sealed record Entry(string Id, WmiQueryState? Query, CustomWmiResult? Custom);

    private void Fill()
    {
        var selected = (list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Entry : null)?.Id;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var q in section?.Queries ?? [])
        {
            Add(new Entry(q.Id, q, null), q.Class, q.Section ?? "–", q.Status, q.Rows, q.Ms, q.LastRunAt, q.Status);
        }
        foreach (var c in section?.Custom ?? [])
        {
            Add(new Entry(c.Id.ToString(), null, c), $"own: {c.Name}", "wmi", c.Status, c.Rows.Count, c.Ms, c.LastRunAt, c.Status);
        }
        if (list.SelectedItems.Count == 0 && list.Items.Count > 0)
        {
            list.Items[0].Selected = true;
        }
        list.EndUpdate();
        FitColumns();
        ShowDetails();

        void Add(Entry entry, string name, string sectionName, string status, int? rows, double? ms, DateTimeOffset? at, string tone)
        {
            var item = new ListViewItem([name, sectionName, status, rows?.ToString(CultureInfo.CurrentCulture) ?? "–", ms is { } m ? $"{m:0} ms" : "–",
                at is { } t ? t.ToLocalTime().ToString("T") : "–"]) { Tag = entry };
            item.ForeColor = tone switch { "error" or "timed out" or "refused" => Theme.Fail, "off" => Theme.Muted, "not available" => Theme.Warn, _ => Theme.Text };
            list.Items.Add(item);
            if (entry.Id == selected)
            {
                item.Selected = true;
            }
        }
    }

    private void FitColumns()
    {
        if (list.Columns.Count < 6 || list.ClientSize.Width <= 0)
        {
            return;
        }
        var others = Enumerable.Range(1, 5).Sum(i => list.Columns[i].Width);
        list.Columns[0].Width = Math.Max(Theme.S(140), list.ClientSize.Width - others - 2);
    }

    private void PlaceSplit()
    {
        var min = Theme.S(260);
        if (split.Width < 2 * min + split.SplitterWidth)
        {
            return;
        }
        split.Panel1MinSize = min;
        split.Panel2MinSize = min;
        if (!splitPlaced)
        {
            split.SplitterDistance = (split.Width - split.SplitterWidth) * 11 / 20;
            splitPlaced = true;
        }
    }

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
        if ((list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag : null) is not Entry entry)
        {
            Add(Kit.Muted(section is null ? "" : "Select a query."));
            details.ResumeLayout();
            return;
        }
        var (title, ns, wql, status, error, offBy, timeout, sectionName) = entry.Query is { } q
            ? (q.Class, q.Namespace, q.Wql, q.Status, q.Error, q.OffBy, (int?)q.TimeoutSeconds, q.Section)
            : (entry.Custom!.Name, entry.Custom.Namespace, entry.Custom.Wql, entry.Custom.Status, entry.Custom.Error, entry.Custom.OffBy, null, "wmi (own query)");
        Add(Kit.Heading(title));
        if (error is not null)
        {
            Add(Kit.Banner(error, status == "not available" ? Tone.Warn : Tone.Fail));
        }
        var facts = Add(Kit.Facts());
        Kit.Fact(facts, "Namespace", Kit.Copyable(ns));
        Kit.Fact(facts, "Query", Kit.Copyable(wql));
        Kit.Fact(facts, "Section", Kit.Label(sectionName ?? "–"));
        Kit.Fact(facts, "Status", Kit.Label(status));
        if (timeout is { } seconds)
        {
            Kit.Fact(facts, "Timeout", Kit.Label($"{seconds} s"));
        }
        Kit.Fact(facts, "Switched off", Kit.Label(offBy switch { "pnmt" => "in PNMT", "server" => "on this server", _ => "no" }));

        var locallyOff = WmiSettings.LocallyOff().Contains(entry.Id);
        var off = new CheckBox { Text = "Off on this server (wins over PNMT)", AutoSize = true, Checked = locallyOff, Margin = Theme.P(0, 8, 0, 4) };
        off.CheckedChanged += (_, _) => SetLocallyOff(entry, off.Checked, sectionName);
        Add(off);
        Add(Kit.Muted(entry.Query is null
            ? "PNMT's own query: its rows go to PNMT with the wmi section."
            : "Off, the query is not run: its part of the section stays empty, as for a class this Windows doesn't have."));

        if (entry.Custom is { } custom && custom.Columns.Count > 0)
        {
            Add(Kit.Label($"{custom.Rows.Count} rows{(custom.Truncated ? $" (the first {WmiQueries.MaxRows})" : "")}", Theme.Strong));
            var rows = new ListView
            {
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Body,
                Height = Theme.S(260),
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = Theme.P(0, 4, 0, 4),
            };
            foreach (var column in custom.Columns)
            {
                rows.Columns.Add(column, Theme.S(130));
            }
            foreach (var row in custom.Rows)
            {
                rows.Items.Add(new ListViewItem(row.Select(v => v ?? "").ToArray()));
            }
            Add(rows);
        }
        details.ResumeLayout();
    }

    private T Add<T>(T control) where T : Control
    {
        details.RowCount++;
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.Controls.Add(control, 0, details.RowCount - 1);
        return control;
    }

    private void SetLocallyOff(Entry entry, bool off, string? sectionName)
    {
        try
        {
            WmiSettings.SetLocallyOff(entry.Id, off);
            logger.LogInformation("WMI: {Query} {State} on this server", entry.Query?.Id ?? entry.Custom?.Name, off ? "switched off" : "switched back to PNMT's setting");
            AskService(sectionName is { } name && SectionNames.IsKnown(name) ? name : null);
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Switching the query", e);
        }
    }

    /// <summary>The service collects the query's section (it takes the switch into account) and writes the WMI state again.</summary>
    private void AskService(string? sectionName)
    {
        try
        {
            File.AppendAllLines(AgentInfo.CollectNowFile, sectionName is null || sectionName == SectionNames.Wmi
                ? [SectionNames.Wmi]
                : [sectionName, SectionNames.Wmi]);
            Kit.SetText(note, $"{DateTime.Now:T}: asked the service; the list updates within a few seconds.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Kit.SetText(note, $"The service could not be asked: {e.Message}");
        }
    }
}
