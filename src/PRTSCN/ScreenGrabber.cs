using System.Drawing.Imaging;

namespace PRTSCN;

internal static class ScreenGrabber
{
    /// <summary>
    /// One frozen bitmap of the area, in physical pixels (the process is Per-Monitor-V2 aware,
    /// so no DPI virtualisation). Taken before any PRTSCN window appears.
    /// 24bpp avoids the zero-alpha problem some GPUs give BitBlt into 32bpp ARGB.
    /// Calls BitBlt directly: Graphics.CopyFromScreen rejects SRCCOPY | CAPTUREBLT as an
    /// "invalid enum value", and CAPTUREBLT is needed to include layered (semi-transparent) windows.
    /// </summary>
    public static Bitmap Capture(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        IntPtr destDc = g.GetHdc();
        try
        {
            if (!Native.BitBlt(destDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            g.ReleaseHdc(destDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
        return bmp;
    }

    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;
}
