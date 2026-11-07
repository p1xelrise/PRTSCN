using System.Drawing.Drawing2D;
using PRTSCN.Core;
using SelHandle = PRTSCN.Core.Handle;

namespace PRTSCN;

/// <summary>
/// The capture screen: a borderless top-most window over the whole virtual desktop showing
/// the frozen screenshot, dimmed outside the selection. All chrome (toolbars, handles, label)
/// is painted here, so there are no child controls to get DPI-scaled wrongly.
/// Coordinates are "client" pixels: physical screen pixels minus the virtual desktop origin.
/// </summary>
internal sealed class CaptureOverlay : Form
{
    sealed class Btn
    {
        public IconKind Icon;
        public string Tip = "";
        public Rectangle Bounds;
        public Tool Tool = Tool.None;
        public Action? Click;
        public Color? Swatch;
        public int? SizeValue;
        public bool Vertical;
        // record toolbar
        public Func<string>? Label;     // text after the icon (width grows to fit)
        public Func<bool>? IsOn;        // toggle / selected segment
        public bool Primary;            // the red Record button
        public bool SepBefore;          // thin divider before this button
    }

    enum Drag { None, Selecting, MoveResize, Drawing }

    readonly Bitmap _shot, _dim;
    readonly Rectangle _virt;
    readonly Rectangle[] _screens;
    readonly float[] _screenScale;
    readonly AppSettings _settings;
    readonly List<WindowInfo> _windows;
    int _hoverWin = -1;
    readonly Action<string, string, string?> _notify;

    Rectangle _sel;
    bool _hasSel;
    Drag _drag;
    SelHandle _handle;
    Point _down;
    Rectangle _selAtDown;
    Point _mouse;

    Tool _tool = Tool.None;
    readonly List<Annotation> _items = new(), _redo = new();
    Annotation? _current;
    TextAnnotation? _editing;
    Color _ink;
    int _inkWidth;

