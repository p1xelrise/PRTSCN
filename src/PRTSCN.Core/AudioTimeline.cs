namespace PRTSCN.Core;

/// <summary>
/// Converts interleaved stereo float audio from a device rate to the recording rate (48 kHz)
/// by linear interpolation. Keeps its position between packets, so packet edges do not click.
/// </summary>
public sealed class LinearResampler
{
    readonly double _step;      // input frames per output frame
    double _t;                  // position of the next output frame; -1 = the remembered previous frame
    float _prevL, _prevR;
    bool _primed;

    public int InRate { get; }
    public int OutRate { get; }

    public LinearResampler(int inRate, int outRate)
    {
        InRate = inRate; OutRate = outRate;
        _step = (double)inRate / outRate;
    }

    /// <summary>Resample <paramref name="frames"/> stereo frames; appends output frames to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, int frames, List<float> output)
    {
        if (frames <= 0) return;
        if (InRate == OutRate)
        {
            for (int i = 0; i < frames * 2; i++) output.Add(input[i]);
            return;
        }
        if (!_primed) { _prevL = input[0]; _prevR = input[1]; _t = 0; _primed = true; }
        // sample k of the virtual sequence: k = -1 is the previous packet's last frame
        while (_t < frames - 1)
        {
            int i = (int)Math.Floor(_t);
            float f = (float)(_t - i);
            float l0, r0;
            if (i < 0) { l0 = _prevL; r0 = _prevR; }
            else { l0 = input[i * 2]; r0 = input[i * 2 + 1]; }
            float l1 = input[(i + 1) * 2], r1 = input[(i + 1) * 2 + 1];
            output.Add(l0 + (l1 - l0) * f);
            output.Add(r0 + (r1 - r0) * f);
            _t += _step;
        }
        _t -= frames;
        _prevL = input[(frames - 1) * 2];
        _prevR = input[(frames - 1) * 2 + 1];
    }
}

/// <summary>
/// One audio source's frames on the recording's timeline (absolute frame numbers at the recording rate).
/// The capture thread appends packets where they belong in time; the mixer reads a range and drops it.
/// Gaps (a silent loopback device sends nothing) read as silence, so sources stay in sync with the video.
/// Thread-safe.
/// </summary>
public sealed class AudioTimeline
{
    readonly float[] _buf;      // stereo ring buffer
    readonly int _capacity;     // frames
    readonly int _gapFrames, _aheadFrames;
    readonly object _lock = new();
    long _start, _end;          // stored frames are [_start, _end)

    public AudioTimeline(int rate, double seconds = 4)
    {
        _capacity = (int)(rate * seconds);
        _buf = new float[_capacity * 2];
        _gapFrames = rate * 30 / 1000;
        _aheadFrames = rate * 150 / 1000;
    }

    public long End { get { lock (_lock) return _end; } }
    public long Start { get { lock (_lock) return _start; } }

    /// <summary>
    /// Add a packet whose first frame belongs at <paramref name="desiredStart"/> (from the recording clock).
    /// Small timing jitter is ignored (the packet goes right after the previous one); a real gap is
    /// filled with silence; a packet far ahead of the clock (device running fast) is dropped.
    /// Returns false when dropped.
    /// </summary>
    public bool Append(long desiredStart, ReadOnlySpan<float> frames)
    {
        int n = frames.Length / 2;
        lock (_lock)
        {
            if (_end > desiredStart + _aheadFrames) return false;
            if (desiredStart > _end + _gapFrames)
            {
                long silence = Math.Min(desiredStart - _end, _capacity);
                for (long i = 0; i < silence; i++) Put(_end + i, 0, 0);
                _end += silence;
                Trim();
            }
            for (int i = 0; i < n; i++) Put(_end + i, frames[i * 2], frames[i * 2 + 1]);
            _end += n;
            Trim();
            return true;
        }
    }

    void Put(long abs, float l, float r)
    {
        int k = (int)(abs % _capacity) * 2;
        _buf[k] = l; _buf[k + 1] = r;
    }

    void Trim()
    {
        if (_end - _start > _capacity) _start = _end - _capacity; // overflow: forget the oldest
    }

    /// <summary>
    /// Mix frames [from, from + count) into <paramref name="mix"/> with a gain, then forget them.
    /// Frames the source does not have are silence. Returns the peak level added (after gain).
    /// </summary>
    public float ReadInto(long from, int count, Span<float> mix, float gain)
    {
        float peak = 0;
        lock (_lock)
        {
            long a = Math.Max(from, _start), b = Math.Min(from + count, _end);
            for (long i = a; i < b; i++)
            {
                int k = (int)(i % _capacity) * 2, o = (int)(i - from) * 2;
                float l = _buf[k] * gain, r = _buf[k + 1] * gain;
                mix[o] += l; mix[o + 1] += r;
                float m = Math.Max(Math.Abs(l), Math.Abs(r));
                if (m > peak) peak = m;
            }
            _start = Math.Max(_start, from + count);
            if (_end < _start) _end = _start; // a late source continues from the read position
        }
        return peak;
    }
}

public static class AudioMath
{
    public const int Rate = 48000;
    public const int Channels = 2;
    /// <summary>The mixer runs this far behind the clock, so late packets still land in time.</summary>
    public const int DelayFrames = Rate / 10;

    /// <summary>Recording-clock time to an absolute frame number.</summary>
    public static long FrameAt(TimeSpan t) => t.Ticks * Rate / TimeSpan.TicksPerSecond;

    /// <summary>Frame number to Media Foundation time (100 ns units).</summary>
    public static long Time(long frame) => frame * TimeSpan.TicksPerSecond / Rate;

    /// <summary>Float to 16-bit PCM with clipping.</summary>
    public static void ToPcm16(ReadOnlySpan<float> src, Span<short> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float v = Math.Clamp(src[i], -1f, 1f);
            dst[i] = (short)Math.Round(v * 32767f);
        }
    }

    /// <summary>Percent volume (0–200) to a linear gain.</summary>
    public static float Gain(int percent) => Math.Clamp(percent, 0, 200) / 100f;
}
