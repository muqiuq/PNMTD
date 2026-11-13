namespace PNMTD.Sensor.Windows.Ui;

/// <summary>The window's look: PNMT's colours (as in the web UI's light theme), Segoe UI, and sizes scaled to the screen's DPI.</summary>
internal static class Theme
{
    public static readonly Color Brand = Color.FromArgb(0x3B, 0x59, 0xA5);
    public static readonly Color BrandDark = Color.FromArgb(0x2C, 0x44, 0x80);
    public static readonly Color BrandSoft = Color.FromArgb(0xE0, 0xE7, 0xFF);
    public static readonly Color Page = Color.FromArgb(0xF1, 0xF5, 0xF9);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(0xCB, 0xD5, 0xE1);
    public static readonly Color Text = Color.FromArgb(0x0F, 0x17, 0x2A);
    public static readonly Color Muted = Color.FromArgb(0x47, 0x55, 0x69);
    public static readonly Color Ok = Color.FromArgb(0x15, 0x80, 0x3D);
    public static readonly Color OkSoft = Color.FromArgb(0xDC, 0xFC, 0xE7);
    public static readonly Color Warn = Color.FromArgb(0xB4, 0x53, 0x09);
    public static readonly Color WarnSoft = Color.FromArgb(0xFE, 0xF3, 0xC7);
    public static readonly Color Fail = Color.FromArgb(0xB9, 0x1C, 0x1C);
    public static readonly Color FailSoft = Color.FromArgb(0xFE, 0xE2, 0xE2);
    public static readonly Color FailBorder = Color.FromArgb(0xFC, 0xA5, 0xA5);
    public static readonly Color NeutralSoft = Color.FromArgb(0xE2, 0xE8, 0xF0);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 14f);
    public static readonly Font Mono = new("Consolas", 9.75f);

    /// <summary>The screen's DPI over 96, set by the window: paddings and fixed sizes are given at 100 %.</summary>
    public static float Scale { get; set; } = 1f;

    public static int S(int pixels) => (int)Math.Round(pixels * Scale);

    public static Padding P(int all) => new(S(all));

    public static Padding P(int horizontal, int vertical) => new(S(horizontal), S(vertical), S(horizontal), S(vertical));

    public static Padding P(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));
}

internal enum Tone { Ok, Warn, Fail, Info, Neutral }

internal enum ButtonKind { Primary, Secondary, Danger }

