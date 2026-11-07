using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PRTSCN.Core;

namespace PRTSCN.Editor;

/// <summary>
/// Developer check:
/// PRTSCN.exe --export-test &lt;source&gt; &lt;out&gt; mp4|gif|audio [cutFrom-cutTo seconds] [width] [fps]
/// Prints what the reader sees, saves the first frame as &lt;out&gt;.png, then exports.
/// </summary>
internal static class EditorDev
{
    public static int Run(string[] args)
    {
        var log = new List<string>();
        try
        {
            string src = args[1], outPath = args[2];
            var kind = args[3] switch { "gif" => ExportKind.Gif, "audio" => ExportKind.Audio, _ => ExportKind.Mp4 };
            var (hasV, hasA) = MediaReader.Probe(src);
            log.Add($"video={hasV} audio={hasA}");
            long duration = 0;
            Size size = Size.Empty;
            double fps = 30;
            if (hasV)
            {
                using var r = new MediaReader(src, video: true);
                duration = r.Duration; size = new Size(r.Width, r.Height); fps = r.Fps;
                log.Add($"{r.Width}x{r.Height} {r.Fps:0.##} fps {TimeSpan.FromTicks(r.Duration)}");
                var buf = new byte[r.Width * r.Height * 4];
                var sw = Stopwatch.StartNew();
                int n = 0; long last = 0;
                while (r.ReadFrame(buf, out long t))
                {
                    if (n == 0) SavePng(buf, r.Width, r.Height, outPath + ".png");
                    n++; last = t;
                }
                log.Add($"decoded {n} frames to {TimeSpan.FromTicks(last)} in {sw.ElapsedMilliseconds} ms");
                // seek check: the first frame after a seek to the middle
                r.Seek(r.Duration / 2);
                sw.Restart();
                r.ReadFrame(buf, out long ts);
                log.Add($"seek to {TimeSpan.FromTicks(r.Duration / 2)} gave frame at {TimeSpan.FromTicks(ts)} in {sw.ElapsedMilliseconds} ms (hardware {r.Hardware})");
                foreach (bool hw in new[] { true, false })
                {
                    using var p = new MediaReader(src, true, s => GifWriter.ScaledSize(s, 960), hw);
                    var pb = new byte[p.Width * p.Height * 4];
                    sw.Restart(); int m = 0;
                    while (p.ReadFrame(pb, out _)) { if (m == 30) SavePng(pb, p.Width, p.Height, outPath + $".preview-{(p.Hardware ? "gpu" : "cpu")}.png"); m++; }
                    log.Add($"preview {p.Width}x{p.Height} hardware={p.Hardware}: {m} frames in {sw.ElapsedMilliseconds} ms");
                }
            }
            if (hasA)
            {
                using var a = new MediaReader(src, video: false);
                if (duration == 0) duration = a.Duration;
                long frames = 0; float peak = 0;
                while (a.ReadAudio(out _, out int f) is { } pcm)
                {
                    frames += f;
                    foreach (var s in pcm) peak = Math.Max(peak, Math.Abs(s / 32768f));
                }
                log.Add($"audio {a.AudioRate} Hz x{a.AudioChannels}, {frames} frames ({frames / (double)Math.Max(1, a.AudioRate):0.00} s), peak {peak:0.000}");
            }

            var edits = new EditList(duration);
            if (args.Length > 4 && args[4].Contains('-'))
            {
                var p = args[4].Split('-');
                edits.AddCut(new TimeRange((long)(double.Parse(p[0]) * 1e7), (long)(double.Parse(p[1]) * 1e7)));
            }
            int width = args.Length > 5 ? int.Parse(args[5]) : 0;
            int outFps = args.Length > 6 ? int.Parse(args[6]) : kind == ExportKind.Gif ? 15 : (int)Math.Round(fps);
            var outSize = width > 0 ? GifWriter.ScaledSize(size, width) : size;
            var spec = new ExportSpec(src, outPath, kind, edits.Kept, outSize, outFps, VideoQuality.High, true, false, true);
            var watch = Stopwatch.StartNew();
            Exporter.Run(spec, null, CancellationToken.None);
            log.Add($"exported {kind} {outSize.Width}x{outSize.Height} @{outFps}, length {TimeSpan.FromTicks(edits.Length)}, {new FileInfo(outPath).Length / 1024} KB in {watch.ElapsedMilliseconds} ms");

            if (kind != ExportKind.Gif && File.Exists(outPath))
            {
                var (v2, a2) = MediaReader.Probe(outPath);
                long d2 = 0;
                if (v2) { using var r2 = new MediaReader(outPath, true); d2 = r2.Duration; log.Add($"result video {r2.Width}x{r2.Height} {r2.Fps:0.##} fps"); }
                if (a2) { using var r3 = new MediaReader(outPath, false); d2 = Math.Max(d2, r3.Duration); }
                log.Add($"result duration {TimeSpan.FromTicks(d2)} audio={a2}");
            }
            File.WriteAllLines(outPath + ".log", log);
            return 0;
        }
        catch (Exception ex)
        {
            log.Add(ex.ToString());
            File.WriteAllLines(args[2] + ".log", log);
            return 1;
        }
    }

    static void SavePng(byte[] bgra, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        bmp.UnlockBits(d);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
