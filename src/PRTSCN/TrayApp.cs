using System.Diagnostics;
using PRTSCN.Core;

namespace PRTSCN;

internal sealed class TrayApp : ApplicationContext
{
    const int HotkeyCapture = 1, HotkeyInstant = 2, HotkeyWindow = 3, HotkeyScroll = 4, HotkeyRecord = 5, HotkeyPause = 6, HotkeyReplay = 7;

    readonly Recording.ReplayService _replay = new();
    readonly ToolStripMenuItem _replayItem, _replaySaveItem, _scheduleItem, _scheduleCancelItem;
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    readonly string _settingsPath = SettingsStore.DefaultPath();
    AppSettings _settings;
    readonly NotifyIcon _tray;
    readonly HotkeyManager _hotkeys = new();
    readonly SynchronizationContext _ui;
    readonly EventWaitHandle _signal;
    readonly ToolStripMenuItem _captureItem, _windowItem, _instantItem, _scrollItem;
    readonly ToolStripMenuItem _recordItem, _recordWindowItem, _recordScreenItem, _recordSoundItem, _stopItem, _pauseItem;
    Recording.RecordSession? _session;
    ToastWindow? _toast;
    bool _busy; // a countdown or scrolling capture is running
    System.Windows.Forms.Timer? _countdown;
    CaptureOverlay? _overlay;
    OptionsForm? _options;
    string? _balloonPath;
    Action? _balloonAction;    // what a click on the last note does (instead of showing the file)
    readonly ToolStripMenuItem _recentItem;
    volatile bool _exiting;

