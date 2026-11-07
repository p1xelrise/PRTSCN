using System.Drawing.Drawing2D;

namespace PRTSCN;

internal enum IconKind { Pen, Line, Arrow, Rect, Marker, Blur, Step, Text, Color, Size, Undo, Copy, Save, Scroll, Print, Close, More,
    Area, Window, Screen, Cursor, Timer, Pause, Play, Restart, Stop, Record, Video, Speaker, Mic, MicOff,
    Follow, AllScreens, Click, Keyboard, Spotlight, Magnifier,
    SkipBack, SkipForward, Scissors, Camera, Folder, Trash, Film }

/// <summary>Original line icons drawn on a 24-unit grid (no third-party icon art).</summary>
internal static class Icons
{
    public static void Draw(Graphics g, IconKind kind, RectangleF box, Color c, float scale, Color ink)
    {
        float u = box.Width / 24f;
        PointF P(float x, float y) => new(box.X + x * u, box.Y + y * u);
        using var pen = new Pen(c, Theme.StrokeIcon * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        switch (kind)
        {
            case IconKind.Pen:
                g.DrawPolygon(pen, new[] { P(16, 3), P(21, 8), P(8.5f, 20.5f), P(3, 21), P(3.5f, 15.5f) });
                g.DrawLine(pen, P(13, 6), P(18, 11));
                break;
            case IconKind.Line:
                g.DrawLine(pen, P(5, 19), P(19, 5));
                break;
            case IconKind.Arrow:
                g.DrawLine(pen, P(5, 19), P(19, 5));
                g.DrawLines(pen, new[] { P(10, 5), P(19, 5), P(19, 14) });
                break;
            case IconKind.Rect:
                using (var path = Theme.Round(new RectangleF(P(4, 6), new SizeF(16 * u, 12 * u)), 2 * u)) g.DrawPath(pen, path);
                break;
            case IconKind.Marker:
                using (var hl = new SolidBrush(Theme.WithAlpha(ink, 150)))
                    g.FillRectangle(hl, new RectangleF(P(3, 18.5f), new SizeF(18 * u, 3.5f * u)));
                g.DrawPolygon(pen, new[] { P(7, 13), P(14, 4), P(19, 9), P(11, 16), P(7, 16) });
                g.DrawLine(pen, P(11, 7.5f), P(15.5f, 12));
                break;
            case IconKind.Blur:
                // a droplet with three soft lines through it
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(P(12, 3), P(8, 9), P(5, 12), P(5, 15));
                    path.AddBezier(P(5, 15), P(5, 19), P(8.5f, 21.5f), P(12, 21.5f));
                    path.AddBezier(P(12, 21.5f), P(15.5f, 21.5f), P(19, 19), P(19, 15));
                    path.AddBezier(P(19, 15), P(19, 12), P(16, 9), P(12, 3));
                    g.DrawPath(pen, path);
                }
                using (var thin = new Pen(Theme.WithAlpha(c, 140), Theme.StrokeIcon * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(thin, P(8, 13.5f), P(16, 13.5f));
                    g.DrawLine(thin, P(7, 16.5f), P(17, 16.5f));
                }
                break;
            case IconKind.Step:
                g.DrawEllipse(pen, new RectangleF(P(3.5f, 3.5f), new SizeF(17 * u, 17 * u)));
                g.DrawLines(pen, new[] { P(10, 9), P(12.5f, 7.5f), P(12.5f, 16.5f) });
                g.DrawLine(pen, P(10, 16.5f), P(15, 16.5f));
                break;
            case IconKind.Size:
                using (var b = new SolidBrush(c))
                {
                    g.FillEllipse(b, new RectangleF(P(3, 15), new SizeF(4 * u, 4 * u)));
                    g.FillEllipse(b, new RectangleF(P(8.5f, 12.5f), new SizeF(6 * u, 6 * u)));
                }
                g.DrawEllipse(pen, new RectangleF(P(15, 9), new SizeF(7 * u, 7 * u)));
                break;
            case IconKind.Text:
                g.DrawLine(pen, P(5, 5), P(19, 5));
                g.DrawLine(pen, P(12, 5), P(12, 20));
                g.DrawLine(pen, P(9, 20), P(15, 20));
                break;
            case IconKind.Color:
                var r = new RectangleF(P(5, 5), new SizeF(14 * u, 14 * u));
                using (var b = new SolidBrush(ink)) g.FillEllipse(b, r);
                using (var ring = new Pen(Theme.HandleFill, 1.25f * scale)) g.DrawEllipse(ring, r);
                break;
            case IconKind.Undo:
                g.DrawLines(pen, new[] { P(9, 4), P(4, 9), P(9, 14) });
                g.DrawBezier(pen, P(4, 9), P(15, 8), P(21, 12), P(15, 20));
                break;
            case IconKind.Copy:
                using (var path = Theme.Round(new RectangleF(P(8, 8), new SizeF(12 * u, 12 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawLines(pen, new[] { P(4, 15), P(4, 4), P(15, 4) });
                break;
            case IconKind.Save:
                g.DrawLine(pen, P(12, 3), P(12, 15));
                g.DrawLines(pen, new[] { P(7, 10), P(12, 15), P(17, 10) });
                g.DrawLines(pen, new[] { P(4, 15), P(4, 20), P(20, 20), P(20, 15) });
                break;
            case IconKind.Scroll:
                // a page with a downward arrow running past its bottom edge
                using (var path = Theme.Round(new RectangleF(P(5, 3), new SizeF(14 * u, 13 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawLine(pen, P(12, 7), P(12, 21));
                g.DrawLines(pen, new[] { P(8.5f, 17.5f), P(12, 21), P(15.5f, 17.5f) });
                break;
            case IconKind.Print:
                g.DrawLines(pen, new[] { P(7, 9), P(7, 3), P(17, 3), P(17, 9) });
                using (var path = Theme.Round(new RectangleF(P(3, 9), new SizeF(18 * u, 8 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawRectangle(pen, P(7, 14).X, P(7, 14).Y, 10 * u, 7 * u);
                break;
            case IconKind.Close:
                g.DrawLine(pen, P(6, 6), P(18, 18));
                g.DrawLine(pen, P(18, 6), P(6, 18));
                break;
            case IconKind.More:
                using (var b = new SolidBrush(c))
                    foreach (var x in new[] { 6f, 12f, 18f }) g.FillEllipse(b, new RectangleF(P(x - 1.5f, 10.5f), new SizeF(3 * u, 3 * u)));
                break;
            case IconKind.Area:
                // four corner brackets
                g.DrawLines(pen, new[] { P(4, 9), P(4, 4), P(9, 4) });
                g.DrawLines(pen, new[] { P(15, 4), P(20, 4), P(20, 9) });
                g.DrawLines(pen, new[] { P(20, 15), P(20, 20), P(15, 20) });
                g.DrawLines(pen, new[] { P(9, 20), P(4, 20), P(4, 15) });
                break;
            case IconKind.Window:
                using (var path = Theme.Round(new RectangleF(P(3, 5), new SizeF(18 * u, 14 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawLine(pen, P(3, 9), P(21, 9));
                break;
            case IconKind.Screen:
                using (var path = Theme.Round(new RectangleF(P(2.5f, 4), new SizeF(19 * u, 13 * u)), 1.5f * u)) g.DrawPath(pen, path);
                g.DrawLine(pen, P(8, 21), P(16, 21));
                g.DrawLine(pen, P(12, 17), P(12, 21));
                break;
            case IconKind.Cursor:
                g.DrawPolygon(pen, new[] { P(6, 3), P(18, 13.5f), P(12.5f, 14.5f), P(15.5f, 20.5f), P(13, 21.5f), P(10, 15.5f), P(6, 19) });
                break;
            case IconKind.Timer:
                g.DrawEllipse(pen, new RectangleF(P(4.5f, 6), new SizeF(15 * u, 15 * u)));
                g.DrawLines(pen, new[] { P(12, 9.5f), P(12, 13.5f), P(14.5f, 15.5f) });
                g.DrawLine(pen, P(9.5f, 2.5f), P(14.5f, 2.5f));
                break;
            case IconKind.Pause:
                using (var b = new SolidBrush(c))
                {
                    g.FillRectangle(b, new RectangleF(P(6.5f, 5), new SizeF(3.5f * u, 14 * u)));
                    g.FillRectangle(b, new RectangleF(P(14, 5), new SizeF(3.5f * u, 14 * u)));
                }
                break;
            case IconKind.Play:
                using (var b = new SolidBrush(c)) g.FillPolygon(b, new[] { P(7, 4.5f), P(19.5f, 12), P(7, 19.5f) });
                break;
            case IconKind.Restart:
                g.DrawArc(pen, new RectangleF(P(4, 4), new SizeF(16 * u, 16 * u)), -60, 300);
                g.DrawLines(pen, new[] { P(15.5f, 2.5f), P(16.2f, 6.2f), P(12.5f, 7) });
                break;
            case IconKind.Stop:
                using (var b = new SolidBrush(c))
                using (var path = Theme.Round(new RectangleF(P(6, 6), new SizeF(12 * u, 12 * u)), 2 * u)) g.FillPath(b, path);
                break;
            case IconKind.Record:
                using (var b = new SolidBrush(c)) g.FillEllipse(b, new RectangleF(P(6, 6), new SizeF(12 * u, 12 * u)));
                break;
            case IconKind.Video:
                using (var path = Theme.Round(new RectangleF(P(2.5f, 6), new SizeF(13 * u, 12 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawPolygon(pen, new[] { P(15.5f, 10.5f), P(21.5f, 7), P(21.5f, 17), P(15.5f, 13.5f) });
                break;
            case IconKind.Speaker:
                g.DrawPolygon(pen, new[] { P(3.5f, 9.5f), P(7.5f, 9.5f), P(12.5f, 5), P(12.5f, 19), P(7.5f, 14.5f), P(3.5f, 14.5f) });
                g.DrawArc(pen, new RectangleF(P(11, 8.5f), new SizeF(7 * u, 7 * u)), -55, 110);
                g.DrawArc(pen, new RectangleF(P(10, 5), new SizeF(12 * u, 14 * u)), -55, 110);
                break;
            case IconKind.Mic:
            case IconKind.MicOff:
                using (var path = Theme.Round(new RectangleF(P(9, 2.5f), new SizeF(6 * u, 11.5f * u)), 3 * u)) g.DrawPath(pen, path);
                g.DrawArc(pen, new RectangleF(P(5.5f, 5.5f), new SizeF(13 * u, 12 * u)), 0, 180);
                g.DrawLine(pen, P(12, 17.5f), P(12, 21));
                g.DrawLine(pen, P(8.5f, 21), P(15.5f, 21));
                if (kind == IconKind.MicOff) g.DrawLine(pen, P(4, 3.5f), P(20, 20.5f));
                break;
            case IconKind.Follow:
                // a dashed view frame with a pointer inside it
                using (var dash = new Pen(c, Theme.StrokeIcon * scale) { DashPattern = new[] { 2f, 1.6f } })
                using (var path = Theme.Round(new RectangleF(P(2.5f, 4.5f), new SizeF(19 * u, 15 * u)), 2 * u)) g.DrawPath(dash, path);
                g.DrawPolygon(pen, new[] { P(10, 8.5f), P(16, 13.5f), P(13, 14), P(14.5f, 17), P(13, 17.7f), P(11.5f, 14.7f), P(10, 16.5f) });
                break;
            case IconKind.AllScreens:
                using (var path = Theme.Round(new RectangleF(P(1.5f, 6), new SizeF(10 * u, 8 * u)), 1.2f * u)) g.DrawPath(pen, path);
                using (var path = Theme.Round(new RectangleF(P(12.5f, 6), new SizeF(10 * u, 8 * u)), 1.2f * u)) g.DrawPath(pen, path);
                g.DrawLine(pen, P(4.5f, 18.5f), P(19.5f, 18.5f));
                g.DrawLine(pen, P(12, 14), P(12, 18.5f));
                break;
            case IconKind.Click:
                g.DrawPolygon(pen, new[] { P(9, 9), P(19, 17.5f), P(14.5f, 18), P(16.5f, 22), P(14.5f, 23), P(12.5f, 19), P(9, 21.5f) });
                g.DrawLine(pen, P(7, 2.5f), P(7, 5));
                g.DrawLine(pen, P(2.5f, 7), P(5, 7));
                g.DrawLine(pen, P(3.5f, 3.5f), P(5, 5));
                g.DrawLine(pen, P(10.5f, 3.5f), P(9.5f, 5));
                break;
            case IconKind.Keyboard:
                using (var path = Theme.Round(new RectangleF(P(2, 6), new SizeF(20 * u, 12 * u)), 2 * u)) g.DrawPath(pen, path);
                using (var b = new SolidBrush(c))
                    foreach (var (x, y) in new[] { (6f, 10f), (10f, 10f), (14f, 10f), (18f, 10f) })
                        g.FillEllipse(b, new RectangleF(P(x - 1, y - 1), new SizeF(2 * u, 2 * u)));
                g.DrawLine(pen, P(7.5f, 14.5f), P(16.5f, 14.5f));
                break;
            case IconKind.Spotlight:
                g.DrawEllipse(pen, new RectangleF(P(7, 7), new SizeF(10 * u, 10 * u)));
                foreach (var (x1, y1, x2, y2) in new[] { (12f, 1.5f, 12f, 4f), (12f, 20f, 12f, 22.5f), (1.5f, 12f, 4f, 12f), (20f, 12f, 22.5f, 12f) })
                    g.DrawLine(pen, P(x1, y1), P(x2, y2));
                break;
            case IconKind.Magnifier:
                g.DrawEllipse(pen, new RectangleF(P(3, 3), new SizeF(13 * u, 13 * u)));
                g.DrawLine(pen, P(14, 14), P(21, 21));
                g.DrawLine(pen, P(9.5f, 6.5f), P(9.5f, 12.5f));
                g.DrawLine(pen, P(6.5f, 9.5f), P(12.5f, 9.5f));
                break;
            case IconKind.SkipBack:
                g.DrawLines(pen, new[] { P(11, 6), P(5, 12), P(11, 18) });
                g.DrawLines(pen, new[] { P(19, 6), P(13, 12), P(19, 18) });
                break;
            case IconKind.SkipForward:
                g.DrawLines(pen, new[] { P(13, 6), P(19, 12), P(13, 18) });
                g.DrawLines(pen, new[] { P(5, 6), P(11, 12), P(5, 18) });
                break;
            case IconKind.Scissors:
                g.DrawEllipse(pen, new RectangleF(P(3, 3), new SizeF(6 * u, 6 * u)));
                g.DrawEllipse(pen, new RectangleF(P(3, 15), new SizeF(6 * u, 6 * u)));
                g.DrawLine(pen, P(8.2f, 8.2f), P(20, 18));
                g.DrawLine(pen, P(8.2f, 15.8f), P(20, 6));
                break;
            case IconKind.Camera:
                using (var path = Theme.Round(new RectangleF(P(3, 7), new SizeF(18 * u, 13 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawLines(pen, new[] { P(8, 7), P(10, 4), P(14, 4), P(16, 7) });
                g.DrawEllipse(pen, new RectangleF(P(8.5f, 10), new SizeF(7 * u, 7 * u)));
                break;
            case IconKind.Folder:
                g.DrawLines(pen, new[] { P(3, 19), P(3, 5), P(9, 5), P(11, 7.5f), P(21, 7.5f), P(21, 19), P(3, 19) });
                break;
            case IconKind.Trash:
                g.DrawLine(pen, P(4, 6.5f), P(20, 6.5f));
                g.DrawLines(pen, new[] { P(9, 6.5f), P(9.5f, 3.5f), P(14.5f, 3.5f), P(15, 6.5f) });
                g.DrawLines(pen, new[] { P(6, 6.5f), P(7, 20.5f), P(17, 20.5f), P(18, 6.5f) });
                g.DrawLine(pen, P(10, 10.5f), P(10, 16.5f));
                g.DrawLine(pen, P(14, 10.5f), P(14, 16.5f));
                break;
            case IconKind.Film:
                using (var path = Theme.Round(new RectangleF(P(3, 5), new SizeF(18 * u, 14 * u)), 2 * u)) g.DrawPath(pen, path);
                g.DrawLine(pen, P(7, 5), P(7, 19));
                g.DrawLine(pen, P(17, 5), P(17, 19));
                g.DrawLine(pen, P(3, 12), P(7, 12));
                g.DrawLine(pen, P(17, 12), P(21, 12));
                break;
        }
        g.SmoothingMode = old;
    }
}
