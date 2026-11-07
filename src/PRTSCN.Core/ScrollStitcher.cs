namespace PRTSCN.Core;

/// <summary>How the next frame lines up with the previous one.</summary>
public readonly record struct StitchMatch(
    bool Found,       // a reliable offset was found
    bool Unchanged,   // the content did not move: end of the page
    int Dy,           // how many pixels the content moved up
    int Top,          // rows at the top that did not move (sticky header)
    int Bottom,       // rows at the bottom that did not move (sticky footer)
    int Columns = 0); // how many columns changed between the frames (for the log)

/// <summary>
/// Lines up consecutive frames of a scrolling capture. Pure logic on ARGB pixel arrays, unit-tested.
///
/// Only columns that changed between the two frames are compared: a fixed side panel, menu or
/// window border inside the selected area does not move, so it is left out. Rows are compared on
/// up to 64 of those columns with a small colour tolerance, and a row counts as equal when at least
/// 90% of its samples are: a moving scrollbar thumb or a little re-rendering noise does not break it.
/// A few rows may disagree completely (a hover highlight, a blinking caret).
/// </summary>
public static class ScrollStitcher
{
    public const int MinOverlapRows = 24;
    const int MaxSamples = 64;
    const int ChannelTolerance = 18;

    public static StitchMatch Match(int[] prev, int[] next, int width, int height, int preferredDy = 0)
    {
        int w = width, h = height;
        if (prev.Length != w * h || next.Length != w * h || h < MinOverlapRows * 2)
            return new StitchMatch(false, false, 0, 0, 0);

        // 1. Columns that changed at all. Everything else is static and carries no position info.
        var active = new List<int>();
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (((prev[y * w + x] ^ next[y * w + x]) & 0xFFFFFF) != 0) { active.Add(x); break; }
        if (active.Count < 3) return new StitchMatch(false, true, 0, 0, 0, active.Count);

        // 2. Sample those columns into compact rows.
        int n = Math.Min(MaxSamples, active.Count);
        var cols = new int[n];
        for (int i = 0; i < n; i++) cols[i] = active[(int)((long)i * active.Count / n)];
        var A = Sample(prev, w, h, cols);
        var B = Sample(next, w, h, cols);
        var informative = new bool[h];
        for (int y = 0; y < h; y++)
        {
            int first = B[y * n];
            for (int i = 1; i < n; i++) if (!Close(B[y * n + i], first)) { informative[y] = true; break; }
        }
        int allowedBadSamples = Math.Max(1, n / 10);
        bool RowEq(int ya, int yb)
        {
            int bad = 0, oa = ya * n, ob = yb * n;
            for (int i = 0; i < n; i++)
                if (A[oa + i] != B[ob + i] && !Close(A[oa + i], B[ob + i]) && ++bad > allowedBadSamples) return false;
            return true;
        }

        // 3. Sticky header and footer: rows that are the same in both frames at the same place.
        int top = 0;
        while (top < h && RowEq(top, top)) top++;
        if (top == h) return new StitchMatch(false, true, 0, 0, 0, active.Count); // only noise changed
        int bottom = 0;
        while (bottom < h - top && RowEq(h - 1 - bottom, h - 1 - bottom)) bottom++;
        top = Math.Min(top, h / 3);
        bottom = Math.Min(bottom, h / 3);
        int band = h - top - bottom;
        if (band < MinOverlapRows * 2) return new StitchMatch(false, false, 0, top, bottom, active.Count);

        // 4. Try every offset; keep the ones where the overlapping rows agree, with their error.
        var candidates = new List<(int dy, double err, int info)>();
        for (int dy = 1; dy <= band - MinOverlapRows; dy++)
        {
            // Up to 10% of rows may disagree (a hover highlight, a caret). Rows of one flat colour match
            // anything, so most of the rows with real content must agree too.
            int overlap = band - dy, allowedBadRows = Math.Max(2, overlap / 10), badRows = 0, matchedInfo = 0, info = 0;
            long diff = 0;
            for (int i = 0; i < overlap; i++)
            {
                bool inf = informative[top + i];
                if (inf) info++;
                if (!RowEq(top + dy + i, top + i)) { if (++badRows > allowedBadRows) break; continue; }
                if (inf) { matchedInfo++; diff += RowDiff(top + dy + i, top + i); }
            }
            if (badRows > allowedBadRows || matchedInfo < 8 || matchedInfo * 10 < info * 7) continue;
            // Error per compared sample, plus a penalty per bad row, so the true offset beats its
            // neighbours one pixel off (which also pass, since rows only need to be close).
            double err = (double)diff / ((long)matchedInfo * n) + 64.0 * badRows / overlap;
            candidates.Add((dy, err, matchedInfo));
        }
        if (candidates.Count == 0) return new StitchMatch(false, false, 0, top, bottom, active.Count);

        // Keep only local minima (drops the one-pixel-off neighbours of each real match), then:
        // the clearly best one wins; among near-equal ones (repeated content), the one closest to
        // the previous offset.
        var minima = candidates.Where((c, i) =>
            (i == 0 || candidates[i - 1].dy != c.dy - 1 || candidates[i - 1].err >= c.err) &&
            (i == candidates.Count - 1 || candidates[i + 1].dy != c.dy + 1 || candidates[i + 1].err > c.err)).ToList();
        double bestErr = minima.Min(c => c.err);
        var nearBest = minima.Where(c => c.err <= bestErr * 1.25 + 0.5).ToList();
        var pick = preferredDy > 0
            ? nearBest.OrderBy(c => Math.Abs(c.dy - preferredDy)).ThenBy(c => c.err).First()
            : nearBest.OrderBy(c => c.err).ThenByDescending(c => c.info).First();
        return new StitchMatch(true, false, pick.dy, top, bottom, active.Count);

        int RowDiff(int ya, int yb)
        {
            int d = 0, oa = ya * n, ob = yb * n;
            for (int i = 0; i < n; i++)
            {
                int a = A[oa + i], b = B[ob + i];
                if (a == b) continue;
                d += Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) + Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) + Math.Abs((a & 0xFF) - (b & 0xFF));
            }
            return d;
        }
    }

    static int[] Sample(int[] px, int w, int h, int[] cols)
    {
        int n = cols.Length;
        var s = new int[h * n];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, o = y * n;
            for (int i = 0; i < n; i++) s[o + i] = px[row + cols[i]];
        }
        return s;
    }

    static bool Close(int a, int b) =>
        Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) <= ChannelTolerance &&
        Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) <= ChannelTolerance &&
        Math.Abs((a & 0xFF) - (b & 0xFF)) <= ChannelTolerance;
}
