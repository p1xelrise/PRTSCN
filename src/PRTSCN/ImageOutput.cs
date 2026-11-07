using System.Drawing.Imaging;
using System.Drawing.Printing;
using PRTSCN.Core;
using CoreFormat = PRTSCN.Core.ImageFormat;

namespace PRTSCN;

internal static class ImageOutput
{
    /// <summary>Puts the image on the clipboard as both a bitmap (CF_DIB) and PNG, which covers chat apps, Office and browsers.</summary>
    public static void CopyToClipboard(Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, bmp);
        data.SetData("PNG", false, png);
        // copy:true flushes the data, so it survives PRTSCN closing; retries cover other apps holding the clipboard.
        Clipboard.SetDataObject(data, true, 10, 100);
    }

    public static void Save(Bitmap bmp, string path, CoreFormat fmt, int jpegQuality)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (fmt == CoreFormat.Jpg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 10, 100));
            bmp.Save(path, codec, ps);
        }
        else
        {
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    /// <summary>Shows the print dialog and prints the image fitted to the page. Returns true if it printed.</summary>
    public static bool Print(Bitmap bmp, IWin32Window owner)
    {
        using var doc = new PrintDocument { DocumentName = "PRTSCN screenshot" };
        doc.DefaultPageSettings.Landscape = bmp.Width > bmp.Height;
        doc.PrintPage += (_, e) =>
        {
            var m = e.MarginBounds;
            float k = Math.Min(1f, Math.Min(m.Width / (float)bmp.Width, m.Height / (float)bmp.Height));
            // Never upscale; at 100 DPI print units, small captures print near screen size.
            float w = bmp.Width * k, h = bmp.Height * k;
            e.Graphics!.DrawImage(bmp, m.Left + (m.Width - w) / 2, m.Top + (m.Height - h) / 2, w, h);
        };
        using var dlg = new PrintDialog { Document = doc, UseEXDialog = true };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
        doc.Print();
        return true;
    }

    public static string SaveToFolder(Bitmap bmp, AppSettings s)
    {
        var name = FileNaming.DefaultName(DateTime.Now, s.Format);
        var path = FileNaming.Unique(s.SaveFolder, name);
        Save(bmp, path, s.Format, s.JpegQuality);
        return path;
    }
}
