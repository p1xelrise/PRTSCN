using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PRTSCN.Core;

namespace PRTSCN.Editor;

/// <summary>
/// Plays a recording in the editor: frames decoded (on the graphics card when it can) at the
/// preview size on a worker thread, sound through WASAPI. Playback skips the trimmed and cut parts.
/// Frames reach the UI through <see cref="FrameReady"/>, which runs on the UI thread.
/// </summary>
internal sealed class Player : IDisposable
{
    const int AudioLatencyMs = 100;

    readonly string _path;
    readonly Control _ui;
    readonly bool _hasVideo, _hasAudio;
    readonly Thread _thread;
    readonly object _lock = new();
    Command? _cmd;
    bool _quit;

    MediaReader? _video;
    MediaReader? _audio;
    Size _viewSize = new(640, 360);
    long _shownTime = -1;
    Bitmap? _pending;          // frame waiting for the UI
    long _pendingTime;
    volatile bool _playing;
    long _position;

    /// <summary>A new frame for the player view (UI thread). The receiver owns the bitmap.</summary>
    public event Action<Bitmap, long>? FrameReady;
    /// <summary>Playback reached the end of the kept parts (UI thread).</summary>
    public event Action? Ended;

    public bool Playing => _playing;
    /// <summary>Where playback is (source time, 100 ns).</summary>
    public long Position => Interlocked.Read(ref _position);
    public Size SourceSize { get; }
    public double Fps { get; } = 30;
    public long Duration { get; }
    public bool HasVideo => _hasVideo;
    public bool HasAudio => _hasAudio;

    abstract record Command;
    sealed record SeekCmd(long Time) : Command;
    sealed record PlayCmd(long From, IReadOnlyList<TimeRange> Kept) : Command;
    sealed record PauseCmd : Command;
    sealed record ResizeCmd(Size Size) : Command;

    public Player(string path, Control ui)
    {
        _path = path;
        _ui = ui;
        (_hasVideo, _hasAudio) = MediaReader.Probe(path);
        if (!_hasVideo && !_hasAudio) throw new InvalidDataException("This file has no video or sound PRTSCN can read.");
        if (_hasVideo)
        {
            using var r = new MediaReader(path, video: true, hardware: false);
            SourceSize = r.SourceSize; Fps = r.Fps; Duration = r.Duration;
        }
        if (_hasAudio)
        {
            _audio = new MediaReader(path, video: false);
            Duration = Math.Max(Duration, _audio.Duration);
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "PRTSCN player" };
        _thread.Start();
    }

    // ---------- commands (UI thread) ----------

    public void Seek(long t) => Send(new SeekCmd(Math.Clamp(t, 0, Duration)));
    public void Play(long from, IReadOnlyList<TimeRange> kept)
    {
        _playing = true; // at once, so the UI shows "playing" before the worker starts
        Send(new PlayCmd(from, kept));
    }

    public void Pause()
    {
        _playing = false;
        Send(new PauseCmd());
    }
    public void SetViewSize(Size s)
    {
        if (s.Width < 16 || s.Height < 16) return;
        Send(new ResizeCmd(s));
    }

    void Send(Command c)
    {
        lock (_lock)
        {
            // a newer seek replaces an older one; a resize is never lost
            if (_cmd is ResizeCmd r && c is not ResizeCmd) { _cmd = c; _resize = r.Size; }
            else _cmd = c;
            Monitor.PulseAll(_lock);
        }
    }

    Size? _resize;

    bool HasCommand { get { lock (_lock) return _cmd != null || _quit; } }

    // ---------- worker ----------

