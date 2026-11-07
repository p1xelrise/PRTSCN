using PRTSCN.Core;

namespace PRTSCN.Recording;

/// <summary>
/// Instant replay: records the screen with the mouse (and the chosen sound) in the background into
/// a rolling buffer, and on request saves the last N seconds as an MP4 in the recordings folder.
/// Nothing is shown on screen while it runs; the tray icon's tooltip says it is on.
/// </summary>
internal sealed class ReplayService : IDisposable
{
    /// <summary>Length of one buffer segment. Shorter = quicker saves, more files.</summary>
    public static readonly TimeSpan Segment = TimeSpan.FromSeconds(10);

    ScreenRecorder? _rec;
    ReplayBuffer? _buf;
    bool _saving;

    public bool Running => _rec != null;
    public bool Saving => _saving;
    public Exception? Error => _rec?.Error;
    /// <summary>How much is buffered (grows to the chosen length after starting).</summary>
    public TimeSpan Held => _buf?.Held ?? TimeSpan.Zero;

    public static string Folder => Path.Combine(Path.GetTempPath(), "PRTSCN", "replay");

    public void Start(AppSettings s, string? folder = null, TimeSpan? segment = null)
    {
        if (_rec != null) return;
        folder ??= Folder;
        _buf = new ReplayBuffer(folder, segment ?? Segment, TimeSpan.FromSeconds(s.ReplaySeconds) + (segment ?? Segment));
        try
        {
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            var audio = new AudioOptions(s.RecordSystemAudio, s.RecordMic, s.MicDeviceId, AudioMath.Gain(s.SystemVolume), AudioMath.Gain(s.MicVolume));
            var opt = new RecorderOptions(Path.Combine(folder, "live.mp4"), s.VideoFps, s.VideoQuality, s.RecordShowCursor, s.HardwareEncoder,
                audio.Any ? audio : null, RecordEffects.None, Watermark.From(s), _buf);
            _rec = new ScreenRecorder(new RecordTarget(RecordKind.Screen, screen), opt);
            _rec.Start();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        try { _rec?.Dispose(); } catch (Exception ex) { CrashLog.Write(ex); }
        _rec = null;
        try { _buf?.Dispose(); } catch (Exception ex) { CrashLog.Write(ex); }
        _buf = null;
    }

    /// <summary>Save the last <paramref name="seconds"/> into <paramref name="folder"/>. Returns the file.</summary>
    public async Task<string> SaveAsync(string folder, int seconds, AppSettings s)
    {
        var buf = _buf ?? throw new InvalidOperationException("Instant replay is off.");
        if (_saving) throw new InvalidOperationException("Already saving the replay.");
        _saving = true;
        try
        {
            return await Task.Run(() =>
            {
                var parts = buf.Take(TimeSpan.FromSeconds(seconds));
                try
                {
                    if (parts.Count == 0) throw new InvalidOperationException("Nothing recorded yet. Try again in a few seconds.");
                    Directory.CreateDirectory(folder);
                    var name = "PRTSCN replay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
                    var target = Path.Combine(folder, name + ".mp4");
                    for (int i = 2; File.Exists(target); i++) target = Path.Combine(folder, $"{name} ({i}).mp4");
                    Size size;
                    using (var r = new Editor.MediaReader(parts[0].Path, video: true, hardware: false)) size = r.SourceSize;
                    var spec = new Editor.ExportSpec(parts[0].Path, target, Editor.ExportKind.Mp4, Array.Empty<TimeRange>(), size,
                        s.VideoFps, s.VideoQuality, true, false, s.HardwareEncoder, Parts: parts);
                    Editor.Exporter.Run(spec, null, CancellationToken.None);
                    return target;
                }
                finally { buf.Release(); }
            });
        }
        finally { _saving = false; }
    }

    public void Dispose() => Stop();
}
