using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>
/// Headless recording check used during development and by the release checklist.
/// PRTSCN.exe --record-test screen|area|window|audio &lt;seconds&gt; &lt;out&gt; [fps] [sound|mic|both]
/// </summary>
internal static class RecordTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    /// <summary>Instant replay: buffer for N seconds in 2 s segments, save the last 3 s and the last 30 s.</summary>
    public static int RunReplay(string[] args)
    {
        var dir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "replay.log");
        var lines = new List<string>();
        try
        {
            int seconds = int.Parse(args[1]);
            var s = new AppSettings { ReplaySeconds = 30, RecordMic = false }.Normalize();
            using var svc = new ReplayService();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            svc.Start(s, Path.Combine(dir, "buffer"), TimeSpan.FromSeconds(2));
            Thread.Sleep(seconds * 1000);
            lines.Add($"held {svc.Held.TotalSeconds:0.0}s, segments on disk {Directory.GetFiles(Path.Combine(dir, "buffer"), "seg-*.mp4").Length}");
            foreach (int want in new[] { 3, 30 })
            {
                sw.Restart();
                var path = svc.SaveAsync(dir, want, s).GetAwaiter().GetResult();
                var (v, a) = Editor.MediaReader.Probe(path);
                using var r = new Editor.MediaReader(path, true);
                lines.Add($"saved last {want}s -> {Path.GetFileName(path)} {r.Width}x{r.Height} {r.Duration / 1e7:0.00}s audio={a} {new FileInfo(path).Length / 1024} KB in {sw.ElapsedMilliseconds} ms");
                Thread.Sleep(1200);
            }
            lines.Add($"error={svc.Error?.Message}");
            File.WriteAllLines(log, lines);
            return svc.Error == null ? 0 : 2;
        }
        catch (Exception ex)
        {
            lines.Add("fail " + ex);
            File.WriteAllLines(log, lines);
            return 1;
        }
    }

    public static int Run(string[] args)
    {
        var log = Path.ChangeExtension(args[3], ".log");
        try
        {
            var kind = args[1];
            int seconds = int.Parse(args[2]);
            int fps = args.Length > 4 ? int.Parse(args[4]) : 30;
            string sound = args.Length > 5 ? args[5] : kind == "audio" ? "sound" : "none";
            var audio = new AudioOptions(sound is "sound" or "both", sound is "mic" or "both", "", 1f, 1f);
            string fxArg = args.Length > 6 ? args[6] : "";
            var fx = new RecordEffects(fxArg.Contains("clicks"), fxArg.Contains("keys") ? KeyDisplay.Shortcuts : KeyDisplay.Off,
                fxArg.Contains("spot"), fxArg.Contains("mag"), 2f, fxArg.Contains("zoom") ? 2f : 0);
            var wm = fxArg.Contains("wm") ? new Watermark("PRTSCN test watermark", Environment.GetEnvironmentVariable("PRTSCN_WM_IMAGE") ?? "", Corner.BottomRight, 0.8f) : null;
            var screen = Screen.PrimaryScreen!.Bounds;
            var target = kind switch
            {
                "window" => new RecordTarget(RecordKind.Window, Rectangle.Empty, GetForegroundWindow()),
                "area" => new RecordTarget(RecordKind.Area, new Rectangle(screen.X + 101, screen.Y + 77, 641, 363)),
                "audio" => new RecordTarget(RecordKind.Audio, Rectangle.Empty),
                "follow" => new RecordTarget(RecordKind.Follow, new Rectangle(screen.X + 200, screen.Y + 200, 1280, 720)),
                "all" => new RecordTarget(RecordKind.AllScreens, SystemInformation.VirtualScreen),
                _ => new RecordTarget(RecordKind.Screen, screen),
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var path = Path.GetFullPath(args[3]);
            ScreenRecorder? video = null;
            using IRecorder rec = target.Kind == RecordKind.Audio
                ? new AudioRecorder(path, audio)
                : video = new ScreenRecorder(target, new RecorderOptions(path, fps, VideoQuality.High, true, true, audio.Any ? audio : null, fx, wm));
            rec.Start();
            DrawLayer? layer = null;
            if (fxArg.Contains("draw") && video != null)
            {
                var a = video.CurrentScreenArea;
                layer = new DrawLayer(Theme.Ink[0], 5, () => { });
                layer.SetArea(a);
                if (Environment.GetEnvironmentVariable("PRTSCN_DRAWMODE") == "1") layer.DrawMode = true; // as when the user draws
                if (target.Kind == RecordKind.Window) { var l = layer; video.Strokes = () => l.Strokes; }
                layer.AddStroke(new PointF(a.Width * 0.2f, a.Height * 0.5f), new PointF(a.Width * 0.5f, a.Height * 0.3f), new PointF(a.Width * 0.8f, a.Height * 0.6f));
                // a second, thick blue stroke, as picked on the bar
                layer.Ink = Theme.Ink[4];
                layer.InkWidth = 16;
                layer.AddStroke(new PointF(a.Width * 0.2f, a.Height * 0.75f), new PointF(a.Width * 0.8f, a.Height * 0.75f));
                Application.DoEvents();
            }
            if (fx.AutoZoom > 0) video?.SimulateClick(new Point(screen.X + screen.Width / 4, screen.Y + screen.Height / 4)); // zoomed in for the snapshot
            float sysPeak = 0, micPeak = 0;
            void Watch(int ms)
            {
                for (int t = 0; t < ms; t += 50)
                {
                    Thread.Sleep(50);
                    sysPeak = Math.Max(sysPeak, rec.Audio?.SystemSound?.Level ?? 0);
                    micPeak = Math.Max(micPeak, rec.Audio?.Mic?.Level ?? 0);
                }
            }
            Watch(seconds * 500);
            rec.Pause();
            Thread.Sleep(1000);
            rec.Resume();
            if (fx.Keys != KeyDisplay.Off) { keybd_event(0x86 /* F23 */, 0, 0, 0); keybd_event(0x86, 0, 2, 0); Thread.Sleep(100); }
            if (video != null) using (var snap = video.Snapshot()) snap.Save(Path.ChangeExtension(args[3], ".png"));
            Watch(seconds * 500);
            rec.Stop();
            layer?.Dispose();
            string size = video != null ? $"{video.FrameSize.Width}x{video.FrameSize.Height}" : "-";
            File.WriteAllText(log, $"ok kind={kind} size={size} fps={fps} frames={video?.FramesWritten} " +
                $"active={rec.Elapsed.TotalSeconds:0.00}s wall={sw.Elapsed.TotalSeconds:0.00}s hw={video?.UsedHardware} bytes={rec.FileBytes} " +
                $"sound={sound} sys={rec.Audio?.SystemSound?.Name}({sysPeak:0.00}) mic={rec.Audio?.Mic?.Name}({micPeak:0.00}) " +
                $"problems={string.Join(";", rec.Audio?.Problems ?? new())} error={rec.Error?.Message}");
            return rec.Error == null ? 0 : 2;
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "fail " + ex);
            return 1;
        }
    }
}
