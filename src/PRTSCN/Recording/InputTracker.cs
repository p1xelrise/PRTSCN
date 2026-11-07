using System.Diagnostics;
using System.Runtime.InteropServices;
using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>
/// Watches mouse clicks and key presses while recording (low-level hooks on their own thread),
/// so the recorder can draw click ripples and the keystroke overlay. Nothing is stored beyond the
/// last few seconds, and nothing leaves the process.
/// </summary>
internal sealed class InputTracker : IDisposable
{
    internal readonly record struct Click(Point Pos, bool Right, long Down);
    internal sealed class KeyLabel { public string Text = ""; public long Time; public bool Typed; public int Repeat = 1; }

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_QUIT = 0x12;

    readonly bool _clicks;
    readonly KeyDisplay _keys;
    readonly object _lock = new();
    readonly List<Click> _recent = new();
    readonly List<KeyLabel> _labels = new();
    readonly Thread _thread;
    readonly HookProc _mouseProc, _keyProc;
    uint _threadId;
    IntPtr _mouseHook, _keyHook;
    volatile bool _leftDown, _rightDown;

    public InputTracker(bool clicks, KeyDisplay keys)
    {
        _clicks = clicks;
        _keys = keys;
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        _thread = new Thread(Run) { IsBackground = true, Name = "PRTSCN input" };
        using var ready = new ManualResetEventSlim();
        _thread.Start(ready);
        ready.Wait(2000);
    }

    Point? _lastClick;
    int _clickCount;
    /// <summary>Left clicks so far, and where the last one was (screen pixels).</summary>
    public int ClickCount { get { lock (_lock) return _clickCount; } }
    public Point? LastClick { get { lock (_lock) return _lastClick; } }

    public bool LeftDown => _leftDown;
    public bool RightDown => _rightDown;

    void Run(object? state)
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        if (_clicks) _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keys != KeyDisplay.Off) _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        ((ManualResetEventSlim)state!).Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
    }

    IntPtr MouseHook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int msg = (int)w;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                bool right = msg == WM_RBUTTONDOWN;
                if (right) _rightDown = true; else _leftDown = true;
                lock (_lock)
                {
                    if (!right) { _lastClick = new Point(info.x, info.y); _clickCount++; }
                    _recent.Add(new Click(new Point(info.x, info.y), right, Stopwatch.GetTimestamp()));
                    if (_recent.Count > 32) _recent.RemoveAt(0);
                }
            }
            else if (msg == WM_LBUTTONUP) _leftDown = false;
            else if (msg == WM_RBUTTONUP) _rightDown = false;
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    IntPtr KeyHook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && ((int)w == WM_KEYDOWN || (int)w == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
            var text = KeystrokeText.Describe((int)info.vkCode, Down(0x11), Down(0x12), Down(0x10), Down(0x5B) || Down(0x5C), _keys, out bool typed);
            if (text != null) AddLabel(text, typed);
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    void AddLabel(string text, bool typed)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var last = _labels.Count > 0 ? _labels[^1] : null;
            double age = last == null ? double.MaxValue : Stopwatch.GetElapsedTime(last.Time, now).TotalSeconds;
            if (last != null && typed && last.Typed && age < OverlayMath.TypingJoinSeconds && last.Text.Length < 40)
            {
                last.Text += text;
                last.Time = now;
            }
            else if (last != null && !typed && last.Text == text && age < OverlayMath.KeyVisibleSeconds)
            {
                last.Repeat++;
                last.Time = now;
            }
            else
            {
                _labels.Add(new KeyLabel { Text = text, Time = now, Typed = typed });
                if (_labels.Count > 3) _labels.RemoveAt(0);
            }
        }
    }

    /// <summary>Clicks younger than the ripple time, with their age in seconds.</summary>
    public List<(Point Pos, bool Right, double Age)> RecentClicks()
    {
        long now = Stopwatch.GetTimestamp();
        var list = new List<(Point, bool, double)>();
        lock (_lock)
        {
            _recent.RemoveAll(c => Stopwatch.GetElapsedTime(c.Down, now).TotalSeconds > OverlayMath.ClickRippleSeconds);
            foreach (var c in _recent) list.Add((c.Pos, c.Right, Stopwatch.GetElapsedTime(c.Down, now).TotalSeconds));
        }
        return list;
    }

    /// <summary>Key labels still visible, oldest first, with their opacity.</summary>
    public List<(string Text, float Opacity)> VisibleKeys()
    {
        long now = Stopwatch.GetTimestamp();
        var list = new List<(string, float)>();
        lock (_lock)
        {
            _labels.RemoveAll(k => OverlayMath.KeyOpacity(Stopwatch.GetElapsedTime(k.Time, now).TotalSeconds) <= 0);
            foreach (var k in _labels)
            {
                string t = k.Typed ? k.Text.Replace(" ", "␣") : k.Text;
                if (k.Repeat > 1) t += $"  ×{k.Repeat}";
                list.Add((t, OverlayMath.KeyOpacity(Stopwatch.GetElapsedTime(k.Time, now).TotalSeconds)));
            }
        }
        return list;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
