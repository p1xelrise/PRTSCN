using System.Drawing.Drawing2D;

namespace PRTSCN.Editor;

internal enum ButtonStyle { Ghost, Primary, Icon, Play }

/// <summary>A painted button in the PRTSCN dark style, with an optional icon.</summary>
internal sealed class EditorButton : Control
{
    readonly float _s;
    bool _hover, _press;
    public ButtonStyle Style { get; set; }
    public IconKind? Icon { get; set; }
    public bool Danger { get; set; }
    public string? Hint { get; set; }

    public EditorButton(string text, IconKind? icon, ButtonStyle style, float scale)
    {
        _s = scale;
        Text = text;
        Icon = icon;
        Style = style;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = Theme.UiFont(style == ButtonStyle.Primary ? 14 : 13, style == ButtonStyle.Primary, scale);
        int h = (int)((style switch { ButtonStyle.Primary => 40, ButtonStyle.Play => 38, _ => 32 }) * scale);
        int w = style is ButtonStyle.Icon or ButtonStyle.Play ? h
            : TextRenderer.MeasureText(text, Font).Width + (int)((icon != null ? 44 : 26) * scale);
        Size = new Size(w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float rad = 6 * _s;
        Color fg;
        switch (Style)
        {
            case ButtonStyle.Primary:
                Theme.FillRound(g, !Enabled ? Theme.SurfaceActive : _press ? ControlPaint.Dark(Theme.Accent, 0.05f) : _hover ? ControlPaint.Light(Theme.Accent, 0.2f) : Theme.Accent, r, rad);
                fg = Enabled ? Theme.OnAccent : Theme.TextMuted;
                break;
            case ButtonStyle.Play:
                using (var b = new SolidBrush(!Enabled ? Theme.SurfaceActive : _hover ? Color.White : Theme.Text)) g.FillEllipse(b, r);
                fg = Theme.Bg;
                break;
            case ButtonStyle.Icon:
                if (_hover && Enabled) Theme.FillRound(g, Theme.SurfaceActive, r, rad);
                fg = !Enabled ? Theme.Border : Danger ? Theme.Danger : _hover ? Theme.Text : Theme.TextMuted;
                break;
            default:
                Theme.FillRound(g, _hover && Enabled ? Theme.SurfaceActive : Theme.Surface, r, rad);
                Theme.StrokeRound(g, Theme.Border, 1, r, rad);
                fg = !Enabled ? Theme.Border : Danger ? Theme.Danger : Theme.Text;
                break;
        }
        if (Focused && ShowFocusCues) Theme.StrokeRound(g, Theme.WithAlpha(Theme.Accent, 170), 1.5f, r, rad);

        float icon = (Style == ButtonStyle.Play ? 16 : 18) * _s;
        if (Style is ButtonStyle.Icon or ButtonStyle.Play)
        {
            if (Icon is { } k) Icons.Draw(g, k, new RectangleF((Width - icon) / 2f, (Height - icon) / 2f, icon, icon), fg, _s, fg);
            return;
        }
        var ts = TextRenderer.MeasureText(Text, Font);
        float total = ts.Width + (Icon != null ? icon + 8 * _s : 0);
        float x = (Width - total) / 2f;
        if (Icon is { } ic)
        {
            Icons.Draw(g, ic, new RectangleF(x, (Height - icon) / 2f, icon, icon), fg, _s, fg);
            x += icon + 8 * _s;
        }
        TextRenderer.DrawText(g, Text, Font, new Point((int)x, (Height - ts.Height) / 2), fg, TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _press = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _press = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _press = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    /// <summary>Developer check: a click, as the user would make it.</summary>
    internal void PerformClickForTest() { if (Enabled) OnClick(EventArgs.Empty); }
}

/// <summary>"Label ........ value ▾" — opens a menu of choices.</summary>
internal sealed class ChoiceRow : Control
{
    readonly float _s;
    string[] _options = Array.Empty<string>();
    int _selected;
    bool _hover;
    public event EventHandler? SelectedChanged;

    public ChoiceRow(string label, float scale)
    {
        _s = scale;
        Text = label;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = Theme.UiFont(14, false, scale);
        Height = (int)(32 * scale);
    }

    public void SetOptions(string[] options, int selected)
    {
        _options = options;
        _selected = Math.Clamp(selected, 0, Math.Max(0, options.Length - 1));
        Invalidate();
    }

    public int Selected
    {
        get => _selected;
        set { value = Math.Clamp(value, 0, Math.Max(0, _options.Length - 1)); if (value == _selected) return; _selected = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hover && Enabled) Theme.FillRound(g, Theme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), 6 * _s);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int pad = (int)(8 * _s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(pad, 0, Width / 2, Height), Theme.TextMuted, flags);
        string value = _options.Length > 0 ? _options[_selected] : "";
        float chev = 10 * _s;
        var vs = TextRenderer.MeasureText(value, Font);
        int vx = (int)(Width - pad - chev - 6 * _s - vs.Width);
        TextRenderer.DrawText(g, value, Font, new Point(vx, (Height - vs.Height) / 2), Enabled ? Theme.Text : Theme.Border, TextFormatFlags.NoPadding);
        using var pen = new Pen(Enabled ? Theme.TextMuted : Theme.Border, 1.5f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - pad - chev / 2, cy = Height / 2f;
        g.DrawLines(pen, new[] { new PointF(cx - 4 * _s, cy - 2 * _s), new PointF(cx, cy + 2 * _s), new PointF(cx + 4 * _s, cy - 2 * _s) });
        if (Focused && ShowFocusCues) Theme.StrokeRound(g, Theme.WithAlpha(Theme.Accent, 170), 1.5f, new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 6 * _s);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled || _options.Length == 0) return;
        var menu = DarkMenu.Create(_s);
        for (int i = 0; i < _options.Length; i++)
        {
            int k = i;
            var item = new ToolStripMenuItem(_options[i]) { Checked = i == _selected };
            item.Click += (_, _) => Selected = k;
            menu.Items.Add(item);
        }
        menu.Show(this, new Point(Width - menu.PreferredSize.Width, Height + 2));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Context menus in the dark style.</summary>
internal static class DarkMenu
{
    public static ContextMenuStrip Create(float scale)
    {
        var m = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = false },
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(14, false, scale),
        };
        m.Renderer = new Renderer();
        m.Closed += (_, _) => m.BeginInvoke(m.Dispose);
        return m;
    }

    sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextMuted;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(Theme.Accent, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.55f), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.75f), new PointF(r.X + r.Width * 0.82f, r.Y + r.Height * 0.3f) });
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfaceActive;
        public override Color MenuItemSelected => Theme.SurfaceActive;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.Surface;
        public override Color CheckSelectedBackground => Theme.SurfaceActive;
        public override Color CheckPressedBackground => Theme.SurfaceActive;
    }
}

/// <summary>A small dark dialog that asks for a new file name.</summary>
internal sealed class RenameDialog : Form
{
    readonly TextBox _box;

    public string FileName => _box.Text.Trim();

    public RenameDialog(string current, float scale)
    {
        Text = "Rename recording";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(14, false, scale);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size((int)(420 * scale), (int)(132 * scale));
        int pad = (int)(16 * scale);
        var label = new Label { Text = "New name (the extension stays):", AutoSize = true, Location = new Point(pad, pad), ForeColor = Theme.TextMuted };
        _box = new TextBox { Text = current, Location = new Point(pad, pad + (int)(24 * scale)), Width = ClientSize.Width - pad * 2 };
        DarkUi.Style(_box);
        var ok = SizeDialog.DarkButton("Rename", true);
        var cancel = SizeDialog.DarkButton("Cancel", false);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange(new Control[] { label, _box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        Load += (_, _) =>
        {
            cancel.Location = new Point(ClientSize.Width - pad - cancel.Width, ClientSize.Height - pad - cancel.Height);
            ok.Location = new Point(cancel.Left - (int)(8 * scale) - ok.Width, cancel.Top);
            _box.SelectAll();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SizeDialog.DarkTitleBar(Handle);
    }
}
