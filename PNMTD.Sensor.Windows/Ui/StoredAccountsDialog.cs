using Microsoft.Extensions.Logging;
using PNMTD.Sensor.Windows.Scripts;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>
/// The accounts whose passwords are stored on this server (docs/windows-sensor-plan.md §7.4): add or replace one (checked with
/// Windows first), remove one. Registered scripts name the account; a scheduled task needs the password again when it changes.
/// </summary>
internal sealed class StoredAccountsDialog : Form
{
    private readonly ILogger logger;
    private readonly ListBox list;

    public StoredAccountsDialog(ILogger logger)
    {
        this.logger = logger;
        Text = "Stored accounts";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        Icon = Assets.Icon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(Theme.S(460), Theme.S(340));
        MinimumSize = new Size(Theme.S(380), Theme.S(300));
        Padding = Theme.P(16);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(Kit.Muted("Passwords kept on this server only, encrypted with DPAPI. They never reach PNMT. A local administrator can still recover them, as any secret on this machine."), 0, 0);
        list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, Margin = Theme.P(0, 8, 0, 8) };
        table.Controls.Add(list, 0, 1);
        var close = Kit.Button("Close", ButtonKind.Secondary, (_, _) => Close());
        table.Controls.Add(Kit.Row(
            Kit.Button("Add or replace…", ButtonKind.Primary, (_, _) => Add()),
            Kit.Button("Remove", ButtonKind.Danger, (_, _) => Remove()),
            close), 0, 2);
        Controls.Add(table);
        CancelButton = close;
        Reload();
    }

    private void Reload()
    {
        list.Items.Clear();
        list.Items.AddRange(StoredAccounts.Names().Cast<object>().ToArray());
    }

    private void Add()
    {
        using var dialog = new AccountDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        try
        {
            StoredAccounts.Set(dialog.Account, dialog.Password);
            logger.LogInformation("Stored the password of {Account}", dialog.Account);
            var scheduled = LocalScripts.All().Where(s => s.Schedule is not null && string.Equals(s.Account, dialog.Account, StringComparison.OrdinalIgnoreCase)).ToList();
            if (scheduled.Count > 0)
            {
                Dialogs.Info(this, "Stored", $"Scheduled tasks keep the password they were registered with: open {string.Join(", ", scheduled.Select(s => s.Name))} and save it again to use the new one.");
            }
            Reload();
        }
        catch (Exception e)
        {
            Dialogs.Error(this, logger, "Storing the account", e);
        }
    }

    private void Remove()
    {
        if (list.SelectedItem is not string account)
        {
            return;
        }
        var users = LocalScripts.All().Where(s => s.Kind == Lib.Agent.RunAsKind.Stored && string.Equals(s.Account, account, StringComparison.OrdinalIgnoreCase)).ToList();
        var text = users.Count == 0
            ? $"The password of {account} is deleted from this server."
            : $"{string.Join(", ", users.Select(s => s.Name))} run as {account}: from PNMT they can't run any more without its password. Their scheduled tasks keep theirs.";
        if (!Dialogs.Confirm(this, $"Remove {account}?", text, "Remove"))
        {
            return;
        }
        StoredAccounts.Remove(account);
        logger.LogInformation("Removed the stored password of {Account}", account);
        Reload();
    }
}

/// <summary>An account whose password is stored on this server (§7.4): the name and the password, entered here only.</summary>
internal sealed class AccountDialog : Form
{
    private readonly TextBox account;
    private readonly TextBox password;

    public AccountDialog()
    {
        Text = "Stored account";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Theme.P(18);
        Icon = Assets.Icon;

        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        account = Kit.Input("DOMAIN\\user");
        account.Width = Theme.S(280);
        password = Kit.Input("");
        password.UseSystemPasswordChar = true;
        password.Width = Theme.S(280);
        void Field(string label, Control control, int row)
        {
            var key = Kit.Label(label, Theme.Strong);
            key.Anchor = AnchorStyles.Left;
            key.Margin = Theme.P(0, 4, 14, 4);
            table.Controls.Add(key, 0, row);
            table.Controls.Add(control, 1, row);
        }
        Field("Account", account, 0);
        Field("Password", password, 1);
        var error = Kit.Label("", Theme.Body, Theme.Fail);
        error.MaximumSize = new Size(Theme.S(280), 0);
        table.Controls.Add(error, 1, 2);
        // Checked with Windows before it is stored: a typo would only show when a script fails.
        var ok = Kit.Button("Store", ButtonKind.Primary, (_, _) =>
        {
            if (Native.Win32.CheckLogon(Account, Password) is { } problem)
            {
                error.Text = problem;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        });
        ok.Enabled = false;
        var cancel = Kit.Button("Cancel", ButtonKind.Secondary, (_, _) => { });
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = Kit.Row(ok, cancel);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = Theme.P(0, 12, 0, 0);
        table.Controls.Add(buttons, 1, 3);
        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
        EventHandler changed = (_, _) => ok.Enabled = account.Text.Trim().Length > 0 && password.Text.Length > 0;
        account.TextChanged += changed;
        password.TextChanged += changed;
    }

    public string Account => account.Text.Trim();

    public string Password => password.Text;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        password.Clear();
    }
}
