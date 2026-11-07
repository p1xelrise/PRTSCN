using System.Drawing;

namespace PRTSCN.Core;

public enum VideoQuality { Original, High, Balanced, Small }

/// <summary>Pure maths for screen recording: frame sizes, crops, bitrates, timestamps.</summary>
public static class RecordingMath
{
    /// <summary>H.264 needs even sizes; drops the odd pixel (never grows past the source).</summary>
    public static Size EvenSize(Size s) => new(Math.Max(2, s.Width & ~1), Math.Max(2, s.Height & ~1));

    /// <summary>Hardware encoders top out around 4096 px; larger areas are scaled down to fit (see FitWithin).</summary>
    public const int MaxDimension = 4096;

    /// <summary>The video frame for a source: the same size when it fits, else scaled down to fit max, even.</summary>
    public static Size FitWithin(Size source, int max = MaxDimension)
    {
        if (source.Width <= max && source.Height <= max) return EvenSize(source);
        double k = Math.Min((double)max / source.Width, (double)max / source.Height);
        return EvenSize(new Size((int)Math.Floor(source.Width * k), (int)Math.Floor(source.Height * k)));
    }

    /// <summary>
    /// The crop inside a monitor for an area recording, in that monitor's pixels:
    /// clipped to the monitor and made even. Empty if the area misses the monitor.
    /// </summary>
    public static Rectangle CropInMonitor(Rectangle area, Rectangle monitor)
    {
        var r = Rectangle.Intersect(area, monitor);
        if (r.Width < 2 || r.Height < 2) return Rectangle.Empty;
        r.Offset(-monitor.X, -monitor.Y);
        var s = EvenSize(r.Size);
        return new Rectangle(r.Location, s);
    }

    /// <summary>The monitor that holds most of the area (an area recording uses one monitor).</summary>
    public static int BestMonitor(Rectangle area, IReadOnlyList<Rectangle> monitors)
    {
        int best = -1; long bestArea = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            var r = Rectangle.Intersect(area, monitors[i]);
            long a = (long)r.Width * r.Height;
            if (a > bestArea) { bestArea = a; best = i; }
        }
        return best;
    }

    /// <summary>Bits per pixel per frame for each preset (H.264, screen content).</summary>
    public static double BitsPerPixel(VideoQuality q) => q switch
    {
        VideoQuality.Original => 0.25,
        VideoQuality.High => 0.13,
        VideoQuality.Balanced => 0.08,
        _ => 0.045,
    };

    /// <summary>Average bitrate in bits per second, clamped to what encoders accept.</summary>
    public static int Bitrate(Size frame, int fps, VideoQuality q)
    {
        double bps = frame.Width * (double)frame.Height * fps * BitsPerPixel(q);
        return (int)Math.Clamp(bps, 500_000, 80_000_000);
    }

    /// <summary>Rough file size per minute for the Options hint.</summary>
    public static double MegabytesPerMinute(Size frame, int fps, VideoQuality q) =>
        Bitrate(frame, fps, q) * 60.0 / 8 / 1_000_000;

    /// <summary>Timestamp of frame n in 100 ns units (Media Foundation time).</summary>
    public static long FrameTime(long n, int fps) => n * 10_000_000L / fps;

    /// <summary>
    /// Which frame is due at an elapsed active time. The writer emits frames up to this index;
    /// when encoding falls behind, it jumps ahead rather than drifting out of real time.
    /// </summary>
    public static long FrameDue(TimeSpan active, int fps) => (long)(active.Ticks * fps / 10_000_000L);

    /// <summary>Copy a frame of size src into a fixed canvas: the part that fits, from the top-left.</summary>
    public static Rectangle FitRegion(Size src, Size canvas) =>
        new(0, 0, Math.Min(src.Width, canvas.Width), Math.Min(src.Height, canvas.Height));

    /// <summary>"01:05" or "1:02:03".</summary>
    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>"820 KB", "12.4 MB", "1.2 GB".</summary>
    public static string FileSize(long bytes) =>
        bytes < 1_000_000 ? $"{Math.Max(0, bytes) / 1000} KB"
        : bytes < 1_000_000_000 ? $"{bytes / 1_000_000.0:0.0} MB"
        : $"{bytes / 1_000_000_000.0:0.00} GB";

    public static string DefaultVideoName(DateTime t) => $"PRTSCN {t:yyyy-MM-dd HH-mm-ss}.mp4";
}
