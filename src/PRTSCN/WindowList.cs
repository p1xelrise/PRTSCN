using System.Runtime.InteropServices;
using System.Text;
using PRTSCN.Core;

namespace PRTSCN;

/// <summary>Visible top-level windows, top of the z-order first, taken just before the overlay opens.</summary>
internal static class WindowList
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const uint LWA_ALPHA = 0x2;

    // The desktop itself: clicking it means "whole monitor", so it is not a pickable window.
    static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW" };

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    const uint PW_RENDERFULLCONTENT = 2;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    /// <summary>
    /// The window's own pixels, as if nothing covered it (PrintWindow asks the window to draw itself).
    /// Returns its visible frame in physical screen coordinates, or null when the window can't
    /// draw itself this way (some games and video players give back black).
    /// </summary>
    public static Bitmap? CaptureUncovered(long handle, out Rectangle frame, out bool roundCorners)
    {
        frame = Rectangle.Empty;
        roundCorners = false;
        var h = new IntPtr(handle);
        if (!IsWindow(h) || !GetWindowRect(h, out RECT wr)) return null;
        var full = Rectangle.FromLTRB(wr.L, wr.T, wr.R, wr.B);
        if (full.Width <= 0 || full.Height <= 0) return null;
        frame = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) == 0
            ? Rectangle.FromLTRB(fr.L, fr.T, fr.R, fr.B) : full;
        frame = Rectangle.Intersect(frame, full);
        if (frame.Width <= 0 || frame.Height <= 0) return null;
        // Windows 11 rounds the corners of normal (not maximised) windows.
        roundCorners = Environment.OSVersion.Version.Build >= 22000 && !IsZoomed(h);

        using var whole = new Bitmap(full.Width, full.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(whole))
        {
            IntPtr dc = g.GetHdc();
            bool ok;
            try { ok = PrintWindow(h, dc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(dc); }
            if (!ok) return null;
        }
        var crop = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(crop))
            g.DrawImage(whole, new Rectangle(0, 0, frame.Width, frame.Height), frame.X - full.X, frame.Y - full.Y, frame.Width, frame.Height, GraphicsUnit.Pixel);
        if (LooksBlank(crop)) { crop.Dispose(); return null; }
        return crop;
    }

    /// <summary>All (nearly) black: the window did not draw itself.</summary>
    static bool LooksBlank(Bitmap b)
    {
        int stepX = Math.Max(1, b.Width / 24), stepY = Math.Max(1, b.Height / 24);
        for (int y = stepY / 2; y < b.Height; y += stepY)
            for (int x = stepX / 2; x < b.Width; x += stepX)
            {
                var c = b.GetPixel(x, y);
                if (c.R + c.G + c.B > 24) return false;
            }
        return true;
    }

    /// <summary>Raise the window to the top of the normal windows (it stays below the PRTSCN overlay).</summary>
    public static void RaiseBehindOverlay(long handle) =>
        SetWindowPos(new IntPtr(handle), IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void Activate(long handle)
    {
        var h = new IntPtr(handle);
        if (IsWindow(h)) SetForegroundWindow(h);
    }

    public static List<WindowInfo> Snapshot()
    {
        var list = new List<WindowInfo>();
        var sb = new StringBuilder(256);
        EnumWindows((h, lParam) =>
        {
            try
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                // Cloaked = hidden by Windows (other virtual desktops, suspended UWP apps).
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TRANSPARENT) != 0) return true; // click-through overlays (game bars, GPU overlays)
                if ((ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(h, out uint colorKey, out byte a, out uint f) && (f & LWA_ALPHA) != 0 && a == 0) return true;

                sb.Clear();
                GetClassName(h, sb, sb.Capacity);
                if (SkipClasses.Contains(sb.ToString())) return true;

                // The visible frame, without Windows 10/11's invisible resize borders.
                RECT r;
                if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r))
                    return true;
                var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
                if (rect.Width <= 0 || rect.Height <= 0) return true;

                sb.Clear();
                GetWindowText(h, sb, sb.Capacity);
                list.Add(new WindowInfo(rect, sb.ToString(), h.ToInt64()));
            }
            catch { /* one odd window must not break the capture */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
