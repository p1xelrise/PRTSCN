using System.Runtime.InteropServices;
using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>
/// One recording from countdown to saved file: runs the recorder and its on-screen chrome
/// (frame + control bar), the pause / restart / discard / stop commands and the auto-stop limits.
/// Lives on the UI thread.
/// </summary>
internal sealed class RecordSession : IDisposable
{
    readonly RecordTarget _target;
    readonly AppSettings _settings;
    readonly Action<string, string, string?, bool> _notify; // title, text, path, error
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    readonly string _stopHint, _pauseHint;
    IRecorder? _rec;
    RecordFrame? _frame;
    RecordControlBar? _bar;
    ToastWindow? _countdownToast;
    DateTime _countdownEnd;
    int _countdownShown;
    bool _ended;

    /// <summary>Raised once, when the session is over (saved, discarded, cancelled or failed).</summary>
    public event Action? Ended;
    public bool Recording => _rec != null;
    public bool CountingDown => _countdownToast != null;
    public TimeSpan Elapsed => _rec?.Elapsed ?? TimeSpan.Zero;
    public bool Paused => _rec?.Paused ?? false;
    /// <summary>The recording becomes a GIF: video only, saved to a temp MP4 first.</summary>
    public bool AsGif => _settings.RecordFormat == RecordFormat.Gif && _target.Kind != RecordKind.Audio;

    /// <summary>
    /// Called when a file is saved, with its path and the summary text. Return true when the
    /// caller takes over (opens the editor, makes a GIF); the "Recording saved" note is then skipped.
    /// </summary>
    public Func<string, string, bool>? OnSaved;

    public RecordSession(RecordTarget target, AppSettings settings, Action<string, string, string?, bool> notify)
    {
        _target = target;
        _settings = settings;
        _notify = notify;
        _stopHint = HotkeyManager.Describe((Keys)settings.RecordHotkey);
        _pauseHint = HotkeyManager.Describe((Keys)settings.PauseHotkey);
        _tick.Tick += (_, _) => OnTick();
    }

    /// <summary>Stop by itself after this long (a scheduled recording).</summary>
    public TimeSpan? StopAfter { get; init; }
    /// <summary>Start at once, without the countdown (a scheduled recording).</summary>
    public bool SkipCountdown { get; init; }

    public void Begin()
    {
        if (_settings.RecordCountdown <= 0 || SkipCountdown) { StartRecorder(); return; }
        Native.EscPressed(); // forget an earlier press
        _countdownToast = new ToastWindow();
        _countdownEnd = DateTime.Now.AddSeconds(_settings.RecordCountdown);
        _countdownShown = -1;
        _tick.Interval = 100;
        _tick.Start();
        OnTick();
    }

    void ShowCountdown(int left)
    {
        var screen = Screen.FromRectangle(CurrentArea()).Bounds;
        _countdownToast!.ShowText(screen, left.ToString(), $"Recording the {_target.Describe()} in {left} s   ·   Esc to cancel", big: true);
    }

