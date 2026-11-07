using System.Drawing;

namespace PRTSCN.Core;

/// <summary>Where the toolbars and size label go around a selection. Everything is clamped to the selection's monitor.</summary>
public static class ToolbarLayout
{
    /// <summary>Vertical tool bar: right of the selection, else left, else inside its right edge. Top-aligned, clamped vertically.</summary>
    public static Rectangle PlaceVertical(Rectangle sel, Size bar, Rectangle screen, int gap)
    {
        int y = Clamp(sel.Top, screen.Top, screen.Bottom - bar.Height);
        int right = sel.Right + gap;
        if (right + bar.Width <= screen.Right) return new Rectangle(right, y, bar.Width, bar.Height);
        int left = sel.Left - gap - bar.Width;
        if (left >= screen.Left) return new Rectangle(left, y, bar.Width, bar.Height);
        int inside = Clamp(sel.Right - gap - bar.Width, screen.Left, screen.Right - bar.Width);
        return new Rectangle(inside, Clamp(sel.Top + gap, screen.Top, screen.Bottom - bar.Height), bar.Width, bar.Height);
    }

    /// <summary>Horizontal action bar: below the selection right-aligned, else above, else inside the bottom edge.</summary>
    public static Rectangle PlaceHorizontal(Rectangle sel, Size bar, Rectangle screen, int gap)
    {
        int x = Clamp(sel.Right - bar.Width, screen.Left, screen.Right - bar.Width);
        int below = sel.Bottom + gap;
        if (below + bar.Height <= screen.Bottom) return new Rectangle(x, below, bar.Width, bar.Height);
        int above = sel.Top - gap - bar.Height;
        if (above >= screen.Top) return new Rectangle(x, above, bar.Width, bar.Height);
        int inside = Clamp(sel.Bottom - gap - bar.Height, screen.Top, screen.Bottom - bar.Height);
        return new Rectangle(Clamp(sel.Right - gap - bar.Width, screen.Left, screen.Right - bar.Width), inside, bar.Width, bar.Height);
    }

    /// <summary>Size label above the top-left corner, or just inside it if there is no room above.</summary>
    public static Point PlaceSizeLabel(Rectangle sel, Size label, Rectangle screen, int gap)
    {
        int x = Clamp(sel.Left, screen.Left, screen.Right - label.Width);
        int y = sel.Top - gap - label.Height;
        if (y < screen.Top) y = Clamp(sel.Top + gap, screen.Top, screen.Bottom - label.Height);
        return new Point(x, y);
    }

    /// <summary>The screen holding the centre of r (falls back to the one with the largest overlap, then the first).</summary>
    public static Rectangle ScreenFor(Rectangle r, IReadOnlyList<Rectangle> screens)
    {
        var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        foreach (var s in screens) if (s.Contains(c)) return s;
        Rectangle best = screens[0]; long bestArea = -1;
        foreach (var s in screens)
        {
            var i = Rectangle.Intersect(s, r);
            long a = (long)i.Width * i.Height;
            if (a > bestArea) { bestArea = a; best = s; }
        }
        return best;
    }

    static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);
}
