using System.Drawing;

namespace PRTSCN.Core;

/// <summary>
/// Zoom while recording: a click zooms in on where it happened, the view follows the pointer while
/// zoomed, and after a quiet moment it zooms back out. Works in source pixels; the result is the part
/// of the recorded view that fills the video.
/// </summary>
public sealed class ZoomCamera
{
    public const double InSeconds = 0.35, OutSeconds = 0.6, HoldSeconds = 2.2;

    readonly Rectangle _view;
    readonly float _max;
    double _level;          // 0 = out, 1 = fully in (eased into a zoom factor)
    double _sinceClick = double.MaxValue;
    PointF _center;

    public ZoomCamera(Rectangle view, float zoom)
    {
        _view = view;
        _max = Math.Clamp(zoom, 1.1f, 4f);
        _center = new PointF(view.X + view.Width / 2f, view.Y + view.Height / 2f);
    }

    public bool Zoomed => _level > 0;

    /// <summary>A click (in source pixels) starts or prolongs the zoom.</summary>
    public void Click(Point p)
    {
        if (_level <= 0) _center = p; // zoom in where the click happened
        _sinceClick = 0;
    }

    /// <summary>Advance by dt seconds with the pointer at p. Returns the zoomed part of the view.</summary>
    public RectangleF Update(Point pointer, double dt)
    {
        _sinceClick += dt;
        bool want = _sinceClick < HoldSeconds;
        _level = want ? Math.Min(1, _level + dt / InSeconds) : Math.Max(0, _level - dt / OutSeconds);
        if (_level > 0)
        {
            // glide towards the pointer (about 85% of the way per second)
            float k = (float)(1 - Math.Pow(0.15, dt));
            _center = new PointF(_center.X + (pointer.X - _center.X) * k, _center.Y + (pointer.Y - _center.Y) * k);
        }
        return Rect();
    }

    public float Factor => 1 + (_max - 1) * (float)OverlayMath.EaseOut((float)_level);

    RectangleF Rect()
    {
        float f = Factor;
        float w = _view.Width / f, h = _view.Height / f;
        float x = Math.Clamp(_center.X - w / 2, _view.Left, _view.Right - w);
        float y = Math.Clamp(_center.Y - h / 2, _view.Top, _view.Bottom - h);
        return new RectangleF(x, y, w, h);
    }
}

/// <summary>Pixelates a rectangle of a BGRA frame (for blurring out private parts of a video).</summary>
public static class Pixelate
{
    /// <summary>A block size that hides text at this frame width (about 1/60 of it, at least 8 px).</summary>
    public static int BlockFor(int frameWidth) => Math.Max(8, frameWidth / 60);

    public static void Apply(byte[] bgra, int stride, int width, int height, Rectangle r, int block)
    {
        r = Rectangle.Intersect(r, new Rectangle(0, 0, width, height));
        if (r.Width <= 0 || r.Height <= 0) return;
        block = Math.Max(1, block);
        for (int by = r.Top; by < r.Bottom; by += block)
        for (int bx = r.Left; bx < r.Right; bx += block)
        {
            int ex = Math.Min(bx + block, r.Right), ey = Math.Min(by + block, r.Bottom);
            long b = 0, g = 0, red = 0;
            int n = (ex - bx) * (ey - by);
            for (int y = by; y < ey; y++)
            {
                int i = y * stride + bx * 4;
                for (int x = bx; x < ex; x++, i += 4) { b += bgra[i]; g += bgra[i + 1]; red += bgra[i + 2]; }
            }
            byte mb = (byte)(b / n), mg = (byte)(g / n), mr = (byte)(red / n);
            for (int y = by; y < ey; y++)
            {
                int i = y * stride + bx * 4;
                for (int x = bx; x < ex; x++, i += 4) { bgra[i] = mb; bgra[i + 1] = mg; bgra[i + 2] = mr; }
            }
        }
    }

