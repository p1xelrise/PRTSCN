using System.Drawing.Drawing2D;

namespace PRTSCN.Recording;

/// <summary>
/// The dashed red frame around what is being recorded. A click-through, never-activated window
/// that is excluded from capture, drawn just outside the area (inside the edges for full screen).
/// </summary>
internal sealed class RecordFrame : Form
{
    const int Pad = 4;          // frame window extends this far beyond the area
    Rectangle _area;
    bool _paused;

    public RecordFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
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
        Native.ExcludeFromCapture(Handle);
    }

    /// <summary>Place the frame around an area (physical pixels), clipped to its monitor.</summary>
    public void SetArea(Rectangle area, bool paused)
    {
        if (area == _area && paused == _paused && Visible) return;
        _area = area;
        _paused = paused;
        var screen = Screen.FromRectangle(area).Bounds;
        var b = Rectangle.Intersect(Rectangle.Inflate(area, Pad, Pad), screen);
        if (!Visible) { Bounds = b; Show(); }
        else if (Bounds != b) Bounds = b;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Magenta);
        float scale = Native.ScaleAt(_area.X + _area.Width / 2, _area.Y + _area.Height / 2);
        int w = Math.Max(2, (int)Math.Round(2 * scale));
        // The line runs just outside the area; where the area touches the screen edge, just inside it.
        var r = _area;
        r.Offset(-Left, -Top);
        float half = w / 2f;
        var f = RectangleF.FromLTRB(
            Math.Max(half, r.Left - half - 1), Math.Max(half, r.Top - half - 1),
            Math.Min(Width - half, r.Right + half + 1), Math.Min(Height - half, r.Bottom + half + 1));
        using var pen = new Pen(_paused ? Theme.Accent : Theme.Danger, w) { DashStyle = DashStyle.Custom, DashPattern = new[] { 4f, 3f } };
        g.SmoothingMode = SmoothingMode.None;
        g.DrawRectangle(pen, f.X, f.Y, f.Width, f.Height);
    }
}
