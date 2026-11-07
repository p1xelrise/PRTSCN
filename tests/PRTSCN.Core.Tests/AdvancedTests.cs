using System.Drawing;
using PRTSCN.Core;
using Xunit;

namespace PRTSCN.Core.Tests;

public class ZoomCameraTests
{
    static readonly Rectangle View = new(0, 0, 1920, 1080);

    [Fact]
    public void StaysOutWithoutClicks()
    {
        var cam = new ZoomCamera(View, 2);
        var r = cam.Update(new Point(500, 500), 1);
        Assert.Equal(new RectangleF(0, 0, 1920, 1080), r);
        Assert.False(cam.Zoomed);
    }

    [Fact]
    public void ZoomsInOnClickThenOutAfterHold()
    {
        var cam = new ZoomCamera(View, 2);
        cam.Click(new Point(960, 540));
        RectangleF r = default;
        for (int i = 0; i < 20; i++) r = cam.Update(new Point(960, 540), 0.05); // 1 s
        Assert.Equal(960, r.Width, 1);
        Assert.Equal(540, r.Height, 1);
        Assert.Equal(480, r.X, 1); // centred on the click
        for (int i = 0; i < 80; i++) r = cam.Update(new Point(960, 540), 0.05); // 4 s more: hold ended, zoomed out
        Assert.False(cam.Zoomed);
        Assert.Equal(1920, r.Width, 1);
    }

    [Fact]
    public void ZoomedViewStaysInsideTheRecording()
    {
        var cam = new ZoomCamera(View, 3);
        cam.Click(new Point(5, 5));
        RectangleF r = default;
        for (int i = 0; i < 20; i++) r = cam.Update(new Point(1919, 1079), 0.05);
        Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= 1920.01f && r.Bottom <= 1080.01f);
    }

    [Fact]
    public void ClicksKeepItZoomed()
    {
        var cam = new ZoomCamera(View, 2);
        for (int s = 0; s < 10; s++)
        {
            cam.Click(new Point(100, 100));
            for (int i = 0; i < 20; i++) cam.Update(new Point(100, 100), 0.05);
        }
        Assert.True(cam.Zoomed);
        Assert.Equal(2, cam.Factor, 2);
    }
}

public class PixelateTests
{
    [Fact]
    public void AveragesEachBlockInsideTheRectOnly()
    {
        int w = 4, h = 2, stride = w * 4;
        var px = new byte[stride * h];
        // left 2x2 block: values 0, 40, 80, 120 in blue; right half 200
        byte[] blue = { 0, 40, 200, 200, 80, 120, 200, 200 };
        for (int i = 0; i < 8; i++) px[i * 4] = blue[i];
        Pixelate.Apply(px, stride, w, h, new Rectangle(0, 0, 2, 2), 2);
        Assert.Equal(60, px[0]);
        Assert.Equal(60, px[4]);
        Assert.Equal(60, px[stride]);
        Assert.Equal(200, px[8]); // outside the rect: untouched
    }

    [Fact]
    public void ClipsToTheFrame()
    {
        var px = new byte[16];
        Pixelate.Apply(px, 8, 2, 2, new Rectangle(-5, -5, 100, 100), 8); // no throw
        Pixelate.Apply(px, 8, 2, 2, new Rectangle(10, 10, 5, 5), 8);
    }

    [Fact]
    public void BoxToPixelsCoversTheArea()
    {
        var r = Pixelate.ToPixels(new BlurBox(new TimeRange(0, 1), 0.25f, 0.5f, 0.5f, 0.25f), 100, 80);
        Assert.Equal(Rectangle.FromLTRB(25, 40, 75, 60), r);
    }
}

public class BlurEditTests
{
    [Fact]
    public void AddNormalisesAndUndoRestores()
    {
        var e = new EditList(100);
        var before = e.Save();
        Assert.True(e.AddBlur(new BlurBox(new TimeRange(10, 50), 0.6f, 0.6f, -0.2f, -0.2f)));
        var b = e.Blurs[0];
        Assert.Equal(0.4f, b.X, 3);
        Assert.Equal(0.2f, b.W, 3);
        Assert.False(e.IsEmpty);
        Assert.Equal(0, e.BlurAt(20, 0.5f, 0.5f));
        Assert.Equal(-1, e.BlurAt(60, 0.5f, 0.5f));
        e.Restore(before);
        Assert.Empty(e.Blurs);
    }

    [Fact]
    public void RefusesTinyBoxes()
    {
        var e = new EditList(100);
        Assert.False(e.AddBlur(new BlurBox(new TimeRange(0, 100), 0.5f, 0.5f, 0.001f, 0.3f)));
        Assert.False(e.AddBlur(new BlurBox(new TimeRange(50, 50), 0.1f, 0.1f, 0.3f, 0.3f)));
    }

