using System.Drawing;
using PRTSCN.Core;

public class SelectionMathTests
{
    [Fact]
    public void FromPoints_any_direction()
    {
        Assert.Equal(new Rectangle(10, 20, 91, 51), SelectionMath.FromPoints(new(100, 70), new(10, 20)));
        Assert.Equal(new Rectangle(5, 5, 1, 1), SelectionMath.FromPoints(new(5, 5), new(5, 5)));
    }

    [Fact]
    public void FromPoints_negative_coords_left_monitor()
    {
        Assert.Equal(new Rectangle(-1920, 0, 1921, 11), SelectionMath.FromPoints(new(-1920, 0), new(0, 10)));
    }

    [Theory]
    [InlineData(100, 100, Handle.NW)]
    [InlineData(299, 100, Handle.NE)]
    [InlineData(100, 199, Handle.SW)]
    [InlineData(299, 199, Handle.SE)]
    [InlineData(199, 100, Handle.N)]
    [InlineData(199, 199, Handle.S)]
    [InlineData(100, 149, Handle.W)]
    [InlineData(299, 149, Handle.E)]
    [InlineData(150, 102, Handle.N)]   // along the top edge, away from handles
    [InlineData(200, 150, Handle.Move)]
    [InlineData(50, 50, Handle.None)]
    public void HitTest(int x, int y, Handle expected)
    {
        var sel = new Rectangle(100, 100, 200, 100);
        Assert.Equal(expected, SelectionMath.HitTest(sel, new Point(x, y), 4));
    }

    [Fact]
    public void Resize_se_grows()
    {
        var r = SelectionMath.Resize(new Rectangle(0, 0, 100, 100), Handle.SE, 10, 20);
        Assert.Equal(new Rectangle(0, 0, 110, 120), r);
    }

    [Fact]
    public void Resize_crossing_edge_flips_and_stays_valid()
    {
        var r = SelectionMath.Resize(new Rectangle(100, 100, 50, 50), Handle.W, 80, 0);
        Assert.Equal(new Rectangle(149, 100, 32, 50), r);
        Assert.True(r.Width >= 1 && r.Height >= 1);
    }

    [Fact]
    public void Move_keeps_size()
    {
        var r = SelectionMath.Resize(new Rectangle(10, 10, 40, 30), Handle.Move, -5, 7);
        Assert.Equal(new Rectangle(5, 17, 40, 30), r);
    }

    [Fact]
    public void ClampMove_keeps_inside()
    {
        var r = SelectionMath.ClampMove(new Rectangle(1900, -10, 100, 100), new Rectangle(0, 0, 1920, 1080));
        Assert.Equal(new Rectangle(1820, 0, 100, 100), r);
    }
}
