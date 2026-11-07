using Microsoft.Win32;

namespace PRTSCN;

internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "PRTSCN";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is string exe) key.SetValue(Name, $"\"{exe}\" --tray");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

internal static class WindowsSnipping
{
    /// <summary>
    /// Windows 11 (and recent Windows 10) can send Print Screen to the Snipping Tool.
    /// While that is on, Windows takes the key first.
    /// </summary>
    public static bool PrintScreenOpensSnipping()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
        return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int v && v == 1;
    }

    public static void OpenSetting()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
        catch { }
    }
}
