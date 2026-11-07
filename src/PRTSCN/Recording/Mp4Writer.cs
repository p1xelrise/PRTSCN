using System.Runtime.InteropServices;
using PRTSCN.Core;
using Vortice.MediaFoundation;

namespace PRTSCN.Recording;

/// <summary>
/// MP4 file writer on the Media Foundation sink writer: H.264 video and/or AAC sound.
/// Frames go in as top-down BGRA; Media Foundation converts them to NV12 and uses the graphics
/// card's encoder when allowed. Sound goes in as 48 kHz stereo 16-bit PCM.
/// Video-less files (audio only) are .m4a. Thread-safe: video and sound come from different threads.
/// </summary>
internal sealed unsafe class Mp4Writer : IMediaSink
{
    // Media Foundation GUIDs (mfapi.h)
    static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    static readonly Guid MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    static readonly Guid MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    static readonly Guid MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    static readonly Guid MT_AAC_PROFILE_LEVEL = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid MediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    static readonly Guid VideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid VideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    static readonly Guid AudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    static readonly Guid AudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    static readonly Guid SINK_WRITER_HW = new("a634a91c-822b-41b9-a494-4de4643612b0"); // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS
    static readonly Guid SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    static readonly Guid TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    static readonly Guid ContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

    /// <summary>AAC at 160 kbit/s (20 000 bytes/s), one of the rates the Windows encoder accepts.</summary>
    const int AacBytesPerSecond = 20000;

    static int _startups;
    static readonly object StartLock = new();

    readonly IMFSinkWriter _writer;
    readonly int _videoStream = -1, _audioStream = -1;
    readonly int _width, _height;
    readonly object _lock = new();
    bool _finished;

    public bool UsedHardware { get; }
    public bool HasAudio => _audioStream >= 0;

