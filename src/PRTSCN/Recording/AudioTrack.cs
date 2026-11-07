using NAudio.CoreAudioApi;
using NAudio.Wave;
using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>What to record for sound. Volumes are linear gains (1 = as is, up to 2).</summary>
internal sealed record AudioOptions(bool SystemSound, bool Microphone, string MicDeviceId, float SystemGain, float MicGain)
{
    public bool Any => SystemSound || Microphone;
}

/// <summary>An audio device in Options' device list.</summary>
internal sealed record AudioDevice(string Id, string Name);

/// <summary>
/// One WASAPI capture (computer sound by loopback, or a microphone), converted to 48 kHz stereo
/// float and placed on the recording timeline by the recording clock.
/// </summary>
internal sealed class AudioSource : IDisposable
{
    readonly IWaveIn _capture;
    readonly WaveFormat _format;
    readonly LinearResampler _resampler;
    readonly Func<TimeSpan> _clock;
    readonly Func<bool> _paused;
    readonly List<float> _resampled = new();
    float[] _stereo = Array.Empty<float>();

    public AudioTimeline Timeline { get; } = new(AudioMath.Rate);
    public string Name { get; }
    /// <summary>Gain applied when mixing. Settable while recording.</summary>
    public volatile float Gain;
    public volatile bool Muted;
    /// <summary>Peak level of the latest packet after gain, 0–1+ (for the meters).</summary>
    public volatile float Level;

