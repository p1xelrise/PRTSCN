using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using PRTSCN.Core;

namespace PRTSCN.Editor;

/// <summary>
/// Preview, trim, cut and save a recording (MP4 / M4A): player, timeline and an export panel
/// for MP4, GIF or sound only. The original file is never changed; Save writes a new file next to it.
/// </summary>
internal sealed class EditorForm : Form
{
    static readonly List<EditorForm> OpenEditors = new();

    string _path;
    readonly Func<AppSettings> _getSettings;
    AppSettings _settings => _getSettings();
    readonly Action _saveSettings;
    readonly Action<string, string, string?, bool> _notify;
    readonly float _s;
    Player _player = null!;
    readonly EditList _edits;
    readonly Stack<EditState> _undo = new(), _redo = new();
    readonly CancellationTokenSource _closing = new();

    // layout
    readonly Header _header;
    readonly Panel _right;
    readonly Panel _center;
    readonly PlayerView _view;
    readonly Timeline _timeline;
    readonly EditorButton _back, _play, _fwd, _undoBtn, _cut, _frameBtn, _blurBtn;
    readonly Label _time;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    readonly System.Windows.Forms.Timer _resizeDebounce = new() { Interval = 150 };

    // export panel
    readonly string[] _kindNames;
    readonly ExportKind[] _kinds;
    readonly DarkSegmented _format, _quality;
    readonly ChoiceRow _mp4Size, _mp4Fps, _gifWidth, _gifFps;
    readonly DarkToggle _sound, _dither;
    readonly Label _lblFormat, _lblQuality, _lblSound, _lblDither, _gifNote, _summary, _status;
    readonly ProgressLine _progress;
    readonly EditorButton _save, _cancel, _saveAs, _copy, _folder, _rename, _delete;
    readonly Size[] _mp4Sizes;
    readonly int[] _mp4FpsValues;
    readonly int[] _gifWidths;

    long _pos;
    CancellationTokenSource? _export;

