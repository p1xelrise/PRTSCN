using System.Drawing;

namespace PRTSCN.Core;

/// <summary>Which part of the selection the pointer is over.</summary>
public enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW }

/// <summary>Pure geometry for the selection rectangle. No UI types, so it is unit-tested on any OS.</summary>
public static class SelectionMath
{
    /// <summary>Rectangle spanned by two points, in either order. Width/height are inclusive of the end pixel.</summary>
    public static Rectangle FromPoints(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);
    }

    /// <summary>Handle under point p. Handles win over Move; tol is the hit radius in pixels.</summary>
    public static Handle HitTest(Rectangle sel, Point p, int tol)
    {
        if (sel.Width <= 0 || sel.Height <= 0) return Handle.None;
        int l = sel.Left, t = sel.Top, r = sel.Right - 1, b = sel.Bottom - 1;
        int cx = l + (r - l) / 2, cy = t + (b - t) / 2;
        bool Near(int px, int py) => Math.Abs(p.X - px) <= tol && Math.Abs(p.Y - py) <= tol;

        if (Near(l, t)) return Handle.NW;
        if (Near(r, t)) return Handle.NE;
        if (Near(l, b)) return Handle.SW;
        if (Near(r, b)) return Handle.SE;
        if (Near(cx, t)) return Handle.N;
        if (Near(cx, b)) return Handle.S;
        if (Near(l, cy)) return Handle.W;
        if (Near(r, cy)) return Handle.E;

        bool inX = p.X >= l - tol && p.X <= r + tol, inY = p.Y >= t - tol && p.Y <= b + tol;
        if (inX && Math.Abs(p.Y - t) <= tol) return Handle.N;
        if (inX && Math.Abs(p.Y - b) <= tol) return Handle.S;
        if (inY && Math.Abs(p.X - l) <= tol) return Handle.W;
        if (inY && Math.Abs(p.X - r) <= tol) return Handle.E;

        return sel.Contains(p) ? Handle.Move : Handle.None;
    }

    /// <summary>
    /// Resize the rectangle by dragging a handle by (dx, dy). Edges may cross; the result is
    /// normalised so it stays a valid rectangle at least 1x1.
    /// </summary>
    public static Rectangle Resize(Rectangle start, Handle h, int dx, int dy)
    {
        int l = start.Left, t = start.Top, r = start.Right - 1, b = start.Bottom - 1;
        switch (h)
        {
            case Handle.Move: l += dx; r += dx; t += dy; b += dy; break;
            case Handle.N: t += dy; break;
            case Handle.S: b += dy; break;
            case Handle.W: l += dx; break;
            case Handle.E: r += dx; break;
            case Handle.NW: l += dx; t += dy; break;
            case Handle.NE: r += dx; t += dy; break;
            case Handle.SW: l += dx; b += dy; break;
            case Handle.SE: r += dx; b += dy; break;
            default: return start;
        }
        return FromPoints(new Point(l, t), new Point(r, b));
    }

    /// <summary>Keep a moved rectangle fully inside bounds (size unchanged if it fits).</summary>
    public static Rectangle ClampMove(Rectangle r, Rectangle bounds)
    {
        int w = Math.Min(r.Width, bounds.Width), h = Math.Min(r.Height, bounds.Height);
        int x = Math.Clamp(r.X, bounds.Left, bounds.Right - w);
        int y = Math.Clamp(r.Y, bounds.Top, bounds.Bottom - h);
        return new Rectangle(x, y, w, h);
    }
}
