namespace PRTSCN;

/// <summary>Global hotkeys through RegisterHotKey on a hidden message-only window.</summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    readonly HashSet<int> _ids = new();
    public event Action<int>? Pressed;

    public HotkeyManager() => CreateHandle(new CreateParams { Parent = Native.HWND_MESSAGE });

    /// <summary>Returns false if another app (another screenshot tool, often) already owns the combination.</summary>
    public bool Register(int id, Keys keys)
    {
        Unregister(id);
        if (keys == Keys.None) return true;
        uint mods = Native.MOD_NOREPEAT;
        if (keys.HasFlag(Keys.Shift)) mods |= Native.MOD_SHIFT;
        if (keys.HasFlag(Keys.Control)) mods |= Native.MOD_CONTROL;
        if (keys.HasFlag(Keys.Alt)) mods |= Native.MOD_ALT;
        uint vk = (uint)(keys & Keys.KeyCode);
        if (!Native.RegisterHotKey(Handle, id, mods, vk)) return false;
        _ids.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) Native.UnregisterHotKey(Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids.ToArray()) Unregister(id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    public static string Describe(Keys k)
    {
        if (k == Keys.None) return "None";
        var parts = new List<string>();
        if (k.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (k.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (k.HasFlag(Keys.Alt)) parts.Add("Alt");
        var code = k & Keys.KeyCode;
        parts.Add(code switch
        {
            Keys.PrintScreen => "Print Screen",
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            Keys.Oemtilde => "`",
            _ => code.ToString(),
        });
        return string.Join(" + ", parts);
    }
}
