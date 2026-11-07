namespace PRTSCN;

/// <summary>Developer check: draws the Options window and the recording bar to PNG files.</summary>
internal static class UiShots
{
    public static void Run(string folder)
    {
        OptionsForm.RenderShots(folder);

        var area = Screen.PrimaryScreen!.Bounds;
        area = new Rectangle(area.X + 200, area.Y + 300, 800, 450);
        using var bar = new Recording.RecordControlBar(() => { }, () => { }, () => { }, () => { }, "Ctrl+Alt+PrintScreen", "Pause", () => { }, meters: true, toggleDraw: () => { }, pickInk: (_, _) => { });
        bar.SetInk(Theme.Ink[4], 9);
        bar.SetDrawing(true);
        bar.PlaceNear(area);
        foreach (var (paused, muted, name) in new[] { (false, false, "recording"), (true, false, "paused"), (false, true, "mic-muted") })
        {
            bar.SetState(TimeSpan.FromSeconds(83), 12_400_000, paused, 0.4f, 0.08f, muted);
            Application.DoEvents();
            using var bmp = new Bitmap(bar.Width, bar.Height);
            bar.DrawToBitmap(bmp, new Rectangle(Point.Empty, bar.Size));
            bmp.Save(Path.Combine(folder, $"bar-{name}.png"));
        }
        bar.SetInk(Theme.Ink[0], 12); // a width from [ ] / the wheel, not one of the four
        Application.DoEvents();
        using (var bmp = new Bitmap(bar.Width, bar.Height))
        {
            bar.DrawToBitmap(bmp, new Rectangle(Point.Empty, bar.Size));
            bmp.Save(Path.Combine(folder, "bar-drawing-custom.png"));
        }
        bar.Close();

        // The record screen, drawn off-screen (never shown).
        var virt = ScreenGrabber.VirtualScreen;
        using var shot = ScreenGrabber.Capture(virt);
        var settings = new PRTSCN.Core.AppSettings { RecordKeys = PRTSCN.Core.KeyDisplay.Shortcuts, RecordMagnifier = true }.Normalize();
        using var overlay = new CaptureOverlay(shot, virt, new List<PRTSCN.Core.WindowInfo>(), false, settings, (_, _, _) => { },
            onRecord: _ => { }, recordKind: Recording.RecordKind.Area);
        _ = overlay.Handle;
        overlay.Bounds = virt;
        var primary = Screen.PrimaryScreen!.Bounds;
        var sel = new Rectangle(primary.X - virt.X + 300, primary.Y - virt.Y + 200, 1280, 720);
        var region = overlay.PreviewRecordMode(Recording.RecordKind.Follow, sel);
        region.Inflate(20, 20);
        region.Intersect(new Rectangle(Point.Empty, virt.Size));
        using var full = new Bitmap(virt.Width, virt.Height);
        overlay.DrawToBitmap(full, new Rectangle(Point.Empty, virt.Size));
        using var crop = full.Clone(region, full.PixelFormat);
        crop.Save(Path.Combine(folder, "record-bar-follow.png"));
    }
}
