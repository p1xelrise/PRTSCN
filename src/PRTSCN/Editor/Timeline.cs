using System.Drawing.Drawing2D;
using PRTSCN.Core;

namespace PRTSCN.Editor;

/// <summary>
/// The editor's timeline: a time ruler, a strip of thumbnails, the sound wave, trim handles,
/// removed parts (red, hatched) and the playhead. Drag across the tracks to select a part,
/// drag a handle to trim, click a removed part to put it back, click or drag the ruler to seek.
/// Blur boxes show as blue bars: drag an end to change when the blur starts or stops, drag the
/// middle to move it in time, click it to jump to its start.
/// </summary>
internal sealed class Timeline : Control
{
    readonly EditList _edits;
    readonly float _s;
    long _playhead;
    TimeRange? _selection;
    List<(long time, Bitmap bmp)> _thumbs = new();
    float[]? _peaks;
    bool _hasVideo = true, _hasAudio = true;

    enum Drag { None, TrimIn, TrimOut, Scrub, Select, Pending, BlurStart, BlurEnd, BlurMove }
    Drag _drag;
    Point _down;
    long _downTime;
    int _hoverCut = -1, _hoverBlur = -1, _dragBlur = -1;
    BlurBox _blurAtDown;
    bool _blurMoved;
    bool _hoverHandle;

    /// <summary>The user moved the playhead (source time).</summary>
    public event Action<long>? Seek;
    /// <summary>Raised before the edit list changes (to save an undo step).</summary>
    public event Action? BeforeEdit;
    /// <summary>The trim or the removed parts changed.</summary>
    public event Action? EditsChanged;
    public event Action? SelectionChanged;

    public Timeline(EditList edits, float scale)
    {
        _edits = edits;
        _s = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Height = (int)(132 * scale);
        Font = new Font("Cascadia Mono", 8.25f);
        if (Font.Name != "Cascadia Mono") Font = new Font("Consolas", 8.25f);
    }

    public long Playhead { get => _playhead; set { if (_playhead == value) return; _playhead = value; Invalidate(); } }
    long? _mark;
    /// <summary>Where a two-step cut starts (Cut pressed with nothing selected); the part up to the playhead is previewed.</summary>
    public long? Mark { get => _mark; set { _mark = value; Invalidate(); } }
    public TimeRange? Selection
    {
        get => _selection;
        set { _selection = value; Invalidate(); SelectionChanged?.Invoke(); }
    }

    public void SetTracks(bool video, bool audio) { _hasVideo = video; _hasAudio = audio; Invalidate(); }
    public void SetThumbs(List<(long, Bitmap)> thumbs) { foreach (var t in _thumbs) t.bmp.Dispose(); _thumbs = thumbs; Invalidate(); }
    public void SetPeaks(float[] peaks) { _peaks = peaks; Invalidate(); }
    /// <summary>How many thumbnails fill the strip, and their size.</summary>
    public (int count, Size size) ThumbLayout(Size video)
    {
        int h = (int)(TrackVideo.Height);
        int w = Math.Max(16, (int)(h * video.Width / (double)Math.Max(1, video.Height)));
        return (Math.Clamp((int)Math.Ceiling(Track.Width / (double)w), 1, 60), new Size(w, h));
    }

    // ---------- geometry ----------

    float Pad => 14 * _s;
    RectangleF Ruler => new(Pad, 8 * _s, Width - Pad * 2, 16 * _s);
    RectangleF Track => new(Pad, Ruler.Bottom + 6 * _s, Width - Pad * 2, Height - Ruler.Bottom - 6 * _s - 10 * _s);
    RectangleF TrackVideo
    {
        get
        {
            var t = Track;
            if (!_hasAudio) return t;
            if (!_hasVideo) return new RectangleF(t.X, t.Y, t.Width, 0);
            return new RectangleF(t.X, t.Y, t.Width, t.Height * 0.62f);
        }
    }
    RectangleF TrackAudio
    {
        get
        {
            var t = Track;
            if (!_hasAudio) return new RectangleF(t.X, t.Bottom, t.Width, 0);
            if (!_hasVideo) return t;
            var v = TrackVideo;
            return new RectangleF(t.X, v.Bottom + 4 * _s, t.Width, t.Bottom - v.Bottom - 4 * _s);
        }
    }

