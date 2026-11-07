using System.Drawing;
using PRTSCN.Core;

public class EditListTests
{
    const long S = 10_000_000; // one second

    [Fact]
    public void Untouched_list_keeps_everything()
    {
        var e = new EditList(10 * S);
        Assert.True(e.IsEmpty);
        Assert.Equal(new[] { new TimeRange(0, 10 * S) }, e.Kept);
        Assert.Equal(10 * S, e.Length);
    }

    [Fact]
    public void Trim_and_cuts_make_kept_parts()
    {
        var e = new EditList(10 * S);
        Assert.True(e.SetTrim(1 * S, 9 * S));
        Assert.True(e.AddCut(new TimeRange(3 * S, 4 * S)));
        Assert.True(e.AddCut(new TimeRange(6 * S, 5 * S))); // reversed range is fine
        Assert.Equal(new[] { new TimeRange(1 * S, 3 * S), new TimeRange(4 * S, 5 * S), new TimeRange(6 * S, 9 * S) }, e.Kept);
        Assert.Equal(6 * S, e.Length);
    }

    [Fact]
    public void Overlapping_cuts_merge_and_can_be_restored()
    {
        var e = new EditList(10 * S);
        e.AddCut(new TimeRange(2 * S, 4 * S));
        e.AddCut(new TimeRange(3 * S, 6 * S));
        Assert.Single(e.Cuts);
        Assert.Equal(new TimeRange(2 * S, 6 * S), e.Cuts[0]);
        Assert.True(e.RemoveCutAt(5 * S));
        Assert.True(e.IsEmpty);
    }

    [Fact]
    public void Refuses_to_remove_everything()
    {
        var e = new EditList(10 * S);
        Assert.False(e.AddCut(new TimeRange(0, 10 * S)));
        Assert.False(e.SetTrim(5 * S, 5 * S));
        e.AddCut(new TimeRange(0, 5 * S));
        Assert.False(e.SetTrim(0, 5 * S)); // only the cut part would be left
    }

    [Fact]
    public void Time_mapping_skips_removed_parts()
    {
        var e = new EditList(10 * S);
        e.SetTrim(1 * S, 9 * S);
        e.AddCut(new TimeRange(3 * S, 4 * S));
        Assert.Equal(0, e.ToOutput(1 * S));
        Assert.Equal(2 * S, e.ToOutput(3 * S + S / 2)); // inside the cut: where it was
        Assert.Equal(2 * S, e.ToOutput(4 * S));
        Assert.Equal(4 * S, e.ToSource(3 * S) - S);
        Assert.Equal(1 * S, e.ToSource(0));
        Assert.Equal(9 * S, e.ToSource(100 * S));
        Assert.Equal(4 * S, e.NextPlayable(3 * S));
        Assert.Equal(1 * S, e.NextPlayable(0));
        Assert.Equal(9 * S, e.NextPlayable(9 * S));
    }

    [Fact]
    public void Undo_snapshot_round_trips()
    {
        var e = new EditList(10 * S);
        var before = e.Save();
        e.SetTrim(2 * S, 8 * S);
        e.AddCut(new TimeRange(3 * S, 4 * S));
        e.Restore(before);
        Assert.True(e.IsEmpty);
    }
}

public class FrameResamplerTests
{
    [Fact]
    public void Frames_fill_fixed_slots()
    {
        var r = new FrameResampler(10); // slots every 1_000_000
        Assert.Empty(r.Feed(0));
        Assert.Equal(new long[] { 0 }, r.Feed(400_000));          // frame 0 fills slot 0
        Assert.Equal(new long[] { 1_000_000 }, r.Feed(1_500_000)); // frame at 0.4 s fills slot 1
        Assert.Equal(new long[] { 2_000_000, 3_000_000 }, r.Flush(3_500_000));
    }

    [Fact]
    public void Fast_source_drops_frames_slow_source_repeats()
    {
        var r = new FrameResampler(5); // slots every 2_000_000
        var filled = new List<long>();
        for (long t = 0; t < 10_000_000; t += 333_333) filled.AddRange(r.Feed(t));
        filled.AddRange(r.Flush(10_000_000));
        Assert.Equal(new long[] { 0, 2_000_000, 4_000_000, 6_000_000, 8_000_000 }, filled);
    }