    /// <summary>
    /// Developer check: opens the editor off-screen on a file, adds a cut and a selection,
    /// waits for the picture, thumbnails and sound wave, and saves the window as a PNG.
    /// </summary>
    internal static void RenderShot(string path, string png, int formatIndex)
    {
        var settings = new AppSettings();
        if (Environment.GetEnvironmentVariable("PRTSCN_REAL_SETTINGS") == "1")
            settings = SettingsStore.Load(SettingsStore.DefaultPath());
        if (Environment.GetEnvironmentVariable("PRTSCN_HW") is { } hw) settings.HardwareEncoder = hw == "1";
        var f = new EditorForm(path, () => settings, () => { }, (_, _, _, _) => { })
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-5000, -5000),
            ShowInTaskbar = false,
        };
        f.Show();
        long d = f._edits.Duration;
        f._edits.AddCut(new TimeRange(d * 55 / 100, d * 68 / 100));
        f._edits.SetTrim(d * 4 / 100, d * 95 / 100);
        f._timeline.Selection = new TimeRange(d * 20 / 100, d * 30 / 100);
        f._format.Selected = Math.Min(formatIndex, f._kinds.Length - 1);
        f._edits.AddBlur(new BlurBox(new TimeRange(0, d), 0.02f, 0.05f, 0.3f, 0.35f));
        if (f._player.HasVideo) f.ToggleBlurMode();
        f.SeekTo(d * 35 / 100);
        f.EditsChanged();
        var until = DateTime.Now.AddSeconds(4);
        while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(15); }
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }
        var log = new List<string>();
        if (f._view.BlurMode)
        {
            // blur editing: drag the bar's ends on the timeline, then move and resize the box on the picture
            var tl0 = f._timeline;
            int undo0 = f._undo.Count;
            tl0.SimulateDrag(tl0.BlurBarPoint(0, -1), new Point(tl0.XOf(d * 20 / 100), tl0.BlurBarPoint(0, -1).Y));
            tl0.SimulateDrag(tl0.BlurBarPoint(0, 1), new Point(tl0.XOf(d * 80 / 100), tl0.BlurBarPoint(0, 1).Y));
            log.Add($"blur bar drag: range {f._edits.Blurs[0].Range.Start * 100.0 / d:0}%-{f._edits.Blurs[0].Range.End * 100.0 / d:0}% (want 20-80), undo +{f._undo.Count - undo0}");
            tl0.SimulateDrag(tl0.BlurBarPoint(0, 0), new Point(tl0.BlurBarPoint(0, 0).X + tl0.XOf(d / 10) - tl0.XOf(0), tl0.BlurBarPoint(0, 0).Y));
            log.Add($"blur bar move: range {f._edits.Blurs[0].Range.Start * 100.0 / d:0}%-{f._edits.Blurs[0].Range.End * 100.0 / d:0}% (want 30-90)");
            f.SeekTo(d * 50 / 100); Pump0(0.3);
            var v = f._view;
            var b0 = f._edits.Blurs[0];
            var mid = v.BoxPoint(0, 0);
            v.SimulateDrag(mid, new Point(mid.X + v.Picture.Width / 5, mid.Y + v.Picture.Height / 5));
            var b1 = f._edits.Blurs[0];
            log.Add($"box move: ({b0.X:0.00},{b0.Y:0.00}) -> ({b1.X:0.00},{b1.Y:0.00}) size {b1.W:0.00}x{b1.H:0.00} (want +0.20,+0.20, same size)");
            var br = v.BoxPoint(0, 2 | 8);
            v.SimulateDrag(br, new Point(br.X + v.Picture.Width / 10, br.Y + v.Picture.Height / 10));
            var b2 = f._edits.Blurs[0];
            log.Add($"box resize: {b1.W:0.00}x{b1.H:0.00} -> {b2.W:0.00}x{b2.H:0.00} at ({b2.X:0.00},{b2.Y:0.00}) (want +0.10 each, same corner), undo steps {f._undo.Count - undo0}");
            // a new box drawn with the mouse, like a user would (covers the timeline selection)
            var pr = v.Picture;
            v.SimulateDrag(new Point(pr.X + pr.Width * 70 / 100, pr.Y + pr.Height * 60 / 100), new Point(pr.X + pr.Width * 95 / 100, pr.Y + pr.Height * 95 / 100));
            log.Add($"drawn box: {(f._edits.Blurs.Count > 1 ? f._edits.Blurs[^1].ToString() : "none")} selection after={f._timeline.Selection?.ToString() ?? "cleared"}");
            if (f._edits.Blurs.Count > 1)
            {
                // cutting the blurred part away should warn, and Ctrl+Z bring it back
                f._timeline.Selection = f._edits.Blurs[^1].Range;
                f.CutSelection();
                log.Add($"cut over blur: hidden={f._edits.HiddenBlurs} status='{f._status.Text}'");
                f.Undo();
                log.Add($"undo: hidden={f._edits.HiddenBlurs}");
            }
            Pump0(0.5);
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                bmp.Save(Path.ChangeExtension(png, ".blur-edit.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            f.ToggleBlurMode();
        }
        static void Pump0(double seconds)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
        }

        // exercise: play for 1.5 s, then save with the chosen format
        void Pump(double seconds, Func<bool>? done = null)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end && !(done?.Invoke() ?? false)) { Application.DoEvents(); Thread.Sleep(10); }
        }
        // a real mouse drag across the thumbnails should select a part and enable Cut
        f._timeline.Selection = null;
        log.Add($"before drag: cut enabled={f._cut.Enabled}");
        var tl = f._timeline;
        int my = tl.Height / 2, x0 = tl.Width * 35 / 100, x1 = tl.Width * 45 / 100;
        static IntPtr XY(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(tl.Handle, 0x201, (IntPtr)1, XY(x0, my));
        for (int x = x0; x <= x1; x += 10) { SendMessage(tl.Handle, 0x200, (IntPtr)1, XY(x, my)); Application.DoEvents(); }
        SendMessage(tl.Handle, 0x202, IntPtr.Zero, XY(x1, my));
        Pump(0.2);
        log.Add($"after drag: selection={tl.Selection} cut enabled={f._cut.Enabled}");
        int cutsBefore = f._edits.Cuts.Count;
        f._cut.PerformClickForTest();
        log.Add($"after Cut: cuts {cutsBefore} -> {f._edits.Cuts.Count}");
        // two-step cut with nothing selected: Cut at the start, move, Cut again
        long d2 = f._edits.Duration;
        f.SeekTo(d2 * 60 / 100); Pump(0.2);
        f._cut.PerformClickForTest();
        log.Add($"mark: {f._timeline.Mark} button='{f._cut.Text}'");
        f.SeekTo(d2 * 70 / 100); Pump(0.2);
        f._cut.PerformClickForTest();
        log.Add($"two-step cut: pos={f._pos} status='{f._status.Text}' cuts={string.Join(",", f._edits.Cuts)} -> {f._edits.Cuts.Count}, mark={f._timeline.Mark?.ToString() ?? "none"} button='{f._cut.Text}'");

        int frames = 0;
        f._player.FrameReady += (_, _) => frames++;
        long before = f._pos;
        f.TogglePlay();
        Pump(1.5);
        log.Add($"play: from {before / 1e7:0.00}s to {f._player.Position / 1e7:0.00}s, {frames} frames, playing={f._player.Playing}");
        // pause only if still playing: at the end it stops by itself, and Play there starts over
        bool wasPlaying = f._player.Playing;
        if (wasPlaying) f.TogglePlay();
        Pump(0.3);
        log.Add($"{(wasPlaying ? "paused" : "had stopped at the end")} at {f._pos / 1e7:0.00}s playing={f._player.Playing} (want False)");
        // play to the end through the cut
        f.SeekTo(d * 50 / 100);
        frames = 0;
        f.TogglePlay();
        Pump(6, () => !f._player.Playing);
        log.Add($"played to {f._player.Position / 1e7:0.00}s (trim out {f._edits.TrimOut / 1e7:0.00}s), {frames} frames, ended={!f._player.Playing}");
        var target = Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-saved" + f.CurrentKind switch { ExportKind.Gif => ".gif", ExportKind.Audio => ".m4a", _ => ".mp4" });
        if (File.Exists(target)) File.Delete(target);
        f.Save(target);
        Pump(60, () => f._export == null);
        log.Add($"save: {f._status.Text} exists={File.Exists(target)}");
        log.Add($"blurs at save: {string.Join("; ", f._edits.Blurs)} hw={settings.HardwareEncoder}");
        long OutTime(long src)
        {
            long acc = 0;
            foreach (var k in f._edits.Kept) { if (src < k.End) return acc + Math.Max(0, src - k.Start); acc += k.Length; }
            return acc;
        }
        foreach (var b in f._edits.Blurs)
            log.Add($"blur source {b.Range.Start / 1e7:0.00}-{b.Range.End / 1e7:0.00}s = output {OutTime(b.Range.Start) / 1e7:0.00}-{OutTime(b.Range.End) / 1e7:0.00}s; kept {string.Join(",", f._edits.Kept.Select(k => $"{k.Start / 1e7:0.00}-{k.End / 1e7:0.00}"))}");
        if (File.Exists(target) && target.EndsWith(".mp4"))
        {
            using (var saved = DecodeFrame(target, 3_000_000, 30)) saved?.Save(Path.ChangeExtension(png, ".saved-frame.png"));
            // a frame where the (moved) blur is showing: the middle of the saved video
            long outLen = f._edits.Kept.Sum(k => k.Length);
            using (var saved = DecodeFrame(target, outLen / 2, 30)) saved?.Save(Path.ChangeExtension(png, ".saved-mid.png"));
        }
        File.WriteAllLines(png + ".log", log);
        f.Close();
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Files the editor can open (what PRTSCN records).</summary>
    public static bool CanOpen(string path) =>
        path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);

    public static void Open(string path, Func<AppSettings> settings, Action saveSettings, Action<string, string, string?, bool> notify)
    {
        var open = OpenEditors.FirstOrDefault(f => string.Equals(f._path, path, StringComparison.OrdinalIgnoreCase));
        if (open != null)
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        if (!File.Exists(path)) { notify("Recording not found", path, null, true); return; }
        try
        {
            var f = new EditorForm(path, settings, saveSettings, notify);
            f.Show();
            f.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            notify("Can't open the recording", ex.Message, null, true);
        }
    }

    EditorForm(string path, Func<AppSettings> settings, Action saveSettings, Action<string, string, string?, bool> notify)
    {
        _path = path;
        _getSettings = settings;
        _saveSettings = saveSettings;
        _notify = notify;
        _s = DeviceDpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(14, false, _s);
        Icon = Theme.AppIcon(32);
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min((int)(1280 * _s), (int)(wa.Width * 0.92)), Math.Min((int)(800 * _s), (int)(wa.Height * 0.92)));
        MinimumSize = new Size((int)(900 * _s), (int)(600 * _s));

        CreatePlayer();
        _edits = new EditList(Math.Max(1, _player.Duration));

        // ---------- center: player, transport, timeline ----------
        _center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _view = new PlayerView(this);
        _view.Click += (_, _) => { if (!_view.BlurMode) TogglePlay(); };
        _timeline = new Timeline(_edits, _s);
        _timeline.SetTracks(_player.HasVideo, _player.HasAudio);
        _timeline.Seek += SeekTo;
        _timeline.BeforeEdit += PushUndo;
        _timeline.EditsChanged += EditsChanged;
        _timeline.SelectionChanged += () => { if (_timeline.Selection is { Length: > 0 }) _timeline.Mark = null; if (_cut != null) UpdateCutButton(); };
        _back = new EditorButton("", IconKind.SkipBack, ButtonStyle.Icon, _s) { Hint = "Back 5 seconds  (Shift+←)" };
        _play = new EditorButton("", IconKind.Play, ButtonStyle.Play, _s) { Hint = "Play / pause  (Space)" };
        _fwd = new EditorButton("", IconKind.SkipForward, ButtonStyle.Icon, _s) { Hint = "Forward 5 seconds  (Shift+→)" };
        _back.Click += (_, _) => SeekTo(_pos - 50_000_000);
        _fwd.Click += (_, _) => SeekTo(_pos + 50_000_000);
        _play.Click += (_, _) => TogglePlay();
        _time = new Label { AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Font = MonoFont(13) };
        _undoBtn = new EditorButton("", IconKind.Undo, ButtonStyle.Icon, _s) { Hint = "Undo  (Ctrl+Z)", Enabled = false };
        _undoBtn.Click += (_, _) => Undo();
        _cut = new EditorButton("Cut", IconKind.Scissors, ButtonStyle.Ghost, _s) { Hint = "Remove a part  (Delete). Drag across the timeline, or press Cut at the start and again at the end." };
        _cut.Click += (_, _) => CutSelection();
        _frameBtn = new EditorButton("Frame", IconKind.Camera, ButtonStyle.Ghost, _s) { Hint = "Save this frame as an image and copy it", Enabled = _player.HasVideo };
        _frameBtn.Click += (_, _) => SaveFrame();
        _blurBtn = new EditorButton("Blur", IconKind.Blur, ButtonStyle.Ghost, _s)
        {
            Hint = "Hide a part of the picture  (B). Drag a box on the video; it covers the selected part of the timeline, or all of it. Click a box to remove it.",
            Enabled = _player.HasVideo,
        };
        _blurBtn.Click += (_, _) => ToggleBlurMode();
        _center.Controls.AddRange(new Control[] { _view, _back, _play, _fwd, _time, _undoBtn, _blurBtn, _cut, _frameBtn, _timeline });
        _center.Resize += (_, _) => LayoutCenter();

        // ---------- right: export panel ----------
        _right = new Panel { Dock = DockStyle.Right, Width = (int)(320 * _s), BackColor = Theme.Bg };
        _right.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, 0, _right.Height); };

        var kinds = new List<(string, ExportKind)>();
        if (_player.HasVideo) { kinds.Add(("MP4", ExportKind.Mp4)); kinds.Add(("GIF", ExportKind.Gif)); }
        if (_player.HasAudio) kinds.Add((_player.HasVideo ? "Sound only" : "M4A sound", ExportKind.Audio));
        _kindNames = kinds.Select(k => k.Item1).ToArray();
        _kinds = kinds.Select(k => k.Item2).ToArray();
        _lblFormat = Caption("FORMAT");
        _format = new DarkSegmented(_kindNames);
        _lblQuality = Caption("QUALITY");
        _quality = new DarkSegmented("High", "Balanced", "Small")
        {
            Selected = _settings.VideoQuality switch { VideoQuality.Balanced => 1, VideoQuality.Small => 2, _ => 0 },
        };

        var src = _player.SourceSize;
        var sizes = new List<Size> { src };
        foreach (int h in new[] { 1080, 720, 480 })
            if (h < src.Height) sizes.Add(RecordingMath.EvenSize(new Size((int)Math.Round(src.Width * h / (double)src.Height), h)));
        _mp4Sizes = sizes.ToArray();
        _mp4Size = new ChoiceRow("Size", _s);
        _mp4Size.SetOptions(_mp4Sizes.Select((z, i) => i == 0 ? $"{z.Width} × {z.Height} (original)" : $"{z.Width} × {z.Height}").ToArray(), 0);

        int srcFps = Math.Max(1, (int)Math.Round(_player.Fps));
        _mp4FpsValues = new[] { srcFps }.Concat(new[] { 30, 15 }.Where(f => f < srcFps)).ToArray();
        _mp4Fps = new ChoiceRow("Frame rate", _s);
        _mp4Fps.SetOptions(_mp4FpsValues.Select((f, i) => i == 0 ? $"{f} fps (as recorded)" : $"{f} fps").ToArray(), 0);
        _lblSound = RowLabel("Include sound");
        _sound = new DarkToggle { Checked = _player.HasAudio, Enabled = _player.HasAudio };

        _gifWidths = AppSettings.GifWidthChoices.Where(w => w == 0 || w < src.Width).ToArray();
        _gifWidth = new ChoiceRow("Width", _s);
        _gifWidth.SetOptions(_gifWidths.Select(w => w == 0 ? $"{src.Width} px (full)" : $"{w} px").ToArray(),
            Math.Max(0, Array.IndexOf(_gifWidths, _settings.GifMaxWidth)));
        _gifFps = new ChoiceRow("Frame rate", _s);
        _gifFps.SetOptions(AppSettings.GifFpsChoices.Select(f => $"{f} fps").ToArray(), Math.Max(0, Array.IndexOf(AppSettings.GifFpsChoices, _settings.GifFps)));
        _lblDither = RowLabel("Smooth gradients (dither)");
        _dither = new DarkToggle { Checked = _settings.GifDither };
        _gifNote = new Label { Text = "GIFs have no sound and get big quickly: keep them short and narrow.", ForeColor = Theme.TextMuted, BackColor = Theme.Bg, Font = Theme.UiFont(12, false, _s), AutoSize = false };

        _summary = new Label { ForeColor = Theme.TextMuted, BackColor = Theme.Bg, Font = Theme.UiFont(12, false, _s), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        _status = new Label { ForeColor = Theme.TextMuted, BackColor = Theme.Bg, Font = Theme.UiFont(12, false, _s), AutoSize = false };
        _progress = new ProgressLine(_s) { Visible = false };
        _save = new EditorButton("Save    Ctrl+S", IconKind.Save, ButtonStyle.Primary, _s) { Hint = "Save the edited copy next to the recording" };
        _cancel = new EditorButton("Cancel", null, ButtonStyle.Ghost, _s) { Visible = false };
        _saveAs = new EditorButton("Save as…", null, ButtonStyle.Ghost, _s);
        _copy = new EditorButton("Copy file", IconKind.Copy, ButtonStyle.Ghost, _s) { Hint = "Copy the recording, to paste in Explorer, chat or email" };
        _folder = new EditorButton("Open folder", IconKind.Folder, ButtonStyle.Ghost, _s);
        _rename = new EditorButton("Rename…", IconKind.Pen, ButtonStyle.Ghost, _s);
        _delete = new EditorButton("Delete", IconKind.Trash, ButtonStyle.Ghost, _s) { Danger = true, Hint = "Move the recording to the Recycle Bin" };
        _save.Click += (_, _) => Save(null);
        _saveAs.Click += (_, _) => SaveAs();
        _cancel.Click += (_, _) => _export?.Cancel();
        _copy.Click += (_, _) => CopyFile();
        _folder.Click += (_, _) => Reveal(_path);
        _rename.Click += (_, _) => Rename();
        _delete.Click += (_, _) => Delete();
        _right.Controls.AddRange(new Control[] { _lblFormat, _format, _lblQuality, _quality, _mp4Size, _mp4Fps, _lblSound, _sound,
            _gifWidth, _gifFps, _lblDither, _dither, _gifNote, _summary, _status, _progress, _save, _cancel, _saveAs, _copy, _folder, _rename, _delete });
        _right.Resize += (_, _) => LayoutRight();

        int startKind = 0;
        if (_settings.RecordFormat == RecordFormat.Gif && _kinds.Contains(ExportKind.Gif)) startKind = Array.IndexOf(_kinds, ExportKind.Gif);
        _format.Selected = startKind;
        _format.SelectedChanged += (_, _) => { LayoutRight(); UpdateSummary(); };
        foreach (var c in new Control[] { _quality, _mp4Size, _mp4Fps, _sound, _gifWidth, _gifFps, _dither })
        {
            if (c is DarkSegmented ds) ds.SelectedChanged += (_, _) => UpdateSummary();
            if (c is ChoiceRow cr) cr.SelectedChanged += (_, _) => UpdateSummary();
            if (c is DarkToggle dt) dt.CheckedChanged += (_, _) => UpdateSummary();
        }

        // ---------- header ----------
        _header = new Header(this) { Dock = DockStyle.Top, Height = (int)(48 * _s) };

        Controls.Add(_center);
        Controls.Add(_right);
        Controls.Add(_header);

        var tips = new ToolTip();
        foreach (var b in new[] { _back, _play, _fwd, _undoBtn, _cut, _frameBtn, _save, _copy, _delete })
            if (b.Hint != null) tips.SetToolTip(b, b.Hint);

        _tick.Tick += (_, _) => OnTick();
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); _player.SetViewSize(_view.ClientSize); };
        _view.Resize += (_, _) => { _resizeDebounce.Stop(); _resizeDebounce.Start(); };

        UpdateTitle();
        UpdateSummary();
        OpenEditors.Add(this);
    }

    void CreatePlayer()
    {
        _player = new Player(_path, this);
        _player.FrameReady += (bmp, t) => _view.SetFrame(bmp);
        _player.Ended += () => { _pos = _edits.TrimOut; _timeline.Playhead = _pos; UpdateTransport(); };
    }

    Font MonoFont(float px)
    {
        var f = new Font("Cascadia Mono", px * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        return f.Name == "Cascadia Mono" ? f : new Font("Consolas", px * _s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.TextMuted, BackColor = Theme.Bg, Font = Theme.UiFont(11, true, _s),
    };

    Label RowLabel(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Font = Theme.UiFont(14, false, _s),
    };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SizeDialog.DarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutCenter();
        LayoutRight();
        _player.SetViewSize(_view.ClientSize);
        _player.Seek(0);
        _tick.Start();
        LoadTimelineData();
        UpdateTransport();
    }

    // ---------- layout ----------

    int S(float v) => (int)Math.Round(v * _s);

    void LayoutCenter()
    {
        int pad = S(16), w = _center.ClientSize.Width - pad * 2, h = _center.ClientSize.Height;
        if (w < 50) return;
        _timeline.SetBounds(pad, h - pad - _timeline.Height, w, _timeline.Height);
        int ty = _timeline.Top - S(12) - S(40);
        _back.Location = new Point(pad, ty + (S(40) - _back.Height) / 2);
        _play.Location = new Point(_back.Right + S(4), ty + (S(40) - _play.Height) / 2);
        _fwd.Location = new Point(_play.Right + S(4), ty + (S(40) - _fwd.Height) / 2);
        _time.Location = new Point(_fwd.Right + S(10), ty + (S(40) - _time.Height) / 2);
        _frameBtn.Location = new Point(pad + w - _frameBtn.Width, ty + (S(40) - _frameBtn.Height) / 2);
        _cut.Location = new Point(_frameBtn.Left - S(8) - _cut.Width, _frameBtn.Top);
        _blurBtn.Location = new Point(_cut.Left - S(8) - _blurBtn.Width, _frameBtn.Top);
        _undoBtn.Location = new Point(_blurBtn.Left - S(8) - _undoBtn.Width, _frameBtn.Top);
        _view.SetBounds(pad, pad, w, Math.Max(S(60), ty - S(12) - pad));
    }

    void LayoutRight()
    {
        int pad = S(16), w = _right.ClientSize.Width - pad * 2, y = pad;
        var kind = CurrentKind;
        void Place(Control c, int height, int gap)
        {
            c.Visible = true;
            c.SetBounds(pad, y, w, height);
            y += height + gap;
        }
        void Hide(params Control[] cs) { foreach (var c in cs) c.Visible = false; }
        void Toggle(Label l, DarkToggle t)
        {
            l.Visible = t.Visible = true;
            int rowH = S(32);
            l.Location = new Point(pad + S(8), y + (rowH - l.Height) / 2);
            t.Location = new Point(pad + w - t.Width - S(8), y + (rowH - t.Height) / 2);
            y += rowH + S(4);
        }

        _lblFormat.Location = new Point(pad, y); _lblFormat.Visible = true; y += _lblFormat.Height + S(8);
        Place(_format, S(32), S(20));
        Hide(_lblQuality, _quality, _mp4Size, _mp4Fps, _lblSound, _sound, _gifWidth, _gifFps, _lblDither, _dither, _gifNote);
        if (kind == ExportKind.Mp4)
        {
            _lblQuality.Location = new Point(pad, y); _lblQuality.Visible = true; y += _lblQuality.Height + S(8);
            Place(_quality, S(32), S(12));
            Place(_mp4Size, S(32), S(4));
            Place(_mp4Fps, S(32), S(4));
            if (_player.HasAudio) Toggle(_lblSound, _sound);
        }
        else if (kind == ExportKind.Gif)
        {
            Place(_gifWidth, S(32), S(4));
            Place(_gifFps, S(32), S(4));
            Toggle(_lblDither, _dither);
            _gifNote.Visible = true;
            _gifNote.SetBounds(pad + S(8), y + S(4), w - S(8), S(40));
        }

        // bottom block, from the bottom up
        int b = _right.ClientSize.Height - pad;
        int half = (w - S(8)) / 2;
        int bh = S(32);
        _delete.SetBounds(pad, b - bh, half, bh);
        _rename.SetBounds(pad + half + S(8), b - bh, half, bh);
        b -= bh + S(8);
        _copy.SetBounds(pad, b - bh, half, bh);
        _folder.SetBounds(pad + half + S(8), b - bh, half, bh);
        b -= bh + S(8);
        _saveAs.SetBounds(pad, b - bh, w, bh);
        b -= bh + S(8);
        _save.SetBounds(pad, b - _save.Height, w, _save.Height);
        _cancel.SetBounds(pad, b - _save.Height, w, _save.Height);
        b = _save.Top - S(10);
        _summary.SetBounds(pad, b - S(20), w, S(20));
        b = _summary.Top - S(4);
        _progress.SetBounds(pad, b - S(6), w, S(6));
        b = _progress.Top - S(6);
        _status.SetBounds(pad, b - S(40), w, S(40));
        _right.Invalidate();
    }

    ExportKind CurrentKind => _kinds.Length == 0 ? ExportKind.Mp4 : _kinds[_format.Selected];

    // ---------- playback ----------

    void OnTick()
    {
        if (_player.Playing)
        {
            _pos = _player.Position;
            _timeline.Playhead = _pos;
        }
        UpdateTransport();
    }

    static string Clock(long t)
    {
        var ts = TimeSpan.FromTicks(Math.Max(0, t));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss\.f") : ts.ToString(@"mm\:ss\.f");
    }

    void UpdateTransport()
    {
        _play.Icon = _player.Playing ? IconKind.Pause : IconKind.Play;
        _play.Invalidate();
        string text = $"{Clock(_edits.ToOutput(_pos))} / {Clock(_edits.Length)}";
        if (_time.Text != text) _time.Text = text;
        _undoBtn.Enabled = _undo.Count > 0;
    }

    void TogglePlay()
    {
        if (_player.Playing)
        {
            _player.Pause();
            _pos = _player.Position;
            _player.Seek(_pos);
        }
        else
        {
            long from = _edits.NextPlayable(_pos);
            if (from >= _edits.TrimOut - 1) from = _edits.TrimIn; // at the end: start over
            _pos = from;
            _player.Play(from, _edits.Kept);
        }
        UpdateTransport();
    }

    void SeekTo(long t)
    {
        _pos = Math.Clamp(t, 0, _edits.Duration);
        _timeline.Playhead = _pos;
        if (_player.Playing) _player.Play(_edits.NextPlayable(_pos), _edits.Kept);
        else _player.Seek(_pos);
        UpdateTransport();
    }

    void StepFrames(int n)
    {
        if (_player.Playing) TogglePlay();
        SeekTo(_pos + (long)(n * 10_000_000 / Math.Max(1, _player.Fps)));
    }

    // ---------- editing ----------

    void PushUndo()
    {
        _undo.Push(_edits.Save());
        _redo.Clear();
    }

    void EditsChanged()
    {
        _timeline.Invalidate();
        _view.Invalidate();
        UpdateSummary();
        UpdateTransport();
        if (_player.Playing) _player.Play(_edits.NextPlayable(_pos), _edits.Kept);
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_edits.Save());
        _edits.Restore(_undo.Pop());
        EditsChanged();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_edits.Save());
        _edits.Restore(_redo.Pop());
        EditsChanged();
    }

    void CutSelection()
    {
        TimeRange sel;
        if (_timeline.Selection is { Length: > 0 } s) sel = s;
        else if (_timeline.Mark is { } mk)
        {
            sel = new TimeRange(Math.Min(mk, _pos), Math.Max(mk, _pos));
            if (sel.Length < EditList.MinLength) { Status("Move the playhead to where the part ends, then press Cut again.", false); return; }
        }
        else
        {
            // nothing selected: start a two-step cut at the playhead
            _timeline.Mark = _pos;
            UpdateCutButton();
            Status($"Cut starts at {Clock(_pos)}. Move to where it ends, then press Cut again (Esc cancels). Or drag across the timeline.", false);
            return;
        }
        var before = _edits.Save();
        int hiddenBefore = _edits.HiddenBlurs;
        if (!_edits.AddCut(sel)) { Status("That would remove everything.", true); return; }
        _undo.Push(before);
        _redo.Clear();
        _timeline.Selection = null;
        _timeline.Mark = null;
        UpdateCutButton();
        SeekTo(_edits.NextPlayable(sel.Start));
        EditsChanged();
        int lost = _edits.HiddenBlurs - hiddenBefore;
        if (lost > 0)
            Status($"Removed {sel.Length / 1e7:0.0} s, and with it {(lost == 1 ? "a blur" : $"{lost} blurs")}: the blurred part is gone from the video. Ctrl+Z to undo.", true);
        else
            Status($"Removed {sel.Length / 1e7:0.0} s. Click the red part to put it back, or Ctrl+Z.", false);
    }

    void UpdateCutButton()
    {
        string text = _timeline.Mark != null && _timeline.Selection is not { Length: > 0 } ? "Cut to here" : "Cut";
        if (_cut.Text == text) return;
        _cut.Text = text;
        _cut.Width = TextRenderer.MeasureText(text, _cut.Font).Width + S(44);
        LayoutCenter();
    }

    void TrimHere(bool start)
    {
        var before = _edits.Save();
        bool ok = start ? _edits.SetTrim(_pos, _edits.TrimOut) : _edits.SetTrim(_edits.TrimIn, _pos);
        if (!ok) { Status(start ? "The start must be before the end." : "The end must be after the start.", true); return; }
        _undo.Push(before);
        _redo.Clear();
        EditsChanged();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ActiveControl is TextBox) return base.ProcessCmdKey(ref msg, keyData);
        bool busy = _export != null;
        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: StepFrames(-1); return true;
            case Keys.Right: StepFrames(1); return true;
            case Keys.Shift | Keys.Left: SeekTo(_pos - 50_000_000); return true;
            case Keys.Shift | Keys.Right: SeekTo(_pos + 50_000_000); return true;
            case Keys.Home: SeekTo(_edits.TrimIn); return true;
            case Keys.End: SeekTo(_edits.TrimOut); return true;
            case Keys.B: ToggleBlurMode(); return true;
            case Keys.I: TrimHere(start: true); return true;
            case Keys.O: TrimHere(start: false); return true;
            case Keys.Delete: case Keys.X: CutSelection(); return true;
            case Keys.Escape:
                if (_view.BlurMode) { ToggleBlurMode(); return true; }
                if (_timeline.Selection != null || _timeline.Mark != null) { _timeline.Selection = null; _timeline.Mark = null; UpdateCutButton(); Status("", false); return true; }
                break;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.S: if (!busy) Save(null); return true;
            case Keys.Control | Keys.Shift | Keys.S: if (!busy) SaveAs(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- summary / status ----------

    void UpdateSummary()
    {
        long len = _edits.Length;
        string clock = RecordingMath.Clock(TimeSpan.FromTicks(len));
        string size = CurrentKind switch
        {
            ExportKind.Mp4 => "about " + RecordingMath.FileSize((long)((RecordingMath.Bitrate(_mp4Sizes[_mp4Size.Selected], _mp4FpsValues[_mp4Fps.Selected], Quality) / 8.0
                + (_sound.Checked && _player.HasAudio ? 20000 : 0)) * len / 1e7)),
            ExportKind.Audio => "about " + RecordingMath.FileSize((long)(20000 * len / 1e7)),
            _ => "GIF, no sound",
        };
        _summary.Text = _edits.IsEmpty ? $"Whole recording {clock}  ·  {size}" : $"Length after edits {clock}  ·  {size}";
    }

    VideoQuality Quality => _quality.Selected switch { 1 => VideoQuality.Balanced, 2 => VideoQuality.Small, _ => VideoQuality.High };

    void Status(string text, bool error)
    {
        _status.ForeColor = error ? Theme.Danger : Theme.Success;
        _status.Text = text;
    }

    void UpdateTitle()
    {
        Text = $"{Path.GetFileName(_path)} – PRTSCN editor";
        _header?.Invalidate();
    }

    // ---------- save ----------

    string DefaultTarget(ExportKind kind)
    {
        var dir = Path.GetDirectoryName(_path)!;
        var name = Path.GetFileNameWithoutExtension(_path);
        var (suffix, ext) = kind switch
        {
            ExportKind.Gif => ("", ".gif"),
            ExportKind.Audio => (Path.GetExtension(_path).Equals(".m4a", StringComparison.OrdinalIgnoreCase) ? " (edited)" : "", ".m4a"),
            _ => (" (edited)", ".mp4"),
        };
        var p = Path.Combine(dir, name + suffix + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name}{suffix} {i}{ext}");
        return p;
    }

    void SaveAs()
    {
        var kind = CurrentKind;
        var target = DefaultTarget(kind);
        using var dlg = new SaveFileDialog
        {
            InitialDirectory = Path.GetDirectoryName(target),
            FileName = Path.GetFileName(target),
            Filter = kind switch { ExportKind.Gif => "GIF animation|*.gif", ExportKind.Audio => "M4A sound|*.m4a", _ => "MP4 video|*.mp4" },
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Status("Pick another name: the recording itself stays as it is.", true);
            return;
        }
        Save(dlg.FileName);
    }

    async void Save(string? target)
    {
        if (_export != null) return;
        if (!File.Exists(_path))
        {
            Status($"The recording is gone: {Path.GetFileName(_path)} was moved or deleted. Restore it (Recycle Bin) and save again.", true);
            return;
        }
        var kind = CurrentKind;
        target ??= DefaultTarget(kind);
        if (_player.Playing) TogglePlay();

        Size size = kind switch
        {
            ExportKind.Gif => GifWriter.ScaledSize(_player.SourceSize, _gifWidths[_gifWidth.Selected]),
            ExportKind.Mp4 => _mp4Sizes[_mp4Size.Selected],
            _ => Size.Empty,
        };
        int fps = kind == ExportKind.Gif ? AppSettings.GifFpsChoices[_gifFps.Selected] : _mp4FpsValues[_mp4Fps.Selected];
        var spec = new ExportSpec(_path, target, kind, _edits.Kept, size, fps, Quality, _sound.Checked, _dither.Checked, _settings.HardwareEncoder,
            _edits.Blurs.ToArray());

        // remember the choices for next time (and for "record as GIF")
        _settings.GifFps = AppSettings.GifFpsChoices[_gifFps.Selected];
        _settings.GifMaxWidth = _gifWidths[_gifWidth.Selected];
        _settings.GifDither = _dither.Checked;

        _export = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var ct = _export.Token;
        SetExporting(true);
        Status("Saving…", false);
        _status.ForeColor = Theme.TextMuted;
        var watch = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => Exporter.Run(spec, p => _progress.Value = (float)p, ct));
            long bytes = new FileInfo(target).Length;
            int hidden = kind == ExportKind.Audio ? 0 : _edits.HiddenBlurs;
            if (hidden > 0)
                Status($"Saved {Path.GetFileName(target)}  ·  {RecordingMath.FileSize(bytes)}. {(hidden == 1 ? "1 blur is" : $"{hidden} blurs are")} in a removed part, so not in the video (faded blue bar).", true);
            else
                Status($"Saved {Path.GetFileName(target)}  ·  {RecordingMath.FileSize(bytes)}", false);
            _notify("Saved", $"{Path.GetFileName(target)}  ·  {RecordingMath.FileSize(bytes)}", target, false);
            if (kind != ExportKind.Gif) _settings.AddRecent(target);
            _saveSettings();
        }
        catch (OperationCanceledException) { if (!IsDisposed) Status("Cancelled. Nothing was saved.", true); }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            if (!IsDisposed) Status("Could not save: " + ex.Message, true);
        }
        finally
        {
            _export?.Dispose();
            _export = null;
            if (!IsDisposed) SetExporting(false);
        }
    }

    void SetExporting(bool on)
    {
        _save.Visible = !on;
        _cancel.Visible = on;
        _progress.Visible = on;
        _progress.Value = 0;
        foreach (var c in new Control[] { _format, _quality, _mp4Size, _mp4Fps, _sound, _gifWidth, _gifFps, _dither, _saveAs, _rename, _delete })
            c.Enabled = !on && (c != _sound || _player.HasAudio);
    }

    // ---------- frame, file actions ----------

    /// <summary>Pixelate the blur boxes in a picture, as the saved video does.</summary>
    static void BlurBitmap(Bitmap bmp, IEnumerable<BlurBox> boxes)
    {
        var list = boxes.ToList();
        if (list.Count == 0) return;
        var bd = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[bd.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
            int block = Pixelate.BlockFor(bmp.Width);
            foreach (var b in list) Pixelate.Apply(px, bd.Stride, bmp.Width, bmp.Height, Pixelate.ToPixels(b, bmp.Width, bmp.Height), block);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
        }
        finally { bmp.UnlockBits(bd); }
    }

    void ToggleBlurMode()
    {
        if (!_player.HasVideo) return;
        _view.BlurMode = !_view.BlurMode;
        if (_view.BlurMode && _player.Playing) TogglePlay();
        _blurBtn.Text = _view.BlurMode ? "Done" : "Blur";
        _blurBtn.Width = TextRenderer.MeasureText(_blurBtn.Text, _blurBtn.Font).Width + S(44);
        LayoutCenter();
        Status(_view.BlurMode
            ? (_timeline.Selection is { Length: > 0 }
                ? "Drag a box over what to hide; it covers the selected part of the timeline. Drag a box to move it, its corners to resize, click to remove. Drag the ends of its blue bar on the timeline to change when."
                : "Drag a box over what to hide; it covers the whole video. Drag a box to move it, its corners to resize, click to remove. Drag the ends of its blue bar on the timeline to change when.")
            : "", false);
    }

    void AddBlur(RectangleF frac)
    {
        var range = _timeline.Selection is { Length: > 0 } sel ? sel : new TimeRange(_edits.TrimIn, _edits.TrimOut);
        var before = _edits.Save();
        if (!_edits.AddBlur(new BlurBox(range, frac.X, frac.Y, frac.Width, frac.Height))) { Status("Drag a bigger box.", true); return; }
        _undo.Push(before);
        _redo.Clear();
        // the selection was used for the blur's time; drop it so a later Cut doesn't remove the blurred part
        _timeline.Selection = null;
        UpdateCutButton();
        EditsChanged();
        _view.Invalidate();
        Status($"Blur added for {range.Length / 1e7:0.0} s (the blue bar on the timeline). Click it to remove it, or Ctrl+Z.", false);
    }

    void RemoveBlurAt(PointF frac)
    {
        int i = _edits.BlurAt(_pos, frac.X, frac.Y);
        if (i < 0) return;
        PushUndo();
        _edits.RemoveBlur(i);
        EditsChanged();
        _view.Invalidate();
        Status("Blur removed.", false);
    }

    async void SaveFrame()
    {
        if (!_player.HasVideo) return;
        if (_player.Playing) TogglePlay();
        long t = _pos;
        try
        {
            var bmp = await Task.Run(() => DecodeFrame(_path, t, _player.Fps));
            if (bmp == null) { Status("No frame there.", true); return; }
            using (bmp)
            {
                BlurBitmap(bmp, _edits.Blurs.Where(b => b.Range.Contains(t)));
                var path = ImageOutput.SaveToFolder(bmp, _settings);
                ImageOutput.CopyToClipboard(bmp);
                Status($"Frame saved as {Path.GetFileName(path)} and copied.", false);
                _notify("Frame saved", Path.GetFileName(path) + " (also copied)", path, false);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Status("Could not save the frame: " + ex.Message, true);
        }
    }

    /// <summary>The full-size frame showing at time t.</summary>
    static Bitmap? DecodeFrame(string path, long t, double fps)
    {
        using var r = new MediaReader(path, video: true);
        var buf = new byte[r.Width * r.Height * 4];
        var prev = new byte[buf.Length];
        bool havePrev = false;
        r.Seek(t);
        long half = (long)(5_000_000 / Math.Max(1, fps));
        while (r.ReadFrame(buf, out long ft))
        {
            if (ft > t + half) { if (havePrev) buf = prev; break; }
            if (ft >= t - half) break;
            (buf, prev) = (prev, buf);
            havePrev = true;
        }
        if (r.Ended && !havePrev) return null;
        if (r.Ended) buf = prev;
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        for (int y = 0; y < r.Height; y++) Marshal.Copy(buf, y * r.Width * 4, d.Scan0 + y * d.Stride, r.Width * 4);
        bmp.UnlockBits(d);
        return bmp;
    }

    void CopyFile()
    {
        try
        {
            Clipboard.SetFileDropList(new StringCollection { _path });
            Status("File copied: paste it in Explorer, a chat or an email.", false);
        }
        catch (Exception ex) { Status("Could not copy: " + ex.Message, true); }
    }

    static void Reveal(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    void Rename()
    {
        using var dlg = new RenameDialog(Path.GetFileNameWithoutExtension(_path), _s);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var name = dlg.FileName;
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Status("A file name can't be empty or contain \\ / : * ? \" < > |", true);
            return;
        }
        var target = Path.Combine(Path.GetDirectoryName(_path)!, name + Path.GetExtension(_path));
        if (string.Equals(target, _path, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target)) { Status("A file with that name already exists.", true); return; }
        ReleaseFile(() =>
        {
            File.Move(_path, target);
            int i = _settings.RecentRecordings.FindIndex(p => string.Equals(p, _path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _settings.RecentRecordings[i] = target;
            _path = target;
            _saveSettings();
            Status("Renamed.", false);
        });
        UpdateTitle();
    }

    void Delete()
    {
        var answer = MessageBox.Show(this, $"Move \"{Path.GetFileName(_path)}\" to the Recycle Bin?\n\nCopies you saved from it stay.",
            "Delete recording", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        var path = _path;
        _player.Dispose();
        _thumbsCts?.Cancel();
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            _settings.RecentRecordings.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _saveSettings();
            Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            CreatePlayer();
            _player.SetViewSize(_view.ClientSize);
            _player.Seek(_pos);
            Status("Could not delete: " + ex.Message, true);
        }
    }

    /// <summary>Close the file (player and readers), run an action, then open it again.</summary>
    void ReleaseFile(Action action)
    {
        _player.Dispose();
        _thumbsCts?.Cancel();
        try { action(); }
        catch (Exception ex) { CrashLog.Write(ex); Status(ex.Message, true); }
        CreatePlayer();
        _player.SetViewSize(_view.ClientSize);
        _player.Seek(_pos);
    }

    // ---------- thumbnails and sound wave ----------

    CancellationTokenSource? _thumbsCts;

    void LoadTimelineData()
    {
        _thumbsCts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var ct = _thumbsCts.Token;
        string path = _path;
        long duration = _edits.Duration;
        bool hasV = _player.HasVideo, hasA = _player.HasAudio;
        var (count, size) = _timeline.ThumbLayout(_player.SourceSize);
        double fps = _player.Fps;
        Task.Run(() =>
        {
            try
            {
                if (hasA)
                {
                    var peaks = Peaks(path, duration, 1600, ct);
                    if (!ct.IsCancellationRequested) BeginInvoke(() => _timeline.SetPeaks(peaks));
                }
                if (hasV)
                {
                    var thumbs = new List<(long, Bitmap)>();
                    using var r = new MediaReader(path, true, _ => size);
                    var buf = new byte[r.Width * r.Height * 4];
                    for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                    {
                        long t = (long)((i + 0.5) * duration / count);
                        r.Seek(t);
                        long ft = 0; int guard = 0;
                        while (r.ReadFrame(buf, out ft) && ft < t - (long)(5_000_000 / fps) && ++guard < 600) { }
                        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
                        var d = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        for (int y = 0; y < r.Height; y++) Marshal.Copy(buf, y * r.Width * 4, d.Scan0 + y * d.Stride, r.Width * 4);
                        bmp.UnlockBits(d);
                        thumbs.Add((t, bmp));
                    }
                    if (ct.IsCancellationRequested) { foreach (var th in thumbs) th.Item2.Dispose(); return; }
                    BeginInvoke(() => _timeline.SetThumbs(thumbs));
                }
            }
            catch (Exception ex) when (ex is not ObjectDisposedException) { CrashLog.Write(ex); }
            catch (ObjectDisposedException) { }
        });
    }

    static float[] Peaks(string path, long duration, int buckets, CancellationToken ct)
    {
        var peaks = new float[buckets];
        using var r = new MediaReader(path, video: false);
        int ch = Math.Max(1, r.AudioChannels);
        while (!ct.IsCancellationRequested && r.ReadAudio(out long t, out int frames) is { } pcm)
        {
            for (int i = 0; i < frames; i++)
            {
                long at = t + (long)i * 10_000_000 / r.AudioRate;
                int b = (int)Math.Clamp(at * buckets / Math.Max(1, duration), 0, buckets - 1);
                float v = 0;
                for (int c = 0; c < ch; c++) v = Math.Max(v, Math.Abs(pcm[i * ch + c] / 32768f));
                if (v > peaks[b]) peaks[b] = v;
            }
        }
        // a quiet track still shows: scale so the loudest part fills about 90 %
        float max = peaks.Max();
        if (max > 0.001f) for (int i = 0; i < peaks.Length; i++) peaks[i] = (float)Math.Sqrt(peaks[i] / max);
        return peaks;
    }

    // ---------- closing ----------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_export != null)
        {
            var answer = MessageBox.Show(this, "Stop saving and close?", "PRTSCN", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { e.Cancel = true; return; }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        OpenEditors.Remove(this);
        _closing.Cancel();
        _tick.Stop();
        _player.Dispose();
        _saveSettings();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _resizeDebounce.Dispose();
            _view.SetFrame(null);
        }
        base.Dispose(disposing);
    }

    // ---------- parts ----------

    /// <summary>File name and facts above the editor.</summary>
    sealed class Header : Control
    {
        readonly EditorForm _f;
        public Header(EditorForm f)
        {
            _f = f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = _f._s;
            using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, 16 * s, Height / 2f - 5 * s, 10 * s, 10 * s);
            using var bold = Theme.UiFont(14, true, s);
            using var small = Theme.UiFont(12, false, s);
            string name = Path.GetFileNameWithoutExtension(_f._path);
            var ns = TextRenderer.MeasureText(name, bold);
            int x = (int)(34 * s);
            TextRenderer.DrawText(g, name, bold, new Point(x, (Height - ns.Height) / 2), Theme.Text, TextFormatFlags.NoPadding);
            var p = _f._player;
            var parts = new List<string> { RecordingMath.Clock(TimeSpan.FromTicks(p.Duration)) };
            if (p.HasVideo) { parts.Add($"{p.SourceSize.Width} × {p.SourceSize.Height}"); parts.Add($"{p.Fps:0.#} fps"); }
            if (!p.HasVideo) parts.Add("sound only");
            try { parts.Add(RecordingMath.FileSize(new FileInfo(_f._path).Length)); } catch { }
            string info = string.Join("  ·  ", parts);
            var isz = TextRenderer.MeasureText(info, small);
            TextRenderer.DrawText(g, info, small, new Point(x + ns.Width + (int)(14 * s), (Height - isz.Height) / 2), Theme.TextMuted, TextFormatFlags.NoPadding);
            using var line = new Pen(Theme.Border);
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>The video picture (or a sound symbol for sound-only files).</summary>
    sealed class PlayerView : Control
    {
        readonly EditorForm _f;
        Bitmap? _frame;
        public PlayerView(EditorForm f)
        {
            _f = f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            Cursor = Cursors.Hand;
        }

        public void SetFrame(Bitmap? bmp)
        {
            var old = _frame;
            _frame = bmp;
            old?.Dispose();
            Invalidate();
        }

        bool _blurMode;
        Rectangle _pic;            // where the picture is drawn
        internal Rectangle Picture => _pic;
        Point? _dragFrom;          // drawing a new box from here
        Point _dragTo;

        // editing a box that shows now: which one, which edges (Left|Right|Top|Bottom, none = move)
        const int EdgeL = 1, EdgeR = 2, EdgeT = 4, EdgeB = 8;
        int _editBox = -1, _editEdges;
        Point _editDown;
        BlurBox _editStart;
        bool _editMoved;

        /// <summary>Drag to add a box; drag a box to move it, its corners or sides to resize it; a click removes one.</summary>
        public bool BlurMode
        {
            get => _blurMode;
            set { _blurMode = value; _dragFrom = null; _editBox = -1; Cursor = value ? Cursors.Cross : Cursors.Hand; Invalidate(); }
        }

        PointF Frac(Point p) => _pic.Width <= 0 ? PointF.Empty
            : new PointF(Math.Clamp((p.X - _pic.X) / (float)_pic.Width, 0, 1), Math.Clamp((p.Y - _pic.Y) / (float)_pic.Height, 0, 1));

        RectangleF BoxRect(BlurBox b) => new(_pic.X + b.X * _pic.Width, _pic.Y + b.Y * _pic.Height, b.W * _pic.Width, b.H * _pic.Height);

        /// <summary>The box showing now under p (newest first) and which of its edges p is on.</summary>
        int BoxHit(Point p, out int edges)
        {
            edges = 0;
            if (_pic.Width <= 0) return -1;
            float tol = 6 * _f._s;
            var blurs = _f._edits.Blurs;
            for (int i = blurs.Count - 1; i >= 0; i--)
            {
                if (!blurs[i].Range.Contains(_f._pos)) continue;
                var r = BoxRect(blurs[i]);
                if (p.X < r.X - tol || p.X > r.Right + tol || p.Y < r.Y - tol || p.Y > r.Bottom + tol) continue;
                if (Math.Abs(p.X - r.X) <= tol) edges |= EdgeL; else if (Math.Abs(p.X - r.Right) <= tol) edges |= EdgeR;
                if (Math.Abs(p.Y - r.Y) <= tol) edges |= EdgeT; else if (Math.Abs(p.Y - r.Bottom) <= tol) edges |= EdgeB;
                if (edges == 0 && !r.Contains(p)) continue;
                return i;
            }
            return -1;
        }

        static Cursor EdgeCursor(int edges) => edges switch
        {
            0 => Cursors.SizeAll,
            EdgeL | EdgeT or EdgeR | EdgeB => Cursors.SizeNWSE,
            EdgeR | EdgeT or EdgeL | EdgeB => Cursors.SizeNESW,
            EdgeL or EdgeR => Cursors.SizeWE,
            _ => Cursors.SizeNS,
        };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_blurMode || e.Button != MouseButtons.Left) return;
            Capture = true;
            int i = BoxHit(e.Location, out int edges);
            if (i >= 0)
            {
                _editBox = i; _editEdges = edges; _editDown = e.Location;
                _editStart = _f._edits.Blurs[i]; _editMoved = false;
                return;
            }
            _dragFrom = _dragTo = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_editBox >= 0) { EditBox(e.Location); return; }
            if (_dragFrom != null) { _dragTo = e.Location; Invalidate(); return; }
            if (_blurMode) Cursor = BoxHit(e.Location, out int edges) >= 0 ? EdgeCursor(edges) : Cursors.Cross;
        }

        /// <summary>Move or resize the box being edited to follow the mouse (one undo step per drag).</summary>
        void EditBox(Point p)
        {
            if (!_editMoved && Math.Abs(p.X - _editDown.X) < 4 && Math.Abs(p.Y - _editDown.Y) < 4) return;
            if (_editBox >= _f._edits.Blurs.Count || _pic.Width <= 0) return;
            float dx = (p.X - _editDown.X) / (float)_pic.Width, dy = (p.Y - _editDown.Y) / (float)_pic.Height;
            var b = _editStart;
            float x = b.X, y = b.Y, w = b.W, h = b.H;
            if (_editEdges == 0)
            {
                x = Math.Clamp(x + dx, 0, 1 - w);
                y = Math.Clamp(y + dy, 0, 1 - h);
            }
            else
            {
                if ((_editEdges & EdgeL) != 0) { float nx = Math.Clamp(x + dx, 0, 1); w += x - nx; x = nx; }
                if ((_editEdges & EdgeR) != 0) w = Math.Clamp(x + w + dx, 0, 1) - x;
                if ((_editEdges & EdgeT) != 0) { float ny = Math.Clamp(y + dy, 0, 1); h += y - ny; y = ny; }
                if ((_editEdges & EdgeB) != 0) h = Math.Clamp(y + h + dy, 0, 1) - y;
            }
            var nb = b with { X = x, Y = y, W = w, H = h };
            if (!_editMoved) { _f.PushUndo(); _editMoved = true; }
            if (_f._edits.SetBlur(_editBox, nb)) { _f.EditsChanged(); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            if (_editBox >= 0)
            {
                bool moved = _editMoved;
                _editBox = -1;
                Invalidate();
                if (!moved) _f.RemoveBlurAt(Frac(e.Location));
                else _f.Status("Blur box changed. Ctrl+Z undoes it.", false);
                return;
            }
            if (_dragFrom is not Point from) return;
            _dragFrom = null;
            Invalidate();
            if (Math.Abs(e.X - from.X) < 6 && Math.Abs(e.Y - from.Y) < 6) return;
            PointF a = Frac(from), b = Frac(e.Location);
            _f.AddBlur(RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }

        /// <summary>Dev check: a point on box i (edges as above, 0 = middle) and a mouse drag.</summary>
        internal Point BoxPoint(int i, int edges)
        {
            var r = BoxRect(_f._edits.Blurs[i]);
            float x = (edges & EdgeL) != 0 ? r.X : (edges & EdgeR) != 0 ? r.Right : r.X + r.Width / 2;
            float y = (edges & EdgeT) != 0 ? r.Y : (edges & EdgeB) != 0 ? r.Bottom : r.Y + r.Height / 2;
            return new Point((int)x, (int)y);
        }
        internal void SimulateDrag(Point from, Point to)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, from.X, from.Y, 0));
            for (int k = 1; k <= 8; k++)
                OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, from.X + (to.X - from.X) * k / 8, from.Y + (to.Y - from.Y) * k / 8, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = _f._s;
            Theme.FillRound(g, Color.Black, new RectangleF(0, 0, Width, Height), 6 * s);
            if (_frame != null)
            {
                var fit = Player.Fit(_frame.Size, ClientSize);
                if (fit.Width >= _frame.Width - 1) fit = _frame.Size;
                var r = new Rectangle((Width - fit.Width) / 2, (Height - fit.Height) / 2, fit.Width, fit.Height);
                _pic = r;
                if (r.Size == _frame.Size) g.DrawImageUnscaled(_frame, r.Location);
                else { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(_frame, r); }
                DrawBlurs(g, r);
            }
            else if (!_f._player.HasVideo)
            {
                float size = 64 * s;
                Icons.Draw(g, IconKind.Speaker, new RectangleF((Width - size) / 2, (Height - size) / 2 - 12 * s, size, size), Theme.TextMuted, s * 2, Theme.TextMuted);
                using var f = Theme.UiFont(14, false, s);
                TextRenderer.DrawText(g, "Sound only", f, new Rectangle(0, (int)((Height + size) / 2), Width, (int)(30 * s)), Theme.TextMuted, TextFormatFlags.HorizontalCenter);
            }
        }

        /// <summary>Blur boxes showing now, pixelated like the saved video; outlined while editing.</summary>
        void DrawBlurs(Graphics g, Rectangle pic)
        {
            long t = _f._pos;
            var src = _frame!;
            int block = Math.Max(1, Pixelate.BlockFor(_f._player.SourceSize.Width) * src.Width / Math.Max(1, _f._player.SourceSize.Width));
            // a removed or trimmed-off moment won't be in the video, so neither is its blur
            var ed = _f._edits;
            bool removed = ed.CutAt(t) >= 0 || t < ed.TrimIn || t >= ed.TrimOut;
            foreach (var b in _f._edits.Blurs)
            {
                bool now = b.Range.Contains(t) && !removed;
                if (!now && !_blurMode) continue;
                var dest = Rectangle.FromLTRB(pic.X + (int)(b.X * pic.Width), pic.Y + (int)(b.Y * pic.Height),
                    pic.X + (int)Math.Ceiling((b.X + b.W) * pic.Width), pic.Y + (int)Math.Ceiling((b.Y + b.H) * pic.Height));
                if (now)
                {
                    var sr = Pixelate.ToPixels(b, src.Width, src.Height);
                    sr.Intersect(new Rectangle(Point.Empty, src.Size));
                    if (sr.Width > 0 && sr.Height > 0)
                    {
                        using var tiny = new Bitmap(Math.Max(1, sr.Width / block), Math.Max(1, sr.Height / block));
                        using (var tg = Graphics.FromImage(tiny))
                        {
                            tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            tg.DrawImage(src, new Rectangle(Point.Empty, tiny.Size), sr, GraphicsUnit.Pixel);
                        }
                        var im = g.InterpolationMode; var pm = g.PixelOffsetMode;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(tiny, dest);
                        g.InterpolationMode = im; g.PixelOffsetMode = pm;
                    }
                }
                if (_blurMode)
                {
                    using var pen = new Pen(now ? Theme.Accent : Theme.WithAlpha(Theme.Accent, 90), 1.5f * _f._s) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(pen, dest);
                    if (now)
                    {
                        // corner grips: drag them to resize, drag inside to move
                        float hs = 7 * _f._s;
                        using var fill = new SolidBrush(Color.White);
                        using var edge = new Pen(Theme.Accent, 1.5f * _f._s);
                        foreach (var c in new PointF[] { new(dest.Left, dest.Top), new(dest.Right, dest.Top), new(dest.Left, dest.Bottom), new(dest.Right, dest.Bottom) })
                        {
                            var hr = new RectangleF(c.X - hs / 2, c.Y - hs / 2, hs, hs);
                            g.FillRectangle(fill, hr);
                            g.DrawRectangle(edge, hr.X, hr.Y, hr.Width, hr.Height);
                        }
                    }
                }
            }
            if (_dragFrom is Point a)
            {
                var r = Rectangle.FromLTRB(Math.Min(a.X, _dragTo.X), Math.Min(a.Y, _dragTo.Y), Math.Max(a.X, _dragTo.X), Math.Max(a.Y, _dragTo.Y));
                r.Intersect(pic);
                using var fill = new SolidBrush(Theme.WithAlpha(Theme.Accent, 40));
                g.FillRectangle(fill, r);
                using var pen = new Pen(Theme.Accent, 1.5f * _f._s);
                g.DrawRectangle(pen, r);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _frame?.Dispose(); _frame = null; }
            base.Dispose(disposing);
        }
    }
}

/// <summary>A thin progress bar in the accent colour. Value can be set from any thread.</summary>
internal sealed class ProgressLine : Control
{
    readonly float _s;
    float _value;

    public ProgressLine(float scale)
    {
        _s = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Bg;
    }

    public float Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            if (!IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(Invalidate); else Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        Theme.FillRound(g, Theme.SurfaceActive, new RectangleF(0, 0, Width, Height), Height / 2f);
        if (_value > 0) Theme.FillRound(g, Theme.Accent, new RectangleF(0, 0, Math.Max(Height, Width * _value), Height), Height / 2f);
    }
}
