using System.Drawing;

namespace PRTSCN.Core;

/// <summary>
/// Animated GIF writer for screen recordings. Each frame stores only the rectangle that changed,
/// with its own 255-colour palette (median cut) and unchanged pixels left transparent, so mostly
/// still screens stay small. Identical frames are merged into a longer delay.
/// Frames go in as top-down BGRA (alpha ignored).
/// </summary>
public sealed class GifWriter : IDisposable
{
    const int Transparent = 255;
    const int MaxColors = 255;

    readonly Stream _out;
    readonly int _width, _height;
    readonly int[] _shown;     // last source colour per pixel (0xRRGGBB), -1 = never drawn
    byte[]? _pending;          // the encoded frame waiting for its delay
    int _pendingDelay;
    bool _finished;

    public int FramesWritten { get; private set; }

    public GifWriter(Stream output, int width, int height, bool loop = true)
    {
        if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        _out = output;
        _width = width;
        _height = height;
        _shown = new int[width * height];
        Array.Fill(_shown, -1);

        var w = new BinaryWriter(_out, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("GIF89a"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)0x70); // no global colour table, 8-bit colour resolution
        w.Write((byte)0);    // background
        w.Write((byte)0);    // aspect
        if (loop)
        {
            w.Write(new byte[] { 0x21, 0xFF, 11 });
            w.Write("NETSCAPE2.0"u8);
            w.Write(new byte[] { 3, 1, 0, 0, 0 }); // loop forever
        }
        w.Flush();
    }

    /// <summary>Frame delay in 1/100 s for frame <paramref name="index"/> at a frame rate, without drift.</summary>
    public static int DelayCs(long index, int fps) =>
        (int)(Math.Round((index + 1) * 100.0 / fps) - Math.Round(index * 100.0 / fps));

    /// <summary>The GIF size for a source: same aspect, at most maxWidth wide (0 = no limit), never larger.</summary>
    public static Size ScaledSize(Size source, int maxWidth)
    {
        if (maxWidth <= 0 || source.Width <= maxWidth) return source;
        return new Size(maxWidth, Math.Max(1, (int)Math.Round(source.Height * (double)maxWidth / source.Width)));
    }

