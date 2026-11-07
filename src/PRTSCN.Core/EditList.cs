namespace PRTSCN.Core;

/// <summary>A span of media time in 100 ns units (Media Foundation time), end exclusive.</summary>
public readonly record struct TimeRange(long Start, long End)
{
    public long Length => End - Start;
    public bool Contains(long t) => t >= Start && t < End;
}

/// <summary>
/// The edits made to one recording: a trim (in / out) and parts cut from the middle.
/// Times are source times in 100 ns units. "Output" time is the time in the saved file,
/// where the trimmed and cut parts are gone.
/// </summary>
public sealed class EditList
{
    /// <summary>The shortest result the editor allows (0.1 s).</summary>
    public const long MinLength = 1_000_000;

    readonly List<TimeRange> _cuts = new();

    public long Duration { get; }
    public long TrimIn { get; private set; }
    public long TrimOut { get; private set; }
    public IReadOnlyList<TimeRange> Cuts => _cuts;

    public EditList(long duration)
    {
        Duration = Math.Max(1, duration);
        TrimOut = Duration;
    }

    public bool IsEmpty => TrimIn == 0 && TrimOut == Duration && _cuts.Count == 0 && _blurs.Count == 0;

    /// <summary>The parts that end up in the saved file, in order.</summary>
    public IReadOnlyList<TimeRange> Kept
    {
        get
        {
            var kept = new List<TimeRange>();
            long at = TrimIn;
            foreach (var c in _cuts)
            {
                if (c.End <= at) continue;
                if (c.Start >= TrimOut) break;
                if (c.Start > at) kept.Add(new TimeRange(at, c.Start));
                at = Math.Max(at, c.End);
            }
            if (at < TrimOut) kept.Add(new TimeRange(at, TrimOut));
            return kept;
        }
    }

    /// <summary>Length of the saved file.</summary>
    public long Length => Kept.Sum(k => k.Length);

    static long LengthOf(long trimIn, long trimOut, IEnumerable<TimeRange> cuts)
    {
        long len = trimOut - trimIn;
        foreach (var c in cuts)
            len -= Math.Max(0, Math.Min(c.End, trimOut) - Math.Max(c.Start, trimIn));
        return len;
    }

    /// <summary>Set the trim; refused (false) when it would leave less than MinLength.</summary>
    public bool SetTrim(long trimIn, long trimOut)
    {
        trimIn = Math.Clamp(trimIn, 0, Duration);
        trimOut = Math.Clamp(trimOut, 0, Duration);
        if (trimOut - trimIn < MinLength) return false;
        if (LengthOf(trimIn, trimOut, _cuts) < MinLength) return false;
        TrimIn = trimIn;
        TrimOut = trimOut;
        return true;
    }

    /// <summary>Cut a part out. Overlapping cuts merge. Refused when nothing would be left.</summary>
    public bool AddCut(TimeRange r)
    {
        long s = Math.Clamp(Math.Min(r.Start, r.End), 0, Duration);
        long e = Math.Clamp(Math.Max(r.Start, r.End), 0, Duration);
        if (e - s <= 0) return false;
        var merged = new List<TimeRange>(_cuts) { new(s, e) };
        merged.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new List<TimeRange>();
        foreach (var c in merged)
        {
            if (result.Count > 0 && c.Start <= result[^1].End)
                result[^1] = new TimeRange(result[^1].Start, Math.Max(result[^1].End, c.End));
            else result.Add(c);
        }
        if (LengthOf(TrimIn, TrimOut, result) < MinLength) return false;
        _cuts.Clear();
        _cuts.AddRange(result);
        return true;
    }

    /// <summary>The index of the cut holding time t, or -1.</summary>
    public int CutAt(long t) => _cuts.FindIndex(c => c.Contains(t));

    public bool RemoveCutAt(long t)
    {
        int i = CutAt(t);
        if (i < 0) return false;
        _cuts.RemoveAt(i);
        return true;
    }

    /// <summary>
    /// Where playback continues from source time t: t itself when it is kept, else the start of the
    /// next kept part, or TrimOut when nothing is left.
    /// </summary>
    public long NextPlayable(long t)
    {
        foreach (var k in Kept)
        {
            if (t < k.Start) return k.Start;
            if (t < k.End) return t;
        }
        return TrimOut;
    }

    /// <summary>Source time to output time (a time inside a removed part maps to where it was).</summary>
    public long ToOutput(long source)
    {
        long o = 0;
        foreach (var k in Kept)
        {
            if (source < k.Start) return o;
            if (source < k.End) return o + source - k.Start;
            o += k.Length;
        }
        return o;
    }

    /// <summary>Output time to source time.</summary>
    public long ToSource(long output)
    {
        long o = 0;
        TimeRange last = default;
        foreach (var k in Kept)
        {
            if (output < o + k.Length) return k.Start + Math.Max(0, output - o);
            o += k.Length;
            last = k;
        }
        return last.End;
    }

    readonly List<BlurBox> _blurs = new();
    /// <summary>Pixelated rectangles, each over a time range (source time).</summary>
    public IReadOnlyList<BlurBox> Blurs => _blurs;

    /// <summary>Add a blur box; boxes smaller than 1% of the frame either way are refused.</summary>
    public bool AddBlur(BlurBox b)
    {
        if (Normalise(b) is not { } n) return false;
        _blurs.Add(n);
        return true;
    }

