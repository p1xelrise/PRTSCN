using PRTSCN.Core;

public class ScrollStitcherTests
{
    const int W = 200, H = 300;

    /// <summary>A tall "page" where every row is different, like text.</summary>
    static int[] Page(int height, int seed = 7)
    {
        var rnd = new Random(seed);
        var p = new int[W * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < W; x++)
                p[y * W + x] = (x % 13 == 0 || rnd.Next(6) == 0) ? rnd.Next(0x1000000) : 0xFFFFFF;
        return p;
    }

    /// <summary>
    /// The viewport at a scroll offset. Optional: sticky header/footer rows, a fixed side panel
    /// (static columns on the left), a moving scrollbar thumb, a hover-highlighted row band.
    /// </summary>
    static int[] View(int[] page, int offset, int header = 0, int footer = 0, int sidebar = 0, int hoverY = -1, int noise = 0)
    {
        var rnd = new Random(offset);
        var v = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int c;
                if (x < sidebar) c = 0x303540 + (y * 7 % 50) * 0x10101;           // fixed panel: never moves
                else if (y < header) c = 0x203040 + x;
                else if (y >= H - footer) c = 0x405060 + x;
                else c = page[(offset + y) * W + x];
                if (x >= W - 12) c = (y > offset / 10 && y < offset / 10 + 40) ? 0x888888 : 0xEEEEEE; // scrollbar thumb moves
                if (hoverY >= 0 && y >= hoverY && y < hoverY + 10 && x >= sidebar) c = (c & 0xFEFEFE) >> 1;  // hover highlight
                if (noise > 0) c = Jitter(c, rnd.Next(-noise, noise + 1));
                v[y * W + x] = c;
            }
        return v;
    }

    static int Jitter(int c, int d)
    {
        int r = Math.Clamp(((c >> 16) & 255) + d, 0, 255), g = Math.Clamp(((c >> 8) & 255) + d, 0, 255), b = Math.Clamp((c & 255) + d, 0, 255);
        return (r << 16) | (g << 8) | b;
    }

    static StitchMatch M(int[] a, int[] b, int preferred = 0) => ScrollStitcher.Match(a, b, W, H, preferred);

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    [InlineData(250)]
    public void Finds_scroll_offset(int dy)
    {
        var page = Page(2000);
        var m = M(View(page, 100), View(page, 100 + dy));
        Assert.True(m.Found);
        Assert.Equal(dy, m.Dy);
    }

    [Fact]
    public void Ignores_sticky_header_and_footer()
    {
        var page = Page(2000);
        var m = M(View(page, 0, header: 40, footer: 30), View(page, 90, header: 40, footer: 30));
        Assert.True(m.Found);
        Assert.Equal(90, m.Dy);
        Assert.Equal(40, m.Top);
        Assert.Equal(30, m.Bottom);
    }

    [Fact]
    public void Ignores_a_fixed_side_panel()
    {
        // e.g. a mail or docs app: the left panel stays put while the content scrolls
        var page = Page(2000);
        var m = M(View(page, 0, sidebar: 60), View(page, 110, sidebar: 60));
        Assert.True(m.Found);
        Assert.Equal(110, m.Dy);
    }

    [Fact]
    public void Survives_a_moving_hover_highlight_and_render_noise()
    {
        var page = Page(2000);
        var m = M(View(page, 0, hoverY: 140, noise: 6), View(page, 80, hoverY: 150, noise: 6));
        Assert.True(m.Found);
        Assert.Equal(80, m.Dy);
    }

    [Fact]
    public void Same_frame_means_end_of_page()
    {
        var page = Page(2000);
        var a = View(page, 500);
        Assert.True(M(a, a).Unchanged);
        Assert.False(M(a, a).Found);
    }

    [Fact]
    public void Only_a_blinking_caret_changed_means_end_of_page()
    {
        var page = Page(2000);
        var a = View(page, 500);
        var b = (int[])a.Clone();
        for (int y = 100; y < 118; y++) b[y * W + 70] = 0x000000;
        Assert.True(M(a, b).Unchanged);
    }

    [Fact]
    public void Unrelated_frames_do_not_match()
    {
        Assert.False(M(View(Page(2000, 1), 0), View(Page(2000, 2), 0)).Found);
    }

    [Fact]
    public void Repeated_rows_prefer_the_previous_offset()
    {
        // A page made of identical 50-row blocks matches at every multiple of 50 apart from the true shift.
        var block = Page(50, 3);
        var page = new int[W * 2000];
        for (int y = 0; y < 2000; y++) Array.Copy(block, (y % 50) * W, page, y * W, W);
        // Moving 120 rows matches at 20, 70, 120, 170 and 220.
        var m = M(View(page, 0), View(page, 120), preferred: 115);
        Assert.True(m.Found);
        Assert.Equal(120, m.Dy);
    }
}

public class ScrollAssemblerTests
{
    const int W = 120, H = 200;

    static int[] Page(int height)
    {
        var rnd = new Random(11);
        var p = new int[W * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < W; x++)
                p[y * W + x] = (y % 9 == 0 || rnd.Next(5) == 0) ? rnd.Next(0x1000000) : 0xFFFFFF;
        return p;
    }

    static int[] View(int[] page, int offset, int header, int footer)
    {
        var v = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                v[y * W + x] = y < header ? 0x102030 + x : y >= H - footer ? 0x405060 + x : page[(offset + y - header) * W + x];
        return v;
    }

    [Fact]
    public void Rebuilds_the_page_exactly_with_header_and_footer()
    {
        const int header = 20, footer = 15, content = H - header - footer;
        var page = Page(1000);
        int[] offsets = { 0, 70, 150, 260, 330, 410, 500 };
        var frames = offsets.Select(o => View(page, o, header, footer)).ToList();

        var asm = new ScrollAssembler(W, H);
        foreach (var f in frames) Assert.True(asm.Add(f).match.Found);
        var segs = asm.Finish();
        var img = ScrollAssembler.Compose(segs, i => frames[i], W);
        int height = img.Length / W;

        // header once, page rows 0 .. last offset + content, footer once
        Assert.Equal(header + offsets[^1] + content + footer, height);
        Assert.Equal(height, asm.Height);
        for (int y = 0; y < height; y++)
        {
            int expected(int x) => y < header ? 0x102030 + x
                : y >= height - footer ? 0x405060 + x
                : page[(y - header) * W + x];
            for (int x = 0; x < W; x++) Assert.Equal(expected(x), img[y * W + x]);
        }
    }

    [Fact]
    public void No_match_keeps_the_first_frame()
    {
        var asm = new ScrollAssembler(W, H);
        var a = View(Page(1000), 0, 0, 0);
        asm.Add(a);
        var segs = asm.Finish();
        Assert.Single(segs);
        Assert.Equal(new ScrollAssembler.Segment(0, 0, H), segs[0]);
    }
}