    /// <summary>Video (and optionally sound). Pass width/height 0 for a sound-only file.</summary>
    public Mp4Writer(string path, int width, int height, int fps, int bitrate, bool allowHardware, bool audio)
    {
        Startup();
        _width = width; _height = height;
        bool video = width > 0 && height > 0;
        try
        {
            (_writer, _videoStream, _audioStream) = Create(path, allowHardware && video);
            UsedHardware = allowHardware && video;
        }
        catch when (allowHardware && video)
        {
            // Some drivers refuse odd sizes or are busy: fall back to the software encoder.
            try { File.Delete(path); } catch { }
            (_writer, _videoStream, _audioStream) = Create(path, false);
            UsedHardware = false;
        }
        catch
        {
            Shutdown();
            throw;
        }

        (IMFSinkWriter, int, int) Create(string p, bool hw)
        {
            using var attrs = MediaFactory.MFCreateAttributes(3);
            attrs.Set(SINK_WRITER_HW, hw ? 1u : 0u);
            attrs.Set(SINK_WRITER_DISABLE_THROTTLING, 0u);
            attrs.Set(TRANSCODE_CONTAINERTYPE, ContainerType_MPEG4);
            var w = MediaFactory.MFCreateSinkWriterFromURL(p, null, attrs);
            try
            {
                int vs = -1, aus = -1;
                if (video)
                {
                    using var outType = MediaFactory.MFCreateMediaType();
                    outType.Set(MT_MAJOR_TYPE, MediaType_Video);
                    outType.Set(MT_SUBTYPE, VideoFormat_H264);
                    outType.Set(MT_AVG_BITRATE, (uint)bitrate);
                    outType.Set(MT_INTERLACE_MODE, 2u); // progressive
                    outType.Set(MT_FRAME_SIZE, Pack(width, height));
                    outType.Set(MT_FRAME_RATE, Pack(fps, 1));
                    outType.Set(MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                    outType.Set(MT_MPEG2_PROFILE, 100u); // High: sharper text at the same bitrate
                    vs = w.AddStream(outType);

                    using var inType = MediaFactory.MFCreateMediaType();
                    inType.Set(MT_MAJOR_TYPE, MediaType_Video);
                    inType.Set(MT_SUBTYPE, VideoFormat_RGB32);
                    inType.Set(MT_INTERLACE_MODE, 2u);
                    inType.Set(MT_FRAME_SIZE, Pack(width, height));
                    inType.Set(MT_FRAME_RATE, Pack(fps, 1));
                    inType.Set(MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                    inType.Set(MT_DEFAULT_STRIDE, (uint)(width * 4)); // positive = top-down rows
                    inType.Set(MT_ALL_SAMPLES_INDEPENDENT, 1u);
                    w.SetInputMediaType(vs, inType, null);
                }
                if (audio)
                {
                    using var outType = MediaFactory.MFCreateMediaType();
                    outType.Set(MT_MAJOR_TYPE, MediaType_Audio);
                    outType.Set(MT_SUBTYPE, AudioFormat_AAC);
                    outType.Set(MT_AUDIO_BITS_PER_SAMPLE, 16u);
                    outType.Set(MT_AUDIO_SAMPLES_PER_SECOND, (uint)AudioMath.Rate);
                    outType.Set(MT_AUDIO_NUM_CHANNELS, (uint)AudioMath.Channels);
                    outType.Set(MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)AacBytesPerSecond);
                    outType.Set(MT_AAC_PAYLOAD_TYPE, 0u);          // raw AAC, as MP4 stores it
                    outType.Set(MT_AAC_PROFILE_LEVEL, 0x29u);      // AAC LC
                    aus = w.AddStream(outType);

                    using var inType = MediaFactory.MFCreateMediaType();
                    inType.Set(MT_MAJOR_TYPE, MediaType_Audio);
                    inType.Set(MT_SUBTYPE, AudioFormat_PCM);
                    inType.Set(MT_AUDIO_BITS_PER_SAMPLE, 16u);
                    inType.Set(MT_AUDIO_SAMPLES_PER_SECOND, (uint)AudioMath.Rate);
                    inType.Set(MT_AUDIO_NUM_CHANNELS, (uint)AudioMath.Channels);
                    inType.Set(MT_AUDIO_BLOCK_ALIGNMENT, (uint)(AudioMath.Channels * 2));
                    inType.Set(MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(AudioMath.Rate * AudioMath.Channels * 2));
                    w.SetInputMediaType(aus, inType, null);
                }
                w.BeginWriting();
                return (w, vs, aus);
            }
            catch
            {
                w.Dispose();
                throw;
            }
        }
    }

    static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    internal static void Startup()
    {
        lock (StartLock)
        {
            if (_startups++ == 0) MediaFactory.MFStartup().CheckError();
        }
    }

    internal static void Shutdown()
    {
        lock (StartLock)
        {
            if (--_startups == 0) MediaFactory.MFShutdown();
        }
    }

    /// <summary>Write one frame. src points at BGRA rows of the frame size, rowPitch bytes apart.</summary>
    public void WriteFrame(IntPtr src, int rowPitch, long time, long duration)
    {
        int stride = _width * 4, len = stride * _height;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(len);
        buffer.Lock(out IntPtr dst, out _, out _);
        try
        {
            byte* s = (byte*)src, d = (byte*)dst;
            for (int y = 0; y < _height; y++)
                Buffer.MemoryCopy(s + (long)y * rowPitch, d + (long)y * stride, stride, stride);
        }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = len;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        lock (_lock) { if (!_finished) _writer.WriteSample(_videoStream, sample); }
    }

    /// <summary>Write a black frame (before the first captured frame, or after a window closes).</summary>
    public void WriteBlack(long time, long duration)
    {
        int stride = _width * 4;
        var row = Marshal.AllocHGlobal(stride * _height);
        try
        {
            new Span<byte>((void*)row, stride * _height).Clear();
            WriteFrame(row, stride, time, duration);
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    /// <summary>Write interleaved stereo 16-bit PCM frames.</summary>
    public void WriteAudio(short[] pcm, int frames, long time, long duration)
    {
        if (_audioStream < 0 || frames <= 0) return;
        int len = frames * AudioMath.Channels * 2;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(len);
        buffer.Lock(out IntPtr dst, out _, out _);
        try { fixed (short* s = pcm) Buffer.MemoryCopy(s, (void*)dst, len, len); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = len;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        lock (_lock) { if (!_finished) _writer.WriteSample(_audioStream, sample); }
    }

    /// <summary>Finish the file (writes the MP4 index). Without this the file does not play.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
            _writer.Finalize();
        }
    }

    public void Dispose()
    {
        try { Finish(); }
        catch (Exception ex) { CrashLog.Write(ex); }
        _writer.Dispose();
        Shutdown();
    }
}
