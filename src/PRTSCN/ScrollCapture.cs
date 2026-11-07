using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PRTSCN.Core;

namespace PRTSCN;

/// <summary>
/// Scrolling capture: scrolls whatever is under the selected area with the mouse wheel, grabs a
/// frame after each step, lines the frames up (ScrollStitcher / ScrollAssembler) and joins them
/// into one tall image. Stops at the end of the page, on Esc, or at MaxHeight.
/// Every step is written to %APPDATA%\PRTSCN\scroll.log for troubleshooting.
/// </summary>
internal static class ScrollCapture
{
    public const int MaxHeight = 30000;
    public const int MaxFrames = 300;

    public sealed record Result(Bitmap? Image, int Frames, string StopReason);

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PRTSCN", "scroll.log");

    public static async Task<Result> RunAsync(Rectangle region, int stepDelayMs, ToastWindow toast, Rectangle toastScreen)
    {
        var log = new List<string> { $"[{DateTime.Now:u}] PRTSCN {typeof(ScrollCapture).Assembly.GetName().Version} region={region} delay={stepDelayMs}ms toastExcluded={toast.Excluded}" };
        var cursorBefore = Cursor.Position;
        var center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        var park = ParkPoint(region);
        var strips = new List<Bitmap>();
        Bitmap? first = null, last = null;
        string reason = "Reached the end of the page.";
        try
        {
            Native.EscPressed();                     // clear any earlier Esc press
            Native.SetCursorPos(park.X, park.Y);     // no hover effects in the first frame
            first = await GrabStable(region, toast, log); // waits for the capture screen to be gone
            var firstPx = Pixels(first);
            var asm = new ScrollAssembler(first.Width, first.Height);
            asm.Add(firstPx);
            last = first;
            int h = first.Height, notches = 1, unchanged = 0;

            while (true)
            {
                if (Native.EscPressed()) { reason = "Stopped with Esc."; break; }
                if (asm.Height >= MaxHeight) { reason = $"Stopped at the {MaxHeight:N0} px limit."; break; }
                if (asm.Frames >= MaxFrames) { reason = "Stopped at the frame limit."; break; }

                toast.ShowText(toastScreen, "Scrolling capture…", $"{asm.Frames} frames · {asm.Height:N0} px   ·   Esc to stop", avoid: region);
                // The wheel goes to whatever is under the mouse: put it over the area, scroll, move it away again.
                Native.SetCursorPos(center.X, center.Y);
                await Task.Delay(30);
                Native.WheelDown(notches);
                await Task.Delay(30);
                Native.SetCursorPos(park.X, park.Y);
                await Task.Delay(stepDelayMs);

                var next = await Grab(region, toast);
                var px = Pixels(next);
                var (m, added) = asm.Add(px);
                if (!m.Found && !m.Unchanged)
                {
                    // Smooth scrolling may still be moving: give it a moment and look again.
                    log.Add($"  step {asm.Frames}: no match yet ({m}), retrying");
                    await Task.Delay(stepDelayMs + 250);
                    next.Dispose();
                    next = await Grab(region, toast);
                    px = Pixels(next);
                    (m, added) = asm.Add(px);
                }
                log.Add($"  step {asm.Frames}: notches={notches} {m}");

                if (m.Unchanged)
                {
                    next.Dispose();
                    if (++unchanged >= 2) break; // twice in a row: really the end
                    continue;
                }
                if (!m.Found)
                {
                    next.Dispose();
                    reason = "Stopped: the frames didn't line up (the page changed while scrolling, or the area doesn't scroll). Kept what was captured.";
                    break;
                }
                unchanged = 0;

                // Copy out just the rows this frame contributes; keep the newest frame whole for the footer.
                foreach (var s in added)
                    strips.Add(CropRows(s.Frame == asm.Frames - 1 ? next : first, s.Y0, s.Y1));
                if (last != first) last.Dispose();
                last = next;

                // Scroll faster when steps are small, slower when the overlap gets thin.
                int band = h - m.Top - m.Bottom;
                if (m.Dy < band / 4 && notches < 5) notches++;
                else if (m.Dy > band * 6 / 10 && notches > 1) notches--;
            }

            var segs = asm.Finish();
            log.Add($"  done: frames={asm.Frames} height={asm.Height} reason={reason}");
            if (asm.Frames == 1)
                return new Result(new Bitmap(first), 1,
                    "The area didn't scroll. Click into the page first, or select a bigger part of it away from fixed bars.");

            // strips holds every segment except the last frame's footer, in order.
            var footer = segs[^1];
            bool footerFromLast = segs.Count == strips.Count + 1;
            int height = strips.Sum(b => b.Height) + (footerFromLast ? footer.Height : 0);
            var outBmp = new Bitmap(first.Width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                int y = 0;
                foreach (var b in strips) { g.DrawImageUnscaled(b, 0, y); y += b.Height; }
                if (footerFromLast && footer.Height > 0)
                    g.DrawImage(last!, new Rectangle(0, y, first.Width, footer.Height), new Rectangle(0, footer.Y0, first.Width, footer.Height), GraphicsUnit.Pixel);
            }
            return new Result(outBmp, asm.Frames, reason);
        }
        catch (Exception ex)
        {
            log.Add("  error: " + ex);
            throw;
        }
        finally
        {
            Native.SetCursorPos(cursorBefore.X, cursorBefore.Y);
            foreach (var s in strips) s.Dispose();
            if (last != null && last != first) last.Dispose();
            first?.Dispose();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.WriteAllLines(LogPath, log);
            }
            catch { }
        }
    }

    /// <summary>A spot just outside the area, on screen, to leave the mouse while grabbing.</summary>
    static Point ParkPoint(Rectangle r)
    {
        var desk = SystemInformation.VirtualScreen;
        var options = new[]
        {
            new Point(r.Left - 12, r.Top + r.Height / 2),
            new Point(r.Right + 12, r.Top + r.Height / 2),
            new Point(r.Left + r.Width / 2, r.Top - 12),
            new Point(r.Left + r.Width / 2, r.Bottom + 12),
        };
        foreach (var p in options)
            if (desk.Contains(p) && Screen.AllScreens.Any(s => s.Bounds.Contains(p))) return p;
        return new Point(r.Right - 2, r.Bottom - 2); // area covers the whole desktop: the corner is the best we can do
    }

    /// <summary>The first frame: wait until two grabs in a row are the same (the capture screen has gone).</summary>
    static async Task<Bitmap> GrabStable(Rectangle region, ToastWindow toast, List<string> log)
    {
        await Task.Delay(200);
        var a = await Grab(region, toast);
        var pa = Pixels(a);
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(120);
            var b = await Grab(region, toast);
            var pb = Pixels(b);
            if (pa.AsSpan().SequenceEqual(pb)) { a.Dispose(); log.Add($"  first frame stable after {i + 1} checks"); return b; }
            a.Dispose();
            a = b;
            pa = pb;
        }
        log.Add("  first frame never settled (animated page?), using the latest");
        return a;
    }

    static Bitmap CropRows(Bitmap src, int y0, int y1)
    {
        int h = Math.Max(1, y1 - y0);
        var b = new Bitmap(src.Width, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(b);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, h), new Rectangle(0, y0, src.Width, h), GraphicsUnit.Pixel);
        return b;
    }

    static async Task<Bitmap> Grab(Rectangle region, ToastWindow toast)
    {
        if (toast.Excluded || !toast.Visible) return ScreenGrabber.Capture(region);
        // Older Windows can't keep the toast out of captures: hide it for the grab.
        toast.Hide();
        await Task.Delay(60);
        try { return ScreenGrabber.Capture(region); }
        finally { toast.Show(); }
    }

    static int[] Pixels(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var px = new int[b.Width * b.Height];
            if (data.Stride == b.Width * 4) Marshal.Copy(data.Scan0, px, 0, px.Length);
            else for (int y = 0; y < b.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width, b.Width);
            for (int i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
            return px;
        }
        finally { b.UnlockBits(data); }
    }
}
