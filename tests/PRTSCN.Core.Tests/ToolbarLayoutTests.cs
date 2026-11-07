using System.Drawing;
using PRTSCN.Core;

public class ToolbarLayoutTests
{
    static readonly Rectangle Screen = new(0, 0, 1920, 1080);
    static readonly Size VBar = new(40, 270);
    static readonly Size HBar = new(150, 40);

    [Fact]
    public void Vertical_goes_right_when_room()
    {
        var r = ToolbarLayout.PlaceVertical(new Rectangle(100, 100, 400, 300), VBar, Screen, 8);
        Assert.Equal(508, r.X); Assert.Equal(100, r.Y);
    }

    [Fact]
    public void Vertical_goes_left_at_right_edge()
    {
        var r = ToolbarLayout.PlaceVertical(new Rectangle(1500, 100, 420, 300), VBar, Screen, 8);
        Assert.Equal(1500 - 8 - 40, r.X);
    }

    [Fact]
    public void Vertical_goes_inside_for_fullscreen_selection()
    {
        var r = ToolbarLayout.PlaceVertical(Screen, VBar, Screen, 8);
        Assert.True(Screen.Contains(r));
    }

    [Fact]
    public void Vertical_clamped_to_bottom()
    {
        var r = ToolbarLayout.PlaceVertical(new Rectangle(100, 1000, 100, 50), VBar, Screen, 8);
        Assert.Equal(1080 - 270, r.Y);
    }

    [Fact]
    public void Horizontal_below_then_above_then_inside()
    {
        Assert.Equal(408, ToolbarLayout.PlaceHorizontal(new Rectangle(100, 100, 400, 300), HBar, Screen, 8).Y);
        Assert.Equal(500 - 8 - 40, ToolbarLayout.PlaceHorizontal(new Rectangle(100, 500, 400, 570), HBar, Screen, 8).Y);
        Assert.True(Screen.Contains(ToolbarLayout.PlaceHorizontal(Screen, HBar, Screen, 8)));
    }

    [Fact]
    public void Bars_stay_on_selection_monitor_with_left_monitor_negative_coords()
    {
        var left = new Rectangle(-1280, 0, 1280, 1024);
        var screens = new[] { left, Screen };
        var sel = new Rectangle(-600, 200, 590, 300);
        var mon = ToolbarLayout.ScreenFor(sel, screens);
        Assert.Equal(left, mon);
        var v = ToolbarLayout.PlaceVertical(sel, VBar, mon, 8);
        Assert.True(left.Contains(v));
    }

    [Fact]
    public void Size_label_moves_inside_at_top_edge()
    {
        var p = ToolbarLayout.PlaceSizeLabel(new Rectangle(10, 0, 300, 300), new Size(80, 20), Screen, 6);
        Assert.Equal(6, p.Y);
        var q = ToolbarLayout.PlaceSizeLabel(new Rectangle(10, 100, 300, 300), new Size(80, 20), Screen, 6);
        Assert.Equal(74, q.Y);
    }
}
