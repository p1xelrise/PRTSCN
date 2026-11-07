using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PRTSCN.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using Box = Vortice.Mathematics.Box;
using Size = System.Drawing.Size;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace PRTSCN.Recording;

internal enum RecordKind { Area, Window, Screen, Audio, Follow, AllScreens }

/// <summary>What to record. Bounds are physical screen pixels (for Follow: the view size and start).</summary>
internal sealed record RecordTarget(RecordKind Kind, Rectangle Bounds, IntPtr Window = default)
{
    public string Describe() => Kind switch
    {
        RecordKind.Window => "window",
        RecordKind.Screen => "screen",
        RecordKind.AllScreens => "all screens",
        RecordKind.Audio => "sound",
        RecordKind.Follow => $"{Bounds.Width} × {Bounds.Height} view (follows the mouse)",
        _ => $"{Bounds.Width} × {Bounds.Height} area",
    };
}

/// <summary>Drawn over the video: click ripples, keystrokes, spotlight, magnifier.</summary>
internal sealed record RecordEffects(bool Clicks, KeyDisplay Keys, bool Spotlight, bool Magnifier, float Zoom, float AutoZoom = 0)
{
    public static readonly RecordEffects None = new(false, KeyDisplay.Off, false, false, 2);
}

internal sealed record RecorderOptions(string Path, int Fps, VideoQuality Quality, bool ShowCursor, bool AllowHardware,
    AudioOptions? Audio = null, RecordEffects? Effects = null, Watermark? Watermark = null, ReplayBuffer? Replay = null);

/// <summary>
/// Records a window, a monitor, part of a monitor, a view that follows the mouse, or every monitor to MP4.
/// Windows.Graphics.Capture delivers frames on the GPU whenever the content changes; each capture
/// (one per monitor for "all screens") is copied into a source texture. A writer thread then, at a
/// constant frame rate, crops the view out of the source, draws the overlays and encodes the frame,
/// so a still screen still produces a smooth, seekable video and ripples animate smoothly.
/// Our own recording chrome is excluded from capture (WDA_EXCLUDEFROMCAPTURE), so it never shows up.
/// </summary>
internal sealed class ScreenRecorder : IRecorder
{
    sealed class Slot
    {
        public required GraphicsCaptureItem Item;
        public required Direct3D11CaptureFramePool Pool;
        public required GraphicsCaptureSession Session;
        public Point Offset;          // where its frames go in the source texture
        public SizeInt32 PoolSize;
    }

    readonly RecordTarget _target;
    readonly RecorderOptions _opt;
    readonly RecordEffects _fx;
    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _ctx;
    readonly IDirect3DDevice _rtDevice;
    readonly List<Slot> _slots = new();
    readonly ID3D11Texture2D _source, _canvas, _output, _staging;
    readonly ID3D11RenderTargetView _sourceView;
    readonly FrameCompositor _compositor;
    readonly Size _sourceSize, _viewSize;
    readonly float _scale;              // canvas to output
    readonly FollowCamera? _camera;
    readonly InputTracker? _input;
    readonly ZoomCamera? _zoom;
    int _clicksSeen;
    readonly IMediaSink _writer;
    readonly AudioTrack? _audio;
    readonly Thread _thread;
    readonly object _gpu = new();
    readonly Stopwatch _active = new(); // runs only while recording and not paused
    readonly ManualResetEventSlim _firstFrame = new(false);
    Point _sourceOrigin;                // screen position of the source's top-left
    Rectangle _view;                    // the recorded part of the source
    volatile bool _stop, _paused, _closed;
    Exception? _error;

    public Size FrameSize { get; }
    public string FilePath => _opt.Path;
    public bool UsedHardware => _writer.UsedHardware;
    public bool Paused => _paused;
    public TimeSpan Elapsed => _active.Elapsed;
    public long FramesWritten { get; private set; }
    /// <summary>The captured window was closed.</summary>
    public bool SourceClosed => _closed;
    /// <summary>Set when the writer thread failed; the recording stopped.</summary>
    public Exception? Error => _error ?? _audio?.Error;
    public AudioTrack? Audio => _audio;
    public event Action? Failed;

    /// <summary>
    /// Drawings to put into the video (screen pixels). Only needed for a window recording: the
    /// capture of a window does not include the drawing layer floating above it.
    /// </summary>
    public Func<DrawLayer.StrokeCopy[]>? Strokes { get; set; }

    /// <summary>What is being recorded right now, in screen pixels (moves for Follow and Window).</summary>
    public Rectangle CurrentScreenArea
    {
        get { lock (_gpu) { var v = _view; v.Offset(_sourceOrigin); return v; } }
    }