    void Run()
    {
        try
        {
            while (true)
            {
                Command? c;
                Size? resize;
                lock (_lock)
                {
                    while (_cmd == null && !_quit) Monitor.Wait(_lock);
                    if (_quit) return;
                    c = _cmd; _cmd = null;
                    resize = _resize; _resize = null;
                }
                if (c is ResizeCmd rc) resize = rc.Size;
                if (resize is { } rs) Reopen(rs);
                switch (c)
                {
                    case SeekCmd s: _playing = false; ShowAt(s.Time); break;
                    case PlayCmd p: PlayLoop(p.From, p.Kept); break;
                    case PauseCmd: _playing = false; break;
                    case ResizeCmd: if (_shownTime >= 0 && !_playing) ShowAt(_shownTime); break;
                }
            }
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    void Reopen(Size view)
    {
        if (!_hasVideo) return;
        var fit = Fit(SourceSize, view);
        if (_video != null && _video.Width == fit.Width && _video.Height == fit.Height) return;
        _video?.Dispose();
        _video = new MediaReader(_path, video: true, _ => fit);
        _viewSize = view;
    }

    /// <summary>The largest size inside the view with the video's shape (never larger than the video).</summary>
    public static Size Fit(Size src, Size view)
    {
        double k = Math.Min(1, Math.Min(view.Width / (double)src.Width, view.Height / (double)src.Height));
        return new Size(Math.Max(2, (int)(src.Width * k) & ~1), Math.Max(2, (int)(src.Height * k) & ~1));
    }

    long FrameTicks => (long)(10_000_000 / Fps);

    /// <summary>Show the frame on screen at time t (paused).</summary>
    void ShowAt(long t)
    {
        Interlocked.Exchange(ref _position, t);
        if (_video == null) { Reopen(_viewSize); if (_video == null) return; }
        var buf = new byte[_video.Width * _video.Height * 4];
        var prev = new byte[buf.Length];
        bool havePrev = false;
        _video.Seek(t);
        long half = FrameTicks / 2;
        while (_video.ReadFrame(buf, out long ft))
        {
            if (ft >= t - half)
            {
                if (ft > t + half && havePrev) Post(prev, _shownTime);
                else Post(buf, ft);
                return;
            }
            (buf, prev) = (prev, buf);
            havePrev = true;
            _shownTime = ft;
            if (HasCommand) return; // a newer seek is waiting: don't finish this one
        }
        if (havePrev) Post(prev, _shownTime);
    }

    void PlayLoop(long from, IReadOnlyList<TimeRange> kept)
    {
        if (kept.Count == 0) return;
        var edits = kept;
        long start = from;
        int seg = edits.ToList().FindIndex(k => start < k.End);
        if (seg < 0 || from >= edits[^1].End) { seg = 0; start = edits[0].Start; }
        if (start < edits[seg].Start) start = edits[seg].Start;

        _playing = true;
        long start0 = start;
        int seg0 = seg;
        WasapiOut? output = null;
        BufferedWaveProvider? sound = null;
        try
        {
            byte[]? frame = null;
            if (_video == null && _hasVideo) Reopen(_viewSize);
            if (_video != null)
            {
                frame = new byte[_video.Width * _video.Height * 4];
                _video.Seek(start);
            }
            bool haveFrame = false;
            long frameTime = 0;

            if (_audio != null)
            {
                _audio.Seek(start);
                sound = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(_audio.AudioRate, _audio.AudioChannels))
                {
                    BufferDuration = TimeSpan.FromSeconds(3),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                try
                {
                    output = new WasapiOut(AudioClientShareMode.Shared, true, AudioLatencyMs);
                    output.Init(sound);
                }
                catch (Exception ex) { CrashLog.Write(ex); output?.Dispose(); output = null; sound = null; }
            }

            // Clock: media time = base + elapsed. Each jump over a removed part moves base forward.
            long clockBase = start;
            int audioSeg = seg;
            bool audioDone = sound == null;
            void FillSound(long upTo)
            {
                while (!audioDone && sound!.BufferedDuration < TimeSpan.FromMilliseconds(400))
                {
                    var r = edits[audioSeg];
                    var pcm = _audio!.ReadAudio(out long at, out int frames);
                    if (pcm == null)
                    {
                        if (++audioSeg >= edits.Count) { audioDone = true; break; }
                        _audio.Seek(edits[audioSeg].Start);
                        continue;
                    }
                    int ch = _audio.AudioChannels, rate = _audio.AudioRate;
                    long first = Math.Max(0, (r.Start - at) * rate / 10_000_000);
                    if (audioSeg == seg0 && at < start0) first = Math.Max(first, (start0 - at) * rate / 10_000_000);
                    long last = Math.Min(frames, (r.End - at) * rate / 10_000_000);
                    if (last > first)
                    {
                        var f = new float[(last - first) * ch];
                        for (int i = 0; i < f.Length; i++) f[i] = pcm[first * ch + i] / 32768f;
                        var bytes = new byte[f.Length * 4];
                        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
                        sound.AddSamples(bytes, 0, bytes.Length);
                    }
                    if (at + (long)frames * 10_000_000 / rate >= r.End)
                    {
                        if (++audioSeg >= edits.Count) { audioDone = true; break; }
                        _audio.Seek(edits[audioSeg].Start);
                    }
                }
            }

            bool NextFrame()
            {
                while (_video!.ReadFrame(frame!, out long t))
                {
                    if (t < start - FrameTicks / 2) continue; // before the play point (decoded from the key frame)
                    if (t >= edits[seg].End)
                    {
                        // jump over the removed part
                        if (seg + 1 >= edits.Count) return false;
                        clockBase += edits[seg + 1].Start - edits[seg].End;
                        seg++;
                        start = edits[seg].Start;
                        _video.Seek(start);
                        continue;
                    }
                    if (t < edits[seg].Start - FrameTicks / 2) continue;
                    frameTime = t;
                    return true;
                }
                return false;
            }

            if (frame != null) haveFrame = NextFrame();
            FillSound(0);
            var sw = Stopwatch.StartNew();
            long latency = 0;
            if (output != null) { output.Play(); latency = AudioLatencyMs * 10_000L; }
            long endOfAll = edits[^1].End;

            while (!HasCommand)
            {
                FillSound(0);
                long now = clockBase + sw.Elapsed.Ticks - latency; // media time being heard / seen now
                if (haveFrame)
                {
                    // the clock base can move while decoding: compare in the frame's own timeline
                    if (frameTime <= now)
                    {
                        Post(frame!, frameTime);
                        Interlocked.Exchange(ref _position, frameTime);
                        haveFrame = NextFrame();
                        continue;
                    }
                    Interlocked.Exchange(ref _position, Math.Max(Position, Math.Min(now, frameTime)));
                    Thread.Sleep((int)Math.Clamp((frameTime - now) / 10_000, 1, 10));
                    continue;
                }
                // no more frames, or sound only: run the clock to the end of the kept parts
                long pos = frame != null ? endOfAll : MapSoundOnly(edits, start0, sw.Elapsed.Ticks - latency);
                Interlocked.Exchange(ref _position, Math.Min(pos, endOfAll));
                if (pos >= endOfAll)
                {
                    _playing = false;
                    _ui.BeginInvoke(() => Ended?.Invoke());
                    break;
                }
                Thread.Sleep(10);
            }
        }
        finally
        {
            _playing = false;
            output?.Stop();
            output?.Dispose();
        }
    }

    /// <summary>Source time after playing <paramref name="elapsed"/> ticks from <paramref name="start"/> through the kept parts.</summary>
    static long MapSoundOnly(IReadOnlyList<TimeRange> kept, long start, long elapsed)
    {
        long left = Math.Max(0, elapsed);
        bool begun = false;
        foreach (var k in kept)
        {
            if (!begun) { if (start >= k.End) continue; begun = true; long len = k.End - Math.Max(start, k.Start); if (left < len) return Math.Max(start, k.Start) + left; left -= len; continue; }
            if (left < k.Length) return k.Start + left;
            left -= k.Length;
        }
        return kept[^1].End;
    }

    void Post(byte[] bgra, long time)
    {
        if (_video == null) return;
        int w = _video.Width, h = _video.Height;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        bmp.UnlockBits(d);
        _shownTime = time;
        Interlocked.Exchange(ref _pendingTime, time);
        var old = Interlocked.Exchange(ref _pending, bmp);
        if (old != null) { old.Dispose(); return; } // the UI has not taken the last one yet: it will take this one
        try
        {
            _ui.BeginInvoke(() =>
            {
                var b = Interlocked.Exchange(ref _pending, null);
                if (b != null) FrameReady?.Invoke(b, Interlocked.Read(ref _pendingTime));
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _pending, null)?.Dispose(); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_quit) return;
            _quit = true;
            Monitor.PulseAll(_lock);
        }
        _thread.Join(2000);
        _video?.Dispose();
        _audio?.Dispose();
        Interlocked.Exchange(ref _pending, null)?.Dispose();
    }
}
