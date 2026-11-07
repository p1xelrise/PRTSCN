using System.Numerics;
using PRTSCN.Core;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;

namespace PRTSCN.Recording;

/// <summary>What to draw over one video frame. Positions are in output (video) pixels.</summary>
internal sealed class OverlayFrame
{
    public Vector2? Pointer;
    /// <summary>Canvas pixels to output pixels.</summary>
    public float Scale = 1;
    public bool Spotlight, Magnifier;
    /// <summary>Magnifier zoom.</summary>
    public float Zoom2 = 2;
    public List<(Vector2 Pos, bool Right, double Age)> Clicks = new();
    public bool LeftDown, RightDown;
    public List<(string Text, float Opacity)> Keys = new();
    /// <summary>The part of the canvas that fills the frame (auto zoom); null = all of it.</summary>
    public RectangleF? Zoom;
    /// <summary>Pen strokes drawn while recording a window (colour, width and points in output pixels).</summary>
    public List<(Color4 Color, float Width, Vector2[] Points)> Strokes = new();
}

/// <summary>Text and/or an image drawn in a corner of every frame.</summary>
internal sealed record Watermark(string Text, string ImagePath, Corner Corner, float Opacity)
{
    public static Watermark? From(AppSettings s) => s.HasWatermark
        ? new Watermark(s.WatermarkText, s.WatermarkImage, s.WatermarkCorner, s.WatermarkOpacity / 100f) : null;
}

/// <summary>
/// Draws the captured canvas into the output frame with Direct2D (scaling when the source is
/// larger than encoders allow), then the overlays: spotlight, magnifier, click ripples and keystrokes.
/// All calls happen under the recorder's GPU lock.
/// </summary>
internal sealed class FrameCompositor : IDisposable
{
    static readonly Color4 LeftClick = new(1f, 0.69f, 0.125f, 1f);   // Theme.Accent #ffb020
    static readonly Color4 RightClick = new(0.31f, 0.63f, 1f, 1f);   // #4ea1ff
    static readonly Color4 PillBg = new(0.071f, 0.075f, 0.094f, 0.88f); // Theme.Bg #121318

    readonly ID2D1Factory1 _factory;
    readonly ID2D1Device _device;
    readonly ID2D1DeviceContext _dc;
    readonly ID2D1Bitmap1 _source, _target;
    readonly ID2D1BitmapBrush1 _lensBrush;
    readonly ID2D1SolidColorBrush _brush;
    readonly IDWriteFactory _dwrite;
    readonly IDWriteTextFormat _keyFont;
    readonly Vortice.RawRectF _dest;
    readonly Vector2 _outSize;
    readonly bool _scaled;
    readonly float _u;
    readonly Vector2 _canvasSize;
    readonly Watermark? _wm;
    readonly ID2D1Bitmap1? _wmImage;
    readonly IDWriteTextLayout? _wmText;

