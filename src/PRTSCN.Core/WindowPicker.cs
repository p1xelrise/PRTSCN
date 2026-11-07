using System.Drawing;

namespace PRTSCN.Core;

/// <summary>A top-level window as seen when the capture started: its visible frame and title.</summary>
public readonly record struct WindowInfo(Rectangle Bounds, string Title, long Handle = 0);

/// <summary>Window picking for the capture overlay. Pure logic, unit-tested.</summary>
public static class WindowPicker
{
    /// <summary>
    /// Turn raw window frames (physical screen coordinates, top of the z-order first) into
    /// overlay coordinates: offset by the virtual-desktop origin, clipped to the desktop,
    /// and dropped if (almost) nothing of them is on screen.
    /// </summary>
    public static List<WindowInfo> Prepare(IEnumerable<WindowInfo> zOrdered, Rectangle virtualScreen, int minSize = 8)
    {
        var desk = new Rectangle(Point.Empty, virtualScreen.Size);
        var list = new List<WindowInfo>();
        foreach (var w in zOrdered)
        {
            var r = w.Bounds;
            r.Offset(-virtualScreen.X, -virtualScreen.Y);
            r = Rectangle.Intersect(r, desk);
            if (r.Width < minSize || r.Height < minSize) continue;
            list.Add(w with { Bounds = r });
        }
        return list;
    }

    /// <summary>Index of the topmost window under p, or -1 (the desktop).</summary>
    public static int HitTest(IReadOnlyList<WindowInfo> windows, Point p)
    {
        for (int i = 0; i < windows.Count; i++)
            if (windows[i].Bounds.Contains(p)) return i;
        return -1;
    }

    /// <summary>"Title · 1280 × 720", title shortened to fit.</summary>
    public static string Label(WindowInfo w, int maxTitle = 48)
    {
        var t = (w.Title ?? "").Trim();
        if (t.Length > maxTitle) t = t[..(maxTitle - 1)].TrimEnd() + "…";
        var size = $"{w.Bounds.Width} × {w.Bounds.Height}";
        return t.Length == 0 ? size : $"{t}   ·   {size}";
    }
}