/// <summary>
/// The window's building blocks. Layout rule: text and input fields stretch with the window (anchored left and right), labels
/// wrap, buttons and short fields (the token) keep their size, and logs fill the height that is left.
/// </summary>
internal static class Kit
{
    private const AnchorStyles Stretch = AnchorStyles.Left | AnchorStyles.Right;

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Theme.Body,
        ForeColor = color ?? Theme.Text,
        Anchor = Stretch,
        Margin = Theme.P(0, 2, 0, 2),
        UseMnemonic = false,
    };

    public static Label Muted(string text) => Label(text, Theme.Body, Theme.Muted);

    public static Label Heading(string text)
    {
        var label = Label(text, Theme.Heading);
        label.Margin = Theme.P(0, 0, 0, 8);
        return label;
    }

    /// <summary>A coloured line of text across the card: a warning, a note.</summary>
    public static Label Banner(string text, Tone tone)
    {
        var (fore, back) = Colors(tone);
        var label = Label(text, Theme.Body, fore);
        label.BackColor = back;
        label.Padding = Theme.P(10, 6);
        label.Margin = Theme.P(0, 4, 0, 6);
        return label;
    }

    public static Label Pill()
    {
        var pill = new Label
        {
            AutoSize = true,
            Font = Theme.Strong,
            Padding = Theme.P(10, 3),
            Margin = Theme.P(0, 2, 10, 2),
            Anchor = AnchorStyles.Left,
            UseMnemonic = false,
        };
        SetPill(pill, "…", Tone.Neutral);
        return pill;
    }

    public static void SetPill(Label pill, string text, Tone tone)
    {
        var (fore, back) = Colors(tone);
        SetText(pill, text);
        pill.ForeColor = fore;
        pill.BackColor = back;
    }

    public static (Color Fore, Color Back) Colors(Tone tone) => tone switch
    {
        Tone.Ok => (Theme.Ok, Theme.OkSoft),
        Tone.Warn => (Theme.Warn, Theme.WarnSoft),
        Tone.Fail => (Theme.Fail, Theme.FailSoft),
        Tone.Info => (Theme.Brand, Theme.BrandSoft),
        _ => (Theme.Muted, Theme.NeutralSoft),
    };

    /// <summary>Sets a text only when it changed: no relayout, no flicker on every refresh.</summary>
    public static void SetText(Control control, string text)
    {
        if (control.Text != text)
        {
            control.Text = text;
        }
    }

    public static Button Button(string text, ButtonKind kind, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Strong,
            Padding = Theme.P(12, 4),
            Margin = Theme.P(0, 4, 8, 4),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseMnemonic = false,
        };
        switch (kind)
        {
            case ButtonKind.Primary:
                button.BackColor = Theme.Brand;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Theme.Brand;
                button.FlatAppearance.MouseOverBackColor = Theme.BrandDark;
                button.FlatAppearance.MouseDownBackColor = Theme.BrandDark;
                break;
            case ButtonKind.Danger:
                button.BackColor = Theme.Surface;
                button.ForeColor = Theme.Fail;
                button.FlatAppearance.BorderColor = Theme.FailBorder;
                button.FlatAppearance.MouseOverBackColor = Theme.FailSoft;
                button.FlatAppearance.MouseDownBackColor = Theme.FailSoft;
                break;
            default:
                button.BackColor = Theme.Surface;
                button.ForeColor = Theme.Text;
                button.FlatAppearance.BorderColor = Theme.Border;
                button.FlatAppearance.MouseOverBackColor = Theme.Page;
                button.FlatAppearance.MouseDownBackColor = Theme.NeutralSoft;
                break;
        }
        button.Click += click;
        return button;
    }

    /// <summary>Buttons and other short controls side by side, wrapping when the window is narrow.</summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = Stretch,
            Margin = Theme.P(0, 4, 0, 0),
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>A value to read and copy (keys, ids, commands): monospace, selectable, stretching.</summary>
    public static TextBox Copyable(string value) => new()
    {
        Text = value,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        Font = Theme.Mono,
        Anchor = Stretch,
        Margin = Theme.P(0, 4, 0, 4),
        TabStop = false,
    };

    public static TextBox Input(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        Font = Theme.Body,
        BorderStyle = BorderStyle.FixedSingle,
        Anchor = Stretch,
        Margin = Theme.P(0, 4, 0, 4),
    };

    /// <summary>A text area of a fixed height that stretches in width.</summary>
    public static TextBox Area(int height) => new()
    {
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Font = Theme.Mono,
        BorderStyle = BorderStyle.FixedSingle,
        Anchor = Stretch,
        Height = Theme.S(height),
        Margin = Theme.P(0, 4, 0, 6),
    };

    /// <summary>A log: monospace, read-only, filling the space it is given in both directions.</summary>
    public static TextBox Log() => new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Font = Theme.Mono,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        Margin = Theme.P(0, 6, 0, 0),
        MinimumSize = new Size(0, Theme.S(80)),
    };

    /// <summary>The scrolling page a tab's cards stand on.</summary>
    public static (Panel Page, TableLayoutPanel Stack) Page()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Page, Padding = Theme.P(16) };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(stack);
        return (page, stack);
    }

    public static void Add(TableLayoutPanel stack, Control control)
    {
        stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, stack.RowCount - 1);
    }

    /// <summary>Label and value pairs: the labels as wide as the longest, the values stretching.</summary>
    public static TableLayoutPanel Facts()
    {
        var facts = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Anchor = Stretch,
            Margin = Theme.P(0, 2, 0, 2),
            BackColor = Color.Transparent,
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return facts;
    }

    public static T Fact<T>(TableLayoutPanel facts, string label, T value) where T : Control
    {
        var key = Label(label, Theme.Body, Theme.Muted);
        key.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        key.Margin = Theme.P(0, 4, 16, 4);
        if (value.Margin == Padding.Empty || value is Label)
        {
            value.Margin = Theme.P(0, 4, 0, 4);
        }
        facts.RowCount++;
        facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        facts.Controls.Add(key, 0, facts.RowCount - 1);
        facts.Controls.Add(value, 1, facts.RowCount - 1);
        return value;
    }

    /// <summary>A short control (a pill) beside a text that wraps.</summary>
    public static TableLayoutPanel Pair(Control left, Control right)
    {
        var pair = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Anchor = Stretch, Margin = Padding.Empty };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pair.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        right.Anchor = Stretch | AnchorStyles.Top;
        pair.Controls.Add(left, 0, 0);
        pair.Controls.Add(right, 1, 0);
        return pair;
    }

    public static void Border(Control control, PaintEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
    }
}

/// <summary>A white card with a heading and a thin border; its rows stretch with the window and wrap.</summary>
internal sealed class Card : TableLayoutPanel
{
    public Card(string heading)
    {
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Surface;
        Padding = Theme.P(18, 14, 18, 14);
        Margin = Theme.P(0, 0, 0, 12);
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        DoubleBuffered = true;
        HeadingLabel = Kit.Heading(heading);
        Add(HeadingLabel);
    }

    public Label HeadingLabel { get; }

    public T Add<T>(T control) where T : Control
    {
        RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(control, 0, RowCount - 1);
        return control;
    }

    /// <summary>Everything but the heading, for content that is built anew when it changed.</summary>
    public void ClearBody()
    {
        SuspendLayout();
        for (var i = Controls.Count - 1; i >= 0; i--)
        {
            var control = Controls[i];
            if (control != HeadingLabel)
            {
                Controls.RemoveAt(i);
                control.Dispose();
            }
        }
        while (RowStyles.Count > 1)
        {
            RowStyles.RemoveAt(RowStyles.Count - 1);
        }
        RowCount = 1;
        ResumeLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Kit.Border(this, e);
    }
}

/// <summary>The icon and the header's logo, embedded in the executable.</summary>
internal static class Assets
{
    public static Icon Icon { get; } = new(Resource("pnmt.ico"));

    public static Image Logo { get; } = LoadImage("logo.png");

    private static Stream Resource(string name) =>
        typeof(Assets).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"The resource {name} is missing.");

    private static Bitmap LoadImage(string name)
    {
        using var stream = Resource(name);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