    public AudioSource(bool loopback, string? deviceId, float gain, Func<TimeSpan> clock, Func<bool> paused)
    {
        _clock = clock;
        _paused = paused;
        Gain = gain;
        using var en = new MMDeviceEnumerator();
        MMDevice device = loopback
            ? FindDefault(en, DataFlow.Render, Role.Multimedia, Role.Console, Role.Communications)
                ?? throw new NoAudioDeviceException("no speakers or headphones are connected")
            : !string.IsNullOrEmpty(deviceId) && TryGet(en, deviceId) is { } picked ? picked
            : FindDefault(en, DataFlow.Capture, Role.Communications, Role.Console, Role.Multimedia)
                ?? throw new NoAudioDeviceException("no microphone is connected or enabled");
        Name = device.FriendlyName;
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 30);
        _format = _capture.WaveFormat;
        _resampler = new LinearResampler(_format.SampleRate, AudioMath.Rate);
        _capture.DataAvailable += OnData;
    }

    /// <summary>
    /// The default device for the first role Windows has one for, else any active device.
    /// A PC can have a microphone but no default "communications" device (then Windows answers
    /// "Element not found"), or a default that was unplugged a moment ago.
    /// </summary>
    static MMDevice? FindDefault(MMDeviceEnumerator en, DataFlow flow, params Role[] roles)
    {
        foreach (var role in roles)
        {
            try { if (en.HasDefaultAudioEndpoint(flow, role)) return en.GetDefaultAudioEndpoint(flow, role); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        try { return en.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(); }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>The microphone picked in Options, if it is still there and enabled.</summary>
    static MMDevice? TryGet(MMDeviceEnumerator en, string id)
    {
        try { var d = en.GetDevice(id); return d.State == DeviceState.Active ? d : null; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    public void Start() => _capture.StartRecording();

    void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (_paused() || e.BytesRecorded <= 0) return;
            int frames = ToStereo(e.Buffer, e.BytesRecorded);
            _resampled.Clear();
            _resampler.Process(_stereo, frames, _resampled);
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resampled);
            int outFrames = span.Length / 2;
            float g = Muted ? 0 : Gain, peak = 0;
            for (int i = 0; i < span.Length; i++) { float m = Math.Abs(span[i]) * g; if (m > peak) peak = m; }
            Level = peak;
            // The packet ends about now: its first frame belongs outFrames before the clock.
            long desired = AudioMath.FrameAt(_clock()) - outFrames;
            Timeline.Append(desired, span);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>Device bytes to interleaved stereo float in _stereo. Returns frames.</summary>
    int ToStereo(byte[] data, int bytes)
    {
        int ch = _format.Channels, bits = _format.BitsPerSample;
        int frames = bytes / _format.BlockAlign;
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        bool isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (_format is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        int bps = bits / 8;
        for (int f = 0; f < frames; f++)
        {
            int o = f * _format.BlockAlign;
            float l = Sample(data, o, bps, isFloat);
            float r = ch > 1 ? Sample(data, o + bps, bps, isFloat) : l;
            _stereo[f * 2] = l;
            _stereo[f * 2 + 1] = r;
        }
        return frames;
    }

    static float Sample(byte[] d, int o, int bps, bool isFloat)
    {
        if (isFloat) return bps == 8 ? (float)BitConverter.ToDouble(d, o) : BitConverter.ToSingle(d, o);
        return bps switch
        {
            2 => BitConverter.ToInt16(d, o) / 32768f,
            3 => ((d[o] << 8 | d[o + 1] << 16 | d[o + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(d, o) / 2147483648f,
            1 => (d[o] - 128) / 128f,
            _ => 0,
        };
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }

    public static IReadOnlyList<AudioDevice> Microphones()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch (Exception ex) { CrashLog.Write(ex); return Array.Empty<AudioDevice>(); }
    }
}

/// <summary>
/// The sound of a recording: computer sound and/or a microphone, mixed with their volumes and
/// written as AAC into the file. Runs a little behind the recording clock so every packet lands
/// at its true time, which keeps sound and picture in sync (pauses included).
/// </summary>
internal sealed class AudioTrack : IDisposable
{
    readonly IMediaSink _writer;
    readonly Func<TimeSpan> _clock;
    readonly Thread _thread;
    readonly float[] _mix = new float[AudioMath.Rate / 10 * 2];
    readonly short[] _pcm = new short[AudioMath.Rate / 10 * 2];
    volatile bool _stop;
    long _pos;

    public AudioSource? SystemSound { get; }
    public AudioSource? Mic { get; }
    /// <summary>Sources that could not be opened (no microphone, device busy).</summary>
    public List<string> Problems { get; } = new();
    public Exception? Error { get; private set; }

    public AudioTrack(AudioOptions opt, IMediaSink writer, Func<TimeSpan> clock, Func<bool> paused)
    {
        _writer = writer;
        _clock = clock;
        if (opt.SystemSound)
        {
            try { SystemSound = new AudioSource(true, null, opt.SystemGain, clock, paused); }
            catch (NoAudioDeviceException ex) { Problems.Add("computer sound (" + ex.Message + ")"); }
            catch (Exception ex) { CrashLog.Write(ex); Problems.Add("computer sound (" + ex.Message + ")"); }
        }
        if (opt.Microphone)
        {
            try { Mic = new AudioSource(false, opt.MicDeviceId, opt.MicGain, clock, paused); }
            catch (NoAudioDeviceException ex) { Problems.Add("microphone (" + ex.Message + ")"); }
            catch (Exception ex) { CrashLog.Write(ex); Problems.Add("microphone (" + ex.Message + ")"); }
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "PRTSCN audio", Priority = ThreadPriority.AboveNormal };
    }

    public void Start()
    {
        foreach (var s in Sources)
        {
            try { s.Start(); }
            catch (Exception ex) { CrashLog.Write(ex); Problems.Add(s.Name + " (" + ex.Message + ")"); }
        }
        _thread.Start();
    }

    IEnumerable<AudioSource> Sources => new[] { SystemSound, Mic }.Where(s => s != null)!;

    void Loop()
    {
        try
        {
            while (!_stop)
            {
                Pump(AudioMath.FrameAt(_clock()) - AudioMath.DelayFrames);
                Thread.Sleep(10);
            }
        }
        catch (Exception ex) { Error = ex; CrashLog.Write(ex); }
    }

    /// <summary>Mix and write everything up to frame <paramref name="target"/>.</summary>
    void Pump(long target)
    {
        int chunk = _mix.Length / 2;
        while (_pos < target)
        {
            int n = (int)Math.Min(chunk, target - _pos);
            var mix = _mix.AsSpan(0, n * 2);
            mix.Clear();
            foreach (var s in Sources) s.Timeline.ReadInto(_pos, n, mix, s.Muted ? 0 : s.Gain);
            AudioMath.ToPcm16(mix, _pcm.AsSpan(0, n * 2));
            _writer.WriteAudio(_pcm, n, AudioMath.Time(_pos), AudioMath.Time(_pos + n) - AudioMath.Time(_pos));
            _pos += n;
        }
    }

    /// <summary>Stop capturing and write the sound up to the end of the recording.</summary>
    public void Stop()
    {
        if (_stop) return;
        _stop = true;
        if (_thread.IsAlive) _thread.Join(3000);
        foreach (var s in Sources) { try { s.Dispose(); } catch { } }
        if (Error == null)
        {
            try { Pump(AudioMath.FrameAt(_clock())); }
            catch (Exception ex) { Error = ex; CrashLog.Write(ex); }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>No device to record from: a plain message for the user, not a crash for the log.</summary>
internal sealed class NoAudioDeviceException(string message) : Exception(message);