    public FrameCompositor(ID3D11Device d3d, ID3D11Texture2D canvas, ID3D11Texture2D output, Watermark? watermark = null)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded);
        using (var dxgi = d3d.QueryInterface<IDXGIDevice>()) _device = _factory.CreateDevice(dxgi);
        _dc = _device.CreateDeviceContext(DeviceContextOptions.None);
        var pf = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);
        using (var s = canvas.QueryInterface<IDXGISurface>())
            _source = _dc.CreateBitmapFromDxgiSurface(s, new BitmapProperties1(pf, 96, 96, BitmapOptions.None));
        using (var t = output.QueryInterface<IDXGISurface>())
            _target = _dc.CreateBitmapFromDxgiSurface(t, new BitmapProperties1(pf, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _dc.Target = _target;
        var od = output.Description;
        _dest = new Vortice.RawRectF(0, 0, od.Width, od.Height);
        _outSize = new Vector2(od.Width, od.Height);
        var cd = canvas.Description;
        _scaled = cd.Width != od.Width || cd.Height != od.Height;
        _u = OverlayMath.Unit(new System.Drawing.Size((int)od.Width, (int)od.Height));

        _brush = _dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _lensBrush = _dc.CreateBitmapBrush(_source, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.Linear));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _keyFont = _dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 22 * _u);
        _canvasSize = new Vector2(cd.Width, cd.Height);

        _wm = watermark;
        if (_wm != null)
        {
            if (!string.IsNullOrWhiteSpace(_wm.ImagePath))
            {
                try { _wmImage = LoadImage(_wm.ImagePath); }
                catch (Exception ex) { CrashLog.Write(ex); } // a missing or broken image: text only
            }
            if (!string.IsNullOrWhiteSpace(_wm.Text))
            {
                using var fmt = _dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 20 * _u);
                _wmText = _dwrite.CreateTextLayout(_wm.Text, fmt, _outSize.X, _outSize.Y);
            }
        }
    }

    /// <summary>Load a PNG/JPG into a premultiplied Direct2D bitmap.</summary>
    unsafe ID2D1Bitmap1 LoadImage(string path)
    {
        using var src = new System.Drawing.Bitmap(path);
        using var bmp = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.None);
            return _dc.CreateBitmap(new Vortice.Mathematics.SizeI(bmp.Width, bmp.Height), bd.Scan0, (uint)bd.Stride, props);
        }
        finally { bmp.UnlockBits(bd); }
    }

    public void Compose(OverlayFrame? o = null)
    {
        _dc.BeginDraw();
        _dc.Clear(new Color4(0, 0, 0, 1));
        if (o?.Zoom is RectangleF z && (z.Width < _canvasSize.X - 0.5f || z.Height < _canvasSize.Y - 0.5f))
            _dc.DrawBitmap(_source, _dest, 1f, InterpolationMode.HighQualityCubic, new Vortice.RawRectF(z.Left, z.Top, z.Right, z.Bottom), null);
        else
            _dc.DrawBitmap(_source, _dest, 1f, _scaled ? InterpolationMode.HighQualityCubic : InterpolationMode.NearestNeighbor, null, null);
        if (o != null)
        {
            if (o.Strokes.Count > 0) DrawStrokes(o.Strokes);
            if (o.Spotlight && o.Pointer is Vector2 sp) DrawSpotlight(sp);
            if (o.Magnifier && o.Pointer is Vector2 mp) DrawMagnifier(mp, o.Scale, o.Zoom2, o.Zoom?.Location ?? default);
            foreach (var c in o.Clicks) DrawRipple(c.Pos, c.Right, c.Age);
            if (o.Pointer is Vector2 hp && (o.LeftDown || o.RightDown)) DrawHeld(hp, o.RightDown && !o.LeftDown);
            if (o.Keys.Count > 0) DrawKeys(o.Keys);
        }
        if (_wm != null) DrawWatermark();
        _dc.EndDraw(out _, out _);
    }

    ID2D1StrokeStyle1? _roundPen;

    /// <summary>Pen strokes with round ends and joins, as the drawing layer shows them on screen.</summary>
    void DrawStrokes(List<(Color4 Color, float Width, Vector2[] Points)> strokes)
    {
        _roundPen ??= _factory.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round, DashCap = CapStyle.Round,
        });
        var aa = _dc.AntialiasMode;
        _dc.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var (color, width, pts) in strokes)
        {
            _brush.Color = color;
            if (pts.Length == 1)
            {
                _dc.FillEllipse(new Ellipse(pts[0], width / 2, width / 2), _brush);
                continue;
            }
            using var path = _factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(pts[0], FigureBegin.Hollow);
                for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            _dc.DrawGeometry(path, _brush, width, _roundPen);
        }
        _dc.AntialiasMode = aa;
    }

    void DrawWatermark()
    {
        float margin = 16 * _u, gap = 6 * _u;
        System.Drawing.SizeF img = default, txt = default;
        if (_wmImage != null)
        {
            var s = _wmImage.PixelSize;
            img = WatermarkMath.Fit(new System.Drawing.SizeF(s.Width, s.Height), new System.Drawing.SizeF(_outSize.X, _outSize.Y));
        }
        if (_wmText != null) { var m = _wmText.Metrics; txt = new System.Drawing.SizeF(m.Width, m.Height); }
        // image above the text, both aligned to the corner's side
        var total = new System.Drawing.SizeF(Math.Max(img.Width, txt.Width), img.Height + txt.Height + (img.Height > 0 && txt.Height > 0 ? gap : 0));
        var box = WatermarkMath.Place(new System.Drawing.SizeF(_outSize.X, _outSize.Y), total, _wm!.Corner, margin);
        bool right = _wm.Corner is Corner.TopRight or Corner.BottomRight;
        float a = _wm.Opacity;
        if (_wmImage != null)
        {
            float x = right ? box.Right - img.Width : box.Left;
            _dc.DrawBitmap(_wmImage, new Vortice.RawRectF(x, box.Top, x + img.Width, box.Top + img.Height), a, InterpolationMode.HighQualityCubic, null, null);
        }
        if (_wmText != null)
        {
            float x = right ? box.Right - txt.Width : box.Left, y = box.Bottom - txt.Height;
            _brush.Color = new Color4(0, 0, 0, 0.55f * a);
            _dc.DrawTextLayout(new Vector2(x + 1.5f * _u, y + 1.5f * _u), _wmText, _brush, DrawTextOptions.None);
            _brush.Color = new Color4(1, 1, 1, a);
            _dc.DrawTextLayout(new Vector2(x, y), _wmText, _brush, DrawTextOptions.None);
        }
    }

    void DrawSpotlight(Vector2 p)
    {
        float r = 150 * _u;
        var stops = new[]
        {
            new GradientStop(0f, new Color4(0, 0, 0, 0)),
            new GradientStop(0.8f, new Color4(0, 0, 0, 0)),
            new GradientStop(1f, new Color4(0, 0, 0, 0.6f)),
        };
        using var collection = _dc.CreateGradientStopCollection(stops);
        using var radial = _dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(p, Vector2.Zero, r, r), collection);
        _dc.FillRectangle(_dest, radial);
    }

    void DrawMagnifier(Vector2 p, float scale, float zoom, System.Drawing.PointF origin)
    {
        float r = Math.Clamp(Math.Min(_outSize.X, _outSize.Y) * 0.16f, 70, 260);
        // Canvas point under the pointer stays under the pointer, everything around it zoomed.
        var canvasP = new Vector2(origin.X, origin.Y) + p / scale;
        _lensBrush.Transform = Matrix3x2.CreateTranslation(-canvasP) * Matrix3x2.CreateScale(scale * zoom) * Matrix3x2.CreateTranslation(p);
        var lens = new Ellipse(p, r, r);
        _brush.Color = new Color4(0, 0, 0, 0.35f);
        _dc.FillEllipse(new Ellipse(p + new Vector2(0, 3 * _u), r + 2 * _u, r + 2 * _u), _brush); // soft shadow
        _dc.FillEllipse(lens, _lensBrush);
        _brush.Color = new Color4(1, 1, 1, 0.95f);
        _dc.DrawEllipse(lens, _brush, 3 * _u);
        _brush.Color = new Color4(0, 0, 0, 0.5f);
        _dc.DrawEllipse(new Ellipse(p, r + 2 * _u, r + 2 * _u), _brush, 1 * _u);
    }

    void DrawRipple(Vector2 p, bool right, double age)
    {
        float t = (float)(age / OverlayMath.ClickRippleSeconds);
        if (t >= 1) return;
        float e = OverlayMath.EaseOut(t);
        float r = (12 + 26 * e) * _u;
        var c = right ? RightClick : LeftClick;
        _brush.Color = new Color4(c.R, c.G, c.B, 0.28f * (1 - t));
        _dc.FillEllipse(new Ellipse(p, r, r), _brush);
        _brush.Color = new Color4(c.R, c.G, c.B, 0.95f * (1 - t));
        _dc.DrawEllipse(new Ellipse(p, r, r), _brush, 3 * _u);
    }

    void DrawHeld(Vector2 p, bool right)
    {
        var c = right ? RightClick : LeftClick;
        float r = 11 * _u;
        _brush.Color = new Color4(c.R, c.G, c.B, 0.45f);
        _dc.FillEllipse(new Ellipse(p, r, r), _brush);
    }

    void DrawKeys(List<(string Text, float Opacity)> keys)
    {
        float padX = 14 * _u, padY = 7 * _u, gap = 10 * _u, radius = 9 * _u, bottom = _outSize.Y - 40 * _u;
        var layouts = keys.Select(k => _dwrite.CreateTextLayout(k.Text, _keyFont, _outSize.X, _outSize.Y)).ToList();
        try
        {
            float total = layouts.Sum(l => l.Metrics.Width + padX * 2) + gap * (layouts.Count - 1);
            float x = (_outSize.X - total) / 2;
            for (int i = 0; i < layouts.Count; i++)
            {
                var m = layouts[i].Metrics;
                float w = m.Width + padX * 2, h = m.Height + padY * 2;
                var rect = new Vortice.RawRectF(x, bottom - h, x + w, bottom);
                float a = keys[i].Opacity;
                _brush.Color = new Color4(PillBg.R, PillBg.G, PillBg.B, PillBg.A * a);
                _dc.FillRoundedRectangle(new RoundedRectangle(rect, radius, radius), _brush);
                _brush.Color = new Color4(1, 1, 1, 0.12f * a);
                _dc.DrawRoundedRectangle(new RoundedRectangle(rect, radius, radius), _brush, 1 * _u);
                _brush.Color = new Color4(0.95f, 0.953f, 0.961f, a);
                _dc.DrawTextLayout(new Vector2(x + padX, bottom - h + padY), layouts[i], _brush, DrawTextOptions.None);
                x += w + gap;
            }
        }
        finally { foreach (var l in layouts) l.Dispose(); }
    }

    public void Dispose()
    {
        _dc.Target = null;
        _wmImage?.Dispose();
        _wmText?.Dispose();
        _roundPen?.Dispose();
        _keyFont.Dispose();
        _dwrite.Dispose();
        _lensBrush.Dispose();
        _brush.Dispose();
        _target.Dispose();
        _source.Dispose();
        _dc.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }
}