    void StartRecorder()
    {
        _countdownToast?.Dispose();
        _countdownToast = null;
        try
        {
            Directory.CreateDirectory(_settings.VideoFolder);
            bool soundOnly = _target.Kind == RecordKind.Audio;
            var name = RecordingMath.DefaultVideoName(DateTime.Now);
            if (soundOnly) name = Path.ChangeExtension(name, ".m4a");
            // A GIF is made from a silent MP4 kept in the temp folder until the GIF is written.
            var folder = AsGif ? Path.Combine(Path.GetTempPath(), "PRTSCN") : _settings.VideoFolder;
            Directory.CreateDirectory(folder);
            var path = UniquePath(Path.Combine(folder, name));
            var audio = AsGif ? new AudioOptions(false, false, "", 1, 1)
                : new AudioOptions(_settings.RecordSystemAudio, _settings.RecordMic, _settings.MicDeviceId,
                    AudioMath.Gain(_settings.SystemVolume), AudioMath.Gain(_settings.MicVolume));
            if (soundOnly) _rec = new AudioRecorder(path, audio);
            else
            {
                var fx = new RecordEffects(_settings.RecordClicks, _settings.RecordKeys, _settings.RecordSpotlight, _settings.RecordMagnifier,
                    _settings.MagnifierZoom / 100f, _settings.RecordAutoZoom && _target.Kind != RecordKind.Follow ? _settings.AutoZoomLevel / 100f : 0);
                var opt = new RecorderOptions(path, _settings.VideoFps, _settings.VideoQuality, _settings.RecordShowCursor, _settings.HardwareEncoder,
                    audio.Any ? audio : null, fx, Watermark.From(_settings));
                _rec = new ScreenRecorder(_target, opt);
            }
            _rec.Start();
            if (_rec.Audio is { Problems.Count: > 0 } a)
                _notify("Recording without some sound", "Could not open " + string.Join(", ", a.Problems) + ".", null, true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _rec?.Dispose();
            if (_rec != null) TryDelete(_rec.FilePath);
            _rec = null;
            _notify("Recording failed", ex.Message, null, true);
            End();
            return;
        }

        var area = CurrentArea();
        if (_settings.RecordShowFrame && _target.Kind is not (RecordKind.Audio or RecordKind.AllScreens))
        {
            _frame = new RecordFrame();
            _frame.SetArea(area, false);
        }
        bool canDraw = _target.Kind != RecordKind.Audio;
        _bar = new RecordControlBar(PauseResume, Restart, Discard, Stop, _stopHint, _pauseHint, _rec.Audio?.Mic != null ? ToggleMic : null, _rec.Audio != null,
            canDraw ? ToggleDraw : null, canDraw ? PickInk : null, canDraw ? UndoDraw : null, canDraw ? ClearDraw : null);
        _bar.PlaceNear(area);
        _tick.Interval = _target.Kind == RecordKind.Follow ? 40 : 250; // the frame glides with a follow view
        _tick.Start();
        OnTick();
    }

    void OnTick()
    {
        if (_ended) return;
        if (_countdownToast != null)
        {
            if (Native.EscPressed()) { Cancel(); return; }
            int left = (int)Math.Ceiling((_countdownEnd - DateTime.Now).TotalSeconds);
            if (left <= 0) { _tick.Stop(); _countdownToast.Hide(); StartRecorder(); return; }
            if (left != _countdownShown) { _countdownShown = left; ShowCountdown(left); }
            return;
        }
        if (_rec == null) return;

        if (_rec.Error != null) { Finish(save: true, reason: "Recording stopped: " + _rec.Error.Message, error: true); return; }
        if (_rec.SourceClosed) { Finish(save: true, reason: "The window was closed."); return; }
        long bytes = _rec.FileBytes;
        if (StopAfter is TimeSpan limit && _rec.Elapsed >= limit)
        { Finish(save: true, reason: $"Scheduled recording finished ({RecordingMath.Clock(limit)})."); return; }
        if (_settings.RecordMaxMinutes > 0 && _rec.Elapsed.TotalMinutes >= _settings.RecordMaxMinutes)
        { Finish(save: true, reason: $"Stopped at the {_settings.RecordMaxMinutes} min limit."); return; }
        if (_settings.RecordMaxMegabytes > 0 && bytes >= _settings.RecordMaxMegabytes * 1_000_000L)
        { Finish(save: true, reason: $"Stopped at the {_settings.RecordMaxMegabytes} MB limit."); return; }

        var area = CurrentArea();
        _frame?.SetArea(area, _rec.Paused);
        if (_draw != null && _target.Kind == RecordKind.Window) _draw.SetArea(area); // drawings move with the window
        if (_target.Kind != RecordKind.Follow) _bar?.PlaceNear(area); // a follow view moves; the bar stays put
        var au = _rec.Audio;
        _bar?.SetState(_rec.Elapsed, bytes, _rec.Paused, au?.SystemSound?.Level ?? 0, au?.Mic?.Level ?? 0, au?.Mic?.Muted ?? false);
    }

    /// <summary>The recorded rectangle on screen right now (a window may move).</summary>
    Rectangle CurrentArea()
    {
        if (_target.Kind == RecordKind.Window && DwmGetWindowAttribute(_target.Window, 9 /* EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) == 0)
            return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        if (_target.Kind == RecordKind.Follow && _rec is ScreenRecorder follow) return follow.CurrentScreenArea;
        if (_target.Kind == RecordKind.AllScreens)
        {
            // The bar sits on the screen with the mouse when recording starts.
            var s = _audioScreen ??= Screen.FromPoint(Cursor.Position).Bounds;
            return s;
        }
        if (_target.Kind == RecordKind.Audio)
        {
            // Sound only: no area; the bar sits at the bottom right of the screen.
            var work = _audioScreen ??= Screen.FromPoint(Cursor.Position).WorkingArea;
            return new Rectangle(work.Right - 2, work.Bottom - 2, 1, 1);
        }
        return _target.Bounds;
    }

    Rectangle? _audioScreen;
    DrawLayer? _draw;

    /// <summary>Where drawings go: the recorded area; the whole screen for a follow view; every screen for "all".</summary>
    Rectangle DrawArea() => _target.Kind switch
    {
        RecordKind.Follow => Screen.FromRectangle(_target.Bounds).Bounds,
        RecordKind.AllScreens => SystemInformation.VirtualScreen,
        _ => CurrentArea(),
    };

    public void ToggleDraw()
    {
        if (_rec == null || _target.Kind == RecordKind.Audio) return;
        if (_draw == null)
        {
            var ink = ColorTranslator.FromHtml(_settings.DrawColor);
            _draw = new DrawLayer(ink, _settings.DrawWidth, () => SetDrawing(false));
            _draw.InkChanged += () => InkChanged(_draw.Ink, _draw.InkWidth);            _draw.SetArea(DrawArea());
            // a window recording sees only that window, not the drawing layer above it: draw the strokes in
            if (_target.Kind == RecordKind.Window && _rec is ScreenRecorder win) { var layer = _draw; win.Strokes = () => layer.Strokes; }
            _bar?.SetInk(_draw.Ink, _draw.InkWidth);
        }
        SetDrawing(!_draw.DrawMode);
    }

    /// <summary>A colour or size was picked on the bar: use it, and start drawing if not already.</summary>
    void PickInk(Color? color, float? width)
    {
        if (_draw == null) ToggleDraw();
        if (_draw == null) return;
        if (color is Color c) _draw.Ink = c;
        if (width is float w) _draw.InkWidth = w;
        InkChanged(_draw.Ink, _draw.InkWidth);
        if (!_draw.DrawMode) SetDrawing(true);
        else { _draw.Activate(); _draw.Focus(); } // keys keep going to the drawing
    }

    /// <summary>Bar buttons: take back the last line, or wipe the drawing. Drawing goes on if it was on.</summary>
    void UndoDraw() { _draw?.Undo(); KeepDrawingFocus(); }
    void ClearDraw() { _draw?.Clear(); KeepDrawingFocus(); }

    void KeepDrawingFocus()
    {
        if (_draw?.DrawMode == true) { _draw.Activate(); _draw.Focus(); } // keys keep going to the drawing
    }

    void InkChanged(Color color, float width)
    {
        _bar?.SetInk(color, width);
        _settings.DrawColor = ColorTranslator.ToHtml(Color.FromArgb(color.R, color.G, color.B)).ToLowerInvariant();
        _settings.DrawWidth = (int)Math.Round(width);
    }

    void SetDrawing(bool on)
    {
        if (_draw == null) return;
        _draw.DrawMode = on;
        _bar?.SetDrawing(on);
        _bar?.PlaceNear(CurrentArea()); // keep the bar above the drawing layer
    }

    void ToggleMic()
    {
        var mic = _rec?.Audio?.Mic;
        if (mic == null) return;
        mic.Muted = !mic.Muted;
        OnTick();
    }

    // ---------- commands ----------

    public void PauseResume()
    {
        if (_rec == null) return;
        if (_rec.Paused) _rec.Resume(); else _rec.Pause();
        OnTick();
    }

    public void Stop() => Finish(save: true, reason: null);

    public void Cancel()
    {
        if (_rec == null) { End(); return; }
        Discard();
    }

    void Discard()
    {
        if (_rec == null) return;
        var path = _rec.FilePath;
        CloseRecorder();
        TryDelete(path);
        _notify("Recording discarded", "Nothing was saved.", null, false);
        End();
    }

    void Restart()
    {
        if (_rec == null) return;
        var path = _rec.FilePath;
        CloseRecorder();
        TryDelete(path);
        StartRecorder(); // same target, no countdown
    }

    void Finish(bool save, string? reason, bool error = false)
    {
        if (_rec == null) { End(); return; }
        var rec = _rec;
        var path = rec.FilePath;
        var elapsed = rec.Elapsed;
        CloseRecorder();
        if (save && File.Exists(path))
        {
            long bytes = new FileInfo(path).Length;
            string text = $"{Path.GetFileName(path)}  ·  {RecordingMath.Clock(elapsed)}  ·  {RecordingMath.FileSize(bytes)}";
            if (reason != null) text = reason + "\n" + text;
            bool handled = false;
            try { handled = OnSaved?.Invoke(path, text) == true; }
            catch (Exception ex) { CrashLog.Write(ex); }
            if (!handled || error) _notify(error ? "Recording stopped" : "Recording saved", text, path, error);
        }
        else if (reason != null) _notify("Recording failed", reason, null, true);
        End();
    }

    void CloseRecorder()
    {
        _tick.Stop();
        _frame?.Dispose(); _frame = null;
        _bar?.Dispose(); _bar = null;
        _draw?.Dispose(); _draw = null;
        try { _rec?.Dispose(); }
        catch (Exception ex) { CrashLog.Write(ex); }
        _rec = null;
    }

    void End()
    {
        if (_ended) return;
        _ended = true;
        _tick.Stop();
        _countdownToast?.Dispose();
        _countdownToast = null;
        Ended?.Invoke();
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){Path.GetExtension(path)}");
            if (!File.Exists(p)) return p;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { CrashLog.Write(ex); }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    public void Dispose()
    {
        CloseRecorder();
        _countdownToast?.Dispose();
        _tick.Dispose();
    }
}
