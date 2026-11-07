namespace PRTSCN.Core;

public static class FileNaming
{
    /// <summary>PRTSCN_2026-11-01_14-03-22.png</summary>
    public static string DefaultName(DateTime when, ImageFormat fmt) =>
        $"PRTSCN_{when:yyyy-MM-dd_HH-mm-ss}.{Extension(fmt)}";

    public static string Extension(ImageFormat fmt) => fmt == ImageFormat.Jpg ? "jpg" : "png";

    public static ImageFormat FormatFromPath(string path, ImageFormat fallback)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" ? ImageFormat.Jpg : ext == ".png" ? ImageFormat.Png : fallback;
    }

    /// <summary>Path in folder that does not exist yet: name.png, name (2).png, name (3).png ...</summary>
    public static string Unique(string folder, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var p = Path.Combine(folder, fileName);
        for (int i = 2; exists(p); i++) p = Path.Combine(folder, $"{stem} ({i}){ext}");
        return p;
    }
}