    public TrayApp(string[] args)
    {
        bool firstRun = !File.Exists(_settingsPath);
        _settings = SettingsStore.Load(_settingsPath);

        var menu = new ContextMenuStrip();
        _captureItem = new ToolStripMenuItem("Capture area", null, (_, _) => StartCapture()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _windowItem = new ToolStripMenuItem("Capture window", null, (_, _) => StartCapture(windowMode: true));
        _instantItem = new ToolStripMenuItem("Capture full screen", null, (_, _) => InstantCapture());
        _scrollItem = new ToolStripMenuItem("Scrolling capture", null, (_, _) => StartCapture(scrollMode: true));
        var delayed = new ToolStripMenuItem("Capture after a delay");
        foreach (var sec in new[] { 3, 5, 10 })
            delayed.DropDownItems.Add($"Area in {sec} seconds", null, (_, _) => DelayedCapture(sec, windowMode: false));
        delayed.DropDownItems.Add(new ToolStripSeparator());
        foreach (var sec in new[] { 3, 5, 10 })
            delayed.DropDownItems.Add($"Window in {sec} seconds", null, (_, _) => DelayedCapture(sec, windowMode: true));
        menu.Items.Add(_captureItem);
        menu.Items.Add(_windowItem);
        menu.Items.Add(_scrollItem);
        menu.Items.Add(delayed);
        menu.Items.Add(_instantItem);
        menu.Items.Add(new ToolStripSeparator());
        _stopItem = new ToolStripMenuItem("Stop recording", null, (_, _) => _session?.Stop()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold), Visible = false };
        _pauseItem = new ToolStripMenuItem("Pause recording", null, (_, _) => _session?.PauseResume()) { Visible = false };
        _recordItem = new ToolStripMenuItem("Record area", null, (_, _) => StartRecordPicker(Recording.RecordKind.Area));
        _recordWindowItem = new ToolStripMenuItem("Record window", null, (_, _) => StartRecordPicker(Recording.RecordKind.Window));
        _recordScreenItem = new ToolStripMenuItem("Record full screen", null, (_, _) => RecordFullScreen());
        _recordSoundItem = new ToolStripMenuItem("Record sound only", null, (_, _) => RecordSoundOnly());
        menu.Items.Add(_stopItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(_recordWindowItem);
        menu.Items.Add(_recordScreenItem);
        menu.Items.Add(_recordSoundItem);
        _scheduleItem = new ToolStripMenuItem("Schedule a recording…", null, (_, _) => ShowSchedule());
        _scheduleCancelItem = new ToolStripMenuItem("Cancel the scheduled recording", null, (_, _) => CancelSchedule()) { Visible = false };
        menu.Items.Add(_scheduleItem);
        menu.Items.Add(_scheduleCancelItem);
        menu.Items.Add(new ToolStripSeparator());
        _replayItem = new ToolStripMenuItem("Instant replay", null, (_, _) => ToggleReplay());
        _replaySaveItem = new ToolStripMenuItem("Save the replay", null, (_, _) => SaveReplay()) { Enabled = false };
        menu.Items.Add(_replayItem);
        menu.Items.Add(_replaySaveItem);
        menu.Items.Add(new ToolStripSeparator());
        _recentItem = new ToolStripMenuItem("Recent recordings");
        _recentItem.DropDownItems.Add("(none)"); // filled when the menu opens
        menu.Items.Add(_recentItem);
        menu.Items.Add("Open a recording…", null, (_, _) => OpenRecordingDialog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open screenshots folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Open recordings folder", null, (_, _) => OpenFolder(_settings.VideoFolder));
        menu.Items.Add("Options…", null, (_, _) => ShowOptions());
        menu.Items.Add("About PRTSCN", null, (_, _) => About());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        menu.Opening += (_, _) =>
        {
            _pauseItem.Text = _session?.Paused == true ? "Resume recording" : "Pause recording";
            FillRecentMenu();
            UpdateAdvancedMenu();
        };

        _tray = new NotifyIcon
        {
            Icon = Theme.AppIcon(SystemInformation.SmallIconSize.Width),
            Text = "PRTSCN",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_countdown != null) { CancelCountdown(); return; } // clicking the tray icon cancels a countdown
            if (_session != null) { if (_session.CountingDown) _session.Cancel(); else _session.Stop(); return; }
            StartCapture();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonAction != null) _balloonAction();
            else if (_balloonPath != null) RevealInExplorer(_balloonPath);
        };

        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _hotkeys.Pressed += id =>
        {
            if (id == HotkeyCapture) StartCapture();
            else if (id == HotkeyWindow) StartCapture(windowMode: true);
            else if (id == HotkeyScroll) StartCapture(scrollMode: true);
            else if (id == HotkeyInstant) InstantCapture();
            else if (id == HotkeyRecord) ToggleRecord();
            else if (id == HotkeyPause) _session?.PauseResume();
            else if (id == HotkeyReplay) SaveReplay();
        };

        // A second launch of PRTSCN.exe signals this one to capture.
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.CaptureSignalName);
        new Thread(() =>
        {
            while (_signal.WaitOne() && !_exiting) _ui.Post(_ => StartCapture(), null);
        }) { IsBackground = true, Name = "PRTSCN signal" }.Start();

        if (_settings.StartWithWindows) TryApplyAutoStart();
        if (_settings.ReplayEnabled) StartReplay(announce: false);
        RegisterHotkeys(announce: true);
        _clock.Tick += (_, _) => OnClock();
        _clock.Start();
        if (_settings.Schedule is { } missed && missed.Start < DateTime.Now.AddMinutes(-1))
        {
            _settings.Schedule = null;
            TrySaveSettings();
            Notify("Scheduled recording missed", $"PRTSCN was not running at {missed.Start:HH:mm}.", null, error: true, force: true);
        }

