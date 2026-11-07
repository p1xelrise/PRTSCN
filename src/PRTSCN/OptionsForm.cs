using System.Drawing.Drawing2D;
using PRTSCN.Core;
using CoreFormat = PRTSCN.Core.ImageFormat;

namespace PRTSCN;

/// <summary>A text box that records a key combination. Backspace or Delete clears it.</summary>
internal sealed class HotkeyBox : TextBox
{
    Keys _value;
    public Keys Value { get => _value; set { _value = value; Text = value == Keys.None ? "None" : HotkeyManager.Describe(value); } }

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        Width = 190;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) != Keys.Tab || keyData.HasFlag(Keys.Control);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var code = e.KeyCode;
        if (code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        if ((code is Keys.Back or Keys.Delete) && e.Modifiers == Keys.None) { Value = Keys.None; return; }
        if (code == Keys.Escape && e.Modifiers == Keys.None) return;
        Value = code | e.Modifiers;
    }

    // Windows sends Print Screen as key-up only.
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.PrintScreen) { Value = Keys.PrintScreen | e.Modifiers; e.Handled = true; }
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); BackColor = Theme.SurfaceActive; }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); BackColor = Theme.Surface; }
}

/// <summary>
/// PRTSCN Options: a dark window with a sidebar (General, Screenshots, Recording, Hotkeys).
/// Edits a copy of the settings; Save hands back <see cref="Result"/>.
/// </summary>
internal sealed class OptionsForm : Form
{
    static readonly string[] Sections = { "General", "Screenshots", "Recording", "Hotkeys" };
    static readonly VideoQuality[] Qualities = { VideoQuality.Original, VideoQuality.High, VideoQuality.Balanced, VideoQuality.Small };
    static int _lastSection;

