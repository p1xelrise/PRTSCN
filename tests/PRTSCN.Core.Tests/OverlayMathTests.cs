using System.Drawing;
using PRTSCN.Core;

public class OverlayMathTests
{
    static readonly Rectangle Desk = new(0, 0, 1920, 1080);

    [Fact]
    public void Camera_stays_still_while_pointer_is_in_the_middle()
    {
        var cam = new FollowCamera(new Rectangle(320, 180, 1280, 720), Desk);
        var start = cam.View;
        for (int i = 0; i < 30; i++) cam.Update(new Point(960, 540), 0.033);
        Assert.Equal(start, cam.View);
    }

    [Fact]
    public void Camera_glides_toward_pointer_near_edge_and_never_leaves_bounds()
    {
        var cam = new FollowCamera(new Rectangle(320, 180, 1280, 720), Desk);
        var first = cam.Update(new Point(1900, 1070), 0.033);
        Assert.True(first.X > 320 && first.X < 640, "first step is partial");
        Rectangle v = first;
        for (int i = 0; i < 120; i++) v = cam.Update(new Point(1900, 1070), 0.033);
        Assert.Equal(new Rectangle(640, 360, 1280, 720), v); // clamped to the bottom-right corner
    }

    [Fact]
    public void Camera_view_shrinks_to_bounds_when_larger()
    {
        var cam = new FollowCamera(new Rectangle(0, 0, 4000, 3000), Desk);
        Assert.Equal(Desk, cam.View);
    }

    [Theory]
    [InlineData(0x41, false, false, false, KeyDisplay.All, "a", true)]
    [InlineData(0x41, false, false, true, KeyDisplay.All, "A", true)]
    [InlineData(0x41, false, false, false, KeyDisplay.Shortcuts, null, false)]
    [InlineData(0x53, true, false, true, KeyDisplay.Shortcuts, "Ctrl + Shift + S", false)]
    [InlineData(0x70, false, false, false, KeyDisplay.Shortcuts, "F1", false)]
    [InlineData(0x1B, false, false, false, KeyDisplay.Shortcuts, "Esc", false)]
    [InlineData(0x0D, false, false, false, KeyDisplay.Shortcuts, null, false)]
    [InlineData(0x0D, false, false, false, KeyDisplay.All, "Enter", false)]
    [InlineData(0x11, true, false, false, KeyDisplay.All, null, false)]
    [InlineData(0x41, true, false, false, KeyDisplay.Off, null, false)]
    [InlineData(0x63, false, false, false, KeyDisplay.All, "3", true)]
    public void Keystroke_labels(int vk, bool ctrl, bool alt, bool shift, KeyDisplay mode, string? expected, bool typed)
    {
        Assert.Equal(expected, KeystrokeText.Describe(vk, ctrl, alt, shift, false, mode, out bool t));
        Assert.Equal(typed, t);
    }

    [Fact]
    public void Unit_scales_with_output_and_is_clamped()
    {
        Assert.Equal(1f, OverlayMath.Unit(new Size(1280, 720)));
        Assert.Equal(1.5f, OverlayMath.Unit(new Size(1920, 1080)));
        Assert.Equal(0.6f, OverlayMath.Unit(new Size(320, 200)));
        Assert.Equal(2.5f, OverlayMath.Unit(new Size(8000, 8000)));
    }

    [Fact]
    public void Key_opacity_holds_then_fades()
    {
        Assert.Equal(1f, OverlayMath.KeyOpacity(0));
        Assert.Equal(1f, OverlayMath.KeyOpacity(1.5));
        Assert.InRange(OverlayMath.KeyOpacity(1.8), 0.49f, 0.51f);
        Assert.Equal(0f, OverlayMath.KeyOpacity(3));
    }

    [Fact]
    public void Ease_out_and_output_mapping()
    {
        Assert.Equal(0f, OverlayMath.EaseOut(0));
        Assert.Equal(1f, OverlayMath.EaseOut(2));
        Assert.True(OverlayMath.EaseOut(0.5) > 0.5f);
        var p = OverlayMath.ToOutput(new Point(500, 300), new Point(-100, 0), new Rectangle(100, 100, 800, 600), 0.5f);
        Assert.Equal(new PointF(250, 100), p);
    }
}