    /// <summary>The blue bar of blur box i, along the bottom of the video strip (four rows).</summary>
    RectangleF BlurBar(int i)
    {
        var tv = TrackVideo;
        var b = _edits.Blurs[i].Range;
        float h = 5 * _s, y = tv.Bottom - h - (i % 4) * (h + 2 * _s);
        return RectangleF.FromLTRB(X(b.Start), y, Math.Max(X(b.End), X(b.Start) + 2), y + h);
    }

    /// <summary>The blur bar under p (newest first) and which part: -1 start, 1 end, 0 middle.</summary>
    int BlurHit(Point p, out int part)
    {
        part = 0;
        if (TrackVideo.Height <= 0) return -1;
        float end = 6 * _s, pad = 3 * _s;
        for (int i = _edits.Blurs.Count - 1; i >= 0; i--)
        {
            var r = BlurBar(i);
            if (p.Y < r.Y - pad || p.Y > r.Bottom + pad || p.X < r.X - end || p.X > r.Right + end) continue;
            float ds = Math.Abs(p.X - r.X), de = Math.Abs(p.X - r.Right);
            if (Math.Min(ds, de) <= end) part = ds <= de ? -1 : 1;
            else if (p.X < r.X || p.X > r.Right) continue;
            return i;
        }
        return -1;
    }

    /// <summary>Dev check: a point on blur bar i (-1 start, 0 middle, 1 end) and the x of a time.</summary>
    internal Point BlurBarPoint(int i, int part)
    {
        var r = BlurBar(i);
        float x = part < 0 ? r.X : part > 0 ? r.Right : (r.X + r.Right) / 2;
        return new Point((int)x, (int)(r.Y + r.Height / 2));
    }
    internal int XOf(long t) => (int)X(t);

