using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>Where a recorder writes its frames and sound.</summary>
internal interface IMediaSink : IDisposable
{
    bool UsedHardware { get; }
    void WriteFrame(IntPtr src, int rowPitch, long time, long duration);
    void WriteAudio(short[] pcm, int frames, long time, long duration);
    void Finish();
}

/// <summary>
/// The instant-replay buffer: the recording goes into short MP4 segments in a temp folder, and only
/// the newest ones are kept. Saving cuts the current segment off, then the last N seconds are
/// joined (re-encoded) into one file.
/// </summary>
internal sealed class ReplayBuffer : IDisposable
{
    readonly string _folder;
    readonly long _segmentLength, _keep;
    readonly object _lock = new();
    readonly List<ReplaySegment> _done = new();
    Sink? _sink;
    int _holds;          // saves in progress: no segment is deleted meanwhile
    int _index;

    public ReplayBuffer(string folder, TimeSpan segment, TimeSpan keep)
    {
        _folder = folder;
        _segmentLength = segment.Ticks;
        _keep = keep.Ticks;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) { CrashLog.Write(ex); }
        Directory.CreateDirectory(folder);
    }

    /// <summary>Seconds of recording held right now (finished segments).</summary>
    public TimeSpan Held
    {
        get { lock (_lock) return _done.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_done[^1].End - _done[0].Start); }
    }

    public IMediaSink Open(int width, int height, int fps, int bitrate, bool hardware, bool sound) =>
        _sink = new Sink(this, width, height, fps, bitrate, hardware, sound);

    string NextPath() => Path.Combine(_folder, $"seg-{Interlocked.Increment(ref _index):D5}.mp4");

    void Finished(ReplaySegment s)
    {
        lock (_lock)
        {
            if (s.End > s.Start) _done.Add(s);
            else TryDelete(s.Path);
            if (_holds > 0) return;
            foreach (var old in ReplayPlan.Expired(_done, _keep))
            {
                _done.Remove(old);
                TryDelete(old.Path);
            }
        }
    }

    /// <summary>
    /// Close the current segment and return the parts holding the last <paramref name="length"/>.
    /// Call <see cref="Release"/> when done reading them. Runs off the UI thread (waits up to 5 s).
    /// </summary>
    public List<(string Path, TimeRange Range)> Take(TimeSpan length)
    {
        var sink = _sink ?? throw new InvalidOperationException("The replay is not running.");
        lock (_lock) _holds++;
        long upTo = sink.RequestCut();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            lock (_lock) if (_done.Count > 0 && _done[^1].End >= upTo) break;
            Thread.Sleep(20);
        }
        lock (_lock) return ReplayPlan.LastPart(_done, length.Ticks);
    }

    public void Release()
    {
        lock (_lock) _holds = Math.Max(0, _holds - 1);
        Finished(new ReplaySegment("", 0, 0)); // prune now
    }

    static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { CrashLog.Write(ex); }
    }

    public void Dispose()
    {
        _sink?.Dispose();
        lock (_lock) _done.Clear();
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>
    /// Writes into the current segment and starts a new one every few seconds (or when asked).
    /// Sound runs a little behind the picture, so the old segment stays open until the sound has
    /// passed its end too; then it is finished on a worker thread.
    /// </summary>
    sealed class Sink : IMediaSink
    {
        readonly ReplayBuffer _owner;
        readonly int _w, _h, _fps, _bitrate;
        readonly bool _hw, _sound;
        readonly object _lock = new();
        Mp4Writer? _cur, _old;
        string _curPath = "", _oldPath = "";
        long _curStart, _oldStart, _oldEnd, _lastVideo;
        volatile bool _cutRequested;
        bool _finished;

        public Sink(ReplayBuffer owner, int w, int h, int fps, int bitrate, bool hw, bool sound)
        {
            _owner = owner; _w = w; _h = h; _fps = fps; _bitrate = bitrate; _hw = hw; _sound = sound;
        }

        public bool UsedHardware { get; private set; }

        /// <summary>Ask for a new segment at the next frame; returns the recording time reached so far.</summary>
        public long RequestCut()
        {
            _cutRequested = true;
            lock (_lock) return _lastVideo;
        }

        Mp4Writer Create(out string path)
        {
            path = _owner.NextPath();
            var w = new Mp4Writer(path, _w, _h, _fps, _bitrate, _hw, _sound);
            UsedHardware = w.UsedHardware;
            return w;
        }

        public void WriteFrame(IntPtr src, int rowPitch, long time, long duration)
        {
            lock (_lock)
            {
                if (_finished) return;
                if (_cur == null) { _cur = Create(out _curPath); _curStart = time; }
                else if (time - _curStart >= _owner._segmentLength || _cutRequested)
                {
                    if (_old != null) CloseOld();
                    (_old, _oldPath, _oldStart, _oldEnd) = (_cur, _curPath, _curStart, time);
                    _cur = Create(out _curPath);
                    _curStart = time;
                    if (!_sound) CloseOld();
                }
                _cutRequested = false;
                _cur.WriteFrame(src, rowPitch, time - _curStart, duration);
                _lastVideo = time + duration;
            }
        }

        public void WriteAudio(short[] pcm, int frames, long time, long duration)
        {
            lock (_lock)
            {
                if (_finished) return;
                if (_old != null)
                {
                    if (time < _oldEnd) { _old.WriteAudio(pcm, frames, time - _oldStart, duration); return; }
                    CloseOld();
                }
                if (_cur != null && time >= _curStart) _cur.WriteAudio(pcm, frames, time - _curStart, duration);
            }
        }

        /// <summary>Finish the old segment on a worker thread (lock held).</summary>
        void CloseOld()
        {
            var (w, path, start, end) = (_old!, _oldPath, _oldStart, _oldEnd);
            _old = null;
            Task.Run(() =>
            {
                try { w.Dispose(); _owner.Finished(new ReplaySegment(path, start, end)); }
                catch (Exception ex) { CrashLog.Write(ex); TryDelete(path); }
            });
        }

        public void Finish()
        {
            lock (_lock)
            {
                if (_finished) return;
                _finished = true;
                try { _old?.Dispose(); } catch (Exception ex) { CrashLog.Write(ex); }
                try { _cur?.Dispose(); } catch (Exception ex) { CrashLog.Write(ex); }
                _old = _cur = null;
            }
        }

        public void Dispose() => Finish();
    }
}
