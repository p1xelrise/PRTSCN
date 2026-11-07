using PRTSCN.Core;

public class SettingsAndNamingTests
{
    [Fact]
    public void Settings_roundtrip_and_normalize()
    {
        var path = Path.Combine(Path.GetTempPath(), "prtscn-test-" + Guid.NewGuid(), "settings.json");
        var s = new AppSettings { Format = ImageFormat.Jpg, JpegQuality = 500, InkColor = "red" };
        SettingsStore.Save(path, s);
        var back = SettingsStore.Load(path);
        Assert.Equal(ImageFormat.Jpg, back.Format);
        Assert.Equal(100, back.JpegQuality);
        Assert.Equal("#ff3347", back.InkColor);
        Assert.Contains("\"Jpg\"", File.ReadAllText(path));
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prtscn-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var s = SettingsStore.Load(path);
        Assert.Equal(AppSettings.KeyPrintScreen, s.CaptureHotkey);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Default_name()
    {
        Assert.Equal("PRTSCN_2026-11-01_14-03-22.png",
            FileNaming.DefaultName(new DateTime(2026, 11, 1, 14, 3, 22), ImageFormat.Png));
    }

    [Fact]
    public void Unique_adds_counter()
    {
        var taken = new HashSet<string> { Path.Combine("d", "a.png"), Path.Combine("d", "a (2).png") };
        Assert.Equal(Path.Combine("d", "a (3).png"), FileNaming.Unique("d", "a.png", taken.Contains));
    }

    [Theory]
    [InlineData("x.JPEG", ImageFormat.Jpg)]
    [InlineData("x.png", ImageFormat.Png)]
    [InlineData("x.bmp", ImageFormat.Png)]
    public void Format_from_path(string p, ImageFormat f) => Assert.Equal(f, FileNaming.FormatFromPath(p, ImageFormat.Png));
}