    /// <summary>Dev check: press, move in steps and release, as a real mouse drag would.</summary>
    internal void SimulateDrag(Point from, Point to)
    {
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, from.X, from.Y, 0));
        for (int k = 1; k <= 8; k++)
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, from.X + (to.X - from.X) * k / 8, from.Y + (to.Y - from.Y) * k / 8, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0));
    }

    float X(long t) => Track.X + (float)(t / (double)_edits.Duration * Track.Width);
    long T(float x) => (long)Math.Clamp((x - Track.X) / Track.Width * _edits.Duration, 0, _edits.Duration);

    // ---------- painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        Theme.FillRound(g, Theme.Surface, new RectangleF(0, 0, Width, Height), 6 * _s);
        DrawRuler(g);
        var tv = TrackVideo; var ta = TrackAudio;

        // video strip
        if (tv.Height > 0)
        {
            var clip = g.Clip;
            using (var path = Theme.Round(tv, 4 * _s)) g.SetClip(path);
            using (var b = new SolidBrush(Theme.SurfaceActive)) g.FillRectangle(b, tv);
            if (_thumbs.Count > 0)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                var sz = _thumbs[0].bmp.Size;
                float w = sz.Width * (tv.Height / sz.Height);
                for (float x = tv.X; x < tv.Right; x += w)
                {
                    long t = T(x + w / 2);
                    var best = _thumbs.MinBy(th => Math.Abs(th.time - t));
                    g.DrawImage(best.bmp, x, tv.Y, w, tv.Height);
                    using var sep = new Pen(Theme.WithAlpha(Theme.Bg, 120));
                    g.DrawLine(sep, x, tv.Y, x, tv.Bottom);
                }
            }
            g.Clip = clip;
        }

        // sound wave
        if (ta.Height > 0)
        {
            using (var b = new SolidBrush(Theme.WithAlpha(Theme.SurfaceActive, 140))) g.FillRectangle(b, ta);
            if (_peaks is { Length: > 0 } peaks)
            {
                using var pen = new Pen(Theme.WithAlpha(Theme.Success, 190), 1);
                float mid = ta.Y + ta.Height / 2;
                for (int x = (int)ta.X; x < (int)ta.Right; x++)
                {
                    int i0 = (int)((x - ta.X) / ta.Width * peaks.Length), i1 = (int)((x + 1 - ta.X) / ta.Width * peaks.Length);
                    float p = 0;
                    for (int i = i0; i <= Math.Min(i1, peaks.Length - 1); i++) p = Math.Max(p, peaks[i]);
                    float h = Math.Max(1, p * ta.Height * 0.95f);
                    g.DrawLine(pen, x + 0.5f, mid - h / 2, x + 0.5f, mid + h / 2);
                }
            }
        }

        var all = RectangleF.FromLTRB(Track.X, Track.Y, Track.Right, Track.Bottom);

        // removed parts
        for (int i = 0; i < _edits.Cuts.Count; i++)
        {
            var c = _edits.Cuts[i];
            var r = RectangleF.FromLTRB(X(c.Start), all.Y, Math.Max(X(c.End), X(c.Start) + 2), all.Bottom);
            using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Theme.WithAlpha(Theme.Danger, i == _hoverCut ? 150 : 100), Theme.WithAlpha(Theme.Danger, i == _hoverCut ? 60 : 35));
            g.FillRectangle(hatch, r);
            using var pen = new Pen(Theme.Danger, 1);
            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        // blurred parts: a blue bar along the bottom of the video strip, one row per box
        if (_edits.Blurs.Count > 0 && tv.Height > 0)
        {
            using var blue = new SolidBrush(Color.FromArgb(220, 0x4e, 0xa1, 0xff));
            using var lit = new SolidBrush(Color.FromArgb(255, 0x8c, 0xc4, 0xff));
            using var grip = new SolidBrush(Color.White);
            using var gone = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(200, 0x4e, 0xa1, 0xff), Color.FromArgb(50, 0x4e, 0xa1, 0xff));
            for (int i = 0; i < _edits.Blurs.Count; i++)
            {
                var r = BlurBar(i);
                bool hot = i == _hoverBlur || (i == _dragBlur && _drag is Drag.BlurStart or Drag.BlurEnd or Drag.BlurMove);
                // a blur whose time was all removed won't be in the saved video: draw it faded
                g.FillRectangle(hot ? lit : _edits.BlurHidden(_edits.Blurs[i]) ? gone : blue, r);
                if (hot)
                {
                    // the ends can be dragged: small white grips
                    float gw = 2 * _s;
                    g.FillRectangle(grip, r.X, r.Y - _s, gw, r.Height + 2 * _s);
                    g.FillRectangle(grip, r.Right - gw, r.Y - _s, gw, r.Height + 2 * _s);
                }
            }
        }

        // trimmed-off ends
        using (var dim = new SolidBrush(Theme.WithAlpha(Theme.Bg, 200)))
        {
            if (_edits.TrimIn > 0) g.FillRectangle(dim, RectangleF.FromLTRB(all.X, all.Y, X(_edits.TrimIn), all.Bottom));
            if (_edits.TrimOut < _edits.Duration) g.FillRectangle(dim, RectangleF.FromLTRB(X(_edits.TrimOut), all.Y, all.Right, all.Bottom));
        }

        // selection
        if (_selection is { } sel && sel.Length > 0)
        {
            var r = RectangleF.FromLTRB(X(sel.Start), all.Y - 2 * _s, X(sel.End), all.Bottom + 2 * _s);
            using (var b = new SolidBrush(Theme.WithAlpha(Theme.Accent, 45))) g.FillRectangle(b, r);
            using var pen = new Pen(Theme.Accent, 1.5f * _s);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        // cut mark: the part from the mark to the playhead will be removed
        if (_mark is { } mk)
        {
            float mx = X(mk), hx = X(_playhead);
            var r = RectangleF.FromLTRB(Math.Min(mx, hx), all.Y, Math.Max(mx, hx), all.Bottom);
            using (var b = new SolidBrush(Theme.WithAlpha(Theme.Danger, 55))) g.FillRectangle(b, r);
            using var pen = new Pen(Theme.Danger, 2 * _s) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, mx, all.Y - 4 * _s, mx, all.Bottom + 4 * _s);
        }

        // trim handles
        DrawHandle(g, X(_edits.TrimIn), true);
        DrawHandle(g, X(_edits.TrimOut), false);

        // playhead
        float px = X(_playhead);
        using (var pen = new Pen(Theme.Text, 2 * _s)) g.DrawLine(pen, px, Ruler.Bottom - 2 * _s, px, Height - 6 * _s);
        var knob = new RectangleF(px - 6 * _s, Ruler.Bottom - 9 * _s, 12 * _s, 8 * _s);
        Theme.FillRound(g, Theme.Text, knob, 2 * _s);
    }

    void DrawHandle(Graphics g, float x, bool left)
    {
        var all = Track;
        float w = 7 * _s;
        var r = left ? new RectangleF(x - w, all.Y - 2 * _s, w, all.Height + 4 * _s) : new RectangleF(x, all.Y - 2 * _s, w, all.Height + 4 * _s);
        Theme.FillRound(g, Theme.Accent, r, 2.5f * _s);
        using var grip = new Pen(Theme.OnAccent, 1.2f * _s);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        g.DrawLine(grip, cx, cy - 6 * _s, cx, cy + 6 * _s);
    }

    void DrawRuler(Graphics g)
    {
        var r = Ruler;
        double seconds = _edits.Duration / 1e7;
        double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600 };
        double step = steps.FirstOrDefault(st => r.Width / (seconds / st) >= 72 * _s, 3600);
        using var tick = new Pen(Theme.Border, 1);
        for (double t = 0; t <= seconds + 1e-9; t += step)
        {
            float x = r.X + (float)(t / seconds * r.Width);
            g.DrawLine(tick, x, r.Bottom - 4 * _s, x, r.Bottom);
            string label = t >= 3600 ? TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss") : step < 1 ? $"{(int)t / 60}:{t % 60:00.0}" : $"{(int)t / 60}:{(int)t % 60:00}";
            var size = TextRenderer.MeasureText(g, label, Font);
            float lx = Math.Clamp(x - size.Width / 2f, r.X - 4 * _s, r.Right - size.Width + 4 * _s);
            TextRenderer.DrawText(g, label, Font, new Point((int)lx, (int)(r.Y - 2 * _s)), Theme.TextMuted, TextFormatFlags.NoPadding);
        }
    }

    // ---------- mouse ----------

    bool NearHandle(Point p, out bool left)
    {
        left = false;
        if (p.Y < Track.Y - 4 * _s) return false;
        float tol = 9 * _s;
        float xi = X(_edits.TrimIn) - 3.5f * _s, xo = X(_edits.TrimOut) + 3.5f * _s;
        float di = Math.Abs(p.X - xi), dout = Math.Abs(p.X - xo);
        if (Math.Min(di, dout) > tol) return false;
        left = di <= dout;
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _down = e.Location;
        _downTime = T(e.X);
        int bi = BlurHit(e.Location, out int part);
        if (bi >= 0)
        {
            _dragBlur = bi;
            _blurAtDown = _edits.Blurs[bi];
            _blurMoved = false;
            _drag = part < 0 ? Drag.BlurStart : part > 0 ? Drag.BlurEnd : Drag.BlurMove;
        }
        else if (NearHandle(e.Location, out bool left))
        {
            BeforeEdit?.Invoke();
            _drag = left ? Drag.TrimIn : Drag.TrimOut;
        }
        else if (e.Y < Track.Y) { _drag = Drag.Scrub; Seek?.Invoke(_downTime); }
        else _drag = Drag.Pending;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        long t = T(e.X);
        switch (_drag)
        {
            case Drag.TrimIn:
                if (_edits.SetTrim(Math.Min(t, _edits.TrimOut - EditList.MinLength), _edits.TrimOut)) { EditsChanged?.Invoke(); Seek?.Invoke(_edits.TrimIn); }
                break;
            case Drag.TrimOut:
                if (_edits.SetTrim(_edits.TrimIn, Math.Max(t, _edits.TrimIn + EditList.MinLength))) { EditsChanged?.Invoke(); Seek?.Invoke(Math.Max(_edits.TrimIn, _edits.TrimOut - 1)); }
                break;
            case Drag.Scrub:
                Seek?.Invoke(t);
                break;
            case Drag.BlurStart or Drag.BlurEnd or Drag.BlurMove:
                DragBlur(e.X);
                break;
            case Drag.Pending:
                if (Math.Abs(e.X - _down.X) > 4 * _s) _drag = Drag.Select;
                break;
            case Drag.Select:
                Selection = new TimeRange(Math.Min(_downTime, t), Math.Max(_downTime, t));
                Seek?.Invoke(t);
                break;
            default:
                int hb = BlurHit(e.Location, out int part);
                int hc = hb < 0 && e.Y >= Track.Y ? _edits.CutAt(t) : -1;
                bool hh = hb < 0 && NearHandle(e.Location, out _);
                if (hc != _hoverCut || hh != _hoverHandle || hb != _hoverBlur) { _hoverCut = hc; _hoverHandle = hh; _hoverBlur = hb; Invalidate(); }
                Cursor = hb >= 0 ? (part != 0 ? Cursors.SizeWE : Cursors.SizeAll) : hh ? Cursors.SizeWE : hc >= 0 ? Cursors.Hand : Cursors.Default;
                break;
        }
    }

    /// <summary>Move an end of the dragged blur bar, or all of it, to follow the mouse.</summary>
    void DragBlur(int x)
    {
        if (_dragBlur < 0 || _dragBlur >= _edits.Blurs.Count) return;
        if (!_blurMoved && Math.Abs(x - _down.X) <= 2 * _s) return;
        var b = _blurAtDown;
        long s = b.Range.Start, en = b.Range.End, t = T(x), dur = _edits.Duration;
        long min = Math.Min(EditList.MinBlur, Math.Max(1, (en - s) / 2));
        switch (_drag)
        {
            case Drag.BlurStart: s = Math.Clamp(t, 0, en - min); break;
            case Drag.BlurEnd: en = Math.Clamp(t, s + min, dur); break;
            default:
                long d = (long)((x - _down.X) / (double)Track.Width * dur);
                d = Math.Clamp(d, -s, dur - en);
                s += d; en += d;
                break;
        }
        var nb = b with { Range = new TimeRange(s, en) };
        if (nb == _edits.Blurs[_dragBlur]) return;
        if (!_blurMoved) { BeforeEdit?.Invoke(); _blurMoved = true; }
        if (!_edits.SetBlur(_dragBlur, nb)) return;
        EditsChanged?.Invoke();
        Seek?.Invoke(_drag == Drag.BlurEnd ? Math.Max(s, en - 1) : s);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        var d = _drag;
        _drag = Drag.None;
        if (d is Drag.BlurStart or Drag.BlurEnd or Drag.BlurMove)
        {
            // a click on a bar (no drag) jumps to where that blur starts
            if (!_blurMoved && _dragBlur < _edits.Blurs.Count) Seek?.Invoke(_edits.Blurs[_dragBlur].Range.Start);
            _dragBlur = -1;
            Invalidate();
            return;
        }
        if (d == Drag.Pending)
        {
            long t = T(e.X);
            if (_edits.CutAt(t) >= 0)
            {
                BeforeEdit?.Invoke();
                _edits.RemoveCutAt(t);
                _hoverCut = -1;
                EditsChanged?.Invoke();
                Invalidate();
                return;
            }
            Selection = null;
            Seek?.Invoke(t);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCut >= 0 || _hoverHandle || _hoverBlur >= 0) { _hoverCut = -1; _hoverHandle = false; _hoverBlur = -1; Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var t in _thumbs) t.bmp.Dispose();
        base.Dispose(disposing);
    }
}
