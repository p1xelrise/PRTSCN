namespace PRTSCN;

internal static class Program
{
    public const string CaptureSignalName = @"Local\PRTSCN.CaptureSignal";

    [STAThread]
    static void Main(string[] args)
    {
        // Developer check: PRTSCN.exe --record-test screen|area|window <seconds> <out.mp4> [fps]
        if (args.Length >= 4 && args[0] == "--record-test") { Environment.ExitCode = Recording.RecordTest.Run(args); return; }
        // Developer check: PRTSCN.exe --replay-test <seconds> <out folder>   instant replay with 2 s segments, saved twice
        if (args.Length >= 3 && args[0] == "--replay-test") { Environment.ExitCode = Recording.RecordTest.RunReplay(args); return; }
        // Developer check: PRTSCN.exe --ui-shots <folder>   renders Options and the recording bar to PNG files
        if (args.Length >= 2 && args[0] == "--ui-shots") { ApplicationConfiguration.Initialize(); UiShots.Run(args[1]); return; }
        // Developer check: PRTSCN.exe --export-test <source> <out> mp4|gif|audio [cutFrom-cutTo] [width] [fps]
        if (args.Length >= 4 && args[0] == "--export-test") { Environment.ExitCode = Editor.EditorDev.Run(args); return; }
        // Developer check: PRTSCN.exe --editor-shot <recording> <out.png> [format index]
        if (args.Length >= 3 && args[0] == "--editor-shot")
        {
            ApplicationConfiguration.Initialize();
            Editor.EditorForm.RenderShot(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 0);
            return;
        }
        using var mutex = new Mutex(true, @"Local\PRTSCN.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: launching PRTSCN again means "capture now".
            try { using var ev = EventWaitHandle.OpenExisting(CaptureSignalName); ev.Set(); } catch { }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        Application.Run(new TrayApp(args));
    }
}

internal static class CrashLog
{
    public static string PathName => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PRTSCN", "error.log");

    public static void Write(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            File.AppendAllText(PathName, $"[{DateTime.Now:u}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