    /// <summary>
    /// Export a 30 fps recording with jittery timestamps, trimmed at an awkward time and with a cut,
    /// and return which source frame lands in each output slot.
    /// </summary>
    static List<int> Export(long trimIn, bool aligned)
    {
        const int fps = 30;
        var r = new FrameResampler(fps);
        var align = new FrameAligner(r.FrameDuration);
        var jitter = new long[] { 0, 2, -3, 1, -1, 3, -2 };
        // two kept parts: trimIn..4 s and 5.0123 s..8 s
        var parts = new[] { (Start: trimIn, End: 40_000_000L), (Start: 50_123_000L, End: 80_000_000L) };
        var shown = new List<int>();
        int held = -1;
        long offset = 0;
        void Push(int k, long outTime, bool onGrid)
        {
            foreach (var _ in r.Feed(aligned ? align.Place(outTime, onGrid) : outTime)) shown.Add(held);
            held = k;
        }
        foreach (var (start, end) in parts)
        {
            align.NewPart();
            int pre = -1;
            bool any = false;
            for (int k = 0; k < 90 * fps; k++)
            {
                long t = k * 10_000_000L / fps + jitter[k % jitter.Length];
                if (t < start) { pre = k; continue; }
                if (t >= end) break;
                // like the exporter: the frame showing at the part's start fills in before its first new frame
                if (!any && pre >= 0 && t > start) Push(pre, offset, onGrid: false);
                any = true;
                Push(k, offset + t - start, onGrid: true);
            }
            offset += end - start;
        }
        foreach (var _ in r.Flush(offset)) shown.Add(held);
        return shown;
    }

    static int Repeats(List<int> shown) => Enumerable.Range(1, shown.Count - 1).Count(i => shown[i] == shown[i - 1]);

    [Theory]
    [InlineData(0L)]            // no trim: frames sit right on the slot edges
    [InlineData(12_345_678L)]   // trimmed at an odd time
    [InlineData(10_000_001L)]   // one tick past a frame
    public void Trimmed_export_keeps_every_frame_once(long trimIn)
    {
        var shown = Export(trimIn, aligned: true);
        // the part was moved under half a frame, so the last frame may also fill the final slot
        var repeats = Enumerable.Range(1, shown.Count - 1).Where(i => shown[i] == shown[i - 1]).ToList();
        Assert.All(repeats, i => Assert.Equal(shown.Count - 1, i));
        // every source frame follows the previous one, except at the cut and a possible last repeat
        var jumps = Enumerable.Range(1, shown.Count - 1).Where(i => shown[i] != shown[i - 1] + 1 && !repeats.Contains(i)).ToList();
        Assert.Single(jumps);
    }

    [Fact]
    public void Without_alignment_jitter_repeats_and_skips_frames()
    {
        Assert.True(Repeats(Export(0, aligned: false)) > 10); // the old behaviour this guards against
    }
}

public class GifWriterTests
{
    static byte[] Solid(int w, int h, Color c)
    {
        var a = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { a[i * 4] = c.B; a[i * 4 + 1] = c.G; a[i * 4 + 2] = c.R; a[i * 4 + 3] = 255; }
        return a;
    }

    [Fact]
    public void Writes_frames_that_decode_back()
    {
        var ms = new MemoryStream();
        var f1 = Solid(40, 30, Color.FromArgb(255, 20, 40, 60));
        var f2 = (byte[])f1.Clone();
        for (int y = 10; y < 20; y++) for (int x = 5; x < 15; x++)
            { int p = (y * 40 + x) * 4; f2[p] = 0; f2[p + 1] = 0; f2[p + 2] = 255; }
        using (var g = new GifWriter(ms, 40, 30))
        {
            g.AddFrame(f1, 40 * 4, 7);
            g.AddFrame(f1, 40 * 4, 6);  // identical: merged into the first frame's delay
            g.AddFrame(f2, 40 * 4, 10);
        }
        var frames = GifReader.Decode(ms.ToArray(), out int w, out int h, out bool loops);
        Assert.Equal((40, 30), (w, h));
        Assert.True(loops);
        Assert.Equal(2, frames.Count);
        Assert.Equal(13, frames[0].Delay);
        Assert.Equal(new Rectangle(0, 0, 40, 30), frames[0].Rect);
        Assert.Equal(new Rectangle(5, 10, 10, 10), frames[1].Rect); // only the changed part
        Assert.Equal(0xFF0000, frames[1].Palette[frames[1].Indices[0]]);
        Assert.Equal(0x14283C & 0xF8F8F8, frames[0].Palette[frames[0].Indices[0]] & 0xF8F8F8);
    }

    [Fact]
    public void Many_colours_compress_and_decode()
    {
        // a gradient with ~3000 colours, larger than one LZW table, with dithering
        int w = 200, h = 120;
        var a = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            { int p = (y * w + x) * 4; a[p] = (byte)(x * 255 / w); a[p + 1] = (byte)(y * 255 / h); a[p + 2] = (byte)((x ^ y) & 255); }
        var ms = new MemoryStream();
        using (var g = new GifWriter(ms, w, h)) g.AddFrame(a, w * 4, 5, dither: true);
        var frames = GifReader.Decode(ms.ToArray(), out _, out _, out _);
        Assert.Single(frames);
        Assert.Equal(w * h, frames[0].Indices.Length);
        Assert.DoesNotContain((byte)255, frames[0].Indices); // nothing transparent on the first frame
    }

