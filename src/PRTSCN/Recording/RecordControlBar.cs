using System.Drawing.Drawing2D;
using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>
/// The floating bar while recording: REC dot, timer, file size, pause, restart, discard, stop.
/// Excluded from capture, never takes focus from the app being recorded, drag it by the timer.
/// </summary>
internal sealed class RecordControlBar : Form
{
    sealed class Item
    {
        public IconKind Icon;
        public string Tip = "";
        public string? Label;
        public Rectangle Bounds;
        public Action? Click;
        public bool Primary;
        public bool Active;
        public Color? Swatch;      // ink row: a colour to pick
        public float Dot;          // ink row: a pen width to pick (logical px)
    }

    Item? _draw;
    readonly List<Item> _ink = new();   // second row while drawing: colours, then pen sizes
    Color _inkColor = Color.Red;
    float _inkWidth = 5;

    /// <summary>Show the Draw button as on or off; while on, a second row offers colours and pen sizes.</summary>
    public void SetDrawing(bool on)
    {
        if (_draw == null || _draw.Active == on) return;
        _draw.Active = on;
        if (_ink.Count > 0) Relayout();
        Invalidate();
    }

    /// <summary>Mark the current pen colour and width in the ink row.</summary>
    public void SetInk(Color color, float width)
    {
        _inkColor = color;
        _inkWidth = width;
        Invalidate();
    }

    bool InkRow => _draw?.Active == true && _ink.Count > 0;
    IEnumerable<Item> AllItems => InkRow ? _items.Concat(_ink) : _items;

    readonly List<Item> _items = new();
    readonly Item _pause;
    readonly Item? _mic;
    readonly bool _meters;
    float _sysLevel, _micLevel;
    bool _micMuted;
    readonly ToolTip _tip = new() { InitialDelay = 400, ShowAlways = true };
    Item? _hover, _pressed;
    Rectangle _status;
    float _scale = 1;
    string _time = "00:00", _size = "";
    bool _paused, _blinkOn = true;
    Point _dragFrom;
    bool _dragging, _userMoved;

