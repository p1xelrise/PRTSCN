using PRTSCN.Core;

public class AudioTimelineTests
{
    static float[] Frames(int n, float v)
    {
        var a = new float[n * 2];
        Array.Fill(a, v);
        return a;
    }

    [Fact]
    public void Packets_land_back_to_back_despite_jitter()
    {
        var t = new AudioTimeline(48000);
        t.Append(0, Frames(480, 0.5f));
        t.Append(470, Frames(480, 0.25f)); // 10 frames early: still goes right after
        Assert.Equal(960, t.End);
        var mix = new float[960 * 2];
        t.ReadInto(0, 960, mix, 1f);
        Assert.Equal(0.5f, mix[479 * 2]);
        Assert.Equal(0.25f, mix[480 * 2]);
    }

    [Fact]
    public void A_real_gap_reads_as_silence_and_keeps_later_sound_on_time()
    {
        var t = new AudioTimeline(48000);
        t.Append(0, Frames(480, 0.5f));
        t.Append(48000, Frames(480, 0.5f)); // loopback was silent for a second
        Assert.Equal(48480, t.End);
        var mix = new float[48480 * 2];
        t.ReadInto(0, 48480, mix, 1f);
        Assert.Equal(0f, mix[24000 * 2]);
        Assert.Equal(0.5f, mix[48000 * 2]);
    }

    [Fact]
    public void Packets_far_ahead_of_the_clock_are_dropped()
    {
        var t = new AudioTimeline(48000);
        t.Append(0, Frames(48000, 0.1f));
        Assert.False(t.Append(1000, Frames(480, 0.1f))); // End is 47 000 frames ahead of where this belongs
        Assert.Equal(48000, t.End);
    }

    [Fact]
    public void Reading_forgets_frames_and_a_late_source_continues_at_the_read_position()
    {
        var t = new AudioTimeline(48000);
        var mix = new float[4800 * 2];
        float peak = t.ReadInto(0, 4800, mix, 1f); // nothing yet: silence
        Assert.Equal(0f, peak);
        Assert.Equal(4800, t.Start);
        Assert.Equal(4800, t.End);
        t.Append(4800, Frames(100, 1f));
        Array.Clear(mix);
        peak = t.ReadInto(4800, 100, mix, 0.5f);
        Assert.Equal(0.5f, peak);
        Assert.Equal(0.5f, mix[0]);
    }

    [Fact]
    public void Resampler_keeps_length_and_level()
    {
        var r = new LinearResampler(44100, 48000);
        var outp = new List<float>();
        for (int k = 0; k < 10; k++) r.Process(Frames(441, 0.3f), 441, outp); // 0.1 s in ten packets
        Assert.InRange(outp.Count / 2, 4790, 4810);
        Assert.All(outp, v => Assert.Equal(0.3f, v, 4));
    }

    [Fact]
    public void Resampler_passes_through_at_the_same_rate()
    {
        var r = new LinearResampler(48000, 48000);
        var outp = new List<float>();
        r.Process(new float[] { 1, 2, 3, 4 }, 2, outp);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, outp);
    }

    [Fact]
    public void Pcm_conversion_clips()
    {
        var dst = new short[3];
        AudioMath.ToPcm16(new[] { 2f, -2f, 0.5f }, dst);
        Assert.Equal(new short[] { 32767, -32767, 16384 }, dst);
        Assert.Equal(48000, AudioMath.FrameAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(10_000_000, AudioMath.Time(48000));
        Assert.Equal(2f, AudioMath.Gain(500));
    }

    [Fact]
    public void Sound_settings_default_and_clamp()
    {
        var d = new AppSettings().Normalize();
        Assert.True(d.RecordSystemAudio);
        Assert.False(d.RecordMic);
        Assert.Equal("", d.MicDeviceId);
        var bad = new AppSettings { SystemVolume = 900, MicVolume = -3, MicDeviceId = null! }.Normalize();
        Assert.Equal(200, bad.SystemVolume);
        Assert.Equal(0, bad.MicVolume);
        Assert.Equal("", bad.MicDeviceId);
    }
}
