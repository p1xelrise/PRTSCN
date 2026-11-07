using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PRTSCN.Recording;

/// <summary>
/// Draw while recording: a see-through window over the recorded area that holds pen strokes.
/// It is a normal (captured) window, so the strokes land in the video exactly as you see them.
/// Drawing mode makes it take the mouse; otherwise clicks pass through to the apps below and
/// the strokes just stay on screen until cleared.
/// Keys while drawing: 1–8 colour, [ ] or the mouse wheel pen size, Ctrl+Z undo, Delete clear,
/// Esc stop drawing. Right-click undoes. The record bar also shows colours and sizes to click.
/// </summary>
internal sealed class DrawLayer : Form
{
    sealed class Stroke { public Color Color; public float Width; public List<PointF> Points = new(); }

    /// <summary>One stroke in screen pixels, for the recorder to draw itself.</summary>
    public sealed record StrokeCopy(Color Color, float Width, PointF[] Points);

    volatile StrokeCopy[] _copies = Array.Empty<StrokeCopy>();

    /// <summary>
    /// The strokes in screen pixels, safe to read from any thread. A window recording captures only
    /// that window, not this layer above it, so the recorder draws these into the video instead.
    /// </summary>
    public StrokeCopy[] Strokes => _copies;

    readonly List<Stroke> _strokes = new();
    Stroke? _current;
    bool _drawMode;
    Color _color;
    float _width;
    readonly Action _stopDrawing;

    /// <summary>The colour or width changed from the keyboard or mouse wheel (so the bar can follow).</summary>
    public event Action? InkChanged;

    /// <summary>Pen colour for the next strokes.</summary>
    public Color Ink { get => _color; set => _color = value; }

    /// <summary>Pen width for the next strokes, in logical pixels (2–40).</summary>
    public float InkWidth { get => _width; set => _width = Math.Clamp(value, MinWidth, MaxWidth); }
    public const float MinWidth = 2, MaxWidth = 40;

    public DrawLayer(Color color, float width, Action stopDrawing)
    {
        _color = color;
        InkWidth = width;
        _stopDrawing = stopDrawing;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = "PRTSCN drawing";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | 0x80 /* TOOLWINDOW */ | 0x8 /* TOPMOST */ | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public bool HasStrokes => _strokes.Count > 0;

    public bool DrawMode
    {
        get => _drawMode;
        set
        {
            if (_drawMode == value) return;
            _drawMode = value;
            if (!IsHandleCreated) return;
            long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            ex = value ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
            Cursor = value ? Cursors.Cross : Cursors.Default;
            Render();
            if (value) { Activate(); Focus(); }
        }
    }

    /// <summary>Cover this area (physical pixels). Strokes keep their place relative to it.</summary>
    public void SetArea(Rectangle area)
    {
        if (Visible && Bounds == area) return;
        Bounds = area;
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, 0x10 /* NOACTIVATE */);
        Render();
    }

    public void Clear() { _strokes.Clear(); _current = null; Render(); }

    /// <summary>Developer check: add a stroke (window pixels) as if drawn.</summary>
    internal void AddStroke(params PointF[] points)
    {
        var s = new Stroke { Color = _color, Width = PenWidth };
        s.Points.AddRange(points);
        _strokes.Add(s);
        Render();
    }

    public void Undo()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        Render();
    }

    float PenWidth => _width * Native.ScaleAt(Left + Width / 2, Top + Height / 2);

    void ChangeWidth(float by)
    {
        float w = InkWidth;
        InkWidth = w + by;
        if (InkWidth != w) InkChanged?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_drawMode) ChangeWidth(Math.Sign(e.Delta) * (InkWidth >= 10 ? 2 : 1));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_drawMode) return;
        if (e.Button == MouseButtons.Right) { Undo(); return; }
        if (e.Button != MouseButtons.Left) return;
        _current = new Stroke { Color = _color, Width = PenWidth };
        _current.Points.Add(e.Location);
        _strokes.Add(_current);
        Render();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_current == null) return;
        var last = _current.Points[^1];
        if (Math.Abs(last.X - e.X) + Math.Abs(last.Y - e.Y) < 2) return;
        _current.Points.Add(e.Location);
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _current = null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: _stopDrawing(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Delete: case Keys.Back: Clear(); return true;
            case Keys.OemOpenBrackets: case Keys.Subtract: case Keys.OemMinus: ChangeWidth(InkWidth > 10 ? -2 : -1); return true;
            case Keys.OemCloseBrackets: case Keys.Add: case Keys.Oemplus: ChangeWidth(InkWidth >= 10 ? 2 : 1); return true;
        }
        var k = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == 0 && k is >= Keys.D1 and <= Keys.D8) { _color = Theme.Ink[k - Keys.D1]; InkChanged?.Invoke(); return true; }
        if ((keyData & Keys.Modifiers) == 0 && k is >= Keys.NumPad1 and <= Keys.NumPad8) { _color = Theme.Ink[k - Keys.NumPad1]; InkChanged?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Paint the strokes into a per-pixel-alpha bitmap and hand it to the window.</summary>
    void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        _copies = _strokes.Select(s => new StrokeCopy(s.Color, s.Width, s.Points.Select(p => new PointF(p.X + Left, p.Y + Top)).ToArray())).ToArray();
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // While drawing, an almost invisible fill makes the whole area take the mouse.
            g.Clear(_drawMode ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var s in _strokes)
            {
                using var pen = new Pen(s.Color, s.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (s.Points.Count == 1)
                {
                    var p = s.Points[0];
                    using var b = new SolidBrush(s.Color);
                    g.FillEllipse(b, p.X - s.Width / 2, p.Y - s.Width / 2, s.Width, s.Width);
                }
                else g.DrawLines(pen, s.Points.ToArray());
            }
        }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { cx = Width, cy = Height };
            var src = new POINT();
            var pos = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