    readonly List<Btn> _tools = new(), _actions = new(), _colorItems = new(), _sizeItems = new();
    List<Btn> _palette = new();   // the open pop-out row: colours or sizes
    Btn? _popupOwner;
    Btn _colorBtn = null!, _sizeBtn = null!, _undoBtn = null!;
    static readonly int[] InkSizes = { 1, 2, 3, 5, 8, 12, 16, 24 };
    readonly bool _windowMode;
    readonly bool _scrollOnSelect;              // opened from "Scrolling capture": start as soon as an area is chosen
    readonly Action<Rectangle>? _onScroll;      // hands the area (physical screen pixels) to the tray app
    // record mode: choose an area, window or screen, then hand it to the tray app to record
    readonly Action<Recording.RecordTarget>? _onRecord;
    bool RecordMode => _onRecord != null;
    Recording.RecordKind _recKind = Recording.RecordKind.Area;
    long _recWindow;
    readonly List<Btn> _recItems = new();
    Rectangle _sizeLabelRect;
    long _pickedWindow;           // window picked in window mode; activated when PRTSCN closes
    Rectangle _vbar, _hbar, _palBar, _tipRect;
    bool _paletteOpen;
    Btn? _hover, _pressed;
    float _scale = 1;
    Rectangle _lastDirty;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 530 };
    bool _caretOn = true;
    DateTime _widthShownUntil;

    public CaptureOverlay(Bitmap shot, Rectangle virt, IEnumerable<WindowInfo> windows, bool windowMode, AppSettings settings,
        Action<string, string, string?> notify, Action<Rectangle>? onScroll = null, bool scrollOnSelect = false,
        Action<Recording.RecordTarget>? onRecord = null, Recording.RecordKind recordKind = Recording.RecordKind.Area)
    {
        _shot = shot;
        _onScroll = onScroll;
        _onRecord = onRecord;
        _recKind = recordKind;
        _scrollOnSelect = scrollOnSelect && onScroll != null;
        _windowMode = windowMode;
        // Only the window hotkey (and recording) picks windows; plain Print Screen is area-only.
        _windows = windowMode || onRecord != null ? WindowPicker.Prepare(windows, virt) : new List<WindowInfo>();
        _virt = virt;
        _settings = settings;
        _notify = notify;
        _ink = Theme.Parse(settings.InkColor);
        _inkWidth = settings.InkWidth;

        _dim = new Bitmap(shot);
        using (var g = Graphics.FromImage(_dim))
        using (var b = new SolidBrush(Theme.WithAlpha(Theme.Bg, Theme.AlphaDim)))
            g.FillRectangle(b, 0, 0, _dim.Width, _dim.Height);

        var all = Screen.AllScreens;
        _screens = all.Select(s => { var r = s.Bounds; r.Offset(-virt.X, -virt.Y); return r; }).ToArray();
        _screenScale = all.Select(s => Native.ScaleAt(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2)).ToArray();

        Text = "PRTSCN";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = virt;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        BuildButtons();
        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();
        _mouse = new Point(Cursor.Position.X - virt.X, Cursor.Position.Y - virt.Y);
        _hoverWin = WindowPicker.HitTest(_windows, _mouse);
        // "Record full screen" from the tray: the monitor under the mouse is selected straight away.
        if (RecordMode && _recKind == Recording.RecordKind.Screen) { _sel = ScreenAt(_mouse); _hasSel = true; Layout2(); }
    }

    /// <summary>Before a drag starts (or while a press has not moved yet), hovering highlights a window.</summary>
    bool Picking => !_hasSel || (_drag == Drag.Selecting && _sel.Width < 4 && _sel.Height < 4);

    // ---------- window plumbing ----------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Re-assert the exact physical bounds; a DPI change while showing must not resize us.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _virt.X, _virt.Y, _virt.Width, _virt.Height, Native.SWP_SHOWWINDOW);
        Activate();
        Native.SetForegroundWindow(Handle);
        Redraw(full: true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        if (_pickedWindow != 0) WindowList.Activate(_pickedWindow); // bring the captured window to the front for real
        foreach (var d in _items.Concat(_redo).OfType<IDisposable>()) d.Dispose();
        _settings.InkColor = Theme.ToHex(_ink);
        _settings.InkWidth = _inkWidth;
        base.OnFormClosed(e);
        _dim.Dispose();
        _shot.Dispose();
    }

    void OnTimer()
    {
        if (_editing != null)
        {
            _caretOn = !_caretOn;
            Redraw();
        }
        if (_widthShownUntil != default && DateTime.Now > _widthShownUntil)
        {
            _widthShownUntil = default;
            Redraw();
        }
    }

    // ---------- buttons and layout ----------

    void BuildButtons()
    {
        void T(IconKind i, Tool t, string tip) => _tools.Add(new Btn { Icon = i, Tool = t, Tip = tip, Vertical = true, Click = () => SetTool(t) });
        T(IconKind.Pen, Tool.Pen, "Pen  (P)");
        T(IconKind.Line, Tool.Line, "Line  (L)");
        T(IconKind.Arrow, Tool.Arrow, "Arrow  (A)");
        T(IconKind.Rect, Tool.Rect, "Rectangle  (R)");
        T(IconKind.Marker, Tool.Marker, "Marker  (M)");
        T(IconKind.Blur, Tool.Blur, "Blur  (B)");
        T(IconKind.Step, Tool.Step, "Numbered steps  (N)");
        T(IconKind.Text, Tool.Text, "Text  (T)");
        _colorBtn = new Btn { Icon = IconKind.Color, Tip = "Colour  (1-8)", Vertical = true, Click = () => TogglePopup(_colorBtn, _colorItems) };
        _sizeBtn = new Btn { Icon = IconKind.Size, Tip = "Size  ([ ]  or mouse wheel)", Vertical = true, Click = () => TogglePopup(_sizeBtn, _sizeItems) };
        _undoBtn = new Btn { Icon = IconKind.Undo, Tip = "Undo  (Ctrl+Z)", Vertical = true, Click = Undo };
        _tools.Add(_colorBtn);
        _tools.Add(_sizeBtn);
        _tools.Add(_undoBtn);
        foreach (var v in InkSizes)
            _sizeItems.Add(new Btn { Icon = IconKind.Size, SizeValue = v, Tip = $"{v}px", Click = () => PickSize(v) });

        _actions.Add(new Btn { Icon = IconKind.Copy, Tip = "Copy  (Ctrl+C)", Click = CopyAndClose });
        _actions.Add(new Btn { Icon = IconKind.Save, Tip = "Save  (Ctrl+S)", Click = SaveAs });
        if (_onScroll != null)
            _actions.Add(new Btn { Icon = IconKind.Scroll, Tip = "Scrolling capture: scroll this area into one long image", Click = StartScroll });
        _actions.Add(new Btn { Icon = IconKind.Print, Tip = "Print  (Ctrl+P)", Click = PrintIt });
        _actions.Add(new Btn { Icon = IconKind.Close, Tip = "Close  (Esc)", Click = Close });

        for (int i = 0; i < Theme.Ink.Length; i++)
        {
            var c = Theme.Ink[i];
            _colorItems.Add(new Btn { Icon = IconKind.Color, Swatch = c, Tip = $"{i + 1}", Click = () => PickColor(c) });
        }
        _colorItems.Add(new Btn { Icon = IconKind.More, Tip = "More colours…", Click = MoreColors });

        if (RecordMode) BuildRecordButtons();
    }

    void BuildRecordButtons()
    {
        _recItems.Add(new Btn { Icon = IconKind.Area, Label = () => "Area", Tip = "Record the selected area", IsOn = () => _recKind == Recording.RecordKind.Area, Click = () => SetRecordKind(Recording.RecordKind.Area) });
        _recItems.Add(new Btn { Icon = IconKind.Window, Label = () => "Window", Tip = "Record a window: click it  (follows it when it moves)", IsOn = () => _recKind == Recording.RecordKind.Window, Click = () => SetRecordKind(Recording.RecordKind.Window) });
        _recItems.Add(new Btn { Icon = IconKind.Screen, Label = () => "Screen", Tip = "Record the whole screen  (F)", IsOn = () => _recKind == Recording.RecordKind.Screen, Click = () => SetRecordKind(Recording.RecordKind.Screen) });
        _recItems.Add(new Btn { Icon = IconKind.Follow, Label = () => "Follow", Tip = "Follow the mouse: record a view of this size that glides after the pointer", IsOn = () => _recKind == Recording.RecordKind.Follow, Click = () => SetRecordKind(Recording.RecordKind.Follow) });
        _recItems.Add(new Btn { Icon = IconKind.AllScreens, Label = () => "All", Tip = "Record all screens together", IsOn = () => _recKind == Recording.RecordKind.AllScreens, Click = () => SetRecordKind(Recording.RecordKind.AllScreens) });
        _recItems.Add(new Btn { Icon = IconKind.Cursor, SepBefore = true, Tip = "Show the mouse pointer in the video", IsOn = () => _settings.RecordShowCursor, Click = () => { _settings.RecordShowCursor = !_settings.RecordShowCursor; Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Click, Tip = "Highlight mouse clicks", IsOn = () => _settings.RecordClicks, Click = () => { _settings.RecordClicks = !_settings.RecordClicks; Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Keyboard, Tip = "Show pressed keys: off, shortcuts only, or all typing (click to change)",
            Label = () => _settings.RecordKeys switch { KeyDisplay.Shortcuts => "Shortcuts", KeyDisplay.All => "All keys", _ => "" },
            IsOn = () => _settings.RecordKeys != KeyDisplay.Off,
            Click = () => { _settings.RecordKeys = (KeyDisplay)(((int)_settings.RecordKeys + 1) % 3); Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Spotlight, Tip = "Spotlight: dim everything except around the pointer", IsOn = () => _settings.RecordSpotlight, Click = () => { _settings.RecordSpotlight = !_settings.RecordSpotlight; Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Magnifier, Tip = "Magnifier: a lens that zooms around the pointer", IsOn = () => _settings.RecordMagnifier, Click = () => { _settings.RecordMagnifier = !_settings.RecordMagnifier; Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Speaker, Tip = "Record computer sound", IsOn = () => _settings.RecordSystemAudio, Click = () => { _settings.RecordSystemAudio = !_settings.RecordSystemAudio; Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Mic, Tip = "Record the microphone  (pick it in Options > Recording)", IsOn = () => _settings.RecordMic, Click = () => { _settings.RecordMic = !_settings.RecordMic; Layout2(); } });
        Btn? format = null;
        format = new Btn
        {
            Icon = IconKind.Video, SepBefore = true,
            Label = () => _settings.RecordFormat == RecordFormat.Gif ? $"GIF · {_settings.GifFps} fps" : $"MP4 · {_settings.VideoFps} fps",
            Tip = "Format and frame rate: MP4 (15 / 30 / 60 fps) or GIF",
            Click = () => ShowFormatMenu(format!),
        };
        _recItems.Add(format);
        _recItems.Add(new Btn { Icon = IconKind.Timer, Label = () => _settings.RecordCountdown == 0 ? "Off" : $"{_settings.RecordCountdown} s", Tip = "Countdown before recording starts: click to change", Click = () => { _settings.RecordCountdown = Next(AppSettings.CountdownChoices, _settings.RecordCountdown); Layout2(); } });
        _recItems.Add(new Btn { Icon = IconKind.Record, SepBefore = true, Primary = true, Label = () => "Record", Tip = "Start recording  (Enter)", Click = StartRecord });
    }

    bool _recCompact;

    /// <summary>Developer check (--ui-shots): select a mode as if clicked, and return where the record bar is.</summary>
    internal Rectangle PreviewRecordMode(Recording.RecordKind kind, Rectangle sel)
    {
        _sel = sel;
        _hasSel = true;
        SetRecordKind(kind);
        Layout2();
        return Rectangle.Union(_hbar, _sel);
    }

    /// <summary>The text shown on a record-bar button, or null for an icon-only button.</summary>
    string? ShownLabel(Btn b)
    {
        if (b.Label == null) return null;
        if (_recCompact && !b.Primary) return null;
        var l = b.Label();
        return string.IsNullOrEmpty(l) ? null : l;
    }

    static int Next(int[] choices, int current)
    {
        int i = Array.IndexOf(choices, current);
        return choices[(i + 1) % choices.Length];
    }

    void SetRecordKind(Recording.RecordKind kind)
    {
        switch (kind)
        {
            case Recording.RecordKind.Screen:
                _sel = _hasSel ? CurrentScreen : ScreenAt(_mouse);
                _hasSel = true;
                _recKind = kind;
                _recWindow = 0;
                Layout2();
                Redraw(full: true);
                break;
            case Recording.RecordKind.Window:
                // Back to picking: the next click chooses a window.
                _recKind = kind;
                ResetSelection();
                break;
            case Recording.RecordKind.AllScreens:
                _sel = new Rectangle(Point.Empty, _virt.Size);
                _hasSel = true;
                _recKind = kind;
                _recWindow = 0;
                Layout2();
                Redraw(full: true);
                break;
            case Recording.RecordKind.Follow:
            {
                // Keep a selected area as the view size; otherwise start with 1280 × 720 around the mouse.
                var screen = _hasSel ? CurrentScreen : ScreenAt(_mouse);
                bool fullScreen = _hasSel && _sel.Width >= screen.Width && _sel.Height >= screen.Height;
                if (!_hasSel || fullScreen || _recKind == Recording.RecordKind.AllScreens)
                {
                    int w = Math.Min(1280, screen.Width / 2 * 2), h = Math.Min(720, screen.Height / 2 * 2);
                    var c = _hasSel && !fullScreen ? new Point(_sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2) : _mouse;
                    if (!screen.Contains(c)) c = new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
                    _sel = new Rectangle(Math.Clamp(c.X - w / 2, screen.Left, screen.Right - w), Math.Clamp(c.Y - h / 2, screen.Top, screen.Bottom - h), w, h);
                }
                _hasSel = true;
                _recKind = kind;
                _recWindow = 0;
                Layout2();
                Redraw(full: true);
                break;
            }
            default:
                _recKind = kind;
                _recWindow = 0;
                if (!_hasSel) Redraw(full: true); else Layout2();
                break;
        }
    }

    /// <summary>Selection was dragged, moved or resized by hand: it is an area now.</summary>
    void BecameArea()
    {
        // A follow view keeps following when resized; other kinds become a plain area.
        if (!RecordMode || _recKind is Recording.RecordKind.Area or Recording.RecordKind.Follow) return;
        _recKind = Recording.RecordKind.Area;
        _recWindow = 0;
    }

    void StartRecord()
    {
        if (!_hasSel || _onRecord == null) return;
        if (_sel.Width < 16 || _sel.Height < 16) { _notify("Area too small", "Select at least 16 × 16 pixels to record.", null); return; }
        var physical = _sel;
        physical.Offset(_virt.X, _virt.Y);
        var target = _recKind switch
        {
            Recording.RecordKind.Window when _recWindow != 0 => new Recording.RecordTarget(Recording.RecordKind.Window, physical, (IntPtr)_recWindow),
            Recording.RecordKind.Screen or Recording.RecordKind.Follow or Recording.RecordKind.AllScreens => new Recording.RecordTarget(_recKind, physical),
            _ => new Recording.RecordTarget(Recording.RecordKind.Area, physical),
        };
        Hide();
        Close();
        _onRecord(target);
    }

    static readonly (int w, int h, string name)[] SizePresets =
    {
        (1920, 1080, "Full HD 16:9"), (1280, 720, "HD 16:9"), (854, 480, "480p 16:9"),
        (1080, 1080, "Square 1:1"), (1080, 1350, "Portrait 4:5"), (1080, 1920, "Vertical 9:16"), (720, 1280, "Vertical 9:16 small"),
    };

    void ShowFormatMenu(Btn owner)
    {
        var menu = Editor.DarkMenu.Create(_scale);
        foreach (var fps in AppSettings.FpsChoices)
        {
            var item = new ToolStripMenuItem($"MP4 · {fps} fps") { Checked = _settings.RecordFormat == RecordFormat.Mp4 && _settings.VideoFps == fps };
            item.Click += (_, _) => { _settings.RecordFormat = RecordFormat.Mp4; _settings.VideoFps = fps; Layout2(); Redraw(full: true); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var gif = new ToolStripMenuItem($"GIF · {_settings.GifFps} fps, no sound") { Checked = _settings.RecordFormat == RecordFormat.Gif };
        gif.Click += (_, _) => { _settings.RecordFormat = RecordFormat.Gif; Layout2(); Redraw(full: true); };
        menu.Items.Add(gif);
        menu.Show(this, new Point(owner.Bounds.Left, owner.Bounds.Bottom + S(4)));
    }

    void ShowSizeMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        foreach (var (w, h, name) in SizePresets)
            menu.Items.Add($"{w} × {h}    {name}", null, (_, _) => SetExactSize(w, h));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Custom size…", null, (_, _) =>
        {
            var size = SizeDialog.Ask(this, _sel.Size);
            if (size is Size s) SetExactSize(s.Width, s.Height);
        });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point(_sizeLabelRect.Left, _sizeLabelRect.Bottom + S(4)));
    }

    /// <summary>Resize the selection to exact pixels around its centre, kept on its monitor.</summary>
    void SetExactSize(int w, int h)
    {
        var screen = CurrentScreen;
        w = Math.Min(w, screen.Width); h = Math.Min(h, screen.Height);
        var c = new Point(_sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2);
        int x = Math.Clamp(c.X - w / 2, screen.Left, screen.Right - w);
        int y = Math.Clamp(c.Y - h / 2, screen.Top, screen.Bottom - h);
        _sel = new Rectangle(x, y, w, h);
        BecameArea();
        Layout2();
        Redraw(full: true);
    }

    void TogglePopup(Btn owner, List<Btn> items)
    {
        bool same = _paletteOpen && _popupOwner == owner;
        _paletteOpen = !same;
        _popupOwner = owner;
        _palette = items;
        Layout2();
    }

    Rectangle CurrentScreen => _hasSel ? ToolbarLayout.ScreenFor(_sel, _screens) : ScreenAt(_mouse);

    Rectangle ScreenAt(Point p)
    {
        foreach (var s in _screens) if (s.Contains(p)) return s;
        return _screens[0];
    }

    float ScaleOf(Rectangle screen)
    {
        int i = Array.IndexOf(_screens, screen);
        return i >= 0 ? _screenScale[i] : 1f;
    }

    int S(float v) => (int)Math.Round(v * _scale);

    /// <summary>Place the toolbars around the selection (named Layout2 so it does not clash with Control.Layout).</summary>
    void Layout2()
    {
        if (!_hasSel) return;
        var screen = CurrentScreen;
        _scale = ScaleOf(screen);
        int b = S(Theme.SizeButton), pad = S(Theme.SizeToolbarPad), gap = S(Theme.SizeToolbarGap);

        if (RecordMode)
        {
            // One horizontal record toolbar below the selection (or above / inside when there is no room).
            using var g = CreateGraphics();
            using var f = Theme.UiFont(Theme.TypeSm, true, _scale);
            List<int> widths = new();
            int total = 0;
            // Too wide for the screen: drop the labels (icons and tooltips remain).
            foreach (bool compact in new[] { false, true })
            {
                _recCompact = compact;
                widths = _recItems.Select(it => ShownLabel(it) is string l
                    ? S(10) + S(Theme.SizeIcon) + S(6) + (int)Math.Ceiling(g.MeasureString(l, f).Width) + S(10) : b).ToList();
                total = widths.Sum() + _recItems.Count(it => it.SepBefore) * S(9) + pad * 2;
                if (total <= screen.Width - gap * 2) break;
            }
            _vbar = Rectangle.Empty;
            _hbar = ToolbarLayout.PlaceHorizontal(_sel, new Size(total, b + pad * 2), screen, gap);
            int x = _hbar.X + pad;
            for (int i = 0; i < _recItems.Count; i++)
            {
                if (_recItems[i].SepBefore) x += S(9);
                _recItems[i].Bounds = new Rectangle(x, _hbar.Y + pad, widths[i], b);
                x += widths[i];
            }
            _palBar = Rectangle.Empty;
            Redraw();
            return;
        }

        var vsize = new Size(b + pad * 2, _tools.Count * b + pad * 2);
        _vbar = ToolbarLayout.PlaceVertical(_sel, vsize, screen, gap);
        for (int i = 0; i < _tools.Count; i++)
            _tools[i].Bounds = new Rectangle(_vbar.X + pad, _vbar.Y + pad + i * b, b, b);

        var hsize = new Size(_actions.Count * b + pad * 2, b + pad * 2);
        _hbar = ToolbarLayout.PlaceHorizontal(_sel, hsize, screen, gap);
        // Do not let the two bars overlap in tight corners: push the action bar left.
        if (_hbar.IntersectsWith(_vbar) && _hbar.Right > _vbar.Left)
            _hbar.X = Math.Max(screen.Left, _vbar.Left - gap - _hbar.Width);
        for (int i = 0; i < _actions.Count; i++)
            _actions[i].Bounds = new Rectangle(_hbar.X + pad + i * b, _hbar.Y + pad, b, b);

        if (_paletteOpen)
        {
            var psize = new Size(_palette.Count * b + pad * 2, b + pad * 2);
            int y = Math.Clamp((_popupOwner ?? _colorBtn).Bounds.Y - pad, screen.Top, screen.Bottom - psize.Height);
            int x = _vbar.Right + gap;
            if (x + psize.Width > screen.Right) x = _vbar.Left - gap - psize.Width;
            x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - psize.Width));
            _palBar = new Rectangle(new Point(x, y), psize);
            for (int i = 0; i < _palette.Count; i++)
                _palette[i].Bounds = new Rectangle(_palBar.X + pad + i * b, _palBar.Y + pad, b, b);
        }
        else _palBar = Rectangle.Empty;
        Redraw();
    }

    IEnumerable<Btn> VisibleButtons()
    {
        if (!_hasSel || _drag == Drag.Selecting) yield break;
        if (RecordMode) { foreach (var b in _recItems) yield return b; yield break; }
        foreach (var b in _tools) yield return b;
        foreach (var b in _actions) yield return b;
        if (_paletteOpen) foreach (var b in _palette) yield return b;
    }

    Btn? HitButton(Point p) => VisibleButtons().FirstOrDefault(b => b.Bounds.Contains(p));

    bool OverChrome(Point p) =>
        _hasSel && _drag != Drag.Selecting && (_vbar.Contains(p) || _hbar.Contains(p) || (_paletteOpen && _palBar.Contains(p)));

    // ---------- invalidation ----------

    Rectangle ComputeDirty()
    {
        var r = Rectangle.Empty;
        void U(Rectangle x) { if (!x.IsEmpty) r = r.IsEmpty ? x : Rectangle.Union(r, x); }
        if (_hasSel)
        {
            // Generous margins: the size label can be wider than a tiny selection,
            // and tooltips sit beside the bars.
            int m = S(120);
            U(Rectangle.Inflate(_sel, m, m));
            if (!_vbar.IsEmpty) U(Rectangle.Inflate(_vbar, S(220), S(8)));
            if (!_hbar.IsEmpty) U(Rectangle.Inflate(_hbar, S(80), S(40)));
            U(_palBar); U(_tipRect);
        }
        else
        {
            U(CurrentScreen);
            if (_hoverWin >= 0) U(_windows[_hoverWin].Bounds);
        }
        return Rectangle.Inflate(r, 4, 4);
    }

    void Redraw(bool full = false)
    {
        var d = ComputeDirty();
        if (full) Invalidate();
        else Invalidate(_lastDirty.IsEmpty ? d : Rectangle.Union(_lastDirty, d));
        _lastDirty = d;
    }

    // ---------- painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_dim, clip, clip, GraphicsUnit.Pixel);
        if (_hasSel && !Picking)
        {
            var r = Rectangle.Intersect(_sel, clip);
            if (!r.IsEmpty) g.DrawImage(_shot, r, r, GraphicsUnit.Pixel);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Picking) { DrawWindowHighlight(g, clip); DrawHint(g); return; }

        var state = g.Save();
        g.SetClip(_sel);
        foreach (var a in _items) a.Draw(g);
        _current?.Draw(g);
        if (_current is BlurAnnotation cb)
        {
            using var dash = new Pen(Theme.Accent, 1) { DashStyle = DashStyle.Dash };
            var br = cb.Rect;
            g.DrawRectangle(dash, br.X, br.Y, br.Width - 1, br.Height - 1);
        }
        if (_editing != null) DrawTextEditing(g, _editing);
        g.Restore(state);

        DrawFrame(g);
        DrawSizeLabel(g);
        if (_drag != Drag.Selecting && RecordMode)
        {
            DrawRecordBar(g);
            DrawTooltip(g);
        }
        else if (_drag != Drag.Selecting)
        {
            DrawBar(g, _vbar, _tools);
            DrawBar(g, _hbar, _actions);
            if (_paletteOpen) DrawBar(g, _palBar, _palette);
            DrawTooltip(g);
        }
    }

    void DrawHint(Graphics g)
    {
        var screen = CurrentScreen;
        _scale = ScaleOf(screen);
        using var f = Theme.UiFont(Theme.TypeBase, false, _scale);
        string text = RecordMode
            ? (_recKind == Recording.RecordKind.Window
                ? "Record: click the window to record   ·   Esc to cancel"
                : "Record: drag an area, click a window, or press F for the whole screen   ·   Esc to cancel")
            : _scrollOnSelect
            ? "Scrolling capture: select the area that scrolls   ·   Esc to cancel"
            : _windowMode
            ? "Click a window to capture it   ·   Drag to select an area   ·   Esc to cancel"
            : "Drag to select an area   ·   Click for the whole screen   ·   Esc to cancel";
        var sz = g.MeasureString(text, f);
        var r = new RectangleF(screen.X + (screen.Width - sz.Width) / 2 - S(12), screen.Y + S(24), sz.Width + S(24), sz.Height + S(12));
        Theme.FillRound(g, Theme.WithAlpha(Theme.Bg, Theme.AlphaToolbar), r, Theme.RadiusPill * _scale);
        Theme.StrokeRound(g, Theme.Border, 1, r, Theme.RadiusPill * _scale);
        using var b = new SolidBrush(Theme.Text);
        g.DrawString(text, f, b, r.X + S(12), r.Y + S(6));
    }

    void DrawWindowHighlight(Graphics g, Rectangle clip)
    {
        if (_hoverWin < 0) return;
        var w = _windows[_hoverWin];
        var r = w.Bounds;
        var vis = Rectangle.Intersect(r, clip);
        if (!vis.IsEmpty)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_shot, vis, vis, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
        }
        var screen = ToolbarLayout.ScreenFor(r, _screens);
        _scale = ScaleOf(screen);
        int bw = Math.Max(2, S(3));
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var pen = new Pen(Theme.Accent, bw) { Alignment = PenAlignment.Inset })
            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        g.SmoothingMode = old;

        string text = WindowPicker.Label(w);
        using var f = Theme.UiFont(Theme.TypeXs, Theme.TypeXsBold, _scale);
        var sz = g.MeasureString(text, f);
        var size = new Size((int)sz.Width + S(12), (int)sz.Height + S(6));
        var p = ToolbarLayout.PlaceSizeLabel(r, size, screen, S(6));
        var lr = new Rectangle(p, size);
        Theme.FillRound(g, Theme.Accent, lr, Theme.RadiusSm * _scale);
        using var b = new SolidBrush(Theme.OnAccent);
        g.DrawString(text, f, b, lr.X + S(6), lr.Y + S(3));
    }

    void DrawFrame(Graphics g)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var pen = new Pen(Theme.Accent, Math.Max(1, S(Theme.StrokeSelection))))
            g.DrawRectangle(pen, _sel.X, _sel.Y, _sel.Width - 1, _sel.Height - 1);
        if (_drag != Drag.Selecting)
        {
            int hs = Math.Max(6, S(Theme.SizeHandle));
            using var fill = new SolidBrush(Theme.HandleFill);
            using var edge = new Pen(Theme.Accent, 1);
            foreach (var p in HandlePoints())
            {
                var r = new Rectangle(p.X - hs / 2, p.Y - hs / 2, hs, hs);
                g.FillRectangle(fill, r);
                g.DrawRectangle(edge, r);
            }
        }
        g.SmoothingMode = old;
    }

    IEnumerable<Point> HandlePoints()
    {
        int l = _sel.Left, t = _sel.Top, r = _sel.Right - 1, b = _sel.Bottom - 1, cx = l + (r - l) / 2, cy = t + (b - t) / 2;
        return new[] { new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy), new Point(r, b), new Point(cx, b), new Point(l, b), new Point(l, cy) };
    }

    void DrawSizeLabel(Graphics g)
    {
        string text = $"{_sel.Width} × {_sel.Height}";
        if (_widthShownUntil != default && !RecordMode) text += $"   ·   {_inkWidth}px";
        if (RecordMode) text += "  ▾"; // click for exact sizes
        using var f = Theme.UiFont(Theme.TypeXs, Theme.TypeXsBold, _scale);
        var sz = g.MeasureString(text, f);
        var size = new Size((int)sz.Width + S(12), (int)sz.Height + S(6));
        var p = ToolbarLayout.PlaceSizeLabel(_sel, size, CurrentScreen, S(6));
        var r = new Rectangle(p, size);
        _sizeLabelRect = r;
        bool hot = RecordMode && r.Contains(_mouse) && _drag == Drag.None;
        Theme.FillRound(g, hot ? Theme.SurfaceActive : Theme.WithAlpha(Theme.Bg, Theme.AlphaToolbar), r, Theme.RadiusSm * _scale);
        using var b = new SolidBrush(Theme.Text);
        g.DrawString(text, f, b, r.X + S(6), r.Y + S(3));
    }

    void DrawBar(Graphics g, Rectangle bar, List<Btn> buttons)
    {
        if (bar.IsEmpty) return;
        Theme.FillRound(g, Theme.WithAlpha(Theme.Bg, Theme.AlphaToolbar), bar, Theme.RadiusMd * _scale);
        Theme.StrokeRound(g, Theme.Border, 1, bar, Theme.RadiusMd * _scale);
        foreach (var btn in buttons)
        {
            var r = btn.Bounds;
            bool active = btn.Tool != Tool.None && btn.Tool == _tool;
            bool hover = btn == _hover, pressed = btn == _pressed;
            bool disabled = btn == _undoBtn && _items.Count == 0 && _editing == null;
            var inner = Rectangle.Inflate(r, -S(2), -S(2));
            if (active || pressed) Theme.FillRound(g, Theme.SurfaceActive, inner, Theme.RadiusSm * _scale);
            else if (hover && !disabled) Theme.FillRound(g, Theme.Surface, inner, Theme.RadiusSm * _scale);
            if (active)
            {
                using var ab = new SolidBrush(Theme.Accent);
                g.FillRectangle(ab, inner.X, inner.Y + S(6), S(2), inner.Height - S(12));
            }

            if (btn.SizeValue is int sv)
            {
                float d = Math.Min(r.Width - S(10), S(2) + sv * _scale * 0.9f);
                var dot = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
                bool cur = sv == _inkWidth;
                using var b = new SolidBrush(cur ? Theme.Accent : hover ? Theme.Text : Theme.TextMuted);
                g.FillEllipse(b, dot);
                if (cur) Theme.StrokeRound(g, Theme.Accent, 1, Rectangle.Inflate(r, -S(3), -S(3)), Theme.RadiusSm * _scale);
                continue;
            }
            if (btn.Swatch is Color sw)
            {
                var c = Rectangle.Inflate(r, -S(8), -S(8));
                using var b = new SolidBrush(sw);
                g.FillEllipse(b, c);
                bool sel = sw.ToArgb() == _ink.ToArgb();
                using var ring = new Pen(sel ? Theme.Accent : Theme.Border, sel ? S(2) : 1);
                g.DrawEllipse(ring, Rectangle.Inflate(c, sel ? S(2) : 0, sel ? S(2) : 0));
                continue;
            }

            var color = disabled ? Theme.Border
                : active ? Theme.Accent
                : hover && btn.Icon == IconKind.Close ? Theme.Danger
                : hover ? Theme.Text : Theme.TextMuted;
            int isz = S(Theme.SizeIcon);
            var box = new RectangleF(r.X + (r.Width - isz) / 2f, r.Y + (r.Height - isz) / 2f, isz, isz);
            Icons.Draw(g, btn.Icon, box, color, _scale, _ink);
        }
    }

    void DrawRecordBar(Graphics g)
    {
        if (_hbar.IsEmpty) return;
        Theme.FillRound(g, Theme.WithAlpha(Theme.Bg, Theme.AlphaToolbar), _hbar, Theme.RadiusMd * _scale);
        Theme.StrokeRound(g, Theme.Border, 1, _hbar, Theme.RadiusMd * _scale);
        using var f = Theme.UiFont(Theme.TypeSm, true, _scale);
        using var sep = new Pen(Theme.Border, 1);
        foreach (var btn in _recItems)
        {
            var r = btn.Bounds;
            if (btn.SepBefore)
            {
                int sx = r.X - S(5);
                g.DrawLine(sep, sx, r.Y + S(6), sx, r.Bottom - S(6));
            }
            bool on = btn.IsOn?.Invoke() == true, hover = btn == _hover, pressed = btn == _pressed;
            var inner = Rectangle.Inflate(r, -S(2), -S(2));
            Color fg;
            if (btn.Primary)
            {
                var bg = pressed ? ControlPaint.Dark(Theme.Danger, 0.05f) : hover ? ControlPaint.Light(Theme.Danger, 0.2f) : Theme.Danger;
                Theme.FillRound(g, bg, inner, Theme.RadiusSm * _scale);
                fg = Theme.OnAccent;
            }
            else
            {
                if (on || pressed) Theme.FillRound(g, Theme.SurfaceActive, inner, Theme.RadiusSm * _scale);
                else if (hover) Theme.FillRound(g, Theme.Surface, inner, Theme.RadiusSm * _scale);
                // A selected mode reads as selected (white); a toggle that is on reads as on (amber).
                bool isMode = btn.Icon is IconKind.Area or IconKind.Window or IconKind.Screen or IconKind.Follow or IconKind.AllScreens;
                fg = on ? (isMode ? Theme.Text : Theme.Accent) : hover ? Theme.Text : Theme.TextMuted;
            }
            int isz = S(Theme.SizeIcon);
            var text = ShownLabel(btn);
            if (text == null)
            {
                Icons.Draw(g, btn.Icon, new RectangleF(r.X + (r.Width - isz) / 2f, r.Y + (r.Height - isz) / 2f, isz, isz), fg, _scale, fg);
                continue;
            }
            int small = S(16);
            var box = new RectangleF(r.X + S(10), r.Y + (r.Height - small) / 2f, small, small);
            Icons.Draw(g, btn.Icon, box, fg, _scale, fg);
            var ts = g.MeasureString(text, f);
            using var tb = new SolidBrush(fg);
            g.DrawString(text, f, tb, box.Right + S(6), r.Y + (r.Height - ts.Height) / 2f);
        }
    }

    void DrawTooltip(Graphics g)
    {
        _tipRect = Rectangle.Empty;
        var btn = _hover;
        if (btn == null || _drag != Drag.None || btn.Swatch != null) return;
        using var f = Theme.UiFont(Theme.TypeSm, false, _scale);
        var sz = g.MeasureString(btn.Tip, f);
        var size = new Size((int)sz.Width + S(12), (int)sz.Height + S(6));
        var screen = CurrentScreen;
        int gap = S(6);
        Rectangle r;
        if (btn.Vertical)
        {
            int y = btn.Bounds.Y + (btn.Bounds.Height - size.Height) / 2;
            // Prefer the side away from the selection.
            bool barOnRight = _vbar.Left >= _sel.Right;
            int x = barOnRight ? _vbar.Right + gap : _vbar.Left - gap - size.Width;
            if (x < screen.Left || x + size.Width > screen.Right) x = barOnRight ? _vbar.Left - gap - size.Width : _vbar.Right + gap;
            r = new Rectangle(x, y, size.Width, size.Height);
        }
        else
        {
            var bar = _paletteOpen && _palBar.Contains(btn.Bounds) ? _palBar : _hbar;
            int x = btn.Bounds.X + (btn.Bounds.Width - size.Width) / 2;
            int y = bar.Bottom + gap;
            if (y + size.Height > screen.Bottom) y = bar.Top - gap - size.Height;
            r = new Rectangle(x, y, size.Width, size.Height);
        }
        r.X = Math.Clamp(r.X, screen.Left, Math.Max(screen.Left, screen.Right - r.Width));
        r.Y = Math.Clamp(r.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - r.Height));
        _tipRect = r;
        Theme.FillRound(g, Theme.SurfaceActive, r, Theme.RadiusSm * _scale);
        using var b = new SolidBrush(Theme.Text);
        g.DrawString(btn.Tip, f, b, r.X + S(6), r.Y + S(3));
    }

    void DrawTextEditing(Graphics g, TextAnnotation t)
    {
        t.Draw(g);
        var (box, caret, h) = t.Measure(g);
        using var dash = new Pen(Theme.Accent, 1) { DashStyle = DashStyle.Dash };
        var b = RectangleF.Inflate(box, 4, 3);
        g.DrawRectangle(dash, b.X, b.Y, b.Width, b.Height);
        if (_caretOn)
        {
            using var cp = new Pen(t.Color, Math.Max(1.5f, t.FontPx / 14f));
            g.DrawLine(cp, caret.X, caret.Y + h * 0.12f, caret.X, caret.Y + h * 0.9f);
        }
    }

    // ---------- mouse ----------

    Point ClampToVirtual(Point p) =>
        new(Math.Clamp(p.X, 0, _virt.Width - 1), Math.Clamp(p.Y, 0, _virt.Height - 1));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = e.Location;
        _mouse = p;

        if (e.Button == MouseButtons.Right)
        {
            if (_editing != null) { CancelText(); return; }
            if (_hasSel) { ResetSelection(); return; }
            Close();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var btn = HitButton(p);
        if (btn != null) { _pressed = btn; Redraw(); return; }
        if (OverChrome(p)) return;
        if (RecordMode && _hasSel && _sizeLabelRect.Contains(p)) { ShowSizeMenu(); return; }
        if (_paletteOpen) { _paletteOpen = false; Layout2(); }
        if (_editing != null) CommitText();

        if (!_hasSel)
        {
            BeginSelecting(p);
            return;
        }

        var h = SelectionMath.HitTest(_sel, p, Math.Max(4, S(5)));
        if (h != SelHandle.None && h != SelHandle.Move && !(_tool != Tool.None && InsideDeep(p)))
        {
            BeginMoveResize(p, h);
            return;
        }
        if (_tool != Tool.None && _sel.Contains(p))
        {
            BeginDrawing(p);
            return;
        }
        if (h == SelHandle.Move)
        {
            BeginMoveResize(p, h);
            return;
        }
        // Outside the selection: start over, unless that would throw away annotations.
        if (_items.Count == 0) BeginSelecting(p);
    }

    /// <summary>With a tool active, edge handles still win, but only right at the border.</summary>
    bool InsideDeep(Point p) => Rectangle.Inflate(_sel, -Math.Max(4, S(5)), -Math.Max(4, S(5))).Contains(p);

    void BeginSelecting(Point p)
    {
        _items.Clear(); _redo.Clear();
        _down = p;
        _sel = SelectionMath.FromPoints(p, p);
        _hasSel = true;
        _drag = Drag.Selecting;
        _scale = ScaleOf(ScreenAt(p));
        Redraw(full: true);
    }

    void BeginMoveResize(Point p, SelHandle h)
    {
        _drag = Drag.MoveResize;
        _handle = h;
        _down = p;
        _selAtDown = _sel;
    }

    void BeginDrawing(Point p)
    {
        _redo.Clear();
        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Marker:
                var s = new StrokeAnnotation { Marker = _tool == Tool.Marker, Color = _ink, Width = _inkWidth };
                s.Points.Add(p);
                _current = s;
                break;
            case Tool.Line:
            case Tool.Arrow:
                _current = new LineAnnotation { A = p, B = p, Arrow = _tool == Tool.Arrow, Color = _ink, Width = _inkWidth };
                break;
            case Tool.Rect:
                _current = new RectAnnotation { A = p, B = p, Color = _ink, Width = _inkWidth };
                break;
            case Tool.Blur:
                _current = new BlurAnnotation(_shot) { A = p, B = p, Width = _inkWidth };
                break;
            case Tool.Step:
                int next = _items.OfType<StepAnnotation>().Select(x => x.Number).DefaultIfEmpty(0).Max() + 1;
                _items.Add(new StepAnnotation { Center = p, Number = next, Color = _ink, Width = _inkWidth });
                Redraw();
                return;
            case Tool.Text:
                _editing = new TextAnnotation { Origin = p, Color = _ink, Width = _inkWidth };
                _caretOn = true;
                Redraw();
                return;
        }
        _drag = Drag.Drawing;
        Redraw();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.Location;
        var prevScreen = CurrentScreen;
        _mouse = p;
        bool shift = ModifierKeys.HasFlag(Keys.Shift);

        switch (_drag)
        {
            case Drag.Selecting:
                bool wasPicking = Picking;
                _sel = SelectionMath.FromPoints(_down, ClampToVirtual(p));
                Redraw(full: wasPicking != Picking); // the window highlight disappears once you drag
                return;

            case Drag.MoveResize:
                int dx = p.X - _down.X, dy = p.Y - _down.Y;
                var bounds = new Rectangle(Point.Empty, _virt.Size);
                _sel = _handle == SelHandle.Move
                    ? SelectionMath.ClampMove(SelectionMath.Resize(_selAtDown, SelHandle.Move, dx, dy), bounds)
                    : Rectangle.Intersect(SelectionMath.Resize(_selAtDown, _handle, dx, dy), bounds);
                Layout2();
                return;

            case Drag.Drawing:
                switch (_current)
                {
                    case StrokeAnnotation s:
                        var last = s.Points[^1];
                        if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 1) s.Points.Add(p);
                        break;
                    case LineAnnotation l:
                        l.B = shift ? Snap45(l.A, p) : p;
                        break;
                    case RectAnnotation r:
                        r.B = shift ? Square(r.A, p) : p;
                        break;
                    case BlurAnnotation bl:
                        bl.B = shift ? Square(bl.A, p) : p;
                        break;
                }
                Redraw();
                return;
        }

        // Hover
        var btn = HitButton(p);
        if (btn != _hover) { _hover = btn; Redraw(); }
        if (!_hasSel)
        {
            int win = WindowPicker.HitTest(_windows, p);
            if (win != _hoverWin || CurrentScreen != prevScreen)
            {
                _hoverWin = win;
                Redraw(full: true);
            }
        }
        UpdateCursor(p, btn);
    }

    static PointF Snap45(PointF a, Point b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return new PointF(a.X + (float)Math.Cos(ang) * len, a.Y + (float)Math.Sin(ang) * len);
    }

    static PointF Square(PointF a, Point b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new PointF(a.X + Math.Sign(dx) * m, a.Y + Math.Sign(dy) * m);
    }

    void UpdateCursor(Point p, Btn? btn)
    {
        Cursor c;
        if (btn != null || OverChrome(p)) c = Cursors.Hand;
        else if (!_hasSel) c = Cursors.Cross;
        else
        {
            var h = SelectionMath.HitTest(_sel, p, Math.Max(4, S(5)));
            if (_tool != Tool.None && InsideDeep(p)) h = SelHandle.Move;
            c = h switch
            {
                SelHandle.N or SelHandle.S => Cursors.SizeNS,
                SelHandle.E or SelHandle.W => Cursors.SizeWE,
                SelHandle.NW or SelHandle.SE => Cursors.SizeNWSE,
                SelHandle.NE or SelHandle.SW => Cursors.SizeNESW,
                SelHandle.Move when _tool == Tool.Text => Cursors.IBeam,
                SelHandle.Move when _tool != Tool.None => Cursors.Cross,
                SelHandle.Move => Cursors.SizeAll,
                _ => _items.Count == 0 ? Cursors.Cross : Cursors.Default,
            };
        }
        if (Cursor != c) Cursor = c;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.Location;
        if (_pressed != null)
        {
            var b = _pressed;
            _pressed = null;
            Redraw();
            if (HitButton(p) == b) b.Click?.Invoke();
            return;
        }

        switch (_drag)
        {
            case Drag.Selecting:
                bool clicked = Picking;
                _drag = Drag.None;
                bool pickedWindow = false;
                if (clicked)
                {
                    // A click picks the highlighted window; on the desktop it picks the whole monitor.
                    int win = WindowPicker.HitTest(_windows, p);
                    pickedWindow = win >= 0;
                    _sel = pickedWindow ? BringToFront(_windows[win]) : ScreenAt(p);
                    if (RecordMode)
                    {
                        _recKind = pickedWindow && _windows[win].Handle != 0 ? Recording.RecordKind.Window : Recording.RecordKind.Screen;
                        _recWindow = pickedWindow ? _windows[win].Handle : 0;
                    }
                }
                else BecameArea();
                Layout2();
                Redraw(full: true);
                if (RecordMode) { }
                else if (pickedWindow && _settings.WindowClickCopies) CopyAndClose();
                else if (_scrollOnSelect) StartScroll();
                break;
            case Drag.Drawing:
                _drag = Drag.None;
                if (_current != null && !_current.IsEmpty) _items.Add(_current);
                _current = null;
                Redraw();
                break;
            case Drag.MoveResize:
                _drag = Drag.None;
                if (_sel != _selAtDown) BecameArea();
                Layout2();
                break;
        }
        UpdateCursor(p, HitButton(p));
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _hasSel && _tool == Tool.None && _sel.Contains(e.Location) && HitButton(e.Location) == null)
        {
            if (RecordMode) StartRecord(); else CopyAndClose();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_hasSel) return;
        _inkWidth = Math.Clamp(_inkWidth + Math.Sign(e.Delta), (int)Theme.StrokeMinInk, (int)Theme.StrokeMaxInk);
        if (_editing != null) _editing.Width = _inkWidth;
        _widthShownUntil = DateTime.Now.AddSeconds(1.2);
        Redraw();
    }

    // ---------- keyboard ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool HandleKey(Keys k)
    {
        var code = k & Keys.KeyCode;
        bool ctrl = k.HasFlag(Keys.Control), shift = k.HasFlag(Keys.Shift);

        if (_editing != null)
        {
            switch (code)
            {
                case Keys.Escape: CancelText(); return true;
                case Keys.Enter when shift: _editing.Text.Append('\n'); Redraw(); return true;
                case Keys.Enter: CommitText(); return true;
                case Keys.Back:
                    if (_editing.Text.Length > 0) _editing.Text.Length--;
                    Redraw();
                    return true;
                case Keys.V when ctrl:
                    if (Clipboard.ContainsText()) _editing.Text.Append(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
                    Redraw();
                    return true;
                default:
                    return false; // let OnKeyPress receive the character
            }
        }

        switch (code)
        {
            case Keys.Escape:
                if (_paletteOpen) { _paletteOpen = false; Layout2(); }
                else Close();
                return true;
            case Keys.Enter:
                if (_hasSel) { if (RecordMode) StartRecord(); else CopyAndClose(); }
                return true;
        }
        if (RecordMode)
        {
            if (_drag != Drag.None) return false;
            if (code == Keys.F || (ctrl && code == Keys.A)) { SetRecordKind(Recording.RecordKind.Screen); return true; }
            if (!_hasSel) return false;
            int st = shift ? 10 : 1;
            Rectangle? mv = code switch
            {
                Keys.Left => ctrl ? Grow(-st, 0) : Nudge(-st, 0),
                Keys.Right => ctrl ? Grow(st, 0) : Nudge(st, 0),
                Keys.Up => ctrl ? Grow(0, -st) : Nudge(0, -st),
                Keys.Down => ctrl ? Grow(0, st) : Nudge(0, st),
                _ => null,
            };
            if (mv is Rectangle m2) { _sel = m2; BecameArea(); Layout2(); return true; }
            return false; // drawing tools and copy/save do not apply while choosing what to record
        }
        if (!_hasSel || _drag != Drag.None) return false;

        if (ctrl)
        {
            switch (code)
            {
                case Keys.C: CopyAndClose(); return true;
                case Keys.S: SaveAs(); return true;
                case Keys.P: PrintIt(); return true;
                case Keys.Z when shift: Redo(); return true;
                case Keys.Z: Undo(); return true;
                case Keys.Y: Redo(); return true;
                case Keys.A: _sel = CurrentScreen; Layout2(); Redraw(full: true); return true;
            }
        }

        int step = shift ? 10 : 1;
        Rectangle? moved = code switch
        {
            Keys.Left => ctrl ? Grow(-step, 0) : Nudge(-step, 0),
            Keys.Right => ctrl ? Grow(step, 0) : Nudge(step, 0),
            Keys.Up => ctrl ? Grow(0, -step) : Nudge(0, -step),
            Keys.Down => ctrl ? Grow(0, step) : Nudge(0, step),
            _ => null,
        };
        if (moved is Rectangle m) { _sel = m; Layout2(); return true; }

        if (ctrl || k.HasFlag(Keys.Alt)) return false;
        switch (code)
        {
            case Keys.P: SetTool(Tool.Pen); return true;
            case Keys.L: SetTool(Tool.Line); return true;
            case Keys.A: SetTool(Tool.Arrow); return true;
            case Keys.R: SetTool(Tool.Rect); return true;
            case Keys.M: SetTool(Tool.Marker); return true;
            case Keys.B: SetTool(Tool.Blur); return true;
            case Keys.N: SetTool(Tool.Step); return true;
            case Keys.OemOpenBrackets: PickSize(StepSize(-1)); return true;
            case Keys.OemCloseBrackets: PickSize(StepSize(+1)); return true;
            case Keys.T: SetTool(Tool.Text); return true;
        }
        if (code >= Keys.D1 && code < Keys.D1 + Theme.Ink.Length) { PickColor(Theme.Ink[code - Keys.D1]); return true; }
        if (code >= Keys.NumPad1 && code < Keys.NumPad1 + Theme.Ink.Length) { PickColor(Theme.Ink[code - Keys.NumPad1]); return true; }
        return false;
    }

    Rectangle Nudge(int dx, int dy) =>
        SelectionMath.ClampMove(SelectionMath.Resize(_sel, SelHandle.Move, dx, dy), new Rectangle(Point.Empty, _virt.Size));

    Rectangle Grow(int dx, int dy) =>
        Rectangle.Intersect(SelectionMath.Resize(_sel, SelHandle.SE, dx, dy), new Rectangle(Point.Empty, _virt.Size));

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_editing == null || char.IsControl(e.KeyChar)) return;
        _editing.Text.Append(e.KeyChar);
        _caretOn = true;
        e.Handled = true;
        Redraw();
    }

    // ---------- commands ----------

    void SetTool(Tool t)
    {
        if (_editing != null) CommitText();
        _tool = _tool == t ? Tool.None : t;
        Layout2();
        UpdateCursor(_mouse, HitButton(_mouse));
    }

    void PickColor(Color c)
    {
        _ink = c;
        if (_editing != null) _editing.Color = c;
        _paletteOpen = false;
        Layout2();
    }

    int StepSize(int dir)
    {
        if (dir > 0) { foreach (var v in InkSizes) if (v > _inkWidth) return v; return InkSizes[^1]; }
        for (int i = InkSizes.Length - 1; i >= 0; i--) if (InkSizes[i] < _inkWidth) return InkSizes[i];
        return InkSizes[0];
    }

    void PickSize(int v)
    {
        _inkWidth = Math.Clamp(v, (int)Theme.StrokeMinInk, (int)Theme.StrokeMaxInk);
        if (_editing != null) _editing.Width = _inkWidth;
        _widthShownUntil = DateTime.Now.AddSeconds(1.2);
        _paletteOpen = false;
        Layout2();
    }

    /// <summary>
    /// Window mode: put the picked window in front. Its own pixels replace whatever covered it
    /// in the frozen screenshot, it is raised in the real z-order, and it is activated when
    /// PRTSCN closes. Returns the area to select.
    /// </summary>
    Rectangle BringToFront(WindowInfo w)
    {
        _pickedWindow = w.Handle;
        if (w.Handle == 0) return w.Bounds;
        try
        {
            WindowList.RaiseBehindOverlay(w.Handle);
            using var pix = WindowList.CaptureUncovered(w.Handle, out var frame, out bool round);
            if (pix == null) return w.Bounds; // keep the frozen pixels
            var dest = frame;
            dest.Offset(-_virt.X, -_virt.Y);
            var desk = new Rectangle(Point.Empty, _virt.Size);
            var vis = Rectangle.Intersect(dest, desk);
            if (vis.IsEmpty) return w.Bounds;
            var src = new Rectangle(vis.X - dest.X, vis.Y - dest.Y, vis.Width, vis.Height);
            using var corners = round ? Theme.Round(new RectangleF(dest.X, dest.Y, dest.Width, dest.Height), 8 * ScaleOf(ToolbarLayout.ScreenFor(dest, _screens))) : null;
            foreach (var target in new[] { _shot, _dim })
            {
                using var g = Graphics.FromImage(target);
                if (corners != null) g.SetClip(corners);
                g.DrawImage(pix, vis, src, GraphicsUnit.Pixel);
                if (target == _dim)
                {
                    using var dimB = new SolidBrush(Theme.WithAlpha(Theme.Bg, Theme.AlphaDim));
                    g.FillRectangle(dimB, vis);
                }
            }
            return vis;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return w.Bounds;
        }
    }

    void MoreColors()
    {
        using var dlg = new ColorDialog { Color = _ink, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) PickColor(dlg.Color);
    }

    void CommitText()
    {
        if (_editing == null) return;
        if (!_editing.IsEmpty) { _items.Add(_editing); _redo.Clear(); }
        _editing = null;
        Redraw();
    }

    void CancelText()
    {
        _editing = null;
        Redraw();
    }

    void Undo()
    {
        if (_editing != null) { CancelText(); return; }
        if (_items.Count == 0) return;
        _redo.Add(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
        Redraw();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _items.Add(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Redraw();
    }

    void ResetSelection()
    {
        _hasSel = false;
        _items.Clear(); _redo.Clear();
        _current = null; _editing = null;
        _pickedWindow = 0;
        _recWindow = 0;
        _paletteOpen = false; _hover = null;
        _vbar = _hbar = _palBar = _tipRect = Rectangle.Empty;
        _hoverWin = WindowPicker.HitTest(_windows, _mouse);
        Redraw(full: true);
    }

    /// <summary>The selection with annotations flattened into it, at 1:1 physical pixels.</summary>
    Bitmap Render()
    {
        CommitText();
        var bmp = new Bitmap(_sel.Width, _sel.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(_shot, new Rectangle(0, 0, _sel.Width, _sel.Height), _sel, GraphicsUnit.Pixel);
        g.TranslateTransform(-_sel.X, -_sel.Y);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var a in _items) a.Draw(g);
        return bmp;
    }

    void StartScroll()
    {
        if (!_hasSel || _onScroll == null) return;
        var physical = _sel;
        physical.Offset(_virt.X, _virt.Y);
        _pickedWindow = 0;
        Hide();      // gone from the screen right away, before the first frame is grabbed
        Close();
        _onScroll(physical);
    }

    void CopyAndClose()
    {
        if (!_hasSel) return;
        using var bmp = Render();
        try
        {
            ImageOutput.CopyToClipboard(bmp);
            _notify("Copied", $"{bmp.Width} × {bmp.Height} screenshot is on the clipboard.", null);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _notify("Could not copy", "Another app is holding the clipboard. Try again.", null);
            return;
        }
        Close();
    }

    void SaveAs()
    {
        if (!_hasSel) return;
        using var bmp = Render();
        Directory.CreateDirectory(_settings.SaveFolder);
        using var dlg = new SaveFileDialog
        {
            Title = "Save screenshot",
            InitialDirectory = _settings.SaveFolder,
            FileName = FileNaming.DefaultName(DateTime.Now, _settings.Format),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FilterIndex = _settings.Format == Core.ImageFormat.Jpg ? 2 : 1,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var fmt = FileNaming.FormatFromPath(dlg.FileName, dlg.FilterIndex == 2 ? Core.ImageFormat.Jpg : Core.ImageFormat.Png);
        try
        {
            ImageOutput.Save(bmp, dlg.FileName, fmt, _settings.JpegQuality);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(this, $"Could not save the file:\n{ex.Message}", "PRTSCN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _notify("Saved", Path.GetFileName(dlg.FileName), dlg.FileName);
        Close();
    }

    void PrintIt()
    {
        if (!_hasSel) return;
        using var bmp = Render();
        try
        {
            if (ImageOutput.Print(bmp, this)) Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(this, $"Could not print:\n{ex.Message}", "PRTSCN", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
