using System.Drawing.Drawing2D;
using System.Text;

namespace PRTSCN;

internal enum Tool { None, Pen, Line, Arrow, Rect, Marker, Blur, Step, Text }

/// <summary>Annotations stay as vectors in overlay coordinates until export flattens them.</summary>
internal abstract class Annotation
{
    public Color Color;
    public float Width;
    public abstract void Draw(Graphics g);
    public virtual bool IsEmpty => false;

    protected Pen MakePen(Color c, float w) =>
        new(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
}

internal sealed class StrokeAnnotation : Annotation
{
    public readonly List<PointF> Points = new();
    public bool Marker;

    public float EffectiveWidth => Marker ? Width * 4 + 6 : Width;

    public override void Draw(Graphics g)
    {
        if (Points.Count == 0) return;
        var c = Marker ? Theme.WithAlpha(Color, Theme.AlphaMarker) : Color;
        float w = EffectiveWidth;
        if (Points.Count == 1)
        {
            using var b = new SolidBrush(c);
            var p = Points[0];
            g.FillEllipse(b, p.X - w / 2, p.Y - w / 2, w, w);
            return;
        }
        using var pen = MakePen(c, w);
        if (Marker) { pen.StartCap = pen.EndCap = LineCap.Flat; }
        // One DrawLines call: a single path, so a marker never darkens where it overlaps itself.
        g.DrawLines(pen, Points.ToArray());
    }
}

internal sealed class LineAnnotation : Annotation
{
    public PointF A, B;
    public bool Arrow;
    public override bool IsEmpty => Math.Abs(A.X - B.X) < 2 && Math.Abs(A.Y - B.Y) < 2;

    public override void Draw(Graphics g)
    {
        if (IsEmpty) return;
        using var pen = MakePen(Color, Width);
        if (!Arrow) { g.DrawLine(pen, A, B); return; }

        float dx = B.X - A.X, dy = B.Y - A.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        float headLen = Math.Min(Width * 4 + 10, len * 0.6f), headHalf = (Width * 3 + 8) / 2;
        float ux = dx / len, uy = dy / len;
        var baseC = new PointF(B.X - ux * headLen, B.Y - uy * headLen);
        var left = new PointF(baseC.X - uy * headHalf, baseC.Y + ux * headHalf);
        var right = new PointF(baseC.X + uy * headHalf, baseC.Y - ux * headHalf);
        // Stop the shaft inside the head so the round cap does not poke out of the tip.
        g.DrawLine(pen, A, new PointF(baseC.X + ux * 1, baseC.Y + uy * 1));
        using var b = new SolidBrush(Color);
        g.FillPolygon(b, new[] { B, left, right });
    }
}

internal sealed class RectAnnotation : Annotation
{
    public PointF A, B;
    public override bool IsEmpty => Math.Abs(A.X - B.X) < 2 || Math.Abs(A.Y - B.Y) < 2;

    public override void Draw(Graphics g)
    {
        if (IsEmpty) return;
        using var pen = MakePen(Color, Width);
        pen.LineJoin = LineJoin.Miter;
        g.DrawRectangle(pen, Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Abs(A.X - B.X), Math.Abs(A.Y - B.Y));
    }
}

internal sealed class TextAnnotation : Annotation
{
    public PointF Origin;
    public readonly StringBuilder Text = new();
    public float FontPx => 10 + Width * 3;
    public override bool IsEmpty => Text.ToString().Trim().Length == 0;

    static readonly StringFormat Fmt = MakeFormat();
    static StringFormat MakeFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    Font MakeFont() => new(Theme.Family, FontPx, FontStyle.Bold, GraphicsUnit.Pixel);

    public override void Draw(Graphics g)
    {
        using var font = MakeFont();
        using var b = new SolidBrush(Color);
        var old = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float y = Origin.Y, lh = font.GetHeight(g);
        foreach (var line in Text.ToString().Split('\n'))
        {
            g.DrawString(line, font, b, Origin.X, y, Fmt);
            y += lh;
        }
        g.TextRenderingHint = old;
    }