    [Theory]
    [InlineData(15, new[] { 7, 6, 7, 7, 6, 7 })]
    [InlineData(10, new[] { 10, 10, 10 })]
    public void Delays_do_not_drift(int fps, int[] expected)
    {
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], GifWriter.DelayCs(i, fps));
    }

    [Fact]
    public void Scaled_size_keeps_aspect_and_never_grows()
    {
        Assert.Equal(new Size(800, 450), GifWriter.ScaledSize(new Size(1920, 1080), 800));
        Assert.Equal(new Size(640, 360), GifWriter.ScaledSize(new Size(640, 360), 800));
        Assert.Equal(new Size(1920, 1080), GifWriter.ScaledSize(new Size(1920, 1080), 0));
    }

    [Fact]
    public void Recent_list_dedupes_and_caps()
    {
        var s = new AppSettings();
        for (int i = 0; i < 15; i++) s.AddRecent($@"C:\v\{i}.mp4");
        s.AddRecent(@"c:\V\3.MP4");
        Assert.Equal(AppSettings.MaxRecent, s.RecentRecordings.Count);
        Assert.Equal(@"c:\V\3.MP4", s.RecentRecordings[0]);
        Assert.Single(s.RecentRecordings, p => p.Equals(@"C:\v\3.mp4", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Minimal GIF decoder for the tests.</summary>
static class GifReader
{
    public record Frame(Rectangle Rect, int Delay, int[] Palette, byte[] Indices);

    public static List<Frame> Decode(byte[] d, out int w, out int h, out bool loops)
    {
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(d, 0, 6));
        w = d[6] | d[7] << 8; h = d[8] | d[9] << 8;
        Assert.Equal(0, d[10] & 0x80); // no global table
        int p = 13;
        loops = false;
        int delay = 0;
        var frames = new List<Frame>();
        while (true)
        {
            byte b = d[p++];
            if (b == 0x3B) break;
            if (b == 0x21)
            {
                byte label = d[p++];
                if (label == 0xF9) delay = d[p + 2] | d[p + 3] << 8;
                if (label == 0xFF && System.Text.Encoding.ASCII.GetString(d, p + 1, 11) == "NETSCAPE2.0") loops = true;
                while (d[p] != 0) p += d[p] + 1;
                p++;
                continue;
            }
            Assert.Equal(0x2C, b);
            var rect = new Rectangle(d[p] | d[p + 1] << 8, d[p + 2] | d[p + 3] << 8, d[p + 4] | d[p + 5] << 8, d[p + 6] | d[p + 7] << 8);
            byte packed = d[p + 8];
            p += 9;
            int n = 2 << (packed & 7);
            var pal = new int[n];
            for (int i = 0; i < n; i++) pal[i] = d[p + i * 3] << 16 | d[p + i * 3 + 1] << 8 | d[p + i * 3 + 2];
            p += n * 3;
            int min = d[p++];
            var data = new List<byte>();
            while (d[p] != 0) { data.AddRange(d.AsSpan(p + 1, d[p]).ToArray()); p += d[p] + 1; }
            p++;
            frames.Add(new Frame(rect, delay, pal, Lzw(data.ToArray(), min, rect.Width * rect.Height)));
        }
        return frames;
    }

    static byte[] Lzw(byte[] data, int min, int count)
    {
        int clear = 1 << min, eoi = clear + 1;
        var output = new List<byte>(count);
        var table = new List<byte[]>();
        void Reset() { table.Clear(); for (int i = 0; i < clear + 2; i++) table.Add(new[] { (byte)i }); }
        Reset();
        int size = min + 1, bitPos = 0;
        byte[]? prev = null;
        while (true)
        {
            int code = 0;
            for (int i = 0; i < size; i++, bitPos++)
                if ((data[bitPos >> 3] >> (bitPos & 7) & 1) != 0) code |= 1 << i;
            if (code == clear) { Reset(); size = min + 1; prev = null; continue; }
            if (code == eoi) break;
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else { Assert.Equal(table.Count, code); entry = [.. prev!, prev![0]]; }
            output.AddRange(entry);
            if (prev != null && table.Count < 4096) table.Add([.. prev, entry[0]]);
            prev = entry;
            if (table.Count == 1 << size && size < 12) size++;
        }
        Assert.Equal(count, output.Count);
        return output.ToArray();
    }
}