    /// <summary>Replace a blur box (new place, size or time range); refused like AddBlur.</summary>
    public bool SetBlur(int index, BlurBox b)
    {
        if ((uint)index >= (uint)_blurs.Count || Normalise(b) is not { } n) return false;
        _blurs[index] = n;
        return true;
    }

    /// <summary>True when none of the blur's time is kept (it sits wholly in a removed or trimmed part).</summary>
    public bool BlurHidden(BlurBox b) => !Kept.Any(k => k.Start < b.Range.End && b.Range.Start < k.End);

    /// <summary>How many blurs will not show in the saved video because their time was removed.</summary>
    public int HiddenBlurs => _blurs.Count(BlurHidden);

    /// <summary>Shortest blur, so its two ends never meet while dragging (0.1 s).</summary>
    public const long MinBlur = 1_000_000;

    BlurBox? Normalise(BlurBox b)
    {
        float x = Math.Clamp(Math.Min(b.X, b.X + b.W), 0, 1), y = Math.Clamp(Math.Min(b.Y, b.Y + b.H), 0, 1);
        float r = Math.Clamp(Math.Max(b.X, b.X + b.W), 0, 1), btm = Math.Clamp(Math.Max(b.Y, b.Y + b.H), 0, 1);
        long s = Math.Clamp(b.Range.Start, 0, Duration), e = Math.Clamp(b.Range.End, 0, Duration);
        if (r - x < 0.01f || btm - y < 0.01f || e <= s) return null;
        return new BlurBox(new TimeRange(s, e), x, y, r - x, btm - y);
    }

    /// <summary>The newest blur box covering this point (fractions) at time t, or -1.</summary>
    public int BlurAt(long t, float fx, float fy) => _blurs.FindLastIndex(b => b.Range.Contains(t) && b.Contains(fx, fy));

    public void RemoveBlur(int index) => _blurs.RemoveAt(index);

    public EditState Save() => new(TrimIn, TrimOut, _cuts.ToArray(), _blurs.ToArray());

    public void Restore(EditState s)
    {
        TrimIn = s.TrimIn;
        TrimOut = s.TrimOut;
        _cuts.Clear();
        _cuts.AddRange(s.Cuts);
        _blurs.Clear();
        _blurs.AddRange(s.Blurs ?? Array.Empty<BlurBox>());
    }
}

/// <summary>A snapshot of an EditList, for undo.</summary>
public sealed record EditState(long TrimIn, long TrimOut, TimeRange[] Cuts, BlurBox[]? Blurs = null);

/// <summary>
/// Turns a stream of decoded frames (each with its output time) into frames at a fixed rate:
/// the frame showing at each slot time is written there. Frames are dropped or repeated as needed.
/// </summary>
public sealed class FrameResampler
{
    readonly int _fps;
    long _slot;            // next slot index
    long _heldTime = -1;   // output time of the frame being held, -1 = none

    public FrameResampler(int fps) => _fps = Math.Max(1, fps);

    public long SlotTime(long slot) => slot * 10_000_000L / _fps;
    public long FrameDuration => 10_000_000L / _fps;

    /// <summary>
    /// A new frame arrives at output time t. Returns the slots the previously held frame fills
    /// (slots before t). The new frame is then held.
    /// </summary>
    public List<long> Feed(long t)
    {
        var slots = new List<long>();
        if (_heldTime >= 0)
            while (SlotTime(_slot) < t) slots.Add(SlotTime(_slot++));
        else
            while (SlotTime(_slot) < t) _slot++; // nothing to show yet: skip slots before the first frame
        _heldTime = t;
        return slots;
    }

    /// <summary>The stream ends at output time end: the held frame fills the slots up to it.</summary>
    public List<long> Flush(long end)
    {
        var slots = new List<long>();
        if (_heldTime < 0) return slots;
        while (SlotTime(_slot) < end) slots.Add(SlotTime(_slot++));
        return slots;
    }
}

/// <summary>
/// Decides when each decoded frame starts showing in the output, so that every output slot gets
/// the nearest frame and no frame is shown twice while the next one is skipped.
/// A trim or cut rarely falls on a frame boundary, and recorded timestamps jitter by a few ticks,
/// so frames would land a hair before or after slot boundaries. Each kept part is therefore moved
/// by under half a frame so its first frame sits on a slot, and frames take over half a frame early.
/// </summary>
public sealed class FrameAligner
{
    readonly long _frame;
    long _shift, _last = long.MinValue;
    bool _aligned;

    public FrameAligner(long frameDuration) => _frame = Math.Max(1, frameDuration);

    /// <summary>A new kept part starts: its frames get their own alignment.</summary>
    public void NewPart() => _aligned = false;

    /// <summary>
    /// The time to feed to <see cref="FrameResampler.Feed"/> for a frame at output time t.
    /// onGrid is false for a frame from before the part (shown at the part's start), which doesn't
    /// set the alignment. Times never go backwards.
    /// </summary>
    public long Place(long t, bool onGrid = true)
    {
        if (onGrid && !_aligned)
        {
            long slot = (long)Math.Round(t / (double)_frame, MidpointRounding.AwayFromZero);
            _shift = t - slot * _frame;
            _aligned = true;
        }
        long at = (onGrid ? t - _shift : t) - _frame / 2;
        if (at < _last) at = _last;
        _last = at;
        return at;
    }
}
