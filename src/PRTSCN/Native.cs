using System.Runtime.InteropServices;

namespace PRTSCN;

internal static partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DPICHANGED = 0x02E0;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_SHOWWINDOW = 0x0040, SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    // ---- input, used by scrolling capture ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }
    const uint INPUT_MOUSE = 0, MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Turn the mouse wheel down by n notches over whatever is under the cursor.</summary>
    public static void WheelDown(int notches)
    {
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { mouseData = -120 * notches, dwFlags = MOUSEEVENTF_WHEEL } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    /// <summary>Esc is down, or was pressed since the last check.</summary>
    public static bool EscPressed() => (GetAsyncKeyState(0x1B) & 0x8001) != 0;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    /// <summary>Keep a window out of screenshots (Windows 10 2004 and later). False if not supported.</summary>
    public static bool ExcludeFromCapture(IntPtr hWnd) => SetWindowDisplayAffinity(hWnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint flags);

    /// <summary>The pointer position in physical screen pixels (PRTSCN is per-monitor DPI aware).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT pt);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Scale factor (1.0 = 96 DPI) of the monitor holding a physical screen point.</summary>
    public static float ScaleAt(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0 && dx > 0)
                return dx / 96f;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1f;
    }
}
