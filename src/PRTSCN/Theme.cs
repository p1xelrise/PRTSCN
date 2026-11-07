using System.Drawing.Drawing2D;

namespace PRTSCN;

internal static partial class Theme
{
    static FontFamily? _family;
    public static FontFamily Family => _family ??= PickFamily();

    static FontFamily PickFamily()
    {
        foreach (var name in FontFamilies)
        {
            try { return new FontFamily(name); } catch (ArgumentException) { }
        }
        return FontFamily.GenericSansSerif;
    }

    public static Font UiFont(float px, bool bold, float scale) =>
        new(Family, px * scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    public static Color WithAlpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Parse(string hex) => ColorTranslator.FromHtml(hex);

    public static string ToHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Color c, float width, RectangleF r, float radius)
    {
        using var p = Round(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }

    /// <summary>The PRTSCN icon from the embedded .ico, at the size Windows asks for.</summary>
    public static Icon AppIcon(int size)
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("prtscn.ico");
        return s is null ? SystemIcons.Application : new Icon(s, new Size(size, size));
    }
}
