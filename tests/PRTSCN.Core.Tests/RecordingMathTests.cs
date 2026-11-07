using System.Drawing;
using PRTSCN.Core;

public class RecordingMathTests
{
    [Theory]
    [InlineData(641, 363, 640, 362)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1, 1, 2, 2)]
    public void EvenSize_drops_the_odd_pixel(int w, int h, int ew, int eh) =>
        Assert.Equal(new Size(ew, eh), RecordingMath.EvenSize(new Size(w, h)));

    [Fact]
    public void FitWithin_scales_wide_monitors_down_to_the_encoder_limit()
    {
        Assert.Equal(new Size(4096, 1152), RecordingMath.FitWithin(new Size(5120, 1440)));
        Assert.Equal(new Size(2160, 3840), RecordingMath.FitWithin(new Size(2160, 3840))); // portrait 4K fits as is
        Assert.Equal(new Size(4096, 2304), RecordingMath.FitWithin(new Size(7680, 4320))); // 8K
        Assert.Equal(new Size(1920, 1080), RecordingMath.FitWithin(new Size(1920, 1080)));
    }

    [Fact]
    public void CropInMonitor_clips_offsets_and_evens()
    {
        var monitor = new Rectangle(1920, 0, 2560, 1440);
        Assert.Equal(new Rectangle(80, 100, 400, 300), RecordingMath.CropInMonitor(new Rectangle(2000, 100, 401, 301), monitor));
        Assert.Equal(new Rectangle(0, 0, 100, 50), RecordingMath.CropInMonitor(new Rectangle(1820, -10, 200, 60), monitor));
        Assert.Equal(Rectangle.Empty, RecordingMath.CropInMonitor(new Rectangle(0, 0, 100, 100), monitor));
    }

    [Fact]
    public void BestMonitor_picks_the_one_holding_most_of_the_area()
    {
        var monitors = new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 2560, 1440) };
        Assert.Equal(1, RecordingMath.BestMonitor(new Rectangle(1800, 0, 400, 400), monitors));
        Assert.Equal(0, RecordingMath.BestMonitor(new Rectangle(100, 100, 50, 50), monitors));
        Assert.Equal(-1, RecordingMath.BestMonitor(new Rectangle(-500, -500, 50, 50), monitors));
    }

    [Fact]
    public void Bitrate_is_ordered_by_quality_and_clamped()
    {
        var hd = new Size(1920, 1080);
        int o = RecordingMath.Bitrate(hd, 30, VideoQuality.Original), h = RecordingMath.Bitrate(hd, 30, VideoQuality.High),
            b = RecordingMath.Bitrate(hd, 30, VideoQuality.Balanced), s = RecordingMath.Bitrate(hd, 30, VideoQuality.Small);
        Assert.True(o > h && h > b && b > s);
        Assert.Equal(500_000, RecordingMath.Bitrate(new Size(16, 16), 15, VideoQuality.Small));
        Assert.Equal(80_000_000, RecordingMath.Bitrate(new Size(4096, 4096), 60, VideoQuality.Original));
    }

    [Fact]
    public void Frame_timing_matches_media_foundation_units()
    {
        Assert.Equal(0, RecordingMath.FrameTime(0, 30));
        Assert.Equal(10_000_000, RecordingMath.FrameTime(30, 30));
        Assert.Equal(166_666, RecordingMath.FrameTime(1, 60));
        Assert.Equal(30, RecordingMath.FrameDue(TimeSpan.FromSeconds(1), 30));
        Assert.Equal(59, RecordingMath.FrameDue(TimeSpan.FromMilliseconds(999), 60));
    }

    [Fact]
    public void Clock_and_file_size_read_well()
    {
        Assert.Equal("00:00", RecordingMath.Clock(TimeSpan.Zero));
        Assert.Equal("01:05", RecordingMath.Clock(TimeSpan.FromSeconds(65)));
        Assert.Equal("1:02:03", RecordingMath.Clock(new TimeSpan(1, 2, 3)));
        Assert.Equal("820 KB", RecordingMath.FileSize(820_000));
        Assert.Equal("12.4 MB", RecordingMath.FileSize(12_400_000));
        Assert.Equal("1.20 GB", RecordingMath.FileSize(1_200_000_000));
    }

    [Fact]
    public void Video_name_is_sortable_and_mp4() =>
        Assert.Equal("PRTSCN 2026-11-07 09-05-03.mp4", RecordingMath.DefaultVideoName(new DateTime(2026, 11, 7, 9, 5, 3)));

    [Fact]
    public void Recording_settings_have_sane_defaults_and_are_clamped()
    {
        var d = new AppSettings().Normalize();
        Assert.Equal(AppSettings.KeyPrintScreen | AppSettings.ModControl | AppSettings.ModAlt, d.RecordHotkey);
        // the High Contrast combo saved by v0.5.0 is moved off
        Assert.Equal(d.RecordHotkey, new AppSettings { RecordHotkey = AppSettings.HighContrastCombo }.Normalize().RecordHotkey);
        Assert.Equal(AppSettings.KeyPause, d.PauseHotkey);
        Assert.Equal(30, d.VideoFps);
        Assert.Equal(VideoQuality.High, d.VideoQuality);
        Assert.Equal(3, d.RecordCountdown);
        Assert.False(string.IsNullOrWhiteSpace(d.VideoFolder));

        var bad = new AppSettings { VideoFps = 24, RecordCountdown = 99, RecordMaxMinutes = -5, VideoFolder = " " }.Normalize();
        Assert.Equal(30, bad.VideoFps);
        Assert.Equal(10, bad.RecordCountdown);
        Assert.Equal(0, bad.RecordMaxMinutes);
        Assert.Equal(AppSettings.DefaultVideoFolder(), bad.VideoFolder);
    }

    [Fact]
    public void Clone_is_a_deep_copy()
    {
        var a = new AppSettings { VideoQuality = VideoQuality.Small, VideoFps = 60 };
        var b = a.Clone();
        b.VideoFps = 15;
        Assert.Equal(60, a.VideoFps);
        Assert.Equal(VideoQuality.Small, b.VideoQuality);
    }
}