        if (firstRun)
        {
            SettingsStore.Save(_settingsPath, _settings);
            Notify("PRTSCN is running", $"Press {HotkeyManager.Describe((Keys)_settings.CaptureHotkey)} to take a screenshot. PRTSCN lives in the tray.", null, force: true);
        }
    }

    void RegisterHotkeys(bool announce)
    {
        var problems = new List<string>();
        var cap = (Keys)_settings.CaptureHotkey;
        var inst = (Keys)_settings.InstantHotkey;
        var win = (Keys)_settings.WindowHotkey;
        if (!_hotkeys.Register(HotkeyCapture, cap)) problems.Add(HotkeyManager.Describe(cap));
        if (!_hotkeys.Register(HotkeyWindow, win)) problems.Add(HotkeyManager.Describe(win));
        var scr = (Keys)_settings.ScrollHotkey;
        if (!_hotkeys.Register(HotkeyScroll, scr)) problems.Add(HotkeyManager.Describe(scr));
        _scrollItem.ShortcutKeyDisplayString = scr == Keys.None ? "" : HotkeyManager.Describe(scr);
        if (!_hotkeys.Register(HotkeyInstant, inst)) problems.Add(HotkeyManager.Describe(inst));
        var rec = (Keys)_settings.RecordHotkey;
        if (!_hotkeys.Register(HotkeyRecord, rec)) problems.Add(HotkeyManager.Describe(rec));
        _recordItem.ShortcutKeyDisplayString = rec == Keys.None ? "" : HotkeyManager.Describe(rec);
        _stopItem.ShortcutKeyDisplayString = _recordItem.ShortcutKeyDisplayString;
        var pause = (Keys)_settings.PauseHotkey;
        _pauseItem.ShortcutKeyDisplayString = pause == Keys.None ? "" : HotkeyManager.Describe(pause);
        if (_session != null) _hotkeys.Register(HotkeyPause, pause);
        var replay = (Keys)_settings.ReplayHotkey;
        _replaySaveItem.ShortcutKeyDisplayString = replay == Keys.None ? "" : HotkeyManager.Describe(replay);
        if (_replay.Running && !_hotkeys.Register(HotkeyReplay, replay)) problems.Add(HotkeyManager.Describe(replay));
        if (!_replay.Running) _hotkeys.Unregister(HotkeyReplay);

        _captureItem.ShortcutKeyDisplayString = cap == Keys.None ? "" : HotkeyManager.Describe(cap);
        _windowItem.ShortcutKeyDisplayString = win == Keys.None ? "" : HotkeyManager.Describe(win);
        _instantItem.ShortcutKeyDisplayString = inst == Keys.None ? "" : HotkeyManager.Describe(inst);

        if (!announce) return;
        if (problems.Count > 0)
            Notify("Hotkey already in use", $"{string.Join(" and ", problems)} is taken by another app (another screenshot tool, such as Lightshot?). Close it, or pick another key in Options.", null, error: true, force: true);
        else if ((cap & Keys.KeyCode) == Keys.PrintScreen && WindowsSnipping.PrintScreenOpensSnipping())
            Notify("Windows is using Print Screen", "Windows sends Print Screen to the Snipping Tool. Turn off \"Use the Print screen key to open screen capture\" in Settings > Accessibility > Keyboard (link in PRTSCN Options).", null, error: true, force: true);
    }

    void StartCapture(bool windowMode = false, bool scrollMode = false)
    {
        if (_overlay != null) { _overlay.Activate(); return; }
        if (_busy) return;
        if (_options != null) return; // hotkeys are paused while Options is open anyway
        try
        {
            if (_session != null) return; // one thing at a time while recording
            var virt = ScreenGrabber.VirtualScreen;
            var windows = windowMode ? WindowList.Snapshot() : new List<WindowInfo>(); // window list and pixels are
            var shot = ScreenGrabber.Capture(virt);                                       // taken before PRTSCN shows
            _overlay = new CaptureOverlay(shot, virt, windows, windowMode, _settings, (t, m, p) => Notify(t, m, p),
                onScroll: r => _ui.Post(_ => RunScroll(r), null), scrollOnSelect: scrollMode);
            _overlay.FormClosed += (_, _) =>
            {
                _overlay = null;
                TrySaveSettings(); // keeps the last ink colour and width
            };
            _overlay.Show();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _overlay = null;
            Notify("Capture failed", ex.Message, null, error: true, force: true);
        }
    }

    // ---------- screen recording ----------

    void ToggleRecord()
    {
        if (_session != null)
        {
            if (_session.CountingDown) _session.Cancel(); else _session.Stop();
            return;
        }
        StartRecordPicker(Recording.RecordKind.Area);
    }

    void StartRecordPicker(Recording.RecordKind kind)
    {
        if (_overlay != null) { _overlay.Activate(); return; }
        if (_busy || _session != null || _options != null) return;
        if (!Recording.CaptureInterop.IsSupported)
        {
            Notify("Recording needs Windows 10 2004 or later", "This version of Windows has no screen capture API for apps.", null, error: true, force: true);
            return;
        }
        try
        {
            var virt = ScreenGrabber.VirtualScreen;
            var windows = WindowList.Snapshot();
            var shot = ScreenGrabber.Capture(virt);
            _overlay = new CaptureOverlay(shot, virt, windows, kind == Recording.RecordKind.Window, _settings, (t, m, p) => Notify(t, m, p),
                onRecord: t => _ui.Post(_ => BeginRecording(t), null), recordKind: kind);
            _overlay.FormClosed += (_, _) =>
            {
                _overlay = null;
                TrySaveSettings(); // keeps fps / countdown / cursor choices
            };
            _overlay.Show();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _overlay = null;
            Notify("Recording failed", ex.Message, null, error: true, force: true);
        }
    }

    void RecordFullScreen()
    {
        if (_overlay != null || _busy || _session != null || _options != null) return;
        if (!Recording.CaptureInterop.IsSupported) { StartRecordPicker(Recording.RecordKind.Screen); return; }
        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        BeginRecording(new Recording.RecordTarget(Recording.RecordKind.Screen, screen));
    }

    void RecordSoundOnly()
    {
        if (_overlay != null || _busy || _session != null || _options != null) return;
        if (!_settings.RecordSystemAudio && !_settings.RecordMic)
        {
            Notify("Nothing to record", "Turn on computer sound or the microphone in Options > Recording.", null, error: true, force: true);
            return;
        }
        BeginRecording(new Recording.RecordTarget(Recording.RecordKind.Audio, Rectangle.Empty));
    }

    void BeginRecording(Recording.RecordTarget target, TimeSpan? stopAfter = null)
    {
        if (_session != null) return;
        var session = new Recording.RecordSession(target, _settings, (t, m, p, err) =>
        {
            Notify(t, m, p, err, force: err || p != null);
            // a click on "Recording saved" opens the editor
            if (!err && p != null && Editor.EditorForm.CanOpen(p)) _balloonAction = () => OpenEditor(p);
        }) { StopAfter = stopAfter, SkipCountdown = stopAfter != null };
        session.OnSaved = (path, text) => OnRecordingSaved(session, path);
        _session = session;
        session.Ended += () => _ui.Post(_ => EndRecording(session), null);
        _hotkeys.Register(HotkeyPause, (Keys)_settings.PauseHotkey);
        UpdateRecordMenu();
        session.Begin();
    }

    void EndRecording(Recording.RecordSession session)
    {
        if (_session != session) return;
        _session = null;
        _hotkeys.Unregister(HotkeyPause);
        session.Dispose();
        UpdateRecordMenu();
        TrySaveSettings(); // keeps the drawing colour and size picked during the recording
    }

    /// <summary>A recording was saved. True when it is taken care of here (editor or GIF), so no note is needed.</summary>
    bool OnRecordingSaved(Recording.RecordSession session, string path)
    {
        if (session.AsGif)
        {
            _ui.Post(_ => MakeGif(path), null);
            return true;
        }
        _settings.AddRecent(path);
        TrySaveSettings();
        if (_settings.OpenEditorAfterRecording && Editor.EditorForm.CanOpen(path) && !path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            _ui.Post(_ => OpenEditor(path), null);
            return true;
        }
        return false;
    }

    /// <summary>Turn the temporary MP4 of a "record as GIF" into a GIF in the recordings folder.</summary>
    async void MakeGif(string mp4)
    {
        var name = Path.ChangeExtension(Path.GetFileName(mp4), ".gif");
        var target = Path.Combine(_settings.VideoFolder, name);
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(_settings.VideoFolder, $"{Path.GetFileNameWithoutExtension(name)} ({i}).gif");
        Notify("Making the GIF…", "This takes a few seconds.", null, force: true);
        try
        {
            var s = _settings;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Size size; long duration;
                using (var r = new Editor.MediaReader(mp4, video: true, hardware: false)) { size = r.SourceSize; duration = r.Duration; }
                var spec = new Editor.ExportSpec(mp4, target, Editor.ExportKind.Gif, new[] { new TimeRange(0, Math.Max(1, duration)) },
                    GifWriter.ScaledSize(size, s.GifMaxWidth), s.GifFps, s.VideoQuality, false, s.GifDither, s.HardwareEncoder);
                Editor.Exporter.Run(spec, null, CancellationToken.None);
            });
            long bytes = new FileInfo(target).Length;
            Notify("GIF saved", $"{Path.GetFileName(target)}  ·  {RecordingMath.FileSize(bytes)}", target, force: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Notify("Could not make the GIF", ex.Message, null, error: true, force: true);
        }
        finally
        {
            try { File.Delete(mp4); } catch (Exception ex) { CrashLog.Write(ex); }
        }
    }

    void OpenEditor(string path) =>
        Editor.EditorForm.Open(path, () => _settings, TrySaveSettings, (t, m, p, err) => Notify(t, m, p, err, force: err));

    void OpenRecordingDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open a recording",
            Filter = "Recordings (*.mp4; *.m4a)|*.mp4;*.m4a|All files|*.*",
            InitialDirectory = Directory.Exists(_settings.VideoFolder) ? _settings.VideoFolder : "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _settings.AddRecent(dlg.FileName);
        TrySaveSettings();
        OpenEditor(dlg.FileName);
    }

    void FillRecentMenu()
    {
        _recentItem.DropDownItems.Clear();
        var files = _settings.RecentRecordings.Where(File.Exists).ToList();
        foreach (var path in files)
        {
            var p = path;
            var item = new ToolStripMenuItem(Path.GetFileName(p), null, (_, _) => OpenEditor(p)) { ToolTipText = p };
            _recentItem.DropDownItems.Add(item);
        }
        if (files.Count == 0) _recentItem.DropDownItems.Add(new ToolStripMenuItem("No recordings yet") { Enabled = false });
        _recentItem.Enabled = true;
    }

    // ---------- instant replay ----------

    void ToggleReplay()
    {
        if (_replay.Running) StopReplay();
        else StartReplay(announce: true);
        _settings.ReplayEnabled = _replay.Running;
        TrySaveSettings();
    }

    void StartReplay(bool announce)
    {
        if (_replay.Running) return;
        if (!Recording.CaptureInterop.IsSupported) { Notify("Instant replay needs Windows 10 2004 or later", "", null, error: true, force: true); return; }
        try
        {
            _replay.Start(_settings);
            _hotkeys.Register(HotkeyReplay, (Keys)_settings.ReplayHotkey);
            if (announce)
                Notify("Instant replay is on", $"PRTSCN keeps the last {ReplayLength()} of this screen. Press {HotkeyManager.Describe((Keys)_settings.ReplayHotkey)} to save it.", null, force: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Notify("Instant replay failed", ex.Message, null, error: true, force: true);
        }
        UpdateRecordMenu();
    }

    void StopReplay()
    {
        _hotkeys.Unregister(HotkeyReplay);
        _replay.Stop();
        UpdateRecordMenu();
    }

    string ReplayLength() => _settings.ReplaySeconds >= 60 ? $"{_settings.ReplaySeconds / 60} min" : $"{_settings.ReplaySeconds} s";

    async void SaveReplay()
    {
        if (!_replay.Running) { Notify("Instant replay is off", "Turn it on in the tray menu first.", null, error: true, force: true); return; }
        if (_replay.Saving) return;
        Notify("Saving the replay…", $"The last {ReplayLength()}. This takes a few seconds.", null, force: true);
        try
        {
            var path = await _replay.SaveAsync(_settings.VideoFolder, _settings.ReplaySeconds, _settings);
            _settings.AddRecent(path);
            TrySaveSettings();
            long bytes = new FileInfo(path).Length;
            Notify("Replay saved", $"{Path.GetFileName(path)}  ·  {RecordingMath.FileSize(bytes)}", path, force: true);
            _balloonAction = () => OpenEditor(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Notify("Could not save the replay", ex.Message, null, error: true, force: true);
        }
    }

    // ---------- scheduled recording ----------

    void ShowSchedule()
    {
        using var f = new ScheduleForm(_settings.Schedule);
        if (f.ShowDialog() != DialogResult.OK || f.Result == null) return;
        _settings.Schedule = f.Result;
        TrySaveSettings();
        var s = f.Result;
        Notify("Recording scheduled", $"{(s.SoundOnly ? "Sound" : "The screen")} {RecordSchedule.Describe(s.Start, DateTime.Now)}, for {s.Minutes} min. Keep PRTSCN running.", null, force: true);
        UpdateAdvancedMenu();
    }

    void CancelSchedule()
    {
        _settings.Schedule = null;
        TrySaveSettings();
        UpdateAdvancedMenu();
    }

    void OnClock()
    {
        if (_replay.Running && _replay.Error is { } err)
        {
            StopReplay();
            _settings.ReplayEnabled = false;
            TrySaveSettings();
            Notify("Instant replay stopped", err.Message, null, error: true, force: true);
        }
        if (_settings.Schedule is { } s && DateTime.Now >= s.Start)
        {
            _settings.Schedule = null;
            TrySaveSettings();
            if (_session != null || _overlay != null || _busy)
            {
                Notify("Scheduled recording skipped", "PRTSCN was busy with another capture or recording.", null, error: true, force: true);
                return;
            }
            var target = s.SoundOnly
                ? new Recording.RecordTarget(Recording.RecordKind.Audio, Rectangle.Empty)
                : new Recording.RecordTarget(Recording.RecordKind.Screen, Screen.FromPoint(Cursor.Position).Bounds);
            BeginRecording(target, TimeSpan.FromMinutes(s.Minutes));
        }
    }

    void UpdateAdvancedMenu()
    {
        _replayItem.Checked = _replay.Running;
        _replaySaveItem.Enabled = _replay.Running && !_replay.Saving;
        _replaySaveItem.Text = $"Save the last {ReplayLength()}";
        var s = _settings.Schedule;
        _scheduleItem.Text = s == null ? "Schedule a recording…" : $"Scheduled: {RecordSchedule.Describe(s.Start, DateTime.Now)}, {s.Minutes} min…";
        _scheduleCancelItem.Visible = s != null;
    }

    void UpdateRecordMenu()
    {
        bool on = _session != null;
        _stopItem.Visible = _pauseItem.Visible = on;
        _recordItem.Visible = _recordWindowItem.Visible = _recordScreenItem.Visible = _recordSoundItem.Visible = !on;
        _captureItem.Enabled = _windowItem.Enabled = _instantItem.Enabled = _scrollItem.Enabled = !on;
        _tray.Text = on ? "PRTSCN: recording" : _replay.Running ? "PRTSCN: instant replay on" : "PRTSCN";
    }

    ToastWindow Toast() => _toast is { IsDisposed: false } ? _toast : (_toast = new ToastWindow());

    // ---------- delayed capture ----------

    void DelayedCapture(int seconds, bool windowMode)
    {
        if (_overlay != null || _busy) return;
        _busy = true;
        int left = seconds;
        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        string what = windowMode ? "Window capture" : "Capture";
        Native.EscPressed(); // clear an earlier press
        Toast().ShowText(screen, left.ToString(), $"{what} in {left} s   ·   Esc or click the tray icon to cancel", big: true);
        _countdown = new System.Windows.Forms.Timer { Interval = 100 };
        var end = DateTime.Now.AddSeconds(seconds);
        _countdown.Tick += (_, _) =>
        {
            if (Native.EscPressed()) { CancelCountdown(); return; }
            int remaining = (int)Math.Ceiling((end - DateTime.Now).TotalSeconds);
            if (remaining > 0)
            {
                if (remaining != left)
                {
                    left = remaining;
                    Toast().ShowText(screen, left.ToString(), $"{what} in {left} s   ·   Esc or click the tray icon to cancel", big: true);
                }
                return;
            }
            StopCountdown();
            bool hidden = !Toast().Excluded;
            Toast().Hide();
            _busy = false;
            if (hidden) { var t = new System.Windows.Forms.Timer { Interval = 80 }; t.Tick += (_, _) => { t.Dispose(); StartCapture(windowMode); }; t.Start(); }
            else StartCapture(windowMode);
        };
        _countdown.Start();
    }

    void StopCountdown()
    {
        _countdown?.Stop();
        _countdown?.Dispose();
        _countdown = null;
    }

    void CancelCountdown()
    {
        StopCountdown();
        _toast?.Hide();
        _busy = false;
    }

    // ---------- scrolling capture ----------

    async void RunScroll(Rectangle region)
    {
        if (_busy) return;
        if (region.Height < ScrollStitcher.MinOverlapRows * 3 || region.Width < 40)
        {
            Notify("Area too small", "Select a bigger part of the page for a scrolling capture.", null, error: true, force: true);
            return;
        }
        _busy = true;
        try
        {
            var screen = Screen.FromRectangle(region).Bounds;
            var toast = Toast();
            toast.ShowText(screen, "Scrolling capture…", "Starting   ·   Esc to stop", avoid: region);
            var result = await ScrollCapture.RunAsync(region, _settings.ScrollDelayMs, toast, screen);
            toast.Hide();
            if (result.Image == null) { Notify("Scrolling capture failed", result.StopReason + " Details: " + ScrollCapture.LogPath, null, error: true, force: true); return; }
            string summary = $"{result.Frames} frames · {result.Image.Width} × {result.Image.Height}.  {result.StopReason}";
            var form = new ScrollResultForm(result.Image, _settings, summary, (t, m, p) => Notify(t, m, p));
            form.Show();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _toast?.Hide();
            Notify("Scrolling capture failed", ex.Message, null, error: true, force: true);
        }
        finally
        {
            _busy = false;
        }
    }

    void InstantCapture()
    {
        if (_overlay != null || _busy) return;
        try
        {
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            using var bmp = ScreenGrabber.Capture(screen);
            var path = ImageOutput.SaveToFolder(bmp, _settings);
            if (_settings.CopyAfterInstantCapture) ImageOutput.CopyToClipboard(bmp);
            Notify("Saved", Path.GetFileName(path) + (_settings.CopyAfterInstantCapture ? " (also copied)" : ""), path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Notify("Capture failed", ex.Message, null, error: true, force: true);
        }
    }

    void ShowOptions()
    {
        if (_options != null) { _options.Activate(); return; }
        _hotkeys.UnregisterAll(); // so pressing Print Screen in the hotkey box does not start a capture
        _options = new OptionsForm(_settings);
        _options.FormClosed += (_, _) =>
        {
            if (_options!.DialogResult == DialogResult.OK)
            {
                _settings = _options.Result;
                TrySaveSettings();
                TryApplyAutoStart();
                // the replay picks up new length / quality / sound / watermark by restarting
                if (_replay.Running) _replay.Stop();
                if (_settings.ReplayEnabled) StartReplay(announce: false);
                else UpdateRecordMenu();
            }
            _options = null;
            RegisterHotkeys(announce: true);
        };
        _options.Show();
        _options.Activate();
    }

    void TryApplyAutoStart()
    {
        try { AutoStart.Apply(_settings.StartWithWindows); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    void TrySaveSettings()
    {
        try { SettingsStore.Save(_settingsPath, _settings); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    void OpenFolder() => OpenFolder(_settings.SaveFolder);

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    static void RevealInExplorer(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    void About()
    {
        var v = typeof(TrayApp).Assembly.GetName().Version;
        MessageBox.Show($"PRTSCN {v?.ToString(3)}\n\nCapture, mark up, copy or save.\n\nSettings: {_settingsPath}", "About PRTSCN",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Notify(string title, string text, string? path, bool error = false, bool force = false)
    {
        if (!_settings.ShowNotifications && !force) return;
        _balloonPath = path;
        _balloonAction = null;
        _tray.ShowBalloonTip(3000, title, text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    void Exit()
    {
        _exiting = true;
        _signal.Set();
        _overlay?.Close();
        _session?.Stop(); // saves what was recorded
        _session?.Dispose();
        _clock.Dispose();
        _replay.Dispose();
        StopCountdown();
        _toast?.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
