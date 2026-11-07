using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PRTSCN;

/// <summary>An on/off switch drawn in the PRTSCN dark style.</summary>
internal sealed class DarkToggle : Control
{
    bool _checked, _hover;
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public DarkToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Size = new Size(40, 22);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var track = !Enabled ? Theme.Surface : _checked ? (_hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent) : (_hover ? Theme.SurfaceActive : Theme.Surface);
        Theme.FillRound(g, track, r, r.Height / 2);
        if (!_checked) Theme.StrokeRound(g, Theme.Border, 1, r, r.Height / 2);
        float d = Height - 6;
        float x = _checked ? Width - d - 3 : 3;
        using var knob = new SolidBrush(!Enabled ? Theme.Border : _checked ? Theme.OnAccent : Theme.TextMuted);
        g.FillEllipse(knob, x, 3, d, d);
        if (Focused && ShowFocusCues) using (var p = new Pen(Theme.WithAlpha(Theme.Accent, 160))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>A row of mutually exclusive choices (segmented control) in the dark style.</summary>
internal sealed class DarkSegmented : Control
{
    readonly string[] _items;
    int _selected, _hover = -1;
    public event EventHandler? SelectedChanged;

    public int Selected
    {
        get => _selected;
        set { value = Math.Clamp(value, 0, _items.Length - 1); if (_selected == value) return; _selected = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public DarkSegmented(params string[] items)
    {
        _items = items;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = new Font("Segoe UI", 9f);
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        int w = 8;
        foreach (var s in items) w += Math.Max(48, TextRenderer.MeasureText(g, s, Font).Width + 22);
        Size = new Size(w, 30);
    }

    RectangleF Cell(int i)
    {
        float inner = Width - 8;
        float total = _items.Sum(CellWeight);
        float x = 4;
        for (int k = 0; k < i; k++) x += inner * CellWeight(_items[k]) / total;
        return new RectangleF(x, 4, inner * CellWeight(_items[i]) / total, Height - 8);
    }

    float CellWeight(string s) => Math.Max(48, TextRenderer.MeasureText(s, Font).Width + 22);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, Theme.Bg, r, 7);
        Theme.StrokeRound(g, Theme.Border, 1, r, 7);
        for (int i = 0; i < _items.Length; i++)
        {
            var c = Cell(i);
            bool sel = i == _selected;
            if (sel) Theme.FillRound(g, Enabled ? Theme.SurfaceActive : Theme.Surface, c, 5);
            else if (i == _hover && Enabled) Theme.FillRound(g, Theme.Surface, c, 5);
            var col = !Enabled ? Theme.Border : sel ? Theme.Text : Theme.TextMuted;
            using var f = new Font(Font, sel ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(g, _items[i], f, Rectangle.Round(c), col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        if (Focused && ShowFocusCues) Theme.StrokeRound(g, Theme.WithAlpha(Theme.Accent, 160), 1, r, 7);
    }

    int HitIndex(Point p)
    {
        for (int i = 0; i < _items.Length; i++) if (Cell(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitIndex(e.Location); if (h != _hover) { _hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); int h = HitIndex(e.Location); if (h >= 0 && Enabled) Selected = h; }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Selected--;
        else if (e.KeyCode == Keys.Right) Selected++;
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>
/// Shows a folder path in the dark style, shortened in the middle when it is too long
/// ("C:\Users\…\Videos\PRTSCN"). The full path is in the tooltip. Click raises <see cref="Browse"/>.
/// </summary>
internal sealed class FolderBox : Control
{
    readonly ToolTip _tip = new();
    bool _hover;
    public event EventHandler? Browse;
    /// <summary>A drop-down look (▾, end ellipsis) for choosing from a list instead of a folder.</summary>
    public bool Chevron { get; set; }

    public FolderBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(260, 28);
        Cursor = Cursors.Hand;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _tip.SetToolTip(this, Text);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, _hover ? Theme.SurfaceActive : Theme.Surface, r, 5);
        Theme.StrokeRound(g, Theme.Border, 1, r, 5);
        int pad = (int)(8 * DeviceDpi / 96f);
        var text = new Rectangle(pad, 0, Width - pad * 2, Height);
        if (Chevron)
        {
            int cw = (int)(14 * DeviceDpi / 96f);
            text.Width -= cw;
            TextRenderer.DrawText(g, "▾", Font, new Rectangle(text.Right, 0, cw, Height), Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | (Chevron ? TextFormatFlags.EndEllipsis : TextFormatFlags.PathEllipsis) | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Browse?.Invoke(this, EventArgs.Empty); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A horizontal slider with its value as a percentage, in the dark style.</summary>
internal sealed class DarkSlider : Control
{
    int _value = 100;
    bool _drag, _hover;
    public int Minimum { get; set; } = 0;
    public int Maximum { get; set; } = 200;
    public int Step { get; set; } = 5;
    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp((int)Math.Round(value / (double)Step) * Step, Minimum, Maximum);
            if (value == _value) return;
            _value = value; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public DarkSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        Size = new Size(220, 28);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    float Dpi => DeviceDpi / 96f;
    int LabelWidth => (int)(46 * Dpi);
    RectangleF Track => new(8 * Dpi, Height / 2f - 2 * Dpi, Width - LabelWidth - 16 * Dpi, 4 * Dpi);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var t = Track;
        float k = (_value - Minimum) / (float)(Maximum - Minimum);
        Theme.FillRound(g, Theme.SurfaceActive, t, t.Height / 2);
        Theme.FillRound(g, Enabled ? Theme.Accent : Theme.Border, new RectangleF(t.X, t.Y, t.Width * k, t.Height), t.Height / 2);
        // 100% tick
        if (Minimum < 100 && Maximum > 100)
        {
            float x100 = t.X + t.Width * (100 - Minimum) / (Maximum - Minimum);
            using var p = new Pen(Theme.TextMuted, 1);
            g.DrawLine(p, x100, t.Bottom + 3 * Dpi, x100, t.Bottom + 6 * Dpi);
        }
        float d = (_hover || _drag ? 16 : 14) * Dpi;
        float cx = t.X + t.Width * k;
        using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.TextMuted)) g.FillEllipse(b, cx - d / 2, Height / 2f - d / 2, d, d);
        if (Focused && ShowFocusCues) using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawEllipse(p, cx - d / 2 - 2, Height / 2f - d / 2 - 2, d + 4, d + 4);
        TextRenderer.DrawText(g, $"{_value}%", Font, new Rectangle(Width - LabelWidth, 0, LabelWidth, Height), Enabled ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    void SetFrom(int x)
    {
        var t = Track;
        Value = Minimum + (int)Math.Round((x - t.X) / t.Width * (Maximum - Minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (!Enabled) return; _drag = true; SetFrom(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) SetFrom(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (Enabled) Value += Math.Sign(e.Delta) * Step; }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (Enabled) Value = 100; }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Value -= Step;
        else if (e.KeyCode == Keys.Right) Value += Step;
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

internal static class DarkUi
{
    public static void Style(TextBox t)
    {
        t.BackColor = Theme.Surface;
        t.ForeColor = Theme.Text;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void Style(NumericUpDown n)
    {
        n.BackColor = Theme.Surface;
        n.ForeColor = Theme.Text;
        n.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>Dark scroll bars and other common controls (Windows 10 1809 and later).</summary>
    public static void DarkScrollbars(Control c)
    {
        c.HandleCreated += (_, _) => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string? idList);
}