    public RecordControlBar(Action pauseResume, Action restart, Action discard, Action stop, string stopHint, string pauseHint,
        Action? toggleMic = null, bool meters = false, Action? toggleDraw = null, Action<Color?, float?>? pickInk = null)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Text = "PRTSCN recording";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _meters = meters;
        if (toggleMic != null)
        {
            _mic = new Item { Icon = IconKind.Mic, Tip = "Mute the microphone", Click = toggleMic };
            _items.Add(_mic);
        }
        if (toggleDraw != null)
        {
            _draw = new Item { Icon = IconKind.Pen, Tip = "Draw on the screen (it shows in the video). 1–8 colour, [ ] or wheel size, right-click or Ctrl+Z undo, Delete clears, Esc stops", Click = toggleDraw };
            _items.Add(_draw);
            if (pickInk != null)
            {
                string[] names = { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "White", "Black" };
                for (int i = 0; i < Theme.Ink.Length; i++)
                {
                    var c = Theme.Ink[i];
                    _ink.Add(new Item { Swatch = c, Tip = $"{names[Math.Min(i, names.Length - 1)]}  ({i + 1})", Click = () => pickInk(c, null) });
                }
                foreach (var w in AppSettings.DrawWidthChoices)
                    _ink.Add(new Item { Dot = w, Tip = $"Pen size {w}  ([ ] or the mouse wheel for any size)", Click = () => pickInk(null, w) });
            }
        }
        _pause = new Item { Icon = IconKind.Pause, Tip = $"Pause  ({pauseHint})", Click = pauseResume };
        _items.Add(_pause);
        _items.Add(new Item { Icon = IconKind.Restart, Tip = "Restart: throw this take away and start again", Click = restart });
        _items.Add(new Item { Icon = IconKind.Close, Tip = "Discard: stop and delete this recording", Click = discard });
        _items.Add(new Item { Icon = IconKind.Stop, Label = "Stop", Primary = true, Tip = $"Stop and save  ({stopHint})", Click = stop });
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ExcludeFromCapture(Handle);
    }

    int S(float v) => (int)Math.Round(v * _scale);

    /// <summary>Place the bar next to the recorded area: above its top-right corner, else below, else inside.</summary>
    public void PlaceNear(Rectangle area)
    {
        if (_userMoved) return;
        var screen = Screen.FromRectangle(area).Bounds;
        _scale = Native.ScaleAt(area.X + area.Width / 2, area.Y + area.Height / 2);
        Relayout();
        int gap = S(10);
        int x = Math.Clamp(area.Right - Width, screen.Left + gap, screen.Right - Width - gap);
        int y = area.Top - gap - Height;
        if (y < screen.Top + gap) y = area.Bottom + gap;
        if (y + Height > screen.Bottom - gap) y = area.Top + gap; // full screen: sits inside, at the top
        if (area.Width >= screen.Width - 4 && area.Height >= screen.Height - 4)
            x = screen.Left + (screen.Width - Width) / 2;
        var b = new Rectangle(x, y, Width, Height);
        if (!Visible) { Bounds = b; Show(); }
        else Bounds = b;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
    }

    public void SetState(TimeSpan elapsed, long bytes, bool paused, float sysLevel = 0, float micLevel = 0, bool micMuted = false)
    {
        _time = RecordingMath.Clock(elapsed);
        _size = RecordingMath.FileSize(bytes);
        if (paused != _paused)
        {
            _paused = paused;
            _pause.Icon = paused ? IconKind.Play : IconKind.Pause;
            _pause.Tip = paused ? _pause.Tip.Replace("Pause", "Resume") : _pause.Tip.Replace("Resume", "Pause");
        }
        _blinkOn = !paused || DateTime.Now.Millisecond < 500; // paused: the dot blinks once a second
        // meters fall back smoothly between packets
        _sysLevel = Math.Max(Math.Min(sysLevel, 1), _sysLevel * 0.6f);
        _micLevel = micMuted ? 0 : Math.Max(Math.Min(micLevel, 1), _micLevel * 0.6f);
        if (_mic != null && micMuted != _micMuted)
        {
            _micMuted = micMuted;
            _mic.Icon = micMuted ? IconKind.MicOff : IconKind.Mic;
            _mic.Tip = micMuted ? "Unmute the microphone" : "Mute the microphone";
        }
        Invalidate();
    }

    void Relayout()
    {
        int pad = S(4), b = S(32);
        using var g = CreateGraphics();
        using var mono = new Font("Cascadia Mono", 14 * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = Theme.UiFont(Theme.TypeSm, false, _scale);
        int statusW = S(12) + S(10) + S(8) + (int)g.MeasureString("00:00:00", mono).Width + S(8) + (int)g.MeasureString("000.0 MB", small).Width + S(8) + (_meters ? S(16) : 0);
        _status = new Rectangle(pad, pad, statusW, b);
        int x = _status.Right + S(9);
        foreach (var it in _items)
        {
            int w = it.Label != null ? S(76) : b;
            if (it.Primary) x += S(2);
            it.Bounds = new Rectangle(x, pad, w, b);
            x += w;
        }
        int width = x + pad, height = b + pad * 2;
        if (InkRow)
        {
            // second row: colour swatches, a gap, then pen sizes
            int rh = S(28), y = pad + b + S(2), ix = pad + S(4);
            bool gap = false;
            foreach (var it in _ink)
            {
                if (it.Swatch == null && !gap) { ix += S(12); gap = true; }
                int w = it.Swatch != null ? S(26) : S(30);
                it.Bounds = new Rectangle(ix, y, w, rh);
                ix += w;
            }
            width = Math.Max(width, ix + S(24) + pad); // room for a custom width number
            height = y + rh + pad;
        }
        ClientSize = new Size(width, height);
        using var path = Theme.Round(new RectangleF(0, 0, Width, Height), Theme.RadiusMd * _scale);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Theme.Bg);
        Theme.StrokeRound(g, Theme.Border, 1, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.RadiusMd * _scale);

        // status: dot, time, size
        float d = S(10);
        var dot = new RectangleF(_status.X + S(8), _status.Y + (_status.Height - d) / 2, d, d);
        if (_blinkOn) using (var b = new SolidBrush(_paused ? Theme.Accent : Theme.Danger)) g.FillEllipse(b, dot);
        using var mono = new Font("Cascadia Mono", 14 * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = Theme.UiFont(Theme.TypeSm, false, _scale);
        using var text = new SolidBrush(_paused ? Theme.Accent : Theme.Text);
        using var muted = new SolidBrush(Theme.TextMuted);
        float tx = dot.Right + S(8);
        var ts = g.MeasureString(_time, mono);
        g.DrawString(_time, mono, text, tx, _status.Y + (_status.Height - ts.Height) / 2);
        var label = _paused ? "Paused" : _size;
        var ss = g.MeasureString(label, small);
        g.DrawString(label, small, muted, tx + ts.Width + S(4), _status.Y + (_status.Height - ss.Height) / 2);

        if (_meters) DrawMeters(g);

        using (var sep = new Pen(Theme.Border, 1))
            g.DrawLine(sep, _status.Right + S(4), _status.Y + S(6), _status.Right + S(4), _status.Bottom - S(6));

        foreach (var it in _items)
        {
            var r = it.Bounds;
            bool hover = it == _hover, pressed = it == _pressed;
            var inner = Rectangle.Inflate(r, -S(2), -S(2));
            if (it.Primary)
            {
                var c = pressed ? ControlPaint.Dark(Theme.Danger, 0.05f) : hover ? ControlPaint.Light(Theme.Danger, 0.2f) : Theme.Danger;
                Theme.FillRound(g, c, inner, Theme.RadiusSm * _scale);
                int isz = S(16);
                var box = new RectangleF(inner.X + S(8), inner.Y + (inner.Height - isz) / 2f, isz, isz);
                Icons.Draw(g, it.Icon, box, Theme.OnAccent, _scale, Theme.OnAccent);
                using var f = Theme.UiFont(Theme.TypeBase, true, _scale);
                using var tb = new SolidBrush(Theme.OnAccent);
                var ls = g.MeasureString(it.Label!, f);
                g.DrawString(it.Label!, f, tb, box.Right + S(4), inner.Y + (inner.Height - ls.Height) / 2);
                continue;
            }
            if (it.Active) Theme.FillRound(g, Theme.Accent, inner, Theme.RadiusSm * _scale);
            else if (pressed) Theme.FillRound(g, Theme.SurfaceActive, inner, Theme.RadiusSm * _scale);
            else if (hover) Theme.FillRound(g, Theme.Surface, inner, Theme.RadiusSm * _scale);
            var col = it.Active ? Theme.OnAccent : it.Icon == IconKind.MicOff || (hover && it.Icon == IconKind.Close) ? Theme.Danger : hover ? Theme.Text : Theme.TextMuted;
            int sz = S(Theme.SizeIcon);
            Icons.Draw(g, it.Icon, new RectangleF(r.X + (r.Width - sz) / 2f, r.Y + (r.Height - sz) / 2f, sz, sz), col, _scale, col);
        }
        if (InkRow) DrawInkRow(g);
    }

    /// <summary>Colour swatches and pen sizes; the current ones get a ring.</summary>
    void DrawInkRow(Graphics g)
    {
        using (var sep = new Pen(Theme.Border, 1))
            g.DrawLine(sep, S(8), _ink[0].Bounds.Y - S(1), Width - S(8), _ink[0].Bounds.Y - S(1));
        foreach (var it in _ink)
        {
            var r = it.Bounds;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            if (it == _hover) Theme.FillRound(g, Theme.Surface, Rectangle.Inflate(r, -S(1), -S(1)), Theme.RadiusSm * _scale);
            if (it.Swatch is Color c)
            {
                float d = S(16);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                using (var edge = new Pen(Theme.Border, 1)) g.DrawEllipse(edge, cx - d / 2, cy - d / 2, d, d);
                if (c.ToArgb() == _inkColor.ToArgb())
                {
                    float o = d + S(6);
                    using var ring = new Pen(Theme.Text, 1.5f * _scale);
                    g.DrawEllipse(ring, cx - o / 2, cy - o / 2, o, o);
                }
            }
            else
            {
                // the dot shows the width (capped to fit), in the current colour
                float d = Math.Min(it.Dot * _scale, S(20));
                bool on = Math.Abs(it.Dot - _inkWidth) < 0.5f;
                if (on) Theme.FillRound(g, Theme.SurfaceActive, Rectangle.Inflate(r, -S(1), -S(1)), Theme.RadiusSm * _scale);
                using var b = new SolidBrush(_inkColor.GetBrightness() < 0.15f ? Theme.Text : _inkColor);
                g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            }
        }
        // a width not in the list (from [ ] or the wheel): say it
        if (!AppSettings.DrawWidthChoices.Any(w => Math.Abs(w - _inkWidth) < 0.5f))
        {
            using var small = Theme.UiFont(Theme.TypeSm, false, _scale);
            using var tb = new SolidBrush(Theme.TextMuted);
            var last = _ink[^1].Bounds;
            g.DrawString($"{_inkWidth:0}", small, tb, last.Right + S(4), last.Y + S(6));
        }
    }

    /// <summary>Two small level bars at the right of the status: computer sound, then microphone.</summary>
    void DrawMeters(Graphics g)
    {
        float w = S(4), h = _status.Height - S(14), bottom = _status.Bottom - S(7);
        float x = _status.Right - S(16);
        foreach (var (level, on) in new[] { (_sysLevel, true), (_micLevel, _mic != null && !_micMuted) })
        {
            using (var track = new SolidBrush(Theme.SurfaceActive)) g.FillRectangle(track, x, bottom - h, w, h);
            if (on && level > 0.005f)
            {
                // perceptual scale: quiet sound still moves the bar
                float v = (float)Math.Clamp(1 + Math.Log10(level) / 2.5, 0, 1);
                var c = level > 0.95f ? Theme.Danger : level > 0.5f ? Theme.Accent : Theme.Success;
                using var b = new SolidBrush(c);
                g.FillRectangle(b, x, bottom - h * v, w, h * v);
            }
            x += w + S(3);
        }
    }

    Item? Hit(Point p) => AllItems.FirstOrDefault(i => i.Bounds.Contains(p));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = Hit(e.Location);
        if (_pressed == null) { _dragging = true; _dragFrom = e.Location; }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Location = new Point(Location.X + e.X - _dragFrom.X, Location.Y + e.Y - _dragFrom.Y);
            _userMoved = true;
            return;
        }
        var h = Hit(e.Location);
        if (h != _hover)
        {
            _hover = h;
            if (h != null) _tip.Show(h.Tip, this, h.Bounds.X, Height + S(4), 3000);
            else _tip.Hide(this);
            Invalidate();
        }
        Cursor = h != null ? Cursors.Hand : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        var p = _pressed;
        _pressed = null;
        Invalidate();
        if (p != null && Hit(e.Location) == p) p.Click?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
