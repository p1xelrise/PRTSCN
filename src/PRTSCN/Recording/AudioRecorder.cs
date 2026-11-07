using System.Diagnostics;

namespace PRTSCN.Recording;

/// <summary>What RecordSession needs from a recorder, with or without picture.</summary>
internal interface IRecorder : IDisposable
{
    string FilePath { get; }
    bool Paused { get; }
    TimeSpan Elapsed { get; }
    long FileBytes { get; }
    bool SourceClosed { get; }
    Exception? Error { get; }
    AudioTrack? Audio { get; }
    void Start();
    void Pause();
    void Resume();
    void Stop();
}

/// <summary>Sound only: computer sound and/or microphone to an .m4a (AAC) file.</summary>
internal sealed class AudioRecorder : IRecorder
{
    readonly Mp4Writer _writer;
    readonly AudioTrack _audio;
    readonly Stopwatch _active = new();
    volatile bool _paused;
    bool _stopped;

    public string FilePath { get; }
    public bool Paused => _paused;
    public TimeSpan Elapsed => _active.Elapsed;
    public bool SourceClosed => false;
    public Exception? Error => _audio.Error;
    public AudioTrack? Audio => _audio;

    public long FileBytes
    {
        get { try { return new FileInfo(FilePath).Length; } catch { return 0; } }
    }

    public AudioRecorder(string path, AudioOptions opt)
    {
        if (!opt.Any) throw new InvalidOperationException("Turn on computer sound or the microphone to record sound.");
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new Mp4Writer(path, 0, 0, 0, 0, false, audio: true);
        _audio = new AudioTrack(opt, _writer, () => _active.Elapsed, () => _paused);
        if (_audio.SystemSound == null && _audio.Mic == null)
        {
            _writer.Dispose();
            throw new InvalidOperationException("No sound device could be opened: " + string.Join(", ", _audio.Problems));
        }
    }

    public void Start()
    {
        _active.Start();
        _audio.Start();
    }

    public void Pause() { if (!_paused) { _paused = true; _active.Stop(); } }
    public void Resume() { if (_paused) { _paused = false; _active.Start(); } }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _active.Stop();
        _audio.Stop();
        _writer.Finish();
    }

    public void Dispose()
    {
        try { Stop(); } catch (Exception ex) { CrashLog.Write(ex); }
        _audio.Dispose();
        _writer.Dispose();
    }
}