    /// <summary>Add a frame shown for delayCs hundredths of a second.</summary>
    public void AddFrame(ReadOnlySpan<byte> bgra, int stride, int delayCs, bool dither = false)
    {
        if (_finished) throw new InvalidOperationException("GIF already finished.");
        delayCs = Math.Max(0, delayCs);

        // 1. what changed since the last frame
        int minX = _width, minY = _height, maxX = -1, maxY = -1;
        for (int y = 0; y < _height; y++)
        {
            var row = bgra.Slice(y * stride, _width * 4);
            int o = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int c = row[x * 4 + 2] << 16 | row[x * 4 + 1] << 8 | row[x * 4];
                if (_shown[o + x] != c)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    maxY = y;
                }
            }
        }
        if (maxX < 0)
        {
            // nothing changed: show the previous frame longer
            if (_pending != null) { _pendingDelay += delayCs; return; }
            minX = minY = 0; maxX = maxY = 0; // very first frame identical to "never drawn" cannot happen; keep a 1 px frame
        }
        var rect = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);

        // 2. palette from the changed pixels
        var hist = new int[32768];
        var sumR = new long[32768]; var sumG = new long[32768]; var sumB = new long[32768];
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            int o = y * _width;
            for (int x = rect.Left; x < rect.Right; x++)
            {
                int p = y * stride + x * 4;
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                if (_shown[o + x] == (r << 16 | g << 8 | b)) continue;
                int bin = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                hist[bin]++; sumR[bin] += r; sumG[bin] += g; sumB[bin] += b;
            }
        }
        var (palette, lut) = MedianCut.Build(hist, sumR, sumG, sumB, MaxColors);

        // 3. map pixels to the palette; unchanged pixels stay transparent
        var indices = new byte[rect.Width * rect.Height];
        float[]? err = dither ? new float[(rect.Width + 2) * 2 * 3] : null; // two rows of RGB error
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            int o = y * _width, iy = (y - rect.Top) * rect.Width;
            if (err != null)
            {
                // move "next row" error to "this row" and clear "next row"
                int rowLen = (rect.Width + 2) * 3;
                Array.Copy(err, rowLen, err, 0, rowLen);
                Array.Clear(err, rowLen, rowLen);
            }
            for (int x = rect.Left; x < rect.Right; x++)
            {
                int p = y * stride + x * 4;
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                int src = r << 16 | g << 8 | b;
                int ix = x - rect.Left;
                if (_shown[o + x] == src) { indices[iy + ix] = Transparent; continue; }
                int idx;
                if (err == null)
                    idx = lut[(r >> 3) << 10 | (g >> 3) << 5 | (b >> 3)];
                else
                {
                    int e = (ix + 1) * 3;
                    int rr = Math.Clamp(r + (int)err[e], 0, 255), gg = Math.Clamp(g + (int)err[e + 1], 0, 255), bb = Math.Clamp(b + (int)err[e + 2], 0, 255);
                    idx = MedianCut.Nearest(palette, lut, rr, gg, bb);
                    int pc = palette[idx];
                    float er = rr - (pc >> 16 & 255), eg = gg - (pc >> 8 & 255), eb = bb - (pc & 255);
                    Spread(err, e + 3, er, eg, eb, 7 / 16f);                       // right
                    int n = (rect.Width + 2) * 3 + e;
                    Spread(err, n - 3, er, eg, eb, 3 / 16f);                       // below left
                    Spread(err, n, er, eg, eb, 5 / 16f);                           // below
                    Spread(err, n + 3, er, eg, eb, 1 / 16f);                       // below right
                }
                indices[iy + ix] = (byte)idx;
                _shown[o + x] = src;
            }
        }

        // 4. encode, and write the previous frame now that its delay is known
        FlushPending();
        _pending = EncodeFrame(rect, palette, indices);
        _pendingDelay = delayCs;
    }

    static void Spread(float[] err, int i, float r, float g, float b, float k)
    {
        if (i < 0 || i + 2 >= err.Length) return;
        err[i] += r * k; err[i + 1] += g * k; err[i + 2] += b * k;
    }

    static byte[] EncodeFrame(Rectangle rect, int[] palette, byte[] indices)
    {
        var ms = new MemoryStream();
        // image descriptor with a 256-entry local colour table
        ms.WriteByte(0x2C);
        WriteU16(ms, rect.X); WriteU16(ms, rect.Y); WriteU16(ms, rect.Width); WriteU16(ms, rect.Height);
        ms.WriteByte(0x87);
        for (int i = 0; i < 256; i++)
        {
            int c = i < palette.Length ? palette[i] : 0;
            ms.WriteByte((byte)(c >> 16)); ms.WriteByte((byte)(c >> 8)); ms.WriteByte((byte)c);
        }
        ms.WriteByte(8); // LZW minimum code size
        Lzw.Encode(indices, ms);
        ms.WriteByte(0); // block terminator
        return ms.ToArray();
    }

    void FlushPending()
    {
        if (_pending == null) return;
        // graphic control extension: keep the previous image (disposal 1), transparency on
        _out.WriteByte(0x21); _out.WriteByte(0xF9); _out.WriteByte(4);
        _out.WriteByte(0b0000_0101);
        WriteU16(_out, Math.Clamp(_pendingDelay, 0, 65535));
        _out.WriteByte(Transparent);
        _out.WriteByte(0);
        _out.Write(_pending);
        _pending = null;
        FramesWritten++;
    }

    static void WriteU16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    /// <summary>Write the last frame and the trailer.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        FlushPending();
        _out.WriteByte(0x3B);
        _out.Flush();
    }

    public void Dispose() => Finish();
}

/// <summary>Median-cut palette over a 15-bit colour histogram.</summary>
public static class MedianCut
{
    sealed class Box
    {
        public List<int> Bins = new();
        public long Count;
        public int Range(int ch, out int lo)
        {
            int min = 31, max = 0;
            foreach (var b in Bins) { int v = b >> (10 - ch * 5) & 31; if (v < min) min = v; if (v > max) max = v; }
            lo = min;
            return max - min;
        }
    }