    public ScreenRecorder(RecordTarget target, RecorderOptions opt)
    {
        _target = target;
        _opt = opt;
        _fx = opt.Effects ?? RecordEffects.None;
        (_device, _ctx) = CaptureInterop.CreateDevice();
        _rtDevice = CaptureInterop.CreateWinRtDevice(_device);

        // ---- what feeds the source texture, and which part of it is recorded ----
        switch (target.Kind)
        {
            case RecordKind.Window:
            {
                var item = CaptureInterop.ForWindow(target.Window);
                AddSlot(item, Point.Empty);
                _sourceSize = RecordingMath.EvenSize(new Size(item.Size.Width, item.Size.Height));
                _sourceOrigin = WindowOrigin() ?? target.Bounds.Location;
                _view = new Rectangle(Point.Empty, _sourceSize);
                break;
            }
            case RecordKind.AllScreens:
            {
                var virt = SystemInformation.VirtualScreen;
                _sourceOrigin = virt.Location;
                _sourceSize = RecordingMath.EvenSize(virt.Size);
                foreach (var s in Screen.AllScreens)
                {
                    var mon = MonitorAt(s.Bounds, out var monRect);
                    AddSlot(CaptureInterop.ForMonitor(mon), new Point(monRect.X - virt.X, monRect.Y - virt.Y));
                }
                _view = new Rectangle(Point.Empty, _sourceSize);
                break;
            }
            default:
            {
                var mon = MonitorAt(target.Bounds, out var monRect);
                AddSlot(CaptureInterop.ForMonitor(mon), Point.Empty);
                _sourceOrigin = monRect.Location;
                _sourceSize = monRect.Size;
                if (target.Kind == RecordKind.Screen) _view = new Rectangle(Point.Empty, RecordingMath.EvenSize(monRect.Size));
                else
                {
                    _view = RecordingMath.CropInMonitor(target.Bounds, monRect);
                    if (_view.IsEmpty) throw new InvalidOperationException("The area is not on a screen.");
                }
                if (target.Kind == RecordKind.Follow)
                    _camera = new FollowCamera(_view, new Rectangle(Point.Empty, RecordingMath.EvenSize(monRect.Size)));
                break;
            }
        }
        _viewSize = _view.Size;

        // Wider than encoders allow (5120 × 1440 monitors, all screens): scale down to fit.
        var outSize = RecordingMath.FitWithin(_viewSize);
        FrameSize = outSize;
        _scale = outSize.Width / (float)_viewSize.Width;

        var rt = BindFlags.RenderTarget | BindFlags.ShaderResource;
        _source = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_sourceSize.Width, (uint)_sourceSize.Height, 1, 1, rt, ResourceUsage.Default));
        _sourceView = _device.CreateRenderTargetView(_source);
        _ctx.ClearRenderTargetView(_sourceView, new Color4(0, 0, 0, 1));
        _canvas = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_viewSize.Width, (uint)_viewSize.Height, 1, 1, rt, ResourceUsage.Default));
        _output = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)outSize.Width, (uint)outSize.Height, 1, 1, rt, ResourceUsage.Default));
        _staging = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)outSize.Width, (uint)outSize.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _compositor = new FrameCompositor(_device, _canvas, _output, opt.Watermark);

        int bitrate = RecordingMath.Bitrate(outSize, opt.Fps, opt.Quality);
        bool sound = opt.Audio?.Any == true;
        if (opt.Replay != null) _writer = opt.Replay.Open(outSize.Width, outSize.Height, opt.Fps, bitrate, opt.AllowHardware, sound);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(opt.Path)!);
            _writer = new Mp4Writer(opt.Path, outSize.Width, outSize.Height, opt.Fps, bitrate, opt.AllowHardware, sound);
        }
        if (sound) _audio = new AudioTrack(opt.Audio!, _writer, () => _active.Elapsed, () => _paused);
        if (_fx.AutoZoom > 1) _zoom = new ZoomCamera(new Rectangle(Point.Empty, _viewSize), _fx.AutoZoom);
        if (_fx.Clicks || _zoom != null || _fx.Keys != KeyDisplay.Off) _input = new InputTracker(_fx.Clicks || _zoom != null, _fx.Keys);

        _thread = new Thread(WriterLoop) { IsBackground = true, Name = "PRTSCN recorder", Priority = ThreadPriority.AboveNormal };
    }

    void AddSlot(GraphicsCaptureItem item, Point offset)
    {
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var slot = new Slot { Item = item, Pool = pool, Session = pool.CreateCaptureSession(item), Offset = offset, PoolSize = item.Size };
        pool.FrameArrived += (sender, _) => OnFrame(slot, sender);
        if (_target.Kind == RecordKind.Window) item.Closed += (_, _) => _closed = true;
        const string sessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
        if (ApiInformation.IsPropertyPresent(sessionType, "IsCursorCaptureEnabled"))
            slot.Session.IsCursorCaptureEnabled = _opt.ShowCursor;
        if (ApiInformation.IsPropertyPresent(sessionType, "IsBorderRequired"))
        {
            try { slot.Session.IsBorderRequired = false; } catch { /* Windows 10: the yellow border stays */ }
        }
        _slots.Add(slot);
    }

    /// <summary>Start capturing. Returns once the first frame arrived (or after 1.5 s).</summary>
    public void Start()
    {
        foreach (var s in _slots) s.Session.StartCapture();
        _firstFrame.Wait(1500);
        _active.Start();
        _audio?.Start();
        _thread.Start();
    }

    public void Pause() { if (!_paused) { _paused = true; _active.Stop(); } }
    public void Resume() { if (_paused) { _paused = false; _active.Start(); } }

    /// <summary>Stop and finish the file. Safe to call twice.</summary>
    public void Stop()
    {
        if (_stop) return;
        _stop = true;
        _active.Stop();
        foreach (var s in _slots) { try { s.Session.Dispose(); } catch { } }
        if (_thread.IsAlive) _thread.Join(5000);
        _audio?.Stop();
        _input?.Dispose();
        _writer.Finish();
    }

    /// <summary>Developer check: act as if the left button was clicked here (screen pixels). Repeats so the zoom holds.</summary>
    internal void SimulateClick(Point screen)
    {
        lock (_gpu) _zoom?.Click(new Point(screen.X - _sourceOrigin.X - _view.X, screen.Y - _sourceOrigin.Y - _view.Y));
        _simulated = screen;
    }
    Point? _simulated;

    public long FileBytes
    {
        get { try { return new FileInfo(_opt.Path).Length; } catch { return 0; } }
    }

    /// <summary>Compose a frame now and return it as a bitmap (exactly what goes into the video).</summary>
    public unsafe Bitmap Snapshot()
    {
        var bmp = new Bitmap(FrameSize.Width, FrameSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        lock (_gpu)
        {
            ComposeLocked(0);
            _ctx.CopyResource(_staging, _output);
            var map = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var bd = bmp.LockBits(new Rectangle(Point.Empty, FrameSize), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                for (int y = 0; y < FrameSize.Height; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, (byte*)bd.Scan0 + (long)y * bd.Stride, bd.Stride, FrameSize.Width * 4);
            }
            finally { bmp.UnlockBits(bd); _ctx.Unmap(_staging, 0); }
        }
        return bmp;
    }

    void OnFrame(Slot slot, Direct3D11CaptureFramePool sender)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var content = frame.ContentSize;
            using (var tex = CaptureInterop.TextureOf(frame.Surface))
            lock (_gpu)
            {
                var desc = tex.Description;
                // What of this frame lands in the source: all of it, clipped to the source.
                var src = new Rectangle(0, 0, Math.Min(content.Width, (int)desc.Width), Math.Min(content.Height, (int)desc.Height));
                src = Rectangle.Intersect(src, new Rectangle(-slot.Offset.X, -slot.Offset.Y, _sourceSize.Width, _sourceSize.Height));
                if (_target.Kind == RecordKind.Window && (content.Width < _sourceSize.Width || content.Height < _sourceSize.Height))
                    _ctx.ClearRenderTargetView(_sourceView, new Color4(0, 0, 0, 1)); // window got smaller: black around it
                if (src.Width > 0 && src.Height > 0)
                    _ctx.CopySubresourceRegion(_source, 0, (uint)(slot.Offset.X + src.X), (uint)(slot.Offset.Y + src.Y), 0, tex, 0,
                        new Box(src.Left, src.Top, 0, src.Right, src.Bottom, 1));
            }
            if (content.Width != slot.PoolSize.Width || content.Height != slot.PoolSize.Height)
            {
                slot.PoolSize = content;
                sender.Recreate(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
            }
            _firstFrame.Set();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>Crop the view out of the source and draw the overlays into the output (GPU lock held).</summary>
    void ComposeLocked(double seconds)
    {
        Native.GetCursorPos(out var cursor);
        var pointer = new Point(cursor.X, cursor.Y);
        if (_target.Kind == RecordKind.Window && WindowOrigin() is Point origin) _sourceOrigin = origin;
        if (_camera != null) _view = _camera.Update(new Point(pointer.X - _sourceOrigin.X, pointer.Y - _sourceOrigin.Y), seconds);

        _ctx.CopySubresourceRegion(_canvas, 0, 0, 0, 0, _source, 0, new Box(_view.Left, _view.Top, 0, _view.Right, _view.Bottom, 1));

        // canvas pixels: the recorded view's own coordinates
        Point ToCanvas(Point screen) => new(screen.X - _sourceOrigin.X - _view.X, screen.Y - _sourceOrigin.Y - _view.Y);
        RectangleF? zoom = null;
        if (_zoom != null && _input != null)
        {
            int n = _input.ClickCount;
            if (n != _clicksSeen) { _clicksSeen = n; if (_input.LastClick is Point lc) _zoom.Click(ToCanvas(lc)); }
            if (_simulated is Point sim) _zoom.Click(ToCanvas(sim));
            var z = _zoom.Update(ToCanvas(_simulated ?? pointer), seconds);
            if (_zoom.Zoomed) zoom = z;
        }
        // canvas to output: the zoomed part fills the frame
        var zr = zoom ?? new RectangleF(0, 0, _viewSize.Width, _viewSize.Height);
        float k = FrameSize.Width / zr.Width;
        Vector2 ToOutput(Point screen) { var c = ToCanvas(screen); return new Vector2((c.X - zr.X) * k, (c.Y - zr.Y) * k); }

        OverlayFrame? o = null;
        bool wantPointer = _fx.Spotlight || _fx.Magnifier || _input != null;
        var strokes = Strokes?.Invoke();
        if (strokes is { Length: > 0 })
        {
            o = new OverlayFrame { Scale = k, Zoom = zoom };
            float ox = _sourceOrigin.X + _view.X + zr.X, oy = _sourceOrigin.Y + _view.Y + zr.Y;
            foreach (var s in strokes)
            {
                var pts = new Vector2[s.Points.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2((s.Points[i].X - ox) * k, (s.Points[i].Y - oy) * k);
                o.Strokes.Add((new Color4(s.Color.R / 255f, s.Color.G / 255f, s.Color.B / 255f, s.Color.A / 255f), s.Width * k, pts));
            }
        }
        if (wantPointer || zoom != null)
        {
            o ??= new OverlayFrame();
            o.Scale = k; o.Spotlight = _fx.Spotlight; o.Magnifier = _fx.Magnifier; o.Zoom2 = _fx.Zoom; o.Zoom = zoom;
            var p = ToOutput(pointer);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X < FrameSize.Width && p.Y < FrameSize.Height;
            if (inside) o.Pointer = p;
            if (_input != null)
            {
                o.LeftDown = _fx.Clicks && _input.LeftDown;
                o.RightDown = _fx.Clicks && _input.RightDown;
                if (_fx.Clicks)
                    foreach (var c in _input.RecentClicks()) o.Clicks.Add((ToOutput(c.Pos), c.Right, c.Age));
                o.Keys = _input.VisibleKeys();
            }
        }
        _compositor.Compose(o);
    }

    void WriterLoop()
    {
        long n = 0;
        long duration = RecordingMath.FrameTime(1, _opt.Fps);
        double frameSeconds = 1.0 / _opt.Fps;
        try
        {
            while (!_stop)
            {
                long due = RecordingMath.FrameDue(_active.Elapsed, _opt.Fps);
                if (_paused || n > due)
                {
                    Thread.Sleep(2);
                    continue;
                }
                if (due - n > 2) n = due; // fell behind: skip frames instead of drifting
                long time = RecordingMath.FrameTime(n, _opt.Fps);
                lock (_gpu)
                {
                    ComposeLocked(frameSeconds);
                    _ctx.CopyResource(_staging, _output);
                    var map = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try { _writer.WriteFrame(map.DataPointer, (int)map.RowPitch, time, duration); }
                    finally { _ctx.Unmap(_staging, 0); }
                }
                n++;
                FramesWritten = n;
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            CrashLog.Write(ex);
            _stop = true;
            Failed?.Invoke();
        }
    }

    // ---------- windows and monitors ----------

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO mi);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    /// <summary>The captured window's top-left on screen (its visible frame, as the capture sees it).</summary>
    Point? WindowOrigin() =>
        DwmGetWindowAttribute(_target.Window, 9 /* EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) == 0 ? new Point(r.L, r.T) : null;

    /// <summary>The monitor holding most of the rectangle, and its bounds in physical pixels.</summary>
    static IntPtr MonitorAt(Rectangle r, out Rectangle bounds)
    {
        var rc = new RECT { L = r.Left, T = r.Top, R = r.Right, B = r.Bottom };
        var mon = MonitorFromRect(ref rc, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        bounds = Rectangle.FromLTRB(mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R, mi.rcMonitor.B);
        return mon;
    }

    public void Dispose()
    {
        try { Stop(); } catch (Exception ex) { CrashLog.Write(ex); }
        foreach (var s in _slots) { try { s.Pool.Dispose(); } catch { } }
        _audio?.Dispose();
        _writer.Dispose();
        _compositor.Dispose();
        _staging.Dispose();
        _output.Dispose();
        _canvas.Dispose();
        _sourceView.Dispose();
        _source.Dispose();
        _ctx.Dispose();
        _device.Dispose();
        _firstFrame.Dispose();
    }
}