    readonly AppSettings _orig;
    readonly Panel[] _pages = new Panel[Sections.Length];
    readonly NavList _nav;
    readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };

    // general
    readonly DarkToggle _autostart = new(), _notify = new();
    // screenshots
    readonly DarkSegmented _format = new("PNG", "JPG");
    readonly NumericUpDown _quality = new() { Minimum = 10, Maximum = 100, Increment = 5, Width = 70 };
    readonly FolderBox _folder = new();
    readonly DarkToggle _copyInstant = new(), _windowCopies = new();
    readonly NumericUpDown _scrollDelay = new() { Minimum = 100, Maximum = 3000, Increment = 50, Width = 70 };
    // recording
    readonly FolderBox _videoFolder = new();
    readonly DarkSegmented _fps = new("15", "30", "60");
    readonly DarkSegmented _videoQuality = new("Original", "High", "Balanced", "Small");
    readonly Label _sizeEstimate = Muted("");
    readonly DarkToggle _hardware = new(), _showCursor = new(), _showFrame = new();
    readonly DarkSegmented _countdown = new("Off", "3 s", "5 s", "10 s");
    readonly NumericUpDown _maxMinutes = new() { Minimum = 0, Maximum = 600, Width = 70 };
    readonly NumericUpDown _maxMb = new() { Minimum = 0, Maximum = 100000, Increment = 100, Width = 80 };
    // sound
    readonly DarkToggle _systemAudio = new(), _mic = new();
    readonly DarkSlider _systemVolume = new(), _micVolume = new();
    readonly FolderBox _micDevice = new() { Chevron = true, Width = 260 };
    string _micDeviceId = "";
    // mouse and keys
    readonly DarkToggle _clicks = new(), _spotlight = new(), _magnifier = new();
    readonly DarkSegmented _keys = new("Off", "Shortcuts", "All keys");
    readonly DarkSegmented _zoom = new("1.5×", "2×", "3×");
    // format, GIF, after recording
    readonly DarkSegmented _recFormat = new("MP4", "GIF");
    readonly DarkSegmented _gifFps = new("10", "15", "20", "25");
    readonly DarkSegmented _gifWidth = new("Full", "1280", "800", "640", "480");
    static readonly int[] OptionGifWidths = { 0, 1280, 800, 640, 480 };
    int _gifWidthOrig;
    bool _gifWidthTouched;
    readonly DarkToggle _gifDither = new(), _openEditor = new();
    // advanced: auto zoom, watermark, instant replay
    readonly DarkToggle _autoZoom = new(), _replayOn = new();
    readonly DarkSegmented _autoZoomLevel = new("1.5×", "2×", "3×");
    readonly TextBox _wmText = new() { Width = 200, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, MaxLength = 80 };
    readonly FolderBox _wmImage = new() { Chevron = true, Width = 200 };
    string _wmImagePath = "";
    readonly DarkSegmented _wmCorner = new("Top left", "Top right", "Bottom left", "Bottom right");
    readonly DarkSlider _wmOpacity = new() { Minimum = 10, Maximum = 100, Step = 10 };
    readonly DarkSegmented _replayLength = new("30 s", "1 min", "2 min", "5 min");
    // hotkeys
    readonly HotkeyBox _capture = new(), _window = new(), _instant = new(), _scroll = new(), _record = new(), _pause = new(), _replayKey = new();

    readonly Label _error = new() { AutoSize = true, ForeColor = Theme.Danger, Anchor = AnchorStyles.Left };

    public AppSettings Result { get; private set; }

    public OptionsForm(AppSettings s)
    {
        _orig = s;
        Result = s;
        Text = "PRTSCN Options";
        Icon = Theme.AppIcon(32);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 560);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        TopMost = true;
        KeyPreview = true;

        foreach (var n in new[] { _quality, _scrollDelay, _maxMinutes, _maxMb }) DarkUi.Style(n);

        _pages[0] = BuildGeneral(s);
        _pages[1] = BuildScreenshots(s);
        _pages[2] = BuildRecording(s);
        _pages[3] = BuildHotkeys(s);
        foreach (var p in _pages) { p.Visible = false; _host.Controls.Add(p); }

        _nav = new NavList(Sections) { Dock = DockStyle.Left, Width = 196 };
        _nav.SelectedChanged += i => ShowSection(i);

        Controls.Add(_host);
        Controls.Add(BuildFooter());
        Controls.Add(_nav);
        ShowSection(_lastSection);
    }

    /// <summary>Developer check: render every section to PNG files (PRTSCN.exe --ui-shots &lt;folder&gt;).</summary>
    internal static void RenderShots(string folder)
    {
        Directory.CreateDirectory(folder);
        using var f = new OptionsForm(new AppSettings().Normalize()) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0), ShowInTaskbar = false };
        f.Show();
        f.ClientSize = new Size(f.ClientSize.Width, 1500); // tall, so whole pages show without scrolling
        for (int i = 0; i < Sections.Length; i++)
        {
            f.ShowSection(i);
            Application.DoEvents();
            using var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
            bmp.Save(Path.Combine(folder, $"options-{i + 1}-{Sections[i].ToLowerInvariant()}.png"));
            if (Environment.GetEnvironmentVariable("PRTSCN_SCREEN_SHOTS") == "1")
            {
                // what the screen really shows (DrawToBitmap hides overlap problems): normal size, on screen
                var tall = f.ClientSize;
                f.ClientSize = new Size(tall.Width, 560);
                f.Location = new Point(40, 40);
                f.TopMost = true;
                for (int k = 0; k < 10; k++) { Application.DoEvents(); Thread.Sleep(30); }
                var b = f.RectangleToScreen(f.ClientRectangle);
                using (var scr = new Bitmap(b.Width, b.Height))
                {
                    using (var g = Graphics.FromImage(scr)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                    scr.Save(Path.Combine(folder, $"options-{i + 1}-{Sections[i].ToLowerInvariant()}-screen.png"));
                }
                f.Location = new Point(-4000, 0);
                f.ClientSize = tall;
                Application.DoEvents();
            }
            if (f._pages[i] is ScrollableControl sc && sc.VerticalScroll.Visible)
            {
                sc.AutoScrollPosition = new Point(0, 100_000); // and the end of long pages
                Application.DoEvents();
                using var end = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                f.DrawToBitmap(end, new Rectangle(Point.Empty, f.ClientSize));
                end.Save(Path.Combine(folder, $"options-{i + 1}-{Sections[i].ToLowerInvariant()}-end.png"));
            }
        }
        f.Close();
    }

    void ShowSection(int i)
    {
        _lastSection = i;
        _nav.Selected = i;
        for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = k == i;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SizeDialog.DarkTitleBar(Handle);
    }

    // ---------- pages ----------

    Panel BuildGeneral(AppSettings s)
    {
        var (page, add) = Page("General", "How PRTSCN starts and talks to you.");
        _autostart.Checked = s.StartWithWindows;
        _notify.Checked = s.ShowNotifications;
        add.Row("Start with Windows", "PRTSCN waits in the tray, ready for Print Screen.", _autostart);
        add.Row("Show notifications", "A note when a screenshot or recording is saved. Errors always show.", _notify);
        var v = typeof(OptionsForm).Assembly.GetName().Version;
        add.Note($"PRTSCN {v?.ToString(3)}   ·   settings: {SettingsStore.DefaultPath()}");
        return page;
    }

    Panel BuildScreenshots(AppSettings s)
    {
        var (page, add) = Page("Screenshots", "Saving and copying images.");
        _format.Selected = s.Format == CoreFormat.Jpg ? 1 : 0;
        _quality.Value = Math.Clamp(s.JpegQuality, 10, 100);
        _quality.Enabled = _format.Selected == 1;
        _format.SelectedChanged += (_, _) => _quality.Enabled = _format.Selected == 1;
        add.Row("Image format", "PNG is pixel-perfect. JPG files are smaller.", _format);
        add.Row("JPG quality", "10 to 100. Higher looks better and makes bigger files.", _quality);
        _folder.Text = s.SaveFolder;
        add.Row("Save folder", null, FolderPicker(_folder, "Where should PRTSCN save screenshots?"));
        _copyInstant.Checked = s.CopyAfterInstantCapture;
        add.Row("Copy full-screen captures", "Full-screen captures are saved and also put on the clipboard.", _copyInstant);
        _windowCopies.Checked = s.WindowClickCopies;
        add.Row("Clicking a window copies it", "Skip the editing tools when you pick a window.", _windowCopies);
        _scrollDelay.Value = Math.Clamp(s.ScrollDelayMs, 100, 3000);
        add.Row("Scroll step wait (ms)", "Raise it if long captures come out with repeated or missing parts.", _scrollDelay);
        return page;
    }

    Panel BuildRecording(AppSettings s)
    {
        var (page, add) = Page("Recording", "Screen recordings are saved as MP4 (H.264 with AAC sound) or GIF. Sound only saves as M4A.");
        _videoFolder.Text = s.VideoFolder;
        add.Row("Save folder", null, FolderPicker(_videoFolder, "Where should PRTSCN save recordings?"));

        add.Header("Video");
        _recFormat.Selected = s.RecordFormat == RecordFormat.Gif ? 1 : 0;
        add.Row("Save recordings as", "GIF has no sound. PRTSCN records an MP4 first and turns it into a GIF when you stop.", _recFormat);
        _fps.Selected = Math.Max(0, Array.IndexOf(AppSettings.FpsChoices, s.VideoFps));
        add.Row("Frame rate", "Frames per second. 60 is smoothest; 30 suits most screen work.", _fps);
        _videoQuality.Selected = Math.Max(0, Array.IndexOf(Qualities, s.VideoQuality));
        var q = add.Row("Quality", "", _videoQuality);
        _sizeEstimate.MaximumSize = new Size(q.MinimumSize.Width, 0);
        q.Controls.Add(_sizeEstimate);
        _fps.SelectedChanged += (_, _) => UpdateEstimate();
        _videoQuality.SelectedChanged += (_, _) => UpdateEstimate();
        UpdateEstimate();
        _hardware.Checked = s.HardwareEncoder;
        add.Row("Hardware encoding", "Use the graphics card's video encoder. Turn off if videos come out broken.", _hardware);

        add.Header("GIF");
        _gifFps.Selected = Math.Max(0, Array.IndexOf(AppSettings.GifFpsChoices, s.GifFps));
        add.Row("GIF frame rate", "15 looks smooth for screen work and keeps files small.", _gifFps);
        _gifWidthOrig = s.GifMaxWidth;
        int gw = Array.IndexOf(OptionGifWidths, s.GifMaxWidth);
        _gifWidth.Selected = gw >= 0 ? gw : 2;
        _gifWidth.SelectedChanged += (_, _) => _gifWidthTouched = true;
        add.Row("Widest GIF (px)", "Wider recordings are scaled down. Never up.", _gifWidth);
        _gifDither.Checked = s.GifDither;
        add.Row("Smooth gradients (dither)", "Better photos and gradients, but bigger files and grainy flat areas.", _gifDither);

        add.Header("After recording");
        _openEditor.Checked = s.OpenEditorAfterRecording;
        add.Row("Open the editor", "Preview, trim and cut a video as soon as it is saved. Recent recordings are also in the tray menu.", _openEditor);

        add.Header("Sound");
        _systemAudio.Checked = s.RecordSystemAudio;
        add.Row("Computer sound", "What plays through your speakers or headphones.", _systemAudio);
        _systemVolume.Value = s.SystemVolume;
        add.Row("Computer sound volume", "Double-click resets to 100%.", _systemVolume);
        _mic.Checked = s.RecordMic;
        add.Row("Microphone", "Your voice. Mute it any time from the recording bar.", _mic);
        _micDeviceId = s.MicDeviceId;
        _micDevice.Text = MicName(_micDeviceId);
        _micDevice.Browse += (_, _) => PickMic();
        add.Row("Microphone device", null, _micDevice);
        _micVolume.Value = s.MicVolume;
        add.Row("Microphone volume", "Raise it if your voice is too quiet next to the computer sound.", _micVolume);
        void SyncSound()
        {
            _systemVolume.Enabled = _systemAudio.Checked;
            _micVolume.Enabled = _micDevice.Enabled = _mic.Checked;
        }
        _systemAudio.CheckedChanged += (_, _) => SyncSound();
        _mic.CheckedChanged += (_, _) => SyncSound();
        SyncSound();

        add.Header("Mouse and keys");
        _clicks.Checked = s.RecordClicks;
        add.Row("Highlight clicks", "A ripple where you click: amber for left, blue for right.", _clicks);
        _keys.Selected = (int)s.RecordKeys;
        add.Row("Show pressed keys", "Shortcuts shows Ctrl/Alt/Win combinations and F-keys. All keys also shows typing, passwords included.", _keys);
        _spotlight.Checked = s.RecordSpotlight;
        add.Row("Spotlight", "Dims everything except a circle around the pointer.", _spotlight);
        _magnifier.Checked = s.RecordMagnifier;
        add.Row("Magnifier", "A round lens that zooms in around the pointer.", _magnifier);
        _zoom.Selected = Math.Max(0, Array.IndexOf(AppSettings.ZoomChoices, s.MagnifierZoom));
        add.Row("Magnifier zoom", null, _zoom);
        _magnifier.CheckedChanged += (_, _) => _zoom.Enabled = _magnifier.Checked;
        _zoom.Enabled = _magnifier.Checked;
        _autoZoom.Checked = s.RecordAutoZoom;
        add.Row("Zoom in on clicks", "The video zooms in where you click and follows the pointer, then zooms out after a quiet moment.", _autoZoom);
        _autoZoomLevel.Selected = Math.Max(0, Array.IndexOf(AppSettings.ZoomChoices, s.AutoZoomLevel));
        add.Row("Click zoom", null, _autoZoomLevel);
        _autoZoom.CheckedChanged += (_, _) => _autoZoomLevel.Enabled = _autoZoom.Checked;
        _autoZoomLevel.Enabled = _autoZoom.Checked;

        add.Header("Watermark");
        _wmText.Text = s.WatermarkText;
        add.Row("Text", "Shown in a corner of every video. Empty = none.", _wmText);
        _wmImagePath = s.WatermarkImage;
        _wmImage.Text = ImageName(_wmImagePath);
        _wmImage.Browse += (_, _) => PickWatermarkImage();
        add.Row("Image", "A logo, above the text. PNG works best.", _wmImage);
        _wmCorner.Selected = (int)s.WatermarkCorner;
        add.Row("Corner", null, _wmCorner);
        _wmOpacity.Value = s.WatermarkOpacity;
        add.Row("Opacity (%)", null, _wmOpacity);

        add.Header("Instant replay");
        _replayOn.Checked = s.ReplayEnabled;
        add.Row("Keep the last moments", "Records the screen with the mouse in the background. The hotkey (Hotkeys page) saves what just happened. Uses the graphics card all the time.", _replayOn);
        _replayLength.Selected = Math.Max(0, Array.IndexOf(AppSettings.ReplayChoices, s.ReplaySeconds));
        add.Row("How much to keep", null, _replayLength);

        add.Header("While recording");
        _countdown.Selected = Math.Max(0, Array.IndexOf(AppSettings.CountdownChoices, s.RecordCountdown));
        add.Row("Countdown", "Seconds before recording starts. Esc cancels it.", _countdown);
        _showCursor.Checked = s.RecordShowCursor;
        add.Row("Record the mouse pointer", null, _showCursor);
        _showFrame.Checked = s.RecordShowFrame;
        add.Row("Show a frame around the area", "A dashed line shows what is recorded. It never appears in the video.", _showFrame);

        add.Header("Stop automatically");
        _maxMinutes.Value = Math.Clamp(s.RecordMaxMinutes, 0, 600);
        add.Row("After minutes", "0 = no limit.", _maxMinutes);
        _maxMb.Value = Math.Clamp(s.RecordMaxMegabytes, 0, 100000);
        add.Row("When the file reaches MB", "0 = no limit.", _maxMb);
        return page;
    }

    static string ImageName(string path) => string.IsNullOrWhiteSpace(path) ? "No image" : Path.GetFileName(path);

    void PickWatermarkImage()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Choose an image…", null, (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "Watermark image", Filter = "Images (*.png; *.jpg; *.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _wmImagePath = dlg.FileName;
            _wmImage.Text = ImageName(_wmImagePath);
        });
        menu.Items.Add("No image", null, (_, _) => { _wmImagePath = ""; _wmImage.Text = ImageName(""); });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_wmImage, new Point(0, _wmImage.Height + 2));
    }

    static string MicName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "Windows default microphone";
        var d = Recording.AudioSource.Microphones().FirstOrDefault(m => m.Id == id);
        return d?.Name ?? "Windows default (the chosen one is unplugged)";
    }

    void PickMic()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        void Add(string id, string name)
        {
            var item = new ToolStripMenuItem(name) { Checked = id == _micDeviceId };
            item.Click += (_, _) => { _micDeviceId = id; _micDevice.Text = MicName(id); };
            menu.Items.Add(item);
        }
        Add("", "Windows default microphone");
        var mics = Recording.AudioSource.Microphones();
        if (mics.Count > 0) menu.Items.Add(new ToolStripSeparator());
        foreach (var m in mics) Add(m.Id, m.Name);
        if (mics.Count == 0) menu.Items.Add(new ToolStripMenuItem("No microphone found") { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_micDevice, new Point(0, _micDevice.Height + 2));
    }

    void UpdateEstimate()
    {
        var screen = Screen.PrimaryScreen?.Bounds.Size ?? new Size(1920, 1080);
        var frame = RecordingMath.FitWithin(screen);
        int fps = AppSettings.FpsChoices[_fps.Selected];
        double mb = RecordingMath.MegabytesPerMinute(frame, fps, Qualities[_videoQuality.Selected]);
        _sizeEstimate.Text = $"About {mb:0} MB a minute for the whole screen ({frame.Width} × {frame.Height}, {fps} fps).";
    }

    Panel BuildHotkeys(AppSettings s)
    {
        var (page, add) = Page("Hotkeys", "Click a box and press the keys. Backspace clears it.");
        _capture.Value = (Keys)s.CaptureHotkey;
        _window.Value = (Keys)s.WindowHotkey;
        _instant.Value = (Keys)s.InstantHotkey;
        _scroll.Value = (Keys)s.ScrollHotkey;
        _record.Value = (Keys)s.RecordHotkey;
        _pause.Value = (Keys)s.PauseHotkey;
        add.Header("Screenshots");
        add.Row("Capture an area", null, _capture);
        add.Row("Capture a window", null, _window);
        add.Row("Capture full screen", "Saves straight to the folder.", _instant);
        add.Row("Scrolling capture", null, _scroll);
        add.Header("Recording");
        add.Row("Record / stop", "Opens the record screen. While recording, stops and saves.", _record);
        add.Row("Pause / resume", "Works only while a recording runs.", _pause);
        _replayKey.Value = (Keys)s.ReplayHotkey;
        add.Row("Save the instant replay", "Works while instant replay is on (tray menu or Recording page).", _replayKey);
        if (WindowsSnipping.PrintScreenOpensSnipping())
        {
            var warn = new LinkLabel
            {
                Text = "Windows is sending Print Screen to the Snipping Tool. Turn that off in Windows settings.",
                AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = Theme.Danger,
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, Margin = new Padding(0, 14, 0, 0),
            };
            warn.LinkArea = new LinkArea(warn.Text.IndexOf("Turn that off"), "Turn that off in Windows settings".Length);
            warn.LinkClicked += (_, _) => WindowsSnipping.OpenSetting();
            add.Full(warn);
        }
        return page;
    }

    Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 60, ColumnCount = 3, BackColor = Theme.Bg, Padding = new Padding(24, 12, 20, 12) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var cancel = SizeDialog.DarkButton("Cancel", primary: false);
        var ok = SizeDialog.DarkButton("Save", primary: true);
        cancel.Click += (_, _) => Close(); // modeless form: DialogResult alone does not close it
        ok.Click += (_, _) => OnOk();
        footer.Controls.Add(_error, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(ok, 2, 0);
        AcceptButton = ok;
        CancelButton = cancel;
        return footer;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Tab / Ctrl+Shift+Tab switch sections (except while a hotkey box is recording).
        if (ActiveControl is not HotkeyBox && (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab)))
        {
            int d = keyData.HasFlag(Keys.Shift) ? -1 : 1;
            ShowSection((_nav.Selected + d + Sections.Length) % Sections.Length);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    Control FolderPicker(FolderBox box, string description)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Bg };
        var browse = SizeDialog.DarkButton("Browse…", primary: false);
        browse.MinimumSize = new Size(84, 28);
        browse.Margin = new Padding(6, 0, 0, 0);
        box.Margin = Padding.Empty;
        void Pick()
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = box.Text, UseDescriptionForTitle = true, Description = description };
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        }
        browse.Click += (_, _) => Pick();
        box.Browse += (_, _) => Pick();
        row.Controls.Add(box);
        row.Controls.Add(browse);
        return row;
    }

    // ---------- page layout ----------

    static Label Muted(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.TextMuted, MaximumSize = new Size(300, 0), Margin = new Padding(0, 2, 0, 0) };

    sealed class PageBuilder
    {
        readonly TableLayoutPanel _grid;
        public PageBuilder(TableLayoutPanel grid) => _grid = grid;

        public void Header(string text)
        {
            var l = new Label { Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Accent, Font = new Font("Segoe UI", 8.25f, FontStyle.Bold), Margin = new Padding(0, 18, 0, 4) };
            Full(l);
        }

        /// <summary>A setting: title and description on the left, the control on the right. Returns the text column.</summary>
        public FlowLayoutPanel Row(string title, string? description, Control control)
        {
            // Without a description the title alone sets the width, so a wide control (a folder box)
            // never ends up under the text column, which would paint the background over it.
            int minW = string.IsNullOrEmpty(description) ? 0 : control is DarkSegmented { Width: > 220 } ? 230 : 300;
            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 16, 10), BackColor = Color.Transparent,
                MinimumSize = new Size(minW, 0) };
            text.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Theme.Text, Margin = Padding.Empty });
            if (!string.IsNullOrEmpty(description))
            {
                var d = Muted(description);
                d.MaximumSize = new Size(text.MinimumSize.Width, 0);
                text.Controls.Add(d);
            }
            control.Anchor = AnchorStyles.Right;
            control.Margin = new Padding(0, 8, 0, 8);
            int row = _grid.RowCount++;
            _grid.Controls.Add(text, 0, row);
            _grid.Controls.Add(control, 1, row);
            control.BringToFront(); // if the columns still overlap, the control stays visible
            return text;
        }

        public void Full(Control c)
        {
            int row = _grid.RowCount++;
            _grid.Controls.Add(c, 0, row);
            _grid.SetColumnSpan(c, 2);
        }

        public void Note(string text)
        {
            var l = Muted(text);
            l.MaximumSize = new Size(520, 0);
            l.Margin = new Padding(0, 22, 0, 0);
            Full(l);
        }
    }

    static (Panel page, PageBuilder add) Page(string title, string subtitle)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(28, 22, 24, 16) };
        DarkUi.DarkScrollbars(page);
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Theme.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.CellPaint += (_, e) =>
        {
            // a hairline under each setting row
            if (e.Column != 0 || e.Row < 2) return;
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, e.CellBounds.Left, e.CellBounds.Top, grid.Width, e.CellBounds.Top);
        };
        var b = new PageBuilder(grid);
        b.Full(new Label { Text = title, AutoSize = true, ForeColor = Theme.Text, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 2) });
        var sub = Muted(subtitle);
        sub.MaximumSize = new Size(520, 0);
        sub.Margin = new Padding(0, 0, 0, 8);
        b.Full(sub);
        page.Controls.Add(grid);
        return (page, b);
    }

    // ---------- save ----------

    void OnOk()
    {
        _error.Text = "";
        var keys = new[] { _capture.Value, _window.Value, _instant.Value, _scroll.Value, _record.Value, _pause.Value, _replayKey.Value }.Where(k => k != Keys.None).ToList();
        if (keys.Count != keys.Distinct().Count()) { ShowSection(3); _error.Text = "Each hotkey must be different."; return; }
        if (keys.Contains((Keys)AppSettings.HighContrastCombo)) { ShowSection(3); _error.Text = "Shift+Alt+Print Screen belongs to Windows (High Contrast). Pick another."; return; }

        if (!TryFolder(_folder.Text, out var folder)) { ShowSection(1); _error.Text = "That screenshot folder can't be used. Pick another one."; return; }
        if (!TryFolder(_videoFolder.Text, out var videoFolder)) { ShowSection(2); _error.Text = "That recordings folder can't be used. Pick another one."; return; }

        var r = _orig.Clone();
        r.StartWithWindows = _autostart.Checked;
        r.ShowNotifications = _notify.Checked;
        r.Format = _format.Selected == 1 ? CoreFormat.Jpg : CoreFormat.Png;
        r.JpegQuality = (int)_quality.Value;
        r.SaveFolder = folder;
        r.CopyAfterInstantCapture = _copyInstant.Checked;
        r.WindowClickCopies = _windowCopies.Checked;
        r.ScrollDelayMs = (int)_scrollDelay.Value;
        r.VideoFolder = videoFolder;
        r.VideoFps = AppSettings.FpsChoices[_fps.Selected];
        r.VideoQuality = Qualities[_videoQuality.Selected];
        r.HardwareEncoder = _hardware.Checked;
        r.RecordFormat = _recFormat.Selected == 1 ? RecordFormat.Gif : RecordFormat.Mp4;
        r.GifFps = AppSettings.GifFpsChoices[_gifFps.Selected];
        r.GifMaxWidth = _gifWidthTouched || Array.IndexOf(OptionGifWidths, _gifWidthOrig) >= 0 ? OptionGifWidths[_gifWidth.Selected] : _gifWidthOrig;
        r.GifDither = _gifDither.Checked;
        r.OpenEditorAfterRecording = _openEditor.Checked;
        r.RecordCountdown = AppSettings.CountdownChoices[_countdown.Selected];
        r.RecordShowCursor = _showCursor.Checked;
        r.RecordShowFrame = _showFrame.Checked;
        r.RecordSystemAudio = _systemAudio.Checked;
        r.RecordMic = _mic.Checked;
        r.MicDeviceId = _micDeviceId;
        r.SystemVolume = _systemVolume.Value;
        r.MicVolume = _micVolume.Value;
        r.RecordClicks = _clicks.Checked;
        r.RecordKeys = (KeyDisplay)_keys.Selected;
        r.RecordSpotlight = _spotlight.Checked;
        r.RecordMagnifier = _magnifier.Checked;
        r.MagnifierZoom = AppSettings.ZoomChoices[_zoom.Selected];
        r.RecordMaxMinutes = (int)_maxMinutes.Value;
        r.RecordMaxMegabytes = (int)_maxMb.Value;
        r.CaptureHotkey = (int)_capture.Value;
        r.WindowHotkey = (int)_window.Value;
        r.InstantHotkey = (int)_instant.Value;
        r.ScrollHotkey = (int)_scroll.Value;
        r.RecordHotkey = (int)_record.Value;
        r.PauseHotkey = (int)_pause.Value;
        r.ReplayHotkey = (int)_replayKey.Value;
        r.RecordAutoZoom = _autoZoom.Checked;
        r.AutoZoomLevel = AppSettings.ZoomChoices[_autoZoomLevel.Selected];
        r.WatermarkText = _wmText.Text;
        r.WatermarkImage = _wmImagePath;
        r.WatermarkCorner = (Corner)_wmCorner.Selected;
        r.WatermarkOpacity = _wmOpacity.Value;
        r.ReplayEnabled = _replayOn.Checked;
        r.ReplaySeconds = AppSettings.ReplayChoices[_replayLength.Selected];
        Result = r.Normalize();
        DialogResult = DialogResult.OK;
        Close();
    }

    static bool TryFolder(string text, out string folder)
    {
        folder = text.Trim();
        try { folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>The sidebar: app name and the section list.</summary>
    sealed class NavList : Control
    {
        readonly string[] _items;
        int _selected, _hover = -1;
        public event Action<int>? SelectedChanged;
        public int Selected { get => _selected; set { if (_selected == value) return; _selected = value; Invalidate(); } }

        public NavList(string[] items)
        {
            _items = items;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Font = new Font("Segoe UI", 10f);
        }

        float Dpi => DeviceDpi / 96f;
        RectangleF Item(int i) { float s = Dpi; return new RectangleF(12 * s, (78 + i * 40) * s, Width - 24 * s, 34 * s); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Dpi;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);

            using (var icon = Theme.AppIcon((int)(28 * s))) g.DrawIcon(icon, new Rectangle((int)(20 * s), (int)(24 * s), (int)(28 * s), (int)(28 * s)));
            using (var f = new Font("Segoe UI Semibold", 12f))
                TextRenderer.DrawText(g, "PRTSCN", f, new Point((int)(56 * s), (int)(22 * s)), Theme.Text, TextFormatFlags.NoPadding);
            using (var f = new Font("Segoe UI", 8.5f))
                TextRenderer.DrawText(g, "Options", f, new Point((int)(56 * s), (int)(42 * s)), Theme.TextMuted, TextFormatFlags.NoPadding);

            for (int i = 0; i < _items.Length; i++)
            {
                var r = Item(i);
                bool sel = i == _selected;
                if (sel) Theme.FillRound(g, Theme.SurfaceActive, r, 6 * s);
                else if (i == _hover) Theme.FillRound(g, Theme.WithAlpha(Theme.SurfaceActive, 120), r, 6 * s);
                if (sel) Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y + 8 * s, 3 * s, r.Height - 16 * s), 1.5f * s);
                using var f = new Font(Font, sel ? FontStyle.Bold : FontStyle.Regular);
                var tr = Rectangle.Round(new RectangleF(r.X + 14 * s, r.Y, r.Width - 14 * s, r.Height));
                TextRenderer.DrawText(g, _items[i], f, tr, sel ? Theme.Text : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }

        int Hit(Point p) { for (int i = 0; i < _items.Length; i++) if (Item(i).Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; if (h != _hover) { _hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = Hit(e.Location);
            if (h >= 0) { Selected = h; SelectedChanged?.Invoke(h); }
        }
    }
}
