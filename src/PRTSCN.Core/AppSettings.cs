using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRTSCN.Core;

public enum ImageFormat { Png, Jpg }

/// <summary>What a screen recording is saved as.</summary>
public enum RecordFormat { Mp4, Gif }

/// <summary>User settings, stored as JSON in %APPDATA%\PRTSCN\settings.json.</summary>
public sealed class AppSettings
{
    // Hotkeys are stored as WinForms Keys values (key code | modifier flags).
    public const int KeyPrintScreen = 0x2C;
    public const int ModShift = 0x10000;

    public int CaptureHotkey { get; set; } = KeyPrintScreen;
    public int InstantHotkey { get; set; } = KeyPrintScreen | ModShift;
    public const int ModControl = 0x20000;
    /// <summary>Window capture: hover highlights a window, a click captures it.</summary>
    public int WindowHotkey { get; set; } = KeyPrintScreen | ModShift | ModControl;
    /// <summary>Scrolling capture has no hotkey by default (tray menu and the toolbar button).</summary>
    public int ScrollHotkey { get; set; } = 0;
    /// <summary>Wait after each scroll step, so smooth scrolling can settle.</summary>
    public int ScrollDelayMs { get; set; } = 350;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ImageFormat Format { get; set; } = ImageFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public bool CopyAfterInstantCapture { get; set; } = true;
    /// <summary>Clicking a highlighted window copies it straight away instead of opening the editor on it.</summary>
    public bool WindowClickCopies { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    /// <summary>A deep copy (Options edits a copy, so Cancel leaves the live settings alone).</summary>
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public int InkWidth { get; set; } = 3;
    public string InkColor { get; set; } = "#ff3347";

    /// <summary>Drawing on the screen while recording: pen colour and width (logical pixels).</summary>
    public string DrawColor { get; set; } = "#ff3347";
    public int DrawWidth { get; set; } = 5;
    public static readonly int[] DrawWidthChoices = { 3, 5, 9, 16 };

    // ---- screen recording ----
    public const int ModAlt = 0x40000;
    public const int KeyPause = 0x13;
    /// <summary>Opens the record screen; while recording, stops and saves.</summary>
    public int RecordHotkey { get; set; } = KeyPrintScreen | ModControl | ModAlt;
    /// <summary>
    /// Left Alt + Left Shift + Print Screen is Windows' High Contrast shortcut: Windows takes it before any app,
    /// so it can never work as a hotkey. v0.5.0 shipped it as the record default.
    /// </summary>
    public const int HighContrastCombo = KeyPrintScreen | ModShift | ModAlt;
    /// <summary>Pause / resume. Registered only while a recording runs.</summary>
    public int PauseHotkey { get; set; } = KeyPause;
    public string VideoFolder { get; set; } = DefaultVideoFolder();
    public int VideoFps { get; set; } = 30;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoQuality VideoQuality { get; set; } = VideoQuality.High;
    /// <summary>Use the graphics card's encoder when there is one.</summary>
    public bool HardwareEncoder { get; set; } = true;
    /// <summary>Seconds of countdown before recording starts (0 = off).</summary>
    public int RecordCountdown { get; set; } = 3;
    public bool RecordShowCursor { get; set; } = true;
    /// <summary>Stop by itself after this many minutes (0 = no limit).</summary>
    public int RecordMaxMinutes { get; set; } = 0;
    /// <summary>Stop by itself when the file reaches this many MB (0 = no limit).</summary>
    public int RecordMaxMegabytes { get; set; } = 0;
    /// <summary>Show the dashed frame around the area while recording.</summary>
    public bool RecordShowFrame { get; set; } = true;

    // ---- recording sound ----
    /// <summary>Record what the computer plays (speakers / headphones).</summary>
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMic { get; set; } = false;
    /// <summary>Microphone device id; empty = the Windows default microphone.</summary>
    public string MicDeviceId { get; set; } = "";
    /// <summary>Volumes in percent, 0–200.</summary>
    public int SystemVolume { get; set; } = 100;
    public int MicVolume { get; set; } = 100;

    // ---- mouse & keys ----
    /// <summary>Draw a ripple where the mouse clicks.</summary>
    public bool RecordClicks { get; set; } = true;
    /// <summary>Show pressed keys in the video. Off by default: typing can reveal passwords.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KeyDisplay RecordKeys { get; set; } = KeyDisplay.Off;
    /// <summary>Dim everything except a circle around the pointer.</summary>
    public bool RecordSpotlight { get; set; } = false;
    /// <summary>A round lens that magnifies around the pointer.</summary>
    public bool RecordMagnifier { get; set; } = false;
    /// <summary>Magnifier zoom in percent (150, 200, 300).</summary>
    public int MagnifierZoom { get; set; } = 200;

    // ---- after recording / GIF ----
    /// <summary>Record straight to an animated GIF (made from a temporary MP4 when recording stops).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecordFormat RecordFormat { get; set; } = RecordFormat.Mp4;
    public int GifFps { get; set; } = 15;
    /// <summary>Widest GIF in pixels (0 = the recording's own size).</summary>
    public int GifMaxWidth { get; set; } = 800;
    /// <summary>Dithering: smoother gradients, but bigger files and noisy flat areas.</summary>
    public bool GifDither { get; set; } = false;
    /// <summary>Open the editor when a video recording is saved.</summary>
    public bool OpenEditorAfterRecording { get; set; } = true;
    /// <summary>Newest first; at most <see cref="MaxRecent"/>.</summary>
    public List<string> RecentRecordings { get; set; } = new();
    public const int MaxRecent = 10;

    // ---- advanced (R5) ----
    /// <summary>Zoom in where the mouse clicks, follow the pointer, zoom out after a quiet moment.</summary>
    public bool RecordAutoZoom { get; set; } = false;
    /// <summary>Auto-zoom factor in percent (150, 200, 300).</summary>
    public int AutoZoomLevel { get; set; } = 200;
    /// <summary>Text drawn in a corner of every video (empty = none).</summary>
    public string WatermarkText { get; set; } = "";
    /// <summary>A PNG/JPG drawn in a corner of every video (empty = none). Drawn above the text.</summary>
    public string WatermarkImage { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Corner WatermarkCorner { get; set; } = Corner.BottomRight;
    /// <summary>Watermark opacity in percent (10–100).</summary>
    public int WatermarkOpacity { get; set; } = 70;
    public bool HasWatermark => !string.IsNullOrWhiteSpace(WatermarkText) || !string.IsNullOrWhiteSpace(WatermarkImage);

    /// <summary>Keep recording the screen in the background so the last moments can be saved.</summary>
    public bool ReplayEnabled { get; set; } = false;
    /// <summary>How much the replay keeps, in seconds.</summary>
    public int ReplaySeconds { get; set; } = 60;
    /// <summary>Saves the instant replay.</summary>
    public int ReplayHotkey { get; set; } = 'R' | ModControl | ModAlt;
    /// <summary>A recording set to start by itself (null = none).</summary>
    public RecordSchedule? Schedule { get; set; }

    public static readonly int[] ReplayChoices = { 30, 60, 120, 300 };

    public static readonly int[] GifFpsChoices = { 10, 15, 20, 25 };
    public static readonly int[] GifWidthChoices = { 0, 1280, 960, 800, 640, 480, 320 };

    /// <summary>Put a file at the top of the recent list.</summary>
    public void AddRecent(string path)
    {
        RecentRecordings.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentRecordings.Insert(0, path);
        if (RecentRecordings.Count > MaxRecent) RecentRecordings.RemoveRange(MaxRecent, RecentRecordings.Count - MaxRecent);
    }

    public static readonly int[] ZoomChoices = { 150, 200, 300 };
    public static readonly int[] FpsChoices = { 15, 30, 60 };
    public static readonly int[] CountdownChoices = { 0, 3, 5, 10 };

    public static string DefaultSaveFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PRTSCN");

    public static string DefaultVideoFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PRTSCN");

    /// <summary>Fix out-of-range values (hand-edited JSON, older versions).</summary>
    public AppSettings Normalize()
    {
        JpegQuality = Math.Clamp(JpegQuality, 10, 100);
        ScrollDelayMs = Math.Clamp(ScrollDelayMs, 100, 3000);
        InkWidth = Math.Clamp(InkWidth, 1, 24);
        DrawWidth = Math.Clamp(DrawWidth, 2, 40);
        if (!System.Text.RegularExpressions.Regex.IsMatch(DrawColor ?? "", "^#[0-9a-fA-F]{6}$")) DrawColor = "#ff3347";
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = DefaultSaveFolder();
        if (string.IsNullOrWhiteSpace(VideoFolder)) VideoFolder = DefaultVideoFolder();
        if (Array.IndexOf(FpsChoices, VideoFps) < 0) VideoFps = 30;
        if (RecordHotkey == HighContrastCombo) RecordHotkey = KeyPrintScreen | ModControl | ModAlt;
        RecordCountdown = Math.Clamp(RecordCountdown, 0, 10);
        RecordMaxMinutes = Math.Clamp(RecordMaxMinutes, 0, 600);
        RecordMaxMegabytes = Math.Clamp(RecordMaxMegabytes, 0, 100_000);
        SystemVolume = Math.Clamp(SystemVolume, 0, 200);
        MicVolume = Math.Clamp(MicVolume, 0, 200);
        MicDeviceId ??= "";
        if (Array.IndexOf(ZoomChoices, MagnifierZoom) < 0) MagnifierZoom = 200;
        if (!Enum.IsDefined(RecordKeys)) RecordKeys = KeyDisplay.Off;
        if (!Enum.IsDefined(RecordFormat)) RecordFormat = RecordFormat.Mp4;
        if (Array.IndexOf(GifFpsChoices, GifFps) < 0) GifFps = 15;
        if (Array.IndexOf(GifWidthChoices, GifMaxWidth) < 0) GifMaxWidth = 800;
        if (Array.IndexOf(ZoomChoices, AutoZoomLevel) < 0) AutoZoomLevel = 200;
        WatermarkText = (WatermarkText ?? "").Trim();
        if (WatermarkText.Length > 80) WatermarkText = WatermarkText[..80];
        WatermarkImage ??= "";
        if (!Enum.IsDefined(WatermarkCorner)) WatermarkCorner = Corner.BottomRight;
        WatermarkOpacity = Math.Clamp(WatermarkOpacity, 10, 100);
        if (Array.IndexOf(ReplayChoices, ReplaySeconds) < 0) ReplaySeconds = 60;
        if (Schedule != null) Schedule.Minutes = Math.Clamp(Schedule.Minutes, 1, 600);
        var recent = (RecentRecordings ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        RecentRecordings = new();
        for (int i = recent.Count - 1; i >= 0; i--) AddRecent(recent[i]);
        if (!System.Text.RegularExpressions.Regex.IsMatch(InkColor ?? "", "^#[0-9a-fA-F]{6}$")) InkColor = "#ff3347";
        return this;
    }
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PRTSCN", "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts) ?? new AppSettings()).Normalize();
        }
        catch (JsonException) { /* corrupt file: fall back to defaults, keep the bad file for inspection */
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    /// <summary>Write via a temp file so a crash never leaves half a settings file.</summary>
    public static void Save(string path, AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, overwrite: true);
    }
}
