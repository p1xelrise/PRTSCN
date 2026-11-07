using System.Drawing.Drawing2D;
using InterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
using System.Drawing.Imaging;
using PRTSCN.Recording;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace PRTSCN.Editor;

/// <summary>
/// Reads an MP4 / M4A with the Media Foundation source reader: video decoded to top-down BGRA
/// frames, sound decoded to 16-bit PCM at the file's own rate. One reader reads one stream kind
/// (open two for video + sound) so each can be read and seeked on its own.
/// Video is decoded and scaled on the graphics card when it can be, else on the CPU.
/// </summary>
internal sealed unsafe class MediaReader : IDisposable
{
    static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    static readonly Guid MT_GEOMETRIC_APERTURE = new("66758743-7e5f-400d-980a-aa8596c85696");
    static readonly Guid MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid MediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    static readonly Guid VideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    static readonly Guid AudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    static readonly Guid SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    static readonly Guid SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    static readonly Guid SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    static readonly Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    static readonly Guid PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    readonly IMFSourceReader _r;
    readonly SourceReaderIndex _stream;
    readonly ID3D11Device? _device;
    readonly IMFDXGIDeviceManager? _manager;
    readonly int _decW, _decH;     // decoded picture (display area) size
    readonly int _offX, _offY;     // display area inside the decoded buffer
    readonly int _stride;          // rows apart when the buffer is not 2D (negative = bottom-up)
    readonly byte[]? _scaleBuf;    // CPU path: decoded frame before scaling
    bool _ended;

    public bool IsVideo { get; }
    /// <summary>Frame size handed out by ReadFrame (after scaling).</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>The video's own size.</summary>
    public Size SourceSize { get; }
    public double Fps { get; }
    /// <summary>Length in 100 ns units.</summary>
    public long Duration { get; }
    public int AudioRate { get; }
    public int AudioChannels { get; }
    public bool Ended => _ended;
    /// <summary>Decoding and scaling run on the graphics card.</summary>
    public bool Hardware { get; }

    /// <summary>Which kinds of stream the file has.</summary>
    public static (bool video, bool audio) Probe(string path)
    {
        bool v = false, a = false;
        try { using var r = new MediaReader(path, video: true, hardware: false); v = true; } catch { }
        try { using var r = new MediaReader(path, video: false); a = true; } catch { }
        return (v, a);
    }