    [Fact]
    public void BlurInACutPartIsHidden()
    {
        var e = new EditList(100_000_000);
        e.AddBlur(new BlurBox(new TimeRange(20_000_000, 30_000_000), 0.1f, 0.1f, 0.2f, 0.2f));
        e.AddBlur(new BlurBox(new TimeRange(25_000_000, 50_000_000), 0.1f, 0.1f, 0.2f, 0.2f));
        Assert.Equal(0, e.HiddenBlurs);
        e.AddCut(new TimeRange(20_000_000, 30_000_000));
        Assert.True(e.BlurHidden(e.Blurs[0]));
        Assert.False(e.BlurHidden(e.Blurs[1]));
        Assert.Equal(1, e.HiddenBlurs);
    }

    [Fact]
    public void SetReplacesAndClamps()
    {
        var e = new EditList(100);
        e.AddBlur(new BlurBox(new TimeRange(10, 50), 0.1f, 0.1f, 0.2f, 0.2f));
        Assert.True(e.SetBlur(0, new BlurBox(new TimeRange(30, 500), 0.9f, 0.5f, 0.3f, 0.2f)));
        var b = e.Blurs[0];
        Assert.Equal(new TimeRange(30, 100), b.Range);
        Assert.Equal(0.1f, b.W, 3); // clipped at the right edge
        Assert.False(e.SetBlur(0, new BlurBox(new TimeRange(60, 60), 0.1f, 0.1f, 0.2f, 0.2f)));
        Assert.False(e.SetBlur(3, b));
        Assert.Equal(new TimeRange(30, 100), e.Blurs[0].Range);
    }
}

public class ReplayPlanTests
{
    static readonly List<ReplaySegment> Segs = new()
    {
        new("a", 0, 100), new("b", 100, 200), new("c", 200, 250),
    };

    [Fact]
    public void TakesTheNewestLength()
    {
        var parts = ReplayPlan.LastPart(Segs, 120);
        Assert.Equal(2, parts.Count);
        Assert.Equal(("b", new TimeRange(30, 100)), parts[0]);
        Assert.Equal(("c", new TimeRange(0, 50)), parts[1]);
    }

    [Fact]
    public void ShorterBufferGivesWhatThereIs()
    {
        var parts = ReplayPlan.LastPart(Segs, 10_000);
        Assert.Equal(3, parts.Count);
        Assert.Equal(new TimeRange(0, 100), parts[0].Range);
        Assert.Empty(ReplayPlan.LastPart(new List<ReplaySegment>(), 10));
    }

    [Fact]
    public void ExpiresOnlyWhatIsNotNeeded()
    {
        var old = ReplayPlan.Expired(Segs, 120);
        Assert.Single(old);
        Assert.Equal("a", old[0].Path);
    }
}

public class ScheduleTests
{
    [Fact]
    public void NextAtRollsToTomorrow()
    {
        var now = new DateTime(2026, 11, 7, 15, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 7, 16, 30, 0), RecordSchedule.NextAt(now, new TimeSpan(16, 30, 0)));
        Assert.Equal(new DateTime(2026, 11, 8, 9, 0, 0), RecordSchedule.NextAt(now, new TimeSpan(9, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 8, 15, 0, 0), RecordSchedule.NextAt(now, new TimeSpan(15, 0, 0)));
    }

    [Fact]
    public void Describes()
    {
        var now = new DateTime(2026, 11, 7, 15, 0, 0);
        Assert.Equal("today 16:30", RecordSchedule.Describe(new DateTime(2026, 11, 7, 16, 30, 0), now));
        Assert.Equal("tomorrow 09:05", RecordSchedule.Describe(new DateTime(2026, 11, 8, 9, 5, 0), now));
    }

    [Fact]
    public void SettingsKeepAdvancedValuesInRange()
    {
        var s = new AppSettings { AutoZoomLevel = 7, WatermarkOpacity = 500, ReplaySeconds = 45, WatermarkText = "  hi  ",
            Schedule = new RecordSchedule { Minutes = 9999 } }.Normalize();
        Assert.Equal(200, s.AutoZoomLevel);
        Assert.Equal(100, s.WatermarkOpacity);
        Assert.Equal(60, s.ReplaySeconds);
        Assert.Equal("hi", s.WatermarkText);
        Assert.Equal(600, s.Schedule!.Minutes);
        Assert.True(s.HasWatermark);
    }
}

public class WatermarkMathTests
{
    [Fact]
    public void PlacesInCorners()
    {
        var f = new SizeF(1000, 500);
        var m = new SizeF(100, 20);
        Assert.Equal(new RectangleF(10, 10, 100, 20), WatermarkMath.Place(f, m, Corner.TopLeft, 10));
        Assert.Equal(new RectangleF(890, 470, 100, 20), WatermarkMath.Place(f, m, Corner.BottomRight, 10));
    }

    [Fact]
    public void FitsBigImages()
    {
        var s = WatermarkMath.Fit(new SizeF(1000, 100), new SizeF(1000, 800));
        Assert.Equal(200, s.Width, 2);
        Assert.Equal(20, s.Height, 2);
        Assert.Equal(new SizeF(50, 50), WatermarkMath.Fit(new SizeF(50, 50), new SizeF(1000, 800)));
    }
}