    /// <summary>Box around the text, and where the caret goes (end of the last line).</summary>
    public (RectangleF box, PointF caretTop, float caretHeight) Measure(Graphics g)
    {
        using var font = MakeFont();
        float lh = font.GetHeight(g), w = 0;
        var lines = Text.ToString().Split('\n');
        float lastW = 0;
        foreach (var line in lines)
        {
            lastW = g.MeasureString(line, font, PointF.Empty, Fmt).Width;
            w = Math.Max(w, lastW);
        }
        var box = new RectangleF(Origin.X, Origin.Y, Math.Max(w, FontPx * 0.6f), lh * lines.Length);
        return (box, new PointF(Origin.X + lastW + 1, Origin.Y + lh * (lines.Length - 1)), lh);
    }
}

/// <summary>
/// Blurs a rectangle of the screenshot underneath. Built from heavy down-scaling (factor 8+),
/// so the original detail is gone from the exported pixels, not just hidden.
/// It blurs the screenshot only, not annotations drawn earlier.
/// </summary>
internal sealed class BlurAnnotation : Annotation, IDisposable
{
    public PointF A, B;
    readonly Bitmap _source;
    Bitmap? _patch;
    Rectangle _patchRect;
    int _patchWidth;

    public BlurAnnotation(Bitmap source) => _source = source;

    public Rectangle Rect => Rectangle.Intersect(
        Rectangle.FromLTRB((int)Math.Min(A.X, B.X), (int)Math.Min(A.Y, B.Y), (int)Math.Ceiling(Math.Max(A.X, B.X)), (int)Math.Ceiling(Math.Max(A.Y, B.Y))),
        new Rectangle(Point.Empty, _source.Size));

    public override bool IsEmpty => Rect.Width < 3 || Rect.Height < 3;

    /// <summary>Down-scale factor: ink width 3 (default) is 14, so 14x14 pixel blocks are averaged.</summary>
    int Factor => 8 + (int)Width * 2;

    public override void Draw(Graphics g)
    {
        var r = Rect;
        if (r.Width < 3 || r.Height < 3) return;
        if (_patch == null || r != _patchRect || _patchWidth != (int)Width) Rebuild(r);
        var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_patch!, r);
        g.InterpolationMode = oldI; g.PixelOffsetMode = oldP;
    }

    void Rebuild(Rectangle r)
    {
        _patch?.Dispose();
        int f = Factor;
        int sw = Math.Max(1, r.Width / f), sh = Math.Max(1, r.Height / f);
        using var small = new Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_source, new Rectangle(0, 0, sw, sh), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, attrs);
        }
        var patch = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(patch))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(small, new Rectangle(0, 0, r.Width, r.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, attrs);
        }
        _patch = patch;
        _patchRect = r;
        _patchWidth = (int)Width;
    }

    public void Dispose() { _patch?.Dispose(); _patch = null; }
}

/// <summary>Numbered step marker: a filled circle with 1, 2, 3...</summary>
internal sealed class StepAnnotation : Annotation
{
    public PointF Center;
    public int Number;
    public float Radius => 9 + Width * 2.5f;

    public override void Draw(Graphics g)
    {
        float r = Radius;
        var box = new RectangleF(Center.X - r, Center.Y - r, r * 2, r * 2);
        var oldS = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color)) g.FillEllipse(b, box);
        using (var ring = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1.5f, r / 8))) g.DrawEllipse(ring, box);
        // Dark digits on light colours, white digits on dark ones.
        double lum = (0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B) / 255;
        using var txt = new SolidBrush(lum > 0.62 ? Color.FromArgb(0x12, 0x13, 0x18) : Color.White);
        using var font = new Font(Theme.Family, r * (Number >= 10 ? 0.95f : 1.15f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var oldT = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(Number.ToString(), font, txt, new RectangleF(box.X, box.Y + r * 0.06f, box.Width, box.Height), fmt);
        g.TextRenderingHint = oldT;
        g.SmoothingMode = oldS;
    }
}
