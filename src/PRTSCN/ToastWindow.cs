namespace PRTSCN;

/// <summary>
/// A small status pill (countdown, scrolling progress). It never takes focus, mouse clicks go
/// through it, and it is excluded from screenshots so it never ends up in a capture.
/// </summary>
internal sealed class ToastWindow : Form
{
    string _title = "", _detail = "";
    bool _big;
    float _scale = 1;
    Rectangle _screen;

    /// <summary>False on old Windows 10 builds: then the toast must be hidden while grabbing.</summary>
    public bool Excluded { get; private set; }

    public ToastWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Opacity = 0.96;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x8 /* TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Excluded = Native.ExcludeFromCapture(Handle);
    }

    /// <summary>Show text near the top of a monitor. big = a large countdown number.</summary>
    public void ShowText(Rectangle screen, string title, string detail, bool big = false, Rectangle avoid = default)
    {
        _screen = screen;
        _title = title;
        _detail = detail;
        _big = big;
        _scale = Native.ScaleAt(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        using (var g = CreateGraphics())
        using (var ft = TitleFont())
        using (var fd = Theme.UiFont(Theme.TypeSm, false, _scale))
        {
            var a = g.MeasureString(_title, ft);
            var b = _detail.Length > 0 ? g.MeasureString(_detail, fd) : SizeF.Empty;
            int w = (int)Math.Ceiling(Math.Max(a.Width, b.Width)) + S(32);
            int h = (int)Math.Ceiling(a.Height + b.Height) + S(20);
            var r = new Rectangle(screen.X + (screen.Width - w) / 2, screen.Y + S(_big ? 120 : 24), w, h);
            // Keep off the area being captured (it is excluded from captures anyway, but older
            // Windows builds can't do that, and it would cover what you are looking at).
            if (!avoid.IsEmpty && r.IntersectsWith(avoid))
            {
                var below = new Rectangle(r.X, screen.Bottom - h - S(56), w, h);
                var left = new Rectangle(screen.X + S(16), r.Y, w, h);
                r = !below.IntersectsWith(avoid) ? below : !left.IntersectsWith(avoid) ? left : r;
            }
            if (!Visible) { Bounds = r; Show(); }
            else Bounds = r;
        }
        // Rounded corners: the form itself is shaped, nothing to click anyway.
        using (var path = Theme.Round(new RectangleF(0, 0, Width, Height), Theme.RadiusLg * _scale))
            Region = new Region(path);
        Invalidate();
    }

    Font TitleFont() => _big ? Theme.UiFont(48, true, _scale) : Theme.UiFont(Theme.TypeBase, true, _scale);

    int S(float v) => (int)Math.Round(v * _scale);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var ft = TitleFont();
        using var fd = Theme.UiFont(Theme.TypeSm, false, _scale);
        using var tb = new SolidBrush(_big ? Theme.Accent : Theme.Text);
        using var db = new SolidBrush(Theme.TextMuted);
        var a = g.MeasureString(_title, ft);
        g.DrawString(_title, ft, tb, (Width - a.Width) / 2, S(10));
        if (_detail.Length > 0)
        {
            var b = g.MeasureString(_detail, fd);
            g.DrawString(_detail, fd, db, (Width - b.Width) / 2, S(10) + a.Height);
        }
        using var border = new Pen(Theme.Accent, Math.Max(1, S(1)));
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.RadiusLg * _scale);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawPath(border, path);
    }
}