    /// <param name="sizeFor">Output size for a given video size (null = the video's own size).</param>
    public MediaReader(string path, bool video, Func<Size, Size>? sizeFor = null, bool hardware = true)
    {
        Mp4Writer.Startup();
        IsVideo = video;
        _stream = video ? SourceReaderIndex.FirstVideoStream : SourceReaderIndex.FirstAudioStream;
        try
        {
            if (!video)
            {
                _r = Open(path, null);
                using (var t = MediaFactory.MFCreateMediaType())
                {
                    t.Set(MT_MAJOR_TYPE, MediaType_Audio);
                    t.Set(MT_SUBTYPE, AudioFormat_PCM);
                    t.Set(MT_AUDIO_BITS_PER_SAMPLE, 16u);
                    _r.SetCurrentMediaType(_stream, t);
                }
                using var cur = _r.GetCurrentMediaType(_stream);
                AudioRate = (int)cur.GetUInt32(MT_AUDIO_SAMPLES_PER_SECOND);
                AudioChannels = (int)cur.GetUInt32(MT_AUDIO_NUM_CHANNELS);
            }
            else
            {
                // 1. graphics card: decode + convert + scale in one go
                if (hardware)
                {
                    try
                    {
                        var (dev, ctx) = CaptureInterop.CreateDevice();
                        ctx.Dispose();
                        _device = dev;
                        _manager = MediaFactory.MFCreateDXGIDeviceManager();
                        _manager.ResetDevice(_device).CheckError();
                        _r = Open(path, _manager);
                        var (src, fps) = NativeInfo(_r);
                        var want = Even(sizeFor?.Invoke(src) ?? src);
                        using var t = MediaFactory.MFCreateMediaType();
                        t.Set(MT_MAJOR_TYPE, MediaType_Video);
                        t.Set(MT_SUBTYPE, VideoFormat_RGB32);
                        t.Set(MT_FRAME_SIZE, Pack(want.Width, want.Height));
                        _r.SetCurrentMediaType(_stream, t);
                        SourceSize = src; Fps = fps;
                        Width = _decW = want.Width; Height = _decH = want.Height;
                        Hardware = true;
                    }
                    catch
                    {
                        _r?.Dispose(); _r = null!;
                        _manager?.Dispose(); _manager = null;
                        _device?.Dispose(); _device = null;
                    }
                }
                // 2. CPU: decode + convert, scale with GDI+ when asked
                if (_r == null)
                {
                    _r = Open(path, null);
                    var (src, fps) = NativeInfo(_r);
                    using (var t = MediaFactory.MFCreateMediaType())
                    {
                        t.Set(MT_MAJOR_TYPE, MediaType_Video);
                        t.Set(MT_SUBTYPE, VideoFormat_RGB32);
                        _r.SetCurrentMediaType(_stream, t);
                    }
                    using var cur = _r.GetCurrentMediaType(_stream);
                    ulong size = cur.GetUInt64(MT_FRAME_SIZE);
                    int fw = (int)(size >> 32), fh = (int)(size & 0xFFFFFFFF);
                    var area = Aperture(cur) ?? new Rectangle(0, 0, fw, fh);
                    if (area.Right > fw || area.Bottom > fh) area = new Rectangle(0, 0, fw, fh);
                    _offX = area.X; _offY = area.Y; _decW = area.Width; _decH = area.Height;
                    try { _stride = (int)cur.GetUInt32(MT_DEFAULT_STRIDE); }
                    catch { _stride = -fw * 4; } // uncompressed RGB without a stride is bottom-up
                    SourceSize = new Size(_decW, _decH); Fps = fps;
                    var want = sizeFor?.Invoke(SourceSize) ?? SourceSize;
                    Width = want.Width; Height = want.Height;
                    if (want != SourceSize) _scaleBuf = new byte[_decW * _decH * 4];
                }
            }

            try
            {
                var v = _r.GetPresentationAttribute(SourceReaderIndex.MediaSource, PD_DURATION);
                Duration = Convert.ToInt64(v.Value);
            }
            catch { Duration = 0; }
        }
        catch
        {
            _r?.Dispose();
            _manager?.Dispose();
            _device?.Dispose();
            Mp4Writer.Shutdown();
            throw;
        }
    }

