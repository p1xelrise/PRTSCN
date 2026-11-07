using System.Drawing;
using PRTSCN.Core;

public class WindowPickerTests
{
    static readonly Rectangle Virt = new(-1280, 0, 1280 + 1920, 1080); // monitor on the left

    [Fact]
    public void Prepare_offsets_clips_and_drops_offscreen()
    {
        var raw = new[]
        {
            new WindowInfo(new Rectangle(100, 100, 800, 600), "Editor"),
            new WindowInfo(new Rectangle(-1290, -10, 1300, 1100), "Maximised on left"), // spills past the edge
            new WindowInfo(new Rectangle(-32000, -32000, 160, 28), "Minimised ghost"),
            new WindowInfo(new Rectangle(500, 500, 4, 4), "Tiny"),
        };
        var w = WindowPicker.Prepare(raw, Virt);
        Assert.Equal(2, w.Count);
        Assert.Equal(new Rectangle(1380, 100, 800, 600), w[0].Bounds);
        Assert.Equal(new Rectangle(0, 0, 1290, 1080), w[1].Bounds); // clipped to the desktop
    }

    [Fact]
    public void HitTest_returns_topmost()
    {
        var w = new List<WindowInfo>
        {
            new(new Rectangle(100, 100, 200, 200), "Dialog on top"),
            new(new Rectangle(0, 0, 1000, 800), "Main window"),
        };
        Assert.Equal(0, WindowPicker.HitTest(w, new Point(150, 150)));
        Assert.Equal(1, WindowPicker.HitTest(w, new Point(50, 50)));
        Assert.Equal(-1, WindowPicker.HitTest(w, new Point(1500, 50)));
    }

    [Fact]
    public void Label_shortens_long_titles()
    {
        var l = WindowPicker.Label(new WindowInfo(new Rectangle(0, 0, 640, 480), new string('x', 100)), 10);
        Assert.StartsWith("xxxxxxxxx…", l);
        Assert.EndsWith("640 × 480", l);
        Assert.Equal("640 × 480", WindowPicker.Label(new WindowInfo(new Rectangle(0, 0, 640, 480), "  ")));
    }
}
