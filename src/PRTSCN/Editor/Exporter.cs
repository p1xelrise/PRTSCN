using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PRTSCN.Core;
using PRTSCN.Recording;

namespace PRTSCN.Editor;

internal enum ExportKind { Mp4, Gif, Audio }

/// <summary>What to save: the kept parts of a recording, re-encoded.</summary>
internal sealed record ExportSpec(
    string Source,
    string Target,
    ExportKind Kind,
    IReadOnlyList<TimeRange> Kept,
    Size Size,              // output size (video / GIF)
    int Fps,
    VideoQuality Quality,
    bool Sound,
    bool Dither,
    bool Hardware,
    IReadOnlyList<BlurBox>? Blurs = null,
    // several files joined (instant replay); Source and Kept are then ignored
    IReadOnlyList<(string Path, TimeRange Range)>? Parts = null)
{
    public IReadOnlyList<(string Path, TimeRange Range)> AllParts =>
        Parts ?? Kept.Select(k => (Source, k)).ToList();
}

/// <summary>
/// Re-encodes the kept parts of a recording to MP4 (H.264 + AAC), GIF or M4A.
/// Writes to a temporary file next to the target and renames it at the end, so a failed or
/// cancelled export never leaves half a file.
/// </summary>
internal static class Exporter
{
    public static void Run(ExportSpec spec, Action<double>? progress, CancellationToken ct)
    {
        var tmp = spec.Target + ".part";
        try
        {
            switch (spec.Kind)
            {
                case ExportKind.Mp4: Video(spec, tmp, gif: false, progress, ct); break;
                case ExportKind.Gif: Video(spec, tmp, gif: true, progress, ct); break;
                default: AudioOnly(spec, tmp, progress, ct); break;
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, spec.Target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    static long Total(IReadOnlyList<(string Path, TimeRange Range)> parts) => parts.Sum(k => k.Range.Length);

    static unsafe void Video(ExportSpec spec, string tmp, bool gif, Action<double>? progress, CancellationToken ct)
    {
        var want = gif ? spec.Size : RecordingMath.EvenSize(spec.Size);
        var parts = spec.AllParts;
        if (parts.Count == 0) throw new InvalidOperationException("Nothing to save.");
        var readers = new Dictionary<string, MediaReader>(StringComparer.OrdinalIgnoreCase);
        MediaReader Reader(string path)
        {
            if (!readers.TryGetValue(path, out var r)) readers[path] = r = new MediaReader(path, video: true, _ => want, spec.Hardware);
            return r;
        }
        try { VideoParts(spec, tmp, gif, parts, Reader, progress, ct); }
        finally { foreach (var r in readers.Values) r.Dispose(); }
    }

    static unsafe void VideoParts(ExportSpec spec, string tmp, bool gif, IReadOnlyList<(string Path, TimeRange Range)> parts,
        Func<string, MediaReader> open, Action<double>? progress, CancellationToken ct)
    {
        var reader = open(parts[0].Path);
        var outSize = new Size(reader.Width, reader.Height);
        long total = Total(parts);
        var blurs = spec.Blurs ?? Array.Empty<BlurBox>();
        int block = Pixelate.BlockFor(outSize.Width);
        void Blur(byte[] frame, long sourceTime)
        {
            foreach (var b in blurs)
                if (b.Range.Contains(sourceTime))
                    Pixelate.Apply(frame, outSize.Width * 4, outSize.Width, outSize.Height, Pixelate.ToPixels(b, outSize.Width, outSize.Height), block);
        }

        Mp4Writer? mp4 = null;
        FileStream? gifFile = null;
        GifWriter? gifw = null;
        AudioPump? pump = null;
        try
        {
            if (gif)
            {
                gifFile = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                gifw = new GifWriter(gifFile, outSize.Width, outSize.Height);
            }
            else
            {
                bool sound = spec.Sound && MediaReader.Probe(parts[0].Path).audio;
                mp4 = new Mp4Writer(tmp, outSize.Width, outSize.Height, spec.Fps,
                    RecordingMath.Bitrate(outSize, spec.Fps, spec.Quality), spec.Hardware, sound);
                if (sound) pump = new AudioPump(parts, mp4);
            }

            int srcLen = reader.Width * reader.Height * 4;
            var cur = new byte[srcLen];
            var held = new byte[srcLen];
            var pre = new byte[srcLen];
            var res = new FrameResampler(spec.Fps);
            long frameDur = res.FrameDuration;

            void WriteSlot(byte[] frame, long slotTime)
            {
                ct.ThrowIfCancellationRequested();
                byte[] data = frame;
                int stride = reader.Width * 4;
                if (gifw != null)
                {
                    long index = (long)Math.Round(slotTime * (double)spec.Fps / 10_000_000);
                    gifw.AddFrame(data, stride, GifWriter.DelayCs(index, spec.Fps), spec.Dither);
                }
                else
                {
                    fixed (byte* p = data) mp4!.WriteFrame((IntPtr)p, stride, slotTime, frameDur);
                    pump?.PumpUntil(slotTime + frameDur + 5_000_000, ct);
                }
                progress?.Invoke(total > 0 ? Math.Min(1, (double)slotTime / total) : 0);
            }

            // nearest frame per slot, each part lined up with the slots (no repeated-then-skipped frames)
            var align = new FrameAligner(frameDur);

            void Push(ref byte[] frame, long outTime, bool onGrid = true)
            {
                foreach (var slot in res.Feed(align.Place(outTime, onGrid))) WriteSlot(held, slot);
                (held, frame) = (frame, held);
            }

            long offset = 0;
            foreach (var (path, seg) in parts)
            {
                reader = open(path);
                if (reader.Width != outSize.Width || reader.Height != outSize.Height) throw new InvalidOperationException("The parts have different sizes.");
                reader.Seek(seg.Start);
                align.NewPart();
                bool havePre = false, any = false;
                long preTime = 0;
                while (reader.ReadFrame(cur, out long t))
                {
                    ct.ThrowIfCancellationRequested();
                    if (t < seg.Start) { (pre, cur) = (cur, pre); havePre = true; preTime = t; continue; }
                    if (t >= seg.End) break;
                    if (!any && havePre && t > seg.Start) { Blur(pre, preTime); Push(ref pre, offset, onGrid: false); }
                    any = true;
                    Blur(cur, t);
                    Push(ref cur, offset + t - seg.Start);
                }
                if (!any && havePre) { Blur(pre, preTime); Push(ref pre, offset, onGrid: false); }
                offset += seg.Length;
            }
            foreach (var slot in res.Flush(total)) WriteSlot(held, slot);
            pump?.PumpUntil(long.MaxValue, ct);
            gifw?.Finish();
            mp4?.Finish();
            progress?.Invoke(1);
        }
        finally
        {
            pump?.Dispose();
            mp4?.Dispose();
            gifw?.Dispose();
            gifFile?.Dispose();
        }
    }

    static void AudioOnly(ExportSpec spec, string tmp, Action<double>? progress, CancellationToken ct)
    {
        using var mp4 = new Mp4Writer(tmp, 0, 0, 30, 0, false, audio: true);
        var parts = spec.AllParts;
        using var pump = new AudioPump(parts, mp4);
        long total = Total(parts);
        for (long t = 0; ; t += 10_000_000)
        {
            bool more = pump.PumpUntil(t, ct);
            progress?.Invoke(total > 0 ? Math.Min(1, (double)t / total) : 1);
            if (!more) break;
        }
        mp4.Finish();
        progress?.Invoke(1);
    }

    /// <summary>
    /// Feeds the kept parts of the sound track into the writer, sample-accurately, as 48 kHz stereo,
    /// so the sound lines up with the re-timed video.
    /// </summary>
    sealed class AudioPump : IDisposable
    {
        MediaReader? _r;
        string? _path;
        readonly IReadOnlyList<(string Path, TimeRange Range)> _parts;
        readonly Mp4Writer _w;
        LinearResampler? _resampler;
        readonly List<float> _resampled = new();
        int _seg = -1;
        long _outFrames;      // 48 kHz frames written
        bool _done;

        public AudioPump(IReadOnlyList<(string Path, TimeRange Range)> parts, Mp4Writer w)
        {
            _parts = parts;
            _w = w;
            NextSegment();
        }

        void NextSegment()
        {
            _seg++;
            if (_seg >= _parts.Count) { _done = true; return; }
            var (path, range) = _parts[_seg];
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                _r?.Dispose();
                _r = new MediaReader(path, video: false);
                _path = path;
                _resampler = _r.AudioRate != AudioMath.Rate ? new LinearResampler(_r.AudioRate, AudioMath.Rate) : null;
            }
            _r!.Seek(range.Start);
        }

        /// <summary>Write sound up to output time t. False when all sound is written.</summary>
        public bool PumpUntil(long t, CancellationToken ct)
        {
            while (!_done && AudioMath.Time(_outFrames) < t)
            {
                ct.ThrowIfCancellationRequested();
                var seg = _parts[_seg].Range;
                var pcm = _r!.ReadAudio(out long time, out int frames);
                if (pcm == null) { NextSegment(); continue; }
                int ch = Math.Max(1, _r.AudioChannels), rate = _r.AudioRate;
                // keep only the samples inside this part
                long first = Math.Max(0, (seg.Start - time) * rate / 10_000_000);
                long last = Math.Min(frames, (seg.End - time) * rate / 10_000_000);
                if (last > first) Write(pcm, ch, (int)first, (int)(last - first));
                if (time + (long)frames * 10_000_000 / rate >= seg.End) NextSegment();
            }
            return !_done;
        }

        void Write(short[] pcm, int ch, int start, int count)
        {
            // to float stereo
            var stereo = new float[count * 2];
            for (int i = 0; i < count; i++)
            {
                int s = (start + i) * ch;
                float l = pcm[s] / 32768f, r = ch > 1 ? pcm[s + 1] / 32768f : l;
                stereo[i * 2] = l; stereo[i * 2 + 1] = r;
            }
            float[] data = stereo;
            int frames = count;
            if (_resampler != null)
            {
                _resampled.Clear();
                _resampler.Process(stereo, count, _resampled);
                data = _resampled.ToArray();
                frames = data.Length / 2;
            }
            if (frames == 0) return;
            var out16 = new short[frames * 2];
            AudioMath.ToPcm16(data.AsSpan(0, frames * 2), out16);
            _w.WriteAudio(out16, frames, AudioMath.Time(_outFrames), AudioMath.Time(_outFrames + frames) - AudioMath.Time(_outFrames));
            _outFrames += frames;
        }

        public void Dispose() => _r?.Dispose();
    }
}