    /// <summary>
    /// Returns the palette (0xRRGGBB, at most maxColors) and a lookup from 15-bit colour to
    /// palette index. Bins never seen are filled lazily by <see cref="Nearest"/> (marked -1).
    /// </summary>
    public static (int[] palette, int[] lut) Build(int[] hist, long[] sumR, long[] sumG, long[] sumB, int maxColors)
    {
        var lut = new int[32768];
        Array.Fill(lut, -1);
        var first = new Box();
        for (int i = 0; i < hist.Length; i++)
            if (hist[i] > 0) { first.Bins.Add(i); first.Count += hist[i]; }
        if (first.Bins.Count == 0) return (new[] { 0 }, lut);

        var boxes = new List<Box> { first };
        while (boxes.Count < maxColors)
        {
            // split the box with the widest colour range (weighted by its pixel count)
            Box? best = null; int bestCh = 0; double bestScore = 0;
            foreach (var b in boxes)
            {
                if (b.Bins.Count < 2) continue;
                for (int ch = 0; ch < 3; ch++)
                {
                    double score = b.Range(ch, out _) * Math.Sqrt(b.Count);
                    if (score > bestScore) { bestScore = score; best = b; bestCh = ch; }
                }
            }
            if (best == null) break;
            int shift = 10 - bestCh * 5;
            best.Bins.Sort((x, y) => (x >> shift & 31).CompareTo(y >> shift & 31));
            long half = best.Count / 2, acc = 0;
            int cut = 1;
            for (int i = 0; i < best.Bins.Count - 1; i++)
            {
                acc += hist[best.Bins[i]];
                cut = i + 1;
                if (acc >= half) break;
            }
            var a = new Box(); var c = new Box();
            for (int i = 0; i < best.Bins.Count; i++)
            {
                var target = i < cut ? a : c;
                target.Bins.Add(best.Bins[i]);
                target.Count += hist[best.Bins[i]];
            }
            boxes.Remove(best);
            boxes.Add(a);
            boxes.Add(c);
        }

        var palette = new int[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var bin in boxes[i].Bins) { r += sumR[bin]; g += sumG[bin]; b += sumB[bin]; n += hist[bin]; lut[bin] = i; }
            palette[i] = (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
        }
        return (palette, lut);
    }

    /// <summary>Palette index for a colour, cached per 15-bit bin.</summary>
    public static int Nearest(int[] palette, int[] lut, int r, int g, int b)
    {
        int bin = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
        int hit = lut[bin];
        if (hit >= 0) return hit;
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int c = palette[i];
            int dr = (c >> 16 & 255) - r, dg = (c >> 8 & 255) - g, db = (c & 255) - b;
            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bestD) { bestD = d; best = i; }
        }
        lut[bin] = best;
        return best;
    }
}

/// <summary>GIF LZW compression (8-bit codes), written as 255-byte sub-blocks.</summary>
public static class Lzw
{
    const int MaxCode = 4095, HashSize = 5003;

    public static void Encode(ReadOnlySpan<byte> data, Stream output)
    {
        var bits = new BitPacker(output);
        const int clear = 256, eoi = 257;
        int codeSize = 9, next = 258;
        var htab = new int[HashSize];
        var ctab = new int[HashSize];
        Array.Fill(htab, -1);

        bits.Write(clear, codeSize);
        if (data.Length == 0) { bits.Write(eoi, codeSize); bits.Done(); return; }

        int prefix = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            int c = data[i];
            int key = (c << 12) + prefix;
            int h = (c << 4) ^ prefix;
            if (h >= HashSize) h %= HashSize;
            int step = h == 0 ? 1 : HashSize - h;
            bool found = false;
            while (htab[h] >= 0)
            {
                if (htab[h] == key) { prefix = ctab[h]; found = true; break; }
                h -= step;
                if (h < 0) h += HashSize;
            }
            if (found) continue;

            bits.Write(prefix, codeSize);
            if (next <= MaxCode)
            {
                htab[h] = key;
                ctab[h] = next;
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
                next++;
            }
            else
            {
                bits.Write(clear, codeSize);
                Array.Fill(htab, -1);
                codeSize = 9;
                next = 258;
            }
            prefix = c;
        }
        bits.Write(prefix, codeSize);
        // a code written at the old size may have filled the range: the decoder grows the size first
        if (next == 1 << codeSize && codeSize < 12) codeSize++;
        bits.Write(eoi, codeSize);
        bits.Done();
    }

    sealed class BitPacker
    {
        readonly Stream _s;
        readonly byte[] _block = new byte[255];
        int _len, _acc, _nbits;
        public BitPacker(Stream s) => _s = s;

        public void Write(int code, int size)
        {
            _acc |= code << _nbits;
            _nbits += size;
            while (_nbits >= 8) { Put((byte)_acc); _acc >>= 8; _nbits -= 8; }
        }

        void Put(byte b)
        {
            _block[_len++] = b;
            if (_len == 255) FlushBlock();
        }

        void FlushBlock()
        {
            if (_len == 0) return;
            _s.WriteByte((byte)_len);
            _s.Write(_block, 0, _len);
            _len = 0;
        }

        public void Done()
        {
            if (_nbits > 0) { Put((byte)_acc); _acc = 0; _nbits = 0; }
            FlushBlock();
        }
    }
}