    IMFSourceReader Open(string path, IMFDXGIDeviceManager? manager)
    {
        using var attrs = MediaFactory.MFCreateAttributes(3);
        if (manager != null)
        {
            attrs.Set(SOURCE_READER_D3D_MANAGER, manager);
            attrs.Set(SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1u);
            attrs.Set(READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
        }
        else attrs.Set(SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1u);
        var r = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        r.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        r.SetStreamSelection(_stream, true);
        return r;
    }

    (Size size, double fps) NativeInfo(IMFSourceReader r)
    {
        using var native = r.GetNativeMediaType(_stream, 0);
        ulong s = native.GetUInt64(MT_FRAME_SIZE);
        var size = new Size((int)(s >> 32), (int)(s & 0xFFFFFFFF));
        var ap = Aperture(native);
        if (ap is { Width: > 0, Height: > 0 } a && a.Width <= size.Width && a.Height <= size.Height) size = a.Size;
        double fps = 30;
        try
        {
            ulong fr = native.GetUInt64(MT_FRAME_RATE);
            uint num = (uint)(fr >> 32), den = (uint)fr;
            if (den > 0) fps = (double)num / den;
        }
        catch { }
        if (fps <= 0 || fps > 1000) fps = 30;
        return (size, fps);
    }

    static Rectangle? Aperture(IMFMediaType t)
    {
        foreach (var key in new[] { MT_MINIMUM_DISPLAY_APERTURE, MT_GEOMETRIC_APERTURE })
        {
            try
            {
                var ap = t.GetBlob(key);
                // MFVideoArea: MFOffset x (fract16 + int16), MFOffset y, SIZE (int cx, int cy)
                if (ap.Length >= 16)
                {
                    var r = new Rectangle(BitConverter.ToInt16(ap, 2), BitConverter.ToInt16(ap, 6), BitConverter.ToInt32(ap, 8), BitConverter.ToInt32(ap, 12));
                    if (r.Width > 0 && r.Height > 0 && r.X >= 0 && r.Y >= 0) return r;
                }
            }
            catch { }
        }
        return null;
    }

    static Size Even(Size s) => new(Math.Max(2, s.Width & ~1), Math.Max(2, s.Height & ~1));
    static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    /// <summary>Jump to a time. Reading continues from the key frame (or sound packet) at or before it.</summary>
    public void Seek(long time)
    {
        _r.SetCurrentPosition(Math.Max(0, time));
        _ended = false;
    }

    /// <summary>
    /// Decode the next video frame into <paramref name="dst"/> (Width × Height × 4, top-down BGRA).
    /// False at the end of the stream.
    /// </summary>
    public bool ReadFrame(byte[] dst, out long time)
    {
        while (true)
        {
            using var sample = Next(out time);
            if (sample == null) { if (_ended) return false; continue; }
            using var buffer = sample.ConvertToContiguousBuffer();
            var target = _scaleBuf ?? dst;
            var b2 = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
            try
            {
                IntPtr scan0; int pitch;
                if (b2 != null) b2.Lock2D(out scan0, out pitch);
                else
                {
                    buffer.Lock(out IntPtr p, out _, out int len);
                    pitch = _stride;
                    int rows = len / Math.Max(1, Math.Abs(pitch));
                    scan0 = pitch < 0 ? p + (-pitch) * (rows - 1) : p;
                }
                try
                {
                    int row = _decW * 4;
                    fixed (byte* d = target)
                        for (int y = 0; y < _decH; y++)
                        {
                            byte* s = (byte*)scan0 + (long)(y + _offY) * pitch + _offX * 4;
                            Buffer.MemoryCopy(s, d + (long)y * row, row, row);
                        }
                }
                finally
                {
                    if (b2 != null) b2.Unlock2D(); else buffer.Unlock();
                }
            }
            finally { b2?.Dispose(); }
            if (_scaleBuf != null) Scale(_scaleBuf, _decW, _decH, dst, Width, Height);
            return true;
        }
    }

    /// <summary>Scale a BGRA frame with GDI+ (high quality).</summary>
    public static void Scale(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            using var sb = new Bitmap(sw, sh, sw * 4, PixelFormat.Format32bppRgb, (IntPtr)s);
            using var db = new Bitmap(dw, dh, dw * 4, PixelFormat.Format32bppRgb, (IntPtr)d);
            using var g = Graphics.FromImage(db);
            g.InterpolationMode = dw < sw / 2 ? InterpolationMode.HighQualityBilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
            g.DrawImage(sb, new Rectangle(0, 0, dw, dh), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
        }
    }

    /// <summary>The next block of sound: interleaved 16-bit samples. Null at the end.</summary>
    public short[]? ReadAudio(out long time, out int frames)
    {
        frames = 0;
        while (true)
        {
            using var sample = Next(out time);
            if (sample == null) { if (_ended) return null; continue; }
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr p, out _, out int len);
            try
            {
                var pcm = new short[len / 2];
                fixed (short* d = pcm) Buffer.MemoryCopy((void*)p, d, len, len);
                frames = pcm.Length / Math.Max(1, AudioChannels);
                return pcm;
            }
            finally { buffer.Unlock(); }
        }
    }

    IMFSample? Next(out long time)
    {
        var s = _r.ReadSample(_stream, SourceReaderControlFlag.None, out _, out var flags, out time);
        if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
        {
            _ended = true;
            s?.Dispose();
            return null;
        }
        return s;
    }

    bool _disposed;

    public void Dispose()
    {
        if (_disposed) return; // Media Foundation start-up is counted: shut down exactly once
        _disposed = true;
        _r.Dispose();
        _manager?.Dispose();
        _device?.Dispose();
        Mp4Writer.Shutdown();
    }
}