    /// <summary>A blur box (fractions of the frame) in pixels.</summary>
    public static Rectangle ToPixels(BlurBox box, int width, int height) => Rectangle.FromLTRB(
        (int)Math.Floor(box.X * width), (int)Math.Floor(box.Y * height),
        (int)Math.Ceiling((box.X + box.W) * width), (int)Math.Ceiling((box.Y + box.H) * height));
}

/// <summary>A pixelated rectangle shown during a time range. X/Y/W/H are fractions of the frame (0–1).</summary>
public readonly record struct BlurBox(TimeRange Range, float X, float Y, float W, float H)
{
    public bool Contains(float fx, float fy) => fx >= X && fy >= Y && fx < X + W && fy < Y + H;
}

/// <summary>One finished piece of the instant-replay buffer: a file and the recording time it covers.</summary>
public readonly record struct ReplaySegment(string Path, long Start, long End);

public static class ReplayPlan
{
    /// <summary>
    /// The parts of the newest segments that make up the last <paramref name="length"/> of recording:
    /// (file, range inside that file). Oldest first.
    /// </summary>
    public static List<(string Path, TimeRange Range)> LastPart(IReadOnlyList<ReplaySegment> segments, long length)
    {
        var parts = new List<(string, TimeRange)>();
        if (segments.Count == 0) return parts;
        long end = segments[^1].End, from = Math.Max(segments[0].Start, end - length);
        foreach (var s in segments)
        {
            if (s.End <= from) continue;
            long a = Math.Max(s.Start, from);
            if (s.End - a <= 0) continue;
            parts.Add((s.Path, new TimeRange(a - s.Start, s.End - s.Start)));
        }
        return parts;
    }

    /// <summary>Segments no longer needed to cover <paramref name="keep"/> before the newest end.</summary>
    public static List<ReplaySegment> Expired(IReadOnlyList<ReplaySegment> segments, long keep)
    {
        if (segments.Count == 0) return new();
        long from = segments[^1].End - keep;
        return segments.Where(s => s.End <= from).ToList();
    }
}

/// <summary>A recording set up to start by itself.</summary>
public sealed class RecordSchedule
{
    public DateTime Start { get; set; }
    public int Minutes { get; set; } = 10;
    public bool SoundOnly { get; set; }

    /// <summary>The next time the clock shows this time of day: today, or tomorrow when it has passed.</summary>
    public static DateTime NextAt(DateTime now, TimeSpan timeOfDay)
    {
        var t = now.Date + timeOfDay;
        return t <= now ? t.AddDays(1) : t;
    }

    /// <summary>"today 14:30", "tomorrow 09:00" or "Sat 8 Nov 14:30".</summary>
    public static string Describe(DateTime when, DateTime now)
    {
        string day = when.Date == now.Date ? "today" : when.Date == now.Date.AddDays(1) ? "tomorrow" : when.ToString("ddd d MMM");
        return $"{day} {when:HH:mm}";
    }
}

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

public static class WatermarkMath
{
    /// <summary>Where a watermark of this size goes in a frame, with a margin from the corner.</summary>
    public static RectangleF Place(SizeF frame, SizeF mark, Corner corner, float margin)
    {
        float x = corner is Corner.TopLeft or Corner.BottomLeft ? margin : frame.Width - margin - mark.Width;
        float y = corner is Corner.TopLeft or Corner.TopRight ? margin : frame.Height - margin - mark.Height;
        return new RectangleF(x, y, mark.Width, mark.Height);
    }

    /// <summary>Scale an image watermark so it is at most a fifth of the frame wide and an eighth high.</summary>
    public static SizeF Fit(SizeF image, SizeF frame)
    {
        float k = Math.Min(1, Math.Min(frame.Width / 5 / image.Width, frame.Height / 8 / image.Height));
        return new SizeF(image.Width * k, image.Height * k);
    }
}
