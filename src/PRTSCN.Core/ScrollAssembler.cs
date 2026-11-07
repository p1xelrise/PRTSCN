namespace PRTSCN.Core;

/// <summary>
/// Builds the layout of a scrolling capture, frame by frame: which rows of which frame go into the
/// final image, in order. Tracks exactly where the last added row is, so a sticky footer that is
/// measured a few rows differently from one frame to the next never drops or repeats rows.
/// Pure logic, unit-tested; the app only copies the rows it is told to.
/// </summary>
public sealed class ScrollAssembler
{
    public readonly record struct Segment(int Frame, int Y0, int Y1)
    {
        public int Height => Y1 - Y0;
    }

    readonly int _w, _h;
    readonly List<Segment> _segments = new();
    int[]? _prev;
    int _frames;        // frames accepted so far (index of the next one)
    int _appendEnd;     // rows [0, _appendEnd) of the last accepted frame are already in the image
    int _prevDy;
    bool _matchedOnce;

    public ScrollAssembler(int width, int height) { _w = width; _h = height; }

    public int Frames => _frames;
    public int Height => _segments.Sum(s => s.Height) + (_frames == 0 ? 0 : _h - (_matchedOnce ? _appendEnd : 0));
    public IReadOnlyList<Segment> Segments => _segments;
    public int LastDy => _prevDy;

    /// <summary>
    /// Offer the next frame. On a match the frame is accepted and the segments it adds are returned
    /// (the first match also adds the first frame's segment). Otherwise nothing changes.
    /// </summary>
    public (StitchMatch match, List<Segment> added) Add(int[] pixels)
    {
        var added = new List<Segment>();
        if (_prev == null)
        {
            _prev = pixels;
            _frames = 1;
            return (new StitchMatch(true, false, 0, 0, 0), added);
        }
        var m = ScrollStitcher.Match(_prev, pixels, _w, _h, _prevDy);
        if (!m.Found) return (m, added);

        if (!_matchedOnce)
        {
            _matchedOnce = true;
            _appendEnd = _h - m.Bottom;                     // first frame down to its sticky footer
            added.Add(new Segment(0, 0, _appendEnd));
        }
        int start = _appendEnd - m.Dy;                      // same content row, in the new frame
        int end = Math.Max(start, _h - m.Bottom);
        if (end > start) added.Add(new Segment(_frames, start, end));
        _appendEnd = end;
        _segments.AddRange(added);
        _prev = pixels;
        _prevDy = m.Dy;
        _frames++;
        return (m, added);
    }

    /// <summary>All segments including the last frame's footer (or the whole first frame if nothing matched).</summary>
    public List<Segment> Finish()
    {
        var all = new List<Segment>(_segments);
        if (_frames == 0) return all;
        if (!_matchedOnce) all.Add(new Segment(0, 0, _h));
        else if (_appendEnd < _h) all.Add(new Segment(_frames - 1, _appendEnd, _h));
        return all;
    }

    /// <summary>Compose the final pixels (used by tests; the app copies rows between bitmaps).</summary>
    public static int[] Compose(IReadOnlyList<Segment> segs, Func<int, int[]> frame, int width)
    {
        int total = segs.Sum(s => s.Height);
        var outPx = new int[width * total];
        int y = 0;
        foreach (var s in segs)
        {
            Array.Copy(frame(s.Frame), s.Y0 * width, outPx, y * width, s.Height * width);
            y += s.Height;
        }
        return outPx;
    }
}
